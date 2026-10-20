// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.RuntimeHost;

// Runtime owns execution and actual output evidence; the saved show remains
// owned by ControlHost. This plan never establishes a second Program renderer.
internal sealed record PreparedOutputMappingSurface(
    Guid SurfaceId,
    string? SourceId,
    string? BusRoleId,
    GpuOutputMappingSurface Geometry);

internal sealed class PreparedOutputMappingPlan
{
    public PreparedOutputMappingPlan(
        Guid configurationId, ulong canvasRevision, ulong executionRevision,
        string providerId, string targetId, int targetWidth, int targetHeight,
        PreparedOutputMappingSurface[] surfaces, bool identity)
    {
        ConfigurationId = configurationId;
        CanvasRevision = canvasRevision;
        ExecutionRevision = executionRevision;
        ProviderId = providerId;
        TargetId = targetId;
        TargetWidth = targetWidth;
        TargetHeight = targetHeight;
        Surfaces = Array.AsReadOnly((PreparedOutputMappingSurface[])surfaces.Clone());
        Identity = identity;
    }

    public Guid ConfigurationId { get; }
    public ulong CanvasRevision { get; }
    public ulong ExecutionRevision { get; }
    public string ProviderId { get; }
    public string TargetId { get; }
    public int TargetWidth { get; }
    public int TargetHeight { get; }
    public IReadOnlyList<PreparedOutputMappingSurface> Surfaces { get; }
    public bool Identity { get; }
}

public sealed partial class RuntimeProgramExecutionService
{
    public const int MaximumMappedOutputSurfaces = 8;
    public const long MaximumMappedOutputBytes = 16L * 1024 * 1024;

    private PreparedOutputMappingPlan? _preparedOutputMapping;
    private PreparedOutputMappingPlan? _pendingOutputMapping;
    private PreparedOutputMappingPlan? _activeOutputMapping;
    private bool _pendingOutputMappingDisable;
    private string _outputMappingState = "UNMAPPED";
    private string? _outputMappingError;
    private string? _outputMappingSource;
    private ulong? _outputMappingSequence;

    public OutputCanvasActivationResult OutputMappingSnapshot
    {
        get
        {
            lock (_gate) return OutputMappingSnapshotUnsafe();
        }
    }

