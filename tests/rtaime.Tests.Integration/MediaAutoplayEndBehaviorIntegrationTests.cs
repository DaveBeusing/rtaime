// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class MediaAutoplayEndBehaviorIntegrationTests
{
	[Theory]
	[InlineData(MediaDeckEndBehavior.HoldLastFrame, MediaTransportState.Ended, 7)]
	[InlineData(MediaDeckEndBehavior.Stop, MediaTransportState.Ready, 0)]
	[InlineData(MediaDeckEndBehavior.Loop, MediaTransportState.Playing, 5)]
	[InlineData(MediaDeckEndBehavior.ReturnToIn, MediaTransportState.Paused, 5)]
	public void Effective_IN_OUT_range_applies_each_end_behavior_deterministically(
		MediaDeckEndBehavior endBehavior,
		MediaTransportState expectedState,
		long expectedFrame)
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var asset = LocalMediaTestAsset.ExtractReference1080p50();
		using var deck = new LocalMediaDeckRuntimeService();
		var source = new MediaSourceId(Id(1));
		var opened = deck.Open(
			new MediaDeckOpenRequest(MediaContractVersion.Current, source, asset.Path),
			Prepared(deck, source));
		Assert.True(opened.Probe is not null, opened.Failure?.Message);
		var assetId = opened.Probe!.AssetId;

		var configured = deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.ConfigurePlayback,
			autoPlayOnProgram: true,
			endBehavior: endBehavior,
			inPointFrame: 5,
			outPointFrame: 7));
		Assert.True(configured.Succeeded, configured.Failure?.Message);
		Assert.Equal(5, configured.Snapshot.EffectiveStartFrame);
		Assert.Equal(7, configured.Snapshot.EffectiveEndFrame);

		var armed = deck.ObserveProgramSource(source);
		Assert.Equal(MediaDeckState.Playing, armed.State);
		Assert.True(armed.Transport!.IsOnProgram);
		Assert.Equal(5, armed.Transport.Position.CurrentFrame);
		Assert.Equal(2, armed.Transport.EffectiveRemainingFrames);
		Assert.Equal(TimeSpan.FromMilliseconds(40), armed.Transport.EffectiveRemaining);

		Assert.Equal(5, ProcessFrame(deck));
		Assert.Equal(6, ProcessFrame(deck));
		Assert.Equal(7, ProcessFrame(deck));

		var ended = deck.Snapshot;
		Assert.Equal(expectedState, ended.Transport!.State);
		Assert.Equal(expectedFrame, ended.Transport.Position.CurrentFrame);

		if (endBehavior == MediaDeckEndBehavior.HoldLastFrame)
		{
			deck.ObserveProgramSource(new MediaSourceId(Id(2)));
			var retake = deck.ObserveProgramSource(source);
			Assert.Equal(MediaDeckState.Playing, retake.State);
			Assert.Equal(5, retake.Transport!.Position.CurrentFrame);
		}
	}

	[Fact]
	public void Auto_play_preserves_a_valid_cue_position_and_can_be_disabled()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var asset = LocalMediaTestAsset.ExtractReference1080p50();
		using var deck = new LocalMediaDeckRuntimeService();
		var source = new MediaSourceId(Id(10));
		var opened = deck.Open(
			new MediaDeckOpenRequest(MediaContractVersion.Current, source, asset.Path),
			Prepared(deck, source));
		var assetId = opened.Probe!.AssetId;

		Assert.True(deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.ConfigurePlayback,
			autoPlayOnProgram: true,
			endBehavior: MediaDeckEndBehavior.HoldLastFrame,
			inPointFrame: 5,
			outPointFrame: 20)).Succeeded);
		Assert.True(deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.Seek,
			12)).Succeeded);

		var taken = deck.ObserveProgramSource(source);
		Assert.Equal(MediaDeckState.Playing, taken.State);
		Assert.Equal(12, taken.Transport!.Position.CurrentFrame);

		deck.ObserveProgramSource(new MediaSourceId(Id(11)));
		Assert.True(deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.Pause)).Succeeded);
		Assert.True(deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.ConfigurePlayback,
			autoPlayOnProgram: false,
			endBehavior: MediaDeckEndBehavior.HoldLastFrame,
			inPointFrame: 5,
			outPointFrame: 20)).Succeeded);

		var disabled = deck.ObserveProgramSource(source);
		Assert.Equal(MediaDeckState.Paused, disabled.State);
		Assert.False(disabled.Transport!.AutoPlayOnProgram);
	}

	private static long ProcessFrame(LocalMediaDeckRuntimeService deck)
	{
		deck.ProcessBoundary();
		var boundary = deck.LatestBoundary;
		Assert.NotNull(boundary);
		Assert.True(boundary!.Succeeded, boundary.Failure?.Message);
		return boundary.Transport.Position.CurrentFrame;
	}

	private static PreparedExecutionContract Prepared(LocalMediaDeckRuntimeService deck, MediaSourceId source)
	{
		var provider = deck.ProviderDescriptor;
		var capability = provider.Capabilities.Single(candidate =>
			string.Equals(candidate.Kind, "media.route", StringComparison.Ordinal));
		var resource = provider.Resources.First(candidate =>
			string.Equals(candidate.Kind, "media.route", StringComparison.Ordinal));
		return new PreparedExecutionContract(
			RuntimeContractVersion.Current,
			PreparedExecutionId.New(),
			new AuthoritySnapshotReference(Id(900), new Revision(1)),
			new Generation(1),
			new[]
			{
				new PreparedExecutionBinding(
					source.Value,
					capability.CapabilityId,
					resource,
					source,
					null)
			});
	}

	private static Identity Id(int value) =>
		Identity.Parse($"52000000-0000-0000-0000-{value:000000000000}");
}
