// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using rtaime.Provider.Gpu;

namespace rtaime.RuntimeHost;

/// <summary>
/// One optional software output mapping request. All frames and geometry remain
/// governed by the existing GPU owner; no new Program clock or sink is created.
/// </summary>
public sealed record RuntimeSoftwareOutputTarget(
    string TargetId, IReadOnlyList<GpuOutputMappingInput> Inputs);

/// <summary>Leases all output frames as one group until every consumer releases them.</summary>
public sealed class RuntimeMappedOutputFrameSet : IDisposable
{
    private Dictionary<string, GpuFrame>? _frames;

    internal RuntimeMappedOutputFrameSet(Dictionary<string, GpuFrame> frames) =>
        _frames = frames;

    public IReadOnlyDictionary<string, GpuFrame> Frames
    {
        get
        {
            var frames = _frames ?? throw new ObjectDisposedException(nameof(RuntimeMappedOutputFrameSet));
            return new ReadOnlyDictionary<string, GpuFrame>(frames);
        }
    }

    public void Dispose()
    {
        var frames = Interlocked.Exchange(ref _frames, null);
        if (frames is null) return;
        foreach (var frame in frames.Values)
            frame.Dispose();
        frames.Clear();
    }
}

public sealed record RuntimeMappedOutputGroupResult(
    bool Succeeded, string Code, string Detail, RuntimeMappedOutputFrameSet? Frames);

/// <summary>
/// All-or-none *software frame construction*. The caller still owns subsequent
/// authoritative target binding, native sink reservation and boundary commit.
/// </summary>
public static class RuntimeMappedOutputComposer
{
    public static RuntimeMappedOutputGroupResult Compose(
        GpuProcessingProvider gpu,
        GpuFrame program,
        RuntimeResourceCapabilityProfile profile,
        IReadOnlyList<RuntimeSoftwareOutputTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0 || !gpu.SupportsOutputMapping)
            return new(false, "runtime.output_mapping.unavailable",
                "No admitted software mapping targets or GPU mapping backend.", null);
        if (program.IsDisposed || targets.Any(target => target is null ||
            target.Inputs is null || target.Inputs.Count == 0 ||
            target.Inputs.Any(input => input is null || input.Frame is null || input.Surface is null ||
                input.Frame.IsDisposed || input.Frame.SurfaceId != input.Surface.SourceId)))
            return new(false, "runtime.output_mapping.invalid",
                "Output sources or retained GPU leases are not valid.", null);

        var candidate = new RuntimeResourceCapacityRequest(
            program.Descriptor.Surface.Format, 0, 0,
            targets.Select(target => new RuntimeOutputCapacityRequest(
                target.TargetId, program.Descriptor.Surface.Format,
                target.Inputs.Count, PhysicalOutput: false)).ToArray());
        var admission = RuntimeResourceCapacityAdmission.Evaluate(profile, candidate);
        if (!admission.Admitted)
            return new(false, admission.Code, admission.Detail, null);

        // Validate all target geometries before mapping any output.
        try
        {
            foreach (var target in targets)
            foreach (var input in target.Inputs)
            {
                if (input.Surface.SourceFormat != input.Frame.Descriptor.Surface.Format)
                    return new(false, "runtime.output_mapping.format_mismatch",
                        "Mapped source and geometry formats differ.", null);
                input.Surface.Validate();
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return new(false, "runtime.output_mapping.geometry",
                "Output map geometry is unsupported: " + error.GetType().Name, null);
        }

        var created = new Dictionary<string, GpuFrame>(StringComparer.Ordinal);
        try
        {
            foreach (var target in targets)
            {
                var mapped = gpu.MapOutput(program, target.Inputs);
                if (!mapped.Succeeded || mapped.Frame is null)
                {
                    foreach (var frame in created.Values) frame.Dispose();
                    return new(false, mapped.Failure?.Code ?? "runtime.output_mapping.failed",
                        mapped.Failure?.Message ?? "A mapped GPU target could not be created.", null);
                }
                created.Add(target.TargetId, mapped.Frame);
            }

            return new(true, "runtime.output_mapping.prepared",
                "All mapped software frames are retained; external sink activation has not occurred.",
                new RuntimeMappedOutputFrameSet(created));
        }
        catch
        {
            foreach (var frame in created.Values) frame.Dispose();
            throw;
        }
    }
}
