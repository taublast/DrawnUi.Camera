using AppoMobi.Specials;

namespace DrawnUi.Camera;

// Mock source: a still image replaces the hardware camera. A timer produces the frames and they travel the
// normal preview path (AquireFrameFromNative -> SetFrameFromNative), so preview shaders, ProcessPreview,
// NewPreviewSet and OnRawFrameAvailable see them like camera frames. TakePicture returns the image.
public partial class SkiaCamera
{
    private const int MockFrameIntervalMs = 33;

    private readonly object _mockLock = new();

    // Immutable raster copy of MockSource. Every frame is an SKImage sharing these pixels: no copy, and one
    // GPU upload only, since such images keep the bitmap's generation id as their unique id.
    private SKBitmap _mockBitmap;

    // Set while the mock source feeds frames instead of the hardware.
    private System.Threading.Timer _mockTimer;

    public static readonly BindableProperty MockSourceProperty = BindableProperty.Create(
        nameof(MockSource),
        typeof(SKImage),
        typeof(SkiaCamera),
        null,
        propertyChanged: OnMockSourceChanged);

    /// <summary>
    /// A still image that replaces the hardware camera, for screenshots, demos, simulators and tests.
    /// While set, the hardware camera is not started and no camera permission is requested, <see cref="State"/>
    /// goes On as usual, and the image is delivered as the live frame about 30 times per second through the
    /// same path as camera frames (preview shaders, ProcessPreview, NewPreviewSet, OnRawFrameAvailable).
    /// <see cref="TakePicture"/> returns the image. Video recording is not available: StartVideoRecording fails
    /// with <see cref="NotSupportedException"/>.
    /// Setting it while the camera is on stops the hardware, clearing it starts the hardware again.
    /// The pixels are copied when set, the caller keeps owning the image. Use a raster or encoded image
    /// (for example SKImage.FromEncodedData), not a GPU texture image.
    /// </summary>
    public SKImage MockSource
    {
        get => (SKImage)GetValue(MockSourceProperty);
        set => SetValue(MockSourceProperty, value);
    }

    private static void OnMockSourceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not SkiaCamera camera)
            return;

        var bitmap = CreateMockBitmap(newValue as SKImage);

        SKBitmap old;
        lock (camera._mockLock)
        {
            old = camera._mockBitmap;
            camera._mockBitmap = bitmap;
        }
        old?.Dispose(); // frames already handed out keep their pixels alive

        if (camera.IsOn && (old == null) != (bitmap == null))
        {
            // switched between mock and hardware: restart the way IsOn does
            Tasks.StartDelayed(TimeSpan.FromMilliseconds(16), () =>
            {
                camera.StopInternal(true);
                camera.StartWithPermissionsInternal();
            });
        }
        else if (bitmap != null && camera._mockTimer != null)
        {
            camera.SetSourceFrameDimensions(bitmap.Width, bitmap.Height);
        }
    }

    private static SKBitmap CreateMockBitmap(SKImage image)
    {
        if (image == null)
            return null;

        var bitmap = SKBitmap.FromImage(image);
        if (bitmap == null)
        {
            Super.Log("[SkiaCamera] MockSource: could not read the image pixels (a GPU texture image?), using the camera");
            return null;
        }

        bitmap.SetImmutable();
        return bitmap;
    }

    private SKImage AquireMockFrame()
    {
        lock (_mockLock)
        {
            return _mockBitmap == null ? null : SKImage.FromBitmap(_mockBitmap);
        }
    }

    private void StartMockFrames()
    {
#if ONPLATFORM
        // created but never started: gallery saving, zoom and flash calls keep working
        if (NativeControl == null)
        {
            CreateNative();
            OnNativeControlCreated();
        }
#endif

        int width, height;
        lock (_mockLock)
        {
            if (_mockBitmap == null)
                return; // cleared meanwhile, the restart it scheduled starts the hardware

            width = _mockBitmap.Width;
            height = _mockBitmap.Height;
        }

        SetSourceFrameDimensions(width, height);

        _mockTimer?.Dispose();
        _mockTimer = new System.Threading.Timer(_ => UpdatePreview(), null, 0, MockFrameIntervalMs);

        State = HardwareState.On;
    }

    private void StopMockFrames()
    {
        Interlocked.Exchange(ref _mockTimer, null)?.Dispose();
    }

    private void DisposeMockSource()
    {
        StopMockFrames();

        lock (_mockLock)
        {
            _mockBitmap?.Dispose();
            _mockBitmap = null;
        }
    }

    partial void CoerceStateCore(ref HardwareState state)
    {
        // late reports of a stopping hardware camera must not switch the mock source off
        if (_mockTimer != null)
            state = HardwareState.On;
    }

    private void TakeMockPicture()
    {
        try
        {
            var image = AquireMockFrame();
            if (image == null)
            {
                OnCaptureFailed(new InvalidOperationException("MockSource was cleared"));
                return;
            }

#if ONPLATFORM
            var meta = CreateMetadata();
#else
            var meta = new Metadata();
#endif
            meta.Orientation = 1;
            meta.PixelWidth = image.Width;
            meta.PixelHeight = image.Height;

            var captured = new CapturedImage
            {
                Image = image, // shares the mock pixels, disposing it is safe
                Facing = Facing,
                Time = DateTime.UtcNow,
                Meta = meta,
                // the image exactly as the preview shows it: no device rotation applied
                Rotation = 0,
                DeviceRotation = 0
            };

            CapturedStillImage = captured;
            OnCaptureSuccess(captured);
        }
        catch (Exception e)
        {
            OnCaptureFailed(e);
        }
    }

    private void ThrowIfMockSource()
    {
        if (_mockBitmap != null)
            throw new NotSupportedException("Video recording is not available while MockSource is set");
    }
}
