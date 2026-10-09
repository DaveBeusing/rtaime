// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Media;

internal static class AudioDecibels
{
	public static double ToLinear(double decibels) => Math.Pow(10d, decibels / 20d);

	public static double FromLinear(double linear)
	{
		if (linear <= 0d)
			return double.NegativeInfinity;
		return 20d * Math.Log10(linear);
	}
}

internal readonly record struct AudioDynamicsSample(
	double Left,
	double Right,
	double CompressorGainReductionDb,
	double LimiterGainReductionDb,
	bool LimiterHit);

internal sealed class BoundedBusDynamicsState
{
	private const double SampleRate = 48_000d;

	private readonly AudioBusCompressorConfiguration? _compressor;
	private readonly AudioBusSamplePeakLimiterConfiguration? _limiter;
	private readonly double _compressorAttackCoefficient;
	private readonly double _compressorReleaseCoefficient;
	private readonly double _compressorMakeupGain;
	private readonly double _limiterCeiling;
	private readonly double _limiterReleaseCoefficient;

	private double _compressorGain = 1d;
	private double _limiterGain = 1d;
	private double _workingCompressorGain = 1d;
	private double _workingLimiterGain = 1d;
	private double _blockStartCompressorGain = 1d;
	private double _blockStartLimiterGain = 1d;
	private bool _hasTimeline;
	private ulong _nextSamplePosition;
	private bool _hasCommittedBlock;
	private ulong _committedBlockSamplePosition;
	private uint _committedBlockSampleCount;

	public BoundedBusDynamicsState(AudioBusId busId, AudioBusDynamicsConfiguration? configuration)
	{
		BusId = busId;
		Configuration = configuration;
		_compressor = configuration?.Compressor;
		_limiter = configuration?.Limiter;
		IsActive = configuration?.HasActiveProcessing == true;
		_compressorAttackCoefficient = _compressor is null ? 0d : TimeCoefficient(_compressor.AttackMilliseconds);
		_compressorReleaseCoefficient = _compressor is null ? 0d : TimeCoefficient(_compressor.ReleaseMilliseconds);
		_compressorMakeupGain = _compressor is null ? 1d : AudioDecibels.ToLinear(_compressor.MakeupGainDb);
		_limiterCeiling = _limiter is null ? 1d : AudioDecibels.ToLinear(_limiter.CeilingDbFs);
		_limiterReleaseCoefficient = _limiter is null ? 0d : TimeCoefficient(_limiter.ReleaseMilliseconds);
	}

	public AudioBusId BusId { get; }
	public AudioBusDynamicsConfiguration? Configuration { get; }
	public bool IsActive { get; }

	public void BeginBlock(ulong samplePosition, uint sampleCount)
	{
		if (_hasCommittedBlock &&
			_committedBlockSamplePosition == samplePosition &&
			_committedBlockSampleCount == sampleCount)
		{
			_workingCompressorGain = _blockStartCompressorGain;
			_workingLimiterGain = _blockStartLimiterGain;
			return;
		}

		if (_hasTimeline && samplePosition != _nextSamplePosition)
		{
			_compressorGain = 1d;
			_limiterGain = 1d;
		}

		_blockStartCompressorGain = _compressorGain;
		_blockStartLimiterGain = _limiterGain;
		_workingCompressorGain = _compressorGain;
		_workingLimiterGain = _limiterGain;
		_committedBlockSamplePosition = samplePosition;
		_committedBlockSampleCount = sampleCount;
	}

	public AudioDynamicsSample Process(double left, double right)
	{
		double compressorGainReductionDb = 0d;
		if (_compressor is { Enabled: true } compressor)
		{
			var peak = StereoPeak(left, right);
			var inputDb = AudioDecibels.FromLinear(peak);
			var overThresholdDb = inputDb - compressor.ThresholdDbFs;
			var targetReductionDb = overThresholdDb > 0d
				? overThresholdDb * (1d - (1d / compressor.Ratio))
				: 0d;
			var targetGain = double.IsPositiveInfinity(targetReductionDb)
				? 0d
				: AudioDecibels.ToLinear(-targetReductionDb);
			var coefficient = targetGain < _workingCompressorGain
				? _compressorAttackCoefficient
				: _compressorReleaseCoefficient;
			_workingCompressorGain = targetGain + (coefficient * (_workingCompressorGain - targetGain));
			_workingCompressorGain = Math.Clamp(_workingCompressorGain, 0d, 1d);
			compressorGainReductionDb = GainReductionDb(_workingCompressorGain);
			left *= _workingCompressorGain * _compressorMakeupGain;
			right *= _workingCompressorGain * _compressorMakeupGain;
		}

		double limiterGainReductionDb = 0d;
		var limiterHit = false;
		if (_limiter is { Enabled: true })
		{
			var peak = StereoPeak(left, right);
			var targetGain = peak > _limiterCeiling
				? _limiterCeiling / peak
				: 1d;
			if (!double.IsFinite(targetGain))
				targetGain = 0d;
			targetGain = Math.Clamp(targetGain, 0d, 1d);
			limiterHit = targetGain < 1d;
			if (targetGain < _workingLimiterGain)
			{
				_workingLimiterGain = targetGain;
			}
			else
			{
				_workingLimiterGain =
					targetGain + (_limiterReleaseCoefficient * (_workingLimiterGain - targetGain));
			}
			_workingLimiterGain = Math.Clamp(_workingLimiterGain, 0d, 1d);
			limiterGainReductionDb = GainReductionDb(_workingLimiterGain);
			left *= _workingLimiterGain;
			right *= _workingLimiterGain;
		}

		return new AudioDynamicsSample(
			left,
			right,
			compressorGainReductionDb,
			limiterGainReductionDb,
			limiterHit);
	}

	public void CommitBlock(ulong samplePosition, uint sampleCount)
	{
		_compressorGain = _workingCompressorGain;
		_limiterGain = _workingLimiterGain;
		_nextSamplePosition = checked(samplePosition + sampleCount);
		_hasTimeline = true;
		_hasCommittedBlock = true;
	}

	private static double StereoPeak(double left, double right)
	{
		if (!double.IsFinite(left) || !double.IsFinite(right))
			return double.PositiveInfinity;
		return Math.Max(Math.Abs(left), Math.Abs(right));
	}

	private static double GainReductionDb(double gain)
	{
		if (gain >= 1d)
			return 0d;
		if (gain <= 0d)
			return 240d;
		return -AudioDecibels.FromLinear(gain);
	}

	private static double TimeCoefficient(double milliseconds)
	{
		var samples = milliseconds * 0.001d * SampleRate;
		return Math.Exp(-1d / samples);
	}
}
