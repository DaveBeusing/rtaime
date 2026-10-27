// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using rtaime.Core;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed partial class ReplayViewModel
{
    private RundownViewModel? _rundown;
    private bool _clipOutcomeUnknown;
    private bool _mustMarkNewRange;
    private bool _newRangeStarted;
    private string? _materializedClipName;
    private string _rundownFeedback = "Create a replay, open it on the selected Media Deck, then queue a local Rundown draft.";
    private bool _disposed;

    public ICommand QueueInRundownCommand { get; private set; } = null!;
    public ICommand ReviewNewRangeCommand { get; private set; } = null!;

    public bool ClipOutcomeUnknown => _clipOutcomeUnknown;
    public bool CanQueueInRundown => !_disposed && !IsBusy && !_clipOutcomeUnknown &&
        _rundown?.CanStageReplayClip == true && CanOpenClip &&
        _mediaDeck.IsLoaded && !_mediaDeck.IsBusy && !_mediaDeck.HasError &&
        !_mediaDeck.RequiresSynchronization &&
        string.Equals(_mediaDeck.AssetId, _lastClip?.AssetId, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(_mediaDeck.SourceId, out var id) && id != Guid.Empty;

    public string RundownFeedback
    {
        get => _rundownFeedback;
        private set
        {
            if (_rundownFeedback == value) return;
            _rundownFeedback = value;
            OnPropertyChanged();
        }
    }

    public string ReplayRangeEligibility
    {
        get
        {
            if (_clipOutcomeUnknown)
                return "CREATE OUTCOME UNKNOWN · inspect retained clip files and Media Library; do not repeat the same request.";
            if (_mustMarkNewRange)
                return _newRangeStarted
                    ? "NEW IN MARKED · set a fresh OUT before another CREATE."
                    : "NEW RANGE REQUIRED · mark a new IN and OUT after reviewing the uncertain result.";
            if (_snapshot.ClipState == ReplayControlClipState.Finalizing)
                return "FINALIZING · clip job still owns pinned segments; no duplicate CREATE.";
            if (_snapshot.MaximumStorageBytes <= 0 || _snapshot.RetainedSegmentCount <= 0)
                return "NO CONFIRMED RETAINED SEGMENTS · capture/storage not ready for a clip.";
            if (_snapshot.CaptureState == ReplayControlCaptureState.Failed ||
                _snapshot.Failure?.Code is "replay.storage.unavailable" or "replay.storage.cleanup_pending")
                return "REPLAY STORAGE/CAPTURE FAILURE · inspect errors and cleanup before materialization.";
            if (_snapshot.MarkIn is null || _snapshot.MarkOut is null)
                return "MARK IN + MARK OUT REQUIRED · source-relative capture window not selected.";
            if (!_snapshot.HasSelection || _snapshot.MarkIn < TimeSpan.Zero ||
                _snapshot.SelectedDuration > _snapshot.RetainedDuration)
                return "INVALID / EXPIRED RANGE · re-mark a continuously retained interval.";
            return _snapshot.Discontinuities > 0 || _snapshot.DroppedSamples > 0
                ? "RANGE CANDIDATE · capture gaps observed; Runtime validates pinned continuity before CREATE."
                : "RANGE CANDIDATE · Runtime still verifies pin, finalization and continuity.";
        }
    }

    public string ClipProvenance => _lastClip is not { Succeeded: true } clip
        ? "CLIP PROVENANCE · no finalized adopted file"
        : $"REPLAY {clip.ClipId} · CAPTURE {clip.SourceIn:c}–{clip.SourceOut:c} · " +
          $"SHA-256 {(clip.Sha256 is { Length: 64 } hash ? hash[..16] : "UNVERIFIED")}… · " +
          "clip-file IN=0; Runtime deck and ControlHost Rundown admission still required.";

    private void InitializeRundownWorkflow()
    {
        QueueInRundownCommand = new AsyncRelayCommand(QueueInRundownAsync, () => CanQueueInRundown);
        ReviewNewRangeCommand = new AsyncRelayCommand(ReviewNewRangeAsync,
            () => !_disposed && !IsBusy && _clipOutcomeUnknown);
        _mediaDeck.PropertyChanged += OnReplayDeckChanged;
    }

    public void AttachRundown(RundownViewModel rundown)
    {
        ArgumentNullException.ThrowIfNull(rundown);
        if (_disposed) throw new ObjectDisposedException(nameof(ReplayViewModel));
        if (ReferenceEquals(_rundown, rundown)) return;
        if (_rundown is not null) _rundown.PropertyChanged -= OnReplayRundownChanged;
        _rundown = rundown;
        _rundown.PropertyChanged += OnReplayRundownChanged;
        NotifyReplayJourney();
    }

    private void OnReplayDeckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaDeckViewModel.AssetId) or nameof(MediaDeckViewModel.SourceId)
            or nameof(MediaDeckViewModel.State) or nameof(MediaDeckViewModel.IsBusy)
            or nameof(MediaDeckViewModel.HasError) or nameof(MediaDeckViewModel.RequiresSynchronization))
            NotifyReplayJourney();
    }

    private void OnReplayRundownChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RundownViewModel.CanStageReplayClip) or
            nameof(RundownViewModel.StorageLabel) or nameof(RundownViewModel.State))
            NotifyReplayJourney();
    }

    private async Task QueueInRundownAsync()
    {
        if (!CanQueueInRundown) return;
        await RunAsync(async () =>
        {
        if (_rundown is null) return;
        var clip = RequireReadyClip();
        var catalog = await _client.GetMediaAssetCatalogAsync().ConfigureAwait(false);
        var asset = catalog.Assets.SingleOrDefault(value =>
            string.Equals(value.AssetId.ToString(), clip.AssetId, StringComparison.OrdinalIgnoreCase));
        if (!ReplayRundownEligibility.TryVerifyAsset(clip, asset, out var reason))
            throw new InvalidDataException(reason);

        // The deck may be switched while the catalogue read is pending.
        if (!_mediaDeck.IsLoaded || _mediaDeck.IsBusy || _mediaDeck.HasError ||
            _mediaDeck.RequiresSynchronization ||
            !string.Equals(_mediaDeck.AssetId, clip.AssetId, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(_mediaDeck.SourceId, out var sourceGuid) || sourceGuid == Guid.Empty)
            throw new InvalidOperationException("The confirmed Media Deck no longer owns this replay asset and production slot.");

        var source = new ProductionSourceId(new Identity(sourceGuid));
        if (!_rundown.TryStageReplayClip(clip, asset!, source,
            _materializedClipName ?? asset!.DisplayName))
        {
            RundownFeedback = _rundown.Detail;
            return;
        }
        RundownFeedback = "RUNDOWN DRAFT · verified replay asset and source slot · SAVE in Rundown, then PREPARE, then explicit GO. Program unchanged.";
        });
    }

    private async Task ReviewNewRangeAsync() => await RunAsync(async () =>
    {
        if (!_clipOutcomeUnknown) return;
        var snapshot = await _client.GetReplaySnapshotAsync().ConfigureAwait(false);
        ApplyConfirmedSnapshot(snapshot);
        if (snapshot.ClipState == ReplayControlClipState.Finalizing)
        {
            RundownFeedback = "Clip is still finalizing. Recheck without creating or altering its pinned range.";
            return;
        }
        // A read does not prove that a lost CREATE failed. The operator explicitly
        // starts a different capture interval; the previous file must be reviewed.
        _clipOutcomeUnknown = false;
        _mustMarkNewRange = true;
        _newRangeStarted = false;
        _lastClip = null;
        _materializedClipName = null;
        RundownFeedback = "NEW RANGE UNLOCKED · only after inspecting any finalized file / Media Library asset. Mark a NEW IN and OUT; the previous CREATE is never replayed.";
        NotifyReplayJourney();
    });

    private void NotifyReplayJourney()
    {
        OnPropertyChanged(nameof(ClipOutcomeUnknown));
        OnPropertyChanged(nameof(ReplayRangeEligibility));
        OnPropertyChanged(nameof(ClipProvenance));
        OnPropertyChanged(nameof(CanQueueInRundown));
        OnPropertyChanged(nameof(HasFailure));
        (QueueInRundownCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReviewNewRangeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mediaDeck.PropertyChanged -= OnReplayDeckChanged;
        if (_rundown is not null) _rundown.PropertyChanged -= OnReplayRundownChanged;
        NotifyReplayJourney();
    }
}
