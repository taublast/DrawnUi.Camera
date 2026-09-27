using System.Diagnostics;
using DrawnUi.Camera.Gpu;
using DrawnUi.Camera.Platforms.Windows;

namespace DrawnUi.Camera;

/// <summary>
/// Windows recording on the GPU: camera frames go through a shared ring to a recorder thread with its own device on the UI's
/// adapter, RenderFrameForRecording and ProcessFrame draw on a GPU canvas there, and NV12 samples reach the hardware encoder
/// without a readback. The raster path stays as the automatic fallback, with the reason logged once.
/// </summary>
public partial class SkiaCamera
{
    public static readonly BindableProperty UseGpuProcessingProperty = BindableProperty.Create(
        nameof(UseGpuProcessing),
        typeof(bool),
        typeof(SkiaCamera),
        true,
        propertyChanged: NeedRestart);

    /// <summary>
    /// Windows: process camera frames on the GPU when the machine allows it (default true): the preview is converted and
    /// drawn without a CPU copy, and recordings are composed and encoded on the GPU. When it does not (no accelerated canvas,
    /// camera frames on another adapter than the UI, missing driver features) the raster path is used, the reason is logged
    /// once, and <see cref="RecordingReport"/> says why for recordings. Changing it while the camera is on restarts the
    /// camera; a recording reads it when it starts.
    /// </summary>
    public bool UseGpuProcessing
    {
        get => (bool)GetValue(UseGpuProcessingProperty);
        set => SetValue(UseGpuProcessingProperty, value);
    }

    /// <summary>
    /// Windows: how the current or last recording was produced (path, adapters, fallback reason, frame counts).
    /// </summary>
    public WindowsRecordingReport RecordingReport { get; private set; }

    GpuRecorder _gpuRecorder;
    GpuFrameRing _gpuRing;
    long _reportOffered;
    readonly Stopwatch _reportClock = new();
    static readonly HashSet<string> LoggedGpuReasons = new();

