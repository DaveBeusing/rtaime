// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

// QC is a strictly local, read-only Media Library action. It does not
// produce a Preview, Program, preparation ticket or catalogue mutation.
public sealed partial class MediaPoolInspectorViewModel
{
	private MediaQcScheduler? _qcWorker;
	private SynchronizationContext? _qcContext;
	private MediaQcReport? _qcReport;
	private Guid? _qcJobId;
	private string _qcStatus = "Select a catalogued local clip to run offline QC.";
	private bool _qcDisposed;
	public ObservableCollection<MediaQcCheckResult> QcResults { get; } = [];
	public ICommand StartQcCommand { get; private set; } = null!;
	public ICommand CancelQcCommand { get; private set; } = null!;
	public string QcStatus { get => _qcStatus; private set => Set(ref _qcStatus, value); }
	public MediaQcReport? CurrentQcReport => _qcReport;
	public string QcReportIdentity => _qcReport is null ? "NO QC REPORT" :
		$"EBU QC {_qcReport.ModelRelease} · {_qcReport.ReportId:D} · CAPTURED {_qcReport.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC";
	public bool HasQcResults => QcResults.Count > 0;
	public bool CanStartQc => !_qcDisposed && _qcWorker is not null &&
		SelectedItem is { Kind: MediaPoolItemKind.Clip, ReferenceId: { } id } &&
		_catalogSnapshot.Assets.Any(asset => asset.AssetId.ToString() == id &&
			asset.Availability == MediaAssetAvailability.Online) &&
		_qcWorker.CurrentForAsset(id)?.State is not (MediaQcJobState.Queued or MediaQcJobState.Running);
	public bool CanCancelQc => !_qcDisposed && _qcWorker is not null &&
		_qcJobId is { } job && _qcWorker.Get(job)?.State is MediaQcJobState.Queued or MediaQcJobState.Running;

	private void InitializeQc()
	{
		_qcContext = SynchronizationContext.Current;
		_qcWorker = new MediaQcScheduler();
		_qcWorker.Changed += QcWorkerChanged;
		StartQcCommand = new AsyncRelayCommand(StartSelectedQcAsync, () => CanStartQc);
		CancelQcCommand = new AsyncRelayCommand(CancelSelectedQcAsync, () => CanCancelQc);
	}

	private Task StartSelectedQcAsync()
	{
		if (!TryGetQcAsset(out var asset) || _qcWorker is null || !CanStartQc)
			return Task.CompletedTask;
		try
		{
			var snapshot = _qcWorker.Begin(asset, _catalogSnapshot.Revision);
			_qcJobId = snapshot.JobId;
			_qcReport = null;
			QcResults.Clear();
			QcStatus = snapshot.Detail;
			NotifyQcState();
		}
		catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
		{
			QcStatus = "Offline QC was not started: " + exception.Message;
		}
		return Task.CompletedTask;
	}

	private Task CancelSelectedQcAsync()
	{
		if (_qcWorker is not null && _qcJobId is { } id)
			_qcWorker.Cancel(id);
		return Task.CompletedTask;
	}

	private bool TryGetQcAsset(out MediaAssetDescriptor asset)
	{
		asset = null!;
		var id = SelectedItem?.ReferenceId;
		if (SelectedItem?.Kind != MediaPoolItemKind.Clip || id is null)
			return false;
		asset = _catalogSnapshot.Assets.FirstOrDefault(x => x.AssetId.ToString() == id)!;
		return asset is not null;
	}

	private void QcWorkerChanged(MediaQcJobSnapshot snapshot)
	{
		if (_qcContext is { } context)
			context.Post(_ => ApplyQcSnapshot(snapshot), null);
		else
			ApplyQcSnapshot(snapshot);
	}

	private void ApplyQcSnapshot(MediaQcJobSnapshot snapshot)
	{
		if (_qcDisposed || !string.Equals(SelectedItem?.ReferenceId, snapshot.AssetId, StringComparison.Ordinal))
			return;
		_qcJobId = snapshot.JobId;
		QcStatus = $"{snapshot.State.ToString().ToUpperInvariant()} · {snapshot.Detail}";
		if (snapshot.Report is { } report &&
			_catalogSnapshot.Revision == report.CatalogueRevision &&
			TryGetQcAsset(out var asset) &&
			asset.FingerprintSha256 == report.FingerprintSha256)
			PresentQcReport(report, retained: false);
		NotifyQcState();
	}

	private void PresentQcReport(MediaQcReport report, bool retained)
	{
		_qcReport = report;
		QcResults.Clear();
		foreach (var result in report.Results)
			QcResults.Add(result);
		QcStatus = retained
			? "RETAINED · This is a historical, fingerprint-bound QC report; re-run before relying on current media."
			: "COMPLETE · Source SHA-256 verified at capture; unresolved/not-executed checks are not PASS.";
		NotifyQcState();
	}

