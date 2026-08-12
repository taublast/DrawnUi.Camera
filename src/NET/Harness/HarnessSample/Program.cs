global using SkiaSharp;
using System;
using System.IO;
using CameraTests;
using CameraTests.UI;
using DrawnUi.Camera;
using DrawnUi.Infrastructure;

// --- Part 1: SkiaCamera mock backend smoke test (no native platform involved) ---
using (var camera = new TestCamera())
{
    camera.Start(); // headless flavor: IsOn -> PowerChanged -> State transitions synchronously, no permission hop
    Console.WriteLine($"[Camera] State after Start(): {camera.State}");

    bool gotSample = false;
    camera.AudioSampleAvailable += (data, sampleRate, bits, channels) =>
    {
        gotSample = true;
        Console.WriteLine($"[Camera] AudioSampleAvailable: {data.Length} bytes, {sampleRate}Hz, {bits}bit, {channels}ch");
    };

    camera.InjectAudioSample(MakeSineSample(0, 44100, 1, 440, 0.02));
    Console.WriteLine($"[Camera] InjectAudioSample routed through pipeline: {gotSample}");

    var testPattern = MakeTestPatternBitmap(64, 64, 0); // solid (0,128,255)
    camera.InjectFrame(SKImage.FromBitmap(testPattern)); // camera takes ownership, disposes it
    camera.Pump(); // same drive as MAUI: Paint -> SetFrameFromNative -> OnRawFrameAvailable
    Console.WriteLine($"[Camera] ML hook received {(camera.LastRgba != null ? "RGBA buffer" : "nothing")}, first pixel = " +
        $"{(camera.LastRgba != null ? $"({camera.LastRgba[0]},{camera.LastRgba[1]},{camera.LastRgba[2]},{camera.LastRgba[3]})" : "n/a")}" +
        " (expected ~(0,128,255,255) proves the injected frame was actually scaled/read, not just stored)");
    camera.LastRgba = null;
    camera.Pump(); // nothing injected -> hook must not fire
    Console.WriteLine($"[Camera] Pump with nothing injected fired hook: {(camera.LastRgba != null ? "TRUE - BUG" : "false (as expected)")}");

    camera.Stop();
    Console.WriteLine($"[Camera] State after Stop(): {camera.State}");
}

Console.WriteLine();

// --- Part 1b: shaders (filters) applied through the SAME path as the MAUI sample ---
// AppCamera enables UseRealtimeVideoProcessing and overrides RenderPreviewForProcessing to draw the
// frame through a SkiaShader. Here the headless camera runs that exact override on each injected frame
// and we assert every .sksl filter actually transforms the pixels.
var camOutDir = Path.Combine(AppContext.BaseDirectory, "camera-out");
Directory.CreateDirectory(camOutDir);

// Save a real photo as the source once, so filtered output looks like an actual camera frame.
using (var shaderCam = new TestCamera())
{
    shaderCam.UseRealtimeVideoProcessing = true; // same flag AppCamera sets
    shaderCam.Start();

    using (var sourceBmp = MakeSceneBitmap(256, 256))
    {
        using (var srcData = SKImage.FromBitmap(sourceBmp).Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.Create(Path.Combine(camOutDir, "_source.png")))
            srcData.SaveTo(fs);

        var rawPixel = CenterPixel(sourceBmp);

        foreach (var effect in Enum.GetValues<ShaderEffect>())
        {
            shaderCam.SetEffect(effect); // None -> passthrough baseline, others -> shader

            shaderCam.InjectFrame(SKImage.FromBitmap(sourceBmp.Copy()));
            shaderCam.Pump(); // Paint -> SetFrameFromNative -> ApplyPreviewEffects -> RenderPreviewForProcessing(shader)

            var display = shaderCam.LastPreviewFrame; // == what the .NET camera Display shows
            if (display == null)
            {
                Console.WriteLine($"[Shader] {effect,-6}: display NULL - BUG");
                continue;
            }

            var name = $"{(int)effect:00}_{effect}.png";
            using (var data = display.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(Path.Combine(camOutDir, name)))
                data.SaveTo(fs);

            using var outBmp = SKBitmap.FromImage(display);
            var outPixel = CenterPixel(outBmp);
            var applied = effect == ShaderEffect.None || outPixel != rawPixel;
            Console.WriteLine($"[Shader] {ShaderEffectHelper.GetTitle(effect),-5} -> camera-out/{name}  " +
                $"center {rawPixel} -> {outPixel}  {(applied ? "OK" : "NO CHANGE - BUG")}");
        }
    }

    shaderCam.Stop();
    Console.WriteLine($"[Shader] .NET camera display frames saved to: {camOutDir}");
}

Console.WriteLine();

// --- Part 2: drive each audio visualizer directly with synthetic audio, headless ---
// Bypasses SkiaCamera/AppCamera/FrameOverlay wiring entirely - isolates whether the
// visualizer's own DSP + Render() actually produce visible pixels from known input.
const int width = 400, height = 200;
var outDir = Path.Combine(AppContext.BaseDirectory, "viz-out");
Directory.CreateDirectory(outDir);

IAudioVisualizer[] visualizers =
{
    new AudioLevels(), new AudioLevelsPeak(), new AudioLevelsVU(),
    new AudioOscillograph(), new AudioRadialGauge(), new AudioSoundBars(), new AudioWaveformBars()
};

