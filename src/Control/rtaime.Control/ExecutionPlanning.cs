using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.Control;

public enum LogicalProductionNodeKind
{
    SourceEndpoint = 1,
    PreviewRoute = 2,
    ProgramRoute = 3,
    PreviewSink = 4,
    ProgramSink = 5,
    AuxRoute = 6,
    AuxSink = 7
}

public static class PlanningCapabilityKinds
{
    public const string MediaRoute = "media.route";
}

public sealed record LogicalProductionNode
{
    public LogicalProductionNode(
        Identity nodeId,
        LogicalProductionNodeKind kind,
        string name,
        ProductionSourceId? productionSourceId,
        MediaSourceId? mediaSourceId,
        MediaSinkId? mediaSinkId)
    {
        if (nodeId.IsEmpty)
            throw new ArgumentException("Logical node identity must not be empty.", nameof(nodeId));
        if (!Enum.IsDefined(typeof(LogicalProductionNodeKind), kind))
            throw new ArgumentOutOfRangeException(nameof(kind), "Logical node kind must be a defined value.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Logical node name is required.", nameof(name));

        NodeId = nodeId;
        Kind = kind;
        Name = name.Trim();
        ProductionSourceId = productionSourceId;
        MediaSourceId = mediaSourceId;
        MediaSinkId = mediaSinkId;
    }

    public Identity NodeId { get; }
    public LogicalProductionNodeKind Kind { get; }
    public string Name { get; }
    public ProductionSourceId? ProductionSourceId { get; }
    public MediaSourceId? MediaSourceId { get; }
    public MediaSinkId? MediaSinkId { get; }
}

public sealed record LogicalProductionEdge
{
    public LogicalProductionEdge(Identity edgeId, Identity fromNodeId, Identity toNodeId, string role)
    {
        if (edgeId.IsEmpty)
            throw new ArgumentException("Logical edge identity must not be empty.", nameof(edgeId));
        if (fromNodeId.IsEmpty)
            throw new ArgumentException("Logical edge source identity must not be empty.", nameof(fromNodeId));
        if (toNodeId.IsEmpty)
            throw new ArgumentException("Logical edge target identity must not be empty.", nameof(toNodeId));
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("Logical edge role is required.", nameof(role));

        EdgeId = edgeId;
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        Role = role.Trim();
    }

    public Identity EdgeId { get; }
    public Identity FromNodeId { get; }
    public Identity ToNodeId { get; }
    public string Role { get; }
}

public sealed class LogicalProductionGraph
{
    private readonly ReadOnlyCollection<LogicalProductionNode> _nodes;
    private readonly ReadOnlyCollection<LogicalProductionEdge> _edges;

    public LogicalProductionGraph(
        ProductionId productionId,
        Revision authoritativeRevision,
        IReadOnlyList<LogicalProductionNode> nodes,
        IReadOnlyList<LogicalProductionEdge> edges)
    {
        if (nodes is null)
            throw new ArgumentNullException(nameof(nodes));
        if (edges is null)
            throw new ArgumentNullException(nameof(edges));
        if (nodes.Any(node => node is null))
            throw new ArgumentException("Logical graph nodes must not contain null values.", nameof(nodes));
        if (edges.Any(edge => edge is null))
            throw new ArgumentException("Logical graph edges must not contain null values.", nameof(edges));

        ProductionId = productionId;
        AuthoritativeRevision = authoritativeRevision;
        _nodes = Array.AsReadOnly(nodes.ToArray());
        _edges = Array.AsReadOnly(edges.ToArray());
    }

    public ProductionId ProductionId { get; }
    public Revision AuthoritativeRevision { get; }
    public IReadOnlyList<LogicalProductionNode> Nodes => _nodes;
    public IReadOnlyList<LogicalProductionEdge> Edges => _edges;
}

