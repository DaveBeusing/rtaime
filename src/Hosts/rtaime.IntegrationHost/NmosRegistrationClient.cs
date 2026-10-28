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
		foreach (var (type, _, data) in _resources.RegistrationOrder())
		{
			using var response = await _http.PostAsJsonAsync(new Uri(_root, "resource/"),
				new { type, data }, cancellationToken).ConfigureAwait(false);
			if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
				throw new InvalidDataException($"IS-04 {type} registration was rejected; a partially registered node is not operational.");
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
