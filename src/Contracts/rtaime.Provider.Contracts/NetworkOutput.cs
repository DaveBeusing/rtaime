// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.Contracts;

public static class NetworkOutputCapabilityKinds
{
	public const string Output = "network.output";
}

public enum NetworkOutputProtocolFamily
{
	Srt = 1,
	Ndi = 2
}

public enum NetworkOutputConnectionMode
{
	Caller = 1,
	Listener = 2,
	Rendezvous = 3
}

public enum NetworkOutputVideoCodec
{
	H264 = 1,
	NdiHighBandwidth = 2
}

public enum NetworkOutputAudioCodec
{
	AacLc = 1,
	Float32 = 2
}

public enum NetworkOutputLatencyMode
{
	Low = 1,
	Normal = 2,
	Reliable = 3
}

public enum NetworkOutputLifecycleState
{
	Disabled = 1,
	Connecting = 2,
	Connected = 3,
	Reconnecting = 4,
	Stopping = 5,
	Faulted = 6
}

public abstract record NetworkOutputProtocolSettings
{
	public abstract NetworkOutputProtocolFamily Protocol { get; }
	public abstract string SafeTargetIdentity { get; }
}

public sealed record SrtNetworkOutputSettings : NetworkOutputProtocolSettings
{
	public SrtNetworkOutputSettings(
		Uri endpoint,
		NetworkOutputConnectionMode mode,
		NetworkOutputVideoCodec videoCodec,
		NetworkOutputAudioCodec audioCodec,
		uint videoBitRate,
		uint audioBitRate,
		int latencyMilliseconds,
		NetworkOutputLatencyMode latencyMode = NetworkOutputLatencyMode.Normal,
		string? passphraseEnvironmentVariable = null)
	{
		if (endpoint is null || !endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, "srt", StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException("SRT network output endpoint must be an absolute srt:// URI.", nameof(endpoint));
		if (!string.IsNullOrEmpty(endpoint.UserInfo))
			throw new ArgumentException("SRT network output endpoint must not contain credentials.", nameof(endpoint));
		if (endpoint.Port is <= 0 or > 65535)
			throw new ArgumentException("SRT network output endpoint requires a valid port.", nameof(endpoint));
		if (!Enum.IsDefined(mode))
			throw new ArgumentOutOfRangeException(nameof(mode));
		if (!Enum.IsDefined(latencyMode))
			throw new ArgumentOutOfRangeException(nameof(latencyMode));
		if (!Enum.IsDefined(videoCodec) || videoCodec != NetworkOutputVideoCodec.H264)
			throw new ArgumentOutOfRangeException(nameof(videoCodec), "The SRT reference output supports H.264 video only.");
		if (!Enum.IsDefined(audioCodec) || audioCodec != NetworkOutputAudioCodec.AacLc)
			throw new ArgumentOutOfRangeException(nameof(audioCodec), "The SRT reference output supports AAC-LC audio only.");
		if (videoBitRate is < 500_000 or > 100_000_000)
			throw new ArgumentOutOfRangeException(nameof(videoBitRate));
		if (audioBitRate is < 64_000 or > 512_000)
			throw new ArgumentOutOfRangeException(nameof(audioBitRate));
		if (latencyMilliseconds is < 20 or > 8000)
			throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
		if (!string.IsNullOrWhiteSpace(passphraseEnvironmentVariable) &&
			(passphraseEnvironmentVariable.Length > 128 ||
			 passphraseEnvironmentVariable.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_'))))
		{
			throw new ArgumentException("Secret reference must be an environment-variable name containing only ASCII letters, digits and underscore.", nameof(passphraseEnvironmentVariable));
		}

		Endpoint = endpoint;
		Mode = mode;
		VideoCodec = videoCodec;
		AudioCodec = audioCodec;
		VideoBitRate = videoBitRate;
		AudioBitRate = audioBitRate;
		LatencyMilliseconds = latencyMilliseconds;
		LatencyMode = latencyMode;
		PassphraseEnvironmentVariable = string.IsNullOrWhiteSpace(passphraseEnvironmentVariable) ? null : passphraseEnvironmentVariable.Trim();
	}

	public override NetworkOutputProtocolFamily Protocol => NetworkOutputProtocolFamily.Srt;
	public Uri Endpoint { get; }
	public NetworkOutputConnectionMode Mode { get; }
	public NetworkOutputVideoCodec VideoCodec { get; }
	public NetworkOutputAudioCodec AudioCodec { get; }
	public uint VideoBitRate { get; }
	public uint AudioBitRate { get; }
	public int LatencyMilliseconds { get; }
	public NetworkOutputLatencyMode LatencyMode { get; }
	public string? PassphraseEnvironmentVariable { get; }
	public override string SafeTargetIdentity => $"{Endpoint.Scheme}://{Endpoint.Host}:{Endpoint.Port}{Endpoint.AbsolutePath}";
}

public sealed record NdiNetworkOutputSettings : NetworkOutputProtocolSettings
{
	public NdiNetworkOutputSettings(string sourceName)
	{
		if (string.IsNullOrWhiteSpace(sourceName) || sourceName.Length > 128)
			throw new ArgumentException("NDI source name is required and must not exceed 128 characters.", nameof(sourceName));
		if (sourceName.Any(character => char.IsControl(character)))
			throw new ArgumentException("NDI source name must not contain control characters.", nameof(sourceName));

		SourceName = sourceName.Trim();
	}

	public override NetworkOutputProtocolFamily Protocol => NetworkOutputProtocolFamily.Ndi;
	public string SourceName { get; }
	public override string SafeTargetIdentity => $"ndi://{SourceName}";
}

public sealed record NetworkOutputConfiguration
{
	public NetworkOutputConfiguration(
		string targetId,
		NetworkOutputProtocolFamily protocol,
		VideoFormat videoFormat,
		AudioFormat audioFormat,
		NetworkOutputProtocolSettings settings,
		int queueCapacity = 8,
		int reconnectInitialDelayMilliseconds = 250,
		int reconnectMaximumDelayMilliseconds = 5000,
		int reconnectMaximumAttempts = 0)
	{
		if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 128)
			throw new ArgumentException("Network output target identity is required and must not exceed 128 characters.", nameof(targetId));
		if (!Enum.IsDefined(protocol))
			throw new ArgumentOutOfRangeException(nameof(protocol));
		ArgumentNullException.ThrowIfNull(settings);
		if (settings.Protocol != protocol)
			throw new ArgumentException("Network output protocol must match the typed protocol settings.", nameof(settings));
		if (videoFormat.PixelFormat != PixelFormat.Rgba8 || videoFormat.ScanMode != ScanMode.Progressive)
			throw new ArgumentException("Network output currently accepts progressive RGBA8 Runtime video.", nameof(videoFormat));
		if (audioFormat != AudioFormat.Stereo48kFloat32)
			throw new ArgumentException("Network output currently requires 48 kHz stereo Float32 Runtime audio.", nameof(audioFormat));
		if (queueCapacity is < 1 or > 16)
			throw new ArgumentOutOfRangeException(nameof(queueCapacity));
		if (reconnectInitialDelayMilliseconds is < 50 or > 60_000)
			throw new ArgumentOutOfRangeException(nameof(reconnectInitialDelayMilliseconds));
		if (reconnectMaximumDelayMilliseconds < reconnectInitialDelayMilliseconds || reconnectMaximumDelayMilliseconds > 120_000)
			throw new ArgumentOutOfRangeException(nameof(reconnectMaximumDelayMilliseconds));
		if (reconnectMaximumAttempts is < 0 or > 10_000)
			throw new ArgumentOutOfRangeException(nameof(reconnectMaximumAttempts));

		TargetId = targetId.Trim().ToLowerInvariant();
		Protocol = protocol;
		VideoFormat = videoFormat;
		AudioFormat = audioFormat;
		Settings = settings;
		QueueCapacity = queueCapacity;
		ReconnectInitialDelayMilliseconds = reconnectInitialDelayMilliseconds;
		ReconnectMaximumDelayMilliseconds = reconnectMaximumDelayMilliseconds;
		ReconnectMaximumAttempts = reconnectMaximumAttempts;
	}

	public NetworkOutputConfiguration(
		string targetId,
		Uri endpoint,
		NetworkOutputProtocolFamily protocol,
		NetworkOutputConnectionMode mode,
		VideoFormat videoFormat,
		AudioFormat audioFormat,
		NetworkOutputVideoCodec videoCodec,
		NetworkOutputAudioCodec audioCodec,
		uint videoBitRate,
		uint audioBitRate,
		int latencyMilliseconds,
		int queueCapacity = 8,
		NetworkOutputLatencyMode latencyMode = NetworkOutputLatencyMode.Normal,
		string? passphraseEnvironmentVariable = null,
		int reconnectInitialDelayMilliseconds = 250,
		int reconnectMaximumDelayMilliseconds = 5000,
		int reconnectMaximumAttempts = 0)
		: this(
			targetId,
			protocol,
			videoFormat,
			audioFormat,
			protocol == NetworkOutputProtocolFamily.Srt
				? new SrtNetworkOutputSettings(
					endpoint,
					mode,
					videoCodec,
					audioCodec,
					videoBitRate,
					audioBitRate,
					latencyMilliseconds,
					latencyMode,
					passphraseEnvironmentVariable)
				: throw new ArgumentOutOfRangeException(nameof(protocol), "The compatibility constructor supports SRT only; use typed protocol settings for other providers."),
			queueCapacity,
			reconnectInitialDelayMilliseconds,
			reconnectMaximumDelayMilliseconds,
			reconnectMaximumAttempts)
	{
	}

	public string TargetId { get; }
	public NetworkOutputProtocolFamily Protocol { get; }
	public VideoFormat VideoFormat { get; }
	public AudioFormat AudioFormat { get; }
	public NetworkOutputProtocolSettings Settings { get; }
	public int QueueCapacity { get; }
	public int ReconnectInitialDelayMilliseconds { get; }
	public int ReconnectMaximumDelayMilliseconds { get; }
	public int ReconnectMaximumAttempts { get; }
	public SrtNetworkOutputSettings? SrtSettings => Settings as SrtNetworkOutputSettings;
	public NdiNetworkOutputSettings? NdiSettings => Settings as NdiNetworkOutputSettings;
	public string SafeTargetIdentity => Settings.SafeTargetIdentity;

	public Uri Endpoint => RequireSrt().Endpoint;
	public NetworkOutputConnectionMode Mode => RequireSrt().Mode;
	public NetworkOutputVideoCodec VideoCodec => Settings switch
	{
		SrtNetworkOutputSettings value => value.VideoCodec,
		NdiNetworkOutputSettings => NetworkOutputVideoCodec.NdiHighBandwidth,
		_ => throw new InvalidOperationException("Unknown network-output protocol settings.")
	};
	public NetworkOutputAudioCodec AudioCodec => Settings switch
	{
		SrtNetworkOutputSettings value => value.AudioCodec,
		NdiNetworkOutputSettings => NetworkOutputAudioCodec.Float32,
		_ => throw new InvalidOperationException("Unknown network-output protocol settings.")
	};
	public uint VideoBitRate => SrtSettings?.VideoBitRate ?? 0;
	public uint AudioBitRate => SrtSettings?.AudioBitRate ?? 0;
	public int LatencyMilliseconds => SrtSettings?.LatencyMilliseconds ?? 0;
	public NetworkOutputLatencyMode LatencyMode => RequireSrt().LatencyMode;
	public string? PassphraseEnvironmentVariable => SrtSettings?.PassphraseEnvironmentVariable;

	private SrtNetworkOutputSettings RequireSrt() =>
		SrtSettings ?? throw new InvalidOperationException("SRT-specific settings are unavailable for this network-output provider.");
}

public interface INetworkOutputPayloadLease : IDisposable
{
	ReadOnlyMemory<byte> Memory { get; }
}

public sealed record NetworkOutputProgramSample
{
	public NetworkOutputProgramSample(
		ulong sequenceNumber,
		FrameTiming videoTiming,
		AudioBufferTiming audioTiming,
		INetworkOutputPayloadLease video,
		ReadOnlyMemory<byte> audio)
	{
		if (video is null) throw new ArgumentNullException(nameof(video));
		if (video.Memory.IsEmpty) throw new ArgumentException("Network output requires a non-empty Program video payload.", nameof(video));
		if (audio.IsEmpty) throw new ArgumentException("Network output requires a non-empty Program audio payload.", nameof(audio));
		if (sequenceNumber != videoTiming.SequenceNumber)
			throw new ArgumentException("Network sample sequence must match the Program video timing.", nameof(sequenceNumber));

		SequenceNumber = sequenceNumber;
		VideoTiming = videoTiming;
		AudioTiming = audioTiming;
		Video = video;
		Audio = audio;
	}

	public ulong SequenceNumber { get; }
	public FrameTiming VideoTiming { get; }
	public AudioBufferTiming AudioTiming { get; }
	public INetworkOutputPayloadLease Video { get; }
	public ReadOnlyMemory<byte> Audio { get; }
}

public enum NetworkOutputEnqueueStatus
{
	Accepted = 1,
	DroppedOldest = 2,
	Rejected = 3
}

public sealed record NetworkOutputEnqueueResult
{
	private NetworkOutputEnqueueResult(NetworkOutputEnqueueStatus status, Failure? failure)
	{
		Status = status;
		Failure = failure;
	}

	public NetworkOutputEnqueueStatus Status { get; }
	public Failure? Failure { get; }
	public bool Accepted => Status is NetworkOutputEnqueueStatus.Accepted or NetworkOutputEnqueueStatus.DroppedOldest;

	public static NetworkOutputEnqueueResult AcceptedSample() => new(NetworkOutputEnqueueStatus.Accepted, null);
	public static NetworkOutputEnqueueResult AcceptedAfterDroppingOldest(Failure failure) => new(NetworkOutputEnqueueStatus.DroppedOldest, failure);
	public static NetworkOutputEnqueueResult Rejected(Failure failure) => new(NetworkOutputEnqueueStatus.Rejected, failure);
}

public sealed record NetworkOutputStatistics(
	ulong AcceptedSamples,
	ulong SentSamples,
	ulong DroppedSamples,
	ulong RejectedSamples,
	ulong ReconnectCount,
	ulong PacketsSent,
	ulong BytesSent,
	int QueueDepth,
	int MaximumQueueDepth = 0,
	int QueueCapacity = 0)
{
	public bool Backpressured => QueueCapacity > 0 && QueueDepth >= QueueCapacity;
}

public sealed record NetworkOutputHealthSnapshot
{
	public NetworkOutputHealthSnapshot(
		string targetId,
		string provider,
		NetworkOutputProtocolFamily protocol,
		string safeTargetIdentity,
		NetworkOutputLifecycleState lifecycle,
		bool connected,
		VideoFormat videoFormat,
		AudioFormat audioFormat,
		NetworkOutputVideoCodec videoCodec,
		NetworkOutputAudioCodec audioCodec,
		uint videoBitRate,
		uint audioBitRate,
		int latencyMilliseconds,
		NetworkOutputStatistics statistics,
		DateTimeOffset? lastSuccessfulSendUtc,
		Failure? failure)
	{
		if (string.IsNullOrWhiteSpace(targetId)) throw new ArgumentException("Target identity is required.", nameof(targetId));
		if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider identity is required.", nameof(provider));
		if (string.IsNullOrWhiteSpace(safeTargetIdentity)) throw new ArgumentException("Safe target identity is required.", nameof(safeTargetIdentity));
		if (!Enum.IsDefined(protocol)) throw new ArgumentOutOfRangeException(nameof(protocol));
		if (!Enum.IsDefined(lifecycle)) throw new ArgumentOutOfRangeException(nameof(lifecycle));
		if (!Enum.IsDefined(videoCodec)) throw new ArgumentOutOfRangeException(nameof(videoCodec));
		if (!Enum.IsDefined(audioCodec)) throw new ArgumentOutOfRangeException(nameof(audioCodec));
		if (lifecycle == NetworkOutputLifecycleState.Connected && !connected)
			throw new ArgumentException("Connected lifecycle requires connected transport state.", nameof(connected));
		if (lifecycle == NetworkOutputLifecycleState.Faulted && failure is null)
			throw new ArgumentException("Faulted network output requires failure evidence.", nameof(failure));

		TargetId = targetId.Trim().ToLowerInvariant();
		Provider = provider.Trim();
		Protocol = protocol;
		SafeTargetIdentity = safeTargetIdentity.Trim();
		Lifecycle = lifecycle;
		Connected = connected;
		VideoFormat = videoFormat;
		AudioFormat = audioFormat;
		VideoCodec = videoCodec;
		AudioCodec = audioCodec;
		VideoBitRate = videoBitRate;
		AudioBitRate = audioBitRate;
		LatencyMilliseconds = latencyMilliseconds;
		Statistics = statistics;
		LastSuccessfulSendUtc = lastSuccessfulSendUtc;
		Failure = failure;
	}

	public string TargetId { get; }
	public string Provider { get; }
	public NetworkOutputProtocolFamily Protocol { get; }
	public string SafeTargetIdentity { get; }
	public NetworkOutputLifecycleState Lifecycle { get; }
	public bool Connected { get; }
	public VideoFormat VideoFormat { get; }
	public AudioFormat AudioFormat { get; }
	public NetworkOutputVideoCodec VideoCodec { get; }
	public NetworkOutputAudioCodec AudioCodec { get; }
	public uint VideoBitRate { get; }
	public uint AudioBitRate { get; }
	public int LatencyMilliseconds { get; }
	public NetworkOutputStatistics Statistics { get; }
	public DateTimeOffset? LastSuccessfulSendUtc { get; }
	public Failure? Failure { get; }
}

public interface INetworkOutputSession : IAsyncDisposable
{
	NetworkOutputConfiguration Configuration { get; }
	NetworkOutputHealthSnapshot Snapshot { get; }
	NetworkOutputEnqueueResult TrySubmit(NetworkOutputProgramSample sample);
}
