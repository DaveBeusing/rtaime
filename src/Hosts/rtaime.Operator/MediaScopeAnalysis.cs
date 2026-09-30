// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public enum MediaScopeKind
{
	Histogram,
	Waveform,
	RgbParade,
	Vectorscope
}

public enum MediaScopeProcessingPath
{
	CpuFallback,
	GpuSharedResource
}

public static class GpuMonitoringAnalysisPolicy
{
	public const int MinimumScopeSampleStride = 2;
	public const int MaxScopeSamples = 262_144;
	public static readonly TimeSpan ScopeUpdateInterval = TimeSpan.FromMilliseconds(200);

	public const int LumaHistogramOffset = 0;
	public const int RedHistogramOffset = LumaHistogramOffset + MediaScopeSnapshot.HistogramBins;
	public const int GreenHistogramOffset = RedHistogramOffset + MediaScopeSnapshot.HistogramBins;
	public const int BlueHistogramOffset = GreenHistogramOffset + MediaScopeSnapshot.HistogramBins;
	public const int WaveformOffset = BlueHistogramOffset + MediaScopeSnapshot.HistogramBins;
	public const int RedWaveformOffset = WaveformOffset + MediaScopeSnapshot.WaveformBinCount;
	public const int GreenWaveformOffset = RedWaveformOffset + MediaScopeSnapshot.WaveformBinCount;
	public const int BlueWaveformOffset = GreenWaveformOffset + MediaScopeSnapshot.WaveformBinCount;
	public const int VectorscopeOffset = BlueWaveformOffset + MediaScopeSnapshot.WaveformBinCount;
	public const int ScopeResultCount = VectorscopeOffset + MediaScopeSnapshot.VectorscopeBinCount;
	public const int ScopeResultBytes = ScopeResultCount * sizeof(uint);

	public static int ResolveScopeSampleStride(uint width, uint height)
	{
		if (width == 0) throw new ArgumentOutOfRangeException(nameof(width));
		if (height == 0) throw new ArgumentOutOfRangeException(nameof(height));

		var area = checked((double)width * height);
		var stride = Math.Max(
			MinimumScopeSampleStride,
			Math.Max(1, (int)Math.Ceiling(Math.Sqrt(area / MaxScopeSamples))));
		while (checked(((long)width + stride - 1) / stride * (((long)height + stride - 1) / stride)) > MaxScopeSamples)
			stride++;
		return stride;
	}
}

