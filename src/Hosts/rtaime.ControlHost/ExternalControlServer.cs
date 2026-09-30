// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rtaime.ExternalControl.V1;

namespace rtaime.ControlHost;

public enum ExternalControlRole
{
	Observer = 1,
	Operator = 2,
	Administrator = 3
}

public enum ExternalControlServerState
{
	Disabled = 1,
	Configured = 2,
	Active = 3,
	Degraded = 4,
	Failed = 5,
	Unverified = 6,
	Stopped = 7
}

public sealed record ExternalControlIdentityOptions(
	string ClientId,
	ExternalControlRole Role,
	string? CertificateThumbprint = null,
	string? TokenEnvironmentVariable = null);

public sealed record ExternalControlServerSnapshot(
	ExternalControlServerState State,
	string Detail,
	DateTimeOffset UpdatedAtUtc,
	string? Endpoint);

public sealed record ExternalControlAuditRecord(
	DateTimeOffset TimestampUtc,
	string Event,
	string ClientId,
	string Role,
	string Operation,
	string Outcome,
	string Detail);

public sealed record ExternalControlServerOptions
{
	public const string ApiVersion = "1.0";
	public bool Enabled { get; init; }
	public bool Required { get; init; }
	public string BindAddress { get; init; } = "127.0.0.1";
	public int Port { get; init; } = 55101;
	public string? CertificatePath { get; init; }
	public string? CertificateKeyPath { get; init; }
	public string? CertificatePasswordEnvironmentVariable { get; init; }
	public string? CertificateThumbprint { get; init; }
	public bool RequireMutualTls { get; init; }
	public IReadOnlyList<ExternalControlIdentityOptions> Identities { get; init; } = Array.Empty<ExternalControlIdentityOptions>();
	public int MaxConcurrentConnections { get; init; } = 32;
	public int MaxConcurrentOperationsPerClient { get; init; } = 8;
	public int RequestsPerSecond { get; init; } = 30;
	public int RequestBurst { get; init; } = 60;
	public int MaxRequestBytes { get; init; } = 1024 * 1024;
	public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;
	public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
	public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

	public string Endpoint => $"https://{BindAddress}:{Port}";

	public static ExternalControlServerOptions Load(
		IReadOnlyList<string> args,
		Func<string, string?>? environment = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		environment ??= Environment.GetEnvironmentVariable;
		var defaults = new ExternalControlServerOptions();
		var identitiesJson = Get(args, environment, "external-identities", "RTAIME_EXTERNAL_CONTROL_IDENTITIES", "[]");
		IReadOnlyList<ExternalControlIdentityOptions> identities;
		try
		{
			identities = JsonSerializer.Deserialize<ExternalControlIdentityOptions[]>(
				identitiesJson,
				new JsonSerializerOptions(JsonSerializerDefaults.Web)
			{
				PropertyNameCaseInsensitive = true
			}) ?? Array.Empty<ExternalControlIdentityOptions>();
		}
		catch (JsonException exception)
		{
			throw new ArgumentException("External control identities configuration is invalid JSON.", "external-identities", exception);
		}

		return new ExternalControlServerOptions
		{
			Enabled = ParseBool(Get(args, environment, "external-enabled", "RTAIME_EXTERNAL_CONTROL_ENABLED", defaults.Enabled.ToString()), "external-enabled"),
			Required = ParseBool(Get(args, environment, "external-required", "RTAIME_EXTERNAL_CONTROL_REQUIRED", defaults.Required.ToString()), "external-required"),
			BindAddress = Get(args, environment, "external-bind-address", "RTAIME_EXTERNAL_CONTROL_BIND_ADDRESS", defaults.BindAddress),
			Port = ParseInt(Get(args, environment, "external-port", "RTAIME_EXTERNAL_CONTROL_PORT", defaults.Port.ToString()), "external-port"),
			CertificatePath = NullIfEmpty(Get(args, environment, "external-certificate-path", "RTAIME_EXTERNAL_CONTROL_CERTIFICATE_PATH", string.Empty)),
			CertificateKeyPath = NullIfEmpty(Get(args, environment, "external-certificate-key-path", "RTAIME_EXTERNAL_CONTROL_CERTIFICATE_KEY_PATH", string.Empty)),
			CertificatePasswordEnvironmentVariable = NullIfEmpty(Get(args, environment, "external-certificate-password-env", "RTAIME_EXTERNAL_CONTROL_CERTIFICATE_PASSWORD_ENV", string.Empty)),
			CertificateThumbprint = NullIfEmpty(Get(args, environment, "external-certificate-thumbprint", "RTAIME_EXTERNAL_CONTROL_CERTIFICATE_THUMBPRINT", string.Empty)),
			RequireMutualTls = ParseBool(Get(args, environment, "external-require-mtls", "RTAIME_EXTERNAL_CONTROL_REQUIRE_MTLS", defaults.RequireMutualTls.ToString()), "external-require-mtls"),
			Identities = identities,
			MaxConcurrentConnections = ParseInt(Get(args, environment, "external-max-connections", "RTAIME_EXTERNAL_CONTROL_MAX_CONNECTIONS", defaults.MaxConcurrentConnections.ToString()), "external-max-connections"),
			MaxConcurrentOperationsPerClient = ParseInt(Get(args, environment, "external-max-inflight", "RTAIME_EXTERNAL_CONTROL_MAX_INFLIGHT", defaults.MaxConcurrentOperationsPerClient.ToString()), "external-max-inflight"),
			RequestsPerSecond = ParseInt(Get(args, environment, "external-requests-per-second", "RTAIME_EXTERNAL_CONTROL_REQUESTS_PER_SECOND", defaults.RequestsPerSecond.ToString()), "external-requests-per-second"),
			RequestBurst = ParseInt(Get(args, environment, "external-request-burst", "RTAIME_EXTERNAL_CONTROL_REQUEST_BURST", defaults.RequestBurst.ToString()), "external-request-burst"),
			MaxRequestBytes = ParseInt(Get(args, environment, "external-max-request-bytes", "RTAIME_EXTERNAL_CONTROL_MAX_REQUEST_BYTES", defaults.MaxRequestBytes.ToString()), "external-max-request-bytes"),
			MaxResponseBytes = ParseInt(Get(args, environment, "external-max-response-bytes", "RTAIME_EXTERNAL_CONTROL_MAX_RESPONSE_BYTES", defaults.MaxResponseBytes.ToString()), "external-max-response-bytes"),
			RequestTimeout = TimeSpan.FromMilliseconds(ParseInt(Get(args, environment, "external-request-timeout-ms", "RTAIME_EXTERNAL_CONTROL_REQUEST_TIMEOUT_MS", ((int)defaults.RequestTimeout.TotalMilliseconds).ToString()), "external-request-timeout-ms")),
			ShutdownTimeout = TimeSpan.FromMilliseconds(ParseInt(Get(args, environment, "external-shutdown-timeout-ms", "RTAIME_EXTERNAL_CONTROL_SHUTDOWN_TIMEOUT_MS", ((int)defaults.ShutdownTimeout.TotalMilliseconds).ToString()), "external-shutdown-timeout-ms"))
		};
	}

