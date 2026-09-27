namespace DrawnUi.Camera.Gpu;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

/// <summary>
/// Camera frames passed from the device Media Foundation delivers them on to a consumer device without a CPU copy: three
/// BGRA textures shared through NT handles, and two shared fences (produced / consumed) so that neither side touches a slot
/// the other still uses. The producer copies each frame into a free slot on the camera thread; the consumer takes the latest
/// slot on its own device and GPU-waits for the copy to land. Both devices must be on the same adapter.
/// </summary>
internal sealed unsafe class GpuFrameRing : IDisposable
{
    public const int Slots = 3;

    public readonly int Width, Height;
    public readonly GpuDevices.Adapter Adapter;

    readonly ID3D11Device* _device; // the producer's (Media Foundation's) device, AddRef'd
    readonly ID3D11DeviceContext* _context;
    readonly ID3D11DeviceContext4* _context4;
    readonly ID3D11Texture2D*[] _textures = new ID3D11Texture2D*[Slots];
    public readonly HANDLE[] Handles = new HANDLE[Slots];
    readonly ID3D11Fence* _produced, _consumed;
    public readonly HANDLE ProducedHandle, ConsumedHandle;

    readonly object _lock = new();
    int _latest = -1, _held = -1;
    readonly ulong[] _counter = new ulong[Slots], _lastUse = new ulong[Slots];
    readonly DateTime[] _time = new DateTime[Slots];
    ulong _published;
    bool _disposed;

    /// <summary>Frames copied into the ring.</summary>
    public long Produced => (long)_published;

    /// <summary>Frames not copied because the consumer's GPU had not finished with the free slot yet.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    long _skipped;

    /// <summary>The producer's device: frames of any other device cannot be copied in.</summary>
    public nint Device => (nint)_device;

    /// <summary>Set after each produced frame; the consumer waits on it.</summary>
    public readonly AutoResetEvent FrameReady = new(false);

    GpuFrameRing(ID3D11Device* device, int width, int height, GpuDevices.Adapter adapter)
    {
        Width = width;
        Height = height;
        Adapter = adapter;
        _device = device;
        device->AddRef();
        try
        {
            ID3D11DeviceContext* context;
            device->GetImmediateContext(&context);
            _context = context;
            ID3D11DeviceContext4* context4;
            GpuDevices.ThrowIfFailed(context->QueryInterface(__uuidof<ID3D11DeviceContext4>(), (void**)&context4), "ID3D11DeviceContext4 (fences)");
            _context4 = context4;

            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET),
                MiscFlags = (uint)(D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED),
            };
            for (var i = 0; i < Slots; i++)
            {
                ID3D11Texture2D* texture;
                GpuDevices.ThrowIfFailed(device->CreateTexture2D(&desc, null, &texture), "shared ring texture");
                _textures[i] = texture;
                IDXGIResource1* resource;
                GpuDevices.ThrowIfFailed(texture->QueryInterface(__uuidof<IDXGIResource1>(), (void**)&resource), "IDXGIResource1");
                HANDLE handle;
                var hr = resource->CreateSharedHandle(null, 0x80000001u /* DXGI_SHARED_RESOURCE_READ | WRITE */, null, &handle);
                resource->Release();
                GpuDevices.ThrowIfFailed(hr, "shared handle of a ring texture");
                Handles[i] = handle;
            }

