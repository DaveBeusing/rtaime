// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

// Optional observation-only sidecar. Never changes qualification outcomes, forces collection,
// or retains Replay snapshots, segment descriptors, writers, frames or payloads.
internal sealed class SsdPlaybackMemoryDiagnostic(
    string? sourceCommit, string? buildCommit, string profile, string programFormat, object[] media)
{
    private const int MaximumPoints = 5;
    private const int MaximumBytes = 64 * 1024;
    private readonly List<object> _points = new(MaximumPoints);
    private int _captureFailures;
    private int _omittedPoints;
    private string? _lastDiagnosticError;

    internal void TryCapture(string phase, TimeSpan elapsed, Func<ReplayBufferSnapshot?> readReplay,
        string replaySnapshotBasis, Func<ReplayCaptureWriterLifetimeStatistics?> readWriters,
        Func<ReplayCaptureQueueStatistics?> readQueue)
    {
        if (_points.Count >= MaximumPoints)
        {
            _omittedPoints++;
            return;
        }
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var gc = GC.GetGCMemoryInfo();
            object? replay = null;
            try
            {
                var snapshot = readReplay();
                if (snapshot is not null)
                {
                    var statistics = snapshot.Statistics;
                    replay = new
                    {
                        snapshotBasis = replaySnapshotBasis,
                        captureState = snapshot.CaptureState.ToString(),
                        failureCode = Bound(snapshot.Failure?.Code),
                        acceptedSamples = statistics.AcceptedSamples,
                        droppedSamples = statistics.DroppedSamples,
                        finalizedSegments = statistics.FinalizedSegments,
                        evictedSegments = statistics.EvictedSegments,
                        discontinuities = statistics.Discontinuities,
                        retainedEncodedFileBytes = statistics.RetainedBytes,
                        retainedSegmentCount = snapshot.Segments.Count
                    };
                }
            }
            catch (Exception exception) { RecordFailure("replay", exception); }
            ReplayCaptureWriterLifetimeStatistics? writers = null;
            ReplayCaptureQueueStatistics? queue = null;
            try { writers = readWriters(); }
            catch (Exception exception) { RecordFailure("writer-lifecycle", exception); }
            try { queue = readQueue(); }
            catch (Exception exception) { RecordFailure("queue", exception); }
            _points.Add(new
            {
                index = _points.Count,
                phase,
                elapsedSeconds = elapsed.TotalSeconds,
                capturedAtUtc = DateTimeOffset.UtcNow,
                processId = process.Id,
                privateBytes = process.PrivateMemorySize64,
                workingSetBytes = process.WorkingSet64,
                handles = process.HandleCount,
                processThreads = process.Threads.Count,
                managedPoolThreads = ThreadPool.ThreadCount,
                managedBytesEstimate = GC.GetTotalMemory(false),
                allocatedManagedBytes = GC.GetTotalAllocatedBytes(precise: false),
                gen0 = GC.CollectionCount(0),
                gen1 = GC.CollectionCount(1),
                gen2 = GC.CollectionCount(2),
                totalGcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds,
                lastGc = new
                {
                    index = gc.Index,
                    generation = gc.Generation,
                    heapBytes = gc.HeapSizeBytes,
                    fragmentedBytes = gc.FragmentedBytes,
                    committedManagedBytes = gc.TotalCommittedBytes,
                    pinnedObjects = gc.PinnedObjectsCount,
                    finalizationPending = gc.FinalizationPendingCount,
                    lohBytes = gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes : (long?)null,
                    lohFragmentedBytes = gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].FragmentationAfterBytes : (long?)null,
                    pohBytes = gc.GenerationInfo.Length > 4 ? gc.GenerationInfo[4].SizeAfterBytes : (long?)null,
                    pohFragmentedBytes = gc.GenerationInfo.Length > 4 ? gc.GenerationInfo[4].FragmentationAfterBytes : (long?)null
                },
                replay,
                writerLifetime = writers,
                captureQueue = queue
            });
        }
        catch (Exception exception) { RecordFailure("capture", exception); }
    }

    internal void TryWrite(string? outputDirectory, bool playbackFailed, bool cleanupSucceeded)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("Diagnostic output directory is unavailable.");
            var runtimeSource = CompiledSource(typeof(RuntimeHostProcess).Assembly);
            var harnessSource = CompiledSource(typeof(SsdPlaybackMemoryDiagnostic).Assembly);
            var origin = Environment.GetEnvironmentVariable("RTAIME_SSD_MEMORY_ORIGIN_COMMIT");
            var report = new
            {
                schemaVersion = "1.0",
                diagnosticMode = "OBSERVATION_ONLY_DEFAULT_REPLAY",
                status = _captureFailures == 0 && _omittedPoints == 0 ? "OBSERVED" : "PARTIAL",
                sourceCommit = Bound(sourceCommit),
                buildCommit = Bound(buildCommit),
                compiledRuntimeSourceCommit = runtimeSource,
                compiledDiagnosticSourceCommit = harnessSource,
                originatingCandidateCommit = Bound(origin),
                sourceIdentityMatches = IsCommit(sourceCommit) && sourceCommit == buildCommit &&
                    sourceCommit == runtimeSource && sourceCommit == harnessSource,
                originatingCandidateIdentityPresent = IsCommit(origin),
                profile,
                programFormat,
                media,
                runtimeVersion = Bound(RuntimeInformation.FrameworkDescription),
                operatingSystem = Bound(RuntimeInformation.OSDescription),
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processorCount = Environment.ProcessorCount,
                serverGc = GCSettings.IsServerGC,
                playbackFailed,
                cleanupSucceeded,
                maximumPoints = MaximumPoints,
                maximumBytes = MaximumBytes,
                captureFailures = _captureFailures,
                omittedPoints = _omittedPoints,
                lastDiagnosticError = _lastDiagnosticError,
                points = _points,
                limits = "Observation only. LastGc reports the last completed collection, not the exact contemporaneous heap; private bytes include managed commit, native allocations and process overhead, so their difference is not pure native memory. Allocated bytes are not retained bytes. Writer opens/finalizes/aborts and active sessions are observed at five checkpoints, never every frame. File bytes describe encoded retention, not raw Program frames. No forced collection or test-budget relaxation; this sidecar cannot independently qualify playback."
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report);
            if (bytes.Length > MaximumBytes)
                throw new InvalidDataException("Memory diagnostic exceeds its fixed report bound.");
            var path = Path.Combine(outputDirectory, "ssd-playback-memory-diagnostic.json");
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception exception)
        {
            // A diagnostic failure must never replace the workload exception or cleanup assertion.
            try { Console.WriteLine("SSD MEMORY DIAGNOSTIC UNAVAILABLE: " + exception.GetType().Name); }
            catch { }
        }
    }

    private void RecordFailure(string phase, Exception exception)
    {
        _captureFailures++;
        _lastDiagnosticError = Bound(phase + ":" + exception.GetType().Name);
    }

    private static string? CompiledSource(Assembly assembly) => Bound(assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(value => value.Key == "RtaimeSourceRevision")?.Value);

    private static bool IsCommit(string? value) => value is { Length: 40 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Bound(string? value) => value is { Length: > 256 } ? value[..256] : value;
}
