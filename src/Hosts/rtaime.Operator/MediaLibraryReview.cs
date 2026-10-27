// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
using System.Windows.Media.Imaging;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed record MediaLibraryReviewSample(BitmapSource? Image, string Timecode, string Status);

// Non-destructive inspection markers only. These are source-relative fractions;
// authoring trim and production deck IN/OUT remain with their existing owners.
public static class MediaLibraryReviewRange
{
    public static (long Start, long End)? Map(TimeSpan duration, double inPercent, double outPercent)
    {
        if (duration.Ticks < 2 || !double.IsFinite(inPercent) || !double.IsFinite(outPercent) ||
            inPercent < 0 || outPercent > 100 || inPercent >= outPercent)
            return null;
        var start = (long)decimal.Floor(duration.Ticks * (decimal)inPercent / 100);
        var end = (long)decimal.Ceiling(duration.Ticks * (decimal)outPercent / 100);
        return start >= 0 && end > start && end <= duration.Ticks ? (start, end) : null;
    }
}

// One explicitly requested, selected-asset filmstrip. The analysis service owns
// bounded decoder admission and source-fingerprint checks; the Operator never
// opens files or changes resident Preview/Program playback to inspect frames.
public sealed partial class MediaPoolInspectorViewModel
{
    private MediaAssetFilmstripService? _reviewService;
    private CancellationTokenSource? _reviewStop;
    private long _reviewGeneration;
    private bool _reviewRunning;
    private bool _reviewDisposed;
    private string? _reviewAssetId;
    private string? _reviewFingerprint;
    private double _reviewInPercent;
    private double _reviewOutPercent = 100;
    public double ReviewInPercent
    {
        get => _reviewInPercent;
        set
        {
            if (!double.IsFinite(value) || !Set(ref _reviewInPercent, Math.Clamp(value, 0, 99))) return;
            CancelFrameReview("IN marker changed; review again.");
            CancelAudioReview("IN marker changed; review audio again.");
        }
    }
    public double ReviewOutPercent
    {
        get => _reviewOutPercent;
        set
        {
            if (!double.IsFinite(value) || !Set(ref _reviewOutPercent, Math.Clamp(value, 1, 100))) return;
            CancelFrameReview("OUT marker changed; review again.");
            CancelAudioReview("OUT marker changed; review audio again.");
        }
    }
        private long _reviewLength;
    private long _reviewUpdatedTicks;
    private IReadOnlyList<MediaLibraryReviewSample> _frameReviewSamples = [];
    private string _frameReviewStatus = "Select one online catalogue clip and choose REVIEW FRAMES.";

    public IReadOnlyList<MediaLibraryReviewSample> FrameReviewSamples
    {
        get => _frameReviewSamples;
        private set => Set(ref _frameReviewSamples, value);
    }

    public string FrameReviewStatus
    {
        get => _frameReviewStatus;
        private set => Set(ref _frameReviewStatus, value);
    }

    public ICommand ReviewFramesCommand { get; private set; } = null!;
    public ICommand CancelFrameReviewCommand { get; private set; } = null!;

    private void InitializeMediaReview()
    {
        _reviewService = _catalogClient is null ? null : new MediaAssetFilmstripService(_catalogClient);
        ReviewFramesCommand = new AsyncRelayCommand(ReviewSelectedFramesAsync, CanReviewSelectedFrames);
        CancelFrameReviewCommand = new AsyncRelayCommand(() =>
        {
            CancelFrameReview("Review cancelled. No production routing was changed.");
            return Task.CompletedTask;
        }, () => _reviewRunning || FrameReviewSamples.Count > 0);
        InitializeAudioReview();
    }

    private MediaAssetDescriptor? ReviewSelectedAsset()
    {
        if (SelectedItem is not { Kind: MediaPoolItemKind.Clip, IsOnline: true } item ||
            HasMultipleSelection || !Guid.TryParse(item.ReferenceId, out var id) || id == Guid.Empty)
            return null;
        return _catalogSnapshot.Assets.FirstOrDefault(asset =>
            asset.AssetId.ToString() == item.ReferenceId &&
            asset.Availability == MediaAssetAvailability.Online && asset.Duration > TimeSpan.Zero);
    }

    private bool CanReviewSelectedFrames() =>
        !_reviewDisposed && !_reviewRunning && !_audioReviewRunning && _reviewService is not null &&
        !_catalogBusy && !_batchRunning && !_operator.IsBusy &&
        _operator.IsConnected && !_operator.IsStale &&
        !MediaLibraryHoverPreview.IsUnderPressure(_operator.CurrentLibraryHealthEvidence, DateTimeOffset.UtcNow) &&
        ReviewSelectedAsset() is { } asset &&
        MediaLibraryReviewRange.Map(asset.Duration, ReviewInPercent, ReviewOutPercent) is not null;

