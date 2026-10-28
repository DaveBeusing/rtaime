// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;

namespace rtaime.IntegrationHost;

/// <summary>
/// Deterministic, explicitly virtual IS-04 Node/Device/Sender/Receiver inventory.
/// No Source/Flow/SDP is invented: there is no admitted RTP provider.
/// IDs are supplied by persisted operator configuration, never re-randomized on restart.
/// </summary>
internal sealed class NmosVirtualResources
{
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
	private readonly Dictionary<string, JsonElement> _byId = new(StringComparer.Ordinal);
	private readonly Dictionary<NmosResourceKind, JsonElement[]> _byKind = new();

	public NmosVirtualResources(NmosAdapterOptions config)
	{
		var host = config.BindAddress;
		var origin = $"http://{host}:{config.Port}";
		var nodeUrl = $"{origin}/x-nmos/node/v1.3/";
		var connectionUrl = $"{origin}/x-nmos/connection/v1.2/";
		var tai = NmosTaiTimestamp.Now();
		var tags = new Dictionary<string, string[]> { ["urn:x-rtaime:qualification"] = ["VIRTUAL_NO_MEDIA"] };
		Node = Element(new
		{
			id = Id(config.NodeId), version = tai, label = "rtaime VIRTUAL NMOS Test Node",
			description = "Loopback-only synthetic interoperability endpoint; no media signal or RTP provider",
			tags, href = nodeUrl, caps = new { },
			api = new
			{
				versions = new[] { "v1.3" },
				endpoints = new[] { new { host, port = config.Port, protocol = "http", authorization = false } }
			},
			services = Array.Empty<object>(),
			clocks = Array.Empty<object>(),
			interfaces = new[]
			{
				new { name = "virtual0", chassis_id = (string?)null, port_id = "00-00-00-00-00-00" }
			}
		});
		Device = Element(new
		{
			id = Id(config.DeviceId), version = tai, label = "rtaime VIRTUAL Test Device",
			description = "Synthetic Device, not a physical stream or ST 2110 transmitter", tags,
			type = "urn:x-nmos:device:generic",
			node_id = Id(config.NodeId),
			senders = new[] { Id(config.SenderId) },
			receivers = new[] { Id(config.ReceiverId) },
			controls = new[] { new { type = "urn:x-nmos:control:sr-ctrl/v1.2", href = connectionUrl, authorization = false } }
		});
		Sender = Element(new
		{
			id = Id(config.SenderId), version = tai, label = "VIRTUAL DISABLED Sender (No Signal)",
			description = "Synthetic sender with no Flow, transport file, active stream or SDP", tags,
			flow_id = (string?)null,
			transport = "urn:x-nmos:transport:virtual",
			device_id = Id(config.DeviceId),
			manifest_href = (string?)null,
			interface_bindings = new[] { "virtual0" },
			caps = new { },
			subscription = new { receiver_id = (string?)null, active = false }
		});
		Receiver = Element(new
		{
			id = Id(config.ReceiverId), version = tai, label = "VIRTUAL DISABLED Receiver (No Signal)",
			description = "Synthetic receiver; staged references do not enable RTP reception", tags,
			device_id = Id(config.DeviceId),
			transport = "urn:x-nmos:transport:virtual",
			interface_bindings = new[] { "virtual0" },
			subscription = new { sender_id = (string?)null, active = false },
			format = "urn:x-nmos:format:video",
			caps = new { media_types = new[] { "video/raw" } }
		});
		Add(NmosResourceKind.Node, Node);
		Add(NmosResourceKind.Device, Device);
		Add(NmosResourceKind.Sender, Sender);
		Add(NmosResourceKind.Receiver, Receiver);
		_byKind[NmosResourceKind.Source] = [];
		_byKind[NmosResourceKind.Flow] = [];
	}

	public JsonElement Node { get; }
	public JsonElement Device { get; }
	public JsonElement Sender { get; }
	public JsonElement Receiver { get; }
	public JsonElement[] List(NmosResourceKind kind) => _byKind[kind];
	public bool TryFind(string id, out JsonElement resource) => _byId.TryGetValue(id, out resource);

	public IEnumerable<(string Type, string Id, JsonElement Data)> RegistrationOrder()
	{
		yield return ("node", Node.GetProperty("id").GetString()!, Node);
		yield return ("device", Device.GetProperty("id").GetString()!, Device);
		yield return ("sender", Sender.GetProperty("id").GetString()!, Sender);
		yield return ("receiver", Receiver.GetProperty("id").GetString()!, Receiver);
	}

	public IEnumerable<(string Type, string Id, JsonElement Data)> UnregistrationOrder() =>
		RegistrationOrder().Reverse();

	private void Add(NmosResourceKind kind, JsonElement resource)
	{
		var id = resource.GetProperty("id").GetString()!;
		if (!_byId.TryAdd(id, resource))
			throw new InvalidDataException("Virtual NMOS resource identities must be globally unique.");
		_byKind[kind] = [resource];
	}

	private static string Id(string? value) => Guid.Parse(value!).ToString("D");
	private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}

/// <summary>TAI seconds:nanoseconds are only reference-node timestamps, never scheduled playout authority.</summary>
internal static class NmosTaiTimestamp
{
	public static string Now()
	{
		// This demonstrator uses only unchanging synthetic resources.
		// TAI/UTC leap-second correctness must be externally qualified for timed production.
		var utc = DateTimeOffset.UtcNow;
		return $"{utc.ToUnixTimeSeconds() + 37}:{utc.Millisecond * 1_000_000:D9}";
	}
}
