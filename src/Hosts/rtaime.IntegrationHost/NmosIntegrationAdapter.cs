// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace rtaime.IntegrationHost;

/// <summary>
/// IS-04 observer or loopback virtual interop (IS-04 v1.3.3 / IS-05 v1.2.0).
/// Never enqueues production triggers or acts as a media transport/ControlHost authority.
/// </summary>
public sealed class NmosIntegrationAdapter : IntegrationAdapterBase
{
	private readonly NmosAdapterOptions _options;
	private readonly bool _testMode;
	private readonly bool _dryRun;
	private readonly HttpClient _http;
	private readonly bool _ownsHttp;
	private readonly NmosRegistryDiscoveryClient? _discovery;
	private long _observationGeneration;
	private CancellationTokenSource? _lifetime;
	private Task? _worker;
	private WebApplication? _application;
	private NmosRegistrationClient? _registration;
	private readonly NmosVirtualResources? _resources;
	private readonly NmosVirtualConnections? _connections;
	private NmosTopologySnapshot _topology;

	public NmosIntegrationAdapter(IntegrationAdapterOptions adapter, IntegrationGatewayOptions gateway, HttpClient? http = null)
		: base(adapter.Id, IntegrationAdapterKind.Nmos, adapter.Required)
	{
		_options = adapter.Nmos ?? throw new ArgumentException("NMOS options are required.", nameof(adapter));
		_testMode = gateway.TestMode;
		_dryRun = gateway.DryRun;
		_options.Validate(_testMode, _dryRun);
		_ownsHttp = http is null;
		_http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
		{
			Timeout = TimeSpan.FromMilliseconds(_options.RequestTimeoutMs)
		};
		if (_options.Mode == NmosOperatingMode.Observer)
			_discovery = new NmosRegistryDiscoveryClient(_http, _options);
		_topology = NmosTopologySnapshot.Unavailable(_options.QueryApiUrl ?? "", 0,
			"UNAVAILABLE", "NMOS discovery has not started.");
		if (_options.Mode == NmosOperatingMode.VirtualInterop)
		{
			_resources = new NmosVirtualResources(_options);
			_connections = new NmosVirtualConnections(_options.SenderId!, _options.ReceiverId!);
		}
	}

	/// <summary>Read-only snapshot for a future WPF projection; never asserts hardware RTP admission.</summary>
	public NmosTopologySnapshot Topology => Volatile.Read(ref _topology);
	public ulong VirtualConnectionRevision => _connections?.Revision ?? 0;

