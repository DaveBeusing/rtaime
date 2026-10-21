// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.ControlHost;

/// <summary>
/// ControlHost owns explicit native Preview authorization and optimistic
/// Preview -> Program Take. Neither native PREPARED nor shuttle PLAYING
/// implicitly selects a production source or commits Program.
/// </summary>
public sealed partial class ControlHostIpcServer
{
    private async ValueTask<WireEnvelope> RunSequenceOutputAsync(
        WireEnvelope message, CancellationToken cancellationToken)
    {
        await _sequencePreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var isGet = message.MessageType == "control.sequence.output.get";
            var isRecover = message.MessageType == "control.sequence.output.recover";
            var isPreview = message.MessageType == "control.sequence.output.preview";
            var isTake = message.MessageType == "control.sequence.output.take";
            SequenceOutputCommand? command = null;
            SequencePreparationReference reference;
            if (isGet)
            {
                reference = SequenceJson.Read<SequencePreparationReference>(
                    message.Payload.GetRawText(), 16 * 1024);
                reference.Validate();
            }
            else
            {
                command = SequenceJson.Read<SequenceOutputCommand>(
                    message.Payload.GetRawText(), 16 * 1024);
                command.Validate();
                reference = command.Preparation;
            }

            if (_preparedSequence is not { } binding || binding.Reference != reference ||
                !_runtimeTransport.IsConnected || _showProjectStore is null ||
                _mediaAssetCatalog is null)
                return Error(message, "sequence.output.unavailable",
                    "An exact prepared native sequence and connected Control/Runtime hosts are required.");
            var control = _controlAccessor();
            if (control is null || !control.HasAuthoritativeState ||
                control.State.ProductionId.Value != binding.AuthorityStateId ||
                _runtimeTransport.HostInstanceId != reference.RuntimeHostInstanceId)
                return Error(message, "sequence.output.authority_unavailable",
                    "Committed authority or RuntimeHost epoch changed; reprepare the native sequence.");
            var authoritative = control.State;
            var baseline = _outputCanvasBaseline ?? control.Specification;
            var saved = await _showProjectStore.LoadSequencesAsync(
                baseline, cancellationToken).ConfigureAwait(false);
            var catalogue = await _mediaAssetCatalog.GetSnapshotAsync(
                refreshAvailability: false, cancellationToken).ConfigureAwait(false);
            if (saved.Revision != reference.AuthoringRevision ||
                catalogue.Revision != binding.CatalogueRevision)
                return Error(message, "sequence.output.source_stale",
                    "A saved sequence or catalogue revision changed; old frames cannot be output.");

            if (isGet)
            {
                if (authoritative.Revision != binding.AuthorityRevision)
                    return Error(message, "sequence.output.take_recovery_required",
                        "Committed production changed; inspect explicit Take recovery before another sequence command.");
                var inspected = await _runtimeTransport.GetSequenceOutputAsync(
                    reference, cancellationToken).ConfigureAwait(false);
                if (inspected.ProductionRevision != authoritative.Revision.Value)
                    return Error(message, "sequence.output.runtime_revision_mismatch",
                        "Runtime output observation is not bound to current Control authority.");
                return Success(message, message.MessageType + ".response", inspected);
            }

            var previewSource = new ProductionSourceId(Identity.Parse(command!.PreviewSourceId));
            if (isRecover)
            {
                // This path never executes a Program mutation. An uncertain
                // response can only complete source rebinding after Control
                // and Runtime have already committed the *same* successor.
                if (authoritative.Revision.Value != checked(command.ExpectedProductionRevision + 1) ||
                    authoritative.Routing.ProgramSourceId != previewSource ||
                    binding.AuthorityRevision.Value != command.ExpectedProductionRevision &&
                    binding.AuthorityRevision != authoritative.Revision)
                    return Error(message, "sequence.output.recovery_not_committed",
                        "No exact committed successor exists; a Program Take must not be replayed.");
                var runtime = await _runtimeTransport.GetSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (runtime.AuthorityStateId != binding.AuthorityStateId ||
                    runtime.AuthorityRevision != authoritative.Revision ||
                    runtime.HostInstanceId != reference.RuntimeHostInstanceId)
                    return Error(message, "sequence.output.recovery_runtime_mismatch",
                        "Runtime has no matching committed successor; do not replay Take.");
                var recovered = await _runtimeTransport.ConfirmSequenceTakeAsync(
                    command, cancellationToken).ConfigureAwait(false);
                _preparedSequence = binding with { AuthorityRevision = authoritative.Revision };
                NotifyObservableStateChanged();
                return Success(message, message.MessageType + ".response", recovered);
            }

            if (authoritative.Revision.Value != command.ExpectedProductionRevision ||
                binding.AuthorityRevision != authoritative.Revision ||
                authoritative.Routing.PreviewSourceId != previewSource ||
                authoritative.Routing.ProgramSourceId == previewSource ||
                !control.Specification.Sources.Any(s => s.SourceId == previewSource))
                return Error(message, "sequence.output.preview_stale",
                    "The requested explicitly selected Preview source or production revision is invalid.");

            if (isPreview)
            {
                var preview = await _runtimeTransport.BindSequencePreviewAsync(
                    command, cancellationToken).ConfigureAwait(false);
                NotifyObservableStateChanged();
                return Success(message, message.MessageType + ".response", preview);
            }

            if (!isTake)
                return Error(message, "sequence.output.operation_unknown",
                    "Unknown sequence output operation.");
            var staged = await _runtimeTransport.GetSequenceOutputAsync(
                reference, cancellationToken).ConfigureAwait(false);
            if (staged.PreviewSourceId != command.PreviewSourceId ||
                staged.ProductionRevision != authoritative.Revision.Value ||
                staged.OutputState != "PREVIEW_READY" || staged.LastSourceSlot is null)
                return Error(message, "sequence.output.preview_not_ready",
                    "Real revision-bound Preview pixels and PCM must be presented before Take.");

            // Use precisely the existing Control staged->Runtime prepared/commit
            // ->Control confirmed operation. No separate Program mutator exists.
            var cut = new WireControlCommand(
                ControlContractVersion.Current.ToString(),
                command.TransactionId.ToString(),
                authoritative.ProductionId.ToString(),
                authoritative.Revision.Value,
                command.PreviewSourceId,
                null,
                null);
            var outcome = await MutateAsync(
                InternalRequest("control.sequence.output.take.commit", cut),
                MutationKind.Cut, cancellationToken).ConfigureAwait(false);
            var failure = ReadMutationFailure(outcome);
            if (failure is not null)
                return Error(message, "sequence.output.take_rejected",
                    "The existing production transaction rejected Take: " + failure.Code);

            var after = _controlAccessor()?.State;
            if (after is null || after.ProductionId.Value != binding.AuthorityStateId ||
                after.Revision.Value != checked(authoritative.Revision.Value + 1) ||
                after.Routing.ProgramSourceId != previewSource)
                return Error(message, "sequence.output.take_confirmation_unknown",
                    "Program mutation response was not a matching successor. Observe and recover; never automatically retake.");

            // A lost promotion response does not replay the already committed
            // Program mutation. Explicit recovery can repeat only this exact
            // idempotent source-owner confirmation.
            var taken = await _runtimeTransport.ConfirmSequenceTakeAsync(
                command, cancellationToken).ConfigureAwait(false);
            _preparedSequence = binding with { AuthorityRevision = after.Revision };
            NotifyObservableStateChanged();
            return Success(message, message.MessageType + ".response", taken);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or
            InvalidOperationException or NotSupportedException or IOException or
            TimeoutException or OverflowException)
        {
            return Error(message, "sequence.output.rejected",
                "Sequence output was rejected or needs explicit reconciliation: " + error.GetType().Name);
        }
        finally { _sequencePreparationGate.Release(); }
    }
}
