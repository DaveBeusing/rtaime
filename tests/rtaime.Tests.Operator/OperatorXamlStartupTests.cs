// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Xml;
using System.Xml.Linq;
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
				{
					var detail = $"FIRST CHANCE [{target}] {args.Exception}";
					diagnostics.Add(detail);
					Console.WriteLine(detail);
				}
			}

			void CaptureDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs args)
			{
				// Preserve the failure without crashing the test host before diagnostics/teardown.
				failures.Add(args.Exception);
				diagnostics.Add($"DISPATCHER FAILURE [{target}] {args.Exception}");
				args.Handled = true;
			}
			dispatcher.UnhandledException += CaptureDispatcherFailure;
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
					dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
				}
			}));
			try { Dispatcher.Run(); }
			catch (Exception exception) { failures.Add(exception); }
			finally
			{
				AppDomain.CurrentDomain.FirstChanceException -= Capture;
				dispatcher.UnhandledException -= CaptureDispatcherFailure;
				if (failures.Count == 0)
					completed.TrySetResult();
				else
					completed.TrySetException(new AggregateException(failures));
			}
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

	[Fact]
	public async Task Windows_Operator_executable_shows_its_real_window_and_exits_cleanly()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-operator-launch", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
		var executable = Path.Combine(FindRepositoryRoot(), "src", "Hosts", "rtaime.Operator",
			"bin", configuration, "net10.0-windows", "rtaime.Operator.exe");
		Assert.True(File.Exists(executable), $"Build the Operator before qualification: {executable}");
		var start = new ProcessStartInfo(executable)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		start.ArgumentList.Add($"--log-root={root}");
		start.Environment["RTAIME_CONTROL_ENDPOINT"] = $"rtaime.qualification.{Guid.NewGuid():N}";
		start.Environment["RTAIME_RUNTIME_ENDPOINT"] = $"rtaime.qualification.{Guid.NewGuid():N}";
		start.Environment["RTAIME_OPERATOR_MONITOR_ENDPOINT"] = $"rtaime.qualification.{Guid.NewGuid():N}";
		start.Environment.Remove("RTAIME_APPHOST_LIFECYCLE_FILE");
		start.Environment.Remove("RTAIME_LOG_SESSION_ID");
		using var process = Process.Start(start)!;
		try
		{
			var deadline = Stopwatch.StartNew();
			string logs = string.Empty;
			while (deadline.Elapsed < TimeSpan.FromSeconds(15))
			{
				logs = ReadLogs(root);
				Assert.DoesNotContain("operator.dispatcher-unhandled-exception", logs, StringComparison.Ordinal);
				Assert.DoesNotContain("XamlParseException", logs, StringComparison.Ordinal);
				Assert.False(process.HasExited, $"Operator exited before showing its real window. {logs}");
				if (logs.Contains("operator.window-shown", StringComparison.Ordinal))
					break;
				await Task.Delay(100);
			}
			Assert.Contains("operator.window-shown", logs, StringComparison.Ordinal);
			Assert.True(process.CloseMainWindow(), "The shown Operator window did not accept a close request.");
			await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
			Assert.Equal(0, process.ExitCode);
			logs = ReadLogs(root);
			Assert.Contains("operator.exit", logs, StringComparison.Ordinal);
			Assert.DoesNotContain("operator.dispatcher-unhandled-exception", logs, StringComparison.Ordinal);
			output.WriteLine("WINDOWS LAUNCH PASS: productive Operator executable showed MainWindow and exited cleanly.");
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
			}
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Productive_panel_children_contain_only_elements_or_real_whitespace()
	{
		var root = Path.Combine(FindRepositoryRoot(), "src", "Hosts", "rtaime.Operator");
		var violations = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
			.Where(path => !Path.GetRelativePath(root, path).StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
			.SelectMany(path => InvalidPanelText(XDocument.Load(path, LoadOptions.SetLineInfo))
				.Select(node => $"{Path.GetRelativePath(root, path)}:{((IXmlLineInfo)node).LineNumber}: {node.Value}"))
			.ToArray();
		Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void Panel_text_guard_detects_escaped_whitespace_but_allows_TextBlock_content()
	{
		var bad = XDocument.Parse(@"<StackPanel>\n\t<TextBlock>Title</TextBlock></StackPanel>");
		Assert.Single(InvalidPanelText(bad));
		var good = XDocument.Parse("<StackPanel>\n\t<TextBlock>Title</TextBlock></StackPanel>");
		Assert.Empty(InvalidPanelText(good));
	}

	private static IEnumerable<XText> InvalidPanelText(XDocument document) =>
		document.Descendants()
			.Where(element => element.Name.LocalName is "Grid" or "StackPanel" or "WrapPanel" or
				"DockPanel" or "Canvas" or "UniformGrid" or "VirtualizingStackPanel" or
				"VirtualizingWrapPanel" || element.Name.LocalName.EndsWith(".Children", StringComparison.Ordinal))
			.SelectMany(element => element.Nodes().OfType<XText>())
			.Where(node => !string.IsNullOrWhiteSpace(node.Value));

	private static string ReadLogs(string root)
	{
		var contents = new List<string>();
		foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using var reader = new StreamReader(stream);
			contents.Add(reader.ReadToEnd());
		}
		foreach (var path in Directory.EnumerateFiles(root, "operator-crash-*.log", SearchOption.AllDirectories))
			contents.Add(File.ReadAllText(path));
		return string.Join(Environment.NewLine, contents);
	}

	private static string FindRepositoryRoot()
	{
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
		throw new InvalidOperationException("Repository root containing rtaime.slnx could not be located.");
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
