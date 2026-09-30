// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace rtaime.IntegrationHost;

public enum IntegrationAdapterKind
{
	Osc = 1,
	Midi = 2,
	Discrete = 3,
	Companion = 4
}

public enum IntegrationActionKind
{
	PreviewSelect = 1,
	Cut = 2,
	Dissolve = 3,
	SceneActivate = 4,
	ShowControlGo = 5,
	ShowControlArm = 6,
	ShowControlCancel = 7,
	RecordingStart = 8,
	RecordingStop = 9,
	OutputRoute = 10,
	MediaPlay = 11,
	MediaPause = 12,
	MediaStop = 13,
	MediaCueFrame = 14,
	AudioInputSet = 15
}

public enum IntegrationFeedbackSource
{
	PreviewSource = 1,
	ProgramSource = 2,
	ActiveScene = 3,
	RecordingState = 4,
	RuntimeReadiness = 5,
	MediaDeckState = 6,
	ShowControlState = 7,
	OutputHealth = 8
}

public enum IntegrationTrustMode
{
	System = 1,
	PinnedServerCertificate = 2,
	TestOnlyInsecure = 3
}

public sealed record IntegrationUpstreamOptions
{
	public string Endpoint { get; init; } = "https://127.0.0.1:55101";
	public IntegrationTrustMode TrustMode { get; init; } = IntegrationTrustMode.System;
	public string? TrustedServerCertificateThumbprint { get; init; }
	public string? ClientCertificatePath { get; init; }
	public string? ClientCertificateKeyPath { get; init; }
	public string? ClientCertificatePasswordEnvironmentVariable { get; init; }
	public string? BearerTokenEnvironmentVariable { get; init; }
	public string ClientId { get; init; } = "rtaime-integration-gateway";
	public string ClientName { get; init; } = "rtaime.IntegrationHost";
	public string ClientVersion { get; init; } = "1.0";
	public int RequestTimeoutMs { get; init; } = 5000;

	public void Validate()
	{
		if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
			!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException("Integration upstream endpoint must be an absolute HTTPS URI.", nameof(Endpoint));
		if (!Enum.IsDefined(TrustMode))
			throw new ArgumentOutOfRangeException(nameof(TrustMode));
		if (TrustMode == IntegrationTrustMode.PinnedServerCertificate && string.IsNullOrWhiteSpace(TrustedServerCertificateThumbprint))
			throw new ArgumentException("Pinned upstream trust requires a server certificate thumbprint.", nameof(TrustedServerCertificateThumbprint));
		if (!string.IsNullOrWhiteSpace(ClientCertificateKeyPath) && string.IsNullOrWhiteSpace(ClientCertificatePath))
			throw new ArgumentException("Client certificate key path requires a client certificate path.", nameof(ClientCertificateKeyPath));
		ValidateEnvironmentReference(ClientCertificatePasswordEnvironmentVariable, nameof(ClientCertificatePasswordEnvironmentVariable));
		ValidateEnvironmentReference(BearerTokenEnvironmentVariable, nameof(BearerTokenEnvironmentVariable));
		if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 128)
			throw new ArgumentException("Integration upstream client identity is required and bounded to 128 characters.", nameof(ClientId));
		if (string.IsNullOrWhiteSpace(ClientName) || ClientName.Length > 128)
			throw new ArgumentException("Integration upstream client name is required and bounded to 128 characters.", nameof(ClientName));
		if (string.IsNullOrWhiteSpace(ClientVersion) || ClientVersion.Length > 64)
			throw new ArgumentException("Integration upstream client version is required and bounded to 64 characters.", nameof(ClientVersion));
		if (RequestTimeoutMs is < 100 or > 300000)
			throw new ArgumentOutOfRangeException(nameof(RequestTimeoutMs));
	}

	internal static void ValidateEnvironmentReference(string? value, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(value))
			return;
		if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
			throw new ArgumentException("Secret environment-variable references must be valid environment names.", parameterName);
	}
}

