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
			new FrameTiming(42, 84, new Timebase(1, 50)));
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
		Assert.Equal(descriptor.RequiredPayloadBytes, decoded.PayloadLength);
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
}