    /// <summary>
    /// A running GPU recorder with the camera ring armed, or null with <see cref="RecordingReport"/> saying why.
    /// Reuses the running one (pre-recording turning into the live recording).
    /// </summary>
    async Task<GpuRecorder> PrepareGpuRecordingAsync(WindowsRecordingReport report)
    {
        if (_gpuRecorder != null)
        {
            report.Path = "gpu";
            report.UiAdapter = report.EncoderAdapter = _gpuRecorder.Adapter.ToString();
            report.CaptureAdapter = _gpuRing?.Adapter.ToString();
            return _gpuRecorder;
        }

        string reason = null;
        GpuRecorder recorder = null;
        GpuFrameRing ring = null;
        try
        {
            if (!UseGpuProcessing)
                reason = "UseGpuProcessing is off";
            else if (!EnableVideoRecording)
                reason = "audio-only recording";
            else if (NativeControl is not NativeCamera camera)
                reason = "no native camera";
            else
            {
                var (ui, uiReason) = await GpuDevices.UiAdapterAsync();
                if (GpuDevices.TestWarpUi && ui != null)
                    ui = GpuDevices.WarpAdapter(); // test: pretend the UI renders on the software adapter
                report.UiAdapter = ui?.ToString();
                if (ui == null)
                {
                    reason = uiReason;
                }
                else
                {
                    ring = camera.CreateRecordingRing(out reason);
                    if (ring != null)
                    {
                        report.CaptureAdapter = ring.Adapter.ToString();
                        if (!ring.Adapter.SameAs(ui.Value))
                        {
                            reason = $"camera frames are on {ring.Adapter}, the UI on {ui.Value}";
                        }
                        else
                        {
                            recorder = await Task.Run(() => GpuRecorder.Create(ui.Value));
                            report.EncoderAdapter = recorder.Adapter.ToString();
                            recorder.OpenRing(ring);
                            recorder.FrameArrived = ProcessGpuRecordingFrame;
                            camera.RecordingRingStale = () => Task.Run(ReplaceGpuRing);
                            camera.ArmRecordingRing(ring);
                            _gpuRing = ring;
                            _gpuRecorder = recorder;
                            report.Path = "gpu";
                            Super.Log($"[SkiaCamera] recording on the GPU: UI {report.UiAdapter}, camera frames {report.CaptureAdapter}, encoder {report.EncoderAdapter}", Microsoft.Extensions.Logging.LogLevel.Information);
                            return recorder;
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            reason = "GPU recording setup failed: " + e.Message;
        }

        recorder?.Dispose();
        ring?.Dispose();
        report.Path = "cpu";
        report.FallbackReason = reason;
        lock (LoggedGpuReasons)
        {
            if (LoggedGpuReasons.Add(reason ?? string.Empty))
                Super.Log($"[SkiaCamera] recording on the CPU: {reason}. UI adapter {report.UiAdapter ?? "unknown"}, camera frames {report.CaptureAdapter ?? "unknown"}", Microsoft.Extensions.Logging.LogLevel.Information);
        }
        return null;
    }

    /// <summary>
    /// The camera was set up again during a GPU recording (new device or frame size): a new ring on the new device replaces
    /// the old one. When the new device is on another adapter the recording keeps its last frame and says so in the log.
    /// </summary>
    void ReplaceGpuRing()
    {
        var recorder = _gpuRecorder;
        if (recorder == null || NativeControl is not NativeCamera camera)
            return;
        var ring = camera.CreateRecordingRing(out var reason);
        try
        {
            if (ring == null)
                throw new InvalidOperationException(reason);
            recorder.OpenRing(ring); // throws when the camera moved to another adapter
        }
        catch (Exception e)
        {
            ring?.Dispose();
            Super.Log($"[SkiaCamera] GPU recording cannot follow the camera restart: {e.Message}");
            return;
        }
        camera.ArmRecordingRing(ring);
        Interlocked.Exchange(ref _gpuRing, ring)?.Dispose();
        if (RecordingReport != null)
            RecordingReport.CaptureAdapter = ring.Adapter.ToString();
    }

    /// <summary>
    /// Recorder thread, for each new camera frame: composes the recording frame on the GPU canvas and submits it.
    /// </summary>
    void ProcessGpuRecordingFrame()
    {
        var recorder = _gpuRecorder;
        if (recorder == null)
            return;
        if ((!IsPreRecording && !IsRecording) || _captureVideoEncoder is not WindowsCaptureVideoEncoder { IsGpu: true } encoder)
        {
            recorder.TakeLatestFrame(out _); // keep releasing slots so the camera never waits on us
            return;
        }

        var image = recorder.TakeLatestFrame(out var time);
        if (image == null)
            return;

        var start = _captureVideoStartTime.Kind == DateTimeKind.Utc ? _captureVideoStartTime : _captureVideoStartTime.ToUniversalTime();
        var elapsed = time - start;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        using (encoder.BeginFrame(elapsed, out var canvas, out var info))
        {
            // the raw camera frame before any overlay, from the recording loop as on the raster path, Android and iOS, once
            // the preview mirrors the recording (until then the live preview fires it)
            if (UseRecordingFramesForPreview)
                OnRawFrameAvailable(CreateRawCameraFrameInternal(image, 0));

            if (canvas == null)
                return;

            ComposeRecordingFrame(canvas, info, image, elapsed);

            _diagSubmitSw.Restart();
            encoder.SubmitFrameAsync().GetAwaiter().GetResult(); // synchronous in GPU mode
            _diagSubmitSw.Stop();
            _diagLastSubmitMs = _diagSubmitSw.Elapsed.TotalMilliseconds;
            Interlocked.Increment(ref _diagSubmittedFrames);
            CalculateRecordingFps();
        }
    }

    GpuPreviewView _mirrorView; // UI thread
    bool _mirrorFailureLogged;

    /// <summary>
    /// UI thread, while a GPU recording is mirrored into the preview: the newest composed recording frame as an SKImage
    /// of the UI's context (no copy), or null when nothing is newer.
    /// </summary>
    SKImage GetGpuMirrorImage()
    {
        var ring = _gpuRecorder?.Mirror;
        if (ring == null)
            return null;
        if (_mirrorView == null || !ReferenceEquals(_mirrorView.Ring, ring) || _mirrorView.Display != Angle.eglGetCurrentDisplay())
        {
            _mirrorView?.Dispose();
            _mirrorView = GpuPreviewView.Open(ring, out var reason);
            if (_mirrorView == null)
            {
                if (!_mirrorFailureLogged)
                {
                    _mirrorFailureLogged = true;
                    Super.Log($"[SkiaCamera] the recording cannot be shown in the preview: {reason}", Microsoft.Extensions.Logging.LogLevel.Information);
                }
                return null;
            }
        }
        return _mirrorView.TakeLatest(Superview?.GetGRContext(), out _, out _, out _);
    }

    /// <summary>
    /// UI thread (the view's GL textures belong to the UI's context): drops the view of the recording mirror.
    /// </summary>
    void ReleaseGpuMirrorView()
    {
        _mirrorView?.Dispose();
        _mirrorView = null;
    }

    /// <summary>
    /// Stops the GPU recorder and the camera ring once no encoder is left (the pre-recording to live transition stops the old
    /// encoder while the new one is already set, so the recorder survives it).
    /// </summary>
    void StopGpuRecordingIfIdle()
    {
        if (_captureVideoEncoder != null)
            return;
        (NativeControl as NativeCamera)?.DisarmRecordingRing();
        var recorder = Interlocked.Exchange(ref _gpuRecorder, null);
        var ring = Interlocked.Exchange(ref _gpuRing, null);
        recorder?.Dispose();
        ring?.Dispose();
    }

    /// <summary>
    /// A new report when a recording starts. At the pre-recording to live transition the path and adapters are kept and the
    /// counts restart: they describe the live part the encoder writes.
    /// </summary>
    WindowsRecordingReport BeginRecordingReport(bool continuing)
    {
        if (!continuing || RecordingReport == null)
            RecordingReport = new WindowsRecordingReport();
        Interlocked.Exchange(ref _reportOffered, 0);
        _reportClock.Restart();
        return RecordingReport;
    }

    void FinishRecordingReport(ICaptureVideoEncoder encoder)
    {
        var report = RecordingReport;
        if (report == null)
            return;
        report.FramesOffered = Interlocked.Read(ref _reportOffered);
        if (encoder is WindowsCaptureVideoEncoder windows)
            report.FramesWritten = windows.EncodedFrameCount;
        report.Elapsed = _reportClock.Elapsed;
    }
}
