// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace rtaime.IntegrationHost;

/// <summary>
/// IS-04 v1.3.3 registration client used only for loopback virtual resources.
/// Node-first registration, 5-second heartbeat, child-first deletion.
/// A 404 heartbeat invalidates registration; a 409 never silently changes API version.
/// </summary>
internal sealed class NmosRegistrationClient
{
	private readonly HttpClient _http;
	private readonly Uri _root;
	private readonly NmosVirtualResources _resources;
	private bool _registered;

	public NmosRegistrationClient(HttpClient http, string registryUrl, NmosVirtualResources resources)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
		_root = new Uri(registryUrl, UriKind.Absolute);
		_resources = resources ?? throw new ArgumentNullException(nameof(resources));
	}

	public bool IsRegistered => _registered;

	public async Task RegisterAsync(CancellationToken cancellationToken)
	{
		_registered = false;
		foreach (var (type, id, data) in _resources.RegistrationOrder())
		{
			using var response = await _http.PostAsJsonAsync(new Uri(_root, "resource/"),
				new { type, data }, cancellationToken).ConfigureAwait(false);
			if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
				throw new InvalidDataException($"IS-04 {type} registration was rejected; a partially registered node is not operational.");
			var expectedLocation = new Uri(_root, $"resource/{type}s/{id}");
			var location = response.Headers.Location;
			if (location is null ||
				(location.IsAbsoluteUri && (!string.Equals(location.Scheme, expectedLocation.Scheme, StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(location.Authority, expectedLocation.Authority, StringComparison.OrdinalIgnoreCase))) ||
				!string.Equals(location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString,
					expectedLocation.AbsolutePath, StringComparison.Ordinal))
				throw new InvalidDataException("IS-04 registration response Location does not match the posted resource identity.");
			if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("IS-04 registration response must return an application/json resource.");
			await response.Content.LoadIntoBufferAsync(512 * 1024, cancellationToken).ConfigureAwait(false);
			using var body = await JsonDocument.ParseAsync(
				await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
				new JsonDocumentOptions { MaxDepth = 24 }, cancellationToken).ConfigureAwait(false);
			if (body.RootElement.ValueKind != JsonValueKind.Object ||
				!body.RootElement.TryGetProperty("id", out var identifier) ||
				identifier.ValueKind != JsonValueKind.String ||
				!string.Equals(identifier.GetString(), id, StringComparison.Ordinal) ||
				!body.RootElement.TryGetProperty("version", out var version) ||
				version.ValueKind != JsonValueKind.String)
				throw new InvalidDataException("IS-04 registration response resource identity/version is invalid.");
		}
		_registered = true;
	}

	/// <returns>False on expired registration (404); caller must re-register parent-first.</returns>
	public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post,
			new Uri(_root, $"health/nodes/{_resources.Node.GetProperty("id").GetString()}"));
		using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			_registered = false;
			return false;
		}
		if (response.StatusCode != HttpStatusCode.OK)
		{
			_registered = false;
			throw new InvalidDataException("IS-04 node heartbeat was rejected by the registry.");
		}
		return true;
	}

	public async Task UnregisterAsync(CancellationToken cancellationToken)
	{
		_registered = false;
		List<Exception>? failures = null;
		foreach (var (type, id, _) in _resources.UnregistrationOrder())
		{
			using var request = new HttpRequestMessage(HttpMethod.Delete,
				new Uri(_root, $"resource/{type}s/{id}"));
			try
			{
				using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
				if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.NotFound))
					throw new InvalidDataException($"IS-04 {type} deregistration failed.");
			}
			catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
			{
				(failures ??= []).Add(exception);
			}
		}
		if (failures is not null)
			throw new AggregateException("IS-04 clean unregistration remains unverified.", failures);
	}
}
