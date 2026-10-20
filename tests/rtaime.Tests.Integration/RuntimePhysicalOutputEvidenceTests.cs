// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimePhysicalOutputEvidenceTests
{
    private static readonly VideoFormat QualifiedFormat = new(
        1920, 1080, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive,
        ColorDescription.Rec709FullRgba8);
    private static readonly string Source = new('a', 40);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 20, 0, 0, TimeSpan.Zero);

    private static RuntimePhysicalOutputEvidence Evidence() => new(
        "1.0", "PASS", "fixture-signed-qualification", Source,
        "provider.sdi", "board.001", "driver.24.1", "sdk.4.2", "firmware.8.1", "connector.1",
        RuntimeOutputTransportKind.Sdi,
        QualifiedFormat.Width, QualifiedFormat.Height,
        QualifiedFormat.FrameRate.Numerator, QualifiedFormat.FrameRate.Denominator,
        QualifiedFormat.PixelFormat, QualifiedFormat.ScanMode,
        QualifiedFormat.Color.Primaries, QualifiedFormat.Color.Transfer,
        QualifiedFormat.Color.Matrix, QualifiedFormat.Color.Range,
        QualifiedFormat.Color.BitDepth, QualifiedFormat.Color.Alpha,
        QualifiedFormat.Color.Authority,
        256_000_000, 256_000_000, 256_000_000, 4,
        Now.AddDays(-1), Now.AddDays(30),
        true, true, true, true, true, true);

    private static RuntimePhysicalOutputObservation Observed() => new(
        Source, "provider.sdi", "board.001", "driver.24.1", "sdk.4.2", "firmware.8.1",
        "connector.1", RuntimeOutputTransportKind.Sdi, QualifiedFormat);

    private static RuntimeResourceCapabilityProfile Profile(
        long nativeBudget = 256_000_000,
        string? sourceCommit = null,
        string? driverIdentity = null) =>
        new("provider.sdi", "board.001", "fixture-signed-qualification",
            [QualifiedFormat], ["connector.1"], 2, 2, 4,
            256_000_000, 256_000_000, nativeBudget, true,
            outputCapabilities:
                [new RuntimeOutputTargetCapability("connector.1",
                    RuntimeOutputTransportKind.Sdi, [QualifiedFormat],
                    "fixture-signed-qualification")],
            activeSourceCommit: sourceCommit ?? Source,
            activeDriverIdentity: driverIdentity ?? "driver.24.1",
            activeProviderSdkIdentity: "sdk.4.2",
            activeFirmwareIdentity: "firmware.8.1");

    private static RuntimeResourceCapacityRequest Candidate() =>
        new(QualifiedFormat, 0, 0,
            [new RuntimeOutputCapacityRequest("connector.1", QualifiedFormat, 2, true)]);

    private static (byte[] Payload, byte[] Signature, byte[] PublicKey) Sign(
        ECDsa signer, RuntimePhysicalOutputEvidence evidence)
    {
        // Synthetic test signature. It is not a release/production trust enrollment.
        var payload = JsonSerializer.SerializeToUtf8Bytes(evidence);
        return (payload, signer.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence), signer.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void Physical_capability_requires_an_authenticated_exact_source_grant()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(signer, Evidence());
        var verified = RuntimePhysicalOutputEvidenceVerifier.Verify(
            signed.Payload, signed.Signature, signed.PublicKey, Observed(), Now);
        Assert.True(verified.Accepted, verified.Code);
        Assert.NotNull(verified.Capability);

        // A bare profile flag and even a matching evidence string cannot enable
        // a physical sink. Only a verifier-issued grant reaches provider admission.
        Assert.Equal("runtime.admission.hardware_unverified",
            RuntimeResourceCapacityAdmission.Evaluate(Profile(), Candidate()).Code);
        var admitted = RuntimeResourceCapacityAdmission.Evaluate(Profile(), Candidate(),
            [verified.Capability!]);
        Assert.True(admitted.Admitted, admitted.Detail);
        Assert.Equal(1920L * 1080 * 4 * 2, admitted.RequiredNativeBytes);
        Assert.Equal("runtime.admission.hardware_unverified",
            RuntimeResourceCapacityAdmission.Evaluate(
                Profile(sourceCommit: new string('b', 40)),
                Candidate(), [verified.Capability!]).Code);
        Assert.Equal("runtime.admission.hardware_unverified",
            RuntimeResourceCapacityAdmission.Evaluate(
                Profile(driverIdentity: "driver.25"),
                Candidate(), [verified.Capability!]).Code);

        Assert.Equal("runtime.admission.native_budget",
            RuntimeResourceCapacityAdmission.Evaluate(Profile(nativeBudget: 1),
                Candidate(), [verified.Capability!]).Code);
    }

    [Fact]
    public void Two_physical_ports_require_independent_signed_grants_with_distinct_evidence_ids()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = Evidence();
        var second = Evidence() with
        {
            EvidenceId = "fixture-signed-second-port",
            TargetId = "connector.2"
        };
        var firstSigned = Sign(signer, first);
        var secondSigned = Sign(signer, second);
        var firstVerified = RuntimePhysicalOutputEvidenceVerifier.Verify(
            firstSigned.Payload, firstSigned.Signature, firstSigned.PublicKey,
            Observed(), Now);
        var secondVerified = RuntimePhysicalOutputEvidenceVerifier.Verify(
            secondSigned.Payload, secondSigned.Signature, secondSigned.PublicKey,
            Observed() with { TargetId = "connector.2" }, Now);
        Assert.True(firstVerified.Accepted, firstVerified.Code);
        Assert.True(secondVerified.Accepted, secondVerified.Code);

        var profile = new RuntimeResourceCapabilityProfile(
            "provider.sdi", "board.001", "qualification-suite.2026-10-20",
            [QualifiedFormat], ["connector.1", "connector.2"], 2, 2, 4,
            256_000_000, 256_000_000, 256_000_000, true,
            outputCapabilities:
            [
                new RuntimeOutputTargetCapability("connector.1",
                    RuntimeOutputTransportKind.Sdi, [QualifiedFormat], first.EvidenceId),
                new RuntimeOutputTargetCapability("connector.2",
                    RuntimeOutputTransportKind.Sdi, [QualifiedFormat], second.EvidenceId)
            ],
            activeSourceCommit: Source,
            activeDriverIdentity: "driver.24.1",
            activeProviderSdkIdentity: "sdk.4.2",
            activeFirmwareIdentity: "firmware.8.1");
        var candidate = new RuntimeResourceCapacityRequest(QualifiedFormat, 0, 0,
        [
            new RuntimeOutputCapacityRequest("connector.1", QualifiedFormat, 2, true),
            new RuntimeOutputCapacityRequest("connector.2", QualifiedFormat, 2, true)
        ]);

        Assert.Equal("runtime.admission.hardware_unverified",
            RuntimeResourceCapacityAdmission.Evaluate(profile, candidate,
                [firstVerified.Capability!]).Code);
        Assert.Equal("runtime.admission.hardware_unverified",
            RuntimeResourceCapacityAdmission.Evaluate(profile, candidate,
                [firstVerified.Capability!, firstVerified.Capability!]).Code);

        var decision = RuntimeResourceCapacityAdmission.Evaluate(profile, candidate,
            [firstVerified.Capability!, secondVerified.Capability!]);
        Assert.True(decision.Admitted, decision.Detail);
        Assert.Equal(2L * 1920 * 1080 * 4 * 2, decision.RequiredNativeBytes);
        Assert.Equal(2L * 1920 * 1080 * 4 * 2, decision.RequiredGpuBytes);
    }

    [Fact]
    public void Invalid_signature_untrusted_key_and_modified_evidence_are_rejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var unrelated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(signer, Evidence());

        Assert.Equal("runtime.qualification.signature_invalid",
            RuntimePhysicalOutputEvidenceVerifier.Verify(
                signed.Payload, signed.Signature, unrelated.ExportSubjectPublicKeyInfo(),
                Observed(), Now).Code);
        var tampered = signed.Payload.ToArray();
        tampered[^2] ^= 1;
        Assert.Equal("runtime.qualification.signature_invalid",
            RuntimePhysicalOutputEvidenceVerifier.Verify(
                tampered, signed.Signature, signed.PublicKey, Observed(), Now).Code);
    }

    [Fact]
    public void Format_driver_target_source_and_expiration_cannot_be_inferred()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(signer, Evidence());
        RuntimePhysicalOutputVerificationResult Verify(
            RuntimePhysicalOutputObservation observation, DateTimeOffset now) =>
            RuntimePhysicalOutputEvidenceVerifier.Verify(signed.Payload, signed.Signature,
                signed.PublicKey, observation, now);

        Assert.Equal("runtime.qualification.device_or_format_changed",
            Verify(Observed() with { DriverIdentity = "driver.25" }, Now).Code);
        Assert.Equal("runtime.qualification.device_or_format_changed",
            Verify(Observed() with { ProviderSdkIdentity = "sdk.4.3" }, Now).Code);
        Assert.Equal("runtime.qualification.device_or_format_changed",
            Verify(Observed() with { FirmwareIdentity = "firmware.8.2" }, Now).Code);
        Assert.Equal("runtime.qualification.device_or_format_changed",
            Verify(Observed() with { TargetId = "connector.2" }, Now).Code);
        Assert.Equal("runtime.qualification.device_or_format_changed",
            Verify(Observed() with { SourceCommit = new string('b', 40) }, Now).Code);
        Assert.Equal("runtime.qualification.device_or_format_changed",
            Verify(Observed() with {
                Format = new VideoFormat(1920, 1080, FrameRate.Fps59_94,
                    PixelFormat.Rgba8, ScanMode.Progressive,
                    ColorDescription.Rec709FullRgba8)
            }, Now).Code);
        Assert.Equal("runtime.qualification.expired_or_invalid_time",
            Verify(Observed(), Now.AddDays(31)).Code);
    }

    [Theory]
    [InlineData("ProviderId")]
    [InlineData("DeviceIdentity")]
    [InlineData("DriverIdentity")]
    [InlineData("ProviderSdkIdentity")]
    [InlineData("FirmwareIdentity")]
    [InlineData("TargetId")]
    [InlineData("FrameCadencePassed")]
    public void Signed_but_incomplete_hardware_evidence_is_rejected_before_admission(string missing)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(Evidence()))!;
        Assert.True(fields.Remove(missing));
        var payload = JsonSerializer.SerializeToUtf8Bytes(fields);
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var result = RuntimePhysicalOutputEvidenceVerifier.Verify(
            payload, signature, signer.ExportSubjectPublicKeyInfo(), Observed(), Now);
        Assert.Equal("runtime.qualification.schema_invalid", result.Code);
        Assert.Null(result.Capability);
    }

    [Fact]
    public void Signed_blank_or_excessively_long_device_identifiers_do_not_establish_support()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (var candidate in new[]
        {
            Evidence() with { DeviceIdentity = " " },
            Evidence() with { DriverIdentity = "" },
            Evidence() with { ProviderId = "" },
            Evidence() with { TargetId = "" },
            Evidence() with { EvidenceId = new string('x', 257) }
        })
        {
            var signed = Sign(signer, candidate);
            var observed = Observed() with
            {
                ProviderId = candidate.ProviderId,
                DeviceIdentity = candidate.DeviceIdentity,
                DriverIdentity = candidate.DriverIdentity,
                TargetId = candidate.TargetId
            };
            var result = RuntimePhysicalOutputEvidenceVerifier.Verify(
                signed.Payload, signed.Signature, signed.PublicKey, observed, Now);
            Assert.Equal("runtime.qualification.incomplete_evidence", result.Code);
            Assert.Null(result.Capability);
        }
    }

    [Fact]
    public void Signed_failures_incomplete_color_or_resource_limits_never_promote_support()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (var candidate in new[]
        {
            Evidence() with { QualificationStatus = "UNVERIFIED" },
            Evidence() with { LongSoakPassed = false },
            Evidence() with { GpuBudgetBytes = 0 },
            Evidence() with { Authority = ColorMetadataAuthority.Missing },
            Evidence() with { MaximumSurfaces = 0 },
            Evidence() with { BitDepth = 10 },
            Evidence() with { Matrix = ColorMatrix.Rec709 }
        })
        {
            var signed = Sign(signer, candidate);
            var result = RuntimePhysicalOutputEvidenceVerifier.Verify(
                signed.Payload, signed.Signature, signed.PublicKey, Observed(), Now);
            Assert.False(result.Accepted);
            Assert.Null(result.Capability);
        }
    }
}
