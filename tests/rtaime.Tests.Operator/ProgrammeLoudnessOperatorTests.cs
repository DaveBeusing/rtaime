// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Xml.Linq;
using rtaime.Client;
using rtaime.Media.Contracts;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class ProgrammeLoudnessOperatorTests
{
	[Fact]
	public void Read_only_bus_meters_bind_existing_runtime_evidence_without_new_audio_controls()
	{
		DirectoryInfo? root = new(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
			root = root.Parent;
		Assert.NotNull(root);
		var document = XDocument.Load(Path.Combine(root.FullName, "src", "Hosts", "rtaime.Operator", "AudioMixerControl.xaml"));
		var bindings = new[] { "LoudnessStatus", "LoudnessMomentary", "LoudnessShortTerm",
			"LoudnessIntegrated", "LoudnessRange", "LoudnessTruePeak", "LoudnessDuration",
			"LoudnessReset", "LoudnessPolicyStatus", "LoudnessSource" };
		foreach (var property in bindings)
		{
			Assert.Contains(document.Descendants(), element =>
				element.Attributes().Any(attribute => attribute.Value.Contains("Binding " + property, StringComparison.Ordinal)));
		}
		Assert.DoesNotContain(document.Descendants(), element =>
			element.Name.LocalName is "MediaElement" or "DispatcherTimer");
		Assert.Contains(document.Descendants(), element =>
			element.Attribute("AutomationProperties.Name")?.Value ==
			"Program and bus loudness and true-peak evidence");
	}

	[Fact]
	public void No_loudness_payload_remains_compatible_with_existing_output_meters()
	{
		var bus = new OperatorAudioProductionBusDescriptor(
			"program", 1, false, 0.25, 0.3, 0.3, false, 0, 1, 0);
		Assert.Equal(0.25, bus.LeftPeak);
		Assert.Equal(0.3, bus.RightPeak);
		Assert.Null(bus.Loudness);
	}

	[Fact]
	public void Source_qualification_cannot_be_promoted_by_live_sample_values()
	{
		var reading = new ProgrammeLoudnessSnapshot(1, "ebu-r128-linear",
			"CURRENT", 48000, 2, 40, 2, -23.0, -23.0, -23.0, 3.0, -1.5,
			-33.0, "WITHIN TARGET", "runtime:post-bus-dynamics-and-safety-clamp:program");
		Assert.True(reading.HasIntegratedReading);
		Assert.True(reading.HasTruePeakReading);
		Assert.Equal("UNVERIFIED", reading.Qualification);
	}
}
