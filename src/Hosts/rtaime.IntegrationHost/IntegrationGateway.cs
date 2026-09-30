// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Threading.Channels;
using rtaime.Client;

namespace rtaime.IntegrationHost;

public enum IntegrationAdapterState
{
	Stopped = 1,
	Starting = 2,
	Healthy = 3,
	Degraded = 4,
	Failed = 5
}

public sealed record IntegrationTrigger(
	string AdapterId,
	string Key,
	double? NumericValue = null,
	string? TextValue = null,
	bool? BooleanValue = null,
	DateTimeOffset? ObservedAtUtc = null)
{
	public DateTimeOffset TimestampUtc => ObservedAtUtc ?? DateTimeOffset.UtcNow;
}

public sealed record IntegrationAdapterHealthSnapshot(
	string AdapterId,
	IntegrationAdapterKind Kind,
	IntegrationAdapterState State,
	string Detail,
	DateTimeOffset UpdatedAtUtc,
	ulong DroppedInputs = 0);

public sealed record IntegrationFeedbackSnapshot(
	ulong Sequence,
	DateTimeOffset ObservedAtUtc,
	string PreviewSourceId,
	string ProgramSourceId,
	string? ActiveSceneId,
	string RecordingState,
	string RuntimeReadiness,
	string MediaDeckState,
	string ShowControlState,
	IReadOnlyDictionary<string, string> OutputHealth)
{
	public string Resolve(IntegrationFeedbackMappingOptions mapping)
	{
		ArgumentNullException.ThrowIfNull(mapping);
		return mapping.Source switch
		{
			IntegrationFeedbackSource.PreviewSource => PreviewSourceId,
			IntegrationFeedbackSource.ProgramSource => ProgramSourceId,
			IntegrationFeedbackSource.ActiveScene => ActiveSceneId ?? string.Empty,
			IntegrationFeedbackSource.RecordingState => RecordingState,
			IntegrationFeedbackSource.RuntimeReadiness => RuntimeReadiness,
			IntegrationFeedbackSource.MediaDeckState => MediaDeckState,
			IntegrationFeedbackSource.ShowControlState => ShowControlState,
			IntegrationFeedbackSource.OutputHealth =>
				OutputHealth.TryGetValue(mapping.SourceParameter ?? string.Empty, out var health) ? health : "UNVERIFIED",
			_ => throw new ArgumentOutOfRangeException(nameof(mapping))
		};
	}
}

public delegate bool IntegrationInputSink(IntegrationTrigger trigger);

public interface IIntegrationAdapter : IAsyncDisposable
{
	string Id { get; }
	IntegrationAdapterKind Kind { get; }
	bool Required { get; }
	IntegrationAdapterHealthSnapshot Health { get; }
	ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken);
	ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken);
	ValueTask StopAsync(CancellationToken cancellationToken);
}

public abstract class IntegrationAdapterBase : IIntegrationAdapter
{
	private readonly object _healthGate = new();
	private IntegrationAdapterHealthSnapshot _health;

	protected IntegrationAdapterBase(string id, IntegrationAdapterKind kind, bool required)
	{
		Id = id;
		Kind = kind;
		Required = required;
		_health = new(id, kind, IntegrationAdapterState.Stopped, "Adapter is stopped.", DateTimeOffset.UtcNow);
	}

	public string Id { get; }
	public IntegrationAdapterKind Kind { get; }
	public bool Required { get; }
	public IntegrationAdapterHealthSnapshot Health { get { lock (_healthGate) return _health; } }

	public abstract ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken);
	public abstract ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken);
	public abstract ValueTask StopAsync(CancellationToken cancellationToken);

	protected void SetHealth(IntegrationAdapterState state, string detail, ulong? droppedInputs = null)
	{
		lock (_healthGate)
		{
			_health = new(
				Id,
				Kind,
				state,
				string.IsNullOrWhiteSpace(detail) ? state.ToString() : detail.Trim(),
				DateTimeOffset.UtcNow,
				droppedInputs ?? _health.DroppedInputs);
		}
	}

	protected void IncrementDroppedInputs(string detail)
	{
		lock (_healthGate)
			_health = _health with
			{
				State = IntegrationAdapterState.Degraded,
				Detail = detail,
				UpdatedAtUtc = DateTimeOffset.UtcNow,
				DroppedInputs = _health.DroppedInputs + 1
			};
	}

	public virtual async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}

