#if IOS || MACCATALYST
using AVFoundation;
using Foundation;
using System.Diagnostics;

namespace DrawnUi.Camera;

/// <summary>
/// The library's claims on the app's shared AVAudioSession (preview audio, recording audio, movie output).
/// The first claim saves the app's category, options, mode and preferred input; the last release
/// deactivates the session with NotifyOthersOnDeactivation (other apps' audio resumes) and restores them.
/// Left PlayAndRecord and active, the session muted the app's haptics and system sounds for the rest
/// of the run after the microphone had been used once.
/// </summary>
internal static class AppleAudioSession
{
    static readonly object _lock = new();
    static int _claims;
    static bool _saved;
    static string _category;
    static AVAudioSessionCategoryOptions _options;
    static string _mode;
    static AVAudioSessionPortDescription _preferredInput;

    const int ReleaseGraceMs = 1000;

    /// <summary>
    /// Call before changing the session. Pair every claim with one <see cref="Release"/>.
    /// </summary>
    public static void Claim()
    {
        lock (_lock)
        {
            // a restore still pending from the last release keeps the app's state it saved:
            // what is set now is ours
            if (_claims++ == 0 && !_saved)
            {
                var session = AVAudioSession.SharedInstance();
                _category = session.Category;
                _options = session.CategoryOptions;
                _mode = session.Mode;
                _preferredInput = session.PreferredInput;
                _saved = true;
            }
        }
    }

    /// <summary>
    /// The last release deactivates the session and restores the app's settings, off the calling thread
    /// (deactivation blocks while audio I/O winds down) and after a short grace period: a camera restart or
    /// a preview-to-recording handover claims again within it and keeps the session instead of
    /// deactivating, resuming other apps' audio and interrupting it again. A claim meanwhile cancels the restore.
    /// </summary>
    public static void Release()
    {
        lock (_lock)
        {
            if (_claims <= 0)
                return;

            if (--_claims > 0)
                return;
        }

        Task.Run(async () =>
        {
            await Task.Delay(ReleaseGraceMs);

            lock (_lock)
            {
                if (_claims > 0 || !_saved)
                    return;

                _saved = false;

                var session = AVAudioSession.SharedInstance();

                session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var error);
                if (error != null)
                    Debug.WriteLine($"[AppleAudioSession] Deactivation error: {error.LocalizedDescription}");

                if (!string.IsNullOrEmpty(_category))
                {
                    session.SetCategory(_category, _options, out error);
                    if (error != null)
                        Debug.WriteLine($"[AppleAudioSession] Category restore error: {error.LocalizedDescription}");
                }

                if (!string.IsNullOrEmpty(_mode))
                {
                    session.SetMode(new NSString(_mode), out error);
                    if (error != null)
                        Debug.WriteLine($"[AppleAudioSession] Mode restore error: {error.LocalizedDescription}");
                }

                session.SetPreferredInput(_preferredInput, out error);

                _preferredInput = null;
                Debug.WriteLine($"[AppleAudioSession] Released: category {_category}, mode {_mode}");
            }
        });
    }
}
#endif
