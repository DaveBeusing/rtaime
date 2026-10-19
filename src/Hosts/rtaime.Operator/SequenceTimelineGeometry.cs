// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Numerics;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

/// <summary>
/// Rational 240 kHz authoring geometry. Exact domain ticks are converted into
/// screen coordinates only for the visible portion; no playback clock is owned here.
/// </summary>
public static class SequenceTimelineGeometry
{
    public const long TicksPerSecond = SequenceContract.TicksPerSecond;
    public const double RowHeight = 42;
    public const int MaximumRealizedRows = 16;
    public const int MaximumRealizedClips = 256;

    public static long FrameTicks(long frame, SequenceRate rate)
    {
        if (frame < 0 || rate.Numerator <= 0 || rate.Denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        return checked((long)((BigInteger)frame * TicksPerSecond * rate.Denominator / rate.Numerator));
    }

    public static long Quantize(long tick, SequenceRate rate)
    {
        if (tick < 0 || rate.Numerator <= 0 || rate.Denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(tick));
        var unit = (BigInteger)TicksPerSecond * rate.Denominator;
        var frame = ((BigInteger)tick * rate.Numerator * 2 + unit) / (2 * unit);
        return FrameTicks(checked((long)frame), rate);
    }

    public static long ClipEnd(SequenceClip clip) => checked(clip.TimelineStart + clip.Duration);

    public static long ToTick(double x, double width, long from, long end, SequenceRate rate)
    {
        if (!double.IsFinite(x) || !double.IsFinite(width) || width <= 0 || end <= from)
            throw new ArgumentException("A positive visible authoring window is required.");
        var fraction = Math.Clamp(x / width, 0.0, 1.0);
        var tick = checked(from + (long)Math.Round((end - from) * fraction, MidpointRounding.AwayFromZero));
        return Math.Min(end, Quantize(tick, rate));
    }

    public static double Pixel(long tick, long start, long end, double width)
    {
        if (end <= start || width <= 0) return 0;
        return Math.Clamp((double)(tick - start) / (end - start) * width, 0, width);
    }

    /// <summary>Only clips intersecting the rational horizontal and vertical windows are realized.</summary>
    public static IReadOnlyList<SequenceEditorRow> Project(SequenceDefinition definition,
        long fromTick, long endTick, int firstTrack, int trackCount, double width)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (fromTick < 0 || endTick <= fromTick || firstTrack < 0 || trackCount < 0 ||
            !double.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(fromTick));
        var tracks = definition.Tracks.OrderBy(t => t.LayerOrder).ThenBy(t => t.TrackId).ToArray();
        if (firstTrack >= tracks.Length) return [];
        var selected = tracks.Skip(firstTrack).Take(Math.Min(trackCount, MaximumRealizedRows)).ToArray();
        var result = new List<SequenceEditorRow>(selected.Length);
        var remaining = MaximumRealizedClips;
        foreach (var track in selected)
        {
            var visible = new List<SequenceEditorClip>();
            foreach (var clip in definition.Clips.Where(c => c.TrackId == track.TrackId)
                .OrderBy(c => c.TimelineStart).ThenBy(c => c.ClipId))
            {
                var end = ClipEnd(clip);
                if (clip.TimelineStart >= endTick || end <= fromTick) continue;
                if (remaining == 0) break;
                var displayStart = Math.Max(fromTick, clip.TimelineStart);
                var displayEnd = Math.Min(endTick, end);
                var left = Pixel(displayStart, fromTick, endTick, width);
                var right = Pixel(displayEnd, fromTick, endTick, width);
                visible.Add(new SequenceEditorClip(clip.ClipId, clip.TrackId,
                    clip.Media.AssetId, track.Kind, clip.TimelineStart, end,
                    clip.SourceIn, clip.SourceOut, clip.Enabled && track.Enabled,
                    clip.Muted || track.Muted, left, Math.Max(1.0, right - left)));
                remaining--;
            }
            result.Add(new(track.TrackId, track.Name, track.Kind, track.Enabled,
                track.Muted, track.LayerOrder, visible));
        }
        return result;
    }

    public static long Snap(long tick, SequenceDefinition definition, bool enabled)
    {
        tick = Math.Clamp(Quantize(Math.Max(0, tick), definition.FrameRate), 0, SequenceContract.MaximumTime);
        if (!enabled) return tick;
        var threshold = FrameTicks(10, definition.FrameRate);
        var nearest = tick;
        var distance = threshold + 1;
        // Zero and end are real edges; transitions are saved clip-domain data.
        foreach (var candidate in definition.Clips.SelectMany(c =>
                 new[] { c.TimelineStart, ClipEnd(c) })
                 .Concat(definition.Transitions.SelectMany(t =>
                     new[] { t.Start, checked(t.Start + t.Duration) }))
                 .Append(0))
        {
            var diff = Math.Abs(candidate - tick);
            if (diff > threshold || diff > distance ||
                diff == distance && candidate >= nearest)
                continue;
            nearest = candidate;
            distance = diff;
        }
        return nearest;
    }

    public static long ToMediaTicks(long authoringTick, bool ceiling = false)
    {
        if (authoringTick < 0) throw new ArgumentOutOfRangeException(nameof(authoringTick));
        var numerator = (BigInteger)authoringTick * TimeSpan.TicksPerSecond;
        var divisor = new BigInteger(TicksPerSecond);
        return checked((long)((numerator + (ceiling ? divisor - 1 : 0)) / divisor));
    }

    public static string Timecode(long tick, SequenceRate rate)
    {
        if (rate.Numerator <= 0 || rate.Denominator <= 0) return "00:00:00:00";
        var frame = (BigInteger)Math.Max(0, tick) * rate.Numerator /
            ((BigInteger)TicksPerSecond * rate.Denominator);
        var nominal = (long)Math.Round((double)rate.Numerator / rate.Denominator);
        var seconds = checked((long)(frame / nominal));
        var last = checked((long)(frame % nominal));
        return $"{seconds / 3600:00}:{(seconds / 60) % 60:00}:{seconds % 60:00}:{last:00}";
    }
}

public sealed record SequenceEditorRow(Guid Id, string Name, SequenceMediaKind Kind,
    bool Enabled, bool Muted, int Layer, IReadOnlyList<SequenceEditorClip> Clips);

public sealed record SequenceEditorClip(Guid Id, Guid TrackId, Guid AssetId,
    SequenceMediaKind Kind, long StartTick, long EndTick, long SourceIn, long SourceOut,
    bool Enabled, bool Muted, double Left, double Width);
