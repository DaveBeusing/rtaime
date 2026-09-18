// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text.Json;
using rtaime.AIHost;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class DemoProductionPackageIntegrationTests
{
	[Fact]
	public async Task Bundled_demo_clip_prepares_reproducible_take_ready_state_through_real_process_boundaries()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var root = Path.Combine(Path.GetTempPath(), "rtaime-ap56", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var clipPath = Path.Combine(root, "rtaime-demo-product.mp4");
			File.WriteAllBytes(clipPath, DecodeBundledProductClip());

			await using var fixture = await ProcessFixture.StartAsync();
			await using var deck = new MediaDeckController(fixture.Client);
			var first = await ApplyDemoStateAsync(fixture.Client, deck, clipPath);
			AssertReady(first, deck.Snapshot);

			var second = await ApplyDemoStateAsync(fixture.Client, deck, clipPath);
			AssertReady(second, deck.Snapshot);

			Assert.Equal(first.Production.Routing.ProgramSourceId, second.Production.Routing.ProgramSourceId);
			Assert.Equal(first.Production.Routing.PreviewSourceId, second.Production.Routing.PreviewSourceId);
			Assert.Equal(2, deck.Snapshot.Markers!.CuePoints.Count);
			Assert.Equal(new[] { "Product Intro", "Product End" }, deck.Snapshot.Markers.CuePoints.Select(cue => cue.Name).ToArray());
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	private static async Task<OperatorStatusSnapshot> ApplyDemoStateAsync(
		OperatorControlClient client,
		MediaDeckController deck,
		string clipPath)
	{
		var snapshot = await SynchronizeWithRetryAsync(client);
		var inputA = Assert.Single(snapshot.Sources, source => source.Name == "Input A");
		var inputB = Assert.Single(snapshot.Sources, source => source.Name == "Input B");

		if (snapshot.Production.Routing.ProgramSourceId.ToString() != inputA.Id)
		{
			if (snapshot.Production.Routing.PreviewSourceId.ToString() != inputA.Id)
				Assert.True((await client.SelectPreviewAsync(inputA.Id)).Accepted);
			Assert.True((await client.CutAsync(inputA.Id)).Accepted);
		}

		if (deck.Snapshot.IsLoaded)
			await deck.CloseAsync();

		var opened = await deck.OpenAsync(clipPath, new MediaSourceId(Identity.Parse(inputB.Id)));
		Assert.True(opened.IsLoaded, opened.Failure?.Message);
		Assert.Equal(MediaContainerFormat.Mp4, opened.Probe!.Container);
		Assert.Equal(MediaVideoCodec.H264, opened.Probe.VideoCodec);
		Assert.Equal(MediaAudioCodec.Aac, opened.Probe.AudioCodec);
		Assert.Equal(VideoFormat.Hd1080p50Rgba8, opened.Probe.VideoFormat);
		Assert.Equal(AudioFormat.Stereo48kFloat32, opened.Probe.AudioFormat);

		foreach (var cue in deck.Markers.State.CuePoints.ToArray())
			Assert.True(await deck.Markers.DeleteCueAsync(cue.Id));
		if (deck.Markers.State.InPointFrame is not null)
			Assert.True(await deck.Markers.ClearInAsync());
		if (deck.Markers.State.OutPointFrame is not null)
			Assert.True(await deck.Markers.ClearOutAsync());

		await deck.Timeline.SeekToFrameAsync(0);
		Assert.True(await deck.Markers.SetInAtCurrentFrameAsync());
		await deck.Timeline.SeekToFrameAsync(5);
		Assert.NotNull(await deck.Markers.AddCueAtCurrentFrameAsync("Product Intro"));
		await deck.Timeline.SeekToFrameAsync(20);
		Assert.NotNull(await deck.Markers.AddCueAtCurrentFrameAsync("Product End"));

		var last = deck.Snapshot.Transport!.Position.TotalFrames - 1;
		Assert.True(last > 20);
		await deck.Timeline.SeekToFrameAsync(last);
		Assert.True(await deck.Markers.SetOutAtCurrentFrameAsync());
		await deck.Timeline.SeekToFrameAsync(0);
		await deck.ConfigurePlaybackAsync(true, MediaDeckEndBehavior.HoldLastFrame);

		await client.SetAudioInputStateAsync(inputB.Id, 1.0, muted: false);
		await client.LoadGraphicsOverlayAsync(new OperatorGraphicsAsset(
			"rtaime-demo-lower-third.png",
			1,
			1,
			new byte[] { 255, 255, 255, 255 }));
		await client.SetGraphicsOverlayAsync(false, 0.02, 0.80, 1.0);
		await client.SetAIShowcaseEnabledAsync(true);

		var latest = await client.SynchronizeAsync();
		if (latest.Production.Routing.PreviewSourceId.ToString() != inputB.Id)
			Assert.True((await client.SelectPreviewAsync(inputB.Id)).Accepted);

		return await client.SynchronizeAsync();
	}

	private static void AssertReady(OperatorStatusSnapshot snapshot, MediaDeckSnapshot deck)
	{
		var inputA = Assert.Single(snapshot.Sources, source => source.Name == "Input A");
		var inputB = Assert.Single(snapshot.Sources, source => source.Name == "Input B");
		Assert.Equal(inputA.Id, snapshot.Production.Routing.ProgramSourceId.ToString());
		Assert.Equal(inputB.Id, snapshot.Production.Routing.PreviewSourceId.ToString());
		Assert.True(deck.IsLoaded);
		Assert.Equal(inputB.Id, deck.SourceId!.Value.ToString());
		Assert.Equal(0, deck.Markers!.InPointFrame);
		Assert.Equal(deck.Transport!.Position.TotalFrames - 1, deck.Markers.OutPointFrame);
		Assert.True(deck.Transport.AutoPlayOnProgram);
		Assert.Equal(MediaDeckEndBehavior.HoldLastFrame, deck.Transport.EndBehavior);
		Assert.True(snapshot.GraphicsOverlay.AssetLoaded);
		Assert.False(snapshot.GraphicsOverlay.Visible);
		Assert.True(snapshot.AIShowcase.Enabled);
		var audio = Assert.Single(snapshot.AudioInputs, input => input.SourceId == inputB.Id);
		Assert.Equal(1.0, audio.Gain, 6);
		Assert.False(audio.Muted);
	}

	private static byte[] DecodeBundledProductClip()
	{
		var root = FindRepositoryRoot();
		var assets = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "DemoAssets");
		using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "demo-production.package.json")));
		var product = manifest.RootElement.GetProperty("productClip");
		var bundleFiles = product.GetProperty("bundleFiles")
			.EnumerateArray()
			.Select(element => element.GetString() ?? throw new InvalidDataException("Product Clip bundle file is required."))
			.ToArray();
		Assert.NotEmpty(bundleFiles);

		var encoded = string.Concat(
			bundleFiles.SelectMany(bundleFile =>
				File.ReadLines(Path.Combine(assets, bundleFile))
					.Select(line => line.Trim())
					.Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))));
		var bytes = Convert.FromBase64String(encoded);
		Assert.Equal(
			product.GetProperty("sha256").GetString(),
			Convert.ToHexStringLower(SHA256.HashData(bytes)));
		return bytes;
	}

	private static async Task<OperatorStatusSnapshot> SynchronizeWithRetryAsync(OperatorControlClient client)
	{
		Exception? last = null;
		for (var attempt = 0; attempt < 30; attempt++)
		{
			try { return await client.SynchronizeAsync(); }
			catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
			{
				last = exception;
				await Task.Delay(50);
			}
		}
		throw new InvalidOperationException("Operator snapshot did not become available.", last);
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new DirectoryNotFoundException("Repository root containing rtaime.slnx was not found.");
	}

	private sealed class ProcessFixture : IAsyncDisposable
	{
		private readonly CancellationTokenSource _aiStop;
		private readonly CancellationTokenSource _runtimeStop;
		private readonly CancellationTokenSource _controlStop;
		private readonly Task<AIHostExitCode> _aiRun;
		private readonly Task<RuntimeHostExitCode> _runtimeRun;
		private readonly Task<ControlHostExitCode> _controlRun;

		private ProcessFixture(
			OperatorControlClient client,
			CancellationTokenSource aiStop,
			CancellationTokenSource runtimeStop,
			CancellationTokenSource controlStop,
			Task<AIHostExitCode> aiRun,
			Task<RuntimeHostExitCode> runtimeRun,
			Task<ControlHostExitCode> controlRun)
		{
			Client = client;
			_aiStop = aiStop;
			_runtimeStop = runtimeStop;
			_controlStop = controlStop;
			_aiRun = aiRun;
			_runtimeRun = runtimeRun;
			_controlRun = controlRun;
		}

		public OperatorControlClient Client { get; }

		public static async Task<ProcessFixture> StartAsync()
		{
			var suffix = Guid.NewGuid().ToString("N");
			var aiEndpoint = $"rtaime.test.ap56.ai.{suffix}";
			var runtimeEndpoint = $"rtaime.test.ap56.runtime.{suffix}";
			var controlEndpoint = $"rtaime.test.ap56.control.{suffix}";
			var aiStop = new CancellationTokenSource();
			var runtimeStop = new CancellationTokenSource();
			var controlStop = new CancellationTokenSource();

			var ai = new AIHostProcess(AIHostProcessOptions.Default with { ListenEndpoint = aiEndpoint });
			var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with
			{
				ListenEndpoint = runtimeEndpoint,
				AIEndpoint = aiEndpoint
			});
			var control = new ControlHostProcess(ControlHostProcessOptions.Default with
			{
				ListenEndpoint = controlEndpoint,
				RuntimeEndpoint = runtimeEndpoint,
				ConnectTimeout = TimeSpan.FromMilliseconds(100),
				RequestTimeout = TimeSpan.FromSeconds(3),
				RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
			});

			var aiRun = ai.RunAsync(aiStop.Token);
			await WaitUntilAsync(() => ai.Lifecycle.State is AIHostProcessState.Ready or AIHostProcessState.Degraded);
			var runtimeRun = runtime.RunAsync(runtimeStop.Token);
			await WaitUntilAsync(() => runtime.Lifecycle.State == RuntimeHostProcessState.Ready);
			var controlRun = control.RunAsync(controlStop.Token);
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
				controlEndpoint,
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(4)));
			return new ProcessFixture(client, aiStop, runtimeStop, controlStop, aiRun, runtimeRun, controlRun);
		}

		public async ValueTask DisposeAsync()
		{
			_controlStop.Cancel();
			_runtimeStop.Cancel();
			_aiStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await _controlRun);
			Assert.Equal(RuntimeHostExitCode.Success, await _runtimeRun);
			Assert.Equal(AIHostExitCode.Success, await _aiRun);
			_controlStop.Dispose();
			_runtimeStop.Dispose();
			_aiStop.Dispose();
		}

		private static async Task WaitUntilAsync(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (!condition())
			{
				if (DateTime.UtcNow >= deadline)
					throw new TimeoutException("AP-56 process fixture did not reach the expected state.");
				await Task.Delay(20);
			}
		}
	}
}
