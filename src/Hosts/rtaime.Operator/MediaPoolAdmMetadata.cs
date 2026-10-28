// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

// Local, non-authoritative ADM sidecar; no Runtime or Control mutation.
// The Inspector only projects metadata for an actually selected catalogue Clip.
public sealed partial class MediaPoolInspectorViewModel
{
	private readonly AdmAudioSidecarStore _admStore = new();
	private AdmAudioInspection? _admInspection;
	private string _admStatus = "No ADM metadata imported for the selected catalogue clip.";
	private bool _admBusy;
	private bool _admDisposed;
	private int _admSelectionGeneration;

	public ObservableCollection<AdmTrackMetadataRow> AdmTrackRows { get; } = [];
	public AdmAudioInspection? AdmInspection => _admInspection;
	public string AdmStatus
	{
		get => _admStatus;
		private set => Set(ref _admStatus, value);
	}
	public string AdmLayout => _admInspection?.LayoutLabel ?? "UNVERIFIED";
	public string AdmCapability => _admInspection is null
		? "NOT IMPORTED · STEREO MIX ENGINE UNCHANGED"
		: _admInspection.InterchangeStatus + " · " + _admInspection.RenderStatus;
	public bool HasAdmMetadata => _admInspection is not null;
	public bool CanImportAdm => !_admDisposed && !_admBusy &&
		SelectedItem is { Kind: MediaPoolItemKind.Clip, ReferenceId: { } id } &&
		_catalogSnapshot.Assets.Any(x => x.AssetId.ToString() == id &&
			x.Availability == MediaAssetAvailability.Online);
	public bool CanExportAdm => !_admDisposed && !_admBusy &&
		_admInspection is { } current &&
		TryGetAdmAsset(out var asset) &&
		current.Interchange.AssetId == asset.AssetId.ToString() &&
		current.Interchange.FingerprintSha256 == asset.FingerprintSha256;

	private bool TryGetAdmAsset(out MediaAssetDescriptor asset)
	{
		asset = null!;
		if (SelectedItem is not { Kind: MediaPoolItemKind.Clip, ReferenceId: { } id })
			return false;
		asset = _catalogSnapshot.Assets.FirstOrDefault(x => x.AssetId.ToString() == id)!;
		return asset is not null;
	}

	private void ReconcileAdm(MediaAssetCatalogSnapshot catalog)
	{
		// Re-evaluate under the new catalogue fingerprint; no old selected
		// metadata is retained as if it were current.
		OnAdmSelectionChanged();
	}

	private void OnAdmSelectionChanged()
	{
		if (_admDisposed) return;
		var generation = ++_admSelectionGeneration;
		_admInspection = null;
		AdmTrackRows.Clear();
		AdmStatus = "No source-bound ADM metadata imported for this selection.";
		if (TryGetAdmAsset(out var asset))
		{
			AdmStatus = "ADM metadata: NOT EXECUTED / NOT IMPORTED";
			_ = RestoreAdmAsync(asset, generation);
		}
		NotifyAdm();
	}

	private async Task RestoreAdmAsync(MediaAssetDescriptor selected, int generation)
	{
		try
		{
			var snapshot = await _admStore.LoadAsync(selected);
			if (snapshot is null || _admDisposed || _admBusy ||
				generation != _admSelectionGeneration)
				return;
			if (!TryGetAdmAsset(out var current) ||
				current.AssetId != selected.AssetId ||
				current.FingerprintSha256 != selected.FingerprintSha256)
				return;
			PresentAdm(snapshot, "RETAINED ADM · METADATA ONLY · NOT SOURCE-DECODER VERIFIED");
		}
		catch (Exception error) when (error is IOException or
			UnauthorizedAccessException or ArgumentException or
			InvalidOperationException or System.Xml.XmlException or
			System.Text.Json.JsonException or NotSupportedException or FormatException)
		{
			if (!_admDisposed && generation == _admSelectionGeneration)
				AdmStatus = "ADM sidecar cannot be validated for this file: " +
					error.GetType().Name + " · no metadata promoted.";
		}
	}

	private void PresentAdm(AdmAudioInspection inspection, string status)
	{
		_admInspection = inspection;
		AdmTrackRows.Clear();
		foreach (var row in inspection.Tracks) AdmTrackRows.Add(row);
		AdmStatus = status;
		NotifyAdm();
		BuildInspector();
	}

