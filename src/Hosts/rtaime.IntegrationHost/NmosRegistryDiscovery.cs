// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace rtaime.IntegrationHost;

/// <summary>Discovery is observational and never grants rtaime media capability or Production Authority.</summary>
public enum NmosResourceKind { Node, Device, Source, Flow, Sender, Receiver }

public sealed record NmosResourceSummary(
	NmosResourceKind Kind,
	string Id,
	string Label,
	string Version,
	string? ParentId,
	string? Transport,
	string? Format);

public sealed record NmosTopologySnapshot(
	string State,
	string QueryApiUrl,
	DateTimeOffset ObservedAtUtc,
	ulong ObservationGeneration,
	IReadOnlyList<NmosResourceSummary> Resources,
	string Detail)
{
	public bool IsFresh => State == "OBSERVED";
	public bool HasAdmittedLocalRtpProviders => false;
	public IReadOnlyList<NmosResourceSummary> OfKind(NmosResourceKind kind) =>
		Resources.Where(resource => resource.Kind == kind).ToArray();

	public static NmosTopologySnapshot Unavailable(string url, ulong generation, string state, string detail) =>
		new(state, url, DateTimeOffset.UtcNow, generation, Array.Empty<NmosResourceSummary>(), detail);
}

public sealed record NmosConnectionPlan(
	string SenderId,
	string ReceiverId,
	ulong ObservationGeneration,
	string Status,
	string Detail,
	bool EligibleForProductionActivation);

/// <summary>Read-only preview. A matching transport is not proof of SDP, NIC, RTP or ControlHost admission.</summary>
public static class NmosConnectionPlanner
{
	public static NmosConnectionPlan Preview(NmosTopologySnapshot snapshot, string senderId, string receiverId)
	{
		if (!snapshot.IsFresh)
			return new(senderId, receiverId, snapshot.ObservationGeneration, "UNVERIFIED", "Registry state is stale or unavailable.", false);
		var sender = snapshot.Resources.SingleOrDefault(resource =>
			resource.Kind == NmosResourceKind.Sender && resource.Id == senderId);
		var receiver = snapshot.Resources.SingleOrDefault(resource =>
			resource.Kind == NmosResourceKind.Receiver && resource.Id == receiverId);
		if (sender is null || receiver is null)
			return new(senderId, receiverId, snapshot.ObservationGeneration, "REJECTED",
				"Sender or Receiver is absent from the same complete registry observation.", false);
		if (string.IsNullOrWhiteSpace(sender.Transport) || sender.Transport != receiver.Transport)
			return new(senderId, receiverId, snapshot.ObservationGeneration, "REJECTED",
				"Sender/Receiver transport types are incompatible.", false);
		return new(senderId, receiverId, snapshot.ObservationGeneration, "UNVERIFIED",
			"Read-only NMOS topology match; no locally admitted ST 2110/RTP provider, SDP constraints or authoritative ControlHost execution plan.", false);
	}
}

/// <summary>
/// Pins IS-04 Query API v1.3 (spec v1.3.3). Reads complete bounded resource sets,
/// refuses redirect/error/invalid JSON and never publishes partial/stale resources.
/// No registry mutations, browser URLs, DNS-SD broadcasts or remote production commands.
/// </summary>
public sealed class NmosRegistryDiscoveryClient
{
	public const string QueryVersion = "v1.3";
	public const string SpecificationRelease = "v1.3.3";
	private static readonly (NmosResourceKind Kind, string Path)[] Collections =
	[
		(NmosResourceKind.Node, "nodes"),
		(NmosResourceKind.Device, "devices"),
		(NmosResourceKind.Source, "sources"),
		(NmosResourceKind.Flow, "flows"),
		(NmosResourceKind.Sender, "senders"),
		(NmosResourceKind.Receiver, "receivers")
	];
	private static readonly Regex UuidPattern = new(
		"^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private readonly HttpClient _http;
	private readonly Uri _queryRoot;
	private readonly int _maxPerType;
	private ulong _generation;

	public NmosRegistryDiscoveryClient(HttpClient http, NmosAdapterOptions options)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
		ArgumentNullException.ThrowIfNull(options);
		if (string.IsNullOrWhiteSpace(options.QueryApiUrl))
			throw new ArgumentException("NMOS registry Query API URL is required.", nameof(options));
		_queryRoot = new Uri(options.QueryApiUrl, UriKind.Absolute);
		_maxPerType = options.MaxResourcesPerType;
	}

