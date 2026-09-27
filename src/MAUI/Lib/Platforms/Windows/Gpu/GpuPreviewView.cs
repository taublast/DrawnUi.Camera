namespace DrawnUi.Camera.Gpu;

using SkiaSharp;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

/// <summary>
/// UI thread: the camera frame ring seen from ANGLE's own D3D11 device. The ring textures are opened there once, bound to
/// GL textures of the UI's context through EGLImages (per GRContext: a new context after resume re-binds), and every new
/// frame becomes an SKImage over one of them. The frame's copy is fenced on ANGLE's immediate context, so Skia's draws of
/// it are ordered after the camera's GPU wrote it. No pixel is copied.
/// </summary>
internal sealed unsafe class GpuPreviewView : IDisposable
{
    public readonly GpuFrameRing Ring;
    readonly nint _display;
    ID3D11DeviceContext4* _context4;
    readonly ID3D11Texture2D*[] _textures = new ID3D11Texture2D*[GpuFrameRing.Slots];
    ID3D11Fence* _produced, _consumed;
    ulong _frame;

    GRContext _gr;
    nint _glContext; // the GL context the texture names belong to
    readonly uint[] _gl = new uint[GpuFrameRing.Slots];
    readonly nint[] _egl = new nint[GpuFrameRing.Slots];

    /// <summary>The adapter ANGLE renders on.</summary>
    public readonly GpuDevices.Adapter Adapter;

    /// <summary>The EGL display the view was opened on: a new display (the UI's GL stack set up again) needs a new view.</summary>
    public nint Display => _display;

    GpuPreviewView(GpuFrameRing ring, nint display, GpuDevices.Adapter adapter)
    {
        Ring = ring;
        _display = display;
        Adapter = adapter;
    }

    /// <summary>
    /// Opens the ring on the device of the ANGLE display current on this (UI) thread. Null with the reason when the ring
    /// lives on another adapter or the textures cannot be shared.
    /// </summary>
    public static GpuPreviewView Open(GpuFrameRing ring, out string reason)
    {
        reason = null;
        var display = Angle.eglGetCurrentDisplay();
        var device = display != 0 ? Angle.DeviceOfDisplay(display) : null;
        if (device == null)
        {
            reason = "no ANGLE display or device on the UI thread";
            return null;
        }
        var adapter = GpuDevices.AdapterOf(device);
        if (adapter == null || !adapter.Value.SameAs(ring.Adapter))
        {
            reason = $"camera frames are on {ring.Adapter}, the UI on {adapter?.ToString() ?? "an unknown adapter"}";
            return null;
        }

        var view = new GpuPreviewView(ring, display, adapter.Value);
        ID3D11Device1* device1 = null;
        ID3D11Device5* device5 = null;
        ID3D11DeviceContext* context = null;
        try
        {
            GpuDevices.ThrowIfFailed(device->QueryInterface(__uuidof<ID3D11Device1>(), (void**)&device1), "ANGLE's ID3D11Device1");
            GpuDevices.ThrowIfFailed(device->QueryInterface(__uuidof<ID3D11Device5>(), (void**)&device5), "ANGLE's ID3D11Device5 (fences)");
            device->GetImmediateContext(&context);
            ID3D11DeviceContext4* context4;
            GpuDevices.ThrowIfFailed(context->QueryInterface(__uuidof<ID3D11DeviceContext4>(), (void**)&context4), "ANGLE's ID3D11DeviceContext4");
            view._context4 = context4;
            for (var i = 0; i < GpuFrameRing.Slots; i++)
            {
                ID3D11Texture2D* texture;
                GpuDevices.ThrowIfFailed(device1->OpenSharedResource1(ring.Handles[i], __uuidof<ID3D11Texture2D>(), (void**)&texture), "opening a ring texture on ANGLE's device");
                view._textures[i] = texture;
            }
            ID3D11Fence* produced, consumed;
            GpuDevices.ThrowIfFailed(device5->OpenSharedFence(ring.ProducedHandle, __uuidof<ID3D11Fence>(), (void**)&produced), "produced fence on ANGLE's device");
            view._produced = produced;
            GpuDevices.ThrowIfFailed(device5->OpenSharedFence(ring.ConsumedHandle, __uuidof<ID3D11Fence>(), (void**)&consumed), "consumed fence on ANGLE's device");
            view._consumed = consumed;
            return view;
        }
        catch (Exception e)
        {
            reason = e.Message;
            view.Dispose();
            return null;
        }
        finally
        {
            if (context != null)
                context->Release();
            if (device5 != null)
                device5->Release();
            if (device1 != null)
                device1->Release();
        }
    }

    /// <summary>
    /// UI thread, inside the paint with <paramref name="gr"/>'s context current: the newest frame as an SKImage of that
    /// context (the caller owns and disposes the image; the texture stays with the view), or null when nothing is newer.
    /// </summary>
    public SKImage TakeLatest(GRContext gr, out DateTime time)
    {
        time = default;
        if (gr == null)
            return null;
        if (!ReferenceEquals(gr, _gr))
            Bind(gr);
        if (!Ring.TakeLatest(_context4, _produced, _consumed, ref _frame, out var slot, out time))
            return null;
        using var backend = new GRBackendTexture(Ring.Width, Ring.Height, false, new GRGlTextureInfo(Angle.GL_TEXTURE_2D, _gl[slot], Angle.GL_BGRA8_EXT));
        return SKImage.FromTexture(gr, backend, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888, SKAlphaType.Premul);
    }

    void Bind(GRContext gr)
    {
        Unbind();
        for (var i = 0; i < GpuFrameRing.Slots; i++)
            _gl[i] = Angle.BindTexture(_display, _textures[i], out _egl[i]);
        _glContext = Angle.eglGetCurrentContext();
        gr.ResetContext();
        _gr = gr;
    }

    void Unbind()
    {
        if (_gr == null)
            return;
        // texture names are deleted only in their own context: in a new one the same numbers are someone else's textures
        var sameContext = Angle.eglGetCurrentContext() == _glContext;
        for (var i = 0; i < GpuFrameRing.Slots; i++)
        {
            Angle.UnbindTexture(_display, sameContext ? _gl[i] : 0, _egl[i]);
            _gl[i] = 0;
            _egl[i] = 0;
        }
        if (sameContext && _gr.Handle != IntPtr.Zero)
            _gr.ResetContext(); // our GL calls changed bindings behind Skia's back
        _gr = null;
    }

    /// <summary>
    /// UI thread (the GL textures belong to the UI's context).
    /// </summary>
    public void Dispose()
    {
        try
        {
            Unbind();
        }
        catch
        {
            // the context may be gone already; the textures went with it
        }
        for (var i = 0; i < GpuFrameRing.Slots; i++)
        {
            if (_textures[i] != null)
                _textures[i]->Release();
            _textures[i] = null;
        }
        if (_produced != null)
            _produced->Release();
        if (_consumed != null)
            _consumed->Release();
        _produced = _consumed = null;
        if (_context4 != null)
            _context4->Release();
        _context4 = null;
    }
}
