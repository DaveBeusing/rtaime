// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using rtaime.Client;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.IntegrationHost;

namespace rtaime.Tests.Unit;

public sealed class ProductionIntegrationGatewayTests
{
	[Fact]
	public void Configuration_rejects_unbounded_or_unknown_mapping_targets()
	{
		var options = new IntegrationGatewayOptions
		{
			Enabled = true,
			TestMode = true,
			Upstream = new IntegrationUpstreamOptions { Endpoint = "https://127.0.0.1:55101" },
			Adapters = new[]
			{
				new IntegrationAdapterOptions
				{
					Id = "osc",
					Kind = IntegrationAdapterKind.Osc,
					Osc = new OscAdapterOptions()
				}
			},
			Mappings = new[]
			{
				new IntegrationMappingOptions
				{
					Id = "cut",
					AdapterId = "missing",
					TriggerKey = "/cut",
					Action = new IntegrationActionOptions { Kind = IntegrationActionKind.Cut }
				}
			}
		};

		var exception = Assert.Throws<ArgumentException>(options.Validate);
		Assert.Contains("unknown adapter", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Osc_codec_round_trips_supported_string_feedback_and_rejects_truncation()
	{
		var encoded = OscCodec.EncodeString("/rtaime/program", "source-a");

		Assert.True(OscCodec.TryDecode(encoded, out var decoded));
		Assert.NotNull(decoded);
		Assert.Equal("/rtaime/program", decoded!.Address);
		Assert.Equal("source-a", Assert.IsType<string>(Assert.Single(decoded.Arguments)));
		Assert.False(OscCodec.TryDecode(encoded.AsSpan(0, encoded.Length - 1), out _));
	}

	[Fact]
	public async Task Midi_adapter_maps_virtual_input_and_feedback_without_hardware()
	{
		var backend = new VirtualMidiBackend();
		var adapter = new MidiIntegrationAdapter(
			new IntegrationAdapterOptions
			{
				Id = "midi",
				Kind = IntegrationAdapterKind.Midi,
				Midi = new MidiAdapterOptions { Backend = "virtual" }
			},
			new[]
			{
				new IntegrationFeedbackMappingOptions
				{
					AdapterId = "midi",
					Source = IntegrationFeedbackSource.RecordingState,
					TargetKey = "cc:2:8"
				}
			},
			backend);

		IntegrationTrigger? observed = null;
		await adapter.StartAsync(trigger => { observed = trigger; return true; }, CancellationToken.None);
		backend.Inject(new MidiMessage(MidiMessageKind.ControlChange, 2, 7, 64));

		Assert.NotNull(observed);
		Assert.Equal("cc:2:7", observed!.Key);
		Assert.InRange(observed.NumericValue!.Value, 0.50, 0.51);

		await adapter.EmitFeedbackAsync(Feedback(recording: "RECORDING"), CancellationToken.None);
		var sent = Assert.Single(backend.SentMessages);
		Assert.Equal(MidiMessageKind.ControlChange, sent.Kind);
		Assert.Equal(2, sent.Channel);
		Assert.Equal(8, sent.Data1);
		Assert.Equal(127, sent.Data2);

		await adapter.DisposeAsync();
	}

	[Fact]
	public async Task Discrete_reference_provider_applies_input_and_output_polarity()
	{
		var backend = new VirtualDiscreteIoBackend();
		var adapter = new DiscreteIntegrationAdapter(
			new IntegrationAdapterOptions
			{
				Id = "gpi",
				Kind = IntegrationAdapterKind.Discrete,
				Discrete = new DiscreteAdapterOptions
				{
					ActiveLowInputs = new Dictionary<string, bool>(StringComparer.Ordinal) { ["go"] = true },
					ActiveLowOutputs = new Dictionary<string, bool>(StringComparer.Ordinal) { ["record-tally"] = true }
				}
			},
			new[]
			{
				new IntegrationFeedbackMappingOptions
				{
					AdapterId = "gpi",
					Source = IntegrationFeedbackSource.RecordingState,
					TargetKey = "record-tally"
				}
			},
			backend);

		IntegrationTrigger? observed = null;
		await adapter.StartAsync(trigger => { observed = trigger; return true; }, CancellationToken.None);
		backend.InjectInput("go", false);

		Assert.NotNull(observed);
		Assert.Equal("input:go", observed!.Key);
		Assert.True(observed.BooleanValue);

		await adapter.EmitFeedbackAsync(Feedback(recording: "RECORDING"), CancellationToken.None);
		Assert.True(backend.Outputs.TryGetValue("record-tally", out var physical));
		Assert.False(physical);

		await adapter.DisposeAsync();
	}

	[Fact]
	public void Feedback_resolution_is_snapshot_based_and_output_role_specific()
	{
		var snapshot = Feedback(
			preview: "preview-a",
			program: "program-a",
			recording: "IDLE",
			outputHealth: new Dictionary<string, string>(StringComparer.Ordinal) { ["aux"] = "PASS" });
		var mapping = new IntegrationFeedbackMappingOptions
		{
			AdapterId = "osc",
			Source = IntegrationFeedbackSource.OutputHealth,
			SourceParameter = "aux",
			TargetKey = "/aux/health"
		};

		Assert.Equal("PASS", snapshot.Resolve(mapping));
	}

	[Fact]
	public async Task Gateway_recovers_when_ControlHost_is_unavailable_during_startup()
	{
		var transport = new ProbeTransport(snapshotFailures: 2);
		var client = new OperatorControlClient(transport);
		var adapter = new PassiveAdapter("reference");
		await using var gateway = new IntegrationGateway(GatewayOptions(Adapter("reference")), client);
		gateway.RegisterAdapter(adapter);

		await gateway.StartAsync();

		Assert.Equal("DEGRADED", gateway.Health.State);
		await WaitUntilAsync(() => gateway.CurrentFeedback is not null && gateway.Health.State == "HEALTHY");

		Assert.True(transport.GetSnapshotCalls >= 3);
		Assert.Equal("ControlHost snapshot synchronization recovered.", gateway.Health.Detail);
	}

	[Fact]
	public async Task Gateway_can_stop_restart_and_resnapshot_without_reusing_completed_lifecycle_state()
	{
		var transport = new ProbeTransport();
		var client = new OperatorControlClient(transport);
		var adapter = new PassiveAdapter("reference");
		await using var gateway = new IntegrationGateway(GatewayOptions(Adapter("reference")), client);
		gateway.RegisterAdapter(adapter);

		await gateway.StartAsync();
		var firstSequence = gateway.CurrentFeedback?.Sequence ?? 0;
		var firstSnapshots = transport.GetSnapshotCalls;
		await gateway.StopAsync();

		await gateway.StartAsync();

		Assert.Equal(2, adapter.StartCount);
		Assert.True(transport.GetSnapshotCalls > firstSnapshots);
		Assert.NotNull(gateway.CurrentFeedback);
		Assert.True(gateway.CurrentFeedback!.Sequence > firstSequence);
	}

	[Fact]
	public async Task Gateway_fans_one_snapshot_out_to_all_registered_adapters()
	{
		var transport = new ProbeTransport();
		var client = new OperatorControlClient(transport);
		var left = new PassiveAdapter("left");
		var right = new PassiveAdapter("right");
		await using var gateway = new IntegrationGateway(
			GatewayOptions(Adapter("left"), Adapter("right")),
			client);
		gateway.RegisterAdapter(left);
		gateway.RegisterAdapter(right);

		await gateway.StartAsync();

		Assert.True(left.FeedbackCount >= 1);
		Assert.True(right.FeedbackCount >= 1);
		Assert.Equal(left.LastFeedback?.Sequence, right.LastFeedback?.Sequence);
	}

	[Fact]
	public async Task Gateway_drops_newest_inputs_when_the_bounded_queue_is_full()
	{
		var transport = new ProbeTransport();
		var client = new OperatorControlClient(transport);
		var burst = new BurstAdapter("burst", 16);
		var options = GatewayOptions(Adapter("burst")) with { QueueCapacity = 8 };
		await using var gateway = new IntegrationGateway(options, client);
		gateway.RegisterAdapter(burst);

		await gateway.StartAsync();

		Assert.Equal(8, burst.Rejected);
		Assert.Equal(8UL, gateway.Health.DroppedInputs);
	}

	[Fact]
	public async Task Gateway_serializes_client_snapshot_refresh_with_production_commands()
	{
		var transport = new ProbeTransport(delayMilliseconds: 80);
		var client = new OperatorControlClient(transport);
		var adapter = new PassiveAdapter("control");
		var options = GatewayOptions(
			new[] { Adapter("control") },
			new[]
			{
				new IntegrationMappingOptions
				{
					Id = "cut",
					AdapterId = "control",
					TriggerKey = "cut",
					Action = new IntegrationActionOptions { Kind = IntegrationActionKind.Cut }
				}
			});
		await using var gateway = new IntegrationGateway(options, client);
		gateway.RegisterAdapter(adapter);
		await gateway.StartAsync();

		Assert.True(gateway.TryEnqueue(new IntegrationTrigger("control", "cut")));
		await WaitUntilAsync(() => transport.CutCalls > 0 && transport.GetSnapshotCalls >= 3);

		Assert.Equal(1, transport.MaxConcurrentCalls);
	}

	[Fact]
	public async Task Companion_surface_requires_bearer_authentication_before_discovery_or_command_admission()
	{
		var environmentName = "RTAIME_TEST_COMPANION_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
		var token = "test-token-" + Guid.NewGuid().ToString("N");
		var port = ReserveLoopbackPort();
		Environment.SetEnvironmentVariable(environmentName, token);
		var observed = new List<IntegrationTrigger>();
		await using var adapter = new CompanionIntegrationAdapter(
			new IntegrationAdapterOptions
			{
				Id = "companion",
				Kind = IntegrationAdapterKind.Companion,
				Companion = new CompanionAdapterOptions
				{
					BindAddress = "127.0.0.1",
					Port = port,
					BearerTokenEnvironmentVariable = environmentName
				}
			},
			new[]
			{
				new IntegrationMappingOptions
				{
					Id = "cut",
					AdapterId = "companion",
					TriggerKey = "cut",
					Action = new IntegrationActionOptions { Kind = IntegrationActionKind.Cut }
				}
			},
			Array.Empty<IntegrationFeedbackMappingOptions>());

		try
		{
			await adapter.StartAsync(trigger => { observed.Add(trigger); return true; }, CancellationToken.None);
			using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

			using var unauthenticated = await client.GetAsync("/rtaime/integration/v1/actions");
			Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
			using var rejected = await client.PostAsync("/rtaime/integration/v1/actions/cut", null);
			Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
			Assert.Empty(observed);

			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
			using var accepted = await client.PostAsync("/rtaime/integration/v1/actions/cut", null);
			Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
			Assert.Equal("cut", Assert.Single(observed).Key);
		}
		finally
		{
			Environment.SetEnvironmentVariable(environmentName, null);
		}
	}

	private static IntegrationGatewayOptions GatewayOptions(params IntegrationAdapterOptions[] adapters) =>
		GatewayOptions(adapters, Array.Empty<IntegrationMappingOptions>());

	private static IntegrationGatewayOptions GatewayOptions(
		IReadOnlyList<IntegrationAdapterOptions> adapters,
		IReadOnlyList<IntegrationMappingOptions> mappings) =>
		new()
		{
			Enabled = true,
			TestMode = true,
			QueueCapacity = 256,
			SnapshotMinimumIntervalMs = 100,
			Upstream = new IntegrationUpstreamOptions { Endpoint = "https://127.0.0.1:55101" },
			Adapters = adapters,
			Mappings = mappings
		};

	private static IntegrationAdapterOptions Adapter(string id) =>
		new()
		{
			Id = id,
			Kind = IntegrationAdapterKind.Discrete,
			Discrete = new DiscreteAdapterOptions()
		};

	private static int ReserveLoopbackPort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally
		{
			listener.Stop();
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
			await Task.Delay(25);
		Assert.True(condition(), $"Condition did not become true within {timeoutMilliseconds} ms.");
	}

	private sealed class PassiveAdapter : IntegrationAdapterBase
	{
		public PassiveAdapter(string id)
			: base(id, IntegrationAdapterKind.Discrete, false)
		{
		}

		public int StartCount { get; private set; }
		public int FeedbackCount { get; private set; }
		public IntegrationFeedbackSnapshot? LastFeedback { get; private set; }

		public override ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken)
		{
			StartCount++;
			SetHealth(IntegrationAdapterState.Healthy, "Reference adapter is active.");
			return ValueTask.CompletedTask;
		}

		public override ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken)
		{
			FeedbackCount++;
			LastFeedback = snapshot;
			return ValueTask.CompletedTask;
		}

		public override ValueTask StopAsync(CancellationToken cancellationToken)
		{
			SetHealth(IntegrationAdapterState.Stopped, "Reference adapter is stopped.");
			return ValueTask.CompletedTask;
		}
	}

