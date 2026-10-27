#if ANDROID
using System.Collections.Concurrent;
using Android.Media;
using Android.Opengl;
using Android.Runtime;
using SkiaSharp;
using Image = Android.Media.Image;

namespace DrawnUi.Camera
{
    /// <summary>
    /// How a YUV_420_888 camera frame becomes an RGBA image.
    /// </summary>
    internal readonly struct YuvConversion
    {
        public YuvConversion(int rotation, bool mirrorX, bool mirrorY, float gamma = 1f, bool bw = false, bool invert = false, bool useLut = false)
        {
            Rotation = rotation;
            MirrorX = mirrorX;
            MirrorY = mirrorY;
            Gamma = gamma;
            Bw = bw;
            Invert = invert;
            UseLut = useLut;
        }

        public readonly int Rotation;
        public readonly bool MirrorX;
        public readonly bool MirrorY;
        public readonly float Gamma;
        public readonly bool Bw;
        public readonly bool Invert;
        public readonly bool UseLut;

        public bool Adjusts => Gamma != 1f || Bw || Invert;

        /// <summary>
        /// Output size: rotated by 90/270 swaps the frame's width and height.
        /// </summary>
        public (int Width, int Height) OutputSize(int width, int height)
            => Rotation == 90 || Rotation == 270 ? (height, width) : (width, height);
    }

    /// <summary>
    /// Converts YUV_420_888 camera frames (still photos, and preview frames on phones without the GPU preview)
    /// to RGBA with an SkSL shader, on a GL context of its own on its own thread: off DrawnUi's render thread,
    /// and the context is never made current anywhere else. Same integer math and rotation mapping as the
    /// RenderScript kernels it replaces (Scripts.rs YuvToRGB + adjustRotate in AppoMobi.Maui.Native), so the
    /// colors are the same. Converts on the CPU only where no GL context can be created.
    /// </summary>
    internal sealed class YuvConverter : IDisposable
    {
        const string Sksl = """
uniform shader yPlane;
uniform shader uPlane;
uniform shader vPlane;
uniform shader lut;
uniform float2 srcSize;
uniform float2 outSize;
uniform float rotation;
uniform float2 mirror;
uniform float uvPixelStride;
uniform float2 uvOffset;
uniform float gamma;
uniform float bw;
uniform float invertColors;
uniform float useLut;

half4 main(float2 coord)
{
    float x = floor(coord.x);
    float y = floor(coord.y);
    float useX = mirror.x > 0.5 ? outSize.x - 1.0 - x : x;
    float useY = mirror.y > 0.5 ? outSize.y - 1.0 - y : y;

    // adjustRotate in Scripts.rs, its 180 case included
    float inX = useX;
    float inY = useY;
    if (rotation == 270.0) { inX = srcSize.x - 1.0 - useY; inY = useX; }
    else if (rotation == 180.0) { inX = useY; inY = useX; }
    else if (rotation == 90.0) { inX = useY; inY = srcSize.y - 1.0 - useX; }
    inX = clamp(inX, 0.0, srcSize.x - 1.0);
    inY = clamp(inY, 0.0, srcSize.y - 1.0);

    float cx = uvPixelStride * floor(inX / 2.0);
    float cy = floor(inY / 2.0);
    float Y = floor(float(yPlane.eval(float2(inX, inY) + 0.5).r) * 255.0 + 0.5);
    float U = floor(float(uPlane.eval(float2(cx + uvOffset.x, cy) + 0.5).r) * 255.0 + 0.5);
    float V = floor(float(vPlane.eval(float2(cx + uvOffset.y, cy) + 0.5).r) * 255.0 + 0.5);

    // YuvToRGB in Scripts.rs. Every division there has non-negative operands, so floor() is its integer division;
    // v * 93604 / 131072 is computed as v * 46802 / 65536 to stay exact in 32-bit floats.
    float r = clamp(Y + floor(V * 1436.0 / 1024.0) - 179.0, 0.0, 255.0);
    float g = clamp(Y - floor(U * 46549.0 / 131072.0) + 44.0 - floor(V * 46802.0 / 65536.0) + 91.0, 0.0, 255.0);
    float b = clamp(Y + floor(U * 1814.0 / 1024.0) - 227.0, 0.0, 255.0);

    if (useLut > 0.5)
    {
        r = floor(float(lut.eval(float2(r + 0.5, 0.5)).r) * 255.0 + 0.5);
        g = floor(float(lut.eval(float2(g + 0.5, 0.5)).g) * 255.0 + 0.5);
        b = floor(float(lut.eval(float2(b + 0.5, 0.5)).b) * 255.0 + 0.5);
    }

    if (gamma != 1.0 || bw > 0.5 || invertColors > 0.5)
    {
        float3 c = float3(r, g, b) / 255.0;
        if (gamma != 1.0)
            c = pow(c, float3(gamma));
        if (bw > 0.5)
            c = float3(dot(c, float3(0.299, 0.587, 0.114)));
        if (invertColors > 0.5)
            c = 1.0 - c;
        c = floor(clamp(c, 0.0, 1.0) * 255.0 + 0.5);
        r = c.r;
        g = c.g;
        b = c.b;
    }

    return half4(half3(float3(r, g, b) / 255.0), 1.0);
}
""";

