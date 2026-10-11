---
name: skiacamera-multilens-zoom
description: Pinch-to-zoom across the several back cameras of an iPhone (ultra-wide, wide, telephoto) with DrawnUi.Maui.Camera (SkiaCamera), like the Apple Camera app. How iOS virtual camera devices switch lenses, what blocks the switching, what SkiaCamera does today, how to build it and how to test it on a phone. Load before adding finger zoom or lens switching to a SkiaCamera screen, or before changing the library's Apple device selection or zoom code.
---

# Multi-lens zoom on iPhone with SkiaCamera

## The goal

The user pinches on the preview and the zoom changes smoothly, from the ultra-wide lens to the
telephoto lens, in one gesture, like the Apple Camera app. The app never picks a lens itself:
iOS swaps the physical camera when the zoom crosses a switch-over point.

Apple Camera behavior to match:
- it opens on the main (wide) lens, labeled "1x";
- pinching out goes to the ultra-wide ("0.5x"), pinching in goes to the telephoto ("2x", "3x", "5x" depending on the model);
- when the subject is very close, iOS stays on the ultra-wide (macro), whatever the zoom.

## How iOS does it: virtual devices

Open ONE virtual device. It contains several physical cameras ("constituents") and switches between them by zoom factor.

| Device type | Constituents | Phones |
|---|---|---|
| `BuiltInTripleCamera` | ultra-wide + wide + tele | Pro / Pro Max models |
| `BuiltInDualWideCamera` | ultra-wide + wide | non-Pro models with an ultra-wide |
| `BuiltInDualCamera` | wide + tele | older Plus / X / XS models |
| `BuiltInWideAngleCamera` | one physical lens | every phone (fallback) |

Pick in this order: Triple, then DualWide, then Dual, then WideAngle, with
`AVCaptureDevice.GetDefaultDevice(type, AVMediaTypes.Video, AVCaptureDevicePosition.Back)`.

Zoom on a virtual device:
- `VideoZoomFactor` 1.0 = the full view of the WIDEST constituent (the ultra-wide when there is one). There is one zoom value for the whole range; no lens code in the app.
- `VirtualDeviceSwitchOverVideoZoomFactors` = the zoom factors where the next lens takes over. iPhone 16 Pro triple: `[2, 10]` (ultra-wide below 2, wide from 2, tele from 10). It differs per model (tele 3x or 5x): always read it at run time, never hardcode.
- Labels like Apple's: label = `VideoZoomFactor * DisplayVideoZoomFactorMultiplier` (iOS 18+, 0.5 on the 16 Pro triple, so 2.0 shows "1x" and 10.0 shows "5x"). Before iOS 18, with an ultra-wide constituent, divide by the first switch-over factor instead.
- `MinAvailableVideoZoomFactor` / `MaxAvailableVideoZoomFactor` bound the zoom you can set. They change with the session state (see the depth lock below). Clamp to them every time.
- `ActivePrimaryConstituentDevice` (iOS 15+) = the physical lens in use now. Use it for logs and for an "active lens" UI, not to drive the zoom.
- `PrimaryConstituentDeviceSwitchingBehavior` (iOS 15+): keep `Auto` (the default) for Apple-like behavior. `Restricted` / `Locked` exist when an app must avoid switching (for example while recording).
- With `Auto`, iOS also uses a different lens than the zoom range suggests when the subject is close (close-focus fallback to the ultra-wide, see `FallbackPrimaryConstituentDevices`) or in low light (tele falls back to wide). That is normal, not a bug.
- For a zoom button (not a pinch), `RampToVideoZoom(factor, rate)` animates the zoom smoothly (binding name not checked on device).

## What blocks the lens switch (found on device)

Verified on iPhone 16 Pro, iOS 26.6.1:

1. **A depth output locks the zoom.** With an `AVCaptureDepthDataOutput` in the session, `BuiltInDualWideCamera` reported `MinAvailableVideoZoomFactor = 2.0` and stayed on the wide lens: the ultra-wide was gone. Depth needs the wide lens. Removing the output gave min 1.0 and the ultra-wide back. On `BuiltInTripleCamera` the same output did not lock (its `ActiveDepthDataFormat` was null with the chosen format). Rule: never add a depth output that nothing reads.
2. **Testing on a near subject looks like a broken switch.** Aimed at a monitor at desk distance, the triple camera stayed on the ultra-wide from 1x to 8x (a soft digital crop) for every code variant tried; `LensPosition` was 0.00. Aimed at a subject about 4 m away, it switched as designed: ultra-wide below 2.0, wide from 2.0, tele from about 10.5 (last sample on wide at 10.38); `LensPosition` 0.58 to 0.75.
3. **Not involved** (tested, switching worked with them): the deprecated `AVCaptureStillImageOutput`, `AVCaptureSession.PresetInputPriority` with a manually set `ActiveFormat`, a 1280x720 format and a 1920x1080 60 fps MultiCam format.

