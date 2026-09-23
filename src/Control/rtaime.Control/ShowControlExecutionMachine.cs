// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Control;

public sealed class ShowControlExecutionMachine
{
	private ShowControlCueList? _cueList;
	private ShowControlExecutionSnapshot _snapshot = ShowControlExecutionSnapshot.Idle;

	public ShowControlExecutionSnapshot Snapshot => _snapshot;
	public ShowControlCueList? CueList => _cueList;

	public ShowControlExecutionSnapshot Arm(ShowControlCueList cueList)
	{
		ArgumentNullException.ThrowIfNull(cueList);
		if (_snapshot.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting)
			throw new InvalidOperationException("An executing show-control cue list cannot be replaced.");
		if (_snapshot.State == ShowControlExecutionState.RecoveryRequired)
			throw new InvalidOperationException("Recovery must be acknowledged or cancelled before arming another cue list.");

		_cueList = cueList;
		var firstCue = cueList.Cues[0];
		_snapshot = new ShowControlExecutionSnapshot(
			ShowControlContractVersion.Current,
			ShowControlExecutionId.New(),
			cueList.CueListId,
			ShowControlExecutionState.Armed,
			0,
			0,
			firstCue.CueId,
			firstCue.Actions[0].ActionId,
			NextRevision(),
			null,
			null,
			false,
			null);
		return _snapshot;
	}

	public ShowControlExecutionSnapshot BeginGo()
	{
		RequireCueList();
		if (_snapshot.State != ShowControlExecutionState.Armed)
			throw new InvalidOperationException($"GO is not valid while show control is '{_snapshot.State}'.");
		return SetState(ShowControlExecutionState.Executing);
	}

	public ShowControlExecutionSnapshot BeginWait(ulong targetFrameSequence, string runtimeHostInstanceId)
	{
		if (_snapshot.State != ShowControlExecutionState.Executing)
			throw new InvalidOperationException("A frame wait can begin only while executing.");
		if (string.IsNullOrWhiteSpace(runtimeHostInstanceId))
			throw new ArgumentException("RuntimeHost instance identity is required for a frame-domain wait.", nameof(runtimeHostInstanceId));

		_snapshot = _snapshot with
		{
			State = ShowControlExecutionState.Waiting,
			ExecutionRevision = NextRevision(),
			WaitTargetFrameSequence = targetFrameSequence,
			RuntimeHostInstanceId = runtimeHostInstanceId.Trim(),
			Failure = null
		};
		return _snapshot;
	}

	public ShowControlExecutionSnapshot CompleteWait(ulong observedFrameSequence, string runtimeHostInstanceId)
	{
		if (_snapshot.State != ShowControlExecutionState.Waiting)
			throw new InvalidOperationException("No show-control frame wait is active.");
		if (!string.Equals(_snapshot.RuntimeHostInstanceId, runtimeHostInstanceId, StringComparison.Ordinal))
			throw new InvalidOperationException("RuntimeHost instance changed while a frame-domain wait was active.");
		if (!_snapshot.WaitTargetFrameSequence.HasValue || observedFrameSequence < _snapshot.WaitTargetFrameSequence.Value)
			throw new InvalidOperationException("The frame-domain wait target has not been reached.");

		_snapshot = _snapshot with
		{
			State = ShowControlExecutionState.Executing,
			ExecutionRevision = NextRevision(),
			WaitTargetFrameSequence = null,
			RuntimeHostInstanceId = null
		};
		return CompleteCurrentAction();
	}

	public ShowControlExecutionSnapshot CompleteCurrentAction()
	{
		var cueList = RequireCueList();
		if (_snapshot.State != ShowControlExecutionState.Executing)
			throw new InvalidOperationException("An action can complete only while executing.");
		var cueIndex = _snapshot.CueIndex ?? throw new InvalidOperationException("Show-control cue cursor is unavailable.");
		var actionIndex = _snapshot.ActionIndex ?? throw new InvalidOperationException("Show-control action cursor is unavailable.");
		var cue = cueList.Cues[cueIndex];

		if (actionIndex + 1 < cue.Actions.Count)
		{
			var nextActionIndex = actionIndex + 1;
			_snapshot = _snapshot with
			{
				ActionIndex = nextActionIndex,
				CurrentActionId = cue.Actions[nextActionIndex].ActionId,
				ExecutionRevision = NextRevision(),
				WaitTargetFrameSequence = null,
				RuntimeHostInstanceId = null,
				Failure = null
			};
			return _snapshot;
		}

		if (cueIndex + 1 >= cueList.Cues.Count)
		{
			_snapshot = _snapshot with
			{
				State = ShowControlExecutionState.Completed,
				ActionIndex = null,
				CurrentActionId = null,
				ExecutionRevision = NextRevision(),
				WaitTargetFrameSequence = null,
				RuntimeHostInstanceId = null,
				Failure = null
			};
			return _snapshot;
		}

		var nextCueIndex = cueIndex + 1;
		var nextCue = cueList.Cues[nextCueIndex];
		_snapshot = _snapshot with
		{
			State = ShowControlExecutionState.Armed,
			CueIndex = nextCueIndex,
			ActionIndex = 0,
			CurrentCueId = nextCue.CueId,
			CurrentActionId = nextCue.Actions[0].ActionId,
			ExecutionRevision = NextRevision(),
			WaitTargetFrameSequence = null,
			RuntimeHostInstanceId = null,
			Failure = null
		};
		return _snapshot;
	}

