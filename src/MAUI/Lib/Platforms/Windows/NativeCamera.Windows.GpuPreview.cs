using System.Diagnostics;
using System.Runtime.InteropServices;
using DrawnUi.Camera.Gpu;
using Microsoft.Extensions.Logging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace DrawnUi.Camera;

/// <summary>
/// Preview on the GPU (Windows). Media Foundation gets our own D3D11 device on the adapter ANGLE renders the UI on, the
/// frame reader delivers frames as the camera produces them (NV12 / YUY2; MJPG decoded by the frame server), the camera
/// thread converts each one with the D3D11 video processor into a shared BGRA ring, and the UI thread draws the ring slot
/// as an SKImage of its own context through an EGLImage. No frame is read back to the CPU unless a consumer asks for CPU
/// pixels. Fallbacks, logged once: Media Foundation's own device when it refuses ours; the raster path when frames end up
/// on another adapter than the UI, arrive without a Direct3D surface, or the GPU setup fails.
/// </summary>
public partial class NativeCamera
{
    volatile bool _gpuCapture;               // this session converts native frames on the GPU
    string _gpuDisabledReason;               // a failure that turned the GPU preview off for this camera
    GpuDevices.Adapter? _uiAdapter;
    GpuCaptureDevice _captureDevice;         // ours, kept across restarts
    GpuCapturePipeline _gpuPipeline;         // camera thread
    GpuPreviewView _previewView;             // UI thread
    int _gpuFrameBusy;
    bool _gpuLogged;
    string _gpuNote;
    int _frameRange, _frameMatrix;
    string _frameSubtype;

    volatile bool _snapshotRequested;
    SKImage _snapshot;

    bool _rasterNative;                      // raster path reading a compressed format (MJPG) as the decoder delivers it
    GpuRasterConversion _rasterConversion;
    readonly object _rasterLock = new();

