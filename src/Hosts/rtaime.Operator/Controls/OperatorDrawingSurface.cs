// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Wpf;
using static Vortice.Direct3D11.D3D11;

namespace rtaime.Operator.Controls;

public enum OperatorSurfaceState { Unloaded, WaitingForSize, Suspended, Ready, Faulted }

public sealed class OperatorSurfaceDrawEventArgs(
	OperatorDrawingSurface surface, ID3D11Device1 device, ID3D11DeviceContext1 context) : EventArgs
{
	public OperatorDrawingSurface Surface { get; } = surface;
	public ID3D11Device1 Device { get; } = device;
	public ID3D11DeviceContext1 Context { get; } = context;
}

internal sealed record OperatorSurfaceDiagnostic(
	string Code, DiagnosticSeverity Severity, string Message,
	IReadOnlyDictionary<string, string> Dimensions, Exception? Exception);

// Own only Operator presentation targets. Private Vortice.Wpf resize/front-buffer
// handlers cannot validate descriptors or provide idempotent teardown in a subclass.
public class OperatorDrawingSurface : Image
{
	private ID3D11Device1? _device;
	private ID3D11DeviceContext1? _context;
	private OperatorD3DImage? _image;
	private Window? _window;
	private DispatcherOperation? _update;
	private bool _contentLoaded;
	private string _operation = "unloaded";
	private bool _rendering;
	private bool _refresh = true;
	private bool _recreating;
	private bool _disposing;
	private bool _closed;
	private bool _faulted;
	private int _recoveries;
	private bool? _qualificationFrontBuffer;
	private string _adapter = "uninitialized";
	private string _adapterLuid = "uninitialized";
	private int _maximumWidth = 16384;
	private int _maximumHeight = 16384;
	private readonly Dictionary<string, DateTimeOffset> _diagnosticTimes = [];

	public event EventHandler<DrawingSurfaceEventArgs>? LoadContent;
	public event EventHandler<OperatorSurfaceDrawEventArgs>? Draw;
	public event EventHandler<DrawingSurfaceEventArgs>? UnloadContent;
	public event EventHandler? SurfaceStateChanged;
	internal event Action<OperatorSurfaceDiagnostic>? DiagnosticEmitted;
	internal Action<Texture2DDescription>? BeforeTargetCreation { get; set; }

	public int TextureWidth { get; private set; }
	public int TextureHeight { get; private set; }
	public Format ColorFormat { get; set; } = Format.B8G8R8A8_UNorm;
	public Format DepthStencilFormat { get; set; } = Format.Unknown;
	public ID3D11Texture2D? ColorTexture { get; private set; }
	public ID3D11RenderTargetView? ColorTextureView { get; private set; }
	public bool AlwaysRefresh { get; set; }
	public OperatorSurfaceState SurfaceState { get; private set; } = OperatorSurfaceState.Unloaded;
	public string SurfaceDetail { get; private set; } = "GPU surface unloaded";
	internal Texture2DDescription? LastTargetDescription { get; private set; }
	internal int TargetCreateCount { get; private set; }
	internal int TargetReleaseCount { get; private set; }
	internal int DeviceGeneration { get; private set; }

	static OperatorDrawingSurface() =>
		StretchProperty.OverrideMetadata(typeof(OperatorDrawingSurface), new FrameworkPropertyMetadata(Stretch.Fill));

	public OperatorDrawingSurface()
	{
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		IsVisibleChanged += (_, _) =>
		{
			if (IsVisible) RecoverOnTransition();
			ScheduleUpdate();
		};
	}

	public void Invalidate()
	{
		Dispatcher.VerifyAccess();
		_refresh = true;
	}

	protected virtual DpiScale TargetScale => VisualTreeHelper.GetDpi(this);

