namespace DrawnUi.Camera;

// Live detection of machine-readable codes (QR etc). The contract lives here for every flavor;
// a platform implements it through the partial hook and reports IsCodeDetectionSupported.
partial class SkiaCamera
{
    /// <summary>
    /// True when this platform can detect codes in the live preview. Currently iOS only.
    /// </summary>
    public static bool IsCodeDetectionSupported =>
        OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst(); // IsIOS is true on Mac Catalyst too

    /// <summary>
    /// True when this platform can hand a passkey sign-in code (<see cref="DetectedCode.IsPasskeySignIn"/>)
    /// to the system. Currently iOS 16 and later.
    /// </summary>
    public static bool IsPasskeySignInSupported =>
        OperatingSystem.IsIOSVersionAtLeast(16) && !OperatingSystem.IsMacCatalyst();

    /// <summary>
    /// Hands a passkey sign-in code to the system, which shows its own passkey sheet and does the whole
    /// sign-in: the app never sees a key. Call it from a user action (a tap), never by itself on detection.
    /// Returns false when the code is not a passkey sign-in code, the platform cannot do it
    /// (<see cref="IsPasskeySignInSupported"/>), or the system refused the link.
    /// </summary>
    public static Task<bool> StartPasskeySignInAsync(DetectedCode code)
    {
        if (code == null || !code.IsPasskeySignIn || !IsPasskeySignInSupported)
            return Task.FromResult(false);

#if IOS
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                // the text exactly as scanned: IsPasskeySignIn checked it is "FIDO:/" + digits
                using var url = new Foundation.NSUrl(code.Value);
                return await UIKit.UIApplication.SharedApplication.OpenUrlAsync(url,
                    new UIKit.UIApplicationOpenUrlOptions());
            }
            catch (Exception e)
            {
                Super.Log(e);
                return false;
            }
        });
#else
        return Task.FromResult(false);
#endif
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
