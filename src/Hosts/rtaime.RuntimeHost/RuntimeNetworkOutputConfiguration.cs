// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.RuntimeHost;

public sealed record RuntimeNetworkOutputTarget(string RoleId, NetworkOutputConfiguration Configuration)
{
	public RuntimeNetworkOutputTarget : this()
	{
		if (!string.Equals(RoleId, "program", StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(RoleId, "aux", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("Network output role must be 'program' or 'aux'.", nameof(RoleId));
		}
		RoleId = RoleId.Trim().ToLowerInvariant();
		Configuration = Configuration ?? throw new ArgumentNullException(nameof(Configuration));
	}
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
			var mode = ParseMode(descriptor.Mode);
			var latencyMode = ParseLatencyMode(descriptor.LatencyMode);
			var endpoint = ParseEndpoint(descriptor.Endpoint);
			var target = new RuntimeNetworkOutputTarget(
				descriptor.RoleId ?? string.Empty,
				new NetworkOutputConfiguration(
					descriptor.TargetId ?? string.Empty,
					endpoint,
					NetworkOutputProtocolFamily.Srt,
					mode,
					format,
					AudioFormat.Stereo48kFloat32,
					descriptor.VideoBitRate ?? 12_000_000,
					descriptor.AudioBitRate ?? 192_000,
					descriptor.LatencyMilliseconds ?? 120,
					descriptor.QueueCapacity ?? 8,
					latencyMode,
					descriptor.PassphraseEnvironmentVariable,
					descriptor.ReconnectInitialDelayMilliseconds ?? 250,
					descriptor.ReconnectMaximumDelayMilliseconds ?? 5000,
					descriptor.ReconnectMaximumAttempts ?? 0));
			targets.Add(target);
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
			throw new ArgumentException($"Network output role '{duplicateRoles.Key}' currently supports one configured SRT target.", "network-outputs");

		return Array.AsReadOnly(targets.ToArray());
	}

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

	private static Uri ParseEndpoint(string? value)
	{
		if (string.IsNullOrWhiteSpace(value) ||
			!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint))
		{
			throw new ArgumentException("Network output endpoint must be an absolute srt:// URI.", "network-outputs");
		}
		return endpoint;
	}

	private static NetworkOutputConnectionMode ParseMode(string? value) => (value ?? "caller").Trim().ToLowerInvariant() switch
	{
		"caller" => NetworkOutputConnectionMode.Caller,
		"listener" => NetworkOutputConnectionMode.Listener,
		"rendezvous" => NetworkOutputConnectionMode.Rendezvous,
		_ => throw new ArgumentException("Network output mode must be caller, listener or rendezvous.", "network-outputs")
	};

	private static NetworkOutputLatencyMode ParseLatencyMode(string? value) => (value ?? "normal").Trim().ToLowerInvariant() switch
	{
		"low" => NetworkOutputLatencyMode.Low,
		"normal" => NetworkOutputLatencyMode.Normal,
		"reliable" => NetworkOutputLatencyMode.Reliable,
		_ => throw new ArgumentException("Network output latency mode must be low, normal or reliable.", "network-outputs")
	};

	private sealed record RuntimeNetworkOutputConfigurationDto
	{
		public string? RoleId { get; init; }
		public string? TargetId { get; init; }
		public string? Endpoint { get; init; }
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
