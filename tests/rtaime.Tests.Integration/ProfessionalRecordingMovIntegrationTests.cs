// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Integration;

public sealed class ProfessionalRecordingMovIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Managed_mov_finalizes_and_is_independently_probed(bool fractionalRate)
	{
		var root = CreateTemporaryRoot();
		try
		{
			var format = fractionalRate ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
			var writer = new ManagedQuickTimeMovRecordingWriter(root);
			Assert.Equal("program.mov", writer.ConfigureTarget(root, "program"));
			var outputId = RecordingOutputId.New();
			await writer.OpenAsync(Request(outputId), CancellationToken.None);

			var videoPayload = new byte[checked((int)((long)format.Width * format.Height * 4))];
			var firstSequence = fractionalRate ? 1UL : 3UL;
			for (var offset = 0UL; offset < 2; offset++)
			{
				var sequence = checked(firstSequence + offset);
				var sample = Sample(outputId, sequence, format, out var audioPayload);
				var lease = new TestPayloadLease(videoPayload);
				writer.StagePayload(sequence, lease, audioPayload);
				await writer.WriteAsync(sample, CancellationToken.None);
				Assert.True(lease.Disposed);
			}

			await writer.FinalizeAsync(CancellationToken.None);

			var finalPath = Path.Combine(root, "program.mov");
			Assert.Equal(finalPath, writer.FinalPath);
			Assert.True(File.Exists(finalPath));
			Assert.False(File.Exists(Path.Combine(root, "program.partial.mov")));
			Assert.False(File.Exists(finalPath + ".lock"));

			var probe = QuickTimeMovProbe.Probe(finalPath);
			Assert.Equal("QuickTime Movie (MOV)", probe.Container);
			Assert.Equal("2vuy", probe.VideoSampleEntry);
			Assert.Equal("sowt", probe.AudioSampleEntry);
			Assert.Equal(format.Width, probe.Width);
			Assert.Equal(format.Height, probe.Height);
			Assert.Equal(format.FrameRate, probe.FrameRate);
			Assert.Equal(48_000U, probe.AudioSampleRate);
			Assert.Equal((ushort)2, probe.AudioChannels);
			Assert.Equal((ushort)16, probe.AudioBitsPerSample);
			Assert.Equal(2U, probe.VideoSampleCount);
			Assert.True(probe.AudioSampleFrameCount > 0);
			if (fractionalRate)
			{
				Assert.Equal(TimeSpan.FromSeconds(1.0 / 60_000), probe.VideoStartOffset);
				Assert.Equal(TimeSpan.Zero, probe.AudioStartOffset);
			}
			else
			{
				Assert.Equal(TimeSpan.Zero, probe.VideoStartOffset);
				Assert.Equal(TimeSpan.Zero, probe.AudioStartOffset);
			}
			Assert.InRange(
				Math.Abs(
					(probe.VideoStartOffset + probe.VideoDuration -
					 probe.AudioStartOffset - probe.AudioDuration).TotalSeconds),
				0,
				1.0 / format.FrameRate.FramesPerSecond);
		}
		finally
		{
			DeleteTemporaryRoot(root);
		}
	}

	[Fact]
	public async Task Managed_mov_quota_failure_never_publishes_a_final_artifact()
	{
		var root = CreateTemporaryRoot();
		try
		{
			var writer = new ManagedQuickTimeMovRecordingWriter(root, maximumPayloadBytes: 1);
			writer.ConfigureTarget(root, "quota");
			var outputId = RecordingOutputId.New();
			await writer.OpenAsync(Request(outputId), CancellationToken.None);

			var format = VideoFormat.Hd1080p50Rgba8;
			var sample = Sample(outputId, 0, format, out var audioPayload);
			var videoPayload = new byte[checked((int)((long)format.Width * format.Height * 4))];
			var lease = new TestPayloadLease(videoPayload);
			writer.StagePayload(0, lease, audioPayload);

			await Assert.ThrowsAsync<IOException>(async () =>
				await writer.WriteAsync(sample, CancellationToken.None).AsTask());
			Assert.True(lease.Disposed);

			await writer.AbortAsync(CancellationToken.None);

			Assert.False(File.Exists(Path.Combine(root, "quota.mov")));
			Assert.False(File.Exists(Path.Combine(root, "quota.partial.mov")));
			Assert.False(File.Exists(Path.Combine(root, "quota.mov.lock")));
		}
		finally
		{
			DeleteTemporaryRoot(root);
		}
	}

	[Fact]
	public async Task Independent_probe_rejects_a_truncated_mov()
	{
		var root = CreateTemporaryRoot();
		try
		{
			var writer = new ManagedQuickTimeMovRecordingWriter(root);
			writer.ConfigureTarget(root, "complete");
			var outputId = RecordingOutputId.New();
			await writer.OpenAsync(Request(outputId), CancellationToken.None);

			var format = VideoFormat.Hd1080p50Rgba8;
			var sample = Sample(outputId, 0, format, out var audioPayload);
			var videoPayload = new byte[checked((int)((long)format.Width * format.Height * 4))];
			writer.StagePayload(0, new TestPayloadLease(videoPayload), audioPayload);
			await writer.WriteAsync(sample, CancellationToken.None);
			await writer.FinalizeAsync(CancellationToken.None);

			var source = Path.Combine(root, "complete.mov");
			var truncated = Path.Combine(root, "truncated.mov");
			var bytes = await File.ReadAllBytesAsync(source);
			await File.WriteAllBytesAsync(truncated, bytes.AsMemory(0, bytes.Length - 32).ToArray());

			Assert.Throws<InvalidDataException>(() => QuickTimeMovProbe.Probe(truncated));
		}
		finally
		{
			DeleteTemporaryRoot(root);
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
		var videoTimebase = new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator);
		var surface = new SurfaceDescriptor(
			SurfaceId.New(),
			format,
			SurfaceStorageDomain.Shared,
			SurfaceOwnership.SharedLease,
			new SurfaceLifetimeDescriptor(new Generation(sequence), Identity.New()),
			new OpaqueSurfaceHandle("test.mov", $"surface-{sequence}"));
		var video = new FrameDescriptor(
			MediaContractVersion.Current,
			MediaSourceId.New(),
			surface,
			new FrameTiming(sequence, checked((long)sequence), videoTimebase));

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
			new OpaqueAudioHandle("test.mov.audio", $"audio-{sequence}"));

		audioPayload = new byte[checked((int)((long)audioCount * 2 * sizeof(float)))];
		return new RecordingProgramSample(
			RecordingContractVersion.Current,
			outputId,
			video,
			audio);
	}

	private static ulong AudioPosition(ulong sequence, FrameRate frameRate) =>
		checked((ulong)(((UInt128)sequence * 48_000U * (ulong)frameRate.Denominator) / (ulong)frameRate.Numerator));

	private static string CreateTemporaryRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-professional-mov-recording", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void DeleteTemporaryRoot(string root)
	{
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}

	private sealed class TestPayloadLease : IProgramRecordingPayloadLease
	{
		private readonly ReadOnlyMemory<byte> _memory;

		public TestPayloadLease(ReadOnlyMemory<byte> memory) => _memory = memory;
		public bool Disposed { get; private set; }
		public ReadOnlyMemory<byte> Memory => Disposed
			? throw new ObjectDisposedException(nameof(TestPayloadLease))
			: _memory;
		public void Dispose() => Disposed = true;
	}
}
