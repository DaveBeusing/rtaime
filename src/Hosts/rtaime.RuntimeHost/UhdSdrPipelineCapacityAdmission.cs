// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Explicit consumers of a candidate UHD SDR Program frame. A consumer absent
/// from the active graph is not an implicit pass or a claimed output capability.
/// </summary>
public enum UhdSdrConsumerKind
{
    Decoder = 1,
    Gpu = 2,
    Preview = 3,
    Program = 4,
    Monitoring = 5,
    Recording = 6,
    Replay = 7,
    Ndi = 8,
    Srt = 9,
    Mxo = 10
}

/// <summary>
/// A snapshot supplied by the owning consumer, not a user-authored format
/// preference. Available and byte/throughput limits are planning assertions,
/// not independently verified hardware qualification.
/// </summary>
public sealed record UhdSdrConsumerCapacity(
    UhdSdrConsumerKind Kind,
    string ProviderIdentity,
    VideoFormat Format,
    bool Available,
    long MaximumFrameBytes,
    long MaximumDirectionalBytesPerSecond,
    int DirectionalTransfersPerFrame);

/// <summary>
/// Unique upper-bound frame allocations, not references to shared leases.
/// Callers must account for decoder owners, both live sources, caches, recorder,
/// replay, monitoring and configured network/output queues in these totals.
/// </summary>
public sealed record UhdSdrBufferCapacity(
    int DecoderFrames,
    int LiveFrames,
    int CacheFrames,
    int GpuFrames,
    int ReadbackFrames,
    int SinkFrames,
    long NativeDecoderBytesPerFrame,
    long ManagedBudgetBytes,
    long GpuBudgetBytes,
    long NativeBudgetBytes);

public sealed record UhdSdrPipelineCapacityRequest(
    VideoFormat Format,
    IReadOnlyList<UhdSdrConsumerKind> ActiveConsumers,
    IReadOnlyList<UhdSdrConsumerCapacity> ConsumerCapacities,
    UhdSdrBufferCapacity Buffers);

/// <summary>
/// CapacityFits is only a side-effect-free preflight result. In particular it
/// is NOT an authorization to change RuntimeHost Program format, an approved
/// physical output grant, a measured real-time deadline or release readiness.
/// </summary>
public sealed record UhdSdrPipelineCapacityDecision(
    bool CapacityFits,
    string Code,
    string Detail,
    long FrameBytes,
    long RequiredManagedBytes,
    long RequiredGpuBytes,
    long RequiredNativeBytes,
    long FrameDeadlineTicks)
{
    public static UhdSdrPipelineCapacityDecision Reject(string code, string detail) =>
        new(false, code, detail, 0, 0, 0, 0, 0);
}

/// <summary>
/// Complete UHD SDR candidate planning with exact consumer tuples and finite
/// upper-bound budgets. The existing HD-only RuntimeHost production gate remains
/// authoritative until real decoder/GPU/record/output/monitoring qualification
/// and transactional reservation are wired through the active providers.
/// </summary>
public static class UhdSdrPipelineCapacityAdmission
{
    private static readonly UhdSdrConsumerKind[] MandatoryConsumers =
    [
        UhdSdrConsumerKind.Decoder,
        UhdSdrConsumerKind.Gpu,
        UhdSdrConsumerKind.Preview,
        UhdSdrConsumerKind.Program,
        UhdSdrConsumerKind.Monitoring
    ];