public sealed record IntegrationGatewayHealthSnapshot(
	string State,
	string Detail,
	DateTimeOffset UpdatedAtUtc,
	ulong DroppedInputs,
	IReadOnlyList<IntegrationAdapterHealthSnapshot> Adapters);

public sealed class IntegrationGateway : IAsyncDisposable
{
	private sealed record MappingState(DateTimeOffset LastAcceptedAtUtc);

	private readonly object _gate = new();
	private readonly IntegrationGatewayOptions _options;
	private readonly OperatorControlClient _client;
	private readonly MediaDeckController _mediaDeck;
	private readonly Channel<IntegrationTrigger> _inputs;
	private readonly Dictionary<string, IIntegrationAdapter> _adapters = new(StringComparer.Ordinal);
	private readonly IReadOnlyDictionary<string, IReadOnlyList<IntegrationMappingOptions>> _mappings;
	private readonly Dictionary<string, MappingState> _mappingState = new(StringComparer.Ordinal);
	private readonly CancellationTokenSource _lifetime = new();
	private Task? _inputWorker;
	private Task? _snapshotWorker;
	private IntegrationFeedbackSnapshot? _feedback;
	private ulong _feedbackSequence;
	private ulong _droppedInputs;
	private string _state = "STOPPED";
	private string _detail = "Gateway is stopped.";
	private DateTimeOffset _updatedAtUtc = DateTimeOffset.UtcNow;
	private bool _started;

