// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Evidence emitted by an independently qualified physical output run.
/// The payload must be signed by an explicitly enrolled qualification key.
/// Every format and device field is part of the signed JSON bytes.
/// </summary>
public sealed record RuntimePhysicalOutputEvidence(
    string SchemaVersion,
    string QualificationStatus,
    string EvidenceId,
    string SourceCommit,
    string ProviderId,
    string DeviceIdentity,
    string DriverIdentity,
    string TargetId,
    RuntimeOutputTransportKind Transport,
    uint Width,
    uint Height,
    long FrameRateNumerator,
    long FrameRateDenominator,
    PixelFormat PixelFormat,
    ScanMode ScanMode,
    ColorPrimaries Primaries,
    ColorTransfer Transfer,
    ColorMatrix Matrix,
    NominalRange Range,
    byte BitDepth,
    AlphaMode Alpha,
    ColorMetadataAuthority Authority,
    long ManagedBudgetBytes,
    long GpuBudgetBytes,
    long NativeBudgetBytes,
    int MaximumSurfaces,
    DateTimeOffset QualifiedAtUtc,
    DateTimeOffset ValidUntilUtc,
    bool ScanoutPassed,
    bool ColorAccuracyPassed,
    bool FrameCadencePassed,
    bool AudioVideoSyncPassed,
    bool RecoveryPassed,
    bool LongSoakPassed);

/// <summary>
/// Live host/provider identity established independently from evidence input.
/// Driver and target identity must be obtained from the active provider, not from a saved Show.
/// </summary>
public sealed record RuntimePhysicalOutputObservation(
    string SourceCommit,
    string ProviderId,
    string DeviceIdentity,
    string DriverIdentity,
    string TargetId,
    RuntimeOutputTransportKind Transport,
    VideoFormat Format);

/// <summary>
/// An in-process capability minted by the qualification verifier. An unverified
/// flag or manually supplied evidence string cannot produce this object.
/// </summary>
public sealed class RuntimeVerifiedPhysicalOutputCapability
{
    internal RuntimeVerifiedPhysicalOutputCapability(RuntimePhysicalOutputEvidence evidence, VideoFormat format)
    {
        SourceCommit = evidence.SourceCommit;
        ProviderId = evidence.ProviderId;
        DeviceIdentity = evidence.DeviceIdentity;
        DriverIdentity = evidence.DriverIdentity;
        TargetId = evidence.TargetId;
        Transport = evidence.Transport;
        Format = format;
        EvidenceId = evidence.EvidenceId;
        ManagedBudgetBytes = evidence.ManagedBudgetBytes;
        GpuBudgetBytes = evidence.GpuBudgetBytes;
        NativeBudgetBytes = evidence.NativeBudgetBytes;
        MaximumSurfaces = evidence.MaximumSurfaces;
        ValidUntilUtc = evidence.ValidUntilUtc;
    }

    public string SourceCommit { get; }
    public string ProviderId { get; }
    public string DeviceIdentity { get; }
    public string DriverIdentity { get; }
    public string TargetId { get; }
    public RuntimeOutputTransportKind Transport { get; }
    public VideoFormat Format { get; }
    public string EvidenceId { get; }
    public long ManagedBudgetBytes { get; }
    public long GpuBudgetBytes { get; }
    public long NativeBudgetBytes { get; }
    public int MaximumSurfaces { get; }
    public DateTimeOffset ValidUntilUtc { get; }
}

public sealed record RuntimePhysicalOutputVerificationResult(
    bool Accepted, string Code, RuntimeVerifiedPhysicalOutputCapability? Capability);

