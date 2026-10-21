// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.Client;

public interface ISequenceOutputTransport
{
    ValueTask<SequenceOutputObservation> ActivatePreviewAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default);
    ValueTask<SequenceOutputObservation> TakeProgramAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default);
    ValueTask<SequenceOutputObservation> GetOutputAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default);
    ValueTask<SequenceOutputObservation> RecoverTakeAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Operator-to-Control sequence media authority. A lost Take response is
/// resolved by Get/Recover, never by repeating Take with a new identity.
/// </summary>
public sealed class SequenceOutputClient(ISequenceOutputTransport transport)
{
    public ValueTask<SequenceOutputObservation> ActivatePreviewAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default)
    {
        command.Validate();
        return transport.ActivatePreviewAsync(command, cancellationToken);
    }

    public ValueTask<SequenceOutputObservation> TakeAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default)
    {
        command.Validate();
        return transport.TakeProgramAsync(command, cancellationToken);
    }

    public ValueTask<SequenceOutputObservation> GetAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        return transport.GetOutputAsync(reference, cancellationToken);
    }

    public ValueTask<SequenceOutputObservation> RecoverAsync(
        SequenceOutputCommand command, CancellationToken cancellationToken = default)
    {
        command.Validate();
        return transport.RecoverTakeAsync(command, cancellationToken);
    }
}