            ID3D11Device5* device5;
            GpuDevices.ThrowIfFailed(device->QueryInterface(__uuidof<ID3D11Device5>(), (void**)&device5), "ID3D11Device5 (fences)");
            try
            {
                ID3D11Fence* produced, consumed;
                GpuDevices.ThrowIfFailed(device5->CreateFence(0, D3D11_FENCE_FLAG.D3D11_FENCE_FLAG_SHARED, __uuidof<ID3D11Fence>(), (void**)&produced), "produced fence");
                _produced = produced;
                GpuDevices.ThrowIfFailed(device5->CreateFence(0, D3D11_FENCE_FLAG.D3D11_FENCE_FLAG_SHARED, __uuidof<ID3D11Fence>(), (void**)&consumed), "consumed fence");
                _consumed = consumed;
                HANDLE ph, ch;
                GpuDevices.ThrowIfFailed(produced->CreateSharedHandle(null, GENERIC_ALL, null, &ph), "produced fence handle");
                ProducedHandle = ph;
                GpuDevices.ThrowIfFailed(consumed->CreateSharedHandle(null, GENERIC_ALL, null, &ch), "consumed fence handle");
                ConsumedHandle = ch;
            }
            finally
            {
                device5->Release();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// A ring on the producer's device, or null with the reason.
    /// </summary>
    public static GpuFrameRing Create(ID3D11Device* device, int width, int height, out string reason)
    {
        reason = null;
        var adapter = GpuDevices.AdapterOf(device);
        if (adapter == null)
        {
            reason = "the camera's D3D11 device has no readable adapter";
            return null;
        }
        try
        {
            return new GpuFrameRing(device, width, height, adapter.Value);
        }
        catch (Exception e)
        {
            reason = "frame ring on the camera's device: " + e.Message;
            return null;
        }
    }

    /// <summary>
    /// Producer, camera thread: copies the frame (a BGRA texture of the producer's device, this subresource) into a slot the
    /// consumer is not using. False when the frame does not fit the ring.
    /// </summary>
    public bool Produce(ID3D11Texture2D* source, uint subresource, DateTime time)
    {
        if (!TryAcquireSlot(out var slot, out var target))
            return false;
        _context->CopySubresourceRegion((ID3D11Resource*)target, 0, 0, 0, 0, (ID3D11Resource*)source, subresource, null);
        Publish(slot, time);
        return true;
    }

    /// <summary>
    /// Producer: a slot the consumer is done with, to be written on the producer's device and then <see cref="Publish"/>ed.
    /// The consumer signals "consumed" once its GPU work on a slot is done; when that has not happened yet the frame is
    /// skipped instead of GPU-waiting, because a wait here would stall the camera's device, and with it the preview, behind
    /// the consumer's GPU (a hardware encoder starting up held it for up to 0.9 s).
    /// </summary>
    public bool TryAcquireSlot(out int slot, out ID3D11Texture2D* texture)
    {
        ulong lastUse;
        texture = null;
        lock (_lock)
        {
            slot = -1;
            if (_disposed)
                return false;
            slot = _latest != 0 && _held != 0 ? 0 : _latest != 1 && _held != 1 ? 1 : 2;
            lastUse = _lastUse[slot];
        }
        if (_consumed->GetCompletedValue() < lastUse)
        {
            Interlocked.Increment(ref _skipped);
            return false;
        }
        texture = _textures[slot];
        return true;
    }

    /// <summary>
    /// Producer: the slot written after <see cref="TryAcquireSlot"/> becomes the latest frame.
    /// </summary>
    public void Publish(int slot, DateTime time)
    {
        ulong counter;
        lock (_lock)
            counter = ++_published;
        _context4->Signal(_produced, counter);
        _context->Flush();
        lock (_lock)
        {
            _counter[slot] = counter;
            _time[slot] = time;
            _latest = slot;
        }
        FrameReady.Set();
    }

    /// <summary>
    /// Producer: the slot most recently published, -1 before the first frame.
    /// </summary>
    public int LatestSlot
    {
        get
        {
            lock (_lock)
                return _latest;
        }
    }

    /// <summary>A slot's texture on the producer's device.</summary>
    public ID3D11Texture2D* SlotTexture(int slot) => _textures[slot];

    /// <summary>
    /// Consumer, its own thread: switches to the latest produced slot. Releases the previously held slot by signalling
    /// "consumed" on the consumer's context (after the GPU work already queued on it), then GPU-waits for the new slot's copy.
    /// False when there is nothing newer than the slot already held.
    /// </summary>
    public bool TakeLatest(ID3D11DeviceContext4* consumerContext, ID3D11Fence* produced, ID3D11Fence* consumed, ref ulong consumerFrame,
        out int slot, out DateTime time)
    {
        consumerFrame++;
        int oldHeld = -1, newSlot = -1;
        ulong counter = 0;
        time = default;
        lock (_lock)
        {
            if (_latest >= 0 && _latest != _held)
            {
                oldHeld = _held;
                if (oldHeld >= 0)
                    _lastUse[oldHeld] = consumerFrame - 1;
                newSlot = _held = _latest;
                counter = _counter[newSlot];
                time = _time[newSlot];
            }
        }
        slot = newSlot;
        if (newSlot < 0)
            return false;
        if (oldHeld >= 0)
            consumerContext->Signal(consumed, consumerFrame - 1);
        consumerContext->Wait(produced, counter);
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        for (var i = 0; i < Slots; i++)
        {
            if (Handles[i] != HANDLE.NULL)
                CloseHandle(Handles[i]);
            if (_textures[i] != null)
                _textures[i]->Release();
        }
        if (ProducedHandle != HANDLE.NULL)
            CloseHandle(ProducedHandle);
        if (ConsumedHandle != HANDLE.NULL)
            CloseHandle(ConsumedHandle);
        if (_produced != null)
            _produced->Release();
        if (_consumed != null)
            _consumed->Release();
        if (_context4 != null)
            _context4->Release();
        if (_context != null)
            _context->Release();
        _device->Release();
        FrameReady.Dispose();
    }
}
