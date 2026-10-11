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

## What SkiaCamera does today

Source-read at camera repo commit d4f21f0. NOT verified on a device.

- **Device choice**: `SkiaCamera.Type` (`CameraType.Default` / `Max`, default `Default`, a change restarts the camera). Apple `NativeCamera.SetupHardware`:
  - `Max`: `BuiltInTripleCamera` (iOS 13+), else `BuiltInDualCamera`, else the first back camera from `DevicesWithMediaType`.
  - `Default`: the first back camera, which is a single physical lens. No lens switching.
  - Gap: there is no `BuiltInDualWideCamera` step, so a non-Pro iPhone gets no ultra-wide, even with `Max`.
- **Session**: no depth output (good), stills through `AVCaptureStillImageOutput`, preset `InputPriority` with `ActiveFormat` set from the filtered 8-bit formats.
- **Zoom**: bindable `Zoom` (default 1.0), clamped to `ZoomLimitMin` (default 1) and `ZoomLimitMax` (default 10), then `SetZoom(value)` per platform:
  - Apple: `NativeCamera.SetZoom` sets `VideoZoomFactor` (clamped to Min/MaxAvailable), AND `SetZoom` also sets `Display.ZoomX/ZoomY = value` and `TextureScale = value`. Read literally, the preview gets the lens zoom and the same display zoom on top (about value squared). Check this on a device before building on it.
  - Android: no hardware zoom. Only `Display.ZoomX/ZoomY` (texture zoom). Whether the photo is cropped to match is not checked.
  - Windows: `ZoomControl` clamped, plus the same display zoom as Apple.
  - All three raise `Zoomed` with the value.
- **No pinch gesture in the library.** Apps put a DrawnUi `SkiaHotspotZoom` over the camera (`ZoomMin`, `ZoomMax`, event `Zoomed` with `ZoomEventArgs.Value`) and set `camera.Zoom = e.Value`.
- With `ZoomLimitMax = 10` on a triple camera whose tele starts at 10, the user barely reaches the tele lens; `MaxAvailableVideoZoomFactor` was 123.75 on the 16 Pro.

## How to build it

These change library behavior that consumers see, on every platform. The owner approves the API and the cross-platform contract before any code: an iOS-only meaning of `Zoom` is not acceptable, Android and Windows must behave the same way.

1. **Device**: for multi-lens zoom, open the virtual device: Triple, then DualWide, then Dual, then WideAngle. Decide whether that stays behind `Type = Max` or becomes the default.
2. **One zoom path on Apple**: inside the hardware range, set `VideoZoomFactor` only and keep the display zoom at 1. Use texture zoom only for the part outside the hardware range, and report that part in `TextureScale`. This removes the double zoom.
3. **Zoom units**: choose and document one meaning for `Zoom`:
   - "factor of the widest lens" (1.0 = ultra-wide, the raw AVFoundation value), or
   - "Apple label units" (1.0 = main lens, 0.5 = ultra-wide).
   Expose read-only data for the UI: switch-over points in `Zoom` units, the label multiplier, the active lens. Each is new public API.
4. **Start position**: with an ultra-wide constituent, factor 1.0 is the ultra-wide. To open like Apple Camera, set the zoom to the first switch-over factor (the main lens) after the camera is on.
5. **Pinch math**: pinch scale is a ratio. On gesture start remember the current zoom; on each change set `zoom = startZoom * scale`, clamped to the limits. Clamp `ZoomLimitMax` to the device maximum instead of a fixed 10 when the tele range should be reachable.
6. **Session hygiene**: no depth output unless depth is used. If depth is ever needed, expect the dual-wide camera to lose its ultra-wide while depth runs.
7. **Capture**: the photo and the video must show what the preview shows. Lens zoom does that by itself; any texture-zoom part needs the same crop on capture.
8. **Android parity**: logical multi-camera devices on Android switch lenses through `CONTROL_ZOOM_RATIO` (API 30+) on the logical camera id. The Android implementation today is texture-only. Plan the Android side in the same change, so `Zoom` means the same thing on both.

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
