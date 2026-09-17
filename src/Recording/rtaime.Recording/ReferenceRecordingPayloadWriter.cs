// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

/// <summary>
/// Optional in-process recording capability used by reference backends that can persist actual media payload bytes
/// while the stable RecordingProgramSample continues to carry descriptor-level media contracts only.
/// </summary>
public interface IProgramRecordingPayloadWriter : IProgramRecordingWriter
{
	void StagePayload(ulong sequenceNumber, ReadOnlyMemory<byte> videoPayload, ReadOnlyMemory<byte> audioPayload);
	void DiscardPayload(ulong sequenceNumber);
}

public sealed record ReferenceRecordingPayloadSample(
	ulong SequenceNumber,
	string SourceId,
	VideoFormat VideoFormat,
	long VideoPresentationTimestamp,
	Timebase VideoTimebase,
	byte[] VideoPayload,
	string? AudioStreamId,
	AudioFormat? AudioFormat,
	ulong? AudioSamplePosition,
	uint? AudioSampleCount,
	long? AudioPresentationTimestamp,
	Timebase? AudioTimebase,
	byte[] AudioPayload);

public sealed record ReferenceRecordingPayloadArtifact(
	CompatibilityVersion RecordingVersion,
	string SessionId,
	string OutputId,
	string ProgramSinkId,
	string Name,
	IReadOnlyList<ReferenceRecordingPayloadSample> Samples,
	ulong VideoSampleCount,
	ulong AudioSampleCount,
	string PayloadSha256);

/// <summary>
/// Deterministic, software-only V1 reference container. It is deliberately uncompressed and is evidence for the
/// Recording payload path only; it is not a professional codec/container qualification.
/// </summary>
public sealed class ReferenceRecordingPayloadWriter : IProgramRecordingPayloadWriter
{
	private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RTAIME-REFREC-1\n");
	private const byte SampleMarker = 1;
	private const byte FooterMarker = byte.MaxValue;

	private readonly object _gate = new();
	private readonly string _rootDirectory;
	private readonly long? _maximumPayloadBytes;
	private readonly Dictionary<ulong, StagedPayload> _stagedPayloads = new();

	private FileStream? _stream;
	private BinaryWriter? _writer;
	private IncrementalHash? _payloadHash;
	private string? _partialPath;
	private string? _finalPath;
	private bool _opened;
	private long _payloadBytesWritten;
	private ulong _videoSamples;
	private ulong _audioSamples;

	public ReferenceRecordingPayloadWriter(string rootDirectory, long? maximumPayloadBytes = null)
	{
		if (string.IsNullOrWhiteSpace(rootDirectory))
			throw new ArgumentException("Recording root directory is required.", nameof(rootDirectory));
		if (maximumPayloadBytes is <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes), "Recording payload quota must be greater than zero when specified.");

