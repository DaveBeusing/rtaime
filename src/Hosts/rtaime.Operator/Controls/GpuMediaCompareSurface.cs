// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using rtaime.Media.Contracts;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Wpf;

namespace rtaime.Operator.Controls;

public sealed class GpuMediaCompareSurface : DrawingSurface
{
	private ID3D11VertexShader? _vertexShader;
	private ID3D11PixelShader? _pixelShader;
	private ID3D11InputLayout? _inputLayout;
	private ID3D11Buffer? _vertexBuffer;
	private ID3D11Buffer? _constants;
	private ID3D11SamplerState? _sampler;
	private ID3D11Texture2D? _textureA;
	private ID3D11Texture2D? _textureB;
	private ID3D11ShaderResourceView? _viewA;
	private ID3D11ShaderResourceView? _viewB;
	private MonitoringResourceId? _resourceA;
	private MonitoringResourceId? _resourceB;
	private long _deviceAdapterLuid;

	public static readonly DependencyProperty MonitoringProperty = DependencyProperty.Register(
		nameof(Monitoring), typeof(OperatorMonitoringViewModel), typeof(GpuMediaCompareSurface),
		new FrameworkPropertyMetadata(null, OnStateChanged));

	public static readonly DependencyProperty FrameAProperty = DependencyProperty.Register(
		nameof(FrameA), typeof(OperatorGpuMonitoringFrame), typeof(GpuMediaCompareSurface),
		new FrameworkPropertyMetadata(null, OnFrameChanged));

	public static readonly DependencyProperty FrameBProperty = DependencyProperty.Register(
		nameof(FrameB), typeof(OperatorGpuMonitoringFrame), typeof(GpuMediaCompareSurface),
		new FrameworkPropertyMetadata(null, OnFrameChanged));

	public static readonly DependencyProperty CompareModeProperty = DependencyProperty.Register(
		nameof(CompareMode), typeof(MediaCompareMode), typeof(GpuMediaCompareSurface),
		new FrameworkPropertyMetadata(MediaCompareMode.Off, OnStateChanged));

	public static readonly DependencyProperty WipePositionProperty = DependencyProperty.Register(
		nameof(WipePosition), typeof(double), typeof(GpuMediaCompareSurface),
		new FrameworkPropertyMetadata(0.5d, OnStateChanged, CoerceWipe));

	public GpuMediaCompareSurface()
	{
		DepthStencilFormat = Format.Unknown;
		ColorFormat = Format.B8G8R8A8_UNorm;
		IsHitTestVisible = false;
		LoadContent += OnLoadContent;
		Draw += OnDraw;
		UnloadContent += OnUnloadContent;
		Unloaded += OnUnloaded;
	}

	public OperatorMonitoringViewModel? Monitoring
	{
		get => (OperatorMonitoringViewModel?)GetValue(MonitoringProperty);
		set => SetValue(MonitoringProperty, value);
	}

	public OperatorGpuMonitoringFrame? FrameA
	{
		get => (OperatorGpuMonitoringFrame?)GetValue(FrameAProperty);
		set => SetValue(FrameAProperty, value);
	}

	public OperatorGpuMonitoringFrame? FrameB
	{
		get => (OperatorGpuMonitoringFrame?)GetValue(FrameBProperty);
		set => SetValue(FrameBProperty, value);
	}

	public MediaCompareMode CompareMode
	{
		get => (MediaCompareMode)GetValue(CompareModeProperty);
		set => SetValue(CompareModeProperty, value);
	}

	public double WipePosition
	{
		get => (double)GetValue(WipePositionProperty);
		set => SetValue(WipePositionProperty, value);
	}

	private static object CoerceWipe(DependencyObject _, object value) => Math.Clamp((double)value, 0d, 1d);

