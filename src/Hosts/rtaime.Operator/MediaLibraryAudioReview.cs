// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

// One on-demand, non-authoritative PCM visualization consumer for the selected
// catalogue clip. Reuses the sequence waveform analysis service and its glyph,
// never an additional source decoder, audio output or sequence authoring path.
public sealed partial class MediaPoolInspectorViewModel
{
    private SequenceTimelineWaveformService? _audioReviewService;
    private CancellationTokenSource? _audioReviewStop;
    private long _audioReviewGeneration;
    private bool _audioReviewRunning;
    private string? _audioReviewAssetId;
    private string? _audioReviewFingerprint;
    private long _audioReviewLength;
    private long _audioReviewUpdatedTicks;
    private string _audioReviewStatus = "REVIEW AUDIO requests a source PCM waveform without routing media.";
    private SequenceWaveformPresentation? _audioReviewPresentation;

    public string AudioReviewStatus
    {
        get => _audioReviewStatus;
        private set => Set(ref _audioReviewStatus, value);
    }

    public SequenceWaveformPresentation? AudioReviewPresentation
    {
        get => _audioReviewPresentation;
        private set => Set(ref _audioReviewPresentation, value);
    }

    public ICommand ReviewAudioCommand { get; private set; } = null!;
    public ICommand CancelAudioReviewCommand { get; private set; } = null!;

    private void InitializeAudioReview()
    {
        if (_catalogClient is not null)
            _audioReviewService = new SequenceTimelineWaveformService(_catalogClient);
        ReviewAudioCommand = new AsyncRelayCommand(ReviewSelectedAudioAsync, CanReviewAudio);
        CancelAudioReviewCommand = new AsyncRelayCommand(() =>
        {
            CancelAudioReview("Audio review cleared; no output or saved state changed.");
            return Task.CompletedTask;
        }, () => _audioReviewRunning || AudioReviewPresentation is not null);
    }

    private bool CanReviewAudio()
    {
        var asset = ReviewSelectedAsset();
        return !_audioReviewRunning && _audioReviewService is not null &&
            CanReviewSelectedFrames() && asset is not null &&
            asset.AudioFormat.SampleRate > 0;
    }

    private static bool MatchesReviewAsset(MediaAssetCatalogSnapshot snapshot, MediaAssetDescriptor asset) =>
        snapshot.Assets.Any(current =>
            current.AssetId == asset.AssetId &&
            current.Availability == MediaAssetAvailability.Online &&
            current.FingerprintSha256 == asset.FingerprintSha256 &&
            current.LengthBytes == asset.LengthBytes &&
            current.UpdatedAt.Value.UtcTicks == asset.UpdatedAt.Value.UtcTicks &&
            current.Duration == asset.Duration);

    private bool AudioReviewCurrent(long generation, CancellationToken token, MediaAssetDescriptor asset) =>
        !_reviewDisposed && !token.IsCancellationRequested &&
        _audioReviewGeneration == generation &&
        SelectedItem?.ReferenceId == asset.AssetId.ToString() &&
        !HasMultipleSelection && _operator.IsConnected && !_operator.IsStale &&
        MatchesReviewAsset(_catalogSnapshot, asset);