	private async Task RestoreSelectedQcAsync(string assetId, ulong revision)
	{
		if (_qcWorker is null || !TryGetQcAsset(out var asset) ||
			asset.AssetId.ToString() != assetId)
			return;
		try
		{
			var report = await _qcWorker.RestoreAsync(asset, revision).ConfigureAwait(false);
			if (report is null)
				return;
			void Apply()
			{
				if (!_qcDisposed && _catalogSnapshot.Revision == revision &&
					SelectedItem?.ReferenceId == assetId &&
					_qcWorker?.CurrentForAsset(assetId)?.State is not
						(MediaQcJobState.Queued or MediaQcJobState.Running))
					PresentQcReport(report, retained: true);
			}
			if (_qcContext is { } context) context.Post(_ => Apply(), null);
			else Apply();
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or
			InvalidDataException or InvalidOperationException or System.Xml.XmlException)
		{
			void Warn()
			{
				if (!_qcDisposed && SelectedItem?.ReferenceId == assetId)
					QcStatus = "Stored QC evidence is unavailable, invalid or stale; start a new scan.";
			}
			if (_qcContext is { } context) context.Post(_ => Warn(), null);
			else Warn();
		}
	}

	private void OnQcSelectionChanged()
	{
		if (_qcDisposed) return;
		_qcReport = null;
		QcResults.Clear();
		_qcJobId = null;
		if (!TryGetQcAsset(out var asset))
		{
			QcStatus = "Select a catalogued local clip to run offline QC.";
		}
		else
		{
			var current = _qcWorker?.CurrentForAsset(asset.AssetId.ToString());
			_qcJobId = current?.JobId;
			if (current is { Report: { } report })
				PresentQcReport(report, retained: false);
			else
			{
				QcStatus = current is null
					? "NOT EXECUTED · No QC evidence for this selection."
					: current.State.ToString().ToUpperInvariant() + " · " + current.Detail;
				_ = RestoreSelectedQcAsync(asset.AssetId.ToString(), _catalogSnapshot.Revision);
			}
		}
		NotifyQcState();
	}

	private void ReconcileQc(MediaAssetCatalogSnapshot snapshot)
	{
		_qcWorker?.Reconcile(snapshot);
		OnQcSelectionChanged();
	}

	public async Task ExportSelectedQcAsync(string path, CancellationToken cancellationToken = default)
	{
		if (_qcReport is not { } report || !TryGetQcAsset(out var asset) || _qcWorker is null)
			throw new InvalidOperationException("No validated QC report is selected for export.");
		MediaQcReportStore.CheckBinding(report, asset, _catalogSnapshot.Revision);
		var xml = _qcWorker.Export(report);
		var target = Path.GetFullPath(path);
		var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temp, xml, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temp, target, overwrite: true);
		}
		finally
		{
			if (File.Exists(temp)) File.Delete(temp);
		}
	}

	public async Task ImportSelectedQcAsync(string path, CancellationToken cancellationToken = default)
	{
		if (!TryGetQcAsset(out var asset) || _qcWorker is null)
			throw new InvalidOperationException("Select a catalogued clip before importing a QC result.");
		var file = new FileInfo(path);
		if (!file.Exists || file.Length is < 1 or > MediaQcEbuXml.MaximumDocumentCharacters * 4L)
			throw new InvalidDataException("Imported QC XML exceeds the admitted size.");
		var xml = await File.ReadAllTextAsync(path, cancellationToken);
		var revision = _catalogSnapshot.Revision;
		var report = await _qcWorker.ImportAsync(xml, asset, revision, cancellationToken);
		if (!_qcDisposed && SelectedItem?.ReferenceId == asset.AssetId.ToString() &&
			_catalogSnapshot.Revision == revision)
			PresentQcReport(report, retained: true);
	}

	private void NotifyQcState()
	{
		OnPropertyChanged(nameof(CurrentQcReport));
		OnPropertyChanged(nameof(QcReportIdentity));
		OnPropertyChanged(nameof(HasQcResults));
		OnPropertyChanged(nameof(CanStartQc));
		OnPropertyChanged(nameof(CanCancelQc));
		(StartQcCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(CancelQcCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private void DisposeQc()
	{
		if (_qcDisposed) return;
		_qcDisposed = true;
		if (_qcWorker is not null)
		{
			_qcWorker.Changed -= QcWorkerChanged;
			_qcWorker.Dispose();
		}
		_qcWorker = null;
		_qcJobId = null;
		QcResults.Clear();
	}
}
