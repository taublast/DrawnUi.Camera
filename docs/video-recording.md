# Video Recording

SkiaCamera provides comprehensive video recording capabilities with format selection, quality control, and cross-platform support. Video recording follows the same dual-channel architecture as photo capture - the live preview continues uninterrupted while recording video in the background.

## Basic Video Recording

```csharp
if (camera.CanRecordVideo())
{
    await camera.StartVideoRecording();
    await camera.StopVideoRecording();
}

// Abort recording — discard without saving
await camera.StopVideoRecording(true);

camera.RecordingSuccess += OnRecordingSuccess;
camera.RecordingFailed += OnRecordingFailed;
camera.RecordingProgress += OnRecordingProgress;

private async void OnRecordingSuccess(object sender, CapturedVideo video)
{
    var galleryPath = await camera.MoveVideoToGalleryAsync(video, "MyApp");
}

private void OnRecordingProgress(object sender, TimeSpan duration)
{
    RecordingTimeLabel.Text = $"Recording: {duration:mm\\:ss}";
}
```

## Video Quality Presets

```csharp
camera.VideoQuality = VideoQuality.Low;       // 480p 30 fps, smallest files
camera.VideoQuality = VideoQuality.Standard;  // 720p 30 fps, balanced
camera.VideoQuality = VideoQuality.High;      // 1080p: 30 fps on iOS / Mac Catalyst, 60 fps on Android and Windows
camera.VideoQuality = VideoQuality.Ultra;     // 4K if available, up to 60 fps
```

Frame rates are the target: each platform uses the closest rate the camera supports. The frame rate of `High` differs per platform on purpose: iOS keeps the native Camera app's 1080p at 30 fps, Android and Windows use 60 fps.

## Manual Video Format Selection

```csharp
var formats = await camera.GetAvailableVideoFormatsAsync();

var options = formats.Select((format, index) =>
    $"[{index}] {format.Description}"
).ToArray();

var result = await DisplayActionSheet("Select Video Format", "Cancel", null, options);

if (!string.IsNullOrEmpty(result) && result != "Cancel")
{
    var selectedIndex = Array.FindIndex(options, opt => opt == result);
    if (selectedIndex >= 0)
    {
        camera.VideoQuality = VideoQuality.Manual;
        camera.VideoFormatIndex = selectedIndex;
    }
}
```

## Audio & Video Control

SkiaCamera provides **granular control over video preview, recording, and audio** through four independent properties:

```csharp
camera.EnableVideoPreview = true;        // Show video preview (default: true)
camera.EnableVideoRecording = true;      // Record video frames (default: true)
camera.EnableAudioRecording = true;      // Capture audio (default: true)
camera.EnableAudioMonitoring = false;    // Live audio feedback (default: false)
```

### Usage Scenarios

| Scenario | VideoPreview | VideoRecording | AudioMonitoring | AudioRecording | Output |
|----------|:-----------:|:--------------:|:---------------:|:--------------:|--------|
| Full video recording | on | on | on | on | MP4/MOV with A/V |
| Headless video recording | off | on | - | on | MP4/MOV with A/V |
| Silent video recording | on | on | off | off | MP4/MOV video only |
| Preview only | on | off | off | off | None |
| Pure audio-only recorder | off | off | - | on | M4A audio only |
| Audio monitor only | off | off | on | off | None |

### Common Use Cases

```csharp
// 1. Standard video recording with audio
camera.EnableVideoPreview = true;
camera.EnableVideoRecording = true;
camera.EnableAudioRecording = true;
await camera.StartVideoRecording();

// 2. Audio-only recording (no camera initialization)
camera.EnableVideoPreview = false;
camera.EnableVideoRecording = false;
camera.EnableAudioRecording = true;
await camera.StartVideoRecording(); // Records M4A audio file

// 3. Headless video recording (no preview UI, but camera running)
camera.EnableVideoPreview = false;
camera.EnableVideoRecording = true;
camera.EnableAudioRecording = true;
await camera.StartVideoRecording();

// 4. Silent video recording
camera.EnableVideoPreview = true;
camera.EnableVideoRecording = true;
camera.EnableAudioRecording = false;
await camera.StartVideoRecording();

// 5. Audio monitoring only (no recording, no camera) — e.g. pitch detection, BPM, visualizers
// Can be used hidden in the UI tree or from a ViewModel like a service
var audioMonitor = new SkiaCamera
{
    NeedPermissionsSet = NeedPermissions.Microphone, // only mic needed
    EnableVideoPreview = false,
    EnableVideoRecording = false,
    EnableAudioRecording = false,
    EnableAudioMonitoring = true
};
audioMonitor.IsOn = true;
```

