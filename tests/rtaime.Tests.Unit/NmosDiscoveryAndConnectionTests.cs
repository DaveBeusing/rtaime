// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using rtaime.IntegrationHost;

namespace rtaime.Tests.Unit;

public sealed class NmosDiscoveryAndConnectionTests
{
	private const string NodeId = "a1111111-1111-4111-8111-111111111111";
	private const string DeviceId = "b2222222-2222-4222-8222-222222222222";
	private const string SenderId = "c3333333-3333-4333-8333-333333333333";
	private const string ReceiverId = "d4444444-4444-4444-8444-444444444444";

	[Fact]
	public void Nmos_configuration_defaults_to_safe_disabled_discovery_only_mode()
	{
		Assert.False(new IntegrationGatewayOptions().Enabled);
		Assert.Equal(NmosOperatingMode.Observer, new NmosAdapterOptions().Mode);
		var observer = Observer();
		observer.Validate(testMode: false, dryRun: false);
		Assert.Throws<ArgumentException>(() => (observer with { QueryApiUrl = "http://registry.example/x-nmos/query/v1.3/" })
			.Validate(false, false));
		Assert.Throws<ArgumentException>(() => (observer with { QueryApiUrl = "https://user:token@registry.example/x-nmos/query/v1.3/" })
			.Validate(false, false));
		Assert.Throws<ArgumentException>(() => (observer with { QueryApiUrl = "https://registry.example/x-nmos/query/v1.2/" })
			.Validate(false, false));
		Assert.Throws<ArgumentException>(() => (observer with { NodeId = NodeId }).Validate(false, false));
	}

	[Fact]
	public void Virtual_admission_requires_test_dryrun_loopback_and_persistent_unique_ids()
	{
		var config = Virtual();
		Assert.Throws<ArgumentException>(() => config.Validate(testMode: false, dryRun: true));
		Assert.Throws<ArgumentException>(() => config.Validate(testMode: true, dryRun: false));
		Assert.Throws<ArgumentException>(() => (config with { BindAddress = "0.0.0.0" }).Validate(true, true));
		Assert.Throws<ArgumentException>(() => (config with { BindAddress = "192.0.2.7" }).Validate(true, true));
		Assert.Throws<ArgumentException>(() => (config with { ReceiverId = SenderId }).Validate(true, true));
		Assert.Throws<ArgumentException>(() => (config with { NodeId = null }).Validate(true, true));
		Assert.Throws<ArgumentException>(() => (config with { RegistrationApiUrl = "http://192.0.2.5/x-nmos/registration/v1.3/" }).Validate(true, true));
		Assert.Throws<ArgumentOutOfRangeException>(() => (config with { MaxResourcesPerType = 1024 }).Validate(true, true));
		config.Validate(true, true);
	}

	[Fact]
	public async Task Registry_discovery_validates_full_parent_chain_without_claiming_local_rtp()
	{
		var resources = new NmosVirtualResources(Virtual());
		var handler = new FakeHttpHandler(request =>
		{
			var kind = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
			var data = kind switch
			{
				"nodes" => new[] { resources.Node },
				"devices" => new[] { resources.Device },
				"senders" => new[] { resources.Sender },
				"receivers" => new[] { resources.Receiver },
				_ => Array.Empty<JsonElement>()
			};
			return Json(data);
		});
		using var client = new HttpClient(handler);
		var snapshot = await new NmosRegistryDiscoveryClient(client, Observer()).ReadAsync(CancellationToken.None);
		Assert.Equal("OBSERVED", snapshot.State);
		Assert.Equal(4, snapshot.Resources.Count);
		Assert.True(snapshot.IsFresh);
		Assert.False(snapshot.HasAdmittedLocalRtpProviders);
		Assert.Single(snapshot.OfKind(NmosResourceKind.Node));
		Assert.Empty(snapshot.OfKind(NmosResourceKind.Flow));
		var plan = NmosConnectionPlanner.Preview(snapshot, SenderId, ReceiverId);
		Assert.Equal("UNVERIFIED", plan.Status);
		Assert.False(plan.EligibleForProductionActivation);
		Assert.Equal(6, handler.RequestCount);
	}

