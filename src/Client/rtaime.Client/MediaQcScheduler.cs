// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Threading.Channels;
using System.Xml;
using rtaime.Media.Contracts;

namespace rtaime.Client;

// One background file worker, four queued jobs, no RuntimeHost/ControlHost
// production dependency and no catalogue database writes.
public sealed class MediaQcScheduler : IDisposable, IAsyncDisposable
{
	public const int MaximumQueued = 4;
	public const int MaximumInFlight = MaximumQueued + 1;
	public const int MaximumRetainedJobs = 32;
	private readonly object _gate = new();
	private readonly Channel<Job> _channel;
	private readonly Dictionary<Guid, Job> _jobs = new();
	private readonly CancellationTokenSource _shutdown = new();
	private readonly Task _worker;
	private readonly MediaQcReportStore _store;
	private readonly long _maximumFileBytes;
	private int _inFlight;
	private bool _disposed;

	private sealed class Job(MediaAssetDescriptor asset, ulong revision, MediaQcJobSnapshot snapshot)
	{
		public MediaAssetDescriptor Asset { get; } = asset;
		public ulong Revision { get; } = revision;
		public CancellationTokenSource Cancellation { get; } = new();
		public MediaQcJobSnapshot Snapshot { get; set; } = snapshot;
	}

	public MediaQcScheduler(string? reportDirectory = null,
		long maximumFileBytes = MediaQcFileInspector.DefaultMaximumFileBytes)
	{
		if (maximumFileBytes is < 1 or > 32L * 1024 * 1024 * 1024)
			throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
		_maximumFileBytes = maximumFileBytes;
		_store = new MediaQcReportStore(reportDirectory);
		_channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(MaximumQueued) {
			SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
		});
		_worker = Task.Run(RunAsync);
	}

	public event Action<MediaQcJobSnapshot>? Changed;

	public MediaQcJobSnapshot Begin(MediaAssetDescriptor asset, ulong catalogueRevision)
	{
		ArgumentNullException.ThrowIfNull(asset);
		if (asset.Availability != MediaAssetAvailability.Online)
			throw new InvalidOperationException("Only currently online catalogue clips may enter offline QC.");
		var snapshot = new MediaQcJobSnapshot(Guid.NewGuid(), asset.AssetId.ToString(),
			MediaQcJobState.Queued, "Queued for independent, rate-limited file inspection.",
			null, DateTimeOffset.UtcNow);
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_inFlight >= MaximumInFlight || _jobs.Values.Any(x =>
				x.Snapshot.AssetId == snapshot.AssetId &&
				x.Snapshot.State is MediaQcJobState.Queued or MediaQcJobState.Running))
				throw new InvalidOperationException("QC worker queue is full or this asset is already being inspected.");
			var job = new Job(asset, catalogueRevision, snapshot);
			if (!_channel.Writer.TryWrite(job))
				throw new InvalidOperationException("QC worker queue is at capacity.");
			_inFlight++;
			_jobs.Add(snapshot.JobId, job);
			TrimCompletedUnsafe();
		}
		Notify(snapshot);
		return snapshot;
	}

	public MediaQcJobSnapshot? CurrentForAsset(string assetId)
	{
		lock (_gate)
			return _jobs.Values.Where(x => x.Snapshot.AssetId == assetId)
				.OrderByDescending(x => x.Snapshot.UpdatedUtc)
				.Select(x => x.Snapshot).FirstOrDefault();
	}

	public MediaQcJobSnapshot? Get(Guid jobId)
	{
		lock (_gate)
			return _jobs.TryGetValue(jobId, out var job) ? job.Snapshot : null;
	}

	public bool Cancel(Guid jobId)
	{
		MediaQcJobSnapshot? update;
		lock (_gate)
		{
			if (!_jobs.TryGetValue(jobId, out var job) ||
				job.Snapshot.State is not (MediaQcJobState.Queued or MediaQcJobState.Running))
				return false;
			job.Cancellation.Cancel();
			update = job.Snapshot = job.Snapshot with {
				State = MediaQcJobState.Cancelled,
				Detail = "QC cancelled; no incomplete report was published.",
				UpdatedUtc = DateTimeOffset.UtcNow
			};
		}
		Notify(update);
		return true;
	}

	public void Reconcile(MediaAssetCatalogSnapshot catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		var changed = new List<MediaQcJobSnapshot>();
		lock (_gate)
		{
			if (_disposed) return;
			foreach (var job in _jobs.Values)
			{
				var current = catalog.Assets.FirstOrDefault(x => x.AssetId == job.Asset.AssetId);
				if (current is not null && catalog.Revision == job.Revision &&
					current.FingerprintSha256 == job.Asset.FingerprintSha256 &&
					current.LengthBytes == job.Asset.LengthBytes &&
					string.Equals(current.SourceLocation, job.Asset.SourceLocation, StringComparison.OrdinalIgnoreCase))
					continue;
				if (job.Snapshot.State is MediaQcJobState.Stale or MediaQcJobState.Cancelled) continue;
				if (job.Snapshot.State is MediaQcJobState.Queued or MediaQcJobState.Running)
					job.Cancellation.Cancel();
				var snapshot = job.Snapshot = job.Snapshot with {
					State = MediaQcJobState.Stale,
					Detail = "Catalogue identity or revision changed; old QC evidence is retained as STALE.",
					UpdatedUtc = DateTimeOffset.UtcNow
				};
				changed.Add(snapshot);
			}
		}
		foreach (var snapshot in changed) Notify(snapshot);
	}

	public async Task<MediaQcReport?> RestoreAsync(MediaAssetDescriptor asset, ulong revision,
		CancellationToken cancellationToken = default) =>
		await _store.ReadAsync(asset, revision, cancellationToken).ConfigureAwait(false);

	public async Task<MediaQcReport> ImportAsync(string xml, MediaAssetDescriptor asset,
		ulong revision, CancellationToken cancellationToken = default)
	{
		var report = MediaQcEbuXml.Import(xml);
		MediaQcReportStore.CheckBinding(report, asset, revision);
		await _store.SaveAsync(report, xml, cancellationToken).ConfigureAwait(false);
		return report;
	}

	public string Export(MediaQcReport report) => MediaQcEbuXml.Export(report);

	private async Task RunAsync()
	{
		try
		{
			await foreach (var job in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
			{
				try
				{
					if (job.Cancellation.IsCancellationRequested)
						continue;
					Set(job, MediaQcJobState.Running, "Hashing and inspecting local source outside Program.", null);
					using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _shutdown.Token);
					var report = await MediaQcFileInspector.InspectAsync(
						job.Asset, job.Revision, _maximumFileBytes, linked.Token).ConfigureAwait(false);
					linked.Token.ThrowIfCancellationRequested();
					var xml = MediaQcEbuXml.Export(report);
					await _store.SaveAsync(report, xml, linked.Token).ConfigureAwait(false);
					Set(job, MediaQcJobState.Completed,
						report.Passed ? "All implemented checks passed; unsupported checks remain unmeasured." :
							"QC report complete. Review measured failures and unexecuted capabilities.",
						report);
				}
				catch (OperationCanceledException)
				{
					if (!_shutdown.IsCancellationRequested)
						SetIfActive(job, MediaQcJobState.Cancelled,
							"QC cancelled; no partial report published.");
				}
				catch (MediaQcSourceChangedException)
				{
					SetIfActive(job, MediaQcJobState.Stale,
						"File identity changed; the QC evidence was discarded.");
				}
				catch (Exception exception) when (exception is IOException or
					UnauthorizedAccessException or ArgumentException or InvalidDataException or
					NotSupportedException or XmlException or InvalidOperationException)
				{
					SetIfActive(job, MediaQcJobState.Failed,
						$"Offline QC failed safely ({exception.GetType().Name}); no report published.");
				}
				finally
				{
					lock (_gate) _inFlight--;
				}
			}
		}
		catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
		{
			// The Operator closed; no Program/Runtime action is linked to this worker.
		}
	}

	private void Set(Job job, MediaQcJobState state, string detail, MediaQcReport? report)
	{
		MediaQcJobSnapshot next;
		lock (_gate)
		{
			if (_disposed || job.Snapshot.State is MediaQcJobState.Stale or MediaQcJobState.Cancelled)
				return;
			next = job.Snapshot = job.Snapshot with {
				State = state, Detail = detail, Report = report, UpdatedUtc = DateTimeOffset.UtcNow
			};
		}
		Notify(next);
	}

	private void SetIfActive(Job job, MediaQcJobState state, string detail)
	{
		if (job.Snapshot.State is not (MediaQcJobState.Queued or MediaQcJobState.Running))
			return;
		Set(job, state, detail, null);
	}

	private void TrimCompletedUnsafe()
	{
		if (_jobs.Count <= MaximumRetainedJobs) return;
		foreach (var old in _jobs.Values.Where(x => x.Snapshot.State is not
			(MediaQcJobState.Running or MediaQcJobState.Queued))
			.OrderBy(x => x.Snapshot.UpdatedUtc).Take(_jobs.Count - MaximumRetainedJobs).ToArray())
		{
			_jobs.Remove(old.Snapshot.JobId);
			old.Cancellation.Dispose();
		}
	}

	private void Notify(MediaQcJobSnapshot snapshot)
	{
		var handlers = Changed;
		if (handlers is null) return;
		foreach (Action<MediaQcJobSnapshot> handler in handlers.GetInvocationList())
		{
			try { handler(snapshot); }
			catch (Exception) { /* A presentation subscriber cannot abort QC. */ }
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed) throw new ObjectDisposedException(nameof(MediaQcScheduler));
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			foreach (var job in _jobs.Values)
				if (job.Snapshot.State is MediaQcJobState.Queued or MediaQcJobState.Running)
					job.Cancellation.Cancel();
		}
		_shutdown.Cancel();
		_channel.Writer.TryComplete();
	}

	public async ValueTask DisposeAsync()
	{
		Dispose();
		try { await _worker.ConfigureAwait(false); }
		finally
		{
			lock (_gate)
				foreach (var job in _jobs.Values) job.Cancellation.Dispose();
			_shutdown.Dispose();
		}
	}
}

