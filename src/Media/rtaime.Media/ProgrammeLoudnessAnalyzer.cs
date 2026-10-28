// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Media;

/// <summary>
/// Optional 48 kHz stereo post-bus analyser. Neither modifies nor owns the mix.
/// Processing is allocation-free after construction. Results are observational,
/// not an equipment certification or a programme gain-control instruction.
/// </summary>
public sealed class ProgrammeLoudnessAnalyzer
{
	public const int SampleRate = 48000;
	private const int SliceFrames = 4800; // 100 ms: 75% overlap for 400 ms gating
	private const int ShortTermSlices = 30;
	private const int FirTaps = 33;
	private const int FirMiddle = FirTaps / 2;
	private const int HistogramBins = 1101;
	private const double HistogramMinLufs = -80.0;
	private const double HistogramStep = 0.1;
	private const double AbsoluteGateLufs = -70.0;
	private readonly ProgrammeLoudnessPolicy _policy;
	private readonly double[] _sliceEnergies = new double[ShortTermSlices];
	private readonly long[] _integratedCounts = new long[HistogramBins];
	private readonly double[] _integratedEnergy = new double[HistogramBins];
	private readonly long[] _rangeCounts = new long[HistogramBins];
	private readonly float[] _firLeft = new float[FirTaps];
	private readonly float[] _firRight = new float[FirTaps];
	private static readonly double[,] Interpolator = CreateInterpolator();
	private Biquad _leftShelf;
	private Biquad _rightShelf;
	private Biquad _leftRlb;
	private Biquad _rightRlb;
	private int _sliceFrames;
	private int _sliceWriteIndex;
	private int _sliceCount;
	private long _completedSlices;
	private long _frames;
	private double _sliceSum;
	private double _truePeak;
	private int _firPosition;
	private int _firCount;
	private ulong _nextSample;
	private MediaSourceId? _sourceIdentity;
	private bool _discontinuity;
	private ulong _generation;

	public ProgrammeLoudnessAnalyzer(ProgrammeLoudnessPolicy policy)
	{
		_policy = policy ?? throw new ArgumentNullException(nameof(policy));
		Reset();
	}

	public ulong ResetGeneration => _generation;
	public ProgrammeLoudnessPolicy Policy => _policy;

	public void Reset()
	{
		_generation++;
		_leftShelf = KWeightShelf();
		_rightShelf = KWeightShelf();
		_leftRlb = KWeightRlb();
		_rightRlb = KWeightRlb();
		Array.Clear(_sliceEnergies);
		Array.Clear(_integratedCounts);
		Array.Clear(_integratedEnergy);
		Array.Clear(_rangeCounts);
		Array.Clear(_firLeft);
		Array.Clear(_firRight);
		_sliceFrames = _sliceWriteIndex = _sliceCount = 0;
		_completedSlices = _frames = 0;
		_sliceSum = _truePeak = 0;
		_firPosition = _firCount = 0;
		_sourceIdentity = null;
		_nextSample = 0;
		_discontinuity = false;
	}

	public void Interrupt()
	{
		if (_discontinuity)
			return;
		Reset();
		_discontinuity = true;
	}

	/// <summary>Consume immutable interleaved stereo output samples for exactly this audio frame range.</summary>
	public void Process(ReadOnlySpan<float> samples, ulong samplePosition, MediaSourceId sourceIdentity)
	{
		if (samples.Length == 0 || (samples.Length & 1) != 0)
			throw new ArgumentException("A nonempty interleaved stereo block is required.", nameof(samples));
		var sampleFrames = checked((uint)(samples.Length / 2));
		if (samplePosition > ulong.MaxValue - sampleFrames)
			throw new ArgumentOutOfRangeException(nameof(samplePosition));

		if (_sourceIdentity is not null &&
			(_nextSample != samplePosition || _sourceIdentity.Value != sourceIdentity))
			Reset();

		_sourceIdentity = sourceIdentity;
		_discontinuity = false;
		_nextSample = samplePosition + sampleFrames;
		for (var offset = 0; offset < samples.Length; offset += 2)
		{
			var left = samples[offset];
			var right = samples[offset + 1];
			if (!float.IsFinite(left) || !float.IsFinite(right))
			{
				Interrupt();
				return;
			}
			_truePeak = Math.Max(_truePeak, Math.Max(Math.Abs((double)left), Math.Abs((double)right)));
			ObserveTruePeak(left, right);
			var leftWeighted = _leftRlb.Process(_leftShelf.Process(left));
			var rightWeighted = _rightRlb.Process(_rightShelf.Process(right));
			_sliceSum += leftWeighted * leftWeighted + rightWeighted * rightWeighted;
			_sliceFrames++;
			_frames++;
			if (_sliceFrames == SliceFrames)
				FinishSlice();
		}
	}

