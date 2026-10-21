// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using rtaime.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Media.Contracts;
using PixelFormat = rtaime.Media.Contracts.PixelFormat;
using rtaime.Operator;
using rtaime.Operator.Controls;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Operator;

internal static class MonitorPresentationQualification
{
	internal static async Task RunIsolatedAsync()
	{
		const string childMarker = "RTAIME_VIEWER_QUALIFICATION_CHILD";
		if (Environment.GetEnvironmentVariable(childMarker) == "1")
		{
			await RunStaAsync();
			return;
		}
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
			directory = directory.Parent;
		Assert.NotNull(directory);
		var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
		var start = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = directory.FullName, UseShellExecute = false, CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
		};
		foreach (var argument in new[]
		{
			"test", Path.Combine(directory.FullName, "tests", "rtaime.Tests.Operator", "rtaime.Tests.Operator.csproj"),
			"--configuration", configuration, "--no-build", "--no-restore", "--filter",
			"FullyQualifiedName=rtaime.Tests.Operator.GpuDrawingSurfaceTests.Productive_program_viewer_and_resource_epochs_remain_isolated",
			"--logger", "console;verbosity=detailed"
		}) start.ArgumentList.Add(argument);
		start.Environment[childMarker] = "1";
		using var process = Process.Start(start)!;
		Console.WriteLine($"VIEWER ISOLATION START: parentPid={Environment.ProcessId}; childPid={process.Id}; ownership=fixture-owned-child; timeout=45s.");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		try
		{
			await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
			var result = await stdout;
			var errors = await stderr;
			Console.WriteLine(result);
			Console.WriteLine(errors);
			Assert.True(process.ExitCode == 0,
				$"Isolated productive viewer exit={process.ExitCode}, childPid={process.Id}: {result}\n{errors}");
			Assert.Contains("VIEWER SHARED GPU PASS", result);
			Assert.Contains("VIEWER PROGRAM PASS", result);
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
				Console.WriteLine(await stdout);
				Console.WriteLine(await stderr);
			}
		}
	}

	private static async Task RunStaAsync()
	{
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			var dispatcher = Dispatcher.CurrentDispatcher;
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
			Exception? failure = null;
			dispatcher.UnhandledException += (_, args) => { failure ??= args.Exception; args.Handled = true; };
			dispatcher.BeginInvoke(new Action(async () =>
			{
				OperatorResourceApplication? app = null;
				try
				{
					app = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
					app.InitializeComponent();
					VerifyFractionalPointerZoom();
					await VerifyAsync(dispatcher);
				}
				catch (Exception exception) { failure = exception; }
				finally
				{
					try { app?.Shutdown(); }
					catch (Exception exception) { failure ??= exception; }
					dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
				}
			}));
			try { Dispatcher.Run(); }
			catch (Exception exception) { failure ??= exception; }
			if (failure is null) completed.TrySetResult();
			else completed.TrySetException(failure);
		}) { IsBackground = true, Name = "Program viewer qualification" };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		await completed.Task.WaitAsync(TimeSpan.FromSeconds(45));
		Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Viewer qualification dispatcher did not terminate.");
	}

	private static void VerifyFractionalPointerZoom()
	{
		// Model regression: the real Program/interop qualification below uses actual Runtime observations.
		var bitmap = BitmapSource.Create(1920, 1080, 96, 96, PixelFormats.Bgra32,
			null, new byte[1920 * 1080 * 4], 1920 * 4);
		bitmap.Freeze();
		foreach (var dpi in new[] { 1d, 1.25, 1.5, 2d })
		{
			var monitor = new MonitorView { Frame = bitmap, State = "LIVE" };
			monitor.UpdatePresentationViewport(801, 451, dpi, dpi);
			monitor.ZoomMode = "100%";
			var origin = (monitor.PresentationOffsetX, monitor.PresentationOffsetY);
			for (var cycle = 0; cycle < 100; cycle++)
			{
				monitor.ZoomPresentationAt(1, 333.36, 162.64);
				monitor.ZoomPresentationAt(-1, 333.36, 162.64);
			}
			Assert.Equal(origin, (monitor.PresentationOffsetX, monitor.PresentationOffsetY));
			Assert.Equal(1920 / dpi, monitor.PresentationWidth);
			Assert.Equal(1080 / dpi, monitor.PresentationHeight);
		}
		Console.WriteLine("VIEWER ZOOM PASS: actual local viewer entry points retain the exact native origin after 100 pointer zoom cycles at 100/125/150/200% DPI; rounded physical viewport is shared with rendering.");
	}

	internal static async Task VerifyAsync(Dispatcher dispatcher)
	{
		dispatcher.VerifyAccess();
		var root = Path.Combine(Path.GetTempPath(), "rtaime-viewer", Guid.NewGuid().ToString("N"));
		var runtimeEndpoint = $"rtaime.viewer.runtime.{Guid.NewGuid():N}";
		var controlEndpoint = $"rtaime.viewer.control.{Guid.NewGuid():N}";
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint, RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25), DurabilityRoot = root
		});
		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		var viewer = new ProgramViewer { SourceName = "Confirmed Program / full accessible source identity",
			ProductionFormat = "1920×1080 · 50p · RGBA8", ShowTechnicalOverlay = false };
		var host = new Grid();
		host.Children.Add(viewer);
		var window = new Window { Width = 980, Height = 700, Content = host, ShowInTaskbar = false };
		GpuMonitorPresentationSurface? surface = null;
		CancellationTokenSource? readStop = null;
		IAsyncEnumerator<MonitoringFrame>? frames = null;
		Exception? visualFailure = null;
		try
		{
			await WaitAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready &&
				control.Control?.HasAuthoritativeState == true, dispatcher, "confirmed host startup");
			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
				controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
			var before = await client.SynchronizeAsync();
			readStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			frames = new NamedPipeOperatorMonitoringTransport(runtime.MonitoringEndpoint)
				.ReadFramesAsync(readStop.Token).GetAsyncEnumerator(readStop.Token);
			async Task<MonitoringFrame> NextProgramAsync()
			{
				while (await frames.MoveNextAsync())
					if (frames.Current.Descriptor.StreamKind == MonitoringStreamKind.Program && frames.Current.HasFallbackPayload)
						return frames.Current;
				throw new InvalidOperationException("Runtime monitoring ended before a Program observation.");
			}
			void Present(MonitoringFrame frame)
			{
				var descriptor = frame.Descriptor;
				var bgra = new byte[frame.Pixels.Length];
				MonitoringDisplayTransform.ConvertRgbaToBgra(frame.Pixels.Span, bgra, descriptor.Color);
				var bitmap = BitmapSource.Create(checked((int)descriptor.Width), checked((int)descriptor.Height),
					96, 96, PixelFormats.Bgra32, null, bgra, checked((int)descriptor.Width * 4));
				bitmap.Freeze();
				viewer.SourceId = descriptor.SourceId.ToString();
				viewer.Frame = bitmap;
				viewer.State = "LIVE";
			}
			var first = await NextProgramAsync();
			Present(first);
			window.Show();
			window.UpdateLayout();
			var presentation = Assert.Single(Descendants(viewer).OfType<RtaimeMonitorPresentation>());
			var viewport = Assert.IsAssignableFrom<FrameworkElement>(
				presentation.Template.FindName("PART_MediaViewport", presentation));
			surface = Assert.Single(Descendants(viewer).OfType<GpuMonitorPresentationSurface>());
			Assert.True(viewer.HasPresentedFrame);
			Assert.Contains("REDUCED CPU 320×180", viewer.PresentationQuality);
			Assert.False(viewer.IsGpuPresentationActive);
			var source = viewer.SourceId;
			Assert.True(viewport.Focus());
			var nativeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(viewport),
				Environment.TickCount, Key.D1) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
			viewport.RaiseEvent(nativeKey);
			Assert.True(nativeKey.Handled);
			Assert.Equal("100%", viewer.ZoomMode);
			Assert.Equal(320 / viewer.DpiScaleX, viewer.PresentationWidth, 6);
			Assert.Equal(180 / viewer.DpiScaleY, viewer.PresentationHeight, 6);
			var zoomKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(viewport),
				Environment.TickCount, Key.Add) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
			viewport.RaiseEvent(zoomKey);
			Assert.True(zoomKey.Handled);
			Assert.Equal("FREE", viewer.ZoomMode);
			viewer.FillCommand.Execute(null);
			Assert.Contains("CROPS", viewer.PresentationQuality);
			viewer.FitCommand.Execute(null);
			Assert.True(viewer.BeginRoiSelectionAt(viewport.ActualWidth / 2, viewport.ActualHeight / 2));
			viewer.UpdateRoiSelectionAt(viewport.ActualWidth * .65, viewport.ActualHeight * .6);
			viewer.EndRoiSelection();
			Assert.True(viewer.IsRoiActive);
			var roi = viewer.InspectionRoi;
			viewer.ShowSafeArea = viewer.ShowGrid = viewer.ShowCenterMark = true;
			foreach (var width in new[] { 700d, 1100d, 800d })
			{
				window.Width = width;
				await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
				Present(await NextProgramAsync());
				Assert.Equal(roi, viewer.InspectionRoi);
				Assert.Equal(source, viewer.SourceId);
			}
			await WaitAsync(() => surface.SurfaceState == OperatorSurfaceState.Ready &&
				surface.TextureWidth == checked((int)viewer.ViewportPixelWidth), dispatcher, "settled real viewer target");
			var creates = surface.TargetCreateCount;
			for (var pass = 0; pass < 12; pass++)
			{
				viewer.ZoomInCommand.Execute(null);
				viewer.ZoomOutCommand.Execute(null);
				surface.Invalidate();
			}
			Assert.Equal(creates, surface.TargetCreateCount);
			viewer.ShowRedCommand.Execute(null);
			Assert.True(viewer.IsChannelInspectionUnavailable);
			Assert.StartsWith("RED · GPU CHANNEL VIEW UNAVAILABLE", viewer.PresentationQuality);
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
			Assert.Contains(Descendants(presentation).OfType<TextBlock>(),
				text => text.IsVisible && text.Text.Contains("GPU CHANNEL VIEW UNAVAILABLE", StringComparison.Ordinal));
			viewer.ShowRgbCommand.Execute(null);
			Assert.StartsWith("ROI · GPU ANALYSIS UNAVAILABLE", viewer.PresentationQuality);
			viewer.ClearRoiCommand.Execute(null);
			Assert.DoesNotContain("ANALYSIS UNAVAILABLE", viewer.PresentationQuality);
			viewer.State = "DISCONNECTED";
			Assert.True(viewer.HasPresentedFrame);
			Assert.Contains("RETAINED", viewer.ObservationStatus);
			Assert.Contains("NOT CURRENT", viewer.PresentationQuality);
			var next = await NextProgramAsync();
			Assert.True(next.Descriptor.Timing.SequenceNumber > first.Descriptor.Timing.SequenceNumber);
			Present(next);
			Assert.DoesNotContain("RETAINED", viewer.ObservationStatus);
			var after = await client.SynchronizeAsync();
			Assert.Equal(before.Production.Revision, after.Production.Revision);
			Assert.Equal(source, viewer.SourceId);

			var evidence = Environment.GetEnvironmentVariable("RTAIME_OPERATOR_VISUAL_EVIDENCE");
			if (!string.IsNullOrWhiteSpace(evidence))
			{
				Directory.CreateDirectory(evidence);
				window.UpdateLayout();
				var image = new RenderTargetBitmap(checked((int)Math.Ceiling(viewer.ActualWidth)),
					checked((int)Math.Ceiling(viewer.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
				image.Render(viewer);
				var encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(image));
				using var output = File.Create(Path.Combine(evidence, "program-viewer-fallback.png"));
				encoder.Save(output);
			}
			
			await VerifySharedGpuAsync(viewer, surface, dispatcher);
			var restored = await NextProgramAsync();
			Present(restored);
			Assert.True(restored.Descriptor.Timing.SequenceNumber > next.Descriptor.Timing.SequenceNumber);
			Assert.DoesNotContain("NOT CURRENT", viewer.PresentationQuality);
			Assert.Equal(before.Production.Revision, (await client.SynchronizeAsync()).Production.Revision);
			host.Children.Remove(viewer);
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
			host.Children.Add(viewer);
			window.UpdateLayout();
			Assert.Same(presentation, Assert.Single(Descendants(viewer).OfType<RtaimeMonitorPresentation>()));
			Assert.Equal(source, viewer.SourceId);
			viewer.Frame = null;
			Assert.False(viewer.HasPresentedFrame);
			Assert.False(viewer.IsRoiActive);
			Assert.False(viewer.BeginRoiSelectionAt(10, 10));
			Console.WriteLine("VIEWER PROGRAM VISUAL PASS: confirmed live Runtime Program frames, productive WPF viewer, native fallback, zoom/ROI, shared GPU readback, retained/stale evidence and resource epoch isolation. Host shutdown is qualified separately.");
		}
		catch (Exception exception)
		{
			visualFailure = exception;
			Console.WriteLine($"VIEWER VISUAL FAIL: pid={Environment.ProcessId}; {exception}");
		}
		finally
		{
			var errors = new List<Exception>();
			async Task PhaseAsync(string name, Func<Task> action)
			{
				var elapsed = Stopwatch.StartNew();
				Console.WriteLine($"VIEWER TEARDOWN BEGIN: phase={name}; pid={Environment.ProcessId}; thread={Environment.CurrentManagedThreadId}");
				try
				{
					await action();
					Console.WriteLine($"VIEWER TEARDOWN PASS: phase={name}; elapsedMs={elapsed.Elapsed.TotalMilliseconds:F1}");
				}
				catch (Exception exception)
				{
					Console.WriteLine($"VIEWER TEARDOWN FAIL: phase={name}; elapsedMs={elapsed.Elapsed.TotalMilliseconds:F1}; {exception}");
					errors.Add(new InvalidOperationException($"Fixture teardown failed during '{name}'.", exception));
				}
			}

			// The monitoring lease and productive WPF/COM presentation owner must be
			// retired before asking the authoritative host to drain. Both hosts are
			// in-process fixture owners, not adopted external child processes.
			await PhaseAsync("monitor-cancel", () =>
			{
				readStop?.Cancel();
				return Task.CompletedTask;
			});
			if (frames is not null)
				await PhaseAsync("monitor-dispose", () => frames.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
			await PhaseAsync("monitor-token-dispose", () =>
			{
				readStop?.Dispose();
				return Task.CompletedTask;
			});
			await PhaseAsync("viewer-unload", async () =>
			{
				try
				{
					viewer.GpuFrame = null;
					viewer.Frame = null;
					host.Children.Remove(viewer);
				}
				finally { window.Close(); }
				await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
				if (surface is not null)
				{
					Assert.Equal(OperatorSurfaceState.Unloaded, surface.SurfaceState);
					Assert.Null(surface.ColorTexture);
					Assert.Null(surface.ColorTextureView);
					Assert.Equal(0, surface.RenderingSubscriptionBalance);
					Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
				}
			});
			await PhaseAsync("control-cancel", () =>
			{
				controlStop.Cancel();
				return Task.CompletedTask;
			});
			await PhaseAsync("control-stop", async () =>
			{
				var exit = await controlRun.WaitAsync(TimeSpan.FromSeconds(12));
				Console.WriteLine($"VIEWER HOST EXIT: owner=fixture-owned-in-process; host=ControlHost; pid={Environment.ProcessId}; " +
					$"exit={exit}; state={control.Lifecycle.State}; health={control.Lifecycle.Health}; " +
					$"detail={control.Lifecycle.Detail}; shutdownPhases={JsonSerializer.Serialize(control.ShutdownPhases)}");
				Assert.Equal(ControlHostExitCode.Success, exit);
				Assert.Equal(ControlHostProcessState.Stopped, control.Lifecycle.State);
			});
			await PhaseAsync("runtime-cancel", () =>
			{
				runtimeStop.Cancel();
				return Task.CompletedTask;
			});
			await PhaseAsync("runtime-stop", async () =>
			{
				var exit = await runtimeRun.WaitAsync(TimeSpan.FromSeconds(12));
				Console.WriteLine($"VIEWER HOST EXIT: owner=fixture-owned-in-process; host=RuntimeHost; pid={Environment.ProcessId}; " +
					$"exit={exit}; state={runtime.Lifecycle.State}; health={runtime.Lifecycle.Health}; detail={runtime.Lifecycle.Detail}");
				Assert.Equal(RuntimeHostExitCode.Success, exit);
				Assert.Equal(RuntimeHostProcessState.Stopped, runtime.Lifecycle.State);
			});
			if (controlRun.IsCompleted && runtimeRun.IsCompleted)
				await PhaseAsync("durability-root-cleanup", () =>
				{
					if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
					return Task.CompletedTask;
				});
			else
				errors.Add(new InvalidOperationException("Fixture-owned hosts did not finish; retained the durability root for diagnosis."));

			if (visualFailure is not null)
				errors.Insert(0, new InvalidOperationException("Productive viewer qualification failed before teardown.", visualFailure));
			if (errors.Count != 0)
				throw new AggregateException("Viewer/host qualification did not complete cleanly; see individually attributed teardown phases.", errors);

			Console.WriteLine("VIEWER SHUTDOWN PASS: monitoring reader disconnected; WPF/COM target leases released; ControlHost and RuntimeHost both exited Success and reached Stopped.");
			Console.WriteLine("VIEWER PROGRAM PASS: real RuntimeHost Program frames, productive WPF/GPU viewer, DPI/zoom, ROI and resource epochs remained correct through successful owned host teardown.");
		}
	}

	private static async Task VerifySharedGpuAsync(ProgramViewer viewer, GpuMonitorPresentationSurface surface, Dispatcher dispatcher)
	{
		// A synthetic D3D pattern tests Operator interop, separately from the live Runtime observation.
		ID3D11Device1? device = null;
		ID3D11DeviceContext1? context = null;
		var verifyPixel = false;
		var pixelVerified = false;
		void Draw(object? sender, OperatorSurfaceDrawEventArgs args)
		{
			device = args.Device;
			context = args.Context;
			if (!verifyPixel || !viewer.IsGpuPresentationActive || pixelVerified) return;
			var description = surface.ColorTexture!.Description;
			description.Usage = ResourceUsage.Staging;
			description.BindFlags = BindFlags.None;
			description.MiscFlags = ResourceOptionFlags.None;
			description.CPUAccessFlags = CpuAccessFlags.Read;
			using var readback = args.Device.CreateTexture2D(description);
			args.Context.CopyResource(readback, surface.ColorTexture);
			var mapped = args.Context.Map(readback, 0, MapMode.Read);
			try
			{
				var offset = checked((int)(mapped.RowPitch * (description.Height / 2) + (description.Width / 2) * 4));
				Assert.Equal(0, Marshal.ReadByte(mapped.DataPointer, offset));
				Assert.Equal(0, Marshal.ReadByte(mapped.DataPointer, offset + 1));
				Assert.Equal(255, Marshal.ReadByte(mapped.DataPointer, offset + 2));
				Assert.Equal(255, Marshal.ReadByte(mapped.DataPointer, offset + 3));
				pixelVerified = true;
			}
			finally { args.Context.Unmap(readback, 0); }
		}
		surface.Draw += Draw;
		try
		{
			surface.Invalidate();
			await WaitAsync(() => device is not null && context is not null, dispatcher, "borrow existing viewer device");
			using var texture = device!.CreateTexture2D(new Texture2DDescription
			{
				Width = 1920, Height = 1080, MipLevels = 1, ArraySize = 1,
				Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
				Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
				MiscFlags = ResourceOptionFlags.Shared
			});
			using var target = device.CreateRenderTargetView(texture);
			context!.ClearRenderTargetView(target, new Color4(1, 0, 0, 1));
			context.Flush();
			using var dxgiResource = texture.QueryInterface<IDXGIResource>();
			using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
			using var adapter = dxgiDevice.GetAdapter();
			using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
			var luid = adapter1.Description1.Luid;
			var adapterLuid = ((long)luid.HighPart << 32) | luid.LowPart;
			var resourceId = MonitoringResourceId.New();
			var provider = Identity.New();
			OperatorGpuMonitoringFrame Frame(long adapterIdentity, ulong generation, ulong sequence) => new(
				new MonitoringFrameDescriptor(MonitoringContractVersion.Current, MonitoringStreamKind.Program,
					MediaSourceId.New(), 320, 180, PixelFormat.Rgba8,
					new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)), ColorDescription.SrgbFullRgba8,
					MonitoringSharedResourceCapabilityState.Available,
					new MonitoringSharedResourceDescriptor(resourceId, provider, SurfaceId.New(), VideoFormat.Hd1080p50Rgba8,
						SurfaceStorageDomain.Shared, MonitoringResourceAccessMode.ReadOnly,
						new SurfaceLifetimeDescriptor(new Generation(generation), resourceId.Value),
						new MonitoringSharedResourceInteropDescriptor(MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle,
							adapterIdentity, unchecked((ulong)dxgiResource.SharedHandle.ToInt64())))));
			viewer.FitCommand.Execute(null);
			verifyPixel = true;
			viewer.GpuFrame = Frame(adapterLuid, 1, 1);
			await WaitAsync(() => viewer.IsGpuPresentationActive && pixelVerified, dispatcher, "shared GPU shader red pixel");
			Assert.Contains("GPU SHARED 1920×1080", viewer.PresentationQuality);
			viewer.Zoom100Command.Execute(null);
			Assert.Equal(1920 / viewer.DpiScaleX, viewer.PresentationWidth, 6);
			viewer.InspectionRoi = new MediaInspectionRoi(0, 0, 100, 100);
			await WaitAsync(() => viewer.RoiInspectionReadout.Contains("64 B transfer", StringComparison.Ordinal),
				dispatcher, "bounded shared-resource ROI result");
			viewer.GpuFrame = Frame(adapterLuid ^ 1, 1, 2);
			await WaitAsync(() => !viewer.IsGpuPresentationActive, dispatcher, "adapter rejection fallback");
			Assert.Contains("REDUCED CPU 320×180", viewer.PresentationQuality);
			Assert.Contains("NOT CURRENT", viewer.PresentationQuality);
			Assert.Contains("RETAINED", viewer.ObservationStatus);
			Assert.Equal(320 / viewer.DpiScaleX, viewer.PresentationWidth, 6);
			Assert.False(viewer.IsRoiActive);
			viewer.GpuFrame = Frame(adapterLuid, 2, 1);
			await WaitAsync(() => viewer.IsGpuPresentationActive, dispatcher, "new resource generation");
			viewer.GpuFrame = null;
			await WaitAsync(() => !viewer.IsGpuPresentationActive, dispatcher, "resource retirement");
			Assert.Contains("NOT CURRENT", viewer.PresentationQuality);
			Console.WriteLine("VIEWER SHARED GPU PASS: synthetic red texture through productive shader and native readback; 1920×1080 native basis, 64-byte ROI result, same-ID wrong-adapter fallback, generation restore and resource retirement. This is Operator D3D interop evidence, not provider/CUDA hardware acceptance.");
		}
		finally
		{
			viewer.GpuFrame = null;
			surface.Draw -= Draw;
		}
	}

	private static async Task WaitAsync(Func<bool> condition, Dispatcher dispatcher, string phase)
	{
		var elapsed = Stopwatch.StartNew();
		while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5))
		{
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
			await Task.Delay(10);
		}
		Assert.True(condition(), $"Viewer qualification timed out: {phase}");
	}

	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			var child = VisualTreeHelper.GetChild(root, index);
			yield return child;
			foreach (var descendant in Descendants(child)) yield return descendant;
		}
	}
}