/// <summary>
/// Checks an exact-source physical output result against a trusted ECDSA-P256
/// public key and the live provider/device/driver/target/format observation.
/// Passing software CI or a self-asserted hardware-qualified descriptor cannot
/// manufacture an accepted physical-output grant.
/// </summary>
public static class RuntimePhysicalOutputEvidenceVerifier
{
    private const int MaximumPayloadBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static RuntimePhysicalOutputVerificationResult Verify(
        byte[] signedPayloadUtf8,
        byte[] signatureDer,
        byte[] trustedPublicKeySpki,
        RuntimePhysicalOutputObservation observed,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(signedPayloadUtf8);
        ArgumentNullException.ThrowIfNull(signatureDer);
        ArgumentNullException.ThrowIfNull(trustedPublicKeySpki);
        ArgumentNullException.ThrowIfNull(observed);

        static RuntimePhysicalOutputVerificationResult Deny(string code) => new(false, code, null);

        if (signedPayloadUtf8.Length is < 32 or > MaximumPayloadBytes ||
            signatureDer.Length is < 64 or > 256 ||
            trustedPublicKeySpki.Length is < 64 or > 512)
            return Deny("runtime.qualification.invalid_envelope");

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(trustedPublicKeySpki, out var read);
            if (read != trustedPublicKeySpki.Length || key.KeySize != 256 ||
                !key.VerifyData(signedPayloadUtf8, signatureDer, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
                return Deny("runtime.qualification.signature_invalid");
        }
        catch (CryptographicException)
        {
            return Deny("runtime.qualification.signature_invalid");
        }

        RuntimePhysicalOutputEvidence? evidence;
        try
        {
            using var json = JsonDocument.Parse(signedPayloadUtf8);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return Deny("runtime.qualification.schema_invalid");
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
                if (!members.Add(property.Name))
                    return Deny("runtime.qualification.duplicate_field");
            evidence = json.RootElement.Deserialize<RuntimePhysicalOutputEvidence>(JsonOptions);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            return Deny("runtime.qualification.schema_invalid");
        }
        if (evidence is null || evidence.SchemaVersion != "1.0" ||
            evidence.QualificationStatus != "PASS" ||
            string.IsNullOrWhiteSpace(evidence.EvidenceId) ||
            !Regex.IsMatch(evidence.SourceCommit ?? "", "^[0-9a-f]{40}$",
                RegexOptions.CultureInvariant) ||
            evidence.Transport is RuntimeOutputTransportKind.Software ||
            !Enum.IsDefined(evidence.Transport) ||
            !evidence.ScanoutPassed || !evidence.ColorAccuracyPassed ||
            !evidence.FrameCadencePassed || !evidence.AudioVideoSyncPassed ||
            !evidence.RecoveryPassed || !evidence.LongSoakPassed ||
            evidence.ManagedBudgetBytes <= 0 || evidence.GpuBudgetBytes <= 0 ||
            evidence.NativeBudgetBytes <= 0 || evidence.MaximumSurfaces <= 0)
            return Deny("runtime.qualification.incomplete_evidence");

        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (evidence.QualifiedAtUtc.Offset != TimeSpan.Zero ||
            evidence.ValidUntilUtc.Offset != TimeSpan.Zero ||
            evidence.QualifiedAtUtc > now || evidence.ValidUntilUtc <= now ||
            evidence.ValidUntilUtc <= evidence.QualifiedAtUtc ||
            evidence.ValidUntilUtc - evidence.QualifiedAtUtc > TimeSpan.FromDays(366))
            return Deny("runtime.qualification.expired_or_invalid_time");

        VideoFormat format;
        try
        {
            format = new VideoFormat(evidence.Width, evidence.Height,
                new FrameRate(evidence.FrameRateNumerator, evidence.FrameRateDenominator),
                evidence.PixelFormat, evidence.ScanMode,
                new ColorDescription(evidence.Primaries, evidence.Transfer,
                    evidence.Matrix, evidence.Range, evidence.BitDepth,
                    evidence.Alpha, evidence.Authority));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            return Deny("runtime.qualification.format_invalid");
        }
        if (!format.Color.IsComplete ||
            format.Color.Authority != ColorMetadataAuthority.Authoritative)
            return Deny("runtime.qualification.color_unverified");

        if (!string.Equals(evidence.SourceCommit, observed.SourceCommit, StringComparison.Ordinal) ||
            !string.Equals(evidence.ProviderId, observed.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(evidence.DeviceIdentity, observed.DeviceIdentity, StringComparison.Ordinal) ||
            !string.Equals(evidence.DriverIdentity, observed.DriverIdentity, StringComparison.Ordinal) ||
            !string.Equals(evidence.TargetId, observed.TargetId, StringComparison.Ordinal) ||
            evidence.Transport != observed.Transport || format != observed.Format)
            return Deny("runtime.qualification.device_or_format_changed");

        return new(true, "runtime.qualification.verified",
            new RuntimeVerifiedPhysicalOutputCapability(evidence, format));
    }
}
