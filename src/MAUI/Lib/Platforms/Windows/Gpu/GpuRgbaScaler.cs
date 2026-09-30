namespace DrawnUi.Camera.Gpu;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

/// <summary>
/// ML input on the GPU (Windows): a BGRA ring slot scaled, centre-cropped and rotated by the D3D11 video processor into a
/// small RGBA texture, copied to a staging texture of that slot. The camera thread prepares it for every frame while a
/// consumer asks (<see cref="Prepare"/>); the raw-frame callback of the same frame then only maps the finished staging
/// texture (<see cref="TryRead"/>), so the bytes always belong to the frame the callback was given.
/// </summary>
internal sealed unsafe class GpuRgbaScaler : IDisposable
{
    /// <summary>What a consumer asked for: final size, extra rotation (degrees) and centre crop.</summary>
    public readonly record struct Request(int Width, int Height, int Rotation, float CropRatio);

    readonly ID3D11Device* _device;
    readonly ID3D11DeviceContext* _context;
    readonly ID3D11VideoDevice* _videoDevice;
    readonly ID3D11VideoContext* _videoContext;
    ID3D11VideoProcessorEnumerator* _enum;
    ID3D11VideoProcessor* _vp;
    uint _inWidth, _inHeight;
    ID3D11Texture2D* _output;
    ID3D11VideoProcessorOutputView* _outputView;
    DXGI_FORMAT _outputFormat;
    Request _size;
    Request? _stateFor; // the request the processor's rects and rotation were set for (null after a new processor)
    readonly ID3D11Texture2D*[] _staging = new ID3D11Texture2D*[GpuFrameRing.Slots];
    readonly Request?[] _prepared = new Request?[GpuFrameRing.Slots];
    readonly ulong[] _preparedFrame = new ulong[GpuFrameRing.Slots]; // the ring's publish counter of the frame prepared
    readonly Dictionary<nint, nint> _inputViews = new(); // ring slot texture -> input view (ring textures are stable)
    readonly object _lock = new(); // the camera thread prepares, the raw-frame callback reads
    bool _disposed;

    public GpuRgbaScaler(ID3D11Device* device)
    {
        _device = device;
        device->AddRef();
        ID3D11DeviceContext* context;
        device->GetImmediateContext(&context);
        _context = context;
        ID3D11VideoDevice* videoDevice;
        GpuDevices.ThrowIfFailed(device->QueryInterface(__uuidof<ID3D11VideoDevice>(), (void**)&videoDevice), "ID3D11VideoDevice (ML input)");
        _videoDevice = videoDevice;
        ID3D11VideoContext* videoContext;
        GpuDevices.ThrowIfFailed(context->QueryInterface(__uuidof<ID3D11VideoContext>(), (void**)&videoContext), "ID3D11VideoContext (ML input)");
        _videoContext = videoContext;
    }

    /// <summary>
    /// Camera thread, right after the frame landed in <paramref name="slot"/>: queues the scale into that slot's staging
    /// texture. Nothing waits for the GPU here.
    /// </summary>
    public void Prepare(int slot, ID3D11Texture2D* slotTexture, Request request, ulong frame)
    {
        lock (_lock)
        {
            if (!_disposed)
                PrepareLocked(slot, slotTexture, request, frame);
        }
    }

    /// <summary>
    /// The raw-frame callback, first frame of a new request: scales now and waits for it (the next frames are prepared).
    /// </summary>
    public bool PrepareAndRead(int slot, ID3D11Texture2D* slotTexture, Request request, ulong frame, byte[] buffer)
    {
        lock (_lock)
        {
            if (_disposed)
                return false;
            PrepareLocked(slot, slotTexture, request, frame);
            return ReadLocked(slot, request, frame, buffer);
        }
    }

