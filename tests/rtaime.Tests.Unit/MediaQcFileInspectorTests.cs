// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class MediaQcFileInspectorTests
{
	[Fact]
	public async Task Verified_mp4_reports_only_actual_structural_check_results()
	{
		var root = TempRoot();
		try
		{
			var asset = CreateAsset(root, Movie(), MediaAudioCodec.None);
			var result = await MediaQcFileInspector.InspectAsync(asset, 11);
			Assert.Equal(11UL, result.CatalogueRevision);
			Assert.Equal(asset.FingerprintSha256, result.FingerprintSha256);
			Assert.Equal(MediaQcReport.CurrentModelRelease, result.ModelRelease);
			Assert.Equal(5, result.Results.Count(x => x.State == MediaQcCheckState.Pass));
			Assert.Equal(2, result.Results.Count(x => x.State == MediaQcCheckState.NotExecuted));
			Assert.False(result.FullyEvaluated);
			Assert.NotNull(MediaQcEbuXml.Import(MediaQcEbuXml.Export(result)));
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Truncated_movie_and_missing_audio_fail_only_checks_with_real_evidence()
	{
		var root = TempRoot();
		try
		{
			var intact = Movie();
			var incomplete = intact[..^5];
			var corrupt = CreateAsset(root, incomplete, MediaAudioCodec.Aac);
			var report = await MediaQcFileInspector.InspectAsync(corrupt, 1);
			Assert.Equal(MediaQcCheckState.Failed, report.Results[1].State);
			Assert.NotEqual(MediaQcCheckState.Pass, report.Results[0].State);
			var silent = CreateAsset(root, Movie(), MediaAudioCodec.Aac);
			var noAudio = await MediaQcFileInspector.InspectAsync(silent, 1);
			Assert.Equal(MediaQcCheckState.Failed, noAudio.Results[2].State);
			Assert.Equal(MediaQcCheckState.Pass, noAudio.Results[3].State);
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Invalid_decode_delta_fails_timestamp_check_without_claiming_PTS_qualification()
	{
		var root = TempRoot();
		try
		{
			var report = await MediaQcFileInspector.InspectAsync(CreateAsset(root, Movie(0),
				MediaAudioCodec.None), 4);
			Assert.Equal(MediaQcCheckState.Failed, report.Results[3].State);
			Assert.Contains("presentation timestamps", report.Results[3].Detail, StringComparison.OrdinalIgnoreCase);
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Audio_sample_tables_cannot_qualify_missing_video_timing_or_video_metadata()
	{
		var root = TempRoot();
		try
		{
			var handlerVideo = new byte[12];
			Encoding.ASCII.GetBytes("vide", handlerVideo.AsSpan(8));
			var videoTrack = Box("trak", Box("mdia", Box("hdlr", handlerVideo)
				.Concat(Box("minf", Box("stbl", [])).ToArray())));
			var handlerAudio = new byte[12];
			Encoding.ASCII.GetBytes("soun", handlerAudio.AsSpan(8));
			var header = new byte[8];
			BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
			var sample = new byte[8];
			BinaryPrimitives.WriteUInt32BigEndian(sample.AsSpan(0, 4), 10);
			BinaryPrimitives.WriteUInt32BigEndian(sample.AsSpan(4, 4), 960);
			var audioTrack = Box("trak", Box("mdia", Box("hdlr", handlerAudio)
				.Concat(Box("minf", Box("stbl", Box("stts", header.Concat(sample).ToArray())))).ToArray()));
			var file = Box("ftyp", Encoding.ASCII.GetBytes("isom0000"))
				.Concat(Box("moov", Box("mvhd", new byte[8])
					.Concat(videoTrack).Concat(audioTrack).ToArray()))
				.Concat(Box("mdat", new byte[16])).ToArray();
			var asset = CreateAsset(root, file, MediaAudioCodec.Aac);
			var report = await MediaQcFileInspector.InspectAsync(asset, 8);
			Assert.Equal(MediaQcCheckState.Unsupported, report.Results[3].State);
			Assert.Equal(MediaQcCheckState.Pass, report.Results[4].State);
		}
		finally { Directory.Delete(root, true); }
	}

		[Fact]
	public async Task Source_mutation_invalidates_the_hash_and_prevents_a_published_report()
	{
		var root = TempRoot();
		try
		{
			var asset = CreateAsset(root, Movie(), MediaAudioCodec.None);
			var bytes = await File.ReadAllBytesAsync(asset.SourceLocation);
			bytes[^1] ^= 1;
			await File.WriteAllBytesAsync(asset.SourceLocation, bytes);
			await Assert.ThrowsAsync<MediaQcSourceChangedException>(async () =>
				await MediaQcFileInspector.InspectAsync(asset, 9));
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Stored_QC_is_atomic_and_strictly_bound_to_asset_and_catalogue_revision()
	{
		var root = TempRoot();
		try
		{
			var asset = CreateAsset(root, Movie(), MediaAudioCodec.None);
			var report = await MediaQcFileInspector.InspectAsync(asset, 7);
			var store = new MediaQcReportStore(Path.Combine(root, "reports"));
			var xml = MediaQcEbuXml.Export(report);
			await store.SaveAsync(report, xml, CancellationToken.None);
			var restored = await store.ReadAsync(asset, 7, CancellationToken.None);
			Assert.Equal(report.ReportId, restored!.ReportId);
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
				await store.ReadAsync(asset, 8, CancellationToken.None));
			using var cancelled = new CancellationTokenSource();
			cancelled.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
				await store.SaveAsync(report with { ReportId = Guid.NewGuid() }, xml, cancelled.Token));
			Assert.Equal(report.ReportId,
				(await store.ReadAsync(asset, 7, CancellationToken.None))!.ReportId);
			Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "reports"), "*.tmp"));
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Worker_marks_catalogue_mutation_stale_without_committing_results()
	{
		var root = TempRoot();
		var worker = new MediaQcScheduler(Path.Combine(root, "reports"));
		try
		{
			var bytes = Movie();
			Array.Resize(ref bytes, 4 * 1024 * 1024);
			var asset = CreateAsset(root, bytes, MediaAudioCodec.None);
			var job = worker.Begin(asset, 3);
			worker.Reconcile(new MediaAssetCatalogSnapshot(MediaContractVersion.Current, 4,
				[asset]));
			var status = worker.Get(job.JobId);
			Assert.NotNull(status);
			Assert.Equal(MediaQcJobState.Stale, status.State);
			Assert.Null(status.Report);
			Assert.False(worker.Cancel(job.JobId));
		}
		finally { await worker.DisposeAsync(); Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Unknown_container_never_reports_structural_truncation_as_PASS()
	{
		var root = TempRoot();
		try
		{
			var bytes = new byte[16];
			BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 16);
			var report = await MediaQcFileInspector.InspectAsync(
				CreateAsset(root, bytes, MediaAudioCodec.None), 5);
			Assert.Equal(MediaQcCheckState.Unsupported, report.Results[0].State);
			Assert.Equal(MediaQcCheckState.Unsupported, report.Results[1].State);
			Assert.Null(report.Results[1].Confidence);
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public async Task File_size_quota_rejects_before_any_unverified_QC_report_exists()
	{
		var root = TempRoot();
		try
		{
			var asset = CreateAsset(root, Movie(), MediaAudioCodec.None);
			await Assert.ThrowsAsync<NotSupportedException>(async () =>
				await MediaQcFileInspector.InspectAsync(asset, 5, maximumFileBytes: 8));
		}
		finally { Directory.Delete(root, true); }
	}

		[Fact]
	public async Task Worker_enforces_queue_quota_and_cancellation_without_production_actions()
	{
		var root = TempRoot();
		var worker = new MediaQcScheduler(Path.Combine(root, "reports"));
		try
		{
			var bytes = Movie();
			Array.Resize(ref bytes, 8 * 1024 * 1024);
			var first = CreateAsset(root, bytes, MediaAudioCodec.None);
			var jobs = new List<MediaQcJobSnapshot>();
			for (var index = 0; index < MediaQcScheduler.MaximumInFlight; index++)
				jobs.Add(worker.Begin(CloneIdentity(first), 3));
			Assert.Throws<InvalidOperationException>(() => worker.Begin(CloneIdentity(first), 3));
			Assert.True(worker.Cancel(jobs[0].JobId));
			Assert.Equal(MediaQcJobState.Cancelled, worker.Get(jobs[0].JobId)!.State);
			foreach (var item in jobs.Skip(1)) worker.Cancel(item.JobId);
		}
		finally
		{
			await worker.DisposeAsync();
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public async Task Registered_offline_adapter_can_report_only_its_exact_versioned_check()
	{
		var root = TempRoot();
		try
		{
			var asset = CreateAsset(root, Movie(), MediaAudioCodec.None);
			var original = await MediaQcFileInspector.InspectAsync(asset, 10);
			Assert.Equal(MediaQcCheckState.NotExecuted, original.Results[5].State);
			var adapter = new SimulatedOfflineAdapter(MediaQcProfile.OfflineFileV1.Checks[5]);
			var report = await MediaQcFileInspector.InspectAsync(asset, 10,
				adapters: [adapter]);
			Assert.Equal(MediaQcCheckState.Pass, report.Results[5].State);
			Assert.Equal(MediaQcCheckState.NotExecuted, report.Results[6].State);
			Assert.All(report.Results, result => result.Validate());
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Mismatched_or_forged_adapters_are_rejected_without_fabricating_evidence()
	{
		var definition = MediaQcProfile.OfflineFileV1.Checks[5];
		var fake = new SimulatedOfflineAdapter(definition with { Version = "1.1" });
		Assert.Throws<ArgumentException>(() => MediaQcAdapterRegistry.Admit([fake]));
		Assert.Throws<ArgumentException>(() =>
			MediaQcAdapterRegistry.Admit([
				new SimulatedOfflineAdapter(definition),
				new SimulatedOfflineAdapter(definition)
			]));
		var root = TempRoot();
		try
		{
			var asset = CreateAsset(root, Movie(), MediaAudioCodec.None);
			var corrupt = new SimulatedOfflineAdapter(definition, forgeSource: true);
			await Assert.ThrowsAsync<InvalidDataException>(async () =>
				await MediaQcFileInspector.InspectAsync(asset, 3, adapters: [corrupt]));
		}
		finally { Directory.Delete(root, true); }
	}

	private sealed class SimulatedOfflineAdapter(MediaQcCheckDefinition definition, bool forgeSource = false)
		: IMediaQcCheckAdapter
	{
		public MediaQcCheckDefinition Definition { get; } = definition;
		public ValueTask<MediaQcCheckResult> InspectAsync(
			MediaQcCheckContext context, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var source = forgeSource ? "offline-file:sha256:" + new string('b', 64) :
				"offline-file:sha256:" + context.FingerprintSha256.ToLowerInvariant();
			// Synthetic results are confined to the unit test. Production has no
			// registered loudness or R 103 adapter until independently qualified.
			return ValueTask.FromResult(new MediaQcCheckResult(
				Definition, MediaQcCheckState.Pass,
				"Simulated offline adapter evidence for contract qualification.",
				0, 1, source, "test-only-1.0"));
		}
	}

		private static MediaAssetDescriptor CloneIdentity(MediaAssetDescriptor original) =>
		new(MediaAssetId.New(), original.Origin, original.SourceLocation, original.DisplayName,
			original.Container, original.VideoCodec, original.AudioCodec, original.VideoFormat,
			original.AudioFormat, original.Duration, original.LengthBytes, original.FingerprintSha256,
			original.ImportedAt, original.UpdatedAt, original.Availability);

	private static MediaAssetDescriptor CreateAsset(string root, byte[] data, MediaAudioCodec audio)
	{
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mp4");
		File.WriteAllBytes(path, data);
		var stamp = new UtcTimestamp(DateTimeOffset.UtcNow);
		return new MediaAssetDescriptor(MediaAssetId.New(), MediaAssetOriginKind.LocalFile,
			path, Path.GetFileName(path), MediaContainerFormat.Mp4, MediaVideoCodec.H264, audio,
			VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32,
			TimeSpan.FromSeconds(2), data.Length,
			Convert.ToHexString(SHA256.HashData(data)), stamp, stamp, MediaAssetAvailability.Online);
	}

	private static string TempRoot() => Path.Combine(Path.GetTempPath(), "rtaime-qc-test-" + Guid.NewGuid().ToString("N"));

	private static byte[] Movie(uint delta = 1000)
	{
		var header = new byte[8];
		BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
		var entry = new byte[8];
		BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(0, 4), 120);
		BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4, 4), delta);
		var stts = Box("stts", header.Concat(entry).ToArray());
		var handler = new byte[12];
		Encoding.ASCII.GetBytes("vide", handler.AsSpan(8));
		var mdia = Box("mdia", Box("hdlr", handler)
			.Concat(Box("minf", Box("stbl", stts))).ToArray());
		var moov = Box("moov", Box("mvhd", new byte[8])
			.Concat(Box("trak", mdia)).ToArray());
		return Box("ftyp", Encoding.ASCII.GetBytes("isom0000"))
			.Concat(moov).Concat(Box("mdat", new byte[16])).ToArray();
	}

	private static byte[] Box(string kind, byte[] contents)
	{
		var bytes = new byte[8 + contents.Length];
		BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), (uint)bytes.Length);
		Encoding.ASCII.GetBytes(kind, bytes.AsSpan(4, 4));
		contents.CopyTo(bytes, 8);
		return bytes;
	}
}
