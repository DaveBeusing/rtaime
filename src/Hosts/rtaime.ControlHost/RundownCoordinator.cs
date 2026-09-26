// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.ControlHost;

public sealed class RundownCoordinator : IAsyncDisposable
{
	private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(50);
	private readonly Func<ControlHostService?> _controlAccessor;
	private readonly ShowProjectPersistenceStore _persistence;
	private readonly ShowControlCoordinator _showControl;
	private readonly MediaAssetCatalogService? _mediaCatalog;
	private readonly MediaDeckControlService? _mediaDeck;
	private readonly Action _stateChanged;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly CancellationTokenSource _dispose = new();
	private CancellationTokenSource? _autoAdvanceCancellation;
	private Task? _autoAdvanceWorker;
	private RundownDefinition? _rundown;
	private RundownExecutionSnapshot _execution = RundownExecutionSnapshot.Idle;
	private ulong _storageVersion;
	private bool _loaded;
	private readonly Dictionary<RundownItemId, ushort> _itemRepeatProgress = new();
	private ushort _rundownRepeatProgress;

	public RundownCoordinator(
		Func<ControlHostService?> controlAccessor,
		ShowProjectPersistenceStore persistence,
		ShowControlCoordinator showControl,
		MediaAssetCatalogService? mediaCatalog,
		MediaDeckControlService? mediaDeck,
		Action stateChanged)
	{
		_controlAccessor = controlAccessor ?? throw new ArgumentNullException(nameof(controlAccessor));
		_persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
		_showControl = showControl ?? throw new ArgumentNullException(nameof(showControl));
		_mediaCatalog = mediaCatalog;
		_mediaDeck = mediaDeck;
		_stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
	}

