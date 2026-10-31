#if ANDROID
using System.Runtime.InteropServices;
using Android.Opengl;
using SkiaSharp;

namespace DrawnUi.Camera
{
    /// <summary>
    /// The GPU preview's frames in AHardwareBuffers: the preview's GL thread renders each camera frame into one, and the
    /// canvas samples it on its render thread (Vulkan: imported as a VkImage; OpenGL: bound as an EGLImage texture), with
    /// no CPU copy. Each buffer is owned by one side at a time: Free, then Writing (GL thread), Ready (finished on the GPU),
    /// Displayed (the canvas's image holds it until Skia releases it), Free again.
    /// When the canvas cannot import the buffers the preview falls back to reading frames back (<see cref="Disabled"/>).
    /// </summary>
    internal sealed unsafe class AhbPreview
    {
        public const int Count = 3;

        const int Free = 0, Writing = 1, Ready = 2, Displayed = 3;

        public readonly int Width, Height;

        readonly IntPtr[] _buffers = new IntPtr[Count];
        readonly int[] _state = new int[Count];
        int _latest = -1;
        volatile bool _disabled;
        volatile bool _disposed;

        // GL side (preview GL thread)
        readonly int[] _tex = new int[Count];
        readonly int[] _fbo = new int[Count];
        readonly IntPtr[] _eglImages = new IntPtr[Count];
        IntPtr _eglDisplay;
        bool _fenceSync;

        // canvas side (render thread)
        IntPtr _vkDevice;
        readonly ulong[] _vkImage = new ulong[Count];
        readonly ulong[] _vkMemory = new ulong[Count];
        IntPtr _glContext;
        readonly int[] _canvasTex = new int[Count];

        public AhbPreview(int width, int height)
        {
            Width = width;
            Height = height;
        }

        /// <summary>
        /// True once the canvas could not use the buffers: the preview reads frames back instead.
        /// </summary>
        public bool Disabled => _disabled;

        public void Disable(string why)
        {
            if (_disabled)
                return;
            _disabled = true;
            Super.Log($"[AhbPreview] preview reads frames back instead: {why}");
        }

        #region interop

        [StructLayout(LayoutKind.Sequential)]
        struct AHardwareBuffer_Desc
        {
            public uint width, height, layers, format;
            public ulong usage;
            public uint stride, rfu0;
            public ulong rfu1;
        }

        [DllImport("android")] static extern int AHardwareBuffer_allocate(AHardwareBuffer_Desc* desc, IntPtr* outBuffer);
        [DllImport("android")] static extern void AHardwareBuffer_release(IntPtr buffer);
        [DllImport("EGL")] static extern IntPtr eglGetNativeClientBufferANDROID(IntPtr buffer);
        [DllImport("EGL")] static extern IntPtr eglGetProcAddress(string name);
        [DllImport("EGL")] static extern IntPtr eglGetCurrentContext();
        [DllImport("vulkan")] static extern IntPtr vkGetDeviceProcAddr(IntPtr device, string name);

        const int EGL_NONE = 0x3038;

