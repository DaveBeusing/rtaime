// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Runtime.InteropServices;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Gpu;
using rtaime.Recording;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

/// <summary>
/// Real GPU composition input is consumed by the existing Preview/Program
/// selection, audio production and output readback, not a test-only sink.
/// </summary>
public sealed class SequenceProgramPixelAudioOutputTests
{
    [Fact]
    public async Task Gpu_and_stereo_pcm_enter_existing_program_sink_only_after_control_committed_take()
    {
        var sourceA = new ProductionSourceId(Identity.New());
        var sourceB = new ProductionSourceId(Identity.New());
        var mediaB = new MediaSourceId(sourceB.Value);
        var specification = new ProductionSpecification(
            ControlContractVersion.Current, ProductionId.New(), "Sequence sink qualification",
            [new ProductionSourceSpecification(sourceA, "Input A"),
             new ProductionSourceSpecification(sourceB, "Sequence Preview")],
            new ProductionRoutingState(sourceA, sourceA),
            initialOutputRoles:
            [ProductionOutputRoleState.Program(sourceA),
             ProductionOutputRoleState.Aux(sourceB)]);
        var format = VideoFormat.Hd1080p50Rgba8;
        await using var runtime = new RuntimeProgramExecutionService(
            new MediaSourceId(sourceA.Value), mediaB, format, new NoopRecordingWriter());
        await using var journal = new BoundedProductionJournal(64);
        var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
        var initialized = control.Initialize();
        Assert.True(initialized.Accepted);
        Commit(control, runtime, initialized);

        // Preview selection is a genuine Control-authorized transaction.
        var preview = control.SelectPreview(new SelectPreviewCommand(
            new ControlCommandMetadata(ControlContractVersion.Current,
                CommandId.New(), control.State.ProductionId, control.State.Revision),
            sourceB));
        Commit(control, runtime, preview);
        Assert.True(runtime.IsCommittedSequencePreviewSource(mediaB));

        var sourcePcm = Enumerable.Repeat(0.25f, 960 * 2).ToArray();
        void FeedActualSource(ulong slot)
        {
            using var frame = runtime.SequenceCompositionGpu.Upload(mediaB,
                RgbaFrameBuffer.Solid(format, 220, 60, 10, 255),
                new FrameTiming(slot, checked((long)slot), new Timebase(1, 50)),
                Generation.Initial, "sequence-program-evidence");
            runtime.PresentSequenceInput(mediaB, frame, sourcePcm);
        }

        FeedActualSource(0);
        using (var originalProgram = runtime.ProcessNextBoundary())
        {
            Assert.NotEqual(mediaB, originalProgram.CommittedProgramSourceId);
            Assert.NotEqual((byte)220, originalProgram.ProgramPixels.Span[0]);
        }

        // CUT uses the one existing PreparedExecution/Runtime commit path.
        var take = control.CutProgram(new CutProgramCommand(
            new ControlCommandMetadata(ControlContractVersion.Current,
                CommandId.New(), control.State.ProductionId, control.State.Revision),
            sourceB));
        Commit(control, runtime, take);
        Assert.Equal(mediaB, runtime.CommittedProgramSourceId);
        Assert.True(runtime.IsCommittedSequenceOutputSource(mediaB, allowProgram: true));

        FeedActualSource(1);
        using (var committedProgram = runtime.ProcessNextBoundary())
        {
            Assert.Equal(mediaB, committedProgram.CommittedProgramSourceId);
            Assert.Equal(mediaB, committedProgram.ProgramFrame.SourceId);
            var pixel = committedProgram.ProgramPixels.Span;
            Assert.Equal((byte)220, pixel[0]);
            Assert.Equal((byte)60, pixel[1]);
            Assert.Equal((byte)10, pixel[2]);
            Assert.Equal((byte)255, pixel[3]);
            var actualAudio = MemoryMarshal.Cast<byte, float>(
                committedProgram.ProgramAudioPayload.AsSpan());
            Assert.Equal(sourcePcm.Length, actualAudio.Length);
            Assert.Contains(actualAudio.ToArray(), sample => Math.Abs(sample) > 0.01f);
            Assert.All(actualAudio.ToArray(), sample => Assert.True(float.IsFinite(sample)));
        }
        Assert.Equal(0, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
    }

    private static void Commit(ControlHostService control,
        RuntimeProgramExecutionService runtime, ControlHostOperationResult staged)
    {
        Assert.True(staged.Accepted, staged.Failure?.Message);
        var execution = Assert.IsType<ControlHostExecutionPackage>(staged.Execution);
        var result = runtime.ApplyExecution(execution.PreparedExecution,
            execution.ProgramSinkId, execution.ProgramTransition);
        Assert.True(result.Committed, result.Commit?.Failure?.Message ??
            result.Prepare.Failure?.Message);
        var confirmed = control.ConfirmRuntimeCommit(
            execution.PreparedExecution.PreparedExecutionId, result.Commit!);
        Assert.True(confirmed.Committed, confirmed.Failure?.Message);
    }

    private sealed class NoopRecordingWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
