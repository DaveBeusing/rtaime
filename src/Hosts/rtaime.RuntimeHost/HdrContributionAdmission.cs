// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

public enum HdrContributionConsumer
{
    Decoder = 1,
    GpuPrecision = 2,
    Preview = 3,
    Program = 4,
    SdrMonitoring = 5,
    Recording = 6,
    FileExport = 7,
    Ndi = 8,
    Srt = 9,
    St2110 = 10,
    Sdi = 11,
    Mxo = 12
}

/// <summary>
/// Separate provider observation for a precise HLG/native or half-float consumer.
/// Any values supplied by the caller are planning claims, NOT a verified GPU/SDI
/// grant and NOT license to activate Program. No current production provider
/// supplies a qualified native HLG descriptor through this boundary.
/// </summary>
public sealed record HdrContributionConsumerEvidence(
    HdrContributionConsumer Consumer,
    string ProviderIdentity,
    HdrContributionVideoProfile Format,
    HdrNativePixelLayout ConsumedLayout,
    bool Available,
    bool PreservesTenBitSignal,
    bool PreservesHlgMetadata,
    bool PreservesSourceTiming,
    long MaximumFrameBytes,
    long MaximumDirectionalBytesPerSecond);

public sealed record HdrContributionAllocationBudget(
    int NativeFrameSlots,
    int LinearHalfGpuFrameSlots,
    int NativeSinkFrameSlots,
    long ManagedBudgetBytes,
    long GpuBudgetBytes,
    long NativeBudgetBytes);

public sealed record HdrContributionAdmissionRequest(
    HdrContributionVideoProfile Format,
    IReadOnlyList<HdrContributionConsumer> ActiveConsumers,
    IReadOnlyList<HdrContributionConsumerEvidence> Evidence,
    HdrContributionAllocationBudget Budget,
    string? ExplicitSdrMonitoringTransformId);

public sealed record HdrContributionAdmissionDecision(
    bool CapacityFits, bool ProductionAdmitted,
    string Code, string Detail, long NativeFrameBytes,
    long LinearGpuFrameBytes, long RequiredManagedBytes,
    long RequiredGpuBytes, long RequiredNativeBytes)
{
    public static HdrContributionAdmissionDecision Reject(string code, string reason) =>
        new(false, false, code, reason, 0, 0, 0, 0, 0);
}

/// <summary>
/// Non-authoritative, side-effect-free native precision and HDR metadata
/// preflight. The actual providers currently remain HD/RGBA8-only. No
/// ControlHost commit, output fallback, GPU scheduling or recording state
/// is modified or inferred from a caller-supplied capability description.
/// </summary>
public static class HdrContributionAdmission
{
    private static readonly HdrContributionConsumer[] Mandatory =
    [
        HdrContributionConsumer.Decoder,
        HdrContributionConsumer.GpuPrecision,
        HdrContributionConsumer.Preview,
        HdrContributionConsumer.Program,
        HdrContributionConsumer.SdrMonitoring
    ];

    private static bool IsNativeSink(HdrContributionConsumer kind) =>
        kind is HdrContributionConsumer.Recording or HdrContributionConsumer.FileExport or
            HdrContributionConsumer.Ndi or HdrContributionConsumer.Srt or
            HdrContributionConsumer.St2110 or HdrContributionConsumer.Sdi or
            HdrContributionConsumer.Mxo or HdrContributionConsumer.Program;

    public static HdrContributionAdmissionDecision Evaluate(HdrContributionAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Format is null || !request.Format.IsExactR153LiveHlg)
            return HdrContributionAdmissionDecision.Reject("hdr.admission.format_unsupported",
                "R 153 contribution requires authoritative BT.2100 HLG, BT.2020, limited native 10-bit 4:2:2 at 2160p50/60000/1001.");
        if (request.ActiveConsumers is null || request.Evidence is null || request.Budget is null ||
            request.ActiveConsumers.Any(kind => !Enum.IsDefined(kind)) ||
            request.ActiveConsumers.Distinct().Count() != request.ActiveConsumers.Count ||
            Mandatory.Any(kind => !request.ActiveConsumers.Contains(kind)) ||
            request.ActiveConsumers.Count != request.Evidence.Count ||
            request.Evidence.Any(cap => cap is null || !Enum.IsDefined(cap.Consumer) ||
                !request.ActiveConsumers.Contains(cap.Consumer)) ||
            request.Evidence.Select(cap => cap.Consumer).Distinct().Count() != request.Evidence.Count)
            return HdrContributionAdmissionDecision.Reject("hdr.admission.consumer_set_invalid",
                "Every required and active optional consumer must have one explicit, unique capability.");
        if (string.IsNullOrWhiteSpace(request.ExplicitSdrMonitoringTransformId))
            return HdrContributionAdmissionDecision.Reject("hdr.admission.monitor_transform_required",
                "SDR monitoring needs an explicitly selected transform, separate from native HDR Program pixels.");