	public override async ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (_lifetime is not null) return;
		var lifetime = new CancellationTokenSource();
		_lifetime = lifetime;
		SetHealth(IntegrationAdapterState.Starting, "Starting bounded NMOS registry observation.");
		try
		{
			if (_options.Mode == NmosOperatingMode.VirtualInterop)
			{
				var app = BuildVirtualServer();
				await app.StartAsync(cancellationToken).ConfigureAwait(false);
				_application = app;
				if (_options.RegistrationApiUrl is not null)
				{
					_registration = new NmosRegistrationClient(_http, _options.RegistrationApiUrl, _resources!);
					try
					{
						await _registration.RegisterAsync(cancellationToken).ConfigureAwait(false);
						SetHealth(IntegrationAdapterState.Healthy,
							"VIRTUAL reference resources registered locally; NO RTP/media/Production capability.");
					}
					catch (Exception) when (!cancellationToken.IsCancellationRequested)
					{
						SetHealth(IntegrationAdapterState.Degraded,
							"VIRTUAL reference registry is unavailable; bounded retry will continue.");
					}
				}
				else
					SetHealth(IntegrationAdapterState.Healthy,
						"VIRTUAL local IS-04/05 API is listening; no registry, RTP/media/Production capability.");
			}
			else
			{
				await RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
			}
			_worker = Task.Run(() => PollAsync(lifetime.Token), CancellationToken.None);
		}
		catch
		{
			SetHealth(IntegrationAdapterState.Failed, "NMOS adapter startup failed; no endpoints admitted.");
			await StopAsync(CancellationToken.None).ConfigureAwait(false);
			throw;
		}
	}

	public override ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken)
	{
		// NMOS does not project guessed Program/preview or external Output evidence.
		// Only the existing ControlHost-approved read-only monitoring owns such feedback.
		return ValueTask.CompletedTask;
	}

	private async Task PollAsync(CancellationToken cancellationToken)
	{
		var failures = 0;
		while (!cancellationToken.IsCancellationRequested)
		{
			var interval = _options.Mode == NmosOperatingMode.VirtualInterop
				? _options.HeartbeatIntervalMs : _options.RefreshIntervalMs;
			var wait = Math.Min(60000L, (long)interval << Math.Min(failures, 3));
			try
			{
				await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);
				if (_options.Mode == NmosOperatingMode.Observer)
					await RefreshDiscoveryAsync(cancellationToken).ConfigureAwait(false);
				else if (_registration is not null)
				{
					if (!_registration.IsRegistered)
						await _registration.RegisterAsync(cancellationToken).ConfigureAwait(false);
					else if (!await _registration.HeartbeatAsync(cancellationToken).ConfigureAwait(false))
						await _registration.RegisterAsync(cancellationToken).ConfigureAwait(false);
					SetHealth(IntegrationAdapterState.Healthy,
						"VIRTUAL NMOS registration/heartbeat confirmed; NO media/Production capability.");
				}
				failures = 0;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception)
			{
				failures = Math.Min(5, failures + 1);
				if (_options.Mode == NmosOperatingMode.Observer)
				{
					var generation = NextObservationGeneration();
					Volatile.Write(ref _topology, NmosTopologySnapshot.Unavailable(
						_options.QueryApiUrl!, generation, "STALE",
						"Registry query failed; prior resources cannot be used as live capability evidence."));
				}
				SetHealth(IntegrationAdapterState.Degraded,
					"NMOS registration/query unavailable or rejected; cached capabilities invalid, bounded retry active.");
			}
		}
	}

	private async Task RefreshDiscoveryAsync(CancellationToken cancellationToken)
	{
		try
		{
			var snapshot = await _discovery!.ReadAsync(cancellationToken).ConfigureAwait(false);
			Volatile.Write(ref _topology, snapshot with { ObservationGeneration = NextObservationGeneration() });
			SetHealth(IntegrationAdapterState.Healthy,
				$"IS-04 v1.3 read-only discovery: {snapshot.Resources.Count} validated remote entries; no local RTP provider admitted.");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception)
		{
			Volatile.Write(ref _topology, NmosTopologySnapshot.Unavailable(
				_options.QueryApiUrl!, NextObservationGeneration(), "STALE",
				"Discovery failed; previously observed resources have been invalidated."));
			SetHealth(IntegrationAdapterState.Degraded,
				"NMOS Query API is unavailable or returned invalid resources; no topology admission.");
		}
	}

	private WebApplication BuildVirtualServer()
	{
		var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
		builder.Logging.ClearProviders();
		builder.WebHost.ConfigureKestrel(server =>
		{
			server.Limits.MaxRequestBodySize = 65536;
			server.Limits.MaxConcurrentConnections = 16;
			server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
			server.Listen(IPAddress.Parse(_options.BindAddress), _options.Port);
		});
		var app = builder.Build();

		app.MapGet("/x-nmos/", () => Results.Json(new[] { "node/", "connection/" }));
		app.MapGet("/x-nmos/node/", () => Results.Json(new[] { "v1.3/" }));
		app.MapGet("/x-nmos/node/v1.3/", () =>
			Results.Json(new[] { "self/", "sources/", "flows/", "devices/", "senders/", "receivers/" }));
		app.MapGet("/x-nmos/node/v1.3/self/", () => Results.Json(_resources!.Node));
		app.MapGet("/x-nmos/node/v1.3/{collection}/", (string collection) =>
			TryKind(collection, out var kind)
				? Results.Json(_resources!.List(kind))
				: NmosError(404, "Unknown Node API resource collection."));
		app.MapGet("/x-nmos/node/v1.3/{collection}/{id}", (string collection, string id) =>
		{
			if (!TryKind(collection, out var kind) ||
				!_resources!.List(kind).Any(json => json.GetProperty("id").GetString() == id))
				return NmosError(404, "Unknown Node API resource.");
			return Results.Json(_resources.List(kind).Single(json => json.GetProperty("id").GetString() == id));
		});

		app.MapGet("/x-nmos/connection/", () => Results.Json(new[] { "v1.2/" }));
		app.MapGet("/x-nmos/connection/v1.2/", () => Results.Json(new[] { "single/", "bulk/" }));
		app.MapGet("/x-nmos/connection/v1.2/single/", () => Results.Json(new[] { "senders/", "receivers/" }));
		app.MapGet("/x-nmos/connection/v1.2/bulk/", () => Results.Json(new[] { "senders/", "receivers/" }));
		app.MapGet("/x-nmos/connection/v1.2/single/{kind}/", (string kind) =>
		{
			if (!TryDirection(kind, out var sender)) return NmosError(404, "Unknown connection direction.");
			return Results.Json(new[] { (sender ? _options.SenderId : _options.ReceiverId) + "/" });
		});
		app.MapGet("/x-nmos/connection/v1.2/single/{kind}/{id}/", (string kind, string id) =>
		{
			if (!TryDirection(kind, out var sender) || !_connections!.Exists(sender, id))
				return NmosError(404, "Unknown sender/receiver ID.");
			return Results.Json(sender
				? new[] { "constraints/", "staged/", "active/", "transportfile/", "transporttype/" }
				: new[] { "constraints/", "staged/", "active/", "transporttype/" });
		});
		app.MapGet("/x-nmos/connection/v1.2/single/{kind}/{id}/{resource}/",
			(string kind, string id, string resource) =>
			{
				if (!TryDirection(kind, out var sender) || !_connections!.Exists(sender, id))
					return NmosError(404, "Unknown sender/receiver ID.");
				return resource switch
				{
					"constraints" => Results.Json(_connections.Constraints(sender, id)),
					"staged" => Results.Json(_connections.Read(sender, id, active: false)),
					"active" => Results.Json(_connections.Read(sender, id, active: true)),
					"transporttype" => Results.Json("urn:x-nmos:transport:virtual"),
					"transportfile" when sender => NmosError(404, "Virtual sender has no RTP transport file or SDP."),
					_ => NmosError(404, "Unknown connection resource.")
				};
			});
		app.MapMethods("/x-nmos/connection/v1.2/single/{kind}/{id}/staged/",
			new[] { "PATCH" }, async (HttpContext context, string kind, string id) =>
			{
				if (!TryDirection(kind, out var sender) || !_connections!.Exists(sender, id))
					return NmosError(404, "Unknown sender/receiver.");
				var body = await ReadPatchAsync(context).ConfigureAwait(false);
				return body is null ? NmosError(400, "Invalid or oversized JSON patch.") :
					Outcome(_connections.Stage(sender, id, body.Value));
			});
		app.MapPost("/x-nmos/connection/v1.2/bulk/{kind}/", async (HttpContext context, string kind) =>
		{
			if (!TryDirection(kind, out var sender)) return NmosError(404, "Unknown NMOS bulk direction.");
			var body = await ReadPatchAsync(context).ConfigureAwait(false);
			return body is null ? NmosError(400, "Invalid or oversized bulk request.") :
				Outcome(_connections!.StageBulk(sender, body.Value));
		});
		app.MapGet("/rtaime/nmos/v1/topology", () => Results.Json(new
		{
			profile = "VIRTUAL_NO_MEDIA", is04 = "v1.3", is05 = "v1.2",
			node = _options.NodeId, device = _options.DeviceId,
			senders = new[] { _options.SenderId }, receivers = new[] { _options.ReceiverId },
			registry = _registration?.IsRegistered ?? false, revision = VirtualConnectionRevision,
			productionAdmission = false, detail = "Read-only preview: no physical RTP streams or authorized ControlHost route"
		}));
		return app;
	}

	private static async Task<JsonElement?> ReadPatchAsync(HttpContext context)
	{
		if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
			return null;
		try
		{
			using var body = await JsonDocument.ParseAsync(context.Request.Body,
				new JsonDocumentOptions { MaxDepth = 16 }, context.RequestAborted).ConfigureAwait(false);
			return body.RootElement.Clone();
		}
		catch (JsonException) { return null; }
	}

	private static IResult Outcome(NmosMutationOutcome outcome) =>
		outcome.Result is not null
			? Results.Json(outcome.Result, statusCode: outcome.HttpStatus)
			: NmosError(outcome.HttpStatus, outcome.Detail);

	private static IResult NmosError(int code, string reason) =>
		Results.Json(new { code, error = reason, debug = (string?)null }, statusCode: code);

	private static bool TryKind(string collection, out NmosResourceKind kind)
	{
		kind = collection switch
		{
			"devices" => NmosResourceKind.Device,
			"sources" => NmosResourceKind.Source,
			"flows" => NmosResourceKind.Flow,
			"senders" => NmosResourceKind.Sender,
			"receivers" => NmosResourceKind.Receiver,
			_ => NmosResourceKind.Node
		};
		return collection is "devices" or "sources" or "flows" or "senders" or "receivers";
	}

	private static bool TryDirection(string text, out bool sender)
	{
		sender = text == "senders";
		return text is "senders" or "receivers";
	}

	public override async ValueTask StopAsync(CancellationToken cancellationToken)
	{
		var lifetime = Interlocked.Exchange(ref _lifetime, null);
		if (lifetime is null) return;
		lifetime.Cancel();
		if (_worker is not null)
		{
			try { await _worker.WaitAsync(cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_worker = null;
		var app = Interlocked.Exchange(ref _application, null);
		if (app is not null)
		{
			try { await app.StopAsync(cancellationToken).ConfigureAwait(false); }
			finally { await app.DisposeAsync().ConfigureAwait(false); }
		}
		if (_registration is not null)
		{
			using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			try { await _registration.UnregisterAsync(cleanupTimeout.Token).ConfigureAwait(false); }
			catch (Exception) { SetHealth(IntegrationAdapterState.Degraded,
				"Virtual registry clean deregistration is unverified; registry TTL must remove stale resources."); }
		}
		_registration = null;
		Volatile.Write(ref _topology, NmosTopologySnapshot.Unavailable(
			_options.QueryApiUrl ?? "", NextObservationGeneration(), "STOPPED", "NMOS observation stopped."));
		lifetime.Dispose();
		SetHealth(IntegrationAdapterState.Stopped, "NMOS adapter stopped; no active media connection.");
	}

	private ulong NextObservationGeneration() => checked((ulong)Interlocked.Increment(ref _observationGeneration));

	internal Task RefreshNowForQualificationAsync(CancellationToken cancellationToken) =>
		_options.Mode == NmosOperatingMode.Observer
			? RefreshDiscoveryAsync(cancellationToken)
			: Task.CompletedTask;

	public override async ValueTask DisposeAsync()
	{
		await StopAsync(CancellationToken.None).ConfigureAwait(false);
		if (_ownsHttp) _http.Dispose();
	}
}