	[Fact]
	public async Task Registry_discovery_rejects_duplicates_stale_parents_and_non_json()
	{
		var resources = new NmosVirtualResources(Virtual());
		foreach (var badKind in new[] { "duplicates", "parent", "content-type" })
		{
			using var http = new HttpClient(new FakeHttpHandler(request =>
			{
				var kind = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
				if (badKind == "content-type" && kind == "nodes")
					return new HttpResponseMessage(HttpStatusCode.OK)
					{
						Content = new StringContent("[]", Encoding.UTF8, "text/plain")
					};
				if (kind == "nodes") return Json(new[] { resources.Node });
				if (kind == "devices") return Json(badKind == "parent"
					? new[] { resources.Device.Clone() } : new[] { resources.Device });
				if (kind == "senders")
					return Json(badKind == "duplicates" ? new[] { resources.Sender, resources.Sender } :
						new[] { resources.Sender });
				if (kind == "receivers") return Json(new[] { resources.Receiver });
				return Json(Array.Empty<JsonElement>());
			}));
			var options = badKind == "parent" ? Observer() with { MaxResourcesPerType = 1 } : Observer();
			// Parent case: a max-items failure is also a valid fail-closed negative observation.
			if (badKind == "parent")
			{
				var orphan = new NmosVirtualResources(Virtual());
				http.Dispose();
				using var orphanClient = new HttpClient(new FakeHttpHandler(request =>
				{
					var kind = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
					return Json(kind == "devices" ? new[] { orphan.Device } : Array.Empty<JsonElement>());
				}));
				await Assert.ThrowsAsync<InvalidDataException>(() =>
					new NmosRegistryDiscoveryClient(orphanClient, options).ReadAsync(CancellationToken.None));
			}
			else
				await Assert.ThrowsAsync<InvalidDataException>(() =>
					new NmosRegistryDiscoveryClient(http, options).ReadAsync(CancellationToken.None));
		}
	}

	[Fact]
	public async Task Registry_outage_invalidates_previous_topology_and_recovery_uses_new_generation()
	{
		var published = false;
		var resources = new NmosVirtualResources(Virtual());
		using var client = new HttpClient(new FakeHttpHandler(request =>
		{
			if (!published) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
			var kind = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
			return Json(kind switch
			{
				"nodes" => new[] { resources.Node },
				"devices" => new[] { resources.Device },
				"senders" => new[] { resources.Sender },
				"receivers" => new[] { resources.Receiver },
				_ => Array.Empty<JsonElement>()
			});
		}));
		await using var adapter = new NmosIntegrationAdapter(new IntegrationAdapterOptions
		{
			Id = "nmos",
			Kind = IntegrationAdapterKind.Nmos,
			Nmos = Observer()
		}, Gateway(Observer()), client);
		await adapter.StartAsync(_ => throw new InvalidOperationException("NMOS observer must never emit a Production trigger."),
			CancellationToken.None);
		Assert.Equal("STALE", adapter.Topology.State);
		Assert.Empty(adapter.Topology.Resources);
		Assert.Equal(IntegrationAdapterState.Degraded, adapter.Health.State);
		var firstGeneration = adapter.Topology.ObservationGeneration;

		published = true;
		await adapter.RefreshNowForQualificationAsync(CancellationToken.None);
		Assert.Equal("OBSERVED", adapter.Topology.State);
		Assert.Equal(4, adapter.Topology.Resources.Count);
		Assert.True(adapter.Topology.ObservationGeneration > firstGeneration);
		var observedGeneration = adapter.Topology.ObservationGeneration;

		published = false;
		await adapter.RefreshNowForQualificationAsync(CancellationToken.None);
		Assert.Equal("STALE", adapter.Topology.State);
		Assert.Empty(adapter.Topology.Resources);
		Assert.True(adapter.Topology.ObservationGeneration > observedGeneration);
		Assert.False(NmosConnectionPlanner.Preview(adapter.Topology, SenderId, ReceiverId)
			.EligibleForProductionActivation);
		await adapter.StopAsync(CancellationToken.None);
		Assert.Equal("STOPPED", adapter.Topology.State);
	}

