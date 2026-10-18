// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

// Invoked by the existing productive-window STA fixture; no parallel Application or test dispatcher.
internal static class OperatorControlDesignSystemQualification
{
	internal static async Task VerifyAsync(Window owner, Dispatcher dispatcher)
	{
		var subscriptions = OperatorControlPresentation.ActiveSystemSubscriptions;
		var count = 0;
		var button = new ClickButton { Content = "Apply", Command = new CountingCommand(() => count++) };
		AutomationProperties.SetName(button, "Apply local draft");
		var editor = new RtaimeTextBox { Text = "Original", MinWidth = 180 };
		var chooser = new RtaimeComboBox { MinWidth = 180 };
		chooser.Items.Add("First"); chooser.Items.Add("Second");
		var badge = new RtaimeStatusBadge { Content = "Program", Status = RtaimeStatusKind.OnAir };
		var state = new RtaimeEmptyStatePanel { Title = "No items", Detail = "Select a source." };
		var panel = new StackPanel();
		foreach (var control in new System.Windows.Controls.Control[] { button, editor, chooser, badge, state }) panel.Children.Add(control);
		var window = new Window { Owner = owner, Content = panel, Width = 320, Height = 300, ShowInTaskbar = false };
		try
		{
			window.Show();
			await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
			foreach (var control in new System.Windows.Controls.Control[] { button, editor, chooser })
			{
				Assert.NotNull(control.Template);
				Assert.True(control.ActualHeight >= 24);
				Assert.True(control.ActualWidth >= 24);
				Assert.NotNull(control.FocusVisualStyle);
			}
			
			foreach (var background in new[] { "OperatorBackgroundBrush", "OperatorSurfaceBrush", "OperatorRaisedSurfaceBrush", "OperatorRaisedHoverBrush" })
			{
				var surface = Assert.IsType<SolidColorBrush>(button.FindResource(background)).Color;
				foreach (var foreground in new[] { "OperatorTextBrush", "OperatorSecondaryTextBrush", "OperatorMutedTextBrush", "OperatorDisabledBrush" })
				{
					var text = Assert.IsType<SolidColorBrush>(button.FindResource(foreground)).Color;
					Assert.True(Contrast(text, surface) >= 4.5, foreground + "/" + background);
					Console.WriteLine($"CONTROL CONTRAST {foreground}/{background}: {Contrast(text, surface):F3}");
				}
				Assert.True(Contrast(Assert.IsType<SolidColorBrush>(button.FindResource("OperatorFocusBrush")).Color, surface) >= 3);
			}
			var peer = UIElementAutomationPeer.CreatePeerForElement(button)!;
			Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
			Assert.Equal("Apply local draft", peer.GetName());
			button.PointerClick();
			Assert.Equal(1, count);
			button.Focus();
			button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
			button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
			Assert.Equal(2, count);
			OperatorControlPresentation.SetIsPending(button, true);
			Assert.Equal("Pending confirmation", OperatorControlPresentation.GetStateDescription(button));
			OperatorControlPresentation.SetDisabledReason(button, "Reconnect to edit.");
			button.IsEnabled = false;
			Assert.Contains("Reconnect", AutomationProperties.GetHelpText(button));
			Assert.False(peer.IsEnabled());
			OperatorControlPresentation.SetErrorText(button, "Value is invalid.");
			Assert.Contains("Error:", OperatorControlPresentation.GetStateDescription(button));
			button.IsEnabled = true;
			OperatorControlPresentation.SetErrorText(button, string.Empty);
			OperatorControlPresentation.SetIsPending(button, false);

			Assert.Equal(AutomationControlType.Edit, UIElementAutomationPeer.CreatePeerForElement(editor)!.GetAutomationControlType());
			Assert.True(InputMethod.GetIsInputMethodEnabled(editor));
			Assert.True(editor.IsUndoEnabled);
			Assert.NotNull(editor.Template.FindName("PART_ContentHost", editor));
			Assert.NotNull(editor.ContextMenu);
			editor.SelectAll(); editor.SelectedText = "Changed"; editor.Undo();
			Assert.Equal("Original", editor.Text);
			Assert.Equal("OnAir", UIElementAutomationPeer.CreatePeerForElement(badge)!.GetItemStatus());
			Assert.Equal("No items", UIElementAutomationPeer.CreatePeerForElement(state)!.GetName());

			for (var cycle = 0; cycle < 100; cycle++)
			{
				chooser.IsDropDownOpen = true;
				await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
				var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(chooser.Template.FindName("PART_Popup", chooser));
				var chrome = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
				chooser.IsDropDownOpen = false;
				Assert.False(OperatorControlPresentation.IsAnimating(chrome));
				Assert.False(popup.IsOpen);
				Assert.True(OperatorControlPresentation.ActiveSystemSubscriptions <= subscriptions + 16);
			}

			chooser.IsDropDownOpen = true;
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			var currentPopup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(chooser.Template.FindName("PART_Popup", chooser));
			var currentChrome = Assert.IsAssignableFrom<FrameworkElement>(currentPopup.Child);
			OperatorControlPresentation.SetReducedMotion(currentChrome, true);
			Assert.False(OperatorControlPresentation.IsAnimating(currentChrome));
			Assert.Equal(1, currentChrome.Opacity);
			Assert.True(chooser.IsDropDownOpen);
			OperatorControlPresentation.ApplyPreferenceSnapshot(button, false, true);
			Assert.True(OperatorControlPresentation.GetHighContrast(button));
			Assert.Equal(SystemColors.WindowTextColor, Assert.IsType<SolidColorBrush>(button.Foreground).Color);
			OperatorControlPresentation.ApplyPreferenceSnapshot(button, SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);
			chooser.IsDropDownOpen = false;
			Assert.Equal(2, count);
		}
		finally { window.Close(); }
		await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
		Assert.Equal(subscriptions, OperatorControlPresentation.ActiveSystemSubscriptions);
	}

	private static double Contrast(Color a, Color b)
	{
		static double Linear(byte value)
		{
			var s = value / 255.0;
			return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
		}
		static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
		var x = Luminance(a); var y = Luminance(b);
		return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
	}

	private sealed class ClickButton : RtaimeButton
	{
		// Calls the same ButtonBase click path used by pointer activation without global mouse injection.
		public void PointerClick() => OnClick();
	}
	private sealed class CountingCommand(Action execute) : ICommand
	{
		public event EventHandler? CanExecuteChanged { add { } remove { } }
		public bool CanExecute(object? parameter) => true;
		public void Execute(object? parameter) => execute();
	}
}