	protected override Size ArrangeOverride(Size finalSize)
	{
		// Image returns zero size while Source is absent. Preserve the parent's
		// allocated viewport so deferred startup/recovery can create its first target.
		base.ArrangeOverride(finalSize);
		return finalSize;
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);
		ScheduleUpdate();
	}

	protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
	{
		base.OnDpiChanged(oldDpi, newDpi);
		ScheduleUpdate();
	}

	private void OnLoaded(object sender, RoutedEventArgs args)
	{
		if (DesignerProperties.GetIsInDesignMode(this)) return;
		_closed = false;
		_faulted = false;
		_recoveries = 0;
		_window = Window.GetWindow(this);
		if (_window is null) return;
		_window.Closed += OnWindowClosed;
		_window.StateChanged += OnWindowStateChanged;
		ScheduleUpdate();
	}

	private void OnWindowClosed(object? sender, EventArgs args)
	{
		_closed = true;
		OnUnloaded(this, new RoutedEventArgs());
	}

	private void OnWindowStateChanged(object? sender, EventArgs args)
	{
		if (_window?.WindowState != WindowState.Minimized) RecoverOnTransition();
		ScheduleUpdate();
	}

	private void OnUnloaded(object sender, RoutedEventArgs args)
	{
		Dispatcher.VerifyAccess();
		_update?.Abort();
		_update = null;
		if (_window is not null)
		{
			_window.Closed -= OnWindowClosed;
			_window.StateChanged -= OnWindowStateChanged;
			_window = null;
		}
		DisposeDevice();
		SetState(OperatorSurfaceState.Unloaded, "GPU surface unloaded");
	}

	private void ScheduleUpdate()
	{
		if (!IsLoaded || _closed || _disposing || _window is null || Dispatcher.HasShutdownStarted || _update is not null) return;
		_update = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
		{
			_update = null;
			UpdateTarget();
		}));
	}

	private bool FrontBufferAvailable => _qualificationFrontBuffer ?? _image?.IsFrontBufferAvailable ?? true;

	internal void SetFrontBufferForQualification(bool? available)
	{
		Dispatcher.VerifyAccess();
		_qualificationFrontBuffer = available;
		OnFrontBufferChanged(this, new DependencyPropertyChangedEventArgs());
	}

	private void OnFrontBufferChanged(object sender, DependencyPropertyChangedEventArgs args)
	{
		if (FrontBufferAvailable) RecoverOnTransition();
		ScheduleUpdate();
	}

	private void RecoverOnTransition()
	{
		if (!_faulted || _closed || !IsLoaded || _recreating || _disposing || _recoveries >= 3) return;
		_recoveries++;
		_faulted = false;
		Report("operator.surface.recovery", DiagnosticSeverity.Information,
			$"GPU recovery on a visibility/front-buffer/window transition ({_recoveries}/3).");
	}

	private void UpdateTarget()
	{
		Dispatcher.VerifyAccess();
		if (_closed || !IsLoaded || _window is null || _recreating || _disposing || _faulted) return;
		_recreating = true;
		_operation = "measure";
		try
		{
			if (!IsVisible || _window?.WindowState == WindowState.Minimized || !FrontBufferAvailable)
			{
				ReleaseTargets();
				SetState(OperatorSurfaceState.Suspended, "GPU preview suspended: hidden, minimized or front buffer unavailable");
				return;
			}
			var scale = TargetScale;
			if (!OperatorSurfaceTargetPolicy.TryMeasure(ActualWidth, ActualHeight, scale.DpiScaleX, scale.DpiScaleY,
				_maximumWidth, _maximumHeight, out var target, out var reason))
			{
				ReleaseTargets();
				SetState(OperatorSurfaceState.WaitingForSize, $"GPU preview deferred: {reason}");
				return;
			}
			_operation = "device-initialization";
			InitializeDevice();
			_operation = "measure";
			if (!OperatorSurfaceTargetPolicy.TryMeasure(ActualWidth, ActualHeight, scale.DpiScaleX, scale.DpiScaleY,
				_maximumWidth, _maximumHeight, out target, out reason))
			{
				ReleaseTargets();
				SetState(OperatorSurfaceState.WaitingForSize, $"GPU preview deferred: {reason}");
				return;
			}
			if (ColorTexture is not null && TextureWidth == target.Width && TextureHeight == target.Height)
			{
				SetState(OperatorSurfaceState.Ready, "GPU preview ready");
				StartRendering();
				return;
			}
			var description = OperatorSurfaceTargetPolicy.Describe(target);
			LastTargetDescription = description;
			if (ColorFormat != Format.B8G8R8A8_UNorm || DepthStencilFormat != Format.Unknown ||
				!OperatorSurfaceTargetPolicy.IsValid(description, _maximumWidth, _maximumHeight))
				throw new InvalidOperationException("Operator targets require validated single-sample shared BGRA8 color without depth.");

			_operation = "target-allocation";
			// Diagnostic descriptor is captured before the native allocation, including failure injection.
			Report("operator.surface.create", DiagnosticSeverity.Information, "Creating a validated shared preview target.");
			BeforeTargetCreation?.Invoke(description);
			ID3D11Texture2D? texture = null;
			ID3D11RenderTargetView? view = null;
			try
			{
				texture = _device!.CreateTexture2D(description);
				TargetCreateCount++;
				view = _device.CreateRenderTargetView(texture);
				_image!.Bind(texture);
				ColorTextureView?.Dispose();
				if (ColorTexture is not null) { ColorTexture.Dispose(); TargetReleaseCount++; }
				ColorTexture = texture;
				ColorTextureView = view;
				texture = null;
				view = null;
				TextureWidth = target.Width;
				TextureHeight = target.Height;
			}
			finally
			{
				view?.Dispose();
				if (texture is not null) { texture.Dispose(); TargetReleaseCount++; }
			}
			Source = _image;
			_refresh = true;
			SetState(OperatorSurfaceState.Ready, "GPU preview ready");
			StartRendering();
		}
		catch (Exception exception) when (IsGraphicsFailure(exception))
		{
			_faulted = true;
			Report("operator.surface.failure", DiagnosticSeverity.Error,
				"GPU preview failed; rendering is latched off until a bounded restoration transition.", exception);
			DisposeDevice();
			SetState(OperatorSurfaceState.Faulted, "GPU preview unavailable: " + exception.Message);
		}
		finally { _recreating = false; }
	}

	private void InitializeDevice()
	{
		if (_device is not null) return;
		D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
			FeatureLevel.Level_11_0, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
		using (device)
		using (context)
		{
			_device = device!.QueryInterface<ID3D11Device1>();
			_context = context!.QueryInterface<ID3D11DeviceContext1>();
		}
		using (var dxgi = _device.QueryInterface<IDXGIDevice>())
		using (var adapter = dxgi.GetAdapter())
		using (var adapter1 = adapter.QueryInterface<IDXGIAdapter1>())
		{
			_adapter = adapter1.Description1.Description;
			_adapterLuid = adapter1.Description1.Luid.ToString();
		}
		var required = FormatSupport.Texture2D | FormatSupport.RenderTarget | FormatSupport.ShaderSample;
		if ((_device.CheckFeatureFormatSupport(Format.B8G8R8A8_UNorm) & required) != required)
			throw new InvalidOperationException("Device does not support the BGRA8 preview target format.");
		_image = new OperatorD3DImage(_window!);
		_maximumWidth = Math.Min(16384, _image.MaximumWidth);
		_maximumHeight = Math.Min(16384, _image.MaximumHeight);
		_image.IsFrontBufferAvailableChanged += OnFrontBufferChanged;
		DeviceGeneration++;
		_contentLoaded = true;
		LoadContent?.Invoke(this, new DrawingSurfaceEventArgs(_device, _context!));
	}

	private void StartRendering()
	{
		if (_rendering) return;
		CompositionTarget.Rendering += OnRendering;
		_rendering = true;
	}

	private void StopRendering()
	{
		if (!_rendering) return;
		CompositionTarget.Rendering -= OnRendering;
		_rendering = false;
	}

	private void OnRendering(object? sender, EventArgs args)
	{
		if (SurfaceState != OperatorSurfaceState.Ready || _device is null || _context is null ||
			ColorTextureView is null || _image is null || !FrontBufferAvailable || (!_refresh && !AlwaysRefresh))
			return;
		var image = _image;
		_operation = "draw";
		try
		{
			if (!image.BeginDraw()) return;
			var changed = false;
			try
			{
				_context.OMSetRenderTargets(ColorTextureView);
				_context.RSSetViewport(0, 0, TextureWidth, TextureHeight);
				_context.RSSetScissorRect(0, 0, TextureWidth, TextureHeight);
				Draw?.Invoke(this, new OperatorSurfaceDrawEventArgs(this, _device, _context));
				_context.Flush();
				_device.DeviceRemovedReason.CheckError();
				changed = true;
				_refresh = false;
			}
			finally { image.EndDraw(changed); }
		}
		catch (Exception exception) when (IsGraphicsFailure(exception))
		{
			_faulted = true;
			Report("operator.surface.failure", DiagnosticSeverity.Error, "GPU draw failed; preview suspended.", exception);
			SetState(OperatorSurfaceState.Faulted, "GPU preview unavailable: " + exception.Message);
		}
		if (_faulted) DisposeDevice();
	}

	private void ReleaseTargets()
	{
		StopRendering();
		try { _image?.Bind(null); }
		catch (Exception exception) when (IsGraphicsFailure(exception))
		{
			Report("operator.surface.dispose-failure", DiagnosticSeverity.Warning,
				"Graphics interop rejected target detachment; releasing owned resources.", exception);
		}
		finally
		{
			Source = null;
			ColorTextureView?.Dispose();
			ColorTextureView = null;
			if (ColorTexture is not null) { ColorTexture.Dispose(); TargetReleaseCount++; }
			ColorTexture = null;
			TextureWidth = TextureHeight = 0;
		}
	}

	private void DisposeDevice()
	{
		if (_disposing) return;
		_disposing = true;
		_operation = "cleanup";
		StopRendering();
		try
		{
			try
			{
				if (_contentLoaded && _device is not null && _context is not null)
				{
					_contentLoaded = false;
					UnloadContent?.Invoke(this, new DrawingSurfaceEventArgs(_device, _context));
				}
			}
			finally
			{
				try { ReleaseTargets(); }
				finally
				{
					if (_image is not null)
					{
						_image.IsFrontBufferAvailableChanged -= OnFrontBufferChanged;
						try { _image.Dispose(); }
						catch (Exception exception) when (IsGraphicsFailure(exception))
						{
							Report("operator.surface.dispose-failure", DiagnosticSeverity.Warning, "Graphics interop rejected target detachment during cleanup.", exception);
						}
						finally { _image = null; }
					}
					try { _context?.ClearState(); _context?.Flush(); }
					catch (SharpGenException exception)
					{
						Report("operator.surface.dispose-failure", DiagnosticSeverity.Warning, "Removed device rejected context cleanup.", exception);
					}
					_context?.Dispose();
					_context = null;
					_device?.Dispose();
					_device = null;
				}
			}
		}
		finally { _disposing = false; }
	}

	private void SetState(OperatorSurfaceState state, string detail)
	{
		if (state == SurfaceState && detail == SurfaceDetail) return;
		SurfaceState = state;
		SurfaceDetail = detail;
		Report("operator.surface.state", DiagnosticSeverity.Information, detail);
		SurfaceStateChanged?.Invoke(this, EventArgs.Empty);
	}

	private void Report(string code, DiagnosticSeverity severity, string message, Exception? exception = null)
	{
		var now = DateTimeOffset.UtcNow;
		if (exception is null && _diagnosticTimes.TryGetValue(code, out var previous) &&
			now - previous < TimeSpan.FromSeconds(1)) return;
		_diagnosticTimes[code] = now;
		var dpi = VisualTreeHelper.GetDpi(this);
		var scale = TargetScale;
		var description = LastTargetDescription;
		var dimensions = new Dictionary<string, string>
		{
			["surface"] = GetType().Name, ["phase"] = SurfaceState.ToString(),
			["operationPhase"] = _operation, ["loaded"] = IsLoaded.ToString(),
			["windowState"] = _window?.WindowState.ToString() ?? "detached",
			["threadId"] = Environment.CurrentManagedThreadId.ToString(),
			["recoveryAttempts"] = _recoveries.ToString(),
			["deviceRemovedReason"] = _device is null ? "uninitialized" : $"0x{_device.DeviceRemovedReason.Code:X8}",
			["widthDip"] = ActualWidth.ToString("R", CultureInfo.InvariantCulture),
			["heightDip"] = ActualHeight.ToString("R", CultureInfo.InvariantCulture),
			["requestedWidthPixels"] = Math.Ceiling(ActualWidth * scale.DpiScaleX).ToString("R", CultureInfo.InvariantCulture),
			["requestedHeightPixels"] = Math.Ceiling(ActualHeight * scale.DpiScaleY).ToString("R", CultureInfo.InvariantCulture),
			["dpiX"] = dpi.DpiScaleX.ToString("R", CultureInfo.InvariantCulture),
			["dpiY"] = dpi.DpiScaleY.ToString("R", CultureInfo.InvariantCulture),
			["targetScaleX"] = scale.DpiScaleX.ToString("R", CultureInfo.InvariantCulture),
			["targetScaleY"] = scale.DpiScaleY.ToString("R", CultureInfo.InvariantCulture),
			["visible"] = IsVisible.ToString(), ["frontBufferAvailable"] = FrontBufferAvailable.ToString(),
			["maximumWidth"] = _maximumWidth.ToString(), ["maximumHeight"] = _maximumHeight.ToString(),
			["deviceGeneration"] = DeviceGeneration.ToString(), ["targetCreates"] = TargetCreateCount.ToString(),
			["targetReleases"] = TargetReleaseCount.ToString(), ["adapter"] = _adapter,
			["adapterLuid"] = _adapterLuid, ["featureLevel"] = _device?.FeatureLevel.ToString() ?? "uninitialized"
		};
		if (description is { } value)
		{
			dimensions["widthPixels"] = value.Width.ToString();
			dimensions["heightPixels"] = value.Height.ToString();
			dimensions["mipLevels"] = value.MipLevels.ToString();
			dimensions["arraySize"] = value.ArraySize.ToString();
			dimensions["format"] = value.Format.ToString();
			dimensions["sampleCount"] = value.SampleDescription.Count.ToString();
			dimensions["sampleQuality"] = value.SampleDescription.Quality.ToString();
			dimensions["usage"] = value.Usage.ToString();
			dimensions["bindFlags"] = value.BindFlags.ToString();
			dimensions["cpuAccessFlags"] = value.CPUAccessFlags.ToString();
			dimensions["miscFlags"] = value.MiscFlags.ToString();
		}
		if (exception is not null)
			dimensions["hresult"] = $"0x{(exception is SharpGenException graphics ? graphics.ResultCode.Code : exception.HResult):X8}";
		var diagnostic = new OperatorSurfaceDiagnostic(code, severity, message, dimensions, exception);
		DiagnosticEmitted?.Invoke(diagnostic);
		(Application.Current as App)?.ReportSurfaceDiagnostic(diagnostic);
	}

	private static bool IsGraphicsFailure(Exception exception) =>
		exception is SharpGenException or COMException or InvalidOperationException or ArgumentException;
}
