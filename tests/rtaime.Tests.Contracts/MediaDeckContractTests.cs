// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class MediaDeckContractTests
{
	[Fact]
	public void Loaded_state_requires_probe_transport_and_markers()
	{
		var asset = new MediaAssetId(Id(1));
		var source = new MediaSourceId(Id(2));
		var probe = Probe(asset, source);
		var transport = Transport(asset, source);
		var markers = new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			asset,
			50);

		var snapshot = new MediaDeckSnapshot(
			MediaContractVersion.Current,
			MediaDeckState.Ready,
			source,
			probe,
			transport,
			markers);

		Assert.True(snapshot.IsLoaded);
		Assert.Equal(MediaDeckState.Ready, snapshot.State);
		Assert.Equal(asset, snapshot.Probe!.AssetId);
		Assert.Throws<ArgumentException>(() => new MediaDeckSnapshot(
			MediaContractVersion.Current,
			MediaDeckState.Ready,
			source,
			probe,
			transport));
	}

	[Fact]
	public void Error_state_requires_failure_and_non_error_state_rejects_one()
	{
		Assert.Throws<ArgumentException>(() => new MediaDeckSnapshot(
			MediaContractVersion.Current,
			MediaDeckState.Error));

		Assert.Throws<ArgumentException>(() => new MediaDeckSnapshot(
			MediaContractVersion.Current,
			MediaDeckState.Unloaded,
			failure: new Failure("test.failure", "failure")));

		var failed = MediaDeckSnapshot.Failed("media.deck.failed", "Deck failed.");
		Assert.Equal(MediaDeckState.Error, failed.State);
		Assert.Equal("media.deck.failed", failed.Failure?.Code);
	}

	[Fact]
	public void Probe_source_must_match_deck_source()
	{
		var asset = new MediaAssetId(Id(1));
		var source = new MediaSourceId(Id(2));
		var other = new MediaSourceId(Id(3));
		var probe = Probe(asset, source);
		var transport = Transport(asset, source);
		var markers = new MediaMarkerSnapshot(MediaContractVersion.Current, asset, 50);

		Assert.Throws<ArgumentException>(() => new MediaDeckSnapshot(
			MediaContractVersion.Current,
			MediaDeckState.Paused,
			other,
			probe,
			transport,
			markers));
	}

	private static LocalMediaProbe Probe(MediaAssetId asset, MediaSourceId source) =>
		new(
			MediaContractVersion.Current,
			asset,
			source,
			"reference.mp4",
			MediaContainerFormat.Mp4,
			MediaVideoCodec.H264,
			MediaAudioCodec.Aac,
			VideoFormat.Hd1080p50Rgba8,
			AudioFormat.Stereo48kFloat32,
			TimeSpan.FromSeconds(1));

	private static MediaTransportSnapshot Transport(MediaAssetId asset, MediaSourceId source) =>
		new(
			MediaContractVersion.Current,
			asset,
			source,
			MediaTransportState.Ready,
			new MediaTransportPosition(
				0,
				50,
				TimeSpan.Zero,
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(1),
				FrameRate.Fps50),
			null);

	private static Identity Id(int value) =>
		Identity.Parse($"45000000-0000-0000-0000-{value:000000000000}");
}
