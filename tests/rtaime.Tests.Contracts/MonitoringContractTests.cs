// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class MonitoringContractTests
{
	[Fact]
	public void Monitoring_wire_header_round_trips_version_stream_source_format_and_timing()
	{
		var sourceId = new MediaSourceId(Identity.Parse("91000000-0000-0000-0000-000000000001"));
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Program,
			sourceId,
			320,
			180,
			PixelFormat.Rgba8,
			new FrameTiming(42, 84, new Timebase(1, 50)),
			ColorDescription.Rec709FullRgba8);
		var header = new byte[MonitoringFrameWire.HeaderSize];

		MonitoringFrameWire.WriteHeader(header, descriptor, descriptor.RequiredPayloadBytes);
		var decoded = MonitoringFrameWire.ReadHeader(header);

		Assert.Equal(descriptor.Version, decoded.Descriptor.Version);
		Assert.Equal(descriptor.StreamKind, decoded.Descriptor.StreamKind);
		Assert.Equal(descriptor.SourceId, decoded.Descriptor.SourceId);
		Assert.Equal(descriptor.Width, decoded.Descriptor.Width);
		Assert.Equal(descriptor.Height, decoded.Descriptor.Height);
		Assert.Equal(descriptor.PixelFormat, decoded.Descriptor.PixelFormat);
		Assert.Equal(descriptor.Timing, decoded.Descriptor.Timing);
		Assert.Equal(descriptor.Color, decoded.Descriptor.Color);
		Assert.Equal(descriptor.RequiredPayloadBytes, decoded.PayloadLength);
	}

	[Fact]
	public void Unknown_color_metadata_remains_explicitly_unknown()
	{
		var format = new VideoFormat(1920, 1080, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
		Assert.Equal(ColorMetadataAuthority.Missing, format.Color.Authority);
		Assert.False(format.Color.IsComplete);
		Assert.Equal(NominalRange.Unknown, format.Color.Range);
	}

	[Fact]
	public void Monitoring_frame_rejects_payload_that_does_not_match_descriptor()
	{
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Source,
			new MediaSourceId(Identity.Parse("91000000-0000-0000-0000-000000000002")),
			2,
			2,
			PixelFormat.Rgba8,
			new FrameTiming(0, 0, new Timebase(1, 50)));

		Assert.Throws<ArgumentException>(() => new MonitoringFrame(descriptor, new byte[15]));
	}
	[Fact]
	public void Shared_gpu_resource_round_trips_without_vendor_specific_handle_data()
	{
		var sourceId = new MediaSourceId(Identity.Parse("91000000-0000-0000-0000-000000000003"));
		var resourceId = new MonitoringResourceId(Identity.Parse("91000000-0000-0000-0000-000000000004"));
		var providerId = Identity.Parse("91000000-0000-0000-0000-000000000005");
		var surfaceId = new SurfaceId(Identity.Parse("91000000-0000-0000-0000-000000000006"));
		var resource = new MonitoringSharedResourceDescriptor(
			resourceId,
			providerId,
			surfaceId,
			VideoFormat.Hd1080p50Rgba8,
			SurfaceStorageDomain.Device,
			MonitoringResourceAccessMode.ReadOnly,
			new SurfaceLifetimeDescriptor(new Generation(7), resourceId.Value));
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Program,
			sourceId,
			320,
			180,
			PixelFormat.Rgba8,
			new FrameTiming(42, 84, new Timebase(1, 50)),
			ColorDescription.Rec709FullRgba8,
			MonitoringSharedResourceCapabilityState.Available,
			resource);
		var header = new byte[MonitoringFrameWire.HeaderSize];

		MonitoringFrameWire.WriteHeader(header, descriptor, payloadLength: 0);
		var decoded = MonitoringFrameWire.ReadHeader(header);

		Assert.Equal(0, decoded.PayloadLength);
		Assert.Equal(MonitoringSharedResourceCapabilityState.Available, decoded.Descriptor.SharedResourceCapability);
		var decodedResource = Assert.IsType<MonitoringSharedResourceDescriptor>(decoded.Descriptor.SharedResource);
		Assert.Equal(resource.ResourceId, decodedResource.ResourceId);
		Assert.Equal(resource.ProviderInstanceId, decodedResource.ProviderInstanceId);
		Assert.Equal(resource.SurfaceId, decodedResource.SurfaceId);
		Assert.Equal(resource.Format, decodedResource.Format);
		Assert.Equal(resource.StorageDomain, decodedResource.StorageDomain);
		Assert.Equal(MonitoringResourceAccessMode.ReadOnly, decodedResource.AccessMode);
		Assert.Equal(new Generation(7), decodedResource.Lifetime.Generation);
		Assert.Equal(resourceId.Value, decodedResource.Lifetime.LeaseId);
	}

	[Fact]
	public void Monitoring_frame_allows_resource_only_program_observation()
	{
		var resourceId = new MonitoringResourceId(Identity.Parse("91000000-0000-0000-0000-000000000007"));
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Program,
			new MediaSourceId(Identity.Parse("91000000-0000-0000-0000-000000000008")),
			320,
			180,
			PixelFormat.Rgba8,
			new FrameTiming(9, 18, new Timebase(1, 50)),
			ColorDescription.Rec709FullRgba8,
			MonitoringSharedResourceCapabilityState.Available,
			new MonitoringSharedResourceDescriptor(
				resourceId,
				Identity.Parse("91000000-0000-0000-0000-000000000009"),
				new SurfaceId(Identity.Parse("91000000-0000-0000-0000-000000000010")),
				VideoFormat.Hd1080p50Rgba8,
				SurfaceStorageDomain.Device,
				MonitoringResourceAccessMode.ReadOnly,
				new SurfaceLifetimeDescriptor(Generation.Initial, resourceId.Value)));

		var frame = new MonitoringFrame(descriptor, ReadOnlySpan<byte>.Empty);

		Assert.False(frame.HasFallbackPayload);
		Assert.True(frame.Descriptor.HasSharedResource);
	}

	[Fact]
	public void Unavailable_shared_resource_capability_cannot_advertise_a_current_resource()
	{
		var resourceId = new MonitoringResourceId(Identity.Parse("91000000-0000-0000-0000-000000000011"));
		var resource = new MonitoringSharedResourceDescriptor(
			resourceId,
			Identity.Parse("91000000-0000-0000-0000-000000000012"),
			new SurfaceId(Identity.Parse("91000000-0000-0000-0000-000000000013")),
			VideoFormat.Hd1080p50Rgba8,
			SurfaceStorageDomain.Device,
			MonitoringResourceAccessMode.ReadOnly,
			new SurfaceLifetimeDescriptor(Generation.Initial, resourceId.Value));

		Assert.Throws<ArgumentException>(() => new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Program,
			new MediaSourceId(Identity.Parse("91000000-0000-0000-0000-000000000014")),
			320,
			180,
			PixelFormat.Rgba8,
			new FrameTiming(0, 0, new Timebase(1, 50)),
			ColorDescription.Rec709FullRgba8,
			MonitoringSharedResourceCapabilityState.Unavailable,
			resource));
	}

}
