// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.Gpu;

public enum CudaQualificationStatus
{
	Unverified = 1,
	Passed = 2,
	Failed = 3
}

public sealed record CudaQualificationProfile
{
	public CudaQualificationProfile(
		string expectedDeviceName,
		int deviceOrdinal = 0,
		int warmupIterations = 4,
		int sampleIterations = 30)
	{
		if (string.IsNullOrWhiteSpace(expectedDeviceName))
			throw new ArgumentException("CUDA reference qualification requires an expected device name.", nameof(expectedDeviceName));
		if (deviceOrdinal < 0)
			throw new ArgumentOutOfRangeException(nameof(deviceOrdinal));
		if (warmupIterations < 0)
			throw new ArgumentOutOfRangeException(nameof(warmupIterations));
		if (sampleIterations < 10)
			throw new ArgumentOutOfRangeException(nameof(sampleIterations), "CUDA reference qualification requires at least ten measured samples.");

		ExpectedDeviceName = expectedDeviceName.Trim();
		DeviceOrdinal = deviceOrdinal;
		WarmupIterations = warmupIterations;
		SampleIterations = sampleIterations;
	}

	public string ExpectedDeviceName { get; }
	public int DeviceOrdinal { get; }
	public int WarmupIterations { get; }
	public int SampleIterations { get; }
}

public sealed record CudaQualificationCaseResult(
	string Format,
	string Operation,
	int Samples,
	double FrameBudgetMilliseconds,
	double P50Milliseconds,
	double P95Milliseconds,
	double MaximumMilliseconds,
	bool PixelCorrect,
	bool SurfaceLifetimeCorrect,
	bool TimingBudgetMet);

public sealed record CudaQualificationReport(
	string SchemaVersion,
	DateTimeOffset CapturedAtUtc,
	CudaQualificationStatus Status,
	string ExpectedDeviceName,
	int DeviceOrdinal,
	string DetectedDeviceName,
	ulong? TotalMemoryBytes,
	IReadOnlyList<CudaQualificationCaseResult> Cases,
	IReadOnlyList<string> Failures)
{
	public const string CurrentSchemaVersion = "1.0";
}