		_rootDirectory = Path.GetFullPath(rootDirectory);
		_maximumPayloadBytes = maximumPayloadBytes;
	}

	public string? PartialPath => _partialPath;
	public string? FinalPath => _finalPath;

	public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (_opened)
				throw new InvalidOperationException("Reference recording writer is already open.");
			_stagedPayloads.Clear();
			_payloadBytesWritten = 0;
			_videoSamples = 0;
			_audioSamples = 0;
		}

		Directory.CreateDirectory(_rootDirectory);
		var stem = request.Output.OutputId.ToString();
		var partialPath = Path.Combine(_rootDirectory, stem + ".partial");
		var finalPath = Path.Combine(_rootDirectory, stem + ".rtaime-recording");
		if (File.Exists(partialPath) || File.Exists(finalPath))
			throw new RecordingOutputUnavailableException("Recording output identity already exists in the target directory.");

		try
		{
			var stream = new FileStream(
				partialPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.Read,
				bufferSize: 64 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
			var writer = new BinaryWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
			var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

			stream.Write(Magic);
			writer.Write(request.Version.Major);
			writer.Write(request.Version.Minor);
			writer.Write(request.SessionId.ToString());
			writer.Write(request.Output.OutputId.ToString());
			writer.Write(request.Output.ProgramSinkId.ToString());
			writer.Write(request.Output.Name);
			writer.Flush();

			lock (_gate)
			{
				_stream = stream;
				_writer = writer;
				_payloadHash = payloadHash;
				_partialPath = partialPath;
				_finalPath = finalPath;
				_opened = true;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			DisposeOpenHandles();
			throw new RecordingOutputUnavailableException($"Recording output could not be opened: {exception.Message}");
		}

		return ValueTask.CompletedTask;
	}

	public void StagePayload(ulong sequenceNumber, ReadOnlyMemory<byte> videoPayload, ReadOnlyMemory<byte> audioPayload)
	{
		if (videoPayload.IsEmpty)
			throw new ArgumentException("Reference recording requires a non-empty video payload.", nameof(videoPayload));

		lock (_gate)
		{
			EnsureOpenUnsafe();
			if (!_stagedPayloads.TryAdd(sequenceNumber, new StagedPayload(videoPayload, audioPayload)))
				throw new InvalidOperationException($"Recording payload for sequence '{sequenceNumber}' is already staged.");
		}
	}

	public void DiscardPayload(ulong sequenceNumber)
	{
		lock (_gate)
			_stagedPayloads.Remove(sequenceNumber);
	}

	public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(sample);
		cancellationToken.ThrowIfCancellationRequested();

		BinaryWriter writer;
		IncrementalHash payloadHash;
		StagedPayload payload;
		lock (_gate)
		{
			EnsureOpenUnsafe();
			writer = _writer!;
			payloadHash = _payloadHash!;
			if (!_stagedPayloads.Remove(sample.SequenceNumber, out payload))
				throw new InvalidDataException($"Reference recording payload for sequence '{sample.SequenceNumber}' was not staged.");
		}

		ValidatePayload(sample, payload);
		var payloadBytes = checked((long)payload.Video.Length + payload.Audio.Length);
		lock (_gate)
		{
			var nextTotal = checked(_payloadBytesWritten + payloadBytes);
			if (_maximumPayloadBytes is { } maximum && nextTotal > maximum)
				throw new IOException($"Reference recording payload quota of {maximum} bytes was exhausted.");
			_payloadBytesWritten = nextTotal;
		}

		WriteSample(writer, sample, payload);
		payloadHash.AppendData(payload.Video.Span);
		if (!payload.Audio.IsEmpty)
			payloadHash.AppendData(payload.Audio.Span);

		lock (_gate)
		{
			_videoSamples++;
			if (sample.Audio is not null)
				_audioSamples++;
		}

		return ValueTask.CompletedTask;
	}

	public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		BinaryWriter writer;
		FileStream stream;
		IncrementalHash payloadHash;
		string partialPath;
		string finalPath;
		ulong videoSamples;
		ulong audioSamples;
		lock (_gate)
		{
			EnsureOpenUnsafe();
			if (_stagedPayloads.Count != 0)
				throw new InvalidDataException("Reference recording cannot finalize while staged media payloads remain unwritten.");
			writer = _writer!;
			stream = _stream!;
			payloadHash = _payloadHash!;
			partialPath = _partialPath!;
			finalPath = _finalPath!;
			videoSamples = _videoSamples;
			audioSamples = _audioSamples;
		}

		var hash = payloadHash.GetHashAndReset();
		writer.Write(FooterMarker);
		writer.Write(videoSamples);
		writer.Write(audioSamples);
		writer.Write(hash.Length);
		writer.Write(hash);
		writer.Flush();
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		DisposeOpenHandles();

		File.Move(partialPath, finalPath, overwrite: false);
		lock (_gate)
			_opened = false;
	}

	public ValueTask AbortAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		string? partialPath;
		lock (_gate)
		{
			partialPath = _partialPath;
			_stagedPayloads.Clear();
			_opened = false;
		}

		DisposeOpenHandles();
		if (partialPath is not null && File.Exists(partialPath))
		{
			try
			{
				File.Delete(partialPath);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
		return ValueTask.CompletedTask;
	}

	private static void ValidatePayload(RecordingProgramSample sample, StagedPayload payload)
	{
		var format = sample.Video.Surface.Format;
		if (format.PixelFormat != PixelFormat.Rgba8)
			throw new InvalidDataException($"Reference recording does not support pixel format '{format.PixelFormat}'.");
		var expectedVideoLength = checked((int)((long)format.Width * format.Height * 4));
		if (payload.Video.Length != expectedVideoLength)
			throw new InvalidDataException($"Video payload length {payload.Video.Length} does not match expected RGBA8 length {expectedVideoLength}.");

		if (sample.Audio is null)
		{
			if (!payload.Audio.IsEmpty)
				throw new InvalidDataException("Audio payload was staged for a Program sample without an audio descriptor.");
			return;
		}

		var bytesPerSample = sample.Audio.Format.SampleFormat switch
		{
			AudioSampleFormat.PcmS16 => 2,
			AudioSampleFormat.Float32 => 4,
			_ => throw new InvalidDataException($"Unsupported audio sample format '{sample.Audio.Format.SampleFormat}'.")
		};
		var expectedAudioLength = checked((int)((long)sample.Audio.Timing.SampleCount * sample.Audio.Format.ChannelCount * bytesPerSample));
		if (payload.Audio.Length != expectedAudioLength)
			throw new InvalidDataException($"Audio payload length {payload.Audio.Length} does not match expected length {expectedAudioLength}.");
	}

	private static void WriteSample(BinaryWriter writer, RecordingProgramSample sample, StagedPayload payload)
	{
		var video = sample.Video;
		var format = video.Surface.Format;
		writer.Write(SampleMarker);
		writer.Write(sample.SequenceNumber);
		writer.Write(video.SourceId.ToString());
		writer.Write(format.Width);
		writer.Write(format.Height);
		writer.Write(format.FrameRate.Numerator);
		writer.Write(format.FrameRate.Denominator);
		writer.Write((int)format.PixelFormat);
		writer.Write((int)format.ScanMode);
		writer.Write(video.Timing.PresentationTimestamp);
		writer.Write(video.Timing.Timebase.Numerator);
		writer.Write(video.Timing.Timebase.Denominator);
		writer.Write(payload.Video.Length);
		writer.Write(payload.Video.Span);

		writer.Write(sample.Audio is not null);
		if (sample.Audio is null)
			return;

		var audio = sample.Audio;
		writer.Write(audio.StreamId.ToString());
		writer.Write(audio.Format.SampleRate);
		writer.Write((int)audio.Format.ChannelLayout);
		writer.Write((int)audio.Format.SampleFormat);
		writer.Write(audio.Format.ChannelCount);
		writer.Write(audio.Timing.SamplePosition);
		writer.Write(audio.Timing.SampleCount);
		writer.Write(audio.Timing.PresentationTimestamp);
		writer.Write(audio.Timing.Timebase.Numerator);
		writer.Write(audio.Timing.Timebase.Denominator);
		writer.Write(payload.Audio.Length);
		writer.Write(payload.Audio.Span);
	}

	private void EnsureOpenUnsafe()
	{
		if (!_opened || _stream is null || _writer is null || _payloadHash is null || _partialPath is null || _finalPath is null)
			throw new InvalidOperationException("Reference recording writer is not open.");
	}

	private void DisposeOpenHandles()
	{
		BinaryWriter? writer;
		FileStream? stream;
		IncrementalHash? payloadHash;
		lock (_gate)
		{
			writer = _writer;
			stream = _stream;
			payloadHash = _payloadHash;
			_writer = null;
			_stream = null;
			_payloadHash = null;
		}

		writer?.Dispose();
		stream?.Dispose();
		payloadHash?.Dispose();
	}

	private readonly record struct StagedPayload(ReadOnlyMemory<byte> Video, ReadOnlyMemory<byte> Audio);

	internal static ReadOnlySpan<byte> FileMagic => Magic;
	internal const byte FileSampleMarker = SampleMarker;
	internal const byte FileFooterMarker = FooterMarker;
}

