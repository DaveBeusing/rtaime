// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Vortice.D3DCompiler;
using Xunit;
using Xunit.Abstractions;

namespace rtaime.Tests.Operator;

[CollectionDefinition("Operator application", DisableParallelization = true)]
public sealed class OperatorApplicationCollection
{
}

[Collection("Operator application")]
public sealed partial class OperatorXamlStartupTests(ITestOutputHelper output)
{
	[Fact]
	public async Task Real_MainWindow_and_viewers_load_render_all_workspaces_and_close_on_STA()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-xaml-startup", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var diagnostics = new ConcurrentQueue<string>();
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
					(args.Exception is XamlParseException or ArgumentException ||
					(target == "ProductionMacroControl" && args.Exception is InvalidOperationException)))
				{
					var detail = $"FIRST CHANCE [{target}] {args.Exception}";
					diagnostics.Enqueue(detail);
					Console.WriteLine(detail);
				}
			}

			void CaptureDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs args)
			{
				// Preserve the failure without crashing the test host before diagnostics/teardown.
				failures.Add(args.Exception);
				var detail = $"DISPATCHER FAILURE [{target}] {args.Exception}";
				diagnostics.Enqueue(detail);
				Console.WriteLine(detail);
				args.Handled = true;
			}
			dispatcher.UnhandledException += CaptureDispatcherFailure;
			AppDomain.CurrentDomain.FirstChanceException += Capture;
			App? app = null;
			MainWindow? window = null;
			OperatorViewModel? owner = null;
            var interactionPhases = new List<object>();
            OperatorDrawingSurface[] observedSurfaces = [];
			dispatcher.BeginInvoke(new Action(async () =>
			{
				try
				{
					app = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
					app.InitializeComponent();

					target = "GPU descriptor boundary";
					GpuDrawingSurfaceTests.VerifyDescriptorBoundary(dispatcher);

					target = "ProductionMacroControl";
					await ProductionMacroOperatorTests.VerifyReadOnlyStatusBindingsAsync(dispatcher);

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
							new OperatorSessionRecoveryStore(Path.Combine(root, "session.json")),
                            new OperatorLibraryPreferenceStore(Path.Combine(root, "library.json")));
					}
					catch (Exception exception) { failures.Add(new InvalidOperationException(target, exception)); }
					if (failures.Count != 0)
						throw new AggregateException("Productive WPF composition failed.", failures);

					Assert.NotNull(window);
					app.MainWindow = window;
					Console.WriteLine("COMPOSITION PASS [MainWindow] compiled resource dictionaries and full window tree.");
					var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
					window.ContentRendered += (_, _) =>
					{
						Console.WriteLine("MAIN CONTENT RENDERED EVENT");
						rendered.TrySetResult();
					};
					window.Loaded += (_, _) =>
					{
						foreach (var surface in VisualDescendants(window).OfType<OperatorDrawingSurface>())
						{
							surface.DiagnosticEmitted += diagnostic =>
								Console.WriteLine($"PRIMARY GPU [{surface.GetType().Name}] {diagnostic.Code}: {diagnostic.Message}; {JsonSerializer.Serialize(diagnostic.Dimensions)}; {diagnostic.Exception}");
						}
					};
					var mainRenderCount = 0;
					EventHandler mainRendering = (_, _) => mainRenderCount++;
					CompositionTarget.Rendering += mainRendering;
					window.Closed += (_, _) => CompositionTarget.Rendering -= mainRendering;
					window.Shell.ExitFullscreenCommand.Execute(null);
					window.MinWidth = window.Width = 1600;
					window.MinHeight = window.Height = 900;
					Console.WriteLine("WINDOW SHOW BEGIN [MainWindow]");
					window.Show();
					Console.WriteLine("WINDOW SHOW RETURN [MainWindow]");
					try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
					catch (TimeoutException)
					{
						Console.WriteLine($"MAIN RENDER TIMEOUT: composition frames={mainRenderCount}; window={window.ActualWidth}x{window.ActualHeight}; measure={window.IsMeasureValid}; arrange={window.IsArrangeValid}.");
						foreach (var surface in VisualDescendants(window).OfType<OperatorDrawingSurface>())
							Console.WriteLine($"PRIMARY GPU LAYOUT [{surface.GetType().Name}]: requested={surface.Width}x{surface.Height}; actual={surface.ActualWidth}x{surface.ActualHeight}; desired={surface.DesiredSize}; render={surface.RenderSize}; measure={surface.IsMeasureValid}; arrange={surface.IsArrangeValid}; state={surface.SurfaceState}; creates={surface.TargetCreateCount}; releases={surface.TargetReleaseCount}; detail={surface.SurfaceDetail}.");
						throw;
					}
					Console.WriteLine("CONTENT RENDERED PASS [MainWindow]");
					await OperatorControlDesignSystemQualification.VerifyAsync(window, dispatcher);
					Assert.True(window.IsLoaded && window.IsVisible);

                    // Workspace pane/viewer assertions run in every row of the existing 40-case matrix below.
                    observedSurfaces = VisualDescendants(window).OfType<OperatorDrawingSurface>().ToArray();
                    var identity = (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId);
                    var monitoring = window.Monitoring;
                    async Task Qualify(string name, Func<Task> execute)
                    {
                        target = name;
                        var started = Stopwatch.GetTimestamp();
                        var status = "FAILED";
                        try
                        {
                            await execute();
                            Assert.Equal(identity, (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
                            Assert.Same(monitoring, window.Monitoring);
                            Assert.All(observedSurfaces, surface => Assert.InRange(surface.RenderingSubscriptionBalance, 0, 1));
                            var posted = Stopwatch.GetTimestamp();
                            var queueMilliseconds = await dispatcher.InvokeAsync(
                                () => Stopwatch.GetElapsedTime(posted).TotalMilliseconds, DispatcherPriority.Input)
                                .Task.WaitAsync(TimeSpan.FromSeconds(5));
                            Assert.InRange(queueMilliseconds, 0, 5000);
                            status = "PASS";
                            interactionPhases.Add(new { name, status, elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                                dispatcherMilliseconds = queueMilliseconds,
                                renderingSubscriptions = observedSurfaces.Sum(surface => surface.RenderingSubscriptionBalance) });
                        }
                        finally
                        {
                            if (status != "PASS") interactionPhases.Add(new { name, status,
                                elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                        }
                    }
                    await Qualify("Combined layouts and QoL focus transitions", async () =>
                    {
                        await OperatorCombinedVisualQualification.VerifyWindowAsync(window, dispatcher);
                        // Hidden templates may realize more native surfaces in the matrix; retain every instance for teardown.
                        observedSurfaces = observedSurfaces.Concat(VisualDescendants(window).OfType<OperatorDrawingSurface>()).Distinct().ToArray();
                    });
                    await Qualify("Library smart bins, menus and virtualization", () => MediaLibraryBrowsingTests.VerifyWindowAsync(window, dispatcher));
                    await Qualify("Timeline draft ergonomics", () => TimelineErgonomicsTests.VerifyWindowAsync(window, dispatcher));
                    await Qualify("Live console actions and focus", () => LiveConsoleWindowQualification.VerifyWindowAsync(window, dispatcher));
                    await Qualify("Inspector precision", () => InspectorPrecisionTests.VerifyWindowAsync(window, dispatcher));
                    await Qualify("Compositing layer and Inspector editing", () => CompositingWorkspaceQualification.VerifyWindowAsync(window, dispatcher));
                    await Qualify("Inspector draft undo and transfer", () => InspectorDraftTransactionTests.VerifyWindowAsync(window, dispatcher));
                    await Qualify("Docking, focus, cinema and templates", () => OperatorDockingWindowTests.VerifyAsync(window, dispatcher));
                    await Qualify("Palette and shortcut editing", () => OperatorCommandPaletteTests.VerifyWindowAsync(window, dispatcher));
                    await Qualify("Recent, favorites and help", () => OperatorLibraryTests.VerifyWindowAsync(window, dispatcher));
                    await Qualify("Preflight, activity and notifications", () => ShowReadinessOperatorTests.VerifyWindowAsync(window, dispatcher));
                    await Qualify("GPU surface lifetime", () => GpuDrawingSurfaceTests.VerifySurfaceLifetimeAsync(dispatcher));
					var resumedRender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
					EventHandler rendering = (_, _) => resumedRender.TrySetResult();
					CompositionTarget.Rendering += rendering;
					try
					{
						window.InvalidateVisual();
						await resumedRender.Task.WaitAsync(TimeSpan.FromSeconds(10));
					}
					finally { CompositionTarget.Rendering -= rendering; }
					Assert.True(window.IsLoaded && window.IsVisible);
					Console.WriteLine("GPU PRIMARY WINDOW PASS: productive MainWindow continues rendering after secondary surface teardown.");
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
							var teardown = Stopwatch.StartNew();
							while (!SessionClosedCleanly(Path.Combine(root, "session.json")) &&
								teardown.Elapsed < TimeSpan.FromSeconds(10))
								await Task.Delay(25);
							Assert.True(SessionClosedCleanly(Path.Combine(root, "session.json")),
								"Closed fired before the real asynchronous service/session teardown completed.");
							// Closed precedes WPF's deferred Unloaded broadcast; observe that boundary before counting handlers.
                            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
							Assert.False(window.IsVisible);
                            Assert.Empty(window.Docking.FloatingWindows);
                            Assert.Equal(0, window.InspectorView.InspectorSubscriptionCount);
                            Assert.All(observedSurfaces, surface => Assert.Equal(0, surface.RenderingSubscriptionBalance));
						}
						else if (owner is not null)
						{
							await owner.DisposeAsync();
						}
						app?.Shutdown();
					}
					catch (Exception exception) { failures.Add(exception); }
	                try
                {
                    var evidenceRoot = Environment.GetEnvironmentVariable("RTAIME_OPERATOR_VISUAL_EVIDENCE");
                    if (!string.IsNullOrWhiteSpace(evidenceRoot))
                    {
                        Directory.CreateDirectory(evidenceRoot);
                        File.WriteAllText(Path.Combine(evidenceRoot, "interaction-ui.json"), JsonSerializer.Serialize(new
                        {
                            sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),
                            sdk = Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK"),
                            status = failures.Count == 0 ? "PASS" : "FAILED",
                            scope = "Disconnected real WPF; Input queue delay and event subscription balance; physical touch, mixed-monitor DPI and connected production latency UNVERIFIED",
                            phases = interactionPhases,
                            finalRenderingSubscriptions = observedSurfaces.Sum(surface => surface.RenderingSubscriptionBalance),
                            failures = failures.Select(failure => failure.ToString()).ToArray()
                        }, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch (Exception exception) { failures.Add(exception); }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
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
		catch (TimeoutException)
		{
			await CaptureQualificationStacksAsync();
			throw;
		}
		finally
		{
			var joined = thread.Join(TimeSpan.FromSeconds(5));
			foreach (var diagnostic in diagnostics.ToArray())
				output.WriteLine(diagnostic);
			if (joined)
				Directory.Delete(root, recursive: true);
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
		catch
		{
			output.WriteLine(ReadLogs(root));
			throw;
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
			}
			try { Directory.Delete(root, recursive: true); }
			catch (IOException exception) { output.WriteLine($"Launch evidence retained at {root}: {exception.Message}"); }
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


	[Theory]
	[InlineData(typeof(GpuMonitorPresentationSurface), "ShaderSource", "VSMain", "vs_4_0")]
	[InlineData(typeof(GpuMonitorPresentationSurface), "ShaderSource", "PSMain", "ps_4_0")]
	[InlineData(typeof(GpuMonitorPresentationSurface), "AnalysisShaderSource", "CSMain", "cs_5_0")]
	[InlineData(typeof(GpuMediaCompareSurface), "ShaderSource", "VSMain", "vs_4_0")]
	[InlineData(typeof(GpuMediaCompareSurface), "ShaderSource", "PSMain", "ps_4_0")]
	[InlineData(typeof(GpuScopeAnalysisSurface), "ShaderSource", "CSMain", "cs_5_0")]
	public void Productive_surface_shaders_compile_before_window_startup(
		Type surfaceType, string fieldName, string entryPoint, string profile)
	{
		var field = surfaceType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(field);
		var source = Assert.IsType<string>(field.GetValue(null));
		var bytecode = Compiler.Compile(source, entryPoint, $"{surfaceType.Name}.hlsl", profile);
		Assert.True(bytecode.Span.Length > 0, $"{surfaceType.Name}/{entryPoint}/{profile}: empty shader bytecode.");
	}


	private static async Task CaptureQualificationStacksAsync()
	{
		var executable = Environment.GetEnvironmentVariable("RTAIME_STACK_TOOL");
		if (string.IsNullOrWhiteSpace(executable)) return;
		var start = new ProcessStartInfo(executable)
		{
			UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
			RedirectStandardOutput = true, RedirectStandardError = true
		};
		start.ArgumentList.Add("report");
		start.ArgumentList.Add("--process-id");
		start.ArgumentList.Add(Environment.ProcessId.ToString());
		try
		{
			using var process = Process.Start(start)!;
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
			catch (TimeoutException)
			{
				process.Kill(entireProcessTree: true);
				Console.WriteLine("GPU STA stack collection exceeded its diagnostic deadline.");
				return;
			}
			Console.WriteLine($"GPU STA MANAGED STACKS (diagnostic exit {process.ExitCode}):\n{await stdout}\n{await stderr}");
		}
		catch (Exception exception)
		{
			Console.WriteLine($"GPU STA stack collection unavailable: {exception.Message}");
		}
	}

	private static bool SessionClosedCleanly(string path)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		return document.RootElement.GetProperty("CleanShutdown").GetBoolean();
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
