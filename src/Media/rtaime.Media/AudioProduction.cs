// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Media;

public readonly record struct AudioBusId
{
	public AudioBusId(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
			throw new ArgumentException("Audio bus identity is required and must not exceed 64 characters.", nameof(value));
		if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
			throw new ArgumentException("Audio bus identity may contain only ASCII letters, digits, dash, underscore and dot.", nameof(value));
		Value = value.Trim().ToLowerInvariant();
	}

	public string Value { get; }
	public override string ToString() => Value;

	public static AudioBusId Program { get; } = new("program");
}

public enum AudioCrossfadeLaw
{
	EqualPower = 1,
	Linear = 2
}

public enum AudioClipStrategy
{
	HardClip = 1
}

public sealed record AudioProductionBusConfiguration
{
	public AudioProductionBusConfiguration(AudioBusId busId, AudioGain masterGain, bool muted)
	{
		BusId = busId;
		MasterGain = masterGain;
		Muted = muted;
	}

	public AudioBusId BusId { get; }
	public AudioGain MasterGain { get; }
	public bool Muted { get; }
}

public sealed record AudioProductionSourceConfiguration
{
	private readonly AudioBusId[] _busAssignments;

	public AudioProductionSourceConfiguration(
		MediaSourceId sourceId,
		AudioGain gain,
		bool muted,
		bool followRoutedSource,
		IReadOnlyList<AudioBusId> busAssignments)
	{
		ArgumentNullException.ThrowIfNull(busAssignments);
		if (busAssignments.Count is 0 or > AudioProductionLimits.MaximumBuses)
			throw new ArgumentException("An audio source must be assigned to between one and the maximum supported audio buses.", nameof(busAssignments));
		if (busAssignments.Distinct().Count() != busAssignments.Count)
			throw new ArgumentException("Audio source bus assignments must be unique.", nameof(busAssignments));

		SourceId = sourceId;
		Gain = gain;
		Muted = muted;
		FollowRoutedSource = followRoutedSource;
		_busAssignments = busAssignments.ToArray();
	}

	public MediaSourceId SourceId { get; }
	public AudioGain Gain { get; }
	public bool Muted { get; }
	public bool FollowRoutedSource { get; }
	public IReadOnlyList<AudioBusId> BusAssignments => _busAssignments;

	public bool IsAssignedTo(AudioBusId busId) => Array.IndexOf(_busAssignments, busId) >= 0;
}

public sealed record AudioCrossfadeConfiguration
{
	public AudioCrossfadeConfiguration(
		AudioBusId busId,
		MediaSourceId fromSourceId,
		MediaSourceId toSourceId,
		ulong startSamplePosition,
		uint durationSamples,
		AudioCrossfadeLaw law = AudioCrossfadeLaw.EqualPower)
	{
		if (fromSourceId == toSourceId)
			throw new ArgumentException("Audio crossfade requires two different sources.", nameof(toSourceId));
		if (durationSamples == 0)
			throw new ArgumentOutOfRangeException(nameof(durationSamples));
		if (!Enum.IsDefined(law))
			throw new ArgumentOutOfRangeException(nameof(law));

		BusId = busId;
		FromSourceId = fromSourceId;
		ToSourceId = toSourceId;
		StartSamplePosition = startSamplePosition;
		DurationSamples = durationSamples;
		Law = law;
	}

	public AudioBusId BusId { get; }
	public MediaSourceId FromSourceId { get; }
	public MediaSourceId ToSourceId { get; }
	public ulong StartSamplePosition { get; }
	public uint DurationSamples { get; }
	public AudioCrossfadeLaw Law { get; }
}

public sealed record AudioDuckingConfiguration
{
	private readonly MediaSourceId[] _targetSourceIds;

	public AudioDuckingConfiguration(
		AudioBusId busId,
		bool enabled,
		MediaSourceId sidechainSourceId,
		IReadOnlyList<MediaSourceId> targetSourceIds,
		double threshold,
		double attenuation,
		uint attackSamples,
		uint holdSamples,
		uint releaseSamples)
	{
		ArgumentNullException.ThrowIfNull(targetSourceIds);
		if (targetSourceIds.Count is 0 or > AudioProductionLimits.MaximumSources)
			throw new ArgumentException("Ducking requires a bounded non-empty target source set.", nameof(targetSourceIds));
		if (targetSourceIds.Distinct().Count() != targetSourceIds.Count)
			throw new ArgumentException("Ducking target source identities must be unique.", nameof(targetSourceIds));
		if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(threshold));
		if (!double.IsFinite(attenuation) || attenuation is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(attenuation));
		if (attackSamples == 0)
			throw new ArgumentOutOfRangeException(nameof(attackSamples));
		if (releaseSamples == 0)
			throw new ArgumentOutOfRangeException(nameof(releaseSamples));

		BusId = busId;
		Enabled = enabled;
		SidechainSourceId = sidechainSourceId;
		_targetSourceIds = targetSourceIds.ToArray();
		Threshold = threshold;
		Attenuation = attenuation;
		AttackSamples = attackSamples;
		HoldSamples = holdSamples;
		ReleaseSamples = releaseSamples;
	}

	public AudioBusId BusId { get; }
	public bool Enabled { get; }
	public MediaSourceId SidechainSourceId { get; }
	public IReadOnlyList<MediaSourceId> TargetSourceIds => _targetSourceIds;
	public double Threshold { get; }
	public double Attenuation { get; }
	public uint AttackSamples { get; }
	public uint HoldSamples { get; }
	public uint ReleaseSamples { get; }

	public bool Targets(MediaSourceId sourceId) => Array.IndexOf(_targetSourceIds, sourceId) >= 0;
}

