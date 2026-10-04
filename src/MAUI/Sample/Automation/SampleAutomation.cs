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
/// --auto-run record|preview|restart-test|photo|dispose-test|mock
/// --dispose-during start|recording  dispose-test: when the camera control is disposed
/// --capture video|still          capture mode (record: video)
/// --video-format 1280x720@30     one of the camera's video formats, or --video-quality low|standard|high|ultra
/// --shader none|NAME|FILE.sksl   a ShaderEffect name (Zoom, Movie, Wes, Runner, Desat, BW, Sketch) or an .sksl file
/// --gpu on|off                   GPU or CPU recording path (needs a library build that has UseGpuProcessing)
/// --seconds 10                   recording or preview duration
/// --pre-record 0                 seconds of pre-recording before the live recording starts
/// --audio on|off                 record audio (default on)
/// --mirror-refresh on|off        SkiaCamera.MirrorRecordingToPreview (default on)
/// --stop stop|abort              how the recording ends
/// --after-seconds 0              stay in preview this long after the recording (memory lines keep coming)
/// --repeat 1                     record (photo: take) this many times in a row
/// --stamp                        burn a frame counter into every recorded frame (checked by Mp4Check --stamps)
/// --restart-during N             N seconds into the recording, change a setting that restarts the camera
/// --restart-kind quality|mode|format  which setting: photo quality, capture mode (to Still), or another video format
/// --snapshot FILE.png            preview: save the frame GetPreviewImage returns off the UI thread
/// --snapshot-ab FOLDER           preview: snapshots GPU, CPU, GPU again within seconds (colour comparison of the paths)
/// --raw-rgba 224x224             preview or record: RawCameraFrame.TryGetRgba on every 5th raw frame (success, ms, luma)
/// --check-mirror                 record with --stamp: the preview must show the recording frames (their counter is read back)
/// --raw-rgba-check               with --raw-rgba: pixel error against the Skia path on the same frames (rotations, crop)
/// --raw-rgba-identity            with --raw-rgba and DRAWNUI_CAMERA_TEST_STAMP=1: TryGetRgba bytes and the drawn frame carry the same frame number
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

    /// <summary>
    /// --auto-run ui-record: the recording is driven through the page's own record button handler on the UI thread, and the
    /// page's own save and thumbnail code runs (the file is moved out of the gallery into the run folder afterwards).
    /// </summary>
    public static bool UiFlow => Arg("--auto-run") == "ui-record";

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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint GetForegroundWindow();

    static nint _hwnd;

    /// <summary>
    /// Test runs only, as the window is created (before it is shown): it never becomes the foreground window
    /// (WS_EX_NOACTIVATE) and sits at the bottom of the z-order, so it cannot take focus or keystrokes from whoever works
    /// at the desk. Only this process's own window is touched.
    /// </summary>
    public static void KeepInBackground(Microsoft.UI.Xaml.Window window)
    {
        if (!Enabled)
            return;
        const int GWL_EXSTYLE = -20;
        const long WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (nint)((long)GetWindowLongPtrW(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE));
        SetWindowPos(_hwnd, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
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
            Log($"window placed; foreground is {(GetForegroundWindow() == _hwnd && _hwnd != 0 ? "THIS WINDOW (it took focus)" : "another window")}");
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
                                    || message.StartsWith("[GpuRecorder]") || message.StartsWith("[AppCamera]") || message.StartsWith("[PROBE]")))
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
    static (int Width, int Height) _lastPreviewSize;
    static readonly List<double> PreviewTimes = new();

    static async Task RunAsync(Window window, string outDir)
    {
        var code = 1;
        var result = new StringBuilder();
        try
        {
            var mode = Arg("--auto-run");
            var cam = await FindCamera(window);
            if (mode == "mock")
                await OnMain(() => cam.MockSource = MockImage); // before the page switches the camera on
            cam.NewPreviewSet += (_, source) =>
            {
                _lastPreviewSize = (source.Image?.Width ?? 0, source.Image?.Height ?? 0);
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
            cam.StateChanged += (_, st) => Log($"State {st}{(MainThread.IsMainThread ? " (UI thread)" : $" (thread {Environment.CurrentManagedThreadId})")}");
            var progressLogged = false;
            cam.RecordingProgress += (_, d) =>
            {
                if (progressLogged && d > TimeSpan.FromSeconds(0.5))
                    return;
                progressLogged = true;
                Log($"RecordingProgress {d.TotalMilliseconds:0} ms (thread {Environment.CurrentManagedThreadId}{(MainThread.IsMainThread ? ", UI" : "")})");
            };
            cam.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SkiaCamera.IsRecording) or nameof(SkiaCamera.IsPreRecording))
                {
                    progressLogged = false;
                    Log($"{e.PropertyName} = {(e.PropertyName == nameof(SkiaCamera.IsRecording) ? cam.IsRecording : cam.IsPreRecording)} (thread {Environment.CurrentManagedThreadId}{(MainThread.IsMainThread ? ", UI" : "")})");
                }
            };

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
                "preview" => await Preview(cam, result),
                "restart-test" => await RestartTest(cam, result),
                "photo" => await Photo(cam, result),
                "dispose-test" => await DisposeTest(cam, result),
                "ui-record" => await UiRecord(window, cam, outDir, result),
                "mock" => await Mock(cam, result),
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
            if (Arg("--mirror-refresh") is { } refresh)
                cam.MirrorRecordingToPreview = refresh.Equals("on", StringComparison.OrdinalIgnoreCase);
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
        cam.MeasurePaint = true; // slow paints are logged with their time
        var rawRgba = Arg("--raw-rgba") is { } rawRecordSize ? new RawRgbaProbe(rawRecordSize, cam, false, Arg("--raw-rgba-identity") != null) : null;
        if (rawRgba != null)
            cam.RawFrameProbe = rawRgba.OnFrame; // during a recording the hook fires from the recording loop

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
        Log("StartVideoRecording called");
        await Task.Run(() => cam.StartVideoRecording());
        Log($"recording {(pre > 0 ? "live " : "")}for {seconds} s, IsRecording={cam.IsRecording}");
        var cost = await RecordingCost.StartAsync(cam);
        var restartAt = double.Parse(Arg("--restart-during") ?? "-1", CultureInfo.InvariantCulture);
        if (restartAt >= 0 && restartAt < seconds)
        {
            // a setting that makes the camera set itself up again while the recording runs
            await Task.Delay(TimeSpan.FromSeconds(restartAt));
            var kind = Arg("--restart-kind") ?? "quality";
            var formats = kind == "format" ? await MainThread.InvokeOnMainThreadAsync(() => cam.GetAvailableVideoFormatsAsync()) : null;
            await OnMain(() =>
            {
                switch (kind)
                {
                    case "mode":
                        cam.CaptureMode = CaptureModeType.Still;
                        break;
                    case "format":
                        var current = cam.NativeControl?.GetCurrentVideoFormat();
                        var other = formats?.FirstOrDefault(f => f.Width != current?.Width && f.FrameRate >= 25);
                        if (other != null)
                        {
                            cam.VideoQuality = VideoQuality.Manual;
                            cam.VideoFormatIndex = other.Index;
                        }
                        break;
                    default:
                        cam.PhotoQuality = cam.PhotoQuality == CaptureQuality.High ? CaptureQuality.Medium : CaptureQuality.High;
                        break;
                }
            });
            Log($"camera restart requested during the recording ({kind}): {Describe(cam)}");
            await Task.Delay(TimeSpan.FromSeconds(seconds - restartAt));
        }
        else if (Arg("--check-mirror") != null && Arg("--stamp") != null)
        {
            // the preview must show the recording frames: their burnt-in counter (drawn by ProcessFrame only) is read
            // back from what the preview displays, twice
            var readings = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds / 3));
                var index = i;
                readings.Add(await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    using var shown = cam.Display?.LoadedSource?.Clone();
                    if (shown?.Image is { } image)
                    {
                        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                        File.WriteAllBytes(Path.Combine(outDir, $"preview-during-recording-{index}.png"), data.ToArray());
                    }
                    return ReadStamp(shown?.Image);
                }));
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds / 3));
            Log($"preview shows recording stamps: {string.Join(", ", readings)}");
            result.Append($" previewStamps={string.Join("/", readings)}");
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        var pace = Pace(paceFrom, Clock.Elapsed.TotalMilliseconds);
        var costText = cost.Finish(cam);
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
        result.Append(" " + costText);
        if (rawRgba != null)
        {
            cam.RawFrameProbe = null;
            result.Append(" rawRgba=" + rawRgba);
        }
        return abort || copy != null ? 0 : 1;
    }

    static async Task<int> Preview(AppCamera cam, StringBuilder result)
    {
        var seconds = double.Parse(Arg("--seconds") ?? "10", CultureInfo.InvariantCulture);
        var rgba = Arg("--raw-rgba") is { } rawSize ? new RawRgbaProbe(rawSize, cam, Arg("--raw-rgba-check") != null, Arg("--raw-rgba-identity") != null) : null;
        if (rgba != null)
            cam.RawFrameProbe = rgba.OnFrame;
        cam.MeasurePaint = true;
        cam.TakePaintTiming();
        TakeCameraTiming(cam);
        var uiThreadId = await MainThread.InvokeOnMainThreadAsync(GetCurrentThreadId);
        var process = Process.GetCurrentProcess();
        var uiCpu0 = ThreadCpu(process, uiThreadId);
        var cpu0 = process.TotalProcessorTime;
        var frames0 = Interlocked.Read(ref _previewFrames);
        var from = Clock.Elapsed.TotalMilliseconds;
        var ui = UiHeartbeat(TimeSpan.FromSeconds(seconds));
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var paint = cam.TakePaintTiming();
        var camera = TakeCameraTiming(cam);
        process.Refresh();
        var uiCpu = ThreadCpu(process, uiThreadId) - uiCpu0;
        var cpu = process.TotalProcessorTime - cpu0;
        var frames = Math.Max(1, Interlocked.Read(ref _previewFrames) - frames0);
        var uiThread = await ui;
        // uiCpu: the UI thread's CPU time (paint, texture uploads, flush, everything else of the app) per second and per frame;
        // processCpu: all threads of the process per second
        result.Append(string.Create(CultureInfo.InvariantCulture,
            $" mode=preview preview={Pace(from, Clock.Elapsed.TotalMilliseconds)} uiThread={uiThread} cameraPaintMs=avg{paint.Average:0.00},max{paint.Max:0.00},n{paint.Paints} cameraThreadMs={camera}" +
            $" uiCpuMs=perSec{uiCpu.TotalMilliseconds / seconds:0.0},perFrame{uiCpu.TotalMilliseconds / frames:0.00} processCpuMsPerSec={cpu.TotalMilliseconds / seconds:0.0}"));
        if (rgba != null)
        {
            cam.RawFrameProbe = null;
            result.Append(" rawRgba=" + rgba);
            // how the library served them (internal diagnostics of the Windows GPU path)
            if (cam.NativeControl?.GetType().GetProperty("RgbaCounts", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cam.NativeControl) is ValueTuple<long, long> counts)
                result.Append($" rgbaServed=prepared{counts.Item1},scaledInCallback{counts.Item2}");
        }

        // a camera frame as the library hands it to a consumer off the UI thread
        // (on the raster path the UI usually takes each frame first, so this may need a few tries)
        async Task<string> Snapshot(string file)
        {
            using var image = await Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    if (cam.NativeControl?.GetPreviewImage() is { } frame)
                        return frame;
                    await Task.Delay(5);
                }
                return null;
            });
            if (image == null)
                throw new Exception("GetPreviewImage returned no frame off the UI thread");
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(file, data.ToArray());
            return $"\"{file}\" {image.Width}x{image.Height}";
        }

        if (Arg("--snapshot") is { } snapshot)
            result.Append($" snapshot={await Snapshot(snapshot)}");

        if (Arg("--snapshot-ab") is { } folder)
        {
            // the same scene through both paths within seconds: GPU, CPU, GPU again (the two GPU frames give the noise floor)
            var property = cam.GetType().GetProperty("UseGpuProcessing");
            result.Append($" ab-gpu1={await Snapshot(Path.Combine(folder, "ab-gpu1.png"))}");
            await OnMain(() => property.SetValue(cam, false));
            if (!await Settle(cam, 20))
                throw new Exception("camera did not settle on the CPU path");
            result.Append($" ab-cpu={await Snapshot(Path.Combine(folder, "ab-cpu.png"))}");
            await OnMain(() => property.SetValue(cam, true));
            if (!await Settle(cam, 20))
                throw new Exception("camera did not settle on the GPU path");
            result.Append($" ab-gpu2={await Snapshot(Path.Combine(folder, "ab-gpu2.png"))}");
        }
        return 0;
    }

    /// <summary>
    /// Still photos (--repeat N): each one must arrive through CaptureSuccess with pixels that are not black, and the preview
    /// must come back after it.
    /// </summary>
    static async Task<int> Photo(AppCamera cam, StringBuilder result)
    {
        var repeat = int.Parse(Arg("--repeat") ?? "3");
        var fails = 0;
        var sizes = new List<string>();
        for (var i = 0; i < repeat; i++)
        {
            var done = new TaskCompletionSource<CapturedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CapturedImage> onSuccess = (_, image) => done.TrySetResult(image);
            EventHandler<Exception> onFailed = (_, e) => done.TrySetException(e);
            cam.CaptureSuccess += onSuccess;
            cam.CaptureFailed += onFailed;
            try
            {
                var t = Stopwatch.StartNew();
                await Task.Run(() => cam.TakePicture());
                var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
                if (finished != done.Task)
                    throw new Exception("no CaptureSuccess within 20 s");
                var captured = await done.Task;
                var mean = MeanLuma(captured.Image);
                if (cam.VideoEffect != ShaderEffect.None || cam.CustomShaderPath != null)
                {
                    // the shader baked into the photo, as the sample's own OnCaptureSuccess does
                    var baked = await cam.RenderCapturedPhotoAsync(captured, overlay: null, configureImage: image =>
                        image.VisualEffects.Add(cam.CustomShaderPath != null
                            ? new SkiaShaderEffect { ShaderCode = File.ReadAllText(cam.CustomShaderPath) }
                            : new SkiaShaderEffect { ShaderSource = ShaderEffectHelper.GetFilename(cam.VideoEffect) }),
                        useGpu: true);
                    captured.Image.Dispose();
                    captured.Image = baked;
                    mean = MeanLuma(captured.Image);
                }
                Log(string.Create(CultureInfo.InvariantCulture, $"photo {i + 1}: {captured.Image?.Width}x{captured.Image?.Height} meanLuma={mean:0.0} in {t.ElapsedMilliseconds} ms"));
                sizes.Add($"{captured.Image?.Width}x{captured.Image?.Height}");
                if (captured.Image == null || mean < 2)
                    fails++;
                captured.Dispose();
            }
            catch (Exception e)
            {
                fails++;
                Log($"FAIL photo {i + 1}: {e.Message}");
            }
            finally
            {
                cam.CaptureSuccess -= onSuccess;
                cam.CaptureFailed -= onFailed;
            }
            if (!await Settle(cam, 20))
            {
                fails++;
                Log($"FAIL preview did not come back after photo {i + 1}");
            }
        }
        result.Append($" mode=photo photos={repeat} sizes={string.Join(",", sizes.Distinct())} failures={fails}");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// MockSource test image, 900x1200 (a size no webcam delivers): top half red, bottom half blue, so orientation shows.
    /// </summary>
    static readonly SKImage MockImage = CreateMockImage();

    static SKImage CreateMockImage()
    {
        using var surface = SKSurface.Create(new SKImageInfo(900, 1200));
        surface.Canvas.Clear(SKColors.Blue);
        using var red = new SKPaint { Color = SKColors.Red };
        surface.Canvas.DrawRect(0, 0, 900, 600, red);
        return surface.Snapshot();
    }

    static bool IsRed(byte r, byte g, byte b) => r > 200 && g < 60 && b < 60;
    static bool IsBlue(byte r, byte g, byte b) => b > 200 && r < 60 && g < 60;

    /// <summary>
    /// MockSource: the page switches the camera on with the mock already set. Frames must arrive at about 30 fps, upright,
    /// through NewPreviewSet, ProcessPreview and the raw-frame hook, with the hardware never opened. TakePicture returns the
    /// image, rendering and saving it works, video recording is refused, zoom and facing changes keep the mock, IsOn
    /// off/on works. Clearing it opens the camera; setting it again while the camera runs closes the camera and State stays On.
    /// </summary>
    static async Task<int> Mock(AppCamera cam, StringBuilder result)
    {
        var fails = new List<string>();
        void Check(bool ok, string what)
        {
            Log($"{(ok ? "ok" : "FAIL")} {what}");
            if (!ok)
                fails.Add(what);
        }

        var media = cam.NativeControl?.GetType().GetField("_mediaCapture", BindingFlags.NonPublic | BindingFlags.Instance);
        bool HardwareOpen() => media?.GetValue(cam.NativeControl) != null;
        bool MockFrames() => _lastPreviewSize == (MockImage.Width, MockImage.Height);

        long processed = 0, rawOk = 0, rawUpright = 0;
        var original = cam.ProcessPreview;
        cam.ProcessPreview = frame =>
        {
            Interlocked.Increment(ref processed);
            original?.Invoke(frame);
        };
        var rgba = new byte[48 * 64 * 4];
        cam.RawFrameProbe = frame =>
        {
            if (!frame.TryGetRgba(48, 64, rgba))
                return;
            rawOk++;
            int top = (4 * 48 + 24) * 4, bottom = (60 * 48 + 24) * 4;
            if (IsRed(rgba[top], rgba[top + 1], rgba[top + 2]) && IsBlue(rgba[bottom], rgba[bottom + 1], rgba[bottom + 2]))
                rawUpright++;
        };

        // frames from the start, hardware never opened
        var frames0 = Interlocked.Read(ref _previewFrames);
        await Task.Delay(3000);
        var fps = (Interlocked.Read(ref _previewFrames) - frames0) / 3.0;
        Check(fps is > 20 and < 40, string.Create(CultureInfo.InvariantCulture, $"mock frames at {fps:0.0} fps"));
        Check(MockFrames(), $"preview frame {_lastPreviewSize} is the mock image");
        Check(processed > 0, $"ProcessPreview called {processed} times");
        Check(rawOk > 0 && rawUpright == rawOk, $"raw frames TryGetRgba ok {rawOk}, upright {rawUpright}");
        Check(cam.NativeControl != null && !HardwareOpen(), "hardware camera not opened");
        Check(Math.Abs(cam.PreviewScale - 1f) < 0.001f, string.Create(CultureInfo.InvariantCulture, $"PreviewScale {cam.PreviewScale:0.000}"));

        // photo: the image itself, then the sample's still pipeline
        var done = new TaskCompletionSource<CapturedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CapturedImage> onSuccess = (_, image) => done.TrySetResult(image);
        EventHandler<Exception> onFailed = (_, e) => done.TrySetException(e);
        cam.CaptureSuccess += onSuccess;
        cam.CaptureFailed += onFailed;
        try
        {
            await Task.Run(() => cam.TakePicture());
            if (await Task.WhenAny(done.Task, Task.Delay(10000)) != done.Task)
                throw new Exception("no CaptureSuccess within 10 s");
            var captured = await done.Task;
            using (var pixels = SKBitmap.FromImage(captured.Image))
            {
                var top = pixels.GetPixel(450, 300);
                var bottom = pixels.GetPixel(450, 900);
                Check(pixels.Width == 900 && pixels.Height == 1200 && IsRed(top.Red, top.Green, top.Blue) && IsBlue(bottom.Red, bottom.Green, bottom.Blue),
                    $"photo {pixels.Width}x{pixels.Height} is the mock image, upright");
            }
            Check(captured.Meta?.Orientation == 1 && !string.IsNullOrEmpty(captured.Meta.Software) && captured.Rotation == 0,
                $"photo meta orientation={captured.Meta?.Orientation} software=\"{captured.Meta?.Software}\" rotation={captured.Rotation}");
            captured.Meta.Software = "SkiaCamera mock test";

            var rendered = await cam.RenderCapturedPhotoAsync(captured, overlay: null, drawOverlay: cam.ProcessFrame, useGpu: true);
            Check(rendered is { Width: 900, Height: 1200 }, $"RenderCapturedPhotoAsync {rendered?.Width}x{rendered?.Height}");
            captured.Image.Dispose();
            captured.Image = rendered;

            var path = await cam.SaveToGalleryAsync(captured, "SkiaCameraMockTest");
            Check(path != null && File.Exists(path), $"SaveToGalleryAsync \"{path}\"");
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
                var folder = Path.GetDirectoryName(path);
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            captured.Dispose();
        }
        catch (Exception e)
        {
            Check(false, "photo: " + e.Message);
        }
        finally
        {
            cam.CaptureSuccess -= onSuccess;
            cam.CaptureFailed -= onFailed;
        }

        // video is refused
        Exception thrown = null, reported = null;
        EventHandler<Exception> onRecordingFailed = (_, e) => reported = e;
        cam.RecordingFailed += onRecordingFailed;
        await OnMain(() => cam.CaptureMode = CaptureModeType.Video);
        try
        {
            await Task.Run(() => cam.StartVideoRecording());
        }
        catch (Exception e)
        {
            thrown = e;
        }
        cam.RecordingFailed -= onRecordingFailed;
        Check(thrown is NotSupportedException && reported is NotSupportedException && !cam.IsRecording && !cam.IsPreRecording,
            $"StartVideoRecording refused: thrown={thrown?.GetType().Name} RecordingFailed={reported?.GetType().Name}");
        await OnMain(() => cam.CaptureMode = CaptureModeType.Still);
        Check(await Settle(cam, 10) && MockFrames(), "frames after the capture mode changes");

        // zoom and facing
        await OnMain(() => cam.Zoom = 2);
        Check(await WaitFrames(10, 3) && MockFrames(), "frames after Zoom = 2");
        await OnMain(() =>
        {
            cam.Zoom = 1;
            cam.Facing = CameraPosition.Selfie;
        });
        Check(await Settle(cam, 10) && MockFrames() && !HardwareOpen(), "Facing = Selfie keeps the mock");
        await OnMain(() => cam.Facing = CameraPosition.Default);
        Check(await Settle(cam, 10) && MockFrames() && !HardwareOpen(), "Facing = Default keeps the mock");

        // IsOn off and on
        await OnMain(() => cam.IsOn = false);
        await Task.Delay(1000);
        Check(cam.State == HardwareState.Off, $"IsOn = false: State {cam.State}");
        await OnMain(() => cam.IsOn = true);
        Check(await Settle(cam, 10) && MockFrames() && !HardwareOpen(), "IsOn = true starts the mock again");

        // to the hardware and back while running
        await OnMain(() => cam.MockSource = null);
        Check(await Settle(cam, 20) && !MockFrames() && HardwareOpen(), $"MockSource = null opens the camera: frames {_lastPreviewSize}");
        await OnMain(() => cam.MockSource = MockImage);
        Check(await Settle(cam, 20) && MockFrames(), "MockSource set while the camera runs: mock frames");
        await Task.Delay(3000); // late state reports of the stopping hardware
        Check(cam.State == HardwareState.On && await WaitFrames(30, 3) && MockFrames(), $"State stays On after the hardware stopped: {cam.State}");
        Check(!HardwareOpen(), "hardware camera released");
        Check(Math.Abs(cam.PreviewScale - 1f) < 0.001f, string.Create(CultureInfo.InvariantCulture, $"PreviewScale after the switch {cam.PreviewScale:0.000}"));

        cam.ProcessPreview = original;
        cam.RawFrameProbe = null;
        result.Append(string.Create(CultureInfo.InvariantCulture, $" mode=mock fps={fps:0.0} failures={fails.Count}"));
        if (fails.Count > 0)
            result.Append($" failed=\"{string.Join("; ", fails)}\"");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// A recording as the record button makes it: the page's ToggleVideoRecording (Start and Stop on the UI thread), then
    /// the page's own OnVideoRecordingSuccess (gallery move, thumbnail, what a thumbnail tap would open). Passes when the
    /// page ends up with a saved video that the thumbnail opens. The file is moved from the gallery into the run folder.
    /// </summary>
    static async Task<int> UiRecord(Window window, AppCamera cam, string outDir, StringBuilder result)
    {
        var seconds = double.Parse(Arg("--seconds") ?? "5", CultureInfo.InvariantCulture);
        var page = window.Page;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var toggle = page.GetType().GetMethod("ToggleVideoRecording", flags) ?? throw new Exception("page has no ToggleVideoRecording");
        object Field(string name) => page.GetType().GetField(name, flags)?.GetValue(page);

        var before = Field("_lastSavedVideoPath") as string;
        var uiGaps = UiHeartbeat(TimeSpan.FromSeconds(seconds + 4));
        Log("record button: start");
        toggle.Invoke(page, null);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Log($"record button: stop (IsRecording={cam.IsRecording})");
        toggle.Invoke(page, null);

        string saved = null;
        for (var i = 0; i < 300 && (saved == null || saved == before); i++)
        {
            await Task.Delay(100);
            saved = Field("_lastSavedVideoPath") as string;
        }
        await Task.Delay(500); // the thumbnail is set right after the path
        Log($"page: _lastSavedVideoPath before \"{before}\" now \"{saved}\" exists={saved != null && File.Exists(saved)}");
        var isVideo = Field("_lastMediaWasVideo") is true;
        // the thumbnail's own image belongs to the canvas (it may be uploaded and freed at any time): read a copy on the UI thread
        var (thumbSize, thumbLuma) = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var source = (Field("_previewThumbnail") as SkiaImage)?.LoadedSource;
            if (source?.Image is not { Handle: not 0 } image)
                return ("none", -1.0);
            using var copy = source.Clone();
            return ($"{image.Width}x{image.Height}", MeanLuma(copy?.Image));
        });
        var ui = await uiGaps;
        string moved = null;
        if (saved != null && saved != before && File.Exists(saved))
        {
            moved = Path.Combine(outDir, Path.GetFileName(saved));
            File.Move(saved, moved, true); // out of the user's gallery
        }
        result.Append(string.Create(CultureInfo.InvariantCulture,
            $" mode=ui-record saved={(moved != null ? "yes" : "no")} thumbnailOpensVideo={isVideo} thumbnail={thumbSize} thumbLuma={thumbLuma:0.0} uiThread={ui} file=\"{moved}\""));
        return moved != null && isVideo && thumbLuma > 2 ? 0 : 1;
    }

    /// <summary>
    /// Disposes the camera control while a start runs (--dispose-during start: a capture-mode restart, then the dispose
    /// after a random 0-300 ms) or 2 s into a recording (--dispose-during recording). Passes when the process survives,
    /// no error is raised and the camera device is free afterwards (it can be opened exclusively).
    /// </summary>
    static async Task<int> DisposeTest(AppCamera cam, StringBuilder result)
    {
        var during = Arg("--dispose-during") ?? "start";
        var errors = 0;
        cam.OnError += (_, _) => Interlocked.Increment(ref errors);
        var deviceIds = (await MainThread.InvokeOnMainThreadAsync(() => cam.GetAvailableCamerasAsync()))?.Select(c => c.Id).ToList() ?? [];
        if (during == "recording")
        {
            await Task.Run(() => cam.StartVideoRecording());
            Log($"recording, IsRecording={cam.IsRecording}");
            await Task.Delay(2000);
        }
        else
        {
            await OnMain(() => cam.CaptureMode = cam.CaptureMode == CaptureModeType.Still ? CaptureModeType.Video : CaptureModeType.Still);
            await Task.Delay(500 + new Random(int.Parse(Arg("--seed") ?? "1")).Next(0, 300)); // the debounce is 500 ms
        }
        Log($"disposing the camera control during {during}");
        await OnMain(() =>
        {
            cam.Parent?.RemoveSubView(cam);
            cam.Dispose();
        });
        await Task.Delay(3000);

        // every camera must be free: an exclusive open succeeds only when nobody holds the device
        var free = deviceIds.Count > 0 ? "yes" : "no:no camera listed";
        foreach (var id in deviceIds)
        {
            try
            {
                using var probe = new Windows.Media.Capture.MediaCapture();
                await probe.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
                {
                    VideoDeviceId = id,
                    StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Video,
                    SharingMode = Windows.Media.Capture.MediaCaptureSharingMode.ExclusiveControl,
                });
            }
            catch (Exception e)
            {
                free = "no:" + e.Message.Split('\n')[0].Trim();
            }
        }
        var leftovers = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CaptureVideo_*.mp4")
            .Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddMinutes(-2)).ToList();
        foreach (var file in leftovers)
        {
            // a recording cut by the dispose: keep it in the run folder for the verifier, never in the user's Documents
            var target = Path.Combine(Arg("--out") ?? Path.GetTempPath(), Path.GetFileName(file));
            File.Move(file, target, true);
            Log($"recording file left by the dispose: {target} ({new FileInfo(target).Length} bytes)");
        }
        result.Append($" mode=dispose-test during={during} errors={errors} deviceFree={free} files={leftovers.Count}");
        return errors == 0 && free == "yes" ? 0 : 1;
    }

    /// <summary>
    /// The 20-bit counter --stamp burns into the top row of recording frames, read from an image ("none" when the
    /// pattern is not there: not a recording frame).
    /// </summary>
    static string ReadStamp(SKImage image)
    {
        if (image == null)
            return "none";
        var raster = image.IsTextureBacked ? image.ToRasterImage() : image;
        try
        {
            using var bitmap = SKBitmap.FromImage(raster);
            var b = bitmap.Width / 40f;
            long value = 0;
            for (var i = 0; i < 20; i++)
            {
                // the bottom copy of the counter: the sample's overlay dims the top one
                var c = bitmap.GetPixel((int)(b + i * 1.5f * b + b / 2), (int)(bitmap.Height - b));
                var luma = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                if (luma > 64 && luma < 192)
                    return "none"; // mid-grey: camera content, not a stamp cell
                if (luma >= 192)
                    value |= 1L << i;
            }
            return value.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            if (!ReferenceEquals(raster, image))
                raster.Dispose();
        }
    }

    /// <summary>
    /// Mean luma of an image on a coarse grid (-1 without pixels).
    /// </summary>
    static double MeanLuma(SKImage image)
    {
        if (image == null)
            return -1;
        // ToRasterImage hands back the caller's own object for a raster image: disposing that would free the caller's image
        var raster = image.IsTextureBacked ? image.ToRasterImage() : image;
        try
        {
            using var bitmap = SKBitmap.FromImage(raster);
            return MeanLuma(bitmap);
        }
        finally
        {
            if (!ReferenceEquals(raster, image))
                raster.Dispose();
        }
    }

    static double MeanLuma(SKBitmap bitmap)
    {
        double sum = 0;
        var n = 0;
        for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 64))
        for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 64))
        {
            var c = bitmap.GetPixel(x, y);
            sum += 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            n++;
        }
        return n > 0 ? sum / n : -1;
    }

    /// <summary>
    /// Capture-mode toggles at random intervals, each settle checked for frames within 2 s and state On.
    /// </summary>
    static async Task<int> RestartTest(AppCamera cam, StringBuilder result)
    {
        var rnd = new Random(int.Parse(Arg("--seed") ?? "1"));
        var count = int.Parse(Arg("--toggles") ?? "50");
        int checks = 0, fails = 0;
        cam.MeasurePaint = true; // slow paints are logged with their time
        var stopHeartbeat = new CancellationTokenSource();
        var ui = UiHeartbeat(TimeSpan.FromHours(1), stopHeartbeat.Token);
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
        stopHeartbeat.Cancel();
        result.Append($" mode=restart-test toggles={count} settle-checks={checks} failures={fails} uiThread={await ui}");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// What a recording costs while it runs: UI-thread CPU per preview frame, the GPU recorder thread's CPU per second,
    /// the process CPU per second, and the recorder's GPU time per recording frame (timestamp queries on its device).
    /// </summary>
    sealed class RecordingCost
    {
        uint _uiThread, _recorderThread;
        TimeSpan _ui0, _recorder0, _process0;
        long _frames0;
        double _t0;
        object _recorder;

        public static async Task<RecordingCost> StartAsync(SkiaCamera cam)
        {
            var cost = new RecordingCost { _uiThread = await MainThread.InvokeOnMainThreadAsync(GetCurrentThreadId) };
            cost._recorder = typeof(SkiaCamera).GetField("_gpuRecorder", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cam);
            cost._recorderThread = cost._recorder?.GetType().GetProperty("NativeThreadId")?.GetValue(cost._recorder) is uint id ? id : 0;
            cost._recorder?.GetType().GetMethod("TakeGpuTiming")?.Invoke(cost._recorder, null); // reset
            using var process = Process.GetCurrentProcess();
            cost._ui0 = ThreadCpu(process, cost._uiThread);
            cost._recorder0 = cost._recorderThread != 0 ? ThreadCpu(process, cost._recorderThread) : TimeSpan.Zero;
            cost._process0 = process.TotalProcessorTime;
            cost._frames0 = Interlocked.Read(ref _previewFrames);
            cost._t0 = Clock.Elapsed.TotalSeconds;
            return cost;
        }

        public string Finish(SkiaCamera cam)
        {
            using var process = Process.GetCurrentProcess();
            var seconds = Math.Max(0.001, Clock.Elapsed.TotalSeconds - _t0);
            var frames = Math.Max(1, Interlocked.Read(ref _previewFrames) - _frames0);
            var ui = ThreadCpu(process, _uiThread) - _ui0;
            var recorder = _recorderThread != 0 ? ThreadCpu(process, _recorderThread) - _recorder0 : TimeSpan.Zero;
            var cpu = process.TotalProcessorTime - _process0;
            var gpu = _recorder?.GetType().GetMethod("TakeGpuTiming")?.Invoke(_recorder, null) is ValueTuple<double, double, long> g ? g : default;
            return string.Create(CultureInfo.InvariantCulture,
                $"cost=uiCpuMsPerPreviewFrame{ui.TotalMilliseconds / frames:0.00},recorderCpuMsPerSec{recorder.TotalMilliseconds / seconds:0.0},processCpuMsPerSec{cpu.TotalMilliseconds / seconds:0.0},recorderGpuMsPerFrame{gpu.Item1:0.00}(max{gpu.Item2:0.00},n{gpu.Item3})");
        }
    }

    /// <summary>
    /// --raw-rgba 224x224: RawCameraFrame.TryGetRgba on every 5th raw frame, inside the callback as the contract requires;
    /// reports successes, time per call, the calling thread and the mean luma of the bytes.
    /// With --raw-rgba-check the same frame is also scaled through the camera's Skia path from a raster copy (the old
    /// path, the reference) and the difference is reported per case: display orientation, the three extra rotations of
    /// OutputOrientation.Portrait, and a centre crop of 0.6 (each case is called the same way on both sides).
    /// </summary>
    sealed class RawRgbaProbe(string size, AppCamera cam, bool check, bool identity = false)
    {
        long _idChecks, _idMatches, _idMismatches, _idUnreadable;
        string _idFirstMismatch;
        readonly int _w = int.Parse(size.Split('x')[0]), _h = int.Parse(size.Split('x')[1]);
        readonly byte[] _buffer = new byte[int.Parse(size.Split('x')[0]) * int.Parse(size.Split('x')[1]) * 4];
        readonly byte[] _reference = new byte[int.Parse(size.Split('x')[0]) * int.Parse(size.Split('x')[1]) * 4];
        long _frames, _calls, _ok;
        double _ms, _maxMs, _luma;
        bool _onUi, _offUi;
        static readonly MethodInfo Internal = typeof(SkiaCamera).GetMethod("TryGetRgbaInternal", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly (string Name, int DisplayRotation, float Crop, bool Portrait)[] Cases =
            [("display", 0, 1f, false), ("rot270", 90, 1f, true), ("rot180", 180, 1f, true), ("rot90", 270, 1f, true), ("crop0.6", 0, 0.6f, false)];
        readonly Dictionary<string, (double Sum, long Count, int Max, long Over8, long Values)> _errors = new();
        long _checks;

        public void OnFrame(RawCameraFrame frame)
        {
            if (_frames++ % 5 != 0)
                return;
            var start = Stopwatch.GetTimestamp();
            var ok = frame.TryGetRgba(_w, _h, _buffer);
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _calls++;
            _ms += ms;
            _maxMs = Math.Max(_maxMs, ms);
            if (MainThread.IsMainThread) _onUi = true; else _offUi = true;
            if (!ok)
                return;
            _ok++;
            double sum = 0;
            for (var i = 0; i < _buffer.Length; i += 4 * 7)
                sum += 0.299 * _buffer[i] + 0.587 * _buffer[i + 1] + 0.114 * _buffer[i + 2];
            _luma = sum / (_buffer.Length / (4 * 7));

            if (identity && frame.RawImage != null)
                CheckIdentity(frame.RawImage);
            if (!check || frame.RawImage == null || _checks++ % 3 != 0)
                return;
            // the same frame through the old path: a raster copy never takes the GPU shortcut
            using var raster = frame.RawImage.IsTextureBacked ? frame.RawImage.ToRasterImage() : null;
            var reference = raster ?? frame.RawImage;
            var c = Cases[_checks / 3 % Cases.Length];
            var orientation = c.Portrait ? OutputOrientation.Portrait : OutputOrientation.Display;
            var gpuOk = (bool)Internal.Invoke(cam, [frame.RawImage, _w, _h, _buffer, orientation, c.Crop, c.DisplayRotation]);
            var refOk = (bool)Internal.Invoke(cam, [reference, _w, _h, _reference, orientation, c.Crop, c.DisplayRotation]);
            if (!gpuOk || !refOk)
                return;
            var e = _errors.GetValueOrDefault(c.Name);
            for (var i = 0; i < _buffer.Length; i += 4)
            {
                for (var k = 0; k < 3; k++)
                {
                    var d = Math.Abs(_buffer[i + k] - _reference[i + k]);
                    e.Sum += d;
                    e.Values++;
                    if (d > e.Max) e.Max = d;
                    if (d > 8) e.Over8++;
                }
            }
            e.Count++;
            _errors[c.Name] = e;
        }

        /// <summary>
        /// The frame number stamped into the frame by the library's test knob (DRAWNUI_CAMERA_TEST_STAMP=1), read from the
        /// bytes TryGetRgba returned and from the frame the callback got (the one drawn next): they must be the same.
        /// </summary>
        void CheckIdentity(SKImage drawn)
        {
            _idChecks++;
            using var raster = drawn.IsTextureBacked ? drawn.ToRasterImage() : null;
            using var bitmap = SKBitmap.FromImage(raster ?? drawn);
            var srcW = (float)bitmap.Width;
            var srcH = (float)bitmap.Height;
            var fromDrawn = DecodeStamp((x, y) => Luma(bitmap.GetPixel(x, y)), srcW, srcH, new SKRect(0, 0, srcW, srcH), srcW, srcH);
            var crop = CenterCrop(srcW, srcH, _w, _h);
            var fromBytes = DecodeStamp((x, y) =>
            {
                var i = (Math.Clamp(y, 0, _h - 1) * _w + Math.Clamp(x, 0, _w - 1)) * 4;
                return 0.299 * _buffer[i] + 0.587 * _buffer[i + 1] + 0.114 * _buffer[i + 2];
            }, srcW, srcH, crop, _w, _h);
            if (fromDrawn < 0 || fromBytes < 0)
                _idUnreadable++;
            else if (fromDrawn == fromBytes)
                _idMatches++;
            else
            {
                _idMismatches++;
                _idFirstMismatch ??= $"drawn{fromDrawn}-bytes{fromBytes}";
            }
        }

        static double Luma(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;

        static SKRect CenterCrop(float w, float h, int tw, int th)
        {
            float sa = w / h, ta = (float)tw / th;
            if (Math.Abs(sa - ta) < 0.0001f) return new SKRect(0, 0, w, h);
            if (sa > ta) { var cw = h * ta; var l = (w - cw) / 2; return new SKRect(l, 0, l + cw, h); }
            var ch = w / ta; var t = (h - ch) / 2; return new SKRect(0, t, w, t + ch);
        }

        static long DecodeStamp(Func<int, int, double> luma, float srcW, float srcH, SKRect crop, float outW, float outH)
        {
            long value = 0;
            for (var i = 0; i < 20; i++)
            {
                var cx = srcW * 0.3f + (i + 0.5f) * srcW * 0.02f;
                var cy = srcH * 0.05f;
                var l = luma((int)((cx - crop.Left) * outW / crop.Width), (int)((cy - crop.Top) * outH / crop.Height));
                if (l > 180) value |= 1L << i;
                else if (l > 70) return -1; // not a stamp cell
            }
            return value;
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"ok{_ok}/{_calls},avgMs{(_calls > 0 ? _ms / _calls : 0):0.00},maxMs{_maxMs:0.00},thread{(_onUi ? "UI" : "")}{(_offUi ? "Other" : "")},luma{_luma:0.0}") +
            (!identity ? "" : string.Create(CultureInfo.InvariantCulture, $" frameIdentity=checked{_idChecks},same{_idMatches},different{_idMismatches},unreadable{_idUnreadable}{(_idFirstMismatch != null ? $",first:{_idFirstMismatch}" : "")}")) +
            (_errors.Count == 0 ? "" : " rgbaError=" + string.Join(";", _errors.Select(kv => string.Create(CultureInfo.InvariantCulture,
                $"{kv.Key}:frames{kv.Value.Count},mean{kv.Value.Sum / Math.Max(1, kv.Value.Values):0.00},max{kv.Value.Max},over8:{100.0 * kv.Value.Over8 / Math.Max(1, kv.Value.Values):0.00}%"))));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    static TimeSpan ThreadCpu(Process process, uint threadId)
    {
        foreach (ProcessThread thread in process.Threads)
            if (thread.Id == threadId)
                return thread.TotalProcessorTime;
        return TimeSpan.Zero;
    }

    /// <summary>
    /// The library's camera-thread cost per frame (internal diagnostics of the Windows camera), as "avgX,maxY,nZ".
    /// </summary>
    static string TakeCameraTiming(SkiaCamera cam)
    {
        var native = cam.NativeControl;
        var method = native?.GetType().GetMethod("TakeCameraTiming", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method?.Invoke(native, null) is not ValueTuple<double, double, long> t)
            return "unavailable";
        return string.Create(CultureInfo.InvariantCulture, $"avg{t.Item1:0.00},max{t.Item2:0.00},n{t.Item3}");
    }

    /// <summary>
    /// UI-thread responsiveness: posts a tiny job every 10 ms and reports how long the longest one waited.
    /// </summary>
    static async Task<string> UiHeartbeat(TimeSpan duration, CancellationToken stop = default)
    {
        var waits = new List<double>();
        var t = Stopwatch.StartNew();
        while (t.Elapsed < duration && !stop.IsCancellationRequested)
        {
            var posted = Stopwatch.GetTimestamp();
            var at = DateTime.Now;
            await MainThread.InvokeOnMainThreadAsync(() => { });
            var waited = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
            waits.Add(waited);
            if (waited > 50)
                Log(string.Create(CultureInfo.InvariantCulture, $"UI thread blocked {waited:0} ms from {at:HH:mm:ss.fff}"));
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
