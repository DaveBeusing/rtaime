// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public enum MediaFilmstripQuality { Compact = 1, Detailed = 2 }

/// <summary>
/// Read-only, catalogue-bound request. No file path or production deck identity
/// enters the analysis request. Frame times are source-relative TimeSpan ticks.
/// </summary>
public sealed record MediaFilmstripRequest(
    string AssetId, string FingerprintSha256, long LengthBytes, long UpdatedUtcTicks,
    long DurationTicks, long StartTicks, long EndTicks, long FrameRateNumerator,
    long FrameRateDenominator, int Width, int Height, int SampleCount, MediaFilmstripQuality Quality)
{
    public const int MaximumSamples = 8;
    public const int MaximumWidth = 160;
    public const int MaximumHeight = 90;
    public const int MaximumStripBytes = MaximumSamples * MaximumWidth * MaximumHeight * 4;

    public static MediaFilmstripRequest FromAsset(MediaAssetDescriptor asset,
        long startTicks, long endTicks, int width, int height, int sampleCount,
        MediaFilmstripQuality quality = MediaFilmstripQuality.Detailed)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var rate = asset.VideoFormat.FrameRate;
        return new MediaFilmstripRequest(asset.AssetId.ToString(), asset.FingerprintSha256,
            asset.LengthBytes, asset.UpdatedAt.Value.UtcTicks, asset.Duration.Ticks,
            startTicks, endTicks, rate.Numerator, rate.Denominator,
            width, height, sampleCount, quality);
    }

    public void Validate()
    {
        var compact = Quality == MediaFilmstripQuality.Compact;
        if (!Guid.TryParse(AssetId, out var id) || id == Guid.Empty ||
            FingerprintSha256 is null || FingerprintSha256.Length != 64 ||
            !FingerprintSha256.All(Uri.IsHexDigit) || LengthBytes < 1 || UpdatedUtcTicks <= 0 ||
            DurationTicks <= 0 || StartTicks < 0 || EndTicks <= StartTicks ||
            EndTicks > DurationTicks || FrameRateNumerator <= 0 || FrameRateDenominator <= 0 ||
            Quality is not (MediaFilmstripQuality.Compact or MediaFilmstripQuality.Detailed) ||
            Width is < 1 or > MaximumWidth || Height is < 1 or > MaximumHeight ||
            compact && (Width > 80 || Height > 45) ||
            SampleCount is < 1 or > MaximumSamples ||
            (long)Width * Height * 4 * SampleCount > MaximumStripBytes)
            throw new ArgumentException("Filmstrip request exceeds source identity, range or preview capacity.");
    }

    internal string CacheKey => string.Join("|", new[]
    {
        AssetId, FingerprintSha256.ToUpperInvariant(),
        LengthBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        UpdatedUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DurationTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        EndTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FrameRateNumerator.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FrameRateDenominator.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ((int)Quality).ToString(System.Globalization.CultureInfo.InvariantCulture),
        MediaAnalysisContract.Version
    });

    /// <summary>
    /// Use rational nominal frame boundaries only for selecting seek positions.
    /// Variable-frame-rate truth comes exclusively from returned decoded ActualTicks.
    /// </summary>
    public long[] SamplePositions()
    {
        Validate();
        var positions = new List<long>(SampleCount);
        for (var i = 0; i < SampleCount; i++)
        {
            var midpoint = (long)decimal.Floor(StartTicks +
                (decimal)(EndTicks - StartTicks) * (2 * i + 1) / (2 * SampleCount));
            var frameIndex = decimal.Floor((decimal)midpoint * FrameRateNumerator /
                (TimeSpan.TicksPerSecond * (decimal)FrameRateDenominator));
            var aligned = decimal.Round(frameIndex * TimeSpan.TicksPerSecond * FrameRateDenominator /
                FrameRateNumerator, 0, MidpointRounding.AwayFromZero);
            var ticks = Math.Clamp((long)aligned, StartTicks, EndTicks - 1);
            if (positions.Count == 0 || positions[^1] != ticks)
                positions.Add(ticks);
        }
        return positions.ToArray();
    }
}

public sealed record MediaFilmstripSample(long RequestedTicks, long? ActualTicks,
    BitmapSource? Image, MediaAnalysisState State, bool FromCache, string Detail);