        [StructLayout(LayoutKind.Sequential)]
        struct VkAndroidHardwareBufferFormatProperties
        {
            public int sType; public void* pNext; public int format; public ulong externalFormat; public uint formatFeatures;
            public int r, g, b, a; public int model, range, xOffset, yOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkAndroidHardwareBufferProperties
        {
            public int sType; public void* pNext; public ulong allocationSize; public uint memoryTypeBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkExternalMemoryImageCreateInfo { public int sType; public void* pNext; public uint handleTypes; }

        [StructLayout(LayoutKind.Sequential)]
        struct VkImageCreateInfo
        {
            public int sType; public void* pNext; public uint flags; public int imageType; public int format;
            public uint width, height, depth; public uint mipLevels, arrayLayers; public int samples, tiling;
            public uint usage; public int sharingMode; public uint queueFamilyIndexCount; public uint* pQueueFamilyIndices;
            public int initialLayout;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkImportAndroidHardwareBufferInfo { public int sType; public void* pNext; public IntPtr buffer; }

        [StructLayout(LayoutKind.Sequential)]
        struct VkMemoryDedicatedAllocateInfo { public int sType; public void* pNext; public ulong image; public ulong buffer; }

        [StructLayout(LayoutKind.Sequential)]
        struct VkMemoryAllocateInfo { public int sType; public void* pNext; public ulong allocationSize; public uint memoryTypeIndex; }

        const int VK_FORMAT_R8G8B8A8_UNORM = 37;
        const uint VK_IMAGE_USAGE = 0x1 | 0x2 | 0x4; // transfer src/dst (Skia wraps known formats with them) + sampled
        const uint VK_QUEUE_FAMILY_FOREIGN_EXT = 0xFFFFFFFD;

        #endregion

        #region GL thread

        /// <summary>
        /// Preview GL thread, with its EGL context current. False when the device cannot do it (the preview reads back).
        /// </summary>
        public bool InitGl()
        {
            if (Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.O)
                return false;

            _eglDisplay = (IntPtr)EGL14.EglGetCurrentDisplay().NativeHandle;
            var extensions = EGL14.EglQueryString(EGL14.EglGetCurrentDisplay(), EGL14.EglExtensions) ?? "";
            if (!extensions.Contains("EGL_ANDROID_image_native_buffer") || !extensions.Contains("EGL_KHR_image_base"))
                return Fail("EGL native buffer images not supported");
            _fenceSync = extensions.Contains("EGL_KHR_fence_sync");

            var createImage = (delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int*, IntPtr>)eglGetProcAddress("eglCreateImageKHR");
            var targetTexture = (delegate* unmanaged<int, IntPtr, void>)eglGetProcAddress("glEGLImageTargetTexture2DOES");
            if (createImage == null || targetTexture == null)
                return Fail("EGL image functions missing");

            GLES20.GlGenTextures(Count, _tex, 0);
            GLES20.GlGenFramebuffers(Count, _fbo, 0);

            for (int i = 0; i < Count; i++)
            {
                var desc = new AHardwareBuffer_Desc
                {
                    width = (uint)Width, height = (uint)Height, layers = 1,
                    format = 1, // AHARDWAREBUFFER_FORMAT_R8G8B8A8_UNORM
                    usage = 0x100 | 0x200 // GPU_SAMPLED_IMAGE | GPU_FRAMEBUFFER
                };
                IntPtr buffer;
                if (AHardwareBuffer_allocate(&desc, &buffer) != 0)
                    return Fail("AHardwareBuffer_allocate failed");
                _buffers[i] = buffer;

                var client = eglGetNativeClientBufferANDROID(buffer);
                int* attribs = stackalloc int[] { 0x30D2, 1, EGL_NONE }; // EGL_IMAGE_PRESERVED_KHR = TRUE
                _eglImages[i] = createImage(_eglDisplay, IntPtr.Zero, 0x3140, client, attribs); // EGL_NATIVE_BUFFER_ANDROID
                if (_eglImages[i] == IntPtr.Zero)
                    return Fail("eglCreateImageKHR failed");

                GLES20.GlBindTexture(GLES20.GlTexture2d, _tex[i]);
                targetTexture(GLES20.GlTexture2d, _eglImages[i]);
                GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
                GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);

                GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _fbo[i]);
                GLES20.GlFramebufferTexture2D(GLES20.GlFramebuffer, GLES20.GlColorAttachment0, GLES20.GlTexture2d, _tex[i], 0);
                var status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
                if (status != GLES20.GlFramebufferComplete)
                    return Fail($"framebuffer incomplete 0x{status:X}");
            }

            GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
            GLES20.GlBindTexture(GLES20.GlTexture2d, 0);
            return true;
        }

        bool Fail(string what)
        {
            Super.Log($"[AhbPreview] not used: {what}");
            ReleaseGl();
            return false;
        }