        /// <summary>
        /// RGBA 256 entries, the "auto" color negative preset (was the ScriptIntrinsicLUT of SplinesHelper).
        /// </summary>
        internal static readonly byte[] NegativeLut = CreateLut(
            new CubicSpline((33, 255), (119, 127), (185, 0)),
            new CubicSpline((28, 255), (77, 127), (135, 0)),
            new CubicSpline((25, 255), (60, 127), (108, 0)));

        static byte[] CreateLut(CubicSpline red, CubicSpline green, CubicSpline blue)
        {
            static byte Channel(CubicSpline spline, int x) =>
                (byte)Math.Clamp((int)Math.Round(spline.Eval(x / 255f) * 255.0), 0, 255);

            var lut = new byte[256 * 4];
            for (int x = 0; x < 256; x++)
            {
                lut[x * 4] = Channel(red, x);
                lut[x * 4 + 1] = Channel(green, x);
                lut[x * 4 + 2] = Channel(blue, x);
                lut[x * 4 + 3] = 255;
            }
            return lut;
        }

        readonly BlockingCollection<Action> _work = new();
        System.Threading.Thread _thread;

        EGLDisplay _eglDisplay = EGL14.EglNoDisplay;
        EGLContext _eglContext = EGL14.EglNoContext;
        EGLSurface _eglSurface = EGL14.EglNoSurface;
        GRContext _grContext;
        SKRuntimeEffect _effect;
        SKImage _lutImage;
        int _maxTextureSize;
        bool _gpuReady;
        volatile bool _unresponsive;
        volatile string _stage = "starting";

        /// <summary>
        /// How long a caller waits for the GPU before converting on the CPU itself. A photo takes ~40 ms on a
        /// mid-range phone; the wait also covers the context setup when the first photo comes right after the start.
        /// </summary>
        const int GpuWaitMs = 3000;

        /// <summary>
        /// One conversion request. The converter reads the camera image only while State is UsingImage, so a
        /// caller that gave up can convert (and then close) the image itself once ImageReleased is set.
        /// </summary>
        sealed class Job
        {
            public const int Queued = 0, UsingImage = 1, ImageReleased = 2, Abandoned = 3;
            public int State;
            public volatile bool CallerGone;
            public readonly System.Threading.ManualResetEventSlim Released = new(false);
            public readonly System.Threading.ManualResetEventSlim Done = new(false);
            public SKImage Result;
            public Exception Error;

            public void ReleaseImage()
            {
                System.Threading.Volatile.Write(ref State, ImageReleased);
                Released.Set();
            }
        }

        /// <summary>
        /// Starts the converter thread and creates its GL context there (shader compiled too, so the first photo
        /// does not pay for it). Without a GL context the converter still works, on the CPU.
        /// </summary>
        public YuvConverter()
        {
            _thread = new System.Threading.Thread(Loop)
            {
                IsBackground = true,
                Name = "CameraYuvConverter"
            };
            _thread.Start();
            _work.Add(SetupGpu);
        }

        void Loop()
        {
            foreach (var work in _work.GetConsumingEnumerable())
                work();

            ReleaseGpu();
        }

        void SetupGpu()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _stage = "setup: EGL";
                _eglDisplay = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
                int[] version = new int[2];
                if (!EGL14.EglInitialize(_eglDisplay, version, 0, version, 1))
                    throw new InvalidOperationException("EGL initialize failed");

