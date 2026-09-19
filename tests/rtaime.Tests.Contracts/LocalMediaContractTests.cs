// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class LocalMediaContractTests
{
	[Fact]
	public void Probe_exposes_stable_asset_source_and_media_metadata()
	{
		var assetId = new MediaAssetId(Identity.Parse("41000000-0000-0000-0000-000000000001"));
		var sourceId = new MediaSourceId(Identity.Parse("41000000-0000-0000-0000-000000000002"));
		var probe = new LocalMediaProbe(
			MediaContractVersion.Current,
			assetId,
			sourceId,
			"reference.mp4",
			MediaContainerFormat.Mp4,
			MediaVideoCodec.H264,
			MediaAudioCodec.Aac,
			VideoFormat.Hd1080p50Rgba8,
			AudioFormat.Stereo48kFloat32,
			TimeSpan.FromSeconds(10));

		Assert.Equal(assetId, probe.AssetId);
		Assert.Equal(sourceId, probe.SourceId);
		Assert.Equal("reference.mp4", probe.FileName);
		Assert.Equal(MediaContainerFormat.Mp4, probe.Container);
		Assert.Equal(MediaVideoCodec.H264, probe.VideoCodec);
		Assert.Equal(MediaAudioCodec.Aac, probe.AudioCodec);
		Assert.Equal(VideoFormat.Hd1080p50Rgba8, probe.VideoFormat);
		Assert.Equal(AudioFormat.Stereo48kFloat32, probe.AudioFormat);
		Assert.Equal(TimeSpan.FromSeconds(10), probe.Duration);
	}

	[Fact]
	public void Probe_accepts_video_only_media()
	{
		var probe = new LocalMediaProbe(
			MediaContractVersion.Current,
			MediaAssetId.New(),
			MediaSourceId.New(),
			"silent.mp4",
			MediaContainerFormat.Mp4,
			MediaVideoCodec.H264,
			MediaAudioCodec.None,
			VideoFormat.Hd1080p50Rgba8,
			AudioFormat.Stereo48kFloat32,
			TimeSpan.FromSeconds(5));

		Assert.Equal(MediaAudioCodec.None, probe.AudioCodec);
	}

	[Fact]
	public void Probe_rejects_zero_duration()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new LocalMediaProbe(
			MediaContractVersion.Current,
			MediaAssetId.New(),
			MediaSourceId.New(),
			"reference.mp4",
			MediaContainerFormat.Mp4,
			MediaVideoCodec.H264,
			MediaAudioCodec.Aac,
			VideoFormat.Hd1080p50Rgba8,
			AudioFormat.Stereo48kFloat32,
			TimeSpan.Zero));
	}
}
