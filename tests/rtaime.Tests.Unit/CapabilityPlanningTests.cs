using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Tests.Unit;

public sealed class CapabilityPlanningTests
{
    [Fact]
    public void CompleteCapabilityCoverageProducesPreparedExecution()
    {
        var specification = CreateSpecification();
        var authoritative = CreateAuthoritative(specification);
        var provider = CreateProvider(100, 2);

        var result = CapabilityPlanningEngine.Plan(
            specification,
            authoritative,
            new StaticProviderRegistry(provider));

        Assert.True(result.Succeeded);
        Assert.True(result.Validation.IsValid);
        Assert.NotNull(result.Graph);
        Assert.Equal(6, result.Graph!.Nodes.Count);
        Assert.Equal(4, result.Graph.Edges.Count);
        Assert.NotNull(result.Admission);
        Assert.Equal(2, result.Admission!.Bindings.Count);
        Assert.NotNull(result.Plan);
        Assert.Equal(2, result.Plan!.Bindings.Count);
        Assert.NotNull(result.PreparedExecution);
        Assert.Equal(2, result.PreparedExecution!.Bindings.Count);
        Assert.Equal(specification.ProductionId.Value, result.PreparedExecution.AuthoritySnapshot.StateId);
        Assert.Equal(authoritative.Revision, result.PreparedExecution.AuthoritySnapshot.Revision);
        Assert.All(result.PreparedExecution.Bindings, binding =>
        {
            Assert.NotNull(binding.MediaSourceId);
            Assert.NotNull(binding.MediaSinkId);
        });
    }

