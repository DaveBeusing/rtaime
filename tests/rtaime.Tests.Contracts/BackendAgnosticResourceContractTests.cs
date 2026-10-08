using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class BackendAgnosticResourceContractTests
{
    [Fact]
    public void Surface_completion_semantics_require_producer_completion_before_publication()
    {
        Assert.Equal(
            SurfaceCompletionSemantics.ProducerCompletedBeforePublication,
            SurfaceContractSemantics.Completion);
    }

    [Fact]
    public void Surface_read_compatibility_requires_exact_format_generation_and_valid_shared_lease()
    {
        var generation = new Generation(7);
        var surface = new SurfaceDescriptor(
            SurfaceId.New(),
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Shared,
            SurfaceOwnership.SharedLease,
            new SurfaceLifetimeDescriptor(generation, Identity.New()),
            new OpaqueSurfaceHandle("contract.test", "surface"));

        Assert.True(SurfaceContractSemantics.HasValidOwnershipLifetime(surface));
        Assert.True(SurfaceContractSemantics.IsCompatibleForRead(
            surface,
            VideoFormat.Hd1080p50Rgba8,
            generation));

        Assert.False(SurfaceContractSemantics.IsCompatibleForRead(
            surface,
            VideoFormat.Hd1080p59_94Rgba8,
            generation));
        Assert.False(SurfaceContractSemantics.IsCompatibleForRead(
            surface,
            VideoFormat.Hd1080p50Rgba8,
            new Generation(8)));
    }

    [Fact]
    public void Shared_lease_without_explicit_lease_identity_is_rejected_for_consumption()
    {
        var generation = new Generation(3);
        var surface = new SurfaceDescriptor(
            SurfaceId.New(),
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Shared,
            SurfaceOwnership.SharedLease,
            new SurfaceLifetimeDescriptor(generation, null),
            new OpaqueSurfaceHandle("contract.test", "surface"));

        Assert.False(SurfaceContractSemantics.HasValidOwnershipLifetime(surface));
        Assert.False(SurfaceContractSemantics.IsCompatibleForRead(
            surface,
            VideoFormat.Hd1080p50Rgba8,
            generation));
        Assert.Throws<InvalidOperationException>(() =>
            SurfaceContractSemantics.EnsureCompatibleForRead(
                surface,
                VideoFormat.Hd1080p50Rgba8,
                generation));
    }

    [Fact]
    public void Provider_video_format_support_is_exact_and_backend_neutral()
    {
        var capability = new ProviderCapabilityDescriptor(
            CapabilityId.New(),
            "gpu.rgba.composite",
            new[]
            {
                VideoFormat.Hd1080p50Rgba8,
                VideoFormat.Hd1080p59_94Rgba8
            });

        Assert.True(capability.SupportsVideoFormat(VideoFormat.Hd1080p50Rgba8));
        Assert.True(capability.SupportsVideoFormat(VideoFormat.Hd1080p59_94Rgba8));
        Assert.False(capability.SupportsVideoFormat(new VideoFormat(
            1280,
            720,
            FrameRate.Fps50,
            PixelFormat.Rgba8,
            ScanMode.Progressive)));
    }

    [Fact]
    public void Additive_resource_semantics_do_not_change_v1_contract_versions()
    {
        Assert.Equal(new CompatibilityVersion(1, 0), MediaContractVersion.Current);
        Assert.Equal(new CompatibilityVersion(1, 0), ProviderContractVersion.Current);
        Assert.Equal(new CompatibilityVersion(1, 3), MonitoringContractVersion.Current);
    }
}