public static class LogicalProductionGraphValidator
{
    public static ControlValidationReport Validate(LogicalProductionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var issues = new List<ValidationIssue>();
        var nodeIds = new HashSet<Identity>();
        var edgeIds = new HashSet<Identity>();
        var sourceIds = new HashSet<ProductionSourceId>();

        foreach (var node in graph.Nodes)
        {
            if (!nodeIds.Add(node.NodeId))
            {
                issues.Add(new ValidationIssue(
                    "planning.graph.node_identity_duplicate",
                    "Logical graph node identities must be unique.",
                    "graph.nodes"));
            }

            switch (node.Kind)
            {
                case LogicalProductionNodeKind.SourceEndpoint:
                    if (node.ProductionSourceId is null || node.MediaSourceId is null || node.MediaSinkId is not null)
                    {
                        issues.Add(new ValidationIssue(
                            "planning.graph.source_endpoint_invalid",
                            "Source endpoint nodes require a production source and media source, and must not expose a media sink.",
                            "graph.nodes"));
                    }
                    else if (!sourceIds.Add(node.ProductionSourceId.Value))
                    {
                        issues.Add(new ValidationIssue(
                            "planning.graph.source_identity_duplicate",
                            "Production source endpoints must be unique.",
                            "graph.nodes"));
                    }
                    break;

                case LogicalProductionNodeKind.PreviewRoute:
                case LogicalProductionNodeKind.ProgramRoute:
                case LogicalProductionNodeKind.AuxRoute:
                    if (node.MediaSourceId is null || node.MediaSinkId is null)
                    {
                        issues.Add(new ValidationIssue(
                            "planning.graph.route_endpoint_missing",
                            "Route nodes require both media source and media sink identities.",
                            "graph.nodes"));
                    }
                    break;

                case LogicalProductionNodeKind.PreviewSink:
                case LogicalProductionNodeKind.ProgramSink:
                case LogicalProductionNodeKind.AuxSink:
                    if (node.MediaSinkId is null || node.MediaSourceId is not null)
                    {
                        issues.Add(new ValidationIssue(
                            "planning.graph.sink_endpoint_invalid",
                            "Sink endpoint nodes require a media sink and must not expose a media source.",
                            "graph.nodes"));
                    }
                    break;
            }
        }

        foreach (var edge in graph.Edges)
        {
            if (!edgeIds.Add(edge.EdgeId))
            {
                issues.Add(new ValidationIssue(
                    "planning.graph.edge_identity_duplicate",
                    "Logical graph edge identities must be unique.",
                    "graph.edges"));
            }

            if (!nodeIds.Contains(edge.FromNodeId) || !nodeIds.Contains(edge.ToNodeId))
            {
                issues.Add(new ValidationIssue(
                    "planning.graph.edge_endpoint_unknown",
                    "Logical graph edges must reference declared nodes.",
                    "graph.edges"));
            }

            if (edge.FromNodeId == edge.ToNodeId)
            {
                issues.Add(new ValidationIssue(
                    "planning.graph.self_edge",
                    "Logical graph self-edges are not valid in the V1 planning graph.",
                    "graph.edges"));
            }
        }

        RequireExactlyOne(graph, LogicalProductionNodeKind.PreviewRoute, issues);
        RequireExactlyOne(graph, LogicalProductionNodeKind.ProgramRoute, issues);
        RequireExactlyOne(graph, LogicalProductionNodeKind.PreviewSink, issues);
        RequireExactlyOne(graph, LogicalProductionNodeKind.ProgramSink, issues);
        RequireOptionalPair(graph, LogicalProductionNodeKind.AuxRoute, LogicalProductionNodeKind.AuxSink, issues);

        return new ControlValidationReport(issues);
    }

    private static void RequireOptionalPair(
        LogicalProductionGraph graph,
        LogicalProductionNodeKind routeKind,
        LogicalProductionNodeKind sinkKind,
        ICollection<ValidationIssue> issues)
    {
        var routeCount = graph.Nodes.Count(node => node.Kind == routeKind);
        var sinkCount = graph.Nodes.Count(node => node.Kind == sinkKind);
        if (routeCount > 1 || sinkCount > 1 || routeCount != sinkCount)
        {
            issues.Add(new ValidationIssue(
                "planning.graph.optional_output_cardinality",
                $"Optional output role nodes '{routeKind}' and '{sinkKind}' must be absent or appear exactly once as a pair.",
                "graph.nodes"));
        }
    }

