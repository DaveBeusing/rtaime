// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
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

	private void ConfirmContinuityAfterRuntimeCommitUnsafe(
		PreparedExecutionContract prepared, ulong executionRevision, MediaSinkId programSinkId)
	{
		try
		{
			_broadcastContinuity.ConfirmCommittedExecution(prepared, executionRevision, programSinkId);
		}
		catch (Exception error) when (error is InvalidDataException or ArgumentException
			or NotSupportedException or OverflowException)
		{
			// A diagnostic tracker cannot make an already committed Runtime plan
			// appear rejected or roll it back. Revoke all old continuity evidence.
			_broadcastContinuity.InvalidateActiveRoles();
			Observe("runtime.continuity.admission_unverified");
		}
	}

	private void TryObserveContinuityAtCompletedBoundaryUnsafe(
		MediaSourceId source, ulong sequence, ulong audioSamplePosition,
		ulong? auxAudioSamplePosition, Failure? auxFailure)
	{
		try
		{
			ObserveContinuityAtCompletedBoundaryUnsafe(
				source, sequence, audioSamplePosition, auxAudioSamplePosition, auxFailure);
		}
		catch (Exception problem) when (problem is ArgumentException or InvalidDataException
			or InvalidOperationException or OverflowException)
		{
			// A diagnostic observer is not an output provider: it cannot reverse
			// a completed Program boundary or force a second output submission.
			_broadcastContinuity.InvalidateActiveRoles();
			Observe("runtime.continuity.observer_unverified");
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
		{
			if (_inputSignals.TryGetValue(auxFrame.Frame.SourceId, out var auxInput) &&
				auxInput == RuntimeInputSignalState.Lost &&
				!_broadcastTestPatternSources.Contains(auxFrame.Frame.SourceId))
				_broadcastContinuity.ObserveFault("aux", "runtime.continuity.input_lost",
					"Committed Aux input signal was LOST; any fallback-black frame is not qualified continuity.");
			else
				_broadcastContinuity.ObserveComplete("aux", sequence, auxAudioSamplePosition);
		}
	}

	private void TryObserveContinuityOnFailedBoundary(Exception failure)
	{
		try { ObserveContinuityOnFailedBoundary(failure); }
		catch (Exception problem) when (problem is ArgumentException or InvalidDataException
			or InvalidOperationException or OverflowException)
		{
			lock (_gate)
			{
				_broadcastContinuity.InvalidateActiveRoles();
				Observe("runtime.continuity.observer_unverified");
			}
		}
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
				$"CONTINUITY FAILED · incident {snapshot.IncidentGeneration} / {snapshot.IncidentCode}, UTC {snapshot.IncidentStartedAtUtc:O}, Control revision {snapshot.AuthorityRevision}, Runtime execution {snapshot.RuntimeExecutionRevision}, last complete video {snapshot.LastCompleteFrameSequence?.ToString() ?? "UNVERIFIED"} / audio {snapshot.LastCompleteAudioSamplePosition?.ToString() ?? "UNVERIFIED"}; no qualified HoldLast/Mute/Slate output; re-arm required.",
			BroadcastContinuityPhase.RecoveredAwaitingRearm =>
				$"CONTINUITY RECOVERED AWAITING RE-ARM · incident {snapshot.IncidentGeneration}, Control revision {snapshot.AuthorityRevision}; software frames resumed but operator acknowledgement/Control approval NOT CONFIRMED.",
			_ => "CONTINUITY UNVERIFIED · no complete Program/Aux frame+audio observation yet."
		};
	}

	private bool ContinuityHealthyUnsafe(string role) =>
		_broadcastContinuity.CanReportHealthy(role);
}
