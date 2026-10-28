// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Runtime-local software output/incident bookkeeping for the ControlHost-committed plan.
/// No GPU/PCM buffers retained and no new output, clock, command, retry or failover authority.
/// Methods are invoked under the RuntimeProgramExecutionService metadata lock.
/// </summary>
internal sealed class RuntimeBroadcastContinuityTracker
{
	private sealed class Role
	{
		public required string Name;
		public required string Source;
		public required string Sink;
		public required string AuthorityId;
		public ulong AuthorityRevision;
		public ulong ExecutionRevision;
		public BroadcastFailurePolicy Policy = BroadcastFailurePolicy.Conservative("program");
		public BroadcastContinuityPhase Phase = BroadcastContinuityPhase.AwaitingFirstBoundary;
		public ulong? LastCompleteVideo;
		public ulong? LastCompleteAudio;
		public ulong IncidentGeneration;
		public string? IncidentCode;
		public string? IncidentDetail;
		public DateTimeOffset? IncidentAt;
	}

	private readonly Dictionary<string, Role> _roles = new(StringComparer.Ordinal);
	private readonly Queue<BroadcastContinuitySnapshot> _incidents = new();
	private ulong _nextIncident;
	public const int HistoryLimit = 32;

	public IReadOnlyList<BroadcastContinuitySnapshot> RecentIncidents =>
		Array.AsReadOnly(_incidents.ToArray());

	public BroadcastContinuitySnapshot? ForRole(string roleId) =>
		_roles.TryGetValue(roleId, out var role) ? Read(role) : null;

	/// <summary>
	/// Admission only after TransactionalRuntime.Commit succeeded; a rejected prepare
	/// or abandoned commit leaves all previously confirmed source and incident data intact.
	/// A same-identity resync or unrelated Control revision does not clear a latched recovery incident.
	/// </summary>
	public void ConfirmCommittedExecution(PreparedExecutionContract prepared, ulong executionRevision, MediaSinkId programSinkId)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		if (executionRevision == 0)
			throw new InvalidDataException("Continuity admission requires a committed execution revision.");
		var candidates = new Dictionary<string, Role>(StringComparer.Ordinal);
		foreach (var binding in prepared.Bindings.Where(x => x.OutputRoleId is "program" or "aux" || x.MediaSinkId == programSinkId))
		{
			var roleId = binding.MediaSinkId == programSinkId ? "program" : binding.OutputRoleId!;
			if (binding.MediaSourceId is null || binding.MediaSinkId is null || candidates.ContainsKey(roleId))
				throw new InvalidDataException("Committed continuity role requires one unique real source and sink binding.");
			var policy = BroadcastFailurePolicy.Conservative(roleId);
			policy.EnsureRuntimeAdmitted(); // No implicit hold/mute/slate without provider.
			var authorityId = prepared.AuthoritySnapshot.StateId.ToString();
			var source = binding.MediaSourceId.Value.ToString();
			var sink = binding.MediaSinkId.Value.ToString();
			if (_roles.TryGetValue(roleId, out var old) &&
				old.AuthorityId == authorityId &&
				old.Source == source && old.Sink == sink)
			{
				// An unrelated revision cannot acknowledge or erase the incident after a
				// RuntimeHost restart/resync. Keep fault and last completed evidence.
				candidates.Add(roleId, new Role
				{
					Name = roleId, Source = source, Sink = sink,
					AuthorityId = authorityId, AuthorityRevision = prepared.AuthoritySnapshot.Revision.Value,
					ExecutionRevision = executionRevision, Policy = policy,
					Phase = old.Phase, LastCompleteVideo = old.LastCompleteVideo,
					LastCompleteAudio = old.LastCompleteAudio,
					IncidentCode = old.IncidentCode, IncidentDetail = old.IncidentDetail,
					IncidentAt = old.IncidentAt, IncidentGeneration = old.IncidentGeneration
				});
				continue;
			}
			candidates.Add(roleId, new Role
			{
				Name = roleId, Source = source, Sink = sink,
				AuthorityId = authorityId,
				AuthorityRevision = prepared.AuthoritySnapshot.Revision.Value,
				ExecutionRevision = executionRevision, Policy = policy
			});
		}
		if (!candidates.ContainsKey("program"))
			throw new InvalidDataException("Continuity requires a single committed Program role.");
		_roles.Clear();
		foreach (var pair in candidates) _roles.Add(pair.Key, pair.Value);
	}

	public void ObserveComplete(string roleId, ulong sequence, ulong? audioSamplePosition)
	{
		if (!_roles.TryGetValue(roleId, out var role)) return;
		if (role.LastCompleteVideo is { } previous && sequence < previous)
			throw new InvalidDataException("Output completion went backwards without a new ControlHost-committed plan.");
		if (role.LastCompleteAudio is { } previousAudio &&
			audioSamplePosition is { } currentAudio && currentAudio < previousAudio)
			throw new InvalidDataException("Output audio sample position went backwards without committed recovery.");
		role.LastCompleteVideo = sequence;
		role.LastCompleteAudio = audioSamplePosition;
		role.Phase = role.Phase switch
		{
			BroadcastContinuityPhase.Failed or BroadcastContinuityPhase.RecoveredAwaitingRearm =>
				BroadcastContinuityPhase.RecoveredAwaitingRearm,
			_ => BroadcastContinuityPhase.ObservedComplete
		};
	}

	/// <summary>Idempotent per failure episode; never submits a replacement Program frame.</summary>
	public void ObserveFault(string roleId, string code, string detail)
	{
		if (!_roles.TryGetValue(roleId, out var role)) return;
		if (string.IsNullOrWhiteSpace(code) || code.Length > 96 || string.IsNullOrWhiteSpace(detail))
			throw new ArgumentException("Continuity fault codes and details must be bounded nonempty values.");
		if (role.IncidentGeneration == 0 ||
			(role.Phase == BroadcastContinuityPhase.RecoveredAwaitingRearm && role.IncidentCode != code))
		{
			if (_nextIncident == ulong.MaxValue)
				throw new InvalidOperationException("Broadcast continuity incident generations exhausted.");
			role.IncidentGeneration = ++_nextIncident;
			role.IncidentCode = code;
			role.IncidentDetail = detail.Length <= 256 ? detail : detail[..256];
			role.IncidentAt = DateTimeOffset.UtcNow;
			role.Phase = BroadcastContinuityPhase.Failed;
			_incidents.Enqueue(Read(role));
			while (_incidents.Count > HistoryLimit) _incidents.Dequeue();
		}
		role.Phase = BroadcastContinuityPhase.Failed;
	}

	/// <summary>Provider-failure policy has no physical fallback admission. Acknowledgement is a deliberate new Control revision, not a local UI notification.</summary>
	public bool CanReportHealthy(string roleId) =>
		_roles.TryGetValue(roleId, out var role) &&
		role.Phase == BroadcastContinuityPhase.ObservedComplete;

	public void EnsurePolicyAdmissible(BroadcastFailurePolicy policy) =>
		policy.EnsureRuntimeAdmitted();

	private static BroadcastContinuitySnapshot Read(Role role)
	{
		var observation = new BroadcastContinuitySnapshot(
			role.Name, role.Source, role.Sink, role.Policy.Action, role.Phase,
			role.AuthorityId, role.AuthorityRevision, role.ExecutionRevision,
			role.LastCompleteVideo, role.LastCompleteAudio, role.IncidentGeneration,
			role.IncidentCode, role.IncidentDetail, role.IncidentAt, false);
		observation.Validate();
		return observation;
	}
}
