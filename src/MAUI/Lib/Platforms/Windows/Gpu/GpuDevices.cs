namespace DrawnUi.Camera.Gpu;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

/// <summary>
/// Adapters and devices of the Windows GPU frame path. Everything that shares textures has to sit on one adapter, and the
/// adapter that counts is the one ANGLE renders the UI on: our devices are created on it explicitly (by LUID), so a laptop
/// with two GPUs does not end up with the camera, the recorder and the UI on different ones.
/// </summary>
internal static class GpuDevices
{
    /// <summary>
    /// An adapter by identity, with the name for logs.
    /// </summary>
    public readonly record struct Adapter(LUID Luid, string Name)
    {
        public bool SameAs(Adapter other) => Luid.LowPart == other.Luid.LowPart && Luid.HighPart == other.Luid.HighPart;

        public override string ToString() => $"{Name} (LUID {Luid.HighPart:X}:{Luid.LowPart:X8})";
    }

    /// <summary>
    /// The adapter a D3D11 device was created on.
    /// </summary>
    public static unsafe Adapter? AdapterOf(ID3D11Device* device)
    {
        if (device == null)
            return null;
        IDXGIDevice* dxgi = null;
        IDXGIAdapter* adapter = null;
        try
        {
            if (device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgi).FAILED || dxgi->GetAdapter(&adapter).FAILED)
                return null;
            DXGI_ADAPTER_DESC desc;
            if (adapter->GetDesc(&desc).FAILED)
                return null;
            return new Adapter(desc.AdapterLuid, new string((char*)&desc.Description)); // null-terminated WCHAR[128]
        }
        finally
        {
            if (adapter != null)
                adapter->Release();
            if (dxgi != null)
                dxgi->Release();
        }
    }

    /// <summary>
    /// The display current on the UI thread, 0 when none: what an accelerated DrawnUi canvas left current after its last
    /// frame. Only that query runs on the UI thread.
    /// </summary>
    public static async Task<nint> UiThreadDisplayAsync()
    {
        var missing = await Task.Run(() =>
        {
            var lacking = Angle.Load();
            if (lacking == null)
                Angle.eglGetCurrentDisplay(); // binds the import here rather than on the UI thread
            return lacking;
        });
        return missing != null ? 0 : await MainThread.InvokeOnMainThreadAsync(Angle.eglGetCurrentDisplay);
    }

    /// <summary>
    /// The adapter ANGLE renders the given display on, read on the pool. Null with a reason when it cannot be determined.
    /// </summary>
    public static Task<(Adapter? Adapter, string Reason)> AdapterOfDisplayAsync(nint display) => Task.Run(() =>
    {
        var missing = Angle.Load();
        return missing != null ? (null, $"ANGLE lacks {missing}") : AdapterOfDisplay(display);
    });

    static unsafe (Adapter? Adapter, string Reason) AdapterOfDisplay(nint display)
    {
        var device = Angle.DeviceOfDisplay(display);
        if (device == null)
            return (null, "ANGLE does not expose its D3D11 device (EGL_EXT_device_query)");
        var adapter = AdapterOf(device);
        return (adapter, adapter == null ? "the adapter of ANGLE's device could not be read" : null);
    }

    /// <summary>
    /// A D3D11 device of our own on the adapter with this LUID: BGRA and video support, multithread protected (Media
    /// Foundation uses it from its own threads). Returns the device with its immediate context.
    /// </summary>
    public static unsafe ID3D11Device* CreateDevice(Adapter adapter, out ID3D11DeviceContext* context)
    {
        context = null;
        IDXGIFactory4* factory = null;
        IDXGIAdapter* dxgiAdapter = null;
        ID3D11Device* device = null;
        ID3D11Multithread* multithread = null;
        try
        {
            ThrowIfFailed(CreateDXGIFactory1(__uuidof<IDXGIFactory4>(), (void**)&factory), "CreateDXGIFactory1");
            ThrowIfFailed(factory->EnumAdapterByLuid(adapter.Luid, __uuidof<IDXGIAdapter>(), (void**)&dxgiAdapter), $"EnumAdapterByLuid {adapter}");
            var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
            ID3D11DeviceContext* ctx = null;
            ThrowIfFailed(D3D11CreateDevice(dxgiAdapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, HMODULE.NULL,
                (uint)(D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT),
                levels, 2, 7 /* D3D11_SDK_VERSION */, &device, null, &ctx), $"D3D11CreateDevice on {adapter}");
            context = ctx;
            if (ctx->QueryInterface(__uuidof<ID3D11Multithread>(), (void**)&multithread).SUCCEEDED)
                multithread->SetMultithreadProtected(BOOL.TRUE);
            var result = device;
            device = null;
            return result;
        }
        finally
        {
            if (multithread != null)
                multithread->Release();
            if (device != null)
                device->Release();
            if (dxgiAdapter != null)
                dxgiAdapter->Release();
            if (factory != null)
                factory->Release();
        }
    }

    /// <summary>
    /// The software adapter (Microsoft Basic Render Driver). Only for tests that need a second adapter on a one-GPU machine.
    /// </summary>
    public static unsafe Adapter? WarpAdapter()
    {
        IDXGIFactory4* factory = null;
        IDXGIAdapter* adapter = null;
        try
        {
            if (CreateDXGIFactory1(__uuidof<IDXGIFactory4>(), (void**)&factory).FAILED
                || factory->EnumWarpAdapter(__uuidof<IDXGIAdapter>(), (void**)&adapter).FAILED)
                return null;
            DXGI_ADAPTER_DESC desc;
            if (adapter->GetDesc(&desc).FAILED)
                return null;
            return new Adapter(desc.AdapterLuid, new string((char*)&desc.Description));
        }
        finally
        {
            if (adapter != null)
                adapter->Release();
            if (factory != null)
                factory->Release();
        }
    }

    /// <summary>
    /// Test-only: DRAWNUI_CAMERA_TEST_ADAPTER=warp puts our capture device on the software adapter, so camera frames arrive
    /// on another adapter than the UI's: exercises the mismatch detection and the preview's CPU fallback without a second
    /// GPU. Unset in normal use.
    /// </summary>
    public static readonly bool TestWarpCapture = string.Equals(Environment.GetEnvironmentVariable("DRAWNUI_CAMERA_TEST_ADAPTER"), "warp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Test-only: DRAWNUI_CAMERA_TEST_ADAPTER=warp-ui makes the recording treat the software adapter as the UI's: exercises
    /// the recording's CPU fallback while the preview stays on the GPU. Unset in normal use.
    /// </summary>
    public static readonly bool TestWarpUi = string.Equals(Environment.GetEnvironmentVariable("DRAWNUI_CAMERA_TEST_ADAPTER"), "warp-ui", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Test-only: DRAWNUI_CAMERA_TEST_ADAPTER=warp-ui-preview makes the preview treat the software adapter as the UI's: our
    /// capture device cannot be made there (no video support), Media Foundation's own device lands on the real GPU, and the
    /// frames' adapter no longer matches: exercises the whole cascade down to the preview's CPU fallback. Unset in normal use.
    /// </summary>
    public static readonly bool TestWarpUiPreview = string.Equals(Environment.GetEnvironmentVariable("DRAWNUI_CAMERA_TEST_ADAPTER"), "warp-ui-preview", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Test-only: DRAWNUI_CAMERA_TEST_DEVICE_LOST=5 makes the preview's frame conversion fail the way a removed device does
    /// (DXGI_ERROR_DEVICE_REMOVED) 5 seconds after its first frame: exercises the fallback and the cleanup. Unset in normal use.
    /// </summary>
    public static readonly double TestDeviceLostAfter = double.TryParse(Environment.GetEnvironmentVariable("DRAWNUI_CAMERA_TEST_DEVICE_LOST"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : -1;

    /// <summary>
    /// Test-only: DRAWNUI_CAMERA_TEST_STAMP=1 writes each GPU preview frame's number into the frame itself (20 black or
    /// white cells across the middle of the top band) right after conversion, so tests can tell which frame a consumer
    /// got. Unset in normal use.
    /// </summary>
    public static readonly bool TestStamp = Environment.GetEnvironmentVariable("DRAWNUI_CAMERA_TEST_STAMP") == "1";

    /// <summary>
    /// Test-only: DRAWNUI_CAMERA_TEST_RECORDING_PREVIEW=live keeps the live camera preview during a GPU recording instead
    /// of mirroring the recording (the phase R behaviour), to measure one against the other. Unset in normal use.
    /// </summary>
    public static readonly bool TestLivePreviewWhileRecording = Environment.GetEnvironmentVariable("DRAWNUI_CAMERA_TEST_RECORDING_PREVIEW") == "live";

    public static void ThrowIfFailed(HRESULT hr, string what)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"{what} failed: 0x{hr.Value:X8}");
    }
}
