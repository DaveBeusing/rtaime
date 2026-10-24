// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class AudioProductionUnderrunTests
{
	[Fact]
	public void Partial_input_contributes_real_samples_and_missing_evidence_to_each_assigned_bus()
	{
		var source = MediaSourceId.New();
		var other = MediaSourceId.New();
		var aux = new AudioBusId("aux");
		var engine = new AudioProductionEngine(new AudioProductionConfiguration(1,
			[new AudioProductionBusConfiguration(AudioBusId.Program, 1, false),
			 new AudioProductionBusConfiguration(aux, 1, false)],
			[new AudioProductionSourceConfiguration(source, 1, false, false, [AudioBusId.Program, aux]),
			 new AudioProductionSourceConfiguration(other, 1, false, false, [AudioBusId.Program, aux])]));
		AudioProductionSourceBuffer[] inputs =
		[
			new(source, new float[] { 0.25f, -0.125f, 0, 0 }, Available: true, Underrun: true),
			new(other, new float[] { 0.5f, 0.25f, 0.5f, 0.25f })
		];
		foreach (var bus in new[] { AudioBusId.Program, aux })
		{
			var output = new float[4];
			var result = engine.ProcessBus(bus, 0, 2, other, inputs, output);
			Assert.Equal(new float[] { 0.75f, 0.125f, 0.5f, 0.25f }, output);
			Assert.Equal(2, result.ActiveSourceCount);
			Assert.Equal(1, result.MissingSourceCount);
		}

		inputs[0] = new AudioProductionSourceBuffer(source, new float[4], Available: false, Underrun: true);
		var next = new float[4];
		var missing = engine.ProcessBus(AudioBusId.Program, 2, 2, other, inputs, next);
		Assert.Equal(new float[] { 0.5f, 0.25f, 0.5f, 0.25f }, next);
		Assert.Equal(1, missing.ActiveSourceCount);
		Assert.Equal(1, missing.MissingSourceCount);
	}
}
