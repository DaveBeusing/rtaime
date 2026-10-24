// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Srt;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class SrtTransportPacketizationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Large_encoder_drain_is_sent_as_bounded_ordered_messages_at_both_rates(bool use5994)
	{
		var bytes = Packets(35);
		var transport = new CapturingTransport();
		var encoder = new ScriptedEncoder(bytes);
		await using var session = new SrtNetworkOutputSession(Configuration(use5994), () => transport, () => encoder);
		await SubmitAsync(session, 0);
		Assert.Equal(bytes, transport.Messages.SelectMany(message => message).ToArray());
		Assert.Equal(5, transport.Messages.Count);
		Assert.All(transport.Messages, message => Assert.Equal(1316, message.Length));
		var snapshot = session.Snapshot;
		Assert.Equal(1UL, snapshot.Statistics.SentSamples);
		Assert.Equal(5UL, snapshot.Statistics.PacketsSent);
		Assert.Equal((ulong)bytes.Length, snapshot.Statistics.BytesSent);
		Assert.NotNull(snapshot.LastSuccessfulSendUtc);
		Assert.Null(snapshot.Failure);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(187)]
	[InlineData(188)]
	[InlineData(1315)]
	[InlineData(1316)]
	[InlineData(1317)]
	public async Task Partial_packet_and_empty_drains_preserve_byte_order_without_reconnect(int split)
	{
		var bytes = Packets(21);
		var transport = new CapturingTransport();
		var encoder = new ScriptedEncoder(bytes[..split], [], bytes[split..]);
		await using var session = new SrtNetworkOutputSession(Configuration(), () => transport, () => encoder);
		for (ulong sequence = 0; sequence < 3; sequence++)
			await SubmitAsync(session, sequence);
		Assert.Equal(bytes, transport.Messages.SelectMany(message => message).ToArray());
		Assert.All(transport.Messages, message =>
		{
			Assert.InRange(message.Length, 188, 1316);
			Assert.Equal(0, message.Length % 188);
			for (var offset = 0; offset < message.Length; offset += 188)
				Assert.Equal(0x47, message[offset]);
		});
		Assert.Equal(1, transport.Connects);
		Assert.Equal(0UL, session.Snapshot.Statistics.ReconnectCount);
		Assert.Equal(0UL, session.Snapshot.Statistics.DroppedSamples);
		Assert.Equal((ulong)transport.Messages.Count, session.Snapshot.Statistics.PacketsSent);
		Assert.Equal((ulong)bytes.Length, session.Snapshot.Statistics.BytesSent);
	}

	[Fact]
	public async Task Incomplete_packet_does_not_fabricate_a_successful_send()
	{
		var bytes = Packets(1);
		var transport = new CapturingTransport();
		var encoder = new ScriptedEncoder(bytes[..187], [], bytes[187..]);
		await using var session = new SrtNetworkOutputSession(Configuration(), () => transport, () => encoder);
		await SubmitAsync(session, 0);
		await SubmitAsync(session, 1);
		Assert.Empty(transport.Messages);
		Assert.Equal(0UL, session.Snapshot.Statistics.BytesSent);
		Assert.Equal(0UL, session.Snapshot.Statistics.SentSamples);
		Assert.Null(session.Snapshot.LastSuccessfulSendUtc);
		await SubmitAsync(session, 2);
		Assert.Equal(bytes, Assert.Single(transport.Messages));
		Assert.Equal(1UL, session.Snapshot.Statistics.SentSamples);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Failed_message_keeps_successful_prefix_accounting_and_is_not_replayed(bool shortSend)
	{
		var firstBytes = Packets(20);
		var recoveryBytes = Packets(8, 0x31);
		var failed = new CapturingTransport(failMessage: 2, shortSend);
		var recovered = new CapturingTransport();
		var transports = new Queue<CapturingTransport>([failed, recovered]);
		var encoders = new Queue<ScriptedEncoder>([new(firstBytes), new(recoveryBytes)]);
		await using var session = new SrtNetworkOutputSession(Configuration(), () => transports.Dequeue(), () => encoders.Dequeue());
		await SubmitAsync(session, 0);
		Assert.Equal(2, failed.Attempts);
		Assert.Equal(firstBytes[..1316], Assert.Single(failed.Messages));
		Assert.Equal(1UL, session.Snapshot.Statistics.PacketsSent);
		Assert.Equal(1316UL, session.Snapshot.Statistics.BytesSent);
		Assert.Equal(0UL, session.Snapshot.Statistics.SentSamples);
		Assert.Equal(1UL, session.Snapshot.Statistics.DroppedSamples);
		Assert.NotNull(session.Snapshot.LastSuccessfulSendUtc);
		Assert.Equal(1, failed.Disposals);
		await SubmitAsync(session, 1);
		Assert.Equal(recoveryBytes, recovered.Messages.SelectMany(message => message).ToArray());
		Assert.Equal(3UL, session.Snapshot.Statistics.PacketsSent);
		Assert.Equal(1316UL + (ulong)recoveryBytes.Length, session.Snapshot.Statistics.BytesSent);
		Assert.Equal(1UL, session.Snapshot.Statistics.SentSamples);
		Assert.Equal(2, failed.Attempts);
	}

	[Fact]
	public async Task Invalid_ts_sync_drops_the_stream_and_reconnect_discards_its_residual()
	{
		var interrupted = Packets(3);
		interrupted[188] = 0;
		var next = Packets(2, 0x62);
		var firstTransport = new CapturingTransport();
		var nextTransport = new CapturingTransport();
		var transports = new Queue<CapturingTransport>([firstTransport, nextTransport]);
		var encoders = new Queue<ScriptedEncoder>([new(interrupted[..97], interrupted[97..]), new(next)]);
		await using var session = new SrtNetworkOutputSession(Configuration(), () => transports.Dequeue(), () => encoders.Dequeue());
		await SubmitAsync(session, 0);
		await SubmitAsync(session, 1);
		Assert.Equal(interrupted[..188], Assert.Single(firstTransport.Messages));
		Assert.Equal(1UL, session.Snapshot.Statistics.DroppedSamples);
		Assert.Contains("messageBytes=376; packetOffset=0; expectedSync=0x47; actualSync=0x00", session.Snapshot.Failure!.Value.Message);
		await SubmitAsync(session, 2);
		Assert.Equal(next, Assert.Single(nextTransport.Messages));
		Assert.Null(session.Snapshot.Failure);
	}

	internal static byte[] Packets(int count, byte seed = 0x10)
	{
		var bytes = new byte[checked(count * 188)];
		for (var packet = 0; packet < count; packet++)
		{
			var offset = packet * 188;
			Array.Fill(bytes, (byte)(seed + packet), offset, 188);
			bytes[offset] = 0x47;
			bytes[offset + 1] = 0x1f;
			bytes[offset + 2] = 0xff;
			bytes[offset + 3] = (byte)(0x10 | (packet & 15));
		}
		return bytes;
	}

	private static async Task SubmitAsync(SrtNetworkOutputSession session, ulong sequence)
	{
		var lease = new CompletionLease();
		var timebase = new Timebase(session.Configuration.VideoFormat.FrameRate.Denominator,
			session.Configuration.VideoFormat.FrameRate.Numerator);
		var sample = new NetworkOutputProgramSample(sequence, new FrameTiming(sequence, (long)sequence, timebase),
			new AudioBufferTiming(sequence * 960, 960, (long)sequence, timebase), lease, new byte[8]);
		Assert.True(session.TrySubmit(sample).Accepted);
		await lease.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static NetworkOutputConfiguration Configuration(bool use5994 = false) => new(
		"packetization", new Uri("srt://127.0.0.1:9000"), NetworkOutputProtocolFamily.Srt,
		NetworkOutputConnectionMode.Caller, use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8,
		AudioFormat.Stereo48kFloat32, NetworkOutputVideoCodec.H264, NetworkOutputAudioCodec.AacLc,
		12_000_000, 192_000, 120);

	private sealed class CompletionLease : INetworkOutputPayloadLease
	{
		public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ReadOnlyMemory<byte> Memory => new byte[4];
		public void Dispose() => Returned.TrySetResult();
	}

	private sealed class ScriptedEncoder(params byte[][] chunks) : ISrtPayloadEncoder
	{
		private int _index;
		public ValueTask InitializeAsync(NetworkOutputConfiguration configuration, CancellationToken token) => ValueTask.CompletedTask;
		public ValueTask<ReadOnlyMemory<byte>> EncodeAsync(NetworkOutputProgramSample sample, CancellationToken token) =>
			ValueTask.FromResult<ReadOnlyMemory<byte>>(chunks[_index++]);
		public ValueTask<ReadOnlyMemory<byte>> DrainAsync(CancellationToken token) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class CapturingTransport(int failMessage = 0, bool shortSend = false) : ISrtTransport
	{
		public bool IsConnected { get; private set; }
		public int Connects { get; private set; }
		public int Attempts { get; private set; }
		public int Disposals { get; private set; }
		public List<byte[]> Messages { get; } = new();
		public ValueTask ConnectAsync(NetworkOutputConfiguration configuration, string? passphrase, CancellationToken token)
		{
			Connects++;
			IsConnected = true;
			return ValueTask.CompletedTask;
		}
		public ValueTask<int> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
		{
			Attempts++;
			if (Attempts == failMessage)
			{
				if (shortSend) return ValueTask.FromResult(payload.Length - 188);
				throw new IOException("Injected live message failure.");
			}
			Assert.InRange(payload.Length, 188, 1316);
			Assert.Equal(0, payload.Length % 188);
			Messages.Add(payload.ToArray());
			return ValueTask.FromResult(payload.Length);
		}
		public ValueTask DisposeAsync()
		{
			IsConnected = false;
			Disposals++;
			return ValueTask.CompletedTask;
		}
	}
}
