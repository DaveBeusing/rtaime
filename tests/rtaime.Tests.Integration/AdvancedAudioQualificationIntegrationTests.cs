// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class AdvancedAudioQualificationIntegrationTests
{
	[Fact]
	public async Task Full_authoritative_configuration_crosses_Client_Control_and_Runtime()
	{
		var runtimeEndpoint = Endpoint("advanced-audio-runtime");
		var controlEndpoint = Endpoint("advanced-audio-control");
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			ConnectTimeout = TimeSpan.FromSeconds(1),
			RequestTimeout = TimeSpan.FromSeconds(10),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		try
		{
			await WaitUntilAsync(
				() => control.Lifecycle.State == ControlHostProcessState.Ready &&
					control.Control?.HasAuthoritativeState == true,
				timeoutMilliseconds: 10_000);

			var client = new OperatorControlClient(
				new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12)));
			var staleClient = new OperatorControlClient(
				new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12)));
			var initial = await client.SynchronizeAsync();
			var stale = await staleClient.SynchronizeAsync();
			var current = Assert.IsType<OperatorAudioProductionDescriptor>(initial.AudioProduction);
			var sourceA = new MediaSourceId(Identity.Parse(initial.Sources[0].Id));
			var sourceB = new MediaSourceId(Identity.Parse(initial.Sources[1].Id));
			var aux = new AudioBusId("aux");
			var clean = new AudioBusId("clean");
			var iso = new AudioBusId("iso");
			var dynamics = new AudioBusDynamicsConfiguration(
				new AudioBusCompressorConfiguration(true, -18, 4, 5, 120, 2),
				new AudioBusSamplePeakLimiterConfiguration(true, -1, 100));
			var equalizerA = new AudioSourceEqualizerConfiguration(
				new AudioLowShelfEqualizerBand(true, 180, 3),
				new AudioBellEqualizerBand(true, 1_200, -2, 1.25),
				new AudioHighShelfEqualizerBand(true, 7_500, 1));
			var equalizerB = new AudioSourceEqualizerConfiguration(
				new AudioLowShelfEqualizerBand(true, 220, -2),
				new AudioBellEqualizerBand(true, 900, 2, 0.8),
				new AudioHighShelfEqualizerBand(true, 8_500, -1));
			var requested = new AudioProductionConfiguration(
				checked(current.Configuration.Revision + 1),
				new[]
				{
					new AudioProductionBusConfiguration(AudioBusId.Program, 0.75, muted: false, dynamics),
					new AudioProductionBusConfiguration(aux, 0.5, muted: false, dynamics),
					new AudioProductionBusConfiguration(clean, 0.6, muted: false, dynamics),
					new AudioProductionBusConfiguration(iso, 0.8, muted: false, dynamics)
				},
				new[]
				{
					new AudioProductionSourceConfiguration(
						sourceA,
						0.7,
						muted: false,
						followRoutedSource: false,
						new[] { AudioBusId.Program, aux, clean },
						equalizerA),
					new AudioProductionSourceConfiguration(
						sourceB,
						0.4,
						muted: false,
						followRoutedSource: true,
						new[] { AudioBusId.Program, aux, iso },
						equalizerB)
				},
				new AudioCrossfadeConfiguration(
					AudioBusId.Program,
					sourceA,
					sourceB,
					startSamplePosition: 48_000,
					durationSamples: 24_000,
					AudioCrossfadeLaw.EqualPower),
				new AudioDuckingConfiguration(
					AudioBusId.Program,
					enabled: true,
					sourceA,
					new[] { sourceB },
					threshold: 0.2,
					attenuation: 0.25,
					attackSamples: 2_400,
					holdSamples: 12_000,
					releaseSamples: 14_400));

			var confirmed = await client.SetAudioProductionAsync(requested);

			Assert.Equal(requested.Revision, confirmed.Configuration.Revision);
			Assert.Equal(AudioProductionLimits.MaximumBuses, confirmed.Configuration.Buses.Count);
			Assert.Equal(2, confirmed.Configuration.Sources.Count);
			Assert.Equal(3, confirmed.Configuration.GetSource(sourceA).BusAssignments.Count);
			Assert.Equal(3, confirmed.Configuration.GetSource(sourceB).BusAssignments.Count);
			Assert.False(confirmed.Configuration.GetSource(sourceA).FollowRoutedSource);
			Assert.True(confirmed.Configuration.GetSource(sourceB).FollowRoutedSource);
			Assert.NotNull(confirmed.Configuration.Crossfade);
			Assert.NotNull(confirmed.Configuration.Ducking);
			Assert.All(confirmed.Configuration.Buses, bus => Assert.NotNull(bus.Dynamics));
			Assert.All(confirmed.Configuration.Sources, source => Assert.NotNull(source.Equalizer));

			await WaitUntilAsync(
				() => runtime.Runtime?.Snapshot.AudioProduction?.Revision == requested.Revision,
				timeoutMilliseconds: 10_000);
			var runtimeAudio = Assert.IsType<V1AudioProductionSnapshot>(runtime.Runtime!.Snapshot.AudioProduction);
			Assert.Equal(requested.Revision, runtimeAudio.Revision);
			Assert.Equal(AudioProductionLimits.MaximumBuses, runtimeAudio.Buses.Count);
			Assert.Equal(2, runtimeAudio.Sources.Count);
			Assert.Equal(-18, Assert.IsType<AudioBusDynamicsConfiguration>(
				runtimeAudio.Buses.Single(bus => bus.BusId == AudioBusId.Program.Value).Dynamics).Compressor.ThresholdDbFs);
			Assert.Equal(1_200, Assert.IsType<AudioSourceEqualizerConfiguration>(
				runtimeAudio.Sources.Single(source => source.SourceId == sourceA).Equalizer).Mid.FrequencyHz);

			var auxRoute = await client.RouteOutputRoleAsync("aux", initial.Sources[1].Id, aux.Value);
			Assert.True(auxRoute.Accepted, auxRoute.Failure?.ToString());
			Assert.Equal(
				aux.Value,
				Assert.Single(client.Snapshot!.OutputRoles, role => role.RoleId == "aux").AudioBusId);
			Assert.Equal(
				aux.Value,
				Assert.Single(runtime.Runtime.Snapshot.OutputRoles!, role => role.RoleId == "aux").AudioBusId);

			var staleProduction = Assert.IsType<OperatorAudioProductionDescriptor>(stale.AudioProduction);
			var staleConfiguration = new AudioProductionConfiguration(
				checked(staleProduction.Configuration.Revision + 1),
				staleProduction.Configuration.Buses,
				staleProduction.Configuration.Sources,
				staleProduction.Configuration.Crossfade,
				staleProduction.Configuration.Ducking,
				staleProduction.Configuration.ClipStrategy);
			var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
				() => staleClient.SetAudioProductionAsync(staleConfiguration));
			Assert.Contains("control.audio.production.revision_conflict", conflict.Message, StringComparison.Ordinal);
			Assert.Equal(requested.Revision, client.Snapshot.AudioProduction!.Configuration.Revision);
		}
		finally
		{
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (!condition())
		{
			if (DateTime.UtcNow >= deadline)
				throw new TimeoutException("Condition was not reached before the integration-test deadline.");
			await Task.Delay(20);
		}
	}

	private static string Endpoint(string purpose) => $"rtaime.test.{purpose}.{Guid.NewGuid():N}";
}
