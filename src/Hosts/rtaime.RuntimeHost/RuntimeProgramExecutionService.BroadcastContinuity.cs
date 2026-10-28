// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Source/authority-fenced software Program/Aux continuity observation.
/// This is not a hardware fallback player: no automatic cached-frame replay,
/// mute, slate, sink switch or unauthorized Program state mutation is possible.
/// </summary>
public sealed partial class RuntimeProgramExecutionService
{
	private readonly RuntimeBroadcastContinuityTracker _broadcastContinuity = new();

	public BroadcastContinuitySnapshot? GetContinuityObservation(string roleId)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return _broadcastContinuity.ForRole(roleId);
		}
	}

	public IReadOnlyList<BroadcastContinuitySnapshot> RecentContinuityIncidents
	{
		get
		{
			lock (_gate) return _broadcastContinuity.RecentIncidents;
		}
	}

	private void ObserveContinuityAtCompletedBoundaryUnsafe(
		MediaSourceId committedSource, ulong sequence, ulong audioSamplePosition,
		ulong? auxAudioSamplePosition, Failure? auxFailure)
	{
		// Called only after Program video write, final audio production and
		// downstream failure-isolated consumer submissions have completed.
		// Input fallback black is evidence of signal loss, not an all-clear.
		if (_inputSignals.TryGetValue(committedSource, out var input) &&
			input == RuntimeInputSignalState.Lost &&
			!_broadcastTestPatternSources.Contains(committedSource))
		{
			_broadcastContinuity.ObserveFault("program", "runtime.continuity.input_lost",
				"Committed Program input was LOST; any fallback-black frame is not qualified continuity.");
		}
		else
			_broadcastContinuity.ObserveComplete("program", sequence, audioSamplePosition);

		if (auxFailure is { } aux)
			_broadcastContinuity.ObserveFault("aux", aux.Code,
				"Committed Aux provider failed; Program routing remains unchanged.");
		else if (_auxSinkId is not null && _confirmedAuxFrame is { } auxFrame &&
			auxFrame.Frame.Timing.SequenceNumber == sequence)
			_broadcastContinuity.ObserveComplete("aux", sequence, auxAudioSamplePosition);
	}

	private void ObserveContinuityOnFailedBoundary(Exception failure)
	{
		// The existing boundary executor owns _boundaryExecutionGate. Failure
		// recording is a management observation; it does not retry or send output.
		lock (_gate)
		{
			if (_runtime.ActiveExecution is null) return;
			var code = failure switch
			{
				OperationCanceledException => "runtime.continuity.boundary_cancelled",
				InvalidDataException => "runtime.continuity.invalid_media",
				_ => "runtime.continuity.boundary_failed"
			};
			_broadcastContinuity.ObserveFault("program", code,
				"Program boundary aborted without a complete frame and audio confirmation; no approved failover was executed.");
		}
	}

	private Failure? ContinuityRoleFaultUnsafe(string role)
	{
		var snapshot = _broadcastContinuity.ForRole(role);
		if (snapshot is not { Phase: BroadcastContinuityPhase.Failed }) return null;
		return new Failure("runtime.continuity.output_failed",
			$"Broadcast continuity incident {snapshot.IncidentGeneration}: {snapshot.IncidentCode}; last complete Program/Aux frame is diagnostic only, not confirmed hold-last output.");
	}

	private string ContinuityRoleEvidenceUnsafe(string role)
	{
		var snapshot = _broadcastContinuity.ForRole(role);
		if (snapshot is null)
			return "CONTINUITY UNAVAILABLE: no committed role observation.";
		return snapshot.Phase switch
		{
			BroadcastContinuityPhase.ObservedComplete =>
				$"CONTINUITY SOFTWARE OBSERVED · Control revision {snapshot.AuthorityRevision}, Runtime execution {snapshot.RuntimeExecutionRevision}, last complete video frame {snapshot.LastCompleteFrameSequence}, audio sample position {snapshot.LastCompleteAudioSamplePosition?.ToString() ?? "UNVERIFIED"}; physical scan-out UNVERIFIED.",
			BroadcastContinuityPhase.Failed =>
				$"CONTINUITY FAILED · incident {snapshot.IncidentGeneration} / {snapshot.IncidentCode}, Control revision {snapshot.AuthorityRevision}; no qualified HoldLast/Mute/Slate output; re-arm required.",
			BroadcastContinuityPhase.RecoveredAwaitingRearm =>
				$"CONTINUITY RECOVERED AWAITING RE-ARM · incident {snapshot.IncidentGeneration}, Control revision {snapshot.AuthorityRevision}; software frames resumed but operator acknowledgement/Control approval NOT CONFIRMED.",
			_ => "CONTINUITY UNVERIFIED · no complete Program/Aux frame+audio observation yet."
		};
	}

	private bool ContinuityHealthyUnsafe(string role) =>
		_broadcastContinuity.CanReportHealthy(role);
}
