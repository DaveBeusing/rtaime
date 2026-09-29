// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using Xunit;

namespace rtaime.Operator.Tests;

public sealed class OperatorGpuMonitoringFrameTests
{
	[Fact]
	public void Same_provider_rejects_stale_generation_and_accepts_newer_sequence()
	{
		var provider = Identity.New();
		var current = Create(provider, generation: 7, sequence: 20);
		var staleGeneration = Create(provider, generation: 6, sequence: 99);
		var staleSequence = Create(provider, generation: 7, sequence: 19);
		var newerSequence = Create(provider, generation: 7, sequence: 21);
		var newerGeneration = Create(provider, generation: 8, sequence: 1);

		Assert.False(staleGeneration.IsNewerThan(current));
		Assert.False(staleSequence.IsNewerThan(current));
		Assert.True(newerSequence.IsNewerThan(current));
		Assert.True(newerGeneration.IsNewerThan(current));
	}

	[Fact]
	public void Provider_restart_establishes_a_new_presentation_epoch()
	{
		var beforeRestart = Create(Identity.New(), generation: 40, sequence: 400);
		var afterRestart = Create(Identity.New(), generation: 1, sequence: 1);

		Assert.True(afterRestart.IsNewerThan(beforeRestart));
	}

	private static OperatorGpuMonitoringFrame Create(Identity providerInstanceId, ulong generation, ulong sequence)
	{
		var resourceId = MonitoringResourceId.New();
		var resource = new MonitoringSharedResourceDescriptor(
			resourceId,
			providerInstanceId,
			SurfaceId.New(),
			VideoFormat.Hd1080p50Rgba8,
			SurfaceStorageDomain.Shared,
			MonitoringResourceAccessMode.ReadOnly,
			new SurfaceLifetimeDescriptor(new Generation(generation), resourceId.Value),
			new MonitoringSharedResourceInteropDescriptor(
				MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle,
				adapterLuid: 7,
				sharedHandle: 0x1234));
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			MonitoringStreamKind.Program,
			MediaSourceId.New(),
			320,
			180,
			PixelFormat.Rgba8,
			new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)),
			ColorDescription.SrgbFullRgba8,
			MonitoringSharedResourceCapabilityState.Available,
			resource);
		return new OperatorGpuMonitoringFrame(descriptor);
	}
}
