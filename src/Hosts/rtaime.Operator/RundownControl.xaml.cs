// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class RundownControl : UserControl
{
	public RundownControl()
	{
		InitializeComponent();
	}
	private void Rundown_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		// Text editors retain their own native text undo.
		if (Keyboard.FocusedElement is TextBoxBase || DataContext is not RundownViewModel model) return;
		if (Keyboard.Modifiers != ModifierKeys.Control) return;
		var command = e.Key == Key.Z ? model.UndoDraftCommand : e.Key == Key.Y ? model.RedoDraftCommand : null;
		if (command is null) return;
		if (!e.IsRepeat && command.CanExecute(null)) command.Execute(null);
		e.Handled = true;
	}
}