### Audio-Only Recording Output

Audio-only recordings produce `.m4a` files. The file path is delivered via `RecordingSuccess`.

| Platform | Destination | Where the user finds it |
|----------|-------------|------------------------|
| **Windows** | `Videos/{album}` | File Explorer > Videos |
| **Android** | `Music/{album}` via MediaStore | Files app > Music |
| **iOS** | App's Documents folder | Files app > On My iPhone > app |

**iOS Info.plist requirement** for Documents folder visibility:

```xml
<key>UIFileSharingEnabled</key>
<true/>
<key>LSSupportsOpeningDocumentsInPlace</key>
<true/>
```

## Capture Video Flow (Real-Time Processing)

Frame-by-frame video recording with real-time processing via `UseRealtimeVideoProcessing`:

```csharp
var camera = new SkiaCamera
{
    UseRealtimeVideoProcessing = true,
    VideoQuality = VideoQuality.High,
    EnableAudioRecording = true,

    ProcessFrame = (frame) =>
    {
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(128),
            TextSize = 48,
            IsAntialias = true
        };

        frame.Canvas.DrawText("LIVE", 50, 100, paint);
        frame.Canvas.DrawText($"{frame.Time:mm\\:ss}", 50, 160, paint);
    }
};

await camera.StartVideoRecording();
await Task.Delay(10000);
await camera.StopVideoRecording();
```

### Multi-Layer Overlay Example

```csharp
camera.ProcessFrame = (frame) =>
{
    var canvas = frame.Canvas;
    var width = frame.Width;
    var height = frame.Height;

    // Semi-transparent bottom bar
    using var rectPaint = new SKPaint
    {
        Color = SKColors.Black.WithAlpha(100),
        Style = SKPaintStyle.Fill
    };
    canvas.DrawRect(new SKRect(0, height - 120, width, height), rectPaint);

    // Recording indicator
    using var circlePaint = new SKPaint { Color = SKColors.Red };
    canvas.DrawCircle(30, height - 80, 10, circlePaint);

    // Timestamp
    using var textPaint = new SKPaint
    {
        Color = SKColors.White,
        TextSize = 36,
        IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
    };
    canvas.DrawText($"REC {frame.Time:hh\\:mm\\:ss}", 60, height - 65, textPaint);

    // Watermark
    if (_watermarkBitmap != null)
    {
        var logoRect = new SKRect(width - 200, 20, width - 20, 100);
        canvas.DrawBitmap(_watermarkBitmap, logoRect);
    }
};
```

### Platform Implementation

| Platform | Encoder | GPU Support | Hardware Encoding |
|----------|---------|-------------|-------------------|
| **Windows** | Media Foundation (H.264/HEVC) | D3D11 textures | Hardware MFT |
| **Android** | MediaCodec (H.264/HEVC) | Surface input | Hardware codec |
| **iOS/macOS** | AVAssetWriter (H.264/HEVC) | Metal textures | Hardware encoder |

### Performance Notes

- GPU-first rendering, zero-copy encoding when possible
- Automatic frame dropping when processing can't keep up
- Single-frame-in-flight policy prevents memory bloat
- Rotation locking during recording for consistent output

## Pre-Recording (Look-Back Capture)

Pre-recording maintains a circular buffer so the saved clip includes seconds *before* the user pressed record.

```csharp
camera.EnablePreRecording = true;
camera.PreRecordDuration = TimeSpan.FromSeconds(5);
```

With pre-recording enabled, `StartVideoRecording()` becomes a **3-state toggle**:

1. **First call** sets `IsPreRecording = true` — the camera fills an in-memory buffer but writes nothing to disk.
2. **Second call** sets `IsRecording = true` — buffered frames are prepended to the file and live recording begins.
3. **`StopVideoRecording()`** finalizes the file. `StopVideoRecording(true)` aborts and discards.

Both `IsPreRecording` and `IsRecording` are bindable for UI state.

If recording is stopped during pre-recording (before the second call), and the buffer contains less than 1 second of footage, the recording is automatically aborted.

See [PreRecording.md](../PreRecording.md) for the full breakdown.

## DrawnUI Overlay on Frames

You can attach a DrawnUI layout tree as an overlay that renders onto both preview and recorded frames:

```csharp
var overlay = new FrameOverlay(); // your SkiaLayout subclass
camera.InitializeOverlayLayouts(overlay);
```

The same overlay instance is reused for both preview and recording. Use `SkiaCacheType.ImageDoubleBuffered` on animated parts so the encoder thread can snapshot the overlay efficiently without stalling on layout work.

## Real-Time Audio Processing

