// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.RuntimeHost;

/// <summary>
/// The existing RuntimeHost owns this management-only native shuttle. A
/// successful response is not a Preview picture, production commit or Take.
/// </summary>
public sealed partial class RuntimeHostIpcServer
{
    private WireEnvelope DispatchSequenceShuttle(
        WireEnvelope message, V1RuntimeHostService runtime, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_mediaDeckAccessor() is not { } deck ||
                _committedAuthorityStateId is not { } stateId ||
                _committedAuthorityRevision is not { } revision)
                return Error(message, "runtime.sequence.shuttle.unavailable",
                    "Native media and current committed production authority are required.");

            SequenceShuttleObservation response;
            if (message.MessageType == "runtime.sequence.shuttle.get")
            {
                var reference = SequenceJson.Read<SequencePreparationReference>(
                    message.Payload.GetRawText(), 16 * 1024);
                reference.Validate();
                response = deck.GetSequenceShuttle(reference, _hostInstanceId,
                    stateId, revision, runtime.Snapshot.NextSequenceNumber);
            }
            else if (message.MessageType == "runtime.sequence.shuttle.command")
            {
                var request = SequenceJson.Read<SequenceShuttleRequest>(
                    message.Payload.GetRawText(), 16 * 1024);
                request.Validate();
                response = deck.ApplySequenceShuttle(request, _hostInstanceId,
                    stateId, revision, () => runtime.Snapshot.NextSequenceNumber);
                AdvanceStateVersion();
            }
            else
                return Error(message, "runtime.sequence.shuttle.operation_unknown",
                    "Unrecognized prepared sequence shuttle operation.");

            return Success(message, message.MessageType + ".response", response);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or
            InvalidOperationException or IOException or TimeoutException or OverflowException or JsonException)
        {
            return Error(message, "runtime.sequence.shuttle.rejected",
                "Prepared sequence shuttle was rejected: " + error.GetType().Name);
        }
    }
}
