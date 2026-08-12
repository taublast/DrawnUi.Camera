using System.Diagnostics.CodeAnalysis;

namespace DrawnUi.Camera;

/// <summary>
/// Temporary handle for a raw camera frame delivered by <see cref="SkiaCamera.OnRawFrameAvailable(RawCameraFrame)"/>.
/// Copy pixels out fast and leave camera thread fast.
/// Use <see cref="TryGetRgba"/> for hot-loop AI/ML input.
/// Use <see cref="TryGetJpeg"/> or <see cref="TryGetPng"/> for standard image payloads.
/// <see cref="RawImage"/> is an optional advanced path.
/// The frame is valid only for the duration of the callback.
/// </summary>
public readonly struct RawCameraFrame
{
    private readonly SkiaCamera? _owner;

    internal RawCameraFrame(SkiaCamera owner, SKImage? rawImage, int rawImageRotation, int displayRotation,
        bool rawImageIsMirrored, int sourceWidth, int sourceHeight)
    {
        _owner = owner;
        RawImage = rawImage;
        RawImageRotation = rawImageRotation;
        DisplayRotation = displayRotation;
        RawImageIsMirrored = rawImageIsMirrored;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    /// <summary>
    /// Optional raw image handle for advanced consumers. May be null on zero-copy GPU paths.
    /// Do not cache or use outside the callback.
    /// </summary>
    public SKImage? RawImage { get; }

    /// <summary>
    /// Rotate <see cref="RawImage"/> by this to reach display orientation.
    /// Uses normalized degrees: 0/90/180/270.
    /// </summary>
    public int RawImageRotation { get; }

    /// <summary>
    /// Current display rotation in degrees.
    /// Uses normalized degrees: 0/90/180/270.
    /// </summary>
    public int DisplayRotation { get; }

    /// <summary>
    /// True when <see cref="RawImage"/> still needs mirroring to match display orientation.
    /// </summary>
    public bool RawImageIsMirrored { get; }

    /// <summary>
    /// Alias for <see cref="RawImageRotation"/>.
    /// </summary>
    public int Rotation => RawImageRotation;

    /// <summary>
    /// Source frame width before resizing.
    /// </summary>
    public int SourceWidth { get; }

    /// <summary>
    /// Source frame height before resizing.
    /// </summary>
    public int SourceHeight { get; }

    /// <summary>
    /// True when <see cref="RawImage"/> is available on this path.
    /// </summary>
    public bool HasRawImage => RawImage != null;

    /// <summary>
    /// Fill <paramref name="outputBuffer"/> with RGBA8888 pixels at final size.
    /// Preserves aspect ratio and center-crops when needed.
    /// cropRatio=1 uses the full crop window. Smaller values zoom into the center.
    /// Must be called synchronously inside the raw-frame callback.
    /// </summary>
    public bool TryGetRgba(int targetWidth, int targetHeight, byte[] outputBuffer,
        OutputOrientation orientation = OutputOrientation.Display, float cropRatio = 1f)
    {
        return _owner != null &&
               _owner.TryGetRgbaInternal(RawImage, targetWidth, targetHeight, outputBuffer, orientation, cropRatio, DisplayRotation);
    }

    /// <summary>
    /// Allocate and fill a tightly sized RGBA8888 buffer at final size.
    /// Avoid in hot loops when a reusable buffer can be used instead.
    /// </summary>
    public bool TryGetRgbaBytes(int targetWidth, int targetHeight, [NotNullWhen(true)] out byte[]? rgbaBytes,
        OutputOrientation orientation = OutputOrientation.Display, float cropRatio = 1f)
    {
        rgbaBytes = null;

        if (!TryCreateRgbaBytes(targetWidth, targetHeight, orientation, cropRatio, out var buffer))
            return false;

        rgbaBytes = buffer;
        return true;
    }

    /// <summary>
    /// Encode this frame as JPEG at the final size.
    /// </summary>
    public bool TryGetJpeg(int targetWidth, int targetHeight, [NotNullWhen(true)] out byte[]? jpegBytes,
        int quality = 100, OutputOrientation orientation = OutputOrientation.Display, float cropRatio = 1f)
    {
        return TryGetEncodedBytes(targetWidth, targetHeight, SKEncodedImageFormat.Jpeg, quality, orientation, cropRatio, out jpegBytes);
    }

    /// <summary>
    /// Encode this frame as PNG at the final size.
    /// </summary>
    public bool TryGetPng(int targetWidth, int targetHeight, [NotNullWhen(true)] out byte[]? pngBytes,
        OutputOrientation orientation = OutputOrientation.Display, float cropRatio = 1f)
    {
        return TryGetEncodedBytes(targetWidth, targetHeight, SKEncodedImageFormat.Png, 100, orientation, cropRatio, out pngBytes);
    }


    /// <summary>
    /// Fill <paramref name="outputBuffer"/> with RGBA8888 pixels at the final size
    /// and return them as a Base64 string.
    /// </summary>
    public bool TryGetBase64StringRgba(int targetWidth, int targetHeight, byte[] outputBuffer,
        [NotNullWhen(true)] out string? base64,
        OutputOrientation orientation = OutputOrientation.Display, float cropRatio = 1f)
    {
        base64 = null;

        if (targetWidth <= 0 || targetHeight <= 0)
            return false;

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

        if (_owner == null || !_owner.TryGetRgbaInternal(RawImage, targetWidth, targetHeight, outputBuffer, orientation, cropRatio, DisplayRotation))
            return false;

        base64 = Convert.ToBase64String(outputBuffer, 0, requiredBytes);
        return true;
    }

    private bool TryCreateRgbaBytes(int targetWidth, int targetHeight, OutputOrientation orientation, float cropRatio,
        [NotNullWhen(true)] out byte[]? rgbaBytes)
    {
        rgbaBytes = null;

        if (targetWidth <= 0 || targetHeight <= 0)
            return false;

        int requiredBytes;
        try
        {
            requiredBytes = checked(targetWidth * targetHeight * 4);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (_owner == null)
            return false;

        var buffer = new byte[requiredBytes];
        if (!_owner.TryGetRgbaInternal(RawImage, targetWidth, targetHeight, buffer, orientation, cropRatio, DisplayRotation))
            return false;

        rgbaBytes = buffer;
        return true;
    }

    private bool TryGetEncodedBytes(int targetWidth, int targetHeight,
        SKEncodedImageFormat format, int quality,
        OutputOrientation orientation,
        float cropRatio,
        [NotNullWhen(true)] out byte[]? encodedBytes)
    {
        encodedBytes = null;

        if (!TryCreateRgbaBytes(targetWidth, targetHeight, orientation, cropRatio, out var rgbaBytes))
            return false;

        var imageInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(imageInfo, rgbaBytes, imageInfo.RowBytes);
        if (image == null)
            return false;

        using var encoded = image.Encode(format, quality);
        if (encoded == null)
            return false;

        encodedBytes = encoded.ToArray();
        return true;
    }
}