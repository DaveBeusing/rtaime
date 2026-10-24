// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using Rtaime.ExternalControl.V1;

namespace rtaime.Client;

public sealed partial class NamedPipeOperatorControlTransport : ISequencePreparationTransport
{
    public async ValueTask<SequencePreparationContext> GetSequencePreparationContextAsync(
        CancellationToken cancellationToken = default)
    {
        var response = SequenceJson.Read<SequencePreparationContext>(
            (await ExchangeAsync("control.sequence.preparation.context.get", new { }, cancellationToken)
                .ConfigureAwait(false)).Payload.GetRawText(), 16 * 1024);
        response.Validate();
        return response;
    }

    public async ValueTask<SequencePreparationStatus> PrepareSequenceAsync(
        SequencePreparationRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var response = SequenceJson.Read<SequencePreparationStatus>(
            (await ExchangeAsync("control.sequence.prepare", request, cancellationToken).ConfigureAwait(false))
            .Payload.GetRawText(), 16 * 1024);
        response.Validate();
        if (response.SequenceId != request.SequenceId ||
            response.AuthoringRevision != request.ExpectedAuthoringRevision ||
            response.RuntimeHostInstanceId != request.ExpectedRuntimeHostInstanceId)
            throw new InvalidDataException("Sequence preparation response does not match the requested revision or RuntimeHost.");
        return response;
    }

    public async ValueTask<SequencePreparationStatus> GetSequencePreparationAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default) =>
        ValidateSequencePreparationResponse(reference,
            (await ExchangeAsync("control.sequence.preparation.get", reference, cancellationToken)
                .ConfigureAwait(false)).Payload.GetRawText());

    public async ValueTask<SequencePreparationStatus> CancelSequencePreparationAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default) =>
        ValidateSequencePreparationResponse(reference,
            (await ExchangeAsync("control.sequence.preparation.cancel", reference, cancellationToken)
                .ConfigureAwait(false)).Payload.GetRawText());

    internal static SequencePreparationStatus ValidateSequencePreparationResponse(
        SequencePreparationReference reference, string json)
    {
        reference.Validate();
        var result = SequenceJson.Read<SequencePreparationStatus>(json, 16 * 1024);
        result.Validate();
        if (result.SequenceId != reference.SequenceId ||
            result.AuthoringRevision != reference.AuthoringRevision ||
            result.RuntimeHostInstanceId != reference.RuntimeHostInstanceId ||
            result.PreparedExecutionId != reference.PreparedExecutionId ||
            result.Generation != reference.Generation)
            throw new InvalidDataException("Sequence preparation observation is stale or belongs to another owner.");
        return result;
    }
}

public sealed partial class GrpcOperatorControlTransport : ISequencePreparationTransport
{
    public async ValueTask<SequencePreparationContext> GetSequencePreparationContextAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await ExecuteShowRequestAsync(
            request => request.GetSequencePreparationContext = new EmptyRequest(), cancellationToken)
            .ConfigureAwait(false);
        var status = SequenceJson.Read<SequencePreparationContext>(
            reply.PayloadJson.ToStringUtf8(), 16 * 1024);
        status.Validate();
        return status;
    }

    public async ValueTask<SequencePreparationStatus> PrepareSequenceAsync(
        SequencePreparationRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        // Native source fingerprint verification and bounded decoder preroll
        // run on the management lane and can outlast ordinary control RPCs.
        // A lost response must not replay a non-idempotent preparation.
        EnsureSnapshotNotRequired();
        var rpcRequest = Request();
        rpcRequest.PrepareSequence = Raw(request);
        var timeout = _options.RequestTimeout > TimeSpan.FromSeconds(45)
            ? _options.RequestTimeout : TimeSpan.FromSeconds(45);
        var reply = await SendRequestAsync(rpcRequest,
            DateTime.UtcNow + timeout, cancellationToken).ConfigureAwait(false);
        ValidateReply(rpcRequest, reply, snapshot: false);
        var status = SequenceJson.Read<SequencePreparationStatus>(reply.PayloadJson.ToStringUtf8(), 16 * 1024);
        status.Validate();
        if (status.SequenceId != request.SequenceId ||
            status.AuthoringRevision != request.ExpectedAuthoringRevision ||
            status.RuntimeHostInstanceId != request.ExpectedRuntimeHostInstanceId)
            throw new InvalidDataException("Sequence preparation response does not match the requested revision or RuntimeHost.");
        return status;
    }

    public async ValueTask<SequencePreparationStatus> GetSequencePreparationAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default) =>
        NamedPipeOperatorControlTransport.ValidateSequencePreparationResponse(reference,
            (await ExecuteShowRequestAsync(r => r.GetSequencePreparation = Raw(reference), cancellationToken)
                .ConfigureAwait(false)).PayloadJson.ToStringUtf8());

    public async ValueTask<SequencePreparationStatus> CancelSequencePreparationAsync(
        SequencePreparationReference reference, CancellationToken cancellationToken = default) =>
        NamedPipeOperatorControlTransport.ValidateSequencePreparationResponse(reference,
            (await ExecuteShowRequestAsync(r => r.CancelSequencePreparation = Raw(reference), cancellationToken)
                .ConfigureAwait(false)).PayloadJson.ToStringUtf8());
}
