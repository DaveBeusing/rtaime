// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Recording;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

/// <summary>
/// Opt-in Windows reference qualification uses the repository-pinned FFmpeg
/// encoder. This proves two distinct H.264 RGB contributions and two separate
/// 48-kHz AAC tone windows at the real Preview and Program consumers.
/// </summary>
public sealed class ReferenceSequenceDistinctSourcesOutputTests
{
    [Fact]
    public async Task Two_distinct_native_colors_and_tones_reach_monitoring_Preview_and_committed_Program_PCM()
    {
        if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled)
            return;

        var root = Path.Combine(Path.GetTempPath(),
            "rtaime-sequence-distinct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var red = Generate(root, "red", 440);
            var blue = Generate(root, "blue", 880);
            var format = VideoFormat.Hd1080p50Rgba8;
            var inputA = ProductionSourceId.New();
            var inputB = ProductionSourceId.New();
            var mediaB = new MediaSourceId(inputB.Value);
            await using var runtime = new RuntimeProgramExecutionService(
                new MediaSourceId(inputA.Value), mediaB, format, new NoopWriter());
            await using var journal = new BoundedProductionJournal(64);
            var specification = new ProductionSpecification(
                ControlContractVersion.Current, ProductionId.New(), "Distinct Sequence Reference",
                [new ProductionSourceSpecification(inputA, "Input A"),
                 new ProductionSourceSpecification(inputB, "Sequence Preview")],
                new ProductionRoutingState(inputA, inputA),
                initialOutputRoles:
                [ProductionOutputRoleState.Program(inputA),
                 ProductionOutputRoleState.Aux(inputB)]);
            var control = new ControlHostService(
                specification, runtime.ProviderDescriptors, journal);
            Commit(control, runtime, control.Initialize());
            Commit(control, runtime, control.SelectPreview(new SelectPreviewCommand(
                new ControlCommandMetadata(ControlContractVersion.Current,
                    CommandId.New(), control.State.ProductionId,
                    control.State.Revision), inputB)));
            Assert.True(runtime.IsCommittedSequencePreviewSource(mediaB));

            using var decks = new LocalMediaDeckRuntimeService(format, 2);
            var provider = decks.ProviderDescriptor;
            var route = provider.Capabilities.Single(c => c.Kind == "media.route");
            var resources = provider.Resources.Where(r => r.Kind == "media.route").ToArray();
            var owners = new[]
            {
                Owner(red),
                Owner(blue)
            };
            var authority = control.State;
            var nativeExecution = new PreparedExecutionContract(RuntimeContractVersion.Current,
                PreparedExecutionId.New(),
                new AuthoritySnapshotReference(authority.ProductionId.Value, authority.Revision),
                new Generation(1),
                owners.Select((owner, index) =>
                    new PreparedExecutionBinding(owner.SourceId.Value,
                        route.CapabilityId, resources[index], owner.SourceId, null)).ToArray());
            var sequenceId = Guid.NewGuid();
            var map = SequenceRuntimePositionMap.Prepare(sequenceId, 7,
                format.FrameRate, 20 * 4800, runtime.NextSequenceOutputSlot, 1);
            SequenceExecutionClip Clip(int ownerIndex, SequenceMediaKind kind, int order, byte opacity = 255) =>
                new(Guid.NewGuid(), Guid.Parse(owners[ownerIndex].AssetId.ToString()),
                    owners[ownerIndex].FingerprintSha256, kind,
                    owners[ownerIndex].SourceId.ToString(), order,
                    kind == SequenceMediaKind.Audio ? "program" : null,
                    0, 0,
                    opacity == 255 ? null : SequenceTransitionKind.Dissolve,
                    0, opacity == 255 ? 0 : 20 * 4800, opacity != 255);
            // Different native H.264 sources enter a simultaneous upper/lower
            // compositor with an explicit dissolve contribution. The audio
            // sources carry separate frequencies rather than duplicate PCM.
            var saved = new SequenceExecutionSnapshot(
                SequenceExecutionSnapshotContract.Version, sequenceId, 7,
                new(50, 1), 20 * 4800,
                [new SequenceExecutionSlice(0, 20 * 4800,
                [
                    Clip(0, SequenceMediaKind.Video, 0),
                    Clip(1, SequenceMediaKind.Video, 1),
                    Clip(0, SequenceMediaKind.Audio, 2),
                    Clip(1, SequenceMediaKind.Audio, 3)
                ])]);
            Assert.True(decks.PrepareSequenceMedia(
                nativeExecution, map, owners, executionSnapshot: saved).Prepared);
            var hostEpoch = Guid.NewGuid().ToString("D");
            var reference = new SequencePreparationReference(
                SequencePreparationContract.Version, sequenceId, 7, hostEpoch,
                nativeExecution.PreparedExecutionId.ToString(), 1);
            decks.BindSequencePreviewOutput(reference, mediaB,
                authority.ProductionId.Value, authority.Revision, runtime);
            _ = decks.ApplySequenceShuttle(
                new(SequenceShuttleContract.Version, reference, 0, SequenceShuttleAction.Play),
                hostEpoch, authority.ProductionId.Value, authority.Revision,
                () => runtime.NextSequenceOutputSlot);

            await using var monitor = runtime.MonitoringHub.Subscribe(
                capacity: 4, requiresCpuFallback: true);
            var first = decks.PresentSequenceBoundary(runtime, runtime.NextSequenceOutputSlot);
            Assert.True(first.Published, first.FailureCode);
            using (var previewBoundary = runtime.ProcessNextBoundary())
            {
                decks.ObserveSequenceProgramBoundary(previewBoundary);
                Assert.NotEqual(mediaB, previewBoundary.CommittedProgramSourceId);
            }

            // Confirm real Preview pixels, not just decoded memory or an
            // advertised native preparation flag.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                MonitoringFrame? preview = null;
                for (var index = 0; index < 4; index++)
                {
                    var captured = await monitor.ReadAsync(timeout.Token);
                    if (captured.Descriptor.StreamKind == MonitoringStreamKind.Source &&
                        captured.Descriptor.SourceId == mediaB)
                    {
                        preview = captured;
                        break;
                    }
                }
                Assert.NotNull(preview);
                Assert.True(preview!.HasFallbackPayload);
                var rgba = preview.Pixels.Span;
                Assert.True(rgba.Length >= 4);
                Assert.True(rgba[0] > 30 && rgba[2] > 30,
                    "The native Preview contribution must contain both red and blue source pixels.");
            }