public sealed record MediaScopeSnapshot(
	ulong SequenceNumber,
	uint SourceWidth,
	uint SourceHeight,
	ColorDescription Color,
	int SampleStride,
	int SampleCount,
	IReadOnlyList<int> LumaHistogram,
	IReadOnlyList<int> RedHistogram,
	IReadOnlyList<int> GreenHistogram,
	IReadOnlyList<int> BlueHistogram,
	IReadOnlyList<int> Waveform,
	IReadOnlyList<int> RedWaveform,
	IReadOnlyList<int> GreenWaveform,
	IReadOnlyList<int> BlueWaveform,
	IReadOnlyList<int> Vectorscope)
{
	public const int HistogramBins = 256;
	public const int WaveformColumns = 64;
	public const int WaveformLevels = 64;
	public const int VectorscopeSize = 64;
	public const int WaveformBinCount = WaveformColumns * WaveformLevels;
	public const int VectorscopeBinCount = VectorscopeSize * VectorscopeSize;

	public MediaScopeProcessingPath ProcessingPath { get; init; } = MediaScopeProcessingPath.CpuFallback;
	public bool VectorscopeAvailable { get; init; } = true;
	public TimeSpan AnalysisDuration { get; init; }
	public int ResultTransferBytes { get; init; }
	public long ManagedAllocationBytes { get; private set; }
	public string Detail { get; init; } = "CPU fallback analysis";

	public static MediaScopeSnapshot Analyze(MonitoringFrame frame, int sampleStride = 2)
	{
		ArgumentNullException.ThrowIfNull(frame);
		if (sampleStride <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStride));

		var started = Stopwatch.GetTimestamp();
		var allocationStart = GC.GetAllocatedBytesForCurrentThread();
		var descriptor = frame.Descriptor;
		var pixels = frame.Pixels.Span;
		var luma = new int[HistogramBins];
		var red = new int[HistogramBins];
		var green = new int[HistogramBins];
		var blue = new int[HistogramBins];
		var waveform = new int[WaveformBinCount];
		var redWaveform = new int[WaveformBinCount];
		var greenWaveform = new int[WaveformBinCount];
		var blueWaveform = new int[WaveformBinCount];
		var vectorscope = new int[VectorscopeBinCount];
		var vectorscopeAvailable = descriptor.Color.IsComplete;
		var samples = 0;

		for (uint y = 0; y < descriptor.Height; y += (uint)sampleStride)
		{
			for (uint x = 0; x < descriptor.Width; x += (uint)sampleStride)
			{
				var offset = checked((int)(((ulong)y * descriptor.Width + x) * 4UL));
				var r = MonitoringDisplayTransform.TransformCodeValue(pixels[offset], descriptor.Color);
				var g = MonitoringDisplayTransform.TransformCodeValue(pixels[offset + 1], descriptor.Color);
				var b = MonitoringDisplayTransform.TransformCodeValue(pixels[offset + 2], descriptor.Color);
				var y8 = ClampByte((77 * r + 150 * g + 29 * b + 128) >> 8);
				red[r]++;
				green[g]++;
				blue[b]++;
				luma[y8]++;

				var column = Math.Min(WaveformColumns - 1, checked((int)((ulong)x * WaveformColumns / descriptor.Width)));
				IncrementWaveform(waveform, column, y8);
				IncrementWaveform(redWaveform, column, r);
				IncrementWaveform(greenWaveform, column, g);
				IncrementWaveform(blueWaveform, column, b);

				if (vectorscopeAvailable)
				{
					var cb = ClampByte(((-43 * r - 85 * g + 128 * b + 128) >> 8) + 128);
					var cr = ClampByte(((128 * r - 107 * g - 21 * b + 128) >> 8) + 128);
					var vx = Math.Min(VectorscopeSize - 1, cb * VectorscopeSize / 256);
					var vy = Math.Min(VectorscopeSize - 1, cr * VectorscopeSize / 256);
					vectorscope[vy * VectorscopeSize + vx]++;
				}
				samples++;
			}
		}

		var snapshot = new MediaScopeSnapshot(
			descriptor.Timing.SequenceNumber,
			descriptor.Width,
			descriptor.Height,
			descriptor.Color,
			sampleStride,
			samples,
			luma, red, green, blue,
			waveform, redWaveform, greenWaveform, blueWaveform,
			vectorscope)
		{
			ProcessingPath = MediaScopeProcessingPath.CpuFallback,
			VectorscopeAvailable = vectorscopeAvailable,
			AnalysisDuration = Stopwatch.GetElapsedTime(started),
			ResultTransferBytes = 0,
			Detail = vectorscopeAvailable
				? "CPU fallback · display-code scope analysis"
				: "CPU fallback · vectorscope unavailable because color semantics are incomplete"
		};
		snapshot.ManagedAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
		return snapshot;
	}

	public static MediaScopeSnapshot FromGpuResultBuffer(
		ulong sequenceNumber,
		uint sourceWidth,
		uint sourceHeight,
		ColorDescription color,
		int sampleStride,
		int sampleCount,
		ReadOnlySpan<uint> results,
		TimeSpan analysisDuration)
	{
		var allocationStart = GC.GetAllocatedBytesForCurrentThread();
		if (results.Length != GpuMonitoringAnalysisPolicy.ScopeResultCount)
			throw new ArgumentException($"GPU scope result requires exactly {GpuMonitoringAnalysisPolicy.ScopeResultCount} uint values.", nameof(results));
		if (sampleStride <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStride));
		if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

		static int[] Copy(ReadOnlySpan<uint> source, int offset, int length)
		{
			var target = new int[length];
			for (var i = 0; i < length; i++)
				target[i] = checked((int)source[offset + i]);
			return target;
		}

		var vectorscopeAvailable = color.IsComplete;
		var snapshot = new MediaScopeSnapshot(
			sequenceNumber,
			sourceWidth,
			sourceHeight,
			color,
			sampleStride,
			sampleCount,
			Copy(results, GpuMonitoringAnalysisPolicy.LumaHistogramOffset, HistogramBins),
			Copy(results, GpuMonitoringAnalysisPolicy.RedHistogramOffset, HistogramBins),
			Copy(results, GpuMonitoringAnalysisPolicy.GreenHistogramOffset, HistogramBins),
			Copy(results, GpuMonitoringAnalysisPolicy.BlueHistogramOffset, HistogramBins),
			Copy(results, GpuMonitoringAnalysisPolicy.WaveformOffset, WaveformBinCount),
			Copy(results, GpuMonitoringAnalysisPolicy.RedWaveformOffset, WaveformBinCount),
			Copy(results, GpuMonitoringAnalysisPolicy.GreenWaveformOffset, WaveformBinCount),
			Copy(results, GpuMonitoringAnalysisPolicy.BlueWaveformOffset, WaveformBinCount),
			Copy(results, GpuMonitoringAnalysisPolicy.VectorscopeOffset, VectorscopeBinCount))
		{
			ProcessingPath = MediaScopeProcessingPath.GpuSharedResource,
			VectorscopeAvailable = vectorscopeAvailable,
			AnalysisDuration = analysisDuration,
			ResultTransferBytes = GpuMonitoringAnalysisPolicy.ScopeResultBytes,
			Detail = vectorscopeAvailable
				? $"GPU shared resource · {GpuMonitoringAnalysisPolicy.ScopeResultBytes} B result transfer"
				: "GPU shared resource · vectorscope unavailable because color semantics are incomplete"
		};
		snapshot.ManagedAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
		return snapshot;
	}

	private static void IncrementWaveform(int[] bins, int column, byte value)
	{
		var level = Math.Min(WaveformLevels - 1, value * WaveformLevels / 256);
		bins[(WaveformLevels - 1 - level) * WaveformColumns + column]++;
	}

	private static byte ClampByte(int value) => (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
}

