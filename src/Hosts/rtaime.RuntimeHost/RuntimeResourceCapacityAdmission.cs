// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>Transport-typed, exact-format target admission. This model does not verify hardware evidence.</summary>
public enum RuntimeOutputTransportKind
{
    Software = 1,
    Sdi = 2,
    Ndi = 3,
    Srt = 4
}

/// <summary>
/// Per-target format and I/O support. A physical qualification flag is only
/// accepted after an external evidence verifier checks exact source, driver,
/// device, output target, cadence and color identity. A descriptor alone cannot
/// create a real provider output or certify hardware.
/// </summary>
public sealed class RuntimeOutputTargetCapability
{
    private readonly ReadOnlyCollection<VideoFormat> _formats;

    public RuntimeOutputTargetCapability(
        string targetId, RuntimeOutputTransportKind transport,
        IReadOnlyList<VideoFormat> admittedFormats,
        string? verifiedEvidenceId = null)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            throw new ArgumentException("A nonempty output target ID is required.", nameof(targetId));
        if (!Enum.IsDefined(transport))
            throw new ArgumentOutOfRangeException(nameof(transport));
        ArgumentNullException.ThrowIfNull(admittedFormats);
        if (admittedFormats.Count == 0 ||
            admittedFormats.Any(format => format.Width == 0 || format.Height == 0) ||
            admittedFormats.Distinct().Count() != admittedFormats.Count)
            throw new ArgumentException("A finite set of distinct exact video formats is required.",
                nameof(admittedFormats));
        if (verifiedEvidenceId is not null &&
            (string.IsNullOrWhiteSpace(verifiedEvidenceId) ||
             transport == RuntimeOutputTransportKind.Software))
            throw new ArgumentException("Physical qualification evidence must be nonempty and cannot describe a software target.",
                nameof(verifiedEvidenceId));

        TargetId = targetId;
        Transport = transport;
        _formats = Array.AsReadOnly(admittedFormats.ToArray());
        VerifiedEvidenceId = verifiedEvidenceId;
    }

    public string TargetId { get; }
    public RuntimeOutputTransportKind Transport { get; }
    public IReadOnlyList<VideoFormat> AdmittedFormats => _formats;
    public string? VerifiedEvidenceId { get; }
}

