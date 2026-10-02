// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Performance;

public sealed class ManagedQuickTimeRecordingPerformanceTests
{
	[Fact]
	public void Mov_1080p_pixel_conversion_reuses_buffers_with_bounded_allocations()
	{
		var format = VideoFormat.Hd1080p50Rgba8;
		var input = new byte[checked((int)((long)format.Width * format.Height * 4))];
		var output = new byte[checked((int)((long)format.Width * format.Height * 2))];

		ManagedQuickTimeMovRecordingWriter.ConvertRgbaTo2Vuy(
			input,
			output,
			checked((int)format.Width),
			checked((int)format.Height));

		const int iterations = 4;
		var stopwatch = new Stopwatch();
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		stopwatch.Start();
		for (var index = 0; index < iterations; index++)
		{
			ManagedQuickTimeMovRecordingWriter.ConvertRgbaTo2Vuy(
				input,
				output,
				checked((int)format.Width),
				checked((int)format.Height));
		}
		stopwatch.Stop();
		var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		var pixelsPerSecond = checked((double)format.Width * format.Height * iterations / stopwatch.Elapsed.TotalSeconds);

		Assert.True(
			allocated <= 1024,
			$"MOV RGBA-to-2vuy conversion allocated {allocated} bytes after warm-up.");
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"Four 1080p MOV conversions took {stopwatch.Elapsed}; measured throughput was {pixelsPerSecond:N0} pixels/s.");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Mov_1080p_writer_has_bounded_software_write_and_finalize_cost(bool fractionalRate)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-mov-performance", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var format = fractionalRate ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
			var firstSequence = fractionalRate ? 1UL : 0UL;
			var outputId = RecordingOutputId.New();
			var writer = new ManagedQuickTimeMovRecordingWriter(root);
			writer.ConfigureTarget(root, "performance");
			writer.OpenAsync(Request(outputId), CancellationToken.None).GetAwaiter().GetResult();

			var videoPayload = new byte[checked((int)((long)format.Width * format.Height * 4))];

			var warmup = Sample(outputId, firstSequence, format, out var warmupAudio);
			writer.StagePayload(firstSequence, new TestPayloadLease(videoPayload), warmupAudio);
			writer.WriteAsync(warmup, CancellationToken.None).GetAwaiter().GetResult();

			var measuredSequence = checked(firstSequence + 1);
			var measured = Sample(outputId, measuredSequence, format, out var measuredAudio);
			writer.StagePayload(measuredSequence, new TestPayloadLease(videoPayload), measuredAudio);

			var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var writeStopwatch = Stopwatch.StartNew();
			writer.WriteAsync(measured, CancellationToken.None).GetAwaiter().GetResult();
			writeStopwatch.Stop();
			var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

			var finalizeStopwatch = Stopwatch.StartNew();
			writer.FinalizeAsync(CancellationToken.None).GetAwaiter().GetResult();
			finalizeStopwatch.Stop();

			var producedVideoBytes = checked((long)format.Width * format.Height * 2);
			var videoMegabytesPerSecond = producedVideoBytes / 1_000_000d / Math.Max(writeStopwatch.Elapsed.TotalSeconds, 0.000001);
			Assert.True(
				allocated <= 64 * 1024,
				$"Steady-state MOV write allocated {allocated} bytes after buffer warm-up.");
			Assert.True(
				writeStopwatch.Elapsed < TimeSpan.FromSeconds(15),
				$"Steady-state 1080p MOV write took {writeStopwatch.Elapsed}; measured payload throughput was {videoMegabytesPerSecond:N1} MB/s.");
			Assert.True(
				finalizeStopwatch.Elapsed < TimeSpan.FromSeconds(15),
				$"MOV finalization plus independent probe took {finalizeStopwatch.Elapsed}.");

			var probe = QuickTimeMovProbe.Probe(Path.Combine(root, "performance.mov"));
			Assert.Equal(format.FrameRate, probe.FrameRate);
			Assert.Equal(2U, probe.VideoSampleCount);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	private static RecordingStartRequest Request(RecordingOutputId outputId) =>
		new(
			RecordingContractVersion.Current,
			RecordingSessionId.New(),
			new RecordingOutputDescriptor(outputId, MediaSinkId.New(), "Program"),
			ProfessionalRecordingFormats.Mov2VuyPcmProfileId);

	private static RecordingProgramSample Sample(
		RecordingOutputId outputId,
		ulong sequence,
		VideoFormat format,
		out byte[] audioPayload)
	{
		var video = new FrameDescriptor(
			MediaContractVersion.Current,
			MediaSourceId.New(),
			new SurfaceDescriptor(
				SurfaceId.New(),
				format,
				SurfaceStorageDomain.Shared,
				SurfaceOwnership.SharedLease,
				new SurfaceLifetimeDescriptor(new Generation(sequence), Identity.New()),
				new OpaqueSurfaceHandle("performance.mov", $"surface-{sequence}")),
			new FrameTiming(
				sequence,
				checked((long)sequence),
				new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator)));

		var audioStart = AudioPosition(sequence, format.FrameRate);
		var audioEnd = AudioPosition(checked(sequence + 1), format.FrameRate);
		var audioCount = checked((uint)(audioEnd - audioStart));
		var audio = new AudioBufferDescriptor(
			MediaContractVersion.Current,
			AudioStreamId.New(),
			AudioFormat.Stereo48kFloat32,
			Identity.New(),
			new AudioBufferTiming(
				audioStart,
				audioCount,
				checked((long)audioStart),
				new Timebase(1, 48_000)),
			new OpaqueAudioHandle("performance.mov.audio", $"audio-{sequence}"));

		audioPayload = new byte[checked((int)((long)audioCount * 2 * sizeof(float)))];
		return new RecordingProgramSample(
			RecordingContractVersion.Current,
			outputId,
			video,
			audio);
	}

	private static ulong AudioPosition(ulong sequence, FrameRate frameRate) =>
		checked((ulong)(((UInt128)sequence * 48_000U * (ulong)frameRate.Denominator) / (ulong)frameRate.Numerator));

	private sealed class TestPayloadLease : IProgramRecordingPayloadLease
	{
		private readonly ReadOnlyMemory<byte> _memory;

		public TestPayloadLease(ReadOnlyMemory<byte> memory) => _memory = memory;
		public ReadOnlyMemory<byte> Memory => _memory;
		public void Dispose() { }
	}
}
