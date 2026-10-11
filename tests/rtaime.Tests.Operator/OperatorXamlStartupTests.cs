// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Operator;
using Xunit;
using Xunit.Abstractions;

namespace rtaime.Tests.Operator;

[CollectionDefinition("Operator application", DisableParallelization = true)]
public sealed class OperatorApplicationCollection
{
}

[Collection("Operator application")]
public sealed class OperatorXamlStartupTests(ITestOutputHelper output)
{
	[Fact]
	public async Task Real_MainWindow_and_viewers_load_render_all_workspaces_and_close_on_STA()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-xaml-startup", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var diagnostics = new List<string>();
		var thread = new Thread(() =>
		{
			var dispatcher = Dispatcher.CurrentDispatcher;
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
			var target = "Application resources";
			var failures = new List<Exception>();
			var threadId = Environment.CurrentManagedThreadId;
			void Capture(object? sender, FirstChanceExceptionEventArgs args)
			{
				if (Environment.CurrentManagedThreadId == threadId &&
					args.Exception is XamlParseException or ArgumentException)
					diagnostics.Add($"FIRST CHANCE [{target}] {args.Exception}");
			}

			AppDomain.CurrentDomain.FirstChanceException += Capture;
			App? app = null;
			MainWindow? window = null;
			OperatorViewModel? owner = null;
			dispatcher.BeginInvoke(new Action(async () =>
			{
				try
				{
					app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
					app.InitializeComponent();

					// Load both productive BAML views independently: MainWindow's outer line 1
					// cannot identify which nested view supplied an invalid panel child.
					target = "PreviewViewer";
					try { _ = new PreviewViewer(); }
					catch (Exception exception) { failures.Add(new InvalidOperationException(target, exception)); }
					target = "ProgramViewer";
					try { _ = new ProgramViewer(); }
					catch (Exception exception) { failures.Add(new InvalidOperationException(target, exception)); }

					target = "MainWindow";
					owner = new OperatorViewModel(synchronizationContext: SynchronizationContext.Current);
					try
					{
						window = new MainWindow(
							owner,
							new OperatorLayoutStore(Path.Combine(root, "layout.json")),
							new OperatorSessionRecoveryStore(Path.Combine(root, "session.json")));
					}
					catch (Exception exception) { failures.Add(new InvalidOperationException(target, exception)); }
					if (failures.Count != 0)
						throw new AggregateException("Productive WPF composition failed.", failures);

					Assert.NotNull(window);
					var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
					window.ContentRendered += (_, _) => rendered.TrySetResult();
					window.Width = 1600;
					window.Height = 900;
					window.Show();
					await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
					Assert.True(window.IsLoaded && window.IsVisible);

					foreach (var workspace in OperatorWorkspaceNames.All)
					{
						target = $"MainWindow/{workspace}";
						window.Shell.SelectWorkspace(workspace);
						await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
						window.UpdateLayout();
						foreach (var name in new[]
						{
							"TopBar", "WorkspaceNavigation", "LeftToolRegion", "CenterWorkspace",
							"RightInspectorRegion", "LowerTimelineRegion", "BottomTransportRegion"
						})
						{
							var region = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
							Assert.True(region.IsLoaded, $"{workspace}/{name}: pane was omitted from the real window.");
							if (region.IsVisible)
							{
								Assert.True(region.ActualWidth > 0 && region.ActualHeight > 0,
									$"{workspace}/{name}: visible pane has empty layout.");
							}
						}
						Assert.NotEmpty(VisualDescendants(window).OfType<PreviewViewer>());
						Assert.NotEmpty(VisualDescendants(window).OfType<ProgramViewer>());
						diagnostics.Add($"RENDER PASS [{workspace}] real MainWindow and monitor trees.");
					}
					Assert.DoesNotContain(diagnostics, entry => entry.StartsWith("FIRST CHANCE", StringComparison.Ordinal));
				}
				catch (Exception exception)
				{
					failures.Add(exception);
				}
				finally
				{
					try
					{
						if (window is not null)
						{
							target = "MainWindow teardown";
							var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
							window.Closed += (_, _) => closed.TrySetResult();
							window.Close();
							await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
							Assert.False(window.IsVisible);
						}
						else if (owner is not null)
						{
							await owner.DisposeAsync();
						}
						app?.Shutdown();
					}
					catch (Exception exception) { failures.Add(exception); }
					AppDomain.CurrentDomain.FirstChanceException -= Capture;
					if (failures.Count == 0)
						completed.TrySetResult();
					else
						completed.TrySetException(new AggregateException(failures));
					dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
				}
			}));
			Dispatcher.Run();
		}) { IsBackground = true, Name = "Operator XAML qualification" };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		try
		{
			await completed.Task.WaitAsync(TimeSpan.FromSeconds(45));
		}
		finally
		{
			var joined = thread.Join(TimeSpan.FromSeconds(5));
			if (joined)
			{
				foreach (var diagnostic in diagnostics)
					output.WriteLine(diagnostic);
				Directory.Delete(root, recursive: true);
			}
			Assert.True(joined, "The Operator qualification STA dispatcher did not terminate.");
		}
	}

	private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
	{
		for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			var child = VisualTreeHelper.GetChild(root, index);
			yield return child;
			foreach (var descendant in VisualDescendants(child))
				yield return descendant;
		}
	}
}
