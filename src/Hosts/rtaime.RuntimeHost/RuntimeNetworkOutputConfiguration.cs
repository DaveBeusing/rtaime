// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.RuntimeHost;

public sealed record RuntimeNetworkOutputTarget
{
	public RuntimeNetworkOutputTarget(string roleId, NetworkOutputConfiguration configuration)
	{
		if (!string.Equals(roleId, "program", StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(roleId, "aux", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("Network output role must be 'program' or 'aux'.", nameof(roleId));
		}
		RoleId = roleId.Trim().ToLowerInvariant();
		Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
	}

	public string RoleId { get; }
	public NetworkOutputConfiguration Configuration { get; }
}

internal static class RuntimeNetworkOutputConfigurationLoader
{
	private const int MaximumTargets = 8;

	public static IReadOnlyList<RuntimeNetworkOutputTarget> Load(
		IReadOnlyList<string> args,
		Func<string, string?> environment,
		VideoFormat format)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(environment);
		var json = Get(args, environment, "network-outputs", "RTAIME_NETWORK_OUTPUTS");
		if (string.IsNullOrWhiteSpace(json))
			return Array.Empty<RuntimeNetworkOutputTarget>();

		RuntimeNetworkOutputConfigurationDto[] descriptors;
		try
		{
			descriptors = JsonSerializer.Deserialize<RuntimeNetworkOutputConfigurationDto[]>(
				json,
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
				?? Array.Empty<RuntimeNetworkOutputConfigurationDto>();
		}
		catch (JsonException exception)
		{
			throw new ArgumentException("Configuration 'network-outputs' must be a valid JSON array.", "network-outputs", exception);
		}

		if (descriptors.Length > MaximumTargets)
			throw new ArgumentException($"Configuration 'network-outputs' supports at most {MaximumTargets} targets.", "network-outputs");

		var targets = new List<RuntimeNetworkOutputTarget>(descriptors.Length);
		foreach (var descriptor in descriptors)
		{
			if (descriptor is null)
				throw new ArgumentException("Network output configuration entries must not be null.", "network-outputs");

			var protocol = ParseProtocol(descriptor.Protocol);
			var configuration = protocol switch
			{
				NetworkOutputProtocolFamily.Srt => CreateSrtConfiguration(descriptor, format),
				NetworkOutputProtocolFamily.Ndi => CreateNdiConfiguration(descriptor, format),
				_ => throw new ArgumentException("Network output protocol is unsupported.", "network-outputs")
			};

			targets.Add(new RuntimeNetworkOutputTarget(
				descriptor.RoleId ?? string.Empty,
				configuration));
		}

		var duplicateTargets = targets
			.GroupBy(target => target.Configuration.TargetId, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault(group => group.Count() > 1);
		if (duplicateTargets is not null)
			throw new ArgumentException($"Network output target '{duplicateTargets.Key}' is configured more than once.", "network-outputs");

		var duplicateRoles = targets
			.GroupBy(target => target.RoleId, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault(group => group.Count() > 1);
		if (duplicateRoles is not null)
			throw new ArgumentException($"Network output role '{duplicateRoles.Key}' currently supports one configured network target.", "network-outputs");

		return Array.AsReadOnly(targets.ToArray());
	}

	private static NetworkOutputConfiguration CreateSrtConfiguration(
		RuntimeNetworkOutputConfigurationDto descriptor,
		VideoFormat format)
	{
		if (descriptor.SourceName is not null)
			throw new ArgumentException("SRT network output must not define NDI sourceName.", "network-outputs");

		var endpoint = ParseEndpoint(descriptor.Endpoint);
		return new NetworkOutputConfiguration(
			descriptor.TargetId ?? string.Empty,
			NetworkOutputProtocolFamily.Srt,
			format,
			AudioFormat.Stereo48kFloat32,
			new SrtNetworkOutputSettings(
				endpoint,
				ParseMode(descriptor.Mode),
				NetworkOutputVideoCodec.H264,
				NetworkOutputAudioCodec.AacLc,
				descriptor.VideoBitRate ?? 12_000_000,
				descriptor.AudioBitRate ?? 192_000,
				descriptor.LatencyMilliseconds ?? 120,
				ParseLatencyMode(descriptor.LatencyMode),
				descriptor.PassphraseEnvironmentVariable),
			descriptor.QueueCapacity ?? 8,
			descriptor.ReconnectInitialDelayMilliseconds ?? 250,
			descriptor.ReconnectMaximumDelayMilliseconds ?? 5000,
			descriptor.ReconnectMaximumAttempts ?? 0);
	}

	private static NetworkOutputConfiguration CreateNdiConfiguration(
		RuntimeNetworkOutputConfigurationDto descriptor,
		VideoFormat format)
	{
		if (HasSrtOnlyFields(descriptor))
			throw new ArgumentException(
				"NDI network output must not define SRT endpoint, mode, codec bitrate, latency or passphrase fields.",
				"network-outputs");
		if (string.IsNullOrWhiteSpace(descriptor.SourceName))
			throw new ArgumentException("NDI network output requires sourceName.", "network-outputs");

		return new NetworkOutputConfiguration(
			descriptor.TargetId ?? string.Empty,
			NetworkOutputProtocolFamily.Ndi,
			format,
			AudioFormat.Stereo48kFloat32,
			new NdiNetworkOutputSettings(descriptor.SourceName),
			descriptor.QueueCapacity ?? 8,
			descriptor.ReconnectInitialDelayMilliseconds ?? 250,
			descriptor.ReconnectMaximumDelayMilliseconds ?? 5000,
			descriptor.ReconnectMaximumAttempts ?? 0);
	}

	private static bool HasSrtOnlyFields(RuntimeNetworkOutputConfigurationDto descriptor) =>
		descriptor.Endpoint is not null ||
		descriptor.Mode is not null ||
		descriptor.LatencyMode is not null ||
		descriptor.VideoBitRate.HasValue ||
		descriptor.AudioBitRate.HasValue ||
		descriptor.LatencyMilliseconds.HasValue ||
		descriptor.PassphraseEnvironmentVariable is not null;

	private static string? Get(
		IReadOnlyList<string> args,
		Func<string, string?> environment,
		string key,
		string environmentName)
	{
		var prefix = $"--{key}=";
		var commandLine = args.LastOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
		if (commandLine is not null)
			return commandLine[prefix.Length..];
		return environment(environmentName);
	}

	private static NetworkOutputProtocolFamily ParseProtocol(string? value) => (value ?? "srt").Trim().ToLowerInvariant() switch
	{
		"srt" => NetworkOutputProtocolFamily.Srt,
		"ndi" => NetworkOutputProtocolFamily.Ndi,
		_ => throw new ArgumentException("Network output protocol must be 'srt' or 'ndi'.", "network-outputs")
	};

	private static Uri ParseEndpoint(string? value)
	{
		if (string.IsNullOrWhiteSpace(value) ||
			!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint))
		{
			throw new ArgumentException("SRT network output endpoint must be an absolute srt:// URI.", "network-outputs");
		}
		return endpoint;
	}

	private static NetworkOutputConnectionMode ParseMode(string? value) => (value ?? "caller").Trim().ToLowerInvariant() switch
	{
		"caller" => NetworkOutputConnectionMode.Caller,
		"listener" => NetworkOutputConnectionMode.Listener,
		"rendezvous" => NetworkOutputConnectionMode.Rendezvous,
		_ => throw new ArgumentException("SRT network output mode must be caller, listener or rendezvous.", "network-outputs")
	};

	private static NetworkOutputLatencyMode ParseLatencyMode(string? value) => (value ?? "normal").Trim().ToLowerInvariant() switch
	{
		"low" => NetworkOutputLatencyMode.Low,
		"normal" => NetworkOutputLatencyMode.Normal,
		"reliable" => NetworkOutputLatencyMode.Reliable,
		_ => throw new ArgumentException("SRT network output latency mode must be low, normal or reliable.", "network-outputs")
	};

	private sealed record RuntimeNetworkOutputConfigurationDto
	{
		public string? RoleId { get; init; }
		public string? TargetId { get; init; }
		public string? Protocol { get; init; }
		public string? Endpoint { get; init; }
		public string? SourceName { get; init; }
		public string? Mode { get; init; }
		public string? LatencyMode { get; init; }
		public uint? VideoBitRate { get; init; }
		public uint? AudioBitRate { get; init; }
		public int? LatencyMilliseconds { get; init; }
		public int? QueueCapacity { get; init; }
		public string? PassphraseEnvironmentVariable { get; init; }
		public int? ReconnectInitialDelayMilliseconds { get; init; }
		public int? ReconnectMaximumDelayMilliseconds { get; init; }
		public int? ReconnectMaximumAttempts { get; init; }
	}
}
