// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;

namespace rtaime.Tests.Unit;

public sealed class LocalMediaInputPolicyTests
{
	[Theory]
	[InlineData(640, 360, 24_000, 1_001, 1_000_000)]
	[InlineData(720, 480, 30_000, 1_001, 2_500_000)]
	[InlineData(1280, 720, 25, 1, 5_000_000)]
	[InlineData(1280, 720, 60, 1, 20_000_000)]
	[InlineData(1280, 720, 240, 1, 100_000_000)]
	[InlineData(1920, 1080, 24_000, 1_001, 8_000_000)]
	[InlineData(1920, 1080, 60_000, 1_001, 50_000_000)]
	[InlineData(1920, 1080, 120, 1, 150_000_000)]
	[InlineData(2560, 1440, 60, 1, 120_000_000)]
	[InlineData(3840, 2160, 24, 1, 100_000_000)]
	[InlineData(3840, 2160, 30, 1, 250_000_000)]
	[InlineData(4096, 2304, 24_000, 1_001, 300_000_000)]
	public void Common_H264_MP4_input_profiles_are_accepted(
		int width,
		int height,
		long frameRateNumerator,
		long frameRateDenominator,
		int averageBitRate)
	{
		var profile = new LocalMediaInputProfile(
			checked((uint)width),
			checked((uint)height),
			new FrameRate(frameRateNumerator, frameRateDenominator),
			checked((uint)averageBitRate));

		Assert.Null(LocalMediaInputPolicy.Validate(profile));
	}

	[Fact]
	public void Missing_bitrate_metadata_does_not_reject_an_otherwise_supported_stream()
	{
		var profile = new LocalMediaInputProfile(
			1920,
			1080,
			FrameRate.Fps50,
			null);

		Assert.Null(LocalMediaInputPolicy.Validate(profile));
	}

	[Theory]
	[InlineData(47, 48, 25, 1, 5_000_000, "media.file.resolution_unsupported")]
	[InlineData(7680, 4320, 30, 1, 50_000_000, "media.file.resolution_unsupported")]
	[InlineData(3840, 2160, 60, 1, 100_000_000, "media.file.decode_rate_unsupported")]
	[InlineData(1920, 1080, 240, 1, 100_000_000, "media.file.decode_rate_unsupported")]
	[InlineData(1280, 720, 241, 1, 20_000_000, "media.file.frame_rate_unsupported")]
	[InlineData(1920, 1080, 60, 1, 300_000_001, "media.file.bitrate_unsupported")]
	public void Inputs_outside_the_supported_decode_envelope_fail_closed(
		int width,
		int height,
		long frameRateNumerator,
		long frameRateDenominator,
		long averageBitRate,
		string expectedCode)
	{
		var profile = new LocalMediaInputProfile(
			checked((uint)width),
			checked((uint)height),
			new FrameRate(frameRateNumerator, frameRateDenominator),
			checked((uint)averageBitRate));

		var failure = LocalMediaInputPolicy.Validate(profile);

		Assert.NotNull(failure);
		Assert.Equal(expectedCode, failure.Value.Code);
	}
}
