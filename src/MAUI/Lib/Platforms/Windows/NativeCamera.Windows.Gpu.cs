using DrawnUi.Camera.Gpu;
using D3D = TerraFX.Interop.DirectX;

namespace DrawnUi.Camera;

/// <summary>
/// Camera side of the GPU recording path: every Direct3D camera frame can be copied into a <see cref="GpuFrameRing"/> on the
/// device Media Foundation delivered it on, for the recorder thread to compose without a CPU copy.
/// </summary>
public partial class NativeCamera
{
    readonly object _ringLock = new();
    GpuFrameRing _recordingRing;
    nint _frameDevice; // the device of the latest camera frame, AddRef'd
    uint _frameWidth, _frameHeight, _frameFormat;
    bool _ringMismatchLogged;

    const uint DxgiFormatB8G8R8A8 = 87;

    /// <summary>
    /// Raised once (camera thread) when frames no longer fit the armed ring, e.g. after the camera restarted on a new device
    /// or format; the owner creates a new ring and arms it.
    /// </summary>
    internal Action RecordingRingStale { get; set; }

    /// <summary>
    /// Camera thread, for each Direct3D frame: remembers its device and feeds the armed recording ring.
    /// </summary>
    unsafe void FeedGpuRecording(nint texturePtr, uint subresource, uint width, uint height, uint format)
    {
        var texture = (D3D.ID3D11Texture2D*)texturePtr;
        D3D.ID3D11Device* device;
        texture->GetDevice(&device);
        lock (_ringLock)
        {
            if ((nint)device != _frameDevice)
            {
                if (_frameDevice != 0)
                    ((D3D.ID3D11Device*)_frameDevice)->Release();
                _frameDevice = (nint)device; // keeps the reference GetDevice added
            }
            else
            {
                device->Release();
            }
            _frameWidth = width;
            _frameHeight = height;
            _frameFormat = format;

            var ring = _recordingRing;
            if (ring == null)
                return;
            if (ring.Device != _frameDevice || format != DxgiFormatB8G8R8A8 || width != ring.Width || height != ring.Height)
            {
                // the camera was set up again (new device, new format): the recorder needs a new ring, frames wait for it
                if (!_ringMismatchLogged)
                {
                    _ringMismatchLogged = true;
                    Super.Log($"[NativeCameraWindows] camera frames changed (format {format} {width}x{height}, device {(ring.Device != _frameDevice ? "new" : "same")}), replacing the GPU recording ring");
                    RecordingRingStale?.Invoke();
                }
                return;
            }
            ring.Produce(texture, subresource, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// A recording ring on the device and at the size of the latest camera frame, or null with the reason.
    /// </summary>
    internal unsafe GpuFrameRing CreateRecordingRing(out string reason)
    {
        lock (_ringLock)
        {
            if (_frameDevice == 0)
            {
                reason = "no camera frame on a Direct3D device has arrived yet (the camera delivers CPU frames or none)";
                return null;
            }
            if (_frameFormat != DxgiFormatB8G8R8A8)
            {
                reason = $"camera frames are DXGI format {_frameFormat}, not BGRA";
                return null;
            }
            return GpuFrameRing.Create((D3D.ID3D11Device*)_frameDevice, (int)_frameWidth, (int)_frameHeight, out reason);
        }
    }

    /// <summary>
    /// Starts copying every camera frame into the ring.
    /// </summary>
    internal void ArmRecordingRing(GpuFrameRing ring)
    {
        lock (_ringLock)
        {
            _recordingRing = ring;
            _ringMismatchLogged = false;
        }
    }

    /// <summary>
    /// Stops feeding the ring; when this returns no copy into it is in flight, so it can be disposed.
    /// </summary>
    internal void DisarmRecordingRing()
    {
        lock (_ringLock)
            _recordingRing = null;
    }

    unsafe void ReleaseGpuFrameDevice()
    {
        lock (_ringLock)
        {
            _recordingRing = null;
            if (_frameDevice != 0)
                ((D3D.ID3D11Device*)_frameDevice)->Release();
            _frameDevice = 0;
        }
    }
}