There are two override points for live audio:

- **`OnAudioSampleAvailable(AudioSample)`** — fires when `EnableAudioMonitoring = true`. Use for visualization, speech recognition, or any read/modify before encoding. Return the (possibly modified) sample.
- **`WriteAudioSample(AudioSample)`** — fires during recording with `UseRealtimeVideoProcessing = true`. Use for in-place audio effects on the encoder path.

**Important:** `EnableAudioMonitoring = true` is required for `OnAudioSampleAvailable` to fire. This gates both the audio visualizer pipeline and any speech transcription fed from the same stream.

### OnAudioSampleAvailable (monitoring + preprocessing)

```csharp
public class AppCamera : SkiaCamera
{
    protected override AudioSample OnAudioSampleAvailable(AudioSample sample)
    {
        // Apply gain in place
        if (UseGain)
            AmplifyPcm16(sample.Data, GainFactor);

        // Feed visualizers, speech pipeline, etc.
        OnAudioSample?.Invoke(sample);

        return base.OnAudioSampleAvailable(sample);
    }
}
```

### WriteAudioSample (encoder path)

Requires `UseRealtimeVideoProcessing = true`:

```csharp
public class MyCamera : SkiaCamera 
{
    public MyCamera()
    {
        UseRealtimeVideoProcessing = true;
        EnableAudioRecording = true;
    }
    
    public override void WriteAudioSample(AudioSample sample)
    {
        AdjustVolume(sample.Data, sample.Channels, sample.BitDepth, 0.8f);
        
        var peaks = CalculateAudioPeaks(sample.Data, sample.Channels, sample.BitDepth);
        UpdateOscillographVisualization(peaks);
        
        base.WriteAudioSample(sample); // MUST call base to record
    }
}
```

### AudioSample Structure

| Property | Type | Description |
|----------|------|-------------|
| `Data` | `byte[]` | Raw PCM audio data (modify in-place for effects) |
| `TimestampNs` | `long` | Sample timestamp in nanoseconds |
| `SampleRate` | `int` | Sample rate (e.g., 44100 Hz) |
| `Channels` | `int` | Number of channels (1=mono, 2=stereo) |
| `BitDepth` | `AudioBitDepth` | Bit depth (Int16, Int24, Float32) |
| `SampleCount` | `int` | Number of samples in this chunk |
| `Timestamp` | `TimeSpan` | Converted timestamp as TimeSpan |

### Performance Considerations

- Runs on audio capture thread, arrives every ~23ms at 44.1kHz
- Keep processing under 10ms to avoid buffer overruns
- Avoid allocations in the hot path
- Use locks when sharing data with UI thread

## GPS & Video Metadata

SkiaCamera supports embedding **GPS coordinates** into both **photos** (EXIF) and **videos** (MP4/MOV), plus **rich metadata** for videos (author, camera make/model, software, date).

### Setup

**iOS/macOS** (`Info.plist`):
```xml
<key>NSLocationWhenInUseUsageDescription</key>
<string>To be able to geotag photos and videos</string>
```

**Android** (`AndroidManifest.xml`):
```xml
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
```

**Windows** (`Package.appxmanifest`):
```xml
<Capabilities>
    <DeviceCapability Name="location" />
</Capabilities>
```

### Enable GPS

```csharp
camera.InjectGpsLocation = true;
await camera.RefreshGpsLocation(); // Call on main thread
```

**Important:** You must call `RefreshGpsLocation()` yourself. Save methods only check whether `LocationLat`/`LocationLon` are already set.

### How It Works

1. You call `RefreshGpsLocation()` on the main thread
2. It requests permissions, then fetches coordinates into `LocationLat`/`LocationLon`
3. Save methods (`SaveToGalleryAsync`, `MoveVideoToGalleryAsync`) embed GPS automatically

### Photo Capture with GPS

```csharp
// Automatic
camera.InjectGpsLocation = true;
await camera.RefreshGpsLocation();

camera.CaptureSuccess += async (sender, captured) =>
{
    var path = await camera.SaveToGalleryAsync(captured, "MyAlbum");
};

// Manual coordinates
camera.CaptureSuccess += async (sender, captured) =>
{
    Metadata.ApplyGpsCoordinates(captured.Meta, latitude, longitude);
    var path = await camera.SaveToGalleryAsync(captured, "MyAlbum");
};
```

### Video Capture with GPS

