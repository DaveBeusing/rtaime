// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using rtaime.Control;
using rtaime.Control.Contracts;
namespace rtaime.ControlHost;

public sealed record SequenceReceipt(Guid TransactionId, string RequestSha256, ulong Revision, Guid SequenceId);
public sealed record SequenceDocument(string Format, SequenceLibrary Saved,
    SequenceLibrary[] Undo, SequenceLibrary[] Redo, SequenceReceipt[] Receipts);

public sealed partial class ShowProjectPersistenceStore
{
    private const string SequenceFormat = "rtaime.sequence-authoring.v1";
    private static SequenceLibrary EmptySequences => new(SequenceContract.Version, 0, []);
    public static SequenceDocument ReadSequenceDocument(string json)
    {
        var document = SequenceJson.Read<SequenceDocument>(json, 10 * 1024 * 1024);
        if (document.Format != SequenceFormat || document.Saved is null || document.Saved.Revision == 0 ||
            document.Undo is null || document.Redo is null || document.Receipts is null ||
            document.Undo.Length > 8 || document.Redo.Length > 8 || document.Receipts.Length is < 1 or > 64 ||
            document.Receipts.Any(r => r is null || r.TransactionId == Guid.Empty || r.Revision == 0 ||
                r.Revision > document.Saved.Revision || r.RequestSha256 is null || r.RequestSha256.Length != 64 ||
                r.RequestSha256.Any(c => !Uri.IsHexDigit(c))) ||
            document.Receipts.Select(r => r.TransactionId).Distinct().Count() != document.Receipts.Length ||
            document.Receipts.Select(r => r.Revision).Distinct().Count() != document.Receipts.Length ||
            !document.Receipts.Any(r => r.Revision == document.Saved.Revision))
            throw new InvalidDataException("Unsupported or incoherent sequence history.");
        foreach (var library in document.Undo.Concat(document.Redo).Append(document.Saved))
        {
            if (library is null || library.Revision > document.Saved.Revision || ValidateSequenceLibrary(library).Length != 0)
                throw new InvalidDataException("Invalid persisted sequence library.");
        }
        return document;
    }

    private static SequenceIssue[] ValidateSequenceLibrary(SequenceLibrary library,
        SequenceAsset[]? assets = null, string[]? buses = null)
    {
        if (library.Version != SequenceContract.Version || library.Sequences is null ||
            library.Sequences.Length > SequenceContract.MaximumSequences || library.Sequences.Any(s => s is null))
            return [new("sequence.library", "$", "Unsupported or oversized sequence library.")];
        var issues = library.Sequences.SelectMany(s => SequenceModel.Validate(s, library.Revision, assets, buses).Issues).ToList();
        if (issues.Count != 0) return issues.ToArray();
        var identities = library.Sequences.SelectMany(s => new[] { s.SequenceId }
            .Concat(s.Tracks.Select(t => t.TrackId)).Concat(s.Clips.Select(c => c.ClipId))
            .Concat(s.Transitions.Select(t => t.TransitionId))).ToArray();
        if (identities.Distinct().Count() != identities.Length)
            issues.Add(new("sequence.identity", "$", "Entity identities must be unique across the show library."));
        if (library.Sequences.Sum(s => s.Tracks.Length) > SequenceContract.MaximumTracks ||
            library.Sequences.Sum(s => s.Clips.Length) > SequenceContract.MaximumClips ||
            library.Sequences.Sum(s => s.Transitions.Length) > SequenceContract.MaximumTransitions ||
            JsonSerializer.SerializeToUtf8Bytes(library, SequenceJson.Options).Length > SequenceContract.MaximumJsonBytes)
            issues.Add(new("sequence.capacity", "$", "Show sequence library exceeds its bounded capacity."));
        return issues.ToArray();
    }

    public async ValueTask<SequenceLibrary> LoadSequencesAsync(ProductionSpecification baseline,
        CancellationToken cancellationToken = default)
    {
        var project = await LoadAsync(baseline, cancellationToken).ConfigureAwait(false);
        return project.SequenceJson is null ? EmptySequences : ReadSequenceDocument(project.SequenceJson).Saved;
    }

