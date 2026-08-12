#if IOS || MACCATALYST
using System.Numerics;
using CoreVideo;
using Metal;
using Foundation;
using System.Runtime.InteropServices;

namespace DrawnUi.Camera
{
    /// <summary>
    /// Uses Metal compute shader to scale camera frames for preview.
    /// Uses semaphore to prevent GPU command queue backlog (Apple's triple-buffering pattern).
    /// ScaleFromTexture uses double-buffered async readback (like Android PBOs):
    /// GPU writes frame N while CPU reads frame N-1, eliminating per-frame GPU stalls.
    /// </summary>
    public class MetalPreviewScaler : IDisposable
    {
        private IMTLDevice _device;
        private IMTLCommandQueue _commandQueue;
        private IMTLComputePipelineState _scalePipeline;
        private CVMetalTextureCache _textureCache;
        private IMTLTexture _outputTexture;
        private CVPixelBuffer _outputBuffer;
        private MTLPixelFormat _outputFormat = MTLPixelFormat.BGRA8Unorm;

        private int _inputWidth;
        private int _inputHeight;
        private int _outputWidth;
        private int _outputHeight;
        private bool _isInitialized;

        // Semaphore to prevent GPU backlog - max 2 frames in flight
        private SemaphoreSlim _gpuSemaphore;
        private const int MaxFramesInFlight = 2;

        // Double-buffered async readback for ScaleFromTexture
        private IMTLTexture[] _asyncOutputTextures;  // two GPU output textures
        private byte[][] _asyncReadbackBuffers;       // two pinned CPU buffers
        private GCHandle[] _asyncBufferPins;           // GC pins for the CPU buffers
        private int _asyncCurrentSlot;                 // which slot GPU writes to this frame
        private bool _asyncHasPreviousFrame;           // false on very first frame
        private volatile bool _asyncPreviousReady;     // set by completion handler
        private readonly object _asyncLock = new();    // guards completion flag

        // GPU-only passthrough ring for zero-copy preview hand-off to Skia.
        // No CPU readback — the output IMTLTexture is wrapped as a GPU-backed SKImage on the
        // paint thread via SKImage.FromTexture. Ring of 3 decouples camera write cadence from
        // paint read cadence so neither side has to stall.
        private const int GpuRingSize = 3;
        private IMTLTexture[] _gpuRingTextures;
        private int _gpuRingWidth;
        private int _gpuRingHeight;
        private int _gpuRingWriteIdx;
        private MTLPixelFormat _gpuRingFormat = MTLPixelFormat.BGRA8Unorm;

        // Metal shader source for bilinear scaling with rotation + mirror support.
        // Rotation / mirror applied via UV transform on the input sample coord — the output
        // texture is written in display orientation. Uniform branches cost nothing on the GPU.
        private static readonly string ScaleShaderSource =
            "#include <metal_stdlib>\n" +
            "using namespace metal;\n" +
            "\n" +
            "struct ScaleParams {\n" +
            "    uint rotation;\n" +
            "    uint mirror;\n" +
            "    float2 cropOrigin;\n" +
            "    float2 cropSize;\n" +
            "};\n" +
            "\n" +
            "kernel void scaleTexture(texture2d<half, access::sample> inputTexture [[texture(0)]],\n" +
            "                         texture2d<half, access::write> outputTexture [[texture(1)]],\n" +
            "                         constant ScaleParams& params [[buffer(0)]],\n" +
            "                         uint2 gid [[thread_position_in_grid]])\n" +
            "{\n" +
            "    if (gid.x >= outputTexture.get_width() || gid.y >= outputTexture.get_height()) {\n" +
            "        return;\n" +
            "    }\n" +
            "\n" +
            "    float2 outputSize = float2(outputTexture.get_width(), outputTexture.get_height());\n" +
            "    float2 uv = (float2(gid) + 0.5) / outputSize;\n" +
            "    uv = params.cropOrigin + uv * params.cropSize;\n" +
            "\n" +
            "    if (params.mirror != 0u) {\n" +
            "        uv.x = 1.0 - uv.x;\n" +
            "    }\n" +
            "\n" +
            "    float2 src;\n" +
            "    if (params.rotation == 90u) {\n" +
            "        src = float2(uv.y, 1.0 - uv.x);\n" +
            "    } else if (params.rotation == 180u) {\n" +
            "        src = float2(1.0 - uv.x, 1.0 - uv.y);\n" +
            "    } else if (params.rotation == 270u) {\n" +
            "        src = float2(1.0 - uv.y, uv.x);\n" +
            "    } else {\n" +
            "        src = uv;\n" +
            "    }\n" +
            "\n" +
            "    constexpr sampler linearSampler(filter::linear, address::clamp_to_edge);\n" +
            "    half4 color = inputTexture.sample(linearSampler, src);\n" +
            "\n" +
            "    outputTexture.write(color, gid);\n" +
            "}\n";