	public void Validate()
	{
		if (Required && !Enabled)
			throw new ArgumentException("External control cannot be required when it is disabled.", nameof(Required));
		if (!Enabled)
			return;
		if (!TryResolveAddress(BindAddress, out _))
			throw new ArgumentException("External control bind address must be a literal IP address or 'localhost'.", nameof(BindAddress));
		if (Port is < 1 or > 65535)
			throw new ArgumentOutOfRangeException(nameof(Port));
		if (MaxConcurrentConnections is < 1 or > 1024)
			throw new ArgumentOutOfRangeException(nameof(MaxConcurrentConnections));
		if (MaxConcurrentOperationsPerClient is < 1 or > 128)
			throw new ArgumentOutOfRangeException(nameof(MaxConcurrentOperationsPerClient));
		if (RequestsPerSecond is < 1 or > 10000 || RequestBurst < RequestsPerSecond || RequestBurst > 50000)
			throw new ArgumentOutOfRangeException(nameof(RequestBurst), "External request rate and burst limits are invalid.");
		if (MaxRequestBytes is < 1024 or > 16 * 1024 * 1024)
			throw new ArgumentOutOfRangeException(nameof(MaxRequestBytes));
		if (MaxResponseBytes is < 1024 or > 32 * 1024 * 1024)
			throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
		if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5))
			throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
		if (ShutdownTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
		if (string.IsNullOrWhiteSpace(CertificatePath) == string.IsNullOrWhiteSpace(CertificateThumbprint))
			throw new ArgumentException("Exactly one external TLS server certificate source must be configured.");
		if (Identities.Count == 0)
			throw new ArgumentException("At least one authenticated external-control identity must be configured.", nameof(Identities));
		if (Identities.Count > 128)
			throw new ArgumentException("External-control identity mapping is bounded to 128 entries.", nameof(Identities));
		if (Identities.Select(identity => identity.ClientId).Distinct(StringComparer.Ordinal).Count() != Identities.Count)
			throw new ArgumentException("External-control client identities must be unique.", nameof(Identities));
		foreach (var identity in Identities)
		{
			if (string.IsNullOrWhiteSpace(identity.ClientId) || identity.ClientId.Length > 128)
				throw new ArgumentException("External-control ClientId is required and bounded to 128 characters.", nameof(Identities));
			if (!Enum.IsDefined(identity.Role))
				throw new ArgumentException($"External-control role for '{identity.ClientId}' is invalid.", nameof(Identities));
			if (string.IsNullOrWhiteSpace(identity.CertificateThumbprint) && string.IsNullOrWhiteSpace(identity.TokenEnvironmentVariable))
				throw new ArgumentException($"External-control identity '{identity.ClientId}' requires a certificate thumbprint or token environment reference.", nameof(Identities));
		}
		if (RequireMutualTls && Identities.All(identity => string.IsNullOrWhiteSpace(identity.CertificateThumbprint)))
			throw new ArgumentException("Mutual TLS requires at least one certificate-bound external-control identity.", nameof(Identities));
	}

	internal X509Certificate2 LoadServerCertificate(Func<string, string?> environment)
	{
		Validate();
		X509Certificate2 certificate;
		if (!string.IsNullOrWhiteSpace(CertificatePath))
		{
			var path = Path.GetFullPath(CertificatePath);
			if (!File.Exists(path)) throw new FileNotFoundException("External TLS server certificate was not found.", path);
			if (!string.IsNullOrWhiteSpace(CertificateKeyPath))
			{
				var keyPath = Path.GetFullPath(CertificateKeyPath);
				if (!File.Exists(keyPath)) throw new FileNotFoundException("External TLS server private key was not found.", keyPath);
				certificate = LoadPemCertificate(path, keyPath);
			}
			else
			{
				var password = string.IsNullOrWhiteSpace(CertificatePasswordEnvironmentVariable)
					? null
					: environment(CertificatePasswordEnvironmentVariable);
				certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, TlsKeyStorageFlags);
			}
		}
		else
		{
			if (!OperatingSystem.IsWindows())
				throw new PlatformNotSupportedException("Windows certificate-store loading is supported only on Windows.");
			certificate = FindCertificate(CertificateThumbprint!);
		}

		var now = DateTimeOffset.UtcNow;
		if (!certificate.HasPrivateKey)
		{
			certificate.Dispose();
			throw new InvalidOperationException("External TLS server certificate does not contain an accessible private key.");
		}
		if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
		{
			certificate.Dispose();
			throw new InvalidOperationException("External TLS server certificate is not currently valid.");
		}
		return certificate;
	}

	private static X509Certificate2 LoadPemCertificate(string certificatePath, string keyPath)
	{
		using var certificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
		return X509CertificateLoader.LoadPkcs12(
			certificate.Export(X509ContentType.Pkcs12),
			password: null,
			TlsKeyStorageFlags);
	}

	private static X509KeyStorageFlags TlsKeyStorageFlags =>
		OperatingSystem.IsWindows()
			? X509KeyStorageFlags.DefaultKeySet
			: X509KeyStorageFlags.EphemeralKeySet;

	internal static bool TryResolveAddress(string value, out IPAddress address)
	{
		if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
		{
			address = IPAddress.Loopback;
			return true;
		}
		return IPAddress.TryParse(value, out address!);
	}

	private static X509Certificate2 FindCertificate(string thumbprint)
	{
		var normalized = NormalizeThumbprint(thumbprint);
		foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
		{
			using var store = new X509Store(StoreName.My, location);
			store.Open(OpenFlags.ReadOnly);
			var certificate = store.Certificates
				.OfType<X509Certificate2>()
				.FirstOrDefault(candidate => string.Equals(NormalizeThumbprint(candidate.Thumbprint), normalized, StringComparison.OrdinalIgnoreCase));
			if (certificate is not null)
				return certificate;
		}
		throw new InvalidOperationException("Configured external TLS server certificate thumbprint was not found.");
	}

	internal static string NormalizeThumbprint(string? thumbprint) =>
		(thumbprint ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

	private static string Get(IReadOnlyList<string> args, Func<string, string?> environment, string key, string environmentName, string defaultValue)
	{
		var prefix = $"--{key}=";
		var commandLine = args.LastOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
		if (commandLine is not null) return commandLine[prefix.Length..].Trim();
		var environmentValue = environment(environmentName);
		return string.IsNullOrWhiteSpace(environmentValue) ? defaultValue : environmentValue.Trim();
	}

	private static bool ParseBool(string value, string key) =>
		bool.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"Configuration '{key}' must be true or false.", key);

	private static int ParseInt(string value, string key) =>
		int.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException($"Configuration '{key}' must be a positive integer.", key);

	private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ExternalControlServer : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly ExternalControlServerOptions _options;
	private readonly ControlHostIpcServer _dispatcher;
	private readonly Func<string, string?> _environment;
	private readonly ExternalControlAuditBuffer _audit = new(256);
	private WebApplication? _application;
	private X509Certificate2? _serverCertificate;
	private ExternalControlServerSnapshot _snapshot;

	public ExternalControlServer(
		ExternalControlServerOptions options,
		ControlHostIpcServer dispatcher,
		Func<string, string?>? environment = null)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
		_environment = environment ?? Environment.GetEnvironmentVariable;
		_snapshot = new ExternalControlServerSnapshot(
			options.Enabled ? ExternalControlServerState.Configured : ExternalControlServerState.Disabled,
			options.Enabled ? "External control is configured but not started." : "External control is disabled.",
			DateTimeOffset.UtcNow,
			options.Enabled ? options.Endpoint : null);
	}

	public ExternalControlServerSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
	public IReadOnlyList<ExternalControlAuditRecord> AuditRecords => _audit.Snapshot();
	public bool Running => Snapshot.State == ExternalControlServerState.Active;

	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (!_options.Enabled)
			return;
		try
		{
			_options.Validate();
			_serverCertificate = _options.LoadServerCertificate(_environment);
			var security = new ExternalControlSecurityPolicy(_options, _environment, _audit);
			var limiter = new ExternalControlResourceLimiter(_options);
			var builder = WebApplication.CreateSlimBuilder();
			builder.WebHost.ConfigureKestrel(kestrel =>
			{
				kestrel.Limits.MaxConcurrentConnections = _options.MaxConcurrentConnections;
				if (!ExternalControlServerOptions.TryResolveAddress(_options.BindAddress, out var address))
					throw new InvalidOperationException("External control bind address was not validated.");
				kestrel.Listen(address, _options.Port, listen =>
				{
					listen.Protocols = HttpProtocols.Http2;
					listen.UseHttps(https =>
					{
						https.ServerCertificate = _serverCertificate;
						https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
						https.ClientCertificateMode = _options.RequireMutualTls
							? ClientCertificateMode.RequireCertificate
							: ClientCertificateMode.AllowCertificate;
						https.ClientCertificateValidation = security.ValidateClientCertificate;
					});
				});
			});
			builder.Services.AddSingleton(_options);
			builder.Services.AddSingleton(_dispatcher);
			builder.Services.AddSingleton(security);
			builder.Services.AddSingleton(limiter);
			builder.Services.AddSingleton(_audit);
			builder.Services.AddGrpc(options =>
			{
				options.MaxReceiveMessageSize = _options.MaxRequestBytes;
				options.MaxSendMessageSize = _options.MaxResponseBytes;
			});
			_application = builder.Build();
			_application.MapGrpcService<ExternalControlGrpcService>();
			await _application.StartAsync(cancellationToken).ConfigureAwait(false);
			Set(ExternalControlServerState.Active, $"External control is active on '{_options.Endpoint}'.");
		}
		catch (Exception exception) when (!_options.Required)
		{
			Set(ExternalControlServerState.Failed, $"Optional external control failed to start: {Redact(exception.Message)}");
			await CleanupFailedStartAsync().ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			Set(ExternalControlServerState.Failed, $"Required external control failed to start: {Redact(exception.Message)}");
			await CleanupFailedStartAsync().ConfigureAwait(false);
			throw;
		}
	}

	private async ValueTask CleanupFailedStartAsync()
	{
		if (_application is not null)
		{
			try { await _application.DisposeAsync().ConfigureAwait(false); } catch { }
			_application = null;
		}
		_serverCertificate?.Dispose();
		_serverCertificate = null;
	}

	public async ValueTask DisposeAsync()
	{
		var application = _application;
		_application = null;
		if (application is not null)
		{
			using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
			await application.StopAsync(timeout.Token).ConfigureAwait(false);
			await application.DisposeAsync().ConfigureAwait(false);
		}
		_serverCertificate?.Dispose();
		_serverCertificate = null;
		if (_options.Enabled)
			Set(ExternalControlServerState.Stopped, "External control stopped.");
	}

	private void Set(ExternalControlServerState state, string detail)
	{
		lock (_gate) _snapshot = new ExternalControlServerSnapshot(state, detail, DateTimeOffset.UtcNow, _options.Enabled ? _options.Endpoint : null);
	}

	private static string Redact(string value)
	{
		if (string.IsNullOrWhiteSpace(value)) return "External control failure.";
		return value.Length <= 512 ? value : value[..512];
	}
}