	public async ValueTask<RundownWorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			return Snapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<RundownWorkspaceSnapshot> SaveAsync(
		RundownDefinition rundown,
		ulong expectedStorageVersion,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(rundown);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			EnsureEditable();
			await ValidateReferencesAsync(rundown, cancellationToken).ConfigureAwait(false);

			var control = RequireControl();
			var write = await _persistence.UpdateRundownAsync(
				control.Specification,
				RundownCanonicalSerializer.Serialize(rundown),
				expectedStorageVersion,
				cancellationToken).ConfigureAwait(false);
			if (!write.Written || write.Snapshot is null)
				throw new InvalidOperationException(write.Failure?.Message ?? "Rundown persistence failed.");

			_rundown = rundown;
			_storageVersion = write.Snapshot.Version;
			_itemRepeatProgress.Clear();
			_rundownRepeatProgress = 0;
			var selected = _execution.SelectedItemId is { } selectedId &&
				rundown.Items.Any(item => item.ItemId == selectedId)
					? selectedId
					: rundown.Items[0].ItemId;
			_execution = RundownExecutionSnapshot.Idle with
			{
				RundownId = rundown.RundownId,
				SelectedItemId = selected,
				Revision = NextRevision()
			};
			_stateChanged();
			return Snapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<RundownWorkspaceSnapshot> PrepareAsync(
		RundownItemId itemId,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			await PrepareLockedAsync(itemId, cancellationToken).ConfigureAwait(false);
			return Snapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<RundownWorkspaceSnapshot> GoAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			await GoPreparedLockedAsync(cancellationToken).ConfigureAwait(false);
			return Snapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<RundownWorkspaceSnapshot> NextAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			EnsureNavigationAllowed();

			var anchor = _execution.CurrentItemId ?? _execution.PreparedItemId ?? _execution.SelectedItemId
				?? RequireRundown().Items[0].ItemId;
			var next = ResolveNext(anchor);
			if (next is null)
			{
				CancelAutoAdvanceWorker();
				_execution = _execution with
				{
					State = RundownExecutionState.Completed,
					PreparedItemId = null,
					NextItemId = null,
					AutoAdvanceArmed = false,
					Revision = NextRevision()
				};
				_stateChanged();
				return Snapshot();
			}

			await PrepareLockedAsync(next.Value, cancellationToken).ConfigureAwait(false);
			return Snapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<RundownWorkspaceSnapshot> PreviousAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			EnsureNavigationAllowed();
			var rundown = RequireRundown();
			var anchor = _execution.PreparedItemId ?? _execution.CurrentItemId ?? _execution.SelectedItemId
				?? rundown.Items[0].ItemId;
			var index = IndexOf(anchor);
			var previous = rundown.Items[Math.Max(0, index - 1)].ItemId;
			await PrepareLockedAsync(previous, cancellationToken).ConfigureAwait(false);
			return Snapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<RundownWorkspaceSnapshot> HoldAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			if (_execution.State == RundownExecutionState.RecoveryRequired)
				throw new InvalidOperationException("Rundown recovery must be acknowledged before hold/cancel.");

			CancelAutoAdvanceWorker();
			if (_execution.State == RundownExecutionState.Executing)
			{
				var show = await _showControl.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
				if (show.Execution.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting)
					await _showControl.CancelAsync(cancellationToken).ConfigureAwait(false);
			}
			_execution = _execution with
			{
				State = RundownExecutionState.Held,
				AutoAdvanceArmed = false,
				Revision = NextRevision()
			};
			_stateChanged();
			return Snapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<RundownWorkspaceSnapshot> AcknowledgeRecoveryAsync(
		bool resume,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
			if (_execution.State != RundownExecutionState.RecoveryRequired)
				throw new InvalidOperationException("Rundown recovery acknowledgement is not required.");

			var show = await _showControl.AcknowledgeRecoveryAsync(resume, cancellationToken).ConfigureAwait(false);
			_execution = _execution with
			{
				State = resume ? RundownExecutionState.Prepared : RundownExecutionState.Held,
				PreparedItemId = resume ? _execution.CurrentItemId : null,
				AutoAdvanceArmed = false,
				RequiresAcknowledgement = false,
				Failure = null,
				CausalActionId = show.Execution.ExecutionId?.Value,
				Revision = NextRevision()
			};
			_stateChanged();
			return Snapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		_dispose.Cancel();
		CancelAutoAdvanceWorker();
		if (_autoAdvanceWorker is not null)
		{
			try { await _autoAdvanceWorker.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_autoAdvanceCancellation?.Dispose();
		_dispose.Dispose();
		_gate.Dispose();
	}

	private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
	{
		if (_loaded)
			return;

		var control = RequireControl();
		var persisted = await _persistence.LoadRundownAsync(control.Specification, cancellationToken).ConfigureAwait(false);
		_storageVersion = persisted.Version;
		if (!string.IsNullOrWhiteSpace(persisted.Json))
		{
			_rundown = RundownCanonicalSerializer.Deserialize(persisted.Json);
			await ValidateReferencesAsync(_rundown, cancellationToken).ConfigureAwait(false);
		}

		if (_rundown is not null)
		{
			var show = await _showControl.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
			var currentItem = TryMapShowControlItem(show);
			if (show.Execution.State == ShowControlExecutionState.RecoveryRequired && currentItem is not null)
			{
				_execution = new RundownExecutionSnapshot(
					RundownExecutionState.RecoveryRequired,
					_rundown.RundownId,
					currentItem,
					null,
					currentItem,
					PeekNext(currentItem.Value),
					1,
					show.Execution.ExecutionId?.Value,
					false,
					true,
					show.Execution.Failure);
			}
			else
			{
				_execution = RundownExecutionSnapshot.Idle with
				{
					RundownId = _rundown.RundownId,
					SelectedItemId = currentItem ?? _rundown.Items[0].ItemId,
					CurrentItemId = show.Execution.State == ShowControlExecutionState.Completed ? currentItem : null,
					NextItemId = currentItem is null ? _rundown.Items[0].ItemId : PeekNext(currentItem.Value),
					Revision = 1
				};
			}
		}
		_loaded = true;
	}

	private async ValueTask PrepareLockedAsync(RundownItemId itemId, CancellationToken cancellationToken)
	{
		EnsureNavigationAllowed();
		var rundown = RequireRundown();
		var item = rundown.Items.SingleOrDefault(candidate => candidate.ItemId == itemId)
			?? throw new KeyNotFoundException($"Rundown item '{itemId}' does not exist.");

		CancelAutoAdvanceWorker();
		var singleItem = new RundownDefinition(
			rundown.Version,
			rundown.RundownId,
			rundown.Name,
			[item]);
		var cueList = RundownShowControlAdapter.BuildCueList(singleItem);
		await _showControl.SaveCueListAsync(cueList, cancellationToken).ConfigureAwait(false);
		await _showControl.SelectCueListAsync(cueList.CueListId, cancellationToken).ConfigureAwait(false);
		var show = await _showControl.ArmAsync(cancellationToken).ConfigureAwait(false);

		_execution = _execution with
		{
			State = RundownExecutionState.Prepared,
			RundownId = rundown.RundownId,
			SelectedItemId = itemId,
			PreparedItemId = itemId,
			CurrentItemId = null,
			NextItemId = PeekNext(itemId),
			CausalActionId = show.Execution.ExecutionId?.Value,
			AutoAdvanceArmed = false,
			RequiresAcknowledgement = false,
			Failure = null,
			Revision = NextRevision()
		};
		_stateChanged();
	}

	private async ValueTask GoPreparedLockedAsync(CancellationToken cancellationToken)
	{
		var rundown = RequireRundown();
		if (_execution.State != RundownExecutionState.Prepared || _execution.PreparedItemId is not { } itemId)
			throw new InvalidOperationException("Rundown GO requires a prepared item.");

		var item = rundown.Items.Single(candidate => candidate.ItemId == itemId);
		ShowControlWorkspaceSnapshot show;
		try
		{
			show = await _showControl.GoAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or FormatException)
		{
			_execution = Failed(itemId, "rundown.execution.failed", exception.Message);
			_stateChanged();
			return;
		}

		if (show.Execution.State == ShowControlExecutionState.Failed)
		{
			_execution = Failed(
				itemId,
				show.Execution.Failure?.Code ?? "rundown.execution.failed",
				show.Execution.Failure?.Message ?? "Show-control execution failed.");
			_stateChanged();
			return;
		}
		if (show.Execution.State == ShowControlExecutionState.RecoveryRequired)
		{
			_execution = _execution with
			{
				State = RundownExecutionState.RecoveryRequired,
				PreparedItemId = null,
				CurrentItemId = itemId,
				CausalActionId = show.Execution.ExecutionId?.Value,
				AutoAdvanceArmed = false,
				RequiresAcknowledgement = true,
				Failure = show.Execution.Failure,
				Revision = NextRevision()
			};
			_stateChanged();
			return;
		}

		var autoAdvance = item is RundownMediaItem &&
			item.AdvanceMode == RundownAdvanceMode.AutoOnMediaEnd;
		_execution = _execution with
		{
			State = show.Execution.State == ShowControlExecutionState.Waiting || autoAdvance
				? RundownExecutionState.Executing
				: RundownExecutionState.Held,
			PreparedItemId = null,
			CurrentItemId = itemId,
			NextItemId = PeekNext(itemId),
			CausalActionId = show.Execution.ExecutionId?.Value,
			AutoAdvanceArmed = autoAdvance,
			RequiresAcknowledgement = false,
			Failure = null,
			Revision = NextRevision()
		};
		_stateChanged();

		if (show.Execution.State == ShowControlExecutionState.Waiting)
			StartShowControlCompletionWorker(itemId, _execution.Revision);
		else if (autoAdvance)
			StartMediaAutoAdvanceWorker((RundownMediaItem)item, _execution.Revision);
	}

	private void StartShowControlCompletionWorker(RundownItemId itemId, ulong revision)
	{
		CancelAutoAdvanceWorker();
		_autoAdvanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token);
		var token = _autoAdvanceCancellation.Token;
		_autoAdvanceWorker = Task.Run(async () =>
		{
			while (!token.IsCancellationRequested)
			{
				var show = await _showControl.GetSnapshotAsync(token).ConfigureAwait(false);
				if (show.Execution.State is ShowControlExecutionState.Completed or ShowControlExecutionState.Failed or ShowControlExecutionState.RecoveryRequired)
				{
					await ApplyObservedShowControlCompletionAsync(itemId, revision, show, token).ConfigureAwait(false);
					return;
				}
				await Task.Delay(ObservationInterval, token).ConfigureAwait(false);
			}
		}, token);
	}

	private void StartMediaAutoAdvanceWorker(RundownMediaItem item, ulong revision)
	{
		if (_mediaDeck is null)
		{
			_execution = Failed(item.ItemId, "rundown.media_deck.unavailable", "Media-deck control service is not configured.");
			_stateChanged();
			return;
		}

		CancelAutoAdvanceWorker();
		_autoAdvanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token);
		var token = _autoAdvanceCancellation.Token;
		_autoAdvanceWorker = Task.Run(async () =>
		{
			var observedPlaying = false;
			while (!token.IsCancellationRequested)
			{
				var deck = await _mediaDeck.GetSnapshotAsync(token).ConfigureAwait(false);
				if (deck.Probe?.AssetId == new MediaAssetId(item.AssetId))
				{
					observedPlaying |= deck.State == MediaDeckState.Playing;
					if (observedPlaying && deck.State == MediaDeckState.Ended)
					{
						await AutoAdvanceAsync(item.ItemId, revision, token).ConfigureAwait(false);
						return;
					}
					if (deck.State == MediaDeckState.Error)
					{
						await FailObservedMediaAsync(item.ItemId, revision, deck.Failure, token).ConfigureAwait(false);
						return;
					}
				}
				await Task.Delay(ObservationInterval, token).ConfigureAwait(false);
			}
		}, token);
	}

	private async Task ApplyObservedShowControlCompletionAsync(
		RundownItemId itemId,
		ulong revision,
		ShowControlWorkspaceSnapshot show,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_execution.Revision != revision ||
				_execution.CurrentItemId != itemId ||
				_execution.State != RundownExecutionState.Executing)
			{
				return;
			}
			if (show.Execution.State == ShowControlExecutionState.Failed)
			{
				_execution = Failed(
					itemId,
					show.Execution.Failure?.Code ?? "rundown.execution.failed",
					show.Execution.Failure?.Message ?? "Show-control execution failed.");
			}
			else if (show.Execution.State == ShowControlExecutionState.RecoveryRequired)
			{
				_execution = _execution with
				{
					State = RundownExecutionState.RecoveryRequired,
					RequiresAcknowledgement = true,
					AutoAdvanceArmed = false,
					Failure = show.Execution.Failure,
					Revision = NextRevision()
				};
			}
			else
			{
				_execution = _execution with
				{
					State = RundownExecutionState.Held,
					AutoAdvanceArmed = false,
					Revision = NextRevision()
				};
			}
			_stateChanged();
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task AutoAdvanceAsync(
		RundownItemId itemId,
		ulong revision,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_execution.Revision != revision ||
				_execution.CurrentItemId != itemId ||
				_execution.State != RundownExecutionState.Executing ||
				!_execution.AutoAdvanceArmed)
			{
				return;
			}

			var next = ResolveNext(itemId);
			if (next is null)
			{
				_execution = _execution with
				{
					State = RundownExecutionState.Completed,
					AutoAdvanceArmed = false,
					NextItemId = null,
					Revision = NextRevision()
				};
				_stateChanged();
				return;
			}

			_execution = _execution with { AutoAdvanceArmed = false, Revision = NextRevision() };
			await PrepareLockedAsync(next.Value, cancellationToken).ConfigureAwait(false);
			await GoPreparedLockedAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task FailObservedMediaAsync(
		RundownItemId itemId,
		ulong revision,
		Failure? failure,
		CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_execution.Revision != revision ||
				_execution.CurrentItemId != itemId ||
				_execution.State != RundownExecutionState.Executing)
			{
				return;
			}
			_execution = Failed(
				itemId,
				failure?.Code ?? "rundown.media.failed",
				failure?.Message ?? "Media playback failed.");
			_stateChanged();
		}
		finally
		{
			_gate.Release();
		}
	}

