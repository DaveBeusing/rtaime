using System.Runtime.InteropServices;
using System.Text;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.Gpu;

/// <summary>
/// NVIDIA CUDA Driver API backend. The backend is implemented entirely in managed C# plus direct driver interop;
/// no native adapter is required for the AP-08 foundation. Hardware qualification is evidence-driven and is not
/// implied merely by the presence of this implementation.
/// </summary>
public sealed class CudaGpuProcessingBackend : IGpuProcessingBackend
{
    private readonly object _gate = new();
    private const int MaxPooledAllocationsPerSize = 8;

    private readonly Dictionary<SurfaceId, CudaAllocation> _surfaces = new();
    private readonly Dictionary<nuint, Stack<ulong>> _freeAllocations = new();
    private readonly int _deviceOrdinal;
    private IntPtr _context;
    private IntPtr _module;
    private IntPtr _compositeFunction;
    private bool _running;
    private bool _disposed;

    public CudaGpuProcessingBackend(int deviceOrdinal = 0)
    {
        if (deviceOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(deviceOrdinal));

        _deviceOrdinal = deviceOrdinal;
        Info = Detect(deviceOrdinal);
    }

    public GpuBackendInfo Info { get; }
    public SurfaceStorageDomain StorageDomain => SurfaceStorageDomain.Device;

