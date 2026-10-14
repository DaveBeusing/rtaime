// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;

namespace rtaime.Operator;

public enum MediaDurationFilter { Any, UnderOneMinute, OneToTenMinutes, TenMinutesOrLonger, Unknown }
public enum MediaReadinessFilter { Any, Ready, NotReady }

public sealed record MediaSmartBin(
	string Name, string Search, string Category, string Filter,
	MediaDurationFilter Duration, MediaReadinessFilter Readiness)
{
	public const int MaximumBins = 16;
	public bool IsValid =>
		!string.IsNullOrWhiteSpace(Name) && Name.Length <= 128 &&
		Search is { Length: <= 256 } &&
		Category is "All" or "Sources" or "Clips" or "Audio" or "Graphics" or "Compositions" &&
		Filter is "All" or "Video" or "Audio" or "Graphics" or "Online" or "Offline" &&
		Enum.IsDefined(Duration) && Enum.IsDefined(Readiness);
}

public static class MediaSmartBinPolicy
{
	public static bool Matches(MediaPoolItemViewModel item, MediaSmartBin bin)
	{
		if (!bin.IsValid) return false;
		if (bin.Category != "All" && item.Category != bin.Category) return false;
		if (!(bin.Filter switch
		{
			"Video" => item.Kind is MediaPoolItemKind.Source or MediaPoolItemKind.Clip,
			"Audio" => item.Kind == MediaPoolItemKind.Audio,
			"Graphics" => item.Kind == MediaPoolItemKind.Graphics,
			"Online" => item.IsOnline,
			"Offline" => !item.IsOnline,
			_ => true
		})) return false;
		if (bin.Readiness == MediaReadinessFilter.Ready && !item.IsReady ||
			bin.Readiness == MediaReadinessFilter.NotReady && item.IsReady) return false;
		var duration = item.KnownDuration;
		if (!(bin.Duration switch
		{
			MediaDurationFilter.UnderOneMinute => duration is { } d && d > TimeSpan.Zero && d < TimeSpan.FromMinutes(1),
			MediaDurationFilter.OneToTenMinutes => duration >= TimeSpan.FromMinutes(1) && duration < TimeSpan.FromMinutes(10),
			MediaDurationFilter.TenMinutesOrLonger => duration >= TimeSpan.FromMinutes(10),
			MediaDurationFilter.Unknown => duration is null,
			_ => true
		})) return false;
		var query = bin.Search.Trim();
		return query.Length == 0 || new[] { item.Name, item.Detail, item.Format, item.State, item.ReferenceId, item.LocalPath }
			.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
	}
}

// Local presentation preferences; never contains catalogue records or production state.
public sealed class MediaSmartBinStore(string? path = null)
{
	private const int MaximumBytes = 64 * 1024;
	private readonly string _path = path ?? Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtaime", "operator-media-bins.json");
	private readonly SemaphoreSlim _gate = new(1, 1);
	private sealed record Settings(int Version, MediaSmartBin[] Bins);

	public async Task<IReadOnlyList<MediaSmartBin>> LoadAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!File.Exists(_path)) return [];
			await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
				4096, FileOptions.Asynchronous);
			if (stream.Length > MaximumBytes) return [];
			// Bounded read also covers a file that grows after the initial length observation.
			var bytes = new byte[MaximumBytes + 1];
			var count = 0;
			while (count < bytes.Length)
			{
				var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
				if (read == 0) break;
				count += read;
			}
			if (count > MaximumBytes) return [];
			var settings = JsonSerializer.Deserialize<Settings>(bytes.AsSpan(0, count));
			if (settings?.Version != 1 || settings.Bins is null) return [];
			return settings.Bins.Where(bin => bin is not null && bin.IsValid)
				.DistinctBy(bin => bin.Name, StringComparer.OrdinalIgnoreCase).Take(MediaSmartBin.MaximumBins).ToArray();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
		finally { _gate.Release(); }
	}

	public async Task SaveAsync(IEnumerable<MediaSmartBin> bins, CancellationToken cancellationToken = default)
	{
		var snapshot = bins.Where(bin => bin.IsValid).DistinctBy(bin => bin.Name, StringComparer.OrdinalIgnoreCase)
			.Take(MediaSmartBin.MaximumBins).ToArray();
		var bytes = JsonSerializer.SerializeToUtf8Bytes(new Settings(1, snapshot));
		if (bytes.Length > MaximumBytes) throw new InvalidDataException("Smart Bin preferences exceed their size limit.");
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			var directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
			File.Move(temporary, _path, overwrite: true);
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
			_gate.Release();
		}
	}
}