public static class ReferenceRecordingPayloadReader
{
	public static ReferenceRecordingPayloadArtifact Read(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("Recording artifact path is required.", nameof(path));

		using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
		using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
		var magic = reader.ReadBytes(ReferenceRecordingPayloadWriter.FileMagic.Length);
		if (!magic.AsSpan().SequenceEqual(ReferenceRecordingPayloadWriter.FileMagic))
			throw new InvalidDataException("Reference recording magic/version header is invalid.");

		var recordingVersion = new CompatibilityVersion(reader.ReadUInt32(), reader.ReadUInt32());
		var sessionId = reader.ReadString();
		var outputId = reader.ReadString();
		var programSinkId = reader.ReadString();
		var name = reader.ReadString();
		var samples = new List<ReferenceRecordingPayloadSample>();
		using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		ulong footerVideoSamples = 0;
		ulong footerAudioSamples = 0;
		byte[]? footerHash = null;
		while (stream.Position < stream.Length)
		{
			var marker = reader.ReadByte();
			if (marker == ReferenceRecordingPayloadWriter.FileFooterMarker)
			{
				footerVideoSamples = reader.ReadUInt64();
				footerAudioSamples = reader.ReadUInt64();
				var hashLength = reader.ReadInt32();
				if (hashLength <= 0 || hashLength > 128)
					throw new InvalidDataException("Reference recording footer hash length is invalid.");
				footerHash = ReadExact(reader, hashLength, "footer hash");
				break;
			}

			if (marker != ReferenceRecordingPayloadWriter.FileSampleMarker)
				throw new InvalidDataException($"Unknown reference recording record marker '{marker}'.");

			var sequence = reader.ReadUInt64();
			var sourceId = reader.ReadString();
			var videoFormat = new VideoFormat(
				reader.ReadUInt32(),
				reader.ReadUInt32(),
				new FrameRate(reader.ReadInt64(), reader.ReadInt64()),
				(PixelFormat)reader.ReadInt32(),
				(ScanMode)reader.ReadInt32());
			var videoPresentationTimestamp = reader.ReadInt64();
			var videoTimebase = new Timebase(reader.ReadInt64(), reader.ReadInt64());
			var videoLength = reader.ReadInt32();
			if (videoLength <= 0)
				throw new InvalidDataException("Reference recording video payload length is invalid.");
			var videoPayload = ReadExact(reader, videoLength, "video payload");
			payloadHash.AppendData(videoPayload);

			string? audioStreamId = null;
			AudioFormat? audioFormat = null;
			ulong? audioSamplePosition = null;
			uint? audioSampleCount = null;
			long? audioPresentationTimestamp = null;
			Timebase? audioTimebase = null;
			byte[] audioPayload = Array.Empty<byte>();
			if (reader.ReadBoolean())
			{
				audioStreamId = reader.ReadString();
				audioFormat = new AudioFormat(
					reader.ReadUInt32(),
					(AudioChannelLayout)reader.ReadInt32(),
					(AudioSampleFormat)reader.ReadInt32(),
					reader.ReadUInt32());
				audioSamplePosition = reader.ReadUInt64();
				audioSampleCount = reader.ReadUInt32();
				audioPresentationTimestamp = reader.ReadInt64();
				audioTimebase = new Timebase(reader.ReadInt64(), reader.ReadInt64());
				var audioLength = reader.ReadInt32();
				if (audioLength <= 0)
					throw new InvalidDataException("Reference recording audio payload length is invalid.");
				audioPayload = ReadExact(reader, audioLength, "audio payload");
				payloadHash.AppendData(audioPayload);
			}

			samples.Add(new ReferenceRecordingPayloadSample(
				sequence,
				sourceId,
				videoFormat,
				videoPresentationTimestamp,
				videoTimebase,
				videoPayload,
				audioStreamId,
				audioFormat,
				audioSamplePosition,
				audioSampleCount,
				audioPresentationTimestamp,
				audioTimebase,
				audioPayload));
		}

		if (footerHash is null)
			throw new InvalidDataException("Reference recording is incomplete because the final footer is missing.");
		if (stream.Position != stream.Length)
			throw new InvalidDataException("Reference recording contains trailing data after the final footer.");
		if (footerVideoSamples != checked((ulong)samples.Count))
			throw new InvalidDataException("Reference recording footer video count does not match the parsed samples.");
		var parsedAudioSamples = checked((ulong)samples.Count(sample => sample.AudioFormat is not null));
		if (footerAudioSamples != parsedAudioSamples)
			throw new InvalidDataException("Reference recording footer audio count does not match the parsed samples.");

		var calculatedHash = payloadHash.GetHashAndReset();
		if (!CryptographicOperations.FixedTimeEquals(calculatedHash, footerHash))
			throw new InvalidDataException("Reference recording payload checksum validation failed.");

		return new ReferenceRecordingPayloadArtifact(
			recordingVersion,
			sessionId,
			outputId,
			programSinkId,
			name,
			samples.AsReadOnly(),
			footerVideoSamples,
			footerAudioSamples,
			Convert.ToHexString(calculatedHash));
	}

	private static byte[] ReadExact(BinaryReader reader, int length, string field)
	{
		var bytes = reader.ReadBytes(length);
		if (bytes.Length != length)
			throw new InvalidDataException($"Reference recording ended while reading {field}.");
		return bytes;
	}
}
