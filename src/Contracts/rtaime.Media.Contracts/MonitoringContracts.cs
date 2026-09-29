// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Core;

namespace rtaime.Media.Contracts;

public static class MonitoringContractVersion
{
	public static CompatibilityVersion Current { get; } = new(1, 3);

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

public enum MonitoringSharedResourceCapabilityState
{
	Unavailable = 1,
	Available = 2,
	Degraded = 3
}

public enum MonitoringResourceAccessMode
{
	ReadOnly = 1
}

public enum MonitoringSharedResourceInteropKind
{
	None = 0,
	WindowsGraphicsSharedHandle = 1
}

public readonly record struct MonitoringSharedResourceInteropDescriptor
{
	public MonitoringSharedResourceInteropDescriptor(
		MonitoringSharedResourceInteropKind kind,
		long adapterLuid,
		ulong sharedHandle)
	{
		if (!Enum.IsDefined(typeof(MonitoringSharedResourceInteropKind), kind))
			throw new ArgumentOutOfRangeException(nameof(kind));
		if (kind == MonitoringSharedResourceInteropKind.None && (adapterLuid != 0 || sharedHandle != 0))
			throw new ArgumentException("Unavailable monitoring interop cannot carry adapter or handle data.");
		if (kind == MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle && sharedHandle == 0)
			throw new ArgumentOutOfRangeException(nameof(sharedHandle), "Windows graphics sharing requires a non-zero shared handle.");

		Kind = kind;
		AdapterLuid = adapterLuid;
		SharedHandle = sharedHandle;
	}

	public MonitoringSharedResourceInteropKind Kind { get; }
	public long AdapterLuid { get; }
	public ulong SharedHandle { get; }
	public bool IsPresentable => Kind != MonitoringSharedResourceInteropKind.None;
}

public readonly record struct MonitoringResourceId
{
	public MonitoringResourceId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Monitoring resource identity must not be empty.", nameof(value));
		Value = value;
	}

	public Identity Value { get; }
	public static MonitoringResourceId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public sealed record MonitoringSharedResourceDescriptor
{
	public MonitoringSharedResourceDescriptor(
		MonitoringResourceId resourceId,
		Identity providerInstanceId,
		SurfaceId surfaceId,
		VideoFormat format,
		SurfaceStorageDomain storageDomain,
		MonitoringResourceAccessMode accessMode,
		SurfaceLifetimeDescriptor lifetime,
		MonitoringSharedResourceInteropDescriptor interop = default)
	{
		if (providerInstanceId.IsEmpty)
			throw new ArgumentException("Monitoring resource provider identity must not be empty.", nameof(providerInstanceId));
		if (storageDomain is not (SurfaceStorageDomain.Device or SurfaceStorageDomain.Shared))
			throw new ArgumentOutOfRangeException(nameof(storageDomain), "Shared GPU monitoring resources must remain device or shared resident.");
		if (accessMode != MonitoringResourceAccessMode.ReadOnly)
			throw new ArgumentOutOfRangeException(nameof(accessMode), "Monitoring resources are read-only.");
		if (lifetime.LeaseId is null || lifetime.LeaseId.Value.IsEmpty)
			throw new ArgumentException("Monitoring resources require an explicit lease identity.", nameof(lifetime));

		ResourceId = resourceId;
		ProviderInstanceId = providerInstanceId;
		SurfaceId = surfaceId;
		Format = format;
		StorageDomain = storageDomain;
		AccessMode = accessMode;
		Lifetime = lifetime;
		Interop = interop;
	}

	public MonitoringResourceId ResourceId { get; }
	public Identity ProviderInstanceId { get; }
	public SurfaceId SurfaceId { get; }
	public VideoFormat Format { get; }
	public SurfaceStorageDomain StorageDomain { get; }
	public MonitoringResourceAccessMode AccessMode { get; }
	public SurfaceLifetimeDescriptor Lifetime { get; }
	public MonitoringSharedResourceInteropDescriptor Interop { get; }
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
		ColorDescription? color = null,
		MonitoringSharedResourceCapabilityState sharedResourceCapability = MonitoringSharedResourceCapabilityState.Unavailable,
		MonitoringSharedResourceDescriptor? sharedResource = null)
	{
		MonitoringContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MonitoringStreamKind), streamKind))
			throw new ArgumentOutOfRangeException(nameof(streamKind));
		if (width == 0) throw new ArgumentOutOfRangeException(nameof(width));
		if (height == 0) throw new ArgumentOutOfRangeException(nameof(height));
		if (pixelFormat != PixelFormat.Rgba8)
			throw new ArgumentOutOfRangeException(nameof(pixelFormat), "V1 monitoring supports RGBA8 only.");
		if (!Enum.IsDefined(typeof(MonitoringSharedResourceCapabilityState), sharedResourceCapability))
			throw new ArgumentOutOfRangeException(nameof(sharedResourceCapability));
		if (sharedResource is not null && sharedResourceCapability == MonitoringSharedResourceCapabilityState.Unavailable)
			throw new ArgumentException("An unavailable shared-resource capability cannot carry a current resource.", nameof(sharedResource));

		Version = version;
		StreamKind = streamKind;
		SourceId = sourceId;
		Width = width;
		Height = height;
		PixelFormat = pixelFormat;
		Timing = timing;
		Color = color ?? ColorDescription.UnknownRgba8;
		SharedResourceCapability = sharedResourceCapability;
		SharedResource = sharedResource;
	}

	public CompatibilityVersion Version { get; }
	public MonitoringStreamKind StreamKind { get; }
	public MediaSourceId SourceId { get; }
	public uint Width { get; }
	public uint Height { get; }
	public PixelFormat PixelFormat { get; }
	public FrameTiming Timing { get; }
	public ColorDescription Color { get; }
	public MonitoringSharedResourceCapabilityState SharedResourceCapability { get; }
	public MonitoringSharedResourceDescriptor? SharedResource { get; }
	public bool HasSharedResource => SharedResource is not null;
	public int RequiredPayloadBytes => checked((int)((ulong)Width * Height * 4UL));
}

