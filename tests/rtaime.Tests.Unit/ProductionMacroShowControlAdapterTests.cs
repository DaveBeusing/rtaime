// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class ProductionMacroShowControlAdapterTests
{
	[Fact]
	public void Adapter_preserves_macro_and_action_identity_and_order()
	{
		var macroId = new ProductionMacroId(Identity.Parse("83100000-0000-0000-0000-000000000001"));
		var firstId = new ProductionMacroActionId(Identity.Parse("83100000-0000-0000-0000-000000000002"));
		var secondId = new ProductionMacroActionId(Identity.Parse("83100000-0000-0000-0000-000000000003"));
		var macro = new ProductionMacroDefinition(
			ProductionMacroContractVersion.Current,
			macroId,
			"Macro",
			[
				new ProductionMacroAction(firstId, ShowControlActionKind.Cut),
				new ProductionMacroAction(secondId, ShowControlActionKind.WaitFrames, waitFrames: 5)
			]);

		var cueList = ProductionMacroShowControlAdapter.BuildCueList(macro);
		var cue = Assert.Single(cueList.Cues);

		Assert.Equal(macroId.Value, cueList.CueListId.Value);
		Assert.Equal(macroId.Value, cue.CueId.Value);
		Assert.Equal(new[] { firstId.Value, secondId.Value }, cue.Actions.Select(action => action.ActionId.Value));
		Assert.Equal(ShowControlActionKind.WaitFrames, cue.Actions[1].Kind);
	}
}
