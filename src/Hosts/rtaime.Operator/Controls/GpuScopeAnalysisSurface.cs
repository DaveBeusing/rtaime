// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using rtaime.Media.Contracts;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Wpf;

namespace rtaime.Operator.Controls;

public sealed class GpuScopeAnalysisSurface : DrawingSurface
{
	private ID3D11ComputeShader? _shader;
	private ID3D11Buffer? _colorBuffer;
	private ID3D11Buffer? _scopeConstants;
	private ID3D11Buffer? _results;
	private ID3D11Buffer? _readback;
	private ID3D11UnorderedAccessView? _resultsView;
	private ID3D11Texture2D? _sharedTexture;
	private ID3D11ShaderResourceView? _sharedView;
	private MonitoringResourceId? _openedResourceId;
	private long _deviceAdapterLuid;
	private DateTimeOffset _lastAnalysisAt;
	private ulong _lastAnalysisSequence;
	private readonly DispatcherTimer _retryTimer = new(DispatcherPriority.Background)
	{
		Interval = GpuMonitoringAnalysisPolicy.ScopeUpdateInterval
	};

	public static readonly DependencyProperty MonitoringProperty = DependencyProperty.Register(
		nameof(Monitoring),
		typeof(OperatorMonitoringViewModel),
		typeof(GpuScopeAnalysisSurface),
		new FrameworkPropertyMetadata(null, OnAnalysisStateChanged));

	public static readonly DependencyProperty GpuFrameProperty = DependencyProperty.Register(
		nameof(GpuFrame),
		typeof(OperatorGpuMonitoringFrame),
		typeof(GpuScopeAnalysisSurface),
		new FrameworkPropertyMetadata(null, OnFrameChanged));

	public static readonly DependencyProperty AnalysisEnabledProperty = DependencyProperty.Register(
		nameof(AnalysisEnabled),
		typeof(bool),
		typeof(GpuScopeAnalysisSurface),
		new FrameworkPropertyMetadata(false, OnAnalysisStateChanged));

	public GpuScopeAnalysisSurface()
	{
		DepthStencilFormat = Format.Unknown;
		ColorFormat = Format.B8G8R8A8_UNorm;
		IsHitTestVisible = false;
		LoadContent += OnLoadContent;
		Draw += OnDraw;
		UnloadContent += OnUnloadContent;
		Unloaded += OnUnloaded;
		_retryTimer.Tick += OnRetryTick;
	}

	public OperatorMonitoringViewModel? Monitoring
	{
		get => (OperatorMonitoringViewModel?)GetValue(MonitoringProperty);
		set => SetValue(MonitoringProperty, value);
	}

	public OperatorGpuMonitoringFrame? GpuFrame
	{
		get => (OperatorGpuMonitoringFrame?)GetValue(GpuFrameProperty);
		set => SetValue(GpuFrameProperty, value);
	}

	public bool AnalysisEnabled
	{
		get => (bool)GetValue(AnalysisEnabledProperty);
		set => SetValue(AnalysisEnabledProperty, value);
	}

