// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class MediaDeckControllerTests
{
	[Fact]
	public async Task Open_transport_seek_and_marker_commands_reconcile_confirmed_state()
	{
		var transport = new FakeDeckTransport();
		var client = new OperatorControlClient(transport);
		await using var deck = new MediaDeckController(client);
		var sourceId = new MediaSourceId(Id(2));

		var opened = await deck.OpenAsync(@"C:\media\reference.mp4", sourceId);

		Assert.True(opened.IsLoaded);
		Assert.Equal(MediaDeckState.Ready, deck.Snapshot.State);
		Assert.Equal(0, deck.Timeline.State.ConfirmedFrame);
		Assert.Empty(deck.Markers.State.CuePoints);

		await deck.Timeline.SeekToFrameAsync(12);
		Assert.Equal(12, deck.Timeline.State.ConfirmedFrame);
		Assert.Equal(12, deck.Snapshot.Transport!.Position.CurrentFrame);

		Assert.True(await deck.Markers.SetInAtCurrentFrameAsync());
		Assert.Equal(12, deck.Markers.State.InPointFrame);
		Assert.Equal(12, deck.Snapshot.Markers!.InPointFrame);

		var cueId = await deck.Markers.AddCueAtCurrentFrameAsync("Interview");
		Assert.True(cueId.HasValue);
		Assert.Equal(12, deck.Snapshot.Markers!.CuePoints.Single().PositionFrame);

		await deck.PlayAsync();
		Assert.Equal(MediaDeckState.Playing, deck.Snapshot.State);
		Assert.Equal(MediaTransportState.Playing, deck.Snapshot.Transport!.State);
		Assert.True(transport.OpenCalls == 1);
		Assert.True(transport.TransportCalls >= 2);
		Assert.Equal(2, transport.MarkerCalls);
	}

	[Fact]
	public async Task Playback_policy_round_trips_through_confirmed_deck_snapshot()
	{
		var transport = new FakeDeckTransport();
		var client = new OperatorControlClient(transport);
		await using var deck = new MediaDeckController(client);
		await deck.OpenAsync(@"C:\media\reference.mp4", new MediaSourceId(Id(2)));

		var configured = await deck.ConfigurePlaybackAsync(false, MediaDeckEndBehavior.Loop);

		Assert.False(configured.Transport!.AutoPlayOnProgram);
		Assert.Equal(MediaDeckEndBehavior.Loop, configured.Transport.EndBehavior);
		Assert.False(deck.Snapshot.Transport!.AutoPlayOnProgram);
		Assert.Equal(MediaDeckEndBehavior.Loop, deck.Snapshot.Transport.EndBehavior);
	}

	[Fact]
	public async Task Close_clears_timeline_and_marker_projection_and_late_actions_are_safe()
	{
		var transport = new FakeDeckTransport();
		var client = new OperatorControlClient(transport);
		await using var deck = new MediaDeckController(client);
		await deck.OpenAsync(@"C:\media\reference.mp4", new MediaSourceId(Id(2)));
		await deck.Timeline.SeekToFrameAsync(12);
		Assert.True(await deck.Markers.SetInAtCurrentFrameAsync());

		await deck.CloseAsync();

		Assert.False(deck.Snapshot.IsLoaded);
		Assert.False(deck.Timeline.State.IsLoaded);
		Assert.False(deck.Timeline.State.CanSeek);
		Assert.False(deck.Markers.State.IsLoaded);
		Assert.Null(deck.Markers.State.InPointFrame);
		Assert.Empty(deck.Markers.State.CuePoints);

		var transportCalls = transport.TransportCalls;
		var markerCalls = transport.MarkerCalls;
		await deck.Timeline.CompletePointerSeekAsync(20);
		Assert.False(await deck.Markers.SetInAtCurrentFrameAsync());
		Assert.Equal(transportCalls, transport.TransportCalls);
		Assert.Equal(markerCalls, transport.MarkerCalls);
	}

	[Fact]
	public async Task Refresh_replaces_local_projection_with_remote_confirmed_snapshot()
	{
		var transport = new FakeDeckTransport();
		var client = new OperatorControlClient(transport);
		await using var deck = new MediaDeckController(client);
		var sourceId = new MediaSourceId(Id(2));
		await deck.OpenAsync(@"C:\media\reference.mp4", sourceId);
		transport.SetRemoteFrame(25);

		await deck.RefreshAsync();

		Assert.Equal(25, deck.Timeline.State.ConfirmedFrame);
		Assert.Equal(25, deck.Snapshot.Transport!.Position.CurrentFrame);
	}

	private sealed class FakeDeckTransport : IOperatorControlTransport
	{
		private readonly MediaAssetId _assetId = new(Id(1));
		private MediaDeckSnapshot _deck = MediaDeckSnapshot.Unloaded;

		public int OpenCalls { get; private set; }
		public int TransportCalls { get; private set; }
		public int MarkerCalls { get; private set; }

		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorStatusSnapshot>(new NotSupportedException());

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
			SelectPreviewCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());

		public ValueTask<OperatorMutationResponse> CutProgramAsync(
			CutProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());

		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
			DissolveProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());

		public ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(_deck);

		public ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(
			MediaDeckOpenRequest request,
			CancellationToken cancellationToken = default)
		{
			OpenCalls++;
			var probe = new LocalMediaProbe(
				MediaContractVersion.Current,
				_assetId,
				request.SourceId,
				"reference.mp4",
				MediaContainerFormat.Mp4,
				MediaVideoCodec.H264,
				MediaAudioCodec.Aac,
				VideoFormat.Hd1080p50Rgba8,
				AudioFormat.Stereo48kFloat32,
				TimeSpan.FromSeconds(1));
			var transport = Transport(request.SourceId, 0, MediaTransportState.Ready);
			var markers = new MediaMarkerSnapshot(MediaContractVersion.Current, _assetId, 50);
			_deck = new MediaDeckSnapshot(
				MediaContractVersion.Current,
				MediaDeckState.Ready,
				request.SourceId,
				probe,
				transport,
				markers);
			return ValueTask.FromResult(_deck);
		}

		public ValueTask<MediaDeckSnapshot> ApplyMediaDeckTransportAsync(
			MediaTransportCommand command,
			CancellationToken cancellationToken = default)
		{
			TransportCalls++;
			var current = _deck.Transport ?? throw new InvalidOperationException();
			var frame = command.Kind switch
			{
				MediaTransportCommandKind.Seek => command.TargetFrame!.Value,
				MediaTransportCommandKind.Stop => 0,
				_ => current.Position.CurrentFrame
			};
			var state = command.Kind switch
			{
				MediaTransportCommandKind.Play => MediaTransportState.Playing,
				MediaTransportCommandKind.Pause => MediaTransportState.Paused,
				MediaTransportCommandKind.Stop => MediaTransportState.Ready,
				_ => current.State
			};
			var next = Transport(current.SourceId, frame, state);
			if (command.Kind == MediaTransportCommandKind.ConfigurePlayback)
			{
				next = new MediaTransportSnapshot(
					next.Version,
					next.AssetId,
					next.SourceId,
					next.State,
					next.Position,
					next.Failure,
					command.AutoPlayOnProgram!.Value,
					command.EndBehavior!.Value);
			}
			_deck = Rebuild(next, _deck.Markers!);
			return ValueTask.FromResult(_deck);
		}

		public ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(
			MediaMarkerCommand command,
			CancellationToken cancellationToken = default)
		{
			MarkerCalls++;
			var markers = _deck.Markers ?? throw new InvalidOperationException();
			var cues = markers.CuePoints.ToList();
			var inPoint = markers.InPointFrame;
			var outPoint = markers.OutPointFrame;
			switch (command.Kind)
			{
				case MediaMarkerCommandKind.SetInPoint:
					inPoint = command.PositionFrame;
					break;
				case MediaMarkerCommandKind.SetOutPoint:
					outPoint = command.PositionFrame;
					break;
				case MediaMarkerCommandKind.AddCuePoint:
					cues.Add(new MediaCuePoint(command.CuePointId!.Value, command.Name!, command.PositionFrame!.Value));
					break;
			}

			var next = new MediaMarkerSnapshot(
				MediaContractVersion.Current,
				_assetId,
				50,
				inPoint,
				outPoint,
				cues);
			_deck = Rebuild(_deck.Transport!, next);
			return ValueTask.FromResult(_deck);
		}

		public ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default)
		{
			_deck = MediaDeckSnapshot.Unloaded;
			return ValueTask.FromResult(_deck);
		}

		public void SetRemoteFrame(long frame)
		{
			var current = _deck.Transport ?? throw new InvalidOperationException();
			_deck = Rebuild(Transport(current.SourceId, frame, current.State), _deck.Markers!);
		}

		private MediaDeckSnapshot Rebuild(MediaTransportSnapshot transport, MediaMarkerSnapshot markers) =>
			new(
				MediaContractVersion.Current,
				transport.State switch
				{
					MediaTransportState.Playing => MediaDeckState.Playing,
					MediaTransportState.Paused => MediaDeckState.Paused,
					MediaTransportState.Ended => MediaDeckState.Ended,
					_ => MediaDeckState.Ready
				},
				transport.SourceId,
				_deck.Probe,
				transport,
				markers);

		private MediaTransportSnapshot Transport(
			MediaSourceId sourceId,
			long frame,
			MediaTransportState state)
		{
			var position = TimeSpan.FromMilliseconds(frame * 20);
			var duration = TimeSpan.FromSeconds(1);
			return new MediaTransportSnapshot(
				MediaContractVersion.Current,
				_assetId,
				sourceId,
				state,
				new MediaTransportPosition(
					frame,
					50,
					position,
					duration,
					duration - position,
					FrameRate.Fps50),
				null);
		}
	}

	private static Identity Id(int value) =>
		Identity.Parse($"45000000-0000-0000-0000-{value:000000000000}");
}