foreach (var viz in visualizers)
{
    var name = viz.GetType().Name;
    using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Black);

    // ~0.5s of a 440Hz sine wave fed in 20ms chunks, like real mic capture would arrive
    const int sampleRate = 44100;
    const int chunkMs = 20;
    for (int i = 0; i < 25; i++)
    {
        viz.AddSample(MakeSineSample(i * chunkMs / 1000.0, sampleRate, 1, 440, chunkMs / 1000.0));
        // Visualizers render on whatever the LATEST swapped buffer is - render each tick like the real app does
        viz.Render(canvas, new SKRect(0, 0, width, height), 1f);
    }

    surface.Flush();
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    var path = Path.Combine(outDir, $"{name}.png");
    using (var fs = File.Create(path))
        data.SaveTo(fs);

    using var bmp = SKBitmap.FromImage(image);
    int sampled = 0, nonBlack = 0;
    for (int y = 0; y < height; y += 4)
        for (int x = 0; x < width; x += 4)
        {
            sampled++;
            var p = bmp.GetPixel(x, y);
            if (p.Red > 10 || p.Green > 10 || p.Blue > 10) nonBlack++;
        }

    Console.WriteLine($"{name,-18} nonBlack={nonBlack}/{sampled}  -> {path}");
    viz.Dispose();
}

static AudioSample MakeSineSample(double startSeconds, int sampleRate, int channels, double freqHz, double durationSeconds)
{
    int samples = (int)(sampleRate * durationSeconds);
    var data = new byte[samples * channels * 2];
    for (int i = 0; i < samples; i++)
    {
        double t = startSeconds + i / (double)sampleRate;
        short v = (short)(Math.Sin(2 * Math.PI * freqHz * t) * short.MaxValue * 0.6);
        for (int c = 0; c < channels; c++)
        {
            int offset = (i * channels + c) * 2;
            data[offset] = (byte)(v & 0xFF);
            data[offset + 1] = (byte)((v >> 8) & 0xFF);
        }
    }

    return new AudioSample
    {
        Data = data,
        SampleRate = sampleRate,
        Channels = channels,
        BitDepth = AudioBitDepth.Pcm16Bit,
        TimestampNs = (long)(startSeconds * 1_000_000_000)
    };
}

static SKBitmap MakeTestPatternBitmap(int width, int height, int frameIndex)
{
    var bmp = new SKBitmap(width, height);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(new SKColor((byte)((frameIndex * 8) % 256), 128, (byte)(255 - (frameIndex * 8) % 256)));
    return bmp;
}

// Recognizable synthetic scene (sky gradient, sun, ground, color bars) so each filter's effect is
// visible to the eye in the saved PNG, not just a pixel-diff number.
static SKBitmap MakeSceneBitmap(int width, int height)
{
    var bmp = new SKBitmap(width, height);
    using var canvas = new SKCanvas(bmp);

    using (var sky = new SKPaint())
    {
        sky.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, height * 0.6f),
            new[] { new SKColor(80, 150, 230), new SKColor(210, 230, 250) },
            null, SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height * 0.6f, sky);
    }

    using (var sun = new SKPaint { Color = new SKColor(255, 220, 80), IsAntialias = true })
        canvas.DrawCircle(width * 0.75f, height * 0.22f, width * 0.1f, sun);

    using (var ground = new SKPaint { Color = new SKColor(70, 160, 70) })
        canvas.DrawRect(0, height * 0.6f, width, height * 0.4f, ground);

    var bars = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White, SKColors.Black };
    float bw = width / (float)bars.Length;
    for (int i = 0; i < bars.Length; i++)
        using (var p = new SKPaint { Color = bars[i] })
            canvas.DrawRect(i * bw, height * 0.78f, bw, height * 0.22f, p);

    return bmp;
}

static SKColor CenterPixel(SKBitmap bmp) => bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

// Proves a harness/app can plug its own frame-processing (ML/AI) logic into the same
// OnRawFrameAvailable hook the real camera drives, using only injected frames.
class TestCamera : SkiaCamera
{
    public byte[] LastRgba;
    private SkiaShader _effectShader;

    // Console harness has no DrawnUi render loop; drive the protected MAUI-parity entry directly.
    public void Pump() => SetFrameFromNative();

    // Same shader-application pattern as the MAUI sample's AppCamera.RenderPreviewForProcessing override.
    // Loads the exact same .sksl by the exact same filename map (ShaderEffectHelper); FromCode == FromResource.
    public void SetEffect(ShaderEffect effect)
    {
        _effectShader?.Dispose();
        _effectShader = null;

        var file = ShaderEffectHelper.GetFilename(effect);
        if (string.IsNullOrWhiteSpace(file))
            return;

        var path = Path.Combine(AppContext.BaseDirectory, file);
        _effectShader = SkiaShader.FromCode(File.ReadAllText(path), onError: e => Console.WriteLine($"[Shader] compile error: {e}"));
    }

    protected override void RenderPreviewForProcessing(SKCanvas canvas, SKImage frame)
    {
        if (_effectShader != null)
            _effectShader.DrawImage(canvas, frame, 0, 0);
        else
            base.RenderPreviewForProcessing(canvas, frame);
    }

    protected override void OnRawFrameAvailable(RawCameraFrame frame)
    {
        var buffer = new byte[32 * 32 * 4];
        LastRgba = frame.TryGetRgba(32, 32, buffer) ? buffer : null;
    }
}
