// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class MediaAssetPreflightIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_reference_verification_releases_decoder_and_rejects_mutated_identity_and_old_epoch(bool fractionalOutput)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        using var decks = new LocalMediaDeckRuntimeService(fractionalOutput ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8);
        var id = MediaAssetId.New();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        var job = decks.BeginPreflight(asset.Path, id, hash, new(RequireAudio: true));
        Assert.True(job.Running);
        var result = await Completed(decks, job.JobId);
        result.Validate();
        Assert.Equal(fractionalOutput ? "1080p59.94" : "1080p50", result.TargetFormat);
        Assert.True(result.RepresentativeVerificationPassed, result.Detail);
        Assert.Equal(6, result.VerifiedFrames);
        Assert.Equal(3, result.VerifiedSeeks);
        Assert.Equal("UNVERIFIED", result.PhysicalQualification);
        Assert.Equal(MediaDeckState.Unloaded, decks.Snapshot.State);
        Until(() => decks.Resources.Reserved == 0);
        File.AppendAllText(asset.Path, "changed");
        Assert.Equal(MediaAssetReadinessState.Unverified, decks.GetPreflight(job.JobId).State);
        var changed = await Completed(decks, decks.BeginPreflight(asset.Path, id, hash, new()).JobId);
        Assert.Equal(MediaAssetReadinessState.Unavailable, changed.State);
        Assert.Contains("fingerprint", changed.Detail);
        using var restarted = new LocalMediaDeckRuntimeService();
        Assert.Throws<InvalidOperationException>(() => restarted.GetPreflight(job.JobId));
    }

    [Fact]
    public async Task Cancel_during_open_retains_the_only_probe_reservation_and_late_open_never_publishes_ready()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoder = new ControlledDecoder();
        using var decks = new LocalMediaDeckRuntimeService {
            PreflightEnvironment = () => "fixed-test-context",
            PreflightOpen = (_, _, _) => { entered.Set(); release.Wait(); return LocalMediaOpenResult.Ready(Source(decoder)); }
        };
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        var job = decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new());
        Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
        try
        {
            Assert.Equal(1, decks.Resources.Reserved);
            var source = MediaSourceId.New();
            var admission = decks.Open(new(MediaContractVersion.Current, source, asset.Path), Prepared(new LocalMediaFileProvider(), source));
            Assert.Equal("runtime.media_deck.preflight_cancelling", admission.Failure?.Code);
            var cancelled = decks.GetPreflight(job.JobId);
            Assert.True(cancelled.Running);
            Assert.True(cancelled.Cancelled);
            Assert.False(decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new()).Running);
            Assert.False(decks.Probe(asset.Path, MediaAssetId.New()).Succeeded);
            Assert.Equal(job.JobId, decks.GetPreflight(job.JobId).JobId);
            Assert.Equal(0, decoder.Reads);
            Assert.False(decoder.Disposed);
        }
        finally { release.Set(); }
        var result = await Completed(decks, job.JobId);
        Assert.True(result.Cancelled);
        Assert.False(result.RepresentativeVerificationPassed);
        Assert.True(decoder.Disposed);
        Assert.Equal(1, decoder.DisposeCalls);
        Until(() => decks.Resources.Reserved == 0);
    }

    [Fact]
    public async Task Decode_cancellation_mutation_lock_and_environment_invalidation_keep_bounded_ownership()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var decoder = new ControlledDecoder { BlockCall = 1 };
        var context = "context-1";
        using var decks = new LocalMediaDeckRuntimeService {
            PreflightEnvironment = () => context,
            PreflightOpen = (_, _, _) => LocalMediaOpenResult.Ready(Source(decoder))
        };
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        var job = decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new());
        Assert.True(await Task.Run(() => decoder.ReadBlocked.Wait(TimeSpan.FromSeconds(5))));
        Assert.Throws<IOException>(() => File.Open(asset.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        decks.CancelPreflight(job.JobId);
        var cancelled = await Completed(decks, job.JobId);
        Assert.True(cancelled.Cancelled);
        Assert.Equal(1, decoder.CancelledReads);
        Assert.Equal(1, decoder.MaximumConcurrentOperations);
        Until(() => decks.Resources.Reserved == 0);
        for (var index = 0; index < 8; index++)
        {
            var current = new ControlledDecoder();
            decks.PreflightOpen = (_, _, _) => LocalMediaOpenResult.Ready(Source(current));
            var done = await Completed(decks, decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new(SeekPoints: 1, FramesPerPoint: 1)).JobId);
            done.Validate();
            Assert.True(done.RepresentativeVerificationPassed, done.Detail);
            Assert.Equal(1, current.Reads);
            Assert.Equal(1, current.DisposeCalls);
            Until(() => decks.Resources.Reserved == 0);
            context += "-changed";
            Assert.Equal(MediaAssetReadinessState.Unverified, decks.GetPreflight(done.JobId).State);
        }
    }

    [Fact]
    public async Task Cleanup_failure_retains_reservation_and_blocks_all_replacement_without_repeated_dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var decoder = new ControlledDecoder { FailDispose = true };
        var decks = new LocalMediaDeckRuntimeService {
            PreflightEnvironment = () => "fixed",
            PreflightOpen = (_, _, _) => LocalMediaOpenResult.Ready(Source(decoder))
        };
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        var done = await Completed(decks, decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new(SeekPoints: 1, FramesPerPoint: 1)).JobId);
        Assert.Equal(MediaAssetReadinessState.Unavailable, done.State);
        Assert.Equal(1, decks.Resources.Reserved);
        Assert.False(decks.Probe(asset.Path, MediaAssetId.New()).Succeeded);
        var retry = decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new());
        Assert.False(retry.Running);
        Assert.Equal(MediaAssetReadinessState.Unavailable, retry.State);
        Assert.Contains("Restart RuntimeHost", retry.Detail);
        var cancelled = decks.CancelPreflight(done.JobId);
        Assert.Equal(MediaAssetReadinessState.Unavailable, cancelled.State);
        Assert.Contains("cleanup failed", cancelled.Detail);
        Assert.Throws<IOException>(decks.Dispose);
        Assert.Throws<IOException>(decks.Dispose);
        Assert.Equal(1, decoder.DisposeCalls);
    }

    [Fact]
    public void Loaded_decks_deny_preflight_before_any_optional_open_or_seek()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = new LocalMediaFileProvider();
        var decoder = new ControlledDecoder();
        using var session = new LocalMediaRuntimeSession(provider, Source(decoder));
        Assert.True(session.ApplyExecution(Prepared(provider, decoder.Probe.SourceId)).Committed);
        using var decks = new LocalMediaDeckRuntimeService(provider, session);
        var calls = 0;
        decks.PreflightOpen = (_, _, _) => { calls++; throw new InvalidOperationException(); };
        var original = decks.Snapshot;
        for (var index = 0; index < 1000; index++)
            Assert.False(decks.BeginPreflight(@"C:\missing\unreadable.mp4", MediaAssetId.New(), new string('A', 64), new()).Running);
        Assert.Equal(0, calls);
        Assert.Equal(0, decks.Resources.Evicted);
        Assert.Equal(original, decks.Snapshot);
        Assert.Equal(1, decks.Resources.Reserved);
    }

    [Fact]
    public async Task Required_audio_oversize_and_native_failure_never_become_conditional_success()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService();
        var calls = 0;
        decks.PreflightOpen = (_, _, _) => { calls++; return LocalMediaOpenResult.Rejected("media.decoder.unavailable", "Installed decoder absent."); };
        var budget = await Completed(decks, decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new(MaximumBytes: 1)).JobId);
        Assert.Equal(MediaAssetReadinessState.Unavailable, budget.State);
        Assert.Equal(0, calls);
        Until(() => decks.Resources.Reserved == 0);
        var missing = await Completed(decks, decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new()).JobId);
        Assert.Contains("media.decoder.unavailable", missing.Detail);
        Until(() => decks.Resources.Reserved == 0);
        var decoder = new ControlledDecoder();
        var noAudio = decoder.Probe with { };
        // Preserve the production constructor's audio absence rather than relying on a display label.
        noAudio = new LocalMediaProbe(MediaContractVersion.Current, noAudio.AssetId, noAudio.SourceId, noAudio.FileName,
            noAudio.Container, noAudio.VideoCodec, MediaAudioCodec.None, noAudio.VideoFormat, noAudio.AudioFormat, noAudio.Duration);
        decks.PreflightOpen = (_, _, _) => LocalMediaOpenResult.Ready(new LocalMediaFileSource(asset.Path, noAudio, decoder));
        var audio = await Completed(decks, decks.BeginPreflight(asset.Path, MediaAssetId.New(), hash, new(RequireAudio: true)).JobId);
        Assert.Equal(MediaAssetReadinessState.Unavailable, audio.State);
        Assert.Contains("Required audio", audio.Detail);
        Assert.Equal(0, decoder.Reads);
        Assert.Equal(1, decoder.DisposeCalls);
    }

    private static async Task<MediaAssetPreflightObservation> Completed(LocalMediaDeckRuntimeService decks, string job)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var value = decks.GetPreflight(job);
            if (!value.Running) return value;
            await Task.Delay(10, deadline.Token);
        }
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
        public bool IgnoreCancellation { get; init; }
        public int DisposeCalls { get; private set; }
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
                    try { ReleaseRead.Wait(IgnoreCancellation ? CancellationToken.None : cancellationToken); }
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
                DisposeCalls++;
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