public sealed record OscAdapterOptions
{
	public string ListenAddress { get; init; } = "127.0.0.1";
	public int ListenPort { get; init; } = 9000;
	public int MaxPacketBytes { get; init; } = 4096;
	public IReadOnlyList<string> SourceAllowlist { get; init; } = Array.Empty<string>();
	public string? FeedbackAddress { get; init; }
	public int? FeedbackPort { get; init; }

	public void Validate()
	{
		if (!IPAddress.TryParse(ListenAddress, out _))
			throw new ArgumentException("OSC listen address must be a literal IP address.", nameof(ListenAddress));
		if (ListenPort is < 1 or > 65535)
			throw new ArgumentOutOfRangeException(nameof(ListenPort));
		if (MaxPacketBytes is < 64 or > 65507)
			throw new ArgumentOutOfRangeException(nameof(MaxPacketBytes));
		if (SourceAllowlist.Count > 64 || SourceAllowlist.Any(value => !IPAddress.TryParse(value, out _)))
			throw new ArgumentException("OSC source allowlist is bounded to 64 literal IP addresses.", nameof(SourceAllowlist));
		if (string.IsNullOrWhiteSpace(FeedbackAddress) != (FeedbackPort is null))
			throw new ArgumentException("OSC feedback address and port must be configured together.");
		if (!string.IsNullOrWhiteSpace(FeedbackAddress) && !IPAddress.TryParse(FeedbackAddress, out _))
			throw new ArgumentException("OSC feedback address must be a literal IP address.", nameof(FeedbackAddress));
		if (FeedbackPort is < 1 or > 65535)
			throw new ArgumentOutOfRangeException(nameof(FeedbackPort));
	}
}

public sealed record MidiAdapterOptions
{
	public string Backend { get; init; } = "windows";
	public string? InputDevice { get; init; }
	public string? OutputDevice { get; init; }
	public int ReconnectIntervalMs { get; init; } = 1000;

	public void Validate()
	{
		if (Backend is not ("windows" or "virtual"))
			throw new ArgumentException("MIDI backend must be 'windows' or 'virtual'.", nameof(Backend));
		if (ReconnectIntervalMs is < 100 or > 30000)
			throw new ArgumentOutOfRangeException(nameof(ReconnectIntervalMs));
		if (InputDevice is { Length: > 256 } || OutputDevice is { Length: > 256 })
			throw new ArgumentException("MIDI device identity is bounded to 256 characters.");
	}
}

public sealed record DiscreteAdapterOptions
{
	public string Provider { get; init; } = "virtual";
	public IReadOnlyDictionary<string, bool> ActiveLowInputs { get; init; } =
		new Dictionary<string, bool>(StringComparer.Ordinal);
	public IReadOnlyDictionary<string, bool> ActiveLowOutputs { get; init; } =
		new Dictionary<string, bool>(StringComparer.Ordinal);

	public void Validate()
	{
		if (!string.Equals(Provider, "virtual", StringComparison.Ordinal))
			throw new ArgumentException("Only the deterministic virtual discrete provider is qualified in this release.", nameof(Provider));
		if (ActiveLowInputs.Count > 256 || ActiveLowOutputs.Count > 256)
			throw new ArgumentException("Discrete polarity maps are bounded to 256 channels.");
		ValidateChannels(ActiveLowInputs.Keys);
		ValidateChannels(ActiveLowOutputs.Keys);
	}

	private static void ValidateChannels(IEnumerable<string> channels)
	{
		foreach (var channel in channels)
		{
			if (string.IsNullOrWhiteSpace(channel) || channel.Length > 128)
				throw new ArgumentException("Discrete channel identities must be non-empty and bounded to 128 characters.");
		}
	}
}

public sealed record CompanionAdapterOptions
{
	public string BindAddress { get; init; } = "127.0.0.1";
	public int Port { get; init; } = 55120;
	public string? BearerTokenEnvironmentVariable { get; init; }
	public int MaxRequestBytes { get; init; } = 16384;

