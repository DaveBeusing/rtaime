// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

public sealed partial class LocalMediaDeckRuntimeService
{
    private const int AnalysisMaximumRetainedJobs = 32;
    private const int AnalysisMaximumCacheEntries = 32;
    private const long AnalysisMaximumCacheBytes = 8L * 1024 * 1024;
    private readonly object _analysisGate = new();
    private readonly Dictionary<string, AnalysisWork> _analysisJobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _analysisGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalysisWork> _analysisPending = new(StringComparer.Ordinal);
    private readonly LinkedList<AnalysisWork> _analysisQueue = new();
    private readonly LinkedList<string> _analysisCacheOrder = new();
    private readonly Dictionary<string, (MediaAnalysisObservation Result, long Bytes, LinkedListNode<string> Node)> _analysisCache =
        new(StringComparer.Ordinal);
    private AnalysisWork? _analysisActive;
    private long _analysisCacheBytes;
    private bool _analysisStopped;
    // The test seam uses the same provider semantics without exposing media paths to clients.
    internal Func<string, MediaSourceId, MediaAssetId?, LocalMediaOpenResult>? AnalysisOpen { get; set; }

    private sealed class AnalysisWork(MediaAnalysisRequest request, MediaAnalysisFileIdentity identity, string path, string epoch)
    {
        internal readonly string Id = Guid.NewGuid().ToString("D");
        internal readonly CancellationTokenSource Stop = new(TimeSpan.FromSeconds(45));
        internal readonly MediaAnalysisRequest Request = request;
        internal readonly MediaAnalysisFileIdentity FileIdentity = identity;
        internal readonly string Path = path;
        internal readonly string Epoch = epoch;
        internal LocalMediaResourceLedger.LocalMediaResourceTicket? Ticket;
        internal Task? Execution;
        internal MediaAnalysisObservation Observation = new(
            MediaAnalysisContract.Version, Guid.NewGuid().ToString("D"), request.AssetId, request.ConsumerId,
            request.Generation, epoch, request.Kind, MediaAnalysisState.Pending, request.PositionTicks, null,
            0, 0, null, 0, 0, null, false, false, null, "Queued behind bounded optional media analysis.");
    }

