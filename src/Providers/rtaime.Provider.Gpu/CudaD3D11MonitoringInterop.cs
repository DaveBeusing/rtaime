// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using rtaime.Media.Contracts;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace rtaime.Provider.Gpu;

internal sealed class CudaD3D11MonitoringInterop : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly long _adapterLuid;
    private bool _disposed;

    private CudaD3D11MonitoringInterop(ID3D11Device device, ID3D11DeviceContext context, long adapterLuid)
    {
        _device = device;
        _context = context;
        _adapterLuid = adapterLuid;
    }

    public static bool TryCreate(int cudaDevice, out CudaD3D11MonitoringInterop? interop)
    {
        interop = null;
        if (!OperatingSystem.IsWindows())
            return false;

        ID3D11Device? createdDevice = null;
        ID3D11DeviceContext? createdContext = null;
        try
        {
            var luidBytes = new byte[8];
            if (CudaGraphicsNative.cuDeviceGetLuid(luidBytes, out _, cudaDevice) != CudaResult.Success)
                return false;

            var adapterLuid = BinaryPrimitives.ReadInt64LittleEndian(luidBytes);
            var luid = new Luid(
                unchecked((uint)adapterLuid),
                unchecked((int)(adapterLuid >> 32)));

            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>(luid);
            Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 },
                out createdDevice,
                out createdContext).CheckError();

            interop = new CudaD3D11MonitoringInterop(createdDevice, createdContext, adapterLuid);
            createdDevice = null;
            createdContext = null;
            return true;
        }
        catch
        {
            // Device creation can yield resources even when the HRESULT fails.
            createdContext?.Dispose();
            createdDevice?.Dispose();
            interop?.Dispose();
            interop = null;
            return false;
        }
    }

    public bool TryExport(
        ulong sourceDevicePointer,
        VideoFormat format,
        out GpuBackendMonitoringResource? resource)
    {
        resource = null;
        if (_disposed || sourceDevicePointer == 0 || format.PixelFormat != PixelFormat.Rgba8)
            return false;

        ID3D11Texture2D? texture = null;
        IntPtr cudaGraphicsResource = IntPtr.Zero;
        var mapped = false;
        try
        {
            texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = format.Width,
                Height = format.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared
            });

            Check(
                CudaGraphicsNative.cuGraphicsD3D11RegisterResource(
                    out cudaGraphicsResource,
                    texture.NativePointer,
                    flags: 0),
                "cuGraphicsD3D11RegisterResource");

            var resources = new[] { cudaGraphicsResource };
            Check(CudaGraphicsNative.cuGraphicsMapResources(1, resources, IntPtr.Zero), "cuGraphicsMapResources");
            mapped = true;
            Check(
                CudaGraphicsNative.cuGraphicsSubResourceGetMappedArray(
                    out var destinationArray,
                    cudaGraphicsResource,
                    arrayIndex: 0,
                    mipLevel: 0),
                "cuGraphicsSubResourceGetMappedArray");

            var copy = new CudaMemcpy2D
            {
                SourceMemoryType = CudaMemoryType.Device,
                SourceDevice = sourceDevicePointer,
                SourcePitch = checked((nuint)(format.Width * 4U)),
                DestinationMemoryType = CudaMemoryType.Array,
                DestinationArray = destinationArray,
                WidthInBytes = checked((nuint)(format.Width * 4U)),
                Height = format.Height
            };
            Check(CudaGraphicsNative.cuMemcpy2D_v2(ref copy), "cuMemcpy2D_v2");
            Check(CudaGraphicsNative.cuGraphicsUnmapResources(1, resources, IntPtr.Zero), "cuGraphicsUnmapResources");
            mapped = false;
            Check(CudaGraphicsNative.cuCtxSynchronize(), "cuCtxSynchronize");
            Check(CudaGraphicsNative.cuGraphicsUnregisterResource(cudaGraphicsResource), "cuGraphicsUnregisterResource");
            cudaGraphicsResource = IntPtr.Zero;

            using var dxgiResource = texture.QueryInterface<IDXGIResource>();
            var sharedHandle = dxgiResource.SharedHandle;
            if (sharedHandle == IntPtr.Zero)
                throw new InvalidOperationException("D3D11 monitoring texture did not expose a shared handle.");

            _context.Flush();
            var ownedTexture = texture;
            texture = null;
            resource = new GpuBackendMonitoringResource(
                new MonitoringSharedResourceInteropDescriptor(
                    MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle,
                    _adapterLuid,
                    unchecked((ulong)sharedHandle.ToInt64())),
                ownedTexture.Dispose);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (mapped && cudaGraphicsResource != IntPtr.Zero)
            {
                var resources = new[] { cudaGraphicsResource };
                _ = CudaGraphicsNative.cuGraphicsUnmapResources(1, resources, IntPtr.Zero);
            }
            if (cudaGraphicsResource != IntPtr.Zero)
                _ = CudaGraphicsNative.cuGraphicsUnregisterResource(cudaGraphicsResource);
            texture?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _context.ClearState();
        _context.Flush();
        _context.Dispose();
        _device.Dispose();
    }

    private static void Check(CudaResult result, string operation)
    {
        if (result != CudaResult.Success)
            throw new InvalidOperationException($"CUDA graphics operation '{operation}' failed with '{result}' ({(int)result}).");
    }

    private enum CudaResult
    {
        Success = 0
    }

    private enum CudaMemoryType
    {
        Host = 1,
        Device = 2,
        Array = 3,
        Unified = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CudaMemcpy2D
    {
        public nuint SourceXInBytes;
        public nuint SourceY;
        public CudaMemoryType SourceMemoryType;
        public IntPtr SourceHost;
        public ulong SourceDevice;
        public IntPtr SourceArray;
        public nuint SourcePitch;
        public nuint DestinationXInBytes;
        public nuint DestinationY;
        public CudaMemoryType DestinationMemoryType;
        public IntPtr DestinationHost;
        public ulong DestinationDevice;
        public IntPtr DestinationArray;
        public nuint DestinationPitch;
        public nuint WidthInBytes;
        public nuint Height;
    }

    private static class CudaGraphicsNative
    {
        private const string Library = "nvcuda.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuDeviceGetLuid(
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 8)] byte[] luid,
            out uint deviceNodeMask,
            int device);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuGraphicsD3D11RegisterResource(
            out IntPtr cudaResource,
            IntPtr d3dResource,
            uint flags);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuGraphicsMapResources(
            uint count,
            [In] IntPtr[] resources,
            IntPtr stream);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuGraphicsUnmapResources(
            uint count,
            [In] IntPtr[] resources,
            IntPtr stream);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuGraphicsSubResourceGetMappedArray(
            out IntPtr array,
            IntPtr resource,
            uint arrayIndex,
            uint mipLevel);

        [DllImport(Library, EntryPoint = "cuMemcpy2D_v2", CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuMemcpy2D_v2(ref CudaMemcpy2D copy);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuGraphicsUnregisterResource(IntPtr resource);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern CudaResult cuCtxSynchronize();
    }
}
