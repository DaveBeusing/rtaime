// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using rtaime.Control;
using rtaime.Control.Contracts;
namespace rtaime.ControlHost;

public sealed record OutputCanvasReceipt(Guid TransactionId, string RequestSha256, ulong Revision);
public sealed record OutputCanvasDocument(string Format, OutputCanvasConfiguration Saved,
    OutputCanvasConfiguration[] History, OutputCanvasReceipt[] Receipts);

public sealed partial class ShowProjectPersistenceStore
{
    private const string CanvasFormat = "rtaime.output-canvas.v1";

    public static OutputCanvasDocument ReadOutputCanvasDocument(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new InvalidDataException("Canvas history exceeds its byte cap.");
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        OutputCanvasJson.RejectDuplicates(parsed.RootElement);
        var document = JsonSerializer.Deserialize<OutputCanvasDocument>(json, OutputCanvasJson.Options)
            ?? throw new InvalidDataException("Canvas document is required.");
        if (document.Format != CanvasFormat || document.Saved is null || document.History is null || document.Receipts is null ||
            document.History.Length > 4 || document.Receipts.Length > 64 ||
            document.Receipts.Any(r => r is null || r.TransactionId == Guid.Empty || r.Revision == 0 || r.Revision > document.Saved.EditRevision ||
                r.RequestSha256 is null || r.RequestSha256.Length != 64 || r.RequestSha256.Any(c => !Uri.IsHexDigit(c))) ||
            document.Receipts.Select(r => r.TransactionId).Distinct().Count() != document.Receipts.Length)
            throw new InvalidDataException("Unsupported or invalid canvas history/receipt document.");
        foreach (var model in document.History.Append(document.Saved))
        {
            if (model is null || !OutputCanvasModel.Validate(model).IsValid ||
                JsonSerializer.SerializeToUtf8Bytes(model, OutputCanvasJson.Options).Length > OutputCanvasContract.MaximumJsonBytes)
                throw new InvalidDataException("Invalid persisted canvas configuration.");
            if (model.ConfigurationId != document.Saved.ConfigurationId || model.EditRevision > document.Saved.EditRevision)
                throw new InvalidDataException("Canvas history identity/revision mismatch.");
        }
        return document;
    }

    public async ValueTask<OutputCanvasConfiguration> LoadOutputCanvasAsync(ProductionSpecification baseline,
        OutputCanvasConfiguration legacy, CancellationToken cancellationToken = default)
    {
        var project = await LoadAsync(baseline, cancellationToken).ConfigureAwait(false);
        return project.OutputCanvasJson is null ? legacy : ReadOutputCanvasDocument(project.OutputCanvasJson).Saved;
    }

    public async ValueTask<OutputCanvasSaveResult> SaveOutputCanvasAsync(ProductionSpecification baseline,
        OutputCanvasConfiguration legacy, OutputCanvasSaveRequest request, OutputCanvasTarget[] targets,
        CancellationToken cancellationToken = default)
    {
        // Snapshot caller-owned arrays before the first await.
        var canonicalRequest = JsonSerializer.Serialize(request, OutputCanvasJson.Options);
        request = OutputCanvasJson.Read<OutputCanvasSaveRequest>(canonicalRequest);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireDocumentAsync(baseline.ProductionId, cancellationToken).ConfigureAwait(false);
            var project = Deserialize(current, baseline);
            var document = project.OutputCanvasJson is null
                ? new OutputCanvasDocument(CanvasFormat, legacy, [], [])
                : ReadOutputCanvasDocument(project.OutputCanvasJson);
            var saved = document.Saved;
            var receipt = document.Receipts.SingleOrDefault(r => r.TransactionId == request.TransactionId);
            OutputCanvasSaveResult Reject(string code, string message) => new(false, saved.EditRevision, saved,
                new(request.ConfigurationId, request.ExpectedSavedRevision, [new(code, "$", message)]));
            if (receipt is not null)
                return receipt.RequestSha256 == hash
                    ? new(true, receipt.Revision, saved, new(request.ConfigurationId, receipt.Revision, []), true)
                    : Reject("canvas.transaction_conflict", "Transaction identity was reused with different content.");
            if (request.ExpectedSavedRevision != saved.EditRevision)
                return Reject("canvas.revision_conflict", "Expected saved revision does not match the current configuration.");
            OutputCanvasConfiguration draft;
            try { draft = OutputCanvasModel.Apply(saved, request); }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            { return Reject("canvas.transaction", exception.Message); }
            var effective = project.ApplyTo(baseline);
            var validation = OutputCanvasModel.Validate(draft, effective.Sources.Select(s => s.SourceId.ToString()).ToArray(),
                baseline.InitialOutputRoles.Select(r => r.RoleId.ToString()).ToArray(), targets);
            if (!validation.IsValid) return new(false, saved.EditRevision, draft, validation);
            if (JsonSerializer.SerializeToUtf8Bytes(draft, OutputCanvasJson.Options).Length > OutputCanvasContract.MaximumJsonBytes)
                return Reject("canvas.capacity", "Saved model exceeds its byte cap.");
            var next = new OutputCanvasDocument(CanvasFormat, draft,
                document.History.Append(saved).TakeLast(4).ToArray(),
                document.Receipts.Append(new(request.TransactionId, hash, draft.EditRevision)).TakeLast(64).ToArray());
            var json = JsonSerializer.Serialize(next, OutputCanvasJson.Options);
            _ = ReadOutputCanvasDocument(json);
            // The existing outer CAS/SQLite transaction preserves all unrelated show subdocuments.
            await WriteAsync(project with { OutputCanvasJson = json }, current.Version, baseline, cancellationToken).ConfigureAwait(false);
            return new(true, draft.EditRevision, draft, validation);
        }
        finally { _gate.Release(); }
    }
}
