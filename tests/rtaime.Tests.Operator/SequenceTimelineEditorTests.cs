// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class SequenceTimelineEditorTests
{
    private static readonly string Fingerprint = new('A', 64);

    private static SequenceDefinition Sequence(int extraClips = 0)
    {
        var v = new SequenceTrack(Guid.NewGuid(), "Video A", SequenceMediaKind.Video, 1);
        var a = new SequenceTrack(Guid.NewGuid(), "Audio B", SequenceMediaKind.Audio, 2,
            AudioBusId: "program");
        var frame = 4800L;
        var clips = new List<SequenceClip>
        {
            new(Guid.NewGuid(), v.TrackId,
                new(Guid.NewGuid(), SequenceMediaKind.Video, Fingerprint), 0, 20 * frame, 0, 20 * frame),
            new(Guid.NewGuid(), a.TrackId,
                new(Guid.NewGuid(), SequenceMediaKind.Audio, Fingerprint), 0, 20 * frame, 0, 20 * frame)
        };
        for (var i = 0; i < extraClips; i++)
            clips.Add(new(Guid.NewGuid(), i % 2 == 0 ? v.TrackId : a.TrackId,
                new(Guid.NewGuid(), i % 2 == 0 ? SequenceMediaKind.Video : SequenceMediaKind.Audio, Fingerprint),
                0, frame, (i + 25L) * frame, frame));
        return new(SequenceContract.Version, Guid.NewGuid(), "Actual saved source",
            new(50, 1), [v, a], clips.ToArray(), []);
    }

    [Fact]
    public void Ten_thousand_offscreen_objects_do_not_realize_in_the_Operator_viewport()
    {
        var sequence = Sequence(10_000);
        var visible = SequenceTimelineGeometry.Project(sequence, 0, 20 * 4800L,
            0, 80, 1100);
        Assert.Equal(2, visible.Count);
        Assert.Equal(2, visible.Sum(r => r.Clips.Count));
        var later = SequenceTimelineGeometry.Project(sequence, 25 * 4800L, 30000 * 4800L,
            0, 60, 800);
        Assert.Equal(SequenceTimelineGeometry.MaximumRealizedClips, later.Sum(r => r.Clips.Count));
        Assert.InRange(later.Sum(r => r.Clips.Count), 0, 256);
        var offscreen = SequenceTimelineGeometry.Project(sequence, 0, 4800, 20, 6, 500);
        Assert.Empty(offscreen);
    }

    [Fact]
    public void Large_overlapping_timeline_projects_only_ordered_bounded_clips()
    {
        var definition = Sequence(1_600);
        var track = definition.Tracks[0];
        const long first = 25 * 4800L;
        const long last = 2000 * 4800L;
        var rows = SequenceTimelineGeometry.Project(definition, first, last, 0, 8, 720);

        Assert.Equal(SequenceTimelineGeometry.MaximumRealizedClips, rows.Sum(row => row.Clips.Count));
        var expected = definition.Clips.Where(clip => clip.TrackId == track.TrackId &&
            clip.TimelineStart < last && SequenceTimelineGeometry.ClipEnd(clip) > first)
            .OrderBy(clip => clip.TimelineStart).ThenBy(clip => clip.ClipId)
            .Take(SequenceTimelineGeometry.MaximumRealizedClips)
            .Select(clip => clip.ClipId);
        Assert.Equal(expected, rows[0].Clips.Select(clip => clip.Id));
        Assert.Empty(rows[1].Clips);
    }

    [Fact]
    public async Task Unchanged_measured_viewport_keeps_existing_timeline_rows_and_selection_focus()
    {
        using var vm = new SequenceTimelineEditorViewModel(
            new SequenceAuthoringClient(new RetainedSequenceTransport(Sequence())));
        await vm.RefreshAsync();
        await vm.LoadAsync();
        vm.SetViewport(760, 2);
        var initialVideo = vm.VisibleRows.Single(row => row.Kind == SequenceMediaKind.Video);
        var initialAudio = vm.VisibleRows.Single(row => row.Kind == SequenceMediaKind.Audio);
        var collectionChanges = 0;
        vm.VisibleRows.CollectionChanged += (_, _) => collectionChanges++;

        vm.SetViewport(760, 2);
        Assert.Equal(0, collectionChanges);
        Assert.Same(initialVideo, vm.VisibleRows.Single(row => row.Kind == SequenceMediaKind.Video));
        Assert.Same(initialAudio, vm.VisibleRows.Single(row => row.Kind == SequenceMediaKind.Audio));

        vm.SelectClip(vm.Sequence!.Clips[0].ClipId);
        Assert.NotSame(initialVideo, vm.VisibleRows.Single(row => row.Kind == SequenceMediaKind.Video));
        Assert.Same(initialAudio, vm.VisibleRows.Single(row => row.Kind == SequenceMediaKind.Audio));
        Assert.True(collectionChanges > 0);

        vm.SetViewport(535, 2);
        Assert.All(vm.VisibleRows.SelectMany(row => row.Clips),
            clip => Assert.InRange(clip.Left + clip.Width, 0, 536));
        Assert.Equal(2, vm.VisibleRows.Count);
    }

    [Fact]
    public void Timeline_edge_snapping_avoids_per_clip_managed_materialization()
    {
        var definition = Sequence(2_000);
        var target = 30 * 4800L + 200;
        for (var i = 0; i < 8; i++)
            Assert.Equal(30 * 4800L, SequenceTimelineGeometry.Snap(target, definition, true));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 128; i++)
            Assert.Equal(30 * 4800L, SequenceTimelineGeometry.Snap(target, definition, true));
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(bytes, 0, 128 * 1024);
    }

    [Theory]
    [InlineData(50, 1, 4800)]
    [InlineData(60000, 1001, 4004)]
    public void Rational_source_time_and_subpixel_zoom_roundtrip_without_floating_drift(
        int numerator, int denominator, int frameTicks)
    {
        var rate = new SequenceRate(numerator, denominator);
        Assert.Equal(25L * frameTicks, SequenceTimelineGeometry.FrameTicks(25, rate));
        Assert.Equal(25L * frameTicks,
            SequenceTimelineGeometry.Quantize(25L * frameTicks, rate));
        Assert.Equal(4004L, SequenceTimelineGeometry.FrameTicks(1, new(60000, 1001)));
        Assert.Equal(40000L, SequenceTimelineGeometry.ToMediaTicks(960));
        Assert.Equal(40041L, SequenceTimelineGeometry.ToMediaTicks(961, ceiling: false));
        foreach (var scale in new[] { 1.0, 1.25, 1.50, 2.0 })
        {
            const double baseWidth = 840.0;
            var width = baseWidth * scale;
            var start = 20L * frameTicks;
            var end = 120L * frameTicks;
            var framePoint = 50L * frameTicks;
            var x = SequenceTimelineGeometry.Pixel(framePoint, start, end, width);
            var recovered = SequenceTimelineGeometry.ToTick(x, width, start, end, rate);
            Assert.Equal(framePoint, recovered);
        }
    }

    [Fact]
    public async Task Selected_clip_edit_and_undo_use_versioned_authoritative_mutation()
    {
        var transport = new RetainedSequenceTransport(Sequence());
        using var vm = new SequenceTimelineEditorViewModel(new SequenceAuthoringClient(transport));
        await vm.RefreshAsync();
        await vm.LoadAsync();
        Assert.True(vm.CanEdit);
        Assert.Equal("UNAVAILABLE", vm.Snapshot!.PlaybackState);
        var original = vm.Sequence!.Clips[0];
        vm.SelectClip(original.ClipId);
        var target = 22L * 4800;
        await vm.EditAsync([new(SequenceEditKind.MoveClip, original.ClipId, Position: target)]);
        Assert.Equal(target, vm.Sequence!.Clips.Single(c => c.ClipId == original.ClipId).TimelineStart);
        Assert.Equal(6UL, vm.Snapshot!.AuthoringRevision);
        Assert.Null(vm.Snapshot.ActiveExecutionRevision);
        Assert.Equal(1, transport.CommittedEdits);
        Assert.Equal(0, transport.ProductionActions);
        await vm.EditAsync([new(SequenceEditKind.TrimClip, original.ClipId,
            SourceIn: 2 * 4800L, SourceOut: 20 * 4800L)]);
        Assert.Equal(24L * 4800, vm.Sequence!.Clips.Single(c => c.ClipId == original.ClipId).TimelineStart);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => vm.Snapshot?.AuthoringRevision == 8, TimeSpan.FromSeconds(3)));
        Assert.Equal(target, vm.Sequence!.Clips.Single(c => c.ClipId == original.ClipId).TimelineStart);
        Assert.Equal(8UL, vm.Snapshot!.AuthoringRevision);
        Assert.Equal("UNAVAILABLE", vm.Snapshot.PlaybackState);
    }

    [Fact]
    public async Task Drag_cancel_does_not_mutate_saved_clip_and_rejection_does_not_replay()
    {
        var transport = new RetainedSequenceTransport(Sequence());
        using var vm = new SequenceTimelineEditorViewModel(new SequenceAuthoringClient(transport));
        await vm.RefreshAsync(); await vm.LoadAsync();
        var first = vm.Sequence!.Clips[0];
        Assert.True(vm.BeginGesture(first.ClipId, SequenceEditorGestureKind.Move, 0));
        vm.UpdateGesture(5 * 4800);
        vm.CancelGesture();
        await vm.CompleteGestureAsync();
        Assert.Equal(0, transport.CommittedEdits);
        Assert.Equal(0, vm.Sequence!.Clips[0].TimelineStart);
        transport.ForceConflict = true;
        await vm.EditAsync([new(SequenceEditKind.MoveClip, first.ClipId, Position: 4800)]);
        Assert.Equal(0, transport.CommittedEdits);
        Assert.Equal(0, vm.Sequence!.Clips[0].TimelineStart);
        Assert.Contains("CONFLICT", vm.Status);
        Assert.Equal(1, transport.Attempts);
    }

    [Fact]
    public async Task Real_clip_transition_and_tool_state_are_distinct_from_playback()
    {
        var definition = Sequence();
        var video = definition.Clips[0];
        var neighbor = video with
        {
            ClipId = Guid.NewGuid(),
            TimelineStart = 15 * 4800L,
            SourceOut = 5 * 4800L,
            Duration = 5 * 4800L,
            Media = video.Media with { AssetId = Guid.NewGuid() }
        };
        var model = definition with
        {
            Clips = definition.Clips.Append(neighbor).ToArray(),
            Transitions = [new(Guid.NewGuid(), video.ClipId, neighbor.ClipId,
                SequenceTransitionKind.Dissolve, 15 * 4800L, 5 * 4800L)]
        };
        var rendered = SequenceTimelineGeometry.Project(model, 10 * 4800L, 21 * 4800L, 0, 6, 800);
        var vrow = rendered.Single(x => x.Kind == SequenceMediaKind.Video);
        Assert.Equal(2, vrow.Clips.Count);
        Assert.All(vrow.Clips, c => Assert.True(c.Width > 0));
        var snap = SequenceTimelineGeometry.Snap(15 * 4800L + 200, model, true);
        Assert.Equal(15 * 4800L, snap);
    }

    [Fact]
    public async Task New_sequence_and_two_video_two_audio_library_drops_are_saved_without_Program_take()
    {
        var backend = new RetainedSequenceTransport(Sequence());
        var catalogue = new AuthoredAssetCatalog();
        using var vm = new SequenceTimelineEditorViewModel(new SequenceAuthoringClient(backend), catalogue);
        await vm.RefreshAsync();
        await vm.CreateAsync();
        Assert.Equal(4, vm.TrackCount);
        Assert.Equal(6UL, vm.Snapshot!.AuthoringRevision);
        Assert.Equal(2, vm.Sequence!.Tracks.Count(t => t.Kind == SequenceMediaKind.Video));
        Assert.Equal(2, vm.Sequence.Tracks.Count(t => t.Kind == SequenceMediaKind.Audio));
        Assert.Equal("UNAVAILABLE", vm.Snapshot.PlaybackState);
        foreach (var track in vm.Sequence.Tracks)
            await vm.InsertCatalogueAssetAsync(track.TrackId, catalogue.Asset.AssetId.Value.Value);
        Assert.Equal(4, vm.Sequence!.Clips.Length);
        Assert.Equal(10UL, vm.Snapshot!.AuthoringRevision);
        Assert.All(vm.Sequence.Clips, clip => Assert.Equal(catalogue.Asset.FingerprintSha256,
            clip.Media.FingerprintSha256));
        Assert.Equal(4, backend.CommittedEdits);
        Assert.Equal(0, backend.ProductionActions);
        Assert.Null(vm.Snapshot.ActiveExecutionRevision);
    }

    [Fact]
    public async Task Native_playout_requires_real_revision_bound_Preview_output_before_Take()
    {
        var saved = new RetainedSequenceTransport(Sequence());
        using var editor = new SequenceTimelineEditorViewModel(new SequenceAuthoringClient(saved));
        await editor.RefreshAsync(); await editor.LoadAsync();
        var source = Guid.NewGuid().ToString();
        var program = Guid.NewGuid().ToString();
        var authority = new SequenceOperatorAuthority(true, false, false, true, source, program, 9);
        var native = new SequencePlayoutFixture(editor.Sequence!.SequenceId, source, 5, 9);
        var controller = new SequenceOperatorPlayoutViewModel(editor, new SequenceAuthoringClient(saved),
            new SequencePreparationClient(native), new SequenceShuttleClient(native),
            new SequenceOutputClient(native), () => authority);
        editor.AttachPlayout(controller);

        Assert.True(controller.CanPrepare);
        await controller.PrepareAsync();
        Assert.Equal("PREPARED · NO OUTPUT", controller.Status);
        Assert.False(controller.CanArm);
        Assert.False(controller.CanTake);
        await controller.ActivatePreviewAsync();
        Assert.False(controller.CanArm);
        editor.EditCursorTick = 4800;
        await controller.ShuttleAsync(SequenceShuttleAction.Cue, editor.EditCursorTick);
        await controller.ShuttleAsync(SequenceShuttleAction.Play);
        Assert.Contains("PLAYING", controller.ShuttleEvidence);
        Assert.False(controller.CanArm); // Native PLAYING is never visible output.
        native.PreviewFramesAvailable = true;
        await controller.ObserveAsync();
        Assert.Equal("PREVIEW READY", controller.Status);
        Assert.True(controller.CanArm);
        await controller.ArmAsync();
        Assert.Equal("TAKE ARMED", controller.Status);
        Assert.True(controller.CanTake);
        Assert.Equal(0, native.TakeCount);

        // An explicit saved edit changes the authoring revision and revokes
        // the armed output without replaying a Program transaction.
        var clip = editor.Sequence.Clips[0];
        await editor.EditAsync([new(SequenceEditKind.MoveClip, clip.ClipId,
            Position: 22 * 4800L)]);
        Assert.Equal("STALE", controller.Status);
        Assert.False(controller.CanTake);
        Assert.Equal(0, native.TakeCount);
    }

    [Fact]
    public async Task Native_Take_lost_reply_recovers_only_the_same_committed_successor()
    {
        var saved = new RetainedSequenceTransport(Sequence());
        using var editor = new SequenceTimelineEditorViewModel(new SequenceAuthoringClient(saved));
        await editor.RefreshAsync(); await editor.LoadAsync();
        var source = Guid.NewGuid().ToString();
        var program = Guid.NewGuid().ToString();
        var authority = new SequenceOperatorAuthority(true, false, false, true, source, program, 11);
        var native = new SequencePlayoutFixture(editor.Sequence!.SequenceId, source, 5, 11)
        {
            PreviewFramesAvailable = true, DropTakeReply = true
        };
        editor.AttachPlayout(new SequenceOperatorPlayoutViewModel(editor,
            new SequenceAuthoringClient(saved), new SequencePreparationClient(native),
            new SequenceShuttleClient(native), new SequenceOutputClient(native),
            () => authority));
        var playout = editor.Playout;
        await playout.PrepareAsync();
        await playout.ActivatePreviewAsync();
        await playout.ShuttleAsync(SequenceShuttleAction.Play);
        await playout.ObserveAsync();
        Assert.Equal("PREVIEW READY", playout.Status);
        await playout.ArmAsync();
        await playout.TakeAsync();
        Assert.Equal("TAKE UNCONFIRMED", playout.Status);
        Assert.Equal(1, native.TakeCount);
        Assert.False(playout.CanTake);
        await playout.TakeAsync();
        Assert.Equal(1, native.TakeCount);

        authority = authority with { Stale = true };
        Assert.False(playout.CanRecover);
        authority = authority with { Stale = false, Revision = 11 };
        await playout.RecoverAsync();
        Assert.Equal("TAKE UNCONFIRMED", playout.Status);
        Assert.Equal(0, native.RecoveryCount);
        Assert.Equal(1, native.TakeCount);
        authority = authority with { Revision = 12, ProgramSourceId = source };
        await playout.RecoverAsync();
        Assert.Equal("PROGRAM CONFIRMED", playout.Status);
        Assert.Equal(1, native.RecoveryCount);
        Assert.Equal(1, native.TakeCount);
        Assert.Equal(native.LastTakeTransactionId, native.LastRecoveryTransactionId);
        Assert.Contains("PROGRAM SLOT", playout.OutputEvidence);
    }

    [Fact]
    public async Task Prepared_sequence_authority_change_is_fail_closed_and_release_is_explicit()
    {
        var saved = new RetainedSequenceTransport(Sequence());
        using var editor = new SequenceTimelineEditorViewModel(new SequenceAuthoringClient(saved));
        await editor.RefreshAsync(); await editor.LoadAsync();
        var source = Guid.NewGuid().ToString();
        var authority = new SequenceOperatorAuthority(true, false, false, true, source,
            Guid.NewGuid().ToString(), 6);
        var native = new SequencePlayoutFixture(editor.Sequence!.SequenceId, source, 5, 6);
        editor.AttachPlayout(new SequenceOperatorPlayoutViewModel(editor,
            new SequenceAuthoringClient(saved), new SequencePreparationClient(native),
            new SequenceShuttleClient(native), new SequenceOutputClient(native),
            () => authority));
        var playout = editor.Playout;
        await playout.PrepareAsync();
        Assert.True(playout.CanActivate);
        authority = authority with { Stale = true };
        Assert.False(playout.CanActivate);
        Assert.False(playout.CanTake);
        authority = authority with { Stale = false, PreviewSourceId = Guid.NewGuid().ToString() };
        Assert.False(playout.CanActivate);
        Assert.False(playout.CanShuttle);
        await playout.CancelAsync();
        Assert.Equal(1, native.CancelCount);
        Assert.Equal("SAVED · NOT PREPARED", playout.Status);
    }

    [Fact]
    public async Task Long_native_prepare_keeps_original_Preview_fence_and_requires_explicit_release()
    {
        var saved = new RetainedSequenceTransport(Sequence());
        using var editor = new SequenceTimelineEditorViewModel(new SequenceAuthoringClient(saved));
        await editor.RefreshAsync(); await editor.LoadAsync();
        var source = Guid.NewGuid().ToString();
        var authority = new SequenceOperatorAuthority(true, false, false, true,
            source, Guid.NewGuid().ToString(), 4);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new SequencePlayoutFixture(editor.Sequence!.SequenceId, source, 5, 4)
        {
            PreparationGate = gate
        };
        editor.AttachPlayout(new SequenceOperatorPlayoutViewModel(editor,
            new SequenceAuthoringClient(saved), new SequencePreparationClient(native),
            new SequenceShuttleClient(native), new SequenceOutputClient(native),
            () => authority));
        var pending = editor.Playout.PrepareAsync();
        Assert.Equal("PREPARING", editor.Playout.Status);
        authority = authority with { PreviewSourceId = Guid.NewGuid().ToString(), Revision = 5 };
        gate.SetResult();
        await pending;
        Assert.Equal("STALE", editor.Playout.Status);
        Assert.Contains(source, editor.Playout.NativeOwner);
        Assert.False(editor.Playout.CanActivate);
        Assert.False(editor.Playout.CanTake);
        await editor.Playout.CancelAsync();
        Assert.Equal(1, native.CancelCount);
    }

    [Fact]
    public async Task Unsupported_native_profile_surfaces_actual_rejection_without_Preview_or_Take()
    {
        var saved = new RetainedSequenceTransport(Sequence());
        using var editor = new SequenceTimelineEditorViewModel(new SequenceAuthoringClient(saved));
        await editor.RefreshAsync(); await editor.LoadAsync();
        var native = new SequencePlayoutFixture(editor.Sequence!.SequenceId,
            Guid.NewGuid().ToString(), 5, 3) { RejectPrepare = true };
        var authority = new SequenceOperatorAuthority(true, false, false, true,
            native.SourceId, Guid.NewGuid().ToString(), 3);
        editor.AttachPlayout(new SequenceOperatorPlayoutViewModel(editor,
            new SequenceAuthoringClient(saved), new SequencePreparationClient(native),
            new SequenceShuttleClient(native), new SequenceOutputClient(native),
            () => authority));
        await editor.Playout.PrepareAsync();
        Assert.Equal("REJECTED", editor.Playout.Status);
        Assert.Contains("sequence.native.profile_unsupported", editor.Playout.Detail);
        Assert.False(editor.Playout.CanActivate);
        Assert.False(editor.Playout.CanTake);
        Assert.Equal(0, native.TakeCount);
    }

    private sealed class SequencePlayoutFixture(Guid sequenceId, string sourceId, ulong revision, ulong productionRevision)
        : ISequencePreparationTransport, ISequenceShuttleTransport, ISequenceOutputTransport
    {
        private readonly string _epoch = Guid.NewGuid().ToString();
        private readonly string _prepared = Guid.NewGuid().ToString();
        private SequencePreparationReference? _reference;
        private ulong _shuttleRevision = 1;
        private string _shuttleState = "STOPPED";
        public bool PreviewFramesAvailable { get; set; }
        public bool DropTakeReply { get; set; }
        public bool RejectPrepare { get; set; }
        public TaskCompletionSource? PreparationGate { get; set; }
        public string SourceId => sourceId;
        public int TakeCount { get; private set; }
        public int CancelCount { get; private set; }
        public int RecoveryCount { get; private set; }
        public Guid LastTakeTransactionId { get; private set; }
        public Guid LastRecoveryTransactionId { get; private set; }

        public ValueTask<SequencePreparationContext> GetSequencePreparationContextAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SequencePreparationContext(SequencePreparationContract.Version, _epoch, revision));
        public async ValueTask<SequencePreparationStatus> PrepareSequenceAsync(
            SequencePreparationRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(sequenceId, request.SequenceId);
            Assert.Equal(revision, request.ExpectedAuthoringRevision);
            if (PreparationGate is not null)
                await PreparationGate.Task.WaitAsync(cancellationToken);
            if (RejectPrepare)
                return new(SequencePreparationContract.Version, sequenceId, revision, _epoch,
                    "", 1, 0, "REJECTED", "sequence.native.profile_unsupported",
                    "Native provider profile does not support the requested sequence.");
            _reference = new(SequencePreparationContract.Version, sequenceId, revision, _epoch, _prepared, 1);
            return Prepared();
        }
        public ValueTask<SequencePreparationStatus> GetSequencePreparationAsync(
            SequencePreparationReference reference, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_reference, reference);
            return ValueTask.FromResult(Prepared());
        }
        public ValueTask<SequencePreparationStatus> CancelSequencePreparationAsync(
            SequencePreparationReference reference, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_reference, reference);
            CancelCount++;
            return ValueTask.FromResult(Prepared() with { State = "CANCELED", DecoderOwners = 0 });
        }
        private SequencePreparationStatus Prepared() => new(
            SequencePreparationContract.Version, sequenceId, revision, _epoch, _prepared,
            1, 2, "PREPARED", null, "Native reference owner ready; not output.");
        public ValueTask<SequenceShuttleObservation> GetSequenceShuttleAsync(
            SequencePreparationReference reference, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_reference, reference);
            return ValueTask.FromResult(Shuttle(reference));
        }
        public ValueTask<SequenceShuttleObservation> ApplySequenceShuttleAsync(
            SequenceShuttleRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_shuttleRevision, request.ExpectedStateRevision);
            _shuttleRevision++;
            _shuttleState = request.Action switch
            {
                SequenceShuttleAction.Play => "PLAYING",
                SequenceShuttleAction.Pause => "PAUSED",
                SequenceShuttleAction.Cue or SequenceShuttleAction.Seek => "PAUSED",
                SequenceShuttleAction.Stop => "STOPPED",
                _ => "CUED"
            };
            return ValueTask.FromResult(Shuttle(request.Preparation));
        }
        private SequenceShuttleObservation Shuttle(SequencePreparationReference reference) => new(
            SequenceShuttleContract.Version, reference, _shuttleRevision, 1, 0, _shuttleState,
            0, 0, false, true, "UNAVAILABLE", "Native staging, no output.");
        public ValueTask<SequenceOutputObservation> ActivatePreviewAsync(
            SequenceOutputCommand command, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_reference, command.Preparation);
            Assert.Equal(sourceId, command.PreviewSourceId);
            return ValueTask.FromResult(Output(command.Preparation, "BUFFERING", productionRevision));
        }
        public ValueTask<SequenceOutputObservation> GetOutputAsync(
            SequencePreparationReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Output(reference,
                PreviewFramesAvailable ? "PREVIEW_READY" : "BUFFERING", productionRevision));
        public ValueTask<SequenceOutputObservation> TakeProgramAsync(
            SequenceOutputCommand command, CancellationToken cancellationToken = default)
        {
            TakeCount++;
            LastTakeTransactionId = command.TransactionId;
            Assert.Equal(productionRevision, command.ExpectedProductionRevision);
            return DropTakeReply
                ? ValueTask.FromException<SequenceOutputObservation>(new TimeoutException("Simulated lost Take receipt."))
                : ValueTask.FromResult(Output(command.Preparation, "TAKE_COMMITTED", productionRevision + 1));
        }
        public ValueTask<SequenceOutputObservation> RecoverTakeAsync(
            SequenceOutputCommand command, CancellationToken cancellationToken = default)
        {
            RecoveryCount++;
            LastRecoveryTransactionId = command.TransactionId;
            Assert.Equal(LastTakeTransactionId, command.TransactionId);
            return ValueTask.FromResult(Output(command.Preparation, "PROGRAM_CONFIRMED", productionRevision + 1));
        }
        private SequenceOutputObservation Output(SequencePreparationReference reference, string state, ulong production) =>
            new(SequenceOutputContract.Version, reference, sourceId, state,
                state == "BUFFERING" ? 0UL : 5UL, 0,
                state == "BUFFERING" ? null : 5UL,
                state == "PROGRAM_CONFIRMED" ? 5UL : null,
                production, null, "Exact source-bound output observation.");
    }

    private sealed class AuthoredAssetCatalog : IMediaAssetCatalogClient
    {
        public MediaAssetDescriptor Asset { get; } = new(
            MediaAssetId.New(), MediaAssetOriginKind.LocalFile, @"C:\fixtures\multitrack.mp4",
            "Reference clip", MediaContainerFormat.Mp4, MediaVideoCodec.H264,
            MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32,
            TimeSpan.FromSeconds(2), 128_000, new string('A', 64),
            new UtcTimestamp(DateTimeOffset.UtcNow),
            new UtcTimestamp(DateTimeOffset.UtcNow), MediaAssetAvailability.Online);

        public ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MediaAssetCatalogSnapshot(MediaContractVersion.Current, 2, [Asset]));
        public ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(IReadOnlyList<string> sourceLocations,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(MediaAssetId assetId,
            string sourceLocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(MediaAssetId assetId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogSnapshot>(new NotSupportedException());
    }

    private sealed class RetainedSequenceTransport(SequenceDefinition initial) : ISequenceAuthoringTransport
    {
        private SequenceDefinition _saved = initial;
        private readonly Dictionary<Guid, SequenceDefinition> _additional = [];
        private readonly Stack<SequenceDefinition> _undo = [];
        private readonly Stack<SequenceDefinition> _redo = [];
        private ulong _revision = 5;
        public bool ForceConflict { get; set; }
        public int CommittedEdits { get; private set; }
        public int Attempts { get; private set; }
        public int ProductionActions => 0;
        public ValueTask<SequenceList> ListSequencesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SequenceList(_revision,
                _additional.Values.Append(_saved).Select(s =>
                    new SequenceSummary(s.SequenceId, s.Name, s.FrameRate,
                        s.Tracks.Length, s.Clips.Length)).ToArray(),
                SequenceContract.Version, "UNAVAILABLE"));
        public ValueTask<SequenceSnapshot> GetSequenceAsync(Guid sequenceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SequenceSnapshot(_revision,
                _additional.TryGetValue(sequenceId, out var saved) ? saved : _saved,
                null, "UNAVAILABLE", new(sequenceId, _revision, [])));
        public ValueTask<SequenceMutationResult> EditSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (ForceConflict || request.ExpectedRevision != _revision)
                return ValueTask.FromResult(new SequenceMutationResult(false, _revision,
                    request.SequenceId, null,
                    [new("sequence.revision_conflict", "$", "Other authoring revision is active.")]));
            var source = _additional.TryGetValue(request.SequenceId, out var extra) ? extra : _saved;
            var next = SequenceEdits.Apply(source, request.Edits!);
            var validated = SequenceModel.Validate(next);
            if (!validated.IsValid)
                return ValueTask.FromResult(new SequenceMutationResult(false, _revision,
                    request.SequenceId, null, validated.Issues));
            _undo.Push(_saved); _redo.Clear();
            if (_additional.ContainsKey(request.SequenceId)) _additional[request.SequenceId] = next;
            else _saved = next;
            ++_revision; CommittedEdits++;
            return ValueTask.FromResult(new SequenceMutationResult(true, _revision,
                request.SequenceId, _saved, []));
        }
        public ValueTask<SequenceMutationResult> UndoSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default)
        {
            if (_undo.Count == 0 || request.ExpectedRevision != _revision)
                return ValueTask.FromResult(new SequenceMutationResult(false, _revision,
                    request.SequenceId, null, [new("sequence.history_empty", "$", "No undo.")]));
            _redo.Push(_saved); _saved = _undo.Pop(); ++_revision;
            return ValueTask.FromResult(new SequenceMutationResult(true, _revision,
                request.SequenceId, null, []));
        }
        public ValueTask<SequenceMutationResult> RedoSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default)
        {
            if (_redo.Count == 0 || request.ExpectedRevision != _revision)
                return ValueTask.FromResult(new SequenceMutationResult(false, _revision,
                    request.SequenceId, null, [new("sequence.history_empty", "$", "No redo.")]));
            _undo.Push(_saved); _saved = _redo.Pop(); ++_revision;
            return ValueTask.FromResult(new SequenceMutationResult(true, _revision,
                request.SequenceId, null, []));
        }
        public ValueTask<SequenceMutationResult> SaveSequenceAsync(SequenceMutationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Definition is not { } created || request.ExpectedRevision != _revision ||
                _additional.ContainsKey(created.SequenceId))
                return ValueTask.FromResult(new SequenceMutationResult(false, _revision,
                    request.SequenceId, null, [new("sequence.revision_conflict", "$", "Invalid creation revision.")]));
            _additional.Add(created.SequenceId, created);
            ++_revision;
            return ValueTask.FromResult(new SequenceMutationResult(true, _revision,
                created.SequenceId, created, []));
        }
    }
}
