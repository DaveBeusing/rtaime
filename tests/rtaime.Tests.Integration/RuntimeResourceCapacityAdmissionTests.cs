// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeResourceCapacityAdmissionTests
{
    private static readonly VideoFormat Hd = VideoFormat.Hd1080p50Rgba8;
    private static readonly VideoFormat Uhd =
        new(3840, 2160, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static RuntimeResourceCapabilityProfile Profile(
        int decoders = 8, long managedBytes = 2L * 1024 * 1024 * 1024,
        long gpuBytes = 512L * 1024 * 1024,
        IReadOnlyList<VideoFormat>? formats = null) =>
        new("virtual.reference", "synthetic.dev.fixture", null,
            formats ?? [Hd, Uhd], ["program", "aux-a", "aux-b"],
            decoders, decoders, 12, managedBytes, gpuBytes, 0, false);

    [Fact]
    public void One_atomic_candidate_can_plan_more_than_four_decoder_owners_and_multiple_software_targets()
    {
        var profile = Profile();
        var decision = RuntimeResourceCapacityAdmission.Evaluate(profile,
            new(Hd, 6, 5,
                [new("program", Hd, 8, false), new("aux-a", Uhd, 4, false)]));
        Assert.True(decision.Admitted, decision.Detail);
        Assert.Equal("runtime.admission.admitted", decision.Code);
        Assert.True(decision.RequiredManagedBytes > 0);
        Assert.True(decision.RequiredGpuBytes > 0);
    }

    [Fact]
    public void Decoder_and_sequence_demands_use_per_device_limits()
    {
        var profile = Profile(decoders: 6);
        Assert.Equal("runtime.admission.decoder_capacity",
            RuntimeResourceCapacityAdmission.Evaluate(profile, new(Hd, 7, 6, [])).Code);
        // Seven sequence owners cannot fit six requested decoder owners,
        // regardless of the device capacity; reject malformed demand first.
        Assert.Equal("runtime.admission.request_invalid",
            RuntimeResourceCapacityAdmission.Evaluate(profile, new(Hd, 6, 7, [])).Code);
        Assert.Equal("runtime.admission.request_invalid",
            RuntimeResourceCapacityAdmission.Evaluate(profile, new(Hd, 2, 3, [])).Code);
    }

    [Fact]
    public void Independent_output_targets_require_exact_advertised_target_format_and_surface_count()
    {
        var profile = Profile();
        Assert.Equal("runtime.admission.output_invalid",
            RuntimeResourceCapacityAdmission.Evaluate(profile, new(Hd, 0, 0,
                [new("program", Hd, 1, false), new("program", Hd, 1, false)])).Code);
        Assert.Equal("runtime.admission.output_invalid",
            RuntimeResourceCapacityAdmission.Evaluate(profile, new(Hd, 0, 0,
                [new("program", Hd, 13, false)])).Code);
        Assert.Equal("runtime.admission.output_unsupported",
            RuntimeResourceCapacityAdmission.Evaluate(profile, new(Hd, 0, 0,
                [new("nonexistent", Hd, 1, false)])).Code);
        Assert.Equal("runtime.admission.output_unsupported",
            RuntimeResourceCapacityAdmission.Evaluate(Profile(formats: [Hd]), new(Hd, 0, 0,
                [new("program", Uhd, 1, false)])).Code);
    }

    [Fact]
    public void Uhd_and_physical_output_are_not_inferred_from_current_HD_or_software_evidence()
    {
        var profile = Profile(formats: [Hd]);
        Assert.Equal("runtime.admission.format_unsupported",
            RuntimeResourceCapacityAdmission.Evaluate(profile, new(Uhd, 1, 1, [])).Code);
        Assert.Equal("runtime.admission.hardware_unverified",
            RuntimeResourceCapacityAdmission.Evaluate(Profile(), new(Hd, 1, 1,
                [new("program", Hd, 1, true)])).Code);
        Assert.Throws<ArgumentException>(() => new RuntimeResourceCapabilityProfile(
            "provider", "device", null, [Hd], ["program"], 4, 4, 8,
            1024 * 1024 * 1024L, 0, 0, true));
    }

    [Fact]
    public void Out_of_budget_and_overflow_are_rejected_before_allocation()
    {
        var small = Profile(managedBytes: 100L * 1024 * 1024);
        Assert.Equal("runtime.admission.managed_budget",
            RuntimeResourceCapacityAdmission.Evaluate(small, new(Hd, 2, 2, [])).Code);
        Assert.Equal("runtime.admission.gpu_budget",
            RuntimeResourceCapacityAdmission.Evaluate(Profile(gpuBytes: 1024), new(Hd, 0, 0,
                [new("program", Hd, 1, false)])).Code);
        var immense = new VideoFormat(uint.MaxValue, uint.MaxValue,
            FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
        Assert.Equal("runtime.admission.arithmetic_overflow",
            RuntimeResourceCapacityAdmission.Evaluate(Profile(formats: [immense]), new(immense, 1, 1, [])).Code);
    }

    [Fact]
    public void Distinct_output_targets_do_not_inherit_another_devices_color_or_cadence()
    {
        var rec709 = new VideoFormat(1920, 1080, FrameRate.Fps50,
            PixelFormat.Rgba8, ScanMode.Progressive, ColorDescription.Rec709FullRgba8);
        var srgb = new VideoFormat(1920, 1080, FrameRate.Fps50,
            PixelFormat.Rgba8, ScanMode.Progressive, ColorDescription.SrgbFullRgba8);
        var targets = new[]
        {
            new RuntimeOutputTargetCapability("program", RuntimeOutputTransportKind.Sdi, [rec709]),
            new RuntimeOutputTargetCapability("aux-a", RuntimeOutputTransportKind.Ndi, [srgb]),
            new RuntimeOutputTargetCapability("aux-b", RuntimeOutputTransportKind.Srt, [rec709])
        };
        var profile = new RuntimeResourceCapabilityProfile(
            "provider", "device", null, [rec709, srgb],
            ["program", "aux-a", "aux-b"], 2, 2, 4,
            256_000_000, 256_000_000, 0, false,
            outputCapabilities: targets);

        var planned = RuntimeResourceCapacityAdmission.Evaluate(profile,
            new(rec709, 0, 0,
                [new("program", rec709, 1, false), new("aux-a", srgb, 1, false)]));
        Assert.True(planned.Admitted, planned.Detail);

        var wrongColor = RuntimeResourceCapacityAdmission.Evaluate(profile,
            new(rec709, 0, 0, [new("program", srgb, 1, false)]));
        Assert.Equal("runtime.admission.output_unsupported", wrongColor.Code);

        var wrongCadence = new VideoFormat(1920, 1080, FrameRate.Fps59_94,
            PixelFormat.Rgba8, ScanMode.Progressive, ColorDescription.Rec709FullRgba8);
        Assert.Equal("runtime.admission.output_unsupported",
            RuntimeResourceCapacityAdmission.Evaluate(profile,
                new(rec709, 0, 0, [new("aux-b", wrongCadence, 1, false)])).Code);
        Assert.Equal("runtime.admission.hardware_unverified",
            RuntimeResourceCapacityAdmission.Evaluate(profile,
                new(rec709, 0, 0, [new("program", rec709, 1, true)])).Code);

        Assert.Equal(RuntimeOutputTransportKind.Sdi, profile.OutputCapabilities[0].Transport);
        Assert.Equal(RuntimeOutputTransportKind.Ndi, profile.OutputCapabilities[1].Transport);
        Assert.Equal(RuntimeOutputTransportKind.Srt, profile.OutputCapabilities[2].Transport);
    }

    [Fact]
    public void Physical_output_is_not_inferred_from_a_profile_wide_hardware_claim()
    {
        var knownColor = new VideoFormat(1920, 1080, FrameRate.Fps50,
            PixelFormat.Rgba8, ScanMode.Progressive, ColorDescription.Rec709FullRgba8);
        var profile = new RuntimeResourceCapabilityProfile(
            "provider", "device", "bound-source-evidence",
            [knownColor], ["program"], 2, 2, 4,
            256_000_000, 256_000_000, 256_000_000, true);
        // Even a historically broad hardwareQualified flag cannot admit an
        // unspecified physical SDI/NDI/SRT target.
        Assert.Equal("runtime.admission.hardware_unverified",
            RuntimeResourceCapacityAdmission.Evaluate(profile,
                new(knownColor, 0, 0, [new("program", knownColor, 1, true)])).Code);

        Assert.Throws<ArgumentException>(() => new RuntimeResourceCapabilityProfile(
            "provider", "device", null,
            [knownColor], ["program"], 2, 2, 4,
            256_000_000, 256_000_000, 0, false,
            outputCapabilities:
                [new RuntimeOutputTargetCapability("program", RuntimeOutputTransportKind.Sdi,
                    [knownColor], "unverified-evidence")]));
    }

    [Fact]
    public void Target_profiles_reject_unsupported_formats_and_mutable_external_arrays()
    {
        var formats = new[] { Hd, Uhd };
        var targets = new[]
        {
            new RuntimeOutputTargetCapability("program", RuntimeOutputTransportKind.Software, formats)
        };
        var profile = new RuntimeResourceCapabilityProfile(
            "provider", "device", null, formats, ["program"], 2, 2, 4,
            256_000_000, 256_000_000, 0, false,
            outputCapabilities: targets);
        formats[0] = Uhd;
        targets[0] = new RuntimeOutputTargetCapability("program",
            RuntimeOutputTransportKind.Software, [Uhd]);
        Assert.Equal(Hd, profile.OutputCapabilities[0].AdmittedFormats[0]);

        Assert.Throws<ArgumentException>(() => new RuntimeResourceCapabilityProfile(
            "provider", "device", null, [Hd], ["program"], 2, 2, 4,
            256_000_000, 256_000_000, 0, false,
            outputCapabilities:
                [new RuntimeOutputTargetCapability("program", RuntimeOutputTransportKind.Software, [Uhd])]));
        Assert.Throws<ArgumentException>(() => new RuntimeOutputTargetCapability(
            "program", RuntimeOutputTransportKind.Software, [Hd], "physical-evidence-not-allowed"));
    }

    [Fact]
    public void Profile_snapshots_do_not_follow_mutable_caller_arrays()
    {
        var formats = new[] { Hd, Uhd };
        var targets = new[] { "program", "aux-a" };
        var profile = new RuntimeResourceCapabilityProfile("p", "device", null,
            formats, targets, 3, 3, 5, 1_000_000_000, 256_000_000, 0, false);
        formats[0] = Uhd;
        targets[0] = "other";
        Assert.Equal(Hd, profile.AdmittedFormats[0]);
        Assert.Equal("program", profile.AdmittedTargets[0]);
    }
}