public sealed class MonitoringFrame
{
	private readonly byte[] _pixels;

	public MonitoringFrame(MonitoringFrameDescriptor descriptor, ReadOnlySpan<byte> pixels)
	{
		Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		if (pixels.Length != descriptor.RequiredPayloadBytes && !(pixels.IsEmpty && descriptor.HasSharedResource))
			throw new ArgumentException($"Monitoring frame requires exactly {descriptor.RequiredPayloadBytes} RGBA bytes unless a shared GPU resource is present.", nameof(pixels));
		_pixels = pixels.ToArray();
	}

	public MonitoringFrameDescriptor Descriptor { get; }
	public ReadOnlyMemory<byte> Pixels => _pixels;
	public bool HasFallbackPayload => _pixels.Length != 0;
}

public static class MonitoringFrameWire
{
	private const uint Magic = 0x314E4F4D; // MON1 in little-endian byte order.
	public const int HeaderSize = 272;

	public static void WriteHeader(Span<byte> destination, MonitoringFrameDescriptor descriptor, int payloadLength)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		if (destination.Length < HeaderSize) throw new ArgumentException($"Monitoring header requires {HeaderSize} bytes.", nameof(destination));
		if (payloadLength != descriptor.RequiredPayloadBytes && !(payloadLength == 0 && descriptor.HasSharedResource))
			throw new ArgumentOutOfRangeException(nameof(payloadLength));

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
		BinaryPrimitives.WriteInt32LittleEndian(destination[112..116], (int)descriptor.SharedResourceCapability);
		if (descriptor.SharedResource is { } resource)
		{
			BinaryPrimitives.WriteInt32LittleEndian(destination[116..120], 1);
			if (!resource.ResourceId.Value.Value.TryWriteBytes(destination[120..136]))
				throw new InvalidOperationException("Monitoring resource identity could not be encoded.");
			if (!resource.ProviderInstanceId.Value.TryWriteBytes(destination[136..152]))
				throw new InvalidOperationException("Monitoring provider identity could not be encoded.");
			if (!resource.SurfaceId.Value.Value.TryWriteBytes(destination[152..168]))
				throw new InvalidOperationException("Monitoring surface identity could not be encoded.");
			BinaryPrimitives.WriteUInt32LittleEndian(destination[168..172], resource.Format.Width);
			BinaryPrimitives.WriteUInt32LittleEndian(destination[172..176], resource.Format.Height);
			BinaryPrimitives.WriteInt64LittleEndian(destination[176..184], resource.Format.FrameRate.Numerator);
			BinaryPrimitives.WriteInt64LittleEndian(destination[184..192], resource.Format.FrameRate.Denominator);
			BinaryPrimitives.WriteInt32LittleEndian(destination[192..196], (int)resource.Format.PixelFormat);
			BinaryPrimitives.WriteInt32LittleEndian(destination[196..200], (int)resource.Format.ScanMode);
			BinaryPrimitives.WriteInt32LittleEndian(destination[200..204], (int)resource.StorageDomain);
			BinaryPrimitives.WriteInt32LittleEndian(destination[204..208], (int)resource.AccessMode);
			BinaryPrimitives.WriteUInt64LittleEndian(destination[208..216], resource.Lifetime.Generation.Value);
			BinaryPrimitives.WriteInt32LittleEndian(destination[216..220], (int)resource.Format.Color.Primaries);
			BinaryPrimitives.WriteInt32LittleEndian(destination[220..224], (int)resource.Format.Color.Transfer);
			BinaryPrimitives.WriteInt32LittleEndian(destination[224..228], (int)resource.Format.Color.Matrix);
			BinaryPrimitives.WriteInt32LittleEndian(destination[228..232], (int)resource.Format.Color.Range);
			destination[232] = resource.Format.Color.BitDepth;
			BinaryPrimitives.WriteInt32LittleEndian(destination[236..240], (int)resource.Format.Color.Alpha);
			BinaryPrimitives.WriteInt32LittleEndian(destination[240..244], (int)resource.Format.Color.Authority);
			BinaryPrimitives.WriteInt32LittleEndian(destination[244..248], (int)resource.Interop.Kind);
			BinaryPrimitives.WriteInt64LittleEndian(destination[248..256], resource.Interop.AdapterLuid);
			BinaryPrimitives.WriteUInt64LittleEndian(destination[256..264], resource.Interop.SharedHandle);
		}
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
		var sharedResourceCapability = (MonitoringSharedResourceCapabilityState)BinaryPrimitives.ReadInt32LittleEndian(source[112..116]);
		if (!Enum.IsDefined(typeof(MonitoringSharedResourceCapabilityState), sharedResourceCapability))
			throw new InvalidDataException("Monitoring shared-resource capability state is invalid.");