public static class CudaReferenceHardwareQualification
{
	private static readonly MediaSourceId SourceA = new(Identity.Parse("8d000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId SourceB = new(Identity.Parse("8d000000-0000-0000-0000-000000000002"));
	private static readonly MediaSourceId LayerSource = new(Identity.Parse("8d000000-0000-0000-0000-000000000003"));
	private static readonly MediaSourceId OutputSource = new(Identity.Parse("8d000000-0000-0000-0000-000000000004"));

	public static CudaQualificationReport Run(
		CudaQualificationProfile profile,
		DateTimeOffset? capturedAtUtc = null)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var captured = (capturedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
		var detected = CudaGpuProcessingBackend.Detect(profile.DeviceOrdinal);
		var failures = new List<string>();
		var cases = new List<CudaQualificationCaseResult>();

		if (!detected.Available)
		{
			failures.Add(detected.Failure?.Message ?? "CUDA backend is unavailable.");
			return CreateReport(profile, detected, captured, CudaQualificationStatus.Failed, cases, failures);
		}
		if (!detected.HardwareAccelerated || detected.Kind != GpuBackendKind.NvidiaCuda)
		{
			failures.Add("Detected backend is not the NVIDIA CUDA hardware backend.");
			return CreateReport(profile, detected, captured, CudaQualificationStatus.Failed, cases, failures);
		}
		if (!detected.DeviceName.Contains(profile.ExpectedDeviceName, StringComparison.OrdinalIgnoreCase))
		{
			failures.Add($"Detected CUDA device '{detected.DeviceName}' does not match required reference device '{profile.ExpectedDeviceName}'.");
			return CreateReport(profile, detected, captured, CudaQualificationStatus.Failed, cases, failures);
		}

		try
		{
			using var provider = new GpuProcessingProvider(new CudaGpuProcessingBackend(profile.DeviceOrdinal));
			provider.Start();
			foreach (var format in new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 })
			{
				cases.Add(RunCase(provider, format, "CUT_A", GpuTransition.CutToA, includeLayer: false, profile));
				cases.Add(RunCase(provider, format, "CUT_B", GpuTransition.CutToB, includeLayer: false, profile));
				cases.Add(RunCase(provider, format, "DISSOLVE_50", GpuTransition.Dissolve(128), includeLayer: false, profile));
				cases.Add(RunCase(provider, format, "DISSOLVE_LAYER", GpuTransition.Dissolve(128), includeLayer: true, profile));
			}
		}
		catch (Exception exception)
		{
			failures.Add($"CUDA qualification execution failed: {exception.GetType().Name}: {exception.Message}");
		}

		foreach (var result in cases)
		{
			if (!result.PixelCorrect)
				failures.Add($"{result.Format}/{result.Operation}: output pixel verification failed.");
			if (!result.SurfaceLifetimeCorrect)
				failures.Add($"{result.Format}/{result.Operation}: GPU surface lifetime did not return to the expected baseline.");
			if (!result.TimingBudgetMet)
				failures.Add($"{result.Format}/{result.Operation}: P95 or maximum synchronous composite/readback latency exceeded the V1 frame budget guard.");
		}

		var status = failures.Count == 0 && cases.Count == 8
			? CudaQualificationStatus.Passed
			: CudaQualificationStatus.Failed;
		return CreateReport(profile, detected, captured, status, cases, failures);
	}

	public static CudaQualificationReport Unverified(
		CudaQualificationProfile profile,
		string reason,
		DateTimeOffset? capturedAtUtc = null)
	{
		ArgumentNullException.ThrowIfNull(profile);
		if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("UNVERIFIED evidence requires a reason.", nameof(reason));
		var detected = CudaGpuProcessingBackend.Detect(profile.DeviceOrdinal);
		return CreateReport(
			profile,
			detected,
			(capturedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
			CudaQualificationStatus.Unverified,
			Array.Empty<CudaQualificationCaseResult>(),
			new[] { reason.Trim() });
	}

	public static string Serialize(CudaQualificationReport report)
	{
		ArgumentNullException.ThrowIfNull(report);
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			writer.WriteString("schemaVersion", report.SchemaVersion);
			writer.WriteString("capturedAtUtc", report.CapturedAtUtc);
			writer.WriteString("status", report.Status.ToString().ToUpperInvariant());
			writer.WriteString("expectedDeviceName", report.ExpectedDeviceName);
			writer.WriteNumber("deviceOrdinal", report.DeviceOrdinal);
			writer.WriteString("detectedDeviceName", report.DetectedDeviceName);
			if (report.TotalMemoryBytes is { } memory) writer.WriteNumber("totalMemoryBytes", memory);
			else writer.WriteNull("totalMemoryBytes");
			writer.WriteStartArray("cases");
			foreach (var item in report.Cases.OrderBy(item => item.Format, StringComparer.Ordinal).ThenBy(item => item.Operation, StringComparer.Ordinal))
			{
				writer.WriteStartObject();
				writer.WriteString("format", item.Format);
				writer.WriteString("operation", item.Operation);
				writer.WriteNumber("samples", item.Samples);
				writer.WriteNumber("frameBudgetMilliseconds", item.FrameBudgetMilliseconds);
				writer.WriteNumber("p50Milliseconds", item.P50Milliseconds);
				writer.WriteNumber("p95Milliseconds", item.P95Milliseconds);
				writer.WriteNumber("maximumMilliseconds", item.MaximumMilliseconds);
				writer.WriteBoolean("pixelCorrect", item.PixelCorrect);
				writer.WriteBoolean("surfaceLifetimeCorrect", item.SurfaceLifetimeCorrect);
				writer.WriteBoolean("timingBudgetMet", item.TimingBudgetMet);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteStartArray("failures");
			foreach (var failure in report.Failures) writer.WriteStringValue(failure);
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static CudaQualificationCaseResult RunCase(
		GpuProcessingProvider provider,
		VideoFormat format,
		string operation,
		GpuTransition transition,
		bool includeLayer,
		CudaQualificationProfile profile)
	{
		var timebase = new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator);
		var timing = new FrameTiming(0, 0, timebase);
		var sourceA = new StaticRgbaSource(SourceA, RgbaFrameBuffer.Solid(format, 16, 32, 64));
		var sourceB = new StaticRgbaSource(SourceB, RgbaFrameBuffer.Solid(format, 192, 128, 64));
		var layerSource = new StaticRgbaSource(LayerSource, RgbaFrameBuffer.Solid(format, 255, 255, 255, 96));

		using var a = sourceA.Materialize(provider, timing);
		using var b = sourceB.Materialize(provider, timing);
		using var layer = layerSource.Materialize(provider, timing);
		var baseline = provider.ActiveSurfaceCount;
		if (baseline != 3) throw new InvalidOperationException($"CUDA qualification expected three persistent input surfaces, found {baseline}.");

		var expected = ExpectedPixel(transition, includeLayer);
		var pixelCorrect = true;
		var surfaceLifetimeCorrect = true;
		for (var index = 0; index < profile.WarmupIterations; index++)
		{
			using var output = Compose(provider, a, b, layer, transition, includeLayer);
			var pixels = provider.Readback(output);
			pixelCorrect &= ReadCenterPixel(pixels, format) == expected;
		}
		surfaceLifetimeCorrect &= provider.ActiveSurfaceCount == baseline;

		var samples = new double[profile.SampleIterations];
		for (var index = 0; index < samples.Length; index++)
		{
			var started = Stopwatch.GetTimestamp();
			using (var output = Compose(provider, a, b, layer, transition, includeLayer))
			{
				var pixels = provider.Readback(output);
				pixelCorrect &= ReadCenterPixel(pixels, format) == expected;
			}
			samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			surfaceLifetimeCorrect &= provider.ActiveSurfaceCount == baseline;
		}

		Array.Sort(samples);
		var p50 = Percentile(samples, 0.50);
		var p95 = Percentile(samples, 0.95);
		var maximum = samples[^1];
		var frameBudget = 1000.0 * format.FrameRate.Denominator / format.FrameRate.Numerator;
		var timingBudgetMet = p95 <= frameBudget && maximum <= frameBudget * 2.0;

		return new CudaQualificationCaseResult(
			FormatName(format),
			operation,
			samples.Length,
			frameBudget,
			p50,
			p95,
			maximum,
			pixelCorrect,
			surfaceLifetimeCorrect,
			timingBudgetMet);
	}

	private static GpuFrame Compose(
		GpuProcessingProvider provider,
		GpuFrame a,
		GpuFrame b,
		GpuFrame layer,
		GpuTransition transition,
		bool includeLayer)
	{
		var request = new GpuCompositeRequest(
			OutputSource,
			a,
			b,
			transition,
			includeLayer ? new GpuKeyLayer(layer, 192) : null);
		var result = provider.Composite(request);
		if (!result.Succeeded)
			throw new InvalidOperationException(result.Failure?.Message ?? "CUDA composite qualification case failed.");
		return result.Frame!;
	}

	private static RgbaPixel ExpectedPixel(GpuTransition transition, bool includeLayer)
	{
		var red = Blend(16, 192, transition.BlendWeight);
		var green = Blend(32, 128, transition.BlendWeight);
		var blue = Blend(64, 64, transition.BlendWeight);
		var alpha = byte.MaxValue;
		if (!includeLayer) return new RgbaPixel(red, green, blue, alpha);

		var effectiveAlpha = ScaleAlpha(96, 192);
		return new RgbaPixel(
			AlphaComposite(red, 255, effectiveAlpha),
			AlphaComposite(green, 255, effectiveAlpha),
			AlphaComposite(blue, 255, effectiveAlpha),
			CompositeAlpha(alpha, effectiveAlpha));
	}

	private static RgbaPixel ReadCenterPixel(byte[] pixels, VideoFormat format)
	{
		var width = checked((int)format.Width);
		var height = checked((int)format.Height);
		var offset = checked(((height / 2) * width + width / 2) * 4);
		return new RgbaPixel(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
	}

	private static double Percentile(double[] sorted, double percentile)
	{
		var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
		return sorted[index];
	}

	private static byte Blend(byte a, byte b, byte weight) =>
		(byte)((a * (255 - weight) + b * weight + 127) / 255);

	private static byte ScaleAlpha(byte alpha, byte opacity) =>
		(byte)((alpha * opacity + 127) / 255);

	private static byte AlphaComposite(byte background, byte foreground, byte alpha) =>
		(byte)((background * (255 - alpha) + foreground * alpha + 127) / 255);

	private static byte CompositeAlpha(byte backgroundAlpha, byte foregroundAlpha) =>
		(byte)(foregroundAlpha + (backgroundAlpha * (255 - foregroundAlpha) + 127) / 255);

	private static string FormatName(VideoFormat format) =>
		format == VideoFormat.Hd1080p50Rgba8 ? "1080p50" : "1080p59.94";

	private static CudaQualificationReport CreateReport(
		CudaQualificationProfile profile,
		GpuBackendInfo detected,
		DateTimeOffset captured,
		CudaQualificationStatus status,
		IReadOnlyList<CudaQualificationCaseResult> cases,
		IReadOnlyList<string> failures) =>
		new(
			CudaQualificationReport.CurrentSchemaVersion,
			captured,
			status,
			profile.ExpectedDeviceName,
			profile.DeviceOrdinal,
			detected.DeviceName,
			detected.TotalMemoryBytes,
			cases.ToArray(),
			failures.ToArray());

	private readonly record struct RgbaPixel(byte Red, byte Green, byte Blue, byte Alpha);
}