internal sealed record ExternalControlPrincipal(string ClientId, ExternalControlRole Role, string ClientName, string ClientVersion);

internal sealed class ExternalControlSecurityPolicy
{
	private readonly ExternalControlServerOptions _options;
	private readonly Func<string, string?> _environment;
	private readonly ExternalControlAuditBuffer _audit;

	public ExternalControlSecurityPolicy(
		ExternalControlServerOptions options,
		Func<string, string?> environment,
		ExternalControlAuditBuffer audit)
	{
		_options = options;
		_environment = environment;
		_audit = audit;
	}

	public bool ValidateClientCertificate(X509Certificate2? certificate, X509Chain? _, SslPolicyErrors __)
	{
		if (certificate is null) return !_options.RequireMutualTls;
		var now = DateTimeOffset.UtcNow;
		if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
			return false;
		var thumbprint = ExternalControlServerOptions.NormalizeThumbprint(certificate.Thumbprint);
		return _options.Identities.Any(identity =>
			!string.IsNullOrWhiteSpace(identity.CertificateThumbprint) &&
			string.Equals(ExternalControlServerOptions.NormalizeThumbprint(identity.CertificateThumbprint), thumbprint, StringComparison.OrdinalIgnoreCase));
	}

	public ExternalControlPrincipal Authenticate(ServerCallContext context)
	{
		var headers = context.RequestHeaders;
		var declaredClientId = Header(headers, "x-rtaime-client-id");
		var clientName = Bounded(Header(headers, "x-rtaime-client-name") ?? declaredClientId ?? "external-client", 128);
		var clientVersion = Bounded(Header(headers, "x-rtaime-client-version") ?? "unknown", 64);
		var certificate = context.GetHttpContext().Connection.ClientCertificate;
		if (certificate is not null)
		{
			var thumbprint = ExternalControlServerOptions.NormalizeThumbprint(certificate.Thumbprint);
			var identity = _options.Identities.FirstOrDefault(candidate =>
				!string.IsNullOrWhiteSpace(candidate.CertificateThumbprint) &&
				string.Equals(ExternalControlServerOptions.NormalizeThumbprint(candidate.CertificateThumbprint), thumbprint, StringComparison.OrdinalIgnoreCase) &&
				(string.IsNullOrWhiteSpace(declaredClientId) || string.Equals(candidate.ClientId, declaredClientId, StringComparison.Ordinal)));
			if (identity is not null)
				return Accept(identity, clientName, clientVersion, "client-certificate");
		}

		if (!string.IsNullOrWhiteSpace(declaredClientId))
		{
			var identity = _options.Identities.FirstOrDefault(candidate =>
				string.Equals(candidate.ClientId, declaredClientId, StringComparison.Ordinal) &&
				!string.IsNullOrWhiteSpace(candidate.TokenEnvironmentVariable));
			if (identity is not null)
			{
				var expected = _environment(identity.TokenEnvironmentVariable!);
				var supplied = BearerToken(headers);
				if (!string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(supplied) && FixedTimeEquals(expected, supplied))
					return Accept(identity, clientName, clientVersion, "bearer-token");
			}
		}

		_audit.Add(new ExternalControlAuditRecord(DateTimeOffset.UtcNow, "authentication", declaredClientId ?? "unknown", "none", "connect", "denied", "Client authentication failed."));
		throw new RpcException(new Status(StatusCode.Unauthenticated, "External control authentication failed."));
	}

