// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Media.Contracts;

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

public static class AudioEqualizerLimits
{
	public const double MinimumFrequencyHz = 20d;
	public const double MaximumFrequencyHz = 20_000d;
	public const double MinimumGainDb = -18d;
	public const double MaximumGainDb = 18d;
	public const double MinimumBellQ = 0.1d;
	public const double MaximumBellQ = 10d;
}

public sealed record AudioLowShelfEqualizerBand
{
	public AudioLowShelfEqualizerBand(bool enabled, double frequencyHz, double gainDb)
	{
		ValidateFrequency(frequencyHz);
		ValidateGain(gainDb);
		Enabled = enabled;
		FrequencyHz = frequencyHz;
		GainDb = gainDb;
	}

	public bool Enabled { get; }
	public double FrequencyHz { get; }
	public double GainDb { get; }

	private static void ValidateFrequency(double value)
	{
		if (!double.IsFinite(value) || value is < AudioEqualizerLimits.MinimumFrequencyHz or > AudioEqualizerLimits.MaximumFrequencyHz)
			throw new ArgumentOutOfRangeException(nameof(value), $"EQ frequency must be finite and in the inclusive range {AudioEqualizerLimits.MinimumFrequencyHz}..{AudioEqualizerLimits.MaximumFrequencyHz} Hz.");
	}

	private static void ValidateGain(double value)
	{
		if (!double.IsFinite(value) || value is < AudioEqualizerLimits.MinimumGainDb or > AudioEqualizerLimits.MaximumGainDb)
			throw new ArgumentOutOfRangeException(nameof(value), $"EQ gain must be finite and in the inclusive range {AudioEqualizerLimits.MinimumGainDb}..{AudioEqualizerLimits.MaximumGainDb} dB.");
	}
}

public sealed record AudioBellEqualizerBand
{
	public AudioBellEqualizerBand(bool enabled, double frequencyHz, double gainDb, double q)
	{
		if (!double.IsFinite(frequencyHz) || frequencyHz is < AudioEqualizerLimits.MinimumFrequencyHz or > AudioEqualizerLimits.MaximumFrequencyHz)
			throw new ArgumentOutOfRangeException(nameof(frequencyHz), $"EQ frequency must be finite and in the inclusive range {AudioEqualizerLimits.MinimumFrequencyHz}..{AudioEqualizerLimits.MaximumFrequencyHz} Hz.");
		if (!double.IsFinite(gainDb) || gainDb is < AudioEqualizerLimits.MinimumGainDb or > AudioEqualizerLimits.MaximumGainDb)
			throw new ArgumentOutOfRangeException(nameof(gainDb), $"EQ gain must be finite and in the inclusive range {AudioEqualizerLimits.MinimumGainDb}..{AudioEqualizerLimits.MaximumGainDb} dB.");
		if (!double.IsFinite(q) || q is < AudioEqualizerLimits.MinimumBellQ or > AudioEqualizerLimits.MaximumBellQ)
			throw new ArgumentOutOfRangeException(nameof(q), $"EQ Q must be finite and in the inclusive range {AudioEqualizerLimits.MinimumBellQ}..{AudioEqualizerLimits.MaximumBellQ}.");
		Enabled = enabled;
		FrequencyHz = frequencyHz;
		GainDb = gainDb;
		Q = q;
	}

	public bool Enabled { get; }
	public double FrequencyHz { get; }
	public double GainDb { get; }
	public double Q { get; }
}

public sealed record AudioHighShelfEqualizerBand
{
	public AudioHighShelfEqualizerBand(bool enabled, double frequencyHz, double gainDb)
	{
		if (!double.IsFinite(frequencyHz) || frequencyHz is < AudioEqualizerLimits.MinimumFrequencyHz or > AudioEqualizerLimits.MaximumFrequencyHz)
			throw new ArgumentOutOfRangeException(nameof(frequencyHz), $"EQ frequency must be finite and in the inclusive range {AudioEqualizerLimits.MinimumFrequencyHz}..{AudioEqualizerLimits.MaximumFrequencyHz} Hz.");
		if (!double.IsFinite(gainDb) || gainDb is < AudioEqualizerLimits.MinimumGainDb or > AudioEqualizerLimits.MaximumGainDb)
			throw new ArgumentOutOfRangeException(nameof(gainDb), $"EQ gain must be finite and in the inclusive range {AudioEqualizerLimits.MinimumGainDb}..{AudioEqualizerLimits.MaximumGainDb} dB.");
		Enabled = enabled;
		FrequencyHz = frequencyHz;
		GainDb = gainDb;
	}

	public bool Enabled { get; }
	public double FrequencyHz { get; }
	public double GainDb { get; }
}

