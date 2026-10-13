// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using rtaime.Operator.Controls;
using rtaime.Operator;
using Vortice.Mathematics;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using Xunit;

namespace rtaime.Tests.Operator;

[Collection("Operator application")]
public sealed class GpuDrawingSurfaceTests
{
	internal static void VerifyDescriptorBoundary(Dispatcher dispatcher)
	{
		dispatcher.VerifyAccess();
		D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
			FeatureLevel.Level_11_0, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
		using (device)
		using (context)
		{
			Assert.NotNull(device);
			var descriptor = new Texture2DDescription
			{
				Width = uint.MaxValue, Height = 100, MipLevels = 1, ArraySize = 1,
				Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
				Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
				CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.Shared
			};
			var failure = Assert.Throws<SharpGenException>(() => device.CreateTexture2D(descriptor));
			Assert.Equal(unchecked((int)0x80070057), failure.ResultCode.Code);
			var package = typeof(Vortice.Wpf.DrawingSurface).Assembly
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			Console.WriteLine($"GPU DESCRIPTOR REPRO PASS: Hardware D3D11; Vortice.Wpf={package}; Width=4294967295 Height=100 MipLevels=1 ArraySize=1 Format=BGRA8 SampleCount=1 Quality=0 Usage=Default Bind=RenderTarget|ShaderResource CPU=None Misc=Shared HRESULT=0x{failure.ResultCode.Code:X8}. This proves the invalid-width descriptor boundary, not the unrecorded production-session descriptor.");
		}
	}

	[Theory]
	[InlineData(1920, 1080, 1, 1920, 1080)]
	[InlineData(1920, 1080, 1.25, 2400, 1350)]
	[InlineData(1920, 1080, 1.5, 2880, 1620)]
	[InlineData(1920, 1080, 2, 3840, 2160)]
	[InlineData(100.1, 50.1, 1.25, 126, 63)]
	[InlineData(0.1, 0.1, 1, 1, 1)]
	[InlineData(8192, 8192, 2, 16384, 16384)]
	public void Physical_targets_cover_fractional_pixels_without_clamping(
		double width, double height, double scale, int expectedWidth, int expectedHeight)
	{
		Assert.True(OperatorSurfaceTargetPolicy.TryMeasure(width, height, scale, scale,
			16384, 16384, out var target, out var reason), reason);
		Assert.Equal(new OperatorSurfaceTarget(expectedWidth, expectedHeight), target);
		Assert.True(OperatorSurfaceTargetPolicy.IsValid(OperatorSurfaceTargetPolicy.Describe(target), 16384, 16384));
	}

	[Theory]
	[InlineData(0, 100, 1)]
	[InlineData(100, 0, 1)]
	[InlineData(-1, 100, 1)]
	[InlineData(double.NaN, 100, 1)]
	[InlineData(100, double.PositiveInfinity, 1)]
	[InlineData(100, 100, double.NaN)]
	[InlineData(100, 100, double.PositiveInfinity)]
	[InlineData(100, 100, 0)]
	[InlineData(double.MaxValue, 100, 2)]
	[InlineData(16384.1, 100, 1)]
	[InlineData(8192.1, 100, 2)]
	[InlineData(double.Epsilon, 100, double.Epsilon)]
	public void Transient_nonfinite_overflow_and_unsupported_targets_are_deferred(double width, double height, double scale)
	{
		Assert.False(OperatorSurfaceTargetPolicy.TryMeasure(width, height, scale, scale,
			16384, 16384, out var target, out _));
		Assert.Equal(default, target);
	}

