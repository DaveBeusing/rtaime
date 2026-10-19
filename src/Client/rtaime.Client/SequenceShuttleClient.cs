// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.Client;

public interface ISequenceShuttleTransport
{
    ValueTask<SequenceShuttleObservation> GetSequenceShuttleAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default);
    ValueTask<SequenceShuttleObservation> ApplySequenceShuttleAsync(
        SequenceShuttleRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes guarded native sequence position commands via ControlHost only.
/// No Preview picture, Program Take or additional media clock is available.
/// </summary>
public sealed class SequenceShuttleClient(ISequenceShuttleTransport transport)
{
    public ValueTask<SequenceShuttleObservation> GetAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default)
    {
        reference.Validate();
        return transport.GetSequenceShuttleAsync(reference, cancellationToken);
    }

    public ValueTask<SequenceShuttleObservation> ApplyAsync(
        SequenceShuttleRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        return transport.ApplySequenceShuttleAsync(request, cancellationToken);
    }
}
