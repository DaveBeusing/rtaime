// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// A finite, provider/device-specific admission envelope. Creating this descriptor
/// does not certify hardware; the owning provider must establish its evidence.
/// No published wire or persistence format depends on this in-process type.
/// </summary>
public sealed class RuntimeResourceCapabilityProfile
{
    private readonly ReadOnlyCollection<VideoFormat> _formats;
    private readonly ReadOnlyCollection<string> _targets;

    public RuntimeResourceCapabilityProfile(
        string providerId,
        string deviceIdentity,
        string? qualificationEvidence,
        IReadOnlyList<VideoFormat> admittedFormats,
        IReadOnlyList<string> admittedTargets,
        int maximumDecoderOwners,
        int maximumSequenceOwners,
        int maximumSurfacesPerTarget,
        long managedBudgetBytes,
        long gpuBudgetBytes,
        long nativeBudgetBytes,
        bool hardwareQualified)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(deviceIdentity))
            throw new ArgumentException("Provider and device identities are required.");
        ArgumentNullException.ThrowIfNull(admittedFormats);
        ArgumentNullException.ThrowIfNull(admittedTargets);
        if (admittedFormats.Count == 0 || admittedFormats.Any(format => format.Width == 0 || format.Height == 0))
            throw new ArgumentException("At least one valid admitted format is required.", nameof(admittedFormats));
        if (maximumDecoderOwners < 1 || maximumSequenceOwners < 1 ||
            maximumSequenceOwners > maximumDecoderOwners || maximumSurfacesPerTarget < 1 ||
            managedBudgetBytes < 1 || gpuBudgetBytes < 0 || nativeBudgetBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDecoderOwners), "Resource capacities must be finite and internally consistent.");
        if (admittedTargets.Any(string.IsNullOrWhiteSpace) ||
            admittedTargets.Distinct(StringComparer.Ordinal).Count() != admittedTargets.Count)
            throw new ArgumentException("Output targets must be unique, stable and nonempty.", nameof(admittedTargets));
        if (hardwareQualified &&
            (string.IsNullOrWhiteSpace(qualificationEvidence) || gpuBudgetBytes == 0 || nativeBudgetBytes == 0))
            throw new ArgumentException("Physical qualification requires source-bound evidence and finite GPU/native budgets.");

        ProviderId = providerId;
        DeviceIdentity = deviceIdentity;
        QualificationEvidence = qualificationEvidence;
        _formats = Array.AsReadOnly(admittedFormats.Distinct().ToArray());
        _targets = Array.AsReadOnly(admittedTargets.ToArray());
        MaximumDecoderOwners = maximumDecoderOwners;
        MaximumSequenceOwners = maximumSequenceOwners;
        MaximumSurfacesPerTarget = maximumSurfacesPerTarget;
        ManagedBudgetBytes = managedBudgetBytes;
        GpuBudgetBytes = gpuBudgetBytes;
        NativeBudgetBytes = nativeBudgetBytes;
        HardwareQualified = hardwareQualified;
    }

    public string ProviderId { get; }
    public string DeviceIdentity { get; }
    public string? QualificationEvidence { get; }
    public IReadOnlyList<VideoFormat> AdmittedFormats => _formats;
    public IReadOnlyList<string> AdmittedTargets => _targets;
    public int MaximumDecoderOwners { get; }
    public int MaximumSequenceOwners { get; }
    public int MaximumSurfacesPerTarget { get; }
    public long ManagedBudgetBytes { get; }
    public long GpuBudgetBytes { get; }
    public long NativeBudgetBytes { get; }
    public bool HardwareQualified { get; }
}

public sealed record RuntimeOutputCapacityRequest(
    string TargetId, VideoFormat Format, int VisibleSurfaces, bool PhysicalOutput);

public sealed record RuntimeResourceCapacityRequest(
    VideoFormat Format, int DecoderOwners, int SequenceOwners,
    IReadOnlyList<RuntimeOutputCapacityRequest> Outputs);