	[Fact]
	public void Descriptor_validation_rejects_each_incompatible_shared_target_field()
	{
		var valid = OperatorSurfaceTargetPolicy.Describe(new OperatorSurfaceTarget(160, 90));
		var variants = new List<Texture2DDescription>();
		var value = valid; value.Width = 0; variants.Add(value);
		value = valid; value.Height = 16385; variants.Add(value);
		value = valid; value.MipLevels = 0; variants.Add(value);
		value = valid; value.ArraySize = 2; variants.Add(value);
		value = valid; value.Format = Format.R8G8B8A8_UNorm; variants.Add(value);
		value = valid; value.SampleDescription = new SampleDescription(2, 0); variants.Add(value);
		value = valid; value.SampleDescription = new SampleDescription(1, 1); variants.Add(value);
		value = valid; value.Usage = ResourceUsage.Staging; variants.Add(value);
		value = valid; value.BindFlags = BindFlags.ShaderResource; variants.Add(value);
		value = valid; value.CPUAccessFlags = CpuAccessFlags.Read; variants.Add(value);
		value = valid; value.MiscFlags = ResourceOptionFlags.None; variants.Add(value);
		Assert.All(variants, descriptor => Assert.False(OperatorSurfaceTargetPolicy.IsValid(descriptor, 16384, 16384)));
	}

	internal static async Task VerifySurfaceLifetimeAsync(Dispatcher dispatcher)
	{
		dispatcher.VerifyAccess();
		Console.WriteLine("GPU LIFETIME BEGIN: constructing productive surfaces");
		foreach (var surface in new OperatorDrawingSurface[]
		{
			new GpuMonitorPresentationSurface(), new GpuMediaCompareSurface(), new GpuScopeAnalysisSurface()
		})
			await VerifyProductiveSurfaceAsync(surface, dispatcher);
		await VerifyDpiAndRecoveryAsync(dispatcher);
		await VerifyViewportLayoutAsync(dispatcher);
	}

	private static async Task VerifyProductiveSurfaceAsync(OperatorDrawingSurface surface, Dispatcher dispatcher)
	{
		Console.WriteLine($"GPU LIFETIME SETUP [{surface.GetType().Name}]");
		var diagnostics = new List<OperatorSurfaceDiagnostic>();
		var descriptions = new List<Texture2DDescription>();
		surface.DiagnosticEmitted += diagnostic =>
		{
			diagnostics.Add(diagnostic);
			Console.WriteLine($"GPU DIAGNOSTIC [{surface.GetType().Name}] {diagnostic.Code}: {diagnostic.Message}; {JsonSerializer.Serialize(diagnostic.Dimensions)}; {diagnostic.Exception}");
		};
		surface.BeforeTargetCreation = descriptions.Add;
		surface.Width = surface.Height = 0;
		surface.HorizontalAlignment = HorizontalAlignment.Left;
		surface.VerticalAlignment = VerticalAlignment.Top;
		var grid = new Grid();
		grid.Children.Add(surface);
		var window = new Window { Width = 400, Height = 300, Content = grid, ShowInTaskbar = false };
		var pixelVerified = false;
		surface.Draw += (_, args) =>
		{
			if (pixelVerified) return;
			Console.WriteLine($"GPU PIXEL READBACK BEGIN [{surface.GetType().Name}]");
			args.Context.ClearRenderTargetView(surface.ColorTextureView!, new Color4(1, 0, 0, 1));
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
				Assert.Equal(0, Marshal.ReadByte(mapped.DataPointer, 0));
				Assert.Equal(0, Marshal.ReadByte(mapped.DataPointer, 1));
				Assert.Equal(255, Marshal.ReadByte(mapped.DataPointer, 2));
				Assert.Equal(255, Marshal.ReadByte(mapped.DataPointer, 3));
			}
			finally { args.Context.Unmap(readback, 0); }
			pixelVerified = true;
			Console.WriteLine($"GPU PIXEL READBACK PASS [{surface.GetType().Name}]");
		};
		try
		{
			Console.WriteLine($"GPU WINDOW SHOW BEGIN [{surface.GetType().Name}]");
			window.Show();
			Console.WriteLine($"GPU WINDOW SHOW RETURN [{surface.GetType().Name}]");
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.WaitingForSize, dispatcher, "zero target deferred");
			Assert.Equal(0, surface.TargetCreateCount);
			surface.Width = 160; surface.Height = 90;
			await ReadyAsync(surface, dispatcher);
			await WaitForAsync(() => pixelVerified, dispatcher, "real shared-target BGRA pixel readback");
			var initial = surface.TargetCreateCount;
			for (var index = 0; index < 30; index++) { surface.Invalidate(); surface.Width = 160 + index / 10d; }
			await ReadyAsync(surface, dispatcher, expectedWidth: checked((int)Math.Ceiling(162.9 * VisualTreeHelper.GetDpi(surface).DpiScaleX)));
			Assert.Equal(initial + 1, surface.TargetCreateCount);
			var stable = surface.TargetCreateCount;
			for (var index = 0; index < 30; index++) surface.Invalidate();
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			Assert.Equal(stable, surface.TargetCreateCount);

