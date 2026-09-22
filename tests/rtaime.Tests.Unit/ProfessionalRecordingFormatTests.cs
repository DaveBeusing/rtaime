// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class ProfessionalRecordingFormatTests
{
	[Fact]
	public void Mp4_capability_defines_the_qualified_delivery_contract()
	{
		var capability = ProfessionalRecordingFormats.Mp4H264Aac;

		Assert.Equal("mp4-h264-aac", capability.Id);
		Assert.Equal("ISO Base Media File Format (MP4)", capability.Container);
		Assert.Equal("H.264/AVC", capability.VideoCodec);
		Assert.Equal("AAC-LC", capability.AudioCodec);
		Assert.Equal(".mp4", capability.FileExtension);
		Assert.Equal(20_000_000U, capability.VideoBitRate);
		Assert.Equal(192_000U, capability.AudioBitRate);
		Assert.Equal(AudioFormat.Stereo48kFloat32, capability.RequiredInputAudioFormat);
		Assert.Equal(
			new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
			capability.SupportedInputVideoFormats);
	}

	[Fact]
	public void Mp4_availability_is_fail_closed_outside_Windows()
	{
		var availability = ProfessionalRecordingFormats.GetMp4H264AacAvailability();

		Assert.Equal(ProfessionalRecordingFormats.Mp4H264Aac, availability.Capability);
		Assert.Equal(OperatingSystem.IsWindows(), availability.Available);
		if (!OperatingSystem.IsWindows())
			Assert.NotNull(availability.UnavailableReason);
	}

	[Fact]
	public void Mp4_writer_normalizes_only_the_mp4_target_extension()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-mp4-target", Guid.NewGuid().ToString("N"));
		var writer = new WindowsMediaFoundationMp4RecordingWriter(root);

		Assert.Equal("program.mp4", writer.ConfigureTarget(root, "program"));
		Assert.Equal("program.mp4", writer.ConfigureTarget(root, "program.mp4"));
		Assert.Throws<ArgumentException>(() => writer.ConfigureTarget(root, "program.mov"));
		Assert.Throws<ArgumentException>(() => writer.ConfigureTarget(root, Path.Combine("nested", "program.mp4")));
	}

	[Fact]
	public void Reference_writer_keeps_the_deterministic_reference_extension()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-reference-target", Guid.NewGuid().ToString("N"));
		var writer = new ReferenceRecordingPayloadWriter(root);

		Assert.Equal("evidence.rtaime-recording", writer.ConfigureTarget(root, "evidence"));
	}

	[Fact]
	public void Mp4_writer_rejects_invalid_failure_quota()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-mp4-quota", Guid.NewGuid().ToString("N"));

		Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsMediaFoundationMp4RecordingWriter(root, 0));
	}
}
