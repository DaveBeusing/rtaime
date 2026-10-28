// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class UhdSdrPipelineCapacityAdmissionTests
{
    private static UhdSdrPipelineCapacityRequest Candidate(VideoFormat? format = null)
    {
        var exact = format ?? UhdSdrVideoProfiles.Rgb2160p50;
        UhdSdrConsumerKind[] consumers =
        [
            UhdSdrConsumerKind.Decoder, UhdSdrConsumerKind.Gpu,
            UhdSdrConsumerKind.Preview, UhdSdrConsumerKind.Program,
            UhdSdrConsumerKind.Monitoring, UhdSdrConsumerKind.Recording,
            UhdSdrConsumerKind.Replay, UhdSdrConsumerKind.Ndi,
            UhdSdrConsumerKind.Srt, UhdSdrConsumerKind.Mxo
        ];

        // Synthetic test evidence exercises arithmetic only, not actual SDK,
        // device throughput, native decode, physical output or long-soak proof.
        return new UhdSdrPipelineCapacityRequest(
            exact, consumers,
            consumers.Select(kind => new UhdSdrConsumerCapacity(
                kind, "synthetic.test.provider", exact, true,
                33_177_600L, 10_000_000_000L, kind == UhdSdrConsumerKind.Gpu ? 2 : 1))
                .ToArray(),
            new UhdSdrBufferCapacity(
                DecoderFrames: 11, LiveFrames: 2, CacheFrames: 8,
                GpuFrames: 6, ReadbackFrames: 67, SinkFrames: 12,
                NativeDecoderBytesPerFrame: 12_000_000L,
                ManagedBudgetBytes: 4_000_000_000L,
                GpuBudgetBytes: 512_000_000L,
                NativeBudgetBytes: 1_000_000_000L));
    }

    [Theory]
    [InlineData(25, 1)]
    [InlineData(50, 1)]
    [InlineData(60_000, 1_001)]
    public void Exact_profiles_fit_only_as_non_authoritative_resource_preflight(
        long numerator, long denominator)
    {
        var format = new VideoFormat(3840, 2160, new FrameRate(numerator, denominator),
            PixelFormat.Rgba8, ScanMode.Progressive, ColorDescription.Rec709FullRgba8);
        var candidate = Candidate(format);
        var result = UhdSdrPipelineCapacityAdmission.Evaluate(candidate);

        Assert.True(result.CapacityFits, result.Detail);
        Assert.Equal("uhd.admission.capacity_fits", result.Code);
        Assert.Equal(33_177_600L, result.FrameBytes);
        Assert.Equal(100L * 33_177_600L, result.RequiredManagedBytes);
        Assert.Equal(6L * 33_177_600L, result.RequiredGpuBytes);
        Assert.Equal(132_000_000L, result.RequiredNativeBytes);
        Assert.Equal(UhdSdrVideoProfiles.FrameDeadlineTicks(format), result.FrameDeadlineTicks);

        // A synthetic capacity plan must never grant production or recording.
        Assert.Throws<ArgumentException>(() =>
            (RuntimeHostProcessOptions.Default with { Format = format }).Validate());
        Assert.DoesNotContain(format,
            ProfessionalRecordingFormats.Mp4H264Aac.SupportedInputVideoFormats);
    }

    [Fact]
    public void Unknown_untrusted_or_non_rgb_color_is_never_an_SDR_profile()
    {
        var unknown = new VideoFormat(3840, 2160, FrameRate.Fps50,
            PixelFormat.Rgba8, ScanMode.Progressive);
        var result = UhdSdrPipelineCapacityAdmission.Evaluate(Candidate(unknown));
        Assert.False(result.CapacityFits);
        Assert.Equal("uhd.admission.format_unsupported", result.Code);

        var ycbcr = new ColorDescription(ColorPrimaries.Rec709, ColorTransfer.Rec709,
            ColorMatrix.Rec709, NominalRange.Limited, 8, AlphaMode.Opaque,
            ColorMetadataAuthority.Authoritative);
        var ycbcrFormat = new VideoFormat(3840, 2160, FrameRate.Fps50,
            PixelFormat.Rgba8, ScanMode.Progressive, ycbcr);
        Assert.Equal("uhd.admission.format_unsupported",
            UhdSdrPipelineCapacityAdmission.Evaluate(Candidate(ycbcrFormat)).Code);
    }

    [Theory]
    [InlineData(UhdSdrConsumerKind.Decoder)]
    [InlineData(UhdSdrConsumerKind.Gpu)]
    [InlineData(UhdSdrConsumerKind.Preview)]
    [InlineData(UhdSdrConsumerKind.Program)]
    [InlineData(UhdSdrConsumerKind.Monitoring)]
    [InlineData(UhdSdrConsumerKind.Recording)]
    [InlineData(UhdSdrConsumerKind.Replay)]
    [InlineData(UhdSdrConsumerKind.Ndi)]
    [InlineData(UhdSdrConsumerKind.Srt)]
    [InlineData(UhdSdrConsumerKind.Mxo)]
    public void Every_active_consumer_rejects_exact_tuple_mismatch(UhdSdrConsumerKind kind)
    {
        var request = Candidate();
        var mismatched = request.ConsumerCapacities.Select(capacity =>
            capacity.Kind == kind
                ? capacity with { Format = UhdSdrVideoProfiles.Rgb2160p59_94 }
                : capacity).ToArray();
        var decision = UhdSdrPipelineCapacityAdmission.Evaluate(
            request with { ConsumerCapacities = mismatched });

        Assert.False(decision.CapacityFits);
        Assert.Equal("uhd.admission.consumer_format_mismatch", decision.Code);
    }

    [Fact]
    public void Missing_duplicate_disabled_and_insufficient_provider_capability_fail_closed()
    {
        var candidate = Candidate();
        var missing = candidate with
        {
            ConsumerCapacities = candidate.ConsumerCapacities
                .Where(cap => cap.Kind != UhdSdrConsumerKind.Program).ToArray()
        };
        Assert.Equal("uhd.admission.consumer_set_invalid",
            UhdSdrPipelineCapacityAdmission.Evaluate(missing).Code);

        var removed = candidate with
        {
            ActiveConsumers = candidate.ActiveConsumers
                .Where(kind => kind != UhdSdrConsumerKind.Program).ToArray(),
            ConsumerCapacities = candidate.ConsumerCapacities
                .Where(cap => cap.Kind != UhdSdrConsumerKind.Program).ToArray()
        };
        Assert.Equal("uhd.admission.consumer_set_invalid",
            UhdSdrPipelineCapacityAdmission.Evaluate(removed).Code);

        var disabled = candidate with
        {
            ConsumerCapacities = candidate.ConsumerCapacities.Select(cap =>
                cap.Kind == UhdSdrConsumerKind.Ndi ? cap with { Available = false } : cap).ToArray()
        };
        Assert.Equal("uhd.admission.consumer_unavailable",
            UhdSdrPipelineCapacityAdmission.Evaluate(disabled).Code);

        var bandwidth = candidate with
        {
            ConsumerCapacities = candidate.ConsumerCapacities.Select(cap =>
                cap.Kind == UhdSdrConsumerKind.Gpu
                    ? cap with { MaximumDirectionalBytesPerSecond = 2_000_000_000L }
                    : cap).ToArray()
        };
        Assert.Equal("uhd.admission.consumer_throughput",
            UhdSdrPipelineCapacityAdmission.Evaluate(bandwidth).Code);
    }

    [Fact]
    public void Readback_decoder_sink_and_each_memory_domain_require_finite_capacity()
    {
        var candidate = Candidate();
        Assert.Equal(RuntimeProgramExecutionService.ProgramReadbackBufferCapacity, 67);
        Assert.Equal("uhd.admission.buffer_evidence_invalid",
            UhdSdrPipelineCapacityAdmission.Evaluate(candidate with
            {
                Buffers = candidate.Buffers with { ReadbackFrames = 66 }
            }).Code);
        Assert.Equal("uhd.admission.buffer_evidence_invalid",
            UhdSdrPipelineCapacityAdmission.Evaluate(candidate with
            {
                Buffers = candidate.Buffers with { DecoderFrames = 10 }
            }).Code);
        Assert.Equal("uhd.admission.buffer_evidence_invalid",
            UhdSdrPipelineCapacityAdmission.Evaluate(candidate with
            {
                Buffers = candidate.Buffers with { SinkFrames = 0 }
            }).Code);
        Assert.Equal("uhd.admission.managed_budget",
            UhdSdrPipelineCapacityAdmission.Evaluate(candidate with
            {
                Buffers = candidate.Buffers with { ManagedBudgetBytes = 2_000_000_000L }
            }).Code);
        Assert.Equal("uhd.admission.gpu_budget",
            UhdSdrPipelineCapacityAdmission.Evaluate(candidate with
            {
                Buffers = candidate.Buffers with { GpuBudgetBytes = 33_177_600L }
            }).Code);
        Assert.Equal("uhd.admission.native_budget",
            UhdSdrPipelineCapacityAdmission.Evaluate(candidate with
            {
                Buffers = candidate.Buffers with { NativeBudgetBytes = 1L }
            }).Code);
        Assert.Equal("uhd.admission.arithmetic_overflow",
            UhdSdrPipelineCapacityAdmission.Evaluate(candidate with
            {
                Buffers = candidate.Buffers with { NativeDecoderBytesPerFrame = long.MaxValue }
            }).Code);
    }
}