    [Fact]
    public void MissingCapabilityFailsClosed()
    {
        var specification = CreateSpecification();
        var authoritative = CreateAuthoritative(specification);
        var provider = CreateProvider(100, 2, capabilityKind: "unrelated.capability");

        var result = CapabilityPlanningEngine.Plan(
            specification,
            authoritative,
            new StaticProviderRegistry(provider));

        Assert.False(result.Succeeded);
        Assert.Null(result.PreparedExecution);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "planning.capability.missing");
    }

    [Fact]
    public void IncompatibleVideoCapabilityDoesNotMatch()
    {
        var requirement = new CapabilityRequirement(
            ProviderContractVersion.Current,
            Id(900),
            PlanningCapabilityKinds.MediaRoute,
            1,
            new[] { VideoFormat.Hd1080p50Rgba8 });

        var capability = new ProviderCapabilityDescriptor(
            new CapabilityId(Id(901)),
            PlanningCapabilityKinds.MediaRoute,
            new[] { VideoFormat.Hd1080p59_94Rgba8 });

        Assert.False(CapabilityRequirementMatcher.Matches(requirement, capability));
    }

    [Fact]
    public void MultipleProvidersUseDeterministicLowestIdentitySelection()
    {
        var specification = CreateSpecification();
        var authoritative = CreateAuthoritative(specification);
        var selected = CreateProvider(100, 2);
        var later = CreateProvider(200, 2);

        var result = CapabilityPlanningEngine.Plan(
            specification,
            authoritative,
            new StaticProviderRegistry(later, selected));

        Assert.True(result.Succeeded);
        Assert.All(result.Admission!.Bindings, binding => Assert.Equal(selected.ProviderId, binding.ProviderId));
    }

    [Fact]
    public void ResourceExhaustionFailsClosedWithoutPreparedExecution()
    {
        var specification = CreateSpecification();
        var authoritative = CreateAuthoritative(specification);
        var provider = CreateProvider(100, 1);

        var result = CapabilityPlanningEngine.Plan(
            specification,
            authoritative,
            new StaticProviderRegistry(provider));

        Assert.False(result.Succeeded);
        Assert.Null(result.Plan);
        Assert.Null(result.PreparedExecution);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "planning.resource.exhausted");
    }

    [Fact]
    public void GraphValidationRejectsUnknownEdgeEndpoint()
    {
        var source = new ProductionSourceId(Id(1));
        var mediaSource = new MediaSourceId(source.Value);
        var previewSinkId = new MediaSinkId(Id(10));
        var programSinkId = new MediaSinkId(Id(11));
        var previewRouteId = Id(20);
        var programRouteId = Id(21);

        var graph = new LogicalProductionGraph(
            new ProductionId(Id(30)),
            Revision.Initial,
            new LogicalProductionNode[]
            {
                new(Id(2), LogicalProductionNodeKind.SourceEndpoint, "Source", source, mediaSource, null),
                new(previewRouteId, LogicalProductionNodeKind.PreviewRoute, "Preview Route", null, mediaSource, previewSinkId),
                new(programRouteId, LogicalProductionNodeKind.ProgramRoute, "Program Route", null, mediaSource, programSinkId),
                new(Id(22), LogicalProductionNodeKind.PreviewSink, "Preview Sink", null, null, previewSinkId),
                new(Id(23), LogicalProductionNodeKind.ProgramSink, "Program Sink", null, null, programSinkId)
            },
            new[]
            {
                new LogicalProductionEdge(Id(40), Id(999), previewRouteId, "invalid-source")
            });

        var validation = LogicalProductionGraphValidator.Validate(graph);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "planning.graph.edge_endpoint_unknown");
    }

    [Fact]
    public void PlanningOutputIsStableAcrossProviderSnapshotOrder()
    {
        var specification = CreateSpecification();
        var authoritative = CreateAuthoritative(specification);
        var firstProvider = CreateProvider(100, 2);
        var secondProvider = CreateProvider(200, 2);

        var first = CapabilityPlanningEngine.Plan(
            specification,
            authoritative,
            new StaticProviderRegistry(firstProvider, secondProvider));
        var second = CapabilityPlanningEngine.Plan(
            specification,
            authoritative,
            new StaticProviderRegistry(secondProvider, firstProvider));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.PreparedExecution!.PreparedExecutionId, second.PreparedExecution!.PreparedExecutionId);
        Assert.Equal(
            first.PreparedExecution.Bindings.Select(binding => binding.LogicalNodeId),
            second.PreparedExecution.Bindings.Select(binding => binding.LogicalNodeId));
        Assert.Equal(
            first.PreparedExecution.Bindings.Select(binding => binding.Resource.ResourceId),
            second.PreparedExecution.Bindings.Select(binding => binding.Resource.ResourceId));
        Assert.Equal(
            first.Graph!.Nodes.Select(node => node.NodeId),
            second.Graph!.Nodes.Select(node => node.NodeId));
        Assert.Equal(
            first.Graph.Edges.Select(edge => edge.EdgeId),
            second.Graph.Edges.Select(edge => edge.EdgeId));
    }

    [Fact]
    public void DuplicateProviderIdentityFailsRegistryValidationClosed()
    {
        var specification = CreateSpecification();
        var authoritative = CreateAuthoritative(specification);
        var provider = CreateProvider(100, 2);

        var result = CapabilityPlanningEngine.Plan(
            specification,
            authoritative,
            new StaticProviderRegistry(provider, provider));

        Assert.False(result.Succeeded);
        Assert.Null(result.PreparedExecution);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "planning.registry.provider_identity_duplicate");
    }

    [Fact]
    public void AuthoritativeProductionMismatchFailsBeforeGraphCompilation()
    {
        var specification = CreateSpecification();
        var authoritative = CreateAuthoritative(specification);
        var mismatched = new AuthoritativeProductionState(
            authoritative.Version,
            new ProductionId(Id(9999)),
            authoritative.Revision,
            authoritative.Routing);

        var result = CapabilityPlanningEngine.Plan(
            specification,
            mismatched,
            new StaticProviderRegistry(CreateProvider(100, 2)));

        Assert.False(result.Succeeded);
        Assert.Null(result.Graph);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "planning.authority.production_mismatch");
    }

    [Fact]
    public void NewAuthoritativeRevisionProducesDifferentStablePreparedExecution()
    {
        var specification = CreateSpecification();
        var initial = CreateAuthoritative(specification);
        var provider = CreateProvider(100, 2);

        var first = CapabilityPlanningEngine.Plan(
            specification,
            initial,
            new StaticProviderRegistry(provider));

        var command = new CutProgramCommand(
            new ControlCommandMetadata(
                ControlContractVersion.Current,
                new CommandId(Id(800)),
                specification.ProductionId,
                initial.Revision),
            specification.Sources[0].SourceId);
        var transition = ControlDomainEngine.Apply(specification, initial, command);
        Assert.True(transition.Committed);

        var second = CapabilityPlanningEngine.Plan(
            specification,
            transition.AuthoritativeState,
            new StaticProviderRegistry(provider));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(first.PreparedExecution!.PreparedExecutionId, second.PreparedExecution!.PreparedExecutionId);
        Assert.Equal(initial.Revision.Value, first.PreparedExecution.PlanGeneration.Value);
        Assert.Equal(transition.AuthoritativeState.Revision.Value, second.PreparedExecution.PlanGeneration.Value);
    }

    private static ProductionSpecification CreateSpecification()
    {
        var sourceA = new ProductionSourceId(Id(1));
        var sourceB = new ProductionSourceId(Id(2));

        return new ProductionSpecification(
            ControlContractVersion.Current,
            new ProductionId(Id(10)),
            "Planning Test Production",
            new[]
            {
                new ProductionSourceSpecification(sourceA, "Source A"),
                new ProductionSourceSpecification(sourceB, "Source B")
            },
            new ProductionRoutingState(sourceA, sourceB));
    }

    private static AuthoritativeProductionState CreateAuthoritative(ProductionSpecification specification)
    {
        var initialized = ControlDomainEngine.Initialize(specification);
        Assert.True(initialized.Succeeded);
        return initialized.State!.Authoritative;
    }

    private static ProviderDescriptor CreateProvider(int seed, int resourceCount, string? capabilityKind = null)
    {
        var providerId = new ProviderId(Id(seed));
        var kind = capabilityKind ?? PlanningCapabilityKinds.MediaRoute;
        var resources = Enumerable.Range(0, resourceCount)
            .Select(index => new ProviderResourceDescriptor(
                new ProviderResourceId(Id(seed * 10 + index + 1)),
                providerId,
                kind,
                1,
                true))
            .ToArray();

        return new ProviderDescriptor(
            ProviderContractVersion.Current,
            providerId,
            $"Provider {seed}",
            new ProviderAvailability(ProviderAvailabilityState.Available),
            new[]
            {
                new ProviderCapabilityDescriptor(
                    new CapabilityId(Id(seed * 10 + 9)),
                    kind,
                    Array.Empty<VideoFormat>())
            },
            resources);
    }

    private static Identity Id(int value) =>
        Identity.Parse($"00000000-0000-0000-0000-{value:000000000000}");

    private sealed class StaticProviderRegistry : IProviderCapabilityRegistry
    {
        private readonly IReadOnlyList<ProviderDescriptor> _providers;

        public StaticProviderRegistry(params ProviderDescriptor[] providers)
        {
            _providers = Array.AsReadOnly(providers.ToArray());
        }

        public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
    }
}