    public OutputCanvasActivationResult PrepareOutputMapping(OutputCanvasRuntimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_boundaryExecutionGate)
        lock (_gate)
        {
            ThrowIfDisposed();
            _preparedOutputMapping = null;
            if (request.Configuration is null || request.Configuration.ConfigurationId == Guid.Empty)
                return RejectMappingUnsafe("mapping.invalid_configuration");
            var execution = _runtime.ActiveExecution;
            if (execution is null || _runtime.State.ExecutionRevision.Value != request.ExpectedExecutionRevision)
                return RejectMappingUnsafe("mapping.runtime_revision_changed");
            if (!request.Enabled)
            {
                _preparedOutputMapping = null;
                _outputMappingState = "PREPARED_DISABLE";
                _outputMappingError = null;
                return OutputMappingSnapshotUnsafe();
            }

            try
            {
                var compiled = CompileOutputMappingUnsafe(request.Configuration, request.ExpectedExecutionRevision);
                _preparedOutputMapping = compiled;
                _outputMappingState = "PREPARED";
                _outputMappingError = null;
                return OutputMappingSnapshotUnsafe();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException or OverflowException)
            {
                return RejectMappingUnsafe("mapping.prepare_rejected." + exception.GetType().Name);
            }
        }
    }

    public OutputCanvasActivationResult CommitOutputMapping(
        Guid configurationId, ulong revision, ulong executionRevision, bool enabled)
    {
        lock (_boundaryExecutionGate)
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_runtime.ActiveExecution is null ||
                _runtime.State.ExecutionRevision.Value != executionRevision)
                return RejectMappingUnsafe("mapping.runtime_revision_changed");

            if (enabled)
            {
                if (_preparedOutputMapping is not { } prepared ||
                    prepared.ConfigurationId != configurationId ||
                    prepared.CanvasRevision != revision ||
                    prepared.ExecutionRevision != executionRevision)
                    return RejectMappingUnsafe("mapping.prepared_revision_changed");
                _pendingOutputMapping = prepared;
                _pendingOutputMappingDisable = false;
            }
            else
            {
                if (_outputMappingState != "PREPARED_DISABLE")
                    return RejectMappingUnsafe("mapping.disable_not_prepared");
                _pendingOutputMapping = null;
                _pendingOutputMappingDisable = true;
            }

            _preparedOutputMapping = null;
            _outputMappingState = "PENDING_BOUNDARY";
            _outputMappingError = null;
            return OutputMappingSnapshotUnsafe();
        }
    }

    private OutputCanvasActivationResult OutputMappingSnapshotUnsafe()
    {
        var active = _activeOutputMapping;
        var pending = _pendingOutputMapping;
        return new OutputCanvasActivationResult(
            active?.CanvasRevision,
            pending?.CanvasRevision,
            _outputMappingState,
            _outputMappingError,
            _outputMappingSource,
            active?.TargetId,
            active?.TargetWidth ?? 0,
            active?.TargetHeight ?? 0,
            active?.ExecutionRevision,
            _outputMappingSequence);
    }

    private OutputCanvasActivationResult RejectMappingUnsafe(string code)
    {
        _preparedOutputMapping = null;
        _outputMappingError = code;
        // An invalid new request must not disturb the already active plan.
        _outputMappingState = _activeOutputMapping is null ? "REJECTED" : "ACTIVE_REJECTED_UPDATE";
        Observe("output.mapping.rejected:" + code);
        return OutputMappingSnapshotUnsafe();
    }

    private PreparedOutputMappingPlan CompileOutputMappingUnsafe(
        OutputCanvasConfiguration config, ulong expectedExecutionRevision)
    {
        if (!_gpu.SupportsOutputMapping)
            throw new NotSupportedException("Output mapping is unavailable on this GPU backend.");
        if (config.Version != OutputCanvasContract.Version ||
            config.Canvases is null || config.Surfaces is null || config.Bindings is null)
            throw new ArgumentException("Unsupported or incomplete canvas configuration.");
        if (config.Canvases.Length > OutputCanvasContract.MaximumCanvases ||
            config.Surfaces.Length > OutputCanvasContract.MaximumSurfaces ||
            config.Bindings.Length > OutputCanvasContract.MaximumBindings)
            throw new ArgumentException("Canvas authoring capacity was exceeded.");

        var activeBindings = config.Bindings.Where(b => b is not null && b.Enabled).ToArray();
        if (activeBindings.Length != 1)
            throw new NotSupportedException("Exactly one governed Program output can be mapped by this provider.");
        var target = activeBindings[0];
        var role = OutputRoleSnapshotsUnsafe().SingleOrDefault(x => x.RoleId == "program")
            ?? throw new InvalidOperationException("There is no committed Program output role.");
        if (role.TargetId.ToString() != target.TargetId ||
            role.ProviderId.ToString() != target.ProviderId ||
            target.ProfileId != "production")
            throw new NotSupportedException("Canvas output does not match the governed Program provider and target.");
        var canvas = config.Canvases.SingleOrDefault(c => c is not null && c.CanvasId == target.CanvasId)
            ?? throw new ArgumentException("Output canvas identity is missing.");
        if (canvas.Width <= 0 || canvas.Height <= 0)
            throw new ArgumentException("Output canvas dimensions are invalid.");

        var bytes = RgbaFrameBuffer.RequiredByteLength(_format);
        if (bytes > MaximumMappedOutputBytes)
            throw new NotSupportedException("The mapped output exceeds the admitted framebuffer allocation budget.");
        var mapped = config.Surfaces
            .Where(surface => surface is not null && surface.Enabled && surface.CanvasId == canvas.CanvasId)
            .OrderBy(surface => surface.ZOrder)
            .ThenBy(surface => surface.SurfaceId.ToString("N"), StringComparer.Ordinal)
            .ToArray();
        if (mapped.Length is < 1 or > MaximumMappedOutputSurfaces)
            throw new NotSupportedException("Mapped output requires one to eight admitted visible surfaces.");

        var execution = _runtime.ActiveExecution ?? throw new InvalidOperationException("Runtime has no active execution.");
        var prepared = new PreparedOutputMappingSurface[mapped.Length];
        var scaleX = (double)_format.Width / canvas.Width;
        var scaleY = (double)_format.Height / canvas.Height;
        var knownSources = execution.PreparedExecution.Bindings
            .Where(binding => binding.MediaSourceId is not null)
            .Select(binding => binding.MediaSourceId!.Value.ToString())
            // The two already-registered virtual sources remain available even if
            // neither currently owns the Preview or Program route.
            .Concat(_virtualMedia.Sources.Select(source => source.SourceId.ToString()))
            .Concat(_externalSourcePipelines.Keys.Select(s => s.ToString()))
            .Distinct(StringComparer.Ordinal).ToArray();
        var activeBuses = execution.PreparedExecution.Bindings
            .Where(binding => binding.OutputRoleId is "program" or "aux")
            .Select(binding => binding.OutputRoleId!).ToArray();

        for (var i = 0; i < mapped.Length; i++)
        {
            var authored = mapped[i];
            var input = authored.Input ?? throw new ArgumentException("Canvas input is missing.");
            if ((input.SourceId is null) == (input.BusRoleId is null))
                throw new ArgumentException("Each surface requires one source or role.");
            if (input.SourceId is not null &&
                !knownSources.Contains(input.SourceId, StringComparer.Ordinal))
                throw new ArgumentException("Mapped source is not in the committed Runtime source set.");
            if (input.BusRoleId is not null && !activeBuses.Contains(input.BusRoleId, StringComparer.Ordinal))
                throw new ArgumentException("Mapped output bus is not committed.");
            if (authored.Crop is null || authored.Destination is null)
                throw new ArgumentException("Surface crop/quad is missing.");

            GpuOutputPoint Point(OutputCanvasPoint p) => new(p.X * scaleX, p.Y * scaleY);
            var blend = authored.EdgeBlend;
            var curve = blend?.Curve == "linear" ? GpuOutputBlendCurve.Linear : GpuOutputBlendCurve.SmoothStep;
            if (blend is not null && blend.Curve is not ("linear" or "smoothstep"))
                throw new ArgumentException("Unsupported edge blend curve.");
            var spec = new GpuOutputMappingSurface(
                default, _format,
                authored.Crop.X, authored.Crop.Y,
                authored.Crop.Width, authored.Crop.Height,
                Point(authored.Destination.TopLeft), Point(authored.Destination.TopRight),
                Point(authored.Destination.BottomRight), Point(authored.Destination.BottomLeft),
                blend is null ? GpuOutputEdgeBlend.None :
                    new(blend.Left, blend.Top, blend.Right, blend.Bottom, curve),
                authored.ZOrder, authored.SurfaceId);
            spec.Validate();
            prepared[i] = new PreparedOutputMappingSurface(
                authored.SurfaceId, input.SourceId, input.BusRoleId, spec);
        }

        var identity = prepared.Length == 1 &&
            prepared[0].BusRoleId == "program" &&
            prepared[0].Geometry is { } item &&
            item.CropX == 0 && item.CropY == 0 &&
            item.CropWidth == 1 && item.CropHeight == 1 &&
            item.TopLeft == new GpuOutputPoint(0, 0) &&
            item.TopRight == new GpuOutputPoint(_format.Width, 0) &&
            item.BottomRight == new GpuOutputPoint(_format.Width, _format.Height) &&
            item.BottomLeft == new GpuOutputPoint(0, _format.Height) &&
            item.Blend.Left == 0 && item.Blend.Top == 0 &&
            item.Blend.Right == 0 && item.Blend.Bottom == 0;

        return new PreparedOutputMappingPlan(config.ConfigurationId, config.EditRevision,
            expectedExecutionRevision, target.ProviderId, target.TargetId,
            checked((int)_format.Width), checked((int)_format.Height), prepared, identity);
    }

    // Called only under the existing Program boundary execution lock.
    private void AdmitOutputMappingBoundaryUnsafe()
    {
        if (!_pendingOutputMappingDisable && _pendingOutputMapping is null)
            return;
        if (_pendingOutputMapping is { } pending &&
            (_runtime.ActiveExecution is null ||
             _runtime.State.ExecutionRevision.Value != pending.ExecutionRevision ||
             _programSinkId?.ToString() != pending.TargetId))
        {
            _pendingOutputMapping = null;
            _pendingOutputMappingDisable = false;
            _outputMappingError = "mapping.revision_changed_at_boundary";
            _outputMappingState = _activeOutputMapping is null ? "REJECTED" : "ACTIVE_REJECTED_UPDATE";
            Observe("output.mapping.rejected:revision_changed_at_boundary");
            return;
        }
        _activeOutputMapping = _pendingOutputMapping;
        _pendingOutputMapping = null;
        _pendingOutputMappingDisable = false;
        _outputMappingSource = null;
        _outputMappingSequence = null;
        _outputMappingError = null;
        _outputMappingState = _activeOutputMapping is null ? "UNMAPPED" : "ACTIVE";
        Observe("output.mapping.boundary:" + _outputMappingState);
    }

    private void InvalidateOutputMappingUnsafe(string code)
    {
        _preparedOutputMapping = null;
        _pendingOutputMapping = null;
        _pendingOutputMappingDisable = false;
        _activeOutputMapping = null;
        _outputMappingError = code;
        _outputMappingState = "STALE_EXECUTION";
        _outputMappingSource = null;
        _outputMappingSequence = null;
        Observe("output.mapping.invalidated:" + code);
    }

    private bool NeedsMappedSource(MediaSourceId id) =>
        _activeOutputMapping?.Surfaces.Any(surface => surface.SourceId == id.ToString()) == true ||
        _activeOutputMapping?.Surfaces.Any(surface =>
            surface.BusRoleId == "aux" && _runtime.ActiveExecution?.PreparedExecution.Bindings
                .Any(binding => binding.OutputRoleId == "aux" && binding.MediaSourceId == id) == true) == true;

    // Render post-composite without ever mutating the original Program frame.
    // Runtime consumers receive the same mapped frame and its qualified pixels.
    private GpuFrame? TryRenderMappedOutput(
        GpuFrame program, IReadOnlyDictionary<MediaSourceId, GpuFrame> sourceFrames)
    {
        var plan = _activeOutputMapping;
        if (plan is null) return null;
        if (plan.ExecutionRevision != _runtime.State.ExecutionRevision.Value ||
            _programSinkId?.ToString() != plan.TargetId)
        {
            InvalidateOutputMappingUnsafe("mapping.revision_changed");
            return null;
        }
        _outputMappingSource = program.Descriptor.SourceId.ToString();
        _outputMappingSequence = program.Descriptor.Timing.SequenceNumber;
        if (plan.Identity) return null;

        var inputs = new GpuOutputMappingInput[plan.Surfaces.Count];
        for (var i = 0; i < inputs.Length; i++)
        {
            var item = plan.Surfaces[i];
            GpuFrame? frame = null;
            if (item.BusRoleId == "program") frame = program;
            else
            {
                var sourceId = item.SourceId;
                if (item.BusRoleId == "aux")
                    sourceId = _runtime.ActiveExecution?.PreparedExecution.Bindings
                        .SingleOrDefault(binding => binding.OutputRoleId == "aux")
                        ?.MediaSourceId?.ToString();
                if (sourceId is not null)
                    frame = sourceFrames.Values.FirstOrDefault(candidate =>
                        candidate.Descriptor.SourceId.ToString() == sourceId);
            }
            if (frame is null)
            {
                InvalidateOutputMappingUnsafe("mapping.source_unavailable");
                return null;
            }
            inputs[i] = new GpuOutputMappingInput(frame,
                item.Geometry with { SourceId = frame.SurfaceId });
        }

        var result = _gpu.MapOutput(program, inputs);
        if (result.Succeeded) return result.Frame;
        InvalidateOutputMappingUnsafe(result.Failure?.Code ?? "mapping.render_failed");
        return null;
    }
}
