// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class HighResolutionOutputAdmissionTests
{
	[Theory]
	[InlineData(25, 1)]
	[InlineData(50, 1)]
	[InlineData(60_000, 1_001)]
	public void Generic_UHD_descriptor_and_byte_sizing_do_not_grant_Program_admission(long numerator, long denominator)
	{
		var format = new VideoFormat(3840, 2160, new FrameRate(numerator, denominator),
			PixelFormat.Rgba8, ScanMode.Progressive);

		// Size arithmetic is safe without allocating a full-resolution payload.
		Assert.Equal(33_177_600, RgbaFrameBuffer.RequiredByteLength(format));
		Assert.Equal(4 * RgbaFrameBuffer.RequiredByteLength(VideoFormat.Hd1080p50Rgba8),
			RgbaFrameBuffer.RequiredByteLength(format));
		AssertRejectedByProductionConsumers(format);
		Assert.Throws<ArgumentException>(() => new VirtualTimingProvider(format));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Existing_HD_tuples_remain_admitted_without_color_relabeling(bool fractional)
	{
		var format = fractional ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
		var options = RuntimeHostProcessOptions.Default with { Format = format };
		options.Validate();

		Assert.Equal(ColorDescription.UnknownRgba8, format.Color);
		Assert.True(VirtualMediaReferenceProvider.IsSupportedFormat(format));
		using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
		Assert.NotEmpty(gpu.Descriptor.Capabilities);
		Assert.All(gpu.Descriptor.Capabilities, capability => Assert.True(capability.SupportsVideoFormat(format)));
		Assert.All(RecordingProfiles(), profile => Assert.Contains(format, profile.SupportedInputVideoFormats));
		Assert.Equal(VideoFormat.Hd1080p50Rgba8, RuntimeHostProcessOptions.Default.Format);
	}

	[Theory]
	[InlineData("2160p25")]
	[InlineData("2160p50")]
	[InlineData("2160p59.94")]
	public void Unsupported_UHD_configuration_is_rejected_from_both_configuration_sources(string alias)
	{
		var commandLine = Assert.Throws<ArgumentException>(() =>
			RuntimeHostProcessOptions.Load(new[] { $"--format={alias}" }, _ => null));
		var environment = Assert.Throws<ArgumentException>(() =>
			RuntimeHostProcessOptions.Load(Array.Empty<string>(),
				key => key == "RTAIME_RUNTIME_FORMAT" ? alias : null));

		Assert.Equal("format", commandLine.ParamName);
		Assert.Equal("format", environment.ParamName);
	}

	[Fact]
	public void Admitted_UHD_input_policy_cannot_select_UHD_Program_output()
	{
		var rate = new FrameRate(25, 1);
		var input = new LocalMediaInputProfile(MediaVideoCodec.H264, 3840, 2160, rate, 100_000_000);
		Assert.Null(LocalMediaInputPolicy.Validate(input));

		// Policy admission is distinct from actual decode and normalized Program capability.
		AssertRejectedByProductionConsumers(new VideoFormat(input.Width, input.Height, rate,
			PixelFormat.Rgba8, ScanMode.Progressive));
		RuntimeHostProcessOptions.Default.Validate();
	}

	[Theory]
	[InlineData(8, AlphaMode.Straight, true)]
	[InlineData(10, AlphaMode.Straight, false)]
	[InlineData(8, AlphaMode.Premultiplied, false)]
	public void Changing_HD_color_precision_or_alpha_does_not_inherit_legacy_capabilities(
		byte bitDepth, AlphaMode alpha, bool explicitColor)
	{
		var legacy = ColorDescription.UnknownRgba8;
		var color = explicitColor ? ColorDescription.Rec709FullRgba8 :
			new ColorDescription(legacy.Primaries, legacy.Transfer, legacy.Matrix,
				legacy.Range, bitDepth, alpha, legacy.Authority);
		var format = new VideoFormat(1920, 1080, FrameRate.Fps50,
			PixelFormat.Rgba8, ScanMode.Progressive, color);

		Assert.NotEqual(VideoFormat.Hd1080p50Rgba8, format);
		AssertRejectedByProductionConsumers(format);
	}

	private static void AssertRejectedByProductionConsumers(VideoFormat format)
	{
		var options = RuntimeHostProcessOptions.Default with { Format = format };
		var failure = Assert.Throws<ArgumentException>(() => options.Validate());
		Assert.Equal(nameof(RuntimeHostProcessOptions.Format), failure.ParamName);
		Assert.False(VirtualMediaReferenceProvider.IsSupportedFormat(format));

		using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
		Assert.NotEmpty(gpu.Descriptor.Capabilities);
		Assert.All(gpu.Descriptor.Capabilities, capability => Assert.False(capability.SupportsVideoFormat(format)));
		Assert.All(RecordingProfiles(), profile => Assert.DoesNotContain(format, profile.SupportedInputVideoFormats));
	}

	private static RecordingProfileDescriptor[] RecordingProfiles() =>
	[
		ProfessionalRecordingFormats.Mp4H264Aac,
		ProfessionalRecordingFormats.Mov2VuyPcm,
		ProfessionalRecordingFormats.MxfOp1aUncompressedPcm
	];
}
