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

        /// <summary>
        /// True when this is a passkey sign-in code: the QR a site shows for "sign in with a passkey on
        /// another device" (FIDO cross-device authentication). Its text is "FIDO:/" followed by digits
        /// only, and that is all this checks. Hand it to SkiaCamera.StartPasskeySignInAsync on a tap.
        /// </summary>
        public bool IsPasskeySignIn
        {
            get
            {
                const string prefix = "FIDO:/";
                const int maxLength = 1024; // real ones are a few hundred digits

                var value = Value;
                if (value == null || value.Length <= prefix.Length || value.Length > maxLength
                    || !value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }

                for (int i = prefix.Length; i < value.Length; i++)
                {
                    if (value[i] < '0' || value[i] > '9')
                        return false;
                }

                return true;
            }
        }
    }
}
