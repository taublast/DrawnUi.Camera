#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using CameraTests.UI;
using CameraTests.Views;
using DrawnUi.Camera;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;

namespace CameraTests;

/// <summary>
/// Test runs of the sample driven from the command line (Windows). Inert unless <c>--auto-run</c> is given. Everything happens
/// inside this process through the camera control's own API: no desktop input is simulated. Writes a log and one RESULT line,
/// then exits with code 0 on success.
/// <code>
/// --auto-run record|preview|restart-test
/// --capture video|still          capture mode (record: video)
/// --video-format 1280x720@30     one of the camera's video formats, or --video-quality low|standard|high|ultra
/// --shader none|NAME|FILE.sksl   a ShaderEffect name (Zoom, Movie, Wes, Runner, Desat, BW, Sketch) or an .sksl file
/// --gpu on|off                   GPU or CPU recording path (needs a library build that has UseGpuProcessing)
/// --seconds 10                   recording or preview duration
/// --pre-record 0                 seconds of pre-recording before the live recording starts
/// --audio on|off                 record audio (default on)
/// --stop stop|abort              how the recording ends
/// --after-seconds 0              stay in preview this long after the recording (memory lines keep coming)
/// --repeat 1                     record this many times in a row
/// --stamp                        burn a frame counter into every recorded frame (checked by Mp4Check --stamps)
/// --restart-during N             N seconds into the recording, change a setting that restarts the camera
/// --memory-every 10              seconds between MEM lines (private bytes, handles, GC counts)
/// --out FOLDER                   where the recording is copied (default %TEMP%\SkiaCameraRuns)
/// --log FILE                     log file (default FOLDER\run.log)
/// --title TEXT                   window title (default "AGENT MEASUREMENT, do not touch")
/// --seed 1                       restart-test: random intervals
/// </code>
/// </summary>
public static class SampleAutomation
{
    static readonly string[] Args = Environment.GetCommandLineArgs();

