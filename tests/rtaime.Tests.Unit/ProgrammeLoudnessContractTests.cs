// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class ProgrammeLoudnessContractTests
{
	[Fact]
	public void Old_bus_payload_without_loudness_field_is_compatible_and_never_looks_measured()
	{
		const string oldPayload = """
			{"BusId":"program","MasterGain":1,"Muted":false,"LeftPeak":0,"RightPeak":0,
			"PreClipPeak":0,"Clipping":false,"ClippedSampleValues":0,
			"ActiveSourceCount":0,"MissingSourceCount":0}
			""";
		var bus = JsonSerializer.Deserialize<OperatorAudioProductionBusDescriptor>(oldPayload);
		Assert.NotNull(bus);
		Assert.Equal("program", bus.BusId);
		Assert.Null(bus.Loudness);
	}

	[Fact]
	public void Optional_v1_measurement_round_trips_without_loss_of_validity_and_provenance()
	{
		var reading = new ProgrammeLoudnessSnapshot(
			1, "ebu-r128-linear", "CURRENT", 48000, 2, 15, 3,
			-22.9, -23.0, -22.9, null, -2.0, -32.9,
			"WITHIN TARGET", "runtime:post-bus-dynamics-and-safety-clamp:program",
			"UNVERIFIED");
		var bus = new OperatorAudioProductionBusDescriptor(
			"program", 1, false, 0.5, 0.5, 0.5, false, 0, 1, 0,
			Loudness: reading);
		var json = JsonSerializer.Serialize(bus);
		var returned = JsonSerializer.Deserialize<OperatorAudioProductionBusDescriptor>(json);
		Assert.NotNull(returned);
		Assert.Equal(reading, returned.Loudness);
		Assert.Equal("UNVERIFIED", returned.Loudness!.Qualification);
	}

	[Fact]
	public void Broadcast_policy_is_advisory_and_profile_ranges_are_guarded()
	{
		var p = ProgrammeLoudnessPolicy.EbuR128Linear;
		Assert.Equal(-23, p.TargetLufs);
		Assert.Equal(1, p.ToleranceLu);
		Assert.Equal(-1, p.MaximumTruePeakDbtp);
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new ProgrammeLoudnessPolicy("streaming", double.NaN, 1, -1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new ProgrammeLoudnessPolicy("streaming", -18, 0, -1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new ProgrammeLoudnessPolicy("streaming", -18, 1, 2));
	}
}
