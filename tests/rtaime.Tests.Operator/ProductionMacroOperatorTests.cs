// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Operator;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

[Collection("Operator application")]
public sealed class ProductionMacroOperatorTests
{
	[Fact]
	public void Macro_surface_uses_only_custom_interaction_controls()
	{
		var root = FindRepositoryRoot();
		var path = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ProductionMacroControl.xaml");
		var document = XDocument.Load(path);

		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeComboBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeListBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeTextBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeCheckBox");
		Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeButton");

		Assert.DoesNotContain(document.Descendants(), element =>
			element.Name.LocalName is "Button" or "ToggleButton" or "CheckBox" or "RadioButton" or
				"TextBox" or "ComboBox" or "ListBox" or "ListView" or "DataGrid" or "Slider");
	}

	[Fact]
	public void Macro_editor_exposes_closed_governed_union_without_script_surface()
	{
		var root = FindRepositoryRoot();
		var viewModel = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ProductionMacroViewModel.cs"));
		var surface = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "ProductionMacroControl.xaml"));

		foreach (var action in new[]
		{
			"ShowControlActionKind.ActivateScene",
			"ShowControlActionKind.SetPreview",
			"ShowControlActionKind.Cut",
			"ShowControlActionKind.Dissolve",
			"ShowControlActionKind.JumpMediaCue",
			"ShowControlActionKind.MediaOpen",
			"ShowControlActionKind.MediaPlay",
			"ShowControlActionKind.MediaPause",
			"ShowControlActionKind.MediaStop",
			"ShowControlActionKind.SetLayerVisibility",
			"ShowControlActionKind.StartRecording",
			"ShowControlActionKind.StopRecording",
			"ShowControlActionKind.WaitFrames",
			"ShowControlActionKind.SetAudioRouting",
			"ShowControlActionKind.RouteOutputRole",
			"ShowControlActionKind.SetAudioInputState"
		})
		{
			Assert.Contains(action, viewModel, StringComparison.Ordinal);
		}

		Assert.DoesNotContain("CodeEditor", surface, StringComparison.Ordinal);
		Assert.DoesNotContain("ScriptText", surface, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Process.Start", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeHost", viewModel, StringComparison.Ordinal);
		Assert.DoesNotContain("Provider.", viewModel, StringComparison.Ordinal);
	}

	[Fact]
	public void Scenes_and_cues_workspace_hosts_macro_surface()
	{
		var root = FindRepositoryRoot();
		var live = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "LiveSceneCueControl.xaml"));
		var window = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml.cs"));

		Assert.Contains("Header=\"MACROS\"", live, StringComparison.Ordinal);
		Assert.Contains("ProductionMacroControl", live, StringComparison.Ordinal);
		Assert.Contains("ProductionMacros = new ProductionMacroViewModel(client)", window, StringComparison.Ordinal);
	}


	[Fact]
	public void Macro_status_projections_have_no_public_setter()
	{
		foreach (var name in new[]
		{
			nameof(ProductionMacroViewModel.LastCompletedAction), nameof(ProductionMacroViewModel.CurrentAction),
			nameof(ProductionMacroViewModel.State), nameof(ProductionMacroViewModel.Status),
			nameof(ProductionMacroViewModel.Failure), nameof(ProductionMacroViewModel.StorageLabel)
		})
		{
			var property = typeof(ProductionMacroViewModel).GetProperty(name);
			Assert.NotNull(property);
			Assert.False(property.GetSetMethod(nonPublic: true)?.IsPublic == true,
				$"{name} must remain writable only by the confirmed model projection.");
		}
	}

	internal static async Task VerifyReadOnlyStatusBindingsAsync(Dispatcher dispatcher)
	{
		Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
		Assert.True(dispatcher.CheckAccess());
		var transport = new MacroStatusTransport();
		var viewModel = new ProductionMacroViewModel(new OperatorControlClient(transport));
		var changes = new List<string?>();
		viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
		await viewModel.RefreshAsync();

		using var trace = new StringWriter();
		using var listener = new TextWriterTraceListener(trace);
		var source = PresentationTraceSources.DataBindingSource;
		var previousLevel = source.Switch.Level;
		source.Switch.Level = SourceLevels.Error;
		source.Listeners.Add(listener);
		var view = new ProductionMacroControl { DataContext = viewModel };
		var window = new Window { Content = view, Width = 640, Height = 700, ShowInTaskbar = false };
		try
		{
			var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			window.ContentRendered += (_, _) => rendered.TrySetResult();
			window.Show();
			await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await SettleBindingsAsync();

			var texts = Descendants(view).OfType<TextBlock>().ToArray();
			var runs = texts.SelectMany(text => text.Inlines.OfType<Run>()).ToArray();
			Run StatusRun(string path) => Assert.Single(runs, run =>
				BindingOperations.GetBinding(run, Run.TextProperty)?.Path?.Path == path);
			var last = StatusRun(nameof(ProductionMacroViewModel.LastCompletedAction));
			var current = StatusRun(nameof(ProductionMacroViewModel.CurrentAction));
			foreach (var run in new[] { last, current })
			{
				var expression = BindingOperations.GetBindingExpression(run, Run.TextProperty);
				Assert.NotNull(expression);
				Assert.Equal(BindingStatus.Active, expression.Status);
				Assert.Equal(BindingMode.OneWay, expression.ParentBinding.Mode);
				Assert.Equal("—", run.Text);
			}

			foreach (var name in new[]
			{
				nameof(ProductionMacroViewModel.State), nameof(ProductionMacroViewModel.Status),
				nameof(ProductionMacroViewModel.Failure), nameof(ProductionMacroViewModel.StorageLabel)
			})
			{
				var target = name == nameof(ProductionMacroViewModel.State)
					? Assert.Single(Descendants(view).OfType<ContentControl>(), control =>
						BindingOperations.GetBinding(control, ContentControl.ContentProperty)?.Path?.Path == name)
					: (DependencyObject)Assert.Single(texts, text =>
						BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == name);
				var property = target is TextBlock ? TextBlock.TextProperty : ContentControl.ContentProperty;
				var expression = BindingOperations.GetBindingExpression(target, property);
				Assert.NotNull(expression);
				Assert.Equal(BindingStatus.Active, expression.Status);
				var metadata = Assert.IsAssignableFrom<FrameworkPropertyMetadata>(property.GetMetadata(target.GetType()));
				Assert.True(expression.ParentBinding.Mode == BindingMode.OneWay ||
					(expression.ParentBinding.Mode == BindingMode.Default && !metadata.BindsTwoWayByDefault),
					$"{name} must not write back to a read-only status projection.");
			}

			transport.Snapshot = transport.Snapshot with
			{
				Execution = ProductionMacroExecutionSnapshot.Idle with
				{
					State = ProductionMacroExecutionState.Completed,
					MacroId = transport.Macro.MacroId,
					LastCompletedActionId = transport.Macro.Actions[0].ActionId,
					Revision = 1
				}
			};
			await viewModel.RefreshAsync();
			await SettleBindingsAsync();
			Assert.Contains(nameof(ProductionMacroViewModel.LastCompletedAction), changes);
			Assert.Equal("CUT", viewModel.LastCompletedAction);
			Assert.Equal("CUT", last.Text);
			Assert.Equal("COMPLETED", viewModel.State);
			Assert.Equal("NONE", viewModel.Failure);

			last.SetCurrentValue(Run.TextProperty, "UNCONFIRMED DISPLAY TEXT");
			await SettleBindingsAsync();
			Assert.Equal("CUT", viewModel.LastCompletedAction);
			Assert.NotNull(BindingOperations.GetBindingExpression(last, Run.TextProperty));

			transport.Snapshot = transport.Snapshot with
			{
				Execution = transport.Snapshot.Execution with
				{
					State = ProductionMacroExecutionState.Executing,
					CurrentActionId = transport.Macro.Actions[1].ActionId,
					Revision = 2
				}
			};
			await viewModel.RefreshAsync();
			await SettleBindingsAsync();
			Assert.Equal("DISSOLVE · 12f", current.Text);

			transport.Snapshot = transport.Snapshot with
			{
				Execution = transport.Snapshot.Execution with
				{
					State = ProductionMacroExecutionState.Completed,
					CurrentActionId = null,
					LastCompletedActionId = transport.Macro.Actions[1].ActionId,
					Revision = 3
				}
			};
			await viewModel.RefreshAsync();
			await SettleBindingsAsync();
			Assert.Equal("DISSOLVE · 12f", last.Text);
			Assert.Equal(viewModel.LastCompletedAction, last.Text);
			Assert.Equal("—", current.Text);
			source.Flush();
			Assert.True(string.IsNullOrWhiteSpace(trace.ToString()), trace.ToString());
			Console.WriteLine("MACRO BINDING PASS: real compiled view, confirmed CUT/DISSOLVE status updates, read-only source and no binding trace errors.");
		}
		finally
		{
			window.Close();
			source.Listeners.Remove(listener);
			source.Switch.Level = previousLevel;
		}

		async Task SettleBindingsAsync()
		{
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
			await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
		}
	}

	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			var child = VisualTreeHelper.GetChild(root, index);
			yield return child;
			foreach (var descendant in Descendants(child))
				yield return descendant;
		}
	}

	private sealed class MacroStatusTransport : IOperatorControlTransport
	{
		public ProductionMacroDefinition Macro { get; } = new(
			ProductionMacroContractVersion.Current, ProductionMacroId.New(), "Confirmed status",
			[
				new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Cut),
				new ProductionMacroAction(ProductionMacroActionId.New(), ShowControlActionKind.Dissolve, durationFrames: 12)
			]);

		public MacroStatusTransport() =>
			Snapshot = new ProductionMacroWorkspaceSnapshot([Macro], 1, ProductionMacroExecutionSnapshot.Idle);

		public ProductionMacroWorkspaceSnapshot Snapshot { get; set; }

		public ValueTask<ProductionMacroWorkspaceSnapshot> GetProductionMacroSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(Snapshot);
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Only confirmed macro snapshots are provided.");
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Production mutations are outside status qualification.");
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Production mutations are outside status qualification.");
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Production mutations are outside status qualification.");
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new InvalidOperationException("Repository root containing rtaime.slnx could not be located.");
	}
}