                int[] attribs =
                {
                    EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8, EGL14.EglAlphaSize, 8,
                    EGL14.EglRenderableType, 0x40, // EGL_OPENGL_ES3_BIT_KHR
                    EGL14.EglSurfaceType, EGL14.EglPbufferBit,
                    EGL14.EglNone
                };
                var configs = new EGLConfig[1];
                var numConfigs = new int[1];
                if (!EGL14.EglChooseConfig(_eglDisplay, attribs, 0, configs, 0, 1, numConfigs, 0) || numConfigs[0] == 0)
                    throw new InvalidOperationException("EGL choose config failed");

                _eglContext = EGL14.EglCreateContext(_eglDisplay, configs[0], EGL14.EglNoContext,
                    new[] { EGL14.EglContextClientVersion, 3, EGL14.EglNone }, 0);
                if (_eglContext == null || _eglContext == EGL14.EglNoContext)
                    throw new InvalidOperationException("EGL create context failed");

                _eglSurface = EGL14.EglCreatePbufferSurface(_eglDisplay, configs[0],
                    new[] { EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone }, 0);
                if (_eglSurface == null || _eglSurface == EGL14.EglNoSurface)
                    throw new InvalidOperationException("EGL create pbuffer failed");

                if (!EGL14.EglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext))
                    throw new InvalidOperationException("EGL make current failed");

                var max = new int[1];
                GLES20.GlGetIntegerv(GLES20.GlMaxTextureSize, max, 0);
                _maxTextureSize = max[0];

                _stage = "setup: GRContext";
                _grContext = GRContext.CreateGl(GRGlInterface.Create());
                if (_grContext == null)
                    throw new InvalidOperationException("GRContext create failed");

                _stage = "setup: shader";
                _effect = SKRuntimeEffect.CreateShader(Sksl, out var errors);
                if (_effect == null)
                    throw new InvalidOperationException($"YUV shader: {errors}");

                var lutInfo = new SKImageInfo(256, 1, SKColorType.Rgba8888, SKAlphaType.Opaque);
                _lutImage = SKImage.FromPixelCopy(lutInfo, NegativeLut);

                _stage = "setup: warm-up";
                using (var plane = SKImage.FromPixelCopy(new SKImageInfo(16, 16, SKColorType.Gray8, SKAlphaType.Opaque), new byte[256]))
                using (var warm = Render(plane, plane, plane, 16, 16, 16, 16, new YuvConversion(90, false, false), 2, 0, 1))
                {
                }

