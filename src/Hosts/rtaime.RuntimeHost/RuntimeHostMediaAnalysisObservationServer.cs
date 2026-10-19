// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Read-only, per-job frame observation plane. No management messages, native decoder
/// handles, source paths, commands or production state cross this endpoint.
/// </summary>
public sealed class RuntimeHostMediaAnalysisObservationServer : IAsyncDisposable
{
    private const int RequestLength = 36 + 64;
    private const int HeaderLength = 16;
    private readonly string _endpoint;
    private readonly Func<LocalMediaDeckRuntimeService?> _deckAccessor;
    private readonly RuntimePipeListener _listener;
    private readonly SemaphoreSlim _sessions = new(4, 4);

    public RuntimeHostMediaAnalysisObservationServer(
        string endpoint, Func<LocalMediaDeckRuntimeService?> deckAccessor,
        Func<NamedPipeServerStream>? pipeFactory = null, TimeSpan? sessionDrainTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Analysis observation endpoint is required.", nameof(endpoint));
        _endpoint = endpoint;
        _deckAccessor = deckAccessor ?? throw new ArgumentNullException(nameof(deckAccessor));
        _listener = new RuntimePipeListener(
            _endpoint, "media-analysis-observation",
            pipeFactory ?? (() => OperatorPipeServerFactory.Create(_endpoint, PipeDirection.InOut)),
            ProcessAsync, sessionDrainTimeout);
    }

    public string Endpoint => _endpoint;
    public bool Running => _listener.Running;
    public RuntimePipeListenerSnapshot Listener => _listener.Snapshot;
    public Task ListenerCompletion => _listener.Completion;
    public event Action<RuntimePipeListenerSnapshot, Exception> ListenerFaulted
    {
        add => _listener.TerminalFaulted += value;
        remove => _listener.TerminalFaulted -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _listener.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync().ConfigureAwait(false);
        _sessions.Dispose();
    }

    private async Task ProcessAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            if (!await _sessions.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var incoming = new byte[RequestLength];
                await pipe.ReadExactlyAsync(incoming, timeout.Token).ConfigureAwait(false);
                var jobId = Encoding.ASCII.GetString(incoming, 0, 36);
                var tokenText = Encoding.ASCII.GetString(incoming, 36, 64);
                byte[] token;
                try
                {
                    if (!Guid.TryParseExact(jobId, "D", out _) || tokenText.Length != 64)
                        return;
                    token = Convert.FromHexString(tokenText);
                }
                catch (FormatException) { return; }

                var accepted = _deckAccessor()?.TryReadAnalysisFrame(jobId, token,
                    out var width, out var height, out var rgba) == true;
                var header = new byte[HeaderLength];
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), accepted ? 1 : 0);
                if (accepted && rgba is not null &&
                    rgba.Length == checked(width * height * 4) &&
                    rgba.Length <= MediaAnalysisContract.MaximumFrameBytes)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), width);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), height);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), rgba.Length);
                    await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                    await pipe.WriteAsync(rgba, timeout.Token).ConfigureAwait(false);
                }
                else
                    await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException)
            {
                // The observation plane is disposable and must never affect Program.
            }
            finally { _sessions.Release(); }
        }
    }
}