	private sealed class BurstAdapter : IntegrationAdapterBase
	{
		private readonly int _count;

		public BurstAdapter(string id, int count)
			: base(id, IntegrationAdapterKind.Discrete, false)
		{
			_count = count;
		}

		public int Rejected { get; private set; }

		public override ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken)
		{
			SetHealth(IntegrationAdapterState.Healthy, "Burst adapter is active.");
			for (var index = 0; index < _count; index++)
			{
				if (!input(new IntegrationTrigger(Id, "event", NumericValue: index)))
					Rejected++;
			}
			return ValueTask.CompletedTask;
		}

		public override ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken) =>
			ValueTask.CompletedTask;

		public override ValueTask StopAsync(CancellationToken cancellationToken)
		{
			SetHealth(IntegrationAdapterState.Stopped, "Burst adapter is stopped.");
			return ValueTask.CompletedTask;
		}
	}

	private sealed class ProbeTransport : IOperatorControlTransport
	{
		private readonly object _gate = new();
		private readonly ProductionSpecification _specification;
		private AuthoritativeProductionState _state;
		private readonly int _delayMilliseconds;
		private int _snapshotFailuresRemaining;
		private int _activeCalls;

		public ProbeTransport(int delayMilliseconds = 0, int snapshotFailures = 0)
		{
			_delayMilliseconds = delayMilliseconds;
			_snapshotFailuresRemaining = snapshotFailures;
			var sourceA = new ProductionSourceSpecification(
				new ProductionSourceId(Identity.Parse("9a000000-0000-0000-0000-00000000000a")),
				"Input A");
			var sourceB = new ProductionSourceSpecification(
				new ProductionSourceId(Identity.Parse("9a000000-0000-0000-0000-00000000000b")),
				"Input B");
			_specification = new ProductionSpecification(
				ControlContractVersion.Current,
				new ProductionId(Identity.Parse("9a000000-0000-0000-0000-000000000001")),
				"Integration Gateway Test",
				new[] { sourceA, sourceB },
				new ProductionRoutingState(sourceB.SourceId, sourceA.SourceId));
			var initialized = ControlDomainEngine.Initialize(_specification);
			_state = initialized.State?.Authoritative
				?? throw new InvalidOperationException("Test production state did not initialize.");
		}

		public int GetSnapshotCalls { get; private set; }
		public int CutCalls { get; private set; }
		public int MaxConcurrentCalls { get; private set; }

		public async ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
		{
			await EnterAsync(cancellationToken);
			try
			{
				lock (_gate)
				{
					GetSnapshotCalls++;
					if (_snapshotFailuresRemaining > 0)
					{
						_snapshotFailuresRemaining--;
						throw new IOException("Simulated ControlHost snapshot outage.");
					}
					return Snapshot(_state);
				}
			}
			finally
			{
				Exit();
			}
		}

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
			SelectPreviewCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());

		public async ValueTask<OperatorMutationResponse> CutProgramAsync(
			CutProgramCommand command,
			CancellationToken cancellationToken = default)
		{
			await EnterAsync(cancellationToken);
			try
			{
				lock (_gate)
				{
					CutCalls++;
					var result = ControlDomainEngine.Apply(_specification, _state, command);
					if (!result.Committed)
						return new OperatorMutationResponse(
							false,
							result.AuthoritativeState,
							new Failure("integration.test.cut_rejected", "Test CUT command was rejected."));
					_state = result.AuthoritativeState;
					return new OperatorMutationResponse(true, _state, null);
				}
			}
			finally
			{
				Exit();
			}
		}

		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
			DissolveProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());

		private async ValueTask EnterAsync(CancellationToken cancellationToken)
		{
			lock (_gate)
			{
				_activeCalls++;
				MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _activeCalls);
			}
			try
			{
				if (_delayMilliseconds > 0)
					await Task.Delay(_delayMilliseconds, cancellationToken);
			}
			catch
			{
				Exit();
				throw;
			}
		}

		private void Exit()
		{
			lock (_gate)
				_activeCalls--;
		}

		private static OperatorStatusSnapshot Snapshot(AuthoritativeProductionState state)
		{
			var sourceIds = new[] { state.Routing.PreviewSourceId, state.Routing.ProgramSourceId }.Distinct().ToArray();
			return new OperatorStatusSnapshot(
				state,
				sourceIds.Select((id, index) => new OperatorSourceDescriptor(id.ToString(), $"Input {index + 1}")).ToArray(),
				"Ready",
				"Locked",
				"Valid",
				"Optional",
				"Idle",
				false,
				0);
		}
	}

	private static IntegrationFeedbackSnapshot Feedback(
		string preview = "preview",
		string program = "program",
		string recording = "IDLE",
		IReadOnlyDictionary<string, string>? outputHealth = null) =>
		new(
			1,
			DateTimeOffset.UtcNow,
			preview,
			program,
			null,
			recording,
			"PASS:Ready",
			"READY",
			"IDLE",
			outputHealth ?? new Dictionary<string, string>(StringComparer.Ordinal));
}
