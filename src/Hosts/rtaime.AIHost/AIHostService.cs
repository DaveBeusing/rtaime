using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Provider.Inference;

namespace rtaime.AIHost;

/// <summary>
/// AIHost composition boundary. It owns governed inference execution only;
/// it has no Control or Runtime dependency and therefore no production authority.
/// </summary>
public sealed class AIHostService
{
    public AIHostService(GovernedInferenceRuntime runtime)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public GovernedInferenceRuntime Runtime { get; }

    public IReadOnlyList<InferenceCapabilityDescriptor> Capabilities => Runtime.Capabilities;

    public AIExecutionSnapshot Snapshot => Runtime.Snapshot;

    public ValueTask<GovernedInferenceExecutionResult> ExecuteAsync(
        GovernedInferenceExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        Runtime.ExecuteAsync(request, cancellationToken);

    public static AIHostService CreateManagedReference(InferenceRuntimeLimits? limits = null) =>
        new(new GovernedInferenceRuntime(
            new IInferenceProvider[] { new ManagedReferencePersonSegmentationProvider() },
            limits ?? InferenceRuntimeLimits.ReferenceV1));
}
