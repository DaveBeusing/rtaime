// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

// Narrow adapter around the sole production Client -> ControlHost route.
// Tests can exercise the exact UI transaction semantics without a second authority.
internal interface IOutputCanvasGateway
{
    ValueTask<OutputCanvasSnapshot> GetAsync(CancellationToken cancellationToken = default);
    ValueTask<OutputCanvasActivationResult> EvidenceAsync(CancellationToken cancellationToken = default);
    ValueTask<OutputCanvasValidation> ValidateAsync(OutputCanvasConfiguration draft, CancellationToken cancellationToken = default);
    ValueTask<OutputCanvasSaveResult> SaveAsync(OutputCanvasSaveRequest request, CancellationToken cancellationToken = default);
    ValueTask<OutputCanvasActivationResult> ActivateAsync(OutputCanvasActivationRequest request, CancellationToken cancellationToken = default);
}

internal sealed class OutputCanvasGateway(OperatorControlClient client) : IOutputCanvasGateway
{
    public ValueTask<OutputCanvasSnapshot> GetAsync(CancellationToken token = default) =>
        client.GetOutputCanvasAsync(token);
    public ValueTask<OutputCanvasActivationResult> EvidenceAsync(CancellationToken token = default) =>
        client.GetOutputCanvasEvidenceAsync(token);
    public ValueTask<OutputCanvasValidation> ValidateAsync(OutputCanvasConfiguration draft, CancellationToken token = default) =>
        client.ValidateOutputCanvasAsync(draft, token);
    public ValueTask<OutputCanvasSaveResult> SaveAsync(OutputCanvasSaveRequest request, CancellationToken token = default) =>
        client.SaveOutputCanvasAsync(request, token);
    public ValueTask<OutputCanvasActivationResult> ActivateAsync(OutputCanvasActivationRequest request, CancellationToken token = default) =>
        client.ActivateOutputCanvasAsync(request, token);
}
