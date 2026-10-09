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
				role.Enabled)).ToArray(),
			state.CompositingState is null
				? null
				: new PersistedCompositingState(
					state.CompositingState.Version.ToString(),
					state.CompositingState.Layers.Select(layer => new PersistedCompositingLayer(
						layer.LayerId,
						(int)layer.Kind,
						layer.Order,
						layer.Visible,
						layer.Opacity,
						layer.PositionX,
						layer.PositionY,
						layer.Scale,
						layer.ContentIdentity,
						layer.RotationDegrees,
						layer.AnchorX,
						layer.AnchorY,
						layer.CropLeft,
						layer.CropTop,
						layer.CropRight,
						layer.CropBottom,
						layer.ProcessingStack.Select(ToPersistedProcessingNode).ToArray())).ToArray())));
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

		ProductionCompositingState? recoveredCompositingState = null;
		if (snapshot.CompositingState is not null)
		{
			var compositingVersion = CompatibilityVersion.Parse(snapshot.CompositingState.Version);
			recoveredCompositingState = new ProductionCompositingState(
				compositingVersion,
				snapshot.CompositingState.Layers.Select(layer => new ProductionCompositingLayerState(
					layer.LayerId,
					Enum.IsDefined(typeof(ProductionCompositingLayerKind), layer.Kind)
						? (ProductionCompositingLayerKind)layer.Kind
						: throw new InvalidDataException($"Recovered compositing layer kind '{layer.Kind}' is invalid."),
					layer.Order,
					layer.Visible,
					layer.Opacity,
					layer.PositionX,
					layer.PositionY,
					layer.Scale,
					layer.ContentIdentity,
					layer.RotationDegrees,
					layer.AnchorX,
					layer.AnchorY,
					layer.CropLeft,
					layer.CropTop,
					layer.CropRight,
					layer.CropBottom,
					processingStack: CanonicalProcessingStack(layer.ProcessingStack, layer.ProcessingNode)
						.Select(FromPersistedProcessingNode)
						.ToArray())).ToArray());
		}

		if (activeSceneId is { } recoveredActiveSceneId)
		{
			var activeScene = specification.Scenes.Single(scene => scene.SceneId == recoveredActiveSceneId);
			if (activeScene.CompositingState is not null)
			{
				if (recoveredCompositingState is null)
				{
					activeSceneId = null;
				}
				else if (!activeScene.CompositingState.Equals(recoveredCompositingState))
				{
					throw new InvalidDataException("Recovered active scene does not match recovered compositing state.");
				}
			}
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
			outputRoles,
			recoveredCompositingState);
	}

	private sealed record PersistedAuthoritySnapshot(
		string Version,
		string ProductionId,
		ulong Revision,
		string PreviewSourceId,
		string ProgramSourceId,
		string? ActiveSceneId = null,
		PersistedOutputRole[]? OutputRoles = null,
		PersistedCompositingState? CompositingState = null);

	private sealed record PersistedCompositingState(
		string Version,
		PersistedCompositingLayer[] Layers);

	private static PersistedProcessingNode ToPersistedProcessingNode(ProductionCompositingProcessingNodeState node) => new(
		node.NodeId,
		(int)node.Kind,
		node.Enabled,
		new PersistedColorGrade(
			node.ColorGrade.Brightness,
			node.ColorGrade.Contrast,
			node.ColorGrade.Saturation));

	private static ProductionCompositingProcessingNodeState FromPersistedProcessingNode(PersistedProcessingNode node)
	{
		if (!Enum.IsDefined(typeof(ProductionCompositingProcessingNodeKind), node.Kind))
			throw new InvalidDataException($"Recovered processing node kind '{node.Kind}' is invalid.");
		return new ProductionCompositingProcessingNodeState(
			node.NodeId,
			(ProductionCompositingProcessingNodeKind)node.Kind,
			node.Enabled,
			new ProductionColorGradeSettings(
				node.ColorGrade.Brightness,
				node.ColorGrade.Contrast,
				node.ColorGrade.Saturation));
	}

	private static PersistedProcessingNode[] CanonicalProcessingStack(
		PersistedProcessingNode[]? processingStack,
		PersistedProcessingNode? legacyProcessingNode)
	{
		if (processingStack is not null && legacyProcessingNode is not null)
			throw new InvalidDataException("Recovered compositing layer contains both legacy processing-node and processing-stack state.");
		var canonical = processingStack ??
			(legacyProcessingNode is null ? Array.Empty<PersistedProcessingNode>() : new[] { legacyProcessingNode });
		if (canonical.Length > ProductionCompositingProcessingStackLimits.MaximumNodeCount)
			throw new InvalidDataException($"Recovered processing stack exceeds the maximum of {ProductionCompositingProcessingStackLimits.MaximumNodeCount} nodes.");
		if (canonical.Any(node => node is null))
			throw new InvalidDataException("Recovered processing stack must not contain null nodes.");
		if (canonical.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != canonical.Length)
			throw new InvalidDataException("Recovered processing stack contains duplicate node identities.");
		return canonical;
	}

	private sealed record PersistedCompositingLayer(
		string LayerId,
		int Kind,
		int Order,
		bool Visible,
		byte Opacity,
		double PositionX,
		double PositionY,
		double Scale,
		string ContentIdentity,
		double RotationDegrees = 0,
		double AnchorX = 0,
		double AnchorY = 0,
		double CropLeft = 0,
		double CropTop = 0,
		double CropRight = 0,
		double CropBottom = 0,
		PersistedProcessingNode[]? ProcessingStack = null,
		PersistedProcessingNode? ProcessingNode = null);

	private sealed record PersistedProcessingNode(
		string NodeId,
		int Kind,
		bool Enabled,
		PersistedColorGrade ColorGrade);

	private sealed record PersistedColorGrade(
		double Brightness,
		double Contrast,
		double Saturation);

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
