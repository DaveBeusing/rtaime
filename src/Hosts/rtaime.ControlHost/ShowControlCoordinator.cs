// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.ControlHost;

public sealed record ShowControlFrameObservation(
	string RuntimeHostInstanceId,
	ulong NextFrameSequence,
	TimeSpan FrameBudget);

public delegate ValueTask<Failure?> ShowControlActionExecutor(
	ShowControlAction action,
	CancellationToken cancellationToken);

public delegate ValueTask<ShowControlFrameObservation> ShowControlFrameObserver(
	CancellationToken cancellationToken);

public sealed class ShowControlCoordinator : IAsyncDisposable
{
	private readonly Func<ControlHostService?> _controlAccessor;
	private readonly ShowControlPersistenceStore _persistence;
	private readonly ShowControlActionExecutor _actionExecutor;
	private readonly ShowControlFrameObserver _frameObserver;
	private readonly Action _stateChanged;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Dictionary<ShowControlCueListId, ShowControlCueList> _cueLists = new();
	private readonly ShowControlExecutionMachine _machine = new();
	private ShowControlCueListId? _selectedCueListId;
	private ulong _storageVersion;
	private bool _restored;
	private CancellationTokenSource? _waitCancellation;
	private Task? _waitWorker;

	public ShowControlCoordinator(
		Func<ControlHostService?> controlAccessor,
		ShowControlPersistenceStore persistence,
		ShowControlActionExecutor actionExecutor,
		ShowControlFrameObserver frameObserver,
		Action stateChanged)
	{
		_controlAccessor = controlAccessor ?? throw new ArgumentNullException(nameof(controlAccessor));
		_persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
		_actionExecutor = actionExecutor ?? throw new ArgumentNullException(nameof(actionExecutor));
		_frameObserver = frameObserver ?? throw new ArgumentNullException(nameof(frameObserver));
		_stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> SaveCueListAsync(
		ShowControlCueList cueList,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(cueList);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			ValidateCueListReferences(cueList);
			if (_machine.Snapshot.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting or ShowControlExecutionState.RecoveryRequired &&
				_machine.Snapshot.CueListId == cueList.CueListId)
			{
				throw new InvalidOperationException("The active show-control cue list cannot be edited while execution is active or awaiting recovery.");
			}

			_cueLists[cueList.CueListId] = cueList;
			_selectedCueListId ??= cueList.CueListId;
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.cue_list.saved", $"Cue list '{cueList.Name}' ({cueList.CueListId}) was saved.", cueList.CueListId.Value);
			_stateChanged();
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> SelectCueListAsync(
		ShowControlCueListId cueListId,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			if (!_cueLists.ContainsKey(cueListId))
				throw new KeyNotFoundException($"Show-control cue list '{cueListId}' does not exist.");
			if (_machine.Snapshot.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting or ShowControlExecutionState.RecoveryRequired)
				throw new InvalidOperationException("Cue-list selection cannot change while show control is active or awaiting recovery.");

			_selectedCueListId = cueListId;
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.cue_list.selected", $"Cue list '{cueListId}' was selected.", cueListId.Value);
			_stateChanged();
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> ArmAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			var cueList = SelectedCueList();
			ValidateCueListReferences(cueList);
			_machine.Arm(cueList);
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.armed", $"Cue list '{cueList.Name}' was armed.", _machine.Snapshot.ExecutionId?.Value);
			_stateChanged();
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> GoAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			_machine.BeginGo();
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.go", CurrentCursorDetail("GO"), _machine.Snapshot.ExecutionId?.Value);
			_stateChanged();
			await ExecuteActiveActionsLockedAsync(cancellationToken).ConfigureAwait(false);
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> CancelAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			CancelWaitWorker();
			_machine.Cancel();
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.cancelled", CurrentCursorDetail("Execution cancelled"), _machine.Snapshot.ExecutionId?.Value);
			_stateChanged();
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> AcknowledgeRecoveryAsync(
		bool resume,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			_machine.AcknowledgeRecovery(resume);
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal(
				resume ? "show_control.recovery.resume_acknowledged" : "show_control.recovery.cancel_acknowledged",
				CurrentCursorDetail(resume ? "Recovery resume acknowledged" : "Recovery cancellation acknowledged"),
				_machine.Snapshot.ExecutionId?.Value);
			_stateChanged();
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		CancelWaitWorker();
		var worker = _waitWorker;
		if (worker is not null)
		{
			try { await worker.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_waitCancellation?.Dispose();
		_gate.Dispose();
	}

	private async ValueTask ExecuteActiveActionsLockedAsync(CancellationToken cancellationToken)
	{
		while (_machine.Snapshot.State == ShowControlExecutionState.Executing)
		{
			var action = _machine.CurrentAction();
			if (action.Kind == ShowControlActionKind.WaitFrames)
			{
				await BeginFrameWaitLockedAsync(action, cancellationToken).ConfigureAwait(false);
				return;
			}

			Journal(
				"show_control.action.started",
				CurrentCursorDetail($"Action {action.Kind} started"),
				action.ActionId.Value);
			await PersistAsync(cancellationToken).ConfigureAwait(false);

			Failure? failure;
			try
			{
				failure = await _actionExecutor(action, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				failure = new Failure("show_control.action.exception", exception.Message);
			}

			if (failure is not null)
			{
				_machine.Fail(failure);
				await PersistAsync(cancellationToken).ConfigureAwait(false);
				Journal("show_control.action.failed", CurrentCursorDetail(failure.Message), action.ActionId.Value, failure);
				_stateChanged();
				return;
			}

			Journal(
				"show_control.action.completed",
				CurrentCursorDetail($"Action {action.Kind} completed"),
				action.ActionId.Value);
			_machine.CompleteCurrentAction();
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			_stateChanged();
		}
	}

	private async ValueTask BeginFrameWaitLockedAsync(ShowControlAction action, CancellationToken cancellationToken)
	{
		var frames = action.WaitFrames ?? throw new InvalidOperationException("Frame wait action has no frame count.");
		ShowControlFrameObservation observation;
		try
		{
			observation = await _frameObserver(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			var failure = new Failure("show_control.wait.runtime_unavailable", $"Runtime frame timing is unavailable: {exception.Message}");
			_machine.Fail(failure);
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.wait.failed", failure.Message, action.ActionId.Value, failure);
			_stateChanged();
			return;
		}

		ulong target;
		try { target = checked(observation.NextFrameSequence + frames); }
		catch (OverflowException)
		{
			var failure = new Failure("show_control.wait.sequence_exhausted", "Runtime frame sequence cannot represent the requested wait target.");
			_machine.Fail(failure);
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.wait.failed", failure.Message, action.ActionId.Value, failure);
			_stateChanged();
			return;
		}

		_machine.BeginWait(target, observation.RuntimeHostInstanceId);
		await PersistAsync(cancellationToken).ConfigureAwait(false);
		Journal(
			"show_control.wait.started",
			$"{CurrentCursorDetail("Frame wait started")}; target sequence {target}.",
			action.ActionId.Value);
		_stateChanged();

		CancelWaitWorker();
		_waitCancellation = new CancellationTokenSource();
		var token = _waitCancellation.Token;
		var timeout = WaitTimeout(frames, observation.FrameBudget);
		_waitWorker = Task.Run(() => WaitForFrameTargetAsync(target, observation.RuntimeHostInstanceId, timeout, token), CancellationToken.None);
	}

	private async Task WaitForFrameTargetAsync(
		ulong targetFrameSequence,
		string runtimeHostInstanceId,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (!cancellationToken.IsCancellationRequested)
		{
			ShowControlFrameObservation observation;
			try
			{
				observation = await _frameObserver(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch
			{
				if (DateTimeOffset.UtcNow >= deadline)
				{
					await FailWaitAsync(
						new Failure("show_control.wait.timeout", "Runtime frame timing did not recover before the bounded wait timeout."),
						cancellationToken).ConfigureAwait(false);
					return;
				}
				await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (!string.Equals(observation.RuntimeHostInstanceId, runtimeHostInstanceId, StringComparison.Ordinal))
			{
				await RequireRecoveryAsync(
					new Failure("show_control.wait.runtime_restarted", "RuntimeHost changed while a production-frame wait was active."),
					cancellationToken).ConfigureAwait(false);
				return;
			}

			if (observation.NextFrameSequence >= targetFrameSequence)
			{
				await CompleteWaitAsync(observation, cancellationToken).ConfigureAwait(false);
				return;
			}

			if (DateTimeOffset.UtcNow >= deadline)
			{
				await FailWaitAsync(
					new Failure("show_control.wait.timeout", $"Production frame sequence did not reach {targetFrameSequence} before the bounded wait timeout."),
					cancellationToken).ConfigureAwait(false);
				return;
			}

			var poll = observation.FrameBudget > TimeSpan.Zero
				? TimeSpan.FromTicks(Math.Clamp(observation.FrameBudget.Ticks / 4, TimeSpan.FromMilliseconds(2).Ticks, TimeSpan.FromMilliseconds(20).Ticks))
				: TimeSpan.FromMilliseconds(10);
			await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task CompleteWaitAsync(ShowControlFrameObservation observation, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_machine.Snapshot.State != ShowControlExecutionState.Waiting)
				return;
			var actionId = _machine.Snapshot.CurrentActionId?.Value;
			_machine.CompleteWait(observation.NextFrameSequence, observation.RuntimeHostInstanceId);
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.wait.completed", $"Frame wait completed at Runtime sequence {observation.NextFrameSequence}.", actionId);
			_stateChanged();
			await ExecuteActiveActionsLockedAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task FailWaitAsync(Failure failure, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_machine.Snapshot.State != ShowControlExecutionState.Waiting)
				return;
			var actionId = _machine.Snapshot.CurrentActionId?.Value;
			_machine.Fail(failure);
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.wait.failed", failure.Message, actionId, failure);
			_stateChanged();
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task RequireRecoveryAsync(Failure failure, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_machine.Snapshot.State != ShowControlExecutionState.Waiting)
				return;
			var actionId = _machine.Snapshot.CurrentActionId?.Value;
			_machine.RequireRecovery(failure);
			await PersistAsync(cancellationToken).ConfigureAwait(false);
			Journal("show_control.recovery.required", failure.Message, actionId, failure);
			_stateChanged();
		}
		finally
		{
			_gate.Release();
		}
	}

	private async ValueTask EnsureRestoredAsync(CancellationToken cancellationToken)
	{
		if (_restored)
			return;
		var control = RequireControl();
		var persisted = await _persistence.LoadAsync(control.Specification.ProductionId, cancellationToken).ConfigureAwait(false);
		_cueLists.Clear();
		foreach (var cueList in persisted.CueLists)
		{
			ValidateCueListReferences(cueList);
			_cueLists.Add(cueList.CueListId, cueList);
		}
		_selectedCueListId = persisted.SelectedCueListId;
		_storageVersion = persisted.StorageVersion;

		if (persisted.Execution.State != ShowControlExecutionState.Idle)
		{
			var cueListId = persisted.Execution.CueListId
				?? throw new InvalidDataException("Persisted show-control execution has no cue-list identity.");
			if (!_cueLists.TryGetValue(cueListId, out var cueList))
				throw new InvalidDataException("Persisted show-control execution cue list is unavailable.");
			var restored = _machine.Restore(cueList, persisted.Execution);
			if (restored.State == ShowControlExecutionState.RecoveryRequired)
			{
				await PersistAsync(cancellationToken).ConfigureAwait(false);
				Journal(
					"show_control.recovery.required",
					"ControlHost restart interrupted active show-control execution; operator acknowledgement is required.",
					restored.ExecutionId?.Value,
					restored.Failure);
			}
		}
		_restored = true;
	}

	private async ValueTask PersistAsync(CancellationToken cancellationToken)
	{
		var control = RequireControl();
		var orderedLists = _cueLists.Values
			.OrderBy(list => list.Name, StringComparer.Ordinal)
			.ThenBy(list => list.CueListId.ToString(), StringComparer.Ordinal)
			.ToArray();
		var write = await _persistence.SaveAsync(
			control.Specification.ProductionId,
			orderedLists,
			_selectedCueListId,
			_machine.Snapshot,
			_storageVersion,
			cancellationToken).ConfigureAwait(false);
		if (!write.Written || write.Persisted is null)
			throw new InvalidOperationException(write.Failure?.Message ?? "Show-control workspace could not be persisted.");
		_storageVersion = write.Persisted.StorageVersion;
	}

	private void ValidateCueListReferences(ShowControlCueList cueList)
	{
		var control = RequireControl();
		foreach (var action in cueList.Cues.SelectMany(cue => cue.Actions))
		{
			switch (action.Kind)
			{
				case ShowControlActionKind.ActivateScene:
				var sceneId = new SceneId(Identity.Parse(action.SceneId!));
				if (!control.Specification.Scenes.Any(scene => scene.SceneId == sceneId))
					throw new InvalidDataException($"Show-control action references unknown Scene '{sceneId}'.");
				break;
			case ShowControlActionKind.SetPreview:
				var sourceId = new ProductionSourceId(Identity.Parse(action.SourceId!));
				if (!control.Specification.Sources.Any(source => source.SourceId == sourceId))
					throw new InvalidDataException($"Show-control action references unknown production source '{sourceId}'.");
				break;
			case ShowControlActionKind.JumpMediaCue:
			case ShowControlActionKind.MediaPlay:
			case ShowControlActionKind.MediaPause:
			case ShowControlActionKind.MediaStop:
				_ = Identity.Parse(action.MediaAssetId!);
				if (action.Kind == ShowControlActionKind.JumpMediaCue)
					_ = Identity.Parse(action.MediaCuePointId!);
				break;
		}
	}

	private ShowControlCueList SelectedCueList()
	{
		if (!_selectedCueListId.HasValue)
			throw new InvalidOperationException("No show-control cue list is selected.");
		return _cueLists.TryGetValue(_selectedCueListId.Value, out var cueList)
			? cueList
			: throw new InvalidOperationException("Selected show-control cue list is unavailable.");
	}

	private ShowControlWorkspaceSnapshot WorkspaceSnapshot() =>
		new(
			_cueLists.Values
				.OrderBy(list => list.Name, StringComparer.Ordinal)
				.ThenBy(list => list.CueListId.ToString(), StringComparer.Ordinal)
				.ToArray(),
			_selectedCueListId,
			_machine.Snapshot);

	private ControlHostService RequireControl()
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			throw new InvalidOperationException("ControlHost has no committed authoritative state yet.");
		return control;
	}

	private void Journal(string code, string detail, Identity? causationId, Failure? failure = null)
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			return;
		control.RecordShowControlEvent(code, detail, causationId, failure);
	}

	private string CurrentCursorDetail(string prefix) =>
		$"{prefix}; state={_machine.Snapshot.State}; cue={_machine.Snapshot.CueIndex?.ToString() ?? "-"}; action={_machine.Snapshot.ActionIndex?.ToString() ?? "-"}.";

	private static TimeSpan WaitTimeout(uint frames, TimeSpan frameBudget)
	{
		var budget = frameBudget > TimeSpan.Zero ? frameBudget : TimeSpan.FromMilliseconds(40);
		var expectedTicks = Math.Min((double)TimeSpan.MaxValue.Ticks, budget.Ticks * (double)frames);
		var expected = TimeSpan.FromTicks((long)expectedTicks);
		var timeout = expected + TimeSpan.FromSeconds(5);
		if (timeout < TimeSpan.FromSeconds(10))
			timeout = TimeSpan.FromSeconds(10);
		if (timeout > TimeSpan.FromMinutes(30))
			timeout = TimeSpan.FromMinutes(30);
		return timeout;
	}

	private void CancelWaitWorker()
	{
		try { _waitCancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
	}
}
