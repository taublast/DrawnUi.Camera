#if ANDROID
using Android.Opengl;
using Android.Views;
using Java.Nio;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace DrawnUi.Camera
{
    /// <summary>
    /// GPU-based preview renderer that replaces the RenderScript/ImageReader preview pipeline.
    /// Uses a dedicated GL thread with EGL pbuffer surface, SurfaceTexture for zero-copy camera
    /// frames, and OES shader for GPU-native YUV→RGB conversion.
    /// Mirrors AndroidCaptureVideoEncoder's GPU thread pattern but without encoding.
    /// </summary>
    public class GlPreviewRenderer : IDisposable
    {
        // EGL resources (owned by this class)
        private EGLDisplay _eglDisplay = EGL14.EglNoDisplay;
        private EGLContext _eglContext = EGL14.EglNoContext;
        private EGLSurface _eglPbufferSurface = EGL14.EglNoSurface;

        // GPU camera frame provider (reuse existing class)
        private GpuCameraFrameProvider _gpuFrameProvider;

        // Readback FBO (render OES texture here, then glReadPixels)
        private int _readbackFbo;
        private int _readbackRbo;

        // Source FBO for blit path (renders OES at camera resolution, then blits to readback FBO)
        private int _sourceFbo;
        private int _sourceRbo;

        private int _previewWidth;
        private int _previewHeight;

        // Pre-allocated row swap buffer for in-place Y-flip (direct path only)
        private byte[] _flipRowBuffer;

        // Double-buffered pinned image pool — zero per-frame allocation for pixel data
        private SkiaCpuImagePool _imagePool;

        // One GL thread owns the EGL context from Initialize to Dispose: setup, frames, buffer reset
        // and cleanup all run on it. The context is never made current on another thread: some GL
        // implementations (the Android emulator's) lose the context's objects when it moves.
        private System.Threading.Thread _glThread;
        private System.Threading.ManualResetEventSlim _frameSignal;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _glWork = new();
        private readonly object _frameLock = new();
        private volatile bool _stopThread;
        private volatile bool _running;
        private int _postResetFramesToIgnore;
        private bool _hasDeliveredPreviewFrame;

        // Preview delivery — event consumer owns the SKImage lifecycle

        // Camera parameters
        private bool _isFrontCamera;
        private int _cameraWidth;
        private int _cameraHeight;
        private int _sensorOrientation;
        // glReadPixels returns bottom-to-top data. The SurfaceTexture transform for 90°/0° sensors
        // produces an FBO where GL y=0 = image bottom, so we need the CPU Y-flip.
        // For 270°/180° sensors (most front cameras) the transform produces the opposite Y ordering,
        // so glReadPixels already returns top-to-bottom — CPU Y-flip would invert it.
        private bool _needsYFlip;

        // State
        private bool _initialized;
        private bool _disposed;

        /// <summary>
        /// Use glBlitFramebuffer path instead of direct render + CPU Y-flip.
        /// Renders OES at camera resolution to a source FBO, then blits downscaled + Y-flipped to the
        /// readback FBO via glBlitFramebuffer. Eliminates CPU Y-flip and uses GPU bilinear filtering.
        /// Auto-enabled on GLES 3.0+ devices (virtually all Android 4.3+ hardware) during Initialize().
        /// Can be forced off for debugging or low-end device fallback.
        /// </summary>
        internal static bool UseBlitPreview { get; set; } = false;

        /// <summary>
        /// Fired when a new preview frame is ready.
        /// Parameters: SKImage (caller takes ownership), timestampNs from SurfaceTexture.
        /// </summary>
        public event Action<SKImage, long> PreviewFrameReady;

        /// <summary>
        /// Number of preview frame hits to ignore after ResetPreviewBuffers().
        /// Use this to drain stale native/SurfaceTexture queue contents before delivering preview again.
        /// </summary>
        public static int PreviewFramesToIgnoreAfterReset { get; set; } = 5;

        /// <summary>
        /// Optional pull-driven gate. When provided and returns false, the current camera frame is
        /// consumed but no preview image is rendered or read back.
        /// </summary>
        public Func<bool> ShouldGeneratePreviewFrame { get; set; }

        public bool IsInitialized => _initialized;
        public int PreviewWidth => _previewWidth;
        public int PreviewHeight => _previewHeight;

        public bool ResetPreviewBuffers()
        {
            if (!_initialized || _disposed)
                return false;

            if (_running)
            {
                System.Diagnostics.Debug.WriteLine("[GlPreviewRenderer] ResetPreviewBuffers skipped: renderer is running");
                return false;
            }

            bool resetSuccess = false;
            RunOnGlThread(() =>
            {
                try
                {
                    ReleasePreviewBuffers();
                    _postResetFramesToIgnore = _hasDeliveredPreviewFrame
                        ? Math.Max(0, PreviewFramesToIgnoreAfterReset)
                        : 0;
                    resetSuccess = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] ResetPreviewBuffers error: {ex.Message}");
                }
            }, 5000);
            return resetSuccess;
        }

        /// <summary>
        /// Runs work on the GL thread and waits for it (or for the timeout).
        /// </summary>
        private void RunOnGlThread(Action work, int timeoutMs)
        {
            var done = new System.Threading.ManualResetEventSlim(false);
            _glWork.Enqueue(() =>
            {
                try { work(); }
                finally { done.Set(); }
            });
            _frameSignal?.Set();
            done.Wait(timeoutMs);
        }

        private void RunGlWork()
        {
            while (_glWork.TryDequeue(out var work))
                work();
        }

        /// <summary>
        /// Initialize the GPU preview renderer. Creates EGL context, SurfaceTexture, and readback FBO.
        /// Must be called before Start(). Starts the renderer's GL thread, which does the initialization.
        /// </summary>
        public bool Initialize(int cameraWidth, int cameraHeight, int previewWidth, int previewHeight, bool isFrontCamera, int sensorOrientation = 90)
        {
            if (_initialized) return true;

            _cameraWidth = cameraWidth;
            _cameraHeight = cameraHeight;
            _previewWidth = previewWidth;
            _previewHeight = previewHeight;
            _isFrontCamera = isFrontCamera;
            _sensorOrientation = sensorOrientation;
            // 90° and 0° sensor orientations: SurfaceTexture transform puts image bottom at GL y=0 → need CPU flip.
            // 270° and 180° sensor orientations: transform reverses Y in the FBO → CPU flip would double-invert.
            _needsYFlip = true;//(sensorOrientation == 90 || sensorOrientation == 0); isFrontCamera whatever..

            // Initialize EGL and GL resources on the GL thread that also renders (EGL context is thread-local)
            bool initSuccess = false;
            var initDone = new System.Threading.ManualResetEventSlim(false);
            Exception initError = null;

            _stopThread = false;
            _frameSignal = new System.Threading.ManualResetEventSlim(false);

            _glThread = new System.Threading.Thread(() =>
            {
                try
                {
                    SetupEgl();
                    MakeCurrent();

                    // Auto-enable GPU blit path on GLES 3.0+ (glBlitFramebuffer + conditional Y-flip in dst coords).
                    // Eliminates the CPU row-copy Y-flip loop in ProcessPreviewFrameDirect().
                    // GLES 3.0 is available on all Android 4.3+ devices (API 18+, released 2013).
                    // Must be checked after MakeCurrent() so the GL context is active for GlGetString.
                    if (!UseBlitPreview)
                    {
                        var glVersion = GLES20.GlGetString(GLES20.GlVersion); // e.g. "OpenGL ES 3.2 NVIDIA ..."
                        UseBlitPreview = glVersion != null && glVersion.Contains("OpenGL ES 3");
                        System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] GLES version: '{glVersion}' → UseBlitPreview={UseBlitPreview}");
                    }

                    // Create GPU camera frame provider (creates SurfaceTexture + OES shader)
                    _gpuFrameProvider = new GpuCameraFrameProvider();
                    if (!_gpuFrameProvider.Initialize(cameraWidth, cameraHeight))
                    {
                        throw new InvalidOperationException("Failed to initialize GpuCameraFrameProvider");
                    }

                    // Create readback FBO at preview resolution
                    SetupReadbackFbo(previewWidth, previewHeight);

                    // Blit path: also create source FBO at camera resolution for OES rendering
                    if (UseBlitPreview)
                    {
                        SetupSourceFbo(cameraWidth, cameraHeight);
                    }

                    // Pool owns the two pinned pixel buffers — glReadPixels writes directly into them
                    _imagePool = new SkiaCpuImagePool(previewWidth, previewHeight);

                    // CPU Y-flip buffer only needed for non-blit (direct) path
                    if (!UseBlitPreview)
                    {
                        _flipRowBuffer = new byte[previewWidth * 4];
                    }

                    initSuccess = true;
                }
                catch (Exception ex)
                {
                    initError = ex;
                    System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] Initialize error: {ex.Message}");
                }
                finally
                {
                    initDone.Set();
                }

                // the context stays current on this thread for its whole life
                if (initSuccess)
                    GlPreviewLoop();

                Cleanup();
            })
            {
                IsBackground = true,
                Name = "GlPreviewRenderer",
                Priority = System.Threading.ThreadPriority.AboveNormal
            };

            _glThread.Start();
            initDone.Wait(5000); // 5s timeout for initialization

            if (!initSuccess)
            {
                System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] Initialization failed: {initError?.Message}");
                // the GL thread cleans up and exits when its initialization ends
                _stopThread = true;
                _frameSignal.Set();
                return false;
            }

            _initialized = true;
            System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] Initialized: camera={cameraWidth}x{cameraHeight} preview={previewWidth}x{previewHeight} front={isFrontCamera} sensor={sensorOrientation}° needsYFlip={_needsYFlip} blitPath={UseBlitPreview}");
            return true;
        }

        /// <summary>
        /// Get the Surface for use as Camera2 output target.
        /// </summary>
        public Surface GetCameraOutputSurface()
        {
            return _gpuFrameProvider?.GetCameraOutputSurface();
        }

        /// <summary>
        /// Start the GL preview thread. Begins processing and delivering camera frames.
        /// </summary>
        public void Start()
        {
            if (!_initialized || _running) return;

            // Re-register the SurfaceTexture frame listener — it stops firing after
            // Camera2 session cycles (removed from session during recording, re-added after)
            _gpuFrameProvider?.ResetFrameListener();

            _gpuFrameProvider?.Start();

            // Subscribe to frame available events from SurfaceTexture
            if (_gpuFrameProvider?.Renderer != null)
            {
                _gpuFrameProvider.Renderer.OnFrameAvailable += OnCameraFrameAvailable;
            }

            _running = true;
            _frameSignal?.Set();
            System.Diagnostics.Debug.WriteLine("[GlPreviewRenderer] Started");
        }

        /// <summary>
        /// Stop the GL preview thread. Resources are kept for restart.
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            _running = false;

            // Unsubscribe from frame events
            if (_gpuFrameProvider?.Renderer != null)
            {
                _gpuFrameProvider.Renderer.OnFrameAvailable -= OnCameraFrameAvailable;
            }

            _gpuFrameProvider?.Stop();

            // wait for a frame in progress (up to 1 s, as the thread join did): no frame is processed once Stop returns
            if (System.Threading.Monitor.TryEnter(_frameLock, 1000))
                System.Threading.Monitor.Exit(_frameLock);

            System.Diagnostics.Debug.WriteLine("[GlPreviewRenderer] Stopped");
        }

        /// <summary>
        /// Called on arbitrary camera thread when a new frame is available from SurfaceTexture.
        /// Just signals the GL thread — no heavy work here.
        /// </summary>
        private void OnCameraFrameAvailable(object sender, Android.Graphics.SurfaceTexture surfaceTexture)
        {
            if (!_running) return;
            _frameSignal?.Set();
        }

        /// <summary>
        /// GL loop on the renderer's GL thread, with the EGL context current since initialization
        /// (bound once instead of per frame). Processes frames while started; runs queued GL work
        /// (buffer reset) at any time.
        /// </summary>
        private void GlPreviewLoop()
        {
            while (!_stopThread)
            {
                // Wait with timeout for frame signal while started; when stopped, sleep until woken
                _frameSignal.Wait(_running ? 100 : System.Threading.Timeout.Infinite);
                _frameSignal.Reset();

                RunGlWork();

                if (_stopThread) break;

                lock (_frameLock)
                {
                    if (!_running)
                        continue;

                    try
                    {
                        ProcessPreviewFrame();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] GlPreviewLoop error: {ex.Message}");
                    }
                }
            }

            RunGlWork();
            System.Diagnostics.Debug.WriteLine("[GlPreviewRenderer] GL thread exited");
        }

        /// <summary>
        /// Process a single preview frame on the GL thread.
        /// MakeCurrent → UpdateTexImage → OES render → readback → SKImage.
        /// Dispatches to Direct or Blit path based on UseBlitPreview flag.
        /// </summary>
        private void ProcessPreviewFrame()
        {
            if (_eglDisplay == EGL14.EglNoDisplay || _eglContext == EGL14.EglNoContext)
                return;

            // 1. Update SurfaceTexture (MUST be on EGL context thread — context bound once in GlPreviewLoop)
            if (!_gpuFrameProvider.TryProcessFrameNoWait(out long timestampNs))
                return;

            if (_postResetFramesToIgnore > 0)
            {
                _postResetFramesToIgnore--;
                return;
            }

            if (ShouldGeneratePreviewFrame != null && !ShouldGeneratePreviewFrame())
                return;

            EnsurePreviewBuffersReady();

            // 3. Reset GL state to clean defaults
            GLES20.GlUseProgram(0);
            GLES20.GlDisable(GLES20.GlBlend);
            GLES20.GlDisable(GLES20.GlDepthTest);
            GLES20.GlDisable(GLES20.GlStencilTest);
            GLES20.GlDisable(GLES20.GlScissorTest);
            GLES20.GlColorMask(true, true, true, true);
            GLES20.GlActiveTexture(GLES20.GlTexture0);
            GLES20.GlBindTexture(GLES20.GlTexture2d, 0);
            GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, 0);
            GLES20.GlBindBuffer(GLES20.GlArrayBuffer, 0);
            GLES20.GlBindBuffer(GLES20.GlElementArrayBuffer, 0);
            while (GLES20.GlGetError() != GLES20.GlNoError) { }

            // 4. Dispatch to the appropriate processing path
            SKImage image = UseBlitPreview
                ? ProcessPreviewFrameBlit()
                : ProcessPreviewFrameDirect();

            // 5. Restore default FBO
            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);

            // 6. Deliver preview — event consumer owns the SKImage lifecycle.
            // Fire directly on the GL thread: UpdatePreview() only posts an async invalidate to the
            // main thread (< 1µs), so the GL loop is not blocked. Task.Run was causing unpredictable
            // thread-pool scheduling delays (5-50ms) and back-to-back delivery races that disposed
            // frames before the UI thread could display them.
            if (image != null)
            {
                _hasDeliveredPreviewFrame = true;
                PreviewFrameReady?.Invoke(image, timestampNs);
            }
        }

        /// <summary>
        /// DIRECT path (original): render OES at preview resolution → glReadPixels → CPU Y-flip → SKImage.
        /// </summary>
        private SKImage ProcessPreviewFrameDirect()
        {
            // Bind readback FBO, set viewport, clear
            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _readbackFbo);
            GLES20.GlViewport(0, 0, _previewWidth, _previewHeight);
            GLES20.GlClear(GLES20.GlColorBufferBit);

            // Render OES texture — GPU driver does YUV→RGB automatically via samplerExternalOES
            _gpuFrameProvider.RenderToFramebuffer(_previewWidth, _previewHeight, _isFrontCamera);

            // Readback directly into the pool's write slot — no intermediate buffer
            var dest = _imagePool.GetWriteBuffer();
            ReadPixels(_previewWidth, _previewHeight);

            // glReadPixels returns bottom-to-top data. For 90°/0° sensor orientations the
            // SurfaceTexture transform leaves the image inverted in the FBO, so we must flip.
            // For 270°/180° sensors (most front cameras) the transform already yields the
            // correct Y ordering — flipping would invert the image (upside-down selfie bug).
            if (_needsYFlip)
            {
                int rowBytes = _previewWidth * 4;
                int halfHeight = _previewHeight / 2;
                for (int y = 0; y < halfHeight; y++)
                {
                    int topOffset = y * rowBytes;
                    int bottomOffset = (_previewHeight - 1 - y) * rowBytes;
                    System.Buffer.BlockCopy(dest, topOffset, _flipRowBuffer, 0, rowBytes);
                    System.Buffer.BlockCopy(dest, bottomOffset, dest, topOffset, rowBytes);
                    System.Buffer.BlockCopy(_flipRowBuffer, 0, dest, bottomOffset, rowBytes);
                }
            }

            return _imagePool.CommitAndGetImage();
        }

        /// <summary>
        /// BLIT path (recording-style): render OES at camera resolution to source FBO →
        /// glBlitFramebuffer downscale + GPU Y-flip to readback FBO → glReadPixels small buffer.
        /// Mirrors AndroidCaptureVideoEncoder + GlPreviewScaler pipeline but without the encoder.
        /// Eliminates CPU Y-flip; GPU bilinear filtering handles downscale.
        /// </summary>
        private SKImage ProcessPreviewFrameBlit()
        {
            // 1. Render OES at camera resolution to source FBO
            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _sourceFbo);
            GLES20.GlViewport(0, 0, _cameraWidth, _cameraHeight);
            GLES20.GlClear(GLES20.GlColorBufferBit);
            _gpuFrameProvider.RenderToFramebuffer(_cameraWidth, _cameraHeight, _isFrontCamera);

            // 2. Blit from source → readback with downscale + optional Y-flip.
            //    For 90°/0° sensors: invert dst Y (dstY0=height, dstY1=0) so glReadPixels
            //    returns top-to-bottom data — same trick as GlPreviewScaler.
            //    For 270°/180° sensors (most front cameras): SurfaceTexture transform already
            //    yields the correct Y ordering; blit without Y-flip to avoid double-inversion.
            int dstY0 = _needsYFlip ? _previewHeight : 0;
            int dstY1 = _needsYFlip ? 0 : _previewHeight;
            GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, _sourceFbo);
            GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, _readbackFbo);
            GLES30.GlBlitFramebuffer(
                0, 0, _cameraWidth, _cameraHeight,          // src: full camera resolution
                0, dstY0, _previewWidth, dstY1,              // dst: preview resolution, conditionally Y-flipped
                GLES20.GlColorBufferBit,
                GLES20.GlLinear);                             // GPU bilinear filtering

            // 3. Readback directly into pool's write slot — no intermediate buffer
            GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, _readbackFbo);
            ReadPixels(_previewWidth, _previewHeight);

            // 4. No Y-flip needed — blit already flipped on GPU; commit the pool slot
            return _imagePool.CommitAndGetImage();
        }

        #region EGL Setup

        /// <summary>
        /// Create EGL display, context, and a 1x1 pbuffer surface.
        /// Pattern from AndroidCaptureVideoEncoder.SetupEglForCodecSurface but using EGL_PBUFFER_BIT.
        /// </summary>
        private void SetupEgl()
        {
            _eglDisplay = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
            int[] version = new int[2];
            if (!EGL14.EglInitialize(_eglDisplay, version, 0, version, 1))
                throw new InvalidOperationException("EGL initialize failed");

            int[] attribList = {
                EGL14.EglRedSize, 8,
                EGL14.EglGreenSize, 8,
                EGL14.EglBlueSize, 8,
                EGL14.EglAlphaSize, 8,
                EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
                EGL14.EglSurfaceType, 0x0001, // EGL_PBUFFER_BIT
                EGL14.EglNone
            };
            EGLConfig[] configs = new EGLConfig[1];
            int[] numConfigs = new int[1];
            if (!EGL14.EglChooseConfig(_eglDisplay, attribList, 0, configs, 0, configs.Length, numConfigs, 0))
                throw new InvalidOperationException("EGL choose config failed");

            int[] ctxAttribs = { EGL14.EglContextClientVersion, 2, EGL14.EglNone };
            _eglContext = EGL14.EglCreateContext(_eglDisplay, configs[0], EGL14.EglNoContext, ctxAttribs, 0);
            if (_eglContext == null || _eglContext == EGL14.EglNoContext)
                throw new InvalidOperationException("EGL create context failed");

            // 1x1 pbuffer — we only need a valid surface to make the context current.
            // All rendering goes to our FBO, not the pbuffer.
            int[] pbufferAttribs = { EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone };
            _eglPbufferSurface = EGL14.EglCreatePbufferSurface(_eglDisplay, configs[0], pbufferAttribs, 0);
            if (_eglPbufferSurface == null || _eglPbufferSurface == EGL14.EglNoSurface)
                throw new InvalidOperationException("EGL create pbuffer surface failed");

            System.Diagnostics.Debug.WriteLine("[GlPreviewRenderer] EGL setup complete");
        }

        private void MakeCurrent()
        {
            if (!EGL14.EglMakeCurrent(_eglDisplay, _eglPbufferSurface, _eglPbufferSurface, _eglContext))
                throw new InvalidOperationException("EGL make current failed");
        }

        #endregion

        #region GL Resources

        private void EnsurePreviewBuffersReady()
        {
            if (_readbackFbo == 0)
            {
                SetupReadbackFbo(_previewWidth, _previewHeight);
            }

            if (UseBlitPreview && _sourceFbo == 0)
            {
                SetupSourceFbo(_cameraWidth, _cameraHeight);
            }

            if (_imagePool == null)
            {
                _imagePool = new SkiaCpuImagePool(_previewWidth, _previewHeight);
            }

            if (!UseBlitPreview && _flipRowBuffer == null)
            {
                _flipRowBuffer = new byte[_previewWidth * 4];
            }
        }

        private void ReleasePreviewBuffers()
        {
            if (_readbackFbo != 0)
            {
                GLES20.GlDeleteFramebuffers(1, new int[] { _readbackFbo }, 0);
                _readbackFbo = 0;
            }
            if (_readbackRbo != 0)
            {
                GLES20.GlDeleteRenderbuffers(1, new int[] { _readbackRbo }, 0);
                _readbackRbo = 0;
            }
            if (_sourceFbo != 0)
            {
                GLES20.GlDeleteFramebuffers(1, new int[] { _sourceFbo }, 0);
                _sourceFbo = 0;
            }
            if (_sourceRbo != 0)
            {
                GLES20.GlDeleteRenderbuffers(1, new int[] { _sourceRbo }, 0);
                _sourceRbo = 0;
            }

            _flipRowBuffer = null;
            _imagePool?.Dispose();
            _imagePool = null;
        }

        /// <summary>
        /// Create FBO + renderbuffer at preview resolution for readback.
        /// Same pattern as GlPreviewScaler.Initialize().
        /// </summary>
        private void SetupReadbackFbo(int width, int height)
        {
            int[] fbo = new int[1];
            GLES20.GlGenFramebuffers(1, fbo, 0);
            _readbackFbo = fbo[0];

            int[] rbo = new int[1];
            GLES20.GlGenRenderbuffers(1, rbo, 0);
            _readbackRbo = rbo[0];

            GLES20.GlBindRenderbuffer(GLES20.GlRenderbuffer, _readbackRbo);
            GLES30.GlRenderbufferStorage(GLES20.GlRenderbuffer, GLES30.GlRgba8, width, height);

            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _readbackFbo);
            GLES20.GlFramebufferRenderbuffer(GLES20.GlFramebuffer,
                GLES20.GlColorAttachment0, GLES20.GlRenderbuffer, _readbackRbo);

            int status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
            if (status != GLES20.GlFramebufferComplete)
            {
                throw new InvalidOperationException($"Readback FBO incomplete: 0x{status:X}");
            }

            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
            GLES20.GlBindRenderbuffer(GLES20.GlRenderbuffer, 0);

            System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] Readback FBO created: {width}x{height}");
        }

        /// <summary>
        /// Create FBO + renderbuffer at camera resolution for OES rendering (blit path source).
        /// The OES texture is rendered here at full camera resolution, then glBlitFramebuffer
        /// downscales + Y-flips to the readback FBO.
        /// </summary>
        private void SetupSourceFbo(int width, int height)
        {
            int[] fbo = new int[1];
            GLES20.GlGenFramebuffers(1, fbo, 0);
            _sourceFbo = fbo[0];

            int[] rbo = new int[1];
            GLES20.GlGenRenderbuffers(1, rbo, 0);
            _sourceRbo = rbo[0];

            GLES20.GlBindRenderbuffer(GLES20.GlRenderbuffer, _sourceRbo);
            GLES30.GlRenderbufferStorage(GLES20.GlRenderbuffer, GLES30.GlRgba8, width, height);

            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _sourceFbo);
            GLES20.GlFramebufferRenderbuffer(GLES20.GlFramebuffer,
                GLES20.GlColorAttachment0, GLES20.GlRenderbuffer, _sourceRbo);

            int status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
            if (status != GLES20.GlFramebufferComplete)
            {
                throw new InvalidOperationException($"Source FBO incomplete: 0x{status:X}");
            }

            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
            GLES20.GlBindRenderbuffer(GLES20.GlRenderbuffer, 0);

            System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] Source FBO created: {width}x{height}");
        }

        /// <summary>
        /// Reads the frame from the bound GL_READ_FRAMEBUFFER straight into the pool's pinned write slot.
        /// Not through a mapped pixel buffer: on Mali GPUs mapped buffer memory is uncached, and copying a
        /// 960x1280 frame out of it took 87 ms (preview at 9 fps); the direct read takes about 13 ms.
        /// Must be called on the GL thread.
        /// </summary>
        private void ReadPixels(int width, int height)
        {
            GLES20.GlReadPixels(0, 0, width, height, GLES20.GlRgba, GLES20.GlUnsignedByte, _imagePool.GetWriteDirectBuffer());
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Runs on the GL thread when it exits.
        /// </summary>
        private void Cleanup()
        {
            _gpuFrameProvider?.Dispose();
            _gpuFrameProvider = null;

            // GL resources must be cleaned up with EGL context current
            // If we can't make current, skip GL cleanup (context may already be destroyed)
            bool canCleanupGl = false;
            try
            {
                if (_eglDisplay != EGL14.EglNoDisplay && _eglContext != EGL14.EglNoContext && _eglPbufferSurface != EGL14.EglNoSurface)
                {
                    MakeCurrent();
                    canCleanupGl = true;
                }
            }
            catch { }

            if (canCleanupGl)
            {
                try
                {
                    ReleasePreviewBuffers();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] GL cleanup error: {ex.Message}");
                }
            }
            else
            {
                _flipRowBuffer = null;
                _imagePool?.Dispose();
                _imagePool = null;
            }

            // Destroy EGL resources
            try
            {
                if (_eglDisplay != EGL14.EglNoDisplay)
                {
                    EGL14.EglMakeCurrent(_eglDisplay, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);

                    if (_eglPbufferSurface != EGL14.EglNoSurface)
                    {
                        EGL14.EglDestroySurface(_eglDisplay, _eglPbufferSurface);
                        _eglPbufferSurface = EGL14.EglNoSurface;
                    }
                    if (_eglContext != EGL14.EglNoContext)
                    {
                        EGL14.EglDestroyContext(_eglDisplay, _eglContext);
                        _eglContext = EGL14.EglNoContext;
                    }
                    EGL14.EglTerminate(_eglDisplay);
                    _eglDisplay = EGL14.EglNoDisplay;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlPreviewRenderer] EGL cleanup error: {ex.Message}");
            }

            _initialized = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            // the GL thread releases everything with its own context, then exits
            _stopThread = true;
            _frameSignal?.Set();
            if (_glThread != null && _glThread != System.Threading.Thread.CurrentThread)
                _glThread.Join(2000);
            _glThread = null;

            System.Diagnostics.Debug.WriteLine("[GlPreviewRenderer] Disposed");
        }

        #endregion
    }
}
#endif