                _gpuReady = true;
                _stage = "idle";
                Super.Log($"[YuvConverter] GPU ready in {sw.ElapsedMilliseconds} ms (GL '{GLES20.GlGetString(GLES20.GlRenderer)}', max texture {_maxTextureSize})");
            }
            catch (Exception e)
            {
                _stage = "idle";
                Super.Log($"[YuvConverter] GPU conversion unavailable, converting on the CPU: {e.Message}");
                ReleaseGpu();
            }
        }

        void ReleaseGpu()
        {
            _gpuReady = false;

            _lutImage?.Dispose();
            _lutImage = null;
            _effect?.Dispose();
            _effect = null;
            _grContext?.Dispose();
            _grContext = null;

            if (_eglDisplay != EGL14.EglNoDisplay)
            {
                EGL14.EglMakeCurrent(_eglDisplay, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                if (_eglSurface != EGL14.EglNoSurface)
                    EGL14.EglDestroySurface(_eglDisplay, _eglSurface);
                if (_eglContext != EGL14.EglNoContext)
                    EGL14.EglDestroyContext(_eglDisplay, _eglContext);
            }

            _eglSurface = EGL14.EglNoSurface;
            _eglContext = EGL14.EglNoContext;
            _eglDisplay = EGL14.EglNoDisplay;
        }

        /// <summary>
        /// Converts the frame and returns a raster image the caller owns. Blocks the calling thread until done.
        /// The image must stay open until this returns.
        /// </summary>
        /// <param name="purgeAfter">Release the GPU memory of this size afterwards (full-size photos).</param>
        public SKImage Convert(Image image, YuvConversion options, bool purgeAfter)
        {
            if (_unresponsive)
                return ConvertOnCpu(image, options);

            var job = new Job();
            try
            {
                _work.Add(() => RunJob(job, image, options, purgeAfter));
            }
            catch (InvalidOperationException)
            {
                return ConvertOnCpu(image, options); // disposed
            }

            if (job.Done.Wait(GpuWaitMs) && job.Error == null && job.Result != null)
                return job.Result;

            if (job.Error != null)
            {
                Super.Log($"[YuvConverter] GPU conversion failed, converting on the CPU: {job.Error.Message}");
            }
            else if (!job.Done.IsSet)
            {
                // the converter thread is stuck: never touch it again, and convert this frame here
                _unresponsive = true;
                Super.Log($"[YuvConverter] GPU converter did not respond within {GpuWaitMs} ms (stage: {_stage}), converting on the CPU from now on");

                job.CallerGone = true;
                if (System.Threading.Interlocked.CompareExchange(ref job.State, Job.Abandoned, Job.Queued) != Job.Queued)
                    job.Released.Wait(); // it is copying the planes or converting on the CPU: cannot hang
                if (job.Done.IsSet && job.Result != null)
                    return job.Result;
            }

            return ConvertOnCpu(image, options);
        }

        void RunJob(Job job, Image image, YuvConversion options, bool purgeAfter)
        {
            if (System.Threading.Interlocked.CompareExchange(ref job.State, Job.UsingImage, Job.Queued) != Job.Queued)
                return; // the caller converted it already

            try
            {
                job.Result = ConvertOnThread(job, image, options, purgeAfter);
                if (job.CallerGone)
                {
                    job.Result?.Dispose();
                    job.Result = null;
                }
            }
            catch (Exception e)
            {
                job.Error = e;
            }
            finally
            {
                job.ReleaseImage();
                job.Done.Set();
                _stage = "idle";
            }
        }

        SKImage ConvertOnThread(Job job, Image image, YuvConversion options, bool purgeAfter)
        {
            _stage = "convert: planes";
            if (!PlaneLayout.TryRead(image, out var layout))
            {
                Super.Log($"[YuvConverter] unexpected YUV plane layout, converting on the CPU");
                return ConvertOnCpu(image, options);
            }

            var (outW, outH) = options.OutputSize(image.Width, image.Height);
            if (!_gpuReady || Math.Max(Math.Max(outW, outH), layout.MaxImageSide) > _maxTextureSize)
            {
                if (_gpuReady)
                    Super.Log($"[YuvConverter] {outW}x{outH} is above the GPU texture limit {_maxTextureSize}, converting on the CPU");
                return ConvertOnCpu(image, options);
            }

            // the planes are copied first (a plain memory copy, no per-pixel work): from here on the GPU work does not
            // read the camera image, so a caller that stops waiting can close it safely
            using var yImage = layout.CreateYImage();
            using var uImage = layout.CreateUImage();
            using var vImage = layout.Interleaved ? null : layout.CreateVImage(); // interleaved: V is read from the U image
            job.ReleaseImage();

            var result = Render(yImage, uImage, vImage ?? uImage, image.Width, image.Height, outW, outH, options,
                layout.UvPixelStride, layout.UOffset, layout.VOffset);

            // the image is released: a failure here is converted on the CPU by the caller, which still holds it
            if (result == null)
                throw new InvalidOperationException($"GPU surface {outW}x{outH} failed");

            if (purgeAfter)
                _grContext.PurgeUnlockedResources(true);

            return result;
        }

        /// <summary>
        /// One shader pass into a GPU surface and the single readback.
        /// </summary>
        SKImage Render(SKImage yImage, SKImage uImage, SKImage vImage, int srcW, int srcH, int outW, int outH,
            YuvConversion options, int uvPixelStride, int uOffset, int vOffset)
        {
            _stage = "convert: draw";
            _grContext.ResetContext();
            var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);

            var uniforms = new SKRuntimeEffectUniforms(_effect)
            {
                ["srcSize"] = new[] { (float)srcW, srcH },
                ["outSize"] = new[] { (float)outW, outH },
                ["rotation"] = (float)options.Rotation,
                ["mirror"] = new[] { options.MirrorX ? 1f : 0f, options.MirrorY ? 1f : 0f },
                ["uvPixelStride"] = (float)uvPixelStride,
                ["uvOffset"] = new[] { (float)uOffset, vOffset },
                ["gamma"] = options.Gamma,
                ["bw"] = options.Bw ? 1f : 0f,
                ["invertColors"] = options.Invert ? 1f : 0f,
                ["useLut"] = options.UseLut ? 1f : 0f,
            };

            using var yShader = yImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
            using var uShader = uImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
            using var vShader = vImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
            using var lutShader = _lutImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
            var children = new SKRuntimeEffectChildren(_effect)
            {
                ["yPlane"] = yShader,
                ["uPlane"] = uShader,
                ["vPlane"] = vShader,
                ["lut"] = lutShader,
            };

            var info = new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(_grContext, false, info);
            if (surface == null)
                return null;

            using (var shader = _effect.ToShader(uniforms, children))
            using (var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src })
            {
                surface.Canvas.DrawRect(0, 0, outW, outH, paint);
            }

            // the single readback
            _stage = "convert: readback";
            SKImage result;
            using (var gpuImage = surface.Snapshot())
            {
                result = gpuImage.ToRasterImage(true);
            }

            return result;
        }

        /// <summary>
        /// Last resort where no GL context exists: the same math per pixel on the CPU.
        /// </summary>
        internal static unsafe SKImage ConvertOnCpu(Image image, YuvConversion options)
        {
            var planes = image.GetPlanes();
            byte* yPtr = (byte*)JNIEnv.GetDirectBufferAddress(planes[0].Buffer.Handle);
            byte* uPtr = (byte*)JNIEnv.GetDirectBufferAddress(planes[1].Buffer.Handle);
            byte* vPtr = (byte*)JNIEnv.GetDirectBufferAddress(planes[2].Buffer.Handle);
            int yRowStride = planes[0].RowStride;
            int uvRowStride = planes[1].RowStride;
            int uvPixelStride = planes[1].PixelStride;

            int srcW = image.Width, srcH = image.Height;
            var (dstW, dstH) = options.OutputSize(srcW, srcH);

            var lut = options.UseLut ? NegativeLut : null;
            var gammaCurve = new float[256];
            for (int i = 0; i < 256; i++)
                gammaCurve[i] = options.Gamma == 1f ? i / 255f : MathF.Pow(i / 255f, options.Gamma);
            bool adjust = options.Adjusts;

            var bitmap = new SKBitmap(new SKImageInfo(dstW, dstH, SKColorType.Rgba8888, SKAlphaType.Premul));
            byte* dst = (byte*)bitmap.GetPixels();
            int dstStride = bitmap.RowBytes;

            System.Threading.Tasks.Parallel.For(0, dstH, y =>
            {
                byte* row = dst + (long)y * dstStride;
                for (int x = 0; x < dstW; x++)
                {
                    MapToSource(options, x, y, srcW, srcH, dstW, dstH, out int inX, out int inY);

                    int uvIndex = uvPixelStride * (inX / 2) + uvRowStride * (inY / 2);
                    int yv = yPtr[inY * yRowStride + inX];
                    int u = uPtr[uvIndex];
                    int v = vPtr[uvIndex];

                    int r = Math.Clamp(yv + v * 1436 / 1024 - 179, 0, 255);
                    int g = Math.Clamp(yv - u * 46549 / 131072 + 44 - v * 93604 / 131072 + 91, 0, 255);
                    int b = Math.Clamp(yv + u * 1814 / 1024 - 227, 0, 255);

                    if (lut != null)
                    {
                        r = lut[r * 4];
                        g = lut[g * 4 + 1];
                        b = lut[b * 4 + 2];
                    }

                    if (adjust)
                    {
                        float fr = gammaCurve[r], fg = gammaCurve[g], fb = gammaCurve[b];
                        if (options.Bw)
                            fr = fg = fb = fr * 0.299f + fg * 0.587f + fb * 0.114f;
                        if (options.Invert)
                        {
                            fr = 1f - fr;
                            fg = 1f - fg;
                            fb = 1f - fb;
                        }
                        r = Pack(fr);
                        g = Pack(fg);
                        b = Pack(fb);
                    }

                    byte* p = row + x * 4;
                    p[0] = (byte)r;
                    p[1] = (byte)g;
                    p[2] = (byte)b;
                    p[3] = 255;
                }
            });

            bitmap.SetImmutable();
            var result = SKImage.FromBitmap(bitmap);
            bitmap.Dispose();
            return result;
        }

        static int Pack(float c) => (int)(Math.Clamp(c, 0f, 1f) * 255f + 0.5f);

        static void MapToSource(YuvConversion o, int x, int y, int srcW, int srcH, int outW, int outH, out int inX, out int inY)
        {
            int useX = o.MirrorX ? outW - 1 - x : x;
            int useY = o.MirrorY ? outH - 1 - y : y;

            inX = useX;
            inY = useY;
            if (o.Rotation == 270)
            {
                inX = srcW - 1 - useY;
                inY = useX;
            }
            else if (o.Rotation == 180)
            {
                inX = useY;
                inY = useX;
            }
            else if (o.Rotation == 90)
            {
                inX = useY;
                inY = srcH - 1 - useX;
            }

            inX = Math.Clamp(inX, 0, srcW - 1);
            inY = Math.Clamp(inY, 0, srcH - 1);
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            if (_thread != null && _thread != System.Threading.Thread.CurrentThread)
                _thread.Join(2000);
            _thread = null;
        }

        /// <summary>
        /// The frame's planes as 8-bit images copied from the camera's buffers: Y, and the chroma either interleaved
        /// (one buffer read at two byte offsets) or planar.
        /// </summary>
        readonly struct PlaneLayout
        {
            readonly IntPtr _y, _u, _v;
            readonly int _width, _height, _yRowStride, _uvRowStride, _uvWidthBytes, _chromaHeight;
            readonly bool _interleaved;

            public readonly int UvPixelStride;
            public readonly int UOffset;
            public readonly int VOffset;

            public bool Interleaved => _interleaved;

            public int MaxImageSide => Math.Max(Math.Max(_width, _height), _uvWidthBytes);

            PlaneLayout(IntPtr y, IntPtr u, IntPtr v, int width, int height, int yRowStride, int uvRowStride,
                int uvPixelStride, int uvWidthBytes, int chromaHeight, bool interleaved, int uOffset, int vOffset)
            {
                _y = y;
                _u = u;
                _v = v;
                _width = width;
                _height = height;
                _yRowStride = yRowStride;
                _uvRowStride = uvRowStride;
                UvPixelStride = uvPixelStride;
                _uvWidthBytes = uvWidthBytes;
                _chromaHeight = chromaHeight;
                _interleaved = interleaved;
                UOffset = uOffset;
                VOffset = vOffset;
            }

            public static bool TryRead(Image image, out PlaneLayout layout)
            {
                layout = default;
                var planes = image.GetPlanes();
                if (planes == null || planes.Length < 3 || planes[0].PixelStride != 1
                    || planes[1].PixelStride != planes[2].PixelStride || planes[1].RowStride != planes[2].RowStride)
                    return false;

                int width = image.Width, height = image.Height;
                int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
                int yRowStride = planes[0].RowStride;
                int uvRowStride = planes[1].RowStride;
                int uvPixelStride = planes[1].PixelStride;

                IntPtr y = JNIEnv.GetDirectBufferAddress(planes[0].Buffer.Handle);
                IntPtr u = JNIEnv.GetDirectBufferAddress(planes[1].Buffer.Handle);
                IntPtr v = JNIEnv.GetDirectBufferAddress(planes[2].Buffer.Handle);
                if (y == IntPtr.Zero || u == IntPtr.Zero || v == IntPtr.Zero)
                    return false;

                long ySize = planes[0].Buffer.Capacity();
                long uSize = planes[1].Buffer.Capacity();
                long vSize = planes[2].Buffer.Capacity();
                if ((long)yRowStride * (height - 1) + width > ySize)
                    return false;

                if (uvPixelStride == 1)
                {
                    long need = (long)uvRowStride * (chromaHeight - 1) + chromaWidth;
                    if (need > uSize || need > vSize)
                        return false;
                    layout = new PlaneLayout(y, u, v, width, height, yRowStride, uvRowStride, 1, chromaWidth,
                        chromaHeight, false, 0, 0);
                    return true;
                }

                // NV12 / NV21: U and V are the same interleaved bytes, one apart; each plane buffer stops one byte
                // short of the other's last byte, which is still valid memory read from the lower pointer
                long distance = (long)v - (long)u;
                if (uvPixelStride == 2 && (distance == 1 || distance == -1))
                {
                    IntPtr low = distance > 0 ? u : v;
                    long available = Math.Max((long)u + uSize, (long)v + vSize) - (long)low;
                    int widthBytes = 2 * chromaWidth;
                    if ((long)uvRowStride * (chromaHeight - 1) + widthBytes > available)
                        return false;
                    layout = new PlaneLayout(y, low, low, width, height, yRowStride, uvRowStride, 2, widthBytes,
                        chromaHeight, true, distance > 0 ? 0 : 1, distance > 0 ? 1 : 0);
                    return true;
                }

                return false;
            }

            static SKImage Gray(IntPtr pixels, int width, int height, int rowBytes)
                => SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque), pixels, rowBytes);

            public SKImage CreateYImage() => Gray(_y, _width, _height, _yRowStride);

            public SKImage CreateUImage() => Gray(_u, _uvWidthBytes, _chromaHeight, _uvRowStride);

            public SKImage CreateVImage() => Gray(_v, _uvWidthBytes, _chromaHeight, _uvRowStride);
        }
    }
}
#endif
