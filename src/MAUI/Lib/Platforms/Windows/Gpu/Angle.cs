namespace DrawnUi.Camera.Gpu;

using System.Runtime.InteropServices;
using SkiaSharp;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

/// <summary>
/// The parts of EGL / GLES that the Windows GPU frame path needs, called on the ANGLE build the app already loaded
/// (libEGL.dll / libGLESv2.dll next to the executable). Extension entry points are resolved once through eglGetProcAddress.
/// </summary>
internal static unsafe class Angle
{
    const string EglLib = "libEGL.dll";
    const string GlesLib = "libGLESv2.dll";

    public const int EGL_NONE = 0x3038;
    public const int EGL_RED_SIZE = 0x3024, EGL_GREEN_SIZE = 0x3023, EGL_BLUE_SIZE = 0x3022, EGL_ALPHA_SIZE = 0x3021;
    public const int EGL_SURFACE_TYPE = 0x3033, EGL_PBUFFER_BIT = 0x0001, EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056;
    public const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    public const int EGL_DEVICE_EXT = 0x322C;
    public const int EGL_D3D11_DEVICE_ANGLE = 0x33A1;
    public const int EGL_PLATFORM_DEVICE_EXT = 0x313F;
    public const int EGL_D3D11_TEXTURE_ANGLE = 0x3484;
    public const uint GL_TEXTURE_2D = 0x0DE1, GL_BGRA8_EXT = 0x93A1, GL_RENDERER = 0x1F01;
    const uint GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_LINEAR = 0x2601;
    const uint GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_CLAMP_TO_EDGE = 0x812F;

