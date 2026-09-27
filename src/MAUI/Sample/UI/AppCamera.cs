using CameraTests.UI;
using DrawnUi.Camera;
using DrawnUi.Infrastructure;

namespace CameraTests.Views
{
    public partial class AppCamera : SkiaCamera
    {
        public static readonly BindableProperty VideoEffectProperty = BindableProperty.Create(
            nameof(VideoEffect),
            typeof(ShaderEffect),
            typeof(AppCamera),
            ShaderEffect.None);

        public ShaderEffect VideoEffect
        {
            get => (ShaderEffect)GetValue(VideoEffectProperty);
            set => SetValue(VideoEffectProperty, value);
        }

        public AppCamera()
        {
            //set defaults for this camera
            NeedPermissionsSet = NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone;

            //GPS metadata
            this.InjectGpsLocation = true;

            //audio 
            this.EnableAudioMonitoring = false;

            this.EnableAudioRecording = true;
            UseRealtimeVideoProcessing = true;

            VideoQuality = VideoQuality.Standard;

            ProcessFrame = OnFrameProcessing;
            ProcessPreview = OnFrameProcessing;

            VideoDiagnosticsOn = MauiProgram.ShowDebug;

            //ConstantUpdate = true; //this updates camera even if native didn't send any frames
        }

        public static readonly BindableProperty UseGainProperty = BindableProperty.Create(
            nameof(UseGain),
            typeof(bool),
            typeof(AppCamera),
            false);

        public bool UseGain
        {
            get => (bool)GetValue(UseGainProperty);
            set => SetValue(UseGainProperty, value);
        }


        public static readonly BindableProperty VisualizerNameProperty = BindableProperty.Create(
            nameof(VisualizerName),
            typeof(string),
            typeof(AppCamera),
            "None");

        public string VisualizerName
        {
            get => (string)GetValue(VisualizerNameProperty);
            set => SetValue(VisualizerNameProperty, value);
        }


        /// <summary>
        /// Gain multiplier applied to raw PCM when UseGain is true.
        /// </summary>
        public float GainFactor { get; set; } = 3.0f;

        public void SwitchVisualizer(int index = -1)
        {
            if (VideoDataOverlay is IAppOverlay appOverlay)
            {
                VisualizerName = appOverlay.SwitchVisualizer(index);
            }
        }

        public event Action<AudioSample> OnAudioSample;

        protected override AudioSample OnAudioSampleAvailable(AudioSample sample)
        {
            if (UseGain && sample.Data != null && sample.Data.Length > 1)
            {
                AmplifyPcm16(sample.Data, GainFactor);
            }

            OnAudioSample?.Invoke(sample);

            if (VideoDataOverlay is IAppOverlay appOverlay)
            {
                appOverlay.AddAudioSample(sample);
            }

            return base.OnAudioSampleAvailable(sample);
        }