        try
        {
            var nativeLayout = HdrNativeFrameLayout.TightlyPacked(
                request.Format.Width, request.Format.Height, request.Format.Layout);
            var gpuLayout = HdrNativeFrameLayout.TightlyPacked(
                request.Format.Width, request.Format.Height,
                HdrNativePixelLayout.LinearRgbaHalfLe);
            var bytesPerNativeFrame = nativeLayout.RequiredBytes;
            var bytesPerGpuFrame = gpuLayout.RequiredBytes;
            var nativeRate = checked((long)(((Int128)bytesPerNativeFrame *
                request.Format.FrameRate.Numerator +
                request.Format.FrameRate.Denominator - 1) /
                request.Format.FrameRate.Denominator));
            var gpuRate = checked((long)(((Int128)bytesPerGpuFrame *
                request.Format.FrameRate.Numerator +
                request.Format.FrameRate.Denominator - 1) /
                request.Format.FrameRate.Denominator));

            foreach (var cap in request.Evidence)
            {
                if (string.IsNullOrWhiteSpace(cap.ProviderIdentity) || !cap.Available)
                    return HdrContributionAdmissionDecision.Reject("hdr.admission.provider_unavailable",
                        $"Consumer {cap.Consumer} has no available, identified native HDR provider.");
                if (cap.Format != request.Format)
                    return HdrContributionAdmissionDecision.Reject("hdr.admission.color_or_rate_mismatch",
                        $"Consumer {cap.Consumer} has different color, precision, cadence or metadata.");
                if (cap.Consumer == HdrContributionConsumer.GpuPrecision ||
                    cap.Consumer == HdrContributionConsumer.SdrMonitoring)
                {
                    if (cap.ConsumedLayout != HdrNativePixelLayout.LinearRgbaHalfLe ||
                        cap.MaximumFrameBytes < bytesPerGpuFrame ||
                        cap.MaximumDirectionalBytesPerSecond < gpuRate)
                        return HdrContributionAdmissionDecision.Reject("hdr.admission.gpu_precision_unsupported",
                            $"Consumer {cap.Consumer} cannot retain high-precision linear GPU raster/throughput.");
                }
                else if (cap.ConsumedLayout != HdrNativePixelLayout.PlanarYcbcr422P10Le ||
                    cap.MaximumFrameBytes < bytesPerNativeFrame ||
                    cap.MaximumDirectionalBytesPerSecond < nativeRate)
                    return HdrContributionAdmissionDecision.Reject("hdr.admission.native_precision_unsupported",
                        $"Consumer {cap.Consumer} cannot accept the exact native 10-bit plane payload/throughput.");
                if (!cap.PreservesSourceTiming)
                    return HdrContributionAdmissionDecision.Reject("hdr.admission.pts_unverified",
                        $"Consumer {cap.Consumer} cannot prove original frame PTS/timebase preservation.");
                if (IsNativeSink(cap.Consumer) &&
                    (!cap.PreservesTenBitSignal || !cap.PreservesHlgMetadata))
                    return HdrContributionAdmissionDecision.Reject("hdr.admission.sink_hdr_unverified",
                        $"Sink {cap.Consumer} lacks native ten-bit or HLG metadata retention; implicit SDR downconversion is forbidden.");
            }

            var budget = request.Budget;
            var activeNativeSinkCount = request.ActiveConsumers.Count(IsNativeSink);
            if (budget.NativeFrameSlots is < 1 or > 256 ||
                budget.LinearHalfGpuFrameSlots is < 1 or > 256 ||
                budget.NativeSinkFrameSlots < activeNativeSinkCount ||
                budget.NativeSinkFrameSlots > 512 ||
                budget.ManagedBudgetBytes <= 0 || budget.GpuBudgetBytes <= 0 ||
                budget.NativeBudgetBytes <= 0)
                return HdrContributionAdmissionDecision.Reject("hdr.admission.resource_evidence_invalid",
                    "Finite native decode, high-precision GPU, per-sink payload and domain budgets are required.");

            // Distinct owner allocations, not duplicate references to retained leases.
            // True live owner counts must come from Runtime/Provider observation.
            var requiredManaged = checked(bytesPerNativeFrame *
                (long)budget.NativeSinkFrameSlots);
            var requiredGpu = checked(bytesPerGpuFrame *
                (long)budget.LinearHalfGpuFrameSlots);
            var requiredNative = checked(bytesPerNativeFrame *
                (long)budget.NativeFrameSlots);
            if (requiredManaged > budget.ManagedBudgetBytes)
                return HdrContributionAdmissionDecision.Reject("hdr.admission.managed_budget",
                    "Sink frame staging exceeds the finite managed budget.");
            if (requiredGpu > budget.GpuBudgetBytes)
                return HdrContributionAdmissionDecision.Reject("hdr.admission.gpu_budget",
                    "High-precision GPU surfaces exceed the finite GPU budget.");
            if (requiredNative > budget.NativeBudgetBytes)
                return HdrContributionAdmissionDecision.Reject("hdr.admission.native_budget",
                    "Native decoder slots exceed the finite native budget.");

            return new(true, false, "hdr.admission.capacity_only",
                "Only exact HDR candidate arithmetic and declared per-consumer capabilities fit. No current production GPU/decoder/recording/monitoring provider has qualified this live mode; no Program activation is permitted.",
                bytesPerNativeFrame, bytesPerGpuFrame, requiredManaged,
                requiredGpu, requiredNative);
        }
        catch (OverflowException)
        {
            return HdrContributionAdmissionDecision.Reject("hdr.admission.arithmetic_overflow",
                "Native/high-precision frame memory or throughput arithmetic overflowed.");
        }
    }
}