public enum MediaCompareMode
{
	Off,
	SplitVertical,
	SplitHorizontal,
	WipeVertical,
	WipeHorizontal,
	Difference
}

public sealed record MediaComparisonCompatibility(bool IsCompatible, string Detail)
{
	public static MediaComparisonCompatibility Evaluate(MonitoringFrameDescriptor? a, MonitoringFrameDescriptor? b)
	{
		if (a is null || b is null)
			return new(false, "A and B frames are both required.");
		if (a.Width != b.Width || a.Height != b.Height)
			return new(false, "Difference requires matching frame dimensions.");
		if (a.PixelFormat != b.PixelFormat)
			return new(false, "Difference requires matching pixel formats.");
		if (!a.Color.IsComplete || !b.Color.IsComplete)
			return new(false, "Difference requires complete color semantics; unknown color metadata is not guessed.");
		if (a.Color != b.Color)
			return new(false, "Difference requires matching color semantics.");
		return new(true, "Difference inputs are semantically compatible.");
	}

	public static MediaComparisonCompatibility Evaluate(OperatorGpuMonitoringFrame? program, OperatorGpuMonitoringFrame? preview)
	{
		if (program is null || preview is null)
			return new(false, "GPU comparison requires Program and confirmed Preview shared resources.");
		if (program.Descriptor.StreamKind != MonitoringStreamKind.Program)
			return new(false, "A must be the Program monitoring resource.");
		if (preview.Descriptor.StreamKind != MonitoringStreamKind.Source)
			return new(false, "B must be the confirmed Preview source monitoring resource.");
		if (program.Resource.AccessMode != MonitoringResourceAccessMode.ReadOnly ||
			preview.Resource.AccessMode != MonitoringResourceAccessMode.ReadOnly)
			return new(false, "GPU comparison requires read-only monitoring resources.");
		if (!program.Resource.Interop.IsPresentable || !preview.Resource.Interop.IsPresentable)
			return new(false, "GPU comparison requires presentable shared resources.");
		if (program.Resource.Format.Width != preview.Resource.Format.Width ||
			program.Resource.Format.Height != preview.Resource.Format.Height)
			return new(false, "GPU comparison requires matching full-resolution resource dimensions.");
		if (program.Resource.Format.PixelFormat != preview.Resource.Format.PixelFormat)
			return new(false, "GPU comparison requires matching shared-resource pixel formats.");
		if (!program.Resource.Format.Color.IsComplete || !preview.Resource.Format.Color.IsComplete)
			return new(false, "GPU comparison requires complete color semantics; unknown color metadata is not guessed.");
		if (program.Resource.Format.Color != preview.Resource.Format.Color)
			return new(false, "GPU comparison requires matching shared-resource color semantics.");
		return new(true, "GPU Program/Preview resources are semantically compatible.");
	}
}