    /// <summary>
    /// Value of a switch given as "--name value" or "--name=value", null when absent.
    /// </summary>
    public static string Arg(string name)
    {
        for (var i = 1; i < Args.Length; i++)
        {
            if (Args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < Args.Length ? Args[i + 1] : string.Empty;
            if (Args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return Args[i][(name.Length + 1)..];
        }
        return null;
    }

    public static bool Enabled => Arg("--auto-run") != null;

    static string _logPath;
    static readonly object LogGate = new();
    static int _disturbed;

    static void Log(string line)
    {
        lock (LogGate)
        {
            try
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Called with the app window: labels and places it, and starts the run when --auto-run is given.
    /// </summary>
    public static void Attach(Window window)
    {
        if (!Enabled)
            return;

        var outDir = Arg("--out") ?? Path.Combine(Path.GetTempPath(), "SkiaCameraRuns");
        Directory.CreateDirectory(outDir);
        _logPath = Arg("--log") ?? Path.Combine(outDir, "run.log");
        var title = Arg("--title") ?? "AGENT MEASUREMENT, do not touch";
        window.Title = title;
        window.Created += (_, _) => Task.Delay(500).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => Place(window, title)));
        Trace.Listeners.Add(new LibraryLog()); // the camera library logs through Super.Log -> Trace
        Log($"==== {string.Join(' ', Args.Skip(1))}");
        _ = Task.Run(() => RunAsync(window, outDir));
    }

    static void Place(Window window, string title)
    {
        try
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;
            var app = native.AppWindow;
            app.Title = title;
            // out of the way: a small window in the bottom-right corner of the work area
            var area = DisplayArea.GetFromWindowId(app.Id, DisplayAreaFallback.Primary).WorkArea;
            app.MoveAndResize(new global::Windows.Graphics.RectInt32(area.X + area.Width - 680, area.Y + area.Height - 560, 680, 560));
            if (native.Content is Microsoft.UI.Xaml.UIElement root)
            {
                // input is only logged: a run that received any is reported as disturbed
                root.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Disturbed("pointer")), true);
                root.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, new KeyEventHandler((_, e) => Disturbed($"key {e.Key}")), true);
            }
        }
        catch (Exception e)
        {
            Log("window placement failed: " + e.Message);
        }
    }

    /// <summary>
    /// Copies the camera library's own log lines (tags [SkiaCamera], [NativeCameraWindows], [GpuRecorder], [AppCamera]) into the run log.
    /// </summary>
    sealed class LibraryLog : TraceListener
    {
        public override void Write(string message)
        {
        }

        public override void WriteLine(string message)
        {
            if (message != null && (message.StartsWith("[SkiaCamera]") || message.StartsWith("[NativeCameraWindows]")
                                    || message.StartsWith("[GpuRecorder]") || message.StartsWith("[AppCamera]")))
                Log("LIB " + message);
        }
    }

    static void Disturbed(string what)
    {
        Interlocked.Increment(ref _disturbed);
        Log($"INPUT {what}: run disturbed");
    }

    // ------------------------------------------------------------------ run

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long _previewFrames;
    static readonly List<double> PreviewTimes = new();

    static async Task RunAsync(Window window, string outDir)
    {
        var code = 1;
        var result = new StringBuilder();
        try
        {
            var mode = Arg("--auto-run");
            var cam = await FindCamera(window);
            cam.NewPreviewSet += (_, _) =>
            {
                Interlocked.Increment(ref _previewFrames);
                var now = Clock.Elapsed.TotalMilliseconds;
                lock (PreviewTimes)
                {
                    if (PreviewTimes.Count > 0 && now - PreviewTimes[^1] > 300)
                        Log($"preview gap {now - PreviewTimes[^1]:0} ms ended");
                    PreviewTimes.Add(now);
                }
            };
            cam.OnError += (_, e) => Log("OnError " + e);
            cam.StateChanged += (_, st) => Log("State " + st);

            if (!await WaitFrames(30, 30))
                throw new Exception("no preview frames within 30 s of start");

            await Configure(cam, mode);
            if (!await Settle(cam, 20))
                throw new Exception("camera did not settle after configuration");
            Log("configured: " + Describe(cam));
            var memory = MemoryLoop();

            code = mode switch
            {
                "record" => await Record(cam, outDir, result),
                "preview" => await Preview(result),
                "restart-test" => await RestartTest(cam, result),
                _ => throw new Exception($"unknown --auto-run {mode}")
            };
            _stopMemory = true;
            await memory;
        }
        catch (Exception e)
        {
            Log("FAILED " + e);
            result.Append($" error=\"{e.Message}\"");
            code = 1;
        }

        Log($"RESULT ok={(code == 0 ? 1 : 0)}{result} disturbed={_disturbed}");
        await Task.Delay(300);
        Environment.Exit(code);
    }

    static async Task<AppCamera> FindCamera(Window window)
    {
        for (var i = 0; i < 600; i++)
        {
            var page = window.Page;
            if (page?.GetType().GetField("CameraControl", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(page) is AppCamera cam)
                return cam;
            await Task.Delay(100);
        }
        throw new Exception("camera control not found");
    }

    static async Task<bool> WaitFrames(int count, int seconds)
    {
        var start = Interlocked.Read(ref _previewFrames);
        var t = Stopwatch.StartNew();
        while (Interlocked.Read(ref _previewFrames) - start < count)
        {
            if (t.Elapsed.TotalSeconds > seconds)
                return false;
            await Task.Delay(50);
        }
        return true;
    }

    static readonly FieldInfo Debounce = typeof(SkiaCamera).GetField("_restartDebounceTimer", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Waits for a pending restart to finish: no debounce timer, state On, and new preview frames.
    /// </summary>
    static async Task<bool> Settle(SkiaCamera cam, int seconds)
    {
        var t = Stopwatch.StartNew();
        await Task.Delay(700); // the debounce is 500 ms
        while (t.Elapsed.TotalSeconds < seconds)
        {
            if (Debounce?.GetValue(cam) == null && cam.State == HardwareState.On && await WaitFrames(10, 3))
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    static Task OnMain(Action action) => MainThread.InvokeOnMainThreadAsync(action);

    static async Task Configure(AppCamera cam, string mode)
    {
        var capture = Arg("--capture") ?? (mode == "record" ? "video" : null);
        var shader = Arg("--shader");
        var gpu = Arg("--gpu");
        var audio = Arg("--audio");
        var quality = Arg("--video-quality");
        var format = Arg("--video-format");

        VideoFormat chosen = null;
        if (format != null)
        {
            var formats = await MainThread.InvokeOnMainThreadAsync(() => cam.GetAvailableVideoFormatsAsync());
            chosen = formats?.FirstOrDefault(f => $"{f.Width}x{f.Height}@{f.FrameRate}".Equals(format, StringComparison.OrdinalIgnoreCase))
                     ?? throw new Exception($"video format {format} not offered; the camera has: {string.Join(", ", formats?.Select(f => $"{f.Width}x{f.Height}@{f.FrameRate}").Distinct() ?? [])}");
        }

        if (Arg("--stamp") != null)
        {
            // a frame counter burnt into every recorded frame (20 bits, top and bottom rows) so a decoder can check
            // each frame: none repeated, none missing, none out of order (Mp4Check --stamps)
            var original = cam.ProcessFrame;
            long counter = 0;
            var whitePaint = new SKPaint { Color = SKColors.White };
            var blackPaint = new SKPaint { Color = SKColors.Black };
            cam.ProcessFrame = frame =>
            {
                original?.Invoke(frame);
                var n = counter++;
                var b = frame.Width / 40f;
                for (var i = 0; i < 20; i++)
                {
                    var paint = ((n >> i) & 1) != 0 ? whitePaint : blackPaint;
                    var x = b + i * 1.5f * b;
                    frame.Canvas.DrawRect(SKRect.Create(x, b / 2, b, b), paint);
                    frame.Canvas.DrawRect(SKRect.Create(x, frame.Height - 1.5f * b, b, b), paint);
                }
            };
        }

        await OnMain(() =>
        {
            if (capture != null)
                cam.CaptureMode = capture.Equals("still", StringComparison.OrdinalIgnoreCase) ? CaptureModeType.Still : CaptureModeType.Video;
            if (chosen != null)
            {
                cam.VideoQuality = VideoQuality.Manual;
                cam.VideoFormatIndex = chosen.Index;
            }
            else if (quality != null)
                cam.VideoQuality = Enum.Parse<VideoQuality>(quality, true);
            if (audio != null)
                cam.EnableAudioRecording = audio.Equals("on", StringComparison.OrdinalIgnoreCase);
            if (shader != null)
            {
                if (shader.EndsWith(".sksl", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(shader))
                        throw new Exception($"shader file {shader} not found");
                    cam.CustomShaderPath = Path.GetFullPath(shader);
                }
                else
                {
                    cam.CustomShaderPath = null;
                    cam.VideoEffect = Enum.Parse<ShaderEffect>(shader, true);
                }
            }
            if (gpu != null)
            {
                var property = cam.GetType().GetProperty("UseGpuProcessing")
                               ?? throw new Exception("--gpu needs a library build with SkiaCamera.UseGpuProcessing");
                property.SetValue(cam, gpu.Equals("on", StringComparison.OrdinalIgnoreCase));
            }
        });
    }

    static string Describe(SkiaCamera cam)
    {
        var vf = cam.NativeControl?.GetCurrentVideoFormat();
        return $"mode={cam.CaptureMode} state={cam.State} videoFormat={(vf == null ? "none" : $"{vf.Width}x{vf.Height}@{vf.FrameRate}")} preview={cam.NativeControl?.PreviewWidth}x{cam.NativeControl?.PreviewHeight} videoQuality={cam.VideoQuality}";
    }

    // ------------------------------------------------------------------ modes

    static async Task<int> Record(AppCamera cam, string outDir, StringBuilder result)
    {
        var repeat = int.Parse(Arg("--repeat") ?? "1");
        var code = 0;
        for (var i = 0; i < repeat; i++)
        {
            var one = new StringBuilder();
            code |= await RecordOnce(cam, outDir, one);
            if (repeat > 1)
                Log($"recording {i + 1}/{repeat}:{one}");
            result.Append(one);
        }
        return code;
    }

    static async Task<int> RecordOnce(AppCamera cam, string outDir, StringBuilder result)
    {
        var seconds = double.Parse(Arg("--seconds") ?? "10", CultureInfo.InvariantCulture);
        var pre = double.Parse(Arg("--pre-record") ?? "0", CultureInfo.InvariantCulture);
        var abort = (Arg("--stop") ?? "stop").Equals("abort", StringComparison.OrdinalIgnoreCase);

        var done = new TaskCompletionSource<CapturedVideo>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CapturedVideo> onSuccess = (_, video) => done.TrySetResult(video);
        EventHandler<Exception> onFailed = (_, e) => done.TrySetException(e);
        cam.RecordingSuccess += onSuccess;
        cam.RecordingFailed += onFailed;
        try
        {
            return await RecordCore(cam, outDir, result, seconds, pre, abort, done);
        }
        finally
        {
            cam.RecordingSuccess -= onSuccess;
            cam.RecordingFailed -= onFailed;
        }
    }

    static async Task<int> RecordCore(AppCamera cam, string outDir, StringBuilder result, double seconds, double pre, bool abort,
        TaskCompletionSource<CapturedVideo> done)
    {

        if (pre > 0)
        {
            await OnMain(() =>
            {
                cam.EnablePreRecording = true;
                cam.PreRecordDuration = TimeSpan.FromSeconds(pre);
            });
            await Task.Run(() => cam.StartVideoRecording());
            Log($"pre-recording for {pre + 1:0.#} s");
            await Task.Delay(TimeSpan.FromSeconds(pre + 1));
        }

        var paceFrom = Clock.Elapsed.TotalMilliseconds;
        var uiGaps = UiHeartbeat(TimeSpan.FromSeconds(seconds + 2));
        await Task.Run(() => cam.StartVideoRecording());
        Log($"recording {(pre > 0 ? "live " : "")}for {seconds} s, IsRecording={cam.IsRecording}");
        var restartAt = double.Parse(Arg("--restart-during") ?? "-1", CultureInfo.InvariantCulture);
        if (restartAt >= 0 && restartAt < seconds)
        {
            // a setting that makes the camera set itself up again while the recording runs
            await Task.Delay(TimeSpan.FromSeconds(restartAt));
            await OnMain(() => cam.PhotoQuality = cam.PhotoQuality == CaptureQuality.High ? CaptureQuality.Medium : CaptureQuality.High);
            Log($"camera restart requested during the recording (PhotoQuality -> {cam.PhotoQuality})");
            await Task.Delay(TimeSpan.FromSeconds(seconds - restartAt));
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        var pace = Pace(paceFrom, Clock.Elapsed.TotalMilliseconds);
        var ui = await uiGaps;

        await Task.Run(() => cam.StopVideoRecording(abort));
        Log(abort ? "aborted" : "stopped");

        CapturedVideo video = null;
        if (!abort)
        {
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            if (finished != done.Task)
                throw new Exception("no RecordingSuccess within 60 s of stop");
            video = await done.Task;
        }
        else
        {
            await Task.Delay(2000); // abort raises no event; give the teardown time to finish
        }

        var after = double.Parse(Arg("--after-seconds") ?? "0", CultureInfo.InvariantCulture);
        if (after > 0)
        {
            Log($"staying in preview for {after} s after the recording");
            await Task.Delay(TimeSpan.FromSeconds(after));
        }

        var report = cam.GetType().GetProperty("RecordingReport")?.GetValue(cam)?.ToString() ?? "report=unavailable";
        string copy = null;
        if (video != null && File.Exists(video.FilePath))
        {
            var effect = cam.CustomShaderPath != null ? Path.GetFileNameWithoutExtension(cam.CustomShaderPath) : cam.VideoEffect.ToString();
            var vf = cam.NativeControl?.GetCurrentVideoFormat();
            copy = Path.Combine(outDir, $"rec-{(Arg("--gpu") ?? "default")}-{effect}-{vf?.Width}x{vf?.Height}-{DateTime.Now:HHmmss}.mp4");
            File.Move(video.FilePath, copy, true); // the library wrote it to Documents; the run keeps it in its own folder
        }

        result.Append($" mode=record {report} preview={pace} uiThread={ui} file=\"{copy}\" bytes={(copy != null ? new FileInfo(copy).Length : 0)} duration={video?.Duration.TotalSeconds:0.00}");
        return abort || copy != null ? 0 : 1;
    }

    static async Task<int> Preview(StringBuilder result)
    {
        var seconds = double.Parse(Arg("--seconds") ?? "10", CultureInfo.InvariantCulture);
        var from = Clock.Elapsed.TotalMilliseconds;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        result.Append($" mode=preview preview={Pace(from, Clock.Elapsed.TotalMilliseconds)}");
        return 0;
    }

    /// <summary>
    /// Capture-mode toggles at random intervals, each settle checked for frames within 2 s and state On.
    /// </summary>
    static async Task<int> RestartTest(AppCamera cam, StringBuilder result)
    {
        var rnd = new Random(int.Parse(Arg("--seed") ?? "1"));
        var count = int.Parse(Arg("--toggles") ?? "50");
        int checks = 0, fails = 0;
        for (var i = 0; i < count; i++)
        {
            await OnMain(() => cam.CaptureMode = cam.CaptureMode == CaptureModeType.Still ? CaptureModeType.Video : CaptureModeType.Still);
            await Task.Delay(rnd.Next(200, 3001));
            if (i % 5 == 4 || i == count - 1)
            {
                checks++;
                if (!await Settle(cam, 20))
                {
                    fails++;
                    Log($"FAIL settle after toggle {i + 1}: {Describe(cam)}");
                }
            }
        }
        result.Append($" mode=restart-test toggles={count} settle-checks={checks} failures={fails}");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// UI-thread responsiveness: posts a tiny job every 10 ms and reports how long the longest one waited.
    /// </summary>
    static async Task<string> UiHeartbeat(TimeSpan duration)
    {
        var waits = new List<double>();
        var t = Stopwatch.StartNew();
        while (t.Elapsed < duration)
        {
            var posted = Stopwatch.GetTimestamp();
            await MainThread.InvokeOnMainThreadAsync(() => { });
            waits.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
            await Task.Delay(10);
        }
        waits.Sort();
        return waits.Count == 0 ? "none" : string.Create(CultureInfo.InvariantCulture,
            $"wait-ms-p50={waits[waits.Count / 2]:0.0},p99={waits[Math.Min(waits.Count - 1, (int)(waits.Count * 0.99))]:0.0},max={waits[^1]:0.0}");
    }

    static string Pace(double fromMs, double toMs)
    {
        List<double> times;
        lock (PreviewTimes)
            times = PreviewTimes.Where(t => t >= fromMs && t <= toMs).ToList();
        if (times.Count < 2)
            return "none";
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).OrderBy(g => g).ToList();
        double P(double q) => gaps[Math.Min(gaps.Count - 1, (int)(q * gaps.Count))];
        return string.Create(CultureInfo.InvariantCulture,
            $"{times.Count / ((toMs - fromMs) / 1000):0.0}fps(gap-ms-p50={P(0.5):0.0},p95={P(0.95):0.0},max={gaps[^1]:0.0})");
    }

    static volatile bool _stopMemory;

    static async Task MemoryLoop()
    {
        var every = int.Parse(Arg("--memory-every") ?? "10");
        while (!_stopMemory)
        {
            using (var p = Process.GetCurrentProcess())
                Log(string.Create(CultureInfo.InvariantCulture,
                    $"MEM private={p.PrivateMemorySize64 / 1048576.0:0.0}MB ws={p.WorkingSet64 / 1048576.0:0.0}MB handles={p.HandleCount} gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} managed={GC.GetTotalMemory(false) / 1048576.0:0.0}MB previewFrames={Interlocked.Read(ref _previewFrames)}"));
            for (var i = 0; i < every * 10 && !_stopMemory; i++)
                await Task.Delay(100);
        }
    }
}
#endif
