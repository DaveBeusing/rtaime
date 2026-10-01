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
	private bool _rundownRecoveryAmbiguous;

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
			await RefreshFrameCountdownLockedAsync(cancellationToken).ConfigureAwait(false);
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
			await CancelForManualOverrideLockedAsync("prepare another item", cancellationToken).ConfigureAwait(false);
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
			if (_execution.State == RundownExecutionState.Prepared)
			{
				await GoPreparedLockedAsync(cancellationToken).ConfigureAwait(false);
				return Snapshot();
			}

			if (_execution.State == RundownExecutionState.Executing &&
				_execution.AutoAdvanceArmed &&
				_execution.PendingNextItemId is { } pending)
			{
				await CancelForManualOverrideLockedAsync("manual GO", cancellationToken).ConfigureAwait(false);
				await PrepareLockedAsync(pending, cancellationToken).ConfigureAwait(false);
				await GoPreparedLockedAsync(cancellationToken).ConfigureAwait(false);
				return Snapshot();
			}

			throw new InvalidOperationException("Rundown GO requires a prepared item or an armed pending follow target.");
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
			await CancelForManualOverrideLockedAsync("manual NEXT", cancellationToken).ConfigureAwait(false);
			EnsureNavigationAllowed();

			var anchor = _execution.CurrentItemId ?? _execution.PreparedItemId ?? _execution.SelectedItemId
				?? RequireRundown().Items[0].ItemId;
			var next = ResolveNext(anchor);
			if (next is null)
			{
				SetCompleted(anchor);
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
			await CancelForManualOverrideLockedAsync("manual PREVIOUS", cancellationToken).ConfigureAwait(false);
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
			var show = await _showControl.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
			if (show.Execution.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting)
				await _showControl.CancelAsync(cancellationToken).ConfigureAwait(false);

			_execution = ClearAutomation(_execution) with
			{
				State = RundownExecutionState.Held,
				Revision = NextRevision()
			};
			Journal("rundown.automation.held", "Rundown automation was held by the operator.", _execution.CausalActionId);
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

			if (_rundownRecoveryAmbiguous)
			{
				var pending = _execution.PendingNextItemId;
				var show = await _showControl.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
				if (show.Execution.State == ShowControlExecutionState.RecoveryRequired)
					await _showControl.AcknowledgeRecoveryAsync(resume: false, cancellationToken).ConfigureAwait(false);

				_rundownRecoveryAmbiguous = false;
				_execution = ClearAutomation(_execution) with
				{
					State = RundownExecutionState.Held,
					RequiresAcknowledgement = false,
					Failure = null,
					Revision = NextRevision()
				};
				if (resume && pending is { } next)
				{
					await PrepareLockedAsync(next, cancellationToken, cancelAutomationWorker: false, preserveCurrentItem: true).ConfigureAwait(false);
				}
				else
				{
					_stateChanged();
				}
				return Snapshot();
			}

			var recoveredShow = await _showControl.AcknowledgeRecoveryAsync(resume, cancellationToken).ConfigureAwait(false);
			_execution = ClearAutomation(_execution) with
			{
				State = resume ? RundownExecutionState.Prepared : RundownExecutionState.Held,
				PreparedItemId = resume ? _execution.CurrentItemId : null,
				RequiresAcknowledgement = false,
				Failure = null,
				CausalActionId = recoveredShow.Execution.ExecutionId?.Value,
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
			var currentItemId = TryMapShowControlItem(show);
			var currentItem = currentItemId is { } id
				? _rundown.Items.FirstOrDefault(item => item.ItemId == id)
				: null;

			if (show.Execution.State == ShowControlExecutionState.RecoveryRequired && currentItem is not null)
			{
				var pending = PreviewResolvedNext(currentItem.ItemId);
				_rundownRecoveryAmbiguous = IsAutomatedFollow(currentItem.FollowAction.Kind);
				_execution = new RundownExecutionSnapshot(
					RundownExecutionState.RecoveryRequired,
					_rundown.RundownId,
					currentItem.ItemId,
					null,
					currentItem.ItemId,
					pending,
					1,
					show.Execution.ExecutionId?.Value,
					false,
					true,
					show.Execution.Failure,
					currentItem.FollowAction.Kind,
					pending,
					currentItem.FollowAction.DelayFrames,
					show.Execution.RuntimeHostInstanceId,
					show.Execution.WaitTargetFrameSequence.HasValue && currentItem.FollowAction.DelayFrames.HasValue
						? show.Execution.WaitTargetFrameSequence.Value - Math.Min(show.Execution.WaitTargetFrameSequence.Value, currentItem.FollowAction.DelayFrames.Value)
						: null,
					show.Execution.WaitTargetFrameSequence,
					null,
					RemainingItemRepeats(currentItem.ItemId),
					RemainingRundownRepeats(currentItem.ItemId));
				Journal("rundown.recovery.required", "Rundown execution was interrupted; automatic progression will not be replayed.", show.Execution.ExecutionId?.Value, show.Execution.Failure);
			}
			else if (show.Execution.State == ShowControlExecutionState.Armed && currentItem is not null)
			{
				_execution = DecorateRepeatState(RundownExecutionSnapshot.Idle with
				{
					State = RundownExecutionState.Prepared,
					RundownId = _rundown.RundownId,
					SelectedItemId = currentItem.ItemId,
					PreparedItemId = currentItem.ItemId,
					NextItemId = PreviewResolvedNext(currentItem.ItemId),
					CausalActionId = show.Execution.ExecutionId?.Value,
					FollowActionKind = currentItem.FollowAction.Kind,
					Revision = 1
				}, currentItem.ItemId);
			}
			else if (show.Execution.State == ShowControlExecutionState.Failed && currentItem is not null)
			{
				_execution = Failed(
					currentItem.ItemId,
					show.Execution.Failure?.Code ?? "rundown.execution.failed",
					show.Execution.Failure?.Message ?? "Show-control execution failed.") with
				{
					RundownId = _rundown.RundownId,
					CausalActionId = show.Execution.ExecutionId?.Value
				};
			}
			else if (show.Execution.State == ShowControlExecutionState.Completed && currentItem is not null &&
				IsAutomatedFollow(currentItem.FollowAction.Kind) &&
				PreviewResolvedNext(currentItem.ItemId) is { } pending)
			{
				_rundownRecoveryAmbiguous = true;
				var failure = new Failure(
					"rundown.recovery.pending_follow_ambiguous",
					"ControlHost restarted after item completion while an automatic follow action may have been pending; automatic replay is blocked.");
				_execution = DecorateRepeatState(new RundownExecutionSnapshot(
					RundownExecutionState.RecoveryRequired,
					_rundown.RundownId,
					currentItem.ItemId,
					null,
					currentItem.ItemId,
					pending,
					1,
					show.Execution.ExecutionId?.Value,
					false,
					true,
					failure,
					currentItem.FollowAction.Kind,
					pending,
					currentItem.FollowAction.DelayFrames), currentItem.ItemId);
				Journal("rundown.recovery.required", failure.Message, show.Execution.ExecutionId?.Value, failure);
			}
			else
			{
				_execution = RundownExecutionSnapshot.Idle with
				{
					RundownId = _rundown.RundownId,
					SelectedItemId = currentItemId ?? _rundown.Items[0].ItemId,
					CurrentItemId = show.Execution.State == ShowControlExecutionState.Completed ? currentItemId : null,
					NextItemId = currentItemId is null ? _rundown.Items[0].ItemId : PreviewResolvedNext(currentItemId.Value),
					Revision = 1
				};
			}
		}
		_loaded = true;
	}

	private async ValueTask PrepareLockedAsync(
		RundownItemId itemId,
		CancellationToken cancellationToken,
		bool cancelAutomationWorker = true,
		bool preserveCurrentItem = false)
	{
		EnsureNavigationAllowed();
		var rundown = RequireRundown();
		var item = rundown.Items.SingleOrDefault(candidate => candidate.ItemId == itemId)
			?? throw new KeyNotFoundException($"Rundown item '{itemId}' does not exist.");

		if (cancelAutomationWorker)
			CancelAutoAdvanceWorker();
		var priorCurrent = preserveCurrentItem ? _execution.CurrentItemId : null;
		var singleItem = new RundownDefinition(
			rundown.Version,
			rundown.RundownId,
			rundown.Name,
			[item]);
		var cueList = RundownShowControlAdapter.BuildCueList(singleItem);
		await _showControl.SaveCueListAsync(cueList, cancellationToken).ConfigureAwait(false);
		await _showControl.SelectCueListAsync(cueList.CueListId, cancellationToken).ConfigureAwait(false);
		var show = await _showControl.ArmAsync(cancellationToken).ConfigureAwait(false);

		_execution = DecorateRepeatState(ClearAutomation(_execution) with
		{
			State = RundownExecutionState.Prepared,
			RundownId = rundown.RundownId,
			SelectedItemId = itemId,
			PreparedItemId = itemId,
			CurrentItemId = priorCurrent,
			NextItemId = PreviewResolvedNext(itemId),
			CausalActionId = show.Execution.ExecutionId?.Value,
			FollowActionKind = item.FollowAction.Kind,
			RequiresAcknowledgement = false,
			Failure = null,
			Revision = NextRevision()
		}, itemId);
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
			_execution = DecorateRepeatState(ClearAutomation(_execution) with
			{
				State = RundownExecutionState.RecoveryRequired,
				PreparedItemId = null,
				CurrentItemId = itemId,
				CausalActionId = show.Execution.ExecutionId?.Value,
				RequiresAcknowledgement = true,
				Failure = show.Execution.Failure,
				FollowActionKind = item.FollowAction.Kind,
				Revision = NextRevision()
			}, itemId);
			Journal("rundown.recovery.required", "Show Control reported ambiguous rundown execution.", show.Execution.ExecutionId?.Value, show.Execution.Failure);
			_stateChanged();
			return;
		}

		var automated = IsAutomatedFollow(item.FollowAction.Kind);
		var pending = automated ? PreviewResolvedNext(itemId) : null;
		_execution = DecorateRepeatState(ClearAutomation(_execution) with
		{
			State = show.Execution.State == ShowControlExecutionState.Waiting || automated
				? RundownExecutionState.Executing
				: RundownExecutionState.Held,
			PreparedItemId = null,
			CurrentItemId = itemId,
			NextItemId = pending ?? PreviewResolvedNext(itemId),
			CausalActionId = show.Execution.ExecutionId?.Value,
			AutoAdvanceArmed = automated,
			RequiresAcknowledgement = false,
			Failure = null,
			FollowActionKind = item.FollowAction.Kind,
			PendingNextItemId = pending,
			FollowDelayFrames = item.FollowAction.DelayFrames,
			Revision = NextRevision()
		}, itemId);
		if (automated)
			Journal("rundown.follow.armed", $"Follow action {item.FollowAction.Kind} armed for item '{item.ItemId}'.", show.Execution.ExecutionId?.Value);
		else if (item.FollowAction.Kind == RundownFollowActionKind.Hold)
			Journal("rundown.automation.held", $"Follow action HOLD reached for item '{item.ItemId}'.", show.Execution.ExecutionId?.Value);
		_stateChanged();

		if (show.Execution.State == ShowControlExecutionState.Waiting)
			StartShowControlCompletionWorker(itemId, _execution.Revision);
		else if (item.FollowAction.Kind == RundownFollowActionKind.AutoOnMediaEnd)
			StartMediaAutoAdvanceWorker((RundownMediaItem)item, _execution.Revision);
		else if (automated)
			StartConfirmedCompletionWorker(itemId, _execution.Revision);
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
				Journal("rundown.follow.cancelled", "Rundown progression stopped because Show Control failed.", show.Execution.ExecutionId?.Value, show.Execution.Failure);
			}
			else if (show.Execution.State == ShowControlExecutionState.RecoveryRequired)
			{
				_rundownRecoveryAmbiguous = _execution.AutoAdvanceArmed || _execution.FollowTargetFrameSequence.HasValue;
				_execution = _execution with
				{
					State = RundownExecutionState.RecoveryRequired,
					RequiresAcknowledgement = true,
					AutoAdvanceArmed = false,
					Failure = show.Execution.Failure,
					RuntimeHostInstanceId = show.Execution.RuntimeHostInstanceId ?? _execution.RuntimeHostInstanceId,
					FollowTargetFrameSequence = show.Execution.WaitTargetFrameSequence ?? _execution.FollowTargetFrameSequence,
					Revision = NextRevision()
				};
				Journal("rundown.recovery.required", "Rundown follow execution requires operator recovery acknowledgement.", show.Execution.ExecutionId?.Value, show.Execution.Failure);
			}
			else if (_execution.FollowTargetFrameSequence.HasValue)
			{
				await FireDelayedFollowLockedAsync(itemId, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await HandleConfirmedCompletionLockedAsync(itemId, revision, cancellationToken).ConfigureAwait(false);
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

			await HandleConfirmedCompletionLockedAsync(itemId, revision, cancellationToken).ConfigureAwait(false);
			_stateChanged();
		}
		finally
		{
			_gate.Release();
		}
	}

	private void StartConfirmedCompletionWorker(RundownItemId itemId, ulong revision)
	{
		CancelAutoAdvanceWorker();
		_autoAdvanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token);
		var token = _autoAdvanceCancellation.Token;
		_autoAdvanceWorker = Task.Run(async () =>
		{
			await Task.Yield();
			await AutoAdvanceAsync(itemId, revision, token).ConfigureAwait(false);
		}, CancellationToken.None);
	}

	private async ValueTask HandleConfirmedCompletionLockedAsync(
		RundownItemId itemId,
		ulong revision,
		CancellationToken cancellationToken)
	{
		if (_execution.Revision != revision ||
			_execution.CurrentItemId != itemId ||
			_execution.State != RundownExecutionState.Executing)
		{
			return;
		}

		var item = RequireRundown().Items[IndexOf(itemId)];
		switch (item.FollowAction.Kind)
		{
			case RundownFollowActionKind.Manual:
			case RundownFollowActionKind.Hold:
				_execution = ClearAutomation(_execution) with
				{
					State = RundownExecutionState.Held,
					FollowActionKind = item.FollowAction.Kind,
					Revision = NextRevision()
				};
				if (item.FollowAction.Kind == RundownFollowActionKind.Hold)
					Journal("rundown.automation.held", $"Rundown held after item '{item.ItemId}'.", _execution.CausalActionId);
				return;
			case RundownFollowActionKind.PrepareNext:
				await FollowToNextLockedAsync(item, prepareOnly: true, cancellationToken).ConfigureAwait(false);
				return;
			case RundownFollowActionKind.AutoGoNext:
			case RundownFollowActionKind.AutoOnMediaEnd:
				await FollowToNextLockedAsync(item, prepareOnly: false, cancellationToken).ConfigureAwait(false);
				return;
			case RundownFollowActionKind.AutoGoNextAfterFrames:
				await BeginDelayedFollowLockedAsync(item, cancellationToken).ConfigureAwait(false);
				return;
			default:
				throw new InvalidDataException($"Unsupported rundown follow action '{item.FollowAction.Kind}'.");
		}
	}

	private async ValueTask FollowToNextLockedAsync(
		RundownItem item,
		bool prepareOnly,
		CancellationToken cancellationToken)
	{
		var expected = _execution.PendingNextItemId;
		var next = ResolveNext(item.ItemId);
		if (expected != next)
		{
			_execution = Failed(item.ItemId, "rundown.follow.stale_target", "The armed follow target no longer matches the bounded rundown sequence.");
			Journal("rundown.follow.invalidated", "Armed follow target was invalidated before execution.", _execution.CausalActionId, _execution.Failure);
			return;
		}
		if (next is null)
		{
			SetCompleted(item.ItemId);
			return;
		}

		Journal("rundown.follow.fired", $"Follow action {item.FollowAction.Kind} selected item '{next.Value}'.", _execution.CausalActionId);
		_execution = ClearAutomation(_execution) with
		{
			State = RundownExecutionState.Held,
			CurrentItemId = item.ItemId,
			Revision = NextRevision()
		};
		await PrepareLockedAsync(next.Value, cancellationToken, cancelAutomationWorker: false, preserveCurrentItem: true).ConfigureAwait(false);
		if (!prepareOnly)
			await GoPreparedLockedAsync(cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask BeginDelayedFollowLockedAsync(RundownItem item, CancellationToken cancellationToken)
	{
		var expected = _execution.PendingNextItemId;
		var next = ResolveNext(item.ItemId);
		if (expected != next)
		{
			_execution = Failed(item.ItemId, "rundown.follow.stale_target", "The armed delayed follow target no longer matches the bounded rundown sequence.");
			Journal("rundown.follow.invalidated", "Delayed follow target was invalidated before timing began.", _execution.CausalActionId, _execution.Failure);
			return;
		}
		if (next is null)
		{
			SetCompleted(item.ItemId);
			return;
		}

		var frames = item.FollowAction.DelayFrames
			?? throw new InvalidDataException("Delayed follow action has no frame count.");
		var rundown = RequireRundown();
		var delayItem = new RundownHoldItem(
			item.ItemId,
			"Follow delay",
			frames,
			RundownRepeatPolicy.None,
			RundownFollowAction.Manual);
		var delayRundown = new RundownDefinition(rundown.Version, rundown.RundownId, rundown.Name, [delayItem]);
		var cueList = RundownShowControlAdapter.BuildCueList(delayRundown);
		await _showControl.SaveCueListAsync(cueList, cancellationToken).ConfigureAwait(false);
		await _showControl.SelectCueListAsync(cueList.CueListId, cancellationToken).ConfigureAwait(false);
		await _showControl.ArmAsync(cancellationToken).ConfigureAwait(false);
		var show = await _showControl.GoAsync(cancellationToken).ConfigureAwait(false);
		if (show.Execution.State == ShowControlExecutionState.Failed)
		{
			_execution = Failed(item.ItemId,
				show.Execution.Failure?.Code ?? "rundown.follow.delay.failed",
				show.Execution.Failure?.Message ?? "Delayed rundown follow failed.");
			return;
		}
		if (show.Execution.State == ShowControlExecutionState.RecoveryRequired)
		{
			_rundownRecoveryAmbiguous = true;
			_execution = _execution with
			{
				State = RundownExecutionState.RecoveryRequired,
				AutoAdvanceArmed = false,
				RequiresAcknowledgement = true,
				Failure = show.Execution.Failure,
				Revision = NextRevision()
			};
			return;
		}
		if (show.Execution.State != ShowControlExecutionState.Waiting ||
			show.Execution.WaitTargetFrameSequence is not { } target ||
			string.IsNullOrWhiteSpace(show.Execution.RuntimeHostInstanceId))
		{
			_execution = Failed(item.ItemId, "rundown.follow.delay.no_timing_evidence", "Show Control did not produce authoritative production-frame timing for the delayed follow.");
			return;
		}

		var start = target >= frames ? target - frames : 0;
		_execution = DecorateRepeatState(_execution with
		{
			State = RundownExecutionState.Executing,
			CurrentItemId = item.ItemId,
			NextItemId = next,
			PendingNextItemId = next,
			CausalActionId = show.Execution.ExecutionId?.Value,
			AutoAdvanceArmed = true,
			FollowActionKind = RundownFollowActionKind.AutoGoNextAfterFrames,
			FollowDelayFrames = frames,
			RuntimeHostInstanceId = show.Execution.RuntimeHostInstanceId,
			FollowStartFrameSequence = start,
			FollowTargetFrameSequence = target,
			RemainingFollowFrames = frames,
			Revision = NextRevision()
		}, item.ItemId);
		Journal("rundown.follow.delay.started", $"Delayed follow armed from Runtime frame {start} to {target} for item '{item.ItemId}'.", show.Execution.ExecutionId?.Value);
		_stateChanged();
		StartShowControlCompletionWorker(item.ItemId, _execution.Revision);
	}

	private async ValueTask FireDelayedFollowLockedAsync(
		RundownItemId itemId,
		CancellationToken cancellationToken)
	{
		if (_execution.PendingNextItemId is not { } next)
		{
			SetCompleted(itemId);
			return;
		}

		Journal("rundown.follow.delay.completed", $"Delayed follow reached production frame {_execution.FollowTargetFrameSequence}.", _execution.CausalActionId);
		Journal("rundown.follow.fired", $"Delayed follow selected item '{next}'.", _execution.CausalActionId);
		_execution = ClearAutomation(_execution) with
		{
			State = RundownExecutionState.Held,
			CurrentItemId = itemId,
			Revision = NextRevision()
		};
		await PrepareLockedAsync(next, cancellationToken, cancelAutomationWorker: false, preserveCurrentItem: true).ConfigureAwait(false);
		await GoPreparedLockedAsync(cancellationToken).ConfigureAwait(false);
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
				Journal("rundown.repeat.current", $"Repeat-current iteration {_itemRepeatProgress[current]} of {item.Repeat.RepeatCount} selected for item '{current}'.", current.Value);
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
			Journal("rundown.repeat.rundown", $"Rundown repeat iteration {_rundownRepeatProgress} of {item.Repeat.RepeatCount} selected.", current.Value);
			return rundown.Items[0].ItemId;
		}
		return null;
	}

	private RundownItemId? PreviewResolvedNext(RundownItemId current)
	{
		var rundown = RequireRundown();
		var item = rundown.Items[IndexOf(current)];
		if (item.Repeat.Mode == RundownRepeatMode.RepeatItem &&
			_itemRepeatProgress.GetValueOrDefault(current) < item.Repeat.RepeatCount)
		{
			return current;
		}

		var index = IndexOf(current);
		if (index + 1 < rundown.Items.Count)
			return rundown.Items[index + 1].ItemId;

		if (item.Repeat.Mode == RundownRepeatMode.RepeatRundown &&
			_rundownRepeatProgress < item.Repeat.RepeatCount)
		{
			return rundown.Items[0].ItemId;
		}
		return null;
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
		ClearAutomation(_execution) with
		{
			State = RundownExecutionState.Failed,
			PreparedItemId = null,
			CurrentItemId = itemId,
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

	private async ValueTask CancelForManualOverrideLockedAsync(string reason, CancellationToken cancellationToken)
	{
		if (_execution.State != RundownExecutionState.Executing)
			return;

		CancelAutoAdvanceWorker();
		var show = await _showControl.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		if (show.Execution.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting)
			await _showControl.CancelAsync(cancellationToken).ConfigureAwait(false);

		var previousAction = _execution.FollowActionKind;
		_execution = ClearAutomation(_execution) with
		{
			State = RundownExecutionState.Held,
			Revision = NextRevision()
		};
		Journal("rundown.follow.invalidated", $"Pending {previousAction} automation was invalidated by {reason}.", _execution.CausalActionId);
		_stateChanged();
	}

	private async ValueTask RefreshFrameCountdownLockedAsync(CancellationToken cancellationToken)
	{
		if (_execution.State != RundownExecutionState.Executing ||
			_execution.FollowTargetFrameSequence is not { } target ||
			string.IsNullOrWhiteSpace(_execution.RuntimeHostInstanceId))
		{
			return;
		}

		try
		{
			var observation = await _showControl.ObserveFrameAsync(cancellationToken).ConfigureAwait(false);
			if (!string.Equals(observation.RuntimeHostInstanceId, _execution.RuntimeHostInstanceId, StringComparison.Ordinal))
				return;
			var remaining = observation.NextFrameSequence >= target
				? 0U
				: checked((uint)Math.Min(uint.MaxValue, target - observation.NextFrameSequence));
			_execution = _execution with { RemainingFollowFrames = remaining };
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
		{
			// Show Control owns timing failure/recovery. Snapshot enrichment must not create a second authority path.
		}
	}

	private static bool IsAutomatedFollow(RundownFollowActionKind kind) =>
		kind is RundownFollowActionKind.PrepareNext or
			RundownFollowActionKind.AutoGoNext or
			RundownFollowActionKind.AutoGoNextAfterFrames or
			RundownFollowActionKind.AutoOnMediaEnd;

	private RundownExecutionSnapshot DecorateRepeatState(RundownExecutionSnapshot snapshot, RundownItemId itemId) =>
		snapshot with
		{
			RemainingItemRepeats = RemainingItemRepeats(itemId),
			RemainingRundownRepeats = RemainingRundownRepeats(itemId)
		};

	private ushort RemainingItemRepeats(RundownItemId itemId)
	{
		var item = RequireRundown().Items[IndexOf(itemId)];
		if (item.Repeat.Mode != RundownRepeatMode.RepeatItem)
			return 0;
		var completed = _itemRepeatProgress.GetValueOrDefault(itemId);
		return completed >= item.Repeat.RepeatCount ? (ushort)0 : checked((ushort)(item.Repeat.RepeatCount - completed));
	}

	private ushort RemainingRundownRepeats(RundownItemId itemId)
	{
		_ = itemId;
		var rundown = RequireRundown();
		var repeat = rundown.Items[^1].Repeat;
		if (repeat.Mode != RundownRepeatMode.RepeatRundown)
			return 0;
		return _rundownRepeatProgress >= repeat.RepeatCount
			? (ushort)0
			: checked((ushort)(repeat.RepeatCount - _rundownRepeatProgress));
	}

	private static RundownExecutionSnapshot ClearAutomation(RundownExecutionSnapshot snapshot) =>
		snapshot with
		{
			AutoAdvanceArmed = false,
			PendingNextItemId = null,
			FollowDelayFrames = null,
			RuntimeHostInstanceId = null,
			FollowStartFrameSequence = null,
			FollowTargetFrameSequence = null,
			RemainingFollowFrames = null
		};

	private void SetCompleted(RundownItemId itemId)
	{
		_execution = ClearAutomation(_execution) with
		{
			State = RundownExecutionState.Completed,
			CurrentItemId = itemId,
			PreparedItemId = null,
			NextItemId = null,
			Revision = NextRevision()
		};
		Journal("rundown.follow.fired", $"Rundown reached its bounded end after item '{itemId}'.", _execution.CausalActionId);
	}

	private void Journal(string code, string detail, Identity? causationId, Failure? failure = null)
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			return;
		control.RecordRundownEvent(code, detail, causationId, failure);
	}

	private void CancelAutoAdvanceWorker()
	{
		try { _autoAdvanceCancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
	}
}
