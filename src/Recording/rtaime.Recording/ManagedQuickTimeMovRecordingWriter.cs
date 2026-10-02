// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

/// <summary>
/// Managed QuickTime/MOV writer for committed Program video/audio.
/// It stores uncompressed 8-bit YUV 4:2:2 ('2vuy') video and stereo PCM16 ('sowt') audio.
/// All file I/O and pixel/audio conversion execute on the asynchronous ProgramRecorder worker.
/// </summary>
public sealed class ManagedQuickTimeMovRecordingWriter :
	IProgramRecordingPayloadWriter,
	IConfigurableProgramRecordingWriter
{
	private const uint MovieTimescale = 60_000;
	private const uint AudioTimescale = 48_000;
	private const uint AudioBytesPerSampleFrame = 4;
	private readonly object _gate = new();
	private readonly string _defaultRootDirectory;
	private readonly long? _maximumPayloadBytes;
	private readonly Dictionary<ulong, StagedPayload> _stagedPayloads = new();
	private readonly List<ulong> _videoPresentationTimes = new();
	private readonly List<ulong> _videoChunkOffsets = new();
	private readonly List<ulong> _audioChunkOffsets = new();
	private readonly List<uint> _audioSamplesPerChunk = new();

	private string? _configuredRootDirectory;
	private string? _configuredFileName;
	private string? _partialPath;
	private string? _finalPath;
	private string? _reservationPath;
	private FileStream? _reservation;
	private FileStream? _stream;
	private long _mdatHeaderOffset;
	private bool _opened;
	private VideoFormat? _videoFormat;
	private AudioFormat? _audioFormat;
	private long? _videoOrigin;
	private long? _audioOrigin;
	private ulong _videoPresentationDelayMovieTicks;
	private ulong _audioPresentationDelayMovieTicks;
	private long _lastVideoPresentationTime = -1;
	private ulong? _expectedAudioSamplePosition;
	private ulong _audioSampleFrames;
	private byte[]? _uyvyBuffer;
	private byte[]? _pcm16Buffer;
	private long _payloadBytesWritten;

	public ManagedQuickTimeMovRecordingWriter(string rootDirectory, long? maximumPayloadBytes = null)
	{
		if (string.IsNullOrWhiteSpace(rootDirectory))
			throw new ArgumentException("Recording root directory is required.", nameof(rootDirectory));
		if (maximumPayloadBytes is <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes), "Recording payload quota must be greater than zero when specified.");

		_defaultRootDirectory = Path.GetFullPath(rootDirectory);
		_maximumPayloadBytes = maximumPayloadBytes;
	}

	public string? FinalPath => _finalPath;

	public string ConfigureTarget(string destinationDirectory, string fileName)
	{
		if (string.IsNullOrWhiteSpace(destinationDirectory))
			throw new ArgumentException("Recording destination directory is required.", nameof(destinationDirectory));
		if (string.IsNullOrWhiteSpace(fileName))
			throw new ArgumentException("Recording file name is required.", nameof(fileName));

		var normalized = NormalizeFileName(fileName);
		lock (_gate)
		{
			if (_opened)
				throw new InvalidOperationException("Recording target cannot change while the writer is open.");

			_configuredRootDirectory = Path.GetFullPath(destinationDirectory.Trim());
			_configuredFileName = normalized;
		}

		return normalized;
	}

	public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		cancellationToken.ThrowIfCancellationRequested();
		if (request.ProfileId is { } requestedProfile && requestedProfile != ProfessionalRecordingFormats.Mov2VuyPcmProfileId)
			throw new RecordingOutputUnavailableException($"QuickTime writer does not support recording profile '{requestedProfile}'.");

		string rootDirectory;
		string fileName;
		lock (_gate)
		{
			if (_opened)
				throw new InvalidOperationException("QuickTime recording writer is already open.");

			rootDirectory = _configuredRootDirectory ?? _defaultRootDirectory;
			fileName = _configuredFileName ?? request.Output.OutputId + ProfessionalRecordingFormats.Mov2VuyPcm.FileExtension;
		}

		Directory.CreateDirectory(rootDirectory);
		var finalPath = Path.Combine(rootDirectory, fileName);
		var partialPath = BuildPartialPath(finalPath);
		var reservationPath = finalPath + ".lock";
		if (File.Exists(finalPath) || File.Exists(partialPath) || File.Exists(reservationPath))
			throw new RecordingOutputUnavailableException("Recording output identity already exists in the target directory.");

		FileStream? reservation = null;
		FileStream? stream = null;
		try
		{
			reservation = new FileStream(
				reservationPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 1,
				FileOptions.WriteThrough);
			stream = new FileStream(
				partialPath,
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.Read,
				bufferSize: 1024 * 1024,
				FileOptions.SequentialScan | FileOptions.WriteThrough);

			WriteAtom(stream, BuildFileTypeAtom());
			var mdatHeaderOffset = stream.Position;
			WriteUInt32(stream, 1);
			WriteFourCc(stream, "mdat");
			WriteUInt64(stream, 0);

			lock (_gate)
			{
				ReleaseStagedPayloadsUnsafe();
				_videoPresentationTimes.Clear();
				_videoChunkOffsets.Clear();
				_audioChunkOffsets.Clear();
				_audioSamplesPerChunk.Clear();
				_partialPath = partialPath;
				_finalPath = finalPath;
				_reservationPath = reservationPath;
				_reservation = reservation;
				_stream = stream;
				_mdatHeaderOffset = mdatHeaderOffset;
				_videoFormat = null;
				_audioFormat = null;
				_videoOrigin = null;
				_audioOrigin = null;
				_videoPresentationDelayMovieTicks = 0;
				_audioPresentationDelayMovieTicks = 0;
				_lastVideoPresentationTime = -1;
				_expectedAudioSamplePosition = null;
				_audioSampleFrames = 0;
				_uyvyBuffer = null;
				_pcm16Buffer = null;
				_payloadBytesWritten = 0;
				_opened = true;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			stream?.Dispose();
			reservation?.Dispose();
			TryDelete(partialPath);
			TryDelete(reservationPath);
			throw new RecordingOutputUnavailableException($"Recording output could not be opened: {exception.Message}");
		}

		return ValueTask.CompletedTask;
	}

	public void StagePayload(
		ulong sequenceNumber,
		IProgramRecordingPayloadLease videoPayload,
		ReadOnlyMemory<byte> audioPayload)
	{
		ArgumentNullException.ThrowIfNull(videoPayload);
		if (videoPayload.Memory.IsEmpty)
			throw new ArgumentException("MOV recording requires a non-empty Program video payload.", nameof(videoPayload));

		lock (_gate)
		{
			EnsureOpenUnsafe();
			if (!_stagedPayloads.TryAdd(sequenceNumber, new StagedPayload(videoPayload, audioPayload)))
				throw new InvalidOperationException($"Recording payload for sequence '{sequenceNumber}' is already staged.");
		}
	}

	public void DiscardPayload(ulong sequenceNumber)
	{
		IProgramRecordingPayloadLease? lease = null;
		lock (_gate)
		{
			if (_stagedPayloads.Remove(sequenceNumber, out var payload))
				lease = payload.VideoLease;
		}
		lease?.Dispose();
	}

	public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(sample);
		cancellationToken.ThrowIfCancellationRequested();

		StagedPayload payload;
		lock (_gate)
		{
			EnsureOpenUnsafe();
			if (!_stagedPayloads.Remove(sample.SequenceNumber, out var stagedPayload) || stagedPayload is null)
				throw new InvalidDataException($"MOV recording payload for sequence '{sample.SequenceNumber}' was not staged.");
			payload = stagedPayload;
		}

		try
		{
			ValidateSample(sample, payload);
			var payloadBytes = checked((long)payload.Video.Length + payload.Audio.Length);
			lock (_gate)
			{
				var nextTotal = checked(_payloadBytesWritten + payloadBytes);
				if (_maximumPayloadBytes is { } maximum && nextTotal > maximum)
					throw new IOException($"MOV recording payload quota of {maximum} bytes was exhausted.");
				_payloadBytesWritten = nextTotal;
			}

			WriteSample(sample, payload.Video.Span, payload.Audio.Span);
			return ValueTask.CompletedTask;
		}
		finally
		{
			payload.VideoLease.Dispose();
		}
	}

	public ValueTask FinalizeAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		string partialPath;
		string finalPath;
		FileStream stream;
		lock (_gate)
		{
			EnsureOpenUnsafe();
			if (_stagedPayloads.Count != 0)
				throw new InvalidDataException("MOV recording cannot finalize while staged payloads remain unwritten.");
			if (_videoPresentationTimes.Count == 0 || _audioSampleFrames == 0)
				throw new InvalidDataException("MOV recording cannot finalize before at least one complete A/V sample was written.");
			stream = _stream ?? throw new InvalidOperationException("MOV recording stream is unavailable.");
			partialPath = _partialPath!;
			finalPath = _finalPath!;
		}

		try
		{
			var mdatEnd = stream.Position;
			stream.Position = checked(_mdatHeaderOffset + 8);
			WriteUInt64(stream, checked((ulong)(mdatEnd - _mdatHeaderOffset)));
			stream.Position = mdatEnd;
			WriteAtom(stream, BuildMovieAtom());
			stream.Flush(flushToDisk: true);
			stream.Dispose();

			lock (_gate)
				_stream = null;

			var probe = QuickTimeMovProbe.Probe(partialPath);
			ValidateProbe(probe);
			File.Move(partialPath, finalPath, overwrite: false);
			ReleaseReservation(deleteReservation: true);

			lock (_gate)
				_opened = false;
		}
		catch
		{
			lock (_gate)
			{
				_stream?.Dispose();
				_stream = null;
				_opened = false;
			}
			TryDelete(partialPath);
			ReleaseReservation(deleteReservation: true);
			throw;
		}

		return ValueTask.CompletedTask;
	}

	public ValueTask AbortAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		string? partialPath;
		lock (_gate)
		{
			partialPath = _partialPath;
			ReleaseStagedPayloadsUnsafe();
			_stream?.Dispose();
			_stream = null;
			_opened = false;
		}

		if (partialPath is not null)
			TryDelete(partialPath);
		ReleaseReservation(deleteReservation: true);
		return ValueTask.CompletedTask;
	}

	internal static void ConvertRgbaTo2Vuy(ReadOnlySpan<byte> rgba, Span<byte> uyvy, int width, int height)
	{
		if (width <= 0 || height <= 0 || (width & 1) != 0)
			throw new ArgumentOutOfRangeException(nameof(width), "2vuy conversion requires positive even width.");
		if (rgba.Length != checked(width * height * 4))
			throw new ArgumentException("RGBA payload length does not match dimensions.", nameof(rgba));
		if (uyvy.Length != checked(width * height * 2))
			throw new ArgumentException("2vuy payload length does not match dimensions.", nameof(uyvy));

		var destination = 0;
		for (var pixel = 0; pixel < width * height; pixel += 2)
		{
			var first = pixel * 4;
			var second = first + 4;
			var r0 = rgba[first];
			var g0 = rgba[first + 1];
			var b0 = rgba[first + 2];
			var r1 = rgba[second];
			var g1 = rgba[second + 1];
			var b1 = rgba[second + 2];

			var y0 = ToLimitedY(r0, g0, b0);
			var y1 = ToLimitedY(r1, g1, b1);
			var cb0 = ToLimitedCb(r0, g0, b0);
			var cb1 = ToLimitedCb(r1, g1, b1);
			var cr0 = ToLimitedCr(r0, g0, b0);
			var cr1 = ToLimitedCr(r1, g1, b1);

			uyvy[destination++] = checked((byte)((cb0 + cb1 + 1) / 2));
			uyvy[destination++] = checked((byte)y0);
			uyvy[destination++] = checked((byte)((cr0 + cr1 + 1) / 2));
			uyvy[destination++] = checked((byte)y1);
		}
	}

	private static int ToLimitedY(byte red, byte green, byte blue) =>
		Math.Clamp(((47 * red + 157 * green + 16 * blue + 128) >> 8) + 16, 16, 235);

	private static int ToLimitedCb(byte red, byte green, byte blue) =>
		Math.Clamp(((-26 * red - 87 * green + 112 * blue + 128) >> 8) + 128, 16, 240);

	private static int ToLimitedCr(byte red, byte green, byte blue) =>
		Math.Clamp(((112 * red - 102 * green - 10 * blue + 128) >> 8) + 128, 16, 240);

	private void ValidateSample(RecordingProgramSample sample, StagedPayload payload)
	{
		var videoFormat = sample.Video.Surface.Format;
		var profile = ProfessionalRecordingFormats.Mov2VuyPcm;
		if (!profile.SupportedInputVideoFormats.Contains(videoFormat))
		{
			throw new InvalidDataException(
				$"MOV recording does not support Program format '{videoFormat.Width}x{videoFormat.Height} {videoFormat.FrameRate}'.");
		}

		if (sample.Audio is null)
			throw new InvalidDataException("Qualified MOV recording requires Program audio.");
		if (sample.Audio.Format != profile.RequiredInputAudioFormat)
			throw new InvalidDataException("Qualified MOV recording requires 48 kHz stereo Float32 Program audio.");

		var expectedVideo = checked((int)((long)videoFormat.Width * videoFormat.Height * 4));
		if (payload.Video.Length != expectedVideo)
			throw new InvalidDataException($"Program RGBA8 payload length {payload.Video.Length} does not match expected {expectedVideo}.");

		var expectedAudio = checked((int)((long)sample.Audio.Timing.SampleCount * sample.Audio.Format.ChannelCount * sizeof(float)));
		if (payload.Audio.Length != expectedAudio)
			throw new InvalidDataException($"Program Float32 audio payload length {payload.Audio.Length} does not match expected {expectedAudio}.");

		if (_videoFormat is { } establishedVideo && establishedVideo != videoFormat)
			throw new InvalidDataException("Program video format changed during an active MOV recording.");
		if (_audioFormat is { } establishedAudio && establishedAudio != sample.Audio.Format)
			throw new InvalidDataException("Program audio format changed during an active MOV recording.");
	}

	private void WriteSample(
		RecordingProgramSample sample,
		ReadOnlySpan<byte> rgba,
		ReadOnlySpan<byte> float32Audio)
	{
		var stream = _stream ?? throw new InvalidOperationException("MOV recording stream is unavailable.");
		var videoFormat = sample.Video.Surface.Format;
		var audio = sample.Audio!;

		if (_videoFormat is null)
		{
			_videoFormat = videoFormat;
			_audioFormat = audio.Format;
			var firstVideoAtMovieScale = ToTimescale(sample.Video.Timing.PresentationTimestamp, sample.Video.Timing.Timebase, MovieTimescale);
			var firstAudioAtMovieScale = ToTimescale(audio.Timing.PresentationTimestamp, audio.Timing.Timebase, MovieTimescale);
			var commonMovieOrigin = Math.Min(firstVideoAtMovieScale, firstAudioAtMovieScale);
			_videoPresentationDelayMovieTicks = checked((ulong)(firstVideoAtMovieScale - commonMovieOrigin));
			_audioPresentationDelayMovieTicks = checked((ulong)(firstAudioAtMovieScale - commonMovieOrigin));
			_videoOrigin = firstVideoAtMovieScale;
			_audioOrigin = ToTimescale(audio.Timing.PresentationTimestamp, audio.Timing.Timebase, AudioTimescale);
			_expectedAudioSamplePosition = audio.Timing.SamplePosition;
			_uyvyBuffer = new byte[checked((int)((long)videoFormat.Width * videoFormat.Height * 2))];
		}

		var videoAbsolute = ToTimescale(sample.Video.Timing.PresentationTimestamp, sample.Video.Timing.Timebase, MovieTimescale);
		var videoPresentationTime = checked(videoAbsolute - _videoOrigin!.Value);
		if (videoPresentationTime < 0 || videoPresentationTime <= _lastVideoPresentationTime)
			throw new InvalidDataException("Program video timestamps are not strictly monotonic for MOV recording.");

		if (_expectedAudioSamplePosition != audio.Timing.SamplePosition)
			throw new InvalidDataException("Program audio sample positions are not contiguous for MOV recording.");
		var audioAbsolute = ToTimescale(audio.Timing.PresentationTimestamp, audio.Timing.Timebase, AudioTimescale);
		var audioPresentationTime = checked(audioAbsolute - _audioOrigin!.Value);
		if (_audioSampleFrames == 0)
		{
			if (audioPresentationTime != 0)
				throw new InvalidDataException("First Program audio sample must resolve to the MOV A/V origin.");
		}
		else if (audioPresentationTime != checked((long)_audioSampleFrames))
		{
			throw new InvalidDataException("Program audio timestamp does not match its contiguous 48 kHz sample position.");
		}

		var uyvy = _uyvyBuffer!;
		ConvertRgbaTo2Vuy(rgba, uyvy, checked((int)videoFormat.Width), checked((int)videoFormat.Height));
		var videoChunkOffset = checked((ulong)stream.Position);
		stream.Write(uyvy);

		var pcmLength = checked((int)((long)audio.Timing.SampleCount * audio.Format.ChannelCount * sizeof(short)));
		if (_pcm16Buffer is null || _pcm16Buffer.Length < pcmLength)
			_pcm16Buffer = new byte[pcmLength];
		var pcm = _pcm16Buffer.AsSpan(0, pcmLength);
		WindowsMediaFoundationMp4RecordingWriter.ConvertFloat32ToPcm16(float32Audio, pcm);
		var audioChunkOffset = checked((ulong)stream.Position);
		stream.Write(pcm);

		_videoPresentationTimes.Add(checked((ulong)videoPresentationTime));
		_videoChunkOffsets.Add(videoChunkOffset);
		_audioChunkOffsets.Add(audioChunkOffset);
		_audioSamplesPerChunk.Add(audio.Timing.SampleCount);
		_audioSampleFrames = checked(_audioSampleFrames + audio.Timing.SampleCount);
		_expectedAudioSamplePosition = checked(audio.Timing.SamplePosition + audio.Timing.SampleCount);
		_lastVideoPresentationTime = videoPresentationTime;
	}

	private byte[] BuildMovieAtom()
	{
		var format = _videoFormat ?? throw new InvalidOperationException("MOV video format is unavailable.");
		var videoDurations = BuildVideoDurations(format);
		var videoDuration = Sum(videoDurations);
		var audioDuration = _audioSampleFrames;
		var audioDurationAtMovieScale = ToMovieDuration(audioDuration, AudioTimescale);
		var presentedVideoDuration = checked(_videoPresentationDelayMovieTicks + videoDuration);
		var presentedAudioDuration = checked(_audioPresentationDelayMovieTicks + audioDurationAtMovieScale);
		var movieDuration = Math.Max(presentedVideoDuration, presentedAudioDuration);

		return Atom("moov", stream =>
		{
			WriteAtom(stream, BuildMovieHeader(movieDuration));
			WriteAtom(stream, BuildVideoTrack(format, videoDurations, videoDuration, _videoPresentationDelayMovieTicks));
			WriteAtom(stream, BuildAudioTrack(audioDuration, audioDurationAtMovieScale, _audioPresentationDelayMovieTicks));
		});
	}

	private byte[] BuildMovieHeader(ulong duration) =>
		Atom("mvhd", stream =>
		{
			WriteVersionFlags(stream, 1, 0);
			WriteUInt64(stream, 0);
			WriteUInt64(stream, 0);
			WriteUInt32(stream, MovieTimescale);
			WriteUInt64(stream, duration);
			WriteUInt32(stream, 0x00010000);
			WriteUInt16(stream, 0x0100);
			WriteUInt16(stream, 0);
			WriteUInt32(stream, 0);
			WriteUInt32(stream, 0);
			WriteIdentityMatrix(stream);
			for (var index = 0; index < 6; index++) WriteUInt32(stream, 0);
			WriteUInt32(stream, 3);
		});

	private byte[] BuildVideoTrack(
		VideoFormat format,
		IReadOnlyList<uint> durations,
		ulong mediaDuration,
		ulong presentationDelay) =>
		Atom("trak", stream =>
		{
			WriteAtom(stream, BuildTrackHeader(1, checked(presentationDelay + mediaDuration), format.Width, format.Height, audio: false));
			if (presentationDelay != 0)
				WriteAtom(stream, BuildEditList(presentationDelay, mediaDuration));
			WriteAtom(stream, Atom("mdia", media =>
			{
				WriteAtom(media, BuildMediaHeader(MovieTimescale, mediaDuration));
				WriteAtom(media, BuildHandler("vide", "VideoHandler"));
				WriteAtom(media, Atom("minf", minf =>
				{
					WriteAtom(minf, Atom("vmhd", vmhd =>
					{
						WriteVersionFlags(vmhd, 0, 1);
						WriteUInt16(vmhd, 0);
						WriteUInt16(vmhd, 0);
						WriteUInt16(vmhd, 0);
						WriteUInt16(vmhd, 0);
					}));
					WriteAtom(minf, BuildDataInformation());
					WriteAtom(minf, BuildVideoSampleTable(format, durations));
				}));
			}));
		});

	private byte[] BuildAudioTrack(
		ulong mediaDuration,
		ulong mediaDurationAtMovieScale,
		ulong presentationDelay) =>
		Atom("trak", stream =>
		{
			WriteAtom(stream, BuildTrackHeader(2, checked(presentationDelay + mediaDurationAtMovieScale), 0, 0, audio: true));
			if (presentationDelay != 0)
				WriteAtom(stream, BuildEditList(presentationDelay, mediaDurationAtMovieScale));
			WriteAtom(stream, Atom("mdia", media =>
			{
				WriteAtom(media, BuildMediaHeader(AudioTimescale, mediaDuration));
				WriteAtom(media, BuildHandler("soun", "SoundHandler"));
				WriteAtom(media, Atom("minf", minf =>
				{
					WriteAtom(minf, Atom("smhd", smhd =>
					{
						WriteVersionFlags(smhd, 0, 0);
						WriteUInt16(smhd, 0);
						WriteUInt16(smhd, 0);
					}));
					WriteAtom(minf, BuildDataInformation());
					WriteAtom(minf, BuildAudioSampleTable());
				}));
			}));
		});

	private static byte[] BuildEditList(ulong presentationDelay, ulong mediaDurationAtMovieScale) =>
		Atom("edts", stream =>
		{
			WriteAtom(stream, Atom("elst", elst =>
			{
				WriteVersionFlags(elst, 1, 0);
				WriteUInt32(elst, 2);
				WriteUInt64(elst, presentationDelay);
				WriteInt64(elst, -1);
				WriteUInt16(elst, 1);
				WriteUInt16(elst, 0);
				WriteUInt64(elst, mediaDurationAtMovieScale);
				WriteInt64(elst, 0);
				WriteUInt16(elst, 1);
				WriteUInt16(elst, 0);
			}));
		});

	private static byte[] BuildTrackHeader(uint trackId, ulong duration, uint width, uint height, bool audio) =>
		Atom("tkhd", stream =>
		{
			WriteVersionFlags(stream, 1, 0x000007);
			WriteUInt64(stream, 0);
			WriteUInt64(stream, 0);
			WriteUInt32(stream, trackId);
			WriteUInt32(stream, 0);
			WriteUInt64(stream, duration);
			WriteUInt32(stream, 0);
			WriteUInt32(stream, 0);
			WriteUInt16(stream, 0);
			WriteUInt16(stream, 0);
			WriteUInt16(stream, audio ? (ushort)0x0100 : (ushort)0);
			WriteUInt16(stream, 0);
			WriteIdentityMatrix(stream);
			WriteUInt32(stream, checked(width << 16));
			WriteUInt32(stream, checked(height << 16));
		});

	private static byte[] BuildMediaHeader(uint timescale, ulong duration) =>
		Atom("mdhd", stream =>
		{
			WriteVersionFlags(stream, 1, 0);
			WriteUInt64(stream, 0);
			WriteUInt64(stream, 0);
			WriteUInt32(stream, timescale);
			WriteUInt64(stream, duration);
			WriteUInt16(stream, 0);
			WriteUInt16(stream, 0);
		});

	private static byte[] BuildHandler(string handlerType, string name) =>
		Atom("hdlr", stream =>
		{
			WriteVersionFlags(stream, 0, 0);
			WriteUInt32(stream, 0);
			WriteFourCc(stream, handlerType);
			WriteUInt32(stream, 0);
			WriteUInt32(stream, 0);
			WriteUInt32(stream, 0);
			foreach (var character in name)
				stream.WriteByte(checked((byte)character));
			stream.WriteByte(0);
		});

	private static byte[] BuildDataInformation() =>
		Atom("dinf", stream =>
		{
			WriteAtom(stream, Atom("dref", dref =>
			{
				WriteVersionFlags(dref, 0, 0);
				WriteUInt32(dref, 1);
				WriteAtom(dref, Atom("url ", url => WriteVersionFlags(url, 0, 1)));
			}));
		});

	private byte[] BuildVideoSampleTable(VideoFormat format, IReadOnlyList<uint> durations)
	{
		var sampleSize = checked(format.Width * format.Height * 2);
		return Atom("stbl", stream =>
		{
			WriteAtom(stream, BuildVideoSampleDescription(format));
			WriteAtom(stream, BuildTimeToSample(durations));
			WriteAtom(stream, BuildSampleToChunk(Enumerable.Repeat(1U, _videoChunkOffsets.Count).ToArray()));
			WriteAtom(stream, BuildSampleSize(sampleSize, checked((uint)_videoChunkOffsets.Count)));
			WriteAtom(stream, BuildChunkOffsets(_videoChunkOffsets));
		});
	}

	private byte[] BuildAudioSampleTable() =>
		Atom("stbl", stream =>
		{
			WriteAtom(stream, BuildAudioSampleDescription());
			WriteAtom(stream, Atom("stts", stts =>
			{
				WriteVersionFlags(stts, 0, 0);
				WriteUInt32(stts, 1);
				WriteUInt32(stts, checked((uint)_audioSampleFrames));
				WriteUInt32(stts, 1);
			}));
			WriteAtom(stream, BuildSampleToChunk(_audioSamplesPerChunk));
			WriteAtom(stream, BuildSampleSize(AudioBytesPerSampleFrame, checked((uint)_audioSampleFrames)));
			WriteAtom(stream, BuildChunkOffsets(_audioChunkOffsets));
		});

	private static byte[] BuildVideoSampleDescription(VideoFormat format) =>
		Atom("stsd", stream =>
		{
			WriteVersionFlags(stream, 0, 0);
			WriteUInt32(stream, 1);
			WriteAtom(stream, Atom("2vuy", entry =>
			{
				entry.Write(new byte[6]);
				WriteUInt16(entry, 1);
				WriteUInt16(entry, 0);
				WriteUInt16(entry, 0);
				WriteUInt32(entry, 0);
				WriteUInt32(entry, 0);
				WriteUInt32(entry, 0);
				WriteUInt16(entry, checked((ushort)format.Width));
				WriteUInt16(entry, checked((ushort)format.Height));
				WriteUInt32(entry, 0x00480000);
				WriteUInt32(entry, 0x00480000);
				WriteUInt32(entry, 0);
				WriteUInt16(entry, 1);
				WritePascalString32(entry, "Uncompressed 4:2:2");
				WriteUInt16(entry, 24);
				WriteInt16(entry, -1);
				WriteAtom(entry, Atom("fiel", fiel =>
				{
					fiel.WriteByte(1);
					fiel.WriteByte(0);
				}));
				WriteAtom(entry, Atom("colr", colr =>
				{
					WriteFourCc(colr, "nclc");
					WriteUInt16(colr, 1);
					WriteUInt16(colr, 1);
					WriteUInt16(colr, 1);
				}));
			}));
		});

	private static byte[] BuildAudioSampleDescription() =>
		Atom("stsd", stream =>
		{
			WriteVersionFlags(stream, 0, 0);
			WriteUInt32(stream, 1);
			WriteAtom(stream, Atom("sowt", entry =>
			{
				entry.Write(new byte[6]);
				WriteUInt16(entry, 1);
				WriteUInt16(entry, 0);
				WriteUInt16(entry, 0);
				WriteUInt32(entry, 0);
				WriteUInt16(entry, 2);
				WriteUInt16(entry, 16);
				WriteUInt16(entry, 0);
				WriteUInt16(entry, 0);
				WriteUInt32(entry, AudioTimescale << 16);
			}));
		});

	private static byte[] BuildTimeToSample(IReadOnlyList<uint> durations)
	{
		var entries = new List<(uint Count, uint Delta)>();
		foreach (var duration in durations)
		{
			if (entries.Count != 0 && entries[^1].Delta == duration)
			{
				var previous = entries[^1];
				entries[^1] = (checked(previous.Count + 1), previous.Delta);
			}
			else
			{
				entries.Add((1, duration));
			}
		}

		return Atom("stts", stream =>
		{
			WriteVersionFlags(stream, 0, 0);
			WriteUInt32(stream, checked((uint)entries.Count));
			foreach (var entry in entries)
			{
				WriteUInt32(stream, entry.Count);
				WriteUInt32(stream, entry.Delta);
			}
		});
	}

	private static byte[] BuildSampleToChunk(IReadOnlyList<uint> samplesPerChunk)
	{
		var entries = new List<(uint FirstChunk, uint SamplesPerChunk)>();
		for (var index = 0; index < samplesPerChunk.Count; index++)
		{
			var value = samplesPerChunk[index];
			if (value == 0)
				throw new InvalidDataException("MOV sample-to-chunk table cannot contain an empty chunk.");
			if (entries.Count == 0 || entries[^1].SamplesPerChunk != value)
				entries.Add((checked((uint)index + 1), value));
		}

		return Atom("stsc", stream =>
		{
			WriteVersionFlags(stream, 0, 0);
			WriteUInt32(stream, checked((uint)entries.Count));
			foreach (var entry in entries)
			{
				WriteUInt32(stream, entry.FirstChunk);
				WriteUInt32(stream, entry.SamplesPerChunk);
				WriteUInt32(stream, 1);
			}
		});
	}

	private static byte[] BuildSampleSize(uint sampleSize, uint sampleCount) =>
		Atom("stsz", stream =>
		{
			WriteVersionFlags(stream, 0, 0);
			WriteUInt32(stream, sampleSize);
			WriteUInt32(stream, sampleCount);
		});

	private static byte[] BuildChunkOffsets(IReadOnlyList<ulong> offsets) =>
		Atom("co64", stream =>
		{
			WriteVersionFlags(stream, 0, 0);
			WriteUInt32(stream, checked((uint)offsets.Count));
			foreach (var offset in offsets)
				WriteUInt64(stream, offset);
		});

	private IReadOnlyList<uint> BuildVideoDurations(VideoFormat format)
	{
		var nominal = checked((uint)DivideRound(
			checked((Int128)MovieTimescale * format.FrameRate.Denominator),
			format.FrameRate.Numerator));
		var durations = new uint[_videoPresentationTimes.Count];
		for (var index = 0; index < durations.Length - 1; index++)
		{
			var delta = checked(_videoPresentationTimes[index + 1] - _videoPresentationTimes[index]);
			if (delta == 0 || delta > uint.MaxValue)
				throw new InvalidDataException("MOV video presentation-time delta is outside the supported range.");
			durations[index] = checked((uint)delta);
		}
		durations[^1] = nominal;
		return durations;
	}

	private void ValidateProbe(QuickTimeMovProbeResult probe)
	{
		var format = _videoFormat!.Value;
		if (probe.Container != "QuickTime Movie (MOV)" ||
			probe.VideoSampleEntry != "2vuy" ||
			probe.AudioSampleEntry != "sowt" ||
			probe.Width != format.Width ||
			probe.Height != format.Height ||
			probe.AudioSampleRate != AudioTimescale ||
			probe.AudioChannels != 2 ||
			probe.AudioBitsPerSample != 16 ||
			probe.VideoSampleCount != checked((uint)_videoPresentationTimes.Count) ||
			probe.AudioSampleFrameCount != _audioSampleFrames)
		{
			throw new InvalidDataException("Independent MOV probe did not confirm the finalized recording profile.");
		}
	}

	private static string NormalizeFileName(string fileName)
	{
		var trimmed = fileName.Trim();
		if (!string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal) ||
			trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			throw new ArgumentException(
				"Recording file name must be a valid file name without directory components.",
				nameof(fileName));
		}

		var extension = Path.GetExtension(trimmed);
		if (string.IsNullOrEmpty(extension))
			return trimmed + ProfessionalRecordingFormats.Mov2VuyPcm.FileExtension;
		if (!string.Equals(extension, ProfessionalRecordingFormats.Mov2VuyPcm.FileExtension, StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException("The managed QuickTime recording writer supports the .mov container only.", nameof(fileName));

		return trimmed;
	}

	private static string BuildPartialPath(string finalPath)
	{
		var directory = Path.GetDirectoryName(finalPath)!;
		var stem = Path.GetFileNameWithoutExtension(finalPath);
		return Path.Combine(directory, stem + ".partial.mov");
	}

	private void EnsureOpenUnsafe()
	{
		if (!_opened || _stream is null)
			throw new InvalidOperationException("MOV recording writer is not open.");
	}

	private void ReleaseStagedPayloadsUnsafe()
	{
		foreach (var payload in _stagedPayloads.Values)
			payload.VideoLease.Dispose();
		_stagedPayloads.Clear();
	}

	private void ReleaseReservation(bool deleteReservation)
	{
		string? reservationPath;
		lock (_gate)
		{
			_reservation?.Dispose();
			_reservation = null;
			reservationPath = _reservationPath;
		}
		if (deleteReservation && reservationPath is not null)
			TryDelete(reservationPath);
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
			// Cleanup is best-effort after the primary recording failure.
		}
	}

	private static ulong ToMovieDuration(ulong duration, uint sourceTimescale) =>
		checked((ulong)DivideRound(
			checked((Int128)duration * MovieTimescale),
			sourceTimescale));

	private static long ToTimescale(long timestamp, Timebase timebase, uint timescale)
	{
		if (timestamp < 0)
			throw new ArgumentOutOfRangeException(nameof(timestamp), "Media timestamp must not be negative.");
		return DivideRound(
			checked((Int128)timestamp * timebase.Numerator * timescale),
			timebase.Denominator);
	}

	private static long DivideRound(Int128 numerator, long denominator)
	{
		if (numerator < 0)
			throw new ArgumentOutOfRangeException(nameof(numerator));
		if (denominator <= 0)
			throw new ArgumentOutOfRangeException(nameof(denominator));
		return checked((long)((numerator + (denominator / 2)) / denominator));
	}

	private static ulong Sum(IReadOnlyList<uint> values)
	{
		ulong result = 0;
		foreach (var value in values)
			result = checked(result + value);
		return result;
	}

	private static byte[] BuildFileTypeAtom() =>
		Atom("ftyp", stream =>
		{
			WriteFourCc(stream, "qt  ");
			WriteUInt32(stream, 0);
			WriteFourCc(stream, "qt  ");
		});

	private static byte[] Atom(string type, Action<MemoryStream> writePayload)
	{
		using var stream = new MemoryStream();
		stream.Position = 8;
		writePayload(stream);
		if (stream.Length > uint.MaxValue)
			throw new InvalidOperationException($"MOV metadata atom '{type}' exceeds the 32-bit atom-size limit.");
		var length = checked((uint)stream.Length);
		stream.Position = 0;
		WriteUInt32(stream, length);
		WriteFourCc(stream, type);
		return stream.ToArray();
	}

	private static void WriteAtom(Stream stream, byte[] atom) => stream.Write(atom);

	private static void WriteVersionFlags(Stream stream, byte version, uint flags) =>
		WriteUInt32(stream, checked(((uint)version << 24) | (flags & 0x00ffffff)));

	private static void WriteIdentityMatrix(Stream stream)
	{
		WriteUInt32(stream, 0x00010000);
		WriteUInt32(stream, 0);
		WriteUInt32(stream, 0);
		WriteUInt32(stream, 0);
		WriteUInt32(stream, 0x00010000);
		WriteUInt32(stream, 0);
		WriteUInt32(stream, 0);
		WriteUInt32(stream, 0);
		WriteUInt32(stream, 0x40000000);
	}

	private static void WritePascalString32(Stream stream, string value)
	{
		var length = Math.Min(31, value.Length);
		stream.WriteByte(checked((byte)length));
		for (var index = 0; index < length; index++)
			stream.WriteByte(checked((byte)value[index]));
		for (var index = length + 1; index < 32; index++)
			stream.WriteByte(0);
	}

	private static void WriteFourCc(Stream stream, string value)
	{
		if (value.Length != 4 || value.Any(character => character > 0x7f))
			throw new ArgumentException("FourCC values must contain exactly four ASCII characters.", nameof(value));
		foreach (var character in value)
			stream.WriteByte(checked((byte)character));
	}

	private static void WriteUInt16(Stream stream, ushort value)
	{
		Span<byte> buffer = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteInt16(Stream stream, short value)
	{
		Span<byte> buffer = stackalloc byte[2];
		BinaryPrimitives.WriteInt16BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteInt64(Stream stream, long value)
	{
		Span<byte> buffer = stackalloc byte[8];
		BinaryPrimitives.WriteInt64BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteUInt32(Stream stream, uint value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteUInt64(Stream stream, ulong value)
	{
		Span<byte> buffer = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private sealed record StagedPayload(
		IProgramRecordingPayloadLease VideoLease,
		ReadOnlyMemory<byte> Audio)
	{
		public ReadOnlyMemory<byte> Video => VideoLease.Memory;
	}
}