public sealed record RuntimeResourceCapacityDecision(
    bool Admitted, string Code, string Detail, long RequiredManagedBytes, long RequiredGpuBytes)
{
    public static RuntimeResourceCapacityDecision Reject(string code, string detail) =>
        new(false, code, detail, 0, 0);
}

/// <summary>
/// Side-effect-free admission for a complete decoder/sequence/output candidate.
/// No frame, native handle, provider sink or Program state is changed on rejection.
/// The caller still owns real provider reservation and atomic prepare/commit.
/// </summary>
public static class RuntimeResourceCapacityAdmission
{
    public static RuntimeResourceCapacityDecision Evaluate(
        RuntimeResourceCapabilityProfile profile, RuntimeResourceCapacityRequest request)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Outputs is null || request.DecoderOwners < 0 || request.SequenceOwners < 0 ||
            request.SequenceOwners > request.DecoderOwners)
            return RuntimeResourceCapacityDecision.Reject("runtime.admission.request_invalid",
                "Decoder and sequence demands must be nonnegative and sequence owners must fit decoder owners.");
        if (!profile.AdmittedFormats.Contains(request.Format))
            return RuntimeResourceCapacityDecision.Reject("runtime.admission.format_unsupported",
                "The exact format, color description and cadence are not in the provider's admitted profile.");
        if (request.DecoderOwners > profile.MaximumDecoderOwners ||
            request.SequenceOwners > profile.MaximumSequenceOwners)
            return RuntimeResourceCapacityDecision.Reject("runtime.admission.decoder_capacity",
                "The provider's finite decoder or sequence-owner capacity is insufficient.");
        if (request.Outputs.Count > profile.AdmittedTargets.Count ||
            request.Outputs.Any(output => output is null ||
                string.IsNullOrWhiteSpace(output.TargetId) || output.VisibleSurfaces < 1 ||
                output.VisibleSurfaces > profile.MaximumSurfacesPerTarget) ||
            request.Outputs.Select(output => output.TargetId).Distinct(StringComparer.Ordinal).Count() != request.Outputs.Count)
            return RuntimeResourceCapacityDecision.Reject("runtime.admission.output_invalid",
                "Output targets must be unique and have an admitted number of visible surfaces.");
        if (request.Outputs.Any(output => !profile.AdmittedTargets.Contains(output.TargetId) ||
                                          !profile.AdmittedFormats.Contains(output.Format)))
            return RuntimeResourceCapacityDecision.Reject("runtime.admission.output_unsupported",
                "The provider has not admitted this output target or its exact format.");
        if (request.Outputs.Any(output => output.PhysicalOutput) && !profile.HardwareQualified)
            return RuntimeResourceCapacityDecision.Reject("runtime.admission.hardware_unverified",
                "Physical output requires verified device, driver, format and resource evidence.");

        try
        {
            // The existing retained pool needs ten RGBA frame slots, one scratch frame,
            // and ten bounded audio payload slots per admitted decoder owner.
            var frameBytes = checked((long)request.Format.Width * request.Format.Height * 4L);
            var decoderBytes = checked(request.DecoderOwners * checked(frameBytes * 11L + 10L * 32_768));
            long outputBytes = 0;
            foreach (var output in request.Outputs)
                outputBytes = checked(outputBytes +
                    checked((long)output.Format.Width * output.Format.Height * 4L));
            var managedBytes = checked(decoderBytes + outputBytes);
            var gpuBytes = checked(outputBytes * 2L);
            if (managedBytes > profile.ManagedBudgetBytes)
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.managed_budget",
                    "The candidate exceeds the finite managed frame/pool allocation budget.");
            if (gpuBytes > profile.GpuBudgetBytes)
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.gpu_budget",
                    "The candidate exceeds the measured GPU output-surface budget.");
            return new(true, "runtime.admission.admitted",
                "The declared finite resource envelope admits this candidate; actual provider reservation and commit remain required.",
                managedBytes, gpuBytes);
        }
        catch (OverflowException)
        {
            return RuntimeResourceCapacityDecision.Reject("runtime.admission.arithmetic_overflow",
                "Candidate resource arithmetic overflowed and was rejected before allocation.");
        }
    }
}
