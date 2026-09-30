namespace DrawnUi.Camera.Gpu;

using System.Runtime.InteropServices;
using SkiaSharp;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

/// <summary>
/// Our own D3D11 device for Media Foundation, created on a chosen adapter (the UI's), handed to MediaCapture through
/// IAdvancedMediaCaptureInitializationSettings.SetDirectxDeviceManager. Frames then arrive on a device that shares the UI's
/// adapter, instead of wherever Media Foundation would have put them.
/// </summary>
internal sealed unsafe class GpuCaptureDevice : IDisposable
{
    public readonly GpuDevices.Adapter Adapter;
    ID3D11Device* _device;
    IMFDXGIDeviceManager* _manager;

    public nint Device => (nint)_device;

    static readonly Guid IidAdvancedInitializationSettings = new("3DE21209-8BA6-4f2a-A577-2819B56FF14D"); // mfcaptureengine / windows.media.capture.h

    GpuCaptureDevice(GpuDevices.Adapter adapter) => Adapter = adapter;

    public static GpuCaptureDevice Create(GpuDevices.Adapter adapter)
    {
        var created = new GpuCaptureDevice(adapter);
        try
        {
            created._device = GpuDevices.CreateDevice(adapter, out var context);
            context->Release();
            uint token;
            IMFDXGIDeviceManager* manager;
            GpuDevices.ThrowIfFailed(MFCreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
            created._manager = manager;
            GpuDevices.ThrowIfFailed(manager->ResetDevice((IUnknown*)created._device, token), "IMFDXGIDeviceManager.ResetDevice");
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Hands the device to a MediaCaptureInitializationSettings before InitializeAsync.
    /// </summary>
    public void AttachTo(object settings)
    {
        var raw = ((WinRT.IWinRTObject)settings).NativeObject.ThisPtr;
        var iid = IidAdvancedInitializationSettings;
        GpuDevices.ThrowIfFailed(new HRESULT(Marshal.QueryInterface(raw, in iid, out var advanced)), "IAdvancedMediaCaptureInitializationSettings");
        try
        {
            // SetDirectxDeviceManager is the first method after IUnknown (vtable slot 3)
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)(*(void***)advanced)[3])(advanced, (nint)_manager);
            GpuDevices.ThrowIfFailed(new HRESULT(hr), "SetDirectxDeviceManager");
        }
        finally
        {
            Marshal.Release(advanced);
        }
    }

    public void Dispose()
    {
        if (_manager != null)
            _manager->Release();
        _manager = null;
        if (_device != null)
            _device->Release();
        _device = null;
    }
}

/// <summary>
/// Raster path, camera thread, for compressed camera formats (MJPG): the Bgra8 frame reader cannot decode and convert them
/// (its start fails with OutputFormatNotSupported), so the reader delivers the decoder's output (NV12 / YUY2 textures) and
/// the video processor converts each frame on its own device into a BGRA texture that is read back.
/// </summary>
internal sealed unsafe class GpuRasterConversion : IDisposable
{
    ID3D11Device* _device; // AddRef'd
    GpuFrameConverter _converter;
    ID3D11Texture2D* _target;
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    /// <summary>The BGRA texture of the last conversion (for the GPU recording feed).</summary>
    public nint Target => (nint)_target;

    /// <summary>
    /// Converts one frame (a texture of any device, this subresource) and reads the BGRA result back.
    /// </summary>
    public SKImage Convert(nint texture, uint subresource, int nominalRange, int yuvMatrix)
    {
        var source = (ID3D11Texture2D*)texture;
        ID3D11Device* device;
        source->GetDevice(&device);
        try
        {
            if (device != _device)
            {
                Release();
                _device = device;
                device->AddRef();
                _converter = new GpuFrameConverter(device);
            }
            D3D11_TEXTURE2D_DESC desc;
            source->GetDesc(&desc);
            if (_target == null || Width != desc.Width || Height != desc.Height)
            {
                _converter.ReleaseTargets();
                if (_target != null)
                    _target->Release();
                _target = null;
                var targetDesc = new D3D11_TEXTURE2D_DESC
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET),
                };
                ID3D11Texture2D* target;
                GpuDevices.ThrowIfFailed(device->CreateTexture2D(&targetDesc, null, &target), "BGRA texture for the raster path");
                _target = target;
                Width = desc.Width;
                Height = desc.Height;
            }
            _converter.Convert(source, subresource, nominalRange, yuvMatrix, _target);
            return _converter.ReadBack(_target);
        }
        finally
        {
            device->Release();
        }
    }

    void Release()
    {
        _converter?.Dispose(); // also drops its view of the target
        _converter = null;
        if (_target != null)
            _target->Release();
        _target = null;
        if (_device != null)
            _device->Release();
        _device = null;
        Width = Height = 0;
    }

    public void Dispose() => Release();
}

