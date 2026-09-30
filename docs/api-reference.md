# API Reference

## Core Properties

```csharp
// Camera Control
public bool IsOn { get; set; }                    // Start/stop camera
public CameraPosition Facing { get; set; }        // Camera selection mode
public int CameraIndex { get; set; }              // Manual camera index
public HardwareState State { get; }               // Current state (read-only)
public bool IsBusy { get; }                       // Processing state (read-only)

// Capture Settings
public CaptureQuality PhotoQuality { get; set; }  // Photo quality
public int PhotoFormatIndex { get; set; }          // Manual format index
public CaptureFlashMode CaptureFlashMode { get; set; }   // Flash mode for capture

// Flash Control
public FlashMode FlashMode { get; set; }           // Preview torch mode
public bool IsFlashSupported { get; }              // Flash availability
public bool IsAutoFlashSupported { get; }          // Auto flash support
public SkiaImageEffect Effect { get; set; }        // Real-time simple color filters

// Video Recording
public bool IsRecording { get; }                   // Recording state (read-only)
public VideoQuality VideoQuality { get; set; }     // Video quality preset
public int VideoFormatIndex { get; set; }          // Manual format index
public bool UseRealtimeVideoProcessing { get; set; }       // Enable frame-by-frame capture mode
public Action<DrawableFrame> ProcessFrame { get; set; }    // Draw on each recorded video frame
public Action<DrawableFrame> ProcessPreview { get; set; }  // Draw on each preview frame before display
public bool UseRecordingFramesForPreview { get; set; }     // Use encoder output as preview during recording (default: true)
public float PreviewScale { get; }                 // Preview-to-recording scale factor (read-only)

// Pre-Recording
public bool EnablePreRecording { get; set; }       // Enable pre-recording buffer
public TimeSpan PreRecordDuration { get; set; }    // Duration of pre-recording buffer (default: 5s)
public bool IsPreRecording { get; }                // Pre-recording state (read-only)
public TimeSpan LiveRecordingDuration { get; }     // Duration of current live recording (excluding buffer)

// Zoom & Limits
public double Zoom { get; set; }                   // Current zoom level
public double ZoomLimitMin { get; set; }           // Minimum zoom
public double ZoomLimitMax { get; set; }           // Maximum zoom

// Audio
public bool EnableAudioRecording { get; set; }     // Include audio in video recordings (default: true)
public bool EnableAudioMonitoring { get; set; }    // Enable live audio callbacks/visualization (default: false)
public CameraAudioMode AudioMode { get; set; }     // Audio processing mode (default: Default)

// Video/Audio Control
public bool EnableVideoPreview { get; set; }       // Show video preview (default: true)
public bool EnableVideoRecording { get; set; }     // Record video frames (default: true)

// Code Detection (QR, barcodes)
public CameraCodeTypes CodeDetection { get; set; } // Formats to detect in the live preview (default: None = off, no cost)
public static bool IsCodeDetectionSupported { get; }  // Platform can detect codes (currently iOS only)
public SKRect DisplayRect { get; }                 // Drawn preview image rect, canvas pixels
public bool TryMapPreviewPoint(SKPoint normalized, out SKPoint pixels)  // Preview-normalized point -> canvas pixels

// Passkey sign-in codes ("FIDO:/..." QR shown by a site)
public static bool IsPasskeySignInSupported { get; }  // System can take such a code (currently iOS 16+)
public static Task<bool> StartPasskeySignInAsync(DetectedCode code)  // Hands the code to the system; call from a tap
```

## Core Methods

