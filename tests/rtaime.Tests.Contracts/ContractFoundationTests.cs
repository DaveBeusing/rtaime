using System.Text.Json;
using rtaime.AI.Contracts;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class ContractFoundationTests
{
    [Fact]
    public void All_contract_families_fail_closed_for_unknown_versions()
    {
        var unknown = new CompatibilityVersion(99, 0);

        Assert.False(ControlContractVersion.IsSupported(unknown));
        Assert.False(RuntimeContractVersion.IsSupported(unknown));
        Assert.False(MediaContractVersion.IsSupported(unknown));
        Assert.False(ProviderContractVersion.IsSupported(unknown));
        Assert.False(AIContractVersion.IsSupported(unknown));

        Assert.Throws<NotSupportedException>(() => ControlContractVersion.EnsureSupported(unknown));
        Assert.Throws<NotSupportedException>(() => RuntimeContractVersion.EnsureSupported(unknown));
        Assert.Throws<NotSupportedException>(() => MediaContractVersion.EnsureSupported(unknown));
        Assert.Throws<NotSupportedException>(() => ProviderContractVersion.EnsureSupported(unknown));
        Assert.Throws<NotSupportedException>(() => AIContractVersion.EnsureSupported(unknown));
    }

    [Fact]
    public void Contract_versions_are_explicitly_v1_0()
    {
        var expected = new CompatibilityVersion(1, 0);

        Assert.Equal(expected, ControlContractVersion.Current);
        Assert.Equal(expected, RuntimeContractVersion.Current);
        Assert.Equal(expected, MediaContractVersion.Current);
        Assert.Equal(expected, ProviderContractVersion.Current);
        Assert.Equal(expected, AIContractVersion.Current);
    }

    [Fact]
    public void Control_contracts_snapshot_input_collections_and_round_trip()
    {
        var sourceA = new ProductionSourceSpecification(ProductionSourceId.New(), "Camera A");
        var sourceB = new ProductionSourceSpecification(ProductionSourceId.New(), "Camera B");
        var mutableSources = new List<ProductionSourceSpecification> { sourceA, sourceB };
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            ProductionId.New(),
            "Reference production",
            mutableSources,
            new ProductionRoutingState(sourceA.SourceId, sourceB.SourceId));

        mutableSources.Clear();

        Assert.Equal(2, specification.Sources.Count);
        var copy = RoundTrip(specification);
        Assert.Equal(specification.ProductionId, copy.ProductionId);
        Assert.Equal(specification.Name, copy.Name);
        Assert.Equal(2, copy.Sources.Count);
        Assert.Equal(sourceA.SourceId, copy.InitialRouting.PreviewSourceId);
    }

    [Fact]
    public void Control_command_metadata_preserves_expected_revision()
    {
        var metadata = new ControlCommandMetadata(
            ControlContractVersion.Current,
            CommandId.New(),
            ProductionId.New(),
            new Revision(42));
        var command = new SelectPreviewCommand(metadata, ProductionSourceId.New());

        var copy = RoundTrip(command);

        Assert.Equal(new Revision(42), copy.Metadata.ExpectedRevision);
        Assert.Equal(command.SourceId, copy.SourceId);
    }

    [Fact]
    public void Control_validation_report_is_immutable_and_transportable()
    {
        var issues = new List<ValidationIssue> { new("control.invalid", "Invalid state", "routing.program") };
        var report = new ControlValidationReport(issues);
        issues.Clear();

        Assert.False(report.IsValid);
        Assert.Single(report.Issues);

        var copy = RoundTrip(report);
        Assert.False(copy.IsValid);
        Assert.Single(copy.Issues);
        Assert.Equal("control.invalid", copy.Issues[0].Code);
    }

    [Fact]
    public void Media_video_formats_cover_v1_reference_rates()
    {
        Assert.Equal(FrameRate.Fps50, VideoFormat.Hd1080p50Rgba8.FrameRate);
        Assert.Equal(FrameRate.Fps59_94, VideoFormat.Hd1080p59_94Rgba8.FrameRate);
        Assert.Equal(1920u, VideoFormat.Hd1080p50Rgba8.Width);
        Assert.Equal(1080u, VideoFormat.Hd1080p50Rgba8.Height);
    }

    [Fact]
    public void Media_contract_rejects_unknown_enum_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VideoFormat(1920, 1080, FrameRate.Fps50, (PixelFormat)999, ScanMode.Progressive));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SurfaceDescriptor(
                SurfaceId.New(),
                VideoFormat.Hd1080p50Rgba8,
                (SurfaceStorageDomain)999,
                SurfaceOwnership.ProducerOwned,
                new SurfaceLifetimeDescriptor(Generation.Initial, null),
                null));
    }

    [Fact]
    public void Frame_descriptor_round_trips_without_bulk_media_payload()
    {
        var descriptor = ContractFixtures.Frame();
        var json = JsonSerializer.Serialize(descriptor);
        var copy = JsonSerializer.Deserialize<FrameDescriptor>(json);

        Assert.NotNull(copy);
        Assert.Equal(descriptor.SourceId, copy.SourceId);
        Assert.Equal(descriptor.Surface.SurfaceId, copy.Surface.SurfaceId);
        Assert.Equal(descriptor.Timing.SequenceNumber, copy.Timing.SequenceNumber);
        Assert.DoesNotContain("PixelData", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BufferData", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_descriptor_round_trips_with_capabilities_resources_and_availability()
    {
        var descriptor = ContractFixtures.Provider();
        var copy = RoundTrip(descriptor);

        Assert.Equal(descriptor.ProviderId, copy.ProviderId);
        Assert.Equal(ProviderAvailabilityState.Available, copy.Availability.State);
        Assert.Single(copy.Capabilities);
        Assert.Single(copy.Resources);
    }

    [Fact]
    public void Provider_unavailable_state_requires_failure()
    {
        Assert.Throws<ArgumentException>(() => new ProviderAvailability(ProviderAvailabilityState.Unavailable));
        var availability = new ProviderAvailability(
            ProviderAvailabilityState.Unavailable,
            new Failure("provider.offline", "Provider is offline."));
        Assert.Equal(ProviderAvailabilityState.Unavailable, availability.State);
    }

    [Fact]
    public void Prepared_execution_contract_round_trips_without_control_contract_dependency()
    {
        var prepared = ContractFixtures.PreparedExecution();
        var copy = RoundTrip(prepared);

        Assert.Equal(prepared.PreparedExecutionId, copy.PreparedExecutionId);
        Assert.Equal(prepared.AuthoritySnapshot.StateId, copy.AuthoritySnapshot.StateId);
        Assert.Equal(prepared.AuthoritySnapshot.Revision, copy.AuthoritySnapshot.Revision);
        Assert.Single(copy.Bindings);
    }

    [Fact]
    public void Runtime_prepare_and_commit_results_enforce_structural_outcomes()
    {
        var preparedId = PreparedExecutionId.New();
        var reservationId = Identity.New();
        var prepared = new RuntimePrepareResult(
            RuntimeContractVersion.Current,
            preparedId,
            RuntimePrepareStatus.Prepared,
            reservationId,
            null);

        Assert.Equal(RuntimePrepareStatus.Prepared, RoundTrip(prepared).Status);

        Assert.Throws<ArgumentException>(() => new RuntimePrepareResult(
            RuntimeContractVersion.Current,
            preparedId,
            RuntimePrepareStatus.Prepared,
            null,
            null));

        var committed = new RuntimeCommitResult(
            RuntimeContractVersion.Current,
            RuntimeCommitStatus.Committed,
            ExecutionInstanceId.New(),
            new Revision(1),
            null);

        Assert.Equal(RuntimeCommitStatus.Committed, RoundTrip(committed).Status);
    }

    [Fact]
    public void AI_request_canonicalizes_parameter_order_and_round_trips()
    {
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            InferenceCapabilityId.New(),
            ContractFixtures.Frame(),
            new UtcTimestamp(DateTimeOffset.Parse("2026-09-15T12:00:00+00:00")),
            new[]
            {
                new InferenceParameter("zeta", "2"),
                new InferenceParameter("alpha", "1")
            });

        Assert.Equal(new[] { "alpha", "zeta" }, request.Parameters.Select(parameter => parameter.Name));

        var copy = RoundTrip(request);
        Assert.Equal(request.RequestId, copy.RequestId);
        Assert.Equal(new[] { "alpha", "zeta" }, copy.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public void AI_result_requires_failure_for_non_success_status()
    {
        Assert.Throws<ArgumentException>(() => new GovernedInferenceResult(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            InferenceExecutionStatus.Failed,
            Array.Empty<InferenceOutput>(),
            null));

        var result = new GovernedInferenceResult(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            InferenceExecutionStatus.Succeeded,
            new[] { new InferenceOutput("label", "person") },
            null);

        Assert.Equal(InferenceExecutionStatus.Succeeded, RoundTrip(result).Status);
    }

    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new Xunit.Sdk.XunitException($"JSON round-trip returned null for {typeof(T).FullName}.");
    }
}

internal static class ContractFixtures
{
    public static FrameDescriptor Frame()
    {
        var surface = new SurfaceDescriptor(
            SurfaceId.New(),
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Shared,
            SurfaceOwnership.SharedLease,
            new SurfaceLifetimeDescriptor(Generation.Initial, Identity.New()),
            new OpaqueSurfaceHandle("fixture", "surface-001"));

        return new FrameDescriptor(
            MediaContractVersion.Current,
            MediaSourceId.New(),
            surface,
            new FrameTiming(7, 12_600, new Timebase(1, 90_000)));
    }

    public static ProviderDescriptor Provider()
    {
        var providerId = ProviderId.New();
        var capability = new ProviderCapabilityDescriptor(
            CapabilityId.New(),
            "video.source",
            new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 });
        var resource = new ProviderResourceDescriptor(
            ProviderResourceId.New(),
            providerId,
            "video.source",
            2,
            true);

        return new ProviderDescriptor(
            ProviderContractVersion.Current,
            providerId,
            "Fixture Provider",
            new ProviderAvailability(ProviderAvailabilityState.Available),
            new[] { capability },
            new[] { resource });
    }

    public static PreparedExecutionContract PreparedExecution()
    {
        var provider = Provider();
        var capability = provider.Capabilities[0];
        var binding = new PreparedExecutionBinding(
            Identity.New(),
            capability.CapabilityId,
            provider.Resources[0],
            MediaSourceId.New(),
            null);

        return new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(Identity.New(), new Revision(3)),
            new Generation(2),
            new[] { binding });
    }
}
