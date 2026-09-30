// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Media;

public readonly record struct AudioProductionSourceBuffer(
	MediaSourceId SourceId,
	ReadOnlyMemory<float> Samples,
	bool Available = true);

public readonly record struct AudioProductionBlockResult(
	AudioBusId BusId,
	ulong ConfigurationRevision,
	double LeftPeak,
	double RightPeak,
	double PreClipPeak,
	ulong ClippedSampleValues,
	double DuckingGain,
	double DuckingReduction,
	bool SidechainAvailable,
	double? CrossfadeProgress,
	int ActiveSourceCount,
	int MissingSourceCount)
{
	public double MasterPeak => Math.Max(LeftPeak, RightPeak);
	public bool Clipping => ClippedSampleValues > 0;
}

public sealed class AudioProductionEngine
{
	private readonly object _gate = new();
	private AudioProductionConfiguration _configuration;
	private double _duckingGain = 1;
	private uint _duckingHoldRemaining;

	public AudioProductionEngine(AudioProductionConfiguration configuration)
	{
		_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
	}

	public AudioProductionConfiguration Configuration
	{
		get
		{
			lock (_gate)
				return _configuration;
		}
	}

	public void ApplyConfiguration(AudioProductionConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		lock (_gate)
		{
			if (configuration.Revision < _configuration.Revision)
				throw new InvalidOperationException(
					$"Audio production revision cannot move backwards from '{_configuration.Revision}' to '{configuration.Revision}'.");

			var oldDucking = _configuration.Ducking;
			_configuration = configuration;
			if (configuration.Ducking is null ||
				oldDucking is null ||
				configuration.Ducking.SidechainSourceId != oldDucking.SidechainSourceId ||
				configuration.Ducking.BusId != oldDucking.BusId)
			{
				_duckingGain = 1;
				_duckingHoldRemaining = 0;
			}
		}
	}

	public AudioProductionBlockResult ProcessBus(
		AudioBusId busId,
		ulong samplePosition,
		uint sampleCount,
		MediaSourceId routedSourceId,
		ReadOnlySpan<AudioProductionSourceBuffer> sourceBuffers,
		Span<float> destination)
	{
		if (sampleCount == 0)
			throw new ArgumentOutOfRangeException(nameof(sampleCount));
		var requiredValues = checked((int)sampleCount * 2);
		if (destination.Length != requiredValues)
			throw new ArgumentException("Stereo destination length must exactly match sampleCount * 2.", nameof(destination));

		lock (_gate)
		{
			var configuration = _configuration;
			var bus = configuration.GetBus(busId);
			destination.Clear();

			var sidechainIndex = -1;
			var ducking = configuration.Ducking is { Enabled: true } configuredDucking &&
				configuredDucking.BusId == busId
					? configuredDucking
					: null;
			if (ducking is not null)
				sidechainIndex = FindBuffer(sourceBuffers, ducking.SidechainSourceId);

			var activeSources = 0;
			var missingSources = 0;
			for (var sourceIndex = 0; sourceIndex < configuration.Sources.Count; sourceIndex++)
			{
				var source = configuration.Sources[sourceIndex];
				if (!source.IsAssignedTo(busId))
					continue;
				if (source.FollowRoutedSource && source.SourceId != routedSourceId)
					continue;
				var bufferIndex = FindBuffer(sourceBuffers, source.SourceId);
				if (bufferIndex >= 0 && sourceBuffers[bufferIndex].Available)
					activeSources++;
				else
					missingSources++;
			}

			double leftPeak = 0;
			double rightPeak = 0;
			double preClipPeak = 0;
			ulong clippedValues = 0;
			double? finalCrossfadeProgress = null;
			var sidechainAvailable = ducking is null || (sidechainIndex >= 0 && sourceBuffers[sidechainIndex].Available);
			var masterGain = bus.Muted ? 0d : bus.MasterGain;

			for (var frame = 0; frame < sampleCount; frame++)
			{
				var absoluteSample = checked(samplePosition + (ulong)frame);
				var duckGain = AdvanceDucking(ducking, sidechainAvailable, sidechainIndex, sourceBuffers, frame);
				double left = 0;
				double right = 0;

				for (var sourceIndex = 0; sourceIndex < configuration.Sources.Count; sourceIndex++)
				{
					var source = configuration.Sources[sourceIndex];
					if (!source.IsAssignedTo(busId))
						continue;
					if (source.FollowRoutedSource && source.SourceId != routedSourceId)
						continue;

					var bufferIndex = FindBuffer(sourceBuffers, source.SourceId);
					if (bufferIndex < 0)
						continue;
					ref readonly var buffer = ref sourceBuffers[bufferIndex];
					if (!buffer.Available)
						continue;
					if (buffer.Samples.Length < requiredValues)
						throw new ArgumentException($"Audio source '{source.SourceId}' payload is shorter than the requested stereo block.", nameof(sourceBuffers));

					var gain = source.Muted ? 0d : source.Gain;
					if (configuration.Crossfade is { } crossfade && crossfade.BusId == busId)
					{
						var (fromGain, toGain, progress) = ResolveCrossfade(crossfade, absoluteSample);
						if (source.SourceId == crossfade.FromSourceId)
							gain *= fromGain;
						else if (source.SourceId == crossfade.ToSourceId)
							gain *= toGain;
						finalCrossfadeProgress = progress;
					}
					if (ducking is not null && ducking.Targets(source.SourceId))
						gain *= duckGain;

					var samples = buffer.Samples.Span;
					var offset = frame * 2;
					var sourceLeft = float.IsFinite(samples[offset]) ? samples[offset] : 0f;
					var sourceRight = float.IsFinite(samples[offset + 1]) ? samples[offset + 1] : 0f;
					left += sourceLeft * gain;
					right += sourceRight * gain;
				}

				left *= masterGain;
				right *= masterGain;
				preClipPeak = Math.Max(preClipPeak, Math.Max(Math.Abs(left), Math.Abs(right)));

				var clippedLeft = Clip(left, configuration.ClipStrategy, ref clippedValues);
				var clippedRight = Clip(right, configuration.ClipStrategy, ref clippedValues);
				var outputOffset = frame * 2;
				destination[outputOffset] = clippedLeft;
				destination[outputOffset + 1] = clippedRight;
				leftPeak = Math.Max(leftPeak, Math.Abs((double)clippedLeft));
				rightPeak = Math.Max(rightPeak, Math.Abs((double)clippedRight));
			}

			return new AudioProductionBlockResult(
				busId,
				configuration.Revision,
				leftPeak,
				rightPeak,
				preClipPeak,
				clippedValues,
				_duckingGain,
				1 - _duckingGain,
				sidechainAvailable,
				finalCrossfadeProgress,
				activeSources,
				missingSources);
		}
	}