        [StructLayout(LayoutKind.Sequential)]
        private struct ScaleParams
        {
            public uint Rotation;
            public uint Mirror;
            public Vector2 CropOrigin;
            public Vector2 CropSize;
        }

        public bool IsInitialized => _isInitialized;
        public int OutputWidth => _outputWidth;
        public int OutputHeight => _outputHeight;

        /// <summary>
        /// Initialize the Metal scaler with specified dimensions.
        /// <paramref name="outputFormat"/> controls the byte order of the readback buffer:
        /// BGRA8Unorm (default) matches Skia Bgra8888 for the preview pipeline,
        /// RGBA8Unorm matches the documented ML buffer contract consumed by RawCameraFrame.TryGetRgba.
        /// </summary>
        public bool Initialize(int inputWidth, int inputHeight, int outputWidth, int outputHeight,
            MTLPixelFormat outputFormat = MTLPixelFormat.BGRA8Unorm)
        {
            try
            {
                _inputWidth = inputWidth;
                _inputHeight = inputHeight;
                _outputWidth = outputWidth;
                _outputHeight = outputHeight;
                _outputFormat = outputFormat;

                // Get Metal device
                _device = MTLDevice.SystemDefault;
                if (_device == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MetalPreviewScaler] No Metal device available");
                    return false;
                }

                // Create command queue
                _commandQueue = _device.CreateCommandQueue();
                if (_commandQueue == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MetalPreviewScaler] Failed to create command queue");
                    return false;
                }

                // Create texture cache
                _textureCache = new CVMetalTextureCache(_device);
                if (_textureCache == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MetalPreviewScaler] Failed to create texture cache");
                    return false;
                }

                // Compile shader
                NSError error;
                var compileOptions = new MTLCompileOptions();
                var library = _device.CreateLibrary(ScaleShaderSource, compileOptions, out error);
                //if (library == null || error != null)
                //{
                //    System.Diagnostics.Debug.WriteLine($"[MetalPreviewScaler] Shader compile error: {error?.LocalizedDescription}");
                //    return false;
                //}

