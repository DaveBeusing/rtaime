// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Contracts;

public sealed class ProductionMacroContractTests
{
	[Fact]
	public void Macro_library_round_trips_stable_identity_order_and_governed_payloads()
	{
		var macroId = new ProductionMacroId(Identity.Parse("83000000-0000-0000-0000-000000000001"));
		var previewId = new ProductionMacroActionId(Identity.Parse("83000000-0000-0000-0000-000000000002"));
		var waitId = new ProductionMacroActionId(Identity.Parse("83000000-0000-0000-0000-000000000003"));
		var routeId = new ProductionMacroActionId(Identity.Parse("83000000-0000-0000-0000-000000000004"));
		var source = "83000000-0000-0000-0000-000000000005";
		var macro = new ProductionMacroDefinition(
			ProductionMacroContractVersion.Current,
			macroId,
			"Open Show",
			[
				new ProductionMacroAction(previewId, ShowControlActionKind.SetPreview, sourceId: source),
				new ProductionMacroAction(waitId, ShowControlActionKind.WaitFrames, waitFrames: 25),
				new ProductionMacroAction(routeId, ShowControlActionKind.RouteOutputRole, sourceId: source, outputRoleId: "Aux")
			],
			"Bounded opening sequence.");

		var restored = Assert.Single(ProductionMacroCanonicalSerializer.Deserialize(
			ProductionMacroCanonicalSerializer.Serialize([macro])));

		Assert.Equal(macroId, restored.MacroId);
		Assert.Equal("Open Show", restored.Name);
		Assert.Equal(new[] { previewId, waitId, routeId }, restored.Actions.Select(action => action.ActionId));
		Assert.Equal(25U, restored.Actions[1].Command.WaitFrames);
		Assert.Equal("Aux", restored.Actions[2].Command.OutputRoleId);
		Assert.Equal(source, restored.Actions[2].Command.SourceId);
	}

	[Fact]
	public void Macro_action_union_rejects_unknown_and_unbounded_payloads()
	{
		var actionId = ProductionMacroActionId.New();
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new ProductionMacroAction(actionId, (ShowControlActionKind)999));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new ProductionMacroAction(
				actionId,
				ShowControlActionKind.WaitFrames,
				waitFrames: ShowControlAction.MaximumWaitFrames + 1));
	}

	[Fact]
	public void Macro_definition_is_bounded_and_action_identity_is_unique()
	{
		var id = ProductionMacroActionId.New();
		var action = new ProductionMacroAction(id, ShowControlActionKind.Cut);
		Assert.Throws<ArgumentException>(() =>
			new ProductionMacroDefinition(
				ProductionMacroContractVersion.Current,
				ProductionMacroId.New(),
				"Duplicate",
				[action, action]));

		var actions = Enumerable.Range(0, ProductionMacroDefinition.MaximumActions + 1)
			.Select(_ => new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Cut))
			.ToArray();
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new ProductionMacroDefinition(
				ProductionMacroContractVersion.Current,
				ProductionMacroId.New(),
				"Too many",
				actions));
	}

	[Fact]
	public void Output_role_action_requires_role_and_source_and_carries_no_generic_code()
	{
		var actionId = ProductionMacroActionId.New();
		Assert.Throws<ArgumentException>(() =>
			new ProductionMacroAction(actionId, ShowControlActionKind.RouteOutputRole, sourceId: Identity.New().ToString()));
		Assert.Throws<ArgumentException>(() =>
			new ProductionMacroAction(actionId, ShowControlActionKind.RouteOutputRole, outputRoleId: "Aux"));

		var action = new ProductionMacroAction(
			actionId,
			ShowControlActionKind.RouteOutputRole,
			sourceId: Identity.New().ToString(),
			outputRoleId: "Aux");
		Assert.Equal(ShowControlActionKind.RouteOutputRole, action.Kind);
	}

	[Fact]
	public void Reorder_preserves_macro_action_identity_and_payload()
	{
		var first = new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Cut);
		var second = new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.WaitFrames, waitFrames: 10);
		var macro = new ProductionMacroDefinition(
			ProductionMacroContractVersion.Current,
			ProductionMacroId.New(),
			"Reorder",
			[first, second]);

		var reordered = macro.Reorder([second.ActionId, first.ActionId]);

		Assert.Equal([second.ActionId, first.ActionId], reordered.Actions.Select(action => action.ActionId));
		Assert.Equal(ShowControlActionKind.WaitFrames, reordered.Actions[0].Kind);
		Assert.Equal(10U, reordered.Actions[0].Command.WaitFrames);
	}

	[Fact]
	public void Closed_action_union_has_no_macro_nesting_or_generic_script_action()
	{
		var names = Enum.GetNames<ShowControlActionKind>();
		Assert.DoesNotContain(names, name => name.Contains("Macro", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(names, name => name.Contains("Script", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(names, name => name.Contains("Execute", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Execution_snapshot_round_trips_recovery_evidence()
	{
		var snapshot = new ProductionMacroExecutionSnapshot(
			ProductionMacroExecutionState.RecoveryRequired,
			ProductionMacroExecutionId.New(),
			ProductionMacroId.New(),
			2,
			ProductionMacroActionId.New(),
			ProductionMacroActionId.New(),
			7,
			120,
			"runtime-instance",
			true,
			new Failure("production_macro.recovery.test", "Ambiguous outcome."));

		var restored = ProductionMacroExecutionSerializer.Deserialize(
			ProductionMacroExecutionSerializer.Serialize(snapshot));

		Assert.Equal(snapshot, restored);
	}
}
