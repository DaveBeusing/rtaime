using System.Globalization;
using System.Text;

namespace rtaime.Recording;

/// <summary>
/// Local, orderly-finalized recording artifact writer for the V1 architectural proof.
/// It persists Program sample identity/timing and opaque media-handle references, not decoded bulk media.
/// A future qualified encoder backend can implement IProgramRecordingWriter without changing ProgramRecorder.
/// </summary>
public sealed class LocalRecordingManifestWriter : IProgramRecordingWriter
{
    private readonly string _rootDirectory;
    private FileStream? _stream;
    private StreamWriter? _writer;
    private string? _partialPath;
    private string? _finalPath;
    private bool _opened;

    public LocalRecordingManifestWriter(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Recording root directory is required.", nameof(rootDirectory));

        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string? FinalPath => _finalPath;

    public async ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_opened)
            throw new InvalidOperationException("Recording writer is already open.");

        Directory.CreateDirectory(_rootDirectory);

        var stem = request.Output.OutputId.ToString();
        _partialPath = Path.Combine(_rootDirectory, stem + ".partial");
        _finalPath = Path.Combine(_rootDirectory, stem + ".rtaime-recording");

        if (File.Exists(_partialPath) || File.Exists(_finalPath))
            throw new RecordingOutputUnavailableException("Recording output identity already exists in the target directory.");

        try
        {
            _stream = new FileStream(
                _partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
            _opened = true;

            await WriteLineAsync(
                $"BEGIN|version={request.Version}|session={request.SessionId}|output={request.Output.OutputId}|sink={request.Output.ProgramSinkId}|name={Escape(request.Output.Name)}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await DisposeOpenHandlesAsync().ConfigureAwait(false);
            throw new RecordingOutputUnavailableException($"Recording output could not be opened: {exception.Message}");
        }
    }

    public async ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(sample);

        var audio = sample.Audio is null
            ? "audio=none"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"audioStream={sample.Audio.StreamId}|audioPosition={sample.Audio.Timing.SamplePosition}|audioSamples={sample.Audio.Timing.SampleCount}");

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"SAMPLE|sequence={sample.Video.Timing.SequenceNumber}|source={sample.Video.SourceId}|surface={sample.Video.Surface.SurfaceId}|pts={sample.Video.Timing.PresentationTimestamp}|{audio}");
        await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
    {
        EnsureOpen();
        await WriteLineAsync("END|state=finalized", cancellationToken).ConfigureAwait(false);
        await _writer!.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
        await DisposeOpenHandlesAsync().ConfigureAwait(false);

        if (_partialPath is null || _finalPath is null)
            throw new InvalidOperationException("Recording paths were not initialized.");

        File.Move(_partialPath, _finalPath, overwrite: false);
        _opened = false;
    }

    public async ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await DisposeOpenHandlesAsync().ConfigureAwait(false);
        _opened = false;
    }

    private async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _writer!.WriteLineAsync(line).ConfigureAwait(false);
    }

    private async ValueTask DisposeOpenHandlesAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private void EnsureOpen()
    {
        if (!_opened || _writer is null || _stream is null)
            throw new InvalidOperationException("Recording writer is not open.");
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
