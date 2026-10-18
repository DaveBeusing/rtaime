// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using System.Text.Json.Nodes;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Persistence;
namespace rtaime.Tests.Integration;

public sealed partial class ShowProjectPersistenceIntegrationTests
{
    [Fact]
    public async Task Sequence_migration_preserves_legacy_show_and_rejects_partial_future_and_stale_writes()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-sequence-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var spec = CreateSpecification();
        try
        {
            await using var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
            await management.InitializeAsync();
            var store = new ShowProjectPersistenceStore(management);
            await store.LoadOrCreateAsync(spec);
            var legacy = await management.GetDocumentAsync("show.project", spec.ProductionId.ToString());
            var before = JsonNode.Parse(legacy!.Json)!.AsObject();
            Assert.Equal("rtaime.show-project.v1", before["format"]!.GetValue<string>());
            Assert.Empty((await store.LoadSequencesAsync(spec)).Sequences);
            Assert.Equal(legacy, await management.GetDocumentAsync("show.project", spec.ProductionId.ToString()));
            var sequence = new SequenceDefinition("1.0", Guid.NewGuid(), "New sequence", new(50, 1), [], [], []);
            var request = new SequenceMutationRequest("1.0", Guid.NewGuid(), 0, sequence.SequenceId, sequence);
            Assert.True((await store.MutateSequenceAsync(spec, "save", request, [], [])).Saved);
            var saved = await management.GetDocumentAsync("show.project", spec.ProductionId.ToString());
            var after = JsonNode.Parse(saved!.Json)!.AsObject();
            Assert.Equal("rtaime.show-project.v3", after["format"]!.GetValue<string>());
            foreach (var property in before.Where(p => p.Key is not "format" and not "sequenceJson"))
                Assert.True(JsonNode.DeepEquals(property.Value, after[property.Key]), property.Key);
            var current = await store.LoadAsync(spec);
            var history = ShowProjectPersistenceStore.ReadSequenceDocument(current.SequenceJson!);
            Assert.Throws<InvalidDataException>(() => ShowProjectPersistenceStore.ReadSequenceDocument(
                JsonSerializer.Serialize(history with { Format = "future" }, SequenceJson.Options)));
            Assert.Throws<InvalidDataException>(() => ShowProjectPersistenceStore.ReadSequenceDocument(
                JsonSerializer.Serialize(history with { Saved = history.Saved with { Sequences = null! } }, SequenceJson.Options)));
            var bad = request with { TransactionId = Guid.NewGuid(), ExpectedRevision = 1, Definition = sequence with { Clips = null! } };
            Assert.False((await store.MutateSequenceAsync(spec, "save", bad, [], [])).Saved);
            Assert.Equal(saved, await management.GetDocumentAsync("show.project", spec.ProductionId.ToString()));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await store.MutateSequenceAsync(spec, "save", request, [], [], cancelled.Token));
            }
            var writes = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => store.MutateSequenceAsync(spec, "save",
                request with { TransactionId = Guid.NewGuid(), ExpectedRevision = 1, Definition = sequence with { Name = "Concurrent " + i } },
                [], []).AsTask()));
            Assert.Single(writes.Where(w => w.Saved));
            Assert.Single(writes.Where(w => !w.Saved && w.Issues.Any(i => i.Code == "sequence.revision_conflict")));
            var reopened = new ShowProjectPersistenceStore(management);
            Assert.True((await reopened.MutateSequenceAsync(spec, "save", request, [], [])).Replayed);
            Assert.Equal(2UL, (await reopened.LoadSequencesAsync(spec)).Revision);
            var latest = await management.GetDocumentAsync("show.project", spec.ProductionId.ToString());
            var invalidOuter = JsonNode.Parse(latest!.Json)!;
            invalidOuter["format"] = "rtaime.show-project.v2";
            var tampered = await management.PutDocumentAsync("show.project", spec.ProductionId.ToString(), invalidOuter.ToJsonString(), latest.Version);
            Assert.True(tampered.Written);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.LoadAsync(spec));
            Assert.Equal(tampered.Document, await management.GetDocumentAsync("show.project", spec.ProductionId.ToString()));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
