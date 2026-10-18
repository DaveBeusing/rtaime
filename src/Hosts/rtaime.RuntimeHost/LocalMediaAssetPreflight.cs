// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

public sealed partial class LocalMediaDeckRuntimeService
{
    private readonly string _preflightEpoch = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _preflightStop;
    private Task? _preflightTask;
    private LocalMediaResourceLedger.LocalMediaResourceTicket? _preflightTicket;
    private MediaAssetPreflightObservation? _preflight;
    private LocalMediaFileSource? _preflightQuarantine;
    private FileStream? _preflightIdentityQuarantine;
    private string[]? _preflightModulePaths;
    // An injectable production-provider seam permits deterministic cancellation/open tests.
    internal Func<string, MediaSourceId, MediaAssetId?, LocalMediaOpenResult>? PreflightOpen { get; set; }
    internal Func<string>? PreflightEnvironment { get; set; }

    private string PreflightEnvironmentIdentity(bool capture = false)
    {
        if (PreflightEnvironment is not null) return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_preflightEpoch + "|" + PreflightEnvironment())));
        if (capture)
        {
            using var process = Process.GetCurrentProcess();
            var paths = process.Modules.Cast<ProcessModule>().Select(module => module.FileName)
                .Where(path => path.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(513).ToArray();
            if (paths.Length > 512) throw new IOException("Loaded-module inventory exceeds the preflight bound.");
            _preflightModulePaths = paths;
        }
        var observedPaths = _preflightModulePaths ?? throw new IOException("Decoder module inventory is unavailable.");
        var text = new StringBuilder(_preflightEpoch).Append('|').Append(Environment.OSVersion.VersionString);
        foreach (var path in observedPaths)
        {
            var file = new FileInfo(path);
            text.Append('|').Append(file.FullName).Append(':').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks)
                .Append(':').Append(FileVersionInfo.GetVersionInfo(path).FileVersion);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public MediaAssetPreflightObservation BeginPreflight(string path, MediaAssetId assetId,
        string fingerprint, MediaAssetPreflightOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || fingerprint.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("An exact catalogue SHA-256 is required.", nameof(fingerprint));
        var full = Path.GetFullPath(path);
        if (full.Length > 4096) throw new ArgumentException("Preflight path exceeds the metadata bound.", nameof(path));
        var initial = new MediaAssetPreflightObservation(Guid.NewGuid().ToString("N"), assetId.ToString(),
            _preflightEpoch, full, fingerprint.ToUpperInvariant(), 0, 0,
            _format.FrameRate == FrameRate.Fps50 ? "1080p50" : "1080p59.94", "",
            MediaAssetReadinessState.Unverified, false, false, "UNVERIFIED", 0, 0,
            options.RequireAudio, DateTimeOffset.UtcNow);
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_gate)
            {
                if (_preflightQuarantine is not null || _preflightIdentityQuarantine is not null)
                    return initial with { State = MediaAssetReadinessState.Unavailable,
                        Detail = "Preflight cleanup retained an owner. Restart RuntimeHost through the supported application lifecycle before retrying." };
                if (_preflight is { Running: true } || _probeReservations != 0 || _entries.Any(e => e is not null))
                    return initial with { Detail = "Preflight unavailable: close file decks deliberately first. Live decks are never evicted." };
                var sourceId = MediaSourceId.New();
                if (!_ledger.TryReserve(Guid.NewGuid(), new(_ledger.RuntimeEpoch, sourceId, null, null, null,
                    LocalMediaResourcePurpose.Preflight), out var ticket, out var failure))
                    return initial with { Detail = failure!.Value.Code + ": " + failure.Value.Message };
                _preflightTicket = ticket;
                _preflightStop?.Dispose();
                _preflightStop = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                _probeReservations++;
                _preflight = initial with { Running = true, Detail = "Verifying file identity on the non-Program worker." };
                var token = _preflightStop.Token;
                _preflightTask = Task.Run(() => RunPreflightAsync(initial, assetId, options, token, ticket!));
                _ = _preflightTask.ContinueWith(completed => ticket!.OwnerCompleted(),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return _preflight;
            }
        }
    }

    public MediaAssetPreflightObservation GetPreflight(string jobId)
    {
        MediaAssetPreflightObservation value;
        lock (_gate)
        {
            if (_preflight is null || _preflight.JobId != jobId)
                throw new InvalidOperationException("Preflight belongs to another job or Runtime lifetime; retry explicitly.");
            value = _preflight;
            if (value.RepresentativeVerificationPassed && _entries.Any(entry => entry is not null))
            {
                _preflight = value = value with { State = MediaAssetReadinessState.Unverified,
                    Detail = "File deck admission invalidated optional evidence. Use current resident deck readiness." };
            }
        }
        if (!value.RepresentativeVerificationPassed) return value;
        try
        {
            var file = new FileInfo(value.SourceLocation);
            if (DateTimeOffset.UtcNow - value.ObservedAtUtc > TimeSpan.FromSeconds(10) ||
                !file.Exists || file.Length != value.LengthBytes || file.LastWriteTimeUtc.Ticks != value.LastWriteUtcTicks ||
                value.EnvironmentIdentity != PreflightEnvironmentIdentity())
                return value with { State = MediaAssetReadinessState.Unverified,
                    Detail = "Evidence expired or file/decoder environment changed. Prepare again." };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return value with { State = MediaAssetReadinessState.Unverified, Detail = exception.Message };
        }
        return value;
    }

    public MediaAssetPreflightObservation CancelPreflight(string jobId)
    {
        lock (_management)
        {
            lock (_gate)
            {
                if (_preflight is null || _preflight.JobId != jobId)
                    throw new InvalidOperationException("Preflight job is not current.");
            }
            _preflightStop?.Cancel();
            lock (_gate)
            {
                var failedOwner = _preflightQuarantine is not null || _preflightIdentityQuarantine is not null;
                _preflight = _preflight! with { Cancelled = true,
                    State = failedOwner ? MediaAssetReadinessState.Unavailable : MediaAssetReadinessState.Unverified,
                    Detail = failedOwner ? _preflight.Detail : "Cancelled; the worker retains its reservation until native cleanup completes." };
                return _preflight;
            }
        }
    }

    private async Task RunPreflightAsync(MediaAssetPreflightObservation initial, MediaAssetId assetId,
        MediaAssetPreflightOptions options, CancellationToken token, LocalMediaResourceLedger.LocalMediaResourceTicket ticket)
    {
        var value = initial;
        LocalMediaFileSource? source = null;
        FileStream? identity = null;
        var nativeOpenInProgress = false;
        try
        {
            // No hashing, metadata probing or native work occurs before shared capacity admission.
            if (!OperatingSystem.IsWindows() || Path.GetPathRoot(initial.SourceLocation)?.Length != 3)
                throw new IOException("Preflight requires a directly addressed local Windows file.");
            var file = new FileInfo(initial.SourceLocation);
            for (FileSystemInfo? item = file; item is not null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Preflight does not follow reparse/mounted paths.");
            identity = new FileStream(initial.SourceLocation, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (identity.Length < 1 || identity.Length > options.MaximumBytes)
                throw new IOException("File exceeds the selected bounded fingerprint budget.");
            value = value with { LengthBytes = identity.Length, LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks };
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            int read;
            while ((read = await identity.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                // At most 8 MiB/s requested source reads, one admitted worker, no recursive scan.
                await Task.Delay(8, token).ConfigureAwait(false);
            }
            if (Convert.ToHexString(hash.GetHashAndReset()) != initial.FingerprintSha256)
                throw new IOException("File content no longer matches the catalogue fingerprint; relink or reimport deliberately.");
            token.ThrowIfCancellationRequested();
            nativeOpenInProgress = true;
            var opened = PreflightOpen is { } factory
                ? factory(initial.SourceLocation, ticket.Identity.SourceId, assetId)
                : _provider.TryOpen(initial.SourceLocation, ticket.Identity.SourceId, assetId);
            nativeOpenInProgress = false;
            source = opened.Source;
            if (source is null) throw new IOException(opened.Failure?.Code + ": " + opened.Failure?.Message);
            token.ThrowIfCancellationRequested();
            PublishPreflight(value with { Running = true, State = MediaAssetReadinessState.DecoderAvailable,
                Detail = "Decoder negotiated; representative seek/decode verification is running." });
            if (options.RequireAudio && source.Probe.AudioCodec == MediaAudioCodec.None)
                throw new IOException("Required audio was not admitted by the decoder.");
            var total = Math.Max(1L, (long)(source.Probe.Duration.TotalSeconds *
                source.Probe.VideoFormat.FrameRate.Numerator / source.Probe.VideoFormat.FrameRate.Denominator));
            for (var point = 0; point < options.SeekPoints; point++)
            {
                token.ThrowIfCancellationRequested();
                var position = options.SeekPoints == 1 ? 0 : (total - 1) * point / options.SeekPoints;
                var seek = source.SeekToFrame(position);
                if (!seek.Succeeded) throw new IOException(seek.Failure?.Code + ": " + seek.Failure?.Message);
                value = value with { VerifiedSeeks = value.VerifiedSeeks + 1 };
                for (var frame = 0; frame < options.FramesPerPoint; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    var result = source.ReadNext((ulong)value.VerifiedFrames, token);
                    if (!result.Succeeded || result.Frame is null ||
                        options.RequireAudio && result.Frame.AudioPayload.IsEmpty)
                        throw new IOException(result.Failure?.Code + ": representative video/required audio could not be decoded.");
                    value = value with { VerifiedFrames = value.VerifiedFrames + 1 };
                }
            }
            token.ThrowIfCancellationRequested();
            file.Refresh();
            if (file.Length != value.LengthBytes || file.LastWriteTimeUtc.Ticks != value.LastWriteUtcTicks)
                throw new IOException("File or decoder environment changed during verification.");
            value = value with { EnvironmentIdentity = PreflightEnvironmentIdentity(capture: true), State = MediaAssetReadinessState.ConditionallyReady,
                Detail = "Representative seek/decode passed. Whole-file, sustained hardware and future deck capacity remain UNVERIFIED; no route was changed." };
        }
        catch (OperationCanceledException)
        {
            value = value with { Cancelled = true, State = MediaAssetReadinessState.Unverified, Detail = "Preflight cancelled; no playback state was changed." };
        }
        catch (Exception exception)
        {
            if (nativeOpenInProgress) ticket.Quarantine(source ?? (object)this);
            value = value with { State = MediaAssetReadinessState.Unavailable, Detail = exception.Message };
        }
        finally
        {
            try { source?.Dispose(); }
            catch (Exception exception)
            {
                ticket.Quarantine(source ?? (object)this);
                lock (_gate) _preflightQuarantine = source;
                value = value with { State = MediaAssetReadinessState.Unavailable, Detail = "Preflight cleanup failed; Runtime restart required: " + exception.Message };
            }
            try { identity?.Dispose(); }
            catch (Exception exception) { ticket.Quarantine(source ?? (object)this); lock (_gate) _preflightIdentityQuarantine = identity; value = value with { State = MediaAssetReadinessState.Unavailable, Detail = "Identity handle cleanup failed; Runtime restart required: " + exception.Message }; }
            if (token.IsCancellationRequested) value = value with { Cancelled = true,
                State = _preflightQuarantine is not null || _preflightIdentityQuarantine is not null
                    ? MediaAssetReadinessState.Unavailable : MediaAssetReadinessState.Unverified };
            lock (_gate)
            {
                if (_preflightQuarantine is null && _preflightIdentityQuarantine is null) _probeReservations--;
                _preflight = value with { Running = false, Detail = value.Detail.Length > 1024 ? value.Detail[..1024] : value.Detail, ObservedAtUtc = DateTimeOffset.UtcNow };
            }
        }
    }

    private void PublishPreflight(MediaAssetPreflightObservation value)
    {
        lock (_gate) _preflight = value;
    }

    private void StopPreflight()
    {
        _preflightStop?.Cancel();
        if (_preflightTask is { } task && !task.Wait(TimeSpan.FromSeconds(10)))
        {
            _preflightTicket?.Quarantine(this);
            throw new IOException("Preflight native owner did not terminate; reservation remains quarantined until supported Runtime restart.");
        }
        if (_preflightTask is { IsCompleted: true }) _preflightTicket?.OwnerCompleted();
        if (_preflightQuarantine is not null || _preflightIdentityQuarantine is not null) throw new IOException("Preflight retained a failed native owner; supported Runtime restart is required.");
        _preflightStop?.Dispose();
        _preflightStop = null;
    }
}
