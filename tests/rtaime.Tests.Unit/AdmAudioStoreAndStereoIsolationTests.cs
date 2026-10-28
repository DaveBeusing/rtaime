// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class AdmAudioStoreAndStereoIsolationTests
{
	private static readonly string Fingerprint = new('A', 64);
	private static readonly UtcTimestamp Stamp =
		new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

	[Fact]
	public async Task Persisted_ADM_is_bound_to_immutable_asset_and_survives_restart_as_metadata_only()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-adm-" + Guid.NewGuid().ToString("N"));
		try
		{
			var asset = Asset();
			var observation = AdmAudioXml.ImportXml(Fixture("stereo.xml"), asset);
			await new AdmAudioSidecarStore(root).SaveAsync(observation, asset);
			var restored = await new AdmAudioSidecarStore(root).LoadAsync(asset);
			Assert.NotNull(restored);
			Assert.Equal(Fixture("stereo.xml"), AdmAudioXml.ExportXml(restored));
			Assert.Equal(asset.AssetId.ToString(), restored.Interchange.AssetId);
			Assert.Equal(asset.FingerprintSha256, restored.Interchange.FingerprintSha256);
			Assert.StartsWith("UNSUPPORTED", restored.PublishStatus);
			Assert.Null(await new AdmAudioSidecarStore(root).LoadAsync(Asset(MediaAssetId.New())));
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
				await new AdmAudioSidecarStore(root).LoadAsync(Asset(fingerprint: new string('B', 64))));
		}
		finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
	}

	[Fact]
	public async Task Failed_or_cancelled_overwrite_does_not_replace_existing_sidecar()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-adm-" + Guid.NewGuid().ToString("N"));
		try
		{
			var asset = Asset();
			var store = new AdmAudioSidecarStore(root);
			var initial = AdmAudioXml.ImportXml(Fixture("stereo.xml"), asset);
			await store.SaveAsync(initial, asset);
			using var cancel = new CancellationTokenSource();
			cancel.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
				await store.SaveAsync(AdmAudioXml.ImportXml(Fixture("surround-5-1.xml"), asset),
					asset, cancel.Token));
			Assert.Equal(Fixture("stereo.xml"), AdmAudioXml.ExportXml((await store.LoadAsync(asset))!));
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
				await store.SaveAsync(AdmAudioXml.ImportXml(Fixture("stereo.xml"),
					Asset(fingerprint: new string('B', 64))), asset));
			Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
			Assert.Equal(Fixture("stereo.xml"), AdmAudioXml.ExportXml((await store.LoadAsync(asset))!));
		}
		finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
	}

	[Fact]
	public void Imported_ADM_does_not_change_a_single_stereo_PCM_sample_or_routing_semantic()
	{
		var sourceA = new MediaSourceId(Identity.Parse("b1000000-0000-0000-0000-000000000001"));
		var sourceB = new MediaSourceId(Identity.Parse("b1000000-0000-0000-0000-000000000002"));
		var config = AudioProductionConfiguration.CreateLegacyCompatible([sourceA, sourceB]);
		var baseline = new AudioProductionEngine(config);
		var afterMetadata = new AudioProductionEngine(config);
		var sourceSamples = new float[] { -0.6f, 0.1f, 0.25f, -0.45f, 0f, 0.33f, -0.12f, 0.8f };
		var buffers = new[] {
			new AudioProductionSourceBuffer(sourceA, sourceSamples),
			new AudioProductionSourceBuffer(sourceB, sourceSamples)
		};
		var control = new float[sourceSamples.Length];
		var observed = new float[sourceSamples.Length];
		var original = baseline.ProcessBus(AudioBusId.Program, 0, 4, sourceB, buffers, control);
		var adm = AdmAudioXml.ImportXml(Fixture("audio-object.xml"), Asset());
		Assert.Equal("UNVERIFIED", adm.DecodeStatus);
		Assert.Contains("UNSUPPORTED", adm.RenderStatus);
		var withAdm = afterMetadata.ProcessBus(AudioBusId.Program, 0, 4, sourceB, buffers, observed);
		Assert.Equal(original, withAdm);
		for (var i = 0; i < control.Length; i++)
			Assert.Equal(BitConverter.SingleToInt32Bits(control[i]), BitConverter.SingleToInt32Bits(observed[i]));
		Assert.Equal(sourceSamples, buffers[0].Samples.ToArray());
	}

	private static MediaAssetDescriptor Asset(MediaAssetId? id = null, string? fingerprint = null) =>
		new(id ?? new MediaAssetId(Identity.Parse("bd000000-0000-0000-0000-000000000001")),
			MediaAssetOriginKind.LocalFile, @"C:\asset\studio.mp4", "Studio",
			MediaContainerFormat.Mp4, MediaVideoCodec.H264, MediaAudioCodec.Aac,
			VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32,
			TimeSpan.FromSeconds(10), 1024, fingerprint ?? Fingerprint,
			Stamp, Stamp, MediaAssetAvailability.Online);

	private static byte[] Fixture(string name)
	{
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
			root = root.Parent;
		return File.ReadAllBytes(Path.Combine((root ?? throw new InvalidOperationException()).FullName,
			"tests", "fixtures", "adm", "2025", name));
	}
}