public sealed record AudioSourceEqualizerConfiguration
{
	public AudioSourceEqualizerConfiguration(
		AudioLowShelfEqualizerBand lowShelf,
		AudioBellEqualizerBand mid,
		AudioHighShelfEqualizerBand highShelf)
	{
		LowShelf = lowShelf ?? throw new ArgumentNullException(nameof(lowShelf));
		Mid = mid ?? throw new ArgumentNullException(nameof(mid));
		HighShelf = highShelf ?? throw new ArgumentNullException(nameof(highShelf));
	}

	public AudioLowShelfEqualizerBand LowShelf { get; }
	public AudioBellEqualizerBand Mid { get; }
	public AudioHighShelfEqualizerBand HighShelf { get; }

	public bool HasActiveProcessing =>
		(LowShelf.Enabled && LowShelf.GainDb != 0d) ||
		(Mid.Enabled && Mid.GainDb != 0d) ||
		(HighShelf.Enabled && HighShelf.GainDb != 0d);
}

public static class AudioDynamicsLimits
{
	public const double MinimumCompressorThresholdDbFs = -60d;
	public const double MaximumCompressorThresholdDbFs = 0d;
	public const double MinimumCompressorRatio = 1d;
	public const double MaximumCompressorRatio = 20d;
	public const double MinimumAttackMilliseconds = 0.1d;
	public const double MaximumAttackMilliseconds = 200d;
	public const double MinimumReleaseMilliseconds = 5d;
	public const double MaximumReleaseMilliseconds = 5_000d;
	public const double MinimumMakeupGainDb = 0d;
	public const double MaximumMakeupGainDb = 24d;
	public const double MinimumLimiterCeilingDbFs = -24d;
	public const double MaximumLimiterCeilingDbFs = 0d;
}

public sealed record AudioBusCompressorConfiguration
{
	public AudioBusCompressorConfiguration(
		bool enabled,
		double thresholdDbFs,
		double ratio,
		double attackMilliseconds,
		double releaseMilliseconds,
		double makeupGainDb)
	{
		if (!double.IsFinite(thresholdDbFs) ||
			thresholdDbFs is < AudioDynamicsLimits.MinimumCompressorThresholdDbFs or > AudioDynamicsLimits.MaximumCompressorThresholdDbFs)
			throw new ArgumentOutOfRangeException(nameof(thresholdDbFs), $"Compressor threshold must be finite and in the inclusive range {AudioDynamicsLimits.MinimumCompressorThresholdDbFs}..{AudioDynamicsLimits.MaximumCompressorThresholdDbFs} dBFS.");
		if (!double.IsFinite(ratio) ||
			ratio is < AudioDynamicsLimits.MinimumCompressorRatio or > AudioDynamicsLimits.MaximumCompressorRatio)
			throw new ArgumentOutOfRangeException(nameof(ratio), $"Compressor ratio must be finite and in the inclusive range {AudioDynamicsLimits.MinimumCompressorRatio}..{AudioDynamicsLimits.MaximumCompressorRatio}.");
		if (!double.IsFinite(attackMilliseconds) ||
			attackMilliseconds is < AudioDynamicsLimits.MinimumAttackMilliseconds or > AudioDynamicsLimits.MaximumAttackMilliseconds)
			throw new ArgumentOutOfRangeException(nameof(attackMilliseconds), $"Compressor attack must be finite and in the inclusive range {AudioDynamicsLimits.MinimumAttackMilliseconds}..{AudioDynamicsLimits.MaximumAttackMilliseconds} ms.");
		if (!double.IsFinite(releaseMilliseconds) ||
			releaseMilliseconds is < AudioDynamicsLimits.MinimumReleaseMilliseconds or > AudioDynamicsLimits.MaximumReleaseMilliseconds)
			throw new ArgumentOutOfRangeException(nameof(releaseMilliseconds), $"Compressor release must be finite and in the inclusive range {AudioDynamicsLimits.MinimumReleaseMilliseconds}..{AudioDynamicsLimits.MaximumReleaseMilliseconds} ms.");
		if (!double.IsFinite(makeupGainDb) ||
			makeupGainDb is < AudioDynamicsLimits.MinimumMakeupGainDb or > AudioDynamicsLimits.MaximumMakeupGainDb)
			throw new ArgumentOutOfRangeException(nameof(makeupGainDb), $"Compressor makeup gain must be finite and in the inclusive range {AudioDynamicsLimits.MinimumMakeupGainDb}..{AudioDynamicsLimits.MaximumMakeupGainDb} dB.");

		Enabled = enabled;
		ThresholdDbFs = thresholdDbFs;
		Ratio = ratio;
		AttackMilliseconds = attackMilliseconds;
		ReleaseMilliseconds = releaseMilliseconds;
		MakeupGainDb = makeupGainDb;
	}