    public static UhdSdrPipelineCapacityDecision Evaluate(UhdSdrPipelineCapacityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!UhdSdrVideoProfiles.IsExactSdrRgbCandidate(request.Format))
            return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.format_unsupported",
                "Only exact BT.709 SDR 8-bit full-range RGBA 2160p25/50/60000/1001 candidates are modeled; unknown, YCbCr, HDR and altered color are unavailable.");

        if (request.ActiveConsumers is null || request.ConsumerCapacities is null ||
            request.Buffers is null)
            return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.request_invalid",
                "Active consumers, their provider capacities and buffer budgets are required.");

        if (request.ActiveConsumers.Any(kind => !Enum.IsDefined(kind)) ||
            request.ActiveConsumers.Distinct().Count() != request.ActiveConsumers.Count ||
            MandatoryConsumers.Any(kind => !request.ActiveConsumers.Contains(kind)) ||
            request.ConsumerCapacities.Count != request.ActiveConsumers.Count ||
            request.ConsumerCapacities.Any(capacity => capacity is null ||
                !Enum.IsDefined(capacity.Kind) ||
                !request.ActiveConsumers.Contains(capacity.Kind)) ||
            request.ConsumerCapacities.Select(capacity => capacity.Kind).Distinct().Count() !=
                request.ConsumerCapacities.Count)
            return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.consumer_set_invalid",
                "Every mandatory and active optional consumer needs exactly one matching capability; missing, duplicate and unknown consumers fail closed.");

        var buffers = request.Buffers;
        var hasOptionalSink = request.ActiveConsumers.Any(kind =>
            kind is UhdSdrConsumerKind.Recording or UhdSdrConsumerKind.Replay or
                UhdSdrConsumerKind.Ndi or UhdSdrConsumerKind.Srt or UhdSdrConsumerKind.Mxo);

        if (buffers.DecoderFrames < 11 || buffers.LiveFrames < 1 || buffers.CacheFrames < 0 ||
            buffers.GpuFrames < 1 ||
            buffers.ReadbackFrames < RuntimeProgramExecutionService.ProgramReadbackBufferCapacity ||
            buffers.SinkFrames < (hasOptionalSink ? 1 : 0) ||
            buffers.NativeDecoderBytesPerFrame <= 0 ||
            buffers.ManagedBudgetBytes <= 0 || buffers.GpuBudgetBytes <= 0 ||
            buffers.NativeBudgetBytes <= 0)
            return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.buffer_evidence_invalid",
                "Budget all 11 decoder pool/scratch frames per owner, live/cache/GPU frames, the current 67-slot readback capacity, active sink queues and finite native/GPU/managed byte envelopes.");

        try
        {
            var frameBytes = UhdSdrVideoProfiles.RequiredFrameBytes(request.Format);
            var oneDirectionalRate =
                UhdSdrVideoProfiles.RequiredDirectionalBytesPerSecond(request.Format);
            foreach (var capacity in request.ConsumerCapacities)
            {
                if (string.IsNullOrWhiteSpace(capacity.ProviderIdentity) ||
                    !capacity.Available)
                    return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.consumer_unavailable",
                        $"Consumer '{capacity.Kind}' has no available, identified provider.");

                if (capacity.Format != request.Format)
                    return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.consumer_format_mismatch",
                        $"Consumer '{capacity.Kind}' cannot accept the exact resolution, cadence and color tuple.");

                if (capacity.DirectionalTransfersPerFrame <= 0 ||
                    capacity.MaximumFrameBytes < frameBytes ||
                    capacity.MaximumDirectionalBytesPerSecond <
                        checked(oneDirectionalRate * capacity.DirectionalTransfersPerFrame))
                    return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.consumer_throughput",
                        $"Consumer '{capacity.Kind}' has not established sufficient per-frame payload and directional transfer bandwidth.");
            }

            var managedSlots = checked((long)buffers.DecoderFrames + buffers.LiveFrames +
                buffers.CacheFrames + buffers.ReadbackFrames + buffers.SinkFrames);
            var requiredManagedBytes = checked(managedSlots * frameBytes);
            var requiredGpuBytes = checked((long)buffers.GpuFrames * frameBytes);
            var requiredNativeBytes = checked((long)buffers.DecoderFrames *
                buffers.NativeDecoderBytesPerFrame);

            if (requiredManagedBytes > buffers.ManagedBudgetBytes)
                return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.managed_budget",
                    "The aggregate upper-bound decoder, live, cache, readback and sink allocation exceeds managed memory admission.");
            if (requiredGpuBytes > buffers.GpuBudgetBytes)
                return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.gpu_budget",
                    "The GPU surface allocation exceeds the provider's finite GPU budget.");
            if (requiredNativeBytes > buffers.NativeBudgetBytes)
                return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.native_budget",
                    "Native decoder reservations exceed the finite native memory budget.");

            return new UhdSdrPipelineCapacityDecision(true, "uhd.admission.capacity_fits",
                "Exact-format consumer and conservative resource preflight fits. Production activation still requires independently observed provider, deadline, soak and hardware evidence.",
                frameBytes, requiredManagedBytes, requiredGpuBytes, requiredNativeBytes,
                UhdSdrVideoProfiles.FrameDeadlineTicks(request.Format));
        }
        catch (OverflowException)
        {
            return UhdSdrPipelineCapacityDecision.Reject("uhd.admission.arithmetic_overflow",
                "Frame, throughput or pool resource arithmetic overflowed; no resources were reserved.");
        }
    }
}
