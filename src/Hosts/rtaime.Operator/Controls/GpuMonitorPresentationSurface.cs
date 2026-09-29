// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using rtaime.Media.Contracts;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Wpf;

namespace rtaime.Operator.Controls;

public sealed class GpuMonitorPresentationSurface : DrawingSurface
{
	private ID3D11VertexShader? _vertexShader;
	private ID3D11PixelShader? _pixelShader;
	private ID3D11InputLayout? _inputLayout;
	private ID3D11Buffer? _vertexBuffer;
	private ID3D11Buffer? _colorBuffer;
	private ID3D11SamplerState? _linearSampler;
	private ID3D11SamplerState? _pointSampler;
	private ID3D11Texture2D? _sharedTexture;
	private ID3D11ShaderResourceView? _sharedView;
	private MonitoringResourceId? _openedResourceId;
	private long _deviceAdapterLuid;
	private FrameworkElement? _standaloneViewport;

	public static readonly DependencyProperty MonitorProperty = DependencyProperty.Register(
		nameof(Monitor),
		typeof(MonitorView),
		typeof(GpuMonitorPresentationSurface),
		new FrameworkPropertyMetadata(null, OnPresentationChanged));

	public static readonly DependencyProperty GpuFrameProperty = DependencyProperty.Register(
		nameof(GpuFrame),
		typeof(OperatorGpuMonitoringFrame),
		typeof(GpuMonitorPresentationSurface),
		new FrameworkPropertyMetadata(null, OnPresentationChanged));

	public static readonly DependencyProperty StandaloneFitProperty = DependencyProperty.Register(
		nameof(StandaloneFit),
		typeof(bool),
		typeof(GpuMonitorPresentationSurface),
		new FrameworkPropertyMetadata(false, OnPresentationChanged));

	public GpuMonitorPresentationSurface()
	{
		DepthStencilFormat = Format.Unknown;
		ColorFormat = Format.B8G8R8A8_UNorm;
		IsHitTestVisible = false;
		LoadContent += OnLoadContent;
		Draw += OnDraw;
		UnloadContent += OnUnloadContent;
		Loaded += OnSurfaceLoaded;
		Unloaded += OnSurfaceUnloaded;
	}

	public MonitorView? Monitor
	{
		get => (MonitorView?)GetValue(MonitorProperty);
		set => SetValue(MonitorProperty, value);
	}

	public OperatorGpuMonitoringFrame? GpuFrame
	{
		get => (OperatorGpuMonitoringFrame?)GetValue(GpuFrameProperty);
		set => SetValue(GpuFrameProperty, value);
	}

	public bool StandaloneFit
	{
		get => (bool)GetValue(StandaloneFitProperty);
		set => SetValue(StandaloneFitProperty, value);
	}