    [DllImport(EglLib)] public static extern nint eglGetCurrentDisplay();
    [DllImport(EglLib)] public static extern nint eglGetCurrentContext();
    [DllImport(EglLib)] static extern nint eglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(EglLib)] public static extern int eglInitialize(nint dpy, out int major, out int minor);
    [DllImport(EglLib)] public static extern int eglTerminate(nint dpy);
    [DllImport(EglLib)] public static extern int eglChooseConfig(nint dpy, int[] attribs, nint[] configs, int size, out int count);
    [DllImport(EglLib)] public static extern nint eglCreateContext(nint dpy, nint config, nint share, int[] attribs);
    [DllImport(EglLib)] public static extern int eglDestroyContext(nint dpy, nint ctx);
    [DllImport(EglLib)] public static extern nint eglCreatePbufferSurface(nint dpy, nint config, int[] attribs);
    [DllImport(EglLib)] public static extern int eglDestroySurface(nint dpy, nint surface);
    [DllImport(EglLib)] public static extern int eglMakeCurrent(nint dpy, nint draw, nint read, nint ctx);
    [DllImport(EglLib)] public static extern int eglGetError();
    [DllImport(EglLib)] public static extern int eglReleaseThread();

    [DllImport(GlesLib)] static extern void glGenTextures(int n, out uint textures);
    [DllImport(GlesLib)] static extern void glBindTexture(uint target, uint texture);
    [DllImport(GlesLib)] static extern void glDeleteTextures(int n, ref uint textures);
    [DllImport(GlesLib)] static extern void glTexParameteri(uint target, uint name, uint value);
    [DllImport(GlesLib)] static extern uint glGetError();
    [DllImport(GlesLib)] public static extern nint glGetString(uint name);

    static delegate* unmanaged<nint, int, nint*, int> _queryDisplayAttrib;
    static delegate* unmanaged<nint, int, nint*, int> _queryDeviceAttrib;
    static delegate* unmanaged<int, nint, nint*, nint> _createDevice;
    static delegate* unmanaged<nint, int> _releaseDevice;
    static delegate* unmanaged<int, nint, int*, nint> _getPlatformDisplay;
    static delegate* unmanaged<nint, nint, int, nint, int*, nint> _createImage;
    static delegate* unmanaged<nint, nint, int> _destroyImage;
    static delegate* unmanaged<uint, nint, void> _imageTargetTexture;
    static string _missing;

    /// <summary>
    /// Resolves the extension entry points. Null when all are there, else the names that are missing.
    /// </summary>
    public static string Load()
    {
        if (_getPlatformDisplay != null || _missing != null)
            return _missing;
        var missing = new List<string>();
        nint Proc(string name)
        {
            var p = eglGetProcAddress(name);
            if (p == 0)
                missing.Add(name);
            return p;
        }

        _queryDisplayAttrib = (delegate* unmanaged<nint, int, nint*, int>)Proc("eglQueryDisplayAttribEXT");
        _queryDeviceAttrib = (delegate* unmanaged<nint, int, nint*, int>)Proc("eglQueryDeviceAttribEXT");
        _createDevice = (delegate* unmanaged<int, nint, nint*, nint>)Proc("eglCreateDeviceANGLE");
        _releaseDevice = (delegate* unmanaged<nint, int>)Proc("eglReleaseDeviceANGLE");
        _createImage = (delegate* unmanaged<nint, nint, int, nint, int*, nint>)Proc("eglCreateImageKHR");
        _destroyImage = (delegate* unmanaged<nint, nint, int>)Proc("eglDestroyImageKHR");
        _imageTargetTexture = (delegate* unmanaged<uint, nint, void>)Proc("glEGLImageTargetTexture2DOES");
        _getPlatformDisplay = (delegate* unmanaged<int, nint, int*, nint>)Proc("eglGetPlatformDisplayEXT");
        _missing = missing.Count > 0 ? string.Join(", ", missing) : null;
        return _missing;
    }

    /// <summary>
    /// The D3D11 device ANGLE renders the given display with (borrowed pointer, not AddRef'd), 0 when it cannot be queried.
    /// </summary>
    public static ID3D11Device* DeviceOfDisplay(nint display)
    {
        nint eglDevice = 0, d3d = 0;
        if (Load() != null || display == 0)
            return null;
        if (_queryDisplayAttrib(display, EGL_DEVICE_EXT, &eglDevice) == 0 || eglDevice == 0)
            return null;
        if (_queryDeviceAttrib(eglDevice, EGL_D3D11_DEVICE_ANGLE, &d3d) == 0)
            return null;
        return (ID3D11Device*)d3d;
    }

    /// <summary>
    /// A private ANGLE display on our own D3D11 device, initialized, with an ES context and a 16x16 pbuffer.
    /// </summary>
    public static (nint display, nint eglDevice, nint context, nint pbuffer) CreatePrivateDisplay(ID3D11Device* device)
    {
        var eglDevice = _createDevice(EGL_D3D11_DEVICE_ANGLE, (nint)device, null);
        if (eglDevice == 0)
            throw new InvalidOperationException($"eglCreateDeviceANGLE failed, egl 0x{eglGetError():X}");
        var none = EGL_NONE;
        var display = _getPlatformDisplay(EGL_PLATFORM_DEVICE_EXT, eglDevice, &none);
        if (display == 0 || eglInitialize(display, out _, out _) == 0)
        {
            _releaseDevice(eglDevice);
            throw new InvalidOperationException($"private ANGLE display on our device failed, egl 0x{eglGetError():X}");
        }

        var configs = new nint[1];
        eglChooseConfig(display, [EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE], configs, 1, out var count);
        var context = count > 0 ? eglCreateContext(display, configs[0], 0, [EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE]) : 0;
        var pbuffer = context != 0 ? eglCreatePbufferSurface(display, configs[0], [EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE]) : 0;
        if (pbuffer == 0)
        {
            var error = eglGetError();
            if (context != 0)
                eglDestroyContext(display, context);
            eglTerminate(display);
            _releaseDevice(eglDevice);
            throw new InvalidOperationException($"context on the private display failed, egl 0x{error:X}");
        }
        return (display, eglDevice, context, pbuffer);
    }

    public static void DestroyPrivateDisplay(nint display, nint eglDevice, nint context, nint pbuffer)
    {
        eglMakeCurrent(display, 0, 0, 0);
        if (context != 0)
            eglDestroyContext(display, context);
        if (pbuffer != 0)
            eglDestroySurface(display, pbuffer);
        eglTerminate(display);
        eglReleaseThread();
        if (eglDevice != 0)
            _releaseDevice(eglDevice);
    }

    /// <summary>
    /// A GL texture of the current context bound to a D3D11 texture of the display's device through an EGLImage. No copy:
    /// Direct3D writes to the texture are what the GL texture shows.
    /// </summary>
    public static uint BindTexture(nint display, ID3D11Texture2D* texture, out nint image)
    {
        var none = EGL_NONE;
        image = _createImage(display, 0, EGL_D3D11_TEXTURE_ANGLE, (nint)texture, &none);
        if (image == 0)
            throw new InvalidOperationException($"eglCreateImageKHR(EGL_D3D11_TEXTURE_ANGLE) failed, egl 0x{eglGetError():X}");
        glGenTextures(1, out var gl);
        glBindTexture(GL_TEXTURE_2D, gl);
        _imageTargetTexture(GL_TEXTURE_2D, image);
        var error = glGetError();
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glBindTexture(GL_TEXTURE_2D, 0);
        if (error != 0)
        {
            glDeleteTextures(1, ref gl);
            _destroyImage(display, image);
            image = 0;
            throw new InvalidOperationException($"glEGLImageTargetTexture2DOES gl error 0x{error:X}");
        }
        return gl;
    }

    public static void UnbindTexture(nint display, uint gl, nint image)
    {
        if (gl != 0)
            glDeleteTextures(1, ref gl);
        if (image != 0)
            _destroyImage(display, image);
    }

    /// <summary>
    /// A BGRA D3D11 texture of the display's device as an SKImage of <paramref name="gr"/> (no copy).
    /// </summary>
    public static SKImage WrapImage(nint display, GRContext gr, ID3D11Texture2D* texture, int width, int height, out uint gl, out nint image)
    {
        gl = BindTexture(display, texture, out image);
        gr.ResetContext(); // the raw GL calls changed bindings behind Skia's back
        using var backend = new GRBackendTexture(width, height, false, new GRGlTextureInfo(GL_TEXTURE_2D, gl, GL_BGRA8_EXT)); // Skia keeps its own copy
        return SKImage.FromTexture(gr, backend, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888, SKAlphaType.Premul)
               ?? throw new InvalidOperationException("SKImage.FromTexture returned null for the EGLImage texture");
    }

    /// <summary>
    /// An SKSurface of <paramref name="gr"/> drawing straight into a BGRA D3D11 render target of the display's device.
    /// </summary>
    public static SKSurface WrapSurface(nint display, GRContext gr, ID3D11Texture2D* texture, int width, int height, out uint gl, out nint image)
    {
        gl = BindTexture(display, texture, out image);
        gr.ResetContext();
        using var backend = new GRBackendTexture(width, height, false, new GRGlTextureInfo(GL_TEXTURE_2D, gl, GL_BGRA8_EXT)); // Skia keeps its own copy
        return SKSurface.Create(gr, backend, GRSurfaceOrigin.TopLeft, 1, SKColorType.Bgra8888)
               ?? throw new InvalidOperationException("SKSurface over the D3D render target returned null");
    }
}