            var before = control.State.Revision;
            var command = new SequenceOutputCommand(SequenceOutputContract.Version,
                reference, mediaB.ToString(), before.Value, Guid.NewGuid());
            var armed = decks.ArmSequenceTake(command,
                authority.ProductionId.Value, before, runtime, hostEpoch);
            Assert.Equal("TAKE_PENDING", armed.OutputState);
            var cut = control.CutProgram(new CutProgramCommand(
                new ControlCommandMetadata(ControlContractVersion.Current,
                    CommandId.New(), control.State.ProductionId, before), inputB));
            Assert.True(cut.Accepted, cut.Failure?.Message);
            var admitted = Assert.IsType<ControlHostExecutionPackage>(cut.Execution);
            Assert.Equal(command, decks.RequireArmedSequenceTake(
                admitted.PreparedExecution, admitted.ProgramTransition));
            var confirmed = runtime.ApplyArmedSequenceTake(
                admitted.PreparedExecution, admitted.ProgramSinkId,
                admitted.ProgramTransition,
                () => decks.AuthorizeSequenceProgram(reference, mediaB,
                    authority.ProductionId.Value, before,
                    admitted.PreparedExecution.AuthoritySnapshot.Revision, runtime));
            Assert.True(confirmed.Committed, confirmed.Commit?.Failure?.Message);
            Assert.True(control.ConfirmRuntimeCommit(
                admitted.PreparedExecution.PreparedExecutionId, confirmed.Commit!).Committed);