public sealed record MediaFilmstripResult(MediaFilmstripRequest Request, MediaAnalysisState State,
    IReadOnlyList<MediaFilmstripSample> Samples, string Detail)
{
    public int CompletedCount => Samples.Count(item => item.State == MediaAnalysisState.Complete);
}

public sealed record MediaFilmstripProgress(int Completed, int Total, MediaAnalysisState State);

/// <summary>
/// One replaceable analysis consumer for library/sequence thumbnails. Duplicate callers
/// of the same request share a task; the final waiter owns cancellation. Runtime still
/// owns catalogue authorization, independent decoder admission, disk cache and cleanup.
/// </summary>
public sealed class MediaAssetFilmstripService : IDisposable
{
    internal const int MaximumCacheEntries = 16;
    internal const long MaximumCacheBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    private readonly IMediaAssetCatalogClient _client;
    private readonly string _consumer = "operator-filmstrip-" + Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private readonly SemaphoreSlim _analysisLane = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private CancellationTokenSource? _activeStop;
    private Task<MediaFilmstripResult?>? _activeTask;
    private string? _activeKey;
    private int _waiters;
    private long _cacheBytes;
    private long _generation;
    private bool _disposed;

    private sealed record CacheEntry(MediaFilmstripResult Result, long Bytes,
        LinkedListNode<string> Node, DateTimeOffset InsertedAt);