	public void Validate()
	{
		if (!IPAddress.TryParse(BindAddress, out _))
			throw new ArgumentException("Companion bind address must be a literal IP address.", nameof(BindAddress));
		if (Port is < 1 or > 65535)
			throw new ArgumentOutOfRangeException(nameof(Port));
		IntegrationUpstreamOptions.ValidateEnvironmentReference(BearerTokenEnvironmentVariable, nameof(BearerTokenEnvironmentVariable));
		if (string.IsNullOrWhiteSpace(BearerTokenEnvironmentVariable))
			throw new ArgumentException("Companion adapter requires a bearer-token environment reference.", nameof(BearerTokenEnvironmentVariable));
		if (MaxRequestBytes is < 1024 or > 1024 * 1024)
			throw new ArgumentOutOfRangeException(nameof(MaxRequestBytes));
	}
}

public sealed record IntegrationAdapterOptions
{
	public required string Id { get; init; }
	public IntegrationAdapterKind Kind { get; init; }
	public bool Enabled { get; init; } = true;
	public bool Required { get; init; }
	public OscAdapterOptions? Osc { get; init; }
	public MidiAdapterOptions? Midi { get; init; }
	public DiscreteAdapterOptions? Discrete { get; init; }
	public CompanionAdapterOptions? Companion { get; init; }

	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64)
			throw new ArgumentException("Adapter identity is required and bounded to 64 characters.", nameof(Id));
		if (!System.Text.RegularExpressions.Regex.IsMatch(Id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"))
			throw new ArgumentException("Adapter identity contains unsupported characters.", nameof(Id));
		if (!Enum.IsDefined(Kind))
			throw new ArgumentOutOfRangeException(nameof(Kind));
		switch (Kind)
		{
			case IntegrationAdapterKind.Osc:
				(Osc ?? throw new ArgumentException("OSC adapter options are required.", nameof(Osc))).Validate();
				break;
			case IntegrationAdapterKind.Midi:
				(Midi ?? throw new ArgumentException("MIDI adapter options are required.", nameof(Midi))).Validate();
				break;
			case IntegrationAdapterKind.Discrete:
				(Discrete ?? throw new ArgumentException("Discrete adapter options are required.", nameof(Discrete))).Validate();
				break;
			case IntegrationAdapterKind.Companion:
				(Companion ?? throw new ArgumentException("Companion adapter options are required.", nameof(Companion))).Validate();
				break;
		}
	}
}

public sealed record IntegrationActionOptions
{
	public IntegrationActionKind Kind { get; init; }
	public string? TargetId { get; init; }
	public string? SecondaryTargetId { get; init; }
	public uint DurationFrames { get; init; } = 12;
	public long? Frame { get; init; }
	public string? RecordingDirectory { get; init; }
	public string? RecordingFileName { get; init; }
	public double Gain { get; init; } = 1.0;
	public bool Muted { get; init; }
	public bool UseTriggerValue { get; init; }
	public double TriggerScale { get; init; } = 1.0;
	public double TriggerOffset { get; init; }
	public double TriggerMinimum { get; init; }
	public double TriggerMaximum { get; init; } = 4.0;

