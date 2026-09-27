using System.Globalization;

namespace DrawnUi.Camera;

/// <summary>
/// How a Windows recording was produced: on the GPU or on the CPU, why the GPU path was not taken, the adapters involved and
/// the frame counts. Updated while recording and final after stop. For diagnostics, logs and tests.
/// </summary>
public sealed class WindowsRecordingReport
{
    /// <summary>"gpu" when frames were composed and encoded on the GPU, "cpu" for the raster path.</summary>
    public string Path { get; internal set; }

    /// <summary>Why the GPU path was not used; null when it was.</summary>
    public string FallbackReason { get; internal set; }

    /// <summary>The adapter ANGLE renders the UI on.</summary>
    public string UiAdapter { get; internal set; }

    /// <summary>The adapter of the device the camera frames arrive on.</summary>
    public string CaptureAdapter { get; internal set; }

    /// <summary>The adapter of the recorder / encoder device (GPU path).</summary>
    public string EncoderAdapter { get; internal set; }

    /// <summary>The video transforms of the sink writer (GPU path): converter and encoder, hardware or software.</summary>
    public string VideoEncoder { get; internal set; }

    /// <summary>Camera frames delivered while recording.</summary>
    public long FramesOffered { get; internal set; }

    /// <summary>Frames written to the file.</summary>
    public long FramesWritten { get; internal set; }

    /// <summary>Camera frames that did not make it into the file.</summary>
    public long FramesDropped => Math.Max(0, FramesOffered - FramesWritten);

    /// <summary>Wall-clock time from the first to the last frame of the recording.</summary>
    public TimeSpan Elapsed { get; internal set; }

    /// <summary>Written frames per second of <see cref="Elapsed"/>.</summary>
    public double Fps => Elapsed.TotalSeconds > 0 ? FramesWritten / Elapsed.TotalSeconds : 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"path={Path} frames={FramesWritten} offered={FramesOffered} dropped={FramesDropped} seconds={Elapsed.TotalSeconds:0.00} fps={Fps:0.0} fallback=\"{FallbackReason}\" uiAdapter=\"{UiAdapter}\" captureAdapter=\"{CaptureAdapter}\" encoderAdapter=\"{EncoderAdapter}\" videoEncoder=\"{VideoEncoder}\"");
}