    public static GpuBackendInfo Detect(int deviceOrdinal = 0)
    {
        if (deviceOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(deviceOrdinal));

        if (!OperatingSystem.IsWindows())
        {
            return Unavailable(
                "CUDA Driver backend is currently implemented for the Windows V1 reference platform only.");
        }

        try
        {
            var init = CudaNative.cuInit(0);
            if (init != CudaResult.Success)
                return Unavailable($"CUDA driver initialization returned '{init}'.");

            var countResult = CudaNative.cuDeviceGetCount(out var deviceCount);
            if (countResult != CudaResult.Success)
                return Unavailable($"CUDA device enumeration returned '{countResult}'.");
            if (deviceCount <= deviceOrdinal)
                return Unavailable($"CUDA device ordinal '{deviceOrdinal}' is not present. Detected devices: {deviceCount}.");

            var deviceResult = CudaNative.cuDeviceGet(out var device, deviceOrdinal);
            if (deviceResult != CudaResult.Success)
                return Unavailable($"CUDA device acquisition returned '{deviceResult}'.");

            var name = new StringBuilder(256);
            var nameResult = CudaNative.cuDeviceGetName(name, name.Capacity, device);
            var deviceName = nameResult == CudaResult.Success && name.Length > 0
                ? name.ToString()
                : $"CUDA Device {deviceOrdinal}";

            ulong? memoryBytes = null;
            if (CudaNative.cuDeviceTotalMem_v2(out var totalMemory, device) == CudaResult.Success)
                memoryBytes = (ulong)totalMemory;

            return new GpuBackendInfo(
                GpuBackendKind.NvidiaCuda,
                deviceName,
                hardwareAccelerated: true,
                available: true,
                memoryBytes);
        }
        catch (DllNotFoundException)
        {
            return Unavailable("NVIDIA CUDA Driver library 'nvcuda.dll' is not available.");
        }
        catch (EntryPointNotFoundException)
        {
            return Unavailable("The installed NVIDIA driver does not expose the required CUDA Driver API entry points.");
        }
        catch (BadImageFormatException)
        {
            return Unavailable("The available CUDA Driver library is not compatible with the current process architecture.");
        }
        catch (Exception exception)
        {
            return Unavailable($"CUDA capability detection failed: {exception.GetType().Name}.");
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_running)
                return;
            if (!Info.Available)
                throw new InvalidOperationException(Info.Failure?.Message ?? "CUDA backend is unavailable.");

            Check(CudaNative.cuInit(0), "cuInit");
            Check(CudaNative.cuDeviceGet(out var device, _deviceOrdinal), "cuDeviceGet");
            Check(CudaNative.cuCtxCreate_v2(out _context, 0, device), "cuCtxCreate_v2");

            try
            {
                SetCurrentContext();
                var ptx = Marshal.StringToHGlobalAnsi(CompositeKernelPtx);
                try
                {
                    Check(CudaNative.cuModuleLoadData(out _module, ptx), "cuModuleLoadData");
                }
                finally
                {
                    Marshal.FreeHGlobal(ptx);
                }

                Check(
                    CudaNative.cuModuleGetFunction(out _compositeFunction, _module, "composite_rgba"),
                    "cuModuleGetFunction");
                _running = true;
            }
            catch
            {
                CleanupContext();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed || !_running && _context == IntPtr.Zero)
                return;

            if (_context != IntPtr.Zero)
            {
                try
                {
                    SetCurrentContext();
                    foreach (var allocation in _surfaces.Values)
                        CudaNative.cuMemFree_v2(allocation.DevicePointer);
                    _surfaces.Clear();
                    foreach (var pool in _freeAllocations.Values)
                    {
                        while (pool.TryPop(out var pointer))
                            CudaNative.cuMemFree_v2(pointer);
                    }
                    _freeAllocations.Clear();
                }
                finally
                {
                    CleanupContext();
                }
            }

            _running = false;
        }
    }

    public void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels)
    {
        lock (_gate)
        {
            EnsureRunning();
            EnsureRgba8(format);
            if (_surfaces.ContainsKey(surfaceId))
                throw new InvalidOperationException("CUDA surface identity is already allocated.");

            var byteLength = RgbaFrameBuffer.RequiredByteLength(format);
            if (rgbaPixels.Length != byteLength)
                throw new ArgumentException("CUDA upload length does not match the target format.", nameof(rgbaPixels));

            SetCurrentContext();
            var allocationLength = (nuint)byteLength;
            var pointer = RentAllocation(allocationLength);

            try
            {
                ref var source = ref MemoryMarshal.GetReference(rgbaPixels);
                Check(CudaNative.cuMemcpyHtoD_v2(pointer, ref source, allocationLength), "cuMemcpyHtoD_v2");
                _surfaces.Add(surfaceId, new CudaAllocation(format, pointer, allocationLength));
            }
            catch
            {
                ReturnAllocation(pointer, allocationLength);
                throw;
            }
        }
    }

    public void Composite(SurfaceId outputSurfaceId, VideoFormat format, GpuCompositeOperation operation)
    {
        lock (_gate)
        {
            EnsureRunning();
            EnsureRgba8(format);
            if (_surfaces.ContainsKey(outputSurfaceId))
                throw new InvalidOperationException("CUDA output surface identity is already allocated.");

            var backgroundA = Get(operation.BackgroundA, format);
            var backgroundB = Get(operation.BackgroundB, format);
            var layer = operation.Layer is { } layerId ? Get(layerId, format) : null;
            var byteLength = (nuint)RgbaFrameBuffer.RequiredByteLength(format);

            SetCurrentContext();
            var outputPointer = RentAllocation(byteLength);

            try
            {
                var pixelCount = checked(format.Width * format.Height);
                using var arguments = new CudaKernelArguments();
                arguments.AddUInt64(backgroundA.DevicePointer);
                arguments.AddUInt64(backgroundB.DevicePointer);
                arguments.AddUInt64(layer?.DevicePointer ?? 0UL);
                arguments.AddUInt64(outputPointer);
                arguments.AddUInt32(pixelCount);
                arguments.AddUInt32(operation.Transition.BlendWeight);
                arguments.AddUInt32(operation.LayerOpacity);
                arguments.AddUInt32(operation.LayerVisible && layer is not null ? 1U : 0U);

                const uint blockSize = 256;
                var gridSize = (pixelCount + blockSize - 1) / blockSize;
                Check(
                    CudaNative.cuLaunchKernel(
                        _compositeFunction,
                        gridSize,
                        1,
                        1,
                        blockSize,
                        1,
                        1,
                        0,
                        IntPtr.Zero,
                        arguments.PointerArray,
                        IntPtr.Zero),
                    "cuLaunchKernel");
                Check(CudaNative.cuCtxSynchronize(), "cuCtxSynchronize");

                _surfaces.Add(outputSurfaceId, new CudaAllocation(format, outputPointer, byteLength));
            }
            catch
            {
                ReturnAllocation(outputPointer, byteLength);
                throw;
            }
        }
    }

    public byte[] Readback(SurfaceId surfaceId, VideoFormat format)
    {
        lock (_gate)
        {
            EnsureRunning();
            var allocation = Get(surfaceId, format);
            SetCurrentContext();

            var host = new byte[checked((int)allocation.ByteLength)];
            Check(CudaNative.cuMemcpyDtoH_v2(host, allocation.DevicePointer, allocation.ByteLength), "cuMemcpyDtoH_v2");
            return host;
        }
    }

    public void Release(SurfaceId surfaceId)
    {
        lock (_gate)
        {
            if (_disposed || !_surfaces.Remove(surfaceId, out var allocation))
                return;

            if (_context != IntPtr.Zero)
            {
                SetCurrentContext();
                ReturnAllocation(allocation.DevicePointer, allocation.ByteLength);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            Stop();
            _disposed = true;
        }
    }

    private ulong RentAllocation(nuint byteLength)
    {
        if (_freeAllocations.TryGetValue(byteLength, out var pool) && pool.TryPop(out var pointer))
            return pointer;

        Check(CudaNative.cuMemAlloc_v2(out pointer, byteLength), "cuMemAlloc_v2");
        return pointer;
    }

    private void ReturnAllocation(ulong pointer, nuint byteLength)
    {
        if (!_freeAllocations.TryGetValue(byteLength, out var pool))
        {
            pool = new Stack<ulong>();
            _freeAllocations.Add(byteLength, pool);
        }

        if (pool.Count < MaxPooledAllocationsPerSize)
        {
            pool.Push(pointer);
            return;
        }

        Check(CudaNative.cuMemFree_v2(pointer), "cuMemFree_v2");
    }

    private CudaAllocation Get(SurfaceId surfaceId, VideoFormat format)
    {
        if (!_surfaces.TryGetValue(surfaceId, out var allocation))
            throw new InvalidOperationException($"CUDA surface '{surfaceId}' is not allocated.");
        if (allocation.Format != format)
            throw new InvalidOperationException("CUDA surface format does not match the requested processing format.");
        return allocation;
    }

    private void CleanupContext()
    {
        if (_module != IntPtr.Zero)
        {
            CudaNative.cuModuleUnload(_module);
            _module = IntPtr.Zero;
            _compositeFunction = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero)
        {
            CudaNative.cuCtxDestroy_v2(_context);
            _context = IntPtr.Zero;
        }
    }

    private void SetCurrentContext()
    {
        if (_context == IntPtr.Zero)
            throw new InvalidOperationException("CUDA context is not initialized.");
        Check(CudaNative.cuCtxSetCurrent(_context), "cuCtxSetCurrent");
    }

    private static void EnsureRgba8(VideoFormat format)
    {
        if (format.PixelFormat != PixelFormat.Rgba8)
            throw new NotSupportedException("CUDA GPU processing foundation supports RGBA8 only.");
    }

    private void EnsureRunning()
    {
        ThrowIfDisposed();
        if (!_running)
            throw new InvalidOperationException("CUDA GPU backend is not running.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void Check(CudaResult result, string operation)
    {
        if (result != CudaResult.Success)
            throw new InvalidOperationException($"CUDA operation '{operation}' failed with '{result}' ({(int)result}).");
    }

    private static GpuBackendInfo Unavailable(string message) =>
        new(
            GpuBackendKind.NvidiaCuda,
            "NVIDIA CUDA GPU",
            hardwareAccelerated: true,
            available: false,
            totalMemoryBytes: null,
            new Failure("gpu.cuda.unavailable", message));

    private sealed record CudaAllocation(VideoFormat Format, ulong DevicePointer, nuint ByteLength);

    private sealed class CudaKernelArguments : IDisposable
    {
        private readonly List<IntPtr> _values = new();
        private IntPtr _pointerArray;

        public IntPtr PointerArray
        {
            get
            {
                if (_pointerArray != IntPtr.Zero)
                    return _pointerArray;

                _pointerArray = Marshal.AllocHGlobal(IntPtr.Size * _values.Count);
                for (var index = 0; index < _values.Count; index++)
                    Marshal.WriteIntPtr(_pointerArray, index * IntPtr.Size, _values[index]);
                return _pointerArray;
            }
        }

        public void AddUInt64(ulong value)
        {
            EnsureNotMaterialized();
            var pointer = Marshal.AllocHGlobal(sizeof(long));
            Marshal.WriteInt64(pointer, unchecked((long)value));
            _values.Add(pointer);
        }

        public void AddUInt32(uint value)
        {
            EnsureNotMaterialized();
            var pointer = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(pointer, unchecked((int)value));
            _values.Add(pointer);
        }

        public void Dispose()
        {
            if (_pointerArray != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pointerArray);
                _pointerArray = IntPtr.Zero;
            }

            foreach (var value in _values)
                Marshal.FreeHGlobal(value);
            _values.Clear();
        }

        private void EnsureNotMaterialized()
        {
            if (_pointerArray != IntPtr.Zero)
                throw new InvalidOperationException("CUDA kernel arguments cannot change after pointer materialization.");
        }
    }

    private enum CudaResult
    {
        Success = 0
    }

    private static class CudaNative
    {
        private const string Library = "nvcuda.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuInit(uint flags);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuDeviceGetCount(out int count);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuDeviceGet(out int device, int ordinal);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
        internal static extern CudaResult cuDeviceGetName(StringBuilder name, int length, int device);

        [DllImport(Library, EntryPoint = "cuDeviceTotalMem_v2", CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuDeviceTotalMem_v2(out nuint bytes, int device);

        [DllImport(Library, EntryPoint = "cuCtxCreate_v2", CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuCtxCreate_v2(out IntPtr context, uint flags, int device);

        [DllImport(Library, EntryPoint = "cuCtxDestroy_v2", CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuCtxDestroy_v2(IntPtr context);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuCtxSetCurrent(IntPtr context);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuCtxSynchronize();

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuModuleLoadData(out IntPtr module, IntPtr image);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuModuleUnload(IntPtr module);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
        internal static extern CudaResult cuModuleGetFunction(out IntPtr function, IntPtr module, string name);

        [DllImport(Library, EntryPoint = "cuMemAlloc_v2", CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuMemAlloc_v2(out ulong devicePointer, nuint bytes);

        [DllImport(Library, EntryPoint = "cuMemFree_v2", CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuMemFree_v2(ulong devicePointer);

        [DllImport(Library, EntryPoint = "cuMemcpyHtoD_v2", CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuMemcpyHtoD_v2(ulong destination, ref byte source, nuint bytes);

        [DllImport(Library, EntryPoint = "cuMemcpyDtoH_v2", CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuMemcpyDtoH_v2([Out] byte[] destination, ulong source, nuint bytes);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuLaunchKernel(
            IntPtr function,
            uint gridDimX,
            uint gridDimY,
            uint gridDimZ,
            uint blockDimX,
            uint blockDimY,
            uint blockDimZ,
            uint sharedMemBytes,
            IntPtr stream,
            IntPtr kernelParams,
            IntPtr extra);
    }

    private const string CompositeKernelPtx = """
.version 6.4
.target sm_52
.address_size 64

.visible .entry composite_rgba(
    .param .u64 p_bgA,
    .param .u64 p_bgB,
    .param .u64 p_layer,
    .param .u64 p_output,
    .param .u32 p_pixelCount,
    .param .u32 p_weight,
    .param .u32 p_layerOpacity,
    .param .u32 p_hasLayer)
{
    .reg .pred %p<3>;
    .reg .b32 %r<48>;
    .reg .b64 %rd<16>;

    ld.param.u64 %rd1, [p_bgA];
    ld.param.u64 %rd2, [p_bgB];
    ld.param.u64 %rd3, [p_layer];
    ld.param.u64 %rd4, [p_output];
    ld.param.u32 %r4, [p_pixelCount];
    ld.param.u32 %r5, [p_weight];
    ld.param.u32 %r6, [p_layerOpacity];
    ld.param.u32 %r7, [p_hasLayer];

    mov.u32 %r0, %tid.x;
    mov.u32 %r1, %ctaid.x;
    mov.u32 %r2, %ntid.x;
    mad.lo.s32 %r3, %r1, %r2, %r0;
    setp.ge.u32 %p1, %r3, %r4;
    @%p1 bra DONE;

    mul.wide.u32 %rd5, %r3, 4;
    add.s64 %rd6, %rd1, %rd5;
    add.s64 %rd7, %rd2, %rd5;
    add.s64 %rd8, %rd4, %rd5;

    ld.global.u32 %r8, [%rd6];
    ld.global.u32 %r9, [%rd7];

    and.b32 %r10, %r8, 255;
    shr.u32 %r11, %r8, 8;
    and.b32 %r11, %r11, 255;
    shr.u32 %r12, %r8, 16;
    and.b32 %r12, %r12, 255;
    shr.u32 %r13, %r8, 24;

    and.b32 %r14, %r9, 255;
    shr.u32 %r15, %r9, 8;
    and.b32 %r15, %r15, 255;
    shr.u32 %r16, %r9, 16;
    and.b32 %r16, %r16, 255;
    shr.u32 %r17, %r9, 24;

    sub.u32 %r18, 255, %r5;

    mul.lo.u32 %r19, %r10, %r18;
    mad.lo.u32 %r19, %r14, %r5, %r19;
    add.u32 %r19, %r19, 127;
    div.u32 %r19, %r19, 255;

    mul.lo.u32 %r20, %r11, %r18;
    mad.lo.u32 %r20, %r15, %r5, %r20;
    add.u32 %r20, %r20, 127;
    div.u32 %r20, %r20, 255;

    mul.lo.u32 %r21, %r12, %r18;
    mad.lo.u32 %r21, %r16, %r5, %r21;
    add.u32 %r21, %r21, 127;
    div.u32 %r21, %r21, 255;

    mul.lo.u32 %r22, %r13, %r18;
    mad.lo.u32 %r22, %r17, %r5, %r22;
    add.u32 %r22, %r22, 127;
    div.u32 %r22, %r22, 255;

    setp.eq.u32 %p2, %r7, 0;
    @%p2 bra PACK;

    add.s64 %rd9, %rd3, %rd5;
    ld.global.u32 %r23, [%rd9];
    and.b32 %r24, %r23, 255;
    shr.u32 %r25, %r23, 8;
    and.b32 %r25, %r25, 255;
    shr.u32 %r26, %r23, 16;
    and.b32 %r26, %r26, 255;
    shr.u32 %r27, %r23, 24;

    mul.lo.u32 %r28, %r27, %r6;
    add.u32 %r28, %r28, 127;
    div.u32 %r28, %r28, 255;
    sub.u32 %r29, 255, %r28;

    mul.lo.u32 %r30, %r19, %r29;
    mad.lo.u32 %r30, %r24, %r28, %r30;
    add.u32 %r30, %r30, 127;
    div.u32 %r19, %r30, 255;

    mul.lo.u32 %r31, %r20, %r29;
    mad.lo.u32 %r31, %r25, %r28, %r31;
    add.u32 %r31, %r31, 127;
    div.u32 %r20, %r31, 255;

    mul.lo.u32 %r32, %r21, %r29;
    mad.lo.u32 %r32, %r26, %r28, %r32;
    add.u32 %r32, %r32, 127;
    div.u32 %r21, %r32, 255;

    mul.lo.u32 %r33, %r22, %r29;
    add.u32 %r33, %r33, 127;
    div.u32 %r33, %r33, 255;
    add.u32 %r22, %r28, %r33;

PACK:
    shl.b32 %r34, %r20, 8;
    shl.b32 %r35, %r21, 16;
    shl.b32 %r36, %r22, 24;
    or.b32 %r37, %r19, %r34;
    or.b32 %r37, %r37, %r35;
    or.b32 %r37, %r37, %r36;
    st.global.u32 [%rd8], %r37;

DONE:
    ret;
}
""";
}