	public void Validate()
	{
		if (!Enum.IsDefined(Kind))
			throw new ArgumentOutOfRangeException(nameof(Kind));
		if (TargetId is { Length: > 256 } || SecondaryTargetId is { Length: > 256 })
			throw new ArgumentException("Integration action identities are bounded to 256 characters.");
		if (Kind is IntegrationActionKind.PreviewSelect or IntegrationActionKind.SceneActivate && string.IsNullOrWhiteSpace(TargetId))
			throw new ArgumentException($"{Kind} requires TargetId.", nameof(TargetId));
		if (Kind == IntegrationActionKind.OutputRoute && (string.IsNullOrWhiteSpace(TargetId) || string.IsNullOrWhiteSpace(SecondaryTargetId)))
			throw new ArgumentException("OutputRoute requires role TargetId and source SecondaryTargetId.");
		if (Kind == IntegrationActionKind.Dissolve && DurationFrames is 0 or > 10000)
			throw new ArgumentOutOfRangeException(nameof(DurationFrames));
		if (Kind == IntegrationActionKind.MediaCueFrame && Frame is < 0)
			throw new ArgumentOutOfRangeException(nameof(Frame));
		if (Kind == IntegrationActionKind.RecordingStart &&
			(string.IsNullOrWhiteSpace(RecordingDirectory) || string.IsNullOrWhiteSpace(RecordingFileName)))
			throw new ArgumentException("RecordingStart requires destination directory and file name.");
		if (!double.IsFinite(Gain) || Gain is < 0 or > 4)
			throw new ArgumentOutOfRangeException(nameof(Gain));
		if (!double.IsFinite(TriggerScale) || !double.IsFinite(TriggerOffset) ||
			!double.IsFinite(TriggerMinimum) || !double.IsFinite(TriggerMaximum) ||
			TriggerMinimum > TriggerMaximum)
			throw new ArgumentException("Trigger scaling bounds are invalid.");
	}
}

public sealed record IntegrationMappingOptions
{
	public required string Id { get; init; }
	public required string AdapterId { get; init; }
	public required string TriggerKey { get; init; }
	public IntegrationActionOptions Action { get; init; } = new();
	public int DebounceMs { get; init; }
	public int MinimumIntervalMs { get; init; }

	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128)
			throw new ArgumentException("Mapping identity is required and bounded to 128 characters.", nameof(Id));
		if (string.IsNullOrWhiteSpace(AdapterId) || AdapterId.Length > 64)
			throw new ArgumentException("Mapping adapter identity is required.", nameof(AdapterId));
		if (string.IsNullOrWhiteSpace(TriggerKey) || TriggerKey.Length > 256)
			throw new ArgumentException("Mapping trigger key is required and bounded to 256 characters.", nameof(TriggerKey));
		if (DebounceMs is < 0 or > 60000 || MinimumIntervalMs is < 0 or > 60000)
			throw new ArgumentOutOfRangeException(nameof(DebounceMs));
		Action.Validate();
	}
}

public sealed record IntegrationFeedbackMappingOptions
{
	public required string AdapterId { get; init; }
	public IntegrationFeedbackSource Source { get; init; }
	public string? SourceParameter { get; init; }
	public required string TargetKey { get; init; }

	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(AdapterId) || AdapterId.Length > 64)
			throw new ArgumentException("Feedback adapter identity is required.", nameof(AdapterId));
		if (!Enum.IsDefined(Source))
			throw new ArgumentOutOfRangeException(nameof(Source));
		if (Source == IntegrationFeedbackSource.OutputHealth && string.IsNullOrWhiteSpace(SourceParameter))
			throw new ArgumentException("Output-health feedback requires an output-role identity.", nameof(SourceParameter));
		if (SourceParameter is { Length: > 128 })
			throw new ArgumentException("Feedback source parameter is bounded to 128 characters.", nameof(SourceParameter));
		if (string.IsNullOrWhiteSpace(TargetKey) || TargetKey.Length > 256)
			throw new ArgumentException("Feedback target key is required and bounded to 256 characters.", nameof(TargetKey));
	}
}

public sealed record IntegrationGatewayOptions
{
	public const string SupportedSchemaVersion = "1.0";
	private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