/// <summary>
/// A finite, provider/device-specific admission envelope. Creating this descriptor
/// does not certify hardware; the owning provider must establish its evidence.
/// No published wire or persistence format depends on this in-process type.
/// </summary>
public sealed class RuntimeResourceCapabilityProfile
{
    private readonly ReadOnlyCollection<VideoFormat> _formats;
    private readonly ReadOnlyCollection<string> _targets;
    private readonly ReadOnlyCollection<RuntimeOutputTargetCapability> _outputCapabilities;

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
        bool hardwareQualified,
        int maximumRegisteredSources = 8,
        IReadOnlyList<RuntimeOutputTargetCapability>? outputCapabilities = null,
        string? activeSourceCommit = null,
        string? activeDriverIdentity = null,
        string? activeProviderSdkIdentity = null,
        string? activeFirmwareIdentity = null)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(deviceIdentity))
            throw new ArgumentException("Provider and device identities are required.");
        ArgumentNullException.ThrowIfNull(admittedFormats);
        ArgumentNullException.ThrowIfNull(admittedTargets);
        if (admittedFormats.Count == 0 || admittedFormats.Any(format => format.Width == 0 || format.Height == 0))
            throw new ArgumentException("At least one valid admitted format is required.", nameof(admittedFormats));
        if (maximumDecoderOwners < 1 || maximumSequenceOwners < 1 ||
            maximumSequenceOwners > maximumDecoderOwners || maximumSurfacesPerTarget < 1 ||
            maximumRegisteredSources < maximumDecoderOwners || maximumRegisteredSources > 1024 ||
            managedBudgetBytes < 1 || gpuBudgetBytes < 0 || nativeBudgetBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDecoderOwners), "Resource capacities must be finite and internally consistent.");
        if (admittedTargets.Any(string.IsNullOrWhiteSpace) ||
            admittedTargets.Distinct(StringComparer.Ordinal).Count() != admittedTargets.Count)
            throw new ArgumentException("Output targets must be unique, stable and nonempty.", nameof(admittedTargets));
        if (hardwareQualified &&
            (string.IsNullOrWhiteSpace(qualificationEvidence) || gpuBudgetBytes == 0 || nativeBudgetBytes == 0))
            throw new ArgumentException("Physical qualification requires source-bound evidence and finite GPU/native budgets.");
        var qualifiedTargets = outputCapabilities?.ToArray() ??
            Array.Empty<RuntimeOutputTargetCapability>();
        if (qualifiedTargets.Any(target => target is null ||
                !admittedTargets.Contains(target.TargetId, StringComparer.Ordinal) ||
                target.AdmittedFormats.Any(format => !admittedFormats.Contains(format))) ||
            qualifiedTargets.Select(target => target.TargetId)
                .Distinct(StringComparer.Ordinal).Count() != qualifiedTargets.Length)
            throw new ArgumentException("Target-specific capabilities must be unique and belong to the exact provider format and target sets.",
                nameof(outputCapabilities));
        // One provider may have several independently qualified physical ports.
        // The profile-level evidence label is not a per-target security credential:
        // each port's own verifier-issued grant must match its declared target ID.
        if (!hardwareQualified &&
            qualifiedTargets.Any(target => target.VerifiedEvidenceId is not null))
            throw new ArgumentException("Physical target evidence requires an enabled, finite hardware profile.",
                nameof(outputCapabilities));

        ProviderId = providerId;
        DeviceIdentity = deviceIdentity;
        QualificationEvidence = qualificationEvidence;
        ActiveSourceCommit = activeSourceCommit;
        ActiveDriverIdentity = activeDriverIdentity;
        ActiveProviderSdkIdentity = activeProviderSdkIdentity;
        ActiveFirmwareIdentity = activeFirmwareIdentity;
        _formats = Array.AsReadOnly(admittedFormats.Distinct().ToArray());
        _targets = Array.AsReadOnly(admittedTargets.ToArray());
        _outputCapabilities = Array.AsReadOnly(qualifiedTargets);
        MaximumDecoderOwners = maximumDecoderOwners;
        MaximumSequenceOwners = maximumSequenceOwners;
        MaximumSurfacesPerTarget = maximumSurfacesPerTarget;
        MaximumRegisteredSources = maximumRegisteredSources;
        ManagedBudgetBytes = managedBudgetBytes;
        GpuBudgetBytes = gpuBudgetBytes;
        NativeBudgetBytes = nativeBudgetBytes;
        HardwareQualified = hardwareQualified;
    }

    public string ProviderId { get; }
    public string DeviceIdentity { get; }
    public string? QualificationEvidence { get; }
    /// <summary>Observed running binary/source identity, not a saved Show setting.</summary>
    public string? ActiveSourceCommit { get; }
    /// <summary>Observed provider driver identity, not user-authored metadata.</summary>
    public string? ActiveDriverIdentity { get; }
    public string? ActiveProviderSdkIdentity { get; }
    public string? ActiveFirmwareIdentity { get; }
    public IReadOnlyList<VideoFormat> AdmittedFormats => _formats;
    public IReadOnlyList<string> AdmittedTargets => _targets;
    /// <summary>Optional exact per-target color/cadence and SDI/NDI/SRT/software capability matrix.</summary>
    public IReadOnlyList<RuntimeOutputTargetCapability> OutputCapabilities => _outputCapabilities;
    public int MaximumDecoderOwners { get; }
    public int MaximumSequenceOwners { get; }
    public int MaximumSurfacesPerTarget { get; }
    public int MaximumRegisteredSources { get; }
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
    public long RequiredNativeBytes { get; init; }

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
        RuntimeResourceCapabilityProfile profile,
        RuntimeResourceCapacityRequest request,
        IReadOnlyList<RuntimeVerifiedPhysicalOutputCapability>? verifiedPhysicalOutputs = null)
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

        // The product-wide format set is not a Cartesian promise that every
        // output target can accept every frame-rate/color/resolution tuple.
        foreach (var output in request.Outputs)
        {
            var target = profile.OutputCapabilities.FirstOrDefault(cap =>
                string.Equals(cap.TargetId, output.TargetId, StringComparison.Ordinal));
            if (profile.OutputCapabilities.Count > 0 &&
                (target is null || !target.AdmittedFormats.Contains(output.Format)))
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.output_unsupported",
                    "This output target does not advertise the exact resolution, cadence and color tuple.");
            if (!output.PhysicalOutput) continue;
            if (target is null || target.Transport == RuntimeOutputTransportKind.Software ||
                target.VerifiedEvidenceId is null)
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.hardware_unverified",
                    "This exact physical output target lacks accepted source-bound qualification evidence.");
            if (!output.Format.Color.IsComplete ||
                output.Format.Color.Authority != ColorMetadataAuthority.Authoritative)
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.color_unverified",
                    "Physical output requires an authoritative, complete color description.");
            // A profile flag and matching evidence IDs are assertions, not proof.
            // Physical admission needs a verifier-minted, unexpired exact-source grant.
            var grants = verifiedPhysicalOutputs?.Where(grant =>
                string.Equals(grant.TargetId, output.TargetId, StringComparison.Ordinal)).ToArray();
            if (grants is not { Length: 1 } ||
                grants[0].ValidUntilUtc <= DateTimeOffset.UtcNow ||
                grants[0].ProviderId != profile.ProviderId ||
                grants[0].DeviceIdentity != profile.DeviceIdentity ||
                grants[0].SourceCommit != profile.ActiveSourceCommit ||
                grants[0].DriverIdentity != profile.ActiveDriverIdentity ||
                grants[0].ProviderSdkIdentity != profile.ActiveProviderSdkIdentity ||
                grants[0].FirmwareIdentity != profile.ActiveFirmwareIdentity ||
                grants[0].EvidenceId != target.VerifiedEvidenceId ||
                grants[0].Transport != target.Transport ||
                grants[0].Format != output.Format ||
                output.VisibleSurfaces > grants[0].MaximumSurfaces)
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.hardware_unverified",
                    "A current signed qualification grant is required for this exact physical target and format.");
        }

        try
        {
            // The existing retained pool needs ten RGBA frame slots, one scratch frame,
            // and ten bounded audio payload slots per admitted decoder owner.
            var frameBytes = checked((long)request.Format.Width * request.Format.Height * 4L);
            var decoderBytes = checked(request.DecoderOwners * checked(frameBytes * 11L + 10L * 32_768));
            long outputBytes = 0;
            long nativeBytes = 0;
            foreach (var output in request.Outputs)
            {
                var perTargetBytes = checked((long)output.Format.Width * output.Format.Height * 4L);
                outputBytes = checked(outputBytes + perTargetBytes);
                if (!output.PhysicalOutput) continue;
                var reservedNative = checked(perTargetBytes * 2L);
                nativeBytes = checked(nativeBytes + reservedNative);
                var grant = verifiedPhysicalOutputs!.Single(g => g.TargetId == output.TargetId);
                if (perTargetBytes > grant.ManagedBudgetBytes ||
                    reservedNative > grant.NativeBudgetBytes ||
                    reservedNative > grant.GpuBudgetBytes)
                    return RuntimeResourceCapacityDecision.Reject("runtime.admission.qualified_target_budget",
                        "The measured per-target memory budgets cannot admit this physical output.");
            }
            var managedBytes = checked(decoderBytes + outputBytes);
            var gpuBytes = checked(outputBytes * 2L);
            if (managedBytes > profile.ManagedBudgetBytes)
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.managed_budget",
                    "The candidate exceeds the finite managed frame/pool allocation budget.");
            if (gpuBytes > profile.GpuBudgetBytes)
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.gpu_budget",
                    "The candidate exceeds the measured GPU output-surface budget.");
            if (nativeBytes > profile.NativeBudgetBytes)
                return RuntimeResourceCapacityDecision.Reject("runtime.admission.native_budget",
                    "The candidate exceeds the finite native output-surface reservation budget.");
            return new(true, "runtime.admission.admitted",
                "The finite verified capacity envelope admits this candidate; actual provider reservation, commit and physical output remain required.",
                managedBytes, gpuBytes)
            { RequiredNativeBytes = nativeBytes };
        }
        catch (OverflowException)
        {
            return RuntimeResourceCapacityDecision.Reject("runtime.admission.arithmetic_overflow",
                "Candidate resource arithmetic overflowed and was rejected before allocation.");
        }
    }
}
