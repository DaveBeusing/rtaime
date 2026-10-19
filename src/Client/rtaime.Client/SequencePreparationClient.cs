// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.Client;

public interface ISequencePreparationTransport
{
    ValueTask<SequencePreparationContext> GetSequencePreparationContextAsync(
        CancellationToken cancellationToken = default);
    ValueTask<SequencePreparationStatus> PrepareSequenceAsync(
        SequencePreparationRequest request, CancellationToken cancellationToken = default);
    ValueTask<SequencePreparationStatus> GetSequencePreparationAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default);
    ValueTask<SequencePreparationStatus> CancelSequencePreparationAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// Prepares bounded decoder resources for an exact saved sequence revision.
/// No Play, Take, Program route or Preview picture is authorized by this API.
/// </summary>
public sealed class SequencePreparationClient(ISequencePreparationTransport transport)
{
    public ValueTask<SequencePreparationContext> ContextAsync(
        CancellationToken cancellationToken = default) =>
        transport.GetSequencePreparationContextAsync(cancellationToken);

    public ValueTask<SequencePreparationStatus> PrepareAsync(
        SequencePreparationRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        return transport.PrepareSequenceAsync(request, cancellationToken);
    }

    public ValueTask<SequencePreparationStatus> GetAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        return transport.GetSequencePreparationAsync(reference, cancellationToken);
    }

    public ValueTask<SequencePreparationStatus> CancelAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        return transport.CancelSequencePreparationAsync(reference, cancellationToken);
    }
}
