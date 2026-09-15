using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.AI;

public interface IAIClock
{
    UtcTimestamp GetUtcNow();
}

public sealed class SystemAIClock : IAIClock
{
    public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UtcNow);
}

public sealed record InferenceRuntimeLimits
{
    public InferenceRuntimeLimits(uint computeUnits, ulong vramBytes, uint maxConcurrentRequests)
    {
        if (computeUnits == 0) throw new ArgumentOutOfRangeException(nameof(computeUnits));
        if (vramBytes == 0) throw new ArgumentOutOfRangeException(nameof(vramBytes));
        if (maxConcurrentRequests == 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));

        ComputeUnits = computeUnits;
        VramBytes = vramBytes;
        MaxConcurrentRequests = maxConcurrentRequests;
    }

    public uint ComputeUnits { get; }
    public ulong VramBytes { get; }
    public uint MaxConcurrentRequests { get; }

    public static InferenceRuntimeLimits ReferenceV1 => new(100, 512UL * 1024 * 1024, 2);
}

public sealed record AIExecutionObservation(
    InferenceRequestId RequestId,
    UtcTimestamp ObservedAt,
    string Code,
    InferenceExecutionStatus Status,
    Failure? Failure);

public sealed record AIExecutionSnapshot(
    AIAvailabilityState State,
    uint ActiveRequests,
    uint ReservedComputeUnits,
    ulong ReservedVramBytes,
    ulong Completed,
    ulong Rejected,
    ulong TimedOut,
    ulong Cancelled,
    ulong Failed);

public enum AIResultUseStatus
{
    Use = 1,
    Fallback = 2
}

public sealed record AIResultUseDecision(AIResultUseStatus Status, Failure? Failure)
{
    public bool Usable => Status == AIResultUseStatus.Use;
}

public static class AIResultUsePolicy
{
    public static AIResultUseDecision Evaluate(
        GovernedInferenceExecutionResult executionResult,
        SurfaceId expectedSourceFrameId,
        Duration maximumFreshness,
        double minimumConfidence)
    {
        ArgumentNullException.ThrowIfNull(executionResult);
        if (maximumFreshness.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(maximumFreshness));
        if (minimumConfidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minimumConfidence));

        if (executionResult.Result.Status != InferenceExecutionStatus.Succeeded || executionResult.Metadata is null)
        {
            return new AIResultUseDecision(
                AIResultUseStatus.Fallback,
                executionResult.Result.Failure ?? new Failure("ai.result.unavailable", "Inference result is not usable."));
        }

        var metadata = executionResult.Metadata;
        if (metadata.SourceFrameId != expectedSourceFrameId)
        {
            return new AIResultUseDecision(
                AIResultUseStatus.Fallback,
                new Failure("ai.result.source_mismatch", "Inference result does not belong to the expected source frame."));
        }

        if (metadata.Freshness.CompareTo(maximumFreshness) > 0)
        {
            return new AIResultUseDecision(
                AIResultUseStatus.Fallback,
                new Failure("ai.result.stale", "Inference result exceeded the configured freshness budget."));
        }

        if (metadata.Confidence < minimumConfidence)
        {
            return new AIResultUseDecision(
                AIResultUseStatus.Fallback,
                new Failure("ai.result.confidence_low", "Inference result confidence is below the configured threshold."));
        }

        return new AIResultUseDecision(AIResultUseStatus.Use, null);
    }
}

public sealed class GovernedInferenceRuntime
{
    private readonly object _gate = new();
    private readonly IInferenceProvider[] _providers;
    private readonly InferenceResourceGovernor _governor;
    private readonly IAIClock _clock;
    private readonly List<AIExecutionObservation> _observations = new();
    private bool _executionDegraded;
    private ulong _completed;
    private ulong _rejected;
    private ulong _timedOut;
    private ulong _cancelled;
    private ulong _failed;