    void PrepareLocked(int slot, ID3D11Texture2D* slotTexture, Request request, ulong frame)
    {
        D3D11_TEXTURE2D_DESC desc;
        slotTexture->GetDesc(&desc);
        Ensure(desc.Width, desc.Height, request);

        if (_stateFor != request) // the processor keeps its state: set once per request, not per frame
        {
            // the crop is chosen for the size before the rotation, as the raster path does
            SkiaCamera.GetDrawSizeForOutputRotation(request.Width, request.Height, request.Rotation, out var drawWidth, out var drawHeight);
            var crop = SkiaCamera.GetCenterCropSourceRect((int)desc.Width, (int)desc.Height, drawWidth, drawHeight, request.CropRatio);
            var source = new RECT { left = (int)MathF.Round(crop.Left), top = (int)MathF.Round(crop.Top), right = (int)MathF.Round(crop.Right), bottom = (int)MathF.Round(crop.Bottom) };
            var target = new RECT { left = 0, top = 0, right = request.Width, bottom = request.Height };
            _videoContext->VideoProcessorSetStreamSourceRect(_vp, 0, BOOL.TRUE, &source);
            _videoContext->VideoProcessorSetStreamDestRect(_vp, 0, BOOL.TRUE, &target);
            _videoContext->VideoProcessorSetOutputTargetRect(_vp, BOOL.TRUE, &target);
            _videoContext->VideoProcessorSetStreamRotation(_vp, 0, request.Rotation != 0 ? BOOL.TRUE : BOOL.FALSE,
                (D3D11_VIDEO_PROCESSOR_ROTATION)(request.Rotation / 90)); // clockwise, as the raster path's canvas rotation
            _stateFor = request;
        }

        var stream = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = BOOL.TRUE, pInputSurface = InputView(slotTexture) };
        GpuDevices.ThrowIfFailed(_videoContext->VideoProcessorBlt(_vp, _outputView, 0, 1, &stream), "VideoProcessorBlt ML input");
        _context->CopyResource((ID3D11Resource*)_staging[slot], (ID3D11Resource*)_output);
        _prepared[slot] = request;
        _preparedFrame[slot] = frame;
    }

    /// <summary>
    /// The raw-frame callback: copies the prepared pixels of <paramref name="slot"/> into <paramref name="buffer"/> as
    /// RGBA8888. False when that slot was not prepared for this request (the caller scales another way).
    /// </summary>
    public bool TryRead(int slot, Request request, ulong frame, byte[] buffer)
    {
        lock (_lock)
            return !_disposed && ReadLocked(slot, request, frame, buffer);
    }

    bool ReadLocked(int slot, Request request, ulong frame, byte[] buffer)
    {
        if (_prepared[slot] != request || _preparedFrame[slot] != frame)
            return false;
        D3D11_MAPPED_SUBRESOURCE mapped;
        // the copy was queued when the frame arrived; it has normally finished by the time the frame is drawn
        if (_context->Map((ID3D11Resource*)_staging[slot], 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped).FAILED)
            return false;
        try
        {
            var row = request.Width * 4;
            fixed (byte* dst = buffer)
            {
                for (var y = 0; y < request.Height; y++)
                {
                    var from = (byte*)mapped.pData + y * mapped.RowPitch;
                    var to = dst + y * row;
                    if (_outputFormat == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM)
                    {
                        Buffer.MemoryCopy(from, to, row, row);
                    }
                    else
                    {
                        for (var x = 0; x < row; x += 4) // BGRA -> RGBA
                        {
                            to[x] = from[x + 2];
                            to[x + 1] = from[x + 1];
                            to[x + 2] = from[x];
                            to[x + 3] = from[x + 3];
                        }
                    }
                }
            }
            return true;
        }
        finally
        {
            _context->Unmap((ID3D11Resource*)_staging[slot], 0);
        }
    }

    void Ensure(uint inWidth, uint inHeight, Request request)
    {
        if (_vp != null && inWidth == _inWidth && inHeight == _inHeight && request.Width == _size.Width && request.Height == _size.Height)
            return;
        Release();
        var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputWidth = inWidth,
            InputHeight = inHeight,
            OutputWidth = (uint)request.Width,
            OutputHeight = (uint)request.Height,
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
        };
        ID3D11VideoProcessorEnumerator* vpEnum;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorEnumerator(&content, &vpEnum), "video processor enumerator (ML input)");
        _enum = vpEnum;
        uint support;
        _outputFormat = _enum->CheckVideoProcessorFormat(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, &support).SUCCEEDED
                        && (support & (uint)D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT.D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) != 0
            ? DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM
            : DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        ID3D11VideoProcessor* vp;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessor(_enum, 0, &vp), "video processor (ML input)");
        _vp = vp;
        var rgb = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE { RGB_Range = 0 }; // full-range RGB in and out: no level change
        _videoContext->VideoProcessorSetStreamColorSpace(_vp, 0, &rgb);
        _videoContext->VideoProcessorSetOutputColorSpace(_vp, &rgb);

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)request.Width,
            Height = (uint)request.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = _outputFormat,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
        };
        ID3D11Texture2D* output;
        GpuDevices.ThrowIfFailed(_device->CreateTexture2D(&desc, null, &output), "ML input texture");
        _output = output;
        var viewDesc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC { ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D };
        ID3D11VideoProcessorOutputView* view;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorOutputView((ID3D11Resource*)_output, _enum, &viewDesc, &view), "ML input output view");
        _outputView = view;

        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        for (var i = 0; i < GpuFrameRing.Slots; i++)
        {
            ID3D11Texture2D* staging;
            GpuDevices.ThrowIfFailed(_device->CreateTexture2D(&desc, null, &staging), "ML input staging texture");
            _staging[i] = staging;
        }
        _inWidth = inWidth;
        _inHeight = inHeight;
        _size = request;
    }

    ID3D11VideoProcessorInputView* InputView(ID3D11Texture2D* texture)
    {
        if (_inputViews.TryGetValue((nint)texture, out var cached))
            return (ID3D11VideoProcessorInputView*)cached;
        var desc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D };
        ID3D11VideoProcessorInputView* view;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)texture, _enum, &desc, &view), "ML input view of a ring slot");
        texture->AddRef();
        _inputViews[(nint)texture] = (nint)view;
        return view;
    }

    /// <summary>
    /// Camera thread, when the ring is replaced: drops the views of the old ring's textures (and their references).
    /// </summary>
    public void ReleaseInputs()
    {
        lock (_lock)
        {
            foreach (var view in _inputViews)
            {
                ((ID3D11VideoProcessorInputView*)view.Value)->Release();
                ((ID3D11Texture2D*)view.Key)->Release();
            }
            _inputViews.Clear();
            Array.Clear(_prepared);
        }
    }

    void Release()
    {
        foreach (var view in _inputViews)
        {
            ((ID3D11VideoProcessorInputView*)view.Value)->Release();
            ((ID3D11Texture2D*)view.Key)->Release();
        }
        _inputViews.Clear();
        for (var i = 0; i < GpuFrameRing.Slots; i++)
        {
            if (_staging[i] != null)
                _staging[i]->Release();
            _staging[i] = null;
            _prepared[i] = null;
        }
        if (_outputView != null)
            _outputView->Release();
        _outputView = null;
        if (_output != null)
            _output->Release();
        _output = null;
        if (_vp != null)
            _vp->Release();
        _vp = null;
        _stateFor = null;
        if (_enum != null)
            _enum->Release();
        _enum = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Release();
        }
        _videoContext->Release();
        _videoDevice->Release();
        _context->Release();
        _device->Release();
    }
}
