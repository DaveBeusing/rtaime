// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public enum MediaHoverCapability { Unavailable, ExistingThumbnail, Offline, IndependentAnalysis }
public sealed record MediaHoverPreview(string Key, ImageSource? Thumbnail, string Status,
    long? RequestedTicks = null, long? ActualTicks = null);

// A single replaceable, read-only analysis consumer. No production deck or local decoder is opened here.
// The Client/ControlHost/RuntimeHost analysis contract owns admission, source identity and native cleanup.
public sealed class MediaLibraryHoverPreview : IDisposable
{
    private readonly IMediaAssetCatalogClient? _analysisClient;
    private readonly string _consumerId = "operator-library-" + Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;

    public MediaLibraryHoverPreview(IMediaAssetCatalogClient? analysisClient = null) =>
        _analysisClient = analysisClient;

    // Optional presentation pressure must never become evidence of production readiness.
    public static bool IsUnderPressure(OperatorHealthDescriptor? health, DateTimeOffset now) =>
        health is not null && health.ObservedAtUtc != default && now >= health.ObservedAtUtc &&
        now - health.ObservedAtUtc <= TimeSpan.FromSeconds(10) &&
        (health.Runtime.State == "FAIL" || health.FrameBudget > TimeSpan.Zero && health.FrameTime >= health.FrameBudget);

    public static bool CanAnalyze(MediaPoolItemViewModel item) =>
        item.Kind == MediaPoolItemKind.Clip && item.IsOnline &&
        item.KnownDuration is { Ticks: > 0 } &&
        Guid.TryParse(item.ReferenceId, out var assetId) && assetId != Guid.Empty;

    public static long MapPositionTicks(TimeSpan duration, double fraction)
    {
        if (duration <= TimeSpan.Zero || !double.IsFinite(fraction))
            throw new ArgumentOutOfRangeException(nameof(fraction));
        var normalized = Math.Clamp(fraction, 0, 1);
        return (long)Math.Min(duration.Ticks - 1, Math.Round(duration.Ticks * normalized));
    }

    public static MediaHoverCapability Capability(MediaPoolItemViewModel item, bool hasAnalysis = false) =>
        !item.IsOnline ? MediaHoverCapability.Offline :
        hasAnalysis && CanAnalyze(item) ? MediaHoverCapability.IndependentAnalysis :
        item.Thumbnail is null ? MediaHoverCapability.Unavailable : MediaHoverCapability.ExistingThumbnail;

