// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO.Pipes;
using System.Runtime.CompilerServices;
using rtaime.Media.Contracts;

namespace rtaime.Client;

/// <summary>
/// Loss-tolerant visual monitoring transport. This transport is intentionally independent from
/// <see cref="IOperatorControlTransport"/> and never carries commands or production authority.
/// </summary>
public sealed class NamedPipeOperatorMonitoringTransport
{
	private readonly string _endpoint;
	private readonly TimeSpan _connectTimeout;
	private readonly TimeSpan _reconnectDelay;

	public NamedPipeOperatorMonitoringTransport(
		string endpoint,
		TimeSpan? connectTimeout = null,
		TimeSpan? reconnectDelay = null)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Monitoring endpoint is required.", nameof(endpoint));
		_endpoint = endpoint.Trim();
		_connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
		_reconnectDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(250);
		if (_connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
		if (_reconnectDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
	}

	public string Endpoint => _endpoint;

	public async IAsyncEnumerable<MonitoringFrame> ReadFramesAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await using var pipe = new NamedPipeClientStream(
				".",
				_endpoint,
				PipeDirection.In,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

			if (!await TryConnectAsync(pipe, cancellationToken).ConfigureAwait(false))
			{
				if (cancellationToken.IsCancellationRequested) yield break;
				await DelayBeforeReconnectAsync(cancellationToken).ConfigureAwait(false);
				continue;
			}

			var header = new byte[MonitoringFrameWire.HeaderSize];
			while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
			{
				MonitoringFrame? frame;
				try
				{
					await pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
					var (descriptor, payloadLength) = MonitoringFrameWire.ReadHeader(header);
					var payload = new byte[payloadLength];
					await pipe.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
					frame = new MonitoringFrame(descriptor, payload);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					yield break;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
				{
					break;
				}

				yield return frame;
			}

			await DelayBeforeReconnectAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<bool> TryConnectAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
	{
		try
		{
			using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			connect.CancelAfter(_connectTimeout);
			await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
			return pipe.IsConnected;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private Task DelayBeforeReconnectAsync(CancellationToken cancellationToken) =>
		_reconnectDelay > TimeSpan.Zero
			? Task.Delay(_reconnectDelay, cancellationToken)
			: Task.CompletedTask;
}