    private async Task ReviewSelectedFramesAsync()
    {
        if (!CanReviewSelectedFrames() || ReviewSelectedAsset() is not { } asset || _reviewService is null ||
            MediaLibraryReviewRange.Map(asset.Duration, ReviewInPercent, ReviewOutPercent) is not { } range)
            return;

        CancelFrameReview("Starting a new selected-asset review.");
        var generation = _reviewGeneration;
        using var stop = new CancellationTokenSource();
        _reviewStop = stop;
        _reviewRunning = true;
        _reviewAssetId = asset.AssetId.ToString();
        _reviewFingerprint = asset.FingerprintSha256;
        _reviewLength = asset.LengthBytes;
        _reviewUpdatedTicks = asset.UpdatedAt.Value.UtcTicks;
        FrameReviewStatus = "ANALYZING · up to five source-bound frames, not Preview or Program.";
        RaiseReviewCommands();

        try
        {
            var result = await _reviewService.RequestAsync(
                asset, range.Start, range.End, 112, 63, 5,
                MediaFilmstripQuality.Detailed, cancellationToken: stop.Token);

            if (!ReviewIsCurrent(generation, stop.Token, asset)) return;
            if (result is null)
            {
                FrameReviewStatus = "CANCELLED · no analysis frame published.";
                return;
            }

            FrameReviewSamples = result.Samples.Take(5).Select(sample =>
                new MediaLibraryReviewSample(sample.Image,
                    (sample.ActualTicks is { } pts ? "PTS " : "REQUEST ") +
                    new TimeSpan(sample.ActualTicks ?? sample.RequestedTicks)
                        .ToString(@"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture),
                    sample.State == MediaAnalysisState.Complete && sample.Image is not null
                        ? (sample.FromCache ? "SOURCE FRAME · CACHE" : "SOURCE FRAME")
                        : sample.State.ToString().ToUpperInvariant() + " · " + sample.Detail))
                .ToArray();
            FrameReviewStatus = result.CompletedCount == result.Samples.Count &&
                result.State == MediaAnalysisState.Complete && result.Samples.Count > 0
                ? $"SOURCE FRAMES · {result.CompletedCount} decoded samples; no deck or routing change."
                : result.State.ToString().ToUpperInvariant() + " · " + result.Detail +
                  $" · {result.CompletedCount}/{result.Samples.Count} confirmed frames.";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ReviewIsCurrent(generation, stop.Token, asset))
                FrameReviewStatus = "UNAVAILABLE · bounded source analysis failed. Refresh or relink the asset.";
        }
        finally
        {
            if (ReferenceEquals(_reviewStop, stop))
            {
                _reviewStop = null;
                _reviewRunning = false;
                RaiseReviewCommands();
            }
        }
    }

    private bool ReviewIsCurrent(long generation, CancellationToken token, MediaAssetDescriptor asset)
    {
        if (_reviewDisposed || token.IsCancellationRequested || generation != _reviewGeneration ||
            !_operator.IsConnected || _operator.IsStale || HasMultipleSelection ||
            SelectedItem?.Kind != MediaPoolItemKind.Clip ||
            SelectedItem.ReferenceId != asset.AssetId.ToString())
            return false;

        var current = _catalogSnapshot.Assets.FirstOrDefault(candidate =>
            candidate.AssetId == asset.AssetId);
        return current is not null && current.Availability == MediaAssetAvailability.Online &&
            current.FingerprintSha256 == asset.FingerprintSha256 &&
            current.LengthBytes == asset.LengthBytes &&
            current.UpdatedAt.Value.UtcTicks == asset.UpdatedAt.Value.UtcTicks &&
            current.Duration == asset.Duration &&
            current.VideoFormat.FrameRate == asset.VideoFormat.FrameRate;
    }

    private void ReconcileMediaReviewContext()
    {
        if (_reviewAssetId is not null)
        {
            var asset = _catalogSnapshot.Assets.FirstOrDefault(candidate =>
                candidate.AssetId.ToString() == _reviewAssetId);
            if (SelectedItem?.ReferenceId != _reviewAssetId || HasMultipleSelection ||
                asset is null || asset.Availability != MediaAssetAvailability.Online ||
                asset.FingerprintSha256 != _reviewFingerprint || asset.LengthBytes != _reviewLength ||
                asset.UpdatedAt.Value.UtcTicks != _reviewUpdatedTicks ||
                !_operator.IsConnected || _operator.IsStale)
                CancelFrameReview("STALE · selection or asset identity changed; review again.");
        }
        ReconcileAudioReviewContext();
        RaiseReviewCommands();
    }

    private void ReconcileMediaReviewPressure()
    {
        if (!MediaLibraryHoverPreview.IsUnderPressure(
                _operator.CurrentLibraryHealthEvidence, DateTimeOffset.UtcNow))
        {
            RaiseReviewCommands();
            return;
        }
        if (_reviewRunning)
            CancelFrameReview("PAUSED · Runtime frame budget is under pressure.");
        if (_audioReviewRunning)
            CancelAudioReview("PAUSED · Runtime frame budget is under pressure.");
        RaiseReviewCommands();
    }

    private void CancelFrameReview(string status)
    {
        ++_reviewGeneration;
        try { _reviewStop?.Cancel(); }
        catch (ObjectDisposedException) { }
        _reviewStop = null;
        _reviewRunning = false;
        _reviewAssetId = null;
        _reviewFingerprint = null;
        FrameReviewSamples = [];
        FrameReviewStatus = status;
        RaiseReviewCommands();
    }

    private void RaiseReviewCommands()
    {
        (ReviewFramesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelFrameReviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RaiseAudioReviewCommands();
    }

    private void DisposeMediaReview()
    {
        if (_reviewDisposed) return;
        _reviewDisposed = true;
        CancelFrameReview("Review closed.");
        _reviewService?.Dispose();
        DisposeAudioReview();
    }
}