```csharp
// Automatic
camera.InjectGpsLocation = true;
await camera.RefreshGpsLocation();

camera.RecordingSuccess += async (sender, video) =>
{
    var galleryPath = await camera.MoveVideoToGalleryAsync(video, "MyAlbum");
};

// Manual coordinates
camera.RecordingSuccess += async (sender, video) =>
{
    video.Latitude = 34.0522;
    video.Longitude = -118.2437;
    var galleryPath = await camera.MoveVideoToGalleryAsync(video, "MyAlbum");
};

// Custom metadata
camera.RecordingSuccess += async (sender, video) =>
{
    video.Meta = new Metadata
    {
        Software = "MyApp Pro 2.0",
        CameraOwnerName = "John Doe",
        UserComment = "Race lap 3",
        Vendor = "CustomCam",
        Model = "RaceCam X1"
    };
    var galleryPath = await camera.MoveVideoToGalleryAsync(video, "MyAlbum");
};
```

### Direct File Injection (Without Gallery)

**Video metadata:**
```csharp
var meta = new Metadata
{
    Software = "MyApp 1.0",
    Vendor = "Apple",
    Model = "iPhone 15 Pro"
};
Metadata.ApplyGpsCoordinates(meta, 34.0522, -118.2437);
meta.DateTimeOriginal = DateTime.Now;
await Mp4MetadataInjector.InjectMetadataAsync(filePath, meta);

// GPS only
await Mp4LocationInjector.InjectLocationAsync(filePath, 34.0522, -118.2437);

// Arbitrary atoms
await Mp4MetadataInjector.InjectAtomsAsync(filePath, new Dictionary<string, string>
{
    [Mp4MetadataInjector.Atom_Artist] = "John Doe",
    [Mp4MetadataInjector.Atom_Comment] = "Race lap 3"
});
```

**Photo GPS:**
```csharp
Metadata.ApplyGpsCoordinates(captured.Meta, 34.0522, -118.2437);
await using var stream = camera.CreateOutputStreamRotated(captured, false);
using var exifStream = await JpegExifInjector.InjectExifMetadata(stream, captured.Meta);

await using var fileStream = File.Create(appPath);
await exifStream.CopyToAsync(fileStream);
```

**Read/write existing files:**
```csharp
var atoms = Mp4MetadataInjector.ReadAtoms("/path/to/video.mp4");

if (Mp4MetadataInjector.ReadLocation("/path/to/video.mp4", out double lat, out double lon))
    Console.WriteLine($"Video location: {lat}, {lon}");
```

### Supported Video Metadata Atoms

| Atom | Constant | Metadata Property | Description |
|------|----------|------------------|-------------|
| `\u00a9xyz` | `Atom_Location` | `GpsLatitude`/`GpsLongitude` | GPS location (ISO 6709) |
| `\u00a9too` | `Atom_Software` | `Software` | App name/version |
| `\u00a9mak` | `Atom_Make` | `Vendor` | Device manufacturer |
| `\u00a9mod` | `Atom_Model` | `Model` | Device model |
| `\u00a9day` | `Atom_Date` | `DateTimeOriginal` | Recording date/time |
| `\u00a9ART` | `Atom_Artist` | `CameraOwnerName` | Author/artist |
| `\u00a9cmt` | `Atom_Comment` | `UserComment` | Free-text comment |

### Platform Details

| Feature | Android | iOS/macOS | Windows |
|---------|---------|-----------|---------|
| Photo GPS (EXIF) | `JpegExifInjector` + `ExifInterface` | `JpegExifInjector` + Photos framework | `JpegExifInjector` |
| Video metadata | `Mp4MetadataInjector` | `Mp4MetadataInjector` + `CLLocation` | `Mp4MetadataInjector` |
| Gallery shows location | Google Photos | Apple Photos | File properties |

## AudioSampleConverter

A lightweight audio preprocessing utility for raw PCM16 audio. Handles stereo-to-mono downmix, sample rate conversion, and optional silence gating - useful for speech-to-text APIs (OpenAI Realtime, Whisper, Azure Speech, etc.).

**Smart passthrough** - each processing step is skipped when not needed, with zero allocations when all conditions match.

```csharp
var preprocessor = new AudioSampleConverter(targetSampleRate: 24000);
// Or: new AudioSampleConverter(targetSampleRate: 16000, silenceRmsThreshold: 0);

preprocessor.SetFormat(sampleRate: 48000, channels: 2);

byte[] result = preprocessor.Process(rawPcm16Data);
if (result != null)
{
    // result is mono PCM16 at target sample rate
}

preprocessor.Reset(); // Reset state for new session
```

### Constructor Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `targetSampleRate` | *(required)* | Output sample rate in Hz |
| `silenceRmsThreshold` | 0.003 | RMS level below which audio is silence. 0 to disable. |
| `silentChunksBeforeMute` | 100 | Consecutive silent chunks before suppressing output |
