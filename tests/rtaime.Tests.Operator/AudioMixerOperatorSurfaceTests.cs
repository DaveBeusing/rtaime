// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class AudioMixerOperatorSurfaceTests
{
	[Fact]
	public async Task Mixer_projects_maximum_eight_sources_and_four_buses_from_confirmed_Runtime_evidence()
	{
		await using var owner = new OperatorViewModel(runtimeReadiness: new PassiveReadinessService());
		var buses = new[]
		{
			AudioBusId.Program,
			new AudioBusId("aux"),
			new AudioBusId("clean"),
			new AudioBusId("iso")
		};
		var sourceIds = Enumerable.Range(1, AudioProductionLimits.MaximumSources)
			.Select(index => new MediaSourceId(Identity.Parse($"ac000000-0000-0000-0000-{index:000000000000}")))
			.ToArray();
		var dynamics = Dynamics();
		var configuration = new AudioProductionConfiguration(
			7,
			buses.Select(bus => new AudioProductionBusConfiguration(bus, 1, false, dynamics)).ToArray(),
			sourceIds.Select(source => new AudioProductionSourceConfiguration(
				source,
				1,
				false,
				false,
				buses,
				Equalizer())).ToArray());
		var evidence = buses.Select((bus, index) => new OperatorAudioProductionBusDescriptor(
			bus.Value,
			1,
			false,
			0.1 + (index * 0.1),
			0.08 + (index * 0.1),
			0.4,
			index == 3,
			index == 3 ? 2UL : 0UL,
			8,
			index == 2 ? 1 : 0,
			0.5,
			index + 0.5,
			index + 0.25,
			(ulong)index,
			dynamics)).ToArray();
		var production = Descriptor(configuration, evidence);
		var sources = sourceIds.Select((source, index) =>
			new OperatorSourceDescriptor(source.ToString(), $"Source {index + 1}", "LIVE", "1080p50", "PASS")).ToArray();
		var inputs = sourceIds.Select((source, index) =>
			new OperatorAudioInputDescriptor(
				source.ToString(),
				$"stream-{index + 1}",
				1,
				false,
				Math.Min(1, 0.1 + index * 0.05),
				Math.Min(1, 0.08 + index * 0.05),
				Math.Min(1, 0.1 + index * 0.05),
				false,
				index == 7 ? "UNDERRUN" : "HEALTHY")).ToArray();
		var outputRoles = new[]
		{
			OutputRole("program", "program", sourceIds[0]),
			OutputRole("aux", "aux", sourceIds[1])
		};

		owner.AudioMixer.ApplyProjection(production, sources, inputs, outputRoles, refreshDrafts: true);

		Assert.Equal(AudioProductionLimits.MaximumSources, owner.AudioMixer.Sources.Count);
		Assert.Equal(AudioProductionLimits.MaximumBuses, owner.AudioMixer.Buses.Count);
		Assert.All(owner.AudioMixer.Sources, source => Assert.Equal(4, source.Assignments.Count));
		Assert.All(owner.AudioMixer.Sources, source => Assert.All(source.Assignments, assignment => Assert.True(assignment.IsAssigned)));
		Assert.Equal("MISSING / UNDERRUN", owner.AudioMixer.Sources[^1].RuntimeState);
		var program = Assert.Single(owner.AudioMixer.Buses.Where(bus => bus.BusId == "program"));
		Assert.Equal(0.1, program.LeftPeak, 6);
		Assert.Contains("PROGRAM", program.OutputRoles, StringComparison.Ordinal);
		var aux = Assert.Single(owner.AudioMixer.Buses.Where(bus => bus.BusId == "aux"));
		Assert.Contains("AUX", aux.OutputRoles, StringComparison.Ordinal);
		Assert.Contains("RMS NOT EXPOSED", owner.AudioMixer.MeteringLabel, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Source_and_bus_drafts_preserve_during_meter_refresh_and_reset_on_authoritative_refresh()
	{
		await using var owner = new OperatorViewModel(runtimeReadiness: new PassiveReadinessService());
		var sourceId = new MediaSourceId(Identity.Parse("ac100000-0000-0000-0000-000000000001"));
		var dynamics = Dynamics();
		var configuration = new AudioProductionConfiguration(
			11,
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 1, false, dynamics) },
			new[] { new AudioProductionSourceConfiguration(sourceId, 1, false, true, new[] { AudioBusId.Program }, Equalizer()) });
		var evidence = new[]
		{
			new OperatorAudioProductionBusDescriptor("program", 1, false, 0.2, 0.15, 0.3, false, 0, 1, 0, 0.3, 2.5, 1.25, 3, dynamics)
		};
		var production = Descriptor(configuration, evidence);
		var sources = new[] { new OperatorSourceDescriptor(sourceId.ToString(), "Camera A") };
		var inputs = new[] { new OperatorAudioInputDescriptor(sourceId.ToString(), "stream-a", 1, false, 0.2, 0.15, 0.2, false, "HEALTHY") };

		owner.AudioMixer.ApplyProjection(production, sources, inputs, Array.Empty<OperatorOutputRoleDescriptor>(), refreshDrafts: true);
		var source = Assert.Single(owner.AudioMixer.Sources);
		var bus = Assert.Single(owner.AudioMixer.Buses);
		source.Gain = 1.7;
		source.MidGainDb = 4.5;
		bus.MasterGain = 0.75;
		bus.CompressorThresholdDbFs = -24;

		owner.AudioMixer.ApplyProjection(
			Descriptor(configuration, new[]
			{
				evidence[0] with { LeftPeak = 0.42, CompressorGainReductionDb = 4.0 }
			}),
			sources,
			new[]
			{
				new OperatorAudioInputDescriptor(
					inputs[0].SourceId,
					inputs[0].StreamId,
					inputs[0].Gain,
					inputs[0].Muted,
					0.42,
					inputs[0].RightPeak,
					0.42,
					inputs[0].Clipping,
					inputs[0].Health)
			},
			Array.Empty<OperatorOutputRoleDescriptor>(),
			refreshDrafts: false);

		Assert.Equal(1.7, source.Gain);
		Assert.Equal(4.5, source.MidGainDb);
		Assert.Equal(0.75, bus.MasterGain);
		Assert.Equal(-24, bus.CompressorThresholdDbFs);
		Assert.Equal(0.42, source.LeftPeak, 6);
		Assert.Equal(4.0, bus.CompressorReductionDb, 6);
		Assert.True(source.HasDraftChanges);
		Assert.True(bus.HasDraftChanges);

		var sourceConfig = source.BuildConfiguration(configuration.Sources[0]);
		Assert.Equal(1.7, sourceConfig.Gain);
		Assert.Equal(4.5, sourceConfig.Equalizer!.Mid.GainDb);
		var busConfig = bus.BuildConfiguration(configuration.Buses[0]);
		Assert.Equal(0.75, busConfig.MasterGain);
		Assert.Equal(-24, busConfig.Dynamics!.Compressor.ThresholdDbFs);

		owner.AudioMixer.ApplyProjection(production, sources, inputs, Array.Empty<OperatorOutputRoleDescriptor>(), refreshDrafts: true);
		Assert.Equal(1, source.Gain);
		Assert.Equal(Equalizer().Mid.GainDb, source.MidGainDb);
		Assert.Equal(1, bus.MasterGain);
		Assert.Equal(dynamics.Compressor.ThresholdDbFs, bus.CompressorThresholdDbFs);
		Assert.False(source.HasDraftChanges);
		Assert.False(bus.HasDraftChanges);
	}

	[Fact]
	public async Task Routing_cell_exposes_pending_and_rejected_without_changing_confirmed_assignment()
	{
		await using var owner = new OperatorViewModel(runtimeReadiness: new PassiveReadinessService());
		var sourceId = new MediaSourceId(Identity.Parse("ac200000-0000-0000-0000-000000000001"));
		var aux = new AudioBusId("aux");
		var configuration = new AudioProductionConfiguration(
			3,
			new[]
			{
				new AudioProductionBusConfiguration(AudioBusId.Program, 1, false),
				new AudioProductionBusConfiguration(aux, 1, false)
			},
			new[] { new AudioProductionSourceConfiguration(sourceId, 1, false, false, new[] { AudioBusId.Program }) });
		var production = Descriptor(configuration, new[]
		{
			new OperatorAudioProductionBusDescriptor("program", 1, false, 0, 0, 0, false, 0, 1, 0),
			new OperatorAudioProductionBusDescriptor("aux", 1, false, 0, 0, 0, false, 0, 0, 0)
		});
		owner.AudioMixer.ApplyProjection(
			production,
			new[] { new OperatorSourceDescriptor(sourceId.ToString(), "Source") },
			new[] { new OperatorAudioInputDescriptor(sourceId.ToString(), "stream", 1, false, 0, 0, 0, false, "HEALTHY") },
			Array.Empty<OperatorOutputRoleDescriptor>(),
			refreshDrafts: true);

		var source = Assert.Single(owner.AudioMixer.Sources);
		var cell = Assert.Single(source.Assignments.Where(item => item.BusId == "aux"));
		Assert.False(cell.IsAssigned);
		Assert.Equal("CONFIRMED", cell.State);

		cell.BeginMutation();
		Assert.Equal("PENDING", cell.State);
		Assert.False(cell.IsAssigned);

		cell.CompleteMutation(success: false);
		Assert.Equal("REJECTED", cell.State);
		Assert.False(cell.IsAssigned);
	}

	private static OperatorAudioProductionDescriptor Descriptor(
		AudioProductionConfiguration configuration,
		IReadOnlyList<OperatorAudioProductionBusDescriptor> buses) =>
		new(
			configuration,
			buses.First().LeftPeak,
			buses.First().RightPeak,
			buses.First().PreClipPeak,
			buses.First().Clipping,
			buses.First().ClippedSampleValues,
			1,
			0,
			true,
			null,
			configuration.Sources.Count,
			buses.First().MissingSourceCount,
			buses);

	private static AudioSourceEqualizerConfiguration Equalizer() =>
		new(
			new AudioLowShelfEqualizerBand(true, 120, 1.5),
			new AudioBellEqualizerBand(true, 1_000, -2, 1.2),
			new AudioHighShelfEqualizerBand(false, 8_000, 0));

	private static AudioBusDynamicsConfiguration Dynamics() =>
		new(
			new AudioBusCompressorConfiguration(true, -18, 4, 5, 120, 2),
			new AudioBusSamplePeakLimiterConfiguration(true, -1, 100));

	private static OperatorOutputRoleDescriptor OutputRole(string roleId, string audioBusId, MediaSourceId source) =>
		new(
			roleId,
			roleId.ToUpperInvariant(),
			source.ToString(),
			$"{roleId}-target",
			"provider",
			1920,
			1080,
			"50",
			"RGBA8",
			"LOCKED",
			"ACTIVE",
			true,
			"PASS",
			"confirmed",
			null,
			audioBusId: audioBusId);

	private sealed class PassiveReadinessService : IRuntimeReadinessService
	{
		public RuntimeReadinessSnapshot Current { get; } =
			RuntimeReadinessSnapshot.Initial(DateTimeOffset.UnixEpoch);

		public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed
		{
			add { }
			remove { }
		}

		public void Observe(RuntimeReadinessObservation observation)
		{
		}

		public void InvalidatePerformance(string detail)
		{
		}
	}
}