    /// <summary>
    /// Compressed camera formats: the raster path's Bgra8 reader cannot decode and convert them.
    /// </summary>
    static bool IsCompressedSubtype(string subtype) =>
        subtype is not null && (subtype.Equals("MJPG", StringComparison.OrdinalIgnoreCase)
                                || subtype.Equals("H264", StringComparison.OrdinalIgnoreCase)
                                || subtype.Equals("HEVC", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Raster path, camera thread, compressed camera format: the decoded frame (NV12 / YUY2 texture) converted to BGRA on
    /// its own device by the video processor and read back; the BGRA texture also feeds a GPU recording. Null when the
    /// frame has no Direct3D surface (the caller then converts the software bitmap).
    /// </summary>
    SKImage ConvertNativeFrameToRaster(Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface surface)
    {
        var texture = SoftwareBitmapPixels.GetDxgiTexture(surface, typeof(ID3D11Texture2D).GUID, out var subresource);
        if (texture == IntPtr.Zero)
            return null;
        try
        {
            lock (_rasterLock)
            {
                _rasterConversion ??= new GpuRasterConversion();
                var image = _rasterConversion.Convert(texture, subresource, _frameRange, _frameMatrix);
                FeedGpuRecording(_rasterConversion.Target, 0, _rasterConversion.Width, _rasterConversion.Height, DxgiFormatB8G8R8A8);
                return image;
            }
        }
        finally
        {
            Marshal.Release(texture);
        }
    }

    /// <summary>
    /// Set while a consumer on the camera thread needs CPU pixels of every frame (the raster recording path while the
    /// preview runs on the GPU): each frame is then also read back.
    /// </summary>
    internal bool CpuFramesWanted { get; set; }

    /// <summary>
    /// Before MediaCapture.InitializeAsync: decides whether this session runs the preview on the GPU and, when it does,
    /// hands Media Foundation our own device on the UI's adapter. True when our device was attached.
    /// </summary>
    async Task<bool> PrepareGpuCaptureAsync(MediaCaptureInitializationSettings settings)
    {
        _gpuCapture = false;
        _gpuLogged = false;
        _gpuNote = null;
        if (!FormsControl.UseGpuProcessing)
        {
            LogPreviewPath("the CPU", "UseGpuProcessing is off");
            return false;
        }
        if (_gpuDisabledReason != null)
        {
            LogPreviewPath("the CPU", _gpuDisabledReason);
            return false;
        }

        var (ui, reason) = await GpuDevices.UiAdapterAsync();
        if (GpuDevices.TestWarpUiPreview && ui != null)
            ui = GpuDevices.WarpAdapter() ?? ui; // test: pretend the UI renders on the software adapter
        if (ui == null)
        {
            LogPreviewPath("the CPU", reason);
            return false;
        }
        _uiAdapter = ui;
        _gpuCapture = true;
        settings.MemoryPreference = MediaCaptureMemoryPreference.Auto;

        // our device on the UI's adapter, chosen by LUID (test: DRAWNUI_CAMERA_TEST_ADAPTER=warp puts it on WARP)
        var target = GpuDevices.TestWarpCapture ? GpuDevices.WarpAdapter() ?? ui.Value : ui.Value;
        try
        {
            if (_captureDevice == null || !_captureDevice.Adapter.SameAs(target))
            {
                _captureDevice?.Dispose();
                _captureDevice = null;
                _captureDevice = GpuCaptureDevice.Create(target);
            }
            _captureDevice.AttachTo(settings);
            return true;
        }
        catch (Exception e)
        {
            NoteGpuCaptureFallback($"our capture device on {target} failed ({e.Message}); Media Foundation uses its own device");
            return false;
        }
    }

    void NoteGpuCaptureFallback(string note)
    {
        _gpuNote = note;
        Super.Log($"[NativeCameraWindows] {note}", LogLevel.Information);
    }

    string _loggedPreviewPath;

    // ML input (RawCameraFrame.TryGetRgba) of GPU frames
    SKImage _uiImage;                        // render thread: the image handed out last, and where it came from
    int _uiSlot = -1;
    ulong _uiFrame;
    volatile object _rgbaWanted;             // GpuRgbaScaler.Request (boxed: set on the render thread, read on the camera thread)
    long _rgbaWantedAt;                      // Stopwatch timestamp of the last request

    /// <summary>
    /// Diagnostics: ML input reads served from the camera thread's preparation and scaled in the callback, since the camera
    /// set up the current pipeline.
    /// </summary>
    internal (long Prepared, long ScaledNow) RgbaCounts => (_gpuPipeline?.RgbaPrepared ?? 0, _gpuPipeline?.RgbaScaledNow ?? 0);

    /// <summary>
    /// Render thread, inside the raw-frame callback of a GPU frame: RGBA8888 of that same frame, scaled, centre-cropped and
    /// rotated by the video processor on the camera's device. The camera thread prepares it for every frame while a
    /// consumer keeps asking, so here it is normally only a copy of the finished result; the first frame of a new size or
    /// crop is scaled right away. False when the image is not the GPU frame handed out last, or the D3D path failed (the
    /// caller then scales it with Skia).
    /// </summary>
    internal bool TryGetGpuRgba(SKImage rawImage, int width, int height, int rotation, float cropRatio, byte[] buffer)
    {
        if (!_gpuCapture || rawImage == null || !ReferenceEquals(rawImage, _uiImage))
            return false;
        var pipeline = _gpuPipeline;
        var ring = _previewView?.Ring;
        if (pipeline == null || ring == null)
            return false;
        var request = new GpuRgbaScaler.Request(width, height, rotation, cropRatio);
        _rgbaWanted = request;
        Volatile.Write(ref _rgbaWantedAt, Stopwatch.GetTimestamp());
        return pipeline.ReadRgba(ring, _uiSlot, _uiFrame, request, buffer);
    }

    void LogPreviewPath(string path, string detail)
    {
        var message = $"[NativeCameraWindows] preview on {path}: {detail}";
        if (message == _loggedPreviewPath)
            return; // the same setup again after a restart
        _loggedPreviewPath = message;
        Super.Log(message, LogLevel.Information);
    }

    /// <summary>
    /// The YUV range and matrix of the frames, from the frame format's Media Foundation attributes.
    /// </summary>
    void ReadFrameColour(MediaFrameFormat format)
    {
        _frameRange = 0;
        _frameMatrix = 0;
        _frameSubtype = format?.Subtype;
        try
        {
            if (format?.Properties is { } properties)
            {
                if (properties.TryGetValue(MfGuids.MF_MT_VIDEO_NOMINAL_RANGE, out var range) && range is uint r)
                    _frameRange = (int)r;
                if (properties.TryGetValue(MfGuids.MF_MT_YUV_MATRIX, out var matrix) && matrix is uint m)
                    _frameMatrix = (int)m;
            }
        }
        catch
        {
            // unknown: the converter picks the usual defaults
        }
        // a camera's MJPG stream is decoded from JPEG, which is full range
        if (_frameRange == 0 && string.Equals(_frameSubtype, "MJPG", StringComparison.OrdinalIgnoreCase))
            _frameRange = 1;
    }

    /// <summary>
    /// Camera thread, for each frame of a GPU session: converts it into the preview ring, feeds the recording ring, and
    /// hands CPU pixels only to consumers that asked for them.
    /// </summary>
    unsafe void ProcessGpuFrame(VideoMediaFrame videoFrame)
    {
        if (Interlocked.Exchange(ref _gpuFrameBusy, 1) == 1)
        {
            FormsControl?.OnWindowsRecordingSourceDrop();
            return;
        }
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            using var surface = videoFrame.Direct3DSurface;
            if (surface == null)
            {
                DisableGpuCapture("the camera delivers CPU frames, not Direct3D surfaces");
                return;
            }
            var texture = SoftwareBitmapPixels.GetDxgiTexture(surface, typeof(ID3D11Texture2D).GUID, out var subresource);
            if (texture == IntPtr.Zero)
            {
                DisableGpuCapture("no texture behind the camera frame: " + SoftwareBitmapPixels.LastD3DError);
                return;
            }
            try
            {
                _gpuPipeline ??= new GpuCapturePipeline { RgbaFailed = reason => Super.Log($"[NativeCameraWindows] ML input scaled on the render thread: {reason}", LogLevel.Information) };
                var time = DateTime.UtcNow;
                // ML input of this frame, prepared while a consumer keeps asking (it stops a second after the last request)
                var rgba = _rgbaWanted as GpuRgbaScaler.Request?;
                if (rgba != null && Stopwatch.GetElapsedTime(Volatile.Read(ref _rgbaWantedAt)).TotalSeconds > 1)
                    rgba = null;
                var failure = _gpuPipeline.Process(texture, subresource, _frameRange, _frameMatrix, _uiAdapter.Value, time, rgba, out var slot, out var slotTexture);
                if (failure != null)
                {
                    DisableGpuCapture(failure);
                    return;
                }
                if (!_gpuLogged)
                {
                    _gpuLogged = true;
                    var captureDevice = _captureDevice;
                    var ours = captureDevice != null && _gpuPipeline.FramesOn(captureDevice.Device);
                    LogPreviewPath("the GPU",
                        $"UI {_uiAdapter}; capture device {(captureDevice != null && _gpuNote == null ? $"ours on {captureDevice.Adapter}" : "Media Foundation's own")}; " +
                        $"frames arrive on {(ours ? "our device" : "another device")} ({_gpuPipeline.FrameAdapter}); {_frameSubtype} {_gpuPipeline.Ring.Width}x{_gpuPipeline.Ring.Height}, " +
                        $"range {_frameRange} matrix {_frameMatrix}{(_gpuNote != null ? $"; {_gpuNote}" : "")}");
                }
                if (slot < 0)
                    return; // every slot still in use: this frame is skipped

                FeedGpuRecording(slotTexture, 0, (uint)_gpuPipeline.Ring.Width, (uint)_gpuPipeline.Ring.Height, DxgiFormatB8G8R8A8);

                SKImage cpuPixels = null;
                if (CpuFramesWanted)
                    cpuPixels = _gpuPipeline.ReadBack(slotTexture);
                if (_snapshotRequested)
                {
                    _snapshotRequested = false;
                    Interlocked.Exchange(ref _snapshot, _gpuPipeline.ReadBack(slotTexture))?.Dispose();
                }
                GpuCameraMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                DeliverGpuFrame(cpuPixels, time);
            }
            finally
            {
                Marshal.Release(texture);
            }
        }
        catch (Exception e)
        {
            DisableGpuCapture("GPU frame conversion failed: " + e.Message);
        }
        finally
        {
            Volatile.Write(ref _gpuFrameBusy, 0);
        }
    }

    /// <summary>
    /// Camera-thread time of the last GPU frame (conversion + ring), milliseconds.
    /// </summary>
    internal double GpuCameraMs
    {
        get => _lastCameraMs;
        private set => AddCameraTiming(value);
    }

    double _lastCameraMs, _cameraMsSum, _cameraMsMax;
    long _cameraMsCount;
    readonly object _cameraTimingLock = new();

    /// <summary>
    /// Camera-thread cost per frame (GPU conversion, or the raster path's readback and wrap), for diagnostics and tests.
    /// </summary>
    void AddCameraTiming(double ms)
    {
        lock (_cameraTimingLock)
        {
            _lastCameraMs = ms;
            _cameraMsSum += ms;
            _cameraMsCount++;
            if (ms > _cameraMsMax)
                _cameraMsMax = ms;
        }
    }

    /// <summary>
    /// Average and maximum camera-thread milliseconds per frame since the last call, and the frame count.
    /// </summary>
    internal (double Average, double Max, long Frames) TakeCameraTiming()
    {
        lock (_cameraTimingLock)
        {
            var result = (_cameraMsCount > 0 ? _cameraMsSum / _cameraMsCount : 0, _cameraMsMax, _cameraMsCount);
            _cameraMsSum = _cameraMsMax = 0;
            _cameraMsCount = 0;
            return result;
        }
    }

    void DeliverGpuFrame(SKImage cpuPixels, DateTime time)
    {
        var meta = FormsControl.CameraDevice?.Meta;
        var rotation = FormsControl.DeviceRotation;
        if (meta != null)
            Metadata.ApplyRotation(meta, rotation);
        var captured = new CapturedImage
        {
            DeviceRotation = rotation,
            Facing = FormsControl.CameraDevice?.Position ?? FormsControl.Facing,
            Time = time,
            Image = cpuPixels, // null unless a consumer asked for CPU pixels: the preview itself is drawn from the GPU ring
            Meta = meta,
            Rotation = rotation
        };
        try
        {
            UpdateCameraMetadata();
            PreviewCaptureSuccess?.Invoke(captured);
        }
        finally
        {
            captured.Dispose(); // an image nobody took
        }
        // while the preview mirrors a recording only the recorder's frames refresh it (MirrorRecordingToPreview decides),
        // as on the raster path, Android and iOS
        if (FormsControl.UseRecordingFramesForPreview && (FormsControl.IsRecording || FormsControl.IsPreRecording))
            return;
        FormsControl.OnWindowsNativePreviewFrameBuffered();
        FormsControl.UpdatePreview();
    }

    /// <summary>
    /// GPU session: on the UI thread (the paint, with the UI's GL context current) the newest frame as an SKImage of that
    /// context; on any other thread a raster copy of a recent frame (camera thread readback), or null when none is buffered.
    /// </summary>
    SKImage GetGpuPreviewImage()
    {
        if (!MainThread.IsMainThread)
        {
            // as on the raster path and on Android: the buffered frame or null, never waiting, ownership to the caller;
            // the camera thread copies the next frame for the next call
            _snapshotRequested = true;
            return Interlocked.Exchange(ref _snapshot, null);
        }

        var ring = _gpuPipeline?.Ring;
        if (ring == null)
            return null;
        if (_previewView == null || !ReferenceEquals(_previewView.Ring, ring) || _previewView.Display != Angle.eglGetCurrentDisplay())
        {
            _previewView?.Dispose();
            _previewView = GpuPreviewView.Open(ring, out var reason);
            if (_previewView == null)
            {
                DisableGpuCapture(reason);
                return null;
            }
        }
        var image = _previewView.TakeLatest(GetExistingGRContext(), out _, out var slot, out var frame);
        if (image != null)
        {
            // the raw-frame callback gets this image next: its ML input is read from the same slot and frame
            _uiImage = image;
            _uiSlot = slot;
            _uiFrame = frame;
        }
        return image;
    }

    /// <summary>
    /// Turns the GPU preview off for this camera and sets the camera up again on the raster path.
    /// </summary>
    void DisableGpuCapture(string reason)
    {
        if (_gpuDisabledReason != null)
            return;
        _gpuDisabledReason = reason ?? "unknown";
        _gpuCapture = false;
        LogPreviewPath("the CPU", _gpuDisabledReason);
        // Media Foundation keeps its own reference until the capture is released; ours goes now (a lost device is not reused)
        Interlocked.Exchange(ref _captureDevice, null)?.Dispose();
        _ = Task.Run(() =>
        {
            Stop(true);
            Start();
        });
    }

    /// <summary>
    /// With the capture hardware: the converter and the preview ring go (the UI's view follows on its next frame).
    /// </summary>
    void ReleaseGpuCapture()
    {
        lock (_rasterLock)
        {
            _rasterConversion?.Dispose();
            _rasterConversion = null;
        }
        var pipeline = Interlocked.Exchange(ref _gpuPipeline, null);
        if (pipeline != null)
        {
            // not while the camera thread converts a frame into it
            SpinWait.SpinUntil(() => Volatile.Read(ref _gpuFrameBusy) == 0, 500);
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// With the camera: everything of the GPU preview, the UI's view on the UI thread.
    /// </summary>
    void DisposeGpuCapture()
    {
        ReleaseGpuCapture();
        var view = Interlocked.Exchange(ref _previewView, null);
        if (view != null)
            MainThread.BeginInvokeOnMainThread(view.Dispose);
        _captureDevice?.Dispose();
        _captureDevice = null;
        Interlocked.Exchange(ref _snapshot, null)?.Dispose();
    }
}