	private async ValueTask ValidateReferencesAsync(
		RundownDefinition rundown,
		CancellationToken cancellationToken)
	{
		var control = RequireControl();
		var sources = control.Specification.Sources.Select(source => source.SourceId).ToHashSet();
		var scenes = control.Specification.Scenes.Select(scene => scene.SceneId).ToHashSet();
		MediaAssetCatalogSnapshot? catalog = null;
		if (rundown.Items.Any(item => item is RundownMediaItem))
		{
			if (_mediaCatalog is null)
				throw new InvalidDataException("Media rundown items require the persistent media catalogue.");
			catalog = await _mediaCatalog.GetSnapshotAsync(refreshAvailability: false, cancellationToken: cancellationToken).ConfigureAwait(false);
		}

		foreach (var item in rundown.Items)
		{
			switch (item)
			{
				case RundownMediaItem media:
					if (!sources.Contains(media.SourceId))
						throw new InvalidDataException($"Rundown media item '{media.ItemId}' references unknown source '{media.SourceId}'.");
					if (catalog!.Assets.All(asset => asset.AssetId.Value != media.AssetId))
						throw new InvalidDataException($"Rundown media item '{media.ItemId}' references unknown persistent asset '{media.AssetId}'.");
					break;
				case RundownSceneItem scene when !scenes.Contains(scene.SceneId):
					throw new InvalidDataException($"Rundown Scene item '{scene.ItemId}' references unknown Scene '{scene.SceneId}'.");
				case RundownGraphicsItem graphics when
					graphics.LayerId is not (ProductionCompositingLayerIds.BitmapGraphics or ProductionCompositingLayerIds.ProductionCg):
					throw new InvalidDataException($"Rundown graphics item '{graphics.ItemId}' references unsupported bounded layer '{graphics.LayerId}'.");
				case RundownAudioRoutingItem audio when
					audio.BreakawaySourceId is { } sourceId && !sources.Contains(sourceId):
					throw new InvalidDataException($"Rundown audio item '{audio.ItemId}' references unknown source '{sourceId}'.");
			}
		}
	}

