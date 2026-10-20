// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeSoftwareOutputPublicationTests
{
    private static readonly VideoFormat Format =
        new(2, 2, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static GpuOutputMappingInput Input(GpuFrame frame, bool faded = false) =>
        new(frame, new GpuOutputMappingSurface(frame.SurfaceId, Format,
            0, 0, 1, 1, new(0, 0), new(2, 0), new(2, 2), new(0, 2),
            faded ? new GpuOutputEdgeBlend(0.5, 0, 0, 0) : GpuOutputEdgeBlend.None, 0));

    private static RuntimeResourceCapabilityProfile Profile() =>
        new("virtual", "managed-reference", null, [Format], ["program", "aux"],
            2, 2, 4, 5_000_000, 5_000_000, 0, false);

    private static GpuFrame Program(GpuProcessingProvider gpu, ulong sequence, byte red) =>
        gpu.Upload(MediaSourceId.New(), RgbaFrameBuffer.Solid(Format, red, 40, 60),
            new FrameTiming(sequence, 0, new Timebase(1, 50)),
            Generation.Initial, "published-program");

    private static RuntimeMappedOutputFrameSet Mapped(GpuProcessingProvider gpu, GpuFrame program)
    {
        var group = RuntimeMappedOutputComposer.Compose(gpu, program, Profile(),
            [new("program", [Input(program)]), new("aux", [Input(program, faded: true)])]);
        Assert.True(group.Succeeded, group.Detail);
        return Assert.IsType<RuntimeMappedOutputFrameSet>(group.Frames);
    }

    [Fact]
    public void One_boundary_publishes_independent_target_pixels_without_mutating_program()
    {
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        using var program = Program(gpu, 12, 90);
        var originalPixels = gpu.Readback(program);
        var epoch = Guid.NewGuid();
        using var published = new RuntimeSoftwareOutputPublication(epoch);

        var staged = published.Stage(epoch, 4, Mapped(gpu, program));
        Assert.True(staged.Succeeded, staged.Detail);
        Assert.False(published.TryAcquire(out _));
        Assert.Equal("runtime.software_output.published",
            published.CommitAtBoundary(epoch, 4, 12).Code);

        Assert.True(published.TryAcquire(out var lease));
        using (lease!)
        {
            Assert.Equal(4UL, lease.ExecutionRevision);
            Assert.Equal(12UL, lease.ProgramSequence);
            Assert.Equal(2, lease.Frames.Count);
            Assert.NotEqual(lease.Frames["program"].SurfaceId, lease.Frames["aux"].SurfaceId);
            Assert.Equal(new byte[] { 90, 40, 60, 255 },
                gpu.Readback(lease.Frames["program"]).Take(4).ToArray());
            Assert.Equal(new byte[] { 45, 20, 30, 255 },
                gpu.Readback(lease.Frames["aux"]).Take(4).ToArray());
        }
        Assert.Equal(originalPixels, gpu.Readback(program));
        Assert.Equal(2, published.Snapshot.ActiveTargets);
        Assert.False(published.Snapshot.HasPreparedGroup);
    }

    [Fact]
    public void Stale_revision_and_epoch_leave_previous_group_active()
    {
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        var epoch = Guid.NewGuid();
        using var published = new RuntimeSoftwareOutputPublication(epoch);
        using var first = Program(gpu, 3, 80);
        Assert.True(published.Stage(epoch, 6, Mapped(gpu, first)).Succeeded);
        Assert.True(published.CommitAtBoundary(epoch, 6, 3).Succeeded);
        using var second = Program(gpu, 4, 160);
        using var rejectedGroup = Mapped(gpu, second);
        Assert.Equal("runtime.software_output.epoch_changed",
            published.Stage(Guid.NewGuid(), 7, rejectedGroup).Code);

        var candidate = Mapped(gpu, second);
        Assert.True(published.Stage(epoch, 7, candidate).Succeeded);
        Assert.Equal("runtime.software_output.stale_boundary",
            published.CommitAtBoundary(epoch, 6, 4).Code);
        Assert.Equal("runtime.software_output.stale_boundary",
            published.CommitAtBoundary(epoch, 7, 3).Code);
        Assert.Equal(3UL, published.Snapshot.ActiveProgramSequence);
        Assert.True(published.TryAcquire(out var existing));
        using (existing!)
            Assert.Equal(new byte[] { 80, 40, 60, 255 },
                gpu.Readback(existing.Frames["program"]).Take(4).ToArray());
        Assert.True(published.CancelPrepared());
        Assert.False(published.Snapshot.HasPreparedGroup);
        Assert.Equal(3UL, published.Snapshot.ActiveProgramSequence);
    }

    [Fact]
    public void Retired_frame_generations_are_bounded_and_released_after_reader_leases()
    {
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        var epoch = Guid.NewGuid();
        using var published = new RuntimeSoftwareOutputPublication(epoch, maximumRetainedOldGenerations: 1);
        using var first = Program(gpu, 1, 80);
        Assert.True(published.Stage(epoch, 1, Mapped(gpu, first)).Succeeded);
        Assert.True(published.CommitAtBoundary(epoch, 1, 1).Succeeded);
        Assert.True(published.TryAcquire(out var original));
        using var second = Program(gpu, 2, 120);
        Assert.True(published.Stage(epoch, 1, Mapped(gpu, second)).Succeeded);
        Assert.True(published.CommitAtBoundary(epoch, 1, 2).Succeeded);
        Assert.Equal(1, published.Snapshot.RetainedOldGenerations);
        Assert.True(published.TryAcquire(out var current));
        Assert.Equal(2UL, current!.ProgramSequence);

        using var third = Program(gpu, 3, 160);
        using var thirdCandidate = Mapped(gpu, third);
        var denied = published.Stage(epoch, 1, thirdCandidate);
        Assert.False(denied.Succeeded);
        Assert.Equal("runtime.software_output.reader_pressure", denied.Code);
        Assert.Equal(2UL, published.Snapshot.ActiveProgramSequence);
        Assert.Equal(new byte[] { 80, 40, 60, 255 },
            gpu.Readback(original!.Frames["program"]).Take(4).ToArray());
        original.Dispose();
        Assert.Equal(0, published.Snapshot.RetainedOldGenerations);
        Assert.Equal(1, published.Snapshot.DeferredDisposalGroups);
        // Releasing a reader does not destroy GPU surfaces on its caller thread.
        using (var stillCharged = Mapped(gpu, third))
        {
            var blocked = published.Stage(epoch, 1, stillCharged);
            Assert.False(blocked.Succeeded);
            Assert.Equal("runtime.software_output.reader_pressure", blocked.Code);
        }
        Assert.Equal(1, published.DrainRetiredOnManagementThread());
        Assert.Equal(0, published.Snapshot.DeferredDisposalGroups);

        var final = Mapped(gpu, third);
        Assert.True(published.Stage(epoch, 1, final).Succeeded);
        Assert.True(published.CommitAtBoundary(epoch, 1, 3).Succeeded);
        Assert.Equal(3UL, published.Snapshot.ActiveProgramSequence);
        Assert.Equal(2UL, current.ProgramSequence);
        Assert.Equal(new byte[] { 120, 40, 60, 255 },
            gpu.Readback(current.Frames["program"]).Take(4).ToArray());
        current.Dispose();
        Assert.Equal(0, published.Snapshot.RetainedOldGenerations);
    }

    [Fact]
    public void Replaced_frames_are_not_disposed_on_the_Program_boundary()
    {
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        var epoch = Guid.NewGuid();
        using var publication = new RuntimeSoftwareOutputPublication(epoch,
            maximumRetainedOldGenerations: 1);
        using var first = Program(gpu, 11, 90);
        Assert.True(publication.Stage(epoch, 7, Mapped(gpu, first)).Succeeded);
        Assert.True(publication.CommitAtBoundary(epoch, 7, 11).Succeeded);

        using var second = Program(gpu, 12, 160);
        Assert.True(publication.Stage(epoch, 7, Mapped(gpu, second)).Succeeded);
        Assert.True(publication.CommitAtBoundary(epoch, 7, 12).Succeeded);
        Assert.Equal(1, publication.Snapshot.DeferredDisposalGroups);
        Assert.Equal(6, gpu.ActiveSurfaceCount);

        using var third = Program(gpu, 13, 200);
        using (var notAdmitted = Mapped(gpu, third))
        {
            var pressure = publication.Stage(epoch, 7, notAdmitted);
            Assert.False(pressure.Succeeded);
            Assert.Equal("runtime.software_output.reader_pressure", pressure.Code);
        }

        Assert.Equal(1, publication.DrainRetiredOnManagementThread());
        Assert.Equal(0, publication.Snapshot.DeferredDisposalGroups);
        Assert.Equal(5, gpu.ActiveSurfaceCount);
        Assert.True(publication.Stage(epoch, 7, Mapped(gpu, third)).Succeeded);
        Assert.True(publication.CommitAtBoundary(epoch, 7, 13).Succeeded);
    }

    [Fact]
    public void Shutdown_does_not_dispose_frames_still_owned_by_readers()
    {
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        using var program = Program(gpu, 8, 70);
        var epoch = Guid.NewGuid();
        var published = new RuntimeSoftwareOutputPublication(epoch);
        Assert.True(published.Stage(epoch, 2, Mapped(gpu, program)).Succeeded);
        Assert.True(published.CommitAtBoundary(epoch, 2, 8).Succeeded);
        Assert.True(published.TryAcquire(out var lease));
        published.Dispose();
        Assert.False(published.TryAcquire(out _));
        Assert.Equal(new byte[] { 70, 40, 60, 255 },
            gpu.Readback(lease!.Frames["program"]).Take(4).ToArray());
        lease.Dispose();
        Assert.Equal(1, gpu.ActiveSurfaceCount);
    }
}
