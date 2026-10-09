// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Media;

internal sealed class BoundedParametricEqualizerState
{
	private const double SampleRate = 48_000d;
	private readonly BiquadCoefficients _lowShelf;
	private readonly BiquadCoefficients _mid;
	private readonly BiquadCoefficients _highShelf;
	private StereoBiquadState _lowState;
	private StereoBiquadState _midState;
	private StereoBiquadState _highState;
	private StereoBiquadState _blockStartLowState;
	private StereoBiquadState _blockStartMidState;
	private StereoBiquadState _blockStartHighState;
	private StereoBiquadState _workingLowState;
	private StereoBiquadState _workingMidState;
	private StereoBiquadState _workingHighState;

	public BoundedParametricEqualizerState(
		MediaSourceId sourceId,
		AudioSourceEqualizerConfiguration? configuration)
	{
		SourceId = sourceId;
		Configuration = configuration;
		IsActive = configuration?.HasActiveProcessing == true;
		_lowShelf = configuration is null
			? BiquadCoefficients.Bypass
			: BiquadCoefficients.LowShelf(configuration.LowShelf);
		_mid = configuration is null
			? BiquadCoefficients.Bypass
			: BiquadCoefficients.Peaking(configuration.Mid);
		_highShelf = configuration is null
			? BiquadCoefficients.Bypass
			: BiquadCoefficients.HighShelf(configuration.HighShelf);
	}

	public MediaSourceId SourceId { get; }
	public AudioSourceEqualizerConfiguration? Configuration { get; }
	public bool IsActive { get; }

	public void CaptureBlockStart()
	{
		_blockStartLowState = _lowState;
		_blockStartMidState = _midState;
		_blockStartHighState = _highState;
	}

	public void BeginPass()
	{
		_workingLowState = _blockStartLowState;
		_workingMidState = _blockStartMidState;
		_workingHighState = _blockStartHighState;
	}

	public (double Left, double Right) Process(double left, double right)
	{
		(left, right) = ProcessStereo(_lowShelf, ref _workingLowState, left, right);
		(left, right) = ProcessStereo(_mid, ref _workingMidState, left, right);
		(left, right) = ProcessStereo(_highShelf, ref _workingHighState, left, right);
		return (left, right);
	}

	public void CommitPass()
	{
		_lowState = _workingLowState;
		_midState = _workingMidState;
		_highState = _workingHighState;
	}

	private static (double Left, double Right) ProcessStereo(
		BiquadCoefficients coefficients,
		ref StereoBiquadState state,
		double left,
		double right)
	{
		if (!coefficients.Enabled)
			return (left, right);

		return (
			ProcessSample(coefficients, ref state.LeftZ1, ref state.LeftZ2, left),
			ProcessSample(coefficients, ref state.RightZ1, ref state.RightZ2, right));
	}

	private static double ProcessSample(
		BiquadCoefficients coefficients,
		ref double z1,
		ref double z2,
		double input)
	{
		var output = (coefficients.B0 * input) + z1;
		z1 = (coefficients.B1 * input) - (coefficients.A1 * output) + z2;
		z2 = (coefficients.B2 * input) - (coefficients.A2 * output);
		return double.IsFinite(output) ? output : 0d;
	}

	private struct StereoBiquadState
	{
		public double LeftZ1;
		public double LeftZ2;
		public double RightZ1;
		public double RightZ2;
	}

	private readonly record struct BiquadCoefficients(
		bool Enabled,
		double B0,
		double B1,
		double B2,
		double A1,
		double A2)
	{
		public static BiquadCoefficients Bypass { get; } = new(false, 1d, 0d, 0d, 0d, 0d);

		public static BiquadCoefficients LowShelf(AudioLowShelfEqualizerBand band)
		{
			if (!band.Enabled || band.GainDb == 0d)
				return Bypass;

			var a = Math.Pow(10d, band.GainDb / 40d);
			var omega = 2d * Math.PI * band.FrequencyHz / SampleRate;
			var cosine = Math.Cos(omega);
			var alpha = Math.Sin(omega) * Math.Sqrt(2d) / 2d;
			var beta = 2d * Math.Sqrt(a) * alpha;
			return Normalize(
				a * ((a + 1d) - ((a - 1d) * cosine) + beta),
				2d * a * ((a - 1d) - ((a + 1d) * cosine)),
				a * ((a + 1d) - ((a - 1d) * cosine) - beta),
				(a + 1d) + ((a - 1d) * cosine) + beta,
				-2d * ((a - 1d) + ((a + 1d) * cosine)),
				(a + 1d) + ((a - 1d) * cosine) - beta);
		}

		public static BiquadCoefficients Peaking(AudioBellEqualizerBand band)
		{
			if (!band.Enabled || band.GainDb == 0d)
				return Bypass;

			var a = Math.Pow(10d, band.GainDb / 40d);
			var omega = 2d * Math.PI * band.FrequencyHz / SampleRate;
			var cosine = Math.Cos(omega);
			var alpha = Math.Sin(omega) / (2d * band.Q);
			return Normalize(
				1d + (alpha * a),
				-2d * cosine,
				1d - (alpha * a),
				1d + (alpha / a),
				-2d * cosine,
				1d - (alpha / a));
		}

		public static BiquadCoefficients HighShelf(AudioHighShelfEqualizerBand band)
		{
			if (!band.Enabled || band.GainDb == 0d)
				return Bypass;

			var a = Math.Pow(10d, band.GainDb / 40d);
			var omega = 2d * Math.PI * band.FrequencyHz / SampleRate;
			var cosine = Math.Cos(omega);
			var alpha = Math.Sin(omega) * Math.Sqrt(2d) / 2d;
			var beta = 2d * Math.Sqrt(a) * alpha;
			return Normalize(
				a * ((a + 1d) + ((a - 1d) * cosine) + beta),
				-2d * a * ((a - 1d) + ((a + 1d) * cosine)),
				a * ((a + 1d) + ((a - 1d) * cosine) - beta),
				(a + 1d) - ((a - 1d) * cosine) + beta,
				2d * ((a - 1d) - ((a + 1d) * cosine)),
				(a + 1d) - ((a - 1d) * cosine) - beta);
		}

		private static BiquadCoefficients Normalize(
			double b0,
			double b1,
			double b2,
			double a0,
			double a1,
			double a2)
		{
			if (!double.IsFinite(a0) || Math.Abs(a0) <= double.Epsilon)
				throw new ArgumentOutOfRangeException(nameof(a0), "EQ coefficient normalization denominator must be finite and non-zero.");

			var coefficients = new BiquadCoefficients(
				true,
				b0 / a0,
				b1 / a0,
				b2 / a0,
				a1 / a0,
				a2 / a0);
			coefficients.Validate();
			return coefficients;
		}

		private void Validate()
		{
			if (!double.IsFinite(B0) || !double.IsFinite(B1) || !double.IsFinite(B2) ||
				!double.IsFinite(A1) || !double.IsFinite(A2))
			{
				throw new ArgumentOutOfRangeException(nameof(B0), "EQ coefficients must be finite.");
			}

			if (1d + A1 + A2 <= 0d || 1d - A1 + A2 <= 0d || 1d - A2 <= 0d)
				throw new ArgumentOutOfRangeException(nameof(A1), "EQ denominator is not a stable second-order section.");
		}
	}
}
