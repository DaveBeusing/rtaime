// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class LocalMediaTransportIntegrationTests
{
	[Fact]
	public void Multi_frame_mp4_supports_play_pause_seek_step_stop_and_av_positioning()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var sourceA = new ProductionSourceId(Id(1));
		var sourceB = new ProductionSourceId(Id(2));
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			new ProductionId(Id(10)),
			"Local Media Transport Integration",
			new[]
			{
				new ProductionSourceSpecification(sourceA, "Transport Clip"),
				new ProductionSourceSpecification(sourceB, "Standby")
			},
			new ProductionRoutingState(sourceA, sourceA));
		var initialized = ControlDomainEngine.Initialize(specification);
		Assert.True(initialized.Succeeded);

		using var referenceAsset = LocalMediaTestAsset.ExtractReference1080p50();
		var provider = new LocalMediaFileProvider();
		var mediaSourceId = new MediaSourceId(sourceA.Value);
		var assetId = new MediaAssetId(Id(100));
		var open = provider.TryOpen(referenceAsset.Path, mediaSourceId, assetId);
		Assert.True(open.Succeeded, open.Failure?.Message);
		Assert.NotNull(open.Source);

		var planning = CapabilityPlanningEngine.Plan(
			specification,
			initialized.State!.Authoritative,
			new SingleProviderRegistry(provider.Descriptor));
		Assert.True(planning.Succeeded, string.Join("; ", planning.Validation.Issues.Select(issue => issue.Message)));
		Assert.NotNull(planning.PreparedExecution);

		using var session = new LocalMediaRuntimeSession(provider, open.Source!);
		var applied = session.ApplyExecution(planning.PreparedExecution!);
		Assert.True(applied.Committed);
		Assert.Equal(MediaTransportState.Ready, session.Transport.State);
		Assert.Equal(50, session.Transport.Position.TotalFrames);

		var play = session.ApplyTransport(Command(assetId, MediaTransportCommandKind.Play));
		Assert.True(play.Succeeded, play.Failure?.Message);
		Assert.Equal(MediaTransportState.Playing, play.Snapshot.State);

		var first = session.ProcessNextBoundary();
		Assert.True(first.Succeeded, $"{first.Status}: {first.Failure?.Message}");
		Assert.Equal(0, first.Transport.Position.CurrentFrame);
		Assert.NotNull(first.Audio);
		Assert.False(first.AudioPayload.IsEmpty);

		var pause = session.ApplyTransport(Command(assetId, MediaTransportCommandKind.Pause));
		Assert.True(pause.Succeeded, pause.Failure?.Message);
		Assert.Equal(MediaTransportState.Paused, pause.Snapshot.State);

		var seek = session.ApplyTransport(Seek(assetId, 2));
		Assert.True(seek.Succeeded, seek.Failure?.Message);
		Assert.Equal(2, seek.Snapshot.Position.CurrentFrame);
		Assert.Equal(TimeSpan.FromMilliseconds(40), seek.Snapshot.Position.Position);

		Assert.True(session.ApplyTransport(Command(assetId, MediaTransportCommandKind.Play)).Succeeded);
		var afterSeek = session.ProcessNextBoundary();
		Assert.True(afterSeek.Succeeded, $"{afterSeek.Status}: {afterSeek.Failure?.Message}");
		Assert.Equal(2, afterSeek.Transport.Position.CurrentFrame);
		Assert.NotNull(afterSeek.Audio);
		Assert.Equal(afterSeek.Video!.Timing.Timebase, afterSeek.Audio!.Timing.Timebase);
		Assert.InRange(
			Math.Abs(afterSeek.Audio.Timing.PresentationTimestamp - afterSeek.Video.Timing.PresentationTimestamp),
			0L,
			200_000L);

		Assert.True(session.ApplyTransport(Command(assetId, MediaTransportCommandKind.Pause)).Succeeded);
		var backward = session.ApplyTransport(Command(assetId, MediaTransportCommandKind.StepBackward));
		Assert.True(backward.Succeeded, backward.Failure?.Message);
		Assert.Equal(1, backward.Snapshot.Position.CurrentFrame);
		var forward = session.ApplyTransport(Command(assetId, MediaTransportCommandKind.StepForward));
		Assert.True(forward.Succeeded, forward.Failure?.Message);
		Assert.Equal(2, forward.Snapshot.Position.CurrentFrame);

		var beforeStart = session.ApplyTransport(Seek(assetId, -100));
		Assert.True(beforeStart.Succeeded, beforeStart.Failure?.Message);
		Assert.Equal(0, beforeStart.Snapshot.Position.CurrentFrame);
		var beyondEnd = session.ApplyTransport(Seek(assetId, 100));
		Assert.True(beyondEnd.Succeeded, beyondEnd.Failure?.Message);
		Assert.Equal(49, beyondEnd.Snapshot.Position.CurrentFrame);

		var stop = session.ApplyTransport(Command(assetId, MediaTransportCommandKind.Stop));
		Assert.True(stop.Succeeded, stop.Failure?.Message);
		Assert.Equal(MediaTransportState.Ready, stop.Snapshot.State);
		Assert.Equal(0, stop.Snapshot.Position.CurrentFrame);

		var illegalPause = session.ApplyTransport(Command(assetId, MediaTransportCommandKind.Pause));
		Assert.False(illegalPause.Succeeded);
		Assert.Equal("media.transport.transition_invalid", illegalPause.Failure?.Code);
	}

	private static MediaTransportCommand Command(MediaAssetId assetId, MediaTransportCommandKind kind) =>
		new(MediaContractVersion.Current, assetId, kind);

	private static MediaTransportCommand Seek(MediaAssetId assetId, long frame) =>
		new(MediaContractVersion.Current, assetId, MediaTransportCommandKind.Seek, frame);

	private static Identity Id(int value) =>
		Identity.Parse($"42000000-0000-0000-0000-{value:000000000000}");

	private sealed class SingleProviderRegistry : IProviderCapabilityRegistry
	{
		private readonly IReadOnlyList<ProviderDescriptor> _providers;

		public SingleProviderRegistry(ProviderDescriptor provider)
		{
			_providers = Array.AsReadOnly(new[] { provider });
		}

		public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
	}
}
