// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class LocalMediaRuntimeIntegrationTests
{
	[Fact]
	public void Reference_mp4_probes_plans_commits_and_delivers_video_audio_through_runtime_path()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var sourceA = new ProductionSourceId(Id(1));
		var sourceB = new ProductionSourceId(Id(2));
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			new ProductionId(Id(10)),
			"Local Media Integration",
			new[]
			{
				new ProductionSourceSpecification(sourceA, "Local Clip"),
				new ProductionSourceSpecification(sourceB, "Standby")
			},
			new ProductionRoutingState(sourceA, sourceA));

		var initialized = ControlDomainEngine.Initialize(specification);
		Assert.True(initialized.Succeeded);

		using var referenceAsset = LocalMediaTestAsset.ExtractReference1080p50();
		var provider = new LocalMediaFileProvider();
		var mediaSourceId = new MediaSourceId(sourceA.Value);
		var open = provider.TryOpen(
			referenceAsset.Path,
			mediaSourceId,
			new MediaAssetId(Id(100)));

		Assert.True(open.Succeeded, open.Failure?.Message);
		Assert.NotNull(open.Source);
		Assert.Equal(MediaContainerFormat.Mp4, open.Source!.Probe.Container);
		Assert.Equal(MediaVideoCodec.H264, open.Source.Probe.VideoCodec);
		Assert.Equal(MediaAudioCodec.Aac, open.Source.Probe.AudioCodec);
		Assert.Equal(VideoFormat.Hd1080p50Rgba8, open.Source.Probe.VideoFormat);
		Assert.Equal(AudioFormat.Stereo48kFloat32, open.Source.Probe.AudioFormat);
		Assert.True(open.Source.Probe.Duration > TimeSpan.Zero);

		var planning = CapabilityPlanningEngine.Plan(
			specification,
			initialized.State!.Authoritative,
			new SingleProviderRegistry(provider.Descriptor));

		Assert.True(planning.Succeeded, string.Join("; ", planning.Validation.Issues.Select(issue => issue.Message)));
		Assert.NotNull(planning.PreparedExecution);
		Assert.Contains(planning.PreparedExecution!.Bindings, binding =>
			binding.MediaSourceId == mediaSourceId && binding.Resource.ProviderId == provider.Descriptor.ProviderId);

		using var session = new LocalMediaRuntimeSession(provider, open.Source);
		var applied = session.ApplyExecution(planning.PreparedExecution);
		Assert.True(applied.Committed);
		Assert.Equal(RuntimeCommitStatus.Committed, applied.Commit!.Status);

		var play = session.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			open.Source.AssetId,
			MediaTransportCommandKind.Play));
		Assert.True(play.Succeeded, play.Failure?.Message);

		var boundary = session.ProcessNextBoundary();
		Assert.True(boundary.Succeeded, $"{boundary.Status}: {boundary.Failure?.Message}");
		Assert.Equal(mediaSourceId, boundary.Video!.SourceId);
		Assert.Equal((long)1920 * 1080 * 4, boundary.RgbaPixels.Length);
		Assert.NotNull(boundary.Audio);
		Assert.False(boundary.AudioPayload.IsEmpty);
		Assert.Equal(AudioFormat.Stereo48kFloat32, boundary.Audio!.Format);
		Assert.Equal(boundary.Video.Timing.Timebase, boundary.Audio.Timing.Timebase);
		Assert.Equal(1UL, session.NextSequenceNumber);
	}

	[Fact]
	public void Reference_mp4_sustains_multiple_playback_cycles_without_decode_failure()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var referenceAsset = LocalMediaTestAsset.ExtractReference1080p50();
		var provider = new LocalMediaFileProvider();
		var open = provider.TryOpen(
			referenceAsset.Path,
			MediaSourceId.New(),
			new MediaAssetId(Id(102)));

		Assert.True(open.Succeeded, open.Failure?.Message);
		Assert.NotNull(open.Source);
		using var source = open.Source!;

		const int targetFrames = 250;
		var decodedFrames = 0;
		ulong sequence = 0;
		while (decodedFrames < targetFrames)
		{
			var decoded = source.ReadNext(sequence);
			if (decoded.Status == LocalMediaFrameReadStatus.Ended)
			{
				var seek = source.SeekToFrame(0);
				Assert.True(seek.Succeeded, seek.Failure?.Message);
				continue;
			}

			Assert.True(decoded.Succeeded, decoded.Failure?.Message);
			Assert.NotNull(decoded.Frame);
			Assert.Equal((long)1920 * 1080 * 4, decoded.Frame!.RgbaPixels.Length);
			decodedFrames++;
			sequence++;
		}

		Assert.Equal(targetFrames, decodedFrames);
	}

	[Fact]
	public void Reference_mp4_normalizes_to_requested_runtime_format()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var referenceAsset = LocalMediaTestAsset.ExtractReference1080p50();
		var targetFormat = VideoFormat.Hd1080p59_94Rgba8;
		var provider = new LocalMediaFileProvider(targetFormat);
		var open = provider.TryOpen(
			referenceAsset.Path,
			MediaSourceId.New(),
			new MediaAssetId(Id(101)));

		Assert.True(open.Succeeded, open.Failure?.Message);
		Assert.NotNull(open.Source);
		Assert.Equal(targetFormat, open.Source!.Probe.VideoFormat);
		Assert.Equal(AudioFormat.Stereo48kFloat32, open.Source.Probe.AudioFormat);

		var decoded = open.Source.ReadNext(0);
		Assert.True(decoded.Succeeded, decoded.Failure?.Message);
		Assert.NotNull(decoded.Frame);
		Assert.Equal(targetFormat, decoded.Frame!.Video.Surface.Format);
		Assert.Equal((long)targetFormat.Width * targetFormat.Height * 4, decoded.Frame.RgbaPixels.Length);
		Assert.NotNull(decoded.Frame.Audio);
		Assert.Equal(AudioFormat.Stereo48kFloat32, decoded.Frame.Audio!.Format);
	}

	[Fact]
	public void Missing_and_invalid_container_files_fail_without_runtime_exception()
	{
		var provider = new LocalMediaFileProvider();
		var missing = provider.TryOpen(
			Path.Combine(Path.GetTempPath(), $"rtaime-{Guid.NewGuid():N}.mp4"),
			MediaSourceId.New());
		Assert.False(missing.Succeeded);
		Assert.Equal("media.file.not_found", missing.Failure?.Code);

		var path = Path.Combine(Path.GetTempPath(), $"rtaime-{Guid.NewGuid():N}.mov");
		try
		{
			File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
			var unsupported = provider.TryOpen(path, MediaSourceId.New());
			Assert.False(unsupported.Succeeded);
			Assert.Equal("media.file.container_unsupported", unsupported.Failure?.Code);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	[Fact]
	public void Corrupt_mp4_fails_closed()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var path = Path.Combine(Path.GetTempPath(), $"rtaime-{Guid.NewGuid():N}.mp4");
		try
		{
			File.WriteAllBytes(path, Enumerable.Range(0, 128).Select(value => (byte)value).ToArray());
			var result = new LocalMediaFileProvider().TryOpen(path, MediaSourceId.New());
			Assert.False(result.Succeeded);
			Assert.NotNull(result.Failure);
			Assert.Equal("media.file.probe_failed", result.Failure?.Code);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	private static Identity Id(int value) =>
		Identity.Parse($"41000000-0000-0000-0000-{value:000000000000}");

	private sealed class SingleProviderRegistry : IProviderCapabilityRegistry
	{
		private readonly IReadOnlyList<ProviderDescriptor> _providers;

		public SingleProviderRegistry(ProviderDescriptor provider)
		{
			_providers = Array.AsReadOnly(new[] { provider });
		}

		public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
	}
}
