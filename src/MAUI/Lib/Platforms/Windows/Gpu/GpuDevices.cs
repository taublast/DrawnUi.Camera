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
internal static unsafe class GpuDevices
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
    public static Adapter? AdapterOf(ID3D11Device* device)
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
    /// The adapter ANGLE renders the UI on: the display current on the UI thread (DrawnUi keeps its context current there).
    /// Call on the UI thread. Null with a reason when it cannot be determined.
    /// </summary>
    public static Adapter? UiAdapter(out string reason)
    {
        reason = null;
        var missing = Angle.Load();
        if (missing != null)
        {
            reason = $"ANGLE lacks {missing}";
            return null;
        }
        var display = Angle.eglGetCurrentDisplay();
        if (display == 0)
        {
            reason = "no ANGLE display is current on the UI thread (canvas not accelerated or not drawn yet)";
            return null;
        }
        var device = Angle.DeviceOfDisplay(display);
        if (device == null)
        {
            reason = "ANGLE does not expose its D3D11 device (EGL_EXT_device_query)";
            return null;
        }
        var adapter = AdapterOf(device);
        if (adapter == null)
            reason = "the adapter of ANGLE's device could not be read";
        return adapter;
    }

    /// <summary>
    /// A D3D11 device of our own on the adapter with this LUID: BGRA and video support, multithread protected (Media
    /// Foundation uses it from its own threads). Returns the device with its immediate context.
    /// </summary>
    public static ID3D11Device* CreateDevice(Adapter adapter, out ID3D11DeviceContext* context)
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
    public static Adapter? WarpAdapter()
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
    /// Test-only: DRAWNUI_CAMERA_TEST_ADAPTER=warp makes the GPU path treat the software adapter as the UI's, to exercise the
    /// adapter-mismatch detection and the CPU fallback without a second GPU. Unset in normal use.
    /// </summary>
    public static readonly bool TestWarp = string.Equals(Environment.GetEnvironmentVariable("DRAWNUI_CAMERA_TEST_ADAPTER"), "warp", StringComparison.OrdinalIgnoreCase);

    public static void ThrowIfFailed(HRESULT hr, string what)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"{what} failed: 0x{hr.Value:X8}");
    }
}