			surface.Visibility = Visibility.Collapsed;
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Suspended, dispatcher, "collapsed surface");
			Assert.Null(surface.ColorTexture);
			surface.Width = 170;
			surface.Visibility = Visibility.Visible;
			await ReadyAsync(surface, dispatcher);
			window.Hide();
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Suspended, dispatcher, "hidden window");
			window.Show();
			await ReadyAsync(surface, dispatcher);
			window.WindowState = WindowState.Minimized;
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Suspended, dispatcher, "minimized window");
			window.WindowState = WindowState.Normal;
			await ReadyAsync(surface, dispatcher);

			// Deterministic front-buffer event seam: not a physical display disconnect.
			surface.SetFrontBufferForQualification(false);
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Suspended, dispatcher, "injected front-buffer loss");
			Assert.Null(surface.ColorTexture);
			surface.Width = 180;
			surface.SetFrontBufferForQualification(true);
			await ReadyAsync(surface, dispatcher);
			surface.SetFrontBufferForQualification(null);

			var failNextAllocation = true;
			surface.BeforeTargetCreation = description =>
			{
				descriptions.Add(description);
				if (!failNextAllocation) return;
				failNextAllocation = false;
				throw new SharpGenException(new Result(unchecked((int)0x887A0005)));
			};
			surface.Width = 181;
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Faulted, dispatcher, "injected device removal");
			Assert.Null(surface.ColorTexture);
			pixelVerified = false;
			surface.SetFrontBufferForQualification(false);
			surface.SetFrontBufferForQualification(true);
			await ReadyAsync(surface, dispatcher);
			await WaitForAsync(() => pixelVerified, dispatcher, "pixel readback after device restoration");
			surface.SetFrontBufferForQualification(null);
			surface.BeforeTargetCreation = descriptions.Add;

			grid.Children.Remove(surface);
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Unloaded, dispatcher, "unloaded surface");
			Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
			grid.Children.Add(surface);
			await ReadyAsync(surface, dispatcher);
			Assert.Equal(3, surface.DeviceGeneration);
			Assert.All(descriptions, descriptor => Assert.True(OperatorSurfaceTargetPolicy.IsValid(descriptor, 16384, 16384)));
			var graphicsFailures = diagnostics.Where(diagnostic => diagnostic.Exception is not null).ToArray();
			Assert.Single(graphicsFailures);
			Assert.Equal("operator.surface.failure", graphicsFailures[0].Code);
			Assert.Equal("0x887A0005", graphicsFailures[0].Dimensions["hresult"]);
			Console.WriteLine($"GPU LIFECYCLE PASS [{surface.GetType().Name}]: zero, coalesced resize, stable redraw, collapse, hide/show, minimize/restore, injected front-buffer loss, injected device removal and successful recovery, unload/reload; real shared BGRA8 red-pixel readback; device generations={surface.DeviceGeneration}.");
		}
		finally
		{
			window.Close();
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			Console.WriteLine($"GPU LAYOUT [{surface.GetType().Name}]: requested={surface.Width}x{surface.Height}; actual={surface.ActualWidth}x{surface.ActualHeight}; desired={surface.DesiredSize}; render={surface.RenderSize}; grid={grid.ActualWidth}x{grid.ActualHeight}; state={surface.SurfaceState}; detail={surface.SurfaceDetail}.");
			Assert.Null(surface.ColorTexture);
			Assert.Null(surface.ColorTextureView);
			Assert.Equal(OperatorSurfaceState.Unloaded, surface.SurfaceState);
			Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
			Console.WriteLine($"GPU CLEANUP PASS [{surface.GetType().Name}]: created={surface.TargetCreateCount}, released={surface.TargetReleaseCount}.");
		}
	}

	private static async Task VerifyDpiAndRecoveryAsync(Dispatcher dispatcher)
	{
		var surface = new QualifiedDpiSurface { Width = 101, Height = 51 };
		var diagnostics = new List<OperatorSurfaceDiagnostic>();
		surface.DiagnosticEmitted += diagnostic =>
		{
			diagnostics.Add(diagnostic);
			Console.WriteLine($"GPU DIAGNOSTIC [{surface.GetType().Name}] {diagnostic.Code}: {diagnostic.Message}; {JsonSerializer.Serialize(diagnostic.Dimensions)}; {diagnostic.Exception}");
		};
		var window = new Window { Width = 400, Height = 300, Content = surface, ShowInTaskbar = false };
		try
		{
			window.Show();
			foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
			{
				surface.ChangeDpi(scale);
				await ReadyAsync(surface, dispatcher, checked((int)Math.Ceiling(101 * scale)));
				Assert.Equal(checked((int)Math.Ceiling(51 * scale)), surface.TextureHeight);
			}
			surface.Width = 8192.1;
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.WaitingForSize, dispatcher, "device-limit deferral");
			Assert.Null(surface.ColorTexture);
			surface.Width = 101;
			await ReadyAsync(surface, dispatcher);
			var calls = 0;
			surface.BeforeTargetCreation = _ =>
			{
				calls++;
				throw new SharpGenException(new Result(unchecked((int)0x80070057)));
			};
			surface.Width = 102;
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Faulted, dispatcher, "injected allocation failure");
			Assert.Null(surface.ColorTexture);
			for (var index = 0; index < 10; index++) { surface.Width = 103 + index; surface.Invalidate(); }
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			Assert.Equal(1, calls);
			for (var index = 0; index < 3; index++)
			{
				surface.SetFrontBufferForQualification(false);
				surface.SetFrontBufferForQualification(true);
				await WaitForAsync(() => calls == index + 2 && surface.SurfaceState == OperatorSurfaceState.Faulted,
					dispatcher, "bounded restoration attempt");
			}
			surface.SetFrontBufferForQualification(false);
			surface.SetFrontBufferForQualification(true);
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			Assert.Equal(4, calls);
			var failures = diagnostics.Where(diagnostic => diagnostic.Code == "operator.surface.failure").ToArray();
			Assert.Equal(4, failures.Length);
			Assert.All(failures, failure =>
			{
				Assert.Equal("0x80070057", failure.Dimensions["hresult"]);
				foreach (var field in new[] { "widthPixels", "heightPixels", "requestedWidthPixels", "requestedHeightPixels",
					"widthDip", "heightDip", "dpiX", "dpiY", "targetScaleX", "targetScaleY", "visible", "frontBufferAvailable",
					"format", "mipLevels", "arraySize", "sampleCount", "sampleQuality", "usage", "bindFlags",
					"cpuAccessFlags", "miscFlags", "adapter", "adapterLuid", "featureLevel", "deviceGeneration", "phase", "operationPhase", "windowState", "loaded", "threadId", "recoveryAttempts", "deviceRemovedReason" })
					Assert.True(failure.Dimensions.ContainsKey(field), $"Missing GPU descriptor diagnostic field {field}.");
			});
			Console.WriteLine("GPU RECOVERY PASS: actual D3D11/D3D9Ex targets at injected 100/125/150/200% scales; oversized target deferred; injected E_INVALIDARG latched, three transition retries, no resize/render allocation storm; full descriptor/HRESULT diagnostics.");
		}
		finally
		{
			window.Close();
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
		}
	}

	private static async Task VerifyViewportLayoutAsync(Dispatcher dispatcher)
	{
		var monitor = new MonitorView();
		var presentation = new RtaimeMonitorPresentation
		{
			Monitor = monitor,
			Style = (Style)Application.Current.FindResource("RtaimeMonitorPresentation")
		};
		var scroll = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Content = presentation
		};
		var window = new Window { Width = 1200, Height = 900, Content = scroll, ShowInTaskbar = false };
		GpuMonitorPresentationSurface? surface = null;
		try
		{
			window.Show();
			window.UpdateLayout();
			var viewport = Assert.IsAssignableFrom<FrameworkElement>(presentation.Template.FindName("PART_MediaViewport", presentation));
			surface = Assert.Single(Descendants(presentation).OfType<GpuMonitorPresentationSurface>());
			foreach (var state in new[] { WindowState.Normal, WindowState.Maximized })
			foreach (var visibility in new[] { Visibility.Visible, Visibility.Hidden, Visibility.Visible })
			{
				window.WindowState = state;
				presentation.Visibility = visibility;
				for (var pass = 0; pass < 30; pass++)
				{
					window.UpdateLayout();
					monitor.UpdatePresentationViewport(viewport.ActualWidth, viewport.ActualHeight, 1.5, 1.5);
					surface.Invalidate();
					await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
					Assert.InRange(viewport.ActualHeight, 1, 900);
					Assert.InRange(surface.ActualHeight, 1, 900);
					Assert.Equal(viewport.ActualHeight, surface.ActualHeight, 6);
				}
			}
			await ReadyAsync(surface, dispatcher);
			Assert.Equal(checked((int)Math.Ceiling(surface.ActualWidth * 1.5)), surface.TextureWidth);
			Assert.Equal(checked((int)Math.Ceiling(surface.ActualHeight * 1.5)), surface.TextureHeight);
			var creates = surface.TargetCreateCount;
			for (var pass = 0; pass < 30; pass++)
			{
				surface.Invalidate();
				await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
			}
			Assert.Equal(creates, surface.TargetCreateCount);
			Console.WriteLine("GPU VIEWPORT PASS: scroll measure, hidden workspaces, normal/maximized layout, 150% physical targets, stable allocations.");
		}
		finally
		{
			window.Close();
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			if (surface is not null) Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
		}
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

	private sealed class QualifiedDpiSurface : OperatorDrawingSurface
	{
		private DpiScale _scale = new(1, 1);
		protected override DpiScale TargetScale => _scale;
		public void ChangeDpi(double scale)
		{
			var previous = _scale;
			_scale = new DpiScale(scale, scale);
			OnDpiChanged(previous, _scale);
		}
	}

	private static Task ReadyAsync(OperatorDrawingSurface surface, Dispatcher dispatcher, int? expectedWidth = null) =>
		WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Ready && surface.ColorTexture is not null &&
			(!expectedWidth.HasValue || surface.TextureWidth == expectedWidth), dispatcher, "ready target");

	private static async Task WaitForAsync(Func<bool> condition, Dispatcher dispatcher, string phase)
	{
		Console.WriteLine($"GPU WAIT BEGIN [{phase}]");
		var deadline = Stopwatch.StartNew();
		while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(5))
		{
			await dispatcher.InvokeAsync(() =>
			{
				// Drive the real WPF layout pass in the resource-only Application fixture.
				foreach (Window window in Application.Current.Windows) window.UpdateLayout();
			}, DispatcherPriority.Render);
			await Task.Delay(10);
		}
		Assert.True(condition(), $"GPU surface qualification timed out: {phase}.");
		Console.WriteLine($"GPU WAIT PASS [{phase}]");
	}

}
