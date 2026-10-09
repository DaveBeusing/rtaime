// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class BoundedBusDynamicsContractTests
{
	[Fact]
	public void Compressor_and_sample_peak_limiter_accept_documented_boundaries()
	{
		_ = new AudioBusCompressorConfiguration(
			true,
			AudioDynamicsLimits.MinimumCompressorThresholdDbFs,
			AudioDynamicsLimits.MaximumCompressorRatio,
			AudioDynamicsLimits.MinimumAttackMilliseconds,
			AudioDynamicsLimits.MaximumReleaseMilliseconds,
			AudioDynamicsLimits.MaximumMakeupGainDb);
		_ = new AudioBusSamplePeakLimiterConfiguration(
			true,
			AudioDynamicsLimits.MinimumLimiterCeilingDbFs,
			AudioDynamicsLimits.MinimumReleaseMilliseconds);
	}

	[Fact]
	public void Compressor_rejects_non_finite_and_out_of_range_parameters()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => Compressor(double.NaN, 2, 10, 100, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => Compressor(-61, 2, 10, 100, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => Compressor(-12, double.PositiveInfinity, 10, 100, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => Compressor(-12, 21, 10, 100, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => Compressor(-12, 2, 0.09, 100, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => Compressor(-12, 2, 10, 5_001, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => Compressor(-12, 2, 10, 100, 24.1));
	}

	[Fact]
	public void Sample_peak_limiter_rejects_non_finite_and_out_of_range_parameters()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBusSamplePeakLimiterConfiguration(true, double.NaN, 100));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBusSamplePeakLimiterConfiguration(true, -24.1, 100));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBusSamplePeakLimiterConfiguration(true, 0.1, 100));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBusSamplePeakLimiterConfiguration(true, -1, double.PositiveInfinity));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBusSamplePeakLimiterConfiguration(true, -1, 4.9));
	}

	private static AudioBusCompressorConfiguration Compressor(
		double thresholdDbFs,
		double ratio,
		double attackMilliseconds,
		double releaseMilliseconds,
		double makeupGainDb) =>
		new(true, thresholdDbFs, ratio, attackMilliseconds, releaseMilliseconds, makeupGainDb);
}
