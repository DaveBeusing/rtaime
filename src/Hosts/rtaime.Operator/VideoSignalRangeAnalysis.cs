// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Operator;

/// <summary>Image coordinates are in source pixels, before display scaling or ROI zoom.</summary>
public readonly record struct VideoActiveImageRegion(int X, int Y, int Width, int Height)
{
	public long PixelCount => (long)Width * Height;

	public void Validate(uint sourceWidth, uint sourceHeight)
	{
		if (X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
			(long)X + Width > sourceWidth || (long)Y + Height > sourceHeight)
			throw new ArgumentOutOfRangeException(nameof(VideoActiveImageRegion),
				"Active image must be a nonempty rectangle inside the source raster.");
	}

	public static VideoActiveImageRegion Full(uint width, uint height) =>
		new(0, 0, checked((int)width), checked((int)height));
}

public readonly record struct VideoRangeLimits(int NominalMin, int NominalMax, int PreferredMin, int PreferredMax, int TotalMin, int TotalMax)
{
	// EBU R 103 v3.0 (May 2020), Annex 1, Table 1. Inclusive digital code values.
	public static bool TryGet(int bitDepth, out VideoRangeLimits limits)
	{
		limits = bitDepth switch
		{
			8 => new(16, 235, 5, 246, 1, 254),
			10 => new(64, 940, 20, 984, 4, 1019),
			_ => default
		};
		return bitDepth is 8 or 10;
	}
}

public enum VideoRangeSampleStatus
{
	Unavailable,
	Unverified,
	SampledWithinNominal,
	SampledOutsideNominal,
	SampledOutOfGamut,
	SampledOutsideTotal
}

public readonly record struct VideoRangeProfile(
	ColorDescription Color,
	int ObservedCodeBitDepth,
	VideoActiveImageRegion ActiveImage,
	string PixelEvidence)
{
	public static VideoRangeProfile ForRgba8(
		ColorDescription color, uint width, uint height, VideoActiveImageRegion? active = null) =>
		new(color, 8, active ?? VideoActiveImageRegion.Full(width, height), "RGBA8 source-code monitoring sample");

	public static VideoRangeProfile ForNativeRgba10(
		ColorDescription color, uint width, uint height, VideoActiveImageRegion? active = null) =>
		new(color, 10, active ?? VideoActiveImageRegion.Full(width, height), "Native 10-bit code-vector input");

	public string? UnverifiedReason
	{
		get
		{
			if (!Color.IsComplete || Color.Authority != ColorMetadataAuthority.Authoritative)
				return "UNVERIFIED: authoritative, complete primaries/transfer/matrix/range metadata is required.";
			if (Color.Range == NominalRange.Full)
				return "UNVERIFIED: full-range file code values cannot be treated as R 103 limited-range broadcast levels.";
			if (Color.Range != NominalRange.Limited)
				return "UNVERIFIED: nominal code range is unknown.";
			if (Color.BitDepth != ObservedCodeBitDepth)
				return $"UNAVAILABLE: {Color.BitDepth}-bit source precision is not preserved by the {ObservedCodeBitDepth}-bit monitoring surface.";
			if (!VideoRangeLimits.TryGet(ObservedCodeBitDepth, out _))
				return "UNAVAILABLE: R 103 code thresholds are implemented for 8-bit and 10-bit native precision only.";
			if (Color.Primaries != ColorPrimaries.Rec709 || Color.Transfer != ColorTransfer.Rec709 ||
				Color.Matrix != ColorMatrix.IdentityRgb)
				return "UNVERIFIED: this RGB code analyzer does not substitute an unknown/unsupported matrix, primaries or HDR transfer.";
			return null;
		}
	}
}

public readonly record struct VideoRangeChannelCounts(int OutsideNominal, int OutsidePreferred, int OutsideTotal)
{
	public double NominalPercent(int samples) => Ratio(OutsideNominal, samples);
	public double PreferredPercent(int samples) => Ratio(OutsidePreferred, samples);
	public double TotalPercent(int samples) => Ratio(OutsideTotal, samples);

	private static double Ratio(int count, int samples) => samples > 0 ? 100.0 * count / samples : 0;
}

