// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class ShowPreflightAndActionHistoryTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
	private static readonly ProductionSourceId Source = ProductionSourceId.New();
	private static readonly ProductionId Production = ProductionId.New();

	[Fact]
	public void Missing_or_stale_evidence_never_becomes_a_pass()
	{
		Assert.All(ShowPreflightSummary.Build(null, false, null, null, [], Now), check => Assert.Equal(ShowPreflightState.Unknown, check.State));
		Assert.All(ShowPreflightSummary.Build(Snapshot(), false, MediaAssetCatalogSnapshot.Empty, Now, [], Now),
			check => Assert.Equal(ShowPreflightState.Unknown, check.State));
		var current = ShowPreflightSummary.Build(Snapshot(), true, null, null, [], Now);
		Assert.Equal(ShowPreflightState.Unknown, current.Single(check => check.Id == "outputs").State);
		Assert.Equal(ShowPreflightState.Unknown, current.Single(check => check.Id == "ai").State);
		Assert.Equal(ShowPreflightState.Unknown, current.Single(check => check.Id == "audio").State);
	}

	[Fact]
	public void Selected_show_distinguishes_no_media_from_missing_catalogue_and_missing_assets()
	{
		var cut = Selected(new(ShowControlActionId.New(), ShowControlActionKind.Cut));
		Assert.Equal(ShowPreflightState.NotApplicable, Checks(Snapshot(cut)).Single(check => check.Id == "assets").State);
		var media = Selected(new(ShowControlActionId.New(), ShowControlActionKind.MediaPlay, mediaAssetId: Identity.New().ToString().ToUpperInvariant()));
		Assert.Equal(ShowPreflightState.Unknown, Checks(Snapshot(media)).Single(check => check.Id == "assets").State);
		var assetId = new MediaAssetId(Identity.Parse(media.CueLists[0].Cues[0].Actions[0].MediaAssetId!));
		foreach (var availability in new[] { MediaAssetAvailability.Online, MediaAssetAvailability.Missing, MediaAssetAvailability.Offline })
		{
			var timestamp = new UtcTimestamp(Now);
			var descriptor = new MediaAssetDescriptor(assetId, MediaAssetOriginKind.LocalFile, @"D:\show\clip.mp4", "Clip",
				MediaContainerFormat.Mp4, MediaVideoCodec.H264, MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8,
				AudioFormat.Stereo48kFloat32, TimeSpan.FromSeconds(10), 1024, new string('A', 64), timestamp, timestamp, availability);
			var catalogue = new MediaAssetCatalogSnapshot(MediaContractVersion.Current, 7, [descriptor]);
			var check = ShowPreflightSummary.Build(Snapshot(media), true, catalogue, Now, [], Now).Single(item => item.Id == "assets");
			Assert.Equal(availability == MediaAssetAvailability.Online ? ShowPreflightState.Pass : ShowPreflightState.Fail, check.State);
		}
		var knownMissing = ShowPreflightSummary.Build(Snapshot(media), true, MediaAssetCatalogSnapshot.Empty, Now, [], Now);
		Assert.Equal(ShowPreflightState.Fail, knownMissing.Single(check => check.Id == "assets").State);
		var expired = ShowPreflightSummary.Build(Snapshot(media), true, MediaAssetCatalogSnapshot.Empty, Now.AddSeconds(-11), [], Now);
		Assert.Equal(ShowPreflightState.Unknown, expired.Single(check => check.Id == "assets").State);
	}

	[Fact]
	public void Cue_reference_check_reports_missing_sources_and_unavailable_marker_evidence_without_mutating_state()
	{
		var missing = Snapshot(Selected(new(ShowControlActionId.New(), ShowControlActionKind.SetPreview, sourceId: ProductionSourceId.New().ToString())));
		var original = missing.Production;
		Assert.Equal(ShowPreflightState.Fail, Checks(missing).Single(check => check.Id == "cues").State);
		Assert.Same(original, missing.Production);
		var unloadedCue = Snapshot(Selected(new(ShowControlActionId.New(), ShowControlActionKind.JumpMediaCue,
			mediaAssetId: Identity.New().ToString(), mediaCuePointId: Identity.New().ToString())));
		Assert.Equal(ShowPreflightState.Unknown, Checks(unloadedCue).Single(check => check.Id == "cues").State);
		Assert.Equal(ShowPreflightState.Pass, Checks(Snapshot(Selected(new(ShowControlActionId.New(), ShowControlActionKind.SetPreview,
			sourceId: Source.ToString().ToUpperInvariant())))).Single(check => check.Id == "cues").State);
	}

	[Fact]
	public void Current_audio_evidence_expires_and_inactive_output_cannot_be_green()
	{
		var audio = new SubsystemHealthSnapshot("audio", "Audio", "Runtime", SubsystemHealthState.Healthy, "Observed audio.",
			Now, Now, [], "None", "", LastObservedAt: Now);
		var role = new OperatorOutputRoleDescriptor("program", "PROGRAM", Source.ToString(), "target", "virtual",
			null, null, null, null, null, "STOPPED", false, "PASS", "Observed", null);
		var snapshot = Snapshot(outputs: [role]);
		var current = ShowPreflightSummary.Build(snapshot, true, null, null, [audio], Now);
		Assert.Equal(ShowPreflightState.Pass, current.Single(check => check.Id == "audio").State);
		Assert.Equal(ShowPreflightState.Unknown, current.Single(check => check.Id == "outputs").State);
		Assert.Equal(ShowPreflightState.Unknown, ShowPreflightSummary.Build(snapshot, true, null, null, [audio], Now.AddSeconds(11)).Single(check => check.Id == "audio").State);
	}

	[Fact]
	public void AI_requires_current_application_evidence_and_preserves_explicit_failure_over_unknown_status()
	{
		var ai = new OperatorAIShowcaseDescriptor(false, "Effect", "DISABLED", "Reference", TimeSpan.Zero, 0,
			null, null, null, false, null, Now);
		Assert.Equal(ShowPreflightState.NotApplicable, Checks(Snapshot(ai: ai)).Single(item => item.Id == "ai").State);
		ai = ai with { Enabled = true, Status = "ACTIVE", SourceSequence = 5, AppliedSequence = 5, EffectVisible = true };
		Assert.Equal(ShowPreflightState.Pass, Checks(Snapshot(ai: ai)).Single(item => item.Id == "ai").State);
		Assert.Equal(ShowPreflightState.Unknown, Checks(Snapshot(ai: ai with { UpdatedAtUtc = Now.AddSeconds(-11) })).Single(item => item.Id == "ai").State);
		Assert.Equal(ShowPreflightState.Fail, Checks(Snapshot(ai: ai with { Status = "UNAVAILABLE", Failure = new Failure("ai.failed", "Unavailable") })).Single(item => item.Id == "ai").State);
	}

	[Fact]
	public async Task Pending_request_is_confirmed_only_by_the_response_with_wire_identity_and_revisions()
	{
		var transport = new Transport { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
		var client = new OperatorControlClient(transport);
		await client.SynchronizeAsync();
		Assert.Empty(client.ActionHistory.Current);
		var request = client.CutPreviewAsync().AsTask();
		var pending = Assert.Single(client.ActionHistory.Current);
		Assert.Equal(OperatorActionOutcome.Pending, pending.Outcome);
		Assert.Null(pending.CompletedAtUtc);
		Assert.Equal(transport.Command!.Metadata.CommandId.ToString(), pending.CommandId);
		Assert.Equal(1UL, pending.ExpectedProductionRevision);
		transport.Pending.SetResult(new(true, State(2), null));
		Assert.True((await request).Accepted);
		var confirmed = Assert.Single(client.ActionHistory.Current);
		Assert.Equal(pending.Id, confirmed.Id);
		Assert.Equal(OperatorActionOutcome.Confirmed, confirmed.Outcome);
		Assert.Equal(2UL, confirmed.ResponseProductionRevision);
		Assert.True(confirmed.CompletedAtUtc >= confirmed.PendingAtUtc);
	}

	[Fact]
	public async Task Rejection_and_lost_response_survive_disconnect_and_resynchronization_without_replay()
	{
		var transport = new Transport { Reject = true };
		var client = new OperatorControlClient(transport);
		await client.SynchronizeAsync();
		Assert.False((await client.CutPreviewAsync()).Accepted);
		var rejected = Assert.Single(client.ActionHistory.Current);
		Assert.Equal(OperatorActionOutcome.Rejected, rejected.Outcome);
		Assert.False(rejected.ResponseAccepted);
		transport.LoseResponse = true;
		await Assert.ThrowsAsync<IOException>(() => client.CutPreviewAsync().AsTask());
		var uncertain = client.ActionHistory.Current.Last();
		Assert.Equal(OperatorActionOutcome.Unconfirmed, uncertain.Outcome);
		Assert.Null(uncertain.ResponseAccepted);
		client.Disconnect();
		transport.LoseResponse = false;
		await client.SynchronizeAsync();
		Assert.Equal(rejected, client.ActionHistory.Current[0]);
		Assert.Equal(uncertain, client.ActionHistory.Current[1]);
		Assert.Equal(2, transport.Mutations);
	}

	[Fact]
	public async Task Accepted_response_is_retained_when_the_following_snapshot_refresh_fails()
	{
		var transport = new Transport { FailRefresh = true };
		var client = new OperatorControlClient(transport);
		await client.SynchronizeAsync();
		await Assert.ThrowsAsync<IOException>(() => client.CutPreviewAsync().AsTask());
		var observation = Assert.Single(client.ActionHistory.Current);
		Assert.Equal(OperatorActionOutcome.Confirmed, observation.Outcome);
		Assert.Equal(2UL, observation.ResponseProductionRevision);
	}

	[Fact]
	public async Task Bounded_session_history_does_not_store_arguments_results_or_exception_messages()
	{
		const string secret = "srt://user:password@private-host?token=credential";
		var transport = new Transport();
		var client = new OperatorControlClient(transport);
		await client.SynchronizeAsync();
		client.ActionHistory.Changed += (_, _) => throw new InvalidOperationException("Presentation observer failed.");
		for (var index = 0; index < 200; index++)
			Assert.Equal(secret, await client.CreateShowTemplateAsync(secret, secret));
		Assert.Equal(OperatorActionHistory.Capacity, client.ActionHistory.Current.Count);
		transport.LoseResponse = true;
		await Assert.ThrowsAsync<IOException>(() => client.CutPreviewAsync().AsTask());
		var json = JsonSerializer.Serialize(client.ActionHistory.Current);
		Assert.DoesNotContain("password", json);
		Assert.DoesNotContain("credential", json);
		Assert.DoesNotContain("private-host", json);
		Assert.Equal(OperatorActionHistory.Capacity, client.ActionHistory.Current.Count);
		Assert.Equal(OperatorActionOutcome.Unconfirmed, client.ActionHistory.Current.Last().Outcome);
	}

	[Fact]
	public void Program_and_aux_evidence_are_independent_and_missing_roles_are_unverified()
	{
		OperatorOutputRoleDescriptor Role(string id, string kind, string health, bool active = true) =>
			new(id, kind, Source.ToString(), "target", "virtual", null, null, null, null, null, active ? "RUNNING" : "STOPPED",
				active, health, "Published role evidence", health == "FAIL" ? new Failure("output.failed", "Unavailable") : null);
		var program = Role("program", "PROGRAM", "PASS");
		var aux = Role("aux", "AUX", "FAIL");
		var snapshot = Snapshot(outputs: [program, aux]);
		var before = JsonSerializer.Serialize(snapshot);
		var checks = Checks(snapshot);
		Assert.Equal(ShowPreflightState.Pass, checks.Single(c => c.Id == "program-output").State);
		Assert.Equal(ShowPreflightState.Fail, checks.Single(c => c.Id == "aux-output").State);
		Assert.Equal(before, JsonSerializer.Serialize(snapshot));
		Assert.Equal(ShowPreflightState.Unknown, Checks(Snapshot(outputs: [program])).Single(c => c.Id == "aux-output").State);
		Assert.Equal(ShowPreflightState.Unknown, Checks(Snapshot(outputs: [Role("program", "PROGRAM", "PASS", false)])).Single(c => c.Id == "program-output").State);
		Assert.All(ShowPreflightSummary.Build(snapshot, false, null, null, [], Now), c => Assert.Equal(ShowPreflightState.Unknown, c.State));
	}

	[Theory]
	[InlineData("IDLE", 0UL, false, false, ShowPreflightState.Unknown)]
	[InlineData("RECORDING", 0UL, true, false, ShowPreflightState.Unknown)]
	[InlineData("RECORDING", 1UL, false, false, ShowPreflightState.Unknown)]
	[InlineData("RECORDING", 1UL, true, false, ShowPreflightState.Pass)]
	[InlineData("RECORDING", 1UL, true, true, ShowPreflightState.Fail)]
	[InlineData("FAILED", 0UL, false, false, ShowPreflightState.Fail)]
	public void Recording_target_requires_current_Runtime_write_evidence(string state, ulong written, bool target, bool failure, ShowPreflightState expected)
	{
		var recording = new OperatorRecordingDescriptor(state, TimeSpan.Zero, target ? "private-destination" : null, null, null,
			written, written, 0, 0, 0, failure ? new Failure("recording.failed", "Unavailable") : null,
			activeProfileId: "profile", activeProviderId: "provider");
		var snapshot = Snapshot(recording: recording);
		var before = JsonSerializer.Serialize(snapshot);
		var check = Checks(snapshot).Single(c => c.Id == "recording");
		Assert.Equal(expected, check.State);
		Assert.DoesNotContain("private-destination", check.Detail);
		Assert.Equal(before, JsonSerializer.Serialize(snapshot));
		Assert.Equal(ShowPreflightState.Unknown, ShowPreflightSummary.Build(snapshot, false, null, null, [], Now).Single(c => c.Id == "recording").State);
	}

	[Fact]
	public void AI_provider_name_or_visibility_alone_cannot_qualify_unsynchronized_effects()
	{
		var ai = new OperatorAIShowcaseDescriptor(true, "Effect", "ACTIVE", "Provider", TimeSpan.Zero, 0,
			8, 7, null, true, null, Now);
		Assert.Equal(ShowPreflightState.Unknown, Checks(Snapshot(ai: ai)).Single(c => c.Id == "ai").State);
		ai = ai with { AppliedSequence = 8, Provider = "UNVERIFIED" };
		Assert.Equal(ShowPreflightState.Unknown, Checks(Snapshot(ai: ai)).Single(c => c.Id == "ai").State);
		Assert.Equal(ShowPreflightState.Pass, Checks(Snapshot(ai: ai with { Provider = "Provider" })).Single(c => c.Id == "ai").State);
	}

	[Fact]
	public async Task Captured_provenance_and_acceptance_survive_pending_disconnect_and_reconnect()
	{
		var transport = new Transport { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
		var client = new OperatorControlClient(transport);
		await client.SynchronizeAsync();
		var requestedAt = client.SnapshotObservedAtUtc;
		var request = client.CutPreviewAsync().AsTask();
		var pending = Assert.Single(client.ActionHistory.Current);
		Assert.Equal(Production.ToString(), pending.ObservedProductionId);
		Assert.Equal(requestedAt, pending.SnapshotObservedAtUtc);
		Assert.NotNull(pending.ClientObservationEpoch);
		Assert.Null(pending.ResponseAccepted);
		client.Disconnect(); await client.SynchronizeAsync();
		transport.Pending.SetResult(new(true, State(2), null));
		await request;
		var confirmed = Assert.Single(client.ActionHistory.Current);
		Assert.Equal(pending.ClientObservationEpoch, confirmed.ClientObservationEpoch);
		Assert.Equal(pending.SnapshotObservedAtUtc, confirmed.SnapshotObservedAtUtc);
		Assert.True(confirmed.ResponseAccepted);
		await client.CreateShowTemplateAsync("private name", null);
		var later = client.ActionHistory.Current.Last();
		Assert.NotEqual(pending.ClientObservationEpoch, later.ClientObservationEpoch);
		Assert.Null(later.ResponseAccepted); // A string response does not expose acceptance or commitment.
	}

	private static IReadOnlyList<ShowPreflightCheck> Checks(OperatorStatusSnapshot snapshot) =>
		ShowPreflightSummary.Build(snapshot, true, null, null, [], Now);
	private static ShowControlWorkspaceSnapshot Selected(ShowControlAction action)
	{
		var list = new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(), "Show",
			[new(ShowControlCueId.New(), "Cue", [action])]);
		return new([list], list.CueListId, ShowControlExecutionSnapshot.Idle);
	}
	private static AuthoritativeProductionState State(ulong revision) =>
		new(ControlContractVersion.Current, Production, new Revision(revision), new(Source, Source));
	private static OperatorStatusSnapshot Snapshot(ShowControlWorkspaceSnapshot? show = null,
		IReadOnlyList<OperatorOutputRoleDescriptor>? outputs = null, OperatorAIShowcaseDescriptor? ai = null, OperatorRecordingDescriptor? recording = null) =>
		new(State(1), [new(Source.ToString(), "Camera")], "READY", "READY", "READY", "UNKNOWN", "IDLE", false, 0,
			showControl: show, outputRoles: outputs, aiShowcase: ai, recording: recording);

	private sealed class Transport : IOperatorControlTransport
	{
		public TaskCompletionSource<OperatorMutationResponse>? Pending { get; init; }
		public CutProgramCommand? Command { get; private set; }
		public bool Reject { get; init; }
		public bool LoseResponse { get; set; }
		public bool FailRefresh { get; init; }
		public int Mutations { get; private set; }
		private int _reads;
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			++_reads > 1 && FailRefresh ? ValueTask.FromException<OperatorStatusSnapshot>(new IOException("Refresh unavailable.")) : ValueTask.FromResult(Snapshot());
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default)
		{
			Command = command; Mutations++;
			if (LoseResponse) return ValueTask.FromException<OperatorMutationResponse>(new IOException("srt://user:password@private-host?token=credential"));
			return Pending is not null ? new(Pending.Task) :
				ValueTask.FromResult(new OperatorMutationResponse(!Reject, State(Reject ? 1UL : 2UL), Reject ? new Failure("test.rejected", "credential") : null));
		}
		public ValueTask<string> CreateShowTemplateAsync(string name, string? cueListId, CancellationToken cancellationToken = default) => ValueTask.FromResult(name);
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
