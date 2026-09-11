---
name: skiacamera
description: Working with DrawnUi.Maui.Camera (SkiaCamera) - device-specific gotchas learned on real phones. Load before touching camera start, format selection, permissions or preview code.
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
