// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Media.Contracts;

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
		new(color, 8, active ?? VideoActiveImageRegion.Full(width, height), "RGBA8 monitoring codes before display conversion");

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
