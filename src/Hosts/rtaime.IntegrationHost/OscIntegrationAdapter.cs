// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace rtaime.IntegrationHost;

internal sealed record OscMessage(string Address, IReadOnlyList<object> Arguments);

internal static class OscCodec
{
	public static bool TryDecode(ReadOnlySpan<byte> packet, out OscMessage? message)
	{
		message = null;
		try
		{
			var offset = 0;
			var address = ReadString(packet, ref offset);
			if (string.IsNullOrWhiteSpace(address) || !address.StartsWith("/", StringComparison.Ordinal))
				return false;
			var tags = ReadString(packet, ref offset);
			if (tags.Length == 0 || tags[0] != ',')
				return false;
			var arguments = new List<object>(Math.Min(tags.Length - 1, 16));
			if (tags.Length > 17) return false;
			foreach (var tag in tags.AsSpan(1))
			{
				switch (tag)
				{
					case 'i':
						EnsureRemaining(packet, offset, 4);
						arguments.Add(BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4)));
						offset += 4;
						break;
					case 'f':
						EnsureRemaining(packet, offset, 4);
						arguments.Add(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4))));
						offset += 4;
						break;
					case 's':
						arguments.Add(ReadString(packet, ref offset));
						break;
					case 'T':
						arguments.Add(true);
						break;
					case 'F':
						arguments.Add(false);
						break;
					default:
						return false;
				}
			}
			if (offset != packet.Length && packet[offset..].IndexOfAnyExcept((byte)0) >= 0)
				return false;
			message = new OscMessage(address, arguments.AsReadOnly());
			return true;
		}
		catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException or DecoderFallbackException)
		{
			return false;
		}
	}

	public static byte[] EncodeString(string address, string value)
	{
		if (string.IsNullOrWhiteSpace(address) || !address.StartsWith("/", StringComparison.Ordinal))
			throw new ArgumentException("OSC address must start with '/'.", nameof(address));
		using var stream = new MemoryStream();
		WriteString(stream, address);
		WriteString(stream, ",s");
		WriteString(stream, value ?? string.Empty);
		return stream.ToArray();
	}

	private static string ReadString(ReadOnlySpan<byte> packet, ref int offset)
	{
		if (offset >= packet.Length) throw new InvalidDataException("OSC string is missing.");
		var terminator = packet[offset..].IndexOf((byte)0);
		if (terminator < 0) throw new InvalidDataException("OSC string is unterminated.");
		var valueBytes = packet.Slice(offset, terminator);
		var value = new UTF8Encoding(false, true).GetString(valueBytes);
		offset = checked(offset + terminator + 1);
		offset = checked((offset + 3) & ~3);
		if (offset > packet.Length) throw new InvalidDataException("OSC string padding exceeds packet.");
		return value;
	}

	private static void WriteString(Stream stream, string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		stream.Write(bytes);
		stream.WriteByte(0);
		while ((stream.Length & 3) != 0) stream.WriteByte(0);
	}

	private static void EnsureRemaining(ReadOnlySpan<byte> packet, int offset, int required)
	{
		if (offset < 0 || required < 0 || offset > packet.Length - required)
			throw new InvalidDataException("OSC packet is truncated.");
	}
}

public sealed class OscIntegrationAdapter : IntegrationAdapterBase
{
	private readonly OscAdapterOptions _options;
	private readonly IReadOnlyList<IntegrationFeedbackMappingOptions> _feedbackMappings;
	private readonly HashSet<IPAddress> _allowlist;
	private CancellationTokenSource? _lifetime;
	private UdpClient? _receiver;
	private UdpClient? _sender;
	private Task? _receiveTask;
	private IntegrationInputSink? _input;

	public OscIntegrationAdapter(
		IntegrationAdapterOptions adapter,
		IReadOnlyList<IntegrationFeedbackMappingOptions> feedbackMappings)
		: base(adapter.Id, IntegrationAdapterKind.Osc, adapter.Required)
	{
		_options = adapter.Osc ?? throw new ArgumentException("OSC options are required.", nameof(adapter));
		_feedbackMappings = feedbackMappings ?? throw new ArgumentNullException(nameof(feedbackMappings));
		_allowlist = _options.SourceAllowlist.Select(IPAddress.Parse).ToHashSet();
	}

	public override ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (_receiver is not null) return ValueTask.CompletedTask;
		SetHealth(IntegrationAdapterState.Starting, "Binding OSC UDP receiver.");
		_input = input;
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_receiver = new UdpClient(AddressFamily.InterNetwork);
		_receiver.Client.Bind(new IPEndPoint(IPAddress.Parse(_options.ListenAddress), _options.ListenPort));
		if (!string.IsNullOrWhiteSpace(_options.FeedbackAddress))
			_sender = new UdpClient(AddressFamily.InterNetwork);
		_receiveTask = Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);
		SetHealth(IntegrationAdapterState.Healthy, $"OSC listening on {_options.ListenAddress}:{_options.ListenPort}.");
		return ValueTask.CompletedTask;
	}

	public override async ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken)
	{
		if (_sender is null || string.IsNullOrWhiteSpace(_options.FeedbackAddress) || _options.FeedbackPort is null)
			return;
		var endpoint = new IPEndPoint(IPAddress.Parse(_options.FeedbackAddress), _options.FeedbackPort.Value);
		foreach (var mapping in _feedbackMappings)
		{
			var packet = OscCodec.EncodeString(mapping.TargetKey, snapshot.Resolve(mapping));
			if (packet.Length > _options.MaxPacketBytes)
				continue;
			await _sender.SendAsync(packet, endpoint, cancellationToken).ConfigureAwait(false);
		}
	}

	public override async ValueTask StopAsync(CancellationToken cancellationToken)
	{
		var lifetime = Interlocked.Exchange(ref _lifetime, null);
		if (lifetime is null) return;
		lifetime.Cancel();
		_receiver?.Dispose();
		_sender?.Dispose();
		_receiver = null;
		_sender = null;
		if (_receiveTask is not null)
		{
			try { await _receiveTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { }
			catch (ObjectDisposedException) { }
		}
		_receiveTask = null;
		lifetime.Dispose();
		SetHealth(IntegrationAdapterState.Stopped, "OSC adapter is stopped.");
	}

	private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var packet = await _receiver!.ReceiveAsync(cancellationToken).ConfigureAwait(false);
				if (_allowlist.Count > 0 && !_allowlist.Contains(packet.RemoteEndPoint.Address))
					continue;
				if (packet.Buffer.Length > _options.MaxPacketBytes || !OscCodec.TryDecode(packet.Buffer, out var message) || message is null)
				{
					IncrementDroppedInputs("Malformed or oversized OSC packet was rejected.");
					continue;
				}

				var argument = message.Arguments.FirstOrDefault();
				var trigger = argument switch
				{
					bool boolean => new IntegrationTrigger(Id, message.Address, BooleanValue: boolean),
					int integer => new IntegrationTrigger(Id, message.Address, NumericValue: integer),
					float number => new IntegrationTrigger(Id, message.Address, NumericValue: number),
					string text => new IntegrationTrigger(Id, message.Address, TextValue: text),
					_ => new IntegrationTrigger(Id, message.Address)
				};
				if (!(_input?.Invoke(trigger) ?? false))
					IncrementDroppedInputs("OSC input could not enter the bounded gateway queue.");
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			SetHealth(IntegrationAdapterState.Failed, $"OSC receive loop failed: {exception.Message}");
		}
	}
}