		MonitoringSharedResourceDescriptor? sharedResource = null;
		var resourcePresent = BinaryPrimitives.ReadInt32LittleEndian(source[116..120]);
		if (resourcePresent is not (0 or 1))
			throw new InvalidDataException("Monitoring shared-resource presence flag is invalid.");
		if (resourcePresent == 1)
		{
			var resourceId = new MonitoringResourceId(new Identity(new Guid(source[120..136])));
			var providerInstanceId = new Identity(new Guid(source[136..152]));
			var surfaceId = new SurfaceId(new Identity(new Guid(source[152..168])));
			var resourceColor = new ColorDescription(
				(ColorPrimaries)BinaryPrimitives.ReadInt32LittleEndian(source[216..220]),
				(ColorTransfer)BinaryPrimitives.ReadInt32LittleEndian(source[220..224]),
				(ColorMatrix)BinaryPrimitives.ReadInt32LittleEndian(source[224..228]),
				(NominalRange)BinaryPrimitives.ReadInt32LittleEndian(source[228..232]),
				source[232],
				(AlphaMode)BinaryPrimitives.ReadInt32LittleEndian(source[236..240]),
				(ColorMetadataAuthority)BinaryPrimitives.ReadInt32LittleEndian(source[240..244]));
			var resourceFormat = new VideoFormat(
				BinaryPrimitives.ReadUInt32LittleEndian(source[168..172]),
				BinaryPrimitives.ReadUInt32LittleEndian(source[172..176]),
				new FrameRate(
					BinaryPrimitives.ReadInt64LittleEndian(source[176..184]),
					BinaryPrimitives.ReadInt64LittleEndian(source[184..192])),
				(PixelFormat)BinaryPrimitives.ReadInt32LittleEndian(source[192..196]),
				(ScanMode)BinaryPrimitives.ReadInt32LittleEndian(source[196..200]),
				resourceColor);
			var lifetime = new SurfaceLifetimeDescriptor(
				new Generation(BinaryPrimitives.ReadUInt64LittleEndian(source[208..216])),
				resourceId.Value);
			var interopKind = (MonitoringSharedResourceInteropKind)BinaryPrimitives.ReadInt32LittleEndian(source[244..248]);
			if (!Enum.IsDefined(typeof(MonitoringSharedResourceInteropKind), interopKind))
				throw new InvalidDataException("Monitoring shared-resource interop kind is invalid.");
			var interop = new MonitoringSharedResourceInteropDescriptor(
				interopKind,
				BinaryPrimitives.ReadInt64LittleEndian(source[248..256]),
				BinaryPrimitives.ReadUInt64LittleEndian(source[256..264]));
			sharedResource = new MonitoringSharedResourceDescriptor(
				resourceId,
				providerInstanceId,
				surfaceId,
				resourceFormat,
				(SurfaceStorageDomain)BinaryPrimitives.ReadInt32LittleEndian(source[200..204]),
				(MonitoringResourceAccessMode)BinaryPrimitives.ReadInt32LittleEndian(source[204..208]),
				lifetime,
				interop);
		}

		var descriptor = new MonitoringFrameDescriptor(
			version,
			streamKind,
			sourceId,
			width,
			height,
			pixelFormat,
			timing,
			color,
			sharedResourceCapability,
			sharedResource);
		var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[80..84]);
		if (payloadLength != descriptor.RequiredPayloadBytes && !(payloadLength == 0 && descriptor.HasSharedResource))
			throw new InvalidDataException("Monitoring frame payload length does not match its descriptor.");
		return (descriptor, payloadLength);
	}
}
