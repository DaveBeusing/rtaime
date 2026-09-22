// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

public sealed record ProgramRecordingFormatCapability(
	string Id,
	string Container,
	string VideoCodec,
	string VideoProfile,
	string AudioCodec,
	string FileExtension,
	IReadOnlyList<VideoFormat> SupportedInputVideoFormats,
	AudioFormat RequiredInputAudioFormat,
	uint VideoBitRate,
	uint AudioBitRate);

public sealed record ProgramRecordingFormatAvailability(
	ProgramRecordingFormatCapability Capability,
	bool Available,
	string? UnavailableReason);

public interface IProgramRecordingFormatCapabilityProvider
{
	ProgramRecordingFormatAvailability FormatAvailability { get; }
}

public static class ProfessionalRecordingFormats
{
	private static readonly ReadOnlyCollection<VideoFormat> Mp4InputFormats =
		Array.AsReadOnly(new[]
		{
			VideoFormat.Hd1080p50Rgba8,
			VideoFormat.Hd1080p59_94Rgba8
		});

	public static ProgramRecordingFormatCapability Mp4H264Aac { get; } =
		new(
			"mp4-h264-aac",
			"ISO Base Media File Format (MP4)",
			"H.264/AVC",
			"Main",
			"AAC-LC",
			".mp4",
			Mp4InputFormats,
			AudioFormat.Stereo48kFloat32,
			20_000_000,
			192_000);

	public static ProgramRecordingFormatAvailability GetMp4H264AacAvailability() =>
		OperatingSystem.IsWindows()
			? new(Mp4H264Aac, true, null)
			: new(
				Mp4H264Aac,
				false,
				"MP4 H.264/AAC recording requires Windows Media Foundation.");
}
