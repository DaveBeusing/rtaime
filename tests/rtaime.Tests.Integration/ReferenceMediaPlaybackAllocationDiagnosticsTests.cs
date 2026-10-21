// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Media;
using rtaime.RuntimeHost;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Integration;

public sealed class ReferenceMediaPlaybackAllocationDiagnosticsTests
{

    [Fact]
    public void Native_decoder_copy_stages_report_source_bound_allocations_and_runtime_input_copy()
    {
        if (Environment.GetEnvironmentVariable("RTAIME_LOCAL_ALLOCATION_DIAGNOSTICS") != "1") return;
        Assert.True(OperatingSystem.IsWindows());

        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var opened = new LocalMediaFileProvider(VideoFormat.Hd1080p50Rgba8)
            .TryOpen(asset.Path, MediaSourceId.New());
        Assert.True(opened.Succeeded, opened.Failure?.Message);
        using var source = opened.Source!;
        var format = source.Probe.VideoFormat;
        var admitted = new RgbaFrameBuffer(format,
            new byte[checked((int)((ulong)format.Width * format.Height * 4UL))]);

        const int warmup = 8, measured = 32;
        for (var index = 0; index < warmup; index++)
        {
            var frame = source.ReadNext((ulong)index);
            Assert.True(frame.Succeeded, frame.Failure?.Message);
            admitted.CopyPixelsFrom(frame.Frame!.RgbaPixels.Span);
        }

        var threadId = Environment.CurrentManagedThreadId;
        var callerBytes = GC.GetAllocatedBytesForCurrentThread();
        var processBytes = GC.GetTotalAllocatedBytes(precise: false);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var gcPause = GC.GetTotalPauseDuration();
        LocalMediaCopyMeasurements runtimeInput = default;
        LocalMediaDecodedFrame? final = null;
        for (var index = 0; index < measured; index++)
        {
            var read = source.ReadNext((ulong)(index + warmup));
            Assert.True(read.Succeeded, read.Failure?.Message);
            final = read.Frame!;
            var start = Stopwatch.GetTimestamp();
            admitted.CopyPixelsFrom(final.RgbaPixels.Span);
            runtimeInput = runtimeInput.Observe(
                final.RgbaPixels.Length, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var measuredCallerBytes = GC.GetAllocatedBytesForCurrentThread() - callerBytes;
        var measuredProcessBytes = GC.GetTotalAllocatedBytes(precise: false) - processBytes;
        var collections = new
        {
            gen0 = GC.CollectionCount(0) - gen0,
            gen1 = GC.CollectionCount(1) - gen1,
            gen2 = GC.CollectionCount(2) - gen2,
            pauseMilliseconds = (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds
        };
        Assert.Equal(threadId, Environment.CurrentManagedThreadId);
        var snapshot = source.Measurements;
        Assert.True(snapshot.CopyDiagnosticsEnabled);
        Assert.True(snapshot.DecoderCopies.Rgb32ToScratch.Operations >= warmup + measured);
        Assert.True(snapshot.DecoderCopies.RgbaSwizzle.Operations >= warmup + measured);
        Assert.True(snapshot.DecoderCopies.NativePcmToScratch.Operations > 0);
        Assert.True(snapshot.DecoderCopies.Pcm16ToFloat32.Operations > 0);
        Assert.Equal((ulong)measured, runtimeInput.Operations);
        Assert.Equal(SHA256.HashData(final!.RgbaPixels.Span), SHA256.HashData(admitted.Pixels.Span));

        var gcInfo = GC.GetGCMemoryInfo();
        Console.WriteLine("NATIVE DECODE COPY SOFTWARE PROFILE: " + JsonSerializer.Serialize(new
        {
            scope = "ISOLATED_1080P50_NATIVE_READER_AND_REPRODUCED_RUNTIME_SOURCE_COPY",
            sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT") ?? "UNAVAILABLE",
            format = format.ToString(), warmup, measured, callerThreadBytesPerFrame = measuredCallerBytes / (double)measured,
            approximateProcessBytesPerFrame = measuredProcessBytes / (double)measured,
            stageWindow = "WHOLE_RUN_FIXED_HISTOGRAM",
            rgb32ToScratch = snapshot.DecoderCopies.Rgb32ToScratch,
            rgbaSwizzle = snapshot.DecoderCopies.RgbaSwizzle,
            nativePcmPacket = snapshot.DecoderCopies.NativePcmToScratch,
            pcm16ToFloat32 = snapshot.DecoderCopies.Pcm16ToFloat32,
            reproducedRuntimeSourceCopy = runtimeInput,
            garbageCollection = collections,
            heapBytesAfterLastGc = gcInfo.HeapSizeBytes,
            lohBytesAfterLastGc = gcInfo.GenerationInfo.Length > 3 ? gcInfo.GenerationInfo[3].SizeAfterBytes : 0,
            pohBytesAfterLastGc = gcInfo.GenerationInfo.Length > 4 ? gcInfo.GenerationInfo[4].SizeAfterBytes : 0,
            physicalDecoderAndGpu = "UNVERIFIED",
            actualRuntimeHostP99 = "UNVERIFIED",
            slotAndHostOwnerQueue = "NOT_IN_ISOLATED_READER"
        }));
    }

    [Fact]
    public void Isolated_native_reader_probe_separates_callback_resources_from_Runtime_and_Replay()
    {
        if (Environment.GetEnvironmentVariable("RTAIME_LOCAL_ALLOCATION_DIAGNOSTICS") != "1") return;
        Assert.True(OperatingSystem.IsWindows());
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var opened = new LocalMediaFileProvider(VideoFormat.Hd1080p50Rgba8).TryOpen(asset.Path, MediaSourceId.New());
        Assert.True(opened.Succeeded, opened.Failure?.ToString());
        using var source = opened.Source!;
        for (var index = 0; index <= 3000; index++)
        {
            if (index % 300 == 0)
                Console.WriteLine("ISOLATED NATIVE READER HANDLE DIAGNOSTIC: " + JsonSerializer.Serialize(new {
                    reads = index, resources = LocalPlaybackHandleDiagnostics.Capture()
                }));
            if (index == 3000) break;
            var frame = source.ReadNext((ulong)index);
            if (frame.Status == LocalMediaFrameReadStatus.Ended)
            {
                Assert.True(source.SeekToFrame(0).Succeeded);
                frame = source.ReadNext((ulong)index);
            }
            Assert.True(frame.Succeeded, frame.Failure?.ToString());
        }
        Console.WriteLine("DIAGNOSTIC ONLY: isolated native reads do not qualify Program playback.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Short_allocation_probe_collects_sampled_types_without_qualifying_long_playback(bool replayEnabled)
    {
        if (Environment.GetEnvironmentVariable("RTAIME_LOCAL_ALLOCATION_DIAGNOSTICS") != "1") return;
        Assert.True(OperatingSystem.IsWindows());
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var root = Path.Combine(Path.GetTempPath(), "rtaime-allocation-" + Guid.NewGuid().ToString("N"));
        var runtimeEndpoint = "rtaime.allocation.runtime." + Guid.NewGuid().ToString("N");
        var controlEndpoint = "rtaime.allocation.control." + Guid.NewGuid().ToString("N");
        using var runtimeStop = new CancellationTokenSource();
        using var controlStop = new CancellationTokenSource();
        using var allocations = new LocalPlaybackAllocationListener();
        var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint },
            runtimeFactory: replayEnabled ? null : ((options, writer) => new RuntimeProgramExecutionService(
                options.SourceAId, options.SourceBId, options.Format, writer, networkOutputs: options.NetworkOutputs)));
        var control = new ControlHostProcess(ControlHostProcessOptions.Default with {
            ListenEndpoint = controlEndpoint, RuntimeEndpoint = runtimeEndpoint, DurabilityRoot = root });
        var runtimeRun = runtime.RunAsync(runtimeStop.Token);
        var controlRun = control.RunAsync(controlStop.Token);
        try
        {
            var deadline = Environment.TickCount64 + 30_000;
            while (control.Lifecycle.State != ControlHostProcessState.Ready)
            {
                Assert.True(Environment.TickCount64 < deadline);
                await Task.Delay(25);
            }
            var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15)));
            var state = await client.SynchronizeAsync();
            await using var deck = new MediaDeckController(client);
            var source = new MediaSourceId(Identity.Parse(state.Sources[0].Id));
            Assert.True((await deck.OpenAsync(asset.Path, source)).IsLoaded);
            await deck.ConfigurePlaybackAsync(false, MediaDeckEndBehavior.Loop);
            if (deck.Snapshot.Transport?.State != MediaTransportState.Playing) await deck.PlayAsync();
            for (var sample = 0; sample <= 6; sample++)
            {
                Console.WriteLine("LOCAL HANDLE TYPE DIAGNOSTIC: " + JsonSerializer.Serialize(new {
                    replayEnabled, seconds = sample * 15, resources = LocalPlaybackHandleDiagnostics.Capture()
                }));
                if (sample < 6) await Task.Delay(TimeSpan.FromSeconds(15));
            }
            Assert.False(runtimeRun.IsCompleted);
            Assert.NotEqual(MediaDeckState.Error, runtime.MediaDeck!.Snapshot.State);
            Console.WriteLine("SAMPLED LOH TYPE AND SIZE DIAGNOSTIC: " + JsonSerializer.Serialize(allocations.Snapshot()));
            Console.WriteLine("DIAGNOSTIC ONLY: short loop playback is not ten-minute or physical SSD qualification.");
        }
        finally
        {
            controlStop.Cancel();
            runtimeStop.Cancel();
            Assert.Equal(ControlHostExitCode.Success, await controlRun.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(runtime.RuntimeDisposed);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
