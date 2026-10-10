// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

/// <summary>
/// Opt-in MXF OP1a worker writer backed by a specifically provisioned BMX raw2bmx executable.
/// No executable discovery or runtime download is performed. This adapter is not
/// available to the recording catalog until interoperability qualification succeeds.
/// </summary>
public sealed class BmxOp1aRecordingWriter :
    IProgramRecordingPayloadWriter,
    IConfigurableProgramRecordingWriter
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _raw2bmxPath;
    private readonly long? _quota;
    private readonly Dictionary<ulong, (IProgramRecordingPayloadLease Video, ReadOnlyMemory<byte> Audio)> _staged = new();
    private string? _configuredName;
    private string? _configuredDirectory;
    private string? _finalPath;
    private string? _partialPath;
    private string? _workingDirectory;
    private FileStream? _videoStream;
    private FileStream? _audioStream;
    private bool _open;
    private VideoFormat? _format;
    private ulong _frames;
    private ulong _audioFrames;
    private ulong? _nextAudioPosition;
    private long _payloadBytes;
    private byte[]? _videoBuffer;
    private byte[]? _audioBuffer;

    public BmxOp1aRecordingWriter(string rootDirectory, string raw2bmxPath, long? maximumPayloadBytes = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(raw2bmxPath))
            throw new ArgumentException("MXF recording requires a root and an explicitly configured BMX executable.");
        if (maximumPayloadBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        _root = Path.GetFullPath(rootDirectory);
        _raw2bmxPath = Path.GetFullPath(raw2bmxPath);
        _quota = maximumPayloadBytes;
    }

    public string? FinalPath => _finalPath;

    public string ConfigureTarget(string destinationDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory) || string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("MXF target directory and file name are required.");
        if (fileName != Path.GetFileName(fileName) || fileName is "." or "..")
            throw new ArgumentException("MXF file name cannot contain directories.", nameof(fileName));
        var name = Path.HasExtension(fileName) ? fileName : fileName + ".mxf";
        if (!name.EndsWith(".mxf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MXF recording requires the .mxf extension.", nameof(fileName));
        lock (_gate)
        {
            if (_open)
                throw new InvalidOperationException("Cannot change an open MXF recording target.");
            _configuredName = name;
            _configuredDirectory = Path.GetFullPath(destinationDirectory);
        }
        return name;
    }

    public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProfileId is { } id && id != ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId)
            throw new RecordingOutputUnavailableException($"BMX writer does not support profile '{id}'.");
        if (!File.Exists(_raw2bmxPath))
            throw new RecordingOutputUnavailableException("The explicitly configured BMX raw2bmx executable is unavailable.");
        lock (_gate)
        {
            if (_open)
                throw new InvalidOperationException("MXF writer already open.");
            var directory = _configuredDirectory ?? _root;
            Directory.CreateDirectory(directory);
            var name = _configuredName ?? request.Output.OutputId + ".mxf";
            _finalPath = Path.Combine(directory, name);
            _partialPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".partial.mxf");
            if (File.Exists(_finalPath) || File.Exists(_partialPath))
                throw new RecordingOutputUnavailableException("Recording output path is already reserved.");
            _workingDirectory = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".mxf-work");
            Directory.CreateDirectory(_workingDirectory);
            try
            {
                _videoStream = new FileStream(Path.Combine(_workingDirectory, "video.uyvy"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                _audioStream = new FileStream(Path.Combine(_workingDirectory, "audio.pcm"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                _open = true;
                _frames = _audioFrames = 0;
                _nextAudioPosition = null;
                _format = null;
                _payloadBytes = 0;
                _videoBuffer = _audioBuffer = null;
            }
            catch
            {
                _videoStream?.Dispose();
                _audioStream?.Dispose();
                Directory.Delete(_workingDirectory, recursive: true);
                throw;
            }
        }
        return ValueTask.CompletedTask;
    }

    public void StagePayload(ulong sequenceNumber, IProgramRecordingPayloadLease videoPayload, ReadOnlyMemory<byte> audioPayload)
    {
        ArgumentNullException.ThrowIfNull(videoPayload);
        lock (_gate)
        {
            EnsureOpen();
            if (videoPayload.Memory.IsEmpty || !_staged.TryAdd(sequenceNumber, (videoPayload, audioPayload)))
                throw new InvalidDataException("MXF video payload is empty or sequence is already staged.");
        }
    }

    public void DiscardPayload(ulong sequenceNumber)
    {
        lock (_gate)
            if (_staged.Remove(sequenceNumber, out var payload))
                payload.Video.Dispose();
    }

    public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();
        (IProgramRecordingPayloadLease Video, ReadOnlyMemory<byte> Audio) payload;
        lock (_gate)
        {
            EnsureOpen();
            if (!_staged.Remove(sample.SequenceNumber, out payload))
                throw new InvalidDataException("MXF Program payload not staged.");
        }
        try
        {
            var format = sample.Video.Surface.Format;
            if (format != VideoFormat.Hd1080p50Rgba8 && format != VideoFormat.Hd1080p59_94Rgba8)
                throw new InvalidDataException("Only progressive 1080p50 and 1080p60000/1001 RGBA8 are supported.");
            if (sample.Audio is null || sample.Audio.Format != AudioFormat.Stereo48kFloat32)
                throw new InvalidDataException("MXF recording requires committed 48 kHz stereo Float32 Program audio.");
            if (_format is { } first && first != format)
                throw new InvalidDataException("MXF video format changed while recording.");
            var videoSize = 1920 * 1080 * 4;
            var audioSize = checked((int)sample.Audio.Timing.SampleCount * 2 * sizeof(float));
            if (payload.Video.Memory.Length != videoSize || payload.Audio.Length != audioSize)
                throw new InvalidDataException("MXF staged Program payload dimensions do not match metadata.");
            if (_nextAudioPosition is { } expected && sample.Audio.Timing.SamplePosition != expected)
                throw new InvalidDataException("MXF audio positions are not contiguous.");
            var required = checked(_payloadBytes + (long)videoSize + audioSize);
            if (_quota is { } quota && required > quota)
                throw new IOException("MXF recording quota exceeded.");
            _videoBuffer ??= new byte[1920 * 1080 * 2];
            if (_audioBuffer is null || _audioBuffer.Length < audioSize / 2)
                _audioBuffer = new byte[audioSize / 2];
            MxfUncompressedEssenceConverter.ConvertVideo(payload.Video.Memory.Span, _videoBuffer, 1920, 1080);
            MxfUncompressedEssenceConverter.ConvertAudio(payload.Audio.Span, _audioBuffer.AsSpan(0, audioSize / 2));
            _videoStream!.Write(_videoBuffer);
            _audioStream!.Write(_audioBuffer.AsSpan(0, audioSize / 2));
            _format ??= format;
            _frames++;
            _audioFrames = checked(_audioFrames + sample.Audio.Timing.SampleCount);
            _nextAudioPosition = checked(sample.Audio.Timing.SamplePosition + sample.Audio.Timing.SampleCount);
            _payloadBytes = required;
            return ValueTask.CompletedTask;
        }
        finally
        {
            payload.Video.Dispose();
        }
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
    {
        string work;
        string partial;
        string final;
        string rate;
        lock (_gate)
        {
            EnsureOpen();
            if (_staged.Count != 0 || _frames == 0 || _audioFrames == 0 || _format is null)
                throw new InvalidDataException("MXF finalization requires complete video and audio samples.");
            rate = _format == VideoFormat.Hd1080p50Rgba8 ? "50" : "5994";
            work = _workingDirectory!;
            partial = _partialPath!;
            final = _finalPath!;
            _videoStream!.Flush(flushToDisk: true);
            _audioStream!.Flush(flushToDisk: true);
            _videoStream.Dispose();
            _audioStream.Dispose();
            _videoStream = _audioStream = null;
        }

        try
        {
            var start = new ProcessStartInfo(_raw2bmxPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = work
            };
            foreach (var arg in new[]
            {
                "-t", "op1a", "-f", rate, "--track-map", "0,1",
                "-o", partial, "--unc_1080p", Path.Combine(work, "video.uyvy"),
                "-s", "48000", "-q", "16", "--audio-chan", "2",
                "--pcm", Path.Combine(work, "audio.pcm")
            })
                start.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = start };
            if (!process.Start())
                throw new IOException("BMX process did not start.");
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            _ = await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"BMX OP1a muxing failed with exit code {process.ExitCode}: {stderr[..Math.Min(stderr.Length, 2048)]}");
            // Structural acceptance is only a preliminary guard. A complete semantic
            // decoder/third-party interoperability qualification is still required.
            MxfOp1aStructureProbe.Probe(partial);
            MxfVerifiedFilePublisher.Publish(partial, final, path => { _ = MxfOp1aStructureProbe.Probe(path); });
        }
        finally
        {
            lock (_gate)
                _open = false;
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            foreach (var payload in _staged.Values)
                payload.Video.Dispose();
            _staged.Clear();
            _videoStream?.Dispose();
            _audioStream?.Dispose();
            _videoStream = _audioStream = null;
            _open = false;
            if (_workingDirectory is { } work && Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private void EnsureOpen()
    {
        if (!_open)
            throw new InvalidOperationException("MXF writer is not open.");
    }
}