    public GovernedInferenceRuntime(
        IEnumerable<IInferenceProvider> providers,
        InferenceRuntimeLimits limits,
        IAIClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers
            .Select(provider => provider ?? throw new ArgumentException("Inference providers must not contain null values.", nameof(providers)))
            .OrderBy(provider => provider.Descriptor.ProviderId.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (_providers.Select(provider => provider.Descriptor.ProviderId).Distinct().Count() != _providers.Length)
            throw new ArgumentException("Inference provider identities must be unique.", nameof(providers));

        _governor = new InferenceResourceGovernor(limits ?? throw new ArgumentNullException(nameof(limits)));
        _clock = clock ?? new SystemAIClock();
    }

    public IReadOnlyList<InferenceProviderDescriptor> Providers =>
        Array.AsReadOnly(_providers.Select(provider => provider.Descriptor).ToArray());

    public IReadOnlyList<InferenceCapabilityDescriptor> Capabilities =>
        Array.AsReadOnly(
            _providers
                .Where(provider => provider.Descriptor.State != InferenceProviderState.Unavailable)
                .SelectMany(provider => provider.Descriptor.Capabilities)
                .GroupBy(capability => capability.CapabilityId)
                .Select(group => group.First())
                .OrderBy(capability => capability.CapabilityId.ToString(), StringComparer.Ordinal)
                .ToArray());

    public IReadOnlyList<AIExecutionObservation> Observations
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<AIExecutionObservation>(_observations.ToArray());
        }
    }

    public AIExecutionSnapshot Snapshot
    {
        get
        {
            var resources = _governor.Snapshot;
            lock (_gate)
            {
                return new AIExecutionSnapshot(
                    DetermineState(),
                    resources.ActiveRequests,
                    resources.ComputeUnits,
                    resources.VramBytes,
                    _completed,
                    _rejected,
                    _timedOut,
                    _cancelled,
                    _failed);
            }
        }
    }

    public async ValueTask<GovernedInferenceExecutionResult> ExecuteAsync(
        GovernedInferenceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.GetUtcNow();
        if (request.Request.Deadline.CompareTo(now) <= 0)
        {
            return RejectBeforeExecution(
                request,
                InferenceExecutionStatus.TimedOut,
                "ai.inference.deadline_expired",
                "Inference request deadline has already expired.");
        }

        var provider = SelectProvider(request.Request.CapabilityId);
        if (provider is null)
        {
            return RejectBeforeExecution(
                request,
                InferenceExecutionStatus.Unavailable,
                "ai.inference.capability_unavailable",
                "No ready or degraded inference provider can satisfy the requested capability.");
        }

        if (!provider.Descriptor.Capabilities.Any(capability => capability.CapabilityId == request.Request.CapabilityId))
        {
            return RejectBeforeExecution(
                request,
                InferenceExecutionStatus.Unavailable,
                "ai.inference.capability_not_advertised",
                "The selected provider does not advertise the requested inference capability.");
        }

        if (!_governor.TryAcquire(request.Context.ResourceBudget, out var lease))
        {
            return RejectBeforeExecution(
                request,
                InferenceExecutionStatus.Unavailable,
                "ai.inference.resource_budget_unavailable",
                "Inference resource budget cannot be admitted without exceeding the AI runtime budget.");
        }

        var admission = new InferenceAdmissionDecision(
            request.Request.RequestId,
            InferenceAdmissionStatus.Admitted,
            provider.Descriptor.ProviderId,
            null);
        Observe(request.Request.RequestId, "ai.inference.admitted", InferenceExecutionStatus.Succeeded, null);

        using (lease)
        {
            var remaining = request.Request.Deadline.Value - _clock.GetUtcNow().Value;
            if (remaining <= TimeSpan.Zero)
            {
                return CompleteFailure(
                    request,
                    admission,
                    InferenceExecutionStatus.TimedOut,
                    new Failure("ai.inference.deadline_expired", "Inference deadline expired before provider execution began."));
            }

            using var timeout = new CancellationTokenSource(remaining);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            InferenceProviderExecutionResult providerResult;
            try
            {
                providerResult = await provider.ExecuteAsync(request, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CompleteFailure(
                    request,
                    admission,
                    InferenceExecutionStatus.Cancelled,
                    new Failure("ai.inference.cancelled", "Inference request was cancelled by the caller."));
            }
            catch (OperationCanceledException)
            {
                return CompleteFailure(
                    request,
                    admission,
                    InferenceExecutionStatus.TimedOut,
                    new Failure("ai.inference.timeout", "Inference execution exceeded its deadline."));
            }
            catch (Exception exception)
            {
                MarkExecutionDegraded();
                return CompleteFailure(
                    request,
                    admission,
                    InferenceExecutionStatus.Failed,
                    new Failure("ai.inference.provider_exception", $"Inference provider failed unexpectedly: {exception.GetType().Name}."));
            }

            if (providerResult.Status == InferenceExecutionStatus.Cancelled && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return CompleteFailure(
                    request,
                    admission,
                    InferenceExecutionStatus.TimedOut,
                    new Failure("ai.inference.timeout", "Inference execution exceeded its deadline."));
            }

            if (providerResult.Status != InferenceExecutionStatus.Succeeded)
            {
                if (providerResult.Status is InferenceExecutionStatus.Failed or InferenceExecutionStatus.Unavailable)
                    MarkExecutionDegraded();

                return CompleteFailure(
                    request,
                    admission,
                    providerResult.Status,
                    providerResult.Failure ?? new Failure("ai.inference.provider_failed", "Inference provider returned a non-success result."));
            }

            var inputFrame = request.Request.InputFrame!;
            var observedAt = _clock.GetUtcNow();
            var metadata = new InferenceResultMetadata(
                new InferenceResultId(CreateDeterministicIdentity(
                    "ai-result",
                    request.Request.RequestId.ToString(),
                    provider.Descriptor.ProviderId.ToString(),
                    providerResult.ModelId!.Value.ToString(),
                    inputFrame.Surface.SurfaceId.ToString())),
                request.Request.CapabilityId,
                inputFrame.Surface.SurfaceId,
                providerResult.ModelId.Value,
                providerResult.ModelVersion!,
                provider.Descriptor.ProviderId,
                observedAt,
                request.Context.ProductionTime,
                providerResult.Freshness,
                providerResult.Confidence,
                providerResult.Uncertainty,
                providerResult.PayloadDescriptor!,
                providerResult.ResourceHandle!);

            var result = new GovernedInferenceResult(
                AIContractVersion.Current,
                request.Request.RequestId,
                InferenceExecutionStatus.Succeeded,
                providerResult.Outputs,
                null);

            lock (_gate)
                _completed++;
            Observe(request.Request.RequestId, "ai.inference.completed", InferenceExecutionStatus.Succeeded, null);

            return new GovernedInferenceExecutionResult(
                AIContractVersion.Current,
                admission,
                result,
                metadata);
        }
    }

    private IInferenceProvider? SelectProvider(InferenceCapabilityId capabilityId) =>
        _providers
            .Where(provider => provider.Descriptor.Capabilities.Any(capability => capability.CapabilityId == capabilityId))
            .Where(provider => provider.Descriptor.State != InferenceProviderState.Unavailable)
            .OrderBy(provider => provider.Descriptor.State == InferenceProviderState.Ready ? 0 : 1)
            .ThenBy(provider => provider.Descriptor.ProviderId.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();

    private GovernedInferenceExecutionResult RejectBeforeExecution(
        GovernedInferenceExecutionRequest request,
        InferenceExecutionStatus status,
        string code,
        string message)
    {
        var failure = new Failure(code, message);
        var admission = new InferenceAdmissionDecision(
            request.Request.RequestId,
            InferenceAdmissionStatus.Rejected,
            null,
            failure);
        var result = new GovernedInferenceResult(
            AIContractVersion.Current,
            request.Request.RequestId,
            status,
            Array.Empty<InferenceOutput>(),
            failure);

        lock (_gate)
        {
            _rejected++;
            if (status == InferenceExecutionStatus.TimedOut)
                _timedOut++;
        }
        Observe(request.Request.RequestId, "ai.inference.admission_rejected", status, failure);
        return new GovernedInferenceExecutionResult(AIContractVersion.Current, admission, result, null);
    }

    private GovernedInferenceExecutionResult CompleteFailure(
        GovernedInferenceExecutionRequest request,
        InferenceAdmissionDecision admission,
        InferenceExecutionStatus status,
        Failure failure)
    {
        lock (_gate)
        {
            switch (status)
            {
                case InferenceExecutionStatus.TimedOut:
                    _timedOut++;
                    break;
                case InferenceExecutionStatus.Cancelled:
                    _cancelled++;
                    break;
                default:
                    _failed++;
                    break;
            }
        }

        Observe(request.Request.RequestId, $"ai.inference.{status.ToString().ToLowerInvariant()}", status, failure);
        var result = new GovernedInferenceResult(
            AIContractVersion.Current,
            request.Request.RequestId,
            status,
            Array.Empty<InferenceOutput>(),
            failure);
        return new GovernedInferenceExecutionResult(AIContractVersion.Current, admission, result, null);
    }

    private AIAvailabilityState DetermineState()
    {
        if (_providers.Length == 0 || _providers.All(provider => provider.Descriptor.State == InferenceProviderState.Unavailable))
            return AIAvailabilityState.Unavailable;
        if (_executionDegraded || _providers.Any(provider => provider.Descriptor.State == InferenceProviderState.Degraded))
            return AIAvailabilityState.Degraded;
        return AIAvailabilityState.Ready;
    }

    private void MarkExecutionDegraded()
    {
        lock (_gate)
            _executionDegraded = true;
    }

    private void Observe(
        InferenceRequestId requestId,
        string code,
        InferenceExecutionStatus status,
        Failure? failure)
    {
        lock (_gate)
        {
            _observations.Add(new AIExecutionObservation(
                requestId,
                _clock.GetUtcNow(),
                code,
                status,
                failure));
        }
    }

    private static Identity CreateDeterministicIdentity(string scope, params string[] parts)
    {
        var canonical = string.Join("\n", new[] { scope }.Concat(parts));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Identity(new Guid(guidBytes));
    }
}

internal sealed class InferenceResourceGovernor
{
    private readonly object _gate = new();
    private readonly InferenceRuntimeLimits _limits;
    private uint _activeRequests;
    private uint _computeUnits;
    private ulong _vramBytes;

    public InferenceResourceGovernor(InferenceRuntimeLimits limits)
    {
        _limits = limits;
    }

    public InferenceResourceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new InferenceResourceSnapshot(_activeRequests, _computeUnits, _vramBytes);
        }
    }

    public bool TryAcquire(InferenceResourceBudget budget, out IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(budget);
        lock (_gate)
        {
            if (_activeRequests >= _limits.MaxConcurrentRequests ||
                budget.ComputeUnits > _limits.ComputeUnits - _computeUnits ||
                budget.VramBytes > _limits.VramBytes - _vramBytes)
            {
                lease = NullLease.Instance;
                return false;
            }

            _activeRequests++;
            _computeUnits += budget.ComputeUnits;
            _vramBytes += budget.VramBytes;
            lease = new ResourceLease(this, budget);
            return true;
        }
    }

    private void Release(InferenceResourceBudget budget)
    {
        lock (_gate)
        {
            if (_activeRequests == 0 || _computeUnits < budget.ComputeUnits || _vramBytes < budget.VramBytes)
                throw new InvalidOperationException("Inference resource accounting underflow.");

            _activeRequests--;
            _computeUnits -= budget.ComputeUnits;
            _vramBytes -= budget.VramBytes;
        }
    }

    private sealed class ResourceLease : IDisposable
    {
        private InferenceResourceGovernor? _owner;
        private readonly InferenceResourceBudget _budget;

        public ResourceLease(InferenceResourceGovernor owner, InferenceResourceBudget budget)
        {
            _owner = owner;
            _budget = budget;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_budget);
        }
    }

    private sealed class NullLease : IDisposable
    {
        public static readonly NullLease Instance = new();
        public void Dispose() { }
    }
}

internal readonly record struct InferenceResourceSnapshot(
    uint ActiveRequests,
    uint ComputeUnits,
    ulong VramBytes);
