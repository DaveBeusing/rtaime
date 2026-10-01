// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;

namespace rtaime.IntegrationHost;

public sealed record DiscreteInputEvent(string ChannelId, bool State, DateTimeOffset ObservedAtUtc);

public interface IDiscreteIoBackend : IAsyncDisposable
{
	event Action<DiscreteInputEvent>? InputChanged;
	string State { get; }
	ValueTask StartAsync(CancellationToken cancellationToken);
	ValueTask SetOutputAsync(string channelId, bool state, CancellationToken cancellationToken);
	ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed class VirtualDiscreteIoBackend : IDiscreteIoBackend
{
	private readonly ConcurrentDictionary<string, bool> _outputs = new(StringComparer.Ordinal);
	public event Action<DiscreteInputEvent>? InputChanged;
	public string State { get; private set; } = "STOPPED";
	public IReadOnlyDictionary<string, bool> Outputs => new Dictionary<string, bool>(_outputs, StringComparer.Ordinal);

	public ValueTask StartAsync(CancellationToken cancellationToken)
	{
		State = "HEALTHY";
		return ValueTask.CompletedTask;
	}

	public void InjectInput(string channelId, bool state, DateTimeOffset? observedAtUtc = null)
	{
		if (State != "HEALTHY") throw new InvalidOperationException("Virtual discrete backend is not active.");
		if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel identity is required.", nameof(channelId));
		InputChanged?.Invoke(new DiscreteInputEvent(channelId.Trim(), state, observedAtUtc ?? DateTimeOffset.UtcNow));
	}

	public ValueTask SetOutputAsync(string channelId, bool state, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel identity is required.", nameof(channelId));
		_outputs[channelId.Trim()] = state;
		return ValueTask.CompletedTask;
	}

	public ValueTask StopAsync(CancellationToken cancellationToken)
	{
		State = "STOPPED";
		return ValueTask.CompletedTask;
	}

	public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
}

public sealed class DiscreteIntegrationAdapter : IntegrationAdapterBase
{
	private readonly DiscreteAdapterOptions _options;
	private readonly IReadOnlyList<IntegrationFeedbackMappingOptions> _feedbackMappings;
	private readonly IDiscreteIoBackend _backend;
	private IntegrationInputSink? _input;

	public DiscreteIntegrationAdapter(
		IntegrationAdapterOptions adapter,
		IReadOnlyList<IntegrationFeedbackMappingOptions> feedbackMappings,
		IDiscreteIoBackend backend)
		: base(adapter.Id, IntegrationAdapterKind.Discrete, adapter.Required)
	{
		_options = adapter.Discrete ?? throw new ArgumentException("Discrete options are required.", nameof(adapter));
		_feedbackMappings = feedbackMappings ?? throw new ArgumentNullException(nameof(feedbackMappings));
		_backend = backend ?? throw new ArgumentNullException(nameof(backend));
	}

	public override async ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		_input = input;
		_backend.InputChanged += OnInputChanged;
		SetHealth(IntegrationAdapterState.Starting, "Starting discrete I/O provider.");
		try
		{
			await _backend.StartAsync(cancellationToken).ConfigureAwait(false);
			SetHealth(IntegrationAdapterState.Healthy, $"Discrete provider state: {_backend.State}.");
		}
		catch (Exception exception)
		{
			_backend.InputChanged -= OnInputChanged;
			SetHealth(IntegrationAdapterState.Failed, $"Discrete provider failed: {exception.Message}");
			throw;
		}
	}

	public override async ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken)
	{
		foreach (var mapping in _feedbackMappings)
		{
			var logical = ParseBoolean(snapshot.Resolve(mapping));
			var activeLow = _options.ActiveLowOutputs.TryGetValue(mapping.TargetKey, out var configured) && configured;
			await _backend.SetOutputAsync(mapping.TargetKey, activeLow ? !logical : logical, cancellationToken).ConfigureAwait(false);
		}
	}

	public override async ValueTask StopAsync(CancellationToken cancellationToken)
	{
		_backend.InputChanged -= OnInputChanged;
		await _backend.StopAsync(cancellationToken).ConfigureAwait(false);
		_input = null;
		SetHealth(IntegrationAdapterState.Stopped, "Discrete adapter is stopped.");
	}

	private void OnInputChanged(DiscreteInputEvent input)
	{
		var activeLow = _options.ActiveLowInputs.TryGetValue(input.ChannelId, out var configured) && configured;
		var logical = activeLow ? !input.State : input.State;
		if (!(_input?.Invoke(new IntegrationTrigger(
			Id,
			$"input:{input.ChannelId}",
			NumericValue: logical ? 1 : 0,
			BooleanValue: logical,
			ObservedAtUtc: input.ObservedAtUtc)) ?? false))
			IncrementDroppedInputs("Discrete input could not enter the bounded gateway queue.");
	}

	private static bool ParseBoolean(string value) =>
		bool.TryParse(value, out var boolean)
			? boolean
			: !(string.IsNullOrWhiteSpace(value) ||
				value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("STOPPED", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("FAIL", StringComparison.OrdinalIgnoreCase));

	public override async ValueTask DisposeAsync()
	{
		await base.DisposeAsync().ConfigureAwait(false);
		await _backend.DisposeAsync().ConfigureAwait(false);
	}
}
