// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using rtaime.Media.Contracts;

namespace rtaime.Client;

/// <summary>
/// Local, non-authoritative metadata sidecar. Its file is independent of
/// ControlHost catalogue persistence, prepared Program state and audio PCM.
/// A matching AssetId + SHA-256 is required on every read/import.
/// </summary>
public sealed class AdmAudioSidecarStore
{
	private readonly string _directory;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private static readonly UTF8Encoding Utf8 = new(false, true);

	public AdmAudioSidecarStore(string? directory = null)
	{
		_directory = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"rtaime", "audio-adm");
	}

	public async Task<AdmAudioInspection?> LoadAsync(MediaAssetDescriptor asset,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(asset);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var path = PathFor(asset);
			if (!File.Exists(path)) return null;
			var info = new FileInfo(path);
			if (info.Length is < 1 or > AdmAudioInterchange.MaximumJsonBytes)
				throw new InvalidDataException("ADM sidecar length is outside its quota.");
			var json = await File.ReadAllTextAsync(path, Utf8, cancellationToken).ConfigureAwait(false);
			return AdmAudioXml.ImportSidecarJson(json, asset);
		}
		finally { _gate.Release(); }
	}

	public async Task SaveAsync(AdmAudioInspection inspection, MediaAssetDescriptor asset,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(inspection);
		ArgumentNullException.ThrowIfNull(asset);
		inspection.Interchange.Validate(asset.AssetId.ToString(), asset.FingerprintSha256);
		var json = AdmAudioXml.ExportSidecarJson(inspection);
		// Re-parse the exact bytes that will be persisted; source bindings and
		// ADM references cannot be widened by a hand-authored sidecar.
		_ = AdmAudioXml.ImportSidecarJson(json, asset);
		cancellationToken.ThrowIfCancellationRequested();
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Directory.CreateDirectory(_directory);
			var target = PathFor(asset);
			var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				await File.WriteAllTextAsync(temporary, json, Utf8, cancellationToken).ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				File.Move(temporary, target, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporary)) File.Delete(temporary);
			}
		}
		finally { _gate.Release(); }
	}

	public static void CheckBinding(AdmAudioInspection inspection, MediaAssetDescriptor asset)
	{
		ArgumentNullException.ThrowIfNull(inspection);
		ArgumentNullException.ThrowIfNull(asset);
		inspection.Interchange.Validate(asset.AssetId.ToString(), asset.FingerprintSha256);
	}

	private string PathFor(MediaAssetDescriptor asset)
	{
		var id = asset.AssetId.Value;
		return Path.Combine(_directory, id.ToString() + ".json");
	}
}