// Reports are immutable, schema-checked, stored out of the authoritative
// SQLite catalogue and published by atomic rename only after full QC.
public sealed class MediaQcReportStore
{
	private readonly string _root;

	public MediaQcReportStore(string? root = null)
	{
		_root = root ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"rtaime", "qc");
	}

	public static void CheckBinding(MediaQcReport report, MediaAssetDescriptor asset, ulong revision)
	{
		ArgumentNullException.ThrowIfNull(asset);
		report.Validate();
		if (report.CatalogueRevision != revision || report.AssetId != asset.AssetId.ToString() ||
			!string.Equals(report.FingerprintSha256, asset.FingerprintSha256, StringComparison.OrdinalIgnoreCase) ||
			report.SourceLengthBytes != asset.LengthBytes)
			throw new InvalidDataException("QC report asset/fingerprint/catalogue revision is stale.");
	}

	public async Task SaveAsync(MediaQcReport report, string xml, CancellationToken cancellationToken)
	{
		report.Validate();
		MediaQcEbuXml.ValidateSchema(xml);
		var path = ReportPath(report.AssetId);
		Directory.CreateDirectory(_root);
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporary, xml, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}

	public async Task<MediaQcReport?> ReadAsync(MediaAssetDescriptor asset, ulong revision,
		CancellationToken cancellationToken)
	{
		var path = ReportPath(asset.AssetId.ToString());
		if (!File.Exists(path)) return null;
		var info = new FileInfo(path);
		if (info.Length is < 1 or > MediaQcEbuXml.MaximumDocumentCharacters * 4L)
			throw new InvalidDataException("Stored QC XML is invalid/oversized.");
		var report = MediaQcEbuXml.Import(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
		CheckBinding(report, asset, revision);
		// Restored evidence remains historical and never establishes live
		// decoder readiness; callers must display its captured timestamp.
		return report;
	}

	private string ReportPath(string assetId)
	{
		if (!Guid.TryParse(assetId, out var value))
			throw new InvalidDataException("QC asset ID is invalid.");
		return Path.Combine(_root, value.ToString("D") + ".xml");
	}
}
