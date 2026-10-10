using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class GpuLifecycleRecoveryTests
{
    private static readonly VideoFormat TestFormat =
        new(2, 1, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static readonly MediaSourceId SourceA =
        new(Identity.Parse("8f000000-0000-0000-0000-000000000001"));

    private static readonly MediaSourceId SourceB =
        new(Identity.Parse("8f000000-0000-0000-0000-000000000002"));

    private static readonly MediaSourceId Output =
        new(Identity.Parse("8f000000-0000-0000-0000-000000000003"));

    [Fact]
    public void Start_failure_is_explicit_and_recovery_rotates_generation()
    {
        using var backend = new FailFirstStartHardwareBackend();
        using var provider = new GpuProcessingProvider(backend);

        Assert.Throws<InvalidOperationException>(() => provider.Start());

        Assert.Equal(GpuProviderState.Failed, provider.State);
        Assert.Equal(GpuProviderLifecycleReasonCodes.StartFailed, provider.Lifecycle.ReasonCode);
        Assert.Equal(ProviderAvailabilityState.Unavailable, provider.Descriptor.Availability.State);
        Assert.Equal(1, backend.StopCount);

        provider.Recover();

        Assert.Equal(GpuProviderState.Ready, provider.State);
        Assert.Equal(GpuProviderLifecycleReasonCodes.Ready, provider.Lifecycle.ReasonCode);
        Assert.Equal(1UL, provider.Lifecycle.Generation);
        Assert.Equal(ProviderAvailabilityState.Available, provider.Descriptor.Availability.State);
    }

    [Fact]
    public void Cuda_classified_composite_failure_fails_closed_until_recovery()
    {
        using var backend = new FailFirstCompositeHardwareBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        var firstGeneration = provider.Lifecycle.Generation;

        using var a = Upload(provider, SourceA, 10, 20, 30, 0);
        using var b = Upload(provider, SourceB, 40, 50, 60, 0);
        var request = new GpuCompositeRequest(Output, a, b, GpuTransition.CutToA);

        var failed = provider.Composite(request);

        Assert.False(failed.Succeeded);
        Assert.Equal(GpuProviderLifecycleReasonCodes.CompositeFailed, failed.Failure?.Code);
        Assert.Equal(GpuProviderState.Failed, provider.State);
        Assert.Throws<InvalidOperationException>(() => provider.Composite(request));

        provider.Recover();

        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.Equal(0, provider.ActiveSurfaceCount);
        Assert.True(provider.Lifecycle.Generation > firstGeneration);

        using var recoveredA = Upload(provider, SourceA, 10, 20, 30, 1);
        using var recoveredB = Upload(provider, SourceB, 40, 50, 60, 1);
        var recovered = provider.Composite(new GpuCompositeRequest(
            Output,
            recoveredA,
            recoveredB,
            GpuTransition.CutToA));

        Assert.True(recovered.Succeeded, recovered.Failure?.ToString());
        recovered.Frame!.Dispose();
    }

    [Fact]
    public void Cuda_classified_upload_failure_fails_closed()
    {
        using var backend = new FailFirstAllocateHardwareBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        Assert.Throws<InvalidOperationException>(() => Upload(provider, SourceA, 1, 2, 3, 0));
        Assert.Equal(GpuProviderState.Failed, provider.State);
        Assert.Equal(GpuProviderLifecycleReasonCodes.UploadFailed, provider.Lifecycle.ReasonCode);
        Assert.Equal(0, provider.ActiveSurfaceCount);
    }

    [Fact]
    public void Cuda_classified_readback_failure_returns_host_lease_and_fails_closed()
    {
        using var backend = new FailFirstReadbackHardwareBackend();
        using var provider = new GpuProcessingProvider(backend, readbackBufferCapacity: 1);
        provider.Start();
        using var frame = Upload(provider, SourceA, 1, 2, 3, 0);

        Assert.Throws<InvalidOperationException>(() => provider.RentReadback(frame));
        Assert.Equal(GpuProviderState.Failed, provider.State);
        Assert.Equal(GpuProviderLifecycleReasonCodes.ReadbackFailed, provider.Lifecycle.ReasonCode);
        Assert.Equal(0, provider.ReadbackPoolStatistics.ActiveBuffers);
        Assert.Equal(1, provider.ReadbackPoolStatistics.AvailableBuffers);
    }

    [Fact]
    public void Readback_pool_exhaustion_degrades_without_losing_the_active_lease()
    {
        using var backend = new HardwareFacadeBackend();
        using var provider = new GpuProcessingProvider(backend, readbackBufferCapacity: 1);
        provider.Start();
        using var frame = Upload(provider, SourceA, 1, 2, 3, 0);
        using var first = provider.RentReadback(frame);

        Assert.Throws<InvalidOperationException>(() => provider.RentReadback(frame));

        Assert.Equal(GpuProviderState.Degraded, provider.State);
        Assert.Equal(GpuProviderLifecycleReasonCodes.ReadbackPoolExhausted, provider.Lifecycle.ReasonCode);
        Assert.Equal(1, provider.ReadbackPoolStatistics.ActiveBuffers);

        first.Dispose();
        using var recovered = provider.RentReadback(frame);

        Assert.Equal(GpuProviderState.Ready, provider.State);
        Assert.Equal(1, recovered.Memory.Span[0]);
    }

    [Fact]
    public void Stop_failure_is_explicit_and_retryable()
    {
        using var backend = new FailFirstStopHardwareBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        using var frame = Upload(provider, SourceA, 1, 2, 3, 0);

        Assert.Throws<InvalidOperationException>(() => provider.Stop());
        Assert.Equal(GpuProviderState.Failed, provider.State);
        Assert.Equal(GpuProviderLifecycleReasonCodes.StopFailed, provider.Lifecycle.ReasonCode);

        provider.Stop();

        Assert.Equal(GpuProviderState.Stopped, provider.State);
        Assert.Equal(0, provider.ActiveSurfaceCount);
    }

    [Fact]
    public void Monitoring_export_failure_is_isolated_from_program_and_can_recover()
    {
        using var backend = new FailFirstMonitoringExportHardwareBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        using var frame = Upload(provider, SourceA, 1, 2, 3, 0);

        Assert.False(provider.TryExportMonitoringResource(frame, out var missing));
        Assert.Null(missing);
        Assert.Equal(GpuProviderState.Degraded, provider.State);
        Assert.Equal(GpuProviderLifecycleReasonCodes.MonitoringUnavailable, provider.Lifecycle.ReasonCode);

        using var readback = provider.RentReadback(frame);
        Assert.Equal(1, readback.Memory.Span[0]);

        Assert.True(provider.TryExportMonitoringResource(frame, out var recovered));
        Assert.Equal(GpuProviderState.Ready, provider.State);
        recovered!.Dispose();
    }

    [Fact]
    public void Monitoring_release_failure_remains_tracked_and_is_retryable()
    {
        using var backend = new FailFirstMonitoringReleaseHardwareBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        using var frame = Upload(provider, SourceA, 1, 2, 3, 0);
        Assert.True(provider.TryExportMonitoringResource(frame, out var lease));
        Assert.NotNull(lease);

        Assert.Throws<InvalidOperationException>(() => lease!.Dispose());

        Assert.False(lease!.IsDisposed);
        Assert.Equal(1, provider.SharedMonitoringResourceStatistics.ActiveResources);
        Assert.Equal(GpuProviderState.Degraded, provider.State);
        Assert.Equal(GpuProviderLifecycleReasonCodes.MonitoringReleaseFailed, provider.Lifecycle.ReasonCode);

        lease.Dispose();

        Assert.True(lease.IsDisposed);
        Assert.Equal(0, provider.SharedMonitoringResourceStatistics.ActiveResources);
        Assert.Equal(GpuProviderState.Ready, provider.State);
    }

    [Fact]
    public void Concurrent_monitoring_lease_disposal_releases_backend_exactly_once()
    {
        using var backend = new CountingMonitoringBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        using var frame = Upload(provider, SourceA, 3, 4, 5, 0);
        Assert.True(provider.TryExportMonitoringResource(frame, out var lease));
        Assert.NotNull(lease);

        Parallel.For(0, 32, _ => lease!.Dispose());

        Assert.True(lease!.IsDisposed);
        Assert.Equal(1, backend.ReleaseCount);
        Assert.Equal(0, provider.SharedMonitoringResourceStatistics.ActiveResources);
    }

    [Fact]
    public void Shared_monitoring_descriptor_expires_across_stop_and_restart_generation()
    {
        using var backend = new MonitoringHardwareBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        var firstProviderInstance = provider.MonitoringProviderInstanceId;

        using var frame = Upload(provider, SourceA, 1, 2, 3, 0);
        Assert.True(provider.TryExportMonitoringResource(frame, out var lease));
        Assert.NotNull(lease);
        var descriptor = lease!.Descriptor;

        Assert.True(provider.IsMonitoringResourceActive(descriptor));

        provider.Stop();

        Assert.False(provider.IsMonitoringResourceActive(descriptor));
        Assert.Equal(0, provider.SharedMonitoringResourceStatistics.ActiveResources);

        provider.Start();

        Assert.NotEqual(firstProviderInstance, provider.MonitoringProviderInstanceId);
        Assert.False(provider.IsMonitoringResourceActive(descriptor));

        lease.Dispose();
        Assert.True(lease.IsDisposed);
    }

    private static GpuFrame Upload(
        GpuProcessingProvider provider,
        MediaSourceId sourceId,
        byte red,
        byte green,
        byte blue,
        ulong sequence) =>
        provider.Upload(
            sourceId,
            RgbaFrameBuffer.Solid(TestFormat, red, green, blue),
            new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)),
            new Generation(sequence),
            "lifecycle-test");

    private class HardwareFacadeBackend : IGpuProcessingBackend
    {
        protected readonly ManagedReferenceGpuBackend Inner = new();

        public virtual GpuBackendInfo Info { get; } = new(
            GpuBackendKind.NvidiaCuda,
            "CUDA Lifecycle Test Device",
            hardwareAccelerated: true,
            available: true);

        public virtual SurfaceStorageDomain StorageDomain => SurfaceStorageDomain.Device;

        public virtual void Start() => Inner.Start();
        public virtual void Stop() => Inner.Stop();
        public virtual void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels) =>
            Inner.Allocate(surfaceId, format, rgbaPixels);
        public virtual void Composite(SurfaceId outputSurfaceId, VideoFormat format, GpuCompositeOperation operation) =>
            Inner.Composite(outputSurfaceId, format, operation);
        public virtual byte[] Readback(SurfaceId surfaceId, VideoFormat format) => Inner.Readback(surfaceId, format);
        public virtual void ReadbackInto(SurfaceId surfaceId, VideoFormat format, Span<byte> destination) =>
            Inner.ReadbackInto(surfaceId, format, destination);
        public virtual void Release(SurfaceId surfaceId) => Inner.Release(surfaceId);
        public virtual void Dispose() => Inner.Dispose();
    }

    private sealed class FailFirstStartHardwareBackend : HardwareFacadeBackend
    {
        private bool _first = true;
        public int StopCount { get; private set; }

        public override void Start()
        {
            Inner.Start();
            if (_first)
            {
                _first = false;
                throw new InvalidOperationException("Injected CUDA start failure.");
            }
        }

        public override void Stop()
        {
            StopCount++;
            Inner.Stop();
        }
    }

    private sealed class FailFirstAllocateHardwareBackend : HardwareFacadeBackend
    {
        private bool _first = true;

        public override void Allocate(
            SurfaceId surfaceId,
            VideoFormat format,
            ReadOnlySpan<byte> rgbaPixels)
        {
            if (_first)
            {
                _first = false;
                throw new InvalidOperationException("Injected CUDA upload failure.");
            }

            base.Allocate(surfaceId, format, rgbaPixels);
        }
    }

    private sealed class FailFirstReadbackHardwareBackend : HardwareFacadeBackend
    {
        private bool _first = true;

        public override void ReadbackInto(
            SurfaceId surfaceId,
            VideoFormat format,
            Span<byte> destination)
        {
            if (_first)
            {
                _first = false;
                throw new InvalidOperationException("Injected CUDA readback failure.");
            }

            base.ReadbackInto(surfaceId, format, destination);
        }
    }

    private sealed class FailFirstCompositeHardwareBackend : HardwareFacadeBackend
    {
        private bool _first = true;

        public override void Composite(
            SurfaceId outputSurfaceId,
            VideoFormat format,
            GpuCompositeOperation operation)
        {
            if (_first)
            {
                _first = false;
                throw new InvalidOperationException("Injected CUDA composite failure.");
            }

            base.Composite(outputSurfaceId, format, operation);
        }
    }

    private sealed class FailFirstStopHardwareBackend : HardwareFacadeBackend
    {
        private bool _first = true;

        public override void Stop()
        {
            if (_first)
            {
                _first = false;
                throw new InvalidOperationException("Injected CUDA stop failure.");
            }

            base.Stop();
        }
    }

    private class MonitoringHardwareBackend : HardwareFacadeBackend, IGpuSharedMonitoringBackend
    {
        public bool SupportsSharedMonitoringResources => true;
        public bool IsSharedMonitoringExportAvailable => true;

        public virtual bool TryExportMonitoringResource(
            SurfaceId surfaceId,
            VideoFormat format,
            out GpuBackendMonitoringResource? resource)
        {
            resource = CreateMonitoringResource(static () => { });
            return true;
        }

        protected static GpuBackendMonitoringResource CreateMonitoringResource(Action release) =>
            new(
                new MonitoringSharedResourceInteropDescriptor(
                    MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle,
                    adapterLuid: 1,
                    sharedHandle: 0xCAFE),
                release);
    }

    private sealed class CountingMonitoringBackend : MonitoringHardwareBackend
    {
        private int _releaseCount;
        public int ReleaseCount => Volatile.Read(ref _releaseCount);

        public override bool TryExportMonitoringResource(
            SurfaceId surfaceId,
            VideoFormat format,
            out GpuBackendMonitoringResource? resource)
        {
            resource = CreateMonitoringResource(() => Interlocked.Increment(ref _releaseCount));
            return true;
        }
    }

    private sealed class FailFirstMonitoringExportHardwareBackend : MonitoringHardwareBackend
    {
        private bool _first = true;

        public override bool TryExportMonitoringResource(
            SurfaceId surfaceId,
            VideoFormat format,
            out GpuBackendMonitoringResource? resource)
        {
            if (_first)
            {
                _first = false;
                resource = null;
                return false;
            }

            return base.TryExportMonitoringResource(surfaceId, format, out resource);
        }
    }

    private sealed class FailFirstMonitoringReleaseHardwareBackend : MonitoringHardwareBackend
    {
        private bool _failRelease = true;

        public override bool TryExportMonitoringResource(
            SurfaceId surfaceId,
            VideoFormat format,
            out GpuBackendMonitoringResource? resource)
        {
            resource = CreateMonitoringResource(() =>
            {
                if (_failRelease)
                {
                    _failRelease = false;
                    throw new InvalidOperationException("Injected monitoring release failure.");
                }
            });
            return true;
        }
    }
}
