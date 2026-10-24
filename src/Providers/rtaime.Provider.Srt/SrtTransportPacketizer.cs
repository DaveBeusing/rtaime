// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Provider.Srt;

/// <summary>Preserves encoder byte order across drains and emits complete MPEG-TS packets.</summary>
internal sealed class SrtTransportPacketizer
{
	internal const int TransportPacketSize = 188;
	internal const int MaximumMessageSize = 7 * TransportPacketSize;
	private readonly byte[] _residual = new byte[TransportPacketSize];
	private int _residualLength;
	private bool _failed;

	public async ValueTask<int> SendAsync(
		ISrtTransport transport,
		ReadOnlyMemory<byte> encoded,
		Action<int> messageSent,
		CancellationToken cancellationToken)
	{
		if (_failed)
			throw new InvalidOperationException("Failed transport packetization requires a new connection epoch.");
		cancellationToken.ThrowIfCancellationRequested();
		var messages = 0;
		try
		{
			if (_residualLength > 0)
			{
				var count = Math.Min(TransportPacketSize - _residualLength, encoded.Length);
				encoded.Span[..count].CopyTo(_residual.AsSpan(_residualLength));
				_residualLength += count;
				encoded = encoded[count..];
				if (_residualLength < TransportPacketSize)
					return 0;
				await SendMessageAsync(transport, _residual, messageSent, cancellationToken).ConfigureAwait(false);
				messages++;
				_residualLength = 0;
			}

			while (encoded.Length >= TransportPacketSize)
			{
				var count = Math.Min(MaximumMessageSize, encoded.Length / TransportPacketSize * TransportPacketSize);
				await SendMessageAsync(transport, encoded[..count], messageSent, cancellationToken).ConfigureAwait(false);
				messages++;
				encoded = encoded[count..];
			}

			encoded.Span.CopyTo(_residual);
			_residualLength = encoded.Length;
			return messages;
		}
		catch
		{
			// A live message is atomic. Never retry an uncertain prefix on this stream.
			_failed = true;
			throw;
		}
	}

	private static async ValueTask SendMessageAsync(
		ISrtTransport transport,
		ReadOnlyMemory<byte> message,
		Action<int> messageSent,
		CancellationToken cancellationToken)
	{
		for (var offset = 0; offset < message.Length; offset += TransportPacketSize)
			if (message.Span[offset] != 0x47)
				throw new InvalidDataException("Encoded output does not contain aligned MPEG-TS packets.");
		var sent = await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
		if (sent != message.Length)
			throw new IOException($"SRT live transport accepted {sent} of {message.Length} message bytes.");
		messageSent(sent);
	}

	public void Reset()
	{
		_residualLength = 0;
		_failed = false;
		Array.Clear(_residual);
	}
}