    public async ValueTask<SequenceMutationResult> MutateSequenceAsync(ProductionSpecification baseline,
        string operation, SequenceMutationRequest request, SequenceAsset[] assets, string[] buses,
        CancellationToken cancellationToken = default)
    {
        var canonical = JsonSerializer.Serialize(request, SequenceJson.Options);
        request = SequenceJson.Read<SequenceMutationRequest>(canonical);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + canonical)));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireDocumentAsync(baseline.ProductionId, cancellationToken).ConfigureAwait(false);
            var project = Deserialize(current, baseline);
            var document = project.SequenceJson is null
                ? new SequenceDocument(SequenceFormat, EmptySequences, [], [], [])
                : ReadSequenceDocument(project.SequenceJson);
            var saved = document.Saved;
            SequenceMutationResult Reject(string code, string message) =>
                new(false, saved.Revision, request.SequenceId, null, [new(code, "$", message)]);
            if (request.Version != SequenceContract.Version || request.TransactionId == Guid.Empty)
                return Reject("sequence.request", "Supported version and nonempty transaction identity are required.");
            var receipt = document.Receipts.SingleOrDefault(r => r.TransactionId == request.TransactionId);
            if (receipt is not null)
                return receipt.RequestSha256 == hash
                    ? new(true, receipt.Revision, receipt.SequenceId, null, [], true)
                    : Reject("sequence.transaction_conflict", "Transaction identity was reused with different content.");
            if (request.ExpectedRevision != saved.Revision || saved.Revision == ulong.MaxValue)
                return Reject("sequence.revision_conflict", "Expected authoring revision does not match the saved library.");
            SequenceLibrary draft;
            var undo = document.Undo; var redo = document.Redo;
            try
            {
                switch (operation)
                {
                    case "save":
                        if (request.Definition is null || request.Edits is not null ||
                            request.SequenceId == Guid.Empty || request.Definition.SequenceId != request.SequenceId)
                            return Reject("sequence.request", "Save requires a matching sequence definition only.");
                        draft = saved with { Sequences = saved.Sequences.Where(s => s.SequenceId != request.SequenceId).Append(request.Definition).ToArray() };
                        undo = undo.Append(saved).TakeLast(8).ToArray(); redo = [];
                        break;
                    case "edit":
                        if (request.Definition is not null || request.Edits is null)
                            return Reject("sequence.request", "Edit requires a typed edit batch only.");
                        var selected = saved.Sequences.SingleOrDefault(s => s.SequenceId == request.SequenceId);
                        if (selected is null) return Reject("sequence.missing", "Sequence identity is unknown.");
                        var edited = SequenceEdits.Apply(selected, request.Edits);
                        draft = saved with { Sequences = saved.Sequences.Select(s => s.SequenceId == request.SequenceId ? edited : s).ToArray() };
                        undo = undo.Append(saved).TakeLast(8).ToArray(); redo = [];
                        break;
                    case "undo":
                    case "redo":
                        if (request.SequenceId != Guid.Empty || request.Definition is not null || request.Edits is not null)
                            return Reject("sequence.request", "Undo/redo target the library and accept no definition or edits.");
                        var stack = operation == "undo" ? undo : redo;
                        if (stack.Length == 0) return Reject("sequence.history_empty", "No retained authoring history.");
                        draft = stack[^1];
                        if (operation == "undo") { undo = undo[..^1]; redo = redo.Append(saved).TakeLast(8).ToArray(); }
                        else { redo = redo[..^1]; undo = undo.Append(saved).TakeLast(8).ToArray(); }
                        break;
                    default: return Reject("sequence.operation", "Unknown authoring operation.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            { return Reject("sequence.edit", exception.Message); }
            draft = draft with { Revision = saved.Revision + 1 };
            var issues = ValidateSequenceLibrary(draft, assets, buses);
            if (issues.Length != 0) return new(false, saved.Revision, request.SequenceId, null, issues);
            var next = new SequenceDocument(SequenceFormat, draft, undo, redo,
                document.Receipts.Append(new(request.TransactionId, hash, draft.Revision, request.SequenceId)).TakeLast(64).ToArray());
            var json = JsonSerializer.Serialize(next, SequenceJson.Options);
            _ = ReadSequenceDocument(json);
            await WriteAsync(project with { SequenceJson = json }, current.Version, baseline, cancellationToken).ConfigureAwait(false);
            return new(true, draft.Revision, request.SequenceId,
                draft.Sequences.SingleOrDefault(s => s.SequenceId == request.SequenceId), []);
        }
        finally { _gate.Release(); }
    }
}
