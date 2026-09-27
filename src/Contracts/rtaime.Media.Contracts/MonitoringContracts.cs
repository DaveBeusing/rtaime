// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Core;

namespace rtaime.Media.Contracts;

public static class MonitoringContractVersion
{
	public static CompatibilityVersion Current { get; } = new(1, 1);

	public static bool IsSupported(CompatibilityVersion version) => version == Current;

	public static void EnsureSupported(CompatibilityVersion version)
	{
		if (!IsSupported(version))
			throw new NotSupportedException($"Unsupported monitoring contract version '{version}'. Supported version is '{Current}'.");
	}
}

public enum MonitoringStreamKind
{
	Source = 1,
	Program = 2
}

public sealed record MonitoringFrameDescriptor
{
	public MonitoringFrameDescriptor(
		CompatibilityVersion version,
		MonitoringStreamKind streamKind,
		MediaSourceId sourceId,
		uint width,
		uint height,
		PixelFormat pixelFormat,
		FrameTiming timing,
		ColorDescription? color = null)
	{
		MonitoringContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MonitoringStreamKind), streamKind))
			throw new ArgumentOutOfRangeException(nameof(streamKind));
		if (width == 0) throw new ArgumentOutOfRangeException(nameof(width));
		if (height == 0) throw new ArgumentOutOfRangeException(nameof(height));
		if (pixelFormat != PixelFormat.Rgba8)
			throw new ArgumentOutOfRangeException(nameof(pixelFormat), "V1 monitoring supports RGBA8 only.");

		Version = version;
		StreamKind = streamKind;
		SourceId = sourceId;
		Width = width;
		Height = height;
		PixelFormat = pixelFormat;
		Timing = timing;
		Color = color ?? ColorDescription.UnknownRgba8;
	}

	public CompatibilityVersion Version { get; }
	public MonitoringStreamKind StreamKind { get; }
	public MediaSourceId SourceId { get; }
	public uint Width { get; }
	public uint Height { get; }
	public PixelFormat PixelFormat { get; }
	public FrameTiming Timing { get; }
	public ColorDescription Color { get; }
	public int RequiredPayloadBytes => checked((int)((ulong)Width * Height * 4UL));
}

public sealed class MonitoringFrame
{
	private readonly byte[] _pixels;

	public MonitoringFrame(MonitoringFrameDescriptor descriptor, ReadOnlySpan<byte> pixels)
	{
		Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		if (pixels.Length != descriptor.RequiredPayloadBytes)
			throw new ArgumentException($"Monitoring frame requires exactly {descriptor.RequiredPayloadBytes} RGBA bytes.", nameof(pixels));
		_pixels = pixels.ToArray();
	}

	public MonitoringFrameDescriptor Descriptor { get; }
	public ReadOnlyMemory<byte> Pixels => _pixels;
}

public static class MonitoringFrameWire
{
	private const uint Magic = 0x314E4F4D; // MON1 in little-endian byte order.
	public const int HeaderSize = 112;

	public static void WriteHeader(Span<byte> destination, MonitoringFrameDescriptor descriptor, int payloadLength)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		if (destination.Length < HeaderSize) throw new ArgumentException($"Monitoring header requires {HeaderSize} bytes.", nameof(destination));
		if (payloadLength != descriptor.RequiredPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payloadLength));

		destination[..HeaderSize].Clear();
		BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Magic);
		BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], descriptor.Version.Major);
		BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], descriptor.Version.Minor);
		BinaryPrimitives.WriteInt32LittleEndian(destination[12..16], (int)descriptor.StreamKind);
		BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], descriptor.Width);
		BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], descriptor.Height);
		BinaryPrimitives.WriteInt32LittleEndian(destination[24..28], (int)descriptor.PixelFormat);
		BinaryPrimitives.WriteUInt64LittleEndian(destination[32..40], descriptor.Timing.SequenceNumber);
		BinaryPrimitives.WriteInt64LittleEndian(destination[40..48], descriptor.Timing.PresentationTimestamp);
		BinaryPrimitives.WriteInt64LittleEndian(destination[48..56], descriptor.Timing.Timebase.Numerator);
		BinaryPrimitives.WriteInt64LittleEndian(destination[56..64], descriptor.Timing.Timebase.Denominator);
		if (!descriptor.SourceId.Value.Value.TryWriteBytes(destination[64..80]))
			throw new InvalidOperationException("Monitoring source identity could not be encoded.");
		BinaryPrimitives.WriteInt32LittleEndian(destination[80..84], payloadLength);
		BinaryPrimitives.WriteInt32LittleEndian(destination[84..88], (int)descriptor.Color.Primaries);
		BinaryPrimitives.WriteInt32LittleEndian(destination[88..92], (int)descriptor.Color.Transfer);
		BinaryPrimitives.WriteInt32LittleEndian(destination[92..96], (int)descriptor.Color.Matrix);
		BinaryPrimitives.WriteInt32LittleEndian(destination[96..100], (int)descriptor.Color.Range);
		destination[100] = descriptor.Color.BitDepth;
		BinaryPrimitives.WriteInt32LittleEndian(destination[104..108], (int)descriptor.Color.Alpha);
		BinaryPrimitives.WriteInt32LittleEndian(destination[108..112], (int)descriptor.Color.Authority);
	}

	public static (MonitoringFrameDescriptor Descriptor, int PayloadLength) ReadHeader(ReadOnlySpan<byte> source)
	{
		if (source.Length < HeaderSize) throw new InvalidDataException($"Monitoring header requires {HeaderSize} bytes.");
		if (BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]) != Magic)
			throw new InvalidDataException("Monitoring frame magic is invalid.");

		var version = new CompatibilityVersion(
			BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]),
			BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]));
		MonitoringContractVersion.EnsureSupported(version);
		var streamKind = (MonitoringStreamKind)BinaryPrimitives.ReadInt32LittleEndian(source[12..16]);
		var width = BinaryPrimitives.ReadUInt32LittleEndian(source[16..20]);
		var height = BinaryPrimitives.ReadUInt32LittleEndian(source[20..24]);
		var pixelFormat = (PixelFormat)BinaryPrimitives.ReadInt32LittleEndian(source[24..28]);
		var timing = new FrameTiming(
			BinaryPrimitives.ReadUInt64LittleEndian(source[32..40]),
			BinaryPrimitives.ReadInt64LittleEndian(source[40..48]),
			new Timebase(
				BinaryPrimitives.ReadInt64LittleEndian(source[48..56]),
				BinaryPrimitives.ReadInt64LittleEndian(source[56..64])));
		var sourceId = new MediaSourceId(new Identity(new Guid(source[64..80])));
		var color = new ColorDescription(
			(ColorPrimaries)BinaryPrimitives.ReadInt32LittleEndian(source[84..88]),
			(ColorTransfer)BinaryPrimitives.ReadInt32LittleEndian(source[88..92]),
			(ColorMatrix)BinaryPrimitives.ReadInt32LittleEndian(source[92..96]),
			(NominalRange)BinaryPrimitives.ReadInt32LittleEndian(source[96..100]),
			source[100],
			(AlphaMode)BinaryPrimitives.ReadInt32LittleEndian(source[104..108]),
			(ColorMetadataAuthority)BinaryPrimitives.ReadInt32LittleEndian(source[108..112]));
		var descriptor = new MonitoringFrameDescriptor(version, streamKind, sourceId, width, height, pixelFormat, timing, color);
		var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[80..84]);
		if (payloadLength != descriptor.RequiredPayloadBytes)
			throw new InvalidDataException("Monitoring frame payload length does not match its descriptor.");
		return (descriptor, payloadLength);
	}
}
