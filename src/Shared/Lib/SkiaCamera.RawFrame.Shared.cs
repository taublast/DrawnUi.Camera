using System.ComponentModel;

namespace DrawnUi.Camera;

partial class SkiaCamera
{
    /// <summary>
    /// Called on every camera frame before any compositing, overlay or preview downscaling.
    /// During recording this fires with the raw camera input before ProcessFrame draws overlays
    /// (via platform recording loops). During preview-only this fires with the raw preview frame.
    /// Override to implement ML/AI processing. Call <see cref="RawCameraFrame.TryGetRgba(int,int,byte[],OutputOrientation,float)"/> inside to get
    /// final RGBA pixels in a pre-allocated buffer. <see cref="RawCameraFrame.RawImage"/> is an
    /// optional advanced path for custom processing.
    /// Must not block — copy pixels synchronously into a pre-allocated buffer and hand off to a background thread.
    /// </summary>
    protected internal virtual void OnRawFrameAvailable(RawCameraFrame frame)
    {
        OnRawFrameAvailable(frame.RawImage, frame.RawImageRotation);
    }

    /// <summary>
    /// Legacy raw-frame hook preserved for compatibility.
    /// Prefer overriding <see cref="OnRawFrameAvailable(RawCameraFrame)"/> and call <see cref="RawCameraFrame.TryGetRgba(int,int,byte[],OutputOrientation,float)"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Override OnRawFrameAvailable(RawCameraFrame frame) and use frame.TryGetRgba(...) for AI/ML.")]
    protected internal virtual void OnRawFrameAvailable(SKImage? rawImage, int rotation) { }

    /// <summary>
    /// Scales the raw camera frame to display-oriented RGBA pixels.
    /// Must be called synchronously from within OnRawFrameAvailable — context is not valid elsewhere.
    /// Uses the same GPU path the camera uses internally: MetalPreviewScaler (iOS/Mac),
    /// GlPreviewScaler (Android GPU path), SKSurface+GRContext (Windows), CPU SKSurface (Android legacy,
    /// and the headless .NET flavor).
    /// outputBuffer must be pre-allocated: targetWidth * targetHeight * 4 bytes.
    /// Returns false if scaling failed or no raw frame is available.
    /// </summary>
    private partial bool TryGetRgbaCore(SKImage? rawImage, int targetWidth, int targetHeight, byte[] outputBuffer,
        int outputRotation, float cropRatio);

    internal bool TryGetRgbaInternal(SKImage? rawImage, int targetWidth, int targetHeight, byte[] outputBuffer,
        OutputOrientation orientation = OutputOrientation.Display, float cropRatio = 1f, int displayRotation = 0)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
            return false;

        if (!float.IsFinite(cropRatio) || cropRatio <= 0f)
            return false;

        cropRatio = MathF.Min(cropRatio, 1f);

        int requiredBytes;
        try
        {
            requiredBytes = checked(targetWidth * targetHeight * 4);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (outputBuffer == null || outputBuffer.Length < requiredBytes)
            return false;

        int extraRotation = GetExtraRotationForOrientation(orientation, displayRotation);
        return TryGetRgbaCore(rawImage, targetWidth, targetHeight, outputBuffer, extraRotation, cropRatio);
    }

    internal static SKRect GetCenterCropSourceRect(int sourceWidth, int sourceHeight,
        int targetWidth, int targetHeight, float cropRatio = 1f)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            return new SKRect(0, 0, sourceWidth, sourceHeight);

        cropRatio = float.IsFinite(cropRatio) && cropRatio > 0f
            ? MathF.Min(cropRatio, 1f)
            : 1f;

        float sourceAspect = (float)sourceWidth / sourceHeight;
        float targetAspect = (float)targetWidth / targetHeight;

        SKRect rect;

        if (MathF.Abs(sourceAspect - targetAspect) < 0.0001f)
        {
            rect = new SKRect(0, 0, sourceWidth, sourceHeight);
        }
        else if (sourceAspect > targetAspect)
        {
            float cropWidth = sourceHeight * targetAspect;
            float left = (sourceWidth - cropWidth) * 0.5f;
            rect = new SKRect(left, 0, left + cropWidth, sourceHeight);
        }
        else
        {
            float cropHeight = sourceWidth / targetAspect;
            float top = (sourceHeight - cropHeight) * 0.5f;
            rect = new SKRect(0, top, sourceWidth, top + cropHeight);
        }

        if (cropRatio >= 0.9999f)
            return rect;

        float scaledWidth = rect.Width * cropRatio;
        float scaledHeight = rect.Height * cropRatio;
        float centerX = rect.MidX;
        float centerY = rect.MidY;

        return new SKRect(
            centerX - scaledWidth * 0.5f,
            centerY - scaledHeight * 0.5f,
            centerX + scaledWidth * 0.5f,
            centerY + scaledHeight * 0.5f);
    }

    internal static void GetDrawSizeForOutputRotation(int targetWidth, int targetHeight, int outputRotation,
        out int drawWidth, out int drawHeight)
    {
        outputRotation = NormalizeRotationDegrees(outputRotation);
        if (outputRotation == 90 || outputRotation == 270)
        {
            drawWidth = targetHeight;
            drawHeight = targetWidth;
            return;
        }

        drawWidth = targetWidth;
        drawHeight = targetHeight;
    }

    internal static void ApplyCanvasOutputRotation(SKCanvas canvas, int targetWidth, int targetHeight, int outputRotation)
    {
        switch (NormalizeRotationDegrees(outputRotation))
        {
            case 90:
                canvas.Translate(targetWidth, 0);
                canvas.RotateDegrees(90);
                break;
            case 180:
                canvas.Translate(targetWidth, targetHeight);
                canvas.RotateDegrees(180);
                break;
            case 270:
                canvas.Translate(0, targetHeight);
                canvas.RotateDegrees(270);
                break;
        }
    }

    private static int GetExtraRotationForOrientation(OutputOrientation orientation, int displayRotation)
    {
        return orientation switch
        {
            OutputOrientation.Display => 0,
            OutputOrientation.Portrait => NormalizeRotationDegrees(-NormalizeRotationDegrees(displayRotation)),
            _ => 0,
        };
    }

    private static int NormalizeRotationDegrees(int rotation)
    {
        var normalized = rotation % 360;
        if (normalized < 0)
            normalized += 360;
        return normalized;
    }
}
