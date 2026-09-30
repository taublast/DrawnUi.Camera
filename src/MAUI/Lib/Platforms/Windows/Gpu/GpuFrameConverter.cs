namespace DrawnUi.Camera.Gpu;

using SkiaSharp;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

/// <summary>
/// Camera thread: turns a camera frame as Media Foundation delivers it (NV12, YUY2 or BGRA texture, possibly one slice of an
/// array) into a BGRA texture with the D3D11 video processor, following the frame's own YUV range and matrix. Runs on the
/// device the frame lives on. Also reads a BGRA texture back into a raster image for the few consumers that need one.
/// </summary>
internal sealed unsafe class GpuFrameConverter : IDisposable
{
    readonly ID3D11Device* _device;
    readonly ID3D11DeviceContext* _context;
    readonly ID3D11VideoDevice* _videoDevice;
    readonly ID3D11VideoContext* _videoContext;
    ID3D11VideoProcessorEnumerator* _enum;
    ID3D11VideoProcessor* _vp;
    uint _inWidth, _inHeight, _outWidth, _outHeight;
    DXGI_FORMAT _inFormat;
    (int Range, int Matrix) _colour = (-1, -1);
    readonly Dictionary<nint, nint> _outputViews = new(); // ring slot texture -> output view (stable textures)
    ID3D11Texture2D* _staging;
    uint _stagingWidth, _stagingHeight;

    /// <summary>The device frames are converted on.</summary>
    public nint Device => (nint)_device;

    public GpuFrameConverter(ID3D11Device* device)
    {
        _device = device;
        device->AddRef();
        ID3D11DeviceContext* context;
        device->GetImmediateContext(&context);
        _context = context;
        ID3D11VideoDevice* videoDevice;
        GpuDevices.ThrowIfFailed(device->QueryInterface(__uuidof<ID3D11VideoDevice>(), (void**)&videoDevice), "ID3D11VideoDevice on the camera's device");
        _videoDevice = videoDevice;
        ID3D11VideoContext* videoContext;
        GpuDevices.ThrowIfFailed(context->QueryInterface(__uuidof<ID3D11VideoContext>(), (void**)&videoContext), "ID3D11VideoContext");
        _videoContext = videoContext;
    }

