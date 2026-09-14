#if IOS || MACCATALYST
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using CoreGraphics;
using Foundation;
using ImageIO;

namespace DrawnUi.Camera;

/// <summary>
/// Writes capture metadata into an encoded JPEG with ImageIO (CGImageDestination), letting the OS
/// serialize EXIF/TIFF/GPS instead of building the segment by hand.
///
/// WHY: iOS re-serializes a photo's EXIF when Photos imports it. Given the hand-built segment from
/// <see cref="JpegExifInjector"/> it kept only the values small enough to live inside a tag entry
/// (software, ISO, orientation) and blanked everything stored out of line - make, model, lens make,
/// lens model, exposure time, aperture, focal length. Metadata written here survives that import.
/// The image data is copied from the source, so nothing is re-encoded and quality does not change.
/// </summary>
public static class AppleJpegMetadata
{
    /// <summary>
    /// Returns a new JPEG stream carrying <paramref name="meta"/>, or null when ImageIO could not
    /// do it - the caller then falls back to the manual injector.
    /// </summary>
    public static Stream Write(Stream jpegStream, Metadata meta)
    {
        if (jpegStream == null || meta == null)
            return null;

        try
        {
            if (jpegStream.CanSeek)
                jpegStream.Position = 0;

            using var data = NSData.FromStream(jpegStream);
            if (data == null)
                return null;

            using var result = Write(data, meta);
            if (result == null)
                return null;

            return new MemoryStream(result.ToArray());
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[AppleJpegMetadata] Failed: {e}");
            return null;
        }
    }

    /// <summary>
    /// Returns JPEG data carrying <paramref name="meta"/>, or null when ImageIO could not do it.
    /// </summary>
    public static NSData Write(NSData jpeg, Metadata meta)
    {
        if (jpeg == null || jpeg.Length == 0 || meta == null)
            return null;

        using var source = CGImageSource.FromData(jpeg);
        if (source == null || source.ImageCount == 0)
        {
            Debug.WriteLine("[AppleJpegMetadata] Could not read the encoded JPEG");
            return null;
        }

        var properties = CreateProperties(meta);
        var output = new NSMutableData();

        using (var destination = CGImageDestination.Create(output, "public.jpeg", 1))
        {
            if (destination == null)
            {
                output.Dispose();
                Debug.WriteLine("[AppleJpegMetadata] Could not create the JPEG destination");
                return null;
            }

            // Copies the compressed image from the source (no re-encode) and merges our properties
            // over the ones it already carries.
            destination.AddImage(source, 0, properties);

            if (!destination.Close())
            {
                output.Dispose();
                Debug.WriteLine("[AppleJpegMetadata] Could not finalize the JPEG destination");
                return null;
            }
        }

        return output;
    }

