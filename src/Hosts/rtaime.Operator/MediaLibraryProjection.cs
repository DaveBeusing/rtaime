// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

// Observation projection only; no transport, subscriptions, authority or decoder.
public sealed class MediaLibraryProjection
{
    internal const int MaxProjectedItems = 4096;
    internal List<MediaPoolItemViewModel> Items { get; } = [];
    public MediaAssetCollection FilteredItems { get; } = [];

    internal void Refresh(OperatorViewModel control, MediaDeckViewModel deck,
        MediaAssetCatalogSnapshot catalog, Func<string, string> preparationState)
    {
		Items.Clear();

		foreach (var source in control.Sources.Take(MaxProjectedItems))
		{
			var online = IsOnlineState(source.Health);
			Items.Add(new MediaPoolItemViewModel(
				$"source:{source.Id}",
				"Sources",
				MediaPoolItemKind.Source,
				source.Name,
				source.Detail,
				source.Format,
				online ? source.StateDetail : $"OFFLINE · {source.StateDetail}",
				source.Id,
				source.Thumbnail,
				online,
				online));
		}

		foreach (var asset in catalog.Assets.Take(Math.Max(0, MaxProjectedItems - Items.Count)))
		{
			var online = asset.Availability == MediaAssetAvailability.Online;
			var active = string.Equals(deck.AssetId, asset.AssetId.ToString(), StringComparison.Ordinal);
			Items.Add(new MediaPoolItemViewModel(
				$"clip:{asset.AssetId}",
				"Clips",
				MediaPoolItemKind.Clip,
				asset.DisplayName,
				$"{FormatDuration(asset.Duration)} · {asset.VideoCodec.ToString().ToUpperInvariant()}",
				$"{asset.VideoFormat.Width}×{asset.VideoFormat.Height} · {asset.VideoFormat.FrameRate}",
				asset.Availability.ToString().ToUpperInvariant(),
				asset.AssetId.ToString(),
				active
					? control.Sources.FirstOrDefault(source => string.Equals(source.Id, deck.SourceId, StringComparison.Ordinal))?.Thumbnail
					: null,
				online,
				online && active && deck.IsLoaded && !deck.HasError && deck.State is "READY" or "PLAYING" or "PAUSED",
				asset.SourceLocation,
				asset.Duration,
				new MediaSearchMetadata(asset.VideoCodec, asset.AudioCodec, asset.VideoFormat),
                online && active && deck.IsLoaded && !deck.HasError && deck.State is "READY" or "PLAYING" or "PAUSED"
                    ? "PREPARED" : preparationState(asset.AssetId.ToString())));
		}

		if (deck.IsLoaded &&
			catalog.Assets.All(asset => !string.Equals(asset.AssetId.ToString(), deck.AssetId, StringComparison.Ordinal)))
		{
			var mediaOnline = !deck.HasError;
			Items.Add(new MediaPoolItemViewModel(
				$"clip:{deck.AssetId}",
				"Clips",
				MediaPoolItemKind.Clip,
				deck.FileName,
				$"{deck.Duration} · {deck.VideoCodec}",
				$"{deck.Resolution} · {deck.FrameRate}",
				mediaOnline
					? deck.State
					: $"OFFLINE · {(deck.LastError ?? deck.State)}",
				deck.AssetId,
				control.Sources.FirstOrDefault(source => string.Equals(source.Id, deck.SourceId, StringComparison.Ordinal))?.Thumbnail,
				mediaOnline,
				mediaOnline && !deck.IsBusy,
				deck.LocalPath));
		}

		foreach (var input in control.AudioInputs.Take(Math.Max(0, MaxProjectedItems - Items.Count)))
		{
			var online = IsOnlineState(input.Health);
			Items.Add(new MediaPoolItemViewModel(
				$"audio:{input.SourceId}:{input.StreamId}",
				"Audio",
				MediaPoolItemKind.Audio,
				input.SourceName,
				$"{input.SourceType} · {input.AfvLabel}",
				input.StreamId,
				online ? input.Health : $"OFFLINE · {input.Health}",
				input.SourceId,
				null,
				online,
				online));
		}

		if (!string.IsNullOrWhiteSpace(control.GraphicsAssetName) &&
			!string.Equals(control.GraphicsAssetName, "—", StringComparison.Ordinal))
		{
			Items.Add(new MediaPoolItemViewModel(
				$"graphics:{control.GraphicsAssetName}",
				"Graphics",
				MediaPoolItemKind.Graphics,
				control.GraphicsAssetName,
				control.GraphicsDimensions,
				"RGBA / PNG",
				control.GraphicsState,
				control.GraphicsAssetName,
				null,
				true,
				true));
		}

		if (!string.IsNullOrWhiteSpace(control.AIFeature) &&
			!string.Equals(control.AIFeature, "—", StringComparison.Ordinal))
		{
			var ready = !string.Equals(control.AIStatus, "UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
			Items.Add(new MediaPoolItemViewModel(
				"composition:ai-showcase",
				"Compositions",
				MediaPoolItemKind.Composition,
				control.AIFeature,
				control.AIProvider,
				"AI effect",
				control.AIStatus,
				null,
				null,
				ready,
				ready));
		}

		if (Items.Count > MaxProjectedItems)
			Items.RemoveRange(MaxProjectedItems, Items.Count - MaxProjectedItems);

		Items.Sort((left, right) =>
		{
			var category = StringComparer.Ordinal.Compare(left.Category, right.Category);
			if (category != 0) return category;
			var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
			return name != 0 ? name : StringComparer.Ordinal.Compare(left.Key, right.Key);
		});
    }

    internal MediaPoolItemViewModel[] Filter(MediaSmartBin bin, MediaLibrarySearch search)
    {
        var filtered = Items.Where(item => MediaSmartBinPolicy.Matches(item, bin, search))
            .Take(MaxProjectedItems).ToArray();
        FilteredItems.ReplaceWith(filtered);
        return filtered;
    }

	private static string FormatDuration(TimeSpan duration) =>
		duration.TotalHours >= 1
			? duration.ToString(@"hh\:mm\:ss")
			: duration.ToString(@"mm\:ss");

	private static bool IsOnlineState(string value) =>
		!value.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase) &&
		!value.Contains("FAIL", StringComparison.OrdinalIgnoreCase) &&
		!value.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase);

}
