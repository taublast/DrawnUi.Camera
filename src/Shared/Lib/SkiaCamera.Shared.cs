namespace DrawnUi.Camera;

 partial class SkiaCamera
{
    private IAudioCapture _audioCapture;
    private IAudioCapture _previewAudioCapture; // Separate instance for preview-only audio (not recording)

    /// <summary>
    /// Fired when audio sample is captured for recording, or during preview when EnableAudioMonitoring is enabled.
    /// Active during recording and optional live monitoring.
    /// Parameters: (byte[] data, int sampleRate, int bitsPerSample, int channels)
    /// </summary>
    public event Action<byte[], int, int, int> AudioSampleAvailable;

    /// <summary>
    /// Raised when the camera hardware state changes.
    /// </summary>
    public event EventHandler<HardwareState> StateChanged;

    /// <summary>
    /// Audio is available, use by writer etc, you can use to change the audio, apply gain etc.. Raises the AudioSampleAvailable event!
    /// </summary>
    protected virtual AudioSample OnAudioSampleAvailable(AudioSample sample)
    {
        AudioSampleAvailable?.Invoke(
            sample.Data,
            sample.SampleRate,
            sample.BytesPerSample * 8,
            sample.Channels);

        return sample;
    }

    private SKImage _injectedFrame;

    /// <summary>
    /// Injects a single frame to be used as the next preview/recording source frame instead of
    /// the live camera feed. Works on every platform (incl. the headless pure-.NET build) — this
    /// is the supported way to set your own image as the camera source, e.g. for testing or mocking.
    /// Ownership transfers: the frame will be disposed once consumed.
    /// </summary>
    public void InjectFrame(SKImage frame)
    {
        _injectedFrame?.Dispose();
        _injectedFrame = frame;
    }

    /// <summary>
    /// Injects an audio sample as if it was captured live, routing it through the same pipeline
    /// (AudioSampleAvailable, recording writer, monitoring) as real microphone capture.
    /// </summary>
    public void InjectAudioSample(AudioSample sample)
    {
        OnAudioSampleAvailable(sample);
    }

    /// <summary>
    /// Stops the camera by setting IsOn to false
    /// </summary>
    public virtual void Stop()
    {
        IsOn = false;
    }

    /// <summary>
    /// Starts the camera by setting IsOn to true.
    /// The actual camera initialization and permission handling happens automatically.
    /// </summary>
    public virtual void Start()
    {
        IsOn = true;  
    }

    /// <summary>
    /// Gets or sets the current hardware state of the camera pipeline.
    /// </summary>
    public HardwareState State
    {
        get { return (HardwareState)GetValue(StateProperty); }
        set { SetValue(StateProperty, value); }
    }

    public static readonly BindableProperty StateProperty = BindableProperty.Create(
        nameof(State),
        typeof(HardwareState),
        typeof(SkiaCamera),
        HardwareState.Off,
        BindingMode.OneWayToSource, propertyChanged: ControlStateChanged);

    private static void ControlStateChanged(BindableObject bindable, object oldvalue, object newvalue)
    {
        if (bindable is SkiaCamera control)
        {
            control.OnStateChanged(control.State);
        }
    }
    
    /// <summary>
    /// Gets or sets whether the camera control should be powered on.
    /// </summary>
    public bool IsOn
    {
        get { return (bool)GetValue(IsOnProperty); }
        set { SetValue(IsOnProperty, value); }
    }

    public static readonly BindableProperty IsOnProperty = BindableProperty.Create(
        nameof(IsOn),
        typeof(bool),
        typeof(SkiaCamera),
        false,
        propertyChanged: PowerChanged);


}