namespace DrawnUi.Camera
{
    /// <summary>
    /// Machine-readable code formats the camera can detect in the live preview.
    /// Combine as flags in <see cref="SkiaCamera.CodeDetection"/>.
    /// </summary>
    [Flags]
    public enum CameraCodeTypes
    {
        /// <summary>
        /// Detection is off, nothing is added to the capture session. This is the default value.
        /// </summary>
        None = 0,

        Qr = 1 << 0,
        Aztec = 1 << 1,
        DataMatrix = 1 << 2,
        Pdf417 = 1 << 3,
        Ean13 = 1 << 4,
        Ean8 = 1 << 5,
        Code128 = 1 << 6,
        Code39 = 1 << 7,
    }
}
