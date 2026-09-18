// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class MediaAutoplayProductionIpcIntegrationTests
{
	[Fact]
	public async Task CUT_and_DISSOLVE_start_cued_media_when_its_slot_becomes_Program()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var referenceAsset = LocalMediaTestAsset.ExtractReference1080p50();
		var runtimeEndpoint = $"rtaime.test.ap52.runtime.{Guid.NewGuid():N}";
		var controlEndpoint = $"rtaime.test.ap52.control.{Guid.NewGuid():N}";
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		await WaitUntilAsync(() =>
			control.Lifecycle.State == ControlHostProcessState.Ready &&
			control.Control?.HasAuthoritativeState == true);

		var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
			controlEndpoint,
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(5)));
		var initial = await client.SynchronizeAsync();
		var initialProgramId = initial.Production.Routing.ProgramSourceId.ToString();
		var mediaSource = initial.Sources.First(source => !string.Equals(source.Id, initialProgramId, StringComparison.Ordinal));
		var standbySource = initial.Sources.First(source => string.Equals(source.Id, initialProgramId, StringComparison.Ordinal));
		var mediaSourceId = new MediaSourceId(Identity.Parse(mediaSource.Id));

		await using var deck = new MediaDeckController(client);
		var opened = await deck.OpenAsync(referenceAsset.Path, mediaSourceId);
		Assert.True(opened.IsLoaded, opened.Failure?.Message);
		await deck.ConfigurePlaybackAsync(true, MediaDeckEndBehavior.HoldLastFrame);

		await deck.Timeline.SeekToFrameAsync(5);
		Assert.True(await deck.Markers.SetInAtCurrentFrameAsync());
		await deck.Timeline.SeekToFrameAsync(45);
		Assert.True(await deck.Markers.SetOutAtCurrentFrameAsync());
		Assert.True(await deck.Markers.JumpToInAsync());
		Assert.Equal(5, deck.Timeline.State.ConfirmedFrame);

		Assert.True((await client.SelectPreviewAsync(mediaSource.Id)).Accepted);
		Assert.True((await client.CutPreviewAsync()).Accepted);
		await WaitUntilAsync(async () =>
		{
			await deck.RefreshAsync();
			return deck.Snapshot.Transport is { State: MediaTransportState.Playing, IsOnProgram: true };
		});
		Assert.InRange(deck.Snapshot.Transport!.Position.CurrentFrame, 5, 45);
		Assert.Equal(mediaSource.Id, runtime.Runtime!.CommittedProgramSourceId?.ToString());

		Assert.True((await client.SelectPreviewAsync(standbySource.Id)).Accepted);
		Assert.True((await client.CutPreviewAsync()).Accepted);
		await WaitUntilAsync(() => runtime.Runtime!.CommittedProgramSourceId?.ToString() == standbySource.Id);
		await deck.PauseAsync();
		await deck.Timeline.SeekToFrameAsync(12);
		Assert.Equal(12, deck.Timeline.State.ConfirmedFrame);

		Assert.True((await client.SelectPreviewAsync(mediaSource.Id)).Accepted);
		Assert.True((await client.DissolvePreviewAsync(6)).Accepted);
		await WaitUntilAsync(async () =>
		{
			await deck.RefreshAsync();
			return deck.Snapshot.Transport is { State: MediaTransportState.Playing, IsOnProgram: true };
		});
		Assert.InRange(deck.Snapshot.Transport!.Position.CurrentFrame, 12, 45);
		Assert.Equal(mediaSource.Id, runtime.Runtime!.CommittedProgramSourceId?.ToString());

		controlStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		runtimeStop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(8);
		while (!condition() && DateTime.UtcNow < deadline)
			await Task.Delay(25);
		Assert.True(condition());
	}

	private static async Task WaitUntilAsync(Func<Task<bool>> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(8);
		while (!await condition() && DateTime.UtcNow < deadline)
			await Task.Delay(25);
		Assert.True(await condition());
	}
}
