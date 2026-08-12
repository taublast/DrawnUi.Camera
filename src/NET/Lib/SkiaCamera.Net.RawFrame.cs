namespace DrawnUi.Camera;

// Raw-frame / ML processing pipeline for the headless .NET build: no native camera, no GPU
// context, so scaling runs on a plain CPU-backed SKSurface. Functionally equivalent to the
// platform TryGetRgbaCore implementations (Windows/Android/Apple), minus their GRContext reuse.
public partial class SkiaCamera : SkiaControl
{
    /// <summary>
    /// Same drive point as the MAUI flavor's Paint override (SkiaCamera.Maui.cs): when the camera
    /// is On, pull the latest frame and fire the ML/AI hook, so a rendered .NET camera processes
    /// frames exactly like the platform ones. No Display sink is ported here (headless), so preview
    /// compositing is skipped — only the frame-processing (OnRawFrameAvailable) path runs.
    /// </summary>
    protected override void Paint(DrawingContext ctx)
    {
        base.Paint(ctx);

        if (State == HardwareState.On)
            SetFrameFromNative();

        DrawViews(ctx);
    }

    /// <summary>
    /// Headless equivalent of the MAUI flavor's SetFrameFromNative: acquires the injected frame and
    /// fires OnRawFrameAvailable(CreateRawCameraFrameInternal(...)). No Display owns the image here,
    /// so it is disposed after the hook (the hook copies pixels via frame.TryGetRgba).
    /// </summary>
    protected virtual void SetFrameFromNative()
    {
        var image = AquireFrameFromNative();
        if (image == null)
            return;

        using (image)
        {
            OnRawFrameAvailable(CreateRawCameraFrameInternal(image, 0));

            if (UseRealtimeVideoProcessing)
            {
                LastPreviewFrame?.Dispose();
                LastPreviewFrame = ApplyPreviewEffects(image);
            }
        }
    }

    /// <summary>
    /// Headless stand-in for the MAUI flavor's Display sink: the last frame composited through the
    /// realtime-processing path (RenderPreviewForProcessing shader + ProcessPreview overlays).
    /// Owned by the camera and disposed on the next processed frame / dispose. Null until produced.
    /// </summary>
    public SKImage LastPreviewFrame { get; private set; }

    /// <summary>
    /// CPU equivalent of the MAUI flavor's ApplyPreviewEffects: composites the source frame through
    /// RenderPreviewForProcessing (where a subclass draws a shader, exactly like the sample's AppCamera)
    /// then ProcessPreview overlays, on a plain raster surface (no GRContext). Returns the composited image.
    /// </summary>
    protected virtual SKImage ApplyPreviewEffects(SKImage source)
    {
        if (source == null)
            return null;

        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null)
            return null;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        RenderPreviewForProcessing(canvas, source);

        if (ProcessPreview != null)
        {
            var checkpoint = canvas.Save();
            ProcessPreview.Invoke(new DrawableFrame
            {
                Width = source.Width,
                Height = source.Height,
                Canvas = canvas,
                Time = TimeSpan.Zero,
                SourceType = FrameSourceType.Preview,
                Scale = 1f
            });
            canvas.RestoreToCount(checkpoint);
        }

        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>
    /// Headless equivalent of the MAUI flavor's device-aware overload: no real device rotation
    /// to track here, so source size comes straight from the injected image and rotation defaults to 0.
    /// </summary>
    public virtual RawCameraFrame CreateRawCameraFrameInternal(SKImage? rawImage, int rawImageRotation,
        int sourceWidth = 0, int sourceHeight = 0, bool rawImageIsMirrored = false, int? displayRotation = null)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            sourceWidth = rawImage?.Width ?? 0;
            sourceHeight = rawImage?.Height ?? 0;
        }

        return new RawCameraFrame(
            this,
            rawImage,
            rawImageRotation,
            displayRotation ?? 0,
            rawImageIsMirrored,
            sourceWidth,
            sourceHeight);
    }

    private partial bool TryGetRgbaCore(SKImage? rawImage, int targetWidth, int targetHeight, byte[] outputBuffer,
        int outputRotation, float cropRatio)
    {
        if (rawImage == null)
            return false;

        var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(info);
        if (surface == null)
            return false;

        using var paint = new SKPaint();
        var sampling = SkiaSamplingOptions.GetSamplingOptions(FilterQuality.Low);
        GetDrawSizeForOutputRotation(targetWidth, targetHeight, outputRotation, out int drawWidth, out int drawHeight);
        var src = GetCenterCropSourceRect(rawImage.Width, rawImage.Height, drawWidth, drawHeight, cropRatio);

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Save();
        ApplyCanvasOutputRotation(surface.Canvas, targetWidth, targetHeight, outputRotation);
        surface.Canvas.DrawImage(rawImage, src, new SKRect(0, 0, drawWidth, drawHeight), sampling, paint);
        surface.Canvas.Restore();
        surface.Canvas.Flush();

        using var snapshot = surface.Snapshot();
        if (snapshot == null)
            return false;

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(outputBuffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return snapshot.ReadPixels(info, handle.AddrOfPinnedObject(), targetWidth * 4, 0, 0);
        }
        finally
        {
            handle.Free();
        }
    }
}