public static class AudioProductionLimits
{
	public const int MaximumSources = 8;
	public const int MaximumBuses = 4;
}

public sealed record AudioProductionConfiguration
{
	private readonly AudioProductionBusConfiguration[] _buses;
	private readonly AudioProductionSourceConfiguration[] _sources;

	public AudioProductionConfiguration(
		ulong revision,
		IReadOnlyList<AudioProductionBusConfiguration> buses,
		IReadOnlyList<AudioProductionSourceConfiguration> sources,
		AudioCrossfadeConfiguration? crossfade = null,
		AudioDuckingConfiguration? ducking = null,
		AudioClipStrategy clipStrategy = AudioClipStrategy.HardClip)
	{
		ArgumentNullException.ThrowIfNull(buses);
		ArgumentNullException.ThrowIfNull(sources);
		if (buses.Count is 0 or > AudioProductionLimits.MaximumBuses)
			throw new ArgumentException($"Audio production supports 1-{AudioProductionLimits.MaximumBuses} buses.", nameof(buses));
		if (sources.Count is 0 or > AudioProductionLimits.MaximumSources)
			throw new ArgumentException($"Audio production supports 1-{AudioProductionLimits.MaximumSources} sources.", nameof(sources));
		if (buses.Any(bus => bus is null) || sources.Any(source => source is null))
			throw new ArgumentException("Audio production configuration must not contain null entries.");
		if (buses.Select(bus => bus.BusId).Distinct().Count() != buses.Count)
			throw new ArgumentException("Audio bus identities must be unique.", nameof(buses));
		if (sources.Select(source => source.SourceId).Distinct().Count() != sources.Count)
			throw new ArgumentException("Audio source identities must be unique.", nameof(sources));
		if (!buses.Any(bus => bus.BusId == AudioBusId.Program))
			throw new ArgumentException("Audio production requires the Program bus.", nameof(buses));
		if (!Enum.IsDefined(clipStrategy))
			throw new ArgumentOutOfRangeException(nameof(clipStrategy));

		var busIds = buses.Select(bus => bus.BusId).ToHashSet();
		foreach (var source in sources)
		{
			if (source.BusAssignments.Any(busId => !busIds.Contains(busId)))
				throw new ArgumentException($"Audio source '{source.SourceId}' references an unknown bus.", nameof(sources));
		}

		if (crossfade is not null)
		{
			if (!busIds.Contains(crossfade.BusId))
				throw new ArgumentException("Audio crossfade references an unknown bus.", nameof(crossfade));
			if (!sources.Any(source => source.SourceId == crossfade.FromSourceId) ||
				!sources.Any(source => source.SourceId == crossfade.ToSourceId))
				throw new ArgumentException("Audio crossfade references an unknown source.", nameof(crossfade));
		}

		if (ducking is not null)
		{
			if (!busIds.Contains(ducking.BusId))
				throw new ArgumentException("Audio ducking references an unknown bus.", nameof(ducking));
			if (!sources.Any(source => source.SourceId == ducking.SidechainSourceId))
				throw new ArgumentException("Audio ducking sidechain references an unknown source.", nameof(ducking));
			if (ducking.TargetSourceIds.Any(target => !sources.Any(source => source.SourceId == target)))
				throw new ArgumentException("Audio ducking references an unknown target source.", nameof(ducking));
		}

		Revision = revision;
		_buses = buses.OrderBy(bus => bus.BusId.Value, StringComparer.Ordinal).ToArray();
		_sources = sources.OrderBy(source => source.SourceId.ToString(), StringComparer.Ordinal).ToArray();
		Crossfade = crossfade;
		Ducking = ducking;
		ClipStrategy = clipStrategy;
	}

	public ulong Revision { get; }
	public IReadOnlyList<AudioProductionBusConfiguration> Buses => _buses;
	public IReadOnlyList<AudioProductionSourceConfiguration> Sources => _sources;
	public AudioCrossfadeConfiguration? Crossfade { get; }
	public AudioDuckingConfiguration? Ducking { get; }
	public AudioClipStrategy ClipStrategy { get; }

	public AudioProductionBusConfiguration GetBus(AudioBusId busId) =>
		Array.Find(_buses, bus => bus.BusId == busId)
		?? throw new KeyNotFoundException($"Unknown audio bus '{busId}'.");

	public AudioProductionSourceConfiguration GetSource(MediaSourceId sourceId) =>
		Array.Find(_sources, source => source.SourceId == sourceId)
		?? throw new KeyNotFoundException($"Unknown audio production source '{sourceId}'.");

	public static AudioProductionConfiguration CreateLegacyCompatible(IReadOnlyList<MediaSourceId> sources)
	{
		ArgumentNullException.ThrowIfNull(sources);
		if (sources.Count is 0 or > AudioProductionLimits.MaximumSources)
			throw new ArgumentException($"Legacy-compatible audio production requires 1-{AudioProductionLimits.MaximumSources} sources.", nameof(sources));

		return new AudioProductionConfiguration(
			0,
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, AudioGain.Unity, muted: false) },
			sources.Select(sourceId =>
				new AudioProductionSourceConfiguration(
					sourceId,
					AudioGain.Unity,
					muted: false,
					followRoutedSource: true,
					new[] { AudioBusId.Program }))
				.ToArray());
	}
}

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
			var masterGain = bus.Muted ? 0d : bus.MasterGain.Linear;

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

					var gain = source.Muted ? 0d : source.Gain.Linear;
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
