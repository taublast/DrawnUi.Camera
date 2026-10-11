# Usage Guide

## Table of Contents

| # | Section | Description |
|---|---------|-------------|
| 1 | [Declaration / Setup](#1-declaration--setup) | XAML and code-behind setup |
| 2 | [Essential Properties](#2-essential-properties) | Key properties reference |
| 3 | [Camera Lifecycle Management](#3-camera-lifecycle-management) | `IsOn`, `Start()`, global instance management |
| 4 | [Flash Control](#4-flash-control) | Preview torch and capture flash modes |
| 5 | [Capture Format Selection](#5-capture-format-selection) | Quality presets, manual format, preview sync |
| 6 | [Opening Files in Gallery](#6-opening-files-in-gallery) | `OpenFileInGallery()` |
| 7 | [Camera Selection](#7-camera-selection) | Front/back/manual camera switching |
| 8 | [Captured Photo Processing](#8-captured-photo-processing) | `CaptureSuccess` event, `TakePicture()` |
| 9 | [Real-Time Effects & Custom Shaders](#9-real-time-effects--custom-shaders) | Built-in effects, SKSL shaders |
| 10 | [Zoom Control](#10-zoom-control) | Manual zoom, pinch-to-zoom |
| 11 | [Camera State Management](#11-camera-state-management) | `StateChanged` event, `HardwareState` |
| 12 | [Live Processing](#12-live-processing-processframe--processpreview) | Drawing overlays on preview and recorded video, `NewPreviewSet` for processed-preview analysis |
| 13 | [Raw Frame ML Hook](#13-raw-frame-ml-hook-onrawframeavailablerawcameraframe--trygetrgba) | Zero-overhead GPU-accelerated raw frame access for ML/AI inference |
| 14 | [Permission Handling](#14-permission-handling) | `NeedPermissions` flags, `CheckPermissions()`, async helpers |
| 15 | [Complete MVVM Example](#15-complete-mvvm-example) | Full ViewModel + Page example |
| 16 | [Code Detection](#16-code-detection-qr-and-barcodes) | QR codes and barcodes in the live preview, `CodesDetected`, mapping to the screen |
| 17 | [Mock Source](#17-mock-source-a-still-image-instead-of-the-camera) | `MockSource`: a still image as the live camera feed, for screenshots, simulators and tests |

## 1. Declaration / Setup

For installation please see [README](../README.md). Then you would be able to consume camera in your app.

The `Canvas` that would contain `SkiaCamera` *must* have its property `RenderingMode = RenderingModeType.Accelerated`:

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
        RenderingMode = "Accelerated">

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

**Container tips:**
- Keep the container stable: no `Auto` rows, no unset width/height without `Fill`.
- For correct saved video orientation, lock the app or the camera page to portrait. The UI can still respond to landscape rotation by rotating icons/controls — DrawnUI provides device orientation info at runtime.

### Code-Behind

Example for use inside an already created `Canvas`:

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
```

## 2. Essential Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Facing` | `CameraPosition` | `Default` | Camera selection: `Default` (back), `Selfie` (front), `Manual` |
| `CameraIndex` | `int` | `-1` | Manual camera selection index (when `Facing = Manual`) |
| `IsOn` | `bool` | `false` | Camera power state - use this to start/stop camera |
| `PhotoQuality` | `CaptureQuality` | `Max` | Photo quality: `Max`, `Medium`, `Low`, `Preview`, `Manual` |
| `PhotoFormatIndex` | `int` | `0` | Format index for manual capture (when `PhotoQuality = Manual`) |
| `FlashMode` | `FlashMode` | `Off` | Preview torch mode: `Off`, `On`, `Strobe` |
| `CaptureFlashMode` | `CaptureFlashMode` | `Auto` | Flash mode for capture: `Off`, `Auto`, `On` |
| `IsFlashSupported` | `bool` | - | Whether flash is available (read-only) |
| `IsAutoFlashSupported` | `bool` | - | Whether auto flash is supported (read-only) |
| `Effect` | `SkiaImageEffect` | `None` | Real-time effects: `None`, `Sepia`, `BlackAndWhite`, `Pastel` |
| `Zoom` | `double` | `1.0` | Camera zoom level |
| `ZoomLimitMin/Max` | `double` | `1.0/10.0` | Zoom constraints |
| `State` | `HardwareState` | `Off` | Current camera state (read-only) |
| `IsBusy` | `bool` | `false` | Whether camera is processing (read-only) |

## 3. Camera Lifecycle Management

```csharp
// Use IsOn for lifecycle management
camera.IsOn = true;  // Start camera
camera.IsOn = false; // Stop camera
```

**Important**: `IsOn` vs `Start()` difference:
- `IsOn = true`: Proper lifecycle management, handles permissions, app backgrounding
- `Start()`: Direct method call, bypasses safety checks

**App backgrounding:** The camera automatically turns off when the app goes to background and restores its state when the app resumes — no additional coding needed.

### Global Instance Management

SkiaCamera maintains a static list of all active instances to prevent resource conflicts, especially on Windows where hardware release can be slow.

```csharp
// Access all tracked camera instances
var activeCameras = SkiaCamera.Instances;

// Stop all active cameras globally
await SkiaCamera.StopAllAsync();
```

**Automatic Conflict Resolution**:
When a new camera starts, it automatically checks `SkiaCamera.Instances` for other active cameras. If another camera is found to be busy or stopping (common during Hot Reload or rapid page navigation), the new instance will wait until the hardware is fully released before attempting to initialize.

## 4. Flash Control

SkiaCamera provides comprehensive flash control for both preview torch and still image capture:

### Preview Torch Control
```csharp
camera.FlashMode = FlashMode.Off;   // Disable torch
camera.FlashMode = FlashMode.On;    // Enable torch
camera.FlashMode = FlashMode.Strobe; // Strobe mode (future feature)

var currentMode = camera.GetFlashMode();
```

### Capture Flash Mode Control
```csharp
camera.CaptureFlashMode = CaptureFlashMode.Off;   // No flash
camera.CaptureFlashMode = CaptureFlashMode.Auto;  // Auto flash based on lighting
camera.CaptureFlashMode = CaptureFlashMode.On;    // Always flash

if (camera.IsFlashSupported)
{
    if (camera.IsAutoFlashSupported)
    {
        camera.CaptureFlashMode = CaptureFlashMode.Auto;
    }
}
```

### XAML Binding
```xml
<camera:SkiaCamera
    x:Name="CameraControl"
    FlashMode="Off"
    CaptureFlashMode="Auto"
    Facing="Default" />
```

### Flash Mode Cycling Examples
```csharp
// Preview torch cycling
private void OnTorchButtonClicked()
{
    var currentMode = camera.FlashMode;
    var nextMode = currentMode switch
    {
        FlashMode.Off => FlashMode.On,
        FlashMode.On => FlashMode.Off,
        FlashMode.Strobe => FlashMode.Off,
        _ => FlashMode.Off
    };
    camera.FlashMode = nextMode;
}

// Capture flash cycling
private void OnCaptureFlashButtonClicked()
{
    var currentMode = camera.CaptureFlashMode;
    var nextMode = currentMode switch
    {
        CaptureFlashMode.Off => CaptureFlashMode.Auto,
        CaptureFlashMode.Auto => CaptureFlashMode.On,
        CaptureFlashMode.On => CaptureFlashMode.Off,
        _ => CaptureFlashMode.Auto
    };
    camera.CaptureFlashMode = nextMode;
}
```

**Important Notes:**
- `FlashMode` controls preview torch (live view)
- `CaptureFlashMode` controls flash behavior during photo capture
- These are independent - you can have torch off but capture flash on Auto
- Flash capabilities vary by device and camera (front/back)

### Flash Architecture

SkiaCamera implements a **dual-channel flash system**:

| Channel | Property | Modes | Use Case |
|---------|----------|-------|----------|
| Preview Torch | `FlashMode` | Off/On/Strobe | Illumination while composing shots |
| Capture Flash | `CaptureFlashMode` | Off/Auto/On | Optimal lighting for still photos |

**Platform Implementation:**

| Platform | Preview Torch | Capture Flash | Auto Flash |
|----------|---------------|---------------|------------|
| **Android** | `FlashMode.Torch` | `FlashMode.Single` + `ControlAEMode` | `OnAutoFlash` |
| **iOS/macOS** | `AVCaptureTorchMode` | `AVCaptureFlashMode` | `Auto` mode |
| **Windows** | `FlashControl.Enabled` | `FlashControl.Auto` | Auto detection |

## 5. Capture Format Selection

### Quality Presets
```csharp
camera.PhotoQuality = CaptureQuality.Max;     // Highest resolution
camera.PhotoQuality = CaptureQuality.Medium;  // Balanced quality/size
camera.PhotoQuality = CaptureQuality.Low;     // Fastest capture
camera.PhotoQuality = CaptureQuality.Preview; // Smallest usable size
```

### Manual Format Selection
```csharp
var formats = await camera.GetAvailableCaptureFormatsAsync();

var options = formats.Select((format, index) =>
    $"[{index}] {format.Width}x{format.Height}, {format.AspectRatioString}"
).ToArray();

var result = await DisplayActionSheet("Select Capture Format", "Cancel", null, options);

if (!string.IsNullOrEmpty(result))
{
    var selectedIndex = Array.IndexOf(options, result);
    if (selectedIndex >= 0)
    {
        camera.PhotoQuality = CaptureQuality.Manual;
        camera.PhotoFormatIndex = selectedIndex;
    }
}
```

### Reading Current Format
```csharp
var currentFormat = camera.CurrentStillCaptureFormat;
if (currentFormat != null)
{
    Debug.WriteLine($"Current capture format: {currentFormat.Description}");
    Debug.WriteLine($"Resolution: {currentFormat.Width}x{currentFormat.Height}");
    Debug.WriteLine($"Aspect ratio: {currentFormat.AspectRatioString}");
}
```

### Automatic Preview Synchronization

When you change capture format, preview automatically adjusts to match the aspect ratio:

```csharp
camera.PhotoQuality = CaptureQuality.Manual;
camera.PhotoFormatIndex = 2; // Select 4000x3000 (4:3)
// Preview automatically switches to 4:3 — true WYSIWYG
```

### Format Caching
```csharp
var formats = await camera.GetAvailableCaptureFormatsAsync(); // Fast - uses cache
await camera.RefreshAvailableCaptureFormatsAsync(); // Slower - re-detects

// Cache is automatically cleared when camera facing or index changes
```

## 6. Opening Files in Gallery

```csharp
private async void OnCaptureSuccess(object sender, CapturedImage captured)
{
    try
    {
        var fileName = $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
        var filePath = Path.Combine(FileSystem.Current.CacheDirectory, fileName);

        using var fileStream = File.Create(filePath);
        using var data = captured.Image.Encode(SKEncodedImageFormat.Jpeg, 90);
        data.SaveTo(fileStream);

        camera.OpenFileInGallery(filePath);
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"Error opening file in gallery: {ex.Message}");
    }
}
```

**Platform Requirements:**
- **Android**: Requires FileProvider configuration (see [README setup section](../README.md#android))
- **iOS/macOS**: Works out of the box
- **Windows**: Opens with default photo viewer

## 7. Camera Selection

### Automatic Selection
```csharp
camera.Facing = CameraPosition.Default; // Back camera
camera.Facing = CameraPosition.Selfie;  // Front camera

private void SwitchCamera()
{
    if (camera.IsOn)
    {
        camera.Facing = camera.Facing == CameraPosition.Selfie
            ? CameraPosition.Default
            : CameraPosition.Selfie;
    }
}
```

### Manual Camera Selection
```csharp
var cameras = await camera.GetAvailableCamerasAsync();

foreach (var cam in cameras)
{
    Console.WriteLine($"Camera {cam.Index}: {cam.Name} ({cam.Position}) Flash: {cam.HasFlash}");
}

camera.Facing = CameraPosition.Manual;
camera.CameraIndex = 2; // Select third camera
camera.IsOn = true;
```

### CameraInfo Class

```csharp
public class CameraInfo
{
    public string Id { get; set; }           // Platform-specific camera ID
    public string Name { get; set; }         // Human-readable name
    public CameraPosition Position { get; set; } // Front/Back/Unknown
    public int Index { get; set; }           // Index for manual selection
    public bool HasFlash { get; set; }       // Flash capability
}
```

## 8. Captured Photo Processing

### Basic Capture
```csharp
camera.CaptureSuccess += OnCaptureSuccess;
camera.CaptureFailed += OnCaptureFailed;

private async void TakePicture()
{
    if (camera.State == HardwareState.On && !camera.IsBusy)
    {
        camera.FlashScreen(Color.Parse("#EEFFFFFF"));
        await camera.TakePicture().ConfigureAwait(false);
    }
}

private void OnCaptureSuccess(object sender, CapturedImage captured)
{
    var originalImage = captured.Image; // SKImage - full resolution
    var timestamp = captured.Time;
    var metadata = captured.Metadata;
    ProcessCapturedPhoto(originalImage, metadata);
}
```

### Command-Based Capture (MVVM)
```csharp
public ICommand CommandCapturePhoto => new Command(async () =>
{
    if (camera.State == HardwareState.On && !camera.IsBusy)
    {
        camera.FlashScreen(Color.Parse("#EEFFFFFF"));
        await camera.TakePicture().ConfigureAwait(false);
    }
});
```

## 9. Real-Time Effects & Custom Shaders

### Built-in Effects
```csharp
private void CycleEffects()
{
    var effects = new[]
    {
        SkiaImageEffect.None,
        SkiaImageEffect.Sepia,
        SkiaImageEffect.BlackAndWhite,
        SkiaImageEffect.Pastel,
        SkiaImageEffect.Custom
    };

    var currentIndex = Array.IndexOf(effects, camera.Effect);
    var nextIndex = (currentIndex + 1) % effects.Length;
    camera.Effect = effects[nextIndex];
}
```

### Custom Shader Effects
```csharp
public class CameraWithEffects : SkiaCamera
{
    private SkiaShaderEffect _shader;

    public void SetCustomShader(string shaderFilename)
    {
        if (_shader != null && VisualEffects.Contains(_shader))
            VisualEffects.Remove(_shader);

        _shader = new SkiaShaderEffect()
        {
            ShaderSource = shaderFilename,
            FilterMode = SKFilterMode.Linear
        };

        VisualEffects.Add(_shader);
        Effect = SkiaImageEffect.Custom;
    }

    public void ChangeShaderCode(string skslCode)
    {
        if (_shader != null)
            _shader.ShaderCode = skslCode; // Live shader editing!
    }
}
```

### Apply Shader Effect to Captured Photo

Use `RenderCapturedPhotoAsync` to bake a shader effect into a still photo after capture:

```csharp
private async void OnCaptureSuccess(object sender, CapturedImage captured)
{
    var imageWithEffect = await CameraControl.RenderCapturedPhotoAsync(
        captured,
        overlay: null,
        configureImage: image =>
        {
            var shaderEffect = new SkiaShaderEffect
            {
                ShaderSource = ShaderEffectHelper.GetFilename(CameraControl.VideoEffect),
            };
            image.VisualEffects.Add(shaderEffect);
        },
        useGpu: true);

    captured.Image.Dispose();
    captured.Image = imageWithEffect;
    SaveFinalPhotoInBackground(captured);
}
```

Use named arguments here because `drawOverlay` now sits before `configureImage` in the overload.

### Shader Preview Grid (Like Instagram Filters)
```xml
<draw:SkiaLayout Type="Row" ItemsSource="{Binding ShaderItems}">
    <draw:SkiaLayout.ItemTemplate>
        <DataTemplate x:DataType="models:ShaderItem">
            <draw:SkiaShape WidthRequest="80" HeightRequest="80">
                <draw:SkiaImage
                    ImageBitmap="{Binding Source={x:Reference CameraControl}, Path=DisplayPreview}"
                    Aspect="AspectCover">
                    <draw:SkiaImage.VisualEffects>
                        <draw:SkiaShaderEffect ShaderSource="{Binding ShaderFilename}" />
                    </draw:SkiaImage.VisualEffects>
                </draw:SkiaImage>
            </draw:SkiaShape>
        </DataTemplate>
    </draw:SkiaLayout.ItemTemplate>
</draw:SkiaLayout>
```

## 10. Zoom Control

```csharp
camera.Zoom = 2.0; // 2x zoom

private void ZoomIn()
{
    camera.Zoom = Math.Min(camera.Zoom + 0.2, camera.ZoomLimitMax);
}

private void ZoomOut()
{
    camera.Zoom = Math.Max(camera.Zoom - 0.2, camera.ZoomLimitMin);
}

// Pinch-to-zoom gesture
private void OnZoomed(object sender, ZoomEventArgs e)
{
    camera.Zoom = e.Value;
}
```

## 11. Camera State Management

```csharp
camera.StateChanged += OnCameraStateChanged;

private void OnCameraStateChanged(object sender, HardwareState newState)
{
    switch (newState)
    {
        case HardwareState.Off:
            break;
        case HardwareState.On:
            break;
        case HardwareState.Error:
            break;
    }
}

if (camera.State == HardwareState.On)
{
    // Safe to perform camera operations
}
```

## 12. Live Processing: ProcessFrame & ProcessPreview

SkiaCamera provides **two drawing callbacks** for real-time overlay rendering, plus an **event** for read-only frame analysis:

| Callback / Event | Type | When It Fires | Use Case |
|------------------|------|---------------|----------|
| `ProcessFrame` | `Action<DrawableFrame>` | Each frame being **encoded to video** | Watermarks, telemetry, overlays baked into recorded video |
| `ProcessPreview` | `Action<DrawableFrame>` | Each **preview** frame before display | Show overlays on live preview (e.g., gauges, guides) |
| `NewPreviewSet` | `EventHandler<LoadedImageSource>` | Each preview frame after display | Read-only analysis of displayed preview, UI validation, processed-preview scanning |

> **Key Insight**: `ProcessFrame` draws on what gets **recorded**. `ProcessPreview` draws on what the user **sees**. `NewPreviewSet` lets you **read** already processed preview frames. All three are independent.

### ProcessFrame (Video Recording Overlay)

Draws on each frame being encoded to the video file. Requires `UseRealtimeVideoProcessing = true`. Scale is always 1.0 (full recording resolution).

```csharp
camera.UseRealtimeVideoProcessing = true;
camera.ProcessFrame = (frame) =>
{
    using var paint = new SKPaint
    {
        Color = SKColors.White.WithAlpha(128),
        TextSize = 48 * frame.Scale,
        IsAntialias = true
    };
    frame.Canvas.DrawText($"REC {frame.Time:mm\\:ss}", 50, 100, paint);
};
```

### ProcessPreview (Live Preview Overlay)

Draws on each preview frame before it is displayed to the user.

```csharp
camera.ProcessPreview = (frame) =>
{
    using var paint = new SKPaint
    {
        Color = SKColors.LimeGreen,
        TextSize = 48 * frame.Scale,
        IsAntialias = true
    };
    frame.Canvas.DrawText("READY", 50 * frame.Scale, 100 * frame.Scale, paint);
};
```

### Using Both Together

```csharp
void DrawOverlay(DrawableFrame frame)
{
    var s = frame.Scale; // 1.0 for recording, PreviewScale for preview
    using var paint = new SKPaint
    {
        Color = SKColors.White,
        TextSize = 48 * s,
        IsAntialias = true
    };

    frame.Canvas.DrawText($"{frame.Time:mm\\:ss}", 50 * s, 100 * s, paint);

    if (!frame.IsPreview)
    {
        frame.Canvas.DrawText("WATERMARK", 50 * s, 160 * s, paint);
    }
}

camera.UseRealtimeVideoProcessing = true;
camera.ProcessFrame = DrawOverlay;
camera.ProcessPreview = DrawOverlay;
```

### Reuse ProcessFrame / ProcessPreview on Captured Still Photo

`RenderCapturedPhotoAsync` can now run the same `Action<DrawableFrame>` callback over a captured still photo.
`drawOverlay` runs after the captured image is rendered and before any optional `SkiaLayout` overlay is rendered.

```csharp
private async void OnCaptureSuccess(object sender, CapturedImage captured)
{
    var imageWithOverlay = await CameraControl.RenderCapturedPhotoAsync(
        captured,
        overlay: null,
        drawOverlay: CameraControl.ProcessFrame,
        useGpu: true);

    captured.Image.Dispose();
    captured.Image = imageWithOverlay;
}
```

Notes:
- The generated still-photo `DrawableFrame` uses callback-space dimensions for the replayed overlay viewport.
- For rotated stills, `drawOverlay` is replayed using the captured device orientation so reused preview/recording overlay code sees the expected viewport orientation.
- `Scale` defaults to `1.0` unless you pass a custom value.
- `IsPreview` is currently `false`, so draw-overlay code follows the non-preview branch.

### Add a Pre-Overlay Still Stage

Use the new `composeBase` overload when your live preview path depends on a canvas composition step before `ProcessPreview`.

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
- `configureImage` configures the intermediate `SkiaImage`
- `composeBase` composes the rendered still into the destination canvas
- `drawOverlay` draws reusable `DrawableFrame` overlays in replayed callback space based on the captured device orientation
- `overlay.Render(...)` draws any DrawnUI overlay tree

Performance note:
- the extra preparation pass exists only when `composeBase` is supplied
- existing overloads keep the direct-render path and do not spend time on the pre-overlay stage

### DrawableFrame Properties

| Property | Type | Description |
|----------|------|-------------|
| `Canvas` | `SKCanvas` | SkiaSharp canvas for drawing on the frame |
| `Width` | `int` | Frame width in pixels |
| `Height` | `int` | Frame height in pixels |
| `Time` | `TimeSpan` | Elapsed time since recording started |
| `IsPreview` | `bool` | `true` for preview frames, `false` for recording frames |
| `Scale` | `float` | 1.0 for recording frames; `PreviewScale` for preview frames |

For still-photo rendering via `RenderCapturedPhotoAsync(..., drawOverlay: ...)`, `DrawableFrame` uses replayed callback-space dimensions, custom `scale` if provided, and currently sets `IsPreview = false`.

### Camera Layout Coordinates

Two properties expose the camera's position on the canvas:

| Property | Type | Description |
|----------|------|-------------|
| `DrawingRect` | `SKRect` | Full bounding rect of the control on the canvas |
| `DisplayRect` | `SKRect` | Actual image area when using `Fit` aspect — excludes letterbox bars |

Use `DisplayRect` when mapping screen touch coordinates to camera frame coordinates, or when positioning overlays that must stay inside the actual video area.

### NewPreviewSet Event (Read-Only Analysis)

Use `NewPreviewSet` when you want to inspect the **displayed preview** exactly as the user sees it.

What it sees:
- `Effect` and preview shader output
- `ProcessPreview` drawing
- during recording, `ProcessFrame` overlays too when `UseRecordingFramesForPreview = true`

Use it for:
- preview QA/inspection
- scanning the final on-screen preview
- workflows that intentionally depend on what the user sees

Do not use it when the model needs clean camera pixels independent from overlays/effects. For that use the raw-frame hook in section 13.

For AI/ML processing where you need to **read** preview frames without drawing on them:

```csharp
camera.NewPreviewSet += OnNewPreviewFrame;

private void OnNewPreviewFrame(object sender, LoadedImageSource source)
{
    source.ProtectFromDispose = true;
    
    if (!_mlSemaphore.Wait(0))
    {
        source.ProtectFromDispose = false;
        return;
    }

    Task.Run(async () =>
    {
        try
        {
            await RunMLAsync(source.Image);
        }
        finally
        {
            source.ProtectFromDispose = false;
            source.Dispose();
            _mlSemaphore.Release();
        }
    });
}
```

## 13. Raw Frame ML Hook: OnRawFrameAvailable(RawCameraFrame) & TryGetRgba

When you need to run ML/AI inference on **raw camera frames** before any `ProcessFrame` overlay is composited, override `OnRawFrameAvailable(RawCameraFrame frame)` and call `frame.TryGetRgba(...)`.

> **Why not `NewPreviewSet`?** During recording with `UseRecordingFramesForPreview = true` (default), the preview already has `ProcessFrame` overlays baked in.

| Member | Kind | Description |
|--------|------|-------------|
| `OnRawFrameAvailable(RawCameraFrame frame)` | `protected internal virtual` | Called every frame with a temporary raw-frame context. Use `frame.TryGetRgba(...)` for portable AI/ML input. |
| `frame.RawImage` | `SKImage?` | Optional advanced access to the raw frame. May be null on zero-copy GPU paths. Valid only inside the callback. |
| `frame.Rotation` | `int` | Degrees the caller must still rotate `frame.RawImage` by to reach display orientation. Ignore when you use `frame.TryGetRgba(...)`. |
| `frame.TryGetRgba(int targetWidth, int targetHeight, byte[] outputBuffer)` | `bool` | GPU-accelerated scale + pixel readback into a pre-allocated RGBA8888 byte array. |
| `frame.TryGetRgbaBytes(int targetWidth, int targetHeight, out byte[]? rgbaBytes)` | `bool` | Returns an owned raw `RGBA8888` buffer, useful for custom endpoints that accept raw pixels. |
| `frame.TryGetJpeg(int targetWidth, int targetHeight, out byte[]? jpegBytes, int quality = 100)` | `bool` | Returns a JPEG payload encoded from display-oriented frame pixels. Good fit for hosted multimodal APIs. |
| `frame.TryGetPng(int targetWidth, int targetHeight, out byte[]? pngBytes)` | `bool` | Returns a PNG payload encoded from display-oriented frame pixels when lossless upload is needed. |

### Platform Implementation

| Platform | GPU mechanism |
|----------|---------------|
| iOS / MacCatalyst | `MetalPreviewScaler` — Metal compute shader |
| Android (GPU) | `GlPreviewScaler` — `glBlitFramebuffer` + `glReadPixels` |
| Android (legacy) | CPU `SKSurface + DrawImage` |
| Windows | GPU `SKSurface` backed by encoder's `GRContext` |

### Usage

```csharp
public class MyCam : SkiaCamera
{
    private readonly byte[] _mlBuffer = new byte[224 * 224 * 4]; // RGBA8888
    private readonly SemaphoreSlim _mlSemaphore = new(1, 1);

    protected override void OnRawFrameAvailable(RawCameraFrame frame)
    {
        // MUST be called synchronously (Android GPU: EGL context is current)
        if (!frame.TryGetRgba(224, 224, _mlBuffer))
            return;

        if (!_mlSemaphore.Wait(0))
            return;

        var snapshot = _mlBuffer.ToArray();
        int rotation = frame.Rotation;

        Task.Run(() =>
        {
            try   { RunInference(snapshot, rotation); }
            finally { _mlSemaphore.Release(); }
        });
    }
}
```

### Buffer Layout

`outputBuffer` is filled with raw **RGBA8888** pixels, `targetWidth * targetHeight * 4` bytes, top-to-bottom, no row padding.

### Which export method to use

- Use `frame.TryGetRgba(...)` when you already own a reusable buffer and want the fastest hot-loop path.
- Use `frame.TryGetRgbaBytes(...)` when a custom backend expects raw `RGBA8888` plus separate width/height metadata.
- Use `frame.TryGetJpeg(...)` for hosted AI APIs that expect normal image payloads and do not need lossless data.
- Use `frame.TryGetPng(...)` when the destination expects a standard image format but you want lossless encoding.

### Comparison of raw-frame ML options

| API | Fires when | Has overlays? | GPU path? | Zero-alloc? |
|-----|-----------|---------------|-----------|-------------|
| `NewPreviewSet` | After preview display | Yes (when recording) | No | No |
| `ProcessPreview` callback | Preview compositing | Partial | No | No |
| **`OnRawFrameAvailable(RawCameraFrame)` + `frame.TryGetRgba(...)`** | Before any compositing | **Never** | **Yes** | **Yes** |

## 14. Permission Handling

All permission methods are **static** and operate on the main thread internally.

### NeedPermissions flags enum

```csharp
[Flags]
public enum NeedPermissions
{
    Camera     = 1,
    Gallery    = 2,
    Microphone = 4,
    Location   = 8
}

var flags = NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone;
```

### CheckPermissions (shows system dialogs)

```csharp
SkiaCamera.CheckPermissions(
    granted:    () => camera.IsOn = true,
    notGranted: () => ShowPermissionsError(),
    request:    NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone);

// Async wrapper
bool ok = await SkiaCamera.RequestPermissionsAsync(
    NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone);
```

### CheckPermissionsGranted (silent, no dialogs)

```csharp
SkiaCamera.CheckPermissionsGranted(
    granted:    () => camera.IsOn = true,
    notGranted: () => ShowOnboardingScreen(),
    request:    NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone);

// Async wrapper
bool alreadyGranted = await SkiaCamera.RequestPermissionsGrantedAsync(
    NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone);
```

### NeedPermissionsSet (instance property)

Controls which permissions are checked automatically when the camera turns on via `IsOn = true`. Defaults to `Camera | Gallery`.

```csharp
camera.NeedPermissionsSet = NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone;
camera.IsOn = true;
```

### Typical Onboarding Flow

```csharp
bool alreadyGranted = await SkiaCamera.RequestPermissionsGrantedAsync(
    NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone);

if (alreadyGranted)
{
    camera.IsOn = true;
}
else
{
    bool granted = await SkiaCamera.RequestPermissionsAsync(
        NeedPermissions.Camera | NeedPermissions.Gallery | NeedPermissions.Microphone);

    if (granted)
        camera.IsOn = true;
    else
        ShowOpenSettingsHint();
}
```

## 15. Complete MVVM Example

### ViewModel
```csharp
public class CameraViewModel : INotifyPropertyChanged, IDisposable
{
    private SkiaCamera _camera;

    public void AttachCamera(SkiaCamera camera)
    {
        if (_camera == null && camera != null)
        {
            _camera = camera;
            _camera.CaptureSuccess += OnCaptureSuccess;
            _camera.StateChanged += OnCameraStateChanged;
            _camera.NewPreviewSet += OnNewPreviewSet;
        }
    }

    public ICommand CommandCapturePhoto => new Command(async () =>
    {
        if (_camera?.State == HardwareState.On && !_camera.IsBusy)
        {
            _camera.FlashScreen(Color.Parse("#EEFFFFFF"));
            await _camera.TakePicture().ConfigureAwait(false);
        }
    });

    public ICommand CommandSwitchCamera => new Command(() =>
    {
        if (_camera?.IsOn == true)
        {
            _camera.Facing = _camera.Facing == CameraPosition.Selfie
                ? CameraPosition.Default
                : CameraPosition.Selfie;
        }
    });

    private void OnCaptureSuccess(object sender, CapturedImage captured)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Update UI with captured image
        });
    }

    public void Dispose()
    {
        if (_camera != null)
        {
            _camera.CaptureSuccess -= OnCaptureSuccess;
            _camera.StateChanged -= OnCameraStateChanged;
            _camera.NewPreviewSet -= OnNewPreviewSet;
            _camera = null;
        }
    }
}
```

### Page Code-Behind
```csharp
public partial class CameraPage : ContentPage
{
    private readonly CameraViewModel _viewModel;

    public CameraPage(CameraViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = _viewModel;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        var camera = this.FindByName<SkiaCamera>("CameraControl");
        _viewModel.AttachCamera(camera);

        SkiaCamera.CheckPermissions(async (granted) =>
        {
            if (granted)
            {
                camera.IsOn = true;
            }
        });
    }
}
```

## 16. Code Detection (QR and barcodes)

The camera can report machine-readable codes it sees in the live preview. The platform's capture
pipeline does the detection, so your code never reads frame pixels for it and nothing is added to
the render thread.

**Platforms:** iOS. `SkiaCamera.IsCodeDetectionSupported` is `false` on Android, Windows and
Mac Catalyst for now; setting `CodeDetection` there does nothing.

| Member | Description |
|--------|-------------|
| `CodeDetection` | Flags of `CameraCodeTypes` to look for. Default `None`: detection is off and costs nothing. Changing it while the camera is on restarts the camera, so set it before `IsOn = true` when you can. |
| `CodesDetected` | `EventHandler<IReadOnlyList<DetectedCode>>`. Raised while codes are in view (up to once per camera frame) and once with an empty list when they are gone or the camera stops. **Not raised on the UI thread or the render thread.** |
| `DetectedCode` | `Value` (decoded text), `Type` (one `CameraCodeTypes` flag), `Corners` (normalized 0..1 inside the displayed preview image). |
| `TryMapPreviewPoint` | Maps a normalized corner to canvas pixels through `DisplayRect` and `MirrorPreviewX/Y`. |

```csharp
if (SkiaCamera.IsCodeDetectionSupported)
{
    camera.CodeDetection = CameraCodeTypes.Qr;   // before IsOn = true: no restart
    camera.CodesDetected += OnCodesDetected;
}

void OnCodesDetected(object sender, IReadOnlyList<DetectedCode> codes)
{
    // background thread, called often: do little here, skip repeats, then go to the UI thread
    foreach (var code in codes)
    {
        if (!Uri.TryCreate(code.Value, UriKind.Absolute, out var link)
            || (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps))
            continue;

        if (camera.TryMapPreviewPoint(code.Corners[0], out var pixels))
        {
            var x = pixels.X / camera.RenderingScale;   // points
            var y = pixels.Y / camera.RenderingScale;
            MainThread.BeginInvokeOnMainThread(() => ShowLink(link, x, y));
        }
        return;
    }

    MainThread.BeginInvokeOnMainThread(HideLink);
}
```

Notes:

- **`Value` is untrusted input.** It is whatever someone printed. Check it before you act on it, and
  never open a link or launch anything without the user asking for it.
- The same code arrives on every camera frame while it is in view. Compare with the last one and
  update your UI only when the text or the position really changed.
- A mapped point can fall outside the control when the preview is cropped by its `Aspect`. Hide or
  clamp your overlay then.
- Corner winding is not guaranteed (a mirrored selfie preview reverses it). Use the corners as a
  set: their average is the center, min/max give the box.
- Up to four 2D codes are reported at once, and one 1D code (an iOS limit).
- While recording with `UseRecordingFramesForPreview`, the preview shows encoder frames, which are
  center-cropped to the video aspect. When the sensor format has another aspect, mapped points are
  offset during the recording. Hide code overlays while `IsRecording` if that matters to you.

### Passkey sign-in codes

A site that offers "sign in with a passkey on another device" shows a QR code. Its text is `FIDO:/`
followed by digits. The system can take it from there: it shows its own passkey sheet, checks that
the two devices are near each other and signs the user in. Your app only passes the code on and
never sees a key.

| Member | Description |
|--------|-------------|
| `DetectedCode.IsPasskeySignIn` | True when the code's text is `FIDO:/` plus digits only. |
| `SkiaCamera.IsPasskeySignInSupported` | True where the system can take such a code. iOS 16 and later for now. |
| `SkiaCamera.StartPasskeySignInAsync(code)` | Hands the code to the system. Returns false when it is not a passkey code, the platform cannot do it, or the system refused. Safe to call from any thread. |

```csharp
void OnCodesDetected(object sender, IReadOnlyList<DetectedCode> codes)
{
    foreach (var code in codes)
    {
        if (code.IsPasskeySignIn && SkiaCamera.IsPasskeySignInSupported)
        {
            MainThread.BeginInvokeOnMainThread(() => ShowPasskeyButton(code));   // your UI
            return;
        }
    }
}

// when the user taps your button
async void OnPasskeyTapped(DetectedCode code)
{
    var started = await SkiaCamera.StartPasskeySignInAsync(code);
}
```

Start it only from a user action such as a tap, never by itself when the code is detected. The code
is short-lived: the site replaces it after a while, so use the one detected last.

## 17. Mock Source (a still image instead of the camera)

Set `MockSource` to show a still image as the live camera feed: store screenshots, demos, the iOS
Simulator (it has no camera), automated tests. The hardware camera stays off and the camera
permission is not requested, but everything after the camera behaves as usual: `State` goes `On`,
your preview shaders, `ProcessPreview`, `NewPreviewSet` and `OnRawFrameAvailable` receive the
image about 30 times per second, and `TakePicture()` returns it through `CaptureSuccess`.

```csharp
// a debug menu item
async Task ToggleMockSource()
{
    if (CameraControl.MockSource != null)
    {
        CameraControl.MockSource = null;   // back to the camera
        return;
    }

    using var stream = await FileSystem.OpenAppPackageFileAsync("Images/model.jpg");
    CameraControl.MockSource = SKImage.FromEncodedData(stream);
}
```

- Set it before or after `IsOn = true`. Setting it while the camera runs stops the hardware;
  clearing it starts the hardware again.
- The photo from `TakePicture()` is the image as shown (`Rotation = 0`, `DeviceRotation = 0`, `Meta.Orientation = 1`).
  Render your filter into it with `RenderCapturedPhotoAsync` and save it with `SaveToGalleryAsync` as usual.
- Video recording is not available while it is set: `StartVideoRecording()` fails with `NotSupportedException`.
- The pixels are copied when set; you keep owning the image. Use upright pixels: `SKImage.FromEncodedData` ignores EXIF orientation.
- When `MockSource` is null there is no cost: no timer, a null check per frame.

The sample app has a "Mock" button in its settings drawer. Full contract: [API Reference, Mock Source](api-reference.md#mock-source).