	private ExternalControlPrincipal Accept(
		ExternalControlIdentityOptions identity,
		string clientName,
		string clientVersion,
		string mechanism)
	{
		_audit.Add(new ExternalControlAuditRecord(
			DateTimeOffset.UtcNow,
			"authentication",
			identity.ClientId,
			identity.Role.ToString(),
			"connect",
			"accepted",
			$"Client authenticated using {mechanism}."));
		return new ExternalControlPrincipal(identity.ClientId, identity.Role, clientName, clientVersion);
	}

	public void Authorize(ExternalControlPrincipal principal, string operation)
	{
		var required = ExternalControlOperationCatalog.RequiredRole(operation);
		if (principal.Role < required)
		{
			_audit.Add(new ExternalControlAuditRecord(DateTimeOffset.UtcNow, "authorization", principal.ClientId, principal.Role.ToString(), operation, "denied", $"Capability requires role '{required}'."));
			throw new RpcException(new Status(StatusCode.PermissionDenied, "External control capability is not authorized for this client."));
		}
	}

	private static string? Header(Metadata metadata, string key) =>
		metadata.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

	private static string? BearerToken(Metadata metadata)
	{
		var value = Header(metadata, "authorization");
		return value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
			? value[7..]
			: null;
	}

	private static string Bounded(string value, int maximum) =>
		value.Length <= maximum ? value : value[..maximum];