	private static void OnFrameChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var surface = (GpuScopeAnalysisSurface)dependencyObject;
		surface.CloseSharedResource();
		surface._lastAnalysisSequence = 0;
		surface.Invalidate();
	}

	private static void OnAnalysisStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
	{
		var surface = (GpuScopeAnalysisSurface)dependencyObject;
		if (!surface.AnalysisEnabled)
			surface._retryTimer.Stop();
		surface.Invalidate();
	}

	private void OnRetryTick(object? sender, EventArgs e)
	{
		_retryTimer.Stop();
		Invalidate();
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		_retryTimer.Stop();
		CloseSharedResource();
	}

	private void OnLoadContent(object? sender, DrawingSurfaceEventArgs e)
	{
		_deviceAdapterLuid = ResolveAdapterLuid(e.Device);
		try
		{
			var bytecode = Compiler.Compile(ShaderSource, "CSMain", "GpuMonitoringScopes.hlsl", "cs_5_0");
			_shader = e.Device.CreateComputeShader(bytecode.Span);
			_colorBuffer = e.Device.CreateBuffer(
				checked((uint)Marshal.SizeOf<ColorConstants>()),
				BindFlags.ConstantBuffer,
				ResourceUsage.Dynamic,
				CpuAccessFlags.Write);
			_scopeConstants = e.Device.CreateBuffer(
				checked((uint)Marshal.SizeOf<ScopeConstants>()),
				BindFlags.ConstantBuffer,
				ResourceUsage.Dynamic,
				CpuAccessFlags.Write);
			_results = e.Device.CreateBuffer(
				GpuMonitoringAnalysisPolicy.ScopeResultBytes,
				BindFlags.UnorderedAccess,
				ResourceUsage.Default,
				CpuAccessFlags.None,
				ResourceOptionFlags.BufferStructured,
				sizeof(uint));
			_readback = e.Device.CreateBuffer(
				GpuMonitoringAnalysisPolicy.ScopeResultBytes,
				BindFlags.None,
				ResourceUsage.Staging,
				CpuAccessFlags.Read);
			_resultsView = e.Device.CreateUnorderedAccessView(_results);
		}
		catch
		{
			DisposeAnalysisResources();
		}
		Invalidate();
	}

	private void OnUnloadContent(object? sender, DrawingSurfaceEventArgs e)
	{
		_retryTimer.Stop();
		CloseSharedResource();
		DisposeAnalysisResources();
	}

	private void OnDraw(object? sender, DrawEventArgs e)
	{
		e.Context.ClearRenderTargetView(e.Surface.ColorTextureView!, new Color4(0, 0, 0, 0));
		if (!AnalysisEnabled)
			return;

		var frame = GpuFrame;
		if (frame is null)
		{
			Monitoring?.SetGpuScopesUnavailable("GPU scope analysis is waiting for a Program shared resource.");
			return;
		}
		if (_shader is null || _colorBuffer is null || _scopeConstants is null ||
			_results is null || _readback is null || _resultsView is null)
		{
			Monitoring?.SetGpuScopesUnavailable("GPU scope compute capability is unavailable.");
			return;
		}
		if (!TryOpenSharedResource(frame, e.Device))
		{
			Monitoring?.SetGpuScopesUnavailable("Program shared resource cannot be opened on the scope-analysis adapter.");
			return;
		}

		var now = DateTimeOffset.UtcNow;
		if (frame.SequenceNumber == _lastAnalysisSequence)
			return;
		if (_lastAnalysisAt != default && now - _lastAnalysisAt < GpuMonitoringAnalysisPolicy.ScopeUpdateInterval)
		{
			if (!_retryTimer.IsEnabled)
				_retryTimer.Start();
			return;
		}

		try
		{
			Analyze(e.Context, frame);
			_lastAnalysisAt = now;
			_lastAnalysisSequence = frame.SequenceNumber;
			_retryTimer.Stop();
		}
		catch
		{
			_lastAnalysisAt = DateTimeOffset.UtcNow;
			_lastAnalysisSequence = frame.SequenceNumber;
			Monitoring?.SetGpuScopesUnavailable("GPU scope analysis failed; CPU fallback remains eligible.");
			CloseSharedResource();
		}
	}

	private void Analyze(ID3D11DeviceContext1 context, OperatorGpuMonitoringFrame frame)
	{
		var format = frame.Resource.Format;
		var stride = GpuMonitoringAnalysisPolicy.ResolveScopeSampleStride(format.Width, format.Height);
		var sampleWidth = checked(((int)format.Width + stride - 1) / stride);
		var sampleHeight = checked(((int)format.Height + stride - 1) / stride);
		var sampleCount = checked(sampleWidth * sampleHeight);
		UpdateConstants(context, format.Color, stride, checked((int)format.Width), checked((int)format.Height));

		var started = Stopwatch.GetTimestamp();
		context.ClearUnorderedAccessView(_resultsView!, new Int4(0, 0, 0, 0));
		context.CSSetShader(_shader);
		context.CSSetShaderResource(0, _sharedView!);
		context.CSSetConstantBuffer(0, _colorBuffer);
		context.CSSetConstantBuffer(1, _scopeConstants);
		context.CSSetUnorderedAccessView(0, _resultsView);
		context.Dispatch(
			checked((uint)((sampleWidth + 7) / 8)),
			checked((uint)((sampleHeight + 7) / 8)),
			1);
		context.CSUnsetShaderResource(0);
		context.CSUnsetUnorderedAccessView(0);
		context.CopyResource(_readback!, _results!);

		var mapped = context.Map(_readback!, 0, MapMode.Read);
		try
		{
			var results = mapped.AsSpan<uint>(GpuMonitoringAnalysisPolicy.ScopeResultCount);
			var snapshot = MediaScopeSnapshot.FromGpuResultBuffer(
				frame.SequenceNumber,
				format.Width,
				format.Height,
				format.Color,
				stride,
				sampleCount,
				results,
				Stopwatch.GetElapsedTime(started));
			Monitoring?.ApplyGpuScopes(snapshot);
		}
		finally
		{
			context.Unmap(_readback!, 0);
		}
	}

	private void UpdateConstants(ID3D11DeviceContext1 context, ColorDescription color, int stride, int sourceWidth, int sourceHeight)
	{
		var transfer = color.Transfer switch
		{
			ColorTransfer.Linear => 1,
			ColorTransfer.Srgb => 2,
			ColorTransfer.Rec709 => 3,
			_ => 0
		};
		var colorMapped = context.Map(_colorBuffer!, 0, MapMode.WriteDiscard);
		colorMapped.AsSpan<ColorConstants>(1)[0] = new ColorConstants(
			transfer,
			color.Range == NominalRange.Limited ? 1 : 0,
			color.IsComplete ? 1 : 0,
			color.IsComplete ? 1 : 0);
		context.Unmap(_colorBuffer!, 0);

		var scopeMapped = context.Map(_scopeConstants!, 0, MapMode.WriteDiscard);
		scopeMapped.AsSpan<ScopeConstants>(1)[0] = new ScopeConstants(sourceWidth, sourceHeight, stride, 0);
		context.Unmap(_scopeConstants!, 0);
	}

	private bool TryOpenSharedResource(OperatorGpuMonitoringFrame frame, ID3D11Device1 device)
	{
		if (_sharedTexture is not null && _openedResourceId == frame.Resource.ResourceId)
			return true;

		CloseSharedResource();
		var interop = frame.Resource.Interop;
		if (frame.Resource.AccessMode != MonitoringResourceAccessMode.ReadOnly ||
			interop.Kind != MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle ||
			interop.SharedHandle == 0 ||
			interop.AdapterLuid != _deviceAdapterLuid)
		{
			return false;
		}

		try
		{
			_sharedTexture = device.OpenSharedResource<ID3D11Texture2D>(new IntPtr(unchecked((long)interop.SharedHandle)));
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

	private void CloseSharedResource()
	{
		_sharedView?.Dispose();
		_sharedTexture?.Dispose();
		_sharedView = null;
		_sharedTexture = null;
		_openedResourceId = null;
	}

	private void DisposeAnalysisResources()
	{
		_resultsView?.Dispose();
		_readback?.Dispose();
		_results?.Dispose();
		_scopeConstants?.Dispose();
		_colorBuffer?.Dispose();
		_shader?.Dispose();
		_resultsView = null;
		_readback = null;
		_results = null;
		_scopeConstants = null;
		_colorBuffer = null;
		_shader = null;
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
	private readonly record struct ColorConstants(int TransferMode, int LimitedRange, int CompleteColor, int VectorscopeEnabled);

	[StructLayout(LayoutKind.Sequential)]
	private readonly record struct ScopeConstants(int SourceWidth, int SourceHeight, int SampleStride, int Padding);

	private const string ShaderSource = """
Texture2D SourceTexture : register(t0);

cbuffer ColorTransform : register(b0)
{
    int TransferMode;
    int LimitedRange;
    int CompleteColor;
    int VectorscopeEnabled;
};

cbuffer ScopeAnalysis : register(b1)
{
    int SourceWidth;
    int SourceHeight;
    int SampleStride;
    int ScopePadding;
};

RWStructuredBuffer<uint> Results : register(u0);

static const uint HistogramBins = 256;
static const uint WaveformColumns = 64;
static const uint WaveformLevels = 64;
static const uint VectorscopeSize = 64;
static const uint LumaHistogramOffset = 0;
static const uint RedHistogramOffset = 256;
static const uint GreenHistogramOffset = 512;
static const uint BlueHistogramOffset = 768;
static const uint WaveformOffset = 1024;
static const uint RedWaveformOffset = 5120;
static const uint GreenWaveformOffset = 9216;
static const uint BlueWaveformOffset = 13312;
static const uint VectorscopeOffset = 17408;

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

[numthreads(8, 8, 1)]
void CSMain(uint3 threadId : SV_DispatchThreadID)
{
    int x = (int)threadId.x * SampleStride;
    int y = (int)threadId.y * SampleStride;
    if (x >= SourceWidth || y >= SourceHeight)
        return;

    float4 sample = SourceTexture.Load(int3(x, y, 0));
    if (CompleteColor != 0)
    {
        sample.r = TransformComponent(sample.r);
        sample.g = TransformComponent(sample.g);
        sample.b = TransformComponent(sample.b);
    }

    uint r = (uint)round(saturate(sample.r) * 255.0);
    uint g = (uint)round(saturate(sample.g) * 255.0);
    uint b = (uint)round(saturate(sample.b) * 255.0);
    uint y8 = (77 * r + 150 * g + 29 * b + 128) >> 8;

    InterlockedAdd(Results[LumaHistogramOffset + y8], 1);
    InterlockedAdd(Results[RedHistogramOffset + r], 1);
    InterlockedAdd(Results[GreenHistogramOffset + g], 1);
    InterlockedAdd(Results[BlueHistogramOffset + b], 1);

    uint column = min(WaveformColumns - 1, (uint)x * WaveformColumns / (uint)SourceWidth);
    uint lumaLevel = min(WaveformLevels - 1, y8 * WaveformLevels / 256);
    uint redLevel = min(WaveformLevels - 1, r * WaveformLevels / 256);
    uint greenLevel = min(WaveformLevels - 1, g * WaveformLevels / 256);
    uint blueLevel = min(WaveformLevels - 1, b * WaveformLevels / 256);
    InterlockedAdd(Results[WaveformOffset + (WaveformLevels - 1 - lumaLevel) * WaveformColumns + column], 1);
    InterlockedAdd(Results[RedWaveformOffset + (WaveformLevels - 1 - redLevel) * WaveformColumns + column], 1);
    InterlockedAdd(Results[GreenWaveformOffset + (WaveformLevels - 1 - greenLevel) * WaveformColumns + column], 1);
    InterlockedAdd(Results[BlueWaveformOffset + (WaveformLevels - 1 - blueLevel) * WaveformColumns + column], 1);

    if (VectorscopeEnabled != 0)
    {
        int cb = clamp(((-43 * (int)r - 85 * (int)g + 128 * (int)b + 128) >> 8) + 128, 0, 255);
        int cr = clamp(((128 * (int)r - 107 * (int)g - 21 * (int)b + 128) >> 8) + 128, 0, 255);
        uint vx = min(VectorscopeSize - 1, (uint)cb * VectorscopeSize / 256);
        uint vy = min(VectorscopeSize - 1, (uint)cr * VectorscopeSize / 256);
        InterlockedAdd(Results[VectorscopeOffset + vy * VectorscopeSize + vx], 1);
    }
}
""";
}
