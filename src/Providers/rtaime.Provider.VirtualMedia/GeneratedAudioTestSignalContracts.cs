// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Provider.VirtualMedia;

public enum GeneratedAudioTestSignalMode
{
	Silence = 1,
	Tone = 2,
	StereoIdentification = 3,
	ChannelIdentification = 4,
	Pulse = 5
}

public sealed record GeneratedAudioTestSignalConfiguration
{
	public const double DefaultFrequencyHz = 1_000;
	public const double DefaultPeakLevel = 0.25;
	public const double MaximumPeakLevel = 0.5;

	public GeneratedAudioTestSignalConfiguration(
		AudioFormat format,
		GeneratedAudioTestSignalMode mode,
		double frequencyHz = DefaultFrequencyHz,
		double peakLevel = DefaultPeakLevel)
	{
		if (!Enum.IsDefined(typeof(GeneratedAudioTestSignalMode), mode))
			throw new ArgumentOutOfRangeException(nameof(mode));
		if (format.SampleFormat != AudioSampleFormat.Float32)
			throw new NotSupportedException("Generated audio test signals currently use the qualified Float32 audio path.");
		if (!double.IsFinite(frequencyHz) || frequencyHz <= 0 || frequencyHz >= format.SampleRate / 2d)
			throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Generated audio frequency must be finite, positive and below Nyquist.");
		if (!double.IsFinite(peakLevel) || peakLevel < 0 || peakLevel > MaximumPeakLevel)
			throw new ArgumentOutOfRangeException(nameof(peakLevel), $"Generated audio peak level must be in the inclusive range 0..{MaximumPeakLevel:0.##}.");
		if (mode == GeneratedAudioTestSignalMode.StereoIdentification &&
			(format.ChannelLayout != AudioChannelLayout.Stereo || format.ChannelCount != 2))
		{
			throw new ArgumentException("Stereo identification requires the existing Stereo/2-channel layout.", nameof(format));
		}

		Format = format;
		Mode = mode;
		FrequencyHz = frequencyHz;
		PeakLevel = peakLevel;
	}

	public AudioFormat Format { get; }
	public GeneratedAudioTestSignalMode Mode { get; }
	public double FrequencyHz { get; }
	public double PeakLevel { get; }

	public static GeneratedAudioTestSignalConfiguration Default(
		AudioFormat format,
		GeneratedAudioTestSignalMode mode) =>
		new(format, mode);
}

public readonly record struct GeneratedAudioTestSignalFrameInfo(
	GeneratedAudioTestSignalMode Mode,
	string ActiveChannel,
	double LeftPeakLevel,
	double RightPeakLevel)
{
	public double PeakLevel => Math.Max(LeftPeakLevel, RightPeakLevel);
}
