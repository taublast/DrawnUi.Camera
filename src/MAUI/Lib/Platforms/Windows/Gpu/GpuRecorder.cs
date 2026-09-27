namespace DrawnUi.Camera.Gpu;

using System.Collections.Concurrent;
using System.Diagnostics;
using SkiaSharp;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

/// <summary>
/// Composes and converts recording frames on the GPU, off the UI thread. Owns a thread with its own D3D11 device on the UI's
/// adapter, a private ANGLE display on that device and a GRContext current only on that thread, so nothing is shared with the
/// UI's GL context. Camera frames arrive through a <see cref="GpuFrameRing"/>; each recording frame is drawn by Skia into a
/// D3D11 render target, converted to NV12 by the D3D11 video processor into a sample from a Media Foundation allocator on the
/// same device, and handed to the sink writer, whose hardware encoder uses the device through <see cref="DeviceManager"/>.
/// Nothing of the frame is read back to the CPU.
/// </summary>
internal sealed unsafe class GpuRecorder : IDisposable
{
    public readonly GpuDevices.Adapter Adapter;

    readonly Thread _thread;
    readonly ConcurrentQueue<Action> _jobs = new();
    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    int _threadId;

    ID3D11Device* _device;
    ID3D11DeviceContext* _context;
    ID3D11DeviceContext4* _context4;
    ID3D11VideoDevice* _videoDevice;
    ID3D11VideoContext* _videoContext;
    IMFDXGIDeviceManager* _manager;
    bool _mfStarted;

    nint _display, _eglDevice, _eglContext, _pbuffer;
    GRContext _gr;

    // the camera frames, opened on our device
    GpuFrameRing _ring;
    readonly ID3D11Texture2D*[] _ringTextures = new ID3D11Texture2D*[GpuFrameRing.Slots];
    readonly SKImage[] _ringImages = new SKImage[GpuFrameRing.Slots];
    readonly uint[] _ringGl = new uint[GpuFrameRing.Slots];
    readonly nint[] _ringEgl = new nint[GpuFrameRing.Slots];
    ID3D11Fence* _produced, _consumed;
    ulong _consumerFrame;

    // the render target the recording frame is composed into
    ID3D11Texture2D* _target;
    SKSurface _targetSurface;
    uint _targetGl;
    nint _targetEgl;
    int _width, _height;

    // BGRA -> NV12 into allocator samples
    ID3D11VideoProcessorEnumerator* _vpEnum;
    ID3D11VideoProcessor* _vp;
    ID3D11VideoProcessorInputView* _vpInput;
    readonly Dictionary<(nint texture, uint subresource), nint> _vpOutputs = new();
    IMFVideoSampleAllocatorEx* _allocator;

    /// <summary>
    /// Runs on the recorder thread for every new camera frame (and when woken).
    /// </summary>
    public Action FrameArrived { get; set; }

    /// <summary>Recording frames converted and handed out.</summary>
    public long FramesConverted { get; private set; }

    /// <summary>The GPU part of one frame on the recorder thread: compose flush + NV12 conversion, milliseconds.</summary>
    public double LastSubmitMs { get; private set; }

    public IMFDXGIDeviceManager* DeviceManager => _manager;

    GpuRecorder(GpuDevices.Adapter adapter)
    {
        Adapter = adapter;
        _thread = new Thread(Run) { IsBackground = true, Name = "SkiaCamera GPU recorder" };
    }

