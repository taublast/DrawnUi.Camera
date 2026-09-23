# SkiaCamera

Camera control, rendered with SkiaSharp and DrawnUI for .NET MAUI enabling real-time video processing, photo capture with metadata, and audio recording, AI/ML capture-friendly.
Use as Camera or a standalone Audio recorder inside any MAUI app by wrapping with a `Canvas`.  

## Features

- Cross-platform (Android, iOS, MacCatalyst, Windows) with hardware-accelerated SkiaSharp rendering
- Separate additional package for pure .NET harness
- Real-time preview effects (Sepia, B&W, Pastel) and custom SKSL shaders
- Photo capture with post-processing and metadata
- Video recording with real-time frame processing — overlays/effects baked in without post-processing
- Audio-only recording mode with real-time sample visualization
- [Pre-recording buffer](PreRecording.md) — capture seconds before the live recording started
- Abort recording without saving
- Manual and automatic camera selection with enumeration
- Capture format management with quality presets and manual format selection
- Zoom control with configurable limits
- Dual-channel flash control (preview torch + capture flash)
- GPS injection and custom EXIF for both photos and videos
- Built-in permission handling

Read the [blog article](https://taublast.github.io/posts/VideoRecording) about the sample app coming along with this repo.

---


![vlc_0Y0bMKzuHM](https://github.com/user-attachments/assets/21ced7c4-7a05-44bc-ad39-9cfb44c3a4b4)

## What's New  1.10.6.171

 * Built on DrawnUi 1.10.6.17 (`DrawnUi.Maui` / `DrawnUi.Net`), up from 1.10.6.16.
 * Windows: the D3D preview path no longer breaks once another library opens a Direct3D device in the process (DirectML through ONNX Runtime, for one). The `ID3D11Device` / `ID3D11DeviceContext` / `ID3D11Texture2D` COM interop declared the methods that really return `void` (`GetDevice`, `GetDesc`, `GetImmediateContext`, `CopyResource`, `Unmap`, the state setters) without `[PreserveSig]`, so the interop stub read whatever the return register happened to hold as an HRESULT. That was harmless until D3D12/DirectML was loaded, after which `GetImmediateContext` "failed" on every frame, the conversion fell back to the projected `SoftwareBitmap` copy, and the CsWinRT memory-pressure GC storm from 1.10.6.162 came back (twenty induced gen2 collections a second, scrolling stuttered in every window) - and stayed even after the other library went idle. Every void method now carries `[PreserveSig]`; the three value-returning getters (`GetFeatureLevel`, `GetCreationFlags`, `GetExceptionMode`) are declared with their real signatures.

## What's New  1.10.6.162

 * Windows: the app no longer runs a full garbage collection twenty times a second while the camera is on. Every preview frame created a projected `SoftwareBitmap` (and its `BitmapBuffer`), and CsWinRT's constructors for those classes call `GC.AddMemoryPressure(1.2 MB)` each, so at camera frame rate the GC kept inducing gen2 collections that paused every thread, the UI included - scrolling in any window stuttered while the camera ran. The frame's pixels are now read through the raw COM vtables (`SoftwareBitmapPixels`: IVideoMediaFrame, ISoftwareBitmap, IBitmapBuffer, IMemoryBufferByteAccess), no projected object per frame, and the `SKImage` keeps the buffer locked until it is disposed instead of reading memory that was already unlocked. Pre-recording and non-BGRA formats still take the projected path.
 * Windows: switching the camera off now hands the device back. Stopping only paused the frame reader, so the `MediaCapture` stayed open and Windows kept showing the camera as in use (the privacy indicator and the webcam light stayed on, other apps found it busy) until the control was disposed. `Stop(force)` now disposes the frame reader and the `MediaCapture` (`ReleaseHardware()`), `SetupHardware()` releases any previous capture before creating a new one, `Dispose()` goes through the same path, and `Start()` awaits `Setup()` instead of firing it and moving on. Recording is not interrupted: the device is kept while a video is being written.

## Previously 

 * iOS: saved photos keep their metadata. Photos showed "No camera information" and "No lens information" because iOS re-serialized the EXIF while importing raw data and dropped every value stored out of line - make, model, lens make and model, exposure time, aperture, focal length. The asset now goes to Photos as a file, which it stores byte for byte.
 * iOS: EXIF, TIFF and GPS are written with ImageIO (`AppleJpegMetadata`) instead of the hand-built segment in `JpegExifInjector`, which stays as fallback and on other platforms. The compressed image is copied from the source, so nothing is re-encoded and quality is unchanged.
 * iOS: fixed a crash when recording started while preview audio was still starting, e.g. record tapped right after switching `CaptureMode` to Video. Stopping preview audio disposed the `AVAudioEngine` under its running setup (`EXC_BAD_ACCESS` in `outputFormatForBus:`). `AudioCaptureApple` now serializes engine setup and cleanup, and a preview audio start that was stopped meanwhile gives up instead of leaving an engine running.
 * iOS: selfie video mirroring follows `MirrorSavedSelfiePhoto` like the still. The encoder always mirrored front-camera frames, so with `MirrorSavedSelfiePhoto=true` a clip came out flipped against the screen. Applies to the GPU processing path (`CaptureFrameCore`, zero-copy and CPU fallback) and to the native `AVCaptureMovieFileOutput` connection (`VideoMirrored`). While encoder frames feed the preview the display flip is inverted for that time, so the screen looks the same before, during and after recording.
* Fix Android not using audio mode for video
 
## Extending SkiaCamera

Subclass `SkiaCamera` to hook into lifecycle and GPU events:

```csharp
public class MyCamera : SkiaCamera
{
    /// <summary>
    /// Called when camera hardware state changes (Off → On, On → Off, etc.).
    /// Override to react to camera start/stop without subscribing to StateChanged event.
    /// </summary>
    public override void OnStateChanged(HardwareState state)
    {
        base.OnStateChanged(state);
        if (state == HardwareState.On)
        {
            // camera is ready
        }
    }
}
```

For custom native camera implementations that hold GPU-backed resources, implement `INativeCamera.InvalidateGpuResources()`:

```csharp
// Called automatically by SkiaCamera.Paint() when GRContext handle changes
// (e.g. app returns from background and Metal/GL context is recreated).
// Reset any GPU textures, surfaces, or pipelines so they are recreated fresh.
public void InvalidateGpuResources()
{
    // dispose stale GPU resources here
}
```

The default implementation is a no-op — only override when your native camera holds resources bound to the SkiaSharp GRContext.
## Sample Apps

- [SkiaCamera Demo](https://github.com/taublast/DrawnUi.Maui.Camera/tree/main/src/MAUI/Sample) - This repo: recording with processing, shaders, AI captions.
- [Filters Camera](https://github.com/taublast/ShadersCamera) - Still photo-camera with realtime SKSL shaders as photo-filters.
- [SolTempo](https://github.com/taublast/SolTempo) - Audio visualizer and BPM detector using SkiaCamera's audio monitoring capabilities.

## Installation

```bash
dotnet add package DrawnUi.Maui.Camera
```

Initialize DrawnUi inside `MauiProgram.cs`:

```csharp
builder.UseDrawnUi();
```

[Read more about DrawnUi initialization](https://taublast.github.io/DrawnUi/articles/getting-started.html).

## Quick Start

### XAML

```xml
xmlns:draw="http://schemas.appomobi.com/drawnUi/2023/draw"
xmlns:camera="clr-namespace:DrawnUi.Camera;assembly=DrawnUi.Maui.Camera"
```

```xml
<draw:Canvas
    HorizontalOptions="Fill"
    VerticalOptions="Fill"
    Gestures="Lock"
    RenderingMode="Accelerated">

    <camera:SkiaCamera
        x:Name="CameraControl"
        BackgroundColor="Black"
        PhotoQuality="Medium"
        Facing="Default"
        HorizontalOptions="Fill"
        VerticalOptions="Fill"
        ZoomLimitMax="10"
        ZoomLimitMin="1" />

</draw:Canvas>
```

### Code-Behind

```csharp
var camera = new SkiaCamera
{
    BackgroundColor = Colors.Black,
    PhotoQuality = CaptureQuality.Medium,
    Facing = CameraPosition.Default,
    HorizontalOptions = LayoutOptions.Fill,
    VerticalOptions = LayoutOptions.Fill,
    ZoomLimitMax = 10,
    ZoomLimitMin = 1
};

camera.IsOn = true;
```

> **Startup tip:** Set `IsOn = true` after the Canvas has drawn its first frame to avoid initialization race conditions:
> ```csharp
> Canvas.WillFirstTimeDraw += (sender, context) => {
>     Tasks.StartDelayed(TimeSpan.FromMilliseconds(500), () => {
>         CameraControl.IsOn = true;
>     });
> };
> ```

## ML/AI Frame Access

Use raw-frame hook when model needs clean camera input:

```csharp
public class MyCamera : SkiaCamera
{
    private readonly byte[] _rgba = new byte[224 * 224 * 4];
    private const float CropRatio = 1f;

    protected override void OnRawFrameAvailable(RawCameraFrame frame)
    {
        if (!frame.TryGetRgba(224, 224, _rgba, OutputOrientation.Portrait, CropRatio))
            return;

        // queue inference with _rgba
    }
}
```

`width` and `height` are always the final output dimensions after orientation and scaling. Helpers preserve aspect ratio automatically and center-crop when needed. `cropRatio` zooms further into that centered crop window: `1f` keeps the full crop window, `0.5f` keeps its centered half.

On Apple and Android GPU paths, crop + final rotation + scale are completed before the final `byte[]` readback.

Use `NewPreviewSet` only when you want to inspect the final displayed preview. It fires after preview display and may already include preview effects, shaders, and during recording the `ProcessFrame` overlay when `UseRecordingFramesForPreview = true`.

Rule of thumb:
- `OnRawFrameAvailable(RawCameraFrame frame)` + `frame.TryGetRgba(...)`: clean camera input for AI/ML.
- `OutputOrientation.Display`: match what the user sees.
- `OutputOrientation.Portrait`: portrait-up output for models that expect canonical upright frames.
- `cropRatio < 1f`: zoom into the center before scaling when the subject occupies only a small part of the frame.
- `frame.TryGetRgbaBytes(...)`: owned raw `RGBA8888` payload for custom backends.
- `frame.TryGetJpeg(...)` / `frame.TryGetPng(...)`: standard image payloads for hosted multimodal APIs.
- `NewPreviewSet`: analyze exactly what the user currently sees.

`RawCameraFrame.RawImage` is optional advanced access and may be null on zero-copy GPU paths. `RawImageRotation` tells how much raw-image consumers still need to rotate to reach display orientation. `DisplayRotation` tells how the current preview is rotated relative to portrait. Prefer `frame.TryGetRgba(...)` for portable ML code, `frame.TryGetRgbaBytes(...)` for raw custom uploads, and `frame.TryGetJpeg(...)` / `frame.TryGetPng(...)` when the destination expects a normal image file payload.

## Orientation Handling

Lock the app to portrait at the platform level for correct saved video orientation. UI controls can still respond to device tilt by rotating individually.

**Android** (`Platforms/Android/MainActivity.cs`):
```csharp
[Activity(ScreenOrientation = ScreenOrientation.SensorPortrait, ...)]
```

**iOS** (`Platforms/iOS/Info.plist`):
```xml
<key>UIRequiresFullScreen</key>
<true/>
<key>UISupportedInterfaceOrientations</key>
<array>
    <string>UIInterfaceOrientationPortrait</string>
</array>
```

Rotate UI icons in response to device tilt using DrawnUI's rotation event:
```csharp
Super.RotationChanged += OnRotationChanged;

private void OnRotationChanged(object sender, int rotation)
{
    _buttonSettings.Rotation = rotation;
    _buttonFlash.Rotation = rotation;
}
```

## Permissions

You need to set up permissions for camera, microphone (for video with sound) and storage/gallery access.

### Windows

No specific setup needed.

### Apple

Add to `Platforms/iOS/Info.plist` and `Platforms/MacCatalyst/Info.plist` inside `<dict>`:

```xml
<key>NSCameraUsageDescription</key>
<string>This app uses camera so You could take photos</string>
<key>NSPhotoLibraryAddUsageDescription</key>
<string>We need access to the library to save photos</string>
<key>NSPhotoLibraryUsageDescription</key>
<string>We need access to the library to save photos</string>
```

For video with audio:
```xml
<key>NSMicrophoneUsageDescription</key>
<string>In case You want to save videos with sound</string>
```

For GPS geotagging:
```xml
<key>NSLocationWhenInUseUsageDescription</key>
<string>In case You want to be able to geotag taken photos and videos</string>
```

### Android

Add to `Platforms/Android/AndroidManifest.xml` inside `<manifest>`:

```xml
<uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
<uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" android:maxSdkVersion="32" />
<uses-permission android:name="android.permission.READ_MEDIA_VIDEO" />
<uses-permission android:name="android.permission.READ_MEDIA_IMAGES" />
<uses-permission android:name="android.permission.READ_MEDIA_AUDIO" />
<uses-permission android:name="android.permission.CAMERA" />
```

For video with audio:
```xml
<uses-permission android:name="android.permission.RECORD_AUDIO" />
```

For GPS geotagging:
```xml
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
```

#### FileProvider Setup (for OpenFileInGallery)

Add inside `<application>` in `AndroidManifest.xml`:

```xml
<provider
    android:name="androidx.core.content.FileProvider"
    android:authorities="${applicationId}.fileprovider"
    android:exported="false"
    android:grantUriPermissions="true">
    <meta-data
        android:name="android.support.FILE_PROVIDER_PATHS"
        android:resource="@xml/file_paths" />
</provider>
```

Create `Platforms/Android/Resources/xml/file_paths.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<paths xmlns:android="http://schemas.android.com/apk/res/android">
    <external-files-path name="my_images" path="Pictures" />
    <external-files-path name="my_movies" path="Movies" />
    <cache-path name="my_cache" path="." />
</paths>
```

### Runtime Permissions

SkiaCamera has built-in permission handling. When you set `IsOn = true`, it automatically checks and requests permissions defined by `NeedPermissionsSet`.

```csharp
// Quick approach
camera.NeedPermissionsSet = NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone;
camera.IsOn = true;

// Or manual async approach
bool ok = await SkiaCamera.RequestPermissionsAsync(
    NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone);
if (ok) camera.IsOn = true;
```

## Still Photo Rendering

Reuse existing `ProcessFrame` or `ProcessPreview`-style `Action<DrawableFrame>` code on a captured still photo:

```csharp
private async void OnCaptureSuccess(object sender, CapturedImage captured)
{
    var imageWithOverlay = await CameraControl.RenderCapturedPhotoAsync(
        captured,
        drawOverlay: CameraControl.ProcessFrame,
        useGpu: true);

    captured.Image.Dispose();
    captured.Image = imageWithOverlay;
}
```

Notes:
- `drawOverlay` runs after the still image is rendered and before any optional DrawnUI overlay tree.
- The synthetic `DrawableFrame` uses callback-space width and height for the replayed overlay viewport.
- For rotated stills, `drawOverlay` is replayed using the captured device orientation so reused preview/recording overlay code sees the expected viewport orientation.
- `Scale` defaults to `1f` unless you pass another value through the full overload.
- `IsPreview` is currently `false` for this path.
- Use `createdImage` on the legacy convenience overload: `RenderCapturedPhotoAsync(captured, overlay, createdImage, ...)`.
- Use `configureImage` only on the full overload that also exposes `composeBase` and `drawOverlay`.
- Use named arguments when mixing stages to avoid overload ambiguity.

Use the `composeBase` overload when the still path needs a canvas composition step before overlay drawing:

```csharp
private async void OnCaptureSuccess(object sender, CapturedImage captured)
{
    using var sepiaPaint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(_sepiaMatrix) };

    var imageWithPreviewStyle = await CameraControl.RenderCapturedPhotoAsync(
        captured,
        composeBase: (canvas, frameImage) =>
        {
            canvas.DrawImage(frameImage, 0, 0, sepiaPaint);
        },
        drawOverlay: CameraControl.ProcessPreview,
        useGpu: true);

    captured.Image.Dispose();
    captured.Image = imageWithPreviewStyle;
}
```

Ordering for the full overload is:
- `configureImage` configures the temporary `SkiaImage`
- `composeBase` composes the rendered still into the destination canvas
- `drawOverlay` draws reusable `DrawableFrame` overlays in replayed callback space based on the captured device orientation
- optional DrawnUI `overlay` renders last

Performance note:
- the extra preparation pass exists only when `composeBase` is supplied
- older compatibility overloads keep the legacy direct-render path and do not spend time on the pre-overlay stage

## Documentation

| Document | Description |
|----------|-------------|
| [Usage Guide](docs/usage-guide.md) | Setup, properties, lifecycle, flash, capture, zoom, effects, live processing, permissions, MVVM example |
| [Video Recording](docs/video-recording.md) | Recording, audio control, real-time processing, GPS & metadata, AudioSampleConverter |
| [API Reference](docs/api-reference.md) | Properties, methods, events, data classes, enums |
| [Troubleshooting](docs/troubleshooting.md) | Common issues, debug tips, best practices, platform notes |
| [AI Agent Guide](docs/ai-agent-guide.md) | Integration patterns for AI agents |
| [Pre-Recording](PreRecording.md) | Pre-recording buffer feature |

## ToDo

- [ ] Manual camera controls (focus, exposure, ISO, white balance)
- [ ] Android: selfie video mirroring should follow `MirrorSavedSelfiePhoto` like the still (`flipSelfie` in `NativeCamera.Android.cs`) and like Apple does since 1.10.6.151. The Android recording paths (GPU processing in `SkiaCamera.Android.cs`, native MediaRecorder) never mirror front-camera frames, so with `MirrorSavedSelfiePhoto=false` and a mirrored preview (`MirrorPreviewX`) the saved clip does not match the screen.

## References

iOS:
* [Manual Camera Controls in Xamarin.iOS](https://github.com/MicrosoftDocs/xamarin-docs/blob/0506e3bf14b520776fc7d33781f89069bbc57138/docs/ios/user-interface/controls/intro-to-manual-camera-controls.md) by David Britch
