// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeMappedOutputComposerTests
{
    private static readonly VideoFormat Format =
        new(2, 2, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
    private static readonly FrameTiming Timing = new(5, 0, new Timebase(1, 50));

    private static RuntimeResourceCapabilityProfile Profile() =>
        new("virtual", "software-reference", null, [Format],
            ["program", "aux"], 2, 2, 8, 5_000_000, 5_000_000, 0, false);

    private static GpuOutputMappingInput Input(GpuFrame frame, bool feathered = false) =>
        new(frame,
            new GpuOutputMappingSurface(frame.SurfaceId, Format, 0, 0, 1, 1,
                new(0, 0), new(2, 0), new(2, 2), new(0, 2),
                feathered
                    ? new GpuOutputEdgeBlend(0.5, 0, 0, 0)
                    : GpuOutputEdgeBlend.None,
                0));

    [Fact]
    public void Two_mapped_software_targets_retain_independent_frames_and_leave_Program_unchanged()
    {
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        var source = MediaSourceId.New();
        using var program = gpu.Upload(source, RgbaFrameBuffer.Solid(Format, 70, 90, 110),
            Timing, Generation.Initial, "multi-output");
        var baseline = gpu.Readback(program);

        var composed = RuntimeMappedOutputComposer.Compose(gpu, program, Profile(),
            [new("program", [Input(program)]), new("aux", [Input(program, feathered: true)])]);
        Assert.True(composed.Succeeded, composed.Detail);
        using (var group = Assert.IsType<RuntimeMappedOutputFrameSet>(composed.Frames))
        {
            Assert.Equal(2, group.Frames.Count);
            Assert.NotEqual(group.Frames["program"].SurfaceId, group.Frames["aux"].SurfaceId);
            Assert.Equal(new byte[] { 70, 90, 110, 255 },
                gpu.Readback(group.Frames["program"]).Take(4).ToArray());
            Assert.Equal(new byte[] { 35, 45, 55, 255 },
                gpu.Readback(group.Frames["aux"]).Take(4).ToArray());
            Assert.Equal(3, gpu.ActiveSurfaceCount);
            Assert.Equal(baseline, gpu.Readback(program));
        }
        Assert.Equal(1, gpu.ActiveSurfaceCount);
    }

    [Fact]
    public void Unknown_target_and_bad_geometry_do_not_allocate_any_mapped_output()
    {
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        using var program = gpu.Upload(MediaSourceId.New(),
            RgbaFrameBuffer.Solid(Format, 5, 10, 15), Timing,
            Generation.Initial, "multi-output-negative");
        var unknown = RuntimeMappedOutputComposer.Compose(gpu, program, Profile(),
            [new("outside", [Input(program)])]);
        Assert.False(unknown.Succeeded);
        Assert.Equal("runtime.admission.output_unsupported", unknown.Code);
        Assert.Null(unknown.Frames);
        var bad = Input(program) with
        {
            Surface = Input(program).Surface with { BottomRight = new(0, 0) }
        };
        var rejected = RuntimeMappedOutputComposer.Compose(gpu, program, Profile(),
            [new("program", [bad])]);
        Assert.False(rejected.Succeeded);
        Assert.Equal("runtime.output_mapping.geometry", rejected.Code);
        Assert.Null(rejected.Frames);
        Assert.Equal(1, gpu.ActiveSurfaceCount);
    }

    [Fact]
    public void Later_provider_failure_releases_all_earlier_mapped_target_leases()
    {
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        using var foreign = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        foreign.Start();
        using var program = gpu.Upload(MediaSourceId.New(),
            RgbaFrameBuffer.Solid(Format, 70, 90, 110), Timing,
            Generation.Initial, "multi-output");
        using var other = foreign.Upload(MediaSourceId.New(),
            RgbaFrameBuffer.Solid(Format, 10, 20, 30), Timing,
            Generation.Initial, "foreign-output");
        var original = gpu.Readback(program);
        var failed = RuntimeMappedOutputComposer.Compose(gpu, program, Profile(),
            [new("program", [Input(program)]), new("aux", [Input(other)])]);
        Assert.False(failed.Succeeded);
        Assert.Equal("gpu.output_mapping.source_stale", failed.Code);
        Assert.Null(failed.Frames);
        Assert.Equal(1, gpu.ActiveSurfaceCount);
        Assert.Equal(1, foreign.ActiveSurfaceCount);
        Assert.Equal(original, gpu.Readback(program));
    }
}
