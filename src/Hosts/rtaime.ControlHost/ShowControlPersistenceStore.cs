// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.ControlHost;

public sealed record PersistedShowControlWorkspace(
	IReadOnlyList<ShowControlCueList> CueLists,
	ShowControlCueListId? SelectedCueListId,
	ShowControlExecutionSnapshot Execution,
	ulong StorageVersion);

public sealed record ShowControlPersistenceWriteResult(
	bool Written,
	PersistedShowControlWorkspace? Persisted,
	Failure? Failure);

public sealed class ShowControlPersistenceStore
{
	private const string Area = "show-control.workspace";
	private const string DocumentFormat = "rtaime.show-control.v1";
	private const int MaximumCueLists = 32;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly SqliteManagementStore _managementStore;

	public ShowControlPersistenceStore(SqliteManagementStore managementStore)
	{
		_managementStore = managementStore ?? throw new ArgumentNullException(nameof(managementStore));
	}

	public async ValueTask<PersistedShowControlWorkspace> LoadAsync(
		ProductionId productionId,
		CancellationToken cancellationToken = default)
	{
		var document = await _managementStore
			.GetDocumentAsync(Area, productionId.ToString(), cancellationToken)
			.ConfigureAwait(false);
		if (document is null)
			return new PersistedShowControlWorkspace(Array.Empty<ShowControlCueList>(), null, ShowControlExecutionSnapshot.Idle, 0);

		var dto = JsonSerializer.Deserialize<WorkspaceDocument>(document.Json, JsonOptions)
			?? throw new InvalidDataException("Persisted show-control workspace is empty.");
		if (!string.Equals(dto.Format, DocumentFormat, StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported show-control workspace format '{dto.Format}'.");
		if (!string.Equals(dto.ProductionId, productionId.ToString(), StringComparison.Ordinal))
			throw new InvalidDataException("Persisted show-control workspace belongs to a different production.");
		if (dto.CueLists.Length > MaximumCueLists)
			throw new InvalidDataException($"Persisted show-control workspace exceeds the {MaximumCueLists}-list limit.");

		var cueLists = dto.CueLists
			.Select(ShowControlCanonicalSerializer.Deserialize)
			.ToArray();
		if (cueLists.Select(list => list.CueListId).Distinct().Count() != cueLists.Length)
			throw new InvalidDataException("Persisted show-control workspace contains duplicate cue-list identities.");

		ShowControlCueListId? selected = null;
		if (!string.IsNullOrWhiteSpace(dto.SelectedCueListId))
		{
			selected = new ShowControlCueListId(Identity.Parse(dto.SelectedCueListId));
			if (!cueLists.Any(list => list.CueListId == selected.Value))
				throw new InvalidDataException("Persisted selected show-control cue list is unavailable.");
		}

		var execution = dto.Execution is null
			? ShowControlExecutionSnapshot.Idle
			: FromDocument(dto.Execution);
		if (execution.CueListId is { } executionListId &&
			!cueLists.Any(list => list.CueListId == executionListId))
		{
			throw new InvalidDataException("Persisted show-control execution references an unavailable cue list.");
		}

		return new PersistedShowControlWorkspace(cueLists, selected, execution, document.Version);
	}

	public async ValueTask<ShowControlPersistenceWriteResult> SaveAsync(
		ProductionId productionId,
		IReadOnlyList<ShowControlCueList> cueLists,
		ShowControlCueListId? selectedCueListId,
		ShowControlExecutionSnapshot execution,
		ulong expectedStorageVersion,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(cueLists);
		ArgumentNullException.ThrowIfNull(execution);
		if (cueLists.Count > MaximumCueLists)
			throw new ArgumentOutOfRangeException(nameof(cueLists), $"Show-control supports at most {MaximumCueLists} persisted cue lists.");
		if (cueLists.Select(list => list.CueListId).Distinct().Count() != cueLists.Count)
			throw new ArgumentException("Show-control cue-list identities must be unique.", nameof(cueLists));
		if (selectedCueListId.HasValue && !cueLists.Any(list => list.CueListId == selectedCueListId.Value))
			throw new ArgumentException("Selected show-control cue list must exist in the persisted workspace.", nameof(selectedCueListId));
		if (execution.CueListId is { } executionListId && !cueLists.Any(list => list.CueListId == executionListId))
			throw new ArgumentException("Show-control execution must reference a persisted cue list.", nameof(execution));

		var dto = new WorkspaceDocument(
			DocumentFormat,
			productionId.ToString(),
			cueLists.Select(ShowControlCanonicalSerializer.Serialize).ToArray(),
			selectedCueListId?.ToString(),
			ToDocument(execution));
		var json = JsonSerializer.Serialize(dto, JsonOptions);
		var write = await _managementStore
			.PutDocumentAsync(
				Area,
				productionId.ToString(),
				json,
				expectedStorageVersion,
				cancellationToken)
			.ConfigureAwait(false);
		if (!write.Written || write.Document is null)
			return new ShowControlPersistenceWriteResult(false, null, write.Failure);

		return new ShowControlPersistenceWriteResult(
			true,
			new PersistedShowControlWorkspace(cueLists.ToArray(), selectedCueListId, execution, write.Document.Version),
			null);
	}

	private static ExecutionDocument ToDocument(ShowControlExecutionSnapshot snapshot) => new(
		snapshot.Version.ToString(),
		snapshot.ExecutionId?.ToString(),
		snapshot.CueListId?.ToString(),
		(int)snapshot.State,
		snapshot.CueIndex,
		snapshot.ActionIndex,
		snapshot.CurrentCueId?.ToString(),
		snapshot.CurrentActionId?.ToString(),
		snapshot.ExecutionRevision,
		snapshot.WaitTargetFrameSequence,
		snapshot.RuntimeHostInstanceId,
		snapshot.RequiresAcknowledgement,
		snapshot.Failure?.Code,
		snapshot.Failure?.Message);

	private static ShowControlExecutionSnapshot FromDocument(ExecutionDocument document)
	{
		var version = CompatibilityVersion.Parse(document.Version);
		ShowControlContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(ShowControlExecutionState), document.State))
			throw new InvalidDataException($"Persisted show-control state '{document.State}' is invalid.");

		return new ShowControlExecutionSnapshot(
			version,
			string.IsNullOrWhiteSpace(document.ExecutionId) ? null : new ShowControlExecutionId(Identity.Parse(document.ExecutionId)),
			string.IsNullOrWhiteSpace(document.CueListId) ? null : new ShowControlCueListId(Identity.Parse(document.CueListId)),
			(ShowControlExecutionState)document.State,
			document.CueIndex,
			document.ActionIndex,
			string.IsNullOrWhiteSpace(document.CurrentCueId) ? null : new ShowControlCueId(Identity.Parse(document.CurrentCueId)),
			string.IsNullOrWhiteSpace(document.CurrentActionId) ? null : new ShowControlActionId(Identity.Parse(document.CurrentActionId)),
			document.ExecutionRevision,
			document.WaitTargetFrameSequence,
			document.RuntimeHostInstanceId,
			document.RequiresAcknowledgement,
			string.IsNullOrWhiteSpace(document.FailureCode)
				? null
				: new Failure(document.FailureCode, document.FailureMessage ?? "Show-control operation failed."));
	}

	private sealed record WorkspaceDocument(
		string Format,
		string ProductionId,
		string[] CueLists,
		string? SelectedCueListId,
		ExecutionDocument? Execution);

	private sealed record ExecutionDocument(
		string Version,
		string? ExecutionId,
		string? CueListId,
		int State,
		int? CueIndex,
		int? ActionIndex,
		string? CurrentCueId,
		string? CurrentActionId,
		ulong ExecutionRevision,
		ulong? WaitTargetFrameSequence,
		string? RuntimeHostInstanceId,
		bool RequiresAcknowledgement,
		string? FailureCode,
		string? FailureMessage);
}