public sealed record VideoSignalRangeSnapshot(
	VideoRangeProfile Profile,
	ulong FrameSequence,
	int SampleStride,
	int SampleCount,
	VideoRangeSampleStatus Status,
	VideoRangeChannelCounts Red,
	VideoRangeChannelCounts Green,
	VideoRangeChannelCounts Blue,
	VideoRangeChannelCounts Luma,
	VideoRangeChannelCounts AnyComponent,
	string Detail)
{
	public double CoveragePercent => Profile.ActiveImage.PixelCount > 0
		? 100.0 * SampleCount / Profile.ActiveImage.PixelCount : 0;
	public bool PreferredAreaThresholdExceeded =>
		SampleCount > 0 && (long)AnyComponent.OutsidePreferred * 100 > SampleCount;
	public string ChromaStatus => "UNAVAILABLE: native Cb/Cr code values are not present in RGB monitoring; no chroma legal-level claim.";
	public string TechnicalLabel =>
		$"EBU R 103 v3 · {Profile.Color.TechnicalLabel} · {Profile.PixelEvidence} · " +
		$"active {Profile.ActiveImage.Width}x{Profile.ActiveImage.Height} @ {Profile.ActiveImage.X},{Profile.ActiveImage.Y} · frame {FrameSequence}";

	public string DiagnosticLabel
	{
		get
		{
			if (Status is VideoRangeSampleStatus.Unverified or VideoRangeSampleStatus.Unavailable)
				return $"{Status.ToString().ToUpperInvariant()} · {Detail}";
			return $"{Status.ToString().ToUpperInvariant()} · {SampleCount:N0} samples ({CoveragePercent:0.###}% active image) · " +
				$"outside preferred R {Red.PreferredPercent(SampleCount):0.##}% / G {Green.PreferredPercent(SampleCount):0.##}% / " +
				$"B {Blue.PreferredPercent(SampleCount):0.##}% / Y′ {Luma.PreferredPercent(SampleCount):0.##}% / " +
				$"ANY {AnyComponent.PreferredPercent(SampleCount):0.##}% · " +
				$"outside nominal {AnyComponent.OutsideNominal:N0}, preferred {AnyComponent.OutsidePreferred:N0}, " +
				$"total {AnyComponent.OutsideTotal:N0} · sampled UNFILTERED diagnostic, no compliance PASS";
		}
	}

	public static VideoSignalRangeSnapshot Unavailable(VideoRangeProfile profile, ulong sequence, int stride, string reason) =>
		new(profile, sequence, stride, 0, VideoRangeSampleStatus.Unavailable,
			default, default, default, default, default, reason);

	internal static VideoSignalRangeSnapshot Evaluate(
		VideoRangeProfile profile, ulong sequence, int stride, int samples, ReadOnlySpan<uint> counters)
	{
		var reason = profile.UnverifiedReason;
		if (reason is not null)
			return new(profile, sequence, stride, 0,
				reason.StartsWith("UNAVAILABLE", StringComparison.Ordinal)
					? VideoRangeSampleStatus.Unavailable : VideoRangeSampleStatus.Unverified,
				default, default, default, default, default, reason);

		if (counters.Length != VideoSignalRangeAccumulator.CounterCount || samples <= 0)
			return Unavailable(profile, sequence, stride, "UNAVAILABLE: a matching raw-code sample/count buffer is required.");

		static VideoRangeChannelCounts Channel(ReadOnlySpan<uint> values, int offset) => new(
			checked((int)values[offset]), checked((int)values[offset + 1]),
			checked((int)values[offset + 2]));

		var union = Channel(counters, 12);
		var status = union.OutsideTotal > 0 ? VideoRangeSampleStatus.SampledOutsideTotal :
			(long)union.OutsidePreferred * 100 > samples ? VideoRangeSampleStatus.SampledOutOfGamut :
			union.OutsideNominal > 0 ? VideoRangeSampleStatus.SampledOutsideNominal :
			VideoRangeSampleStatus.SampledWithinNominal;
		return new(profile, sequence, stride, samples, status,
			Channel(counters, 0), Channel(counters, 3), Channel(counters, 6), Channel(counters, 9), union,
			"Measured from source code samples before display transform; 1% threshold applies only provisionally to measured active-image samples.");
	}
}

/// <summary>
/// Counts excursions on raw R'G'B' and Rec.709-derived Y' code samples.
/// Uses the exact same grid as existing scopes. Does not alter pixel buffers,
/// rescale studio range, apply a legalizer or assert spatially-filtered compliance.
/// </summary>
internal sealed class VideoSignalRangeAccumulator
{
	public const int CounterCount = 15; // R,G,B,Y,ANY x nominal,preferred,total
	private readonly VideoRangeProfile _profile;
	private readonly VideoRangeLimits _limits;
	private readonly bool _enabled;
	private readonly uint[] _counts = new uint[CounterCount];
	private int _samples;

	public VideoSignalRangeAccumulator(VideoRangeProfile profile)
	{
		_profile = profile;
		_enabled = profile.UnverifiedReason is null;
		VideoRangeLimits.TryGet(profile.ObservedCodeBitDepth, out _limits);
	}