/// <summary>
/// Camera thread: each camera frame, on whatever device Media Foundation delivered it, converted into the BGRA preview ring
/// (<see cref="GpuFrameRing"/>) on that device. The ring and the converter follow the frame's device and size.
/// </summary>
internal sealed unsafe class GpuCapturePipeline : IDisposable
{
    public GpuFrameRing Ring { get; private set; }
    public GpuDevices.Adapter? FrameAdapter { get; private set; }
    ID3D11Device* _frameDevice; // AddRef'd
    GpuFrameConverter _converter;
    GpuRgbaScaler _rgba;
    string _rgbaFailure;
    readonly object _lifetime = new(); // the render thread reads ML input while the camera thread may replace the ring
    readonly System.Diagnostics.Stopwatch _age = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>True when the frames arrive on this device (the one we gave Media Foundation).</summary>
    public bool FramesOn(nint device) => (nint)_frameDevice == device;

    /// <summary>
    /// Converts one frame into a free ring slot and publishes it. Returns null on success (slot -1 when every slot was still
    /// in use and the frame was skipped), or why the frame cannot be used on the GPU (another adapter than the UI's).
    /// With <paramref name="rgba"/> the ML input of that frame is queued too, before the frame is published, so it is
    /// ready when the frame is drawn.
    /// </summary>
    public string Process(nint texture, uint subresource, int nominalRange, int yuvMatrix, GpuDevices.Adapter uiAdapter, DateTime time,
        GpuRgbaScaler.Request? rgba, out int slot, out nint slotTexture)
    {
        slot = -1;
        slotTexture = 0;
        if (GpuDevices.TestDeviceLostAfter >= 0 && _age.Elapsed.TotalSeconds > GpuDevices.TestDeviceLostAfter)
            GpuDevices.ThrowIfFailed(new HRESULT(unchecked((int)0x887A0005)), "frame conversion (test: device removed)");
        var source = (ID3D11Texture2D*)texture;
        ID3D11Device* device;
        source->GetDevice(&device);
        try
        {
            if (device != _frameDevice)
            {
                // first frame, or the camera was set up again on another device
                var adapter = GpuDevices.AdapterOf(device);
                if (adapter == null || !adapter.Value.SameAs(uiAdapter))
                    return $"camera frames are on {adapter?.ToString() ?? "an unknown adapter"}, the UI on {uiAdapter}";
                ReleaseDevice();
                _frameDevice = device;
                device->AddRef();
                FrameAdapter = adapter;
                _converter = new GpuFrameConverter(device);
            }

            D3D11_TEXTURE2D_DESC desc;
            source->GetDesc(&desc);
            if (Ring == null || Ring.Width != desc.Width || Ring.Height != desc.Height)
            {
                lock (_lifetime)
                {
                    _converter.ReleaseTargets();
                    _rgba?.ReleaseInputs();
                    ReleaseStampViews();
                    Ring?.Dispose();
                    Ring = GpuFrameRing.Create(device, (int)desc.Width, (int)desc.Height, out var reason);
                    if (Ring == null)
                        return reason;
                }
            }

            if (!Ring.TryAcquireSlot(out slot, out var target))
            {
                slot = -1;
                return null;
            }
            _converter.Convert(source, subresource, nominalRange, yuvMatrix, target);
            if (GpuDevices.TestStamp)
                StampFrame(device, target, Ring.NextFrame);
            if (rgba is { } request && _rgbaFailure == null)
            {
                try
                {
                    _rgba ??= new GpuRgbaScaler(device);
                    _rgba.Prepare(slot, target, request, Ring.NextFrame);
                }
                catch (Exception e)
                {
                    _rgbaFailure = e.Message; // the ML input goes back to the render thread's path; the preview is not affected
                    RgbaFailed?.Invoke(_rgbaFailure);
                }
            }
            Ring.Publish(slot, time);
            slotTexture = (nint)target;
            return null;
        }
        finally
        {
            device->Release();
        }
    }

