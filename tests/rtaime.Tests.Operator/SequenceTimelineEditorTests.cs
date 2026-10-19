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
