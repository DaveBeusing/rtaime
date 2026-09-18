// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

/// <summary>
/// Authoritative ControlHost composition for the single V1 local media deck.
/// Runtime owns decode/transport; ControlHost owns source authorization and marker persistence.
/// </summary>
public sealed class MediaDeckControlService
{
	private const string DecodeCapabilityKind = "media.file.decode";
	private const string RouteCapabilityKind = "media.route";
	private readonly Func<ControlHostService?> _controlAccessor;
	private readonly IControlRuntimeTransportSeam _runtime;
	private readonly MediaMarkerPersistenceStore _persistence;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private MediaMarkerSnapshot? _markers;
	private ulong _markerStorageVersion;

	public MediaDeckControlService(
		Func<ControlHostService?> controlAccessor,
		IControlRuntimeTransportSeam runtime,
		MediaMarkerPersistenceStore persistence)
	{
		_controlAccessor = controlAccessor ?? throw new ArgumentNullException(nameof(controlAccessor));
		_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
		_persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
	}

	public async ValueTask<MediaDeckSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var runtime = await _runtime.GetMediaDeckSnapshotAsync(cancellationToken).ConfigureAwait(false);
			await EnsureMarkersLoadedAsync(runtime, cancellationToken).ConfigureAwait(false);
			return Compose(runtime);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<MediaDeckSnapshot> OpenAsync(
		MediaDeckOpenRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var control = RequireControl();
			EnsureSourceAllowed(control, request.SourceId);

			var providers = await _runtime.GetProviderDescriptorsAsync(cancellationToken).ConfigureAwait(false);
			var provider = providers.FirstOrDefault(candidate =>
				candidate.Availability.State == ProviderAvailabilityState.Available &&
				candidate.Capabilities.Any(capability =>
					string.Equals(capability.Kind, DecodeCapabilityKind, StringComparison.Ordinal)));
			if (provider is null)
			{
				return MediaDeckSnapshot.Failed(
					"control.media_deck.provider_unavailable",
					"RuntimeHost does not expose an available local-media decode provider.");
			}

			var routeCapability = provider.Capabilities.FirstOrDefault(capability =>
				string.Equals(capability.Kind, RouteCapabilityKind, StringComparison.Ordinal));
			var routeResource = provider.Resources.FirstOrDefault(resource =>
				string.Equals(resource.Kind, RouteCapabilityKind, StringComparison.Ordinal));
			if (routeCapability is null || routeResource is null)
			{
				return MediaDeckSnapshot.Failed(
					"control.media_deck.route_unavailable",
					"Local-media provider does not expose an admissible media.route resource.");
			}

			var authoritative = control.State;
			var prepared = new PreparedExecutionContract(
				RuntimeContractVersion.Current,
				PreparedExecutionId.New(),
				new AuthoritySnapshotReference(authoritative.ProductionId.Value, authoritative.Revision),
				new Generation(authoritative.Revision.Value),
				new[]
				{
					new PreparedExecutionBinding(
						request.SourceId.Value,
						routeCapability.CapabilityId,
						routeResource,
						request.SourceId,
						null)
				});

			var runtime = await _runtime
				.OpenMediaDeckAsync(request, prepared, cancellationToken)
				.ConfigureAwait(false);
			_markers = null;
			_markerStorageVersion = 0;
			await EnsureMarkersLoadedAsync(runtime, cancellationToken).ConfigureAwait(false);
			return Compose(runtime);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
		{
			return MediaDeckSnapshot.Failed("control.media_deck.open_failed", exception.Message);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<MediaDeckSnapshot> ApplyTransportAsync(
		MediaTransportCommand command,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var current = await _runtime.GetMediaDeckSnapshotAsync(cancellationToken).ConfigureAwait(false);
			if (current.Probe is null || current.Transport is null)
				return MediaDeckSnapshot.Failed("control.media_deck.unloaded", "Media deck has no loaded asset.");
			if (command.AssetId != current.Probe.AssetId)
				return MediaDeckSnapshot.Failed("control.media_deck.asset_mismatch", "Transport command asset does not match the loaded deck asset.");

			await _runtime.ApplyMediaDeckTransportAsync(command, cancellationToken).ConfigureAwait(false);
			var runtime = await _runtime.GetMediaDeckSnapshotAsync(cancellationToken).ConfigureAwait(false);
			await EnsureMarkersLoadedAsync(runtime, cancellationToken).ConfigureAwait(false);
			return Compose(runtime);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<MediaDeckSnapshot> ApplyMarkerAsync(
		MediaMarkerCommand command,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var runtime = await _runtime.GetMediaDeckSnapshotAsync(cancellationToken).ConfigureAwait(false);
			await EnsureMarkersLoadedAsync(runtime, cancellationToken).ConfigureAwait(false);
			if (_markers is null || runtime.Probe is null)
				return MediaDeckSnapshot.Failed("control.media_deck.unloaded", "Media deck has no loaded asset.");
			if (command.AssetId != runtime.Probe.AssetId)
				return MediaDeckSnapshot.Failed("control.media_deck.asset_mismatch", "Marker command asset does not match the loaded deck asset.");

			var applied = ApplyMarker(_markers, command);
			if (!applied.Succeeded)
				return Compose(runtime, applied.Failure);

			var persisted = await _persistence
				.SaveAsync(applied.Snapshot, _markerStorageVersion, cancellationToken)
				.ConfigureAwait(false);
			if (!persisted.Written || persisted.Persisted is null)
			{
				return Compose(
					runtime,
					persisted.Failure ?? new Failure(
						"control.media_deck.marker_persistence_failed",
						"Marker state could not be persisted."));
			}

			_markers = persisted.Persisted.Snapshot;
			_markerStorageVersion = persisted.Persisted.StorageVersion;
			return Compose(runtime);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<MediaDeckSnapshot> CloseAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var runtime = await _runtime.CloseMediaDeckAsync(cancellationToken).ConfigureAwait(false);
			_markers = null;
			_markerStorageVersion = 0;
			return Compose(runtime);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async ValueTask EnsureMarkersLoadedAsync(
		MediaDeckRuntimeSnapshot runtime,
		CancellationToken cancellationToken)
	{
		if (runtime.Probe is null || runtime.Transport is null)
		{
			_markers = null;
			_markerStorageVersion = 0;
			return;
		}
		if (_markers is not null && _markers.AssetId == runtime.Probe.AssetId)
			return;

		var persisted = await _persistence
			.LoadAsync(runtime.Probe.AssetId, runtime.Transport.Position.TotalFrames, cancellationToken)
			.ConfigureAwait(false);
		_markers = persisted.Snapshot;
		_markerStorageVersion = persisted.StorageVersion;
	}

	private MediaDeckSnapshot Compose(MediaDeckRuntimeSnapshot runtime, Failure? controlFailure = null)
	{
		if (runtime.State == MediaDeckState.Error)
			return new MediaDeckSnapshot(
				MediaContractVersion.Current,
				MediaDeckState.Error,
				failure: runtime.Failure ?? controlFailure ?? new Failure("control.media_deck.failed", "Media deck failed."));
		if (runtime.Probe is null || runtime.Transport is null || runtime.SourceId is null)
			return runtime.State == MediaDeckState.Loading
				? new MediaDeckSnapshot(MediaContractVersion.Current, MediaDeckState.Loading)
				: MediaDeckSnapshot.Unloaded;
		if (_markers is null)
			throw new InvalidOperationException("Loaded media deck requires marker state.");

		if (controlFailure is not null)
		{
			return new MediaDeckSnapshot(
				MediaContractVersion.Current,
				MediaDeckState.Error,
				failure: controlFailure);
		}

		return new MediaDeckSnapshot(
			MediaContractVersion.Current,
			runtime.State,
			runtime.SourceId,
			runtime.Probe,
			runtime.Transport,
			_markers);
	}

	private static MediaMarkerCommandResult ApplyMarker(
		MediaMarkerSnapshot snapshot,
		MediaMarkerCommand command)
	{
		if (command.AssetId != snapshot.AssetId)
			return MediaMarkerCommandResult.Rejected(snapshot, "media.marker.asset_mismatch", "Marker command asset does not match the active media asset.");

		long? inPoint = snapshot.InPointFrame;
		long? outPoint = snapshot.OutPointFrame;
		var cues = snapshot.CuePoints.ToList();

		switch (command.Kind)
		{
			case MediaMarkerCommandKind.SetInPoint:
				if (!ValidFrame(command.PositionFrame!.Value, snapshot.TotalFrames))
					return OutOfRange(snapshot, command.PositionFrame.Value);
				if (outPoint.HasValue && command.PositionFrame.Value > outPoint.Value)
					return MediaMarkerCommandResult.Rejected(snapshot, "media.marker.in_after_out", "IN point must not be after the current OUT point.");
				inPoint = command.PositionFrame.Value;
				break;

			case MediaMarkerCommandKind.ClearInPoint:
				inPoint = null;
				break;

			case MediaMarkerCommandKind.SetOutPoint:
				if (!ValidFrame(command.PositionFrame!.Value, snapshot.TotalFrames))
					return OutOfRange(snapshot, command.PositionFrame.Value);
				if (inPoint.HasValue && command.PositionFrame.Value < inPoint.Value)
					return MediaMarkerCommandResult.Rejected(snapshot, "media.marker.out_before_in", "OUT point must not be before the current IN point.");
				outPoint = command.PositionFrame.Value;
				break;

			case MediaMarkerCommandKind.ClearOutPoint:
				outPoint = null;
				break;

			case MediaMarkerCommandKind.AddCuePoint:
				if (!ValidFrame(command.PositionFrame!.Value, snapshot.TotalFrames))
					return OutOfRange(snapshot, command.PositionFrame.Value);
				if (cues.Any(cue => cue.Id == command.CuePointId!.Value))
					return MediaMarkerCommandResult.Rejected(snapshot, "media.marker.cue_id_conflict", "Cue-point identity already exists.");
				cues.Add(new MediaCuePoint(command.CuePointId.Value, command.Name!, command.PositionFrame.Value));
				break;

			case MediaMarkerCommandKind.RenameCuePoint:
			{
				var index = cues.FindIndex(cue => cue.Id == command.CuePointId!.Value);
				if (index < 0)
					return MediaMarkerCommandResult.Rejected(snapshot, "media.marker.cue_not_found", "Cue point does not exist.");
				var existing = cues[index];
				cues[index] = new MediaCuePoint(existing.Id, command.Name!, existing.PositionFrame);
				break;
			}

			case MediaMarkerCommandKind.DeleteCuePoint:
			{
				var removed = cues.RemoveAll(cue => cue.Id == command.CuePointId!.Value);
				if (removed == 0)
					return MediaMarkerCommandResult.Rejected(snapshot, "media.marker.cue_not_found", "Cue point does not exist.");
				break;
			}

			default:
				return MediaMarkerCommandResult.Rejected(snapshot, "media.marker.command_unsupported", "Marker command is not supported.");
		}

		return MediaMarkerCommandResult.Applied(new MediaMarkerSnapshot(
			MediaContractVersion.Current,
			snapshot.AssetId,
			snapshot.TotalFrames,
			inPoint,
			outPoint,
			cues));
	}

	private ControlHostService RequireControl()
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			throw new InvalidOperationException("ControlHost has no committed authoritative state.");
		return control;
	}

	private static void EnsureSourceAllowed(ControlHostService control, MediaSourceId sourceId)
	{
		if (!control.Specification.Sources.Any(source => source.SourceId.Value == sourceId.Value))
			throw new InvalidOperationException("Requested media-deck source is not part of the authoritative production specification.");
	}

	private static bool ValidFrame(long frame, long totalFrames) =>
		frame >= 0 && frame < totalFrames;

	private static MediaMarkerCommandResult OutOfRange(MediaMarkerSnapshot snapshot, long frame) =>
		MediaMarkerCommandResult.Rejected(
			snapshot,
			"media.marker.frame_out_of_range",
			$"Marker frame '{frame}' is outside range 0..{snapshot.TotalFrames - 1}.");
}