	private static bool FixedTimeEquals(string expected, string supplied)
	{
		var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
		var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
		return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
	}
}

internal static class ExternalControlOperationCatalog
{
	private static readonly string[] Capabilities =
	[
		"control.ping", "control.snapshot.get",
		"control.preview.select", "control.scene.activate", "control.output.route", "control.program.cut", "control.program.dissolve",
		"control.audio.input.set", "control.audio.routing.set", "control.audio.test_signal.set", "control.test_pattern.set",
		"control.graphics.overlay.load", "control.graphics.cg.apply", "control.graphics.overlay.set", "control.graphics.overlay.clear",
		"control.compositing.layer.set", "control.compositing.layer.transform", "control.compositing.layer.processing", "control.compositing.layers.reorder",
		"control.recording.start", "control.recording.stop", "control.ai_showcase.set",
		"control.media_asset_catalog.snapshot.get", "control.media_asset_catalog.import", "control.media_asset_catalog.relink", "control.media_asset_catalog.remove", "control.media_asset_catalog.availability.refresh",
		"control.media_deck.snapshot.get", "control.media_deck.open", "control.media_deck.transport", "control.media_deck.marker", "control.media_deck.close",
		"control.show_control.snapshot.get", "control.show_control.cue_list.save", "control.show_control.cue_list.select", "control.show_control.arm", "control.show_control.go", "control.show_control.cancel", "control.show_control.recovery.acknowledge",
		"control.rundown.snapshot.get", "control.rundown.save", "control.rundown.prepare", "control.rundown.go", "control.rundown.next", "control.rundown.previous", "control.rundown.hold", "control.rundown.recovery.acknowledge"
	];

	public static IReadOnlyList<string> All => Capabilities;

	public static ExternalControlRole RequiredRole(string operation) =>
		operation is "control.ping" or "control.snapshot.get" or
			"control.media_asset_catalog.snapshot.get" or "control.media_deck.snapshot.get" or
			"control.show_control.snapshot.get" or "control.rundown.snapshot.get"
			? ExternalControlRole.Observer
			: ExternalControlRole.Operator;
}

internal sealed class ExternalControlResourceLimiter
{
	private readonly ExternalControlServerOptions _options;
	private readonly ConcurrentDictionary<string, ClientQuota> _clients = new(StringComparer.Ordinal);

	public ExternalControlResourceLimiter(ExternalControlServerOptions options) => _options = options;

	public async ValueTask<IDisposable> AcquireAsync(string clientId, CancellationToken cancellationToken)
	{
		var quota = _clients.GetOrAdd(clientId, _ => new ClientQuota(_options));
		if (!quota.TryConsumeRequest())
			throw new RpcException(new Status(StatusCode.ResourceExhausted, "External control request rate limit exceeded."));
		if (!await quota.InFlight.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
			throw new RpcException(new Status(StatusCode.ResourceExhausted, "External control in-flight operation limit exceeded."));
		return new Releaser(quota.InFlight);
	}

	private sealed class ClientQuota
	{
		private readonly object _gate = new();
		private readonly int _rate;
		private readonly int _burst;
		private double _tokens;
		private DateTimeOffset _lastRefill = DateTimeOffset.UtcNow;

		public ClientQuota(ExternalControlServerOptions options)
		{
			_rate = options.RequestsPerSecond;
			_burst = options.RequestBurst;
			_tokens = _burst;
			InFlight = new SemaphoreSlim(options.MaxConcurrentOperationsPerClient, options.MaxConcurrentOperationsPerClient);
		}

		public SemaphoreSlim InFlight { get; }

		public bool TryConsumeRequest()
		{
			lock (_gate)
			{
				var now = DateTimeOffset.UtcNow;
				var elapsed = Math.Max(0, (now - _lastRefill).TotalSeconds);
				_tokens = Math.Min(_burst, _tokens + elapsed * _rate);
				_lastRefill = now;
				if (_tokens < 1) return false;
				_tokens -= 1;
				return true;
			}
		}
	}

	private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
	{
		private int _disposed;
		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				semaphore.Release();
		}
	}
}

internal sealed class ExternalControlAuditBuffer
{
	private readonly object _gate = new();
	private readonly Queue<ExternalControlAuditRecord> _records = new();
	private readonly int _capacity;

	public ExternalControlAuditBuffer(int capacity) => _capacity = capacity;

	public void Add(ExternalControlAuditRecord record)
	{
		lock (_gate)
		{
			_records.Enqueue(record);
			while (_records.Count > _capacity)
				_records.Dequeue();
		}
	}

	public IReadOnlyList<ExternalControlAuditRecord> Snapshot()
	{
		lock (_gate) return _records.ToArray();
	}
}

