// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Control;
using rtaime.Control.Contracts;
namespace rtaime.ControlHost;

public sealed partial class ControlHostIpcServer
{
    private readonly ProductionSpecification? _outputCanvasBaseline;

    private async ValueTask<WireEnvelope> RunOutputCanvasAsync(WireEnvelope request, CancellationToken cancellationToken)
    {
        if (_showProjectStore is null) return Error(request, "canvas.unavailable", "Durable show persistence is unavailable.");
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var control = _controlAccessor() ?? throw new InvalidOperationException("Control authority is unavailable.");
            var baseline = _outputCanvasBaseline ?? control.Specification;
            var project = await _showProjectStore.LoadAsync(baseline, cancellationToken).ConfigureAwait(false);
            var persisted = project.OutputCanvasJson is null ? null : ShowProjectPersistenceStore.ReadOutputCanvasDocument(project.OutputCanvasJson).Saved;
            RuntimeRemoteSnapshot? runtime = null;
            try { if (_runtimeTransport.IsConnected) runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException) { }
            var roles = control.State.OutputRoles;
            var targets = (runtime?.OutputRoles ?? []).Where(r => roles.Any(a =>
                    a.RoleId.ToString() == r.RoleId && a.SourceId.Value == r.SourceId.Value))
                .Select(r => new OutputCanvasTarget(r.ProviderId.ToString(), r.TargetId.ToString(), "production",
                    checked((int)r.Format.Width), checked((int)r.Format.Height),
                    "Configured Runtime sink; canvas rendering and physical qualification are not established.", r.RoleId)).ToArray();
            var legacy = persisted ?? (runtime is null
                ? throw new InvalidOperationException("Runtime format is required to reconstruct legacy identity canvases.")
                : OutputCanvasLegacy.Reconstruct(control.State.ProductionId, roles,
                    checked((int)runtime.Format.Width), checked((int)runtime.Format.Height), targets));
            var saved = legacy;
            switch (request.MessageType)
            {
                case "control.output.canvas.get":
                    OutputCanvasActivationResult? evidence = null;
                    if (runtime is not null)
                    {
                        try { evidence = await _runtimeTransport.GetOutputMappingSnapshotAsync(cancellationToken).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or TimeoutException) { }
                    }
                    return Success(request, request.MessageType + ".response",
                        new OutputCanvasSnapshot(saved, saved.EditRevision,
                            evidence?.ActiveRenderRevision, targets,
                            evidence?.ActivationState ?? "RENDERER_REQUIRED"));
                case "control.output.canvas.evidence.get":
                    if (runtime is null)
                        return Error(request, "canvas.runtime_unavailable", "Live mapping evidence requires a connected RuntimeHost.");
                    var appliedEvidence = await _runtimeTransport.GetOutputMappingSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    return Success(request, request.MessageType + ".response", appliedEvidence);
                case "control.output.canvas.activate":
                    var command = OutputCanvasJson.Read<OutputCanvasActivationRequest>(request.Payload.GetRawText());
                    if (command.ConfigurationId != saved.ConfigurationId ||
                        command.ExpectedSavedRevision != saved.EditRevision)
                        return Error(request, "canvas.revision_conflict",
                            "Activation requires the exact currently saved canvas configuration revision.");
                    if (runtime is null || !_runtimeTransport.IsConnected)
                        return Error(request, "canvas.runtime_unavailable", "RuntimeHost must be connected for mapping activation.");
                    if (command.Enabled)
                    {
                        var qualified = OutputCanvasModel.Validate(saved,
                            control.Specification.Sources.Select(source => source.SourceId.ToString()).ToArray(),
                            roles.Select(role => role.RoleId.ToString()).ToArray(), targets);
                        if (!qualified.IsValid)
                            return Error(request, "canvas.activation_invalid",
                                "Saved canvas model or provider capabilities no longer match current production.");
                    }
                    var active = await _runtimeTransport.ActivateOutputMappingAsync(
                        new OutputCanvasRuntimeRequest(saved, runtime.Runtime.ExecutionRevision.Value,
                            runtime.HostInstanceId, command.Enabled), cancellationToken).ConfigureAwait(false);
                    if (active.ActivationState == "PENDING_BOUNDARY")
                        NotifyObservableStateChanged();
                    return Success(request, request.MessageType + ".response", active);
                case "control.output.canvas.validate":
                    var draft = OutputCanvasJson.Read<OutputCanvasConfiguration>(request.Payload.GetRawText());
                    var validation = OutputCanvasModel.Validate(draft,
                        control.Specification.Sources.Select(s => s.SourceId.ToString()).ToArray(),
                        roles.Select(r => r.RoleId.ToString()).ToArray(), targets);
                    var issues = validation.Issues.ToList();
                    if (draft.ConfigurationId != saved.ConfigurationId || saved.EditRevision == ulong.MaxValue ||
                        draft.EditRevision != saved.EditRevision + 1)
                        issues.Add(new("canvas.revision_conflict", "editRevision", "Draft must identify this configuration and the next saved revision."));
                    return Success(request, request.MessageType + ".response", validation with { Issues = issues.ToArray() });
                case "control.output.canvas.save":
                    var edit = OutputCanvasJson.Read<OutputCanvasSaveRequest>(request.Payload.GetRawText());
                    var result = await _showProjectStore.SaveOutputCanvasAsync(baseline, legacy, edit, targets, cancellationToken).ConfigureAwait(false);
                    if (result.Saved && !result.Replayed) NotifyObservableStateChanged();
                    return Success(request, request.MessageType + ".response", result);
                default: return Error(request, "ipc.message.unknown", "Unknown canvas operation.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or JsonException or OverflowException)
        { return Error(request, "canvas.rejected", exception.Message); }
        finally { _mutationGate.Release(); }
    }
}