        /// <summary>
        /// Amplifies PCM16 audio data in-place. Zero allocations.
        /// </summary>
        private static void AmplifyPcm16(byte[] data, float gain)
        {
            for (int i = 0; i < data.Length - 1; i += 2)
            {
                int sample = (short)(data[i] | (data[i + 1] << 8));
                sample = (int)(sample * gain);

                // Clamp to 16-bit range
                if (sample > 32767) sample = 32767;
                else if (sample < -32768) sample = -32768;

                data[i] = (byte)(sample & 0xFF);
                data[i + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        /// <summary>
        /// An .sksl file used instead of <see cref="VideoEffect"/> when set (test runs pick shaders by file).
        /// </summary>
        public string CustomShaderPath { get; set; }

        /// <summary>
        /// Test runs: the ML input of an image through the camera's own Skia scaling (a raster image never takes a GPU
        /// shortcut), as the reference for the pixel error of the GPU path.
        /// </summary>
        public bool ReferenceRgba(SKImage raster, int width, int height, byte[] buffer) => TryGetRgba(raster, width, height, buffer);

        /// <summary>
        /// Called with every raw frame before the base handling (test runs check the ML input with it).
        /// </summary>
        public Action<RawCameraFrame> RawFrameProbe { get; set; }

        protected override void OnRawFrameAvailable(RawCameraFrame frame)
        {
            RawFrameProbe?.Invoke(frame);
            base.OnRawFrameAvailable(frame);
        }

        /// <summary>
        /// When true, the UI-thread time of every Paint of this control is collected (test runs).
        /// </summary>
        public bool MeasurePaint { get; set; }

        private double _paintMsSum, _paintMsMax;
        private long _paintCount;

        protected override void Paint(DrawingContext ctx)
        {
            if (!MeasurePaint)
            {
                base.Paint(ctx);
                return;
            }
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            base.Paint(ctx);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (ms > 30)
                Super.Log($"[AppCamera] paint took {ms:0} ms (from {DateTime.Now.AddMilliseconds(-ms):HH:mm:ss.fff})");
            lock (this)
            {
                _paintMsSum += ms;
                _paintMsMax = Math.Max(_paintMsMax, ms);
                _paintCount++;
            }
        }

        /// <summary>
        /// Average and maximum UI-thread milliseconds per Paint since the last call, and the paint count.
        /// </summary>
        public (double Average, double Max, long Paints) TakePaintTiming()
        {
            lock (this)
            {
                var result = (_paintCount > 0 ? _paintMsSum / _paintCount : 0, _paintMsMax, _paintCount);
                _paintMsSum = _paintMsMax = 0;
                _paintCount = 0;
                return result;
            }
        }

        protected override void RenderPreviewForProcessing(SKCanvas canvas, SKImage frame)
        {
            var shader = _previewEffect.Get(VideoEffect, CustomShaderPath);
            if (shader == null)
            {
                base.RenderPreviewForProcessing(canvas, frame);
                return;
            }

            shader.DrawImage(canvas, frame, 0, 0);
        }

        protected override void RenderFrameForRecording(SKCanvas canvas, SKImage frame, SKRect src, SKRect dst)
        {
            var shader = _recordingEffect.Get(VideoEffect, CustomShaderPath);
            if (shader == null)
            {
                base.RenderFrameForRecording(canvas, frame, src, dst);
                return;
            }

            shader.DrawRect(canvas, frame, dst);
        }

        // One shader instance per role: preview and recording render on different threads (and GPU contexts), and a
        // SkiaShader keeps per-draw state (paint, uniforms, texture shader) that must not be shared between them.
        private readonly EffectSlot _previewEffect = new(), _recordingEffect = new();

        private sealed class EffectSlot
        {
            private SkiaShader _shader;
            private ShaderEffect _effect;
            private string _path;
            private bool _loaded;

            public SkiaShader Get(ShaderEffect effect, string path)
            {
                if (string.IsNullOrEmpty(path) && effect == ShaderEffect.None)
                {
                    Release();
                    return null;
                }

                if (_loaded && _effect == effect && _path == path)
                {
                    return _shader; // null when the file could not be loaded: not retried every frame
                }

                Release();
                _effect = effect;
                _path = path;
                _loaded = true;
                try
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        _shader = SkiaShader.FromCode(File.ReadAllText(path));
                    }
                    else
                    {
                        var filename = ShaderEffectHelper.GetFilename(effect);
                        if (!string.IsNullOrWhiteSpace(filename))
                            _shader = SkiaShader.FromResource(filename);
                    }
                }
                catch (Exception e)
                {
                    // a missing or broken shader file shows the plain frame instead of taking the camera down
                    Super.Log($"[AppCamera] shader {path ?? effect.ToString()} not loaded: {e.Message}");
                    _shader = null;
                }

                return _shader;
            }

            public void Release()
            {
                _shader?.Dispose();
                _shader = null;
                _effect = ShaderEffect.None;
                _path = null;
                _loaded = false;
            }
        }

        private void ReleaseEffectShader()
        {
            _previewEffect.Release();
            _recordingEffect.Release();
        }

        public override void OnWillDisposeWithChildren()
        {
            // the camera stops its recording first: its recording thread may still be drawing with these shaders
            base.OnWillDisposeWithChildren();

            ReleaseEffectShader();

            _paintRec?.Dispose();
            _paintRec = null;
            _paintPreview?.Dispose();
            _paintPreview = null;
        }

        bool DrawOverlay(DrawableFrame frame, FrameOverlay layout)
        {
            if (CurrentVideoFormat != _adaptedToFormat)
            {
                _adaptedToFormat = CurrentVideoFormat;
                AdjustOverlayScale();
            }

            if (_overlayScale != _overlayScaleChanged)
            {
                _overlayScale = _overlayScaleChanged;
                _rectFramePreview = SKRect.Empty;
            }

            //video frame preview area changed inside camera, its different from camera rect because of letterboxing
            if (this.DisplayRect != _previewRect)
            {
                _previewRect = this.DisplayRect;
                _rectFramePreview = SKRect.Empty;
            }

            if (frame.IsPreview && frame.Scale != _previewScale)
            {
                _rectFramePreview = SKRect.Empty;
                _previewScale = frame.Scale;
            }

            var k = _overlayScale;
            var overlayScale = 1.5f * frame.Scale * k; //magic number

            if (_rectOrientation != _orientation && !IsRecording &&
                !IsPreRecording) //lock overlay orientation when recording
            {
                _rectFramePreview = SKRect.Empty;
                _rectOrientation = _orientation;
            }

            var orientation = _rectOrientation;

            var frameRect = new SKRect(0, 0, frame.Width, frame.Height);

            if (frame.IsPreview && _rectFramePreview == SKRect.Empty)
            {
                _rectFramePreview = frameRect;
                if (!layout.NeedMeasure)
                {
                    layout.Invalidate();
                }
            }

            if (orientation == DeviceOrientation.LandscapeLeft ||
                orientation == DeviceOrientation.LandscapeRight)
            {
                overlayScale *= 0.8f; //smaller in landscape
            }

            bool wasMeasured = false;

            layout.AdaptLayoutToMode(frame.IsPreview);

            if (layout.NeedMeasure)
            {
                layout.SetOrientation(orientation, frameRect, overlayScale);
                var measured = layout.Measure(frameRect.Width, frameRect.Height, overlayScale);

                layout.Arrange(
                    new SKRect(0, 0, layout.MeasuredSize.Pixels.Width, layout.MeasuredSize.Pixels.Height),
                    layout.MeasuredSize.Pixels.Width, layout.MeasuredSize.Pixels.Height, overlayScale);

                wasMeasured = true;
            }

            var ctx = new SkiaDrawingContext()
            {
                Canvas = frame.Canvas,
                Width = frame.Width,
                Height = frame.Height,
                Superview = Superview //to enable animations and use of disposal manager
            };

            layout.Render(new DrawingContext(ctx, frameRect, overlayScale));
            _renderedScale = overlayScale;

            return wasMeasured;
        }


        #region DRAWN LAYOUT

        private SKPaint _paintPreview;
        private SKPaint _paintRec;
        private SKPaint _paint;
        private SKFont _fontPreview;
        private SKFont _font;
        private FrameOverlay VideoDataOverlay;
        private SKColor _paintColor;
        private SKColor _paintPreviewColor;
        private float _paintStrokeWidth = -1f;
        private float _paintPreviewStrokeWidth = -1f;
        private float _paintTextSize = -1f;
        private float _paintPreviewTextSize = -1f;
        private SKPaintStyle _paintStyle; // default(SKPaintStyle) == Fill == 0
        private SKPaintStyle _paintPreviewStyle;

        private DeviceOrientation _orientation;

        /// <summary>
        /// Current device orientation used by the frame overlay renderer.
        /// Set this from the page whenever the device orientation changes.
        /// </summary>
        public DeviceOrientation Orientation
        {
            get => _orientation;
            set
            {
                if (_orientation != value)
                {
                    _orientation = value;
                }
            }
        }

        private float _previewScale;
        private float _renderedScale;
        private SKRect _rectFramePreview;
        private float _overlayScale = 1;
        private float _overlayScaleChanged = -1;
        private VideoFormat _adaptedToFormat;
        private DeviceOrientation _rectOrientation = DeviceOrientation.Unknown;
        private DeviceOrientation _rectOrientationLocked = DeviceOrientation.Unknown;
        private SKRect _previewRect;
        public bool LockOrientation { get; set; }

        float GetOverlayBaseDivider(float smallSize)
        {
            var setting = smallSize;
            return setting switch
            {
                0 => 1920f, // Default 1080p max dimension
                720 => 1280f, // 1280x720 max
                1080 => 1920f, // 1920x1080 max
                1440 => 2560f, // 2560x1440 max
                2160 => 3840f, // 3840x2160 max
                4320 => 7680f, // 7680x4320 max
                _ => 1920f
            };
        }

        void AdjustOverlayScale()
        {
            var format = this.CurrentVideoFormat;
            if (format == null)
            {
                _overlayScaleChanged = -1;
                return;
            }

            var (formatWidth, formatHeight) = this.GetRotationCorrectedDimensions(format.Width, format.Height);
            var baseDivider = GetOverlayBaseDivider(Math.Min(format.Width, format.Height));
            _overlayScaleChanged = Math.Max(formatWidth, formatHeight) / baseDivider;
        }

        /// <summary>
        /// ProcessFrame + ProcessPreview events
        /// </summary>
        /// <param name="frame"></param>
        void OnFrameProcessing(DrawableFrame frame)
        {

            // Simple text overlay for testing
            if (_paint == null)
            {
                _paint = new SKPaint
                {
                    IsAntialias = true,
                };
            }

            if (_paintPreview == null)
            {
                _paintPreview = new SKPaint
                {
                    IsAntialias = true,
                    Color = SKColors.Fuchsia
                };
            }

            _font ??= new SKFont();
            _fontPreview ??= new SKFont();

            var paint = frame.IsPreview ? _paintPreview : _paint;
            var font = frame.IsPreview ? _fontPreview : _font;

            // Ref aliases into the correct shadow fields — one branch, zero per-property duplication
            ref var cachedColor = ref (frame.IsPreview ? ref _paintPreviewColor : ref _paintColor);
            ref var cachedTextSize = ref (frame.IsPreview ? ref _paintPreviewTextSize : ref _paintTextSize);
            ref var cachedStyle = ref (frame.IsPreview ? ref _paintPreviewStyle : ref _paintStyle);
            ref var cachedStrokeWidth = ref (frame.IsPreview ? ref _paintPreviewStrokeWidth : ref _paintStrokeWidth);

            var newTextSize = 32 * frame.Scale;
            if (cachedTextSize != newTextSize)
            {
                cachedTextSize = newTextSize;
                font.Size = newTextSize;
            }

            if (cachedStyle != SKPaintStyle.Fill)
            {
                cachedStyle = SKPaintStyle.Fill;
                paint.Style = SKPaintStyle.Fill;
            }

            // text at top left
            var text = string.Empty;
            var text2 = string.Empty;
            SKColor newColor;

            if (IsPreRecording)
            {
                //_labelRec.Text = "PRE";
                text2 = $"{frame.Time:mm\\:ss}";
                newColor = SKColors.White;
            }
            else if (IsRecording)
            {
                //_labelRec.Text = "REC";
                text2 = $"{frame.Time:mm\\:ss}";
                newColor = SKColors.Red;
            }
            else
            {
                //_labelRec.Text = string.Empty;
                newColor = SKColors.Transparent;
                text =
                    $"{CurrentVideoFormat.Width}x{CurrentVideoFormat.Height} ({frame.Width}x{frame.Height}) x{_renderedScale:0.00}";
            }

            if (cachedColor != newColor)
            {
                cachedColor = newColor;
                paint.Color = newColor;
            }


            //PREVIEW - NOT RECORDING BY DESIGN
            if (frame.IsPreview)
            {
                if (VideoDataOverlay != null)
                {
                    DrawOverlay(frame, VideoDataOverlay);
                }
            }
            else
                //RECORDING
            {
                if (VideoDataOverlay != null) // && !Model.IsRecording)
                {
                    DrawOverlay(frame, VideoDataOverlay);
                }
            }

            if (VideoDiagnosticsOn && cachedColor != SKColors.Transparent)
            {
                var newStrokeWidth = 2 * frame.Scale;
                if (cachedStyle != SKPaintStyle.Stroke)
                {
                    cachedStyle = SKPaintStyle.Stroke;
                    paint.Style = SKPaintStyle.Stroke;
                }

                if (cachedStrokeWidth != newStrokeWidth)
                {
                    cachedStrokeWidth = newStrokeWidth;
                    paint.StrokeWidth = newStrokeWidth;
                }

                frame.Canvas.DrawRect(10 * frame.Scale, 10 * frame.Scale, frame.Width - 20 * frame.Scale,
                    frame.Height - 20 * frame.Scale, paint);

                frame.Canvas.DrawText($"{frame.Time:mm\\:ss}", 50 * frame.Scale, 160 * frame.Scale, font, paint);
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            // the camera stops its recording first: its recording thread may still be drawing with these
            base.Dispose(isDisposing);

            if (isDisposing)
            {
                _paint?.Dispose();
                _paintPreview?.Dispose();
                _font?.Dispose();
                _fontPreview?.Dispose();
            }
        }

        public FrameOverlay CreateOverlay()
        {
            var previewLayout = new FrameOverlay();

            this.VideoDataOverlay = previewLayout;

            previewLayout.UseCache = SkiaCacheType.Operations;
            previewLayout.Tag = "Preview";

            InvalidateOverlays();

            if (previewLayout is IAppOverlay appOverlayInit)
            {
                VisualizerName = appOverlayInit.Visualizer?.VisualizerName ?? "None";
            }

            return previewLayout;
        }

        /// <summary>
        /// Call this when overlays need remeasuring, like camera format change, orientation change etc..
        /// </summary>
        public void InvalidateOverlays()
        {
            _overlayScaleChanged = -1;
            _rectFramePreview = SKRect.Empty;
        }

        #endregion
    }
}