Format facts from the same phone: the triple camera has 55 formats; on iOS 26, `HighResolutionStillImageDimensions` never goes above 4224x2376 (16:9) or 4032x3024 (4:3); the 16:9 MultiCam formats list `SecondaryNativeResolutionZoomFactors = [4]`.

## Two zoom layers: lens zoom and texture zoom

A working pattern (from a production AVFoundation camera that switches lenses correctly):
- zoom inside `[MinAvailableVideoZoomFactor, MaxAvailableVideoZoomFactor]`: set `VideoZoomFactor` only, the preview texture stays at scale 1;
- zoom outside that range (for example below 1.0 to show a "wider than the lens" frame, or beyond the hardware maximum): `VideoZoomFactor` stays at the limit, and only the remainder is applied as a texture (display) scale;
- the photo gets the lens zoom for free (it comes from the sensor). A texture zoom is preview-only unless the capture code crops the photo the same way.

## What SkiaCamera does

Built after 1.10.7.61. Verified on Windows (sample test mode); the iPhone and Android device round is pending.

- **Zoom units**: `Zoom` is in Apple label units on every platform: 1 = main lens, 0.5 = ultra-wide, 5 = the 16 Pro tele. Android `CONTROL_ZOOM_RATIO` uses the same units. Default `Zoom` 1, `ZoomLimitMin` 1, `ZoomLimitMax` 10 (in label units 10 already reaches a 5x tele).
- **Device choice** (iOS back camera): `Type = CameraType.Max` opens Triple, then DualWide, then Dual, then the first back camera. `Type = Default` stays one physical lens. A change restarts the camera.
- **One zoom path**: the shared `SetZoom` asks the platform for the zoom the hardware can show (`ApplyHardwareZoom`) and scales the preview only by the rest: `TextureScale = Display.ZoomX/Y = Zoom / hardware`. `RenderCapturedPhotoAsync` applies `TextureScale` to the photo, so the photo matches. Before this, Apple and Windows applied the lens zoom AND the same display zoom.
  - iOS: `VideoZoomFactor = Zoom / units`, clamped to `Min/MaxAvailableVideoZoomFactor`; `units` = `DisplayVideoZoomFactorMultiplier` (iOS 18, net10 builds), else 1 / the first switch-over factor for Triple and DualWide, else 1.
  - Android (11+): `CONTROL_ZOOM_RATIO` on preview, recording, still and AE-reset requests, clamped to `CONTROL_ZOOM_RATIO_RANGE`. The raw `CapturedImage` comes zoomed. Older versions: preview scaling only.
  - Windows and `MockSource`: preview scaling only.
- **Restarts**: on every `State` On the camera re-reads its zoom range and applies `Zoom` again (without raising `Zoomed`).
- **Read-only API for a zoom UI**: `ZoomHardwareMin` (0.5 with an ultra-wide), `ZoomHardwareMax`, `ZoomLensSwitchPoints` (the switch-over factors in label units: `[1, 5]` on the 16 Pro). Empty switch points on Android and Windows.
- **No pinch gesture in the library.** Apps put a DrawnUi `SkiaHotspotZoom` over the camera and set `camera.Zoom = e.Value`; its `ZoomMin`/`ZoomMax` should follow `ZoomHardwareMin` and `ZoomLimitMax`.
- **Start lens**: the app's choice. `Zoom` 1 opens on the main lens like Apple Camera; `Zoom` 0.5 on the ultra-wide.
- **Session**: no depth output (good). Stills through `AVCapturePhotoOutput` since 1.10.7.61.

## Open items

- Android switch points: compute them from the physical cameras of a logical multi-camera (focal lengths and sensor sizes) if a UI needs lens buttons on Android.
- No `ActiveLens` property (declined for now). iOS has `ActivePrimaryConstituentDevice`, Android `LOGICAL_MULTI_CAMERA_ACTIVE_PHYSICAL_ID`.
- Device round: Pro and non-Pro iPhone, an Android phone with a logical multi-camera (Pixel, Samsung). Check that 48 MP stills still work on the virtual device.

## How to test on a phone

- Use a phone with three back cameras (any Pro). The iPhone 16 Pro has the same cameras as the 16 Pro Max. Also use a non-Pro phone (ultra-wide + wide) for the DualWide path.
- Aim at a subject more than 3 m away. A near subject keeps the ultra-wide at every zoom (macro fallback) and proves nothing.
- Temporary logs:
  - at setup: `DeviceType`, `ConstituentDevices`, `VirtualDeviceSwitchOverVideoZoomFactors`, `DisplayVideoZoomFactorMultiplier`, `Min/MaxAvailableVideoZoomFactor`, the active format size;
  - on every zoom change: `VideoZoomFactor`, `ActivePrimaryConstituentDevice?.DeviceType`, `LensPosition`, `Min/MaxAvailableVideoZoomFactor`.
- Pinch slowly from the minimum to the maximum and back. Note the zoom value where each lens takes over and compare it with the switch-over factors.
- Screenshot the preview at each lens and the Apple Camera app at 0.5x / 1x / tele of the same scene: the fields of view must match.
- Take a photo at each lens: it must match the preview.
- Remove every temporary log when done.