	public void Add(int x, int y, int r, int g, int b)
	{
		if (!_enabled ||
			x < _profile.ActiveImage.X || y < _profile.ActiveImage.Y ||
			x >= (long)_profile.ActiveImage.X + _profile.ActiveImage.Width ||
			y >= (long)_profile.ActiveImage.Y + _profile.ActiveImage.Height)
			return;

		// All values use the original limited-range code scale. Ignore Alpha.
		// 0.2126 R' + 0.7152 G' + 0.0722 B' (BT.709 luma coefficients).
		var luma = (2126 * r + 7152 * g + 722 * b + 5000) / 10000;
		bool n = false, p = false, t = false;
		AddChannel(0, r, ref n, ref p, ref t);
		AddChannel(3, g, ref n, ref p, ref t);
		AddChannel(6, b, ref n, ref p, ref t);
		AddChannel(9, luma, ref n, ref p, ref t);
		if (n) _counts[12]++;
		if (p) _counts[13]++;
		if (t) _counts[14]++;
		_samples++;
	}

	private void AddChannel(int baseIndex, int code, ref bool anyNominal, ref bool anyPreferred, ref bool anyTotal)
	{
		if (code < _limits.NominalMin || code > _limits.NominalMax)
		{
			_counts[baseIndex]++;
			anyNominal = true;
		}
		if (code < _limits.PreferredMin || code > _limits.PreferredMax)
		{
			_counts[baseIndex + 1]++;
			anyPreferred = true;
		}
		if (code < _limits.TotalMin || code > _limits.TotalMax)
		{
			_counts[baseIndex + 2]++;
			anyTotal = true;
		}
	}

	public VideoSignalRangeSnapshot Finish(ulong frameSequence, int stride) =>
		VideoSignalRangeSnapshot.Evaluate(_profile, frameSequence, stride, _samples, _counts);
}

public static class VideoSignalRangeAnalysis
{
	/// <summary>Analyzes already-materialized RGBA8 monitoring codes, not display-transformed BGRA.</summary>
	public static VideoSignalRangeSnapshot AnalyzeRgba8(
		MonitoringFrame frame,
		VideoActiveImageRegion? activeImage = null,
		int sampleStride = 1)
	{
		ArgumentNullException.ThrowIfNull(frame);
		if (sampleStride <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStride));
		if (!frame.HasFallbackPayload)
			return VideoSignalRangeSnapshot.Unavailable(
				VideoRangeProfile.ForRgba8(frame.Descriptor.Color, frame.Descriptor.Width, frame.Descriptor.Height),
				frame.Descriptor.Timing.SequenceNumber, sampleStride,
				"UNAVAILABLE: a source-code monitoring payload is not materialized.");
		var descriptor = frame.Descriptor;
		var region = activeImage ?? VideoActiveImageRegion.Full(descriptor.Width, descriptor.Height);
		region.Validate(descriptor.Width, descriptor.Height);
		var profile = VideoRangeProfile.ForRgba8(descriptor.Color, descriptor.Width, descriptor.Height, region);
		var accumulator = new VideoSignalRangeAccumulator(profile);
		var pixels = frame.Pixels.Span;
		for (var y = region.Y; y < (long)region.Y + region.Height; y += sampleStride)
		for (var x = region.X; x < (long)region.X + region.Width; x += sampleStride)
		{
			var offset = checked((int)(((ulong)y * descriptor.Width + (ulong)x) * 4));
			accumulator.Add(x, y, pixels[offset], pixels[offset + 1], pixels[offset + 2]);
		}
		return accumulator.Finish(descriptor.Timing.SequenceNumber, sampleStride);
	}

	/// <summary>Standalone deterministic native-code reference path, not a Runtime/SDI qualified provider.</summary>
	public static VideoSignalRangeSnapshot AnalyzeNativeRgba10(
		ReadOnlySpan<ushort> rgbaCodes,
		uint width,
		uint height,
		ulong frameSequence,
		ColorDescription color,
		VideoActiveImageRegion? activeImage = null,
		int sampleStride = 1)
	{
		if (sampleStride <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStride));
		var active = activeImage ?? VideoActiveImageRegion.Full(width, height);
		active.Validate(width, height);
		if (rgbaCodes.Length != checked((int)((ulong)width * height * 4)))
			throw new ArgumentException("Native 10-bit RGBA code vector must contain four samples per pixel.", nameof(rgbaCodes));
		var profile = VideoRangeProfile.ForNativeRgba10(color, width, height, active);
		var accumulator = new VideoSignalRangeAccumulator(profile);
		for (var y = active.Y; y < (long)active.Y + active.Height; y += sampleStride)
		for (var x = active.X; x < (long)active.X + active.Width; x += sampleStride)
		{
			var offset = checked((int)(((ulong)y * width + (ulong)x) * 4));
			accumulator.Add(x, y, rgbaCodes[offset], rgbaCodes[offset + 1], rgbaCodes[offset + 2]);
		}
		return accumulator.Finish(frameSequence, sampleStride);
	}
}