    /// <summary>
    /// Starts the recorder thread and sets up device, display and GRContext on it. Throws with the reason on failure.
    /// </summary>
    public static GpuRecorder Create(GpuDevices.Adapter adapter)
    {
        var recorder = new GpuRecorder(adapter);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder._jobs.Enqueue(() =>
        {
            try
            {
                recorder.Setup();
                ready.SetResult();
            }
            catch (Exception e)
            {
                ready.SetException(e);
            }
        });
        recorder._thread.Start();
        try
        {
            ready.Task.GetAwaiter().GetResult();
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
        return recorder;
    }

    void Setup()
    {
        _device = GpuDevices.CreateDevice(Adapter, out var context);
        _context = context;
        ID3D11DeviceContext4* context4;
        GpuDevices.ThrowIfFailed(_context->QueryInterface(__uuidof<ID3D11DeviceContext4>(), (void**)&context4), "ID3D11DeviceContext4 (fences)");
        _context4 = context4;
        ID3D11VideoDevice* videoDevice;
        GpuDevices.ThrowIfFailed(_device->QueryInterface(__uuidof<ID3D11VideoDevice>(), (void**)&videoDevice), "ID3D11VideoDevice");
        _videoDevice = videoDevice;
        ID3D11VideoContext* videoContext;
        GpuDevices.ThrowIfFailed(_context->QueryInterface(__uuidof<ID3D11VideoContext>(), (void**)&videoContext), "ID3D11VideoContext");
        _videoContext = videoContext;

        GpuDevices.ThrowIfFailed(MFStartup(0x00020070, 0) /* MF_VERSION, MFSTARTUP_FULL */, "MFStartup");
        _mfStarted = true;
        uint token;
        IMFDXGIDeviceManager* manager;
        GpuDevices.ThrowIfFailed(MFCreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
        _manager = manager;
        GpuDevices.ThrowIfFailed(_manager->ResetDevice((IUnknown*)_device, token), "IMFDXGIDeviceManager.ResetDevice");

        (_display, _eglDevice, _eglContext, _pbuffer) = Angle.CreatePrivateDisplay(_device);
        if (Angle.eglMakeCurrent(_display, _pbuffer, _pbuffer, _eglContext) == 0)
            throw new InvalidOperationException($"eglMakeCurrent on the recorder thread failed, egl 0x{Angle.eglGetError():X}");
        using var glInterface = GRGlInterface.Create();
        _gr = GRContext.CreateGl(glInterface) ?? throw new InvalidOperationException("GRContext on the private ANGLE display failed");
    }

    void Run()
    {
        _threadId = Environment.CurrentManagedThreadId;
        while (!_stop)
        {
            while (_jobs.TryDequeue(out var job))
                job();
            if (_stop)
                break;

            var ring = _ring;
            var handles = ring != null ? new WaitHandle[] { _wake, ring.FrameReady } : new WaitHandle[] { _wake };
            var index = WaitHandle.WaitAny(handles, 200);
            if (index == 1 || (index == 0 && ring != null))
            {
                try
                {
                    FrameArrived?.Invoke();
                }
                catch (Exception e)
                {
                    Super.Log($"[GpuRecorder] frame failed: {e}");
                }
            }
        }
        while (_jobs.TryDequeue(out var job))
            job();
    }

    bool OnThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>
    /// Runs the action on the recorder thread and waits for it.
    /// </summary>
    public void Invoke(Action action)
    {
        if (OnThread)
        {
            action();
            return;
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Enqueue(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        _wake.Set();
        done.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Opens the camera frame ring on our device (recorder thread). Throws when the ring's device is on another adapter or
    /// its textures cannot be shared.
    /// </summary>
    public void OpenRing(GpuFrameRing ring) => Invoke(() =>
    {
        CloseRing();
        if (!ring.Adapter.SameAs(Adapter))
            throw new InvalidOperationException($"camera frames are on {ring.Adapter}, the recorder on {Adapter}");
        ID3D11Device1* device1;
        ID3D11Device5* device5;
        GpuDevices.ThrowIfFailed(_device->QueryInterface(__uuidof<ID3D11Device1>(), (void**)&device1), "ID3D11Device1");
        GpuDevices.ThrowIfFailed(_device->QueryInterface(__uuidof<ID3D11Device5>(), (void**)&device5), "ID3D11Device5");
        try
        {
            for (var i = 0; i < GpuFrameRing.Slots; i++)
            {
                ID3D11Texture2D* texture;
                GpuDevices.ThrowIfFailed(device1->OpenSharedResource1(ring.Handles[i], __uuidof<ID3D11Texture2D>(), (void**)&texture), "opening a ring texture");
                _ringTextures[i] = texture;
                _ringImages[i] = Angle.WrapImage(_display, _gr, texture, ring.Width, ring.Height, out _ringGl[i], out _ringEgl[i]);
            }
            ID3D11Fence* produced, consumed;
            GpuDevices.ThrowIfFailed(device5->OpenSharedFence(ring.ProducedHandle, __uuidof<ID3D11Fence>(), (void**)&produced), "opening the produced fence");
            _produced = produced;
            GpuDevices.ThrowIfFailed(device5->OpenSharedFence(ring.ConsumedHandle, __uuidof<ID3D11Fence>(), (void**)&consumed), "opening the consumed fence");
            _consumed = consumed;
            _ring = ring;
        }
        catch
        {
            CloseRing();
            throw;
        }
        finally
        {
            device1->Release();
            device5->Release();
        }
        _wake.Set();
    });

    void CloseRing()
    {
        _ring = null;
        for (var i = 0; i < GpuFrameRing.Slots; i++)
        {
            _ringImages[i]?.Dispose();
            _ringImages[i] = null;
            if (_ringGl[i] != 0 || _ringEgl[i] != 0)
                Angle.UnbindTexture(_display, _ringGl[i], _ringEgl[i]);
            _ringGl[i] = 0;
            _ringEgl[i] = 0;
            if (_ringTextures[i] != null)
                _ringTextures[i]->Release();
            _ringTextures[i] = null;
        }
        if (_produced != null)
            _produced->Release();
        if (_consumed != null)
            _consumed->Release();
        _produced = _consumed = null;
        _gr?.ResetContext();
    }

    /// <summary>
    /// Recorder thread: the newest camera frame as an image of our GRContext, or null when there is none newer than the last.
    /// </summary>
    public SKImage TakeLatestFrame(out DateTime time)
    {
        time = default;
        var ring = _ring;
        if (ring == null || !ring.TakeLatest(_context4, _produced, _consumed, ref _consumerFrame, out var slot, out time))
            return null;
        return _ringImages[slot];
    }

    /// <summary>
    /// Recorder thread: a cleared canvas over the render target of this size.
    /// </summary>
    public SKCanvas BeginFrame(int width, int height)
    {
        EnsureTarget(width, height);
        var canvas = _targetSurface.Canvas;
        canvas.Clear(SKColors.Transparent);
        return canvas;
    }

    /// <summary>
    /// Recorder thread: finishes the composed frame and converts it into an NV12 sample of the allocator (AddRef'd; the
    /// caller releases it). Null when the encoder still owns every sample for longer than about 400 ms.
    /// </summary>
    public IMFSample* EndFrame()
    {
        var t0 = Stopwatch.GetTimestamp();
        _targetSurface.Canvas.Flush();
        _gr.Flush(true, false); // submit to ANGLE's immediate context: the blit below is ordered after it on the same context

        IMFSample* sample = null;
        for (var attempt = 0; ; attempt++)
        {
            var hr = _allocator->AllocateSample(&sample);
            if (hr.SUCCEEDED)
                break;
            if (hr.Value != unchecked((int)0xC00D4A3E) || attempt >= 400) // MF_E_SAMPLEALLOCATOR_EMPTY: all samples are with the encoder
                return null;
            Thread.Sleep(1);
        }

        IMFMediaBuffer* buffer = null;
        IMFDXGIBuffer* dxgiBuffer = null;
        ID3D11Texture2D* texture = null;
        try
        {
            GpuDevices.ThrowIfFailed(sample->GetBufferByIndex(0, &buffer), "sample buffer");
            GpuDevices.ThrowIfFailed(buffer->QueryInterface(__uuidof<IMFDXGIBuffer>(), (void**)&dxgiBuffer), "IMFDXGIBuffer");
            GpuDevices.ThrowIfFailed(dxgiBuffer->GetResource(__uuidof<ID3D11Texture2D>(), (void**)&texture), "sample texture");
            uint subresource;
            GpuDevices.ThrowIfFailed(dxgiBuffer->GetSubresourceIndex(&subresource), "sample subresource");

            var output = OutputView(texture, subresource);
            var stream = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = BOOL.TRUE, pInputSurface = _vpInput };
            GpuDevices.ThrowIfFailed(_videoContext->VideoProcessorBlt(_vp, output, 0, 1, &stream), "VideoProcessorBlt BGRA -> NV12");

            uint max;
            buffer->GetMaxLength(&max);
            buffer->SetCurrentLength(max); // allocator buffers start empty; the sink writer rejects a 0-length buffer

            FramesConverted++;
            LastSubmitMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var result = sample;
            sample = null;
            return result;
        }
        finally
        {
            if (texture != null)
                texture->Release();
            if (dxgiBuffer != null)
                dxgiBuffer->Release();
            if (buffer != null)
                buffer->Release();
            if (sample != null)
                sample->Release();
        }
    }

    /// <summary>
    /// <see cref="EndFrame"/> as a plain pointer for callers that use another COM projection; release with <see cref="ReleaseSample"/>.
    /// </summary>
    public nint EndFrameSample() => (nint)EndFrame();

    public static void SetSampleTimes(nint sample, long time100ns, long duration100ns)
    {
        ((IMFSample*)sample)->SetSampleTime(time100ns);
        ((IMFSample*)sample)->SetSampleDuration(duration100ns);
    }

    /// <summary>
    /// The transforms a sink writer inserted for a stream, e.g. "Intel Quick Sync Video H.264 Encoder MFT (hardware)".
    /// </summary>
    public static string DescribeTransforms(object sinkWriter, uint stream)
    {
        var unknown = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(sinkWriter);
        IMFSinkWriterEx* writer = null;
        try
        {
            if (((IUnknown*)unknown)->QueryInterface(__uuidof<IMFSinkWriterEx>(), (void**)&writer).FAILED)
                return "unknown";
            var names = new List<string>();
            for (uint i = 0; i < 8; i++)
            {
                Guid category;
                IMFTransform* transform;
                if (writer->GetTransformForStream(stream, i, &category, &transform).FAILED)
                    break;
                var name = category == MfGuids.MFT_CATEGORY_VIDEO_ENCODER ? "encoder" : "converter";
                IMFAttributes* attributes;
                if (transform->GetAttributes(&attributes).SUCCEEDED && attributes != null)
                {
                    var friendly = MfGuids.MFT_FRIENDLY_NAME_Attribute;
                    ushort* text;
                    uint length;
                    if (attributes->GetAllocatedString(&friendly, (char**)&text, &length).SUCCEEDED)
                    {
                        name += $" '{new string((char*)text, 0, (int)length)}'";
                        CoTaskMemFree(text);
                    }
                    var hardwareUrl = MfGuids.MFT_ENUM_HARDWARE_URL_Attribute;
                    uint urlLength;
                    name += attributes->GetStringLength(&hardwareUrl, &urlLength).SUCCEEDED ? " (hardware" : " (software";
                    var clsidKey = MfGuids.MFT_TRANSFORM_CLSID_Attribute;
                    Guid clsid;
                    if (attributes->GetGUID(&clsidKey, &clsid).SUCCEEDED)
                        name += $", CLSID {clsid}";
                    uint count;
                    attributes->GetCount(&count);
                    name += $", {count} attributes)";
                    attributes->Release();
                }
                transform->Release();
                names.Add(name);
            }
            return names.Count > 0 ? string.Join(" -> ", names) : "none reported";
        }
        finally
        {
            if (writer != null)
                writer->Release();
            System.Runtime.InteropServices.Marshal.Release(unknown);
        }
    }

    public static void ReleaseSample(nint sample)
    {
        if (sample != 0)
            ((IMFSample*)sample)->Release();
    }

    ID3D11VideoProcessorOutputView* OutputView(ID3D11Texture2D* texture, uint subresource)
    {
        if (_vpOutputs.TryGetValue(((nint)texture, subresource), out var cached))
            return (ID3D11VideoProcessorOutputView*)cached;

        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        var viewDesc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC();
        if (desc.ArraySize > 1)
        {
            viewDesc.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
            viewDesc.Texture2DArray.MipSlice = 0;
            viewDesc.Texture2DArray.FirstArraySlice = subresource;
            viewDesc.Texture2DArray.ArraySize = 1;
        }
        else
        {
            viewDesc.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D;
        }
        ID3D11VideoProcessorOutputView* view;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorOutputView((ID3D11Resource*)texture, _vpEnum, &viewDesc, &view), "NV12 output view");
        texture->AddRef(); // the pooled sample texture stays alive with its cached view
        _vpOutputs[((nint)texture, subresource)] = (nint)view;
        return view;
    }

    void EnsureTarget(int width, int height)
    {
        if (_target != null && _width == width && _height == height)
            return;
        ReleaseTarget();
        _width = width;
        _height = height;

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
        };
        ID3D11Texture2D* target;
        GpuDevices.ThrowIfFailed(_device->CreateTexture2D(&desc, null, &target), "recording render target");
        _target = target;
        _targetSurface = Angle.WrapSurface(_display, _gr, target, width, height, out _targetGl, out _targetEgl);

        // video processor BGRA (full range) -> NV12 BT.709 limited range, same size
        var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
        };
        ID3D11VideoProcessorEnumerator* vpEnum;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorEnumerator(&content, &vpEnum), "video processor enumerator");
        _vpEnum = vpEnum;
        uint support;
        if (_vpEnum->CheckVideoProcessorFormat(DXGI_FORMAT.DXGI_FORMAT_NV12, &support).FAILED
            || (support & (uint)D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT.D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
            throw new InvalidOperationException("the video processor cannot output NV12");
        ID3D11VideoProcessor* vp;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessor(_vpEnum, 0, &vp), "video processor");
        _vp = vp;
        var input = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE { RGB_Range = 0 };                      // 0-255 RGB
        _videoContext->VideoProcessorSetStreamColorSpace(_vp, 0, &input);
        var output = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE { YCbCr_Matrix = 1, Nominal_Range = 1 }; // BT.709, 16-235
        _videoContext->VideoProcessorSetOutputColorSpace(_vp, &output);
        var inputDesc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D };
        ID3D11VideoProcessorInputView* inputView;
        GpuDevices.ThrowIfFailed(_videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)_target, _vpEnum, &inputDesc, &inputView), "render target input view");
        _vpInput = inputView;

        // NV12 samples on our device, usable by the encoder
        IMFVideoSampleAllocatorEx* allocator;
        GpuDevices.ThrowIfFailed(MFCreateVideoSampleAllocatorEx(__uuidof<IMFVideoSampleAllocatorEx>(), (void**)&allocator), "MFCreateVideoSampleAllocatorEx");
        _allocator = allocator;
        GpuDevices.ThrowIfFailed(_allocator->SetDirectXManager((IUnknown*)_manager), "allocator device manager");
        IMFAttributes* attributes = null;
        IMFMediaType* type = null;
        try
        {
            GpuDevices.ThrowIfFailed(MFCreateAttributes(&attributes, 2), "allocator attributes");
            var bindFlags = MfGuids.MF_SA_D3D11_BINDFLAGS;
            attributes->SetUINT32(&bindFlags, (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_VIDEO_ENCODER));
            var usage = MfGuids.MF_SA_D3D11_USAGE;
            attributes->SetUINT32(&usage, (uint)D3D11_USAGE.D3D11_USAGE_DEFAULT);
            type = MfGuids.CreateVideoType(MfGuids.MFVideoFormat_NV12, width, height, 30);
            GpuDevices.ThrowIfFailed(_allocator->InitializeSampleAllocatorEx(4, 16, attributes, type), "InitializeSampleAllocatorEx NV12");
        }
        finally
        {
            if (type != null)
                type->Release();
            if (attributes != null)
                attributes->Release();
        }
    }

    void ReleaseTarget()
    {
        foreach (var view in _vpOutputs)
        {
            ((ID3D11VideoProcessorOutputView*)view.Value)->Release();
            ((ID3D11Texture2D*)view.Key.texture)->Release();
        }
        _vpOutputs.Clear();
        if (_allocator != null)
        {
            _allocator->UninitializeSampleAllocator();
            _allocator->Release();
            _allocator = null;
        }
        if (_vpInput != null)
            _vpInput->Release();
        _vpInput = null;
        if (_vp != null)
            _vp->Release();
        _vp = null;
        if (_vpEnum != null)
            _vpEnum->Release();
        _vpEnum = null;
        _targetSurface?.Dispose();
        _targetSurface = null;
        if (_targetGl != 0 || _targetEgl != 0)
            Angle.UnbindTexture(_display, _targetGl, _targetEgl);
        _targetGl = 0;
        _targetEgl = 0;
        if (_target != null)
            _target->Release();
        _target = null;
        _width = _height = 0;
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            _jobs.Enqueue(Teardown);
            _stop = true;
            _wake.Set();
            _thread.Join(10000);
        }
        else
        {
            Teardown();
        }
        _wake.Dispose();
    }

    void Teardown()
    {
        try
        {
            if (_gr != null)
            {
                CloseRing();
                ReleaseTarget();
                _gr.Flush(true, true);
                _gr.Dispose();
                _gr = null;
            }
            if (_display != 0)
                Angle.DestroyPrivateDisplay(_display, _eglDevice, _eglContext, _pbuffer);
            _display = 0;
        }
        catch (Exception e)
        {
            Super.Log($"[GpuRecorder] teardown: {e.Message}");
        }

        if (_manager != null)
            _manager->Release();
        _manager = null;
        if (_videoContext != null)
            _videoContext->Release();
        _videoContext = null;
        if (_videoDevice != null)
            _videoDevice->Release();
        _videoDevice = null;
        if (_context4 != null)
            _context4->Release();
        _context4 = null;
        if (_context != null)
        {
            _context->ClearState();
            _context->Flush();
            _context->Release();
        }
        _context = null;
        if (_device != null)
            _device->Release();
        _device = null;
        if (_mfStarted)
            MFShutdown();
        _mfStarted = false;
    }
}

