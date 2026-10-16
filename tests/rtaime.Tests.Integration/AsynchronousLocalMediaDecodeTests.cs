// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class AsynchronousLocalMediaDecodeTests
{
    [Fact]
    public void Native_callback_mailbox_reuses_wait_resources_across_one_thousand_blocked_deliveries()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var callback = new LocalMediaSourceReaderCallback();
        using var request = new AutoResetEvent(false);
        using var process = Process.GetCurrentProcess();
        var stop = 0;
        var producer = new Thread(() =>
        {
            if (!OperatingSystem.IsWindows()) return;
            long timestamp = 0;
            while (true)
            {
                request.WaitOne();
                if (Volatile.Read(ref stop) != 0) return;
                Thread.Sleep(1); // Ensure the owning reader actually blocks on its reusable event.
                callback.OnReadSample(0, 0, 0, timestamp++, IntPtr.Zero);
            }
        }) { IsBackground = true };
        producer.Start();
        var baseline = 0;
        var maximum = 0;
        try
        {
            for (var index = 0; index < 1056; index++)
            {
                callback.BeginRead();
                try
                {
                    request.Set();
                    Assert.Equal(index, callback.WaitForRead(CancellationToken.None).Timestamp);
                }
                finally { callback.FinishRead(); }
                if (index >= 31 && index % 32 == 31)
                {
                    process.Refresh();
                    var handles = process.HandleCount;
                    if (index == 31) baseline = handles;
                    maximum = Math.Max(maximum, handles);
                }
            }
            Assert.InRange(maximum - baseline, 0, 16);
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            request.Set();
            Assert.True(producer.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Native_callback_mailbox_cancellation_wakes_the_owner_and_discards_abandoned_delivery()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var callback = new LocalMediaSourceReaderCallback();
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        callback.BeginRead();
        var pending = Task.Run(() => {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            entered.Set();
            return callback.WaitForRead(cancellation.Token);
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        await Task.Delay(25);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        callback.FinishRead();
        callback.OnReadSample(0, 0, 0, 99, IntPtr.Zero);
        callback.BeginRead();
        callback.OnReadSample(0, 0, 0, 100, IntPtr.Zero);
        Assert.Equal(100, callback.WaitForRead(CancellationToken.None).Timestamp);
        callback.FinishRead();
        callback.Dispose();
        Assert.Equal(0, callback.OnReadSample(0, 0, 0, 101, IntPtr.Zero));
        Assert.Equal(0, callback.OnFlush(0));
        Assert.Throws<ObjectDisposedException>(() => {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            callback.BeginRead();
        });
    }

    [Fact]
    public void Failed_fixed_slot_preparation_releases_the_untransferred_decoder_without_starting_reads()
    {
        var decoder = new ControlledDecoder
        {
            Probe = new LocalMediaProbe(MediaContractVersion.Current, MediaAssetId.New(), MediaSourceId.New(),
                "invalid-extents.mp4", MediaContainerFormat.Mp4, MediaVideoCodec.H264, MediaAudioCodec.Pcm,
                new VideoFormat(uint.MaxValue, uint.MaxValue, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive),
                AudioFormat.Stereo48kFloat32, TimeSpan.FromSeconds(2))
        };
        Assert.Throws<OverflowException>(() => new LocalMediaRuntimeSession(new LocalMediaFileProvider(), Source(decoder)));
        Assert.True(decoder.Disposed);
        Assert.Equal(0, decoder.Reads);
        Assert.Equal(1, decoder.MaximumConcurrentOperations);
    }

    [Fact]
    public void Queue_and_inflight_payloads_are_bounded_and_independent_of_decoder_scratch()
    {
        var decoder = new ControlledDecoder();
        using var worker = new LocalMediaDecodeWorker(Source(decoder));
        Until(() => worker.Snapshot.QueuedFrames == LocalMediaDecodeWorker.QueueCapacity);
        Assert.Equal(LocalMediaDecodeWorker.QueueCapacity, decoder.Reads);
        using var first = worker.TakeDue(0, out _, out _);
        Assert.NotNull(first);
        Assert.Equal((byte)0, first!.Frame.RgbaPixels.Span[0]);
        Until(() => decoder.Reads == LocalMediaDecodeWorker.QueueCapacity + 1 &&
            worker.Snapshot.QueuedFrames == LocalMediaDecodeWorker.QueueCapacity);
        Assert.Equal((byte)0, first.Frame.RgbaPixels.Span[0]);
        Assert.Equal((byte)0, first.Frame.AudioPayload.Span[0]);
        Assert.Equal(LocalMediaDecodeWorker.QueueCapacity, worker.Snapshot.QueuedFrames);
        Assert.Equal(LocalMediaDecodeWorker.QueueCapacity, worker.Snapshot.PeakQueuedFrames);

        Assert.Null(worker.Reposition(12));
        Assert.True(worker.WaitForPreroll(TimeSpan.FromSeconds(3)));
        using var afterSeek = worker.TakeDue(12 * 200_000L, out _, out _);
        Assert.NotNull(afterSeek);
        Assert.Equal((byte)12, afterSeek!.Frame.RgbaPixels.Span[0]);
        Assert.Equal(1, afterSeek.Generation);
        Assert.Equal((byte)0, first.Frame.RgbaPixels.Span[0]);
        Assert.Equal((byte)0, first.Frame.AudioPayload.Span[0]);
        Assert.Equal(1, decoder.MaximumConcurrentOperations);
    }

    [Fact]
    public void Program_admission_and_seek_do_not_wait_for_blocked_decoder_IO()
    {
        var decoder = new ControlledDecoder { BlockCall = 4 };
        var provider = new LocalMediaFileProvider();
        using var session = new LocalMediaRuntimeSession(provider, Source(decoder));
        Assert.True(session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId)).Committed);
        Assert.True(decoder.ReadBlocked.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Play)).Succeeded);
        for (var index = 0; index < 3; index++) Assert.True(session.ProcessNextBoundary().Succeeded);
        var started = Stopwatch.StartNew();
        for (var index = 0; index < 1_000; index++)
            Assert.Equal(LocalMediaRuntimeBoundaryStatus.Buffering, session.ProcessNextBoundary().Status);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"Admission waited on decode: {started.Elapsed}");
        Assert.NotEqual(Environment.CurrentManagedThreadId, decoder.ReadThreadId);
        Assert.Equal(4, decoder.Reads);

        Assert.True(session.ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current,
            decoder.Probe.AssetId, MediaTransportCommandKind.Seek, 12)).Succeeded);
        Assert.True(session.WaitForPreroll(TimeSpan.FromSeconds(3)));
        var fresh = session.ProcessNextBoundary();
        Assert.True(fresh.Succeeded, fresh.Failure?.Message);
        Assert.Equal(12, fresh.Transport.Position.CurrentFrame);
        Assert.Equal((byte)12, fresh.RgbaPixels.Span[0]);
        Assert.Equal(1, decoder.CancelledReads);
        Assert.True(session.DecodeSnapshot.Starvations > 0);
    }

    [Fact]
    public void End_of_stream_after_a_starved_tail_is_observable_without_an_impossible_new_preroll()
    {
        var decoder = new ControlledDecoder { BlockCall = 4, Timestamps = [0, 200_000, 400_000] };
        using var worker = new LocalMediaDecodeWorker(Source(decoder));
        Assert.True(worker.WaitForPreroll(TimeSpan.FromSeconds(3)));
        Assert.True(decoder.ReadBlocked.Wait(TimeSpan.FromSeconds(3)));
        for (var index = 0; index < 3; index++)
        {
            using var frame = worker.TakeDue(index * 200_000L, out _, out _);
            Assert.NotNull(frame);
        }
        Assert.Null(worker.TakeDue(600_000, out var prematureEnd, out _));
        Assert.False(prematureEnd);
        Assert.Equal(LocalMediaDecodeState.Starved, worker.Snapshot.State);
        decoder.ReleaseRead.Set();
        Until(() => worker.Measurements.Decode.Calls == 4);
        Assert.Null(worker.TakeDue(600_000, out var ended, out var failure));
        Assert.True(ended);
        Assert.Null(failure);
        Assert.Equal(LocalMediaDecodeState.Ended, worker.Snapshot.State);
    }

    [Fact]
    public void Close_cancels_a_pending_read_and_joins_before_disposing_the_decoder()
    {
        var decoder = new ControlledDecoder { BlockCall = 4 };
        var worker = new LocalMediaDecodeWorker(Source(decoder));
        Assert.True(worker.WaitForPreroll(TimeSpan.FromSeconds(3)));
        Assert.True(decoder.ReadBlocked.Wait(TimeSpan.FromSeconds(3)));
        worker.Dispose();
        Assert.True(decoder.Disposed);
        Assert.Equal(1, decoder.CancelledReads);
        Assert.Equal(1, decoder.MaximumConcurrentOperations);
        Assert.Equal(LocalMediaDecodeState.Stopped, worker.Snapshot.State);
        Assert.Equal(0, worker.Snapshot.QueuedFrames);
    }

    [Fact]
    public void Decoder_release_failure_is_reported_by_lifecycle_cleanup_instead_of_a_successful_join()
    {
        var decoder = new ControlledDecoder { FailDispose = true };
        var worker = new LocalMediaDecodeWorker(Source(decoder));
        Assert.True(worker.WaitForPreroll(TimeSpan.FromSeconds(3)));
        Assert.Throws<IOException>(() => worker.Dispose());
        Assert.True(decoder.Disposed);
        Assert.Equal("runtime.media_decode.dispose_failed", worker.Snapshot.Failure?.Code);
        Assert.Equal(LocalMediaDecodeState.Failed, worker.Snapshot.State);
        Assert.Equal(0, worker.Snapshot.QueuedFrames);
    }

    [Fact]
    public void Unacknowledged_native_cancellation_is_a_cleanup_failure_and_never_reuses_the_reader()
    {
        var decoder = new ControlledDecoder { BlockCall = 4, FailCancellation = true };
        var worker = new LocalMediaDecodeWorker(Source(decoder));
        Assert.True(worker.WaitForPreroll(TimeSpan.FromSeconds(3)));
        Assert.True(decoder.ReadBlocked.Wait(TimeSpan.FromSeconds(3)));
        Assert.Throws<IOException>(() => worker.Dispose());
        Assert.True(decoder.Disposed);
        Assert.Equal(4, decoder.Reads);
        Assert.Equal("runtime.media_decode.cancellation_failed", worker.Snapshot.Failure?.Code);
        Assert.Equal(LocalMediaDecodeState.Failed, worker.Snapshot.State);
    }

    [Fact]
    public void Asynchronous_decoder_failure_becomes_local_media_error_without_escaping_admission()
    {
        var decoder = new ControlledDecoder { BlockCall = 4 };
        var provider = new LocalMediaFileProvider();
        using var session = new LocalMediaRuntimeSession(provider, Source(decoder));
        Assert.True(session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId)).Committed);
        Assert.True(decoder.ReadBlocked.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Play)).Succeeded);
        decoder.FailReads = true;
        decoder.ReleaseRead.Set();
        Until(() => session.DecodeSnapshot.State == LocalMediaDecodeState.Failed);
        var result = session.ProcessNextBoundary();
        Assert.Equal(LocalMediaRuntimeBoundaryStatus.Failed, result.Status);
        Assert.Equal(MediaTransportState.Error, result.Transport.State);
        Assert.Equal("media.file.decode_failed", result.Failure?.Code);
        Assert.Equal(0, session.DecodeSnapshot.QueuedFrames);
    }

    [Fact]
    public void Rapid_seek_stop_and_rebind_keep_only_the_current_generation_and_retain_inflight_leases()
    {
        var decoder = new ControlledDecoder();
        var provider = new LocalMediaFileProvider();
        using var session = new LocalMediaRuntimeSession(provider, Source(decoder));
        Assert.True(session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId)).Committed);
        session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Play));
        using var admitted = session.ProcessNextBoundary().Retain();
        Assert.Equal((byte)0, admitted.RgbaPixels.Span[0]);
        for (var target = 1; target <= 49; target++)
            Assert.True(session.ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current,
                decoder.Probe.AssetId, MediaTransportCommandKind.Seek, target)).Succeeded);
        Assert.True(session.WaitForPreroll(TimeSpan.FromSeconds(3)));
        var fresh = session.ProcessNextBoundary();
        Assert.True(fresh.Succeeded, fresh.Failure?.Message);
        Assert.Equal(49, fresh.Transport.Position.CurrentFrame);
        Assert.Equal((byte)49, fresh.RgbaPixels.Span[0]);
        Assert.Equal((byte)0, admitted.RgbaPixels.Span[0]);
        Assert.True(session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Stop)).Succeeded);
        Assert.True(session.WaitForPreroll(TimeSpan.FromSeconds(3)));
        session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Play));
        Assert.Equal((byte)0, session.ProcessNextBoundary().RgbaPixels.Span[0]);
        Assert.Equal((byte)0, admitted.RgbaPixels.Span[0]);
        var beforeRebind = session.DecodeSnapshot.Generation;
        var rejected = session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId));
        Assert.False(rejected.Committed);
        Assert.Equal("runtime.prepare.stale_authority_revision", rejected.Prepare.Failure?.Code);
        Assert.Equal(beforeRebind, session.DecodeSnapshot.Generation);
        var rebind = session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId, 2));
        Assert.True(rebind.Committed, rebind.Prepare.Failure?.Message ?? rebind.Commit?.Failure?.Message);
        Assert.True(session.DecodeSnapshot.Generation > beforeRebind);
        Assert.Equal((byte)0, session.ProcessNextBoundary().RgbaPixels.Span[0]);
        Assert.True(session.DecodeSnapshot.Generation >= 51);
        Assert.Equal(1, decoder.MaximumConcurrentOperations);
    }

    [Fact]
    public void Existing_Program_slots_select_current_PTS_after_a_missed_opportunity_without_resequencing_bursts()
    {
        var decoder = new ControlledDecoder();
        var provider = new LocalMediaFileProvider();
        using var session = new LocalMediaRuntimeSession(provider, Source(decoder));
        Assert.True(session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId)).Committed);
        Until(() => session.DecodeSnapshot.QueuedFrames == LocalMediaDecodeWorker.QueueCapacity);
        session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Play));
        Assert.Equal(0, session.ProcessNextBoundary(100).Transport.Position.CurrentFrame);
        var current = session.ProcessNextBoundary(103);
        Assert.True(current.Succeeded);
        Assert.Equal(3, current.Transport.Position.CurrentFrame);
        Assert.Equal(1UL, current.Video!.Timing.SequenceNumber);
        Assert.Equal(2UL, session.NextSequenceNumber);
        Assert.Equal(2UL, session.DecodeSnapshot.DiscardedFrames);
        Assert.Equal(2_880UL, current.Audio!.Timing.SamplePosition);
        session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Pause));
        session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Play));
        Assert.Equal(4, LocalMediaTestPlayback.Next(session).Transport.Position.CurrentFrame);
    }

    [Fact]
    public void Future_VFR_timestamp_is_held_until_due_without_replaying_audio()
    {
        var decoder = new ControlledDecoder { Timestamps = [0, 200_000, 800_000, 1_000_000, 1_200_000] };
        var provider = new LocalMediaFileProvider();
        using var session = new LocalMediaRuntimeSession(provider, Source(decoder));
        Assert.True(session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId)).Committed);
        session.ApplyTransport(Command(decoder, MediaTransportCommandKind.Play));
        Assert.Equal(0, session.ProcessNextBoundary().Video!.Timing.PresentationTimestamp);
        Assert.Equal(200_000, session.ProcessNextBoundary().Video!.Timing.PresentationTimestamp);
        for (var index = 0; index < 2; index++)
        {
            var early = session.ProcessNextBoundary();
            Assert.Equal(LocalMediaRuntimeBoundaryStatus.Repeated, early.Status);
            Assert.Equal(200_000, early.Video!.Timing.PresentationTimestamp);
            Assert.Null(early.Audio);
            Assert.True(early.AudioPayload.IsEmpty);
        }
        Assert.Equal(800_000, session.ProcessNextBoundary().Video!.Timing.PresentationTimestamp);
    }

    [Fact]
    public void Real_clip_replacement_and_close_keep_inflight_payloads_alive_without_old_source_admission()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        using var deck = new LocalMediaDeckRuntimeService();
        var sourceA = MediaSourceId.New();
        var first = deck.Open(new MediaDeckOpenRequest(MediaContractVersion.Current, sourceA, asset.Path),
            Prepared(new LocalMediaFileProvider(), sourceA));
        Assert.Equal(MediaDeckState.Ready, first.State);
        Assert.True(deck.ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current, first.Probe!.AssetId,
            MediaTransportCommandKind.Play)).Succeeded);
        deck.ProcessBoundary(out var captured);
        using var retained = captured;
        Assert.NotNull(retained);
        Assert.True(retained!.Succeeded);
        var original = retained.RgbaPixels.Span[..64].ToArray();
        var sourceB = MediaSourceId.New();
        var second = deck.Open(new MediaDeckOpenRequest(MediaContractVersion.Current, sourceB, asset.Path, MediaAssetId.New()),
            Prepared(new LocalMediaFileProvider(), sourceB));
        Assert.Equal(MediaDeckState.Ready, second.State);
        Assert.True(deck.ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current, second.Probe!.AssetId,
            MediaTransportCommandKind.Play)).Succeeded);
        for (var index = 0; index < 12; index++)
        {
            LocalMediaTestPlayback.Next(deck);
            Assert.Equal(sourceB, deck.LatestBoundary!.Video!.SourceId);
        }
        Assert.Equal(sourceA, retained.Video!.SourceId);
        Assert.Equal(original, retained.RgbaPixels.Span[..64].ToArray());
        Assert.Equal(MediaDeckState.Unloaded, deck.Close().State);
        Assert.Null(deck.LatestBoundary);
        Assert.Equal(original, retained.RgbaPixels.Span[..64].ToArray());
    }

    private static LocalMediaFileSource Source(ControlledDecoder decoder) => new("controlled.mp4", decoder.Probe, decoder);
    private static MediaTransportCommand Command(ControlledDecoder decoder, MediaTransportCommandKind kind) =>
        new(MediaContractVersion.Current, decoder.Probe.AssetId, kind);
    private static void Until(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(3)));

    private static PreparedExecutionContract Prepared(LocalMediaFileProvider provider, MediaSourceId source, ulong revision = 1)
    {
        var capability = provider.Descriptor.Capabilities.Single(c => c.Kind == LocalMediaCapabilityKinds.MediaRoute);
        var resource = provider.Descriptor.Resources.First(r => r.Kind == LocalMediaCapabilityKinds.MediaRoute);
        return new(RuntimeContractVersion.Current, PreparedExecutionId.New(),
            new AuthoritySnapshotReference(source.Value, new Revision(revision)), new Generation(revision),
            [new PreparedExecutionBinding(source.Value, capability.CapabilityId, resource, source, null)]);
    }

    private sealed class ControlledDecoder : ILocalMediaDecoder
    {
        private readonly byte[] _video = new byte[64];
        private readonly byte[] _audio = new byte[960 * 2 * sizeof(float)];
        private int _position;
        private int _reads;
        private int _operations;
        public int Reads => Volatile.Read(ref _reads);
        public int ReadThreadId { get; private set; }
        public int CancelledReads { get; private set; }
        public int MaximumConcurrentOperations { get; private set; }
        public int BlockCall { get; init; }
        public bool FailReads { get; set; }
        public bool FailCancellation { get; init; }
        public bool FailDispose { get; init; }
        public bool Disposed { get; private set; }
        public long[]? Timestamps { get; init; }
        public ManualResetEventSlim ReadBlocked { get; } = new();
        public ManualResetEventSlim ReleaseRead { get; } = new();
        public LocalMediaProbe Probe { get; init; } = new(MediaContractVersion.Current, MediaAssetId.New(),
            MediaSourceId.New(), "controlled.mp4", MediaContainerFormat.Mp4, MediaVideoCodec.H264,
            MediaAudioCodec.Pcm, new VideoFormat(4, 4, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive),
            AudioFormat.Stereo48kFloat32, TimeSpan.FromSeconds(2));

        public bool TryReadNext(ulong sequenceNumber, out LocalMediaDecodedFrame? frame) =>
            TryReadNext(sequenceNumber, CancellationToken.None, out frame);

        public bool TryReadNext(ulong sequenceNumber, CancellationToken cancellationToken, out LocalMediaDecodedFrame? frame)
        {
            Enter();
            try
            {
                ReadThreadId = Environment.CurrentManagedThreadId;
                var call = Interlocked.Increment(ref _reads);
                if (call == BlockCall)
                {
                    ReadBlocked.Set();
                    try { ReleaseRead.Wait(cancellationToken); }
                    catch (OperationCanceledException)
                    {
                        CancelledReads++;
                        if (FailCancellation) throw new LocalMediaReadCancellationException("Injected unacknowledged native flush.");
                        throw;
                    }
                }
                if (FailReads) throw new InvalidDataException("Injected local decoder failure.");
                if (_position >= (Timestamps?.Length ?? 100)) { frame = null; return false; }
                var timestamp = Timestamps?[_position] ?? _position * 200_000L;
                Array.Fill(_video, (byte)_position);
                Array.Fill(_audio, (byte)_position);
                var surface = new SurfaceDescriptor(SurfaceId.New(), Probe.VideoFormat, SurfaceStorageDomain.Host,
                    SurfaceOwnership.ProducerOwned, new SurfaceLifetimeDescriptor(new Generation(sequenceNumber), null),
                    new OpaqueSurfaceHandle("test", "owned"));
                var video = new FrameDescriptor(MediaContractVersion.Current, Probe.SourceId, surface,
                    new FrameTiming(sequenceNumber, timestamp, new Timebase(1, 10_000_000)));
                var audio = new AudioBufferDescriptor(MediaContractVersion.Current, new AudioStreamId(Identity.New()),
                    Probe.AudioFormat, Identity.New(),
                    new AudioBufferTiming((ulong)(timestamp * 48_000 / 10_000_000), 960, timestamp, new Timebase(1, 10_000_000)),
                    new OpaqueAudioHandle("test", "owned"));
                frame = new(video, _video, audio, _audio);
                _position++;
                return true;
            }
            finally { Interlocked.Decrement(ref _operations); }
        }

        public LocalMediaSeekResult SeekToFrame(long frame)
        {
            Enter();
            try { _position = checked((int)frame); return LocalMediaSeekResult.Positioned(frame); }
            finally { Interlocked.Decrement(ref _operations); }
        }

        public void Dispose()
        {
            Enter();
            try
            {
                Disposed = true;
                ReadBlocked.Dispose();
                ReleaseRead.Dispose();
                if (FailDispose) throw new IOException("Injected native decoder release failure.");
            }
            finally { Interlocked.Decrement(ref _operations); }
        }

        private void Enter()
        {
            if (Disposed) throw new ObjectDisposedException(nameof(ControlledDecoder));
            MaximumConcurrentOperations = Math.Max(MaximumConcurrentOperations, Interlocked.Increment(ref _operations));
        }
    }
}