public static class MediaDifference
{
	public static byte[] CreateRgba(MonitoringFrame a, MonitoringFrame b)
	{
		ArgumentNullException.ThrowIfNull(a);
		ArgumentNullException.ThrowIfNull(b);
		var compatibility = MediaComparisonCompatibility.Evaluate(a.Descriptor, b.Descriptor);
		if (!compatibility.IsCompatible)
			throw new InvalidOperationException(compatibility.Detail);

		var left = a.Pixels.Span;
		var right = b.Pixels.Span;
		var output = new byte[left.Length];
		for (var i = 0; i < output.Length; i += 4)
		{
			output[i] = (byte)Math.Abs(left[i] - right[i]);
			output[i + 1] = (byte)Math.Abs(left[i + 1] - right[i + 1]);
			output[i + 2] = (byte)Math.Abs(left[i + 2] - right[i + 2]);
			output[i + 3] = byte.MaxValue;
		}
		return output;
	}

	public static byte[] CreateDisplayRgba(MonitoringFrame a, MonitoringFrame b)
	{
		ArgumentNullException.ThrowIfNull(a);
		ArgumentNullException.ThrowIfNull(b);
		var compatibility = MediaComparisonCompatibility.Evaluate(a.Descriptor, b.Descriptor);
		if (!compatibility.IsCompatible)
			throw new InvalidOperationException(compatibility.Detail);

		var left = a.Pixels.Span;
		var right = b.Pixels.Span;
		var output = new byte[left.Length];
		for (var i = 0; i < output.Length; i += 4)
		{
			var lr = MonitoringDisplayTransform.TransformCodeValue(left[i], a.Descriptor.Color);
			var lg = MonitoringDisplayTransform.TransformCodeValue(left[i + 1], a.Descriptor.Color);
			var lb = MonitoringDisplayTransform.TransformCodeValue(left[i + 2], a.Descriptor.Color);
			var rr = MonitoringDisplayTransform.TransformCodeValue(right[i], b.Descriptor.Color);
			var rg = MonitoringDisplayTransform.TransformCodeValue(right[i + 1], b.Descriptor.Color);
			var rb = MonitoringDisplayTransform.TransformCodeValue(right[i + 2], b.Descriptor.Color);
			output[i] = (byte)Math.Abs(lr - rr);
			output[i + 1] = (byte)Math.Abs(lg - rg);
			output[i + 2] = (byte)Math.Abs(lb - rb);
			output[i + 3] = byte.MaxValue;
		}
		return output;
	}
}