	private static void OnFrameChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var surface = (GpuMediaCompareSurface)dependencyObject;
		surface.CloseResources();
		surface.Invalidate();
	}

	private static void OnStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		((GpuMediaCompareSurface)dependencyObject).Invalidate();
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		Monitoring?.SetGpuComparisonState(false, "GPU A/B compare surface is unloaded.");
		CloseResources();
	}

	private void OnLoadContent(object? sender, DrawingSurfaceEventArgs e)
	{
		_deviceAdapterLuid = ResolveAdapterLuid(e.Device);
		var vertexBytecode = Compiler.Compile(ShaderSource, "VSMain", "GpuMediaCompare.hlsl", "vs_4_0");
		var pixelBytecode = Compiler.Compile(ShaderSource, "PSMain", "GpuMediaCompare.hlsl", "ps_4_0");
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
			checked((uint)(Marshal.SizeOf<CompareVertex>() * 4)),
			BindFlags.VertexBuffer,
			ResourceUsage.Dynamic,
			CpuAccessFlags.Write);
		_constants = e.Device.CreateBuffer(
			checked((uint)Marshal.SizeOf<CompareConstants>()),
			BindFlags.ConstantBuffer,
			ResourceUsage.Dynamic,
			CpuAccessFlags.Write);
		_sampler = e.Device.CreateSamplerState(SamplerDescription.LinearClamp);
		Invalidate();
	}

	private void OnUnloadContent(object? sender, DrawingSurfaceEventArgs e)
	{
		CloseResources();
		_sampler?.Dispose();
		_constants?.Dispose();
		_vertexBuffer?.Dispose();
		_inputLayout?.Dispose();
		_pixelShader?.Dispose();
		_vertexShader?.Dispose();
		_sampler = null;
		_constants = null;
		_vertexBuffer = null;
		_inputLayout = null;
		_pixelShader = null;
		_vertexShader = null;
	}

	private void OnDraw(object? sender, DrawEventArgs e)
	{
		e.Context.ClearRenderTargetView(e.Surface.ColorTextureView!, new Color4(0, 0, 0, 0));
		if (CompareMode == MediaCompareMode.Off)
		{
			Monitoring?.SetGpuComparisonState(false, "A/B comparison is off.");
			return;
		}

		var compatibility = MediaComparisonCompatibility.Evaluate(FrameA, FrameB);
		if (!compatibility.IsCompatible)
		{
			Monitoring?.SetGpuComparisonState(false, compatibility.Detail);
			return;
		}

		if (FrameA is not { } a || FrameB is not { } b ||
			!TryOpenResources(a, b, e.Device))
		{
			Monitoring?.SetGpuComparisonState(false, "GPU compare resources could not be opened on the presentation adapter.");
			return;
		}

		try
		{
			UpdateVertices(e.Context, a.Resource.Format.Width, a.Resource.Format.Height, e.Surface.TextureWidth, e.Surface.TextureHeight);
			UpdateConstants(e.Context, a.Resource.Format.Color);
			e.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
			e.Context.IASetInputLayout(_inputLayout);
			e.Context.IASetVertexBuffer(0, _vertexBuffer!, checked((uint)Marshal.SizeOf<CompareVertex>()));
			e.Context.VSSetShader(_vertexShader);
			e.Context.PSSetShader(_pixelShader);
			e.Context.PSSetShaderResource(0, _viewA!);
			e.Context.PSSetShaderResource(1, _viewB!);
			e.Context.PSSetConstantBuffer(0, _constants);
			e.Context.PSSetSampler(0, _sampler);
			e.Context.Draw(4, 0);
			e.Context.PSUnsetShaderResource(0);
			e.Context.PSUnsetShaderResource(1);
			Monitoring?.SetGpuComparisonState(true, $"GPU {CompareMode} · Program=A · Preview=B");
		}
		catch
		{
			Monitoring?.SetGpuComparisonState(false, "GPU compare presentation failed; CPU fallback remains available.");
			CloseResources();
		}
	}

	private bool TryOpenResources(OperatorGpuMonitoringFrame a, OperatorGpuMonitoringFrame b, ID3D11Device1 device)
	{
		if (_textureA is not null && _textureB is not null &&
			_resourceA == a.Resource.ResourceId && _resourceB == b.Resource.ResourceId)
			return true;

		CloseResources();
		if (!CanOpen(a) || !CanOpen(b))
			return false;

		try
		{
			_textureA = device.OpenSharedResource<ID3D11Texture2D>(new IntPtr(unchecked((long)a.Resource.Interop.SharedHandle)));
			_textureB = device.OpenSharedResource<ID3D11Texture2D>(new IntPtr(unchecked((long)b.Resource.Interop.SharedHandle)));
			_viewA = device.CreateShaderResourceView(_textureA);
			_viewB = device.CreateShaderResourceView(_textureB);
			_resourceA = a.Resource.ResourceId;
			_resourceB = b.Resource.ResourceId;
			return true;
		}
		catch
		{
			CloseResources();
			return false;
		}

		bool CanOpen(OperatorGpuMonitoringFrame frame) =>
			frame.Resource.AccessMode == MonitoringResourceAccessMode.ReadOnly &&
			frame.Resource.Interop.Kind == MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle &&
			frame.Resource.Interop.SharedHandle != 0 &&
			frame.Resource.Interop.AdapterLuid == _deviceAdapterLuid;
	}

	private void UpdateVertices(ID3D11DeviceContext1 context, uint sourceWidth, uint sourceHeight, int targetWidth, int targetHeight)
	{
		var scale = Math.Min(targetWidth / (double)sourceWidth, targetHeight / (double)sourceHeight);
		var width = sourceWidth * scale;
		var height = sourceHeight * scale;
		var leftPx = (targetWidth - width) / 2.0;
		var topPx = (targetHeight - height) / 2.0;
		var left = (float)((leftPx / targetWidth * 2.0) - 1.0);
		var right = (float)(((leftPx + width) / targetWidth * 2.0) - 1.0);
		var top = (float)(1.0 - (topPx / targetHeight * 2.0));
		var bottom = (float)(1.0 - ((topPx + height) / targetHeight * 2.0));

		var mapped = context.Map(_vertexBuffer!, 0, MapMode.WriteDiscard);
		var vertices = mapped.AsSpan<CompareVertex>(4);
		vertices[0] = new CompareVertex(new Vector2(left, top), new Vector2(0, 0));
		vertices[1] = new CompareVertex(new Vector2(right, top), new Vector2(1, 0));
		vertices[2] = new CompareVertex(new Vector2(left, bottom), new Vector2(0, 1));
		vertices[3] = new CompareVertex(new Vector2(right, bottom), new Vector2(1, 1));
		context.Unmap(_vertexBuffer!, 0);
	}

	private void UpdateConstants(ID3D11DeviceContext1 context, ColorDescription color)
	{
		var transfer = color.Transfer switch
		{
			ColorTransfer.Linear => 1,
			ColorTransfer.Srgb => 2,
			ColorTransfer.Rec709 => 3,
			_ => 0
		};
		var mapped = context.Map(_constants!, 0, MapMode.WriteDiscard);
		mapped.AsSpan<CompareConstants>(1)[0] = new CompareConstants(
			(int)CompareMode,
			(float)WipePosition,
			transfer,
			color.Range == NominalRange.Limited ? 1 : 0,
			color.IsComplete ? 1 : 0,
			0, 0, 0);
		context.Unmap(_constants!, 0);
	}

	private void CloseResources()
	{
		_viewB?.Dispose();
		_viewA?.Dispose();
		_textureB?.Dispose();
		_textureA?.Dispose();
		_viewB = null;
		_viewA = null;
		_textureB = null;
		_textureA = null;
		_resourceB = null;
		_resourceA = null;
	}

	private static long ResolveAdapterLuid(ID3D11Device device)
	{
		using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
		using var adapter = dxgiDevice.GetAdapter();
		using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
		var luid = adapter1.Description1.Luid;
		return ((long)luid.HighPart << 32) | luid.LowPart;
	}

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct CompareVertex(Vector2 Position, Vector2 Texture);

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct CompareConstants(
		int CompareMode,
		float WipePosition,
		int TransferMode,
		int LimitedRange,
		int CompleteColor,
		int Padding0,
		int Padding1,
		int Padding2);

	private const string ShaderSource = """
Texture2D ProgramTexture : register(t0);
Texture2D PreviewTexture : register(t1);
SamplerState SourceSampler : register(s0);

cbuffer CompareState : register(b0)
{
    int CompareMode;
    float WipePosition;
    int TransferMode;
    int LimitedRange;
    int CompleteColor;
    int Padding0;
    int Padding1;
    int Padding2;
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

float4 TransformSample(float4 sample)
{
    if (CompleteColor != 0)
    {
        sample.r = TransformComponent(sample.r);
        sample.g = TransformComponent(sample.g);
        sample.b = TransformComponent(sample.b);
    }
    return sample;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    float4 rawA = ProgramTexture.Sample(SourceSampler, input.Texture);
    float4 rawB = PreviewTexture.Sample(SourceSampler, input.Texture);
    float4 a = TransformSample(rawA);
    float4 b = TransformSample(rawB);

    if (CompareMode == 5)
        return float4(abs(a.rgb - b.rgb), 1.0);

    bool useB = false;
    if (CompareMode == 1)
        useB = input.Texture.x >= 0.5;
    else if (CompareMode == 2)
        useB = input.Texture.y >= 0.5;
    else if (CompareMode == 3)
        useB = input.Texture.x >= WipePosition;
    else if (CompareMode == 4)
        useB = input.Texture.y >= WipePosition;

    float4 sample = useB ? b : a;
    sample.rgb *= sample.a;
    return sample;
}
""";
}