internal sealed class ExternalControlGrpcService : ExternalControl.ExternalControlBase
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly ControlHostIpcServer _dispatcher;
	private readonly ExternalControlSecurityPolicy _security;
	private readonly ExternalControlResourceLimiter _limiter;
	private readonly ExternalControlServerOptions _options;
	private readonly ExternalControlAuditBuffer _audit;
	private long _subscriptionSequence;

	public ExternalControlGrpcService(
		ControlHostIpcServer dispatcher,
		ExternalControlSecurityPolicy security,
		ExternalControlResourceLimiter limiter,
		ExternalControlServerOptions options,
		ExternalControlAuditBuffer audit)
	{
		_dispatcher = dispatcher;
		_security = security;
		_limiter = limiter;
		_options = options;
		_audit = audit;
	}

	public override async Task<ExternalControlReply> Execute(ExternalControlRequest request, ServerCallContext context)
	{
		var principal = _security.Authenticate(context);
		ValidateContext(request.Context, principal);
		var (operation, payload) = MapRequest(request);
		_security.Authorize(principal, operation);
		using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
		operationTimeout.CancelAfter(_options.RequestTimeout);
		var operationToken = operationTimeout.Token;
		try
		{
			using var lease = await AcquireWithAuditAsync(principal, operation, operationToken).ConfigureAwait(false);
			var result = await _dispatcher.DispatchExternalAsync(
				operation,
				request.Context.RequestId,
				request.Context.CorrelationId,
				principal.ClientId,
				payload,
				operationToken).ConfigureAwait(false);
			var reply = new ExternalControlReply
			{
				ApiVersion = ExternalControlServerOptions.ApiVersion,
				ResponseType = result.ResponseType,
				RequestId = result.RequestId,
				CorrelationId = result.CorrelationId,
				HostInstanceId = result.HostInstanceId,
				StateVersion = result.StateVersion,
				Sequence = result.Sequence,
				PayloadJson = ByteString.CopyFromUtf8(result.PayloadJson),
				AuthenticatedClientId = principal.ClientId,
				AuthenticatedRole = principal.Role.ToString()
			};
			if (result.ErrorCode is not null)
				reply.Error = new ExternalControlError { Code = result.ErrorCode, Message = result.ErrorMessage ?? "External control request failed." };
			if (request.OperationCase == ExternalControlRequest.OperationOneofCase.Ping)
				reply.Capabilities.Add(ExternalControlOperationCatalog.All);
			_audit.Add(new ExternalControlAuditRecord(
				DateTimeOffset.UtcNow,
				"request",
				principal.ClientId,
				principal.Role.ToString(),
				operation,
				result.ErrorCode is null ? "accepted" : "rejected",
				result.ErrorCode ?? "Request completed."));
			return reply;
		}
		catch (RpcException) { throw; }
		catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
		{
			if (context.CancellationToken.IsCancellationRequested)
				throw new RpcException(new Status(StatusCode.Cancelled, "External control request was cancelled."));
			_audit.Add(new ExternalControlAuditRecord(
				DateTimeOffset.UtcNow,
				"deadline",
				principal.ClientId,
				principal.Role.ToString(),
				operation,
				"rejected",
				"External control server request deadline exceeded."));
			throw new RpcException(new Status(StatusCode.DeadlineExceeded, "External control server request deadline exceeded."));
		}
		catch (InvalidDataException exception)
		{
			throw new RpcException(new Status(StatusCode.InvalidArgument, Bounded(exception.Message, 512)));
		}
	}

	public override async Task SubscribeState(
		StateSubscriptionRequest request,
		IServerStreamWriter<StateNotification> responseStream,
		ServerCallContext context)
	{
		var principal = _security.Authenticate(context);
		ValidateContext(request.Context, principal);
		_security.Authorize(principal, "control.snapshot.get");
		using var lease = await AcquireWithAuditAsync(principal, "control.snapshot.get", context.CancellationToken).ConfigureAwait(false);
		var interval = TimeSpan.FromMilliseconds(Math.Clamp(request.MinimumIntervalMs == 0 ? 250u : request.MinimumIntervalMs, 100u, 5000u));
		var lastHost = _dispatcher.HostInstanceId;
		var lastVersion = _dispatcher.StateVersion;
		await responseStream.WriteAsync(new StateNotification
		{
			HostInstanceId = lastHost,
			BasedOnStateVersion = 0,
			StateVersion = lastVersion,
			Sequence = checked((ulong)Interlocked.Increment(ref _subscriptionSequence))
		}, context.CancellationToken).ConfigureAwait(false);

		while (!context.CancellationToken.IsCancellationRequested)
		{
			await Task.Delay(interval, context.CancellationToken).ConfigureAwait(false);
			var host = _dispatcher.HostInstanceId;
			var version = _dispatcher.StateVersion;
			if (string.Equals(host, lastHost, StringComparison.Ordinal) && version == lastVersion)
				continue;
			await responseStream.WriteAsync(new StateNotification
			{
				HostInstanceId = host,
				BasedOnStateVersion = string.Equals(host, lastHost, StringComparison.Ordinal) ? lastVersion : 0,
				StateVersion = version,
				Sequence = checked((ulong)Interlocked.Increment(ref _subscriptionSequence))
			}, context.CancellationToken).ConfigureAwait(false);
			lastHost = host;
			lastVersion = version;
		}
	}

	private static (string Operation, string Payload) MapRequest(ExternalControlRequest request)
	{
		object payload;
		string operation;
		switch (request.OperationCase)
		{
			case ExternalControlRequest.OperationOneofCase.Ping: operation = "control.ping"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.Snapshot: operation = "control.snapshot.get"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.SelectPreview: operation = "control.preview.select"; payload = Production(request.SelectPreview); break;
			case ExternalControlRequest.OperationOneofCase.CutProgram: operation = "control.program.cut"; payload = Production(request.CutProgram); break;
			case ExternalControlRequest.OperationOneofCase.DissolveProgram: operation = "control.program.dissolve"; payload = Production(request.DissolveProgram); break;
			case ExternalControlRequest.OperationOneofCase.ActivateScene: operation = "control.scene.activate"; payload = Production(request.ActivateScene); break;
			case ExternalControlRequest.OperationOneofCase.RouteOutputRole: operation = "control.output.route"; payload = Production(request.RouteOutputRole); break;
			case ExternalControlRequest.OperationOneofCase.SetAudioInput: operation = "control.audio.input.set"; payload = new { request.SetAudioInput.SourceId, request.SetAudioInput.Gain, request.SetAudioInput.Muted }; break;
			case ExternalControlRequest.OperationOneofCase.SetAudioRouting: operation = "control.audio.routing.set"; payload = new { request.SetAudioRouting.Mode, BreakawaySourceId = NullIfEmpty(request.SetAudioRouting.BreakawaySourceId), request.SetAudioRouting.ExpectedRoutingRevision }; break;
			case ExternalControlRequest.OperationOneofCase.SetAudioTestSignal: operation = "control.audio.test_signal.set"; payload = new { request.SetAudioTestSignal.SourceId, request.SetAudioTestSignal.Enabled, request.SetAudioTestSignal.Mode, request.SetAudioTestSignal.FrequencyHz, request.SetAudioTestSignal.PeakLevel }; break;
			case ExternalControlRequest.OperationOneofCase.SetTestPattern: operation = "control.test_pattern.set"; payload = new { request.SetTestPattern.SourceId, request.SetTestPattern.Enabled, request.SetTestPattern.MotionTiming }; break;
			case ExternalControlRequest.OperationOneofCase.LoadGraphicsOverlay: return Raw("control.graphics.overlay.load", request.LoadGraphicsOverlay);
			case ExternalControlRequest.OperationOneofCase.ApplyProductionCg: return Raw("control.graphics.cg.apply", request.ApplyProductionCg);
			case ExternalControlRequest.OperationOneofCase.SetGraphicsOverlay: return Raw("control.graphics.overlay.set", request.SetGraphicsOverlay);
			case ExternalControlRequest.OperationOneofCase.ClearGraphicsOverlay: operation = "control.graphics.overlay.clear"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.SetCompositingLayer: return Raw("control.compositing.layer.set", request.SetCompositingLayer);
			case ExternalControlRequest.OperationOneofCase.TransformCompositingLayer: return Raw("control.compositing.layer.transform", request.TransformCompositingLayer);
			case ExternalControlRequest.OperationOneofCase.SetCompositingProcessing: return Raw("control.compositing.layer.processing", request.SetCompositingProcessing);
			case ExternalControlRequest.OperationOneofCase.ReorderCompositingLayers: return Raw("control.compositing.layers.reorder", request.ReorderCompositingLayers);
			case ExternalControlRequest.OperationOneofCase.StartRecording: operation = "control.recording.start"; payload = new { request.StartRecording.DestinationDirectory, request.StartRecording.FileName }; break;
			case ExternalControlRequest.OperationOneofCase.StopRecording: operation = "control.recording.stop"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.SetAiShowcase: return Raw("control.ai_showcase.set", request.SetAiShowcase);
			case ExternalControlRequest.OperationOneofCase.GetMediaAssetCatalog: operation = "control.media_asset_catalog.snapshot.get"; payload = new { request.GetMediaAssetCatalog.Offset, request.GetMediaAssetCatalog.Limit }; break;
			case ExternalControlRequest.OperationOneofCase.ImportMediaAssets: operation = "control.media_asset_catalog.import"; payload = new { SourceLocations = request.ImportMediaAssets.SourceLocations.ToArray() }; break;
			case ExternalControlRequest.OperationOneofCase.RelinkMediaAsset: operation = "control.media_asset_catalog.relink"; payload = new { request.RelinkMediaAsset.AssetId, request.RelinkMediaAsset.SourceLocation }; break;
			case ExternalControlRequest.OperationOneofCase.RemoveMediaAsset: operation = "control.media_asset_catalog.remove"; payload = new { request.RemoveMediaAsset.AssetId }; break;
			case ExternalControlRequest.OperationOneofCase.RefreshMediaAssets: operation = "control.media_asset_catalog.availability.refresh"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.GetMediaDeck: operation = "control.media_deck.snapshot.get"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.OpenMediaDeck: operation = "control.media_deck.open"; payload = new { Version = request.OpenMediaDeck.ContractVersion, request.OpenMediaDeck.SourceId, request.OpenMediaDeck.Path, AssetId = NullIfEmpty(request.OpenMediaDeck.AssetId) }; break;
			case ExternalControlRequest.OperationOneofCase.ApplyMediaDeckTransport:
				operation = "control.media_deck.transport";
				payload = new
				{
					Version = request.ApplyMediaDeckTransport.ContractVersion,
					request.ApplyMediaDeckTransport.AssetId,
					request.ApplyMediaDeckTransport.Kind,
					TargetFrame = request.ApplyMediaDeckTransport.HasTargetFrame ? request.ApplyMediaDeckTransport.TargetFrame : (long?)null,
					AutoPlayOnProgram = request.ApplyMediaDeckTransport.HasAutoPlayOnProgram ? request.ApplyMediaDeckTransport.AutoPlayOnProgram : (bool?)null,
					EndBehavior = request.ApplyMediaDeckTransport.HasEndBehavior ? request.ApplyMediaDeckTransport.EndBehavior : (int?)null,
					InPointFrame = request.ApplyMediaDeckTransport.HasInPointFrame ? request.ApplyMediaDeckTransport.InPointFrame : (long?)null,
					OutPointFrame = request.ApplyMediaDeckTransport.HasOutPointFrame ? request.ApplyMediaDeckTransport.OutPointFrame : (long?)null
				};
				break;
			case ExternalControlRequest.OperationOneofCase.ApplyMediaDeckMarker:
				operation = "control.media_deck.marker";
				payload = new
				{
					Version = request.ApplyMediaDeckMarker.ContractVersion,
					request.ApplyMediaDeckMarker.AssetId,
					request.ApplyMediaDeckMarker.Kind,
					PositionFrame = request.ApplyMediaDeckMarker.HasPositionFrame ? request.ApplyMediaDeckMarker.PositionFrame : (long?)null,
					CuePointId = NullIfEmpty(request.ApplyMediaDeckMarker.CuePointId),
					Name = NullIfEmpty(request.ApplyMediaDeckMarker.Name)
				};
				break;
			case ExternalControlRequest.OperationOneofCase.CloseMediaDeck: operation = "control.media_deck.close"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.GetShowControl: operation = "control.show_control.snapshot.get"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.SaveShowControlCueList: operation = "control.show_control.cue_list.save"; payload = new { CueListJson = request.SaveShowControlCueList.CanonicalJson }; break;
			case ExternalControlRequest.OperationOneofCase.SelectShowControlCueList: operation = "control.show_control.cue_list.select"; payload = new { CueListId = request.SelectShowControlCueList.CueListId }; break;
			case ExternalControlRequest.OperationOneofCase.ArmShowControl: operation = "control.show_control.arm"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.GoShowControl: operation = "control.show_control.go"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.CancelShowControl: operation = "control.show_control.cancel"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.AcknowledgeShowControlRecovery: operation = "control.show_control.recovery.acknowledge"; payload = new { request.AcknowledgeShowControlRecovery.Resume }; break;
			case ExternalControlRequest.OperationOneofCase.GetRundown: operation = "control.rundown.snapshot.get"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.SaveRundown: operation = "control.rundown.save"; payload = new { RundownJson = request.SaveRundown.CanonicalJson, ExpectedStorageVersion = request.SaveRundown.ExpectedStorageVersion }; break;
			case ExternalControlRequest.OperationOneofCase.PrepareRundown: operation = "control.rundown.prepare"; payload = new { ItemId = request.PrepareRundown.ItemId }; break;
			case ExternalControlRequest.OperationOneofCase.GoRundown: operation = "control.rundown.go"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.NextRundown: operation = "control.rundown.next"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.PreviousRundown: operation = "control.rundown.previous"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.HoldRundown: operation = "control.rundown.hold"; payload = new { }; break;
			case ExternalControlRequest.OperationOneofCase.AcknowledgeRundownRecovery: operation = "control.rundown.recovery.acknowledge"; payload = new { request.AcknowledgeRundownRecovery.Resume }; break;
			default: throw new RpcException(new Status(StatusCode.InvalidArgument, "External control operation is required."));
		}
		return (operation, JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions));
	}

	private static object Production(ProductionCommand command) => new
	{
		Version = command.ContractVersion,
		CommandId = command.CommandId,
		ProductionId = command.ProductionId,
		ExpectedRevision = command.ExpectedRevision,
		SourceId = NullIfEmpty(command.SourceId),
		DurationFrames = command.HasDurationFrames ? command.DurationFrames : (uint?)null,
		SceneId = NullIfEmpty(command.SceneId),
		OutputRoleId = NullIfEmpty(command.OutputRoleId)
	};

	private static (string Operation, string Payload) Raw(string operation, JsonPayloadRequest request)
	{
		if (request.Json.IsEmpty) throw new RpcException(new Status(StatusCode.InvalidArgument, "Operation JSON payload is required."));
		var payload = request.Json.ToStringUtf8();
		try { using var _ = JsonDocument.Parse(payload); }
		catch (JsonException exception) { throw new RpcException(new Status(StatusCode.InvalidArgument, Bounded(exception.Message, 256))); }
		return (operation, payload);
	}

	private async ValueTask<IDisposable> AcquireWithAuditAsync(
		ExternalControlPrincipal principal,
		string operation,
		CancellationToken cancellationToken)
	{
		try
		{
			return await _limiter.AcquireAsync(principal.ClientId, cancellationToken).ConfigureAwait(false);
		}
		catch (RpcException exception) when (exception.StatusCode == StatusCode.ResourceExhausted)
		{
			_audit.Add(new ExternalControlAuditRecord(
				DateTimeOffset.UtcNow,
				"resource-limit",
				principal.ClientId,
				principal.Role.ToString(),
				operation,
				"rejected",
				exception.Status.Detail));
			throw;
		}
	}

	private void ValidateContext(RequestContext? context, ExternalControlPrincipal principal)
	{
		if (context is null)
		{
			AuditContextRejection(principal, "request.context.missing", "External control request context is required.");
			throw new RpcException(new Status(StatusCode.InvalidArgument, "External control request context is required."));
		}
		if (!string.Equals(context.ApiVersion, ExternalControlServerOptions.ApiVersion, StringComparison.Ordinal))
		{
			AuditContextRejection(principal, "api.version.unsupported", "External control API version is unsupported.");
			throw new RpcException(new Status(StatusCode.FailedPrecondition, "External control API version is unsupported."));
		}
		if (!Guid.TryParse(context.RequestId, out _))
		{
			AuditContextRejection(principal, "request.id.invalid", "External control RequestId must be a GUID.");
			throw new RpcException(new Status(StatusCode.InvalidArgument, "External control RequestId must be a GUID."));
		}
		if (!Guid.TryParse(context.CorrelationId, out _))
		{
			AuditContextRejection(principal, "correlation.id.invalid", "External control CorrelationId must be a GUID.");
			throw new RpcException(new Status(StatusCode.InvalidArgument, "External control CorrelationId must be a GUID."));
		}
		if (context.ClientName.Length > 128 || context.ClientVersion.Length > 64)
		{
			AuditContextRejection(principal, "client.metadata.oversized", "External control client metadata exceeds configured bounds.");
			throw new RpcException(new Status(StatusCode.InvalidArgument, "External control client metadata exceeds configured bounds."));
		}
	}

	private void AuditContextRejection(ExternalControlPrincipal principal, string code, string detail) =>
		_audit.Add(new ExternalControlAuditRecord(
			DateTimeOffset.UtcNow,
			"request-validation",
			principal.ClientId,
			principal.Role.ToString(),
			code,
			"rejected",
			detail));

	private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	private static string Bounded(string value, int length) => value.Length <= length ? value : value[..length];
}
