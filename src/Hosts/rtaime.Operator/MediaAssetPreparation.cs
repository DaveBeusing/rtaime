// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed record MediaPreparationEvidenceRow(string Summary, string Detail);

public sealed partial class MediaPoolInspectorViewModel
{
    private CancellationTokenSource? _preparationStop;
    private string? _preparationJob;
    private DispatcherTimer? _preparationTimer;
    private bool _preparing, _preparationVisible, _preparationDisposed;
    private string _preparationStatus = "Select up to 16 catalogue assets. File decks must be closed deliberately. No routing changes.";
    private IReadOnlyList<MediaAssetPreflightObservation> _preflightEvidence = [];
    public IReadOnlyList<MediaAssetPreflightObservation> PreflightEvidence => _preflightEvidence;
    public IReadOnlyList<MediaPreparationEvidenceRow> PreparationRows
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return _preflightEvidence.Take(16).Select(evidence =>
            {
                var catalog = _catalogSnapshot.Assets.FirstOrDefault(asset =>
                    string.Equals(asset.AssetId.ToString(), evidence.AssetId, StringComparison.OrdinalIgnoreCase));
                var availability = catalog?.Availability.ToString().ToUpperInvariant() ?? "CATALOG UNKNOWN";
                var age = evidence.ObservedAtUtc == default || now < evidence.ObservedAtUtc
                    ? "AGE UNKNOWN"
                    : (now - evidence.ObservedAtUtc).TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s old";
                var current = evidence.ObservedAtUtc != default && now >= evidence.ObservedAtUtc &&
                    now - evidence.ObservedAtUtc <= TimeSpan.FromSeconds(10);
                var conditional = evidence.RepresentativeVerificationPassed && current;
                var checkState = evidence.Cancelled ? "CANCELED" : evidence.Running
                    ? "VERIFYING / " + evidence.State.ToString().ToUpperInvariant()
                    : conditional ? "CONDITIONALLY_READY"
                    : evidence.State == MediaAssetReadinessState.Unavailable ? "UNAVAILABLE"
                    : "UNVERIFIED";
                var resident = _mediaDeck.IsLoaded && !_mediaDeck.HasError &&
                    string.Equals(_mediaDeck.AssetId, evidence.AssetId, StringComparison.OrdinalIgnoreCase) &&
                    _mediaDeck.State is "READY" or "PLAYING" or "PAUSED"
                    ? "RESIDENT PREPARED" : "NOT RESIDENT";
                return new MediaPreparationEvidenceRow(
                    (catalog?.DisplayName ?? evidence.AssetId) + " · " + availability + " · " +
                    checkState + " · " + resident + " · " + age,
                    evidence.Detail);
            }).ToArray();
        }
    }
    public bool RequirePreparationAudio { get; set; }
    public int PreparationSeekPoints { get; set; } = 3;
    public int PreparationFramesPerPoint { get; set; } = 2;
    public string PreparationStatus { get => _preparationStatus; private set => Set(ref _preparationStatus, value); }
    public Visibility PreparationVisibility => _preparationVisible ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPreparing => _preparing;
    public ICommand PrepareAssetsCommand { get; private set; } = null!;
    public ICommand CancelPreparationCommand { get; private set; } = null!;
    public ICommand ClosePreparationCommand { get; private set; } = null!;

    private void InitializePreparation()
    {
        PrepareAssetsCommand = new AsyncRelayCommand(PrepareSelectedAssetsAsync, CanPrepareAssets);
        CancelPreparationCommand = new AsyncRelayCommand(() => { _preparationStop?.Cancel(); return Task.CompletedTask; }, () => _preparing);
        ClosePreparationCommand = new AsyncRelayCommand(() =>
        {
            _preparationVisible = false;
            _preparationTimer?.Stop();
            OnPropertyChanged(nameof(PreparationVisibility));
            return Task.CompletedTask;
        }, () => !_preparing);
        PropertyChanged += PreparationContextChanged;
        _operator.PropertyChanged += PreparationOperatorChanged;
        _preparationTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => ExpirePreparationEvidence(), Dispatcher.CurrentDispatcher);
    }
    public void ShowPreparation()
    {
        _preparationVisible = true;
        ExpirePreparationEvidence(); // Reject expired conditional observations before showing them.
        _preparationTimer?.Start(); // Presentation-only one-second age updates while the panel is visible.
        OnPropertyChanged(nameof(PreparationVisibility));
        OnPropertyChanged(nameof(PreparationRows));
        RaisePreparationCommands();
    }
    private HashSet<MediaAssetId>? SelectedPreparationIds()
    {
        if (SelectedItems.Count is < 1 or > 16) return null;
        var ids = new HashSet<MediaAssetId>();
        foreach (var item in SelectedItems)
        {
            if (item.Kind != MediaPoolItemKind.Clip || !item.IsOnline || !Guid.TryParse(item.ReferenceId, out var id) ||
                !ids.Add(new MediaAssetId(new Identity(id)))) return null;
        }
        return ids;
    }
    private bool CanPrepareAssets()
    {
        if (_preparationDisposed || _preparing || _catalogBusy || _batchRunning || _catalogClient is null ||
            !_operator.IsConnected || _operator.IsStale || _operator.IsBusy) return false;
        var ids = SelectedPreparationIds();
        return ids is not null && _catalogSnapshot.Assets.Count(asset => ids.Contains(asset.AssetId)) == ids.Count;
    }
    private void RaisePreparationCommands()
    {
        (PrepareAssetsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelPreparationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClosePreparationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    private void SetPreparationEvidence(IReadOnlyList<MediaAssetPreflightObservation> values)
    {
        _preflightEvidence = values;
        if (!_preparationDisposed)
        {
            OnPropertyChanged(nameof(PreflightEvidence));
            OnPropertyChanged(nameof(PreparationRows));
            Refresh();
        }
    }
    private void InvalidatePreparationEvidence(string reason)
    {
        if (!_preflightEvidence.Any(value => value.RepresentativeVerificationPassed || value.Running)) return;
        SetPreparationEvidence(_preflightEvidence.Select(value => value.RepresentativeVerificationPassed || value.Running
            ? value with { State = MediaAssetReadinessState.Unverified, Running = false,
                Cancelled = value.Cancelled || value.Running, Detail = reason } : value).ToArray());
    }
    private void ExpirePreparationEvidence()
    {
        if (_preparationDisposed) return;
        if (_preparationVisible && _preflightEvidence.Count > 0)
            OnPropertyChanged(nameof(PreparationRows)); // Reuse existing one-second UI timer.
        var now = DateTimeOffset.UtcNow;
        if (_preflightEvidence.Any(value => value.RepresentativeVerificationPassed &&
            (now < value.ObservedAtUtc || now - value.ObservedAtUtc > TimeSpan.FromSeconds(10))))
            InvalidatePreparationEvidence("Observation expired. Prepare again; no resident deck was retained.");
    }
    private void PreparationContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CatalogEvidence))
        {
            // Catalogue refresh/relink invalidates even an unchanged identity; no reusable positive cache.
            InvalidatePreparationEvidence("Catalogue observation changed. Prepare again.");
            OnPropertyChanged(nameof(PreparationRows));
            _preparationStop?.Cancel();
        }
        if (args.PropertyName is nameof(SelectionCount) or nameof(IsLoading)) RaisePreparationCommands();
    }
    private void PreparationOperatorChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(OperatorViewModel.IsConnected) or nameof(OperatorViewModel.IsStale) or nameof(OperatorViewModel.IsBusy))
            RaisePreparationCommands();
        if (args.PropertyName is nameof(OperatorViewModel.IsConnected) or nameof(OperatorViewModel.IsStale) &&
            (!_operator.IsConnected || _operator.IsStale))
        {
            _preparationStop?.Cancel();
            InvalidatePreparationEvidence("Control connection changed. Prepare again after synchronization.");
        }
    }
    private string AssetPreparationState(string assetId)
    {
        var value = _preflightEvidence.LastOrDefault(item => item.AssetId == assetId);
        if (value is null) return "UNVERIFIED";
        if (value.Running) return value.State == MediaAssetReadinessState.DecoderAvailable ? "DECODER_AVAILABLE" : "VERIFYING";
        if (value.RepresentativeVerificationPassed && DateTimeOffset.UtcNow - value.ObservedAtUtc <= TimeSpan.FromSeconds(10))
            return "CONDITIONALLY_READY";
        return value.State == MediaAssetReadinessState.Unavailable ? "UNAVAILABLE" : "UNVERIFIED";
    }
    public async Task PrepareSelectedAssetsAsync()
    {
        if (!CanPrepareAssets()) return;
        var selectedIds = SelectedPreparationIds()!;
        var selected = _catalogSnapshot.Assets.Where(asset => selectedIds.Contains(asset.AssetId)).ToArray();
        var options = new MediaAssetPreflightOptions(RequirePreparationAudio, PreparationSeekPoints, PreparationFramesPerPoint);
        try { options.Validate(); }
        catch (ArgumentException error) { PreparationStatus = error.Message; return; }
        _preparationStop?.Dispose();
        _preparationStop = new CancellationTokenSource();
        var stop = _preparationStop.Token;
        _preparing = true; OnPropertyChanged(nameof(IsPreparing)); RaisePreparationCommands();
        SetPreparationEvidence([]);
        var results = new List<MediaAssetPreflightObservation>(16);
        try
        {
            foreach (var asset in selected)
            {
                stop.ThrowIfCancellationRequested();
                PreparationStatus = $"Verifying {asset.DisplayName} ({results.Count + 1}/{selected.Length})…";
                // Never abandon a successful admission response: cancellation is sent by job id afterwards.
                var value = await _catalogClient!.BeginMediaAssetPreflightAsync(asset.AssetId, options);
                _preparationJob = value.Running ? value.JobId : null;
                while (value.Running)
                {
                    if (stop.IsCancellationRequested || _preparationDisposed)
                    {
                        await _catalogClient.CancelMediaAssetPreflightAsync(value.JobId);
                        stop.ThrowIfCancellationRequested();
                        throw new OperationCanceledException();
                    }
                    SetPreparationEvidence(results.Append(value).ToArray());
                    await Task.Delay(200, stop);
                    value = await _catalogClient.GetMediaAssetPreflightAsync(value.JobId);
                }
                value.Validate();
                if (results.Any(previous => previous.RuntimeEpoch != value.RuntimeEpoch || previous.TargetFormat != value.TargetFormat))
                    throw new InvalidOperationException("Runtime lifetime or target profile changed during preparation. Prepare the selection again.");
                if (!StringComparer.OrdinalIgnoreCase.Equals(value.SourceLocation, asset.SourceLocation) ||
                    !StringComparer.OrdinalIgnoreCase.Equals(value.FingerprintSha256, asset.FingerprintSha256))
                    throw new InvalidOperationException("Catalogue identity changed. Refresh the library and prepare again.");
                stop.ThrowIfCancellationRequested();
                results.Add(value);
                SetPreparationEvidence(results.ToArray());
                if (!value.RepresentativeVerificationPassed)
                    throw new InvalidOperationException(value.Detail);
                _preparationJob = null;
            }
            PreparationStatus = $"{results.Count} representative checks passed conditionally. Decoders released; whole-file/sustained performance UNVERIFIED. Open in Preview separately for resident deck preroll.";
        }
        catch (Exception error)
        {
            string? cancelFailure = null;
            if (_preparationJob is { } job)
            {
                try { await _catalogClient!.CancelMediaAssetPreflightAsync(job); }
                catch (Exception cleanup) { cancelFailure = " Cancellation could not be observed: " + cleanup.Message; }
            }
            InvalidatePreparationEvidence("Batch incomplete; partial observations rolled back. Prepare again.");
            PreparationStatus = (error is OperationCanceledException
                ? "Cancelled. Partial observations rolled back; native cleanup retains its reservation until complete."
                : "Preparation failed: " + error.Message + " Partial observations rolled back; transport unchanged.") + cancelFailure;
        }
        finally
        {
            _preparationJob = null; _preparing = false;
            OnPropertyChanged(nameof(IsPreparing)); RaisePreparationCommands();
            if (_preparationDisposed) _preparationStop?.Dispose();
        }
    }
    private void DisposePreparation()
    {
        _preparationDisposed = true;
        _preparationStop?.Cancel();
        if (!_preparing) _preparationStop?.Dispose();
        _preparationTimer?.Stop(); _preparationTimer = null;
        PropertyChanged -= PreparationContextChanged;
        _operator.PropertyChanged -= PreparationOperatorChanged;
        // The in-flight method sends cancellation after any admitted response and owns cleanup.
    }
}