	private RundownItemId? ResolveNext(RundownItemId current)
	{
		var rundown = RequireRundown();
		var item = rundown.Items[IndexOf(current)];
		if (item.Repeat.Mode == RundownRepeatMode.RepeatItem)
		{
			var completed = _itemRepeatProgress.GetValueOrDefault(current);
			if (completed < item.Repeat.RepeatCount)
			{
				_itemRepeatProgress[current] = checked((ushort)(completed + 1));
				return current;
			}
			_itemRepeatProgress.Remove(current);
		}

		var index = IndexOf(current);
		if (index + 1 < rundown.Items.Count)
			return rundown.Items[index + 1].ItemId;

		if (item.Repeat.Mode == RundownRepeatMode.RepeatRundown &&
			_rundownRepeatProgress < item.Repeat.RepeatCount)
		{
			_rundownRepeatProgress++;
			_itemRepeatProgress.Clear();
			return rundown.Items[0].ItemId;
		}
		return null;
	}

	private RundownItemId? PeekNext(RundownItemId current)
	{
		var rundown = RequireRundown();
		var index = IndexOf(current);
		return index + 1 < rundown.Items.Count ? rundown.Items[index + 1].ItemId : null;
	}

	private RundownItemId? TryMapShowControlItem(ShowControlWorkspaceSnapshot show)
	{
		if (_rundown is null || show.Execution.CurrentCueId is not { } cueId)
			return null;
		var itemId = new RundownItemId(cueId.Value);
		return _rundown.Items.Any(item => item.ItemId == itemId) ? itemId : null;
	}

