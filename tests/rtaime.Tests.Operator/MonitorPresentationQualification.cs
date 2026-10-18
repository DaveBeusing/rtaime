// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Media.Contracts;
using rtaime.Operator;
using rtaime.Operator.Controls;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Operator;

internal static class MonitorPresentationQualification
{
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
		try
		{
			await WaitAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready &&
				control.Control?.HasAuthoritativeState == true, dispatcher, "confirmed host startup");
			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
				controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
			var before = await client.SynchronizeAsync();
			using var readStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			await using var frames = new NamedPipeOperatorMonitoringTransport(runtime.MonitoringEndpoint)
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
			viewer.Zoom100Command.Execute(null);
			Assert.Equal(320 / viewer.DpiScaleX, viewer.PresentationWidth, 6);
			Assert.Equal(180 / viewer.DpiScaleY, viewer.PresentationHeight, 6);
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
			viewer.ShowRgbCommand.Execute(null);
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
			Console.WriteLine("VIEWER PROGRAM PASS: real RuntimeHost Program sequence advanced through the named-pipe monitoring transport; productive ProgramViewer BAML, fallback native pixels, Fit/Fill/zoom, ROI retention, stale/recovered evidence, resize targets, detach/reattach of the same viewer, unchanged confirmed production revision. PNG is static fallback evidence; interaction assertions are separate. No physical multi-monitor or provider hardware claim.");
		}
		finally
		{
			window.Close();
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			if (surface is not null) Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
			controlStop.Cancel();
			runtimeStop.Cancel();
			try { Assert.Equal(ControlHostExitCode.Success, await controlRun); }
			finally
			{
				Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
				if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
			}
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
