// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class ProgrammeLoudnessArchitectureTests
{
	[Fact]
	public void Loudness_remains_an_observational_post_bus_component_without_a_second_mixer_or_UI_sampler()
	{
		var root = RepositorySnapshot.Load().Root;
		var runtime = Read(root, "src/Hosts/rtaime.RuntimeHost/RuntimeProgramExecutionService.cs");
		var analyser = Read(root, "src/Media/rtaime.Media/ProgrammeLoudnessAnalyzer.cs");
		var mixer = Read(root, "src/Media/rtaime.Media/AudioProduction.cs");
		var view = Read(root, "src/Hosts/rtaime.Operator/OperatorAudioMixerViewModel.cs");

		Assert.Contains("_audioProduction.ProcessBus(", runtime, StringComparison.Ordinal);
		Assert.Contains("analyser.Process(", runtime, StringComparison.Ordinal);
		Assert.Contains("_audioBusMixSamples[bus.BusId].AsSpan(0, requiredValues)", runtime, StringComparison.Ordinal);
		Assert.Contains("RTAIME_PROGRAMME_LOUDNESS_PROFILE", runtime, StringComparison.Ordinal);
		Assert.Contains("if (_programmeLoudnessPolicy is not null", runtime, StringComparison.Ordinal);
		Assert.DoesNotContain("ProgrammeLoudnessAnalyzer", mixer, StringComparison.Ordinal);
		Assert.DoesNotContain("ProgrammeLoudnessAnalyzer", view, StringComparison.Ordinal);
		Assert.DoesNotContain("DispatcherTimer", analyser, StringComparison.Ordinal);
		Assert.DoesNotContain("Task.Delay", analyser, StringComparison.Ordinal);
		Assert.DoesNotContain("File.Write", analyser, StringComparison.Ordinal);
	}

	[Fact]
	public void All_existing_audio_management_wire_owners_expose_optional_loudness()
	{
		var root = RepositorySnapshot.Load().Root;
		foreach (var path in new[]
		{
			"src/Hosts/rtaime.RuntimeHost/RuntimeHostIpcServer.cs",
			"src/Hosts/rtaime.ControlHost/RuntimeHostIpcTransport.cs",
			"src/Hosts/rtaime.ControlHost/ControlHostIpcServer.cs",
			"src/Client/rtaime.Client/NamedPipeOperatorControlTransport.cs"
		})
		{
			var wire = Read(root, path);
			Assert.Contains("ProgrammeLoudnessSnapshot? Loudness = null", wire, StringComparison.Ordinal);
		}
		Assert.Contains("ProgrammeLoudnessSnapshot? Loudness = null",
			Read(root, "src/Client/rtaime.Client/OperatorControlClient.cs"), StringComparison.Ordinal);
	}

	private static string Read(string root, string path) =>
		File.ReadAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
}
