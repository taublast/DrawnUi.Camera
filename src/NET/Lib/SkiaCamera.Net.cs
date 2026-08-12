 
namespace DrawnUi.Camera;

// Mock backend for the pure .NET build (no native platform): wires SkiaCamera's audio
// pipeline to MockAudioCapture so InjectAudioSample() reaches OnAudioSampleAvailable
// exactly like a real platform's mic capture would.
public partial class SkiaCamera : SkiaControl
{
    private MockAudioCapture _mockAudioCapture;

    public SkiaCamera()
    {

    }


    public void StartPreviewAudioCapture()
    {
        _mockAudioCapture ??= new MockAudioCapture();
        _audioCapture = _mockAudioCapture;
        _audioCapture.SampleAvailable += OnAudioSampleAvailable;
    }

    public void StopPreviewAudioCapture()
    {
        if (_audioCapture != null)
        {
            _audioCapture.SampleAvailable -= OnAudioSampleAvailable;
        }
    }

    private void OnAudioSampleAvailable(object sender, AudioSample sample)
    {
        OnAudioSampleAvailable(sample);
    }

    private static void PowerChanged(BindableObject bindable, object oldvalue, object newvalue)
    {
        if (bindable is SkiaCamera control)
        {
            control.State = (bool)newvalue ? HardwareState.On : HardwareState.Off;
        }
    }

    public virtual void OnStateChanged(HardwareState state)
    {
        StateChanged?.Invoke(this, state);

        if (state == HardwareState.On)
            StartPreviewAudioCapture();
        else
            StopPreviewAudioCapture();
    }

    /// <summary>
    /// Headless flavor has no native camera hardware: the only source of frames is InjectFrame().
    /// Consumes and clears the injected frame, same contract as the MAUI flavor's native-backed override.
    /// </summary>
    protected virtual SKImage AquireFrameFromNative()
    {
        if (_injectedFrame != null)
        {
            var injected = _injectedFrame;
            _injectedFrame = null;
            return injected;
        }

        return null;
    }
}