    /// <summary>
    /// Converts <paramref name="source"/> (this subresource) into the BGRA <paramref name="target"/> of the same device.
    /// <paramref name="nominalRange"/> and <paramref name="yuvMatrix"/> are the frame's MF_MT_VIDEO_NOMINAL_RANGE and
    /// MF_MT_YUV_MATRIX (0 when the format does not say).
    /// </summary>
    public void Convert(ID3D11Texture2D* source, uint subresource, int nominalRange, int yuvMatrix, ID3D11Texture2D* target)
    {
        D3D11_TEXTURE2D_DESC inDesc, outDesc;
        source->GetDesc(&inDesc);
        target->GetDesc(&outDesc);
        if (inDesc.Format == outDesc.Format && inDesc.Width == outDesc.Width && inDesc.Height == outDesc.Height)
        {
            _context->CopySubresourceRegion((ID3D11Resource*)target, 0, 0, 0, 0, (ID3D11Resource*)source, subresource, null);
            return;
        }

        EnsureProcessor(inDesc, outDesc);
        SetColour(inDesc, nominalRange, yuvMatrix);

        var stream = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = BOOL.TRUE, pInputSurface = InputView(source, inDesc.ArraySize > 1 ? subresource / Math.Max(1, inDesc.MipLevels) : 0) };
        GpuDevices.ThrowIfFailed(_videoContext->VideoProcessorBlt(_vp, OutputView(target), 0, 1, &stream), "VideoProcessorBlt camera frame -> BGRA");
    }

    // Media Foundation cycles a small pool of frame textures (3-4 seen), so their input views are kept, keyed by texture
    // and array slice. The cache is bounded: a source that hands out new textures all the time only refills it.
    const int MaxInputViews = 8;
    readonly Dictionary<(nint Texture, uint Slice), nint> _inputViews = new();

    ID3D11VideoProcessorInputView* InputView(ID3D11Texture2D* source, uint slice)
    {
        if (_inputViews.TryGetValue(((nint)source, slice), out var cached))
            return (ID3D11VideoProcessorInputView*)cached;
        if (_inputViews.Count >= MaxInputViews)
            ReleaseInputViews();
        var desc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D };
        desc.Texture2D.MipSlice = 0;
        desc.Texture2D.ArraySlice = slice;
        ID3D11VideoProcessorInputView* view;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)source, _enum, &desc, &view), "camera frame input view");
        source->AddRef(); // the key stays this texture: its address cannot be reused while it is cached
        _inputViews[((nint)source, slice)] = (nint)view;
        return view;
    }

    void ReleaseInputViews()
    {
        foreach (var view in _inputViews)
        {
            ((ID3D11VideoProcessorInputView*)view.Value)->Release();
            ((ID3D11Texture2D*)view.Key.Texture)->Release();
        }
        _inputViews.Clear();
    }

    void EnsureProcessor(in D3D11_TEXTURE2D_DESC input, in D3D11_TEXTURE2D_DESC output)
    {
        if (_vp != null && input.Width == _inWidth && input.Height == _inHeight && input.Format == _inFormat
            && output.Width == _outWidth && output.Height == _outHeight)
            return;
        ReleaseProcessor();
        var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputWidth = input.Width,
            InputHeight = input.Height,
            OutputWidth = output.Width,
            OutputHeight = output.Height,
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
        };
        ID3D11VideoProcessorEnumerator* vpEnum;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorEnumerator(&content, &vpEnum), "video processor enumerator for camera frames");
        _enum = vpEnum;
        uint support;
        if (_enum->CheckVideoProcessorFormat(input.Format, &support).FAILED
            || (support & (uint)D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT.D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0)
            throw new InvalidOperationException($"the video processor cannot read camera frames of DXGI format {(int)input.Format}");
        ID3D11VideoProcessor* vp;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessor(_enum, 0, &vp), "video processor for camera frames");
        _vp = vp;
        var outputColour = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE { RGB_Range = 0 }; // full-range RGB
        _videoContext->VideoProcessorSetOutputColorSpace(_vp, &outputColour);
        _inWidth = input.Width;
        _inHeight = input.Height;
        _inFormat = input.Format;
        _outWidth = output.Width;
        _outHeight = output.Height;
        _colour = (-1, -1);
    }

    void SetColour(in D3D11_TEXTURE2D_DESC input, int nominalRange, int yuvMatrix)
    {
        if (_colour == (nominalRange, yuvMatrix))
            return;
        _colour = (nominalRange, yuvMatrix);
        // MF_MT_VIDEO_NOMINAL_RANGE: 1 = 0-255, 2 = 16-235; D3D11: 2 = 0-255, 1 = 16-235. Unknown YUV defaults to 16-235.
        // MF_MT_YUV_MATRIX: 1 = BT.709, 2 = BT.601; D3D11 YCbCr_Matrix: 1 = BT.709, 0 = BT.601. Unknown: 601 below 720 lines.
        var matrix = yuvMatrix switch { 1 => 1u, 2 => 0u, _ => input.Height >= 720 ? 1u : 0u };
        var range = nominalRange == 1 ? 2u : 1u;
        var colour = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE { YCbCr_Matrix = matrix, Nominal_Range = range };
        _videoContext->VideoProcessorSetStreamColorSpace(_vp, 0, &colour);
    }

    ID3D11VideoProcessorOutputView* OutputView(ID3D11Texture2D* target)
    {
        if (_outputViews.TryGetValue((nint)target, out var cached))
            return (ID3D11VideoProcessorOutputView*)cached;
        var desc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC { ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D };
        ID3D11VideoProcessorOutputView* view;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorOutputView((ID3D11Resource*)target, _enum, &desc, &view), "ring slot output view");
        target->AddRef();
        _outputViews[(nint)target] = (nint)view;
        return view;
    }

    /// <summary>
    /// A raster copy of a BGRA texture of this device (staging copy + map): for consumers that need CPU pixels.
    /// </summary>
    public SKImage ReadBack(ID3D11Texture2D* texture)
    {
        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        if (_staging == null || _stagingWidth != desc.Width || _stagingHeight != desc.Height)
        {
            if (_staging != null)
                _staging->Release();
            var stagingDesc = desc;
            stagingDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags = 0;
            ID3D11Texture2D* staging;
            GpuDevices.ThrowIfFailed(_device->CreateTexture2D(&stagingDesc, null, &staging), "readback staging texture");
            _staging = staging;
            _stagingWidth = desc.Width;
            _stagingHeight = desc.Height;
        }
        _context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)texture);
        D3D11_MAPPED_SUBRESOURCE mapped;
        GpuDevices.ThrowIfFailed(_context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "map readback");
        try
        {
            var info = new SKImageInfo((int)desc.Width, (int)desc.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            return SKImage.FromPixelCopy(info, (nint)mapped.pData, (int)mapped.RowPitch);
        }
        finally
        {
            _context->Unmap((ID3D11Resource*)_staging, 0);
        }
    }

    /// <summary>
    /// Drops the output views of ring slots (and the references on their textures), when the ring is replaced.
    /// </summary>
    public void ReleaseTargets()
    {
        foreach (var view in _outputViews)
        {
            ((ID3D11VideoProcessorOutputView*)view.Value)->Release();
            ((ID3D11Texture2D*)view.Key)->Release();
        }
        _outputViews.Clear();
    }

    void ReleaseProcessor()
    {
        ReleaseInputViews(); // views of the enumerator released below
        foreach (var view in _outputViews)
        {
            ((ID3D11VideoProcessorOutputView*)view.Value)->Release();
            ((ID3D11Texture2D*)view.Key)->Release();
        }
        _outputViews.Clear();
        if (_vp != null)
            _vp->Release();
        _vp = null;
        if (_enum != null)
            _enum->Release();
        _enum = null;
    }

    public void Dispose()
    {
        ReleaseProcessor();
        if (_staging != null)
            _staging->Release();
        _staging = null;
        _videoContext->Release();
        _videoDevice->Release();
        _context->Release();
        _device->Release();
    }
}
