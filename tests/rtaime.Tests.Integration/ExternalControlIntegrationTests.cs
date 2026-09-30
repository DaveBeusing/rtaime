// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ExternalControlIntegrationTests
{
	[Fact]
	public void Optional_external_control_does_not_invalidate_local_ControlHost_configuration()
	{
		var options = ControlHostProcessOptions.Default with
		{
			ExternalControl = new ExternalControlServerOptions
			{
				Enabled = true,
				Required = false
			}
		};

		options.Validate();
	}

	[Fact]
	public void Required_external_control_must_be_enabled()
	{
		var options = new ExternalControlServerOptions
		{
			Enabled = false,
			Required = true
		};

		var exception = Assert.Throws<ArgumentException>(options.Validate);

		Assert.Contains("cannot be required", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Grpc_control_reuses_ControlHost_authority_and_named_pipe_remains_available()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		await using var grpc = fixture.CreateClient();
		var external = await grpc.GetSnapshotAsync();
		var target = new ProductionSourceId(Identity.Parse(external.Sources[1].Id));
		var command = new SelectPreviewCommand(
			new ControlCommandMetadata(
				ControlContractVersion.Current,
				CommandId.New(),
				external.Production.ProductionId,
				external.Production.Revision),
			target);

		var result = await grpc.SelectPreviewAsync(command);

		Assert.True(result.Accepted);
		Assert.Equal(target, result.State.Routing.PreviewSourceId);
		Assert.Equal(result.State.Revision, fixture.Control.Control!.State.Revision);

		var local = new NamedPipeOperatorControlTransport(fixture.ControlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
		var localSnapshot = await local.GetSnapshotAsync();
		Assert.Equal(result.State.Revision, localSnapshot.Production.Revision);
		Assert.Equal(grpc.HostInstanceId, local.HostInstanceId);
	}

	[Fact]
	public async Task Capability_discovery_reports_authenticated_role_and_readiness()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Observer);
		await using var grpc = fixture.CreateClient();

		var discovery = await grpc.DiscoverAsync();

		Assert.Equal("1.0", discovery.ApiVersion);
		Assert.Equal("ready", discovery.Readiness);
		Assert.Equal("test-client", discovery.AuthenticatedClientId);
		Assert.Equal(nameof(ExternalControlRole.Observer), discovery.AuthenticatedRole);
		Assert.Contains("control.snapshot.get", discovery.Capabilities);
		Assert.DoesNotContain(discovery.Capabilities, capability => capability.StartsWith("runtime.", StringComparison.Ordinal));
		Assert.DoesNotContain(discovery.Capabilities, capability => capability.StartsWith("ai.", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Observer_can_read_but_server_denies_production_mutation()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Observer);
		await using var grpc = fixture.CreateClient();
		var snapshot = await grpc.GetSnapshotAsync();
		var command = new SelectPreviewCommand(
			new ControlCommandMetadata(
				ControlContractVersion.Current,
				CommandId.New(),
				snapshot.Production.ProductionId,
				snapshot.Production.Revision),
			new ProductionSourceId(Identity.Parse(snapshot.Sources[1].Id)));

		var exception = await Assert.ThrowsAsync<RpcException>(() => grpc.SelectPreviewAsync(command).AsTask());

		Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
		Assert.Equal(snapshot.Production.Revision, fixture.Control.Control!.State.Revision);
		Assert.Contains(fixture.Control.ExternalControlServer!.AuditRecords, record =>
			record.Event == "authorization" && record.Outcome == "denied");
	}

	[Fact]
	public async Task Duplicate_external_mutation_is_idempotent_and_conflicting_request_id_fails_closed()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		await using var grpc = fixture.CreateClient();
		var snapshot = await grpc.GetSnapshotAsync();
		var metadata = new ControlCommandMetadata(
			ControlContractVersion.Current,
			CommandId.New(),
			snapshot.Production.ProductionId,
			snapshot.Production.Revision);
		var sourceA = new ProductionSourceId(Identity.Parse(snapshot.Sources[0].Id));
		var sourceB = new ProductionSourceId(Identity.Parse(snapshot.Sources[1].Id));
		var command = new SelectPreviewCommand(metadata, sourceB);

		var first = await grpc.SelectPreviewAsync(command);
		var second = await grpc.SelectPreviewAsync(command);

		Assert.True(first.Accepted);
		Assert.Equal(first.State.Revision, second.State.Revision);
		Assert.Equal(first.State.Revision, fixture.Control.Control!.State.Revision);

		var conflict = await Assert.ThrowsAsync<ExternalControlRequestException>(
			() => grpc.SelectPreviewAsync(new SelectPreviewCommand(metadata, sourceA)).AsTask());
		Assert.Equal("ipc.request_id_conflict", conflict.Code);
		Assert.Equal(first.State.Revision, fixture.Control.Control.State.Revision);
	}

	[Fact]
	public async Task Mutual_tls_authentication_accepts_pinned_client_certificate()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator, mutualTls: true);
		await using var grpc = fixture.CreateClient(includeClientCertificate: true);

		var snapshot = await grpc.GetSnapshotAsync();

		Assert.NotEmpty(snapshot.Sources);
		Assert.False(grpc.RequiresFullSnapshot);
		Assert.Equal(ExternalControlServerState.Active, fixture.Control.ExternalControlServer!.Snapshot.State);
		Assert.Contains(fixture.Control.ExternalControlServer.AuditRecords, record =>
			record.Event == "authentication" && record.Outcome == "accepted");
	}

	[Fact]
	public async Task Pinned_server_trust_rejects_an_unexpected_certificate_identity()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Observer);
		await using var grpc = fixture.CreateClient(trustedServerThumbprint: new string('0', 40));

		var exception = await Assert.ThrowsAsync<RpcException>(() => grpc.GetSnapshotAsync().AsTask());

		Assert.Contains(exception.StatusCode, new[] { StatusCode.Unavailable, StatusCode.Internal });
		Assert.Equal(ControlHostProcessState.Ready, fixture.Control.Lifecycle.State);
	}

	[Fact]
	public async Task Mutual_tls_rejects_an_unmapped_client_certificate()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Observer, mutualTls: true);
		await using var grpc = fixture.CreateUntrustedMutualTlsClient();

		var exception = await Assert.ThrowsAsync<RpcException>(() => grpc.GetSnapshotAsync().AsTask());

		Assert.Equal(StatusCode.Unavailable, exception.StatusCode);
		Assert.Equal(ControlHostProcessState.Ready, fixture.Control.Lifecycle.State);
	}

	[Fact]
	public async Task Expired_required_server_certificate_fails_ControlHost_startup_closed()
	{
		var suffix = Guid.NewGuid().ToString("N");
		var root = Path.Combine(Path.GetTempPath(), "rtaime-external-control-expired", suffix);
		Directory.CreateDirectory(root);
		var certificate = CreateCertificate(
			root,
			"expired-server",
			client: false,
			DateTimeOffset.UtcNow.AddDays(-3),
			DateTimeOffset.UtcNow.AddDays(-2));
		var passwordEnvironment = certificate.PasswordEnvironment;
		var tokenEnvironment = "RTAIME_EXTERNAL_TEST_TOKEN_" + suffix.ToUpperInvariant();
		Environment.SetEnvironmentVariable(passwordEnvironment, certificate.Password);
		Environment.SetEnvironmentVariable(tokenEnvironment, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
		try
		{
			var external = new ExternalControlServerOptions
			{
				Enabled = true,
				Required = true,
				BindAddress = "127.0.0.1",
				Port = FreePort(),
				CertificatePath = certificate.Path,
				CertificatePasswordEnvironmentVariable = passwordEnvironment,
				Identities = [new ExternalControlIdentityOptions("test-client", ExternalControlRole.Observer, TokenEnvironmentVariable: tokenEnvironment)]
			};
			var control = new ControlHostProcess(ControlHostProcessOptions.Default with
			{
				ListenEndpoint = "rtaime.test.control.expired." + suffix,
				RuntimeEndpoint = "rtaime.test.runtime.expired." + suffix,
				DurabilityRoot = root,
				ExternalControl = external
			});

			var exit = await control.RunAsync(CancellationToken.None);

			Assert.Equal(ControlHostExitCode.StartupFailure, exit);
		}
		finally
		{
			Environment.SetEnvironmentVariable(passwordEnvironment, null);
			Environment.SetEnvironmentVariable(tokenEnvironment, null);
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task State_version_gap_requires_full_resynchronization()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		await using var observer = fixture.CreateClient();
		await using var mutator = fixture.CreateClient();
		var observed = await observer.GetSnapshotAsync();
		var mutationBase = await mutator.GetSnapshotAsync();
		var source = new ProductionSourceId(Identity.Parse(mutationBase.Sources[1].Id));
		var result = await mutator.SelectPreviewAsync(new SelectPreviewCommand(
			new ControlCommandMetadata(
				ControlContractVersion.Current,
				CommandId.New(),
				mutationBase.Production.ProductionId,
				mutationBase.Production.Revision),
			source));
		Assert.True(result.Accepted);
		Assert.True(result.State.Revision.Value > observed.Production.Revision.Value);

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		ExternalControlStateNotification? notification = null;
		await foreach (var current in observer.WatchStateAsync(TimeSpan.FromMilliseconds(100), timeout.Token))
		{
			notification = current;
			break;
		}

		Assert.NotNull(notification);
		Assert.True(notification!.RequiresResynchronization);
		Assert.True(observer.RequiresFullSnapshot);
		_ = await observer.GetSnapshotAsync(timeout.Token);
		Assert.False(observer.RequiresFullSnapshot);
	}

	[Fact]
	public async Task Client_reconnects_and_resnapshots_after_ControlHost_restart()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
		await using var grpc = fixture.CreateClient();
		var before = await grpc.GetSnapshotAsync();
		var previousHost = grpc.HostInstanceId;
		var previousRevision = before.Production.Revision;

		await fixture.RestartControlAsync();

		var after = await grpc.GetSnapshotAsync();

		Assert.NotNull(previousHost);
		Assert.NotEqual(previousHost, grpc.HostInstanceId);
		Assert.False(grpc.RequiresFullSnapshot);
		Assert.Equal(previousRevision, after.Production.Revision);
		Assert.Equal(ControlHostProcessState.Ready, fixture.Control.Lifecycle.State);
	}

	[Fact]
	public async Task Rate_limit_rejects_excess_requests_without_affecting_ControlHost_continuity()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator, requestsPerSecond: 1, requestBurst: 1);
		await using var grpc = fixture.CreateClient();
		_ = await grpc.GetSnapshotAsync();
		var rejected = false;
		for (var attempt = 0; attempt < 5 && !rejected; attempt++)
		{
			try { _ = await grpc.GetSnapshotAsync(); }
			catch (RpcException exception) when (exception.StatusCode == StatusCode.ResourceExhausted) { rejected = true; }
		}

		Assert.True(rejected);
		Assert.Equal(ControlHostProcessState.Ready, fixture.Control.Lifecycle.State);
		Assert.Contains(fixture.Control.ExternalControlServer!.AuditRecords, record =>
			record.Event == "resource-limit" && record.Outcome == "rejected");
	}

	[Fact]
	public async Task Oversized_grpc_message_is_rejected_before_ControlHost_dispatch()
	{
		await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator, maxRequestBytes: 4096);
		await using var grpc = fixture.CreateClient(maxRequestBytes: 1024 * 1024);
		_ = await grpc.GetSnapshotAsync();
		var rgba = new byte[64 * 64 * 4];
		var asset = new OperatorGraphicsAsset("oversized", 64, 64, rgba);

		var exception = await Assert.ThrowsAsync<RpcException>(() => grpc.LoadGraphicsOverlayAsync(asset).AsTask());

		Assert.Equal(StatusCode.ResourceExhausted, exception.StatusCode);
		Assert.Equal(ControlHostProcessState.Ready, fixture.Control.Lifecycle.State);
	}

	private sealed class ExternalFixture : IAsyncDisposable
	{
		private readonly CancellationTokenSource _runtimeStop;
		private CancellationTokenSource _controlStop;
		private readonly Task<RuntimeHostExitCode> _runtimeRun;
		private Task<ControlHostExitCode> _controlRun;
		private readonly ControlHostProcessOptions _controlOptions;
		private ControlHostProcess _control;
		private readonly string? _tokenEnvironment;
		private readonly string _root;
		private readonly CertificateMaterial _serverCertificate;
		private readonly CertificateMaterial? _clientCertificate;
		private readonly string _token;

		private ExternalFixture(
			RuntimeHostProcess runtime,
			ControlHostProcess control,
			ControlHostProcessOptions controlOptions,
			string controlEndpoint,
			int port,
			string root,
			CertificateMaterial serverCertificate,
			CertificateMaterial? clientCertificate,
			string? tokenEnvironment,
			string token,
			CancellationTokenSource runtimeStop,
			CancellationTokenSource controlStop,
			Task<RuntimeHostExitCode> runtimeRun,
			Task<ControlHostExitCode> controlRun)
		{
			Runtime = runtime;
			_control = control;
			_controlOptions = controlOptions;
			ControlEndpoint = controlEndpoint;
			Port = port;
			_root = root;
			_serverCertificate = serverCertificate;
			_clientCertificate = clientCertificate;
			_tokenEnvironment = tokenEnvironment;
			_token = token;
			_runtimeStop = runtimeStop;
			_controlStop = controlStop;
			_runtimeRun = runtimeRun;
			_controlRun = controlRun;
		}

		public RuntimeHostProcess Runtime { get; }
		public ControlHostProcess Control => _control;
		public string ControlEndpoint { get; }
		public int Port { get; }

		public static async Task<ExternalFixture> StartAsync(
			ExternalControlRole role,
			bool mutualTls = false,
			int requestsPerSecond = 30,
			int requestBurst = 60,
			int maxRequestBytes = 1024 * 1024)
		{
			var suffix = Guid.NewGuid().ToString("N");
			var root = Path.Combine(Path.GetTempPath(), "rtaime-external-control-tests", suffix);
			Directory.CreateDirectory(root);
			var serverCertificate = CreateCertificate(root, "server", client: false);
			var clientCertificate = mutualTls ? CreateCertificate(root, "client", client: true) : null;
			var tokenEnvironment = mutualTls ? null : "RTAIME_EXTERNAL_TEST_TOKEN_" + suffix.ToUpperInvariant();
			var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
			if (tokenEnvironment is not null) Environment.SetEnvironmentVariable(tokenEnvironment, token);
			var identity = new ExternalControlIdentityOptions(
				"test-client",
				role,
				clientCertificate?.Thumbprint,
				tokenEnvironment);
			var external = new ExternalControlServerOptions
			{
				Enabled = true,
				Required = true,
				BindAddress = "127.0.0.1",
				Port = FreePort(),
				CertificatePath = serverCertificate.Path,
				CertificatePasswordEnvironmentVariable = serverCertificate.PasswordEnvironment,
				RequireMutualTls = mutualTls,
				Identities = [identity],
				RequestsPerSecond = requestsPerSecond,
				RequestBurst = requestBurst,
				MaxRequestBytes = maxRequestBytes,
				MaxResponseBytes = 4 * 1024 * 1024
			};
			Environment.SetEnvironmentVariable(serverCertificate.PasswordEnvironment, serverCertificate.Password);

			var runtimeEndpoint = "rtaime.test.runtime." + suffix;
			var controlEndpoint = "rtaime.test.control." + suffix;
			var runtimeStop = new CancellationTokenSource();
			var controlStop = new CancellationTokenSource();
			var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
			var controlOptions = ControlHostProcessOptions.Default with
			{
				ListenEndpoint = controlEndpoint,
				RuntimeEndpoint = runtimeEndpoint,
				RuntimeRetryInterval = TimeSpan.FromMilliseconds(25),
				RequestTimeout = TimeSpan.FromSeconds(10),
				DurabilityRoot = root,
				ExternalControl = external
			};
			var control = new ControlHostProcess(controlOptions);
			var runtimeRun = runtime.RunAsync(runtimeStop.Token);
			var controlRun = control.RunAsync(controlStop.Token);
			await WaitUntilAsync(() =>
				control.Lifecycle.State == ControlHostProcessState.Ready &&
				control.Control?.HasAuthoritativeState == true &&
				control.ExternalControlServer?.Snapshot.State == ExternalControlServerState.Active);

			return new ExternalFixture(
				runtime, control, controlOptions, controlEndpoint, external.Port, root, serverCertificate, clientCertificate,
				tokenEnvironment, token, runtimeStop, controlStop, runtimeRun, controlRun);
		}

		public async Task RestartControlAsync()
		{
			_controlStop.Cancel();
			var exit = await _controlRun.ConfigureAwait(false);
			if (exit != ControlHostExitCode.Success)
				throw new InvalidOperationException($"ControlHost restart precondition failed because the previous instance exited with '{exit}'.");
			_controlStop.Dispose();

			_controlStop = new CancellationTokenSource();
			_control = new ControlHostProcess(_controlOptions);
			_controlRun = _control.RunAsync(_controlStop.Token);
			await WaitUntilAsync(() =>
				_control.Lifecycle.State == ControlHostProcessState.Ready &&
				_control.Control?.HasAuthoritativeState == true &&
				_control.ExternalControlServer?.Snapshot.State == ExternalControlServerState.Active);
		}

		public GrpcOperatorControlTransport CreateClient(
			bool includeClientCertificate = false,
			int maxRequestBytes = 1024 * 1024,
			string? trustedServerThumbprint = null) =>
			new(new GrpcOperatorControlOptions
			{
				Endpoint = new Uri($"https://127.0.0.1:{Port}"),
				TrustMode = ExternalControlTrustMode.PinnedServerCertificate,
				TrustedServerCertificateThumbprint = trustedServerThumbprint ?? _serverCertificate.Thumbprint,
				ClientId = "test-client",
				BearerToken = _tokenEnvironment is null ? null : _token,
				ClientCertificatePath = includeClientCertificate ? _clientCertificate?.Path : null,
				ClientCertificatePassword = includeClientCertificate ? _clientCertificate?.Password : null,
				RequestTimeout = TimeSpan.FromSeconds(5),
				MaxRequestBytes = maxRequestBytes
			});

		public GrpcOperatorControlTransport CreateUntrustedMutualTlsClient()
		{
			var certificate = CreateCertificate(_root, "untrusted-client-" + Guid.NewGuid().ToString("N"), client: true);
			return new GrpcOperatorControlTransport(new GrpcOperatorControlOptions
			{
				Endpoint = new Uri($"https://127.0.0.1:{Port}"),
				TrustMode = ExternalControlTrustMode.PinnedServerCertificate,
				TrustedServerCertificateThumbprint = _serverCertificate.Thumbprint,
				ClientId = "test-client",
				ClientCertificatePath = certificate.Path,
				ClientCertificatePassword = certificate.Password,
				RequestTimeout = TimeSpan.FromSeconds(5)
			});
		}

		public async ValueTask DisposeAsync()
		{
			_controlStop.Cancel();
			try { _ = await _controlRun.ConfigureAwait(false); } catch { }
			_runtimeStop.Cancel();
			try { _ = await _runtimeRun.ConfigureAwait(false); } catch { }
			_controlStop.Dispose();
			_runtimeStop.Dispose();
			if (_tokenEnvironment is not null) Environment.SetEnvironmentVariable(_tokenEnvironment, null);
			Environment.SetEnvironmentVariable(_serverCertificate.PasswordEnvironment, null);
			try { Directory.Delete(_root, recursive: true); } catch { }
		}
	}

	private sealed record CertificateMaterial(string Path, string Password, string PasswordEnvironment, string Thumbprint);

	private static CertificateMaterial CreateCertificate(
		string root,
		string name,
		bool client,
		DateTimeOffset? notBefore = null,
		DateTimeOffset? notAfter = null)
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest($"CN=rtaime-{name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
		var usages = new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") };
		request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
		if (!client)
		{
			var san = new SubjectAlternativeNameBuilder();
			san.AddIpAddress(IPAddress.Loopback);
			san.AddDnsName("localhost");
			request.CertificateExtensions.Add(san.Build());
		}
		using var certificate = request.CreateSelfSigned(
			notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5),
			notAfter ?? DateTimeOffset.UtcNow.AddDays(2));
		var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
		var path = Path.Combine(root, name + ".pfx");
		File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
		return new CertificateMaterial(path, password, "RTAIME_EXTERNAL_CERT_PASSWORD_" + Guid.NewGuid().ToString("N").ToUpperInvariant(), certificate.Thumbprint);
	}

	private static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
		finally { listener.Stop(); }
	}

	private static async Task WaitUntilAsync(Func<bool> predicate)
	{
		for (var attempt = 0; attempt < 200; attempt++)
		{
			if (predicate()) return;
			await Task.Delay(25);
		}
		throw new TimeoutException("External-control integration fixture did not reach the expected state.");
	}
}