	private int IndexOf(RundownItemId itemId)
	{
		var rundown = RequireRundown();
		for (var index = 0; index < rundown.Items.Count; index++)
		{
			if (rundown.Items[index].ItemId == itemId)
				return index;
		}
		throw new KeyNotFoundException($"Rundown item '{itemId}' does not exist.");
	}

	private void EnsureEditable()
	{
		if (_execution.State is RundownExecutionState.Executing or RundownExecutionState.RecoveryRequired)
			throw new InvalidOperationException("Rundown cannot be edited while execution is active or awaiting recovery.");
	}

	private void EnsureNavigationAllowed()
	{
		if (_execution.State == RundownExecutionState.Executing)
			throw new InvalidOperationException("Rundown navigation cannot race active execution.");
		if (_execution.State == RundownExecutionState.RecoveryRequired)
			throw new InvalidOperationException("Rundown recovery must be acknowledged before navigation.");
	}

	private RundownDefinition RequireRundown() =>
		_rundown ?? throw new InvalidOperationException("No rundown is authored.");

	private ControlHostService RequireControl()
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			throw new InvalidOperationException("ControlHost has no committed authoritative state yet.");
		return control;
	}

	private RundownExecutionSnapshot Failed(RundownItemId itemId, string code, string message) =>
		_execution with
		{
			State = RundownExecutionState.Failed,
			PreparedItemId = null,
			CurrentItemId = itemId,
			AutoAdvanceArmed = false,
			RequiresAcknowledgement = false,
			Failure = new Failure(code, message),
			Revision = NextRevision()
		};

	private ulong NextRevision() =>
		_execution.Revision == ulong.MaxValue
			? throw new InvalidOperationException("Rundown execution revision is exhausted.")
			: _execution.Revision + 1;

	private RundownWorkspaceSnapshot Snapshot() =>
		new(_rundown, _execution, _storageVersion);

	private void CancelAutoAdvanceWorker()
	{
		try { _autoAdvanceCancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
	}
}
