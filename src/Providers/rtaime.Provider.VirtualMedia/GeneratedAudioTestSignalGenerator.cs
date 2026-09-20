// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Provider.VirtualMedia;

/// <summary>
/// Deterministic generated audio source driven exclusively by authoritative audio sample timing.
/// The generator writes into caller-owned storage and performs no per-buffer managed allocation.
/// </summary>
public sealed class GeneratedAudioTestSignalGenerator
{
	private const uint IdentificationSegmentSeconds = 1;
	private const uint PulsePeriodSeconds = 1;
	private const uint PulseDurationDivisor = 100;

	private readonly GeneratedAudioTestSignalConfiguration _configuration;
	private readonly int _channelCount;
	private readonly double _phaseIncrement;
	private readonly ulong _identificationSegmentSamples;
	private readonly ulong _pulsePeriodSamples;
	private readonly ulong _pulseDurationSamples;

	public GeneratedAudioTestSignalGenerator(GeneratedAudioTestSignalConfiguration configuration)
	{
		_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		_channelCount = checked((int)configuration.Format.ChannelCount);
		_phaseIncrement = Math.Tau * configuration.FrequencyHz / configuration.Format.SampleRate;
		_identificationSegmentSamples = checked((ulong)configuration.Format.SampleRate * IdentificationSegmentSeconds);
		_pulsePeriodSamples = checked((ulong)configuration.Format.SampleRate * PulsePeriodSeconds);
		_pulseDurationSamples = Math.Max(1UL, configuration.Format.SampleRate / PulseDurationDivisor);
	}

	public GeneratedAudioTestSignalConfiguration Configuration => _configuration;

	public GeneratedAudioTestSignalFrameInfo FillInterleavedFloat32(
		AudioBufferTiming timing,
		Span<float> destination)
	{
		var requiredValues = checked((int)timing.SampleCount * _channelCount);
		if (destination.Length != requiredValues)
			throw new ArgumentException("Generated audio destination length does not match the requested sample window.", nameof(destination));

		destination.Clear();
		var leftPeak = 0d;
		var rightPeak = 0d;
		var activeChannel = ResolveActiveChannelLabel(timing.SamplePosition);

		for (var sampleIndex = 0; sampleIndex < timing.SampleCount; sampleIndex++)
		{
			var absoluteSample = timing.SamplePosition + sampleIndex;
			var value = SignalValue(absoluteSample);
			var baseOffset = checked((int)sampleIndex * _channelCount);

			switch (_configuration.Mode)
			{
				case GeneratedAudioTestSignalMode.Silence:
					break;
				case GeneratedAudioTestSignalMode.Tone:
				case GeneratedAudioTestSignalMode.Pulse:
					for (var channel = 0; channel < _channelCount; channel++)
						destination[baseOffset + channel] = value;
					break;
				case GeneratedAudioTestSignalMode.StereoIdentification:
					FillStereoIdentification(absoluteSample, destination, baseOffset, value);
					break;
				case GeneratedAudioTestSignalMode.ChannelIdentification:
					FillChannelIdentification(absoluteSample, destination, baseOffset, value);
					break;
				default:
					throw new InvalidOperationException("Generated audio test signal mode is unsupported.");
			}

			if (_channelCount >= 1)
				leftPeak = Math.Max(leftPeak, Math.Abs(destination[baseOffset]));
			if (_channelCount >= 2)
				rightPeak = Math.Max(rightPeak, Math.Abs(destination[baseOffset + 1]));
			else
				rightPeak = leftPeak;
		}

		return new GeneratedAudioTestSignalFrameInfo(
			_configuration.Mode,
			activeChannel,
			leftPeak,
			rightPeak);
	}

	public GeneratedAudioTestSignalFrameInfo Inspect(AudioBufferTiming timing)
	{
		var activeChannel = ResolveActiveChannelLabel(timing.SamplePosition);
		var peak = _configuration.Mode == GeneratedAudioTestSignalMode.Silence ? 0d : _configuration.PeakLevel;
		var left = 0d;
		var right = 0d;

		switch (_configuration.Mode)
		{
			case GeneratedAudioTestSignalMode.Silence:
				break;
			case GeneratedAudioTestSignalMode.Tone:
			case GeneratedAudioTestSignalMode.Pulse:
				left = peak;
				right = _channelCount >= 2 ? peak : left;
				break;
			case GeneratedAudioTestSignalMode.StereoIdentification:
				(left, right) = StereoIdentificationPeaks(timing.SamplePosition, peak);
				break;
			case GeneratedAudioTestSignalMode.ChannelIdentification:
				var channel = ChannelIdentificationIndex(timing.SamplePosition);
				left = channel == 0 ? peak : 0;
				right = _channelCount == 1 ? left : channel == 1 ? peak : 0;
				break;
		}

		return new GeneratedAudioTestSignalFrameInfo(_configuration.Mode, activeChannel, left, right);
	}

	private float SignalValue(ulong absoluteSample)
	{
		if (_configuration.Mode == GeneratedAudioTestSignalMode.Silence)
			return 0f;
		if (_configuration.Mode == GeneratedAudioTestSignalMode.Pulse &&
			absoluteSample % _pulsePeriodSamples >= _pulseDurationSamples)
		{
			return 0f;
		}

		var phase = _phaseIncrement * absoluteSample;
		return checked((float)(_configuration.PeakLevel * Math.Sin(phase)));
	}

	private void FillStereoIdentification(
		ulong absoluteSample,
		Span<float> destination,
		int offset,
		float value)
	{
		switch ((absoluteSample / _identificationSegmentSamples) % 3UL)
		{
			case 0:
				destination[offset] = value;
				break;
			case 1:
				destination[offset + 1] = value;
				break;
			default:
				destination[offset] = value;
				destination[offset + 1] = value;
				break;
		}
	}

	private void FillChannelIdentification(
		ulong absoluteSample,
		Span<float> destination,
		int offset,
		float value)
	{
		var channel = ChannelIdentificationIndex(absoluteSample);
		destination[offset + channel] = value;
	}

	private int ChannelIdentificationIndex(ulong absoluteSample) =>
		checked((int)((absoluteSample / _identificationSegmentSamples) % (ulong)_channelCount));

	private string ResolveActiveChannelLabel(ulong absoluteSample) =>
		_configuration.Mode switch
		{
			GeneratedAudioTestSignalMode.Silence => "NONE",
			GeneratedAudioTestSignalMode.Tone => "ALL",
			GeneratedAudioTestSignalMode.Pulse => "ALL",
			GeneratedAudioTestSignalMode.StereoIdentification =>
				((absoluteSample / _identificationSegmentSamples) % 3UL) switch
				{
					0 => "LEFT",
					1 => "RIGHT",
					_ => "BOTH"
				},
			GeneratedAudioTestSignalMode.ChannelIdentification when _channelCount == 1 => "MONO",
			GeneratedAudioTestSignalMode.ChannelIdentification when ChannelIdentificationIndex(absoluteSample) == 0 => "LEFT",
			GeneratedAudioTestSignalMode.ChannelIdentification => "RIGHT",
			_ => "NONE"
		};

	private (double Left, double Right) StereoIdentificationPeaks(ulong absoluteSample, double peak) =>
		((absoluteSample / _identificationSegmentSamples) % 3UL) switch
		{
			0 => (peak, 0),
			1 => (0, peak),
			_ => (peak, peak)
		};
}