```csharp
// Camera Management
public async Task<List<CameraInfo>> GetAvailableCamerasAsync()
public async Task<List<CameraInfo>> RefreshAvailableCamerasAsync()

// Permissions - request (shows OS dialogs)
public static void CheckPermissions(Action granted, Action notGranted, NeedPermissions request)
public static Task<bool> RequestPermissionsAsync(NeedPermissions request)

// Permissions - silent check (no dialogs)
public static void CheckPermissionsGranted(Action granted, Action notGranted, NeedPermissions request)
public static Task<bool> RequestPermissionsGrantedAsync(NeedPermissions request)

// Permissions - instance, controls which permissions are required on IsOn = true
public NeedPermissions NeedPermissionsSet { get; set; }  // default: Camera | Gallery

// Capture Format Management
public async Task<List<CaptureFormat>> GetAvailableCaptureFormatsAsync()
public async Task<List<CaptureFormat>> RefreshAvailableCaptureFormatsAsync()
public CaptureFormat CurrentStillCaptureFormat { get; }

// Capture Operations
public async Task TakePicture()
public void FlashScreen(Color color, long duration = 250)
public void OpenFileInGallery(string filePath)
public virtual Task<SKImage> RenderCapturedPhotoAsync(CapturedImage captured, SkiaLayout overlay, Action<SKCanvas, SKImage>? composeBase = null, Action<DrawableFrame>? drawOverlay = null, Action<SkiaImage> configureImage = null, bool useGpu = false, SKColor? background = null, float scale = 1f, bool rotate = true)
public virtual Task<SKImage> RenderCapturedPhotoAsync(CapturedImage captured, SkiaLayout overlay, Action<SkiaImage> createdImage = null, bool useGpu = false, SKColor? background = null, bool rotate = true)
public virtual Task<SKImage> RenderCapturedPhotoAsync(CapturedImage captured, Action<SKCanvas, SKImage> composeBase, Action<DrawableFrame>? drawOverlay = null, Action<SkiaImage> configureImage = null, bool useGpu = false, SKColor? background = null, bool rotate = true)
public virtual Task<SKImage> RenderCapturedPhotoAsync(CapturedImage captured, Action<DrawableFrame> drawOverlay, Action<SkiaImage> configureImage = null, bool useGpu = false, SKColor? background = null, bool rotate = true)

// Video Recording Operations
public async Task StartVideoRecording()
public async Task StopVideoRecording(bool abort = false)  // abort=true discards without saving
public bool CanRecordVideo()
public async Task<List<VideoFormat>> GetAvailableVideoFormatsAsync()
public VideoFormat GetCurrentVideoFormat()
public async Task<string> MoveVideoToGalleryAsync(CapturedVideo video, string album = null, bool deleteOriginal = true)

// Camera Controls
public void SetZoom(double value)

// Flash Control
public void SetFlashMode(FlashMode mode)
public FlashMode GetFlashMode()
public void SetCaptureFlashMode(CaptureFlashMode mode)
public CaptureFlashMode GetCaptureFlashMode()

// Overlay
public void InitializeOverlayLayouts(SkiaLayout overlay)  // Attach DrawnUI layout as frame overlay

// Audio Processing (virtual overrides)
protected virtual AudioSample OnAudioSampleAvailable(AudioSample sample)  // Requires EnableAudioMonitoring = true
public virtual void WriteAudioSample(AudioSample sample)                  // Requires UseRealtimeVideoProcessing = true

// Audio Utilities
public static void AmplifyPcm16(byte[] data, float gainFactor)  // In-place PCM16 gain, zero allocations

// Raw Frame ML Hook
protected internal virtual void OnRawFrameAvailable(RawCameraFrame frame)
```

`RenderCapturedPhotoAsync(..., drawOverlay: ...)` lets you reuse existing `ProcessFrame` or `ProcessPreview`-style `Action<DrawableFrame>` code on a captured still photo. `drawOverlay` runs after the still image is rendered and before any optional `SkiaLayout` overlay is rendered. For rotated stills, the callback is replayed in capture-time viewport orientation so reused overlay code sees the expected callback space.

`RenderCapturedPhotoAsync(..., composeBase: ...)` adds a canvas-stage hook for still-photo composition before `drawOverlay` runs. Existing compatibility overloads keep the legacy direct-render path and do not allocate or execute this extra stage unless the new overload is selected.

## Events

```csharp
// Photo Capture Events
public event EventHandler<CapturedImage> CaptureSuccess;
public event EventHandler<Exception> CaptureFailed;

// Video Recording Events
public event EventHandler<CapturedVideo> RecordingSuccess;
public event EventHandler<Exception> RecordingFailed;
public event EventHandler<TimeSpan> RecordingProgress;

// Preview & State Events
public event EventHandler<LoadedImageSource> NewPreviewSet;    // Final displayed preview, may include preview/recording overlays
public event EventHandler<HardwareState> StateChanged;
public event EventHandler<string> OnError;
public event EventHandler<double> Zoomed;

// Code Detection: codes in view, an empty list when they are gone. Not raised on the UI or render thread.
public event EventHandler<IReadOnlyList<DetectedCode>> CodesDetected;
```