    private static void RequireExactlyOne(
        LogicalProductionGraph graph,
        LogicalProductionNodeKind kind,
        ICollection<ValidationIssue> issues)
    {
        if (graph.Nodes.Count(node => node.Kind == kind) != 1)
        {
            issues.Add(new ValidationIssue(
                "planning.graph.required_node_cardinality",
                $"Logical graph requires exactly one '{kind}' node.",
                "graph.nodes"));
        }
    }
}

public sealed record LogicalCapabilityRequirement
{
    public LogicalCapabilityRequirement(
        Identity logicalNodeId,
        CapabilityRequirement requirement,
        MediaSourceId? mediaSourceId,
        MediaSinkId? mediaSinkId,
        string? outputRoleId = null)
    {
        if (logicalNodeId.IsEmpty)
            throw new ArgumentException("Logical node identity must not be empty.", nameof(logicalNodeId));

        LogicalNodeId = logicalNodeId;
        Requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        MediaSourceId = mediaSourceId;
        MediaSinkId = mediaSinkId;
        OutputRoleId = string.IsNullOrWhiteSpace(outputRoleId) ? null : outputRoleId.Trim().ToLowerInvariant();
    }

    public Identity LogicalNodeId { get; }
    public CapabilityRequirement Requirement { get; }
    public MediaSourceId? MediaSourceId { get; }
    public MediaSinkId? MediaSinkId { get; }
    public string? OutputRoleId { get; }
}

public interface IProviderCapabilityRegistry
{
    IReadOnlyList<ProviderDescriptor> GetProviders();
}

public static class CapabilityRequirementMatcher
{
    public static bool Matches(CapabilityRequirement requirement, ProviderCapabilityDescriptor capability)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(capability);

        if (!string.Equals(requirement.Kind, capability.Kind, StringComparison.Ordinal))
            return false;

        if (requirement.AcceptedVideoFormats.Count == 0)
            return true;

        if (capability.VideoFormats.Count == 0)
            return false;

        return requirement.AcceptedVideoFormats.Any(required => capability.VideoFormats.Contains(required));
    }
}

public sealed record ResourceAdmissionBinding
{
    public ResourceAdmissionBinding(
        LogicalCapabilityRequirement logicalRequirement,
        ProviderId providerId,
        CapabilityId capabilityId,
        ProviderResourceDescriptor resource)
    {
        LogicalRequirement = logicalRequirement ?? throw new ArgumentNullException(nameof(logicalRequirement));
        ProviderId = providerId;
        CapabilityId = capabilityId;
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
    }

    public LogicalCapabilityRequirement LogicalRequirement { get; }
    public ProviderId ProviderId { get; }
    public CapabilityId CapabilityId { get; }
    public ProviderResourceDescriptor Resource { get; }
}

public sealed class ResourceAdmissionResult
{
    private readonly ReadOnlyCollection<ResourceAdmissionBinding> _bindings;

    private ResourceAdmissionResult(ControlValidationReport validation, IReadOnlyList<ResourceAdmissionBinding> bindings)
    {
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _bindings = Array.AsReadOnly(bindings?.ToArray() ?? throw new ArgumentNullException(nameof(bindings)));

        if (validation.IsValid != (_bindings.Count > 0))
            throw new ArgumentException("Successful resource admission requires bindings and rejected admission must not expose bindings.");
    }

    public ControlValidationReport Validation { get; }
    public IReadOnlyList<ResourceAdmissionBinding> Bindings => _bindings;
    public bool Succeeded => Validation.IsValid;

    internal static ResourceAdmissionResult Accepted(IReadOnlyList<ResourceAdmissionBinding> bindings)
    {
        if (bindings is null)
            throw new ArgumentNullException(nameof(bindings));
        if (bindings.Count == 0)
            throw new ArgumentException("Successful resource admission requires at least one binding.", nameof(bindings));

        return new ResourceAdmissionResult(ControlValidationReport.Valid, bindings);
    }