    /// <summary>
    /// Maps <see cref="Metadata"/> onto the ImageIO property dictionaries. Keys are the documented
    /// kCGImageProperty* string values, the same ones <see cref="Metadata.CreateMetadataFromProperties"/>
    /// reads back.
    /// </summary>
    private static NSMutableDictionary CreateProperties(Metadata meta)
    {
        var tiff = new NSMutableDictionary();
        SetString(tiff, "Make", meta.Vendor);
        SetString(tiff, "Model", meta.Model);
        SetString(tiff, "Software", meta.Software);
        SetInt(tiff, "Orientation", meta.Orientation);
        SetDouble(tiff, "XResolution", meta.XResolution);
        SetDouble(tiff, "YResolution", meta.YResolution);
        SetIntString(tiff, "ResolutionUnit", meta.ResolutionUnit);
        if (meta.DateTimeOriginal.HasValue)
            SetString(tiff, "DateTime", FormatDate(meta.DateTimeOriginal.Value));

        var exif = new NSMutableDictionary();
        SetDouble(exif, "ExposureTime", meta.Shutter);
        SetDouble(exif, "FNumber", meta.Aperture);
        SetDouble(exif, "FocalLength", meta.FocalLength);
        SetDouble(exif, "FocalLenIn35mmFilm", meta.FocalLengthIn35mm);
        SetDouble(exif, "ExposureBiasValue", meta.ExposureBias);
        SetDouble(exif, "BrightnessValue", meta.BrightnessValue);
        SetDouble(exif, "DigitalZoomRatio", meta.DigitalZoomRatio);
        SetDouble(exif, "MaxApertureValue", meta.MaxApertureValue);
        SetDouble(exif, "SubjectDistance", meta.SubjectDistance);
        SetDouble(exif, "CompressedBitsPerPixel", meta.CompressedBitsPerPixel);
        SetInt(exif, "PixelXDimension", meta.PixelWidth);
        SetInt(exif, "PixelYDimension", meta.PixelHeight);

        if (meta.ISO.HasValue)
            exif["ISOSpeedRatings"] = NSArray.FromObjects(new NSNumber(meta.ISO.Value));

        if (meta.DateTimeOriginal.HasValue)
            SetString(exif, "DateTimeOriginal", FormatDate(meta.DateTimeOriginal.Value));
        if (meta.DateTimeDigitized.HasValue)
            SetString(exif, "DateTimeDigitized", FormatDate(meta.DateTimeDigitized.Value));

        SetString(exif, "SubsecTime", meta.SubsecTime);
        SetString(exif, "SubsecTimeOriginal", meta.SubsecTimeOriginal);
        SetString(exif, "SubsecTimeDigitized", meta.SubsecTimeDigitized);

        SetString(exif, "LensMake", meta.LensMake);
        SetString(exif, "LensModel", meta.LensModel);
        SetNumberList(exif, "LensSpecification", meta.LensSpecification);
        SetString(exif, "BodySerialNumber", meta.BodySerialNumber);
        SetString(exif, "CameraOwnerName", meta.CameraOwnerName);
        SetString(exif, "ImageUniqueID", meta.ImageUniqueId);
        SetString(exif, "SpectralSensitivity", meta.SpectralSensitivity);
        SetString(exif, "UserComment", meta.UserComment);
        SetString(exif, "RelatedSoundFile", meta.RelatedSoundFile);
        SetNumberList(exif, "SubjectArea", meta.SubjectArea);

        SetIntString(exif, "Flash", meta.Flash);
        SetIntString(exif, "WhiteBalance", meta.WhiteBalance);
        SetIntString(exif, "ExposureMode", meta.ExposureMode);
        SetIntString(exif, "MeteringMode", meta.MeteringMode);
        SetIntString(exif, "SceneCaptureType", meta.SceneCaptureType);
        SetIntString(exif, "ExposureProgram", meta.ExposureProgram);
        SetIntString(exif, "SceneType", meta.SceneType);
        SetIntString(exif, "CustomRendered", meta.CustomRendered);
        SetIntString(exif, "GainControl", meta.GainControl);
        SetIntString(exif, "Contrast", meta.Contrast);
        SetIntString(exif, "Saturation", meta.Saturation);
        SetIntString(exif, "Sharpness", meta.Sharpness);
        SetIntString(exif, "SensingMethod", meta.SensingMethod);
        SetIntString(exif, "SubjectDistRange", meta.SubjectDistanceRange);
        SetIntString(exif, "ColorSpace", meta.ColorSpace);
        SetIntString(exif, "LightSource", meta.LightSource);

        var gps = new NSMutableDictionary();
        if (meta.GpsLatitude.HasValue)
        {
            gps["Latitude"] = new NSNumber(Math.Abs(meta.GpsLatitude.Value));
            SetString(gps, "LatitudeRef",
                !string.IsNullOrEmpty(meta.GpsLatitudeRef) ? meta.GpsLatitudeRef : meta.GpsLatitude.Value >= 0 ? "N" : "S");
        }

        if (meta.GpsLongitude.HasValue)
        {
            gps["Longitude"] = new NSNumber(Math.Abs(meta.GpsLongitude.Value));
            SetString(gps, "LongitudeRef",
                !string.IsNullOrEmpty(meta.GpsLongitudeRef) ? meta.GpsLongitudeRef : meta.GpsLongitude.Value >= 0 ? "E" : "W");
        }

        if (meta.GpsAltitude.HasValue)
        {
            gps["Altitude"] = new NSNumber(Math.Abs(meta.GpsAltitude.Value));
            gps["AltitudeRef"] = new NSNumber(meta.GpsAltitude.Value < 0 ? 1 : 0);
        }

        if (meta.GpsTimestamp.HasValue)
        {
            var stamp = meta.GpsTimestamp.Value.ToUniversalTime();
            SetString(gps, "TimeStamp", stamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            SetString(gps, "DateStamp", stamp.ToString("yyyy:MM:dd", CultureInfo.InvariantCulture));
        }

        var properties = new NSMutableDictionary();
        if (tiff.Count > 0)
            properties[ImageIO.CGImageProperties.TIFFDictionary] = tiff;
        if (exif.Count > 0)
            properties[ImageIO.CGImageProperties.ExifDictionary] = exif;
        if (gps.Count > 0)
            properties[ImageIO.CGImageProperties.GPSDictionary] = gps;

        // Top level orientation is what viewers read; the TIFF one alone is not enough.
        if (meta.Orientation.HasValue)
            properties[ImageIO.CGImageProperties.Orientation] = new NSNumber(meta.Orientation.Value);

        return properties;
    }

    private static string FormatDate(DateTime value)
        => value.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void SetString(NSMutableDictionary dictionary, string key, string value)
    {
        if (!string.IsNullOrEmpty(value))
            dictionary[key] = new NSString(value);
    }

    private static void SetInt(NSMutableDictionary dictionary, string key, int? value)
    {
        if (value.HasValue)
            dictionary[key] = new NSNumber(value.Value);
    }

    private static void SetDouble(NSMutableDictionary dictionary, string key, double? value)
    {
        if (value.HasValue)
            dictionary[key] = new NSNumber(value.Value);
    }

    /// <summary>Several EXIF fields are numeric codes that <see cref="Metadata"/> keeps as strings.</summary>
    private static void SetIntString(NSMutableDictionary dictionary, string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            dictionary[key] = new NSNumber(parsed);
        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
            dictionary[key] = new NSNumber(parsedDouble);
    }

    /// <summary>Lens specification and subject area are comma separated lists in <see cref="Metadata"/>.</summary>
    private static void SetNumberList(NSMutableDictionary dictionary, string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        var numbers = value.Split(',')
            .Select(x => double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? new NSNumber(parsed)
                : null)
            .Where(x => x != null)
            .ToArray();

        if (numbers.Length > 0)
            dictionary[key] = NSArray.FromObjects(numbers);
    }
}
#endif