    public MediaAnalysisBegin BeginAnalysis(MediaAnalysisRequest request, MediaAnalysisFileIdentity fileIdentity, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ArgumentNullException.ThrowIfNull(fileIdentity);
        fileIdentity.Validate();
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096)
            throw new ArgumentException("A bounded, Control-authorized source is required.", nameof(path));
        var job = new AnalysisWork(request, fileIdentity, path, _ledger.RuntimeEpoch);
        job.Observation = job.Observation with { JobId = job.Id };
        lock (_analysisGate)
        {
            if (_analysisStopped || _disposed) return ToBegin(job, MediaAnalysisState.Unavailable, "Runtime media analysis is stopping.");
            if (!_analysisGenerations.TryGetValue(request.ConsumerId, out var prior))
            {
                if (_analysisGenerations.Count >= MediaAnalysisContract.MaximumConsumers)
                    return ToBegin(job, MediaAnalysisState.Unavailable, "The bounded analysis consumer capacity is full.");
            }
            else if (request.Generation <= prior)
                return ToBegin(job, MediaAnalysisState.Stale, "The analysis request generation is no longer current.");

            _analysisGenerations[request.ConsumerId] = request.Generation;
            if (_analysisPending.Remove(request.ConsumerId, out var superseded))
            {
                RemoveQueued(superseded);
                superseded.Stop.Cancel();
                superseded.Observation = Finished(superseded, MediaAnalysisState.Stale,
                    "Replaced before decoder admission.", acknowledged: true);
                superseded.Stop.Dispose();
            }
            if (_analysisActive is { } active &&
                active.Request.ConsumerId == request.ConsumerId)
            {
                active.Stop.Cancel();
                active.Observation = active.Observation with {
                    State = MediaAnalysisState.Stale, Detail = "Replaced; waiting for native cleanup.",
                    CancellationAcknowledged = false, RgbaPixels = null, Waveform = null
                };
            }
            _analysisJobs.Add(job.Id, job);
            _analysisPending.Add(request.ConsumerId, job);
            _analysisQueue.AddLast(job);
            PruneCompletedJobs();
            StartNextAnalysis();
            return ToBegin(job, job.Observation.State, job.Observation.Detail);
        }
    }

    public MediaAnalysisObservation GetAnalysis(string jobId)
    {
        lock (_analysisGate)
        {
            if (!_analysisJobs.TryGetValue(jobId, out var job))
                throw new InvalidOperationException("Analysis job is unknown or belongs to another Runtime lifetime.");
            return job.Observation;
        }
    }

    public MediaAnalysisObservation CancelAnalysis(string jobId)
    {
        lock (_analysisGate)
        {
            if (!_analysisJobs.TryGetValue(jobId, out var job))
                throw new InvalidOperationException("Analysis job is unknown or belongs to another Runtime lifetime.");
            if (_analysisPending.Remove(job.Request.ConsumerId, out var pending) && ReferenceEquals(pending, job))
            {
                RemoveQueued(job);
                job.Stop.Cancel();
                job.Observation = Finished(job, MediaAnalysisState.Canceled, "Canceled before native admission.", true);
                job.Stop.Dispose();
            }
            else if (ReferenceEquals(_analysisActive, job))
            {
                job.Stop.Cancel();
                job.Observation = job.Observation with {
                    State = MediaAnalysisState.Canceled, CancellationAcknowledged = false,
                    RgbaPixels = null, Waveform = null,
                    Detail = "Cancellation requested; native owner has not yet released its reservation."
                };
            }
            return job.Observation;
        }
    }

    private static MediaAnalysisBegin ToBegin(AnalysisWork work, MediaAnalysisState state, string detail) =>
        new(MediaAnalysisContract.Version, work.Id, work.Request.AssetId, work.Request.ConsumerId,
            work.Request.Generation, work.Epoch, state, detail);

    private static MediaAnalysisObservation Finished(AnalysisWork job, MediaAnalysisState state, string detail,
        bool acknowledged = false, string? code = null) =>
        job.Observation with {
            State = state, CancellationAcknowledged = acknowledged, RgbaPixels = null,
            Waveform = null, ErrorCode = code, Detail = detail.Length <= 512 ? detail : detail[..512]
        };

    private void RemoveQueued(AnalysisWork job)
    {
        var node = _analysisQueue.Find(job);
        if (node is not null) _analysisQueue.Remove(node);
    }

    // Called only with _analysisGate. Exactly one decode worker may be active per Runtime.
    private void StartNextAnalysis()
    {
        if (_analysisStopped || _analysisActive is not null || _analysisQueue.First is null) return;
        var node = _analysisQueue.First!;
        _analysisQueue.RemoveFirst();
        var job = node.Value;
        _analysisPending.Remove(job.Request.ConsumerId);
        _analysisActive = job;
        job.Observation = job.Observation with { State = MediaAnalysisState.Running,
            Detail = "Waiting for global Runtime resource admission." };
        job.Execution = Task.Run(() => ExecuteAnalysisAsync(job));
    }

    private void PruneCompletedJobs()
    {
        while (_analysisJobs.Count > AnalysisMaximumRetainedJobs)
        {
            var finished = _analysisJobs.Values.FirstOrDefault(j => j.Execution?.IsCompleted == true ||
                j.Execution is null && j.Observation.State is
                    MediaAnalysisState.Canceled or MediaAnalysisState.Stale or MediaAnalysisState.Unavailable);
            if (finished is null) return;
            _analysisJobs.Remove(finished.Id);
        }
    }

    private async Task ExecuteAnalysisAsync(AnalysisWork job)
    {
        LocalMediaFileSource? source = null;
        var ticket = (LocalMediaResourceLedger.LocalMediaResourceTicket?)null;
        var openingNative = false;
        var acknowledged = true;
        var observation = job.Observation;
        try
        {
            var cancellation = job.Stop.Token;
            cancellation.ThrowIfCancellationRequested();
            if (!_ledger.TryReserve(Guid.NewGuid(), new(_ledger.RuntimeEpoch, MediaSourceId.New(),
                null, null, null, LocalMediaResourcePurpose.Analysis), out ticket, out var rejected))
            {
                observation = Finished(job, MediaAnalysisState.Unavailable,
                    rejected?.Message ?? "Runtime optional decoder admission is unavailable.",
                    code: rejected?.Code);
                return;
            }
            job.Ticket = ticket;
            var path = Path.GetFullPath(job.Path);
            VerifyPathIdentity(path, job.FileIdentity);
            await VerifyFingerprintAsync(path, job.FileIdentity.FingerprintSha256, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            VerifyPathIdentity(path, job.FileIdentity);

            var cacheKey = string.Join('|', job.Request.AssetId,
                job.FileIdentity.FingerprintSha256, job.FileIdentity.LengthBytes,
                job.FileIdentity.LastWriteUtcTicks, MediaAnalysisContract.Version,
                _format.FrameRate.ToString(), job.Request.Kind, job.Request.PositionTicks,
                job.Request.EndTicks, job.Request.Width, job.Request.Height, job.Request.Level,
                job.Request.BucketCount);
            lock (_analysisGate)
            {
                if (_analysisCache.TryGetValue(cacheKey, out var hit))
                {
                    _analysisCacheOrder.Remove(hit.Node);
                    _analysisCacheOrder.AddLast(hit.Node);
                    observation = hit.Result with { JobId = job.Id, AssetId = job.Request.AssetId,
                        ConsumerId = job.Request.ConsumerId, Generation = job.Request.Generation,
                        RuntimeEpoch = job.Epoch, FromCache = true };
                }
            }
            if (observation.State != MediaAnalysisState.Complete)
            {
                openingNative = true;
                var opened = AnalysisOpen is { } factory
                    ? factory(path, ticket!.Identity.SourceId, new MediaAssetId(Identity.Parse(job.Request.AssetId)))
                    : _provider.TryOpen(path, ticket!.Identity.SourceId, new MediaAssetId(Identity.Parse(job.Request.AssetId)));
                openingNative = false;
                source = opened.Source;
                if (source is null)
                {
                    observation = Finished(job, MediaAnalysisState.Unavailable,
                        opened.Failure?.Message ?? "The source decoder is not available.", code: opened.Failure?.Code);
                }
                else
                {
                    cancellation.ThrowIfCancellationRequested();
                    observation = job.Request.Kind == MediaAnalysisKind.Frame
                        ? DecodeFrame(job, source, cancellation)
                        : DecodeWaveform(job, source, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    VerifyPathIdentity(path, job.FileIdentity);
                    if (observation.State == MediaAnalysisState.Complete)
                        InsertCache(cacheKey, observation);
                }
            }
        }
        catch (OperationCanceledException)
        {
            observation = Finished(job, MediaAnalysisState.Canceled,
                "Canceled; awaiting confirmed decoder cleanup.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or InvalidOperationException or OverflowException)
        {
            var stale = exception is FileIdentityChangedException;
            observation = Finished(job, stale ? MediaAnalysisState.Stale : MediaAnalysisState.Failed,
                stale ? "Media file identity changed. Refresh or relink the catalogue asset." :
                    "Isolated media analysis failed: " + exception.GetType().Name,
                code: stale ? "media.analysis.source_stale" : "media.analysis.failed");
        }
        finally
        {
            if (openingNative && ticket is not null) { ticket.Quarantine(job); acknowledged = false; }
            try { source?.Dispose(); }
            catch (Exception)
            {
                ticket?.Quarantine(source ?? job);
                acknowledged = false;
                observation = Finished(job, MediaAnalysisState.Unavailable,
                    "Native decoder cleanup was not confirmed. Runtime restart is required.",
                    code: "media.analysis.cleanup_uncertain");
            }
            ticket?.OwnerCompleted();
            lock (_analysisGate)
            {
                if (job.Stop.IsCancellationRequested || job.Observation.State == MediaAnalysisState.Stale)
                {
                    var stale = job.Observation.State == MediaAnalysisState.Stale;
                    observation = Finished(job, stale ? MediaAnalysisState.Stale : MediaAnalysisState.Canceled,
                        acknowledged ? "Old analysis completed cleanup without publication." :
                            "Native cancellation remains uncertain; Runtime restart required.",
                        acknowledged);
                }
                else observation = observation with { CancellationAcknowledged = false };
                job.Observation = observation;
                if (ReferenceEquals(_analysisActive, job)) _analysisActive = null;
                job.Stop.Dispose();
                StartNextAnalysis();
            }
        }
    }

    private static MediaAnalysisObservation DecodeFrame(AnalysisWork job, LocalMediaFileSource source, CancellationToken stop)
    {
        var target = FrameForPosition(source.Probe, job.Request.PositionTicks);
        var seek = source.SeekToFrame(target);
        if (!seek.Succeeded)
            return Finished(job, MediaAnalysisState.Unavailable, "Analysis seek is unavailable.",
                code: seek.Failure?.Code);
        for (var offset = 0; offset < 150; offset++)
        {
            stop.ThrowIfCancellationRequested();
            var next = source.ReadNext((ulong)offset, stop);
            if (!next.Succeeded)
                return Finished(job, MediaAnalysisState.Partial, "No frame at the requested media position.",
                    code: next.Failure?.Code);
            var frame = next.Frame!;
            var actual = FrameTicks(frame.Video.Timing);
            if (actual < job.Request.PositionTicks) continue;
            var rgba = ResizeRgba(frame.RgbaPixels.Span, checked((int)source.Probe.VideoFormat.Width),
                checked((int)source.Probe.VideoFormat.Height), job.Request.Width, job.Request.Height);
            return job.Observation with {
                State = MediaAnalysisState.Complete, ActualTicks = actual,
                Width = job.Request.Width, Height = job.Request.Height,
                RgbaPixels = rgba, Detail = "Decoded frame at its actual presentation timestamp."
            };
        }
        return Finished(job, MediaAnalysisState.Partial, "The bounded seek scan did not reach the requested PTS.");
    }

    private static MediaAnalysisObservation DecodeWaveform(AnalysisWork job, LocalMediaFileSource source, CancellationToken stop)
    {
        if (source.Probe.AudioCodec == MediaAudioCodec.None)
            return Finished(job, MediaAnalysisState.Unavailable, "The selected asset has no supported audio stream.",
                code: "media.analysis.audio_unavailable");
        var request = job.Request;
        var rate = checked((int)source.Probe.AudioFormat.SampleRate);
        var channels = checked((int)source.Probe.AudioFormat.ChannelCount);
        var accumulator = new MediaAnalysisWaveformBuilder(request.PositionTicks, request.EndTicks,
            request.BucketCount, rate, channels);
        var target = FrameForPosition(source.Probe, request.PositionTicks);
        var seek = source.SeekToFrame(target);
        if (!seek.Succeeded)
            return Finished(job, MediaAnalysisState.Unavailable, "Audio analysis seek is unavailable.",
                code: seek.Failure?.Code);
        var framePeriod = checked((long)decimal.Round(
            (decimal)TimeSpan.TicksPerSecond * source.Probe.VideoFormat.FrameRate.Denominator /
            source.Probe.VideoFormat.FrameRate.Numerator, 0, MidpointRounding.AwayFromZero));
        long? firstActual = null;
        var ended = false;
        const int maxDecodeWindows = 7_200;
        for (var offset = 0; offset < maxDecodeWindows; offset++)
        {
            stop.ThrowIfCancellationRequested();
            var next = source.ReadNext((ulong)offset, stop);
            if (!next.Succeeded)
            {
                ended = true;
                break;
            }
            var frame = next.Frame!;
            var timestamp = FrameTicks(frame.Video.Timing);
            if (timestamp >= request.EndTicks) { ended = true; break; }
            if (timestamp + framePeriod <= request.PositionTicks) continue;
            firstActual ??= timestamp;
            if (frame.Audio is not null && !frame.AudioPayload.IsEmpty)
                accumulator.Observe(frame.Audio, frame.AudioPayload);
            else
                accumulator.MarkMissing(Math.Max(request.PositionTicks, timestamp),
                    Math.Min(request.EndTicks, checked(timestamp + framePeriod)));
        }
        accumulator.Finish();
        var buckets = accumulator.Build();
        return job.Observation with {
            State = !accumulator.HasSamples ? MediaAnalysisState.Unavailable :
                ended && !accumulator.HasDiscontinuities ? MediaAnalysisState.Complete : MediaAnalysisState.Partial,
            ActualTicks = firstActual, SampleRate = rate, ChannelCount = channels,
            Waveform = buckets, Detail = !accumulator.HasSamples
                ? "No decoded audio samples are available in the requested range."
                : accumulator.HasDiscontinuities ? "Audio peaks include explicit discontinuities."
                : ended ? "Decoded channel peaks and RMS from actual PCM." :
                    "Partial waveform: bounded decode window limit reached."
        };
    }

    private static long FrameForPosition(LocalMediaProbe probe, long ticks)
    {
        var rate = probe.VideoFormat.FrameRate;
        var last = Math.Max(0, checked((long)decimal.Ceiling(
            (decimal)probe.Duration.Ticks * rate.Numerator /
            (TimeSpan.TicksPerSecond * (decimal)rate.Denominator)) - 1);
        var target = checked((long)decimal.Floor((decimal)ticks * rate.Numerator /
            (TimeSpan.TicksPerSecond * (decimal)rate.Denominator)));
        return Math.Clamp(target, 0, last);
    }

    private static long FrameTicks(FrameTiming timing) =>
        checked((long)decimal.Round((decimal)timing.PresentationTimestamp *
            timing.Timebase.Numerator * TimeSpan.TicksPerSecond / timing.Timebase.Denominator,
            0, MidpointRounding.AwayFromZero));

    private static byte[] ResizeRgba(ReadOnlySpan<byte> original, int width, int height, int targetWidth, int targetHeight)
    {
        if (width < 1 || height < 1 || original.Length != checked(width * height * 4))
            throw new InvalidDataException("Decoded RGBA frame has inconsistent dimensions.");
        var output = new byte[checked(targetWidth * targetHeight * 4)];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(height - 1, (int)(((long)y * 2 + 1) * height / (2 * targetHeight)));
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(width - 1, (int)(((long)x * 2 + 1) * width / (2 * targetWidth)));
                original.Slice(checked((sourceY * width + sourceX) * 4), 4)
                    .CopyTo(output.AsSpan(checked((y * targetWidth + x) * 4), 4));
            }
        }
        return output;
    }

    private sealed class FileIdentityChangedException : IOException { }

    private static void VerifyPathIdentity(string path, MediaAnalysisFileIdentity identity)
    {
        if (!OperatingSystem.IsWindows() || Path.GetPathRoot(path)?.Length != 3)
            throw new IOException("Media analysis requires an explicitly catalogued local Windows file.");
        for (FileSystemInfo? current = new FileInfo(path); current is not null;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Media analysis does not traverse links or mounted paths.");
        }
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length != identity.LengthBytes ||
            fileInfo.LastWriteTimeUtc.Ticks != identity.LastWriteUtcTicks)
            throw new FileIdentityChangedException();
    }

    private static async Task VerifyFingerprintAsync(string path, string expected, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            // The optional lane is explicitly rate-limited to ~16 MiB/s, not Program-critical I/O.
            await Task.Delay(4, cancellation).ConfigureAwait(false);
        }
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected,
            StringComparison.OrdinalIgnoreCase))
            throw new FileIdentityChangedException();
    }

    private void InsertCache(string key, MediaAnalysisObservation response)
    {
        var bytes = checked(response.RgbaPixels?.LongLength ?? 0 +
            (long)(response.Waveform?.Length ?? 0) * 128);
        if (bytes > AnalysisMaximumCacheBytes) return;
        lock (_analysisGate)
        {
            if (_analysisStopped || _analysisCache.ContainsKey(key)) return;
            while (_analysisCacheOrder.Count >= AnalysisMaximumCacheEntries ||
                   _analysisCacheBytes + bytes > AnalysisMaximumCacheBytes)
            {
                var oldest = _analysisCacheOrder.First!;
                var evicted = _analysisCache[oldest.Value];
                _analysisCacheBytes -= evicted.Bytes;
                _analysisCache.Remove(oldest.Value);
                _analysisCacheOrder.RemoveFirst();
            }
            var node = _analysisCacheOrder.AddLast(key);
            _analysisCache[key] = (response, bytes, node);
            _analysisCacheBytes += bytes;
        }
    }

    private void StopMediaAnalysis()
    {
        AnalysisWork? active;
        lock (_analysisGate)
        {
            _analysisStopped = true;
            active = _analysisActive;
            active?.Stop.Cancel();
            foreach (var pending in _analysisPending.Values)
            {
                pending.Stop.Cancel();
                pending.Observation = Finished(pending, MediaAnalysisState.Canceled,
                    "Runtime shutdown canceled queued media analysis.", true);
                pending.Stop.Dispose();
            }
            _analysisPending.Clear();
            _analysisQueue.Clear();
            _analysisCache.Clear();
            _analysisCacheOrder.Clear();
            _analysisCacheBytes = 0;
        }
        if (active?.Execution is { } task && !task.Wait(TimeSpan.FromSeconds(10)))
        {
            active.Ticket?.Quarantine(active);
            throw new IOException("Media analysis native owner did not finish cleanup; reservation remains quarantined.");
        }
        if (active?.Ticket is { } ticket && active.Execution?.IsCompleted == true) ticket.OwnerCompleted();
    }
}
