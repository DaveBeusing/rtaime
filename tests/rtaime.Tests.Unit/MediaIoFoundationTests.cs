// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Tests.Unit;

public sealed class MediaIoFoundationTests
{
	[Fact]
	public void Admission_accepts_declared_format_transfer_audio_and_reference()
	{
		var profile = CreateProfile();
		var port = profile.Ports.Single(candidate => candidate.Direction == MediaIoDirection.Input);
		var request = new MediaIoSessionRequest(
			MediaIoContractVersion.Current,
			profile.Provider.ProviderId,
			port.PortId,
			MediaIoDirection.Input,
			VideoFormat.Hd1080p50Rgba8,
			MediaIoTransferMode.SharedOpaqueHandle,
			AudioFormat.Stereo48kFloat32,
			requireExternalReference: true);

		var result = MediaIoAdmission.Validate(profile, request);

		Assert.True(result.IsValid, string.Join(" | ", result.Issues.Select(issue => issue.Code)));
	}

	[Fact]
	public void Admission_fails_closed_for_direction_format_transfer_and_reference_mismatch()
	{
		var profile = CreateProfile();
		var input = profile.Ports.Single(candidate => candidate.Direction == MediaIoDirection.Input);
		var unsupportedFormat = new VideoFormat(1280, 720, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
		var request = new MediaIoSessionRequest(
			MediaIoContractVersion.Current,
			profile.Provider.ProviderId,
			input.PortId,
			MediaIoDirection.Output,
			unsupportedFormat,
			MediaIoTransferMode.PinnedHostLease,
			AudioFormat.Stereo48kFloat32,
			requireExternalReference: true);

		var result = MediaIoAdmission.Validate(profile, request);
		var codes = result.Issues.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);

		Assert.False(result.IsValid);
		Assert.Contains("media.io.direction_mismatch", codes);
		Assert.Contains("media.io.video_format_unsupported", codes);
		Assert.Contains("media.io.transfer_mode_unsupported", codes);
		Assert.Contains("media.io.external_reference_unsupported", codes);
		Assert.Contains("media.io.capability_missing", codes);
	}

	[Fact]
	public void Input_frame_lease_releases_exactly_once()
	{
		var port = MediaIoPortId.New();
		var source = MediaSourceId.New();
		var surface = new SurfaceDescriptor(
			SurfaceId.New(),
			VideoFormat.Hd1080p50Rgba8,
			SurfaceStorageDomain.Shared,
			SurfaceOwnership.SharedLease,
			new SurfaceLifetimeDescriptor(Generation.Initial, Identity.New()),
			new OpaqueSurfaceHandle("rtaime.media-io.test", "lease"));
		var frame = new FrameDescriptor(
			MediaContractVersion.Current,
			source,
			surface,
			new FrameTiming(1, 1, new Timebase(1, 50)));
		var descriptor = new MediaIoInputFrameDescriptor(MediaIoContractVersion.Current, port, frame);
		var releases = 0;
		var lease = new MediaIoInputFrameLease(descriptor, _ => releases++);

		lease.Dispose();
		lease.Dispose();

		Assert.True(lease.IsDisposed);
		Assert.Equal(1, releases);
	}

	private static MediaIoProviderDescriptor CreateProfile()
	{
		var providerId = new ProviderId(Identity.Parse("93000000-0000-0000-0000-000000000001"));
		var inputResource = new ProviderResourceId(Identity.Parse("93000000-0000-0000-0000-000000000002"));
		var outputResource = new ProviderResourceId(Identity.Parse("93000000-0000-0000-0000-000000000003"));
		var formats = new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 };
		var provider = new ProviderDescriptor(
			ProviderContractVersion.Current,
			providerId,
			"Media I/O Test Provider",
			new ProviderAvailability(ProviderAvailabilityState.Available),
			new[]
			{
				new ProviderCapabilityDescriptor(CapabilityId.New(), MediaIoCapabilityKinds.VideoInput, formats),
				new ProviderCapabilityDescriptor(CapabilityId.New(), MediaIoCapabilityKinds.VideoOutput, formats)
			},
			new[]
			{
				new ProviderResourceDescriptor(inputResource, providerId, MediaIoCapabilityKinds.VideoInput, 1, true),
				new ProviderResourceDescriptor(outputResource, providerId, MediaIoCapabilityKinds.VideoOutput, 1, true)
			});

		return new MediaIoProviderDescriptor(
			MediaIoContractVersion.Current,
			provider,
			new[]
			{
				CreatePort(providerId, inputResource, MediaIoDirection.Input, supportsExternalReference: true),
				CreatePort(providerId, outputResource, MediaIoDirection.Output, supportsExternalReference: false)
			});
	}

	private static MediaIoPortDescriptor CreatePort(
		ProviderId providerId,
		ProviderResourceId resourceId,
		MediaIoDirection direction,
		bool supportsExternalReference) =>
		new(
			MediaIoContractVersion.Current,
			providerId,
			resourceId,
			MediaIoPortId.New(),
			direction == MediaIoDirection.Input ? "SDI Input" : "SDI Output",
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
			supportsExternalReference);
}