    public async Task<MediaHoverPreview?> RequestAsync(MediaPoolItemViewModel item,
        double fraction = 0.5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_disposed) return null;
        Cancel();
        var generation = Volatile.Read(ref _generation);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pending = pending;
        try
        {
            // One dwell/debounce path for pointer and keyboard. Later requests supersede earlier jobs.
            await Task.Delay(180, pending.Token);
            if (!Current(generation, pending.Token)) return null;
            var capability = Capability(item, _analysisClient is not null);
            if (capability == MediaHoverCapability.IndependentAnalysis)
                return await AnalyzeAsync(item, fraction, generation, pending.Token);
            return capability switch
            {
                MediaHoverCapability.Offline => new(item.Key, null, "OFFLINE · refresh or relink media"),
                MediaHoverCapability.ExistingThumbnail => new(item.Key, item.Thumbnail,
                    "MONITOR THUMBNAIL · not a time-indexed asset frame"),
                _ => new(item.Key, null, "UNSUPPORTED · no independent asset frame available")
            };
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { return null; }
        finally
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }
    }

    private bool Current(long generation, CancellationToken cancellationToken) =>
        !_disposed && !cancellationToken.IsCancellationRequested &&
        generation == Volatile.Read(ref _generation);

    private async Task<MediaHoverPreview?> AnalyzeAsync(MediaPoolItemViewModel item,
        double fraction, long generation, CancellationToken token)
    {
        var client = _analysisClient!;
        var assetId = Guid.Parse(item.ReferenceId!);
        var requestedTicks = MapPositionTicks(item.KnownDuration!.Value, fraction);
        MediaAnalysisJobRequest? job = null;
        var complete = false;
        try
        {
            var request = new MediaAnalysisRequest(MediaAnalysisContract.Version, assetId.ToString("D"),
                _consumerId, generation, MediaAnalysisKind.Frame, requestedTicks, 0,
                MediaAnalysisContract.MaximumFrameWidth, MediaAnalysisContract.MaximumFrameHeight, 0, 0);
            // Do not abandon an admitted job if cancellation races with Begin: receive its ID, then cancel.
            var begin = await client.BeginMediaAnalysisAsync(request);
            if (begin.Version != request.Version || begin.AssetId != request.AssetId ||
                begin.ConsumerId != request.ConsumerId || begin.Generation != generation ||
                !Guid.TryParse(begin.JobId, out var id) || id == Guid.Empty ||
                string.IsNullOrWhiteSpace(begin.RuntimeEpoch))
                return new(item.Key, null, "UNAVAILABLE · analysis identity was not confirmed", requestedTicks);
            job = new MediaAnalysisJobRequest(request.Version, begin.JobId, request.AssetId,
                _consumerId, generation, begin.RuntimeEpoch);
            if (!Current(generation, token)) return null;
            if (begin.State is MediaAnalysisState.Unavailable or MediaAnalysisState.Stale or
                MediaAnalysisState.Failed or MediaAnalysisState.Canceled)
                return new(item.Key, null, "UNAVAILABLE · " + begin.Detail, requestedTicks);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (!deadline.Token.IsCancellationRequested)
            {
                var observation = await client.GetMediaAnalysisAsync(job, deadline.Token);
                observation.Validate();
                if (!Current(generation, token)) return null;
                if (observation.Version != request.Version || observation.AssetId != request.AssetId ||
                    observation.ConsumerId != request.ConsumerId || observation.Generation != generation ||
                    observation.RuntimeEpoch != begin.RuntimeEpoch || observation.JobId != begin.JobId ||
                    observation.Kind != MediaAnalysisKind.Frame ||
                    observation.RequestedTicks != requestedTicks)
                    return new(item.Key, null, "STALE · analysis source or generation changed", requestedTicks);
                if (observation.State == MediaAnalysisState.Complete)
                {
                    complete = true;
                    if (observation.RgbaPixels is not { } rgba || observation.ActualTicks is not { } pts ||
                        pts < 0 || pts > item.KnownDuration!.Value.Ticks ||
                        observation.Width < 1 || observation.Height < 1)
                        return new(item.Key, null, "UNAVAILABLE · decoded frame payload not confirmed", requestedTicks);
                    var image = CreateFrozenFrame(rgba, observation.Width, observation.Height);
                    return Current(generation, token)
                        ? new(item.Key, image,
                            "ANALYSIS FRAME · requested " + FormatTicks(requestedTicks) +
                            " · actual PTS " + FormatTicks(pts) +
                            (observation.FromCache ? " · cache" : ""),
                            requestedTicks, pts)
                        : null;
                }
                if (observation.State is not (MediaAnalysisState.Pending or MediaAnalysisState.Running))
                    return new(item.Key, null,
                        observation.State.ToString().ToUpperInvariant() + " · " + observation.Detail, requestedTicks);
                await Task.Delay(150, deadline.Token);
            }
            return new(item.Key, null, "UNAVAILABLE · analysis exceeded the optional preview deadline",
                requestedTicks);
        }
        catch (NotSupportedException)
        {
            return Current(generation, token)
                ? new(item.Key, null, "UNSUPPORTED · independent analysis transport is unavailable", requestedTicks)
                : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || !Current(generation, token))
        {
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Analysis is optional presentation. Transport and pipe faults must not escape into WPF input handlers.
            return Current(generation, token)
                ? new(item.Key, null, "UNAVAILABLE · isolated media frame could not be read", requestedTicks)
                : null;
        }
        finally
        {
            if (job is not null && !complete)
            {
                // Cancellation acknowledgment is owned by Runtime, not inferred from the RPC response.
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await client.CancelMediaAnalysisAsync(job, cleanup.Token);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Runtime retains native admission until actual release; no optimistic cleanup assertion.
                }
            }
        }
    }

    private static string FormatTicks(long ticks) =>
        new TimeSpan(ticks).ToString(@"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);

    private static BitmapSource CreateFrozenFrame(byte[] rgba, int width, int height)
    {
        if (width is < 1 or > MediaAnalysisContract.MaximumFrameWidth ||
            height is < 1 or > MediaAnalysisContract.MaximumFrameHeight ||
            rgba.Length != checked(width * height * 4))
            throw new InvalidDataException("Isolated analysis RGBA frame dimensions are invalid.");
        // WPF Bgra32 expects BGRA, whereas the isolated Runtime pipe carries RGBA8.
        var bgra = GC.AllocateUninitializedArray<byte>(rgba.Length);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            bgra, checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    public void Cancel()
    {
        Interlocked.Increment(ref _generation);
        try { _pending?.Cancel(); }
        catch (ObjectDisposedException) { }
        _pending = null;
    }

    public void Dispose() { _disposed = true; Cancel(); }
}
