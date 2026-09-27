// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Media.Contracts;

public enum ColorPrimaries
{
	Unknown = 0,
	Rec709 = 1
}

public enum ColorTransfer
{
	Unknown = 0,
	Rec709 = 1,
	Srgb = 2,
	Linear = 3
}

public enum ColorMatrix
{
	Unknown = 0,
	IdentityRgb = 1,
	Rec709 = 2
}

public enum NominalRange
{
	Unknown = 0,
	Full = 1,
	Limited = 2
}

public enum AlphaMode
{
	Unknown = 0,
	Opaque = 1,
	Straight = 2,
	Premultiplied = 3
}

public enum ColorMetadataAuthority
{
	Unknown = 0,
	Authoritative = 1,
	Inferred = 2,
	Missing = 3
}

public readonly record struct ColorDescription
{
	public ColorDescription(
		ColorPrimaries primaries,
		ColorTransfer transfer,
		ColorMatrix matrix,
		NominalRange range,
		byte bitDepth,
		AlphaMode alpha,
		ColorMetadataAuthority authority)
	{
		if (!Enum.IsDefined(primaries)) throw new ArgumentOutOfRangeException(nameof(primaries));
		if (!Enum.IsDefined(transfer)) throw new ArgumentOutOfRangeException(nameof(transfer));
		if (!Enum.IsDefined(matrix)) throw new ArgumentOutOfRangeException(nameof(matrix));
		if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range));
		if (!Enum.IsDefined(alpha)) throw new ArgumentOutOfRangeException(nameof(alpha));
		if (!Enum.IsDefined(authority)) throw new ArgumentOutOfRangeException(nameof(authority));
		if (bitDepth is > 0 and < 8) throw new ArgumentOutOfRangeException(nameof(bitDepth), "Known bit depth must be at least 8.");

		Primaries = primaries;
		Transfer = transfer;
		Matrix = matrix;
		Range = range;
		BitDepth = bitDepth;
		Alpha = alpha;
		Authority = authority;
	}

	public ColorPrimaries Primaries { get; }
	public ColorTransfer Transfer { get; }
	public ColorMatrix Matrix { get; }
	public NominalRange Range { get; }
	public byte BitDepth { get; }
	public AlphaMode Alpha { get; }
	public ColorMetadataAuthority Authority { get; }

	public bool IsComplete =>
		Primaries != ColorPrimaries.Unknown &&
		Transfer != ColorTransfer.Unknown &&
		Matrix != ColorMatrix.Unknown &&
		Range != NominalRange.Unknown &&
		BitDepth != 0 &&
		Alpha != AlphaMode.Unknown &&
		Authority is ColorMetadataAuthority.Authoritative or ColorMetadataAuthority.Inferred;

	public string TechnicalLabel =>
		$"{Primaries}/{Transfer}/{Matrix} {Range} {(BitDepth == 0 ? "UNKNOWN" : $"{BitDepth}-BIT")} {Alpha} [{Authority}]";

	public static ColorDescription UnknownRgba8 => new(
		ColorPrimaries.Unknown,
		ColorTransfer.Unknown,
		ColorMatrix.IdentityRgb,
		NominalRange.Unknown,
		8,
		AlphaMode.Straight,
		ColorMetadataAuthority.Missing);

	public static ColorDescription Rec709FullRgba8 => new(
		ColorPrimaries.Rec709,
		ColorTransfer.Rec709,
		ColorMatrix.IdentityRgb,
		NominalRange.Full,
		8,
		AlphaMode.Straight,
		ColorMetadataAuthority.Authoritative);

	public static ColorDescription SrgbFullRgba8 => new(
		ColorPrimaries.Rec709,
		ColorTransfer.Srgb,
		ColorMatrix.IdentityRgb,
		NominalRange.Full,
		8,
		AlphaMode.Straight,
		ColorMetadataAuthority.Authoritative);
}