	public ProgrammeLoudnessSnapshot Read(string busId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(busId);
		var momentary = _sliceCount >= 4 ? Loudness(WindowEnergy(4)) : null;
		var shortTerm = _sliceCount >= ShortTermSlices ? Loudness(WindowEnergy(ShortTermSlices)) : null;
		var integrated = Integrated(out var relativeGate);
		var range = _completedSlices >= 300 && integrated.HasValue
			? LoudnessRange(integrated.Value - 20.0) : null;
		var truePeak = _truePeak > 0 ? 20 * Math.Log10(_truePeak) : (double?)null;
		var state = _discontinuity ? "UNVERIFIED" :
			_completedSlices < 4 ? "WARMING" :
			!integrated.HasValue ? "SILENCE" : "CURRENT";
		var policy = "NOT EVALUATED";
		if (state == "CURRENT" && _frames >= 10L * SampleRate)
		{
			policy = integrated!.Value > _policy.TargetLufs + _policy.ToleranceLu
				? "LOUDNESS HIGH"
				: integrated.Value < _policy.TargetLufs - _policy.ToleranceLu
					? "LOUDNESS LOW"
					: "WITHIN TARGET";
			if (truePeak.HasValue && truePeak.Value > _policy.MaximumTruePeakDbtp)
				policy = "TRUE PEAK EXCEEDS CEILING";
		}
		return new ProgrammeLoudnessSnapshot(
			ProgrammeLoudnessSnapshot.CurrentFormatVersion, _policy.ProfileId,
			state, SampleRate, 2, _frames / (double)SampleRate, _generation,
			momentary, shortTerm, integrated, range, truePeak, relativeGate, policy,
			"runtime:post-bus-dynamics-and-safety-clamp:" + busId,
			"UNVERIFIED");
	}

	private void FinishSlice()
	{
		_sliceEnergies[_sliceWriteIndex] = _sliceSum / SliceFrames;
		_sliceWriteIndex = (_sliceWriteIndex + 1) % ShortTermSlices;
		_sliceCount = Math.Min(ShortTermSlices, _sliceCount + 1);
		_completedSlices++;
		_sliceSum = 0;
		_sliceFrames = 0;

		if (_sliceCount >= 4)
		{
			var mean = WindowEnergy(4);
			var loudness = Loudness(mean);
			if (loudness.HasValue && loudness.Value > AbsoluteGateLufs)
			{
				var slot = Bin(loudness.Value);
				_integratedCounts[slot]++;
				_integratedEnergy[slot] += mean;
			}
		}
		if (_sliceCount == ShortTermSlices && _completedSlices % 10 == 0)
		{
			var loudness = Loudness(WindowEnergy(ShortTermSlices));
			if (loudness.HasValue && loudness.Value > AbsoluteGateLufs)
				_rangeCounts[Bin(loudness.Value)]++;
		}
	}

	private double WindowEnergy(int slices)
	{
		double total = 0;
		for (var index = 0; index < slices; index++)
		{
			var slot = (_sliceWriteIndex + ShortTermSlices - index - 1) % ShortTermSlices;
			total += _sliceEnergies[slot];
		}
		return total / slices;
	}

	private double? Integrated(out double? relativeThreshold)
	{
		long count = 0;
		double energy = 0;
		for (var index = 0; index < HistogramBins; index++)
		{
			count += _integratedCounts[index];
			energy += _integratedEnergy[index];
		}
		relativeThreshold = null;
		if (count == 0 || energy <= 0)
			return null;
		var absoluteGated = Loudness(energy / count);
		if (!absoluteGated.HasValue)
			return null;
		relativeThreshold = absoluteGated.Value - 10;
		var gatedMeanThreshold = Math.Pow(10, (relativeThreshold.Value + 0.691) / 10);
		long gatedCount = 0;
		double gatedEnergy = 0;
		for (var index = 0; index < HistogramBins; index++)
		{
			// A quantized bin is a preselection. Its stored exact mean-square
			// energy remains the value used in the final BS.1770 equation.
			if (_integratedCounts[index] == 0 ||
				_integratedEnergy[index] / _integratedCounts[index] <= gatedMeanThreshold)
				continue;
			gatedCount += _integratedCounts[index];
			gatedEnergy += _integratedEnergy[index];
		}
		return gatedCount == 0 ? null : Loudness(gatedEnergy / gatedCount);
	}