    private async Task ReviewSelectedAudioAsync()
    {
        if (!CanReviewAudio() || ReviewSelectedAsset() is not { } asset ||
            MediaLibraryReviewRange.Map(asset.Duration, ReviewInPercent, ReviewOutPercent) is not { } range ||
            _audioReviewService is null || _catalogClient is null)
            return;

        CancelAudioReview("Preparing independent PCM analysis.");
        var generation = _audioReviewGeneration;
        using var stop = new CancellationTokenSource();
        _audioReviewStop = stop;
        _audioReviewRunning = true;
        _audioReviewAssetId = asset.AssetId.ToString();
        _audioReviewFingerprint = asset.FingerprintSha256;
        _audioReviewLength = asset.LengthBytes;
        _audioReviewUpdatedTicks = asset.UpdatedAt.Value.UtcTicks;
        AudioReviewStatus = "ANALYZING · source PCM, at most 120 seconds and 128 buckets.";
        RaiseReviewCommands();
        try
        {
            var before = await _catalogClient.GetMediaAssetCatalogAsync(stop.Token);
            if (!AudioReviewCurrent(generation, stop.Token, asset)) return;
            if (!MatchesReviewAsset(before, asset))
            {
                AudioReviewStatus = "STALE · catalogue fingerprint changed before waveform analysis.";
                return;
            }

            // The existing waveform service works in Sequence's rational edit timebase.
            // This ephemeral request is never written to a sequence or sent to Program.
            var sourceIn = (long)decimal.Floor((decimal)range.Start *
                SequenceContract.TicksPerSecond / TimeSpan.TicksPerSecond);
            var sourceOut = (long)decimal.Ceiling((decimal)range.End *
                SequenceContract.TicksPerSecond / TimeSpan.TicksPerSecond);
            if (sourceOut <= sourceIn) sourceOut = checked(sourceIn + 1);
            var clip = new SequenceClip(Guid.NewGuid(), Guid.NewGuid(),
                new SequenceMediaReference(Guid.Parse(asset.AssetId.ToString()),
                    SequenceMediaKind.Audio, asset.FingerprintSha256),
                sourceIn, sourceOut, 0, sourceOut - sourceIn);
            var observed = await _audioReviewService.RequestAsync(
                clip, sourceIn, sourceOut, 128, generation, stop.Token);
            if (!AudioReviewCurrent(generation, stop.Token, asset)) return;
            var after = await _catalogClient.GetMediaAssetCatalogAsync(stop.Token);
            if (!AudioReviewCurrent(generation, stop.Token, asset) ||
                !MatchesReviewAsset(after, asset))
            {
                if (!stop.IsCancellationRequested)
                    AudioReviewStatus = "STALE · source identity changed during PCM analysis.";
                return;
            }
            if (observed is { Channels: > 0, Buckets.Length: > 0 })
                AudioReviewPresentation = observed;
            AudioReviewStatus = observed?.Status ??
                "UNAVAILABLE · selected source did not provide PCM evidence.";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (AudioReviewCurrent(generation, stop.Token, asset))
                AudioReviewStatus = "UNAVAILABLE · independent PCM analysis failed.";
        }
        finally
        {
            if (ReferenceEquals(_audioReviewStop, stop))
            {
                _audioReviewStop = null;
                _audioReviewRunning = false;
                RaiseReviewCommands();
            }
        }
    }

    private void ReconcileAudioReviewContext()
    {
        if (_audioReviewAssetId is not null)
        {
            var asset = _catalogSnapshot.Assets.FirstOrDefault(candidate =>
                candidate.AssetId.ToString() == _audioReviewAssetId);
            if (SelectedItem?.ReferenceId != _audioReviewAssetId || HasMultipleSelection ||
                asset is null || asset.Availability != MediaAssetAvailability.Online ||
                asset.FingerprintSha256 != _audioReviewFingerprint ||
                asset.LengthBytes != _audioReviewLength ||
                asset.UpdatedAt.Value.UtcTicks != _audioReviewUpdatedTicks ||
                !_operator.IsConnected || _operator.IsStale)
                CancelAudioReview("STALE · source or selection changed; review audio again.");
        }
    }

    private void CancelAudioReview(string status)
    {
        ++_audioReviewGeneration;
        try { _audioReviewStop?.Cancel(); }
        catch (ObjectDisposedException) { }
        _audioReviewService?.Cancel();
        _audioReviewStop = null;
        _audioReviewRunning = false;
        _audioReviewAssetId = null;
        _audioReviewFingerprint = null;
        AudioReviewPresentation = null;
        AudioReviewStatus = status;
        RaiseAudioReviewCommands();
    }

    private void RaiseAudioReviewCommands()
    {
        (ReviewAudioCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelAudioReviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void DisposeAudioReview()
    {
        CancelAudioReview("Audio review closed.");
        _audioReviewService?.Dispose();
    }
}
