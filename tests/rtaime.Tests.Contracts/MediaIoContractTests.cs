// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class MediaIoContractTests
{
	[Fact]
	public void Input_frame_requires_shared_opaque_lease()
	{
		var sourceId = new MediaSourceId(Identity.Parse("92000000-0000-0000-0000-000000000001"));
		var portId = new MediaIoPortId(Identity.Parse("92000000-0000-0000-0000-000000000002"));
		var timing = new FrameTiming(7, 7, new Timebase(1, 50));
		var surfaceId = new SurfaceId(Identity.Parse("92000000-0000-0000-0000-000000000003"));
		var leased = new SurfaceDescriptor(
			surfaceId,
			VideoFormat.Hd1080p50Rgba8,
			SurfaceStorageDomain.Shared,
			SurfaceOwnership.SharedLease,
			new SurfaceLifetimeDescriptor(Generation.Initial, Identity.Parse("92000000-0000-0000-0000-000000000004")),
			new OpaqueSurfaceHandle("rtaime.media-io.test", "surface-7"));
		var frame = new FrameDescriptor(MediaContractVersion.Current, sourceId, leased, timing);

		var descriptor = new MediaIoInputFrameDescriptor(MediaIoContractVersion.Current, portId, frame);

		Assert.Equal(portId, descriptor.PortId);
		Assert.Equal(SurfaceOwnership.SharedLease, descriptor.Video.Surface.Ownership);
		Assert.NotNull(descriptor.Video.Surface.Handle);
		Assert.NotNull(descriptor.Video.Surface.Lifetime.LeaseId);

		var producerOwned = new SurfaceDescriptor(
			SurfaceId.New(),
			VideoFormat.Hd1080p50Rgba8,
			SurfaceStorageDomain.Host,
			SurfaceOwnership.ProducerOwned,
			new SurfaceLifetimeDescriptor(Generation.Initial, null),
			new OpaqueSurfaceHandle("rtaime.media-io.test", "producer"));
		var invalid = new FrameDescriptor(MediaContractVersion.Current, sourceId, producerOwned, timing);
		Assert.Throws<ArgumentException>(() => new MediaIoInputFrameDescriptor(MediaIoContractVersion.Current, portId, invalid));
	}

	[Fact]
	public void Media_io_contract_contains_no_bulk_media_payload_property()
	{
		var types = new[]
		{
			typeof(MediaIoNativeVideoFormat),
			typeof(MediaIoInputFrameDescriptor),
			typeof(MediaIoPortStatus),
			typeof(MediaIoPortDescriptor),
			typeof(MediaIoProviderDescriptor)
		};

		foreach (var property in types.SelectMany(type => type.GetProperties()))
		{
			Assert.NotEqual(typeof(byte[]), property.PropertyType);
			Assert.NotEqual(typeof(Memory<byte>), property.PropertyType);
			Assert.NotEqual(typeof(ReadOnlyMemory<byte>), property.PropertyType);
			Assert.NotEqual(typeof(Stream), property.PropertyType);
		}
	}

	[Fact]
	public void Media_io_provider_ports_must_map_to_declared_provider_resources()
	{
		var providerId = new ProviderId(Identity.Parse("92000000-0000-0000-0000-000000000010"));
		var resourceId = new ProviderResourceId(Identity.Parse("92000000-0000-0000-0000-000000000011"));
		var missingResourceId = new ProviderResourceId(Identity.Parse("92000000-0000-0000-0000-000000000012"));
		var generic = new ProviderDescriptor(
			ProviderContractVersion.Current,
			providerId,
			"Media I/O Test Provider",
			new ProviderAvailability(ProviderAvailabilityState.Available),
			new[]
			{
				new ProviderCapabilityDescriptor(CapabilityId.New(), MediaIoCapabilityKinds.VideoInput, new[] { VideoFormat.Hd1080p50Rgba8 })
			},
			new[]
			{
				new ProviderResourceDescriptor(resourceId, providerId, MediaIoCapabilityKinds.VideoInput, 1, true)
			});
		var port = CreatePort(providerId, missingResourceId, MediaIoDirection.Input);

		Assert.Throws<ArgumentException>(() => new MediaIoProviderDescriptor(MediaIoContractVersion.Current, generic, new[] { port }));
	}

	[Fact]
	public void Media_io_port_exposes_native_and_normalized_formats_separately()
	{
		var providerId = ProviderId.New();
		var resourceId = ProviderResourceId.New();
		var port = CreatePort(providerId, resourceId, MediaIoDirection.Input);

		Assert.Contains(VideoFormat.Hd1080p50Rgba8, port.NormalizedVideoFormats);
		Assert.Contains(port.NativeVideoFormats, format => format.PixelFormat == MediaIoNativePixelFormat.V210);
		Assert.Contains(MediaIoTransferMode.SharedOpaqueHandle, port.TransferModes);
	}

	private static MediaIoPortDescriptor CreatePort(ProviderId providerId, ProviderResourceId resourceId, MediaIoDirection direction) =>
		new(
			MediaIoContractVersion.Current,
			providerId,
			resourceId,
			MediaIoPortId.New(),
			"SDI 1",
			direction,
			MediaIoTransportKind.Sdi,
			new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
			new[]
			{
				new MediaIoNativeVideoFormat(1920, 1080, FrameRate.Fps50, MediaIoNativePixelFormat.V210, ScanMode.Progressive),
				new MediaIoNativeVideoFormat(1920, 1080, FrameRate.Fps59_94, MediaIoNativePixelFormat.V210, ScanMode.Progressive)
			},
			new[] { AudioFormat.Stereo48kFloat32 },
			new[] { MediaIoTransferMode.SharedOpaqueHandle, MediaIoTransferMode.DeviceDirectLease },
			supportsExternalReference: true);
}
