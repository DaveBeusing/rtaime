// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Operator;

public enum MediaScopeKind
{
	Histogram,
	Waveform,
	RgbParade,
	Vectorscope
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

	public static MediaScopeSnapshot Analyze(MonitoringFrame frame, int sampleStride = 2)
	{
		ArgumentNullException.ThrowIfNull(frame);
		if (sampleStride <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStride));

		var descriptor = frame.Descriptor;
		var pixels = frame.Pixels.Span;
		var luma = new int[HistogramBins];
		var red = new int[HistogramBins];
		var green = new int[HistogramBins];
		var blue = new int[HistogramBins];
		var waveform = new int[WaveformColumns * WaveformLevels];
		var redWaveform = new int[WaveformColumns * WaveformLevels];
		var greenWaveform = new int[WaveformColumns * WaveformLevels];
		var blueWaveform = new int[WaveformColumns * WaveformLevels];
		var vectorscope = new int[VectorscopeSize * VectorscopeSize];
		var samples = 0;

		for (uint y = 0; y < descriptor.Height; y += (uint)sampleStride)
		{
			for (uint x = 0; x < descriptor.Width; x += (uint)sampleStride)
			{
				var offset = checked((int)(((ulong)y * descriptor.Width + x) * 4UL));
				var r = pixels[offset];
				var g = pixels[offset + 1];
				var b = pixels[offset + 2];
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

				// BT.709-like display-domain chroma projection. This is explicitly a monitoring
				// visualization derived from the already color-transformed RGBA8 monitor sample.
				var cb = ClampByte(((-43 * r - 85 * g + 128 * b + 128) >> 8) + 128);
				var cr = ClampByte(((128 * r - 107 * g - 21 * b + 128) >> 8) + 128);
				var vx = Math.Min(VectorscopeSize - 1, cb * VectorscopeSize / 256);
				var vy = Math.Min(VectorscopeSize - 1, cr * VectorscopeSize / 256);
				vectorscope[vy * VectorscopeSize + vx]++;
				samples++;
			}
		}

		return new MediaScopeSnapshot(
			descriptor.Timing.SequenceNumber,
			descriptor.Width,
			descriptor.Height,
			descriptor.Color,
			sampleStride,
			samples,
			luma, red, green, blue,
			waveform, redWaveform, greenWaveform, blueWaveform,
			vectorscope);
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
		if (a.Color != b.Color)
			return new(false, "Difference requires matching color semantics.");
		return new(true, "Difference inputs are semantically compatible.");
	}
}