	private double AdvanceDucking(
		AudioDuckingConfiguration? ducking,
		bool sidechainAvailable,
		int sidechainIndex,
		ReadOnlySpan<AudioProductionSourceBuffer> sourceBuffers,
		int frame)
	{
		if (ducking is null)
		{
			_duckingGain = 1;
			_duckingHoldRemaining = 0;
			return 1;
		}

		var active = false;
		if (sidechainAvailable)
		{
			var sidechain = sourceBuffers[sidechainIndex].Samples.Span;
			var offset = frame * 2;
			if (sidechain.Length > offset + 1)
			{
				var left = float.IsFinite(sidechain[offset]) ? Math.Abs((double)sidechain[offset]) : 0;
				var right = float.IsFinite(sidechain[offset + 1]) ? Math.Abs((double)sidechain[offset + 1]) : 0;
				active = Math.Max(left, right) >= ducking.Threshold;
			}
		}

		if (active)
		{
			_duckingHoldRemaining = ducking.HoldSamples;
			var step = (1 - ducking.Attenuation) / ducking.AttackSamples;
			_duckingGain = Math.Max(ducking.Attenuation, _duckingGain - step);
		}
		else if (_duckingHoldRemaining > 0)
		{
			_duckingHoldRemaining--;
		}
		else
		{
			var step = (1 - ducking.Attenuation) / ducking.ReleaseSamples;
			_duckingGain = Math.Min(1, _duckingGain + step);
		}

		return _duckingGain;
	}

	private static (double FromGain, double ToGain, double Progress) ResolveCrossfade(
		AudioCrossfadeConfiguration crossfade,
		ulong absoluteSample)
	{
		double progress;
		if (absoluteSample <= crossfade.StartSamplePosition)
			progress = 0;
		else
		{
			var offset = absoluteSample - crossfade.StartSamplePosition;
			progress = offset >= crossfade.DurationSamples
				? 1
				: offset / (double)crossfade.DurationSamples;
		}

		return crossfade.Law switch
		{
			AudioCrossfadeLaw.EqualPower => (
				Math.Cos(progress * Math.PI * 0.5),
				Math.Sin(progress * Math.PI * 0.5),
				progress),
			AudioCrossfadeLaw.Linear => (1 - progress, progress, progress),
			_ => throw new InvalidOperationException("Unsupported audio crossfade law.")
		};
	}

	private static float Clip(double value, AudioClipStrategy strategy, ref ulong clippedValues)
	{
		if (!double.IsFinite(value))
		{
			clippedValues++;
			return 0;
		}
		if (strategy != AudioClipStrategy.HardClip)
			throw new InvalidOperationException("Unsupported audio clipping strategy.");
		if (value > 1)
		{
			clippedValues++;
			return 1;
		}
		if (value < -1)
		{
			clippedValues++;
			return -1;
		}
		return (float)value;
	}

	private static int FindBuffer(ReadOnlySpan<AudioProductionSourceBuffer> buffers, MediaSourceId sourceId)
	{
		for (var index = 0; index < buffers.Length; index++)
		{
			if (buffers[index].SourceId == sourceId)
				return index;
		}
		return -1;
	}
}