    internal static ResourceAdmissionResult Rejected(ValidationIssue issue) =>
        new(ControlValidationReport.Invalid(issue ?? throw new ArgumentNullException(nameof(issue))), Array.Empty<ResourceAdmissionBinding>());
}

public sealed record ExecutionPlanBinding
{
    public ExecutionPlanBinding(
        Identity logicalNodeId,
        CapabilityId capabilityId,
        ProviderResourceDescriptor resource,
        MediaSourceId? mediaSourceId,
        MediaSinkId? mediaSinkId,
        string? outputRoleId = null)
    {
        if (logicalNodeId.IsEmpty)
            throw new ArgumentException("Logical node identity must not be empty.", nameof(logicalNodeId));

        LogicalNodeId = logicalNodeId;
        CapabilityId = capabilityId;
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        MediaSourceId = mediaSourceId;
        MediaSinkId = mediaSinkId;
        OutputRoleId = string.IsNullOrWhiteSpace(outputRoleId) ? null : outputRoleId.Trim().ToLowerInvariant();
    }

    public Identity LogicalNodeId { get; }
    public CapabilityId CapabilityId { get; }
    public ProviderResourceDescriptor Resource { get; }
    public MediaSourceId? MediaSourceId { get; }
    public MediaSinkId? MediaSinkId { get; }
    public string? OutputRoleId { get; }
}

public sealed class ExecutionPlan
{
    private readonly ReadOnlyCollection<ExecutionPlanBinding> _bindings;

    public ExecutionPlan(
        AuthoritySnapshotReference authoritySnapshot,
        Generation planGeneration,
        IReadOnlyList<ExecutionPlanBinding> bindings)
    {
        AuthoritySnapshot = authoritySnapshot ?? throw new ArgumentNullException(nameof(authoritySnapshot));
        if (bindings is null)
            throw new ArgumentNullException(nameof(bindings));
        if (bindings.Count == 0)
            throw new ArgumentException("Execution plan requires at least one binding.", nameof(bindings));
        if (bindings.Any(binding => binding is null))
            throw new ArgumentException("Execution plan bindings must not contain null values.", nameof(bindings));

        PlanGeneration = planGeneration;
        _bindings = Array.AsReadOnly(bindings
            .OrderBy(binding => binding.LogicalNodeId.ToString(), StringComparer.Ordinal)
            .ToArray());
    }

    public AuthoritySnapshotReference AuthoritySnapshot { get; }
    public Generation PlanGeneration { get; }
    public IReadOnlyList<ExecutionPlanBinding> Bindings => _bindings;
}

public sealed class ExecutionPlanningResult
{
    private ExecutionPlanningResult(
        ControlValidationReport validation,
        LogicalProductionGraph? graph,
        ResourceAdmissionResult? admission,
        ExecutionPlan? plan,
        PreparedExecutionContract? preparedExecution)
    {
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        Graph = graph;
        Admission = admission;
        Plan = plan;
        PreparedExecution = preparedExecution;

        if (validation.IsValid != (graph is not null && admission?.Succeeded == true && plan is not null && preparedExecution is not null))
            throw new ArgumentException("Successful planning requires graph, admission, plan, and prepared execution; rejected planning must not expose a prepared execution.");
    }

    public ControlValidationReport Validation { get; }
    public LogicalProductionGraph? Graph { get; }
    public ResourceAdmissionResult? Admission { get; }
    public ExecutionPlan? Plan { get; }
    public PreparedExecutionContract? PreparedExecution { get; }
    public bool Succeeded => PreparedExecution is not null;

    internal static ExecutionPlanningResult Accepted(
        LogicalProductionGraph graph,
        ResourceAdmissionResult admission,
        ExecutionPlan plan,
        PreparedExecutionContract preparedExecution) =>
        new(ControlValidationReport.Valid, graph, admission, plan, preparedExecution);

    internal static ExecutionPlanningResult Rejected(
        ControlValidationReport validation,
        LogicalProductionGraph? graph = null,
        ResourceAdmissionResult? admission = null)
    {
        if (validation is null)
            throw new ArgumentNullException(nameof(validation));
        if (validation.IsValid)
            throw new ArgumentException("Rejected planning requires validation issues.", nameof(validation));

        return new ExecutionPlanningResult(validation, graph, admission, null, null);
    }
}