                var scaleFunction = library.CreateFunction("scaleTexture");
                if (scaleFunction == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MetalPreviewScaler] Failed to create scale function");
                    return false;
                }

                _scalePipeline = _device.CreateComputePipelineState(scaleFunction, out error);
                //if (_scalePipeline == null || error != null)
                //{
                //    System.Diagnostics.Debug.WriteLine($"[MetalPreviewScaler] Pipeline state error: {error?.LocalizedDescription}");
                //    return false;
                //}

                // Create output texture
                var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(
                    _outputFormat,
                    (nuint)outputWidth,
                    (nuint)outputHeight,
                    false);
                descriptor.Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite;
                _outputTexture = _device.CreateTexture(descriptor);

                // Create output CVPixelBuffer for reading back
                _outputBuffer = new CVPixelBuffer(outputWidth, outputHeight, CVPixelFormatType.CV32BGRA, null);

                // Create semaphore to limit frames in flight (prevents GPU backlog)
                _gpuSemaphore = new SemaphoreSlim(MaxFramesInFlight, MaxFramesInFlight);

                // Double-buffered async readback resources (for ScaleFromTexture)
                int bufferSize = outputWidth * outputHeight * 4;
                _asyncOutputTextures = new IMTLTexture[2];
                _asyncReadbackBuffers = new byte[2][];
                _asyncBufferPins = new GCHandle[2];
                for (int i = 0; i < 2; i++)
                {
                    var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(
                        _outputFormat,
                        (nuint)outputWidth,
                        (nuint)outputHeight,
                        false);
                    desc.Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite;
                    _asyncOutputTextures[i] = _device.CreateTexture(desc);

                    _asyncReadbackBuffers[i] = new byte[bufferSize];
                    _asyncBufferPins[i] = GCHandle.Alloc(_asyncReadbackBuffers[i], GCHandleType.Pinned);
                }
                _asyncCurrentSlot = 0;
                _asyncHasPreviousFrame = false;
                _asyncPreviousReady = false;

                _isInitialized = true;
                System.Diagnostics.Debug.WriteLine($"[MetalPreviewScaler] Initialized: {inputWidth}x{inputHeight} -> {outputWidth}x{outputHeight}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetalPreviewScaler] Initialize error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Scale the input CVPixelBuffer and copy result to output byte array.
        /// Returns true if successful. Skips frame if GPU is backed up (prevents lag spikes).
        /// <paramref name="rotation"/> (0/90/180/270) and <paramref name="mirror"/> are applied
        /// as UV transforms in the compute shader — output texture is written in display orientation.
        /// </summary>
        public bool Scale(CVPixelBuffer inputBuffer, byte[] outputData, out int bytesPerRow,
            int rotation = 0, bool mirror = false,
            float cropOriginX = 0f, float cropOriginY = 0f, float cropSizeX = 1f, float cropSizeY = 1f)
        {
            bytesPerRow = 0;

            if (!_isInitialized || inputBuffer == null || outputData == null)
                return false;

            // Check if GPU is backed up - if so, skip this frame to prevent lag spike
            // This is the key to smooth performance: drop frames rather than queue them
            if (!_gpuSemaphore.Wait(0))  // Non-blocking check
            {
                // GPU is busy with max frames in flight - skip this frame
                return false;
            }

            try
            {
                // Drain ObjC autoreleased objects (CommandBuffer, ComputeCommandEncoder, etc.)
                // created by Metal API calls on this .NET background thread.
                using var pool = new NSAutoreleasePool();

                // Create Metal texture from input CVPixelBuffer (zero-copy, reuses buffer memory)
                var inputTexture = _textureCache.TextureFromImage(
                    inputBuffer,
                    MTLPixelFormat.BGRA8Unorm,
                    inputBuffer.Width,
                    inputBuffer.Height,
                    0,
                    out CVReturn cvError);

                if (cvError != CVReturn.Success || inputTexture == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MetalPreviewScaler] TextureFromImage failed: {cvError}");
                    _gpuSemaphore.Release();
                    return false;
                }

                // Create command buffer
                var commandBuffer = _commandQueue.CommandBuffer();
                var computeEncoder = commandBuffer.ComputeCommandEncoder;

                // Set pipeline and textures
                computeEncoder.SetComputePipelineState(_scalePipeline);
                computeEncoder.SetTexture(inputTexture.Texture, 0);
                computeEncoder.SetTexture(_outputTexture, 1);

                // Inline uniforms — SetBytes copies into the command buffer so no ownership
                // tracking needed per frame.
                var paramsStruct = new ScaleParams
                {
                    Rotation = (uint)(((rotation % 360) + 360) % 360),
                    Mirror = mirror ? 1u : 0u,
                    CropOrigin = new Vector2(cropOriginX, cropOriginY),
                    CropSize = new Vector2(cropSizeX, cropSizeY),
                };
                unsafe
                {
                    computeEncoder.SetBytes((IntPtr)(&paramsStruct), (nuint)sizeof(ScaleParams), 0);
                }

                // Calculate thread groups
                var threadGroupSize = new MTLSize(16, 16, 1);
                var threadGroups = new MTLSize(
                    (_outputWidth + 15) / 16,
                    (_outputHeight + 15) / 16,
                    1);

                computeEncoder.DispatchThreadgroups(threadGroups, threadGroupSize);
                computeEncoder.EndEncoding();

                // Execute and wait
                commandBuffer.Commit();
                commandBuffer.WaitUntilCompleted();

                // Copy output texture to byte array
                bytesPerRow = _outputWidth * 4;
                var region = new MTLRegion
                {
                    Origin = new MTLOrigin(0, 0, 0),
                    Size = new MTLSize(_outputWidth, _outputHeight, 1)
                };

                // Get bytes from output texture
                unsafe
                {
                    fixed (byte* ptr = outputData)
                    {
                        _outputTexture.GetBytes((IntPtr)ptr, (nuint)bytesPerRow, region, 0);
                    }
                }

                // Dispose the CVMetalTexture (not the underlying buffer)
                inputTexture.Dispose();

                // Release semaphore - allow next frame
                _gpuSemaphore.Release();

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetalPreviewScaler] Scale error: {ex.Message}");
                _gpuSemaphore.Release();
                return false;
            }
        }

        /// <summary>
        /// Scale from an existing Metal texture using async double-buffered readback.
        /// GPU writes frame N to slot[current] while CPU reads frame N-1 from slot[previous].
        /// Returns false on the very first call (no previous frame yet) — 1 frame of latency
        /// at 30fps is invisible, but eliminates the per-frame WaitUntilCompleted stall.
        /// <paramref name="rotation"/> (0/90/180/270) and <paramref name="mirror"/> are applied
        /// as UV transforms in the compute shader — output texture is written in display orientation.
        /// </summary>
        public bool ScaleFromTexture(IMTLTexture inputTexture, byte[] outputData, out int bytesPerRow,
            int rotation = 0, bool mirror = false,
            float cropOriginX = 0f, float cropOriginY = 0f, float cropSizeX = 1f, float cropSizeY = 1f)
        {
            bytesPerRow = 0;

            if (!_isInitialized || inputTexture == null || outputData == null)
                return false;

            // Check if GPU is backed up
            if (!_gpuSemaphore.Wait(0))
            {
                return false;
            }

            try
            {
                // Drain ObjC autoreleased objects created by Metal API calls on this .NET background thread.
                using var pool = new NSAutoreleasePool();

                int writeSlot = _asyncCurrentSlot;
                int readSlot = 1 - writeSlot;

                // --- GPU WORK: dispatch compute shader writing to writeSlot ---
                var commandBuffer = _commandQueue.CommandBuffer();
                var computeEncoder = commandBuffer.ComputeCommandEncoder;

                computeEncoder.SetComputePipelineState(_scalePipeline);
                computeEncoder.SetTexture(inputTexture, 0);
                computeEncoder.SetTexture(_asyncOutputTextures[writeSlot], 1);

                var paramsStruct = new ScaleParams
                {
                    Rotation = (uint)(((rotation % 360) + 360) % 360),
                    Mirror = mirror ? 1u : 0u,
                    CropOrigin = new Vector2(cropOriginX, cropOriginY),
                    CropSize = new Vector2(cropSizeX, cropSizeY),
                };
                unsafe
                {
                    computeEncoder.SetBytes((IntPtr)(&paramsStruct), (nuint)sizeof(ScaleParams), 0);
                }

                var threadGroupSize = new MTLSize(16, 16, 1);
                var threadGroups = new MTLSize(
                    (_outputWidth + 15) / 16,
                    (_outputHeight + 15) / 16,
                    1);

                computeEncoder.DispatchThreadgroups(threadGroups, threadGroupSize);
                computeEncoder.EndEncoding();

                // Blit GPU texture → pinned CPU buffer inside the same command buffer
                // so the readback happens on GPU timeline with no CPU wait.
                var blitEncoder = commandBuffer.BlitCommandEncoder;
                bytesPerRow = _outputWidth * 4;
                var region = new MTLRegion
                {
                    Origin = new MTLOrigin(0, 0, 0),
                    Size = new MTLSize(_outputWidth, _outputHeight, 1)
                };
                // GetBytes after commit+completion is the pattern; we just need the
                // completion handler to signal readiness.
                blitEncoder.EndEncoding();

                // Signal completion for this slot via handler (runs on Metal callback thread)
                commandBuffer.AddCompletedHandler((_) =>
                {
                    // Copy bytes from GPU texture to pinned buffer (texture is complete now)
                    unsafe
                    {
                        _asyncOutputTextures[writeSlot].GetBytes(
                            _asyncBufferPins[writeSlot].AddrOfPinnedObject(),
                            (nuint)(_outputWidth * 4),
                            region,
                            0);
                    }
                    _asyncPreviousReady = true;
                    _gpuSemaphore.Release();
                });

                commandBuffer.Commit();
                // No WaitUntilCompleted! GPU runs async.

                // --- CPU WORK: read previous frame's completed data ---
                bool hasOutput = false;
                if (_asyncHasPreviousFrame && _asyncPreviousReady)
                {
                    // Previous slot's data is ready — copy to caller's buffer
                    Buffer.BlockCopy(_asyncReadbackBuffers[readSlot], 0, outputData, 0, outputData.Length);
                    _asyncPreviousReady = false;
                    hasOutput = true;
                }

                // Swap slots for next frame
                _asyncCurrentSlot = readSlot;
                _asyncHasPreviousFrame = true;

                return hasOutput;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetalPreviewScaler] ScaleFromTexture error: {ex.Message}");
                _gpuSemaphore.Release();
                return false;
            }
        }

        /// <summary>
        /// GPU-only passthrough scale. Writes the rotated / mirrored frame into the next ring
        /// slot and returns the texture without any CPU readback. Caller (paint thread) wraps it
        /// as an SKImage via SKImage.FromTexture. Output dimensions (<paramref name="outWidth"/>
        /// × <paramref name="outHeight"/>) must be in display orientation — for 90/270 rotation
        /// the caller is expected to swap the sensor dims before passing them here.
        /// Returns null on failure.
        /// </summary>
        public IMTLTexture DispatchScaleToRing(IMTLTexture inputTexture, int rotation, bool mirror,
            int outWidth, int outHeight,
            float cropOriginX = 0f, float cropOriginY = 0f, float cropSizeX = 1f, float cropSizeY = 1f)
        {
            if (!_isInitialized || inputTexture == null || outWidth <= 0 || outHeight <= 0)
                return null;

            // Lazily (re)allocate the ring when dims change. Allocation only happens on
            // rotation swap or resize, not per frame.
            if (_gpuRingTextures == null || _gpuRingWidth != outWidth || _gpuRingHeight != outHeight)
            {
                DisposeGpuRing();

                _gpuRingTextures = new IMTLTexture[GpuRingSize];
                for (int i = 0; i < GpuRingSize; i++)
                {
                    var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(
                        _gpuRingFormat,
                        (nuint)outWidth,
                        (nuint)outHeight,
                        false);
                    desc.Usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite;
                    _gpuRingTextures[i] = _device.CreateTexture(desc);
                }
                _gpuRingWidth = outWidth;
                _gpuRingHeight = outHeight;
                _gpuRingWriteIdx = 0;
            }

            try
            {
                using var pool = new NSAutoreleasePool();

                int slot = _gpuRingWriteIdx;
                var target = _gpuRingTextures[slot];

                var commandBuffer = _commandQueue.CommandBuffer();
                var computeEncoder = commandBuffer.ComputeCommandEncoder;

                computeEncoder.SetComputePipelineState(_scalePipeline);
                computeEncoder.SetTexture(inputTexture, 0);
                computeEncoder.SetTexture(target, 1);

                var paramsStruct = new ScaleParams
                {
                    Rotation = (uint)(((rotation % 360) + 360) % 360),
                    Mirror = mirror ? 1u : 0u,
                    CropOrigin = new Vector2(cropOriginX, cropOriginY),
                    CropSize = new Vector2(cropSizeX, cropSizeY),
                };
                unsafe
                {
                    computeEncoder.SetBytes((IntPtr)(&paramsStruct), (nuint)sizeof(ScaleParams), 0);
                }

                var threadGroupSize = new MTLSize(16, 16, 1);
                var threadGroups = new MTLSize(
                    (outWidth + 15) / 16,
                    (outHeight + 15) / 16,
                    1);

                computeEncoder.DispatchThreadgroups(threadGroups, threadGroupSize);
                computeEncoder.EndEncoding();

                // Commit and let Metal schedule the write. No WaitUntilCompleted here —
                // when Skia later reads this texture via SKImage.FromTexture, Metal's
                // per-resource hazard tracking serializes the read after this write.
                commandBuffer.Commit();

                _gpuRingWriteIdx = (slot + 1) % GpuRingSize;
                return target;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MetalPreviewScaler] DispatchScaleToRing error: {ex.Message}");
                return null;
            }
        }

        private void DisposeGpuRing()
        {
            if (_gpuRingTextures == null)
                return;

            for (int i = 0; i < _gpuRingTextures.Length; i++)
            {
                _gpuRingTextures[i]?.Dispose();
                _gpuRingTextures[i] = null;
            }
            _gpuRingTextures = null;
            _gpuRingWidth = 0;
            _gpuRingHeight = 0;
            _gpuRingWriteIdx = 0;
        }

        public void Dispose()
        {
            DisposeGpuRing();

            _outputBuffer?.Dispose();
            _outputBuffer = null;

            _outputTexture?.Dispose();
            _outputTexture = null;

            // Dispose async double-buffer resources
            if (_asyncOutputTextures != null)
            {
                for (int i = 0; i < _asyncOutputTextures.Length; i++)
                {
                    _asyncOutputTextures[i]?.Dispose();
                    _asyncOutputTextures[i] = null;
                }
                _asyncOutputTextures = null;
            }
            if (_asyncBufferPins != null)
            {
                for (int i = 0; i < _asyncBufferPins.Length; i++)
                {
                    if (_asyncBufferPins[i].IsAllocated)
                        _asyncBufferPins[i].Free();
                }
                _asyncBufferPins = null;
            }
            _asyncReadbackBuffers = null;

            _textureCache?.Dispose();
            _textureCache = null;

            // Note: Don't dispose _device, _commandQueue, _scalePipeline - they're managed by the system
            _scalePipeline = null;
            _commandQueue = null;
            _device = null;

            _isInitialized = false;
            System.Diagnostics.Debug.WriteLine("[MetalPreviewScaler] Disposed");
        }
    }
}
#endif
