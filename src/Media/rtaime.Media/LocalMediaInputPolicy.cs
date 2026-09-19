// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Media;

public readonly record struct LocalMediaInputProfile(
	MediaVideoCodec Codec,
	uint Width,
	uint Height,
	FrameRate FrameRate,
	uint? AverageBitRate);

public static class LocalMediaInputPolicy
{
	public const uint MinimumWidth = 48;
	public const uint MinimumHeight = 48;
	public const uint MaximumWidth = 4096;
	public const uint MaximumHeight = 2304;
	public const uint MaximumAverageBitRate = 300_000_000;
	public const long MaximumFramesPerSecond = 240;
	public const ulong MaximumMacroblocksPerSecond = 983_040;

	public static Failure? Validate(LocalMediaInputProfile profile)
	{
		if (profile.Width < MinimumWidth || profile.Height < MinimumHeight ||
			profile.Width > MaximumWidth || profile.Height > MaximumHeight)
		{
			return new Failure(
				"media.file.resolution_unsupported",
				$"{profile.Codec} input resolution '{profile.Width}x{profile.Height}' is outside the supported range " +
				$"{MinimumWidth}x{MinimumHeight}..{MaximumWidth}x{MaximumHeight}.");
		}

		var frameRateNumerator = profile.FrameRate.Numerator;
		var frameRateDenominator = profile.FrameRate.Denominator;
		if (frameRateNumerator <= 0 || frameRateDenominator <= 0)
		{
			return new Failure(
				"media.file.frame_rate_unsupported",
				$"{profile.Codec} input frame rate must be greater than zero.");
		}
		if ((decimal)frameRateNumerator >
			(decimal)MaximumFramesPerSecond * frameRateDenominator)
		{
			return new Failure(
				"media.file.frame_rate_unsupported",
				$"{profile.Codec} input frame rate '{profile.FrameRate}' exceeds the supported maximum of {MaximumFramesPerSecond} fps.");
		}

		if (profile.Codec == MediaVideoCodec.H264)
		{
			var macroblocksWide = checked(((ulong)profile.Width + 15UL) / 16UL);
			var macroblocksHigh = checked(((ulong)profile.Height + 15UL) / 16UL);
			var macroblocksPerFrame = checked(macroblocksWide * macroblocksHigh);
			var macroblocksPerSecondNumerator = (decimal)macroblocksPerFrame * frameRateNumerator;
			var macroblocksPerSecondLimit = (decimal)MaximumMacroblocksPerSecond * frameRateDenominator;
			if (macroblocksPerSecondNumerator > macroblocksPerSecondLimit)
			{
				return new Failure(
					"media.file.decode_rate_unsupported",
					$"H.264 input '{profile.Width}x{profile.Height} {profile.FrameRate}' exceeds the supported Level 5.1 decode-rate envelope.");
			}
		}

		if (profile.AverageBitRate is > MaximumAverageBitRate)
		{
			return new Failure(
				"media.file.bitrate_unsupported",
				$"{profile.Codec} input average bitrate '{profile.AverageBitRate.Value}' bit/s exceeds the supported maximum of " +
				$"{MaximumAverageBitRate} bit/s.");
		}

		return null;
	}
}