public static class CapabilityPlanningEngine
{
    public static ExecutionPlanningResult Plan(
        ProductionSpecification specification,
        AuthoritativeProductionState authoritativeState,
        IProviderCapabilityRegistry providerRegistry)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(authoritativeState);
        ArgumentNullException.ThrowIfNull(providerRegistry);

        var stateValidation = ValidateAuthoritativeInput(specification, authoritativeState);
        if (!stateValidation.IsValid)
            return ExecutionPlanningResult.Rejected(stateValidation);

        var graph = CompileGraph(specification, authoritativeState);
        var graphValidation = LogicalProductionGraphValidator.Validate(graph);
        if (!graphValidation.IsValid)
            return ExecutionPlanningResult.Rejected(graphValidation, graph);

        var logicalRequirements = CompileRequirements(graph);
        var providers = providerRegistry.GetProviders();
        var registryValidation = ValidateProviderSnapshot(providers);
        if (!registryValidation.IsValid)
            return ExecutionPlanningResult.Rejected(registryValidation, graph);

        var admission = Admit(logicalRequirements, providers);
        if (!admission.Succeeded)
            return ExecutionPlanningResult.Rejected(admission.Validation, graph, admission);

        var authoritySnapshot = new AuthoritySnapshotReference(
            specification.ProductionId.Value,
            authoritativeState.Revision);

        var plan = new ExecutionPlan(
            authoritySnapshot,
            new Generation(authoritativeState.Revision.Value),
            admission.Bindings
                .Select(binding => new ExecutionPlanBinding(
                    binding.LogicalRequirement.LogicalNodeId,
                    binding.CapabilityId,
                    binding.Resource,
                    binding.LogicalRequirement.MediaSourceId,
                    binding.LogicalRequirement.MediaSinkId,
                    binding.LogicalRequirement.OutputRoleId))
                .ToArray());