    /// <summary>ML input reads served from the camera thread's preparation, and scaled in the callback (diagnostics).</summary>
    public long RgbaPrepared, RgbaScaledNow;

    /// <summary>Raised once, on the camera thread, when the ML input cannot be prepared on this device.</summary>
    public Action<string> RgbaFailed;

    /// <summary>
    /// Raw-frame callback (render thread): the ML input of the frame in <paramref name="slot"/>, prepared on the camera
    /// thread, or scaled right now for the first frame of a new request. False when the D3D path is not available.
    /// </summary>
    public bool ReadRgba(GpuFrameRing ring, int slot, ulong frame, GpuRgbaScaler.Request request, byte[] buffer)
    {
        lock (_lifetime)
            return ReadRgbaLocked(ring, slot, frame, request, buffer);
    }

    bool ReadRgbaLocked(GpuFrameRing ring, int slot, ulong frame, GpuRgbaScaler.Request request, byte[] buffer)
    {
        if (_rgbaFailure != null || slot < 0 || ring == null || !ReferenceEquals(ring, Ring))
            return false; // the frame's ring was replaced meanwhile
        try
        {
            var scaler = _rgba;
            if (scaler != null && scaler.TryRead(slot, request, frame, buffer))
            {
                RgbaPrepared++;
                return true;
            }
            if (scaler == null)
                return false; // the camera thread creates it with the first request
            RgbaScaledNow++;
            return scaler.PrepareAndRead(slot, ring.SlotTexture(slot), request, frame, buffer);
        }
        catch (Exception e)
        {
            _rgbaFailure = e.Message;
            RgbaFailed?.Invoke(_rgbaFailure);
            return false;
        }
    }

    /// <summary>
    /// Camera thread: a raster copy of a ring slot, for consumers that need CPU pixels.
    /// </summary>
    public SKImage ReadBack(nint slotTexture) => _converter?.ReadBack((ID3D11Texture2D*)slotTexture);

    readonly Dictionary<nint, nint> _stampViews = new(); // test stamp: ring slot texture -> render target view

    /// <summary>
    /// Test only (<see cref="GpuDevices.TestStamp"/>): the frame number as 20 black or white cells across the middle of
    /// the frame's top band (x 30-70 %, y 0-10 %), cleared into the ring slot on the camera's device.
    /// </summary>
    void StampFrame(ID3D11Device* device, ID3D11Texture2D* target, ulong frame)
    {
        if (!_stampViews.TryGetValue((nint)target, out var view))
        {
            ID3D11RenderTargetView* rtv;
            GpuDevices.ThrowIfFailed(device->CreateRenderTargetView((ID3D11Resource*)target, null, &rtv), "stamp view");
            target->AddRef();
            _stampViews[(nint)target] = view = (nint)rtv;
        }
        ID3D11DeviceContext* context;
        device->GetImmediateContext(&context);
        ID3D11DeviceContext1* context1;
        var hr = context->QueryInterface(__uuidof<ID3D11DeviceContext1>(), (void**)&context1);
        context->Release();
        if (hr.FAILED)
            return;
        var white = stackalloc float[] { 1, 1, 1, 1 };
        var black = stackalloc float[] { 0, 0, 0, 1 };
        float width = Ring.Width, height = Ring.Height, cell = width * 0.02f;
        for (var i = 0; i < 20; i++)
        {
            var rect = new RECT { left = (int)(width * 0.3f + i * cell), right = (int)(width * 0.3f + (i + 1) * cell), top = 0, bottom = (int)(height * 0.1f) };
            context1->ClearView((ID3D11View*)view, ((frame >> i) & 1) != 0 ? white : black, &rect, 1);
        }
        context1->Release();
    }

    void ReleaseStampViews()
    {
        foreach (var view in _stampViews)
        {
            ((ID3D11RenderTargetView*)view.Value)->Release();
            ((ID3D11Texture2D*)view.Key)->Release();
        }
        _stampViews.Clear();
    }

    void ReleaseDevice()
    {
        ReleaseStampViews();
        lock (_lifetime)
        {
            _rgba?.Dispose();
            _rgba = null;
            Ring?.Dispose();
            Ring = null;
        }
        _converter?.Dispose();
        _converter = null;
        if (_frameDevice != null)
            _frameDevice->Release();
        _frameDevice = null;
        FrameAdapter = null;
    }

    public void Dispose() => ReleaseDevice();
}
