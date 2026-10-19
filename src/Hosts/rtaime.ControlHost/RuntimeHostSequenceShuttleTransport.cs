// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.ControlHost;

/// <summary>
/// Serializes only a prepared-owner identity and an optimistic shuttle command.
/// Native paths, decoded media and production mutation authority stay in Runtime.
/// </summary>
public sealed partial class NamedPipeRuntimeHostTransport
{
    public async ValueTask<SequenceShuttleObservation> GetSequenceShuttleAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        var reply = await ExchangeAsync("runtime.sequence.shuttle.get", reference, cancellationToken)
            .ConfigureAwait(false);
        return ValidateSequenceShuttleObservation(reference,
            SequenceJson.Read<SequenceShuttleObservation>(
                reply.Payload.GetRawText(), 16 * 1024));
    }

    public async ValueTask<SequenceShuttleObservation> ApplySequenceShuttleAsync(
        SequenceShuttleRequest command, CancellationToken cancellationToken = default)
    {
        command.Validate();
        var reply = await ExchangeAsync("runtime.sequence.shuttle.command", command, cancellationToken)
            .ConfigureAwait(false);
        return ValidateSequenceShuttleObservation(command.Preparation,
            SequenceJson.Read<SequenceShuttleObservation>(
                reply.Payload.GetRawText(), 16 * 1024));
    }

    internal static SequenceShuttleObservation ValidateSequenceShuttleObservation(
        SequencePreparationReference reference, SequenceShuttleObservation observed)
    {
        observed.Validate();
        if (observed.Preparation != reference)
            throw new InvalidDataException(
                "Runtime sequence shuttle returned a different prepared sequence or host.");
        return observed;
    }
}