        var preparedExecution = CreatePreparedExecution(plan);
        return ExecutionPlanningResult.Accepted(graph, admission, plan, preparedExecution);
    }

    private static ControlValidationReport ValidateAuthoritativeInput(
        ProductionSpecification specification,
        AuthoritativeProductionState authoritativeState)
    {
        var issues = new List<ValidationIssue>();
        issues.AddRange(ProductionSpecificationValidator.Validate(specification).Issues);

        if (authoritativeState.Version != specification.Version)
        {
            issues.Add(new ValidationIssue(
                "planning.authority.version_mismatch",
                "Authoritative state version does not match the production specification.",
                "authoritative.version"));
        }

        if (authoritativeState.ProductionId != specification.ProductionId)
        {
            issues.Add(new ValidationIssue(
                "planning.authority.production_mismatch",
                "Authoritative state belongs to a different production.",
                "authoritative.productionId"));
        }

        if (!ContainsSource(specification, authoritativeState.Routing.PreviewSourceId))
        {
            issues.Add(new ValidationIssue(
                "planning.authority.preview_source_unknown",
                "Authoritative preview source is not declared by the production specification.",
                "authoritative.routing.previewSourceId"));
        }

        if (!ContainsSource(specification, authoritativeState.Routing.ProgramSourceId))
        {
            issues.Add(new ValidationIssue(
                "planning.authority.program_source_unknown",
                "Authoritative program source is not declared by the production specification.",
                "authoritative.routing.programSourceId"));
        }

        issues.AddRange(ProductionOutputRoleValidator.Validate(
            specification,
            authoritativeState.OutputRoles,
            authoritativeState.Routing,
            "authoritative.outputRoles").Issues);

        return new ControlValidationReport(issues);
    }

    private static LogicalProductionGraph CompileGraph(
        ProductionSpecification specification,
        AuthoritativeProductionState authoritativeState)
    {
        var productionKey = specification.ProductionId.ToString();
        var sourceNodes = specification.Sources
            .OrderBy(source => source.SourceId.ToString(), StringComparer.Ordinal)
            .Select(source => new LogicalProductionNode(
                PlanningIdentity.Create("logical-node", productionKey, "source", source.SourceId.ToString()),
                LogicalProductionNodeKind.SourceEndpoint,
                source.Name,
                source.SourceId,
                new MediaSourceId(source.SourceId.Value),
                null))
            .ToArray();

        var sourceById = sourceNodes.ToDictionary(node => node.ProductionSourceId!.Value);
        var previewSource = sourceById[authoritativeState.Routing.PreviewSourceId];
        var programSource = sourceById[authoritativeState.Routing.ProgramSourceId];

        var previewSinkId = new MediaSinkId(PlanningIdentity.Create("media-sink", productionKey, "preview"));
        var programSinkId = new MediaSinkId(PlanningIdentity.Create("media-sink", productionKey, "program"));

        var previewRoute = new LogicalProductionNode(
            PlanningIdentity.Create("logical-node", productionKey, "preview-route"),
            LogicalProductionNodeKind.PreviewRoute,
            "Preview Route",
            null,
            previewSource.MediaSourceId,
            previewSinkId);

        var programRoute = new LogicalProductionNode(
            PlanningIdentity.Create("logical-node", productionKey, "program-route"),
            LogicalProductionNodeKind.ProgramRoute,
            "Program Route",
            null,
            programSource.MediaSourceId,
            programSinkId);

        var previewSink = new LogicalProductionNode(
            PlanningIdentity.Create("logical-node", productionKey, "preview-sink"),
            LogicalProductionNodeKind.PreviewSink,
            "Preview Sink",
            null,
            null,
            previewSinkId);

        var programSink = new LogicalProductionNode(
            PlanningIdentity.Create("logical-node", productionKey, "program-sink"),
            LogicalProductionNodeKind.ProgramSink,
            "Program Sink",
            null,
            null,
            programSinkId);

        var roleNodes = new List<LogicalProductionNode> { previewRoute, programRoute, previewSink, programSink };
        var edges = new List<LogicalProductionEdge>
        {
            CreateEdge(productionKey, previewSource.NodeId, previewRoute.NodeId, "preview-source-to-route"),
            CreateEdge(productionKey, previewRoute.NodeId, previewSink.NodeId, "preview-route-to-sink"),
            CreateEdge(productionKey, programSource.NodeId, programRoute.NodeId, "program-source-to-route"),
            CreateEdge(productionKey, programRoute.NodeId, programSink.NodeId, "program-route-to-sink")
        };

        var auxRole = authoritativeState.OutputRoles.SingleOrDefault(role => role.Kind == OutputRoleKind.Aux && role.Enabled);
        if (auxRole is not null)
        {
            var auxSource = sourceById[auxRole.SourceId];
            var auxSinkId = new MediaSinkId(PlanningIdentity.Create("media-sink", productionKey, auxRole.TargetId));
            var auxRoute = new LogicalProductionNode(
                PlanningIdentity.Create("logical-node", productionKey, "aux-route"),
                LogicalProductionNodeKind.AuxRoute,
                "Aux Route",
                null,
                auxSource.MediaSourceId,
                auxSinkId);
            var auxSink = new LogicalProductionNode(
                PlanningIdentity.Create("logical-node", productionKey, "aux-sink"),
                LogicalProductionNodeKind.AuxSink,
                "Aux Sink",
                null,
                null,
                auxSinkId);
            roleNodes.Add(auxRoute);
            roleNodes.Add(auxSink);
            edges.Add(CreateEdge(productionKey, auxSource.NodeId, auxRoute.NodeId, "aux-source-to-route"));
            edges.Add(CreateEdge(productionKey, auxRoute.NodeId, auxSink.NodeId, "aux-route-to-sink"));
        }

        var nodes = sourceNodes.Concat(roleNodes).ToArray();
        return new LogicalProductionGraph(
            specification.ProductionId,
            authoritativeState.Revision,
            nodes,
            edges);
    }

    private static LogicalProductionEdge CreateEdge(
        string productionKey,
        Identity fromNodeId,
        Identity toNodeId,
        string role) =>
        new(
            PlanningIdentity.Create("logical-edge", productionKey, fromNodeId.ToString(), toNodeId.ToString(), role),
            fromNodeId,
            toNodeId,
            role);

    private static IReadOnlyList<LogicalCapabilityRequirement> CompileRequirements(LogicalProductionGraph graph) =>
        graph.Nodes
            .Where(node => node.Kind is LogicalProductionNodeKind.PreviewRoute or LogicalProductionNodeKind.ProgramRoute or LogicalProductionNodeKind.AuxRoute)
            .OrderBy(node => node.NodeId.ToString(), StringComparer.Ordinal)
            .Select(node => new LogicalCapabilityRequirement(
                node.NodeId,
                new CapabilityRequirement(
                    ProviderContractVersion.Current,
                    PlanningIdentity.Create("capability-requirement", node.NodeId.ToString(), PlanningCapabilityKinds.MediaRoute),
                    PlanningCapabilityKinds.MediaRoute,
                    1,
                    Array.Empty<VideoFormat>()),
                node.MediaSourceId,
                node.MediaSinkId,
                node.Kind switch
                {
                    LogicalProductionNodeKind.ProgramRoute => OutputRoleIds.Program.ToString(),
                    LogicalProductionNodeKind.AuxRoute => OutputRoleIds.Aux.ToString(),
                    _ => null
                }))
            .ToArray();

    private static ControlValidationReport ValidateProviderSnapshot(IReadOnlyList<ProviderDescriptor>? providers)
    {
        var issues = new List<ValidationIssue>();
        if (providers is null)
        {
            issues.Add(new ValidationIssue(
                "planning.registry.snapshot_missing",
                "Provider capability registry returned no snapshot.",
                "providerRegistry"));
            return new ControlValidationReport(issues);
        }

        if (providers.Any(provider => provider is null))
        {
            issues.Add(new ValidationIssue(
                "planning.registry.provider_null",
                "Provider capability registry must not contain null descriptors.",
                "providerRegistry.providers"));
            return new ControlValidationReport(issues);
        }

        if (providers.Select(provider => provider.ProviderId).Distinct().Count() != providers.Count)
        {
            issues.Add(new ValidationIssue(
                "planning.registry.provider_identity_duplicate",
                "Provider identities must be unique in one planning snapshot.",
                "providerRegistry.providers"));
        }

        var capabilities = providers.SelectMany(provider => provider.Capabilities).ToArray();
        if (capabilities.Select(capability => capability.CapabilityId).Distinct().Count() != capabilities.Length)
        {
            issues.Add(new ValidationIssue(
                "planning.registry.capability_identity_duplicate",
                "Capability identities must be unique in one planning snapshot.",
                "providerRegistry.providers.capabilities"));
        }

        var resources = providers.SelectMany(provider => provider.Resources).ToArray();
        if (resources.Select(resource => resource.ResourceId).Distinct().Count() != resources.Length)
        {
            issues.Add(new ValidationIssue(
                "planning.registry.resource_identity_duplicate",
                "Provider resource identities must be unique in one planning snapshot.",
                "providerRegistry.providers.resources"));
        }

        return new ControlValidationReport(issues);
    }

    private static ResourceAdmissionResult Admit(
        IReadOnlyList<LogicalCapabilityRequirement> logicalRequirements,
        IReadOnlyList<ProviderDescriptor> providers)
    {
        var bindings = new List<ResourceAdmissionBinding>();
        var usedResources = new HashSet<ProviderResourceId>();
        var eligibleProviders = providers
            .Where(provider => provider.Availability.State == ProviderAvailabilityState.Available)
            .OrderBy(provider => provider.ProviderId.ToString(), StringComparer.Ordinal)
            .ToArray();

        foreach (var logicalRequirement in logicalRequirements
                     .OrderBy(item => item.Requirement.RequirementId.ToString(), StringComparer.Ordinal))
        {
            var requirement = logicalRequirement.Requirement;
            var sameKindCapabilities = eligibleProviders
                .SelectMany(provider => provider.Capabilities.Select(capability => (provider, capability)))
                .Where(candidate => string.Equals(candidate.capability.Kind, requirement.Kind, StringComparison.Ordinal))
                .ToArray();

            if (sameKindCapabilities.Length == 0)
            {
                return ResourceAdmissionResult.Rejected(new ValidationIssue(
                    "planning.capability.missing",
                    $"No available provider advertises required capability kind '{requirement.Kind}'.",
                    "capabilityRequirements"));
            }

            var compatibleCapabilities = sameKindCapabilities
                .Where(candidate => CapabilityRequirementMatcher.Matches(requirement, candidate.capability))
                .ToArray();

            if (compatibleCapabilities.Length == 0)
            {
                return ResourceAdmissionResult.Rejected(new ValidationIssue(
                    "planning.capability.incompatible",
                    $"Available providers advertise capability kind '{requirement.Kind}' but none satisfy the declared requirement.",
                    "capabilityRequirements"));
            }

            var candidates = compatibleCapabilities
                .SelectMany(candidate => candidate.provider.Resources
                    .Where(resource =>
                        resource.Reservable &&
                        !usedResources.Contains(resource.ResourceId) &&
                        resource.CapacityUnits >= requirement.RequiredCapacityUnits &&
                        string.Equals(resource.Kind, requirement.Kind, StringComparison.Ordinal))
                    .Select(resource => (candidate.provider, candidate.capability, resource)))
                .OrderBy(candidate => candidate.provider.ProviderId.ToString(), StringComparer.Ordinal)
                .ThenBy(candidate => candidate.capability.CapabilityId.ToString(), StringComparer.Ordinal)
                .ThenBy(candidate => candidate.resource.ResourceId.ToString(), StringComparer.Ordinal)
                .ToArray();

            if (candidates.Length == 0)
            {
                return ResourceAdmissionResult.Rejected(new ValidationIssue(
                    "planning.resource.exhausted",
                    $"No unused reservable resource can admit capability requirement '{requirement.RequirementId}'.",
                    "providerRegistry.providers.resources"));
            }

            var selected = candidates[0];
            usedResources.Add(selected.resource.ResourceId);
            bindings.Add(new ResourceAdmissionBinding(
                logicalRequirement,
                selected.provider.ProviderId,
                selected.capability.CapabilityId,
                selected.resource));
        }

        return ResourceAdmissionResult.Accepted(bindings);
    }

    private static PreparedExecutionContract CreatePreparedExecution(ExecutionPlan plan)
    {
        var canonicalBindings = plan.Bindings
            .Select(binding => string.Join(
                "|",
                binding.LogicalNodeId,
                binding.CapabilityId,
                binding.Resource.ProviderId,
                binding.Resource.ResourceId,
                binding.MediaSourceId?.ToString() ?? "-",
                binding.MediaSinkId?.ToString() ?? "-",
                binding.OutputRoleId ?? "-"))
            .ToArray();

        var preparedExecutionId = new PreparedExecutionId(PlanningIdentity.Create(
            "prepared-execution",
            plan.AuthoritySnapshot.StateId.ToString(),
            plan.AuthoritySnapshot.Revision.ToString(),
            plan.PlanGeneration.ToString(),
            string.Join(";", canonicalBindings)));

        return new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            preparedExecutionId,
            plan.AuthoritySnapshot,
            plan.PlanGeneration,
            plan.Bindings
                .Select(binding => new PreparedExecutionBinding(
                    binding.LogicalNodeId,
                    binding.CapabilityId,
                    binding.Resource,
                    binding.MediaSourceId,
                    binding.MediaSinkId,
                    binding.OutputRoleId))
                .ToArray());
    }

    private static bool ContainsSource(ProductionSpecification specification, ProductionSourceId sourceId) =>
        specification.Sources.Any(source => source.SourceId == sourceId);
}

internal static class PlanningIdentity
{
    public static Identity Create(string scope, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Planning identity scope is required.", nameof(scope));
        if (parts is null)
            throw new ArgumentNullException(nameof(parts));

        var canonical = string.Join('\u001f', new[] { scope }.Concat(parts));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var guidText = Convert.ToHexString(hash.AsSpan(0, 16));
        return new Identity(Guid.ParseExact(guidText, "N"));
    }
}