            var second = decks.PresentSequenceBoundary(runtime, runtime.NextSequenceOutputSlot);
            Assert.True(second.Published, second.FailureCode);
            using (var program = runtime.ProcessNextBoundary())
            {
                decks.ObserveSequenceProgramBoundary(program);
                Assert.Equal(mediaB, program.CommittedProgramSourceId);
                Assert.Equal(mediaB, program.ProgramFrame.SourceId);
                var pixels = program.ProgramPixels.Span;
                Assert.True(pixels.Length > 4);
                Assert.True(pixels[0] > 30 && pixels[2] > 30 && pixels[1] < 80,
                    "Program must contain distinguishable red and blue H.264 layers.");
                Assert.Equal((byte)255, pixels[3]);
                var samples = MemoryMarshal.Cast<byte, float>(
                    program.ProgramAudioPayload.AsSpan());
                Assert.Equal(1920, samples.Length);
                Assert.True(ToneMagnitude(samples, 440) > 0.005,
                    "Committed Program PCM must retain the first AAC tone.");
                Assert.True(ToneMagnitude(samples, 880) > 0.005,
                    "Committed Program PCM must retain the independent second AAC tone.");
            }
            Assert.Equal("PROGRAM_CONFIRMED",
                decks.GetSequenceOutput(reference, hostEpoch).OutputState);
            Assert.Equal("CANCELED",
                decks.CancelPreparedSequence(reference, hostEpoch).State);
            Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static SequenceMediaOwnerRequest Owner(string file) =>
        new(MediaSourceId.New(), MediaAssetId.New(), file,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), 0, 0);

    private static double ToneMagnitude(ReadOnlySpan<float> pcm, int frequency)
    {
        double real = 0;
        double imaginary = 0;
        var frames = pcm.Length / 2;
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (pcm[frame * 2] + pcm[frame * 2 + 1]) * 0.5d;
            var phase = 2d * Math.PI * frequency * frame / 48000;
            real += sample * Math.Cos(phase);
            imaginary += sample * Math.Sin(phase);
        }
        return Math.Sqrt(real * real + imaginary * imaginary) / frames;
    }

    private static string Generate(string directory, string color, int frequency)
    {
        var path = Path.Combine(directory, color + "-" + frequency + ".mp4");
        var ffmpeg = Environment.GetEnvironmentVariable("RTAIME_FFMPEG_PATH");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(
            string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=" + color + ":s=1920x1080:r=50:d=2",
            "-f", "lavfi", "-i", "sine=frequency=" + frequency + ":sample_rate=48000:duration=2",
            "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264",
            "-preset", "ultrafast", "-profile:v", "baseline", "-crf", "32",
            "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac", "-ar", "48000",
            "-ac", "2", "-b:a", "128k", "-shortest", "-movflags", "+faststart",
            path
        })
            process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start())
            throw new InvalidOperationException("Pinned reference encoder did not start.");
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Differentiated native source encoder exceeded its deadline.");
        }
        var errors = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0 || !File.Exists(path) ||
            new FileInfo(path).Length is < 1 or > 32 * 1024 * 1024)
            throw new InvalidDataException("Differentiated H.264/AAC source generation failed: " +
                errors[..Math.Min(512, errors.Length)]);
        return path;
    }

    private static void Commit(ControlHostService control,
        RuntimeProgramExecutionService runtime, ControlHostOperationResult staged)
    {
        Assert.True(staged.Accepted, staged.Failure?.Message);
        var package = Assert.IsType<ControlHostExecutionPackage>(staged.Execution);
        var applied = runtime.ApplyExecution(package.PreparedExecution,
            package.ProgramSinkId, package.ProgramTransition);
        Assert.True(applied.Committed, applied.Commit?.Failure?.Message ??
            applied.Prepare.Failure?.Message);
        Assert.True(control.ConfirmRuntimeCommit(
            package.PreparedExecution.PreparedExecutionId, applied.Commit!).Committed);
    }

    private sealed class NoopWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