	public string SchemaVersion { get; init; } = SupportedSchemaVersion;
	public bool Enabled { get; init; }
	public bool Required { get; init; }
	public bool TestMode { get; init; }
	public bool DryRun { get; init; }
	public int QueueCapacity { get; init; } = 256;
	public int SnapshotMinimumIntervalMs { get; init; } = 250;
	public IntegrationUpstreamOptions Upstream { get; init; } = new();
	public IReadOnlyList<IntegrationAdapterOptions> Adapters { get; init; } = Array.Empty<IntegrationAdapterOptions>();
	public IReadOnlyList<IntegrationMappingOptions> Mappings { get; init; } = Array.Empty<IntegrationMappingOptions>();
	public IReadOnlyList<IntegrationFeedbackMappingOptions> FeedbackMappings { get; init; } = Array.Empty<IntegrationFeedbackMappingOptions>();

	public static IntegrationGatewayOptions Load(IReadOnlyList<string> args, Func<string, string?>? environment = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		environment ??= Environment.GetEnvironmentVariable;
		var explicitPath = args
			.FirstOrDefault(argument => argument.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
			?.Split('=', 2)[1];
		var environmentPath = environment("RTAIME_INTEGRATION_CONFIG");
		var configuredPath = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath : environmentPath;
		if (string.IsNullOrWhiteSpace(configuredPath))
			return new IntegrationGatewayOptions();

		var path = Path.GetFullPath(configuredPath);
		if (!File.Exists(path))
			throw new FileNotFoundException("Integration gateway configuration was not found.", path);
		var parsed = JsonSerializer.Deserialize<IntegrationGatewayOptions>(File.ReadAllText(path), JsonOptions)
			?? throw new InvalidDataException("Integration gateway configuration is empty.");
		parsed.Validate();
		return parsed;
	}

	public void Validate()
	{
		if (!string.Equals(SchemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
			throw new ArgumentException($"Unsupported integration gateway schema version '{SchemaVersion}'.", nameof(SchemaVersion));
		if (Required && !Enabled)
			throw new ArgumentException("Integration gateway cannot be required when disabled.", nameof(Required));
		if (!Enabled)
			return;
		if (QueueCapacity is < 8 or > 4096)
			throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
		if (SnapshotMinimumIntervalMs is < 100 or > 5000)
			throw new ArgumentOutOfRangeException(nameof(SnapshotMinimumIntervalMs));
		Upstream.Validate();
		if (Adapters.Count is 0 or > 32)
			throw new ArgumentException("Enabled integration gateway requires between 1 and 32 adapters.", nameof(Adapters));
		if (Adapters.Select(adapter => adapter.Id).Distinct(StringComparer.Ordinal).Count() != Adapters.Count)
			throw new ArgumentException("Integration adapter identities must be unique.", nameof(Adapters));
		foreach (var adapter in Adapters) adapter.Validate();
		if (Mappings.Count > 512)
			throw new ArgumentException("Integration command mappings are bounded to 512 entries.", nameof(Mappings));
		if (Mappings.Select(mapping => mapping.Id).Distinct(StringComparer.Ordinal).Count() != Mappings.Count)
			throw new ArgumentException("Integration mapping identities must be unique.", nameof(Mappings));
		var adapterIds = Adapters.Select(adapter => adapter.Id).ToHashSet(StringComparer.Ordinal);
		foreach (var mapping in Mappings)
		{
			mapping.Validate();
			if (!adapterIds.Contains(mapping.AdapterId))
				throw new ArgumentException($"Mapping '{mapping.Id}' references unknown adapter '{mapping.AdapterId}'.", nameof(Mappings));
		}
		if (FeedbackMappings.Count > 256)
			throw new ArgumentException("Integration feedback mappings are bounded to 256 entries.", nameof(FeedbackMappings));
		foreach (var feedback in FeedbackMappings)
		{
			feedback.Validate();
			if (!adapterIds.Contains(feedback.AdapterId))
				throw new ArgumentException($"Feedback mapping references unknown adapter '{feedback.AdapterId}'.", nameof(FeedbackMappings));
		}
	}

	private static JsonSerializerOptions CreateJsonOptions()
	{
		var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
		{
			PropertyNameCaseInsensitive = true,
			ReadCommentHandling = JsonCommentHandling.Skip
		};
		options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
		return options;
	}
}
