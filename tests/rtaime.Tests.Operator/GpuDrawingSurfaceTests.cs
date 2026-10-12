// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using rtaime.Operator.Controls;
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
		foreach (var surface in new OperatorDrawingSurface[]
		{
			new GpuMonitorPresentationSurface(), new GpuMediaCompareSurface(), new GpuScopeAnalysisSurface()
		})
			await VerifyProductiveSurfaceAsync(surface, dispatcher);
		await VerifyDpiAndRecoveryAsync(dispatcher);
	}

	private static async Task VerifyProductiveSurfaceAsync(OperatorDrawingSurface surface, Dispatcher dispatcher)
	{
		var diagnostics = new List<OperatorSurfaceDiagnostic>();
		var descriptions = new List<Texture2DDescription>();
		surface.DiagnosticEmitted += diagnostics.Add;
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
		};
		try
		{
			window.Show();
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
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
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

			grid.Children.Remove(surface);
			await WaitForAsync(() => surface.SurfaceState == OperatorSurfaceState.Unloaded, dispatcher, "unloaded surface");
			Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
			grid.Children.Add(surface);
			await ReadyAsync(surface, dispatcher);
			Assert.Equal(2, surface.DeviceGeneration);
			Assert.All(descriptions, descriptor => Assert.True(OperatorSurfaceTargetPolicy.IsValid(descriptor, 16384, 16384)));
			Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Exception is not null);
			Console.WriteLine($"GPU LIFECYCLE PASS [{surface.GetType().Name}]: zero, coalesced resize, stable redraw, collapse, hide/show, minimize/restore, injected front-buffer loss, unload/reload; real shared BGRA8 red-pixel readback; device generations={surface.DeviceGeneration}.");
		}
		finally
		{
			window.Close();
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
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
		surface.DiagnosticEmitted += diagnostics.Add;
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
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
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
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
			Assert.Equal(4, calls);
			var failures = diagnostics.Where(diagnostic => diagnostic.Code == "operator.surface.failure").ToArray();
			Assert.Equal(4, failures.Length);
			Assert.All(failures, failure =>
			{
				Assert.Equal("0x80070057", failure.Dimensions["hresult"]);
				foreach (var field in new[] { "widthPixels", "heightPixels", "requestedWidthPixels", "requestedHeightPixels",
					"widthDip", "heightDip", "dpiX", "dpiY", "targetScaleX", "targetScaleY", "visible", "frontBufferAvailable",
					"format", "mipLevels", "arraySize", "sampleCount", "sampleQuality", "usage", "bindFlags",
					"cpuAccessFlags", "miscFlags", "adapter", "adapterLuid", "featureLevel", "deviceGeneration", "phase" })
					Assert.True(failure.Dimensions.ContainsKey(field), $"Missing GPU descriptor diagnostic field {field}.");
			});
			Console.WriteLine("GPU RECOVERY PASS: actual D3D11/D3D9Ex targets at injected 100/125/150/200% scales; oversized target deferred; injected E_INVALIDARG latched, three transition retries, no resize/render allocation storm; full descriptor/HRESULT diagnostics.");
		}
		finally
		{
			window.Close();
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
			Assert.Equal(surface.TargetCreateCount, surface.TargetReleaseCount);
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
		var deadline = Stopwatch.StartNew();
		while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(5))
		{
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
			await Task.Delay(10);
		}
		Assert.True(condition(), $"GPU surface qualification timed out: {phase}.");
	}

}
