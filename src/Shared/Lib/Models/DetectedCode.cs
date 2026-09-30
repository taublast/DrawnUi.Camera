namespace DrawnUi.Camera
{
    /// <summary>
    /// A machine-readable code found in the live preview, delivered by <see cref="SkiaCamera.CodesDetected"/>.
    /// </summary>
    public sealed class DetectedCode
    {
        public DetectedCode(string value, CameraCodeTypes type, SKPoint[] corners)
        {
            Value = value;
            Type = type;
            Corners = corners;
        }

        /// <summary>
        /// Decoded text payload. Untrusted input: validate before acting on it (opening a link etc).
        /// </summary>
        public string Value { get; }

        /// <summary>
        /// Format of this code, a single flag.
        /// </summary>
        public CameraCodeTypes Type { get; }

        /// <summary>
        /// Corners normalized 0..1 inside the displayed preview image: already rotated and mirrored the
        /// way the preview frame is, (0,0) is its top-left. Winding order is not guaranteed.
        /// Map to canvas pixels with SkiaCamera.TryMapPreviewPoint.
        /// </summary>
        public SKPoint[] Corners { get; }
    }
}
