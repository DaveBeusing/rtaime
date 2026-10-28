// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class HdrContributionAdmissionTests
{
    private static HdrContributionAdmissionRequest Candidate(
        FrameRate? rate = null, bool includeSinks = true)
    {
        var format = rate == FrameRate.Fps59_94
            ? HdrContributionProfiles.Live2160p59_94("source-bound.test")
            : HdrContributionProfiles.Live2160p50("source-bound.test");

        var required = new[]
        {
            HdrContributionConsumer.Decoder,
            HdrContributionConsumer.GpuPrecision,
            HdrContributionConsumer.Preview,
            HdrContributionConsumer.Program,
            HdrContributionConsumer.SdrMonitoring
        };
        var optional = new[]
        {
            HdrContributionConsumer.Recording,
            HdrContributionConsumer.FileExport,
            HdrContributionConsumer.Ndi,
            HdrContributionConsumer.Srt,
            HdrContributionConsumer.St2110,
            HdrContributionConsumer.Sdi,
            HdrContributionConsumer.Mxo
        };
        var consumers = includeSinks ? required.Concat(optional).ToArray() : required;
        var caps = consumers.Select(kind => new HdrContributionConsumerEvidence(
            kind, "synthetic." + kind, format,
            kind is HdrContributionConsumer.GpuPrecision or HdrContributionConsumer.SdrMonitoring
                ? HdrNativePixelLayout.LinearRgbaHalfLe
                : HdrNativePixelLayout.PlanarYcbcr422P10Le,
            Available: true,
            PreservesTenBitSignal: kind != HdrContributionConsumer.SdrMonitoring,
            PreservesHlgMetadata: kind != HdrContributionConsumer.SdrMonitoring,
            PreservesSourceTiming: true,
            MaximumFrameBytes: 66_355_200,
            MaximumDirectionalBytesPerSecond: 10_000_000_000)).ToArray();
        return new HdrContributionAdmissionRequest(format, consumers, caps,
            new HdrContributionAllocationBudget(
                NativeFrameSlots: 2, LinearHalfGpuFrameSlots: 2,
                NativeSinkFrameSlots: 8,
                ManagedBudgetBytes: 1_000_000_000,
                GpuBudgetBytes: 1_000_000_000,
                NativeBudgetBytes: 1_000_000_000),
            "reference.hlg-bt2020-to-srgb.reinhard-scene.v1");
    }

    [Theory]
    [InlineData(50, 1)]
    [InlineData(60000, 1001)]
    public void Exact_HLG_software_capacity_never_authorizes_Program_or_record_output(
        long numerator, long denominator)
    {
        var candidate = Candidate(new FrameRate(numerator, denominator));
        var decision = HdrContributionAdmission.Evaluate(candidate);
        Assert.True(decision.CapacityFits, decision.Detail);
        Assert.False(decision.ProductionAdmitted);
        Assert.Equal("hdr.admission.capacity_only", decision.Code);
        Assert.Equal(33_177_600L, decision.NativeFrameBytes);
        Assert.Equal(66_355_200L, decision.LinearGpuFrameBytes);
        Assert.Equal(8L * 33_177_600L, decision.RequiredManagedBytes);
        Assert.Equal(2L * 66_355_200L, decision.RequiredGpuBytes);
        Assert.Equal(2L * 33_177_600L, decision.RequiredNativeBytes);
        Assert.Equal(VideoFormat.Hd1080p50Rgba8, RuntimeHostProcessOptions.Default.Format);
        Assert.Equal(2, ProfessionalRecordingFormats.Mp4H264Aac.SupportedInputVideoFormats.Count);
        Assert.Equal(ColorDescription.UnknownRgba8, VideoFormat.Hd1080p50Rgba8.Color);
    }

    [Fact]
    public void Every_active_HDR_sink_must_preserve_ten_bit_and_CICP_metadata()
    {
        var candidate = Candidate();
        foreach (var kind in new[]
        {
            HdrContributionConsumer.Program,
            HdrContributionConsumer.Recording,
            HdrContributionConsumer.FileExport,
            HdrContributionConsumer.Ndi,
            HdrContributionConsumer.Srt,
            HdrContributionConsumer.St2110,
            HdrContributionConsumer.Sdi,
            HdrContributionConsumer.Mxo
        })
        {
            foreach (var loseDepth in new[] { true, false })
            {
                var wrong = candidate with
                {
                    Evidence = candidate.Evidence.Select(cap => cap.Consumer == kind
                        ? cap with
                        {
                            PreservesTenBitSignal = !loseDepth,
                            PreservesHlgMetadata = loseDepth
                        }
                        : cap).ToArray()
                };
                var decision = HdrContributionAdmission.Evaluate(wrong);
                Assert.Equal("hdr.admission.sink_hdr_unverified", decision.Code);
                Assert.False(decision.ProductionAdmitted);
            }
        }
    }

    [Fact]
    public void Missing_SDR_monitor_transform_is_rejected_not_implicitly_converted()
    {
        var candidate = Candidate();
        var failed = HdrContributionAdmission.Evaluate(candidate with
        {
            ExplicitSdrMonitoringTransformId = null
        });
        Assert.Equal("hdr.admission.monitor_transform_required", failed.Code);

        var wrong = candidate with
        {
            Evidence = candidate.Evidence.Select(cap =>
                cap.Consumer == HdrContributionConsumer.SdrMonitoring
                    ? cap with { ConsumedLayout = HdrNativePixelLayout.PlanarYcbcr422P10Le }
                    : cap).ToArray()
        };
        Assert.Equal("hdr.admission.gpu_precision_unsupported",
            HdrContributionAdmission.Evaluate(wrong).Code);
    }

    [Fact]
    public void False_color_rate_PQ_and_P010_reject_without_mutating_legacy_Program()
    {
        var candidate = Candidate();
        var with25 = candidate with
        {
            Format = new HdrContributionVideoProfile(3840, 2160, new FrameRate(25, 1),
                candidate.Format.Layout, candidate.Format.Metadata)
        };
        Assert.Equal("hdr.admission.format_unsupported",
            HdrContributionAdmission.Evaluate(with25).Code);

        var with420 = candidate with
        {
            Format = new HdrContributionVideoProfile(3840, 2160, FrameRate.Fps50,
                HdrNativePixelLayout.P010Ycbcr420Le, candidate.Format.Metadata)
        };
        Assert.Equal("hdr.admission.format_unsupported",
            HdrContributionAdmission.Evaluate(with420).Code);

        var changedColor = candidate with
        {
            Evidence = candidate.Evidence.Select(cap => cap.Consumer ==
                HdrContributionConsumer.Decoder
                ? cap with { Format = HdrContributionProfiles.Live2160p50("different-provenance") }
                : cap).ToArray()
        };
        Assert.Equal("hdr.admission.color_or_rate_mismatch",
            HdrContributionAdmission.Evaluate(changedColor).Code);
        RuntimeHostProcessOptions.Default.Validate();
    }

    [Fact]
    public void Missing_provider_and_PTS_or_native_precision_mismatch_fail_closed()
    {
        var candidate = Candidate();
        Assert.Equal("hdr.admission.consumer_set_invalid",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Evidence = candidate.Evidence
                    .Where(cap => cap.Consumer != HdrContributionConsumer.Decoder).ToArray()
            }).Code);

        Assert.Equal("hdr.admission.provider_unavailable",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Evidence = candidate.Evidence.Select(cap =>
                    cap.Consumer == HdrContributionConsumer.Decoder
                        ? cap with { Available = false } : cap).ToArray()
            }).Code);

        Assert.Equal("hdr.admission.pts_unverified",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Evidence = candidate.Evidence.Select(cap =>
                    cap.Consumer == HdrContributionConsumer.Recording
                        ? cap with { PreservesSourceTiming = false } : cap).ToArray()
            }).Code);

        Assert.Equal("hdr.admission.native_precision_unsupported",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Evidence = candidate.Evidence.Select(cap =>
                    cap.Consumer == HdrContributionConsumer.Recording
                        ? cap with { ConsumedLayout = HdrNativePixelLayout.P010Ycbcr420Le }
                        : cap).ToArray()
            }).Code);
    }

    [Fact]
    public void Memory_pressure_and_overflow_reject_all_sinks_before_a_live_commit()
    {
        var candidate = Candidate();
        Assert.Equal("hdr.admission.resource_evidence_invalid",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Budget = candidate.Budget with { NativeSinkFrameSlots = 0 }
            }).Code);
        Assert.Equal("hdr.admission.managed_budget",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Budget = candidate.Budget with { ManagedBudgetBytes = 100_000 }
            }).Code);
        Assert.Equal("hdr.admission.gpu_budget",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Budget = candidate.Budget with { GpuBudgetBytes = 100_000 }
            }).Code);
        Assert.Equal("hdr.admission.native_budget",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Budget = candidate.Budget with { NativeBudgetBytes = 100_000 }
            }).Code);
        Assert.Equal("hdr.admission.resource_evidence_invalid",
            HdrContributionAdmission.Evaluate(candidate with
            {
                Budget = candidate.Budget with { NativeFrameSlots = int.MaxValue,
                    NativeBudgetBytes = long.MaxValue,
                    LinearHalfGpuFrameSlots = int.MaxValue,
                    GpuBudgetBytes = long.MaxValue }
            }).Code);
    }
}
