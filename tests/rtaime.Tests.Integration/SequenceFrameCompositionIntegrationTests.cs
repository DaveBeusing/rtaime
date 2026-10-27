// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class SequenceFrameCompositionIntegrationTests
{
    private static readonly VideoFormat Format = VideoFormat.Hd1080p50Rgba8;

    private static (PreparedExecutionContract Execution, SequenceRuntimePositionMap Map,
        SequenceMediaOwnerRequest[] Owners) Request(
        LocalMediaDeckRuntimeService decks, string file, string hash)
    {
        var provider = decks.ProviderDescriptor;
        var route = provider.Capabilities.Single(cap => cap.Kind == "media.route");
        var resources = provider.Resources.Where(resource => resource.Kind == "media.route").ToArray();
        var owners = new[]
        {
            new SequenceMediaOwnerRequest(MediaSourceId.New(), MediaAssetId.New(), file, hash, 0, 0),
            new SequenceMediaOwnerRequest(MediaSourceId.New(), MediaAssetId.New(), file, hash, 0, 0)
        };
        var execution = new PreparedExecutionContract(RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(Identity.New(), new Revision(1)),
            new Generation(1),
            owners.Select((owner, index) =>
                new PreparedExecutionBinding(owner.SourceId.Value, route.CapabilityId,
                    resources[index], owner.SourceId, null)).ToArray());
        var map = SequenceRuntimePositionMap.Prepare(Guid.NewGuid(), 7,
            FrameRate.Fps50, 20 * 4800, 100, 1);
        return (execution, map, owners);
    }

    private static AudioProductionEngine Mixer(MediaSourceId a, MediaSourceId b) =>
        new(new AudioProductionConfiguration(0,
            [new AudioProductionBusConfiguration(AudioBusId.Program, 1, false)],
            [
                new AudioProductionSourceConfiguration(a, 0.5, false, false, [AudioBusId.Program]),
                new AudioProductionSourceConfiguration(b, 0.5, false, false, [AudioBusId.Program])
            ]));

    [Theory]
    [InlineData(25, 1, 9600, 1920)]
    [InlineData(50, 1, 4800, 960)]
    [InlineData(60000, 1001, 4004, 800)]
    public void Source_pcm_window_is_sample_exact_across_first_last_and_looped_frames(
        long numerator, long denominator, long ticksPerFrame, int firstSamples)
    {
        var rate = new FrameRate(numerator, denominator);
        var (firstPosition, firstCount) = LocalMediaDeckRuntimeService.SourcePcmWindow(0, rate);
        Assert.Equal(0UL, firstPosition);
        Assert.Equal((uint)firstSamples, firstCount);
        ulong cumulative = 0;
        for (var frame = 0; frame < 12; frame++)
        {
            var sourceTick = checked(ticksPerFrame * frame);
            var (position, count) = LocalMediaDeckRuntimeService.SourcePcmWindow(sourceTick, rate);
            Assert.Equal(cumulative, position);
            cumulative = checked(cumulative + count);
            Assert.InRange(count, 1U, (uint)SequenceFrameComposition.MaximumAudioSamples);
        }
        Assert.Equal((ulong)(12 * ticksPerFrame / 5), cumulative);
        // Native loop preroll rewinds source sample time without rewinding
        // the authoritative Program clock or changing the PCM length.
        var (looped, loopCount) = LocalMediaDeckRuntimeService.SourcePcmWindow(0, rate);
        Assert.Equal(firstPosition, looped);
        Assert.Equal(firstCount, loopCount);
        Assert.Throws<InvalidOperationException>(() =>
            LocalMediaDeckRuntimeService.SourcePcmWindow(0, new FrameRate(10, 1)));
    }

    [Theory]
    [InlineData("runtime.sequence.ended", true)]
    [InlineData("runtime.sequence.stale_sample", true)]
    [InlineData("runtime.sequence.source_mismatch", true)]
    [InlineData("runtime.sequence.boundary_missing", true)]
    [InlineData("runtime.sequence.boundary_capacity", true)]
    [InlineData("runtime.sequence.boundary_fence", true)]
    [InlineData("runtime.sequence.buffering", false)]
    [InlineData("runtime.sequence.boundary_stale", false)]
    [InlineData(null, false)]
    public void Terminal_native_faults_cannot_be_revived_by_next_slot(
        string? failure, bool terminal)
    {
        Assert.Equal(terminal, LocalMediaDeckRuntimeService.IsTerminalSequenceFailure(failure));
    }

    [Fact]
    public void Authored_slice_end_masks_only_trailing_pcm_and_preserves_frame_sample_count()
    {
        const uint count = 960;
        var window = new float[count * 2];
        Array.Fill(window, 0.5f);
        var masked = LocalMediaDeckRuntimeService.SilenceBeyondSourceSlice(
            window, 0, count, 4002);
        Assert.Equal(159, masked);
        for (var sample = 0; sample < count; sample++)
        {
            Assert.Equal(sample < 801 ? 0.5f : 0f, window[sample * 2]);
            Assert.Equal(sample < 801 ? 0.5f : 0f, window[sample * 2 + 1]);
        }
        Assert.Equal(0, LocalMediaDeckRuntimeService.SilenceBeyondSourceSlice(
            window, 0, count, 4800));
        Assert.Throws<ArgumentException>(() =>
            LocalMediaDeckRuntimeService.SilenceBeyondSourceSlice(
                new float[10], 0, count, 4800));
    }

    [Fact]
    public void Real_two_native_mp4_frames_compose_through_gpu_and_pcm_uses_existing_audio_mixer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(Format, 2);
        var (execution, map, owners) = Request(decks, asset.Path, hash);
        Assert.True(decks.PrepareSequenceMedia(execution, map, owners).Prepared);
        var playing = map.Play(100);
        Assert.True(playing.TryMap(100, out var position));
        Assert.True(decks.RepositionSequenceMedia(map.SequenceId, 7, position).Prepared);

        var native = new SequenceMediaFrameLease?[2];
        Assert.True(decks.TryTakeSequenceFrameSet(map.SequenceId, 7, position,
            [
                new SequenceMediaFrameRequest(owners[0].SourceId, 0),
                new SequenceMediaFrameRequest(owners[1].SourceId, 0)
            ], native, out var issue), issue?.Message);
        using var first = native[0]!;
        using var second = native[1]!;
        var logicalA = MediaSourceId.New();
        var logicalB = MediaSourceId.New();
        var backend = new ManagedReferenceGpuBackend();
        using var gpu = new GpuProcessingProvider(backend);
        gpu.Start();
        var composer = new SequenceFrameComposition(gpu, Mixer(logicalA, logicalB),
            Format, MediaSourceId.New(), maximumVideoLayers: 2, maximumAudioTracks: 2);
        var pixelsA = first.Frame.RgbaPixels.Span;
        var pixelsB = second.Frame.RgbaPixels.Span;
        var audioA = MemoryMarshal.Cast<byte, float>(first.Frame.AudioPayload.Span);
        var audioB = MemoryMarshal.Cast<byte, float>(second.Frame.AudioPayload.Span);
        Assert.Equal(960U, first.Frame.Audio!.Timing.SampleCount);
        Assert.Equal(960U, second.Frame.Audio!.Timing.SampleCount);
        var pcm = new float[960 * 2];

        Assert.True(composer.TryCompose(decks, map.SequenceId, 7, position, native,
            [
                new SequenceVideoCompositionLayer(Guid.NewGuid(), 0, 0, 255),
                new SequenceVideoCompositionLayer(Guid.NewGuid(), 1, 1, 128)
            ],
            [
                new SequenceAudioCompositionTrack(Guid.NewGuid(), 0, logicalA),
                new SequenceAudioCompositionTrack(Guid.NewGuid(), 1, logicalB)
            ],
            new FrameTiming(100, 0, new Timebase(1, 50)),
            0, 960, pcm, out var composite, out var audio, out var failure),
            failure?.Message);
        using (composite)
        {
            Assert.NotNull(composite);
            Assert.NotNull(audio);
            Assert.Equal(2, audio.Value.ActiveSourceCount);
            var rgba = gpu.Readback(composite);
            Assert.Equal(RgbaFrameBuffer.RequiredByteLength(Format), rgba.Length);
            Assert.Equal((byte)255, rgba[3]);
            // The first lower video layer is opaque; the upper true decoded
            // RGBA layer is presented at 128/255 opacity by the real GPU backend.
            var upperAlpha = (pixelsB[3] * 128 + 127) / 255;
            for (var channel = 0; channel < 3; channel++)
            {
                var lower = (pixelsA[channel] * pixelsA[3] + 127) / 255;
                var expected = (lower * (255 - upperAlpha) +
                    pixelsB[channel] * upperAlpha + 127) / 255;
                Assert.Equal((byte)expected, rgba[channel]);
            }
        }
        for (var index = 0; index < pcm.Length; index++)
            Assert.Equal(Math.Clamp(audioA[index] * 0.5f + audioB[index] * 0.5f, -1f, 1f),
                pcm[index], 5);
        Assert.Equal(0, backend.ActiveAllocationCount);

        first.Dispose();
        second.Dispose();
        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Theory]
    [InlineData(4800)]
    [InlineData(4004)]
    [InlineData(9600)]
    public void Sample_exact_linear_audio_crossfades_have_complementary_weights(long durationTicks)
    {
        var outgoingTrack = new SequenceAudioCompositionTrack(
            Guid.NewGuid(), 0, MediaSourceId.New(), 0, durationTicks, FadeIn: false);
        var incomingTrack = outgoingTrack with { ClipId = Guid.NewGuid(), FadeIn = true };
        for (long sampleTick = 0; sampleTick < durationTicks; sampleTick += 5)
        {
            var outgoing = SequenceFrameComposition.TransitionWeight(outgoingTrack, sampleTick);
            var incoming = SequenceFrameComposition.TransitionWeight(incomingTrack, sampleTick);
            Assert.Equal(255, (int)outgoing + incoming);
            if (sampleTick == 0)
            {
                Assert.Equal((byte)255, outgoing);
                Assert.Equal((byte)0, incoming);
            }
        }
    }

    [Fact]
    public void Authored_black_gap_materializes_real_gpu_black_and_exact_silent_pcm_without_native_owners()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var decks = new LocalMediaDeckRuntimeService(Format, 2);
        var backend = new ManagedReferenceGpuBackend();
        using var gpu = new GpuProcessingProvider(backend);
        gpu.Start();
        var logicalA = MediaSourceId.New();
        var logicalB = MediaSourceId.New();
        var composer = new SequenceFrameComposition(gpu, Mixer(logicalA, logicalB),
            Format, MediaSourceId.New(), maximumVideoLayers: 2, maximumAudioTracks: 2);
        var pcm = Enumerable.Repeat(0.4f, 1920).ToArray();
        var success = composer.TryCompose(decks, Guid.NewGuid(), 7,
            new SequenceRuntimePosition(0, 1, 0),
            ReadOnlySpan<SequenceMediaFrameLease?>.Empty,
            ReadOnlySpan<SequenceVideoCompositionLayer>.Empty,
            ReadOnlySpan<SequenceAudioCompositionTrack>.Empty,
            new FrameTiming(100, 0, new Timebase(1, 50)),
            0, 960, pcm, out var video, out var audio, out var failure);
        Assert.True(success, failure?.Message);
        using (video)
        {
            Assert.NotNull(video);
            var rgba = gpu.Readback(video);
            Assert.Equal(RgbaFrameBuffer.RequiredByteLength(Format), rgba.Length);
            Assert.Equal((byte)0, rgba[0]);
            Assert.Equal((byte)0, rgba[1]);
            Assert.Equal((byte)0, rgba[2]);
            Assert.Equal((byte)255, rgba[3]);
        }
        Assert.NotNull(audio);
        Assert.Equal(0, audio.Value.ActiveSourceCount);
        Assert.All(pcm, sample => Assert.Equal(0f, sample));
        Assert.Equal(0, backend.ActiveAllocationCount);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Native_generation_replacement_rejects_old_frame_group_without_gpu_or_audio_output()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(Format, 2);
        var (execution, map, owners) = Request(decks, asset.Path, hash);
        Assert.True(decks.PrepareSequenceMedia(execution, map, owners).Prepared);
        var playing = map.Play(100);
        Assert.True(playing.TryMap(100, out var position));
        Assert.True(decks.RepositionSequenceMedia(map.SequenceId, 7, position).Prepared);
        var native = new SequenceMediaFrameLease?[2];
        Assert.True(decks.TryTakeSequenceFrameSet(map.SequenceId, 7, position,
            [
                new SequenceMediaFrameRequest(owners[0].SourceId, 0),
                new SequenceMediaFrameRequest(owners[1].SourceId, 0)
            ], native, out var issue), issue?.Message);
        using var first = native[0]!;
        using var second = native[1]!;

        var sought = playing.Seek(101, 4800);
        Assert.True(sought.TryMap(101, out var newer));
        Assert.True(decks.RepositionSequenceMedia(map.SequenceId, 7, newer).Prepared);

        var backend = new ManagedReferenceGpuBackend();
        using var gpu = new GpuProcessingProvider(backend);
        gpu.Start();
        var a = MediaSourceId.New(); var b = MediaSourceId.New();
        var composer = new SequenceFrameComposition(gpu, Mixer(a, b),
            Format, MediaSourceId.New(), 2, 2);
        var audioOutput = Enumerable.Repeat(0.25f, 1920).ToArray();
        Assert.False(composer.TryCompose(decks, map.SequenceId, 7, position, native,
            [new SequenceVideoCompositionLayer(Guid.NewGuid(), 0, 0, 255)],
            [new SequenceAudioCompositionTrack(Guid.NewGuid(), 0, a)],
            new FrameTiming(100, 0, new Timebase(1, 50)), 0, 960,
            audioOutput, out var video, out var audio, out var rejected));
        Assert.Null(video);
        Assert.Null(audio);
        Assert.Equal("runtime.sequence.composition_stale", rejected?.Code);
        Assert.All(audioOutput, value => Assert.Equal(0f, value));
        Assert.Equal(0, backend.ActiveAllocationCount);

        first.Dispose();
        second.Dispose();
        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }
}