## RawCameraFrame

```csharp
public readonly struct RawCameraFrame
{
    public SKImage? RawImage { get; }       // Optional advanced access, valid only inside callback
    public int Rotation { get; }            // Extra rotation needed only when using RawImage directly
    public int SourceWidth { get; }         // Raw frame width before resize
    public int SourceHeight { get; }        // Raw frame height before resize
    public bool HasRawImage { get; }
    public bool TryGetRgba(int targetWidth, int targetHeight, byte[] outputBuffer)
    public bool TryGetRgbaBytes(int targetWidth, int targetHeight, out byte[]? rgbaBytes)
    public bool TryGetJpeg(int targetWidth, int targetHeight, out byte[]? jpegBytes, int quality = 100)
    public bool TryGetPng(int targetWidth, int targetHeight, out byte[]? pngBytes)
}
```

Use `OnRawFrameAvailable(RawCameraFrame frame)` + `frame.TryGetRgba(...)` for AI/ML input that must ignore preview overlays and effects.
Use `frame.TryGetRgbaBytes(...)` only when a custom API explicitly accepts raw `RGBA8888` buffers.
Use `frame.TryGetJpeg(...)` or `frame.TryGetPng(...)` when the destination expects normal image payloads.
Use `NewPreviewSet` only when you intentionally need the final displayed preview.

## Data Classes

### CaptureFormat

```csharp
public class CaptureFormat
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int TotalPixels => Width * Height;
    public double AspectRatio => (double)Width / Height;
    public string AspectRatioString { get; }      // "16:9", "4:3"
    public string FormatId { get; set; }          // Platform-specific identifier
    public string Description { get; }
}
```

### CameraInfo

```csharp
public class CameraInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public CameraPosition Position { get; set; }
    public int Index { get; set; }
    public bool HasFlash { get; set; }
}
```

### VideoFormat

```csharp
public class VideoFormat
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public string Codec { get; set; }
    public long BitRate { get; set; }
    public double AspectRatio => (double)Width / Height;
    public string Description { get; }
}
```

### CapturedVideo

```csharp
public class CapturedVideo
{
    public string FilePath { get; set; }
    public TimeSpan Duration { get; set; }
    public VideoFormat Format { get; set; }
    public CameraPosition Facing { get; set; }
    public DateTime Time { get; set; }
    public Metadata Meta { get; set; }            // GPS, author, camera, date
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public long FileSizeBytes { get; set; }
}
```

### DetectedCode

```csharp
public sealed class DetectedCode
{
    public string Value { get; }            // Decoded text. Untrusted input: validate before acting on it
    public CameraCodeTypes Type { get; }    // Format of this code, a single flag
    public SKPoint[] Corners { get; }       // Normalized 0..1 in the displayed preview image, winding not guaranteed
    public bool IsPasskeySignIn { get; }    // Text is "FIDO:/" + digits: a passkey sign-in code
}
```

## Enums

```csharp
[Flags] public enum CameraCodeTypes { None, Qr, Aztec, DataMatrix, Pdf417, Ean13, Ean8, Code128, Code39 }
public enum CameraPosition { Default, Selfie, Manual }
public enum HardwareState { Off, On, Error }
public enum CaptureQuality { Max, Medium, Low, Preview, Manual }
public enum VideoQuality { Low, Standard, High, Ultra, Manual }
public enum FlashMode { Off, On, Strobe }
public enum CaptureFlashMode { Off, Auto, On }
public enum SkiaImageEffect { None, Sepia, BlackAndWhite, Pastel }
public enum CameraAudioMode { Baseline, VideoRecording, Voice, Flat }
```

### CameraAudioMode Details

| Value | Description | iOS | Android |
|-------|-------------|-----|---------|
| `Default` | Standard system audio processing | `AVAudioSessionModeDefault` | `AudioSource.Mic` |
| `VideoRecording` | Optimized for video capture | `AVAudioSessionModeVideoRecording` | `AudioSource.Camcorder` |
| `Voice` | AGC, echo cancellation, noise suppression | `AVAudioEngine SetVoiceProcessingEnabled` | `AudioSource.VoiceCommunication` |
| `Flat` | Minimal processing, flat frequency response | `AVAudioSessionModeMeasurement` | `AudioSource.Unprocessed` (API 29+) |
