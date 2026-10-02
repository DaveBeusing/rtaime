// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;

namespace rtaime.Control;

public static class ProductionMacroShowControlAdapter
{
	public static ShowControlCueList BuildCueList(ProductionMacroDefinition macro)
	{
		ArgumentNullException.ThrowIfNull(macro);
		var actions = macro.Actions.Select(action => action.Command).ToArray();
		return new ShowControlCueList(
			ShowControlContractVersion.Current,
			new ShowControlCueListId(macro.MacroId.Value),
			macro.Name,
			[
				new ShowControlCue(
					new ShowControlCueId(macro.MacroId.Value),
					macro.Name,
					actions)
			]);
	}
}
