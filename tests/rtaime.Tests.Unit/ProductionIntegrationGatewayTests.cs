// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

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