	private double? LoudnessRange(double relativeThreshold)
	{
		var floor = Math.Max(AbsoluteGateLufs, relativeThreshold);
		long count = 0;
		for (var index = 0; index < HistogramBins; index++)
			if (BinLoudness(index) > floor)
				count += _rangeCounts[index];
		if (count < 2)
			return null;
		var lowRank = (long)Math.Ceiling(count * 0.10);
		var highRank = (long)Math.Ceiling(count * 0.95);
		long seen = 0;
		double? lower = null;
		double? upper = null;
		for (var index = 0; index < HistogramBins; index++)
		{
			if (BinLoudness(index) <= floor)
				continue;
			seen += _rangeCounts[index];
			if (lower is null && seen >= lowRank)
				lower = BinLoudness(index);
			if (seen >= highRank)
			{
				upper = BinLoudness(index);
				break;
			}
		}
		return lower.HasValue && upper.HasValue ? Math.Max(0, upper.Value - lower.Value) : null;
	}

	private static double? Loudness(double meanSquared) =>
		meanSquared > 0 && double.IsFinite(meanSquared)
			? -0.691 + 10 * Math.Log10(meanSquared)
			: null;

	private static int Bin(double loudness) =>
		Math.Clamp((int)Math.Floor((loudness - HistogramMinLufs) / HistogramStep), 0, HistogramBins - 1);
	private static double BinLoudness(int bin) => HistogramMinLufs + (bin + 0.5) * HistogramStep;

	private void ObserveTruePeak(float left, float right)
	{
		_firLeft[_firPosition] = left;
		_firRight[_firPosition] = right;
		_firPosition = (_firPosition + 1) % FirTaps;
		_firCount = Math.Min(FirTaps, _firCount + 1);
		if (_firCount < FirTaps)
			return;
		for (var phase = 1; phase < 4; phase++)
		{
			double sampleLeft = 0;
			double sampleRight = 0;
			for (var tap = 0; tap < FirTaps; tap++)
			{
				var index = (_firPosition + tap) % FirTaps;
				var coefficient = Interpolator[phase, tap];
				sampleLeft += _firLeft[index] * coefficient;
				sampleRight += _firRight[index] * coefficient;
			}
			_truePeak = Math.Max(_truePeak, Math.Max(Math.Abs(sampleLeft), Math.Abs(sampleRight)));
		}
	}

	private static double[,] CreateInterpolator()
	{
		var values = new double[4, FirTaps];
		for (var phase = 0; phase < 4; phase++)
		{
			var sum = 0d;
			for (var tap = 0; tap < FirTaps; tap++)
			{
				var position = FirMiddle + phase / 4d - tap;
				var sinc = Math.Abs(position) < 1e-12
					? 1d : Math.Sin(Math.PI * position) / (Math.PI * position);
				var phaseWindow = 2 * Math.PI * tap / (FirTaps - 1);
				var window = 0.42 - 0.50 * Math.Cos(phaseWindow) + 0.08 * Math.Cos(phaseWindow * 2);
				var coefficient = sinc * window;
				values[phase, tap] = coefficient;
				sum += coefficient;
			}
			for (var tap = 0; tap < FirTaps; tap++)
				values[phase, tap] /= sum;
		}
		return values;
	}

	private static Biquad KWeightShelf() =>
		new(1.53512485958697, -2.69169618940638, 1.19839281085285,
			-1.69065929318241, 0.73248077421585);

	private static Biquad KWeightRlb() =>
		new(1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621);

	private struct Biquad(double b0, double b1, double b2, double a1, double a2)
	{
		private double _z1;
		private double _z2;
		public double Process(double input)
		{
			var output = b0 * input + _z1;
			_z1 = b1 * input - a1 * output + _z2;
			_z2 = b2 * input - a2 * output;
			return output;
		}
	}
}
