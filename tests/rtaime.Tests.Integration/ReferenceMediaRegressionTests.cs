// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ReferenceMediaRegressionTests
{
	[Theory]
	[InlineData("hd25", 50, 1)]
	[InlineData("hd50", 50, 1)]
	[InlineData("hd5994", 60_000, 1_001)]
	public void Required_reference_profiles_open_with_h264_aac_and_expected_runtime_normalization(
		string profileId,
		long outputRateNumerator,
		long outputRateDenominator)
	{
		if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled)
			return;

		var profile = Profile(profileId);
		using var asset = ReferenceMediaTestAsset.Generate(profile);
		var outputFormat = new VideoFormat(
			1920,
			1080,
			new FrameRate(outputRateNumerator, outputRateDenominator),
			PixelFormat.Rgba8,
			ScanMode.Progressive);
		var provider = new LocalMediaFileProvider(outputFormat);
		var open = provider.TryOpen(asset.Path, MediaSourceId.New());

		Assert.True(open.Succeeded, open.Failure?.Message);
		Assert.NotNull(open.Source);
		using var source = open.Source!;
		Assert.Equal(MediaContainerFormat.Mp4, source.Probe.Container);
		Assert.Equal(MediaVideoCodec.H264, source.Probe.VideoCodec);
		Assert.Equal(MediaAudioCodec.Aac, source.Probe.AudioCodec);
		Assert.Equal(outputFormat, source.Probe.VideoFormat);
		Assert.Equal(AudioFormat.Stereo48kFloat32, source.Probe.AudioFormat);
		Assert.True(source.Probe.Duration >= TimeSpan.FromSeconds(5.8));
		Assert.Equal(profile.NativeFrameRate.ToString(), asset.Manifest.NativeFrameRate);
		Assert.Equal((uint)48_000, asset.Manifest.AudioSampleRate);
		Assert.Equal((uint)2, asset.Manifest.AudioChannels);
		Assert.Matches("^[0-9a-f]{64}$", asset.Manifest.Sha256);

		var previousTimestamp = long.MinValue;
		for (ulong sequence = 0; sequence < 12; sequence++)
		{
			var decoded = source.ReadNext(sequence);
			Assert.True(decoded.Succeeded, decoded.Failure?.Message);
			Assert.NotNull(decoded.Frame);
			Assert.True(decoded.Frame!.Video.Timing.PresentationTimestamp > previousTimestamp);
			previousTimestamp = decoded.Frame.Video.Timing.PresentationTimestamp;
			Assert.NotNull(decoded.Frame.Audio);
			Assert.False(decoded.Frame.AudioPayload.IsEmpty);
		}
	}

	[Fact]
	public void Six_second_reference_stays_playing_past_five_seconds_and_loops_without_error_or_unbounded_memory_growth()
	{
		if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled)
			return;

		using var asset = ReferenceMediaTestAsset.Generate(ReferenceMediaProfile.Hd50);
		using var deck = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8);
		var source = new MediaSourceId(Id(1));
		var opened = deck.Open(
			new MediaDeckOpenRequest(MediaContractVersion.Current, source, asset.Path),
			Prepared(deck, source));
		Assert.NotNull(opened.Probe);
		Assert.Null(opened.Failure);
		var assetId = opened.Probe!.AssetId;

		var configured = deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.ConfigurePlayback,
			autoPlayOnProgram: false,
			endBehavior: MediaDeckEndBehavior.Loop));
		Assert.True(configured.Succeeded, configured.Failure?.Message);
		Assert.True(deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.Play)).Succeeded);

		for (var frame = 0; frame < 260; frame++)
		{
			var snapshot = deck.ProcessBoundary();
			Assert.NotEqual(MediaDeckState.Error, snapshot.State);
			Assert.Equal(MediaTransportState.Playing, snapshot.Transport!.State);
			Assert.Null(snapshot.Failure);
			Assert.NotNull(deck.LatestBoundary);
			Assert.True(deck.LatestBoundary!.Succeeded, deck.LatestBoundary.Failure?.Message);
		}

		var afterFiveSeconds = deck.Snapshot;
		Assert.Equal(MediaDeckState.Playing, afterFiveSeconds.State);
		Assert.Equal(MediaTransportState.Playing, afterFiveSeconds.Transport!.State);
		Assert.True(afterFiveSeconds.Transport.Position.Position >= TimeSpan.FromSeconds(5));

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var frame = 0; frame < 640; frame++)
		{
			var snapshot = deck.ProcessBoundary();
			Assert.NotEqual(MediaDeckState.Error, snapshot.State);
			Assert.Equal(MediaTransportState.Playing, snapshot.Transport!.State);
			Assert.Null(snapshot.Failure);
		}

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
		var retainedGrowth = memoryAfter - memoryBefore;
		Assert.InRange(retainedGrowth, long.MinValue, 128L * 1024 * 1024);
		Assert.Equal(MediaDeckState.Playing, deck.Snapshot.State);
		Assert.Equal(MediaTransportState.Playing, deck.Snapshot.Transport!.State);
	}

	[Fact]
	public void Encoded_reference_preserves_visual_sync_flash_and_audio_pulse_near_the_same_event()
	{
		if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled)
			return;

		using var asset = ReferenceMediaTestAsset.Generate(ReferenceMediaProfile.Hd50);
		var provider = new LocalMediaFileProvider(VideoFormat.Hd1080p50Rgba8);
		var open = provider.TryOpen(asset.Path, MediaSourceId.New());
		Assert.True(open.Succeeded, open.Failure?.Message);
		Assert.NotNull(open.Source);
		using var source = open.Source!;

		var visualEvents = new List<int>();
		var audioEvents = new List<int>();
		for (var index = 0; index < 110; index++)
		{
			var decoded = source.ReadNext((ulong)index);
			Assert.True(decoded.Succeeded, decoded.Failure?.Message);
			var frame = decoded.Frame!;
			if (BottomPanelLuma(frame.RgbaPixels.Span, 1920, 1080) > 120)
				visualEvents.Add(index);
			if (Peak(frame.AudioPayload.Span) > 0.05)
				audioEvents.Add(index);
		}

		Assert.Contains(visualEvents, frame => Math.Abs(frame - 50) <= 1);
		Assert.Contains(audioEvents, frame => Math.Abs(frame - 50) <= 2);
		Assert.Contains(visualEvents, frame => Math.Abs(frame - 100) <= 1);
		Assert.Contains(audioEvents, frame => Math.Abs(frame - 100) <= 2);
	}

	[Fact]
	public void Reference_media_supports_stop_restart_close_and_reload()
	{
		if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled)
			return;

		using var asset = ReferenceMediaTestAsset.Generate(ReferenceMediaProfile.Hd25);
		using var deck = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8);
		var source = new MediaSourceId(Id(2));

		var opened = deck.Open(
			new MediaDeckOpenRequest(MediaContractVersion.Current, source, asset.Path),
			Prepared(deck, source));
		Assert.NotNull(opened.Probe);
		var assetId = opened.Probe!.AssetId;

		Assert.True(deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.Play)).Succeeded);
		for (var index = 0; index < 20; index++)
			Assert.NotEqual(MediaDeckState.Error, deck.ProcessBoundary().State);

		var stopped = deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.Stop));
		Assert.True(stopped.Succeeded, stopped.Failure?.Message);
		Assert.Equal(MediaTransportState.Ready, stopped.Snapshot.State);
		Assert.Equal(0, stopped.Snapshot.Position.CurrentFrame);

		Assert.True(deck.ApplyTransport(new MediaTransportCommand(
			MediaContractVersion.Current,
			assetId,
			MediaTransportCommandKind.Play)).Succeeded);
		var restarted = deck.ProcessBoundary();
		Assert.Equal(MediaDeckState.Playing, restarted.State);
		Assert.NotNull(deck.LatestBoundary);
		Assert.True(deck.LatestBoundary!.Succeeded, deck.LatestBoundary.Failure?.Message);

		Assert.Equal(MediaDeckState.Unloaded, deck.Close().State);
		var reopened = deck.Open(
			new MediaDeckOpenRequest(MediaContractVersion.Current, source, asset.Path),
			Prepared(deck, source));
		Assert.Equal(MediaDeckState.Ready, reopened.State);
		Assert.Null(reopened.Failure);
	}

	private static ReferenceMediaProfile Profile(string id) =>
		ReferenceMediaProfile.Required.Single(profile =>
			string.Equals(profile.Id, id, StringComparison.Ordinal));

	private static int BottomPanelLuma(ReadOnlySpan<byte> rgba, int width, int height)
	{
		var x = 18;
		var y = height * 2 / 3;
		var offset = checked((y * width + x) * 4);
		return (rgba[offset] + rgba[offset + 1] + rgba[offset + 2]) / 3;
	}

	private static double Peak(ReadOnlySpan<byte> payload)
	{
		if (payload.IsEmpty)
			return 0;
		var samples = MemoryMarshal.Cast<byte, float>(payload);
		var peak = 0d;
		foreach (var sample in samples)
			peak = Math.Max(peak, Math.Abs(sample));
		return peak;
	}

	private static PreparedExecutionContract Prepared(LocalMediaDeckRuntimeService deck, MediaSourceId source)
	{
		var provider = deck.ProviderDescriptor;
		var capability = provider.Capabilities.Single(candidate =>
			string.Equals(candidate.Kind, "media.route", StringComparison.Ordinal));
		var resource = provider.Resources.First(candidate =>
			string.Equals(candidate.Kind, "media.route", StringComparison.Ordinal));
		return new PreparedExecutionContract(
			RuntimeContractVersion.Current,
			PreparedExecutionId.New(),
			new AuthoritySnapshotReference(Id(900), new Revision(1)),
			new Generation(1),
			new[]
			{
				new PreparedExecutionBinding(
					source.Value,
					capability.CapabilityId,
					resource,
					source,
					null)
			});
	}

	private static Identity Id(int value) =>
		Identity.Parse($"53000000-0000-0000-0000-{value:000000000000}");
}

public sealed class ReferenceMediaGenerationTests
{
	[Fact]
	public void Generate_required_reference_media_profiles_and_manifests()
	{
		if (!ReferenceMediaTestAsset.IsRegressionEnabled)
			return;

		var output = Environment.GetEnvironmentVariable("RTAIME_REFERENCE_MEDIA_OUTPUT");
		if (string.IsNullOrWhiteSpace(output))
			return;

		foreach (var profile in ReferenceMediaProfile.Required)
		{
			using var asset = ReferenceMediaTestAsset.Generate(profile);
			Assert.True(File.Exists(asset.Path));
			Assert.True(File.Exists(asset.ManifestPath));
			Assert.True(new FileInfo(asset.Path).Length > 0);
			Assert.Matches("^[0-9a-f]{64}$", asset.Manifest.Sha256);
		}
	}
}
