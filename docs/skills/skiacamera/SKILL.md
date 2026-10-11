---
name: skiacamera
description: Working with DrawnUi.Maui.Camera (SkiaCamera) - device-specific gotchas learned on real phones. Load before touching camera start, format selection, permissions, preview code or MockSource.
---

# SkiaCamera field notes

## iOS: 10-bit formats give a black FIRST session (iPhone 16 Pro / 17 Pro, iOS 26)

- Devices with 10-bit HDR sensors expose each resolution several times, one per pixel format:
  `420v`, `420f` (8-bit) and `x420`, `x422` (10-bit). Selecting a 10-bit one for the
  `AVCaptureVideoDataOutput` (which asks for 32BGRA) delivers NO frames on the process's first
  session: `State` is `On`, the daemon runs at 60 fps, the app-side output counter stays at 0.
  Any later session in the same process works, so "switch to video and back fixes it" is the tell.
- Never pick formats by index into the flat list; duplicates make "the middle entry" arbitrary.
  Since 1.10.6.13 `GetFilteredFormats` keeps 8-bit formats only and quality is a megapixel budget
  over distinct still sizes (`SelectFormatByPixelBudget`). Keep it that way.
- To verify on a device without lib logs: `idevicesyslog -p <app> -p cameracaptured`, then compare
  `_setActiveFormat:` (FourCC of the chosen format), the daemon `FigCaptureFrameCounter` and the
  app-process `FigCaptureFrameCounter ... -> <AVCaptureVideoDataOutput>` lines.

## iOS: permission checks

- `CheckPermissions` runs OS prompts on the main thread on purpose: `CLLocationManager` created off
  the main thread never prompts and never completes, and the awaits resume on arbitrary threads.
- Concurrent callers are queued (1.10.6.13), not dropped. Dropping them let a second debounced
  restart stop the session without a start: `State` On, viewfinder black.
- Apps should gate the viewfinder on the camera permission only (`NeedPermissionsSet = Camera`) and
  ask Photos at save time. A refused Photos prompt must not black out the camera.

## iOS: restarts and exposure

- A Facing / format / quality change restarts through a 500 ms debounce; the old device's frames keep
  flowing until then, and `State` goes On BEFORE the first new frame arrives. Anything keyed on the
  new device (mirror, thumbnails) must wait for the first frame, not for `On`.
- After a (re)start the auto exposure ramps for up to a second: first frames are black, then over-
  and under-shoot. `IsAdjustingExposure` (KVO on `adjustingExposure`) tells when it settled.

## Mock source: a still image instead of the camera

- `camera.MockSource = SKImage.FromEncodedData(stream)` replaces the hardware with the image: store
  screenshots, demos, the iOS Simulator (no camera), tests. The hardware is not started and the camera
  permission is not asked (the other `NeedPermissionsSet` flags are). `State` goes On as usual.
- Frames arrive about 30 per second through the normal camera path, so preview shaders
  (`RenderPreviewForProcessing`), `ProcessPreview`, `NewPreviewSet` and `OnRawFrameAvailable` +
  `frame.TryGetRgba` all see the image. Test the app's live looks on it as on a camera feed.
- Use upright pixels: `SKImage.FromEncodedData` ignores EXIF orientation. A GPU texture image cannot be
  read and is ignored (logged). The camera copies the pixels, the caller keeps the image.
- `TakePicture()` returns the image as `CapturedImage` (`Rotation = 0`, `DeviceRotation = 0`,
  `Meta.Orientation = 1`); `RenderCapturedPhotoAsync` and `SaveToGalleryAsync` work unchanged.
- Video recording is not available: `StartVideoRecording()` raises `RecordingFailed` and throws
  `NotSupportedException`. A REC screen cannot be shown from a mock.
- Set or clear it any time outside a recording: setting it while the camera runs stops the hardware,
  clearing it starts the hardware. Facing, format and capture-mode changes restart it like the camera.
- Keep it out of release builds yourself (a debug menu); when null it costs nothing.