	[Fact]
	public async Task Registry_non_success_and_oversized_collection_fail_without_partial_topology()
	{
		using var missing = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new NmosRegistryDiscoveryClient(missing, Observer()).ReadAsync(CancellationToken.None));
		using var oversized = new HttpClient(new FakeHttpHandler(request =>
		{
			if (request.RequestUri!.AbsolutePath.EndsWith("/nodes/", StringComparison.Ordinal))
			{
				var resources = new NmosVirtualResources(Virtual());
				return Json(new[] { resources.Node, resources.Node });
			}
			return Json(Array.Empty<JsonElement>());
		}));
		await Assert.ThrowsAsync<InvalidDataException>(() =>
			new NmosRegistryDiscoveryClient(oversized, Observer() with { MaxResourcesPerType = 1 })
				.ReadAsync(CancellationToken.None));
	}

	[Fact]
	public async Task Registration_orders_resources_recovers_404_heartbeat_and_deletes_children_first()
	{
		var resources = new NmosVirtualResources(Virtual());
		var requests = new List<string>();
		var ordered = resources.RegistrationOrder().ToArray();
		var registered = 0;
		var heartbeats = 0;
		using var http = new HttpClient(new FakeHttpHandler(request =>
		{
			var method = request.Method.Method;
			var path = request.RequestUri!.AbsolutePath;
			requests.Add(method + " " + path);
			if (path.Contains("/health/nodes/", StringComparison.Ordinal))
			{
				heartbeats++;
				return new HttpResponseMessage(heartbeats == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK);
			}
			if (method == "POST")
			{
				var (kind, id, data) = ordered[registered++ % ordered.Length];
				var result = Json(data);
				result.StatusCode = HttpStatusCode.Created;
				result.Headers.Location = new Uri($"/x-nmos/registration/v1.3/resource/{kind}s/{id}", UriKind.Relative);
				return result;
			}
			return new HttpResponseMessage(HttpStatusCode.NoContent);
		}));
		var session = new NmosRegistrationClient(http, "http://127.0.0.1:3211/x-nmos/registration/v1.3/", resources);
		await session.RegisterAsync(CancellationToken.None);
		Assert.True(session.IsRegistered);
		Assert.False(await session.HeartbeatAsync(CancellationToken.None));
		Assert.False(session.IsRegistered);
		await session.RegisterAsync(CancellationToken.None);
		Assert.True(await session.HeartbeatAsync(CancellationToken.None));
		await session.UnregisterAsync(CancellationToken.None);
		Assert.False(session.IsRegistered);
		Assert.Equal(4, requests.Count(x => x == "POST /x-nmos/registration/v1.3/resource") / 2);
		Assert.EndsWith($"/resource/nodes/{NodeId}", requests.Last(), StringComparison.Ordinal);
		Assert.Contains("DELETE /x-nmos/registration/v1.3/resource/receivers/" + ReceiverId, requests);
	}

	[Fact]
	public async Task Registry_version_conflict_is_not_silently_downgraded()
	{
		var resources = new NmosVirtualResources(Virtual());
		using var client = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)));
		var session = new NmosRegistrationClient(client, "http://127.0.0.1:3211/x-nmos/registration/v1.3/", resources);
		await Assert.ThrowsAsync<InvalidDataException>(() => session.RegisterAsync(CancellationToken.None));
		Assert.False(session.IsRegistered);
	}

	[Fact]
	public async Task Registry_conflict_cleanup_does_not_delete_unaccepted_foreign_resource_ids()
	{
		var resources = new NmosVirtualResources(Virtual());
		var order = resources.RegistrationOrder().ToArray();
		var posts = 0;
		var deletes = new List<string>();
		using var http = new HttpClient(new FakeHttpHandler(request =>
		{
			if (request.Method == HttpMethod.Delete)
			{
				deletes.Add(request.RequestUri!.AbsolutePath);
				return new HttpResponseMessage(HttpStatusCode.NoContent);
			}
			if (++posts == 2) return new HttpResponseMessage(HttpStatusCode.Conflict);
			var (kind, id, data) = order[0];
			var registered = Json(data);
			registered.StatusCode = HttpStatusCode.Created;
			registered.Headers.Location = new Uri($"/x-nmos/registration/v1.3/resource/{kind}s/{id}", UriKind.Relative);
			return registered;
		}));
		var session = new NmosRegistrationClient(http,
			"http://127.0.0.1:3211/x-nmos/registration/v1.3/", resources);
		await Assert.ThrowsAsync<InvalidDataException>(() => session.RegisterAsync(CancellationToken.None));
		Assert.False(session.IsRegistered);
		await session.UnregisterAsync(CancellationToken.None);
		Assert.Single(deletes);
		Assert.Contains("/resource/nodes/" + NodeId, deletes[0], StringComparison.Ordinal);
		Assert.DoesNotContain(deletes, path => path.Contains("/devices/", StringComparison.Ordinal));
	}

	[Fact]
	public void Is05_staging_rejects_sdp_stream_activation_scheduled_time_and_unknown_id()
	{
		var connections = new NmosVirtualConnections(SenderId, ReceiverId);
		Assert.Equal(404, connections.Stage(false, Guid.NewGuid().ToString("D"),
			JsonDocument.Parse("{}").RootElement).HttpStatus);
		foreach (var bad in new[]
		{
			"{\"master_enable\":true,\"activation\":{\"mode\":\"activate_immediate\"}}",
			"{\"sender_id\":\"c3333333-3333-4333-8333-333333333333\"}",
			"{\"transport_file\":{\"type\":\"application/sdp\",\"data\":\"v=0\\r\\n\"}}",
			"{\"transport_params\":[{\"destination_port\":5004}]}",
			"{\"activation\":{\"mode\":\"activate_scheduled_relative\",\"requested_time\":\"5:0\"}}"
		})
		{
			using var doc = JsonDocument.Parse(bad);
			Assert.Equal(409, connections.Stage(false, ReceiverId, doc.RootElement).HttpStatus);
		}
		Assert.Equal(0UL, connections.Revision);
	}

	[Fact]
	public void Is05_disabled_immediate_staging_and_bulk_are_atomic_without_program_route()
	{
		var connections = new NmosVirtualConnections(SenderId, ReceiverId);
		using var immediate = JsonDocument.Parse(
			"{\"master_enable\":false,\"activation\":{\"mode\":\"activate_immediate\"},\"transport_params\":[{}]}");
		var result = connections.Stage(false, ReceiverId, immediate.RootElement);
		Assert.Equal(200, result.HttpStatus);
		Assert.Equal(1UL, connections.Revision);
		using var bulk = JsonDocument.Parse(
			"[{\"id\":\"d4444444-4444-4444-8444-444444444444\",\"params\":{\"master_enable\":false}}," +
			"{\"id\":\"d4444444-4444-4444-8444-444444444444\",\"params\":{\"master_enable\":true}}]");
		Assert.Equal(409, connections.StageBulk(false, bulk.RootElement).HttpStatus);
		Assert.Equal(1UL, connections.Revision);
		using var valid = JsonDocument.Parse(
			"[{\"id\":\"d4444444-4444-4444-8444-444444444444\",\"params\":{\"master_enable\":false}}]");
		Assert.Equal(200, connections.StageBulk(false, valid.RootElement).HttpStatus);
		Assert.Equal(2UL, connections.Revision);
		var serialized = JsonSerializer.Serialize(connections.Read(false, ReceiverId, true));
		Assert.Contains("\"master_enable\":false", serialized, StringComparison.Ordinal);
		Assert.Contains("\"sender_id\":null", serialized, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Virtual_node_rest_serves_stable_identity_and_rejects_activation_without_triggers()
	{
		var port = ReservePort();
		var config = Virtual() with { Port = port };
		var options = Gateway(config);
		var calls = 0;
		await using var adapter = new NmosIntegrationAdapter(new IntegrationAdapterOptions
		{
			Id = "nmos",
			Kind = IntegrationAdapterKind.Nmos,
			Nmos = config
		}, options);
		await adapter.StartAsync(_ => { calls++; return true; }, CancellationToken.None);
		using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
		var node = await client.GetFromJsonAsync<JsonElement>("/x-nmos/node/v1.3/self/");
		Assert.Equal(NodeId, node.GetProperty("id").GetString());
		Assert.Equal("VIRTUAL_NO_MEDIA", node.GetProperty("tags").GetProperty("urn:x-rtaime:qualification")[0].GetString());
		var senders = await client.GetFromJsonAsync<JsonElement[]>("/x-nmos/node/v1.3/senders/");
		Assert.Equal(SenderId, Assert.Single(senders!).GetProperty("id").GetString());
		var flows = await client.GetFromJsonAsync<JsonElement[]>("/x-nmos/node/v1.3/flows/");
		Assert.Empty(flows!);
		using var unsafeRequest = new StringContent(
			"{\"sender_id\":\"" + SenderId + "\",\"master_enable\":true,\"activation\":{\"mode\":\"activate_immediate\"}}",
			Encoding.UTF8, "application/json");
		using var rejected = await client.PatchAsync(
			$"/x-nmos/connection/v1.2/single/receivers/{ReceiverId}/staged", unsafeRequest);
		Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
		Assert.Equal(0UL, adapter.VirtualConnectionRevision);
		using var disable = new StringContent(
			"{\"master_enable\":false,\"activation\":{\"mode\":\"activate_immediate\"}}",
			Encoding.UTF8, "application/json");
		using var accepted = await client.PatchAsync(
			$"/x-nmos/connection/v1.2/single/receivers/{ReceiverId}/staged", disable);
		Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
		var active = await client.GetFromJsonAsync<JsonElement>(
			$"/x-nmos/connection/v1.2/single/receivers/{ReceiverId}/active/");
		Assert.False(active.GetProperty("master_enable").GetBoolean());
		Assert.Equal(0, calls);
		Assert.Contains("VIRTUAL", adapter.Health.Detail, StringComparison.Ordinal);
		await adapter.StopAsync(CancellationToken.None);
	}

	private static int ReservePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	private static NmosAdapterOptions Observer() => new()
	{
		Mode = NmosOperatingMode.Observer,
		QueryApiUrl = "https://registry.example/x-nmos/query/v1.3/"
	};

	private static NmosAdapterOptions Virtual() => new()
	{
		Mode = NmosOperatingMode.VirtualInterop,
		NodeId = NodeId, DeviceId = DeviceId, SenderId = SenderId, ReceiverId = ReceiverId
	};

	private static IntegrationGatewayOptions Gateway(NmosAdapterOptions config) => new()
	{
		Enabled = true, TestMode = true, DryRun = true,
		Upstream = new IntegrationUpstreamOptions { Endpoint = "https://127.0.0.1:55101" },
		Adapters = [new IntegrationAdapterOptions { Id = "nmos", Kind = IntegrationAdapterKind.Nmos, Nmos = config }]
	};

	private static HttpResponseMessage Json<T>(T value) =>
		new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

	private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
	{
		public int RequestCount { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestCount++;
			return Task.FromResult(response(request));
		}
	}
}
