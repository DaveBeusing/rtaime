// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace rtaime.IntegrationHost;

public sealed class CompanionIntegrationAdapter : IntegrationAdapterBase
{
	private readonly CompanionAdapterOptions _options;
	private readonly IReadOnlyList<IntegrationMappingOptions> _mappings;
	private readonly IReadOnlyList<IntegrationFeedbackMappingOptions> _feedbackMappings;
	private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
	private IntegrationInputSink? _input;
	private WebApplication? _application;
	private string? _token;
	private IntegrationFeedbackSnapshot? _latestFeedback;

	public CompanionIntegrationAdapter(
		IntegrationAdapterOptions adapter,
		IReadOnlyList<IntegrationMappingOptions> mappings,
		IReadOnlyList<IntegrationFeedbackMappingOptions> feedbackMappings)
		: base(adapter.Id, IntegrationAdapterKind.Companion, adapter.Required)
	{
		_options = adapter.Companion ?? throw new ArgumentException("Companion options are required.", nameof(adapter));
		_mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
		_feedbackMappings = feedbackMappings ?? throw new ArgumentNullException(nameof(feedbackMappings));
	}

	public override async ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (_application is not null) return;
		_input = input;
		_token = Environment.GetEnvironmentVariable(_options.BearerTokenEnvironmentVariable!);
		if (string.IsNullOrWhiteSpace(_token))
		{
			SetHealth(IntegrationAdapterState.Failed, "Companion bearer token environment reference resolved to an empty value.");
			throw new InvalidOperationException("Companion bearer token is unavailable.");
		}

		SetHealth(IntegrationAdapterState.Starting, "Starting Companion-compatible HTTP/WebSocket surface.");
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
		builder.Logging.ClearProviders();
		builder.WebHost.ConfigureKestrel(server =>
		{
			server.Limits.MaxRequestBodySize = _options.MaxRequestBytes;
			server.Listen(IPAddress.Parse(_options.BindAddress), _options.Port);
		});
		var app = builder.Build();
		app.UseWebSockets();

		app.MapGet("/rtaime/integration/v1/health", (HttpContext context) =>
			Authorized(context)
				? Results.Json(new { adapter = Id, health = Health })
				: Results.Unauthorized());

		app.MapGet("/rtaime/integration/v1/state", (HttpContext context) =>
			Authorized(context)
				? Results.Json(ProjectFeedback(_latestFeedback))
				: Results.Unauthorized());

		app.MapGet("/rtaime/integration/v1/actions", (HttpContext context) =>
			Authorized(context)
				? Results.Json(_mappings.Select(mapping => new { mapping.Id, key = mapping.TriggerKey, action = mapping.Action.Kind.ToString() }))
				: Results.Unauthorized());

		app.MapMethods("/rtaime/integration/v1/actions/{key}", new[] { "GET", "POST" }, async (HttpContext context, string key) =>
		{
			if (!Authorized(context)) return Results.Unauthorized();
			if (!_mappings.Any(mapping => string.Equals(mapping.TriggerKey, key, StringComparison.Ordinal)))
				return Results.NotFound();
			double? numeric = null;
			string? text = null;
			bool? boolean = null;
			if (context.Request.Query.TryGetValue("value", out var queryValue))
				ParseValue(queryValue.ToString(), out numeric, out text, out boolean);
			else if (context.Request.ContentLength is > 0)
			{
				using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
				if (document.RootElement.TryGetProperty("value", out var value))
				{
					if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) numeric = number;
					else if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) boolean = value.GetBoolean();
					else if (value.ValueKind == JsonValueKind.String) ParseValue(value.GetString() ?? string.Empty, out numeric, out text, out boolean);
				}
			}
			var accepted = _input?.Invoke(new IntegrationTrigger(Id, key, numeric, text, boolean)) ?? false;
			return accepted ? Results.Accepted() : Results.StatusCode(StatusCodes.Status429TooManyRequests);
		});

		app.Map("/rtaime/integration/v1/feedback", async context =>
		{
			if (!Authorized(context))
			{
				context.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}
			if (!context.WebSockets.IsWebSocketRequest)
			{
				context.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}
			using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
			var id = Guid.NewGuid();
			var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(8)
			{
				FullMode = BoundedChannelFullMode.DropOldest,
				SingleReader = true,
				SingleWriter = false
			});
			_subscribers[id] = channel;
			try
			{
				if (_latestFeedback is not null)
					channel.Writer.TryWrite(JsonSerializer.Serialize(ProjectFeedback(_latestFeedback)));
				await foreach (var payload in channel.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
				{
					var bytes = Encoding.UTF8.GetBytes(payload);
					await socket.SendAsync(bytes, WebSocketMessageType.Text, true, context.RequestAborted).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
			{
			}
			catch (WebSocketException)
			{
			}
			finally
			{
				_subscribers.TryRemove(id, out _);
			}
		});

		try
		{
			await app.StartAsync(cancellationToken).ConfigureAwait(false);
			_application = app;
			SetHealth(IntegrationAdapterState.Healthy, $"Companion-compatible surface listening on {_options.BindAddress}:{_options.Port}.");
		}
		catch
		{
			await app.DisposeAsync().ConfigureAwait(false);
			SetHealth(IntegrationAdapterState.Failed, "Companion-compatible surface failed to start.");
			throw;
		}
	}

	public override ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken)
	{
		_latestFeedback = snapshot;
		var payload = JsonSerializer.Serialize(ProjectFeedback(snapshot));
		foreach (var channel in _subscribers.Values)
			channel.Writer.TryWrite(payload);
		return ValueTask.CompletedTask;
	}

	public override async ValueTask StopAsync(CancellationToken cancellationToken)
	{
		var app = Interlocked.Exchange(ref _application, null);
		if (app is null) return;
		foreach (var channel in _subscribers.Values)
			channel.Writer.TryComplete();
		_subscribers.Clear();
		try { await app.StopAsync(cancellationToken).ConfigureAwait(false); }
		finally { await app.DisposeAsync().ConfigureAwait(false); }
		_input = null;
		_token = null;
		SetHealth(IntegrationAdapterState.Stopped, "Companion adapter is stopped.");
	}

	private object ProjectFeedback(IntegrationFeedbackSnapshot? snapshot)
	{
		if (snapshot is null) return new { sequence = 0UL, values = Array.Empty<object>() };
		return new
		{
			snapshot.Sequence,
			snapshot.ObservedAtUtc,
			values = _feedbackMappings.Select(mapping => new
			{
				source = mapping.Source.ToString(),
				parameter = mapping.SourceParameter,
				key = mapping.TargetKey,
				value = snapshot.Resolve(mapping)
			})
		};
	}

	private bool Authorized(HttpContext context)
	{
		if (string.IsNullOrEmpty(_token)) return false;
		var header = context.Request.Headers.Authorization.ToString();
		const string prefix = "Bearer ";
		if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
		var supplied = Encoding.UTF8.GetBytes(header[prefix.Length..]);
		var expected = Encoding.UTF8.GetBytes(_token);
		return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
	}

	private static void ParseValue(string value, out double? numeric, out string? text, out bool? boolean)
	{
		numeric = null;
		text = null;
		boolean = null;
		if (bool.TryParse(value, out var parsedBoolean)) boolean = parsedBoolean;
		else if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedNumeric)) numeric = parsedNumeric;
		else text = value;
	}
}