	public ShowControlExecutionSnapshot Fail(Failure failure)
	{
		ArgumentNullException.ThrowIfNull(failure);
		if (_snapshot.State is not (ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting))
			throw new InvalidOperationException("Only active show-control execution can fail.");

		_snapshot = _snapshot with
		{
			State = ShowControlExecutionState.Failed,
			ExecutionRevision = NextRevision(),
			WaitTargetFrameSequence = null,
			RuntimeHostInstanceId = null,
			RequiresAcknowledgement = false,
			Failure = failure
		};
		return _snapshot;
	}

	public ShowControlExecutionSnapshot Cancel()
	{
		if (_snapshot.State == ShowControlExecutionState.Idle)
			return _snapshot;
		if (_snapshot.State == ShowControlExecutionState.Completed)
			return _snapshot;

		_snapshot = _snapshot with
		{
			State = ShowControlExecutionState.Cancelled,
			ExecutionRevision = NextRevision(),
			WaitTargetFrameSequence = null,
			RuntimeHostInstanceId = null,
			RequiresAcknowledgement = false,
			Failure = null
		};
		return _snapshot;
	}

	public ShowControlExecutionSnapshot Restore(ShowControlCueList cueList, ShowControlExecutionSnapshot persisted)
	{
		ArgumentNullException.ThrowIfNull(cueList);
		ArgumentNullException.ThrowIfNull(persisted);
		ShowControlContractVersion.EnsureSupported(persisted.Version);
		if (persisted.CueListId != cueList.CueListId)
			throw new InvalidDataException("Persisted show-control execution references a different cue list.");

		ValidateCursor(cueList, persisted);
		_cueList = cueList;

		if (persisted.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting)
		{
			_snapshot = persisted with
			{
				State = ShowControlExecutionState.RecoveryRequired,
				ExecutionRevision = checked(persisted.ExecutionRevision + 1),
				WaitTargetFrameSequence = null,
				RuntimeHostInstanceId = null,
				RequiresAcknowledgement = true,
				Failure = new Failure(
					"show_control.recovery.uncertain_action",
					"ControlHost restarted while an action was active; automatic replay is blocked.")
			};
			return _snapshot;
		}

		_snapshot = persisted;
		return _snapshot;
	}

	public ShowControlExecutionSnapshot AcknowledgeRecovery(bool resume)
	{
		var cueList = RequireCueList();
		if (_snapshot.State != ShowControlExecutionState.RecoveryRequired || !_snapshot.RequiresAcknowledgement)
			throw new InvalidOperationException("Show-control recovery acknowledgement is not required.");

		if (!resume)
			return Cancel();

		var cueIndex = _snapshot.CueIndex ?? throw new InvalidOperationException("Recovery cue cursor is unavailable.");
		var actionIndex = _snapshot.ActionIndex ?? throw new InvalidOperationException("Recovery action cursor is unavailable.");
		var action = cueList.Cues[cueIndex].Actions[actionIndex];
		if (!action.ReplaySafeAfterUncertainCompletion)
			throw new InvalidOperationException($"Action '{action.Kind}' cannot be replayed safely after uncertain completion; cancel the execution instead.");

		_snapshot = _snapshot with
		{
			State = ShowControlExecutionState.Armed,
			ExecutionRevision = NextRevision(),
			WaitTargetFrameSequence = null,
			RuntimeHostInstanceId = null,
			RequiresAcknowledgement = false,
			Failure = null
		};
		return _snapshot;
	}

	public ShowControlAction CurrentAction()
	{
		var cueList = RequireCueList();
		var cueIndex = _snapshot.CueIndex ?? throw new InvalidOperationException("Show-control cue cursor is unavailable.");
		var actionIndex = _snapshot.ActionIndex ?? throw new InvalidOperationException("Show-control action cursor is unavailable.");
		return cueList.Cues[cueIndex].Actions[actionIndex];
	}

	private ShowControlExecutionSnapshot SetState(ShowControlExecutionState state)
	{
		_snapshot = _snapshot with
		{
			State = state,
			ExecutionRevision = NextRevision(),
			Failure = null
		};
		return _snapshot;
	}

	private ShowControlCueList RequireCueList() =>
		_cueList ?? throw new InvalidOperationException("No show-control cue list is loaded.");

	private ulong NextRevision() =>
		_snapshot.ExecutionRevision == ulong.MaxValue
			? throw new InvalidOperationException("Show-control execution revision is exhausted.")
			: _snapshot.ExecutionRevision + 1;

	private static void ValidateCursor(ShowControlCueList cueList, ShowControlExecutionSnapshot snapshot)
	{
		if (snapshot.State == ShowControlExecutionState.Idle)
			return;
		if (!snapshot.CueIndex.HasValue || snapshot.CueIndex.Value < 0 || snapshot.CueIndex.Value >= cueList.Cues.Count)
			throw new InvalidDataException("Persisted show-control cue cursor is invalid.");

		var cue = cueList.Cues[snapshot.CueIndex.Value];
		if (snapshot.CurrentCueId != cue.CueId)
			throw new InvalidDataException("Persisted show-control cue identity does not match its cursor.");

		if (snapshot.State == ShowControlExecutionState.Completed)
			return;
		if (!snapshot.ActionIndex.HasValue || snapshot.ActionIndex.Value < 0 || snapshot.ActionIndex.Value >= cue.Actions.Count)
			throw new InvalidDataException("Persisted show-control action cursor is invalid.");
		if (snapshot.CurrentActionId != cue.Actions[snapshot.ActionIndex.Value].ActionId)
			throw new InvalidDataException("Persisted show-control action identity does not match its cursor.");
	}
}