	public IntegrationGateway(IntegrationGatewayOptions options, OperatorControlClient client)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_options.Validate();
		_mediaDeck = new MediaDeckController(_client);
		_inputs = Channel.CreateBounded<IntegrationTrigger>(new BoundedChannelOptions(_options.QueueCapacity)
		{
			AllowSynchronousContinuations = false,
			FullMode = BoundedChannelFullMode.DropWrite,
			SingleReader = true,
			SingleWriter = false
		});
		_mappings = _options.Mappings
			.GroupBy(mapping => MappingKey(mapping.AdapterId, mapping.TriggerKey), StringComparer.Ordinal)
			.ToDictionary(
				group => group.Key,
				group => (IReadOnlyList<IntegrationMappingOptions>)Array.AsReadOnly(group.ToArray()),
				StringComparer.Ordinal);
	}

	public IntegrationFeedbackSnapshot? CurrentFeedback { get { lock (_gate) return _feedback; } }

	public IntegrationGatewayHealthSnapshot Health
	{
		get
		{
			lock (_gate)
			{
				return new IntegrationGatewayHealthSnapshot(
					_state,
					_detail,
					_updatedAtUtc,
					_droppedInputs,
					Array.AsReadOnly(_adapters.Values.Select(adapter => adapter.Health).OrderBy(health => health.AdapterId, StringComparer.Ordinal).ToArray()));
			}
		}
	}

	public void RegisterAdapter(IIntegrationAdapter adapter)
	{
		ArgumentNullException.ThrowIfNull(adapter);
		lock (_gate)
		{
			if (_started) throw new InvalidOperationException("Adapters cannot be registered after the gateway starts.");
			if (!_adapters.TryAdd(adapter.Id, adapter))
				throw new InvalidOperationException($"Adapter '{adapter.Id}' is already registered.");
		}
	}

	public bool TryEnqueue(IntegrationTrigger trigger)
	{
		ArgumentNullException.ThrowIfNull(trigger);
		if (string.IsNullOrWhiteSpace(trigger.AdapterId) || string.IsNullOrWhiteSpace(trigger.Key))
			return false;
		lock (_gate)
		{
			if (!_started || !_adapters.ContainsKey(trigger.AdapterId))
				return false;
		}
		if (_inputs.Writer.TryWrite(trigger))
			return true;
		lock (_gate)
		{
			_droppedInputs++;
			_state = "DEGRADED";
			_detail = "Input queue capacity was reached; newest integration input was dropped.";
			_updatedAtUtc = DateTimeOffset.UtcNow;
		}
		return false;
	}

	public async Task RunAsync(CancellationToken cancellationToken)
	{
		await StartAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		finally
		{
			await StopAsync(CancellationToken.None).ConfigureAwait(false);
		}
	}

	public async ValueTask StartAsync(CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			if (_started) return;
			_started = true;
			_state = "STARTING";
			_detail = "Establishing upstream ControlHost state and integration adapters.";
			_updatedAtUtc = DateTimeOffset.UtcNow;
		}

		try
		{
			var initial = await _client.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
			foreach (var adapter in SnapshotAdapters())
			{
				try
				{
					await adapter.StartAsync(TryEnqueue, cancellationToken).ConfigureAwait(false);
				}
				catch when (!adapter.Required)
				{
				}
			}

			var failedRequired = SnapshotAdapters().FirstOrDefault(adapter =>
				adapter.Required && adapter.Health.State != IntegrationAdapterState.Healthy);
			if (failedRequired is not null)
				throw new InvalidOperationException($"Required integration adapter '{failedRequired.Id}' failed to start: {failedRequired.Health.Detail}");

			await PublishFeedbackAsync(initial, cancellationToken).ConfigureAwait(false);
			var token = _lifetime.Token;
			_inputWorker = Task.Run(() => RunInputsAsync(token), CancellationToken.None);
			_snapshotWorker = Task.Run(() => RunSnapshotPumpAsync(token), CancellationToken.None);
			SetGatewayState("HEALTHY", "Integration gateway is active.");
		}
		catch
		{
			SetGatewayState("FAILED", "Integration gateway failed during startup.");
			throw;
		}
	}

	public async ValueTask StopAsync(CancellationToken cancellationToken = default)
	{
		bool stop;
		lock (_gate)
		{
			stop = _started;
			_started = false;
		}
		if (!stop) return;

		_lifetime.Cancel();
		_inputs.Writer.TryComplete();
		var workers = new[] { _inputWorker, _snapshotWorker }.Where(task => task is not null).Cast<Task>().ToArray();
		if (workers.Length > 0)
		{
			try { await Task.WhenAll(workers).WaitAsync(cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
			catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		}

		foreach (var adapter in SnapshotAdapters().Reverse())
		{
			try { await adapter.StopAsync(cancellationToken).ConfigureAwait(false); }
			catch { }
		}
		SetGatewayState("STOPPED", "Integration gateway is stopped.");
	}

	private async Task RunInputsAsync(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var trigger in _inputs.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
				await ProcessTriggerAsync(trigger, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	internal async ValueTask ProcessTriggerAsync(IntegrationTrigger trigger, CancellationToken cancellationToken = default)
	{
		if (!_mappings.TryGetValue(MappingKey(trigger.AdapterId, trigger.Key), out var mappings))
			return;

		foreach (var mapping in mappings)
		{
			var now = trigger.TimestampUtc;
			if (_mappingState.TryGetValue(mapping.Id, out var state))
			{
				var elapsed = now - state.LastAcceptedAtUtc;
				var threshold = Math.Max(mapping.DebounceMs, mapping.MinimumIntervalMs);
				if (elapsed < TimeSpan.FromMilliseconds(threshold))
					continue;
			}
			_mappingState[mapping.Id] = new MappingState(now);
			await ExecuteMappingAsync(mapping, trigger, cancellationToken).ConfigureAwait(false);
		}
	}

	private async ValueTask ExecuteMappingAsync(
		IntegrationMappingOptions mapping,
		IntegrationTrigger trigger,
		CancellationToken cancellationToken)
	{
		if (_options.DryRun)
		{
			SetGatewayState("HEALTHY", $"Dry-run accepted mapping '{mapping.Id}' without Production mutation.");
			return;
		}

		for (var attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				await ExecuteActionAsync(mapping.Action, trigger, cancellationToken).ConfigureAwait(false);
				var snapshot = await _client.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
				await PublishFeedbackAsync(snapshot, cancellationToken).ConfigureAwait(false);
				SetGatewayState("HEALTHY", $"Mapping '{mapping.Id}' completed through rtaime.Client.");
				return;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception) when (attempt == 0 && IsRetryable(exception))
			{
				await _client.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				SetGatewayState("DEGRADED", $"Mapping '{mapping.Id}' failed: {exception.Message}");
				return;
			}
		}
	}

	private async ValueTask ExecuteActionAsync(
		IntegrationActionOptions action,
		IntegrationTrigger trigger,
		CancellationToken cancellationToken)
	{
		switch (action.Kind)
		{
			case IntegrationActionKind.PreviewSelect:
				await _client.SelectPreviewAsync(action.TargetId!, cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.Cut:
				if (string.IsNullOrWhiteSpace(action.TargetId))
					await _client.CutPreviewAsync(cancellationToken).ConfigureAwait(false);
				else
					await _client.CutAsync(action.TargetId, cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.Dissolve:
				if (string.IsNullOrWhiteSpace(action.TargetId))
					await _client.DissolvePreviewAsync(action.DurationFrames, cancellationToken).ConfigureAwait(false);
				else
					await _client.DissolveAsync(action.TargetId, action.DurationFrames, cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.SceneActivate:
				await _client.ActivateSceneAsync(action.TargetId!, cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.ShowControlGo:
				await _client.GoShowControlAsync(cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.ShowControlArm:
				await _client.ArmShowControlAsync(cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.ShowControlCancel:
				await _client.CancelShowControlAsync(cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.RecordingStart:
				await _client.StartRecordingAsync(action.RecordingDirectory!, action.RecordingFileName!, cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.RecordingStop:
				await _client.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.OutputRoute:
				await _client.RouteOutputRoleAsync(action.TargetId!, action.SecondaryTargetId!, cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.MediaPlay:
				await _mediaDeck.RefreshAsync(cancellationToken).ConfigureAwait(false);
				await _mediaDeck.PlayAsync(cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.MediaPause:
				await _mediaDeck.RefreshAsync(cancellationToken).ConfigureAwait(false);
				await _mediaDeck.PauseAsync(cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.MediaStop:
				await _mediaDeck.RefreshAsync(cancellationToken).ConfigureAwait(false);
				await _mediaDeck.StopAsync(cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.MediaCueFrame:
				await _mediaDeck.RefreshAsync(cancellationToken).ConfigureAwait(false);
				var targetFrame = action.UseTriggerValue
					? checked((long)Math.Round(ResolveNumeric(action, trigger), MidpointRounding.AwayFromZero))
					: action.Frame ?? 0;
				await _mediaDeck.Timeline.SeekToFrameAsync(targetFrame, cancellationToken).ConfigureAwait(false);
				break;
			case IntegrationActionKind.AudioInputSet:
				var gain = action.UseTriggerValue ? ResolveNumeric(action, trigger) : action.Gain;
				await _client.SetAudioInputStateAsync(action.TargetId!, gain, action.Muted, cancellationToken).ConfigureAwait(false);
				break;
			default:
				throw new InvalidOperationException($"Unsupported integration action '{action.Kind}'.");
		}
	}

	private static double ResolveNumeric(IntegrationActionOptions action, IntegrationTrigger trigger)
	{
		var raw = trigger.NumericValue ??
			(trigger.BooleanValue.HasValue ? (trigger.BooleanValue.Value ? 1d : 0d) : 0d);
		var scaled = (raw * action.TriggerScale) + action.TriggerOffset;
		return Math.Clamp(scaled, action.TriggerMinimum, action.TriggerMaximum);
	}

	private async Task RunSnapshotPumpAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(_options.SnapshotMinimumIntervalMs), cancellationToken).ConfigureAwait(false);
				try
				{
					var snapshot = await _client.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
					await PublishFeedbackAsync(snapshot, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					return;
				}
				catch (Exception exception)
				{
					SetGatewayState("DEGRADED", $"ControlHost snapshot refresh failed: {exception.Message}");
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private async ValueTask PublishFeedbackAsync(OperatorStatusSnapshot snapshot, CancellationToken cancellationToken)
	{
		var outputs = snapshot.OutputRoles.ToDictionary(role => role.RoleId, role => role.HealthState, StringComparer.Ordinal);
		var feedback = new IntegrationFeedbackSnapshot(
			checked(++_feedbackSequence),
			DateTimeOffset.UtcNow,
			snapshot.Production.Routing.PreviewSourceId.ToString(),
			snapshot.Production.Routing.ProgramSourceId.ToString(),
			snapshot.Production.ActiveSceneId?.ToString(),
			snapshot.Recording.State,
			$"{snapshot.Health.Runtime.State}:{snapshot.RuntimeStatus}",
			snapshot.MediaDeck.State.ToString().ToUpperInvariant(),
			snapshot.ShowControl.Execution.State.ToString().ToUpperInvariant(),
			new ReadOnlyDictionary<string, string>(outputs));
		lock (_gate) _feedback = feedback;

		foreach (var adapter in SnapshotAdapters())
		{
			if (adapter.Health.State is IntegrationAdapterState.Failed or IntegrationAdapterState.Stopped)
				continue;
			try
			{
				await adapter.EmitFeedbackAsync(feedback, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				SetGatewayState("DEGRADED", $"Feedback to adapter '{adapter.Id}' failed: {exception.Message}");
			}
		}
	}

	private IReadOnlyList<IIntegrationAdapter> SnapshotAdapters()
	{
		lock (_gate)
			return Array.AsReadOnly(_adapters.Values.OrderBy(adapter => adapter.Id, StringComparer.Ordinal).ToArray());
	}

	private void SetGatewayState(string state, string detail)
	{
		lock (_gate)
		{
			_state = state;
			_detail = detail;
			_updatedAtUtc = DateTimeOffset.UtcNow;
		}
	}

	private static bool IsRetryable(Exception exception) =>
		exception is RemoteHostSessionChangedException ||
		exception is ExternalControlRequestException external &&
			(external.Code.Contains("revision", StringComparison.OrdinalIgnoreCase) ||
			 external.Code.Contains("conflict", StringComparison.OrdinalIgnoreCase)) ||
		exception is InvalidOperationException invalid &&
			invalid.Message.Contains("snapshot", StringComparison.OrdinalIgnoreCase);

	private static string MappingKey(string adapterId, string triggerKey) => adapterId + "\n" + triggerKey;

	public async ValueTask DisposeAsync()
	{
		await StopAsync(CancellationToken.None).ConfigureAwait(false);
		foreach (var adapter in SnapshotAdapters())
			await adapter.DisposeAsync().ConfigureAwait(false);
		await _mediaDeck.DisposeAsync().ConfigureAwait(false);
		_lifetime.Dispose();
	}
}

public static class IntegrationAdapterFactory
{
	public static IIntegrationAdapter Create(
		IntegrationAdapterOptions adapter,
		IntegrationGatewayOptions gateway)
	{
		var feedback = gateway.FeedbackMappings
			.Where(mapping => string.Equals(mapping.AdapterId, adapter.Id, StringComparison.Ordinal))
			.ToArray();
		var mappings = gateway.Mappings
			.Where(mapping => string.Equals(mapping.AdapterId, adapter.Id, StringComparison.Ordinal))
			.ToArray();

		return adapter.Kind switch
		{
			IntegrationAdapterKind.Osc => new OscIntegrationAdapter(adapter, feedback),
			IntegrationAdapterKind.Midi => new MidiIntegrationAdapter(
				adapter,
				feedback,
				gateway.TestMode || string.Equals(adapter.Midi!.Backend, "virtual", StringComparison.Ordinal)
					? new VirtualMidiBackend()
					: new WinMmMidiBackend(adapter.Midi)),
			IntegrationAdapterKind.Discrete => new DiscreteIntegrationAdapter(
				adapter,
				feedback,
				new VirtualDiscreteIoBackend()),
			IntegrationAdapterKind.Companion => new CompanionIntegrationAdapter(adapter, mappings, feedback),
			_ => throw new ArgumentOutOfRangeException(nameof(adapter.Kind))
		};
	}
}