        /// <summary>
        /// GL thread: a free buffer to render the next frame into, or -1 when the canvas still holds all of them.
        /// </summary>
        public int BeginWrite()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Interlocked.CompareExchange(ref _state[i], Writing, Free) == Free)
                    return i;
            }
            return -1;
        }

        public int Framebuffer(int index) => _fbo[index];

        /// <summary>
        /// GL thread, after drawing into the buffer: waits until the GPU finished it, then hands it to the canvas.
        /// A frame the canvas did not take yet is replaced and freed.
        /// </summary>
        public void EndWrite(int index)
        {
            WaitForGpu();

            Volatile.Write(ref _state[index], Ready);
            var previous = Interlocked.Exchange(ref _latest, index);
            if (previous >= 0 && previous != index)
                Interlocked.CompareExchange(ref _state[previous], Free, Ready);
        }

        /// <summary>
        /// GL thread: drops a buffer that was not filled (a frame skipped after BeginWrite).
        /// </summary>
        public void CancelWrite(int index) => Volatile.Write(ref _state[index], Free);

        void WaitForGpu()
        {
            if (_fenceSync)
            {
                var createSync = (delegate* unmanaged<IntPtr, int, int*, IntPtr>)eglGetProcAddress("eglCreateSyncKHR");
                var clientWait = (delegate* unmanaged<IntPtr, IntPtr, int, ulong, int>)eglGetProcAddress("eglClientWaitSyncKHR");
                var destroySync = (delegate* unmanaged<IntPtr, IntPtr, int>)eglGetProcAddress("eglDestroySyncKHR");
                if (createSync != null && clientWait != null && destroySync != null)
                {
                    int* attribs = stackalloc int[] { EGL_NONE };
                    var sync = createSync(_eglDisplay, 0x30F9, attribs); // EGL_SYNC_FENCE_KHR
                    if (sync != IntPtr.Zero)
                    {
                        // EGL_SYNC_FLUSH_COMMANDS_BIT_KHR, 100 ms
                        clientWait(_eglDisplay, sync, 0x0001, 100_000_000UL);
                        destroySync(_eglDisplay, sync);
                        return;
                    }
                }
            }

            GLES20.GlFinish();
        }

        /// <summary>
        /// GL thread: releases the GL side and this side's references to the buffers.
        /// </summary>
        public void ReleaseGl()
        {
            _disposed = true;

            var destroyImage = (delegate* unmanaged<IntPtr, IntPtr, int>)eglGetProcAddress("eglDestroyImageKHR");
            for (int i = 0; i < Count; i++)
            {
                if (_fbo[i] != 0)
                    GLES20.GlDeleteFramebuffers(1, new[] { _fbo[i] }, 0);
                if (_tex[i] != 0)
                    GLES20.GlDeleteTextures(1, new[] { _tex[i] }, 0);
                if (_eglImages[i] != IntPtr.Zero && destroyImage != null)
                    destroyImage(_eglDisplay, _eglImages[i]);
                _fbo[i] = 0;
                _tex[i] = 0;
                _eglImages[i] = IntPtr.Zero;
            }

            ReleaseBuffersWhenUnused();
        }

        #endregion

        #region canvas render thread

        /// <summary>
        /// Canvas render thread: the newest finished frame as an image on the canvas's GRContext, no copy, or null when no
        /// new frame is there (the canvas keeps the last one). The buffer returns to the GL thread when Skia releases the image.
        /// </summary>
        public SKImage TakeImage(GRContext context, IntPtr vkDevice)
        {
            if (_disabled || _disposed || context == null)
                return null;

            int index = Interlocked.Exchange(ref _latest, -1);
            if (index < 0)
                return null;
            if (Interlocked.CompareExchange(ref _state[index], Displayed, Ready) != Ready)
                return null;

            SKImage image = null;
            try
            {
                image = context.Backend switch
                {
                    GRBackend.Vulkan => WrapVulkan(context, vkDevice, index),
                    GRBackend.OpenGL => WrapGl(context, index),
                    _ => null
                };
            }
            catch (Exception e)
            {
                Super.Log($"[AhbPreview] import failed: {e.Message}");
            }

            if (image == null)
            {
                Volatile.Write(ref _state[index], Free);
                Disable($"the {context.Backend} canvas cannot import hardware buffers");
            }

            return image;
        }

        void OnImageReleased(IntPtr address, object context)
        {
            int index = (int)context;
            Volatile.Write(ref _state[index], Free);
            if (_disposed)
                ReleaseBuffersWhenUnused();
        }

        SKImage WrapVulkan(GRContext context, IntPtr device, int index)
        {
            if (device == IntPtr.Zero)
                return null;

            if (device != _vkDevice)
            {
                // a new canvas device: the images of the old one went with it
                Array.Clear(_vkImage);
                Array.Clear(_vkMemory);
                _vkDevice = device;
            }

            if (_vkImage[index] == 0 && !ImportVulkan(device, index))
                return null;

            var info = new GRVkImageInfo
            {
                Image = _vkImage[index],
                Alloc = new GRVkAlloc { Memory = _vkMemory[index] },
                ImageTiling = 0, // OPTIMAL
                ImageLayout = 0, // UNDEFINED, acquired from the GL queue for every frame
                Format = VK_FORMAT_R8G8B8A8_UNORM,
                ImageUsageFlags = VK_IMAGE_USAGE,
                SampleCount = 1,
                LevelCount = 1,
                CurrentQueueFamily = VK_QUEUE_FAMILY_FOREIGN_EXT,
                SharingMode = 0,
            };
            using var texture = new GRBackendTexture(Width, Height, info);
            return SKImage.FromTexture(context, texture, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Premul,
                null, OnTextureReleased, index);
        }

        void OnTextureReleased(object context) => OnImageReleased(IntPtr.Zero, context);

        bool ImportVulkan(IntPtr device, int index)
        {
            // functions of a device extension that was not enabled resolve to null: no import on this device
            var getProps = (delegate* unmanaged<IntPtr, IntPtr, VkAndroidHardwareBufferProperties*, int>)vkGetDeviceProcAddr(device, "vkGetAndroidHardwareBufferPropertiesANDROID");
            var createImage = (delegate* unmanaged<IntPtr, VkImageCreateInfo*, void*, ulong*, int>)vkGetDeviceProcAddr(device, "vkCreateImage");
            var allocate = (delegate* unmanaged<IntPtr, VkMemoryAllocateInfo*, void*, ulong*, int>)vkGetDeviceProcAddr(device, "vkAllocateMemory");
            var bind = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, int>)vkGetDeviceProcAddr(device, "vkBindImageMemory");
            var destroyImage = (delegate* unmanaged<IntPtr, ulong, void*, void>)vkGetDeviceProcAddr(device, "vkDestroyImage");
            if (getProps == null || createImage == null || allocate == null || bind == null || destroyImage == null)
                return false;

            var format = new VkAndroidHardwareBufferFormatProperties { sType = 1000129002 };
            var props = new VkAndroidHardwareBufferProperties { sType = 1000129001, pNext = &format };
            if (getProps(device, _buffers[index], &props) != 0 || format.format != VK_FORMAT_R8G8B8A8_UNORM || props.memoryTypeBits == 0)
                return false;

            var external = new VkExternalMemoryImageCreateInfo { sType = 1000072001, handleTypes = 0x400 }; // AHB handle type
            var imageInfo = new VkImageCreateInfo
            {
                sType = 14, pNext = &external, imageType = 1, format = VK_FORMAT_R8G8B8A8_UNORM,
                width = (uint)Width, height = (uint)Height, depth = 1, mipLevels = 1, arrayLayers = 1,
                samples = 1, tiling = 0, usage = VK_IMAGE_USAGE, sharingMode = 0, initialLayout = 0
            };
            ulong image;
            if (createImage(device, &imageInfo, null, &image) != 0)
                return false;

            var import = new VkImportAndroidHardwareBufferInfo { sType = 1000129003, buffer = _buffers[index] };
            var dedicated = new VkMemoryDedicatedAllocateInfo { sType = 1000127001, pNext = &import, image = image };
            var alloc = new VkMemoryAllocateInfo
            {
                sType = 5, pNext = &dedicated, allocationSize = props.allocationSize,
                memoryTypeIndex = (uint)System.Numerics.BitOperations.TrailingZeroCount(props.memoryTypeBits)
            };
            ulong memory;
            if (allocate(device, &alloc, null, &memory) != 0 || bind(device, image, memory, 0) != 0)
            {
                destroyImage(device, image, null);
                return false;
            }

            _vkImage[index] = image;
            _vkMemory[index] = memory;
            return true;
        }

        SKImage WrapGl(GRContext context, int index)
        {
            var current = eglGetCurrentContext();
            if (current == IntPtr.Zero)
                return null;

            if (current != _glContext)
            {
                Array.Clear(_canvasTex);
                _glContext = current;
            }

            if (_canvasTex[index] == 0)
            {
                var targetTexture = (delegate* unmanaged<int, IntPtr, void>)eglGetProcAddress("glEGLImageTargetTexture2DOES");
                if (targetTexture == null || _eglImages[index] == IntPtr.Zero)
                    return null;

                var tex = new int[1];
                GLES20.GlGenTextures(1, tex, 0);
                GLES20.GlBindTexture(GLES20.GlTexture2d, tex[0]);
                targetTexture(GLES20.GlTexture2d, _eglImages[index]);
                GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
                GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
                GLES20.GlBindTexture(GLES20.GlTexture2d, 0);
                if (GLES20.GlGetError() != GLES20.GlNoError)
                {
                    GLES20.GlDeleteTextures(1, tex, 0);
                    return null;
                }
                _canvasTex[index] = tex[0];
                context.ResetContext(); // texture bindings changed behind Skia's back
            }

            using var texture = new GRBackendTexture(Width, Height, false,
                new GRGlTextureInfo(0x0DE1 /* GL_TEXTURE_2D */, (uint)_canvasTex[index], 0x8058 /* GL_RGBA8 */));
            return SKImage.FromTexture(context, texture, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Premul,
                null, OnTextureReleased, index);
        }

        #endregion

        /// <summary>
        /// The buffers' memory goes once neither side uses it: the GL side released, no image displayed.
        /// Vulkan images and memory of the canvas device are freed with them.
        /// </summary>
        void ReleaseBuffersWhenUnused()
        {
            for (int i = 0; i < Count; i++)
            {
                if (Volatile.Read(ref _state[i]) == Displayed)
                    return;
            }

            lock (_buffers)
            {
                if (_vkDevice != IntPtr.Zero)
                {
                    var destroyImage = (delegate* unmanaged<IntPtr, ulong, void*, void>)vkGetDeviceProcAddr(_vkDevice, "vkDestroyImage");
                    var freeMemory = (delegate* unmanaged<IntPtr, ulong, void*, void>)vkGetDeviceProcAddr(_vkDevice, "vkFreeMemory");
                    for (int i = 0; i < Count; i++)
                    {
                        if (_vkImage[i] != 0 && destroyImage != null)
                            destroyImage(_vkDevice, _vkImage[i], null);
                        if (_vkMemory[i] != 0 && freeMemory != null)
                            freeMemory(_vkDevice, _vkMemory[i], null);
                        _vkImage[i] = 0;
                        _vkMemory[i] = 0;
                    }
                }

                for (int i = 0; i < Count; i++)
                {
                    if (_buffers[i] != IntPtr.Zero)
                        AHardwareBuffer_release(_buffers[i]);
                    _buffers[i] = IntPtr.Zero;
                }
            }
        }
    }
}
#endif
