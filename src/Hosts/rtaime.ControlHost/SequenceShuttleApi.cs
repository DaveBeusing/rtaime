// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.ControlHost;

/// <summary>
/// A prepared sequence can change native transport position only while the
/// current committed production, show authoring and catalogue authority are
/// still identical to those validated at Prepare. This path cannot Take.
/// </summary>
public sealed partial class ControlHostIpcServer
{
    private async ValueTask<WireEnvelope> RunSequenceShuttleAsync(
        WireEnvelope message, CancellationToken cancellationToken)
    {
        await _sequencePreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mutation = message.MessageType == "control.sequence.shuttle.command";
            SequenceShuttleRequest? command = null;
            SequencePreparationReference reference;
            if (mutation)
            {
                command = SequenceJson.Read<SequenceShuttleRequest>(
                    message.Payload.GetRawText(), 16 * 1024);
                command.Validate();
                reference = command.Preparation;
            }
            else
            {
                reference = SequenceJson.Read<SequencePreparationReference>(
                    message.Payload.GetRawText(), 16 * 1024);
                reference.Validate();
            }

            if (_preparedSequence is not { } binding || binding.Reference != reference ||
                !_runtimeTransport.IsConnected || _showProjectStore is null ||
                _mediaAssetCatalog is null)
                return Error(message, "sequence.shuttle.unavailable",
                    "A matching current native sequence preparation and connected hosts are required.");

            if (_runtimeTransport.HostInstanceId != reference.RuntimeHostInstanceId)
            {
                _preparedSequence = null;
                return Error(message, "sequence.shuttle.runtime_changed",
                    "RuntimeHost changed; previous decoder identity and transport are invalid.");
            }

            var control = _controlAccessor();
            var baseline = control is null ? null : _outputCanvasBaseline ?? control.Specification;
            var saved = baseline is null ? null : await _showProjectStore.LoadSequencesAsync(
                baseline, cancellationToken).ConfigureAwait(false);
            var catalogue = await _mediaAssetCatalog.GetSnapshotAsync(
                refreshAvailability: false, cancellationToken).ConfigureAwait(false);
            if (control is null || !control.HasAuthoritativeState ||
                control.State.ProductionId.Value != binding.AuthorityStateId ||
                control.State.Revision != binding.AuthorityRevision ||
                saved?.Revision != binding.Reference.AuthoringRevision ||
                catalogue.Revision != binding.CatalogueRevision)
            {
                // Do not silently keep a cached transport after its authoring,
                // catalogue or production revision changes. Runtime retains its
                // owner reservation until native cancellation is confirmed.
                var closed = await _runtimeTransport.CancelSequencePreparationAsync(
                    binding.Reference, cancellationToken).ConfigureAwait(false);
                if (closed.State != "CANCELED")
                    return Error(message, "sequence.shuttle.cleanup_unconfirmed",
                        "The old native preparation cannot be reused until cleanup is confirmed.");
                _preparedSequence = null;
                return Error(message, "sequence.shuttle.authority_changed",
                    "Saved sequence, catalogue or committed production changed; prepare again.");
            }

            var result = mutation
                ? await _runtimeTransport.ApplySequenceShuttleAsync(command!, cancellationToken)
                    .ConfigureAwait(false)
                : await _runtimeTransport.GetSequenceShuttleAsync(reference, cancellationToken)
                    .ConfigureAwait(false);
            result.Validate();
            if (result.Preparation != binding.Reference)
                return Error(message, "sequence.shuttle.owner_mismatch",
                    "Runtime returned a shuttle observation for another prepared owner.");
            return Success(message, message.MessageType + ".response", result);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            InvalidDataException or IOException or NotSupportedException or TimeoutException or
            OverflowException)
        {
            return Error(message, "sequence.shuttle.rejected",
                "Prepared sequence shuttle rejected without Program mutation: " + error.GetType().Name);
        }
        finally { _sequencePreparationGate.Release(); }
    }
}
