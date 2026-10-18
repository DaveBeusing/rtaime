// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
			Console.WriteLine("VIEWER PROGRAM PASS: real RuntimeHost Program sequence advanced through the named-pipe monitoring transport; productive ProgramViewer BAML, fallback native pixels, Fit/Fill/zoom, ROI retention, stale/recovered evidence, resize targets, detach/reattach of the same viewer, unchanged confirmed production revision. PNG is static fallback evidence; interaction assertions are separate. No physical multi-monitor or provider hardware claim.");
		}
		finally
		{
			window.Close();
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			controlStop.Cancel();
			try { Assert.Equal(ControlHostExitCode.Success, await controlRun); }
			finally
			{
				runtimeStop.Cancel();
				try { Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun); }
				finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
			}
			if (surface is not null) Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
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
