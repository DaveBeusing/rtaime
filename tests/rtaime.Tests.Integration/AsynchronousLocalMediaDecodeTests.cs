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
    public void Queue_and_inflight_payloads_are_bounded_and_independent_of_decoder_scratch()
    {
        var decoder = new ControlledDecoder();
        using var worker = new LocalMediaDecodeWorker(Source(decoder));
        Until(() => worker.Snapshot.QueuedFrames == LocalMediaDecodeWorker.QueueCapacity);
        Assert.Equal(6, decoder.Reads);
        using var first = worker.TakeDue(0, out _, out _);
        Assert.NotNull(first);
        Assert.Equal((byte)0, first!.Frame.RgbaPixels.Span[0]);
        for (var index = 1; index <= 3; index++)
        {
            using var consumed = worker.TakeDue(index * 200_000L, out _, out _);
            Assert.NotNull(consumed);
        }
        Until(() => decoder.Reads == 10 && worker.Snapshot.QueuedFrames == 6);
        Assert.Equal((byte)0, first.Frame.RgbaPixels.Span[0]);
        Assert.Equal((byte)0, first.Frame.AudioPayload.Span[0]);
        Assert.Equal(6, worker.Snapshot.QueuedFrames);
        Assert.Equal(6, worker.Snapshot.PeakQueuedFrames);

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
        Assert.True(session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId)).Committed);
        Assert.True(session.DecodeSnapshot.Generation > beforeRebind);
        Assert.Equal((byte)0, session.ProcessNextBoundary().RgbaPixels.Span[0]);
        Assert.True(session.DecodeSnapshot.Generation >= 51);
        Assert.Equal(1, decoder.MaximumConcurrentOperations);
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

    private static PreparedExecutionContract Prepared(LocalMediaFileProvider provider, MediaSourceId source)
    {
        var capability = provider.Descriptor.Capabilities.Single(c => c.Kind == LocalMediaCapabilityKinds.MediaRoute);
        var resource = provider.Descriptor.Resources.First(r => r.Kind == LocalMediaCapabilityKinds.MediaRoute);
        return new(RuntimeContractVersion.Current, PreparedExecutionId.New(),
            new AuthoritySnapshotReference(Identity.New(), new Revision(1)), new Generation(1),
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
        public bool Disposed { get; private set; }
        public long[]? Timestamps { get; init; }
        public ManualResetEventSlim ReadBlocked { get; } = new();
        public ManualResetEventSlim ReleaseRead { get; } = new();
        public LocalMediaProbe Probe { get; } = new(MediaContractVersion.Current, MediaAssetId.New(),
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
                    catch (OperationCanceledException) { CancelledReads++; throw; }
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
            try { Disposed = true; ReadBlocked.Dispose(); ReleaseRead.Dispose(); }
            finally { Interlocked.Decrement(ref _operations); }
        }

        private void Enter()
        {
            if (Disposed) throw new ObjectDisposedException(nameof(ControlledDecoder));
            MaximumConcurrentOperations = Math.Max(MaximumConcurrentOperations, Interlocked.Increment(ref _operations));
        }
    }
}
