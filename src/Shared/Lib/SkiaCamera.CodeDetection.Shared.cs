namespace DrawnUi.Camera;

// Live detection of machine-readable codes (QR etc). The contract lives here for every flavor;
// a platform implements it through the partial hook and reports IsCodeDetectionSupported.
partial class SkiaCamera
{
    /// <summary>
    /// True when this platform can detect codes in the live preview. Currently iOS only.
    /// </summary>
    public static bool IsCodeDetectionSupported
    {
        get
        {
#if IOS
            return true;
#else
            return false;
#endif
        }
    }

    public static readonly BindableProperty CodeDetectionProperty = BindableProperty.Create(
        nameof(CodeDetection),
        typeof(CameraCodeTypes),
        typeof(SkiaCamera),
        CameraCodeTypes.None,
        propertyChanged: (bindable, oldValue, newValue) =>
        {
            if (bindable is SkiaCamera camera)
            {
                camera.OnCodeDetectionChanged();
            }
        });

    /// <summary>
    /// Code formats to detect in the live preview, results come through <see cref="CodesDetected"/>.
    /// Default is None: no detection, no cost. Changing it while the camera is on restarts the camera.
    /// Ignored where <see cref="IsCodeDetectionSupported"/> is false.
    /// </summary>
    public CameraCodeTypes CodeDetection
    {
        get { return (CameraCodeTypes)GetValue(CodeDetectionProperty); }
        set { SetValue(CodeDetectionProperty, value); }
    }

    /// <summary>
    /// Raised when codes are seen in the preview, and once with an empty list when they are gone or
    /// the camera stops. NOT raised on the UI thread and not on the render thread.
    /// </summary>
    public event EventHandler<IReadOnlyList<DetectedCode>> CodesDetected;

    internal bool HasCodesDetectedSubscribers => CodesDetected != null;

    internal void RaiseCodesDetected(IReadOnlyList<DetectedCode> codes)
    {
        CodesDetected?.Invoke(this, codes);
    }

    /// <summary>
    /// Platform hook: <see cref="CodeDetection"/> changed.
    /// </summary>
    partial void OnCodeDetectionChanged();
}