	public bool Enabled { get; }
	public double ThresholdDbFs { get; }
	public double Ratio { get; }
	public double AttackMilliseconds { get; }
	public double ReleaseMilliseconds { get; }
	public double MakeupGainDb { get; }
}

public sealed record AudioBusSamplePeakLimiterConfiguration
{
	public AudioBusSamplePeakLimiterConfiguration(bool enabled, double ceilingDbFs, double releaseMilliseconds)
	{
		if (!double.IsFinite(ceilingDbFs) ||
			ceilingDbFs is < AudioDynamicsLimits.MinimumLimiterCeilingDbFs or > AudioDynamicsLimits.MaximumLimiterCeilingDbFs)
			throw new ArgumentOutOfRangeException(nameof(ceilingDbFs), $"Sample-peak limiter ceiling must be finite and in the inclusive range {AudioDynamicsLimits.MinimumLimiterCeilingDbFs}..{AudioDynamicsLimits.MaximumLimiterCeilingDbFs} dBFS.");
		if (!double.IsFinite(releaseMilliseconds) ||
			releaseMilliseconds is < AudioDynamicsLimits.MinimumReleaseMilliseconds or > AudioDynamicsLimits.MaximumReleaseMilliseconds)
			throw new ArgumentOutOfRangeException(nameof(releaseMilliseconds), $"Sample-peak limiter release must be finite and in the inclusive range {AudioDynamicsLimits.MinimumReleaseMilliseconds}..{AudioDynamicsLimits.MaximumReleaseMilliseconds} ms.");

		Enabled = enabled;
		CeilingDbFs = ceilingDbFs;
		ReleaseMilliseconds = releaseMilliseconds;
	}

	public bool Enabled { get; }
	public double CeilingDbFs { get; }
	public double ReleaseMilliseconds { get; }
}

public sealed record AudioBusDynamicsConfiguration
{
	public AudioBusDynamicsConfiguration(
		AudioBusCompressorConfiguration compressor,
		AudioBusSamplePeakLimiterConfiguration limiter)
	{
		Compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));
		Limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
	}

	public AudioBusCompressorConfiguration Compressor { get; }
	public AudioBusSamplePeakLimiterConfiguration Limiter { get; }
	public bool HasActiveProcessing => Compressor.Enabled || Limiter.Enabled;
}

public sealed record AudioProductionBusConfiguration
{
	public AudioProductionBusConfiguration(
		AudioBusId busId,
		double masterGain,
		bool muted,
		AudioBusDynamicsConfiguration? dynamics = null)
	{
		if (!double.IsFinite(masterGain) || masterGain is < 0 or > 4)
			throw new ArgumentOutOfRangeException(nameof(masterGain), "Audio bus master gain must be finite and in the inclusive range 0..4.");
		BusId = busId;
		MasterGain = masterGain;
		Muted = muted;
		Dynamics = dynamics;
	}

	public AudioBusId BusId { get; }
	public double MasterGain { get; }
	public bool Muted { get; }
	public AudioBusDynamicsConfiguration? Dynamics { get; }
}

public sealed record AudioProductionSourceConfiguration
{
	private readonly AudioBusId[] _busAssignments;

	public AudioProductionSourceConfiguration(
		MediaSourceId sourceId,
		double gain,
		bool muted,
		bool followRoutedSource,
		IReadOnlyList<AudioBusId> busAssignments,
		AudioSourceEqualizerConfiguration? equalizer = null)
	{
		ArgumentNullException.ThrowIfNull(busAssignments);
		if (busAssignments.Count > AudioProductionLimits.MaximumBuses)
			throw new ArgumentException("An audio source may be assigned to at most the maximum supported audio buses.", nameof(busAssignments));
		if (busAssignments.Distinct().Count() != busAssignments.Count)
			throw new ArgumentException("Audio source bus assignments must be unique.", nameof(busAssignments));

		if (!double.IsFinite(gain) || gain is < 0 or > 4)
			throw new ArgumentOutOfRangeException(nameof(gain), "Audio source gain must be finite and in the inclusive range 0..4.");

		SourceId = sourceId;
		Gain = gain;
		Muted = muted;
		FollowRoutedSource = followRoutedSource;
		_busAssignments = busAssignments.ToArray();
		Equalizer = equalizer;
	}

	public MediaSourceId SourceId { get; }
	public double Gain { get; }
	public bool Muted { get; }
	public bool FollowRoutedSource { get; }
	public IReadOnlyList<AudioBusId> BusAssignments => _busAssignments;
	public AudioSourceEqualizerConfiguration? Equalizer { get; }

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
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 1d, muted: false) },
			sources.Select(sourceId =>
				new AudioProductionSourceConfiguration(
					sourceId,
					1d,
					muted: false,
					followRoutedSource: true,
					new[] { AudioBusId.Program }))
				.ToArray());
	}
}

