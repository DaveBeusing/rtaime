// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.Core;

namespace rtaime.ControlHost;

/// <summary>
/// Control-authorized Runtime staging only. Local source paths never appear in
/// the client-facing sequence preparation contract.
/// </summary>
public sealed record SequenceRuntimeSource(
    MediaSourceId SourceId, MediaAssetId AssetId, string LocalPath,
    string FingerprintSha256, long SourceInTicks, long TimelineStartTicks);

public sealed record SequenceRuntimePrepareCommand(
    string Version, string ExpectedRuntimeHostInstanceId, Guid SequenceId,
    ulong AuthoringRevision, long DurationTicks, FrameRate ProgramRate,
    PreparedExecutionContract PreparedExecution, IReadOnlyList<SequenceRuntimeSource> Sources);

public sealed partial class NamedPipeRuntimeHostTransport
{
    public async ValueTask<SequencePreparationStatus> PrepareSequenceAsync(
        SequenceRuntimePrepareCommand command, CancellationToken cancellationToken = default)
    {
        if (!SequencePreparationContract.IsSupportedVersion(command.Version) ||
            command.Sources is null || command.Sources.Count < 1 ||
            command.Sources.Count > SequencePreparationContract.MaximumOwnersForVersion(command.Version))
            throw new InvalidDataException("Sequence preparation requires a versioned bounded native owner set.");
        var response = await ExchangeAsync("runtime.sequence.preparation.prepare", new
        {
            command.Version,
            command.ExpectedRuntimeHostInstanceId,
            command.SequenceId,
            command.AuthoringRevision,
            command.DurationTicks,
            ProgramFrameRate = command.ProgramRate.ToString(),
            PreparedExecution = ToWire(command.PreparedExecution),
            Sources = command.Sources.Select(owner => new
            {
                SourceId = owner.SourceId.ToString(),
                AssetId = owner.AssetId.ToString(),
                owner.LocalPath,
                owner.FingerprintSha256,
                owner.SourceInTicks,
                owner.TimelineStartTicks
            }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        var result = response.Payload.Deserialize<SequencePreparationStatus>(Wire.JsonOptions)
            ?? throw new InvalidDataException("Runtime sequence preparation response is missing.");
        result.Validate();
        if (result.SequenceId != command.SequenceId ||
            result.AuthoringRevision != command.AuthoringRevision ||
            result.RuntimeHostInstanceId != command.ExpectedRuntimeHostInstanceId ||
            result.Generation != command.PreparedExecution.PlanGeneration.Value ||
            result.PreparedExecutionId != command.PreparedExecution.PreparedExecutionId.ToString())
            throw new InvalidDataException("Runtime sequence preparation response is stale or misbound.");
        return result;
    }

    public ValueTask<SequencePreparationStatus> GetSequencePreparationAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default) =>
        ExchangeSequencePreparationReferenceAsync(
            "runtime.sequence.preparation.get", reference, cancellationToken);

    public ValueTask<SequencePreparationStatus> CancelSequencePreparationAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default) =>
        ExchangeSequencePreparationReferenceAsync(
            "runtime.sequence.preparation.cancel", reference, cancellationToken);

    private async ValueTask<SequencePreparationStatus> ExchangeSequencePreparationReferenceAsync(
        string operation, SequencePreparationReference reference, CancellationToken cancellationToken)
    {
        reference.Validate();
        var response = await ExchangeAsync(operation, reference, cancellationToken).ConfigureAwait(false);
        var result = response.Payload.Deserialize<SequencePreparationStatus>(Wire.JsonOptions)
            ?? throw new InvalidDataException("Runtime sequence preparation evidence is missing.");
        result.Validate();
        if (result.SequenceId != reference.SequenceId ||
            result.AuthoringRevision != reference.AuthoringRevision ||
            result.RuntimeHostInstanceId != reference.RuntimeHostInstanceId ||
            result.PreparedExecutionId != reference.PreparedExecutionId ||
            result.Generation != reference.Generation)
            throw new InvalidDataException("Runtime sequence preparation reference changed in transit.");
        return result;
    }
}
