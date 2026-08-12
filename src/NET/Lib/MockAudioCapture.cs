namespace DrawnUi.Camera;

/// <summary>
/// Mock <see cref="IAudioCapture"/> backend for the headless / pure .NET build.
/// There is no real microphone on this target — feed samples manually via <see cref="Push"/>
/// (typically through <see cref="SkiaCamera.InjectAudioSample"/>).
/// </summary>
public class MockAudioCapture : IAudioCapture
{
    public string LastError { get; private set; }
    public bool IsCapturing { get; private set; }
    public int SampleRate { get; private set; } = 44100;
    public int Channels { get; private set; } = 1;
    public CameraAudioMode AudioMode { get; set; }

    public event EventHandler<AudioSample> SampleAvailable;

    public void Push(AudioSample sample) => SampleAvailable?.Invoke(this, sample);

    public Task<bool> StartAsync(int sampleRate = 44100, int channels = 1, AudioBitDepth bitDepth = AudioBitDepth.Pcm16Bit, int deviceIndex = -1)
    {
        SampleRate = sampleRate;
        Channels = channels;
        IsCapturing = true;
        return Task.FromResult(true);
    }

    public Task StopAsync()
    {
        IsCapturing = false;
        return Task.CompletedTask;
    }

    public Task<List<AudioDeviceInfo>> GetAvailableDevicesAsync() =>
        Task.FromResult(new List<AudioDeviceInfo>());

    public void Dispose()
    {
        IsCapturing = false;
    }
}