// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public enum SequenceEditorGestureKind { Move, TrimStart, TrimEnd }

/// <summary>
/// Editor state is a projection of durable ControlHost sequences. Only authoring
/// transactions mutate a show. Cursor, selection and drag are local drafts; there
/// is no separate production clock, implicit Take or optimistic saved revision.
/// </summary>
public sealed class SequenceTimelineEditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SequenceAuthoringClient? _authoring;
    private readonly IMediaAssetCatalogClient? _catalogue;
    private readonly SequenceTimelineWaveformService? _waveforms;
    private SequenceSnapshot? _snapshot;
    private ulong _libraryRevision;
    private bool _revisionConflict;
    private SequenceSummary? _selectedSummary;
    private readonly HashSet<Guid> _selectedClips = [];
    private readonly List<Guid> _selectedOrder = [];
    private SequenceClip? _clipboard;
    private SequenceEditorGesture? _gesture;
    private bool _busy;
    private bool _disposed;
    private bool _snapping = true;
    private string _status = "SEQUENCE AUTHORING · UNAVAILABLE";
    private string _feedback = "Connect to ControlHost and refresh saved sequences.";
    private double _zoom = 1.0;
    private double _viewportWidth = 640;
    private long _visibleStart;
    private int _firstTrack;
    private int _visibleTrackCount = 8;
    private long _editCursor;
    private long _requestGeneration;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SequenceSummary> Sequences { get; } = [];
    public ObservableCollection<SequenceEditorRow> VisibleRows { get; } = [];
    public SequenceTimelineEditorViewModel(
        SequenceAuthoringClient? authoring, IMediaAssetCatalogClient? analysis = null)
    {
        _authoring = authoring;
        _catalogue = analysis;
        if (analysis is not null) _waveforms = new SequenceTimelineWaveformService(analysis);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_disposed && !_busy && _authoring is not null);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !_disposed && !_busy && _authoring is not null);
        LoadCommand = new AsyncRelayCommand(LoadAsync, () => CanLoad);
        UndoCommand = new AsyncRelayCommand(() => HistoryAsync(true), () => CanEdit);
        RedoCommand = new AsyncRelayCommand(() => HistoryAsync(false), () => CanEdit);
        DeleteCommand = new AsyncRelayCommand(() => DeleteAsync(false), () => CanEditSelection);
        RippleDeleteCommand = new AsyncRelayCommand(() => DeleteAsync(true), () => CanEditSelection);
        SplitCommand = new AsyncRelayCommand(SplitAsync, () => CanEditSelection);
        CopyCommand = new AsyncRelayCommand(() => { CopySelected(); return Task.CompletedTask; }, () => CanEditSelection);
        PasteCommand = new AsyncRelayCommand(PasteAsync, () => CanEdit && _clipboard is not null);
        LinkCommand = new AsyncRelayCommand(LinkAsync, () => CanEdit && _selectedClips.Count >= 2);
        UnlinkCommand = new AsyncRelayCommand(UnlinkAsync, () => CanEditSelection);
        TransitionCommand = new AsyncRelayCommand(TransitionAsync, () => CanEdit && _selectedClips.Count == 2);
        ToggleTrackEnabledCommand = new AsyncRelayCommand(() => ToggleTrackAsync(false), () => CanEditSelection);
        ToggleTrackMuteCommand = new AsyncRelayCommand(() => ToggleTrackAsync(true), () => CanEditSelection);
        WaveformCommand = new AsyncRelayCommand(LoadWaveformAsync,
            () => CanEditSelection && SelectedClip?.Media.Kind == SequenceMediaKind.Audio && _waveforms is not null);
        ZoomInCommand = new AsyncRelayCommand(() => { Zoom = Math.Min(64, Zoom * 2); return Task.CompletedTask; }, () => _snapshot is not null && Zoom < 64);
        ZoomOutCommand = new AsyncRelayCommand(() => { Zoom = Math.Max(1, Zoom / 2); return Task.CompletedTask; }, () => _snapshot is not null && Zoom > 1);
        FitCommand = new AsyncRelayCommand(() => { Zoom = 1; return Task.CompletedTask; }, () => _snapshot is not null && Zoom > 1);
        ScrollBackwardCommand = new AsyncRelayCommand(() => { Scroll(-0.8); return Task.CompletedTask; }, () => _snapshot is not null && _visibleStart > 0);
        ScrollForwardCommand = new AsyncRelayCommand(() => { Scroll(0.8); return Task.CompletedTask; }, () => _snapshot is not null && VisibleEnd < DurationTicks);
        NextTracksCommand = new AsyncRelayCommand(() => { FirstTrack = Math.Min(Math.Max(0, TrackCount - VisibleTrackCount), FirstTrack + VisibleTrackCount); return Task.CompletedTask; }, () => _snapshot is not null && FirstTrack + VisibleTrackCount < TrackCount);
        PreviousTracksCommand = new AsyncRelayCommand(() => { FirstTrack = Math.Max(0, FirstTrack - VisibleTrackCount); return Task.CompletedTask; }, () => _snapshot is not null && FirstTrack > 0);
    }

    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RippleDeleteCommand { get; }
    public ICommand SplitCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand LinkCommand { get; }
    public ICommand UnlinkCommand { get; }
    public ICommand TransitionCommand { get; }
    public ICommand ToggleTrackEnabledCommand { get; }
    public ICommand ToggleTrackMuteCommand { get; }
    public ICommand WaveformCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand ScrollBackwardCommand { get; }
    public ICommand ScrollForwardCommand { get; }
    public ICommand NextTracksCommand { get; }
    public ICommand PreviousTracksCommand { get; }

    public SequenceSummary? SelectedSummary
    {
        get => _selectedSummary;
        set { if (_selectedSummary == value) return; _selectedSummary = value; Changed(); RaiseCommands(); }
    }
    public SequenceSnapshot? Snapshot => _snapshot;
    public SequenceDefinition? Sequence => _snapshot?.Sequence;
    public bool HasSequence => Sequence is not null;
    public bool CanLoad => !_disposed && !_busy && _authoring is not null && SelectedSummary is not null;
    public bool CanEdit => !_disposed && !_busy && !_revisionConflict && _authoring is not null &&
        _snapshot?.Validation.IsValid == true;
    public bool CanEditSelection => CanEdit && _selectedClips.Count > 0;
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string Feedback { get => _feedback; private set { _feedback = value; Changed(); } }
    public string SequenceName => Sequence?.Name ?? "NO SAVED SEQUENCE";
    public string RevisionLabel => _snapshot is null ? "REVISION —" :
        $"SAVED REV {_snapshot.AuthoringRevision} · EXECUTION {(_snapshot.ActiveExecutionRevision?.ToString() ?? "UNAVAILABLE")}";
    public string PlaybackLabel => _snapshot is null ? "PLAYBACK UNAVAILABLE" :
        _snapshot.PlaybackState == "UNAVAILABLE" ? "SEQUENCE PLAYBACK UNAVAILABLE · NO TAKE" :
        _snapshot.PlaybackState;
    public long DurationTicks => Sequence?.Clips.Length > 0
        ? Sequence.Clips.Max(c => checked(c.TimelineStart + c.Duration))
        : SequenceTimelineGeometry.FrameTicks(1, Sequence?.FrameRate ?? new SequenceRate(50, 1));
    public int TrackCount => Sequence?.Tracks.Length ?? 0;
    public int VisibleTrackCount => _visibleTrackCount;
    public long VisibleStart => _visibleStart;
    public long VisibleEnd => checked(_visibleStart + VisibleSpan);
    public long VisibleSpan => Math.Max(1, checked((long)Math.Ceiling((double)DurationTicks / _zoom)));
    public long EditCursorTick
    {
        get => _editCursor;
        set
        {
            var next = Math.Clamp(value, 0, Math.Max(0, DurationTicks - 1));
            if (_editCursor == next) return;
            _editCursor = next; Changed(); Changed(nameof(EditCursorLabel)); UpdateSelectedStatus();
        }
    }
    public string EditCursorLabel => SequenceTimelineGeometry.Timecode(_editCursor,
        Sequence?.FrameRate ?? new SequenceRate(50, 1));
    public bool Snapping
    {
        get => _snapping;
        set { _snapping = value; Changed(); }
    }
    public double Zoom
    {
        get => _zoom;
        set
        {
            var next = Math.Clamp(value, 1, 64);
            if (_zoom == next) return;
            _zoom = next; _visibleStart = Math.Min(_visibleStart, Math.Max(0, DurationTicks - VisibleSpan));
            Changed(); RefreshViewport(); RaiseCommands();
        }
    }
    public int FirstTrack
    {
        get => _firstTrack;
        set
        {
            var next = Math.Clamp(value, 0, Math.Max(0, TrackCount - 1));
            if (_firstTrack == next) return;
            _firstTrack = next; Changed(); RefreshViewport(); RaiseCommands();
        }
    }
    public string SelectedStatus { get; private set; } = "SELECT A SAVED CLIP · editing does not change Program";
    public SequenceClip? SelectedClip => Sequence?.Clips.FirstOrDefault(c => _selectedOrder.Contains(c.ClipId));
    public SequenceTrack? SelectedTrack => SelectedClip is { } c
        ? Sequence?.Tracks.SingleOrDefault(t => t.TrackId == c.TrackId) : null;
    public int SelectedCount => _selectedClips.Count;
    public IReadOnlyList<Guid> SelectedIds => _selectedOrder.AsReadOnly();
    public string GestureLabel => _gesture is null ? "NO PENDING EDIT" :
        $"DRAFT {_gesture.Kind.ToString().ToUpperInvariant()} · {SequenceTimelineGeometry.Timecode(_gesture.PreviewTick, Sequence!.FrameRate)} · RELEASE TO APPLY / ESC CANCEL";
    public SequenceWaveformPresentation? SelectedWaveform { get; private set; }
    public string WaveformStatus { get; private set; } = "WAVEFORM · select audio clip to request real analysis";

    public void SetViewport(double width, int visibleTrackCount = 8)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        _viewportWidth = Math.Max(100, width);
        _visibleTrackCount = Math.Clamp(visibleTrackCount, 1, SequenceTimelineGeometry.MaximumRealizedRows);
        RefreshViewport();
    }

    public void Scroll(double fractionOfViewport)
    {
        if (_snapshot is null || !double.IsFinite(fractionOfViewport)) return;
        var shift = checked((long)Math.Round(VisibleSpan * fractionOfViewport));
        _visibleStart = Math.Clamp(checked(_visibleStart + shift), 0,
            Math.Max(0, DurationTicks - VisibleSpan));
        Changed(nameof(VisibleStart)); RefreshViewport(); RaiseCommands();
    }

    public void PlaceCursor(double x, double width)
    {
        if (Sequence is null) return;
        var tick = SequenceTimelineGeometry.ToTick(x, width, VisibleStart, VisibleEnd, Sequence.FrameRate);
        EditCursorTick = SequenceTimelineGeometry.Snap(tick, Sequence, Snapping);
    }

    public void SelectClip(Guid id, bool toggle = false, bool extend = false)
    {
        if (Sequence?.Clips.All(c => c.ClipId != id) != false) return;
        if (extend && _selectedOrder.Count > 0)
        {
            var ordered = Sequence.Clips.OrderBy(c =>
                Sequence.Tracks.Single(t => t.TrackId == c.TrackId).LayerOrder)
                .ThenBy(c => c.TimelineStart).ThenBy(c => c.ClipId)
                .Select(c => c.ClipId).ToArray();
            var a = Array.IndexOf(ordered, _selectedOrder[^1]);
            var b = Array.IndexOf(ordered, id);
            if (a >= 0 && b >= 0)
            {
                for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                    if (_selectedClips.Add(ordered[i])) _selectedOrder.Add(ordered[i]);
            }
        }
        else if (toggle)
        {
            if (!_selectedClips.Add(id)) { _selectedClips.Remove(id); _selectedOrder.Remove(id); }
            else _selectedOrder.Add(id);
        }
        else
        {
            _selectedClips.Clear(); _selectedOrder.Clear();
            _selectedClips.Add(id); _selectedOrder.Add(id);
        }
        UpdateSelectedStatus(); RefreshViewport(); RaiseCommands();
    }

    public void CancelGesture()
    {
        if (_gesture is null) return;
        _gesture = null; Changed(nameof(GestureLabel)); RefreshViewport();
    }

    public bool BeginGesture(Guid id, SequenceEditorGestureKind kind, long pointerTick)
    {
        if (!CanEdit) return false;
        var clip = Sequence!.Clips.SingleOrDefault(c => c.ClipId == id);
        if (clip is null) return false;
        SelectClip(id);
        _gesture = new(clip, kind, pointerTick, pointerTick, _snapshot!.AuthoringRevision);
        Changed(nameof(GestureLabel));
        return true;
    }

    public void UpdateGesture(long pointerTick)
    {
        if (_gesture is not { } g || _snapshot?.AuthoringRevision != g.Revision) { CancelGesture(); return; }
        var tick = SequenceTimelineGeometry.Snap(Math.Max(0, pointerTick), Sequence!, Snapping);
        _gesture = g with { PreviewTick = tick };
        Changed(nameof(GestureLabel)); RefreshViewport();
    }

    public async Task CompleteGestureAsync()
    {
        if (_gesture is not { } g) return;
        _gesture = null;
        Changed(nameof(GestureLabel)); RefreshViewport();
        if (_snapshot?.AuthoringRevision != g.Revision || !CanEdit) return;
        var delta = checked(g.PreviewTick - g.StartPointerTick);
        SequenceEdit? edit = g.Kind switch
        {
            SequenceEditorGestureKind.Move => new(SequenceEditKind.MoveClip, g.Clip.ClipId,
                Position: checked(g.Clip.TimelineStart + delta)),
            SequenceEditorGestureKind.TrimStart => new(SequenceEditKind.TrimClip, g.Clip.ClipId,
                SourceIn: checked(g.Clip.SourceIn + delta), SourceOut: g.Clip.SourceOut),
            SequenceEditorGestureKind.TrimEnd => new(SequenceEditKind.TrimClip, g.Clip.ClipId,
                SourceIn: g.Clip.SourceIn, SourceOut: checked(g.Clip.SourceOut + delta)),
            _ => null
        };
        if (edit is not null && delta != 0) await EditAsync([edit]);
    }

    public async Task RefreshAsync()
    {
        if (_authoring is null) { Feedback = "UNAVAILABLE · sequence authoring transport is not connected."; return; }
        await ExecuteAsync(async () =>
        {
            var result = await _authoring.ListAsync();
            _libraryRevision = result.AuthoringRevision;
            if (result.CapabilityVersion != SequenceContract.Version)
                throw new InvalidDataException("Sequence capability version differs from the accepted contract.");
            Sequences.Clear();
            foreach (var item in result.Sequences) Sequences.Add(item);
            SelectedSummary = Sequences.FirstOrDefault(s => s.SequenceId == SelectedSummary?.SequenceId) ??
                Sequences.FirstOrDefault();
            Feedback = Sequences.Count == 0
                ? "No saved sequences. Use the governed authoring API to create a sequence."
                : $"{Sequences.Count} saved sequence(s) · choose one and LOAD.";
            Status = "SEQUENCE CATALOGUE · AUTHENTIC CONTROLHOST";
        });
    }

    public async Task CreateAsync()
    {
        if (_authoring is null || _disposed || _busy) return;
        await ExecuteAsync(async () =>
        {
            var sequenceId = Guid.NewGuid();
            var tracks = new[]
            {
                new SequenceTrack(Guid.NewGuid(), "V1 Video", SequenceMediaKind.Video, 1),
                new SequenceTrack(Guid.NewGuid(), "V2 Video", SequenceMediaKind.Video, 2),
                new SequenceTrack(Guid.NewGuid(), "A1 Audio", SequenceMediaKind.Audio, 3, AudioBusId: "program"),
                new SequenceTrack(Guid.NewGuid(), "A2 Audio", SequenceMediaKind.Audio, 4, AudioBusId: "program")
            };
            var model = new SequenceDefinition(SequenceContract.Version, sequenceId,
                "Untitled sequence", new SequenceRate(50, 1), tracks, [], []);
            var result = await _authoring.SaveAsync(new SequenceMutationRequest(
                SequenceContract.Version, Guid.NewGuid(), _libraryRevision, sequenceId, Definition: model));
            if (!result.Saved)
            {
                Feedback = "REJECTED · " + string.Join("; ",
                    result.Issues.Take(4).Select(issue => issue.Code + ": " + issue.Message));
                Status = "AUTHORING CONFLICT · REFRESH CATALOGUE";
                return;
            }
            var summary = new SequenceSummary(sequenceId, model.Name,
                model.FrameRate, model.Tracks.Length, 0);
            Sequences.Add(summary);
            SelectedSummary = summary;
            await ReadAsync(sequenceId);
            Feedback = "SAVED · new four-track sequence with no imported media; add catalogue clips to saved tracks.";
        });
    }

    /// <summary>
    /// A library drag supplies only a catalogue asset ID and the saved track ID.
    /// ControlHost rechecks actual catalogue fingerprint/duration and revision
    /// before accepting the mutation. No file path or native decoder enters WPF.
    /// </summary>
    public async Task InsertCatalogueAssetAsync(Guid trackId, Guid assetId)
    {
        if (!CanEdit || Sequence is not { } model || _catalogue is null ||
            trackId == Guid.Empty || assetId == Guid.Empty) return;
        var track = model.Tracks.SingleOrDefault(t => t.TrackId == trackId);
        if (track is null || track.Kind is not (SequenceMediaKind.Video or SequenceMediaKind.Audio))
        {
            Feedback = "REJECTED · only real catalogue video/audio may be dropped onto matching saved tracks.";
            return;
        }
        await ExecuteAsync(async () =>
        {
            var catalog = await _catalogue.GetMediaAssetCatalogAsync();
            var asset = catalog.Assets.SingleOrDefault(a => a.AssetId.ToString() == assetId.ToString("D"));
            if (asset is null || asset.Availability != MediaAssetAvailability.Online ||
                track.Kind == SequenceMediaKind.Video && asset.VideoFormat.Width == 0 ||
                track.Kind == SequenceMediaKind.Audio && asset.AudioFormat.SampleRate == 0)
            {
                Feedback = "REJECTED · selected catalogue source is missing, offline or incompatible with the track.";
                return;
            }
            var duration = checked((long)((BigInteger)asset.Duration.Ticks *
                SequenceContract.TicksPerSecond / TimeSpan.TicksPerSecond));
            var start = SequenceTimelineGeometry.Quantize(EditCursorTick, model.FrameRate);
            if (duration < 1 || duration > SequenceContract.MaximumTime ||
                start > SequenceContract.MaximumTime - duration)
            {
                Feedback = "REJECTED · source duration or sequence placement exceeds the bounded authoring horizon.";
                return;
            }
            var clip = new SequenceClip(Guid.NewGuid(), trackId,
                new SequenceMediaReference(assetId, track.Kind, asset.FingerprintSha256),
                0, duration, start, duration);
            var revision = _snapshot!.AuthoringRevision;
            var result = await _authoring!.EditAsync(new SequenceMutationRequest(
                SequenceContract.Version, Guid.NewGuid(), revision,
                model.SequenceId, Edits: [new SequenceEdit(SequenceEditKind.InsertClip, clip.ClipId, Clip: clip)]));
            await ProcessResultAsync(model.SequenceId, result);
            if (result.Saved)
            {
                SelectClip(clip.ClipId);
                Feedback = $"SAVED · {asset.DisplayName} added to {track.Name} at revision {result.AuthoringRevision}.";
            }
        });
    }

    public async Task LoadAsync()
    {
        if (!CanLoad || SelectedSummary is not { } summary) return;
        await ExecuteAsync(async () => await ReadAsync(summary.SequenceId));
    }

    private async Task ReadAsync(Guid id)
    {
        if (_authoring is null) return;
        var result = await _authoring.GetAsync(id);
        if (result.Sequence.SequenceId != id || result.Sequence.Version != SequenceContract.Version ||
            result.Validation.AuthoringRevision != result.AuthoringRevision)
            throw new InvalidDataException("The read sequence does not match the saved authoring revision.");
        _snapshot = result;
        _libraryRevision = result.AuthoringRevision;
        _revisionConflict = !result.Validation.IsValid;
        _gesture = null;
        _selectedClips.RemoveWhere(c => result.Sequence.Clips.All(x => x.ClipId != c));
        _selectedOrder.RemoveAll(id => !_selectedClips.Contains(id));
        _visibleStart = Math.Clamp(_visibleStart, 0, Math.Max(0, DurationTicks - VisibleSpan));
        _firstTrack = Math.Min(_firstTrack, Math.Max(0, TrackCount - 1));
        EditCursorTick = Math.Clamp(EditCursorTick, 0, Math.Max(0, DurationTicks - 1));
        CancelWaveform();
        Status = result.Validation.IsValid
            ? "SAVED SEQUENCE · AUTHORING READY"
            : "SAVED SEQUENCE · INVALID REFERENCES";
        Feedback = result.Validation.IsValid
            ? $"Loaded {result.Sequence.Name} at authoritative revision {result.AuthoringRevision}. No production changes."
            : string.Join("; ", result.Validation.Issues.Take(4).Select(x => x.Code + ": " + x.Message));
        Changed(nameof(Snapshot)); Changed(nameof(Sequence)); Changed(nameof(HasSequence));
        Changed(nameof(SequenceName)); Changed(nameof(RevisionLabel)); Changed(nameof(PlaybackLabel));
        Changed(nameof(TrackCount)); Changed(nameof(DurationTicks));
        UpdateSelectedStatus(); RefreshViewport(); RaiseCommands();
    }

    public async Task EditAsync(SequenceEdit[] edits)
    {
        if (!CanEdit || _snapshot is not { } old || _authoring is null) return;
        await ExecuteAsync(async () =>
        {
            var result = await _authoring.EditAsync(new(SequenceContract.Version, Guid.NewGuid(),
                old.AuthoringRevision, old.Sequence.SequenceId, Edits: edits));
            await ProcessResultAsync(old.Sequence.SequenceId, result);
        });
    }

    private async Task ProcessResultAsync(Guid selected, SequenceMutationResult result)
    {
        if (!result.Saved)
        {
            Feedback = "REJECTED · " + string.Join("; ",
                result.Issues.Take(4).Select(i => $"{i.Code}: {i.Message}"));
            // A concurrent writer or catalogue mutation invalidates the local revision.
            // Never replay the original edit against a new revision implicitly.
            if (result.Issues.Any(i => i.Code is "sequence.revision_conflict" or
                "sequence.asset_reference" or "sequence.transaction_conflict"))
            {
                _revisionConflict = true;
                Status = "AUTHORING CONFLICT · RELOAD REQUIRED";
            }
            return;
        }
        await ReadAsync(selected);
        Feedback = $"SAVED · authoring revision {result.AuthoringRevision}; active Program is unchanged.";
    }

    private async Task HistoryAsync(bool undo)
    {
        if (!CanEdit || _snapshot is null || _authoring is null) return;
        var id = _snapshot.Sequence.SequenceId;
        var expected = _snapshot.AuthoringRevision;
        await ExecuteAsync(async () =>
        {
            var request = new SequenceMutationRequest(SequenceContract.Version,
                Guid.NewGuid(), expected, Guid.Empty);
            var result = undo ? await _authoring.UndoAsync(request) : await _authoring.RedoAsync(request);
            await ProcessResultAsync(id, result);
        });
    }

    private async Task DeleteAsync(bool ripple)
    {
        if (!CanEdit || SelectedClip is not { } clip || Sequence is null) return;
        var affected = clip.LinkGroupId is { } group
            ? Sequence.Clips.Where(c => c.LinkGroupId == group).Select(c => c.ClipId).ToHashSet()
            : new HashSet<Guid> { clip.ClipId };
        var edits = Sequence.Transitions.Where(t => affected.Contains(t.FromClipId) ||
            affected.Contains(t.ToClipId)).Select(t => new SequenceEdit(
            SequenceEditKind.DeleteTransition, t.TransitionId)).ToList();
        edits.Add(new(ripple ? SequenceEditKind.RippleDelete : SequenceEditKind.DeleteClip, clip.ClipId));
        await EditAsync(edits.ToArray());
    }

    private async Task SplitAsync()
    {
        if (!CanEdit || SelectedClip is not { } clip || Sequence is null) return;
        var group = clip.LinkGroupId is { } linked
            ? Sequence.Clips.Where(c => c.LinkGroupId == linked)
                .OrderBy(c => c.ClipId.ToString(), StringComparer.Ordinal).ToArray()
            : [clip];
        var pos = SequenceTimelineGeometry.Quantize(EditCursorTick, Sequence.FrameRate);
        if (pos <= clip.TimelineStart || pos >= SequenceTimelineGeometry.ClipEnd(clip))
        {
            Feedback = "REJECTED · Blade requires an interior frame of the selected clip.";
            return;
        }
        await EditAsync([new(SequenceEditKind.SplitClip, clip.ClipId, Position: pos,
            Ids: group.Select(_ => Guid.NewGuid()).ToArray(),
            LinkGroupId: clip.LinkGroupId.HasValue ? Guid.NewGuid() : null)]);
    }

    private void CopySelected()
    {
        _clipboard = SelectedClip is { } clip ? clip with { LinkGroupId = null } : null;
        Feedback = _clipboard is null ? "Nothing selected for COPY." :
            "Clip source, IN/OUT and media fingerprint copied locally. PASTE remains an explicit authoring edit.";
        RaiseCommands();
    }

    private async Task PasteAsync()
    {
        if (!CanEdit || Sequence is null || _clipboard is not { } clip) return;
        var inserted = clip with { ClipId = Guid.NewGuid(), TimelineStart =
            SequenceTimelineGeometry.Quantize(EditCursorTick, Sequence.FrameRate), LinkGroupId = null };
        await EditAsync([new(SequenceEditKind.InsertClip, inserted.ClipId, Clip: inserted)]);
    }

    private async Task LinkAsync()
    {
        if (!CanEdit || _selectedClips.Count < 2) return;
        await EditAsync([new(SequenceEditKind.Link, Guid.NewGuid(), Ids: _selectedOrder.ToArray())]);
    }

    private async Task UnlinkAsync()
    {
        if (!CanEdit || SelectedClip?.LinkGroupId is not { } group) { Feedback = "Selected clip is not linked."; return; }
        await EditAsync([new(SequenceEditKind.Unlink, group)]);
    }

    private async Task TransitionAsync()
    {
        if (!CanEdit || Sequence is null || _selectedClips.Count != 2) return;
        var a = Sequence.Clips.Single(c => c.ClipId == _selectedOrder[0]);
        var b = Sequence.Clips.Single(c => c.ClipId == _selectedOrder[1]);
        if (a.TrackId != b.TrackId) { Feedback = "REJECTED · transition endpoints require one track."; return; }
        if (a.TimelineStart > b.TimelineStart) (a, b) = (b, a);
        var track = Sequence.Tracks.Single(t => t.TrackId == a.TrackId);
        var begin = Math.Max(a.TimelineStart, b.TimelineStart);
        var finish = Math.Min(SequenceTimelineGeometry.ClipEnd(a), SequenceTimelineGeometry.ClipEnd(b));
        var length = Math.Max(0, finish - begin);
        var kind = length == 0 ? SequenceTransitionKind.Cut :
            track.Kind == SequenceMediaKind.Audio ? SequenceTransitionKind.Crossfade :
            SequenceTransitionKind.Dissolve;
        var transition = new SequenceTransition(Guid.NewGuid(), a.ClipId, b.ClipId, kind,
            b.TimelineStart, length);
        await EditAsync([new(SequenceEditKind.AddTransition, transition.TransitionId, Transition: transition)]);
    }

    private async Task ToggleTrackAsync(bool mute)
    {
        if (!CanEdit || SelectedTrack is not { } track) return;
        var updated = mute ? track with { Muted = !track.Muted } :
            track with { Enabled = !track.Enabled };
        await EditAsync([new(SequenceEditKind.UpdateTrack, track.TrackId, Track: updated)]);
    }

    private async Task LoadWaveformAsync()
    {
        if (_waveforms is null || SelectedClip is not { Media.Kind: SequenceMediaKind.Audio } clip ||
            Sequence is not { } sequence) return;
        var revision = _snapshot!.AuthoringRevision;
        WaveformStatus = "ANALYZING · bounded native audio observation"; Changed(nameof(WaveformStatus));
        SelectedWaveform = null; Changed(nameof(SelectedWaveform));
        var start = Math.Max(clip.TimelineStart, VisibleStart);
        var end = Math.Min(SequenceTimelineGeometry.ClipEnd(clip), VisibleEnd);
        if (end <= start) { WaveformStatus = "WAVEFORM · clip is outside viewport"; Changed(nameof(WaveformStatus)); return; }
        var sourceStart = checked(clip.SourceIn + start - clip.TimelineStart);
        var sourceEnd = checked(clip.SourceIn + end - clip.TimelineStart);
        try
        {
            var generation = ++_requestGeneration;
            var presentation = await _waveforms.RequestAsync(clip, sourceStart, sourceEnd,
                Math.Clamp((int)(_viewportWidth / 4), 16, MediaAnalysisContract.MaximumBuckets), generation);
            if (_disposed || generation != _requestGeneration || _snapshot?.AuthoringRevision != revision ||
                SelectedClip?.ClipId != clip.ClipId) return;
            SelectedWaveform = presentation;
            WaveformStatus = presentation?.Status ?? "WAVEFORM · no current observation";
            Changed(nameof(SelectedWaveform)); Changed(nameof(WaveformStatus));
            RefreshViewport();
        }
        catch (OperationCanceledException) { }
    }

    private void CancelWaveform()
    {
        _requestGeneration++;
        _waveforms?.Cancel();
        SelectedWaveform = null; WaveformStatus = "WAVEFORM · select audio and request real analysis";
        Changed(nameof(SelectedWaveform)); Changed(nameof(WaveformStatus));
    }

    private async Task ExecuteAsync(Func<Task> work)
    {
        if (_disposed || _busy) return;
        _busy = true; Changed(nameof(CanEdit)); RaiseCommands();
        try { await work(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Status = "AUTHORING UNAVAILABLE";
            Feedback = $"ControlHost request failed ({e.GetType().Name}). No local edit was committed.";
        }
        finally { _busy = false; Changed(nameof(CanEdit)); RaiseCommands(); }
    }

    public void RefreshViewport()
    {
        VisibleRows.Clear();
        if (Sequence is { } definition)
        {
            var rows = SequenceTimelineGeometry.Project(definition, VisibleStart, VisibleEnd,
                FirstTrack, _visibleTrackCount, _viewportWidth);
            foreach (var row in rows)
                VisibleRows.Add(row with { Clips = row.Clips.Select(c => c with {
                    Selected = _selectedClips.Contains(c.Id),
                    Waveform = c.Id == SelectedWaveform?.ClipId ? SelectedWaveform : null
                }).ToArray() });
        }
        Changed(nameof(VisibleStart)); Changed(nameof(VisibleEnd));
        Changed(nameof(VisibleSpan)); Changed(nameof(VisibleRows));
    }

    private void UpdateSelectedStatus()
    {
        if (SelectedClip is { } c && SelectedTrack is { } t)
            SelectedStatus = $"{t.Name} · {c.Media.Kind} · {c.ClipId} · " +
                $"IN {SequenceTimelineGeometry.ToMediaTicks(c.SourceIn) / 10_000_000.0:0.000}s / " +
                $"OUT {SequenceTimelineGeometry.ToMediaTicks(c.SourceOut) / 10_000_000.0:0.000}s · " +
                $"START {SequenceTimelineGeometry.Timecode(c.TimelineStart, Sequence!.FrameRate)} · " +
                $"{(_selectedClips.Count > 1 ? _selectedClips.Count + " selected" : "one selected")}";
        else
            SelectedStatus = "SELECT A SAVED CLIP · editing does not change Program";
        Changed(nameof(SelectedStatus)); Changed(nameof(SelectedClip));
        Changed(nameof(SelectedTrack)); Changed(nameof(SelectedCount));
        CancelWaveform();
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, CreateCommand, LoadCommand, UndoCommand, RedoCommand,
                 DeleteCommand, RippleDeleteCommand, SplitCommand, CopyCommand, PasteCommand, LinkCommand,
                 UnlinkCommand, TransitionCommand, ToggleTrackEnabledCommand, ToggleTrackMuteCommand,
                 WaveformCommand, ZoomInCommand, ZoomOutCommand, FitCommand,
                 ScrollBackwardCommand, ScrollForwardCommand, NextTracksCommand, PreviousTracksCommand })
            if (command is AsyncRelayCommand c) c.RaiseCanExecuteChanged();
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _waveforms?.Dispose();
        _gesture = null;
        _selectedClips.Clear(); _selectedOrder.Clear();
    }

    private sealed record SequenceEditorGesture(SequenceClip Clip, SequenceEditorGestureKind Kind,
        long StartPointerTick, long PreviewTick, ulong Revision);
}
