// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Media;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public sealed partial class RundownViewModel
{
    private void UpdateRowEvidence()
    {
        foreach (var row in Items)
        {
            MediaPoolItemViewModel? media = null;
            OperatorSourceTileViewModel? source = null;
            if (row.Item is RundownMediaItem clip)
            {
                media = _mediaPool.Library.Items.FirstOrDefault(item => item.Kind == MediaPoolItemKind.Clip && item.ReferenceId == clip.AssetId.ToString());
                source = _operator.Sources.FirstOrDefault(item => item.Id == clip.SourceId.ToString());
            }
            else if (row.Item is RundownSceneItem scene)
                source = _operator.Scenes.FirstOrDefault(item => item.Id == scene.SceneId.ToString())?.ProgramSource;
            row.UpdateEvidence(media?.Thumbnail ?? source?.Thumbnail,
                row.Item is RundownHoldItem hold ? $"{hold.Frames}f" :
                media?.KnownDuration is { } duration ? duration.ToString(@"hh\:mm\:ss") : "DURATION UNKNOWN",
                row.Item is RundownMediaItem ? media?.AvailabilityLabel ?? "ASSET UNVERIFIED" : "CONTROL VALIDATES TARGET",
                _confirmedSnapshot?.Execution);
        }
    }
}
public sealed partial class RundownOperatorItemViewModel
{
    public ImageSource? Thumbnail { get; private set; }
    public string Duration { get; private set; } = "DURATION UNKNOWN";
    public string Readiness { get; private set; } = "UNVERIFIED";
    public string RepeatLabel => Item.Repeat.Mode == RundownRepeatMode.None ? "NO REPEAT" : $"{Item.Repeat.Mode} ×{Item.Repeat.RepeatCount}";
    internal void UpdateEvidence(ImageSource? thumbnail, string duration, string availability, RundownExecutionSnapshot? execution)
    {
        Thumbnail = thumbnail;
        Duration = duration;
        Readiness = execution?.PreparedItemId == Item.ItemId && execution.PreparedMediaState == "NEXT_READY"
            ? execution.PreparedMediaExpiresAtUtc is { } expires && expires <= DateTimeOffset.UtcNow
                ? "PREPARE AGAIN · RESIDENCY EXPIRED" : "NEXT_READY · HOST REVALIDATES AT GO"
            : $"{availability} · NOT PREPARED";
        PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new(nameof(Duration)));
        PropertyChanged?.Invoke(this, new(nameof(Readiness)));
    }
}
