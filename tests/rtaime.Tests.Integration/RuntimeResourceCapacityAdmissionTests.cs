// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
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
        Assert.Equal("runtime.admission.decoder_capacity",
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
