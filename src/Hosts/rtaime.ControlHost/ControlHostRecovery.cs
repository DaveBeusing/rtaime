// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.ControlHost;

public enum ControlHostRecoveryState
{
	Fresh = 1,
	Recovered = 2,
	Conflict = 3
}

public sealed record ControlHostRecoverySnapshot(
	ControlHostRecoveryState State,
	Revision? RecoveredRevision,
	string Detail);

internal static class ControlHostRecovery
{
	public const string CheckpointFormat = "rtaime.control.authority.v1";

	public static byte[] Serialize(AuthoritativeProductionState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return JsonSerializer.SerializeToUtf8Bytes(new PersistedAuthoritySnapshot(
			state.Version.ToString(),
			state.ProductionId.ToString(),
			state.Revision.Value,
			state.Routing.PreviewSourceId.ToString(),
			state.Routing.ProgramSourceId.ToString(),
			state.ActiveSceneId?.ToString(),
			state.OutputRoles.Select(role => new PersistedOutputRole(
				role.RoleId.ToString(),
				(int)role.Kind,
				role.SourceId.ToString(),
				role.ProviderSelector,
				role.TargetId,
				role.FormatPolicy,
				role.TimingPolicy,
				role.Enabled)).ToArray()));
	}

	public static async ValueTask<AuthoritativeProductionState?> LoadAsync(
		SqliteManagementStore store,
		ProductionSpecification specification,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(specification);

		var checkpoint = await store.ReadLatestAsync(specification.ProductionId.Value, cancellationToken).ConfigureAwait(false);
		if (checkpoint is null)
			return null;
		if (!string.Equals(checkpoint.Format, CheckpointFormat, StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported production checkpoint format '{checkpoint.Format}'.");
		if (checkpoint.ProductionId != specification.ProductionId.Value)
			throw new InvalidDataException("Production checkpoint identity does not match the configured production.");

		PersistedAuthoritySnapshot snapshot;
		try
		{
			snapshot = JsonSerializer.Deserialize<PersistedAuthoritySnapshot>(checkpoint.Payload.Span)
				?? throw new InvalidDataException("Production checkpoint payload is required.");
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException("Production checkpoint payload is not valid JSON.", exception);
		}

		var version = CompatibilityVersion.Parse(snapshot.Version);
		ControlContractVersion.EnsureSupported(version);
		var productionId = new ProductionId(Identity.Parse(snapshot.ProductionId));
		if (productionId != specification.ProductionId)
			throw new InvalidDataException("Recovered authoritative payload belongs to a different production identity.");

		var revision = new Revision(snapshot.Revision);
		if (revision != checkpoint.AuthoritativeRevision)
			throw new InvalidDataException("Recovered authoritative payload revision does not match the checkpoint revision.");

		var preview = new ProductionSourceId(Identity.Parse(snapshot.PreviewSourceId));
		var program = new ProductionSourceId(Identity.Parse(snapshot.ProgramSourceId));
		if (!specification.Sources.Any(source => source.SourceId == preview))
			throw new InvalidDataException("Recovered Preview source is not present in the production specification.");
		if (!specification.Sources.Any(source => source.SourceId == program))
			throw new InvalidDataException("Recovered Program source is not present in the production specification.");

		var recoveredRouting = new ProductionRoutingState(preview, program);
		SceneId? activeSceneId = null;
		if (!string.IsNullOrWhiteSpace(snapshot.ActiveSceneId))
		{
			activeSceneId = new SceneId(Identity.Parse(snapshot.ActiveSceneId));
			var activeScene = specification.Scenes.FirstOrDefault(scene => scene.SceneId == activeSceneId.Value)
				?? throw new InvalidDataException("Recovered active scene is not present in the production specification.");
			if (activeScene.Routing != recoveredRouting)
				throw new InvalidDataException("Recovered active scene does not match recovered production routing.");
		}

		var outputRoles = snapshot.OutputRoles is { Length: > 0 }
			? snapshot.OutputRoles.Select(role => new ProductionOutputRoleState(
				new OutputRoleId(role.RoleId),
				Enum.IsDefined(typeof(OutputRoleKind), role.Kind)
					? (OutputRoleKind)role.Kind
					: throw new InvalidDataException($"Recovered output role kind '{role.Kind}' is invalid."),
				new ProductionSourceId(Identity.Parse(role.SourceId)),
				role.ProviderSelector,
				role.TargetId,
				role.FormatPolicy,
				role.TimingPolicy,
				role.Enabled)).ToArray()
			: specification.InitialOutputRoles
				.Select(role => role.Kind == OutputRoleKind.Program ? role.WithSource(program) : role)
				.ToArray();
		var outputValidation = ProductionOutputRoleValidator.Validate(
			specification,
			outputRoles,
			recoveredRouting,
			"recovered.outputRoles");
		if (!outputValidation.IsValid)
			throw new InvalidDataException(string.Join("; ", outputValidation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));

		return new AuthoritativeProductionState(
			version,
			productionId,
			revision,
			recoveredRouting,
			activeSceneId,
			outputRoles);
	}

	private sealed record PersistedAuthoritySnapshot(
		string Version,
		string ProductionId,
		ulong Revision,
		string PreviewSourceId,
		string ProgramSourceId,
		string? ActiveSceneId = null,
		PersistedOutputRole[]? OutputRoles = null);

	private sealed record PersistedOutputRole(
		string RoleId,
		int Kind,
		string SourceId,
		string ProviderSelector,
		string TargetId,
		string FormatPolicy,
		string TimingPolicy,
		bool Enabled);
}
