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
        var measurements = source.Measurements;
        Assert.Equal(12UL, measurements.DecodedFrames);
        Assert.True(measurements.DecodedAudioSamples > 0);
        Assert.True(measurements.LastVideoTimestamp > measurements.FirstVideoTimestamp);
        Assert.True(measurements.VideoSecondsPerTick > 0);
        Assert.True(measurements.NativeVideoRead.Calls >= 12);
        // AAC-LC packets contain at most 1,024 sample frames in these generated profiles.
        // A packet may now span several video windows instead of being consumed per frame.
        var minimumAudioPackets = (measurements.DecodedAudioSamples + 1_023) / 1_024;
        Assert.InRange(measurements.NativeAudioRead.Calls, minimumAudioPackets, minimumAudioPackets + 64);
        Assert.True(measurements.OpenMilliseconds >= 0);
        Assert.True(measurements.FirstFrameMilliseconds >= measurements.OpenMilliseconds);
        Assert.Equal(0UL, measurements.ReadFailures);
        Assert.Equal(0UL, measurements.VideoTimestampRegressions);
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
			var snapshot = LocalMediaTestPlayback.Next(deck);
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
			var snapshot = LocalMediaTestPlayback.Next(deck);
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

    [Theory]
    [InlineData("hd25", 50, 1)]
    [InlineData("hd50", 50, 1)]
    [InlineData("hd5994", 60_000, 1_001)]
    public void Real_profiles_preroll_admit_contiguous_audio_and_fence_GOP_seeks(
        string profileId, long numerator, long denominator)
    {
        if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled) return;
        using var asset = ReferenceMediaTestAsset.Generate(Profile(profileId));
        using var deck = new LocalMediaDeckRuntimeService(new VideoFormat(1920, 1080,
            new FrameRate(numerator, denominator), PixelFormat.Rgba8, ScanMode.Progressive));
        var source = MediaSourceId.New();
        var opened = deck.Open(new MediaDeckOpenRequest(MediaContractVersion.Current, source, asset.Path),
            Prepared(deck, source));
        Assert.Equal(MediaDeckState.Ready, opened.State);
        Assert.True(deck.DecodeSnapshot!.Value.QueuedFrames >= 3);
        var assetId = opened.Probe!.AssetId;
        Assert.True(deck.ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current, assetId,
            MediaTransportCommandKind.Play)).Succeeded);
        ulong nextAudioSample = 0;
        long previousPts = -1;
        for (var index = 0; index < 180; index++)
        {
            var observed = LocalMediaTestPlayback.Next(deck);
            Assert.NotEqual(MediaDeckState.Error, observed.State);
            var frame = deck.LatestBoundary!;
            Assert.Equal(LocalMediaRuntimeBoundaryStatus.Frame, frame.Status);
            Assert.True(frame.Video!.Timing.PresentationTimestamp > previousPts);
            previousPts = frame.Video.Timing.PresentationTimestamp;
            Assert.NotNull(frame.Audio);
            Assert.Equal(nextAudioSample, frame.Audio!.Timing.SamplePosition);
            nextAudioSample += frame.Audio.Timing.SampleCount;
            Assert.Equal(frame.Video.Timing.PresentationTimestamp, frame.Audio.Timing.PresentationTimestamp);
            Assert.InRange(deck.DecodeSnapshot!.Value.QueuedFrames, 0, LocalMediaDecodeWorker.QueueCapacity);
        }
        foreach (var target in new long[] { 59, 61 })
        {
            Assert.True(deck.ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current, assetId,
                MediaTransportCommandKind.Seek, target)).Succeeded);
            Assert.Null(deck.LatestBoundary);
            var fresh = LocalMediaTestPlayback.Next(deck);
            Assert.Equal(target, fresh.Transport!.Position.CurrentFrame);
            Assert.NotNull(deck.LatestBoundary!.Audio);
        }
        Assert.Equal(0UL, deck.PlaybackMeasurements!.Value.ReadFailures);
        Assert.Equal(0UL, deck.PlaybackMeasurements!.Value.MissingAudioFrames);
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

		var visualEvents = new List<double>();
		var audioEvents = new List<double>();
		for (var index = 0; index < 110; index++)
		{
			var decoded = source.ReadNext((ulong)index);
			Assert.True(decoded.Succeeded, decoded.Failure?.Message);
			var frame = decoded.Frame!;
			if (BottomPanelLuma(frame.RgbaPixels.Span, 1920, 1080) > 120)
			{
				visualEvents.Add(
					frame.Video.Timing.PresentationTimestamp *
					frame.Video.Timing.Timebase.SecondsPerTick);
			}
			if (Peak(frame.AudioPayload.Span) > 0.05 && frame.Audio is not null)
			{
				audioEvents.Add(
					frame.Audio.Timing.PresentationTimestamp *
					frame.Audio.Timing.Timebase.SecondsPerTick);
			}
		}

		Assert.Contains(visualEvents, seconds => Math.Abs(seconds - 1.0) <= 0.04);
		Assert.Contains(audioEvents, seconds => Math.Abs(seconds - 1.0) <= 0.04);
		Assert.Contains(visualEvents, seconds => Math.Abs(seconds - 2.0) <= 0.04);
		Assert.Contains(audioEvents, seconds => Math.Abs(seconds - 2.0) <= 0.04);
		Assert.True(
			NearestEventDistanceSeconds(visualEvents, audioEvents, 1.0) <= 0.04,
			"Decoded visual and audio events around 1 second must remain within 40 ms.");
		Assert.True(
			NearestEventDistanceSeconds(visualEvents, audioEvents, 2.0) <= 0.04,
			"Decoded visual and audio events around 2 seconds must remain within 40 ms.");
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
			Assert.NotEqual(MediaDeckState.Error, LocalMediaTestPlayback.Next(deck).State);

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
		var restarted = LocalMediaTestPlayback.Next(deck);
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

    [Theory]
    [InlineData("hd50", 300, 50, 1)]
    [InlineData("hd5994", 350, 60000, 1001)]
    public void Audio_packets_are_sliced_on_video_time_without_running_ahead_or_disappearing(
        string profileId, int frames, long rateNumerator, long rateDenominator)
    {
        if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled) return;
        using var asset = ReferenceMediaTestAsset.Generate(Profile(profileId));
        var format = new VideoFormat(1920, 1080, new FrameRate(rateNumerator, rateDenominator),
            PixelFormat.Rgba8, ScanMode.Progressive);
        var open = new LocalMediaFileProvider(format).TryOpen(asset.Path, MediaSourceId.New());
        Assert.True(open.Succeeded, open.Failure?.Message);
        using var source = open.Source!;
        ulong? expectedSample = null;
        for (var frame = 0; frame < frames; frame++)
        {
            var decoded = source.ReadNext((ulong)frame);
            Assert.True(decoded.Succeeded, decoded.Failure?.Message);
            var audio = decoded.Frame!.Audio;
            Assert.NotNull(audio);
            Assert.False(decoded.Frame.AudioPayload.IsEmpty);
            Assert.Equal(decoded.Frame.Video.Timing.PresentationTimestamp, audio.Timing.PresentationTimestamp);
            if (expectedSample is { } expected) Assert.Equal(expected, audio.Timing.SamplePosition);
            expectedSample = audio.Timing.SamplePosition + audio.Timing.SampleCount;
        }
        Assert.Equal(0UL, source.Measurements.MissingAudioFrames);
        Assert.Equal(0UL, source.Measurements.AudioTimestampDiscontinuities);
        Assert.Equal(0d, source.Measurements.MaximumAvTimestampDistanceMilliseconds);
        Assert.True(source.Measurements.NativeAudioRead.Calls < (ulong)frames);
        Assert.True(source.SeekToFrame(61).Succeeded);
        var afterSeek = source.ReadNext((ulong)frames);
        Assert.True(afterSeek.Succeeded); Assert.NotNull(afterSeek.Frame!.Audio);
        Assert.Equal(afterSeek.Frame.Video.Timing.PresentationTimestamp, afterSeek.Frame.Audio.Timing.PresentationTimestamp);
    }

    [Theory]
    [InlineData("h264-baseline")]
    [InlineData("hevc-main")]
    [InlineData("hevc-main10")]
    [InlineData("mov-aac")]
    [InlineData("mov-pcm")]
    [InlineData("vfr")]
    public void Generated_format_profiles_use_real_probe_decode_preroll_and_seek(string profile)
    {
        if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled) return;
        using var asset = ReferenceMediaTestAsset.GenerateFormat(profile);
        ILocalMediaProbe containerProbe = profile.StartsWith("mov-", StringComparison.Ordinal)
            ? new QuickTimeLocalMediaProbe() : new Mp4LocalMediaProbe();
        var metadata = containerProbe.Read(asset.Path);
        Assert.Equal(asset.Manifest.Container == "MP4" ? MediaContainerFormat.Mp4 : MediaContainerFormat.QuickTimeMov, metadata.Container);
        Assert.Equal(profile == "hevc-main10" ? 10 : 8, metadata.BitDepth);
        if (profile == "vfr") Assert.True(metadata.VariableFrameRate);
        using var deck = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8);
        var source = MediaSourceId.New();
        var opened = deck.Open(new MediaDeckOpenRequest(MediaContractVersion.Current, source, asset.Path), Prepared(deck, source));
        if (opened.Failure is { } unavailable && profile.StartsWith("hevc-", StringComparison.Ordinal))
        {
            // Host-specific negative evidence is retained, never reported as decoded or physically qualified.
            Assert.Equal(MediaDeckState.Error, opened.State);
            Assert.Null(opened.Probe);
            Assert.Contains(unavailable.Code, new[] { "media.file.decoder_unavailable", "media.file.normalization_failed" });
            WriteFormatOutcome(asset, metadata, "UNAVAILABLE", unavailable.Code, unavailable.Message);
            return;
        }
        Assert.Equal(MediaDeckState.Ready, opened.State);
        Assert.True(opened.Failure is null, opened.Failure?.Message);
        Assert.NotNull(opened.Probe);
        Assert.Equal(metadata.Container, opened.Probe!.Container);
        Assert.Equal(metadata.VideoCodec, opened.Probe.VideoCodec);
        Assert.Equal(profile == "mov-pcm" ? MediaAudioCodec.Pcm : MediaAudioCodec.Aac, opened.Probe.AudioCodec);
        var assetId = opened.Probe.AssetId;
        Assert.True(deck.ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current, assetId, MediaTransportCommandKind.Play)).Succeeded);
        ulong nextAudio = 0;
        long previousPts = -1;
        for (var index = 0; index < 110; index++)
        {
            var observed = LocalMediaTestPlayback.Next(deck);
            Assert.NotEqual(MediaDeckState.Error, observed.State);
            var frame = deck.LatestBoundary!;
            Assert.Equal(LocalMediaRuntimeBoundaryStatus.Frame, frame.Status);
            Assert.True(frame.Video!.Timing.PresentationTimestamp > previousPts);
            previousPts = frame.Video.Timing.PresentationTimestamp;
            Assert.NotNull(frame.Audio);
            Assert.Equal(nextAudio, frame.Audio!.Timing.SamplePosition);
            nextAudio += frame.Audio.Timing.SampleCount;
            Assert.Equal(frame.Video.Timing.PresentationTimestamp, frame.Audio.Timing.PresentationTimestamp);
        }
        foreach (var target in new long[] { 59, 61 })
        {
            Assert.True(deck.ApplyTransport(new MediaTransportCommand(MediaContractVersion.Current, assetId,
                MediaTransportCommandKind.Seek, target)).Succeeded);
            Assert.Null(deck.LatestBoundary);
            var observed = LocalMediaTestPlayback.Next(deck);
            Assert.Equal(target, observed.Transport!.Position.CurrentFrame);
            Assert.NotNull(deck.LatestBoundary!.Audio);
        }
        Assert.Equal(0UL, deck.PlaybackMeasurements!.Value.ReadFailures);
        Assert.Equal(0UL, deck.PlaybackMeasurements!.Value.MissingAudioFrames);
        WriteFormatOutcome(asset, metadata, "SOFTWARE_DECODED", null, null);
    }

    private static void WriteFormatOutcome(ReferenceMediaTestAsset asset, LocalMediaStreamMetadata metadata,
        string outcome, string? failureCode, string? detail)
    {
        var report = new {
            SchemaVersion = "1.0", SourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),
            asset.Manifest, ProbeVersion = LocalMediaStreamMetadata.CurrentVersion, Metadata = metadata,
            Provider = "Windows Media Foundation", Host = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            DecoderAvailability = outcome, FailureCode = failureCode, Detail = detail, PhysicalQualification = "UNVERIFIED"
        };
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("FORMAT OUTCOME: " + json);
        File.WriteAllText(Path.ChangeExtension(asset.Path, ".outcome.json"), json);
    }

	private static ReferenceMediaProfile Profile(string id) =>
		ReferenceMediaProfile.Required.Single(profile =>
			string.Equals(profile.Id, id, StringComparison.Ordinal));

	private static double NearestEventDistanceSeconds(
		IReadOnlyList<double> visualEvents,
		IReadOnlyList<double> audioEvents,
		double expectedSeconds)
	{
		var visual = visualEvents
			.OrderBy(value => Math.Abs(value - expectedSeconds))
			.First();
		var audio = audioEvents
			.OrderBy(value => Math.Abs(value - expectedSeconds))
			.First();
		return Math.Abs(visual - audio);
	}

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