/// <summary>
/// Media Foundation GUIDs of the GPU path, as in the Windows SDK headers (mfapi.h, mftransform.h, mfreadwrite.h).
/// </summary>
internal static unsafe class MfGuids
{
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid MF_SA_D3D11_BINDFLAGS = new("eacf97ad-065c-4408-bee3-fdcbfd128be2");
    public static readonly Guid MF_SA_D3D11_USAGE = new("e85fe442-2ca3-486e-a9c7-109dda609880");
    public static readonly Guid MF_SINK_WRITER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid MFT_ENUM_HARDWARE_URL_Attribute = new("2fb866ac-b078-4942-ab6c-003d05cda674");
    public static readonly Guid MFT_TRANSFORM_CLSID_Attribute = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c");

    public static IMFMediaType* CreateVideoType(Guid subtype, int width, int height, int fps)
    {
        IMFMediaType* type;
        GpuDevices.ThrowIfFailed(MFCreateMediaType(&type), "MFCreateMediaType");
        var major = MF_MT_MAJOR_TYPE;
        var video = MFMediaType_Video;
        type->SetGUID(&major, &video);
        var sub = MF_MT_SUBTYPE;
        type->SetGUID(&sub, &subtype);
        var size = MF_MT_FRAME_SIZE;
        type->SetUINT64(&size, ((ulong)(uint)width << 32) | (uint)height);
        var rate = MF_MT_FRAME_RATE;
        type->SetUINT64(&rate, ((ulong)(uint)Math.Max(1, fps) << 32) | 1);
        var aspect = MF_MT_PIXEL_ASPECT_RATIO;
        type->SetUINT64(&aspect, (1UL << 32) | 1);
        var interlace = MF_MT_INTERLACE_MODE;
        type->SetUINT32(&interlace, 2); // progressive
        return type;
    }
}