    public MediaAssetFilmstripService(IMediaAssetCatalogClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    internal (int Entries, long Bytes) CacheUsage
    {
        get { lock (_gate) return (_cache.Count, _cacheBytes); }
    }

    public async Task<MediaFilmstripResult?> RequestAsync(
        MediaAssetDescriptor asset, long startTicks, long endTicks, int width, int height,
        int samples, MediaFilmstripQuality quality = MediaFilmstripQuality.Detailed,
        IProgress<MediaFilmstripProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var request = MediaFilmstripRequest.FromAsset(asset, startTicks, endTicks,
            width, height, samples, quality);
        request.Validate();
        if (cancellationToken.IsCancellationRequested) return null;

        // Do not reuse pixels on a stale, relinked or offline catalogue identity.
        // The catalogue remains the owner; this service never opens a media path.
        try
        {
            if (!await IsCurrentAssetAsync(request, cancellationToken))
            {
                lock (_gate) EvictAsset(request.AssetId);
                return new(request, MediaAnalysisState.Stale, [],
                    "Asset is offline, missing or has a newer catalogue fingerprint. Refresh or relink.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(request, MediaAnalysisState.Unavailable, [],
                "Authoritative asset identity could not be verified.");
        }

        Task<MediaFilmstripResult?>? work = null;
        MediaFilmstripResult? cached = null;
        lock (_gate)
        {
            if (_disposed) return null;
            // A relink purges older fingerprints for the same asset.
            foreach (var old in _cache.Where(pair =>
                pair.Value.Result.Request.AssetId == request.AssetId &&
                pair.Value.Result.Request.FingerprintSha256 != request.FingerprintSha256)
                .Select(pair => pair.Key).ToArray())
                RemoveCache(old);
            var key = request.CacheKey;
            if (_cache.TryGetValue(key, out var entry))
            {
                if (DateTimeOffset.UtcNow - entry.InsertedAt <= CacheLifetime)
                {
                    _lru.Remove(entry.Node);
                    _lru.AddLast(entry.Node);
                    CancelActive(); // A cached selection supersedes an unrelated pending strip.
                    cached = entry.Result;
                }
                else RemoveCache(key);
            }

            if (cached is null && _activeTask is not null &&
                !_activeTask.IsCompleted && _activeKey == key)
            {
                work = _activeTask;
                _waiters++;
            }
            else if (cached is null)
            {
                CancelActive();
                _activeStop = new CancellationTokenSource();
                _activeKey = key;
                _waiters = 1;
                work = RunOwnedAsync(request, _activeStop, progress);
                _activeTask = work;
            }
        }
        if (cached is not null)
        {
            progress?.Report(new(cached.CompletedCount, cached.Samples.Count, MediaAnalysisState.Complete));
            return cached;
        }

        try { return await work!.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeTask, work) && --_waiters == 0 && !work!.IsCompleted)
                    CancelActive();
            }
        }
    }

    private async Task<bool> IsCurrentAssetAsync(MediaFilmstripRequest request, CancellationToken token)
    {
        var catalog = await _client.GetMediaAssetCatalogAsync(token);
        var current = catalog.Assets.FirstOrDefault(item => item.AssetId.ToString() == request.AssetId);
        return current is not null && current.Availability == MediaAssetAvailability.Online &&
            string.Equals(current.FingerprintSha256, request.FingerprintSha256,
                StringComparison.OrdinalIgnoreCase) &&
            current.LengthBytes == request.LengthBytes &&
            current.UpdatedAt.Value.UtcTicks == request.UpdatedUtcTicks &&
            current.Duration.Ticks == request.DurationTicks &&
            current.VideoFormat.FrameRate.Numerator == request.FrameRateNumerator &&
            current.VideoFormat.FrameRate.Denominator == request.FrameRateDenominator;
    }

    private async Task<MediaFilmstripResult?> RunOwnedAsync(MediaFilmstripRequest request,
        CancellationTokenSource stop, IProgress<MediaFilmstripProgress>? progress)
    {
        try { return await RunAsync(request, stop.Token, progress); }
        finally { stop.Dispose(); }
    }

    private async Task<MediaFilmstripResult?> RunAsync(MediaFilmstripRequest request,
        CancellationToken canceled, IProgress<MediaFilmstripProgress>? progress)
    {
        try
        {
            // Rapid scrolling/scrubbing cancels before remote admission.
            await Task.Delay(150, canceled);
            using var totalBudget = CancellationTokenSource.CreateLinkedTokenSource(canceled);
            totalBudget.CancelAfter(TimeSpan.FromSeconds(45));
            // A cancelled slow Begin/transport request cannot overlap the next
            // filmstrip's decoder admission. Cancelled waiters leave this lane.
            await _analysisLane.WaitAsync(totalBudget.Token);
            try
            {
                var points = request.SamplePositions();
                var output = new List<MediaFilmstripSample>(points.Length);
                progress?.Report(new(0, points.Length, MediaAnalysisState.Running));
                foreach (var position in points)
                {
                    totalBudget.Token.ThrowIfCancellationRequested();
                    var sample = await AnalyzeFrameAsync(request, position, totalBudget.Token);
                    output.Add(sample);
                    progress?.Report(new(output.Count, points.Length, MediaAnalysisState.Running));
                }
                // A relink during decode revokes old pixels before presentation.
                if (!await IsCurrentAssetAsync(request, totalBudget.Token))
                    return new(request, MediaAnalysisState.Stale, [],
                        "Catalogue identity changed during thumbnail preparation.");
                totalBudget.Token.ThrowIfCancellationRequested();
                var completed = output.Count(sample => sample.State == MediaAnalysisState.Complete);
                var state = completed == points.Length ? MediaAnalysisState.Complete :
                    completed > 0 ? MediaAnalysisState.Partial : MediaAnalysisState.Unavailable;
                var result = new MediaFilmstripResult(request, state, output.ToArray(),
                    state == MediaAnalysisState.Complete ? "All thumbnails reflect confirmed decoded PTS." :
                    "One or more thumbnails were unavailable; no synthetic frames were generated.");
                if (state == MediaAnalysisState.Complete && !canceled.IsCancellationRequested)
                    lock (_gate)
                        if (!_disposed && _activeKey == request.CacheKey)
                            AddCache(result);
                progress?.Report(new(completed, points.Length, state));
                return result;
            }
            finally { _analysisLane.Release(); }
        }
        catch (OperationCanceledException) when (!canceled.IsCancellationRequested)
        {
            return new(request, MediaAnalysisState.Unavailable, [],
                "Bounded filmstrip analysis deadline exceeded.");
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(request, MediaAnalysisState.Unavailable, [],
                "Independent thumbnail analysis could not complete.");
        }
    }

    private async Task<MediaFilmstripSample> AnalyzeFrameAsync(
        MediaFilmstripRequest request, long position, CancellationToken token)
    {
        var generation = Interlocked.Increment(ref _generation);
        var analysis = new MediaAnalysisRequest(MediaAnalysisContract.Version, request.AssetId,
            _consumer, generation, MediaAnalysisKind.Frame, position, 0,
            request.Width, request.Height, 0, 0);
        MediaAnalysisJobRequest? job = null;
        var complete = false;
        try
        {
            // Do not abandon an acknowledged Begin on cancellation: retain its job ID
            // and send an explicit cancellation after it eventually returns.
            var begin = await _client.BeginMediaAnalysisAsync(analysis);
            if (begin.Version != analysis.Version || begin.AssetId != analysis.AssetId ||
                begin.ConsumerId != analysis.ConsumerId || begin.Generation != generation ||
                !Guid.TryParse(begin.JobId, out var id) || id == Guid.Empty ||
                string.IsNullOrWhiteSpace(begin.RuntimeEpoch))
                return new(position, null, null, MediaAnalysisState.Stale, false,
                    "Analysis job identity could not be verified.");
            job = new MediaAnalysisJobRequest(begin.Version, begin.JobId, begin.AssetId,
                begin.ConsumerId, begin.Generation, begin.RuntimeEpoch);
            token.ThrowIfCancellationRequested();
            if (begin.State is not (MediaAnalysisState.Pending or MediaAnalysisState.Running))
                return new(position, null, null, begin.State, false, begin.Detail);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                var observed = await _client.GetMediaAnalysisAsync(job, deadline.Token);
                observed.Validate();
                token.ThrowIfCancellationRequested();
                if (observed.JobId != job.JobId || observed.AssetId != job.AssetId ||
                    observed.ConsumerId != job.ConsumerId || observed.Generation != job.Generation ||
                    observed.RuntimeEpoch != job.RuntimeEpoch ||
                    observed.Kind != MediaAnalysisKind.Frame || observed.RequestedTicks != position)
                    return new(position, null, null, MediaAnalysisState.Stale, false,
                        "Observation belongs to an old asset, request, or Runtime generation.");
                if (observed.State == MediaAnalysisState.Complete)
                {
                    complete = true;
                    if (observed.ActualTicks is not { } pts || pts < position ||
                        pts > request.DurationTicks ||
                        observed.Width != request.Width || observed.Height != request.Height ||
                        observed.RgbaPixels is not { } rgba)
                        return new(position, null, null, MediaAnalysisState.Unavailable, false,
                            "Confirmed decoded image or PTS is unavailable.");
                    var image = CreateFrozenBitmap(rgba, observed.Width, observed.Height);
                    return new(position, pts, image, MediaAnalysisState.Complete,
                        observed.FromCache, "Decoded source PTS confirmed.");
                }
                if (observed.State is not (MediaAnalysisState.Pending or MediaAnalysisState.Running))
                    return new(position, observed.ActualTicks, null, observed.State, false, observed.Detail);
                await Task.Delay(150, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new(position, null, null, MediaAnalysisState.Unavailable, false,
                "Bounded per-frame analysis deadline exceeded.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(position, null, null, MediaAnalysisState.Unavailable, false,
                "Independent local frame analysis failed.");
        }
        finally
        {
            if (job is not null && !complete)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _client.CancelMediaAnalysisAsync(job, cleanup.Token);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Runtime retains native resource admission until true cleanup.
                }
            }
        }
    }

    private static BitmapSource CreateFrozenBitmap(byte[] rgba, int width, int height)
    {
        if (rgba.Length != checked(width * height * 4)) throw new InvalidDataException("Invalid RGBA payload.");
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, bgra, checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private void AddCache(MediaFilmstripResult result)
    {
        var key = result.Request.CacheKey;
        if (_cache.ContainsKey(key)) RemoveCache(key);
        var bytes = (long)result.Samples.Count(sample => sample.Image is not null) *
            result.Request.Width * result.Request.Height * 4;
        if (bytes > MaximumCacheBytes) return;
        while (_lru.Count >= MaximumCacheEntries || _cacheBytes + bytes > MaximumCacheBytes)
            RemoveCache(_lru.First!.Value);
        var node = _lru.AddLast(key);
        _cache.Add(key, new(result, bytes, node, DateTimeOffset.UtcNow));
        _cacheBytes += bytes;
    }

    private void EvictAsset(string assetId)
    {
        foreach (var key in _cache.Where(pair => pair.Value.Result.Request.AssetId == assetId)
            .Select(pair => pair.Key).ToArray())
            RemoveCache(key);
    }

    private void RemoveCache(string key)
    {
        if (!_cache.Remove(key, out var entry)) return;
        _lru.Remove(entry.Node);
        _cacheBytes -= entry.Bytes;
    }

    private void CancelActive()
    {
        try { _activeStop?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelActive();
            _cache.Clear();
            _lru.Clear();
            _cacheBytes = 0;
        }
    }
}