	public async Task<NmosTopologySnapshot> ReadAsync(CancellationToken cancellationToken)
	{
		var resources = new List<NmosResourceSummary>(6 * Math.Min(_maxPerType, 128));
		var identifiers = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (kind, path) in Collections)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_queryRoot, path + "/"));
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
			using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (response.StatusCode != HttpStatusCode.OK)
				throw new InvalidDataException($"IS-04 {kind} Query returned an unsupported status.");
			if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("IS-04 Query response must carry application/json.");
			await response.Content.LoadIntoBufferAsync(512 * 1024, cancellationToken).ConfigureAwait(false);
			using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
			{
				MaxDepth = 24,
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow
			}, cancellationToken).ConfigureAwait(false);
			if (document.RootElement.ValueKind != JsonValueKind.Array ||
				document.RootElement.GetArrayLength() > _maxPerType)
				throw new InvalidDataException("IS-04 Query collection is malformed or exceeds configured bounds.");
			foreach (var entry in document.RootElement.EnumerateArray())
			{
				var item = ParseResource(entry, kind);
				if (!identifiers.Add(item.Id))
					throw new InvalidDataException("IS-04 Query returned duplicate resource identities.");
				resources.Add(item);
			}
		}
		ValidateParents(resources);
		var generation = ++_generation;
		return new("OBSERVED", _queryRoot.AbsoluteUri, DateTimeOffset.UtcNow, generation,
			Array.AsReadOnly(resources.ToArray()),
			$"Validated {resources.Count} NMOS resources across six v1.3 Query collections; no local RTP/SDP provider admitted.");
	}

	private static NmosResourceSummary ParseResource(JsonElement json, NmosResourceKind kind)
	{
		if (json.ValueKind != JsonValueKind.Object)
			throw new InvalidDataException("NMOS resources must be JSON objects.");
		var id = RequiredString(json, "id", 36);
		if (!UuidPattern.IsMatch(id))
			throw new InvalidDataException("NMOS resource ID is not a valid lower-case UUID.");
		var version = RequiredString(json, "version", 64);
		var tai = version.Split(':');
		if (tai.Length != 2 || !ulong.TryParse(tai[0], out _) ||
			!uint.TryParse(tai[1], out var nanos) || nanos >= 1_000_000_000)
			throw new InvalidDataException("NMOS resource version is not a valid TAI seconds:nanoseconds timestamp.");
		var label = RequiredString(json, "label", 256);
		var parent = kind switch
		{
			NmosResourceKind.Device => RequiredUuid(json, "node_id"),
			NmosResourceKind.Source or NmosResourceKind.Receiver or NmosResourceKind.Sender => RequiredUuid(json, "device_id"),
			NmosResourceKind.Flow => RequiredUuid(json, "source_id"),
			_ => null
		};
		string? transport = null;
		if (kind is NmosResourceKind.Sender or NmosResourceKind.Receiver)
		{
			transport = RequiredString(json, "transport", 128);
			if (!transport.StartsWith("urn:x-nmos:transport:", StringComparison.Ordinal))
				throw new InvalidDataException("Sender/Receiver transport type must be an NMOS transport URN.");
		}
		var format = kind is NmosResourceKind.Source or NmosResourceKind.Flow or NmosResourceKind.Receiver
			? RequiredString(json, "format", 128)
			: null;
		if (format is not null && format is not ("urn:x-nmos:format:video" or "urn:x-nmos:format:audio" or "urn:x-nmos:format:data" or "urn:x-nmos:format:mux"))
			throw new InvalidDataException("Unsupported NMOS media format URN.");
		if (!json.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
			throw new InvalidDataException("NMOS resource tags are required for this v1.3 profile.");
		if (!json.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
			throw new InvalidDataException("NMOS resource description is required for this v1.3 profile.");
		return new(kind, id, label, version, parent, transport, format);
	}

	private static string RequiredUuid(JsonElement json, string field)
	{
		var id = RequiredString(json, field, 36);
		if (!UuidPattern.IsMatch(id))
			throw new InvalidDataException($"NMOS parent field '{field}' is not a valid UUID.");
		return id;
	}

	private static string RequiredString(JsonElement json, string property, int maxLength)
	{
		if (!json.TryGetProperty(property, out var value) ||
			value.ValueKind != JsonValueKind.String ||
			string.IsNullOrWhiteSpace(value.GetString()) ||
			value.GetString()!.Length > maxLength)
			throw new InvalidDataException($"NMOS '{property}' must be a bounded string.");
		return value.GetString()!;
	}

	private static void ValidateParents(IReadOnlyList<NmosResourceSummary> resources)
	{
		var ids = resources.ToDictionary(resource => resource.Id, resource => resource.Kind, StringComparer.Ordinal);
		foreach (var resource in resources)
		{
			if (resource.ParentId is null) continue;
			var requiredKind = resource.Kind switch
			{
				NmosResourceKind.Device => NmosResourceKind.Node,
				NmosResourceKind.Flow => NmosResourceKind.Source,
				_ => NmosResourceKind.Device
			};
			if (!ids.TryGetValue(resource.ParentId, out var actual) || actual != requiredKind)
				throw new InvalidDataException("NMOS resource has a missing or mismatched parent in this registry snapshot.");
		}
	}
}
