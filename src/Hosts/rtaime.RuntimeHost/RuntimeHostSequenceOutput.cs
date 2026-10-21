// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// ControlHost-only sequence Preview/Take output management over the already
/// established Runtime IPC channel. No client may invoke a Program mutation
/// directly against the RuntimeHost.
/// </summary>
public sealed partial class RuntimeHostIpcServer
{
    private WireEnvelope DispatchSequenceOutput(
        WireEnvelope request, RuntimeProgramExecutionService runtime)
    {
        try
        {
            var deck = _mediaDeckAccessor()
                ?? throw new InvalidOperationException("Native media owner is unavailable.");
            if (_committedAuthorityStateId is not { } stateId ||
                _committedAuthorityRevision is not { } authority)
                throw new InvalidOperationException("Runtime has no committed Control authority.");

            if (request.MessageType == "runtime.sequence.output.get")
            {
                var reference = SequenceJson.Read<SequencePreparationReference>(
                    request.Payload.GetRawText(), 16 * 1024);
                reference.Validate();
                return Success(request, request.MessageType + ".response",
                    deck.GetSequenceOutput(reference, _hostInstanceId));
            }

            var command = SequenceJson.Read<SequenceOutputCommand>(
                request.Payload.GetRawText(), 16 * 1024);
            command.Validate();
            if (command.Preparation.RuntimeHostInstanceId != _hostInstanceId)
                return Error(request, "runtime.sequence.output.host_changed",
                    "Native sequence output belongs to another RuntimeHost lifetime.");
            var sourceId = new MediaSourceId(Identity.Parse(command.PreviewSourceId));
            if (request.MessageType == "runtime.sequence.output.preview")
            {
                if (authority.Value != command.ExpectedProductionRevision)
                    return Error(request, "runtime.sequence.output.authority_changed",
                        "Committed Preview authority no longer matches the native sequence request.");
                deck.BindSequencePreviewOutput(command.Preparation,
                    sourceId, stateId, authority, runtime);
            }
            else if (request.MessageType == "runtime.sequence.output.arm_take")
            {
                if (authority.Value != command.ExpectedProductionRevision)
                    return Error(request, "runtime.sequence.output.authority_changed",
                        "Sequence Take can only arm the current committed Preview revision.");
                _ = deck.ArmSequenceTake(command, stateId, authority, runtime, _hostInstanceId);
            }
            else if (request.MessageType == "runtime.sequence.output.disarm_take")
            {
                deck.DisarmSequenceTake(command);
            }
            else if (request.MessageType == "runtime.sequence.output.take")
            {
                if (authority.Value != checked(command.ExpectedProductionRevision + 1))
                    return Error(request, "runtime.sequence.output.take_stale",
                        "Runtime has not committed the exact expected Program successor.");
                deck.AuthorizeSequenceProgram(command.Preparation,
                    sourceId, stateId, new Revision(command.ExpectedProductionRevision),
                    authority, runtime);
            }
            else
                return Error(request, "runtime.sequence.output.operation_unknown",
                    "Unsupported native sequence output operation.");

            AdvanceStateVersion();
            return Success(request, request.MessageType + ".response",
                deck.GetSequenceOutput(command.Preparation, _hostInstanceId));
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or
            InvalidOperationException or NotSupportedException or OverflowException)
        {
            return Error(request, "runtime.sequence.output.rejected",
                "Sequence output operation was rejected without an implicit Take: " + error.GetType().Name);
        }
    }
}