	private static void OnPresentationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var surface = (GpuMonitorPresentationSurface)dependencyObject;
		surface.CloseSharedResource();
		surface.Invalidate();
		if (surface.GpuFrame is null)
			surface.Monitor?.SetGpuPresentationState(false, "CPU/WPF fallback");
	}

	private void OnSurfaceLoaded(object sender, RoutedEventArgs e)
	{
		if (!StandaloneFit || Parent is not FrameworkElement viewport)
			return;

		_standaloneViewport = viewport;
		_standaloneViewport.SizeChanged += OnStandaloneViewportChanged;
		UpdateStandalonePhysicalSize();
	}

	private void OnSurfaceUnloaded(object sender, RoutedEventArgs e)
	{
		if (_standaloneViewport is not null)
			_standaloneViewport.SizeChanged -= OnStandaloneViewportChanged;
		_standaloneViewport = null;
		Monitor?.SetGpuPresentationState(false, "CPU/WPF fallback");
	}

	private void OnStandaloneViewportChanged(object sender, SizeChangedEventArgs e) => UpdateStandalonePhysicalSize();

	private void UpdateStandalonePhysicalSize()
	{
		if (_standaloneViewport is null || _standaloneViewport.ActualWidth <= 0 || _standaloneViewport.ActualHeight <= 0)
			return;

		var dpi = VisualTreeHelper.GetDpi(_standaloneViewport);
		Width = Math.Max(1, Math.Round(_standaloneViewport.ActualWidth * dpi.DpiScaleX));
		Height = Math.Max(1, Math.Round(_standaloneViewport.ActualHeight * dpi.DpiScaleY));
		RenderTransformOrigin = new Point(0.5, 0.5);
		RenderTransform = new ScaleTransform(1.0 / dpi.DpiScaleX, 1.0 / dpi.DpiScaleY);
	}

	private void OnLoadContent(object? sender, DrawingSurfaceEventArgs e)
	{
		_deviceAdapterLuid = ResolveAdapterLuid(e.Device);

		var vertexBytecode = Compiler.Compile(ShaderSource, "VSMain", "GpuMonitorPresentation.hlsl", "vs_4_0");
		var pixelBytecode = Compiler.Compile(ShaderSource, "PSMain", "GpuMonitorPresentation.hlsl", "ps_4_0");
		_vertexShader = e.Device.CreateVertexShader(vertexBytecode.Span);
		_pixelShader = e.Device.CreatePixelShader(pixelBytecode.Span);
		_inputLayout = e.Device.CreateInputLayout(
			new[]
			{
				new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
				new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
			},
			vertexBytecode.Span);
		_vertexBuffer = e.Device.CreateBuffer(
			checked((uint)(Marshal.SizeOf<MonitorVertex>() * 4)),
			BindFlags.VertexBuffer,
			ResourceUsage.Dynamic,
			CpuAccessFlags.Write);
		_colorBuffer = e.Device.CreateBuffer(
			16,
			BindFlags.ConstantBuffer,
			ResourceUsage.Dynamic,
			CpuAccessFlags.Write);
		_linearSampler = e.Device.CreateSamplerState(SamplerDescription.LinearClamp);
		_pointSampler = e.Device.CreateSamplerState(SamplerDescription.PointClamp);
		Invalidate();
	}

	private void OnUnloadContent(object? sender, DrawingSurfaceEventArgs e)
	{
		CloseSharedResource();
		_pointSampler?.Dispose();
		_linearSampler?.Dispose();
		_colorBuffer?.Dispose();
		_vertexBuffer?.Dispose();
		_inputLayout?.Dispose();
		_pixelShader?.Dispose();
		_vertexShader?.Dispose();
		_pointSampler = null;
		_linearSampler = null;
		_colorBuffer = null;
		_vertexBuffer = null;
		_inputLayout = null;
		_pixelShader = null;
		_vertexShader = null;
	}

	private void OnDraw(object? sender, DrawEventArgs e)
	{
		e.Context.ClearRenderTargetView(e.Surface.ColorTextureView!, new Color4(0, 0, 0, 0));
		var frame = GpuFrame;
		if (frame is null || !TryOpenSharedResource(frame, e.Device))
		{
			Monitor?.SetGpuPresentationState(false, "CPU/WPF fallback");
			return;
		}

		try
		{
			var geometry = ResolveGeometry(frame, e.Surface.TextureWidth, e.Surface.TextureHeight);
			UpdateVertices(e.Context, geometry);
			UpdateColorConstants(e.Context, frame.Descriptor.Color);

			e.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
			e.Context.IASetInputLayout(_inputLayout);
			e.Context.IASetVertexBuffer(0, _vertexBuffer, checked((uint)Marshal.SizeOf<MonitorVertex>()));
			e.Context.VSSetShader(_vertexShader);
			e.Context.PSSetShader(_pixelShader);
			e.Context.PSSetShaderResource(0, _sharedView!);
			e.Context.PSSetConstantBuffer(0, _colorBuffer);
			e.Context.PSSetSampler(0, geometry.PointSampling ? _pointSampler : _linearSampler);
			e.Context.Draw(4, 0);
			e.Context.PSUnsetShaderResource(0);

			Monitor?.SetGpuPresentationState(
				true,
				$"GPU D3D11 · {frame.Resource.Format.Width}×{frame.Resource.Format.Height} · {(geometry.PointSampling ? "POINT" : "LINEAR")}");
		}
		catch
		{
			Monitor?.SetGpuPresentationState(false, "GPU presentation failed · CPU/WPF fallback");
			CloseSharedResource();
		}
	}

	private bool TryOpenSharedResource(OperatorGpuMonitoringFrame frame, ID3D11Device1 device)
	{
		if (_sharedTexture is not null && _openedResourceId == frame.Resource.ResourceId)
			return true;

		CloseSharedResource();
		var interop = frame.Resource.Interop;
		if (interop.Kind != MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle ||
			interop.SharedHandle == 0 ||
			interop.AdapterLuid != _deviceAdapterLuid)
		{
			return false;
		}

		try
		{
			_sharedTexture = device.OpenSharedResource<ID3D11Texture2D>(
				new IntPtr(unchecked((long)interop.SharedHandle)));
			_sharedView = device.CreateShaderResourceView(_sharedTexture);
			_openedResourceId = frame.Resource.ResourceId;
			return true;
		}
		catch
		{
			CloseSharedResource();
			return false;
		}
	}

	private PresentationGeometry ResolveGeometry(OperatorGpuMonitoringFrame frame, int targetWidth, int targetHeight)
	{
		var sourceWidth = Math.Max(1.0, frame.Resource.Format.Width);
		var sourceHeight = Math.Max(1.0, frame.Resource.Format.Height);
		double left;
		double top;
		double width;
		double height;
		double scale;

		if (Monitor is not null)
		{
			var dpiX = Math.Max(0.01, Monitor.DpiScaleX);
			var dpiY = Math.Max(0.01, Monitor.DpiScaleY);
			width = Monitor.PresentationWidth * dpiX;
			height = Monitor.PresentationHeight * dpiY;
			left = ((targetWidth - width) / 2.0) + (Monitor.PresentationOffsetX * dpiX);
			top = ((targetHeight - height) / 2.0) + (Monitor.PresentationOffsetY * dpiY);
			scale = width / sourceWidth;
		}
		else
		{
			scale = Math.Min(targetWidth / sourceWidth, targetHeight / sourceHeight);
			width = sourceWidth * scale;
			height = sourceHeight * scale;
			left = (targetWidth - width) / 2.0;
			top = (targetHeight - height) / 2.0;
		}

		var integralScale = scale >= 1.0 && Math.Abs(scale - Math.Round(scale)) <= 0.0001;
		var pointSampling = Monitor?.IsPixelGridVisible == true || integralScale;
		return new PresentationGeometry(left, top, width, height, pointSampling);
	}

	private void UpdateVertices(ID3D11DeviceContext1 context, PresentationGeometry geometry)
	{
		var targetWidth = Math.Max(1.0, TextureWidth);
		var targetHeight = Math.Max(1.0, TextureHeight);
		var left = (float)((geometry.Left / targetWidth * 2.0) - 1.0);
		var right = (float)(((geometry.Left + geometry.Width) / targetWidth * 2.0) - 1.0);
		var top = (float)(1.0 - (geometry.Top / targetHeight * 2.0));
		var bottom = (float)(1.0 - ((geometry.Top + geometry.Height) / targetHeight * 2.0));

		var mapped = context.Map(_vertexBuffer!, 0, MapMode.WriteDiscard);
		var vertices = mapped.AsSpan<MonitorVertex>(4);
		vertices[0] = new MonitorVertex(new Vector2(left, top), new Vector2(0, 0));
		vertices[1] = new MonitorVertex(new Vector2(right, top), new Vector2(1, 0));
		vertices[2] = new MonitorVertex(new Vector2(left, bottom), new Vector2(0, 1));
		vertices[3] = new MonitorVertex(new Vector2(right, bottom), new Vector2(1, 1));
		context.Unmap(_vertexBuffer!, 0);
	}

	private void UpdateColorConstants(ID3D11DeviceContext1 context, ColorDescription color)
	{
		var transfer = color.Transfer switch
		{
			ColorTransfer.Linear => 1,
			ColorTransfer.Srgb => 2,
			ColorTransfer.Rec709 => 3,
			_ => 0
		};
		var mapped = context.Map(_colorBuffer!, 0, MapMode.WriteDiscard);
		mapped.AsSpan<ColorConstants>(1)[0] = new ColorConstants(
			transfer,
			color.Range == NominalRange.Limited ? 1 : 0,
			color.IsComplete ? 1 : 0,
			0);
		context.Unmap(_colorBuffer!, 0);
	}

	private static long ResolveAdapterLuid(ID3D11Device device)
	{
		using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
		using var adapter = dxgiDevice.GetAdapter();
		using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
		var luid = adapter1.Description1.Luid;
		return ((long)luid.HighPart << 32) | luid.LowPart;
	}

	private void CloseSharedResource()
	{
		_sharedView?.Dispose();
		_sharedTexture?.Dispose();
		_sharedView = null;
		_sharedTexture = null;
		_openedResourceId = null;
	}

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct MonitorVertex(Vector2 Position, Vector2 Texture);

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct ColorConstants(int Transfer, int LimitedRange, int Complete, int Padding);

	private readonly record struct PresentationGeometry(double Left, double Top, double Width, double Height, bool PointSampling);

	private const string ShaderSource = """
Texture2D SourceTexture : register(t0);
SamplerState SourceSampler : register(s0);

cbuffer ColorTransform : register(b0)
{
    int TransferMode;
    int LimitedRange;
    int CompleteColor;
    int Padding;
};

struct VSInput
{
    float2 Position : POSITION;
    float2 Texture : TEXCOORD0;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Texture : TEXCOORD0;
};

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.Position = float4(input.Position, 0.0, 1.0);
    output.Texture = input.Texture;
    return output;
}

float SrgbToLinear(float value)
{
    return value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4);
}

float Rec709ToLinear(float value)
{
    return value < 0.081 ? value / 4.5 : pow((value + 0.099) / 1.099, 1.0 / 0.45);
}

float LinearToSrgb(float value)
{
    return value <= 0.0031308 ? value * 12.92 : 1.055 * pow(value, 1.0 / 2.4) - 0.055;
}

float TransformComponent(float value)
{
    if (LimitedRange != 0)
        value = saturate((value - (16.0 / 255.0)) / (219.0 / 255.0));

    float linear = value;
    if (TransferMode == 2)
        linear = SrgbToLinear(value);
    else if (TransferMode == 3)
        linear = Rec709ToLinear(value);

    return saturate(LinearToSrgb(linear));
}

float4 PSMain(PSInput input) : SV_TARGET
{
    float4 sample = SourceTexture.Sample(SourceSampler, input.Texture);
    if (CompleteColor != 0)
    {
        sample.r = TransformComponent(sample.r);
        sample.g = TransformComponent(sample.g);
        sample.b = TransformComponent(sample.b);
    }

    sample.rgb *= sample.a;
    return sample;
}
""";
}
