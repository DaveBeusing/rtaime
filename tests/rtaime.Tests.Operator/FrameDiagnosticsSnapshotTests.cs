// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class FrameDiagnosticsSnapshotTests
{
	[Fact]
	public void Monitoring_timing_is_projected_without_fabricating_identity()
	{
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Program,
			new MediaSourceId(Identity.Parse("91000000-0000-0000-0000-000000000001")),
			1920, 1080, PixelFormat.Rgba8,
			new FrameTiming(125, 250, new Timebase(1, 50)));

		var snapshot = FrameDiagnosticsSnapshot.FromMonitoring(descriptor);

		Assert.Equal(FrameDiagnosticAuthority.Authoritative, snapshot.TimingAuthority);
		Assert.Equal(125UL, snapshot.FrameIndex);
		Assert.Equal(250L, snapshot.PresentationTimestamp);
		Assert.Equal("5.000000 s", snapshot.PresentationTime);
		Assert.Equal("50 fps", snapshot.Rate);
		Assert.Equal(1920U, snapshot.SourceWidth);
		Assert.Equal(1080U, snapshot.SourceHeight);
	}

	[Fact]
	public void Unavailable_snapshot_keeps_unknown_values_explicit()
	{
		var snapshot = FrameDiagnosticsSnapshot.Unavailable;

		Assert.Equal(FrameDiagnosticAuthority.Unavailable, snapshot.TimingAuthority);
		Assert.Null(snapshot.FrameIndex);
		Assert.Null(snapshot.PresentationTimestamp);
		Assert.Equal("UNAVAILABLE", snapshot.PresentationTime);
		Assert.Equal("UNAVAILABLE", snapshot.Rate);
		Assert.Equal("UNAVAILABLE", snapshot.ColorPath);
	}

	[Fact]
	public void Monitoring_snapshot_does_not_invent_unavailable_health_evidence()
	{
		var snapshot = FrameDiagnosticsSnapshot.Unavailable;

		Assert.Equal(FrameDiagnosticAuthority.Unavailable, snapshot.TimingAuthority);
		Assert.Equal("No authoritative monitoring frame diagnostics are available.", snapshot.Detail);
	}
}