	public async Task ImportSelectedAdmAsync(string path, CancellationToken cancellationToken = default)
	{
		if (!CanImportAdm || !TryGetAdmAsset(out var selected))
			throw new InvalidOperationException("Select one online catalogued clip before importing ADM metadata.");
		var source = new FileInfo(path);
		var json = string.Equals(source.Extension, ".json", StringComparison.OrdinalIgnoreCase);
		var maximum = json ? AdmAudioInterchange.MaximumJsonBytes : AdmAudioInterchange.MaximumXmlBytes;
		if (!source.Exists || source.Length is < 16 || source.Length > maximum)
			throw new InvalidDataException("ADM import file is missing or exceeds the admitted byte budget.");
		var revision = _catalogSnapshot.Revision;
		var generation = _admSelectionGeneration;
		_admBusy = true;
		NotifyAdm();
		try
		{
			var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			var inspection = await Task.Run(() => json
				? AdmAudioXml.ImportSidecarJson(new UTF8Encoding(false, true).GetString(bytes), selected)
				: AdmAudioXml.ImportXml(bytes, selected), cancellationToken);
			if (_admDisposed || generation != _admSelectionGeneration ||
				_catalogSnapshot.Revision != revision ||
				!TryGetAdmAsset(out var current) ||
				current.AssetId != selected.AssetId ||
				current.FingerprintSha256 != selected.FingerprintSha256)
				throw new InvalidOperationException("Selected catalogue identity changed during ADM import.");
			await _admStore.SaveAsync(inspection, selected, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			if (_admDisposed || generation != _admSelectionGeneration ||
				!TryGetAdmAsset(out current) ||
				current.FingerprintSha256 != selected.FingerprintSha256)
				throw new InvalidOperationException("ADM import completed after selection changed; reselect source.");
			PresentAdm(inspection,
				"ADM IMPORTED · LOSSLESS SOURCE RETAINED · IMMERSIVE RENDER/PUBLISH UNSUPPORTED");
		}
		finally
		{
			_admBusy = false;
			NotifyAdm();
		}
	}

	public async Task ExportSelectedAdmAsync(string path, CancellationToken cancellationToken = default)
	{
		if (!CanExportAdm || _admInspection is not { } inspection ||
			!TryGetAdmAsset(out var asset))
			throw new InvalidOperationException("No source-matched ADM metadata is available for export.");
		AdmAudioSidecarStore.CheckBinding(inspection, asset);
		var isJson = string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
		var bytes = isJson
			? new UTF8Encoding(false, true).GetBytes(AdmAudioXml.ExportSidecarJson(inspection))
			: AdmAudioXml.ExportXml(inspection);
		var target = Path.GetFullPath(path);
		var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporary, target, overwrite: true);
		}
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}

	public void ReportAdmOperationFailure(string operation, string failureType)
	{
		if (_admDisposed) return;
		AdmStatus = $"ADM {operation} rejected ({failureType}); prior metadata unchanged.";
	}

	private void AddAdmInspectorRows()
	{
		if (_admInspection is not { } inspection)
		{
			Add("clip.adm.status", "ADM", "NOT IMPORTED · IMMERSIVE OUTPUT UNSUPPORTED", "CAPABILITY");
			return;
		}
		Add("clip.adm.profile", "ADM reference", inspection.Interchange.Profile, "METADATA");
		Add("clip.adm.source.version", "ADM model", inspection.Interchange.AdmVersion, "METADATA");
		Add("clip.adm.layout", "Audio layout", inspection.LayoutLabel, "METADATA");
		Add("clip.adm.r123", "R 123 allocation",
			inspection.Interchange.R123Allocation ?? "UNASSIGNED · NO PHYSICAL TRACK CLAIM", "METADATA");
		Add("clip.adm.decode", "Audio decoder", inspection.DecodeStatus, "CAPABILITY");
		Add("clip.adm.render", "Immersive render", inspection.RenderStatus, "CAPABILITY");
		Add("clip.adm.publish", "Immersive output", inspection.PublishStatus, "CAPABILITY");
		foreach (var row in inspection.Tracks)
		{
			var prefix = "clip.adm.uid." + row.AudioTrackUid;
			Add(prefix, row.DisplayTrack, row.AudioTrackUid + " · " +
				(row.ChannelRole ?? "ROLE UNKNOWN") + " · " +
				(row.Language ?? "LANGUAGE UNVERIFIED") + " · " + row.SourceKind,
				"METADATA");
			if (row.Start is not null)
				Add(prefix + ".time", "Start → End", row.Start + " → " +
					(row.End ?? "UNVERIFIED"), "METADATA");
		}
	}

	private void NotifyAdm()
	{
		OnPropertyChanged(nameof(AdmInspection));
		OnPropertyChanged(nameof(AdmLayout));
		OnPropertyChanged(nameof(AdmCapability));
		OnPropertyChanged(nameof(HasAdmMetadata));
		OnPropertyChanged(nameof(CanImportAdm));
		OnPropertyChanged(nameof(CanExportAdm));
	}

	private void DisposeAdm()
	{
		_admDisposed = true;
		_admSelectionGeneration++;
		_admInspection = null;
		AdmTrackRows.Clear();
	}
}
