// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Core;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

/// <summary>
/// Dedicated management-only staging protocol. Paths are supplied only by
/// ControlHost after catalogue authorization, never by Operator clients.
/// No Program command or output sink is reachable through these messages.
/// </summary>
public sealed partial class RuntimeHostIpcServer
{
    private sealed record WireSequenceOwner(
        string SourceId, string AssetId, string LocalPath, string FingerprintSha256,
        long SourceInTicks, long TimelineStartTicks);

    private sealed record WireSequencePrepare(
        string Version, string ExpectedRuntimeHostInstanceId, Guid SequenceId,
        ulong AuthoringRevision, long DurationTicks, string ProgramFrameRate,
        WirePreparedExecution PreparedExecution, WireSequenceOwner[] Sources);

    private WireEnvelope DispatchSequencePreparation(
        WireEnvelope message, RuntimeProgramExecutionService runtime, CancellationToken cancellationToken)
    {
        try
        {
            var deck = _mediaDeckAccessor();
            if (deck is null)
                return Error(message, "runtime.sequence.unavailable", "The local media decoder service is not available.");

            if (message.MessageType == "runtime.sequence.preparation.prepare")
            {
                var wire = message.Payload.Deserialize<WireSequencePrepare>(Wire.JsonOptions)
                    ?? throw new InvalidDataException("Sequence preparation payload is missing.");
                if (wire.Version != SequencePreparationContract.Version ||
                    wire.SequenceId == Guid.Empty || wire.AuthoringRevision == 0 ||
                    wire.ExpectedRuntimeHostInstanceId != _hostInstanceId ||
                    wire.Sources is null || wire.Sources.Length is < 1 or > SequencePreparationContract.MaximumNativeOwners ||
                    wire.DurationTicks is <= 0 or > SequenceContract.MaximumTime)
                    throw new InvalidDataException("Unsupported, stale or unbounded sequence preparation request.");

                var execution = FromWire(wire.PreparedExecution);
                if (_committedAuthorityStateId is not { } state ||
                    _committedAuthorityRevision is not { } revision ||
                    state != execution.AuthoritySnapshot.StateId ||
                    revision != execution.AuthoritySnapshot.Revision)
                    return Error(message, "runtime.sequence.authority_changed",
                        "Prepared sequence must match currently committed production authority; synchronize and retry.");

                cancellationToken.ThrowIfCancellationRequested();
                var map = SequenceRuntimePositionMap.Prepare(wire.SequenceId, wire.AuthoringRevision,
                    FrameRate.Parse(wire.ProgramFrameRate), wire.DurationTicks,
                    runtime.Snapshot.NextSequenceNumber, execution.PlanGeneration.Value);
                var sources = wire.Sources.Select(source => new SequenceMediaOwnerRequest(
                    new MediaSourceId(Identity.Parse(source.SourceId)),
                    new MediaAssetId(Identity.Parse(source.AssetId)),
                    source.LocalPath, source.FingerprintSha256,
                    source.SourceInTicks, source.TimelineStartTicks)).ToArray();
                var preparation = deck.PrepareSequenceMedia(execution, map, sources, cancellationToken);
                if (!preparation.Prepared)
                {
                    var error = preparation.Failure;
                    var rejected = new SequencePreparationStatus(
                        SequencePreparationContract.Version, wire.SequenceId,
                        wire.AuthoringRevision, _hostInstanceId,
                        execution.PreparedExecutionId.ToString(), execution.PlanGeneration.Value,
                        0, "REJECTED", error?.Code,
                        "Native sequence preparation rejected; resource limits, identity or decoder readiness require inspection.");
                    return Success(message, message.MessageType + ".response", rejected);
                }
                var reference = new SequencePreparationReference(
                    SequencePreparationContract.Version, wire.SequenceId,
                    wire.AuthoringRevision, _hostInstanceId, execution.PreparedExecutionId.ToString(),
                    execution.PlanGeneration.Value);
                var status = deck.GetPreparedSequence(reference, _hostInstanceId);
                AdvanceStateVersion();
                return Success(message, message.MessageType + ".response", status);
            }

            var owner = SequenceJson.Read<SequencePreparationReference>(
                message.Payload.GetRawText(), 16 * 1024);
            owner.Validate();
            if (owner.RuntimeHostInstanceId != _hostInstanceId)
                return Error(message, "runtime.sequence.host_changed",
                    "Sequence preparation belongs to another RuntimeHost lifetime.");

            var observation = message.MessageType == "runtime.sequence.preparation.cancel"
                ? deck.CancelPreparedSequence(owner, _hostInstanceId)
                : deck.GetPreparedSequence(owner, _hostInstanceId);
            if (message.MessageType == "runtime.sequence.preparation.cancel")
                AdvanceStateVersion();
            return Success(message, message.MessageType + ".response", observation);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or
            InvalidOperationException or IOException or TimeoutException or OverflowException or JsonException)
        {
            return Error(message, "runtime.sequence.preparation_rejected",
                "Sequence preparation request was rejected: " + error.GetType().Name);
        }
    }
}
