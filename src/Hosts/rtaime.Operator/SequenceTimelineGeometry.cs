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
            // Choose only the earliest visible clips for this track. A large
            // catalogue can have thousands of overlapping authored clips, but
            // the Operator must never allocate or realize all of them per resize.
            var candidates = new List<SequenceClip>(Math.Min(remaining, 32));
            if (remaining > 0)
            {
                foreach (var clip in definition.Clips)
                {
                    if (clip.TrackId != track.TrackId ||
                        clip.TimelineStart >= endTick || ClipEnd(clip) <= fromTick)
                        continue;

                    var low = 0;
                    var high = candidates.Count;
                    while (low < high)
                    {
                        var middle = low + (high - low) / 2;
                        var other = candidates[middle];
                        var order = clip.TimelineStart.CompareTo(other.TimelineStart);
                        if (order == 0) order = clip.ClipId.CompareTo(other.ClipId);
                        if (order >= 0) low = middle + 1;
                        else high = middle;
                    }
                    if (low >= remaining) continue;
                    candidates.Insert(low, clip);
                    if (candidates.Count > remaining)
                        candidates.RemoveAt(remaining);
                }
            }

            var visible = new List<SequenceEditorClip>(candidates.Count);
            foreach (var clip in candidates)
            {
                var end = ClipEnd(clip);
                var displayStart = Math.Max(fromTick, clip.TimelineStart);
                var displayEnd = Math.Min(endTick, end);
                var left = Pixel(displayStart, fromTick, endTick, width);
                var right = Pixel(displayEnd, fromTick, endTick, width);
                visible.Add(new SequenceEditorClip(clip.ClipId, clip.TrackId,
                    clip.Media.AssetId, track.Kind, clip.TimelineStart, end,
                    clip.SourceIn, clip.SourceOut, clip.Enabled && track.Enabled,
                    clip.Muted || track.Muted, left, Math.Max(1.0, right - left)));
            }
            remaining -= visible.Count;
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
        // Test candidates in place, without per-edge arrays or LINQ iterators
        // while the pointer moves during an interactive edit.
        foreach (var clip in definition.Clips)
        {
            ConsiderSnapCandidate(clip.TimelineStart, tick, threshold, ref nearest, ref distance);
            ConsiderSnapCandidate(ClipEnd(clip), tick, threshold, ref nearest, ref distance);
        }
        foreach (var transition in definition.Transitions)
        {
            ConsiderSnapCandidate(transition.Start, tick, threshold, ref nearest, ref distance);
            ConsiderSnapCandidate(checked(transition.Start + transition.Duration),
                tick, threshold, ref nearest, ref distance);
        }
        ConsiderSnapCandidate(0, tick, threshold, ref nearest, ref distance);
        return nearest;
    }

    private static void ConsiderSnapCandidate(long candidate, long tick, long threshold,
        ref long nearest, ref long distance)
    {
        var diff = Math.Abs(candidate - tick);
        if (diff > threshold || diff > distance ||
            (diff == distance && candidate >= nearest))
            return;
        nearest = candidate;
        distance = diff;
    }

    /// <summary>
    /// Snap a dragged clip boundary, rather than the pointer position. The optional
    /// trailing edge lets move gestures magnetize either edge without changing duration.
    /// Magnetic tolerance is measured in viewport pixels and converted to authoring
    /// ticks; snapped results always lie on the rational sequence frame grid.
    /// </summary>
    public static long SnapEditBoundary(long proposedStart, SequenceDefinition definition,
        bool enabled, long visibleSpan, double viewportWidth, long cursorTick,
        Guid editedClipId, long trailingEdgeOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var anchor = Quantize(Math.Max(0, proposedStart), definition.FrameRate);
        if (!enabled || visibleSpan <= 0 || !double.IsFinite(viewportWidth) ||
            viewportWidth <= 0 || trailingEdgeOffset < 0)
            return anchor;

        var tolerance = (long)Math.Ceiling(Math.Clamp(
            (double)visibleSpan * 8.0 / viewportWidth, 0, 1_000_000_000));
        var nearest = anchor;
        var distance = tolerance + 1;
        var moving = definition.Clips.FirstOrDefault(c => c.ClipId == editedClipId);
        var movingGroup = moving?.LinkGroupId;

        void Consider(long edge)
        {
            // The raw source edge may have been authored at subframe precision.
            // Quantize candidates to the edit grid without changing saved sources.
            void TryOffset(long offset)
            {
                if (edge < offset) return;
                var candidate = Quantize(edge - offset, definition.FrameRate);
                var delta = Math.Abs(candidate - anchor);
                if (delta < distance || (delta == distance && candidate < nearest))
                {
                    if (delta > tolerance) return;
                    nearest = candidate;
                    distance = delta;
                }
            }

            TryOffset(0);
            if (trailingEdgeOffset > 0) TryOffset(trailingEdgeOffset);
        }

        Consider(0);
        Consider(Math.Max(0, cursorTick));
        foreach (var clip in definition.Clips)
        {
            if (clip.ClipId == editedClipId ||
                (movingGroup.HasValue && clip.LinkGroupId == movingGroup))
                continue;
            Consider(clip.TimelineStart);
            Consider(ClipEnd(clip));
        }
        foreach (var transition in definition.Transitions)
        {
            if (transition.FromClipId == editedClipId || transition.ToClipId == editedClipId)
                continue;
            Consider(transition.Start);
            Consider(checked(transition.Start + transition.Duration));
        }
        return nearest;
    }

    /// <summary>Bounded source-frame ruler projection shared with clip/cursor geometry.</summary>
    public static IReadOnlyList<SequenceRulerMark> Ruler(long from, long end,
        double width, SequenceRate rate)
    {
        if (from < 0 || end <= from || !double.IsFinite(width) || width <= 0 ||
            rate.Numerator <= 0 || rate.Denominator <= 0)
            return [];

        var frameDivisor = (BigInteger)TicksPerSecond * rate.Denominator;
        var firstFrame = (long)(((BigInteger)from * rate.Numerator + frameDivisor - 1) / frameDivisor);
        var visibleFrames = (double)(end - from) * rate.Numerator / (double)frameDivisor;
        var desiredStep = Math.Max(1, visibleFrames * 115.0 / width);
        long step = 1;
        while (step < desiredStep && step < 1_000_000_000) step *= 10;
        if (step / 5.0 >= desiredStep) step /= 5;
        else if (step / 2.0 >= desiredStep) step /= 2;

        var aligned = checked((firstFrame / step + (firstFrame % step == 0 ? 0 : 1)) * step);
        var marks = new List<SequenceRulerMark>(Math.Min(64, (int)(width / 70) + 1));
        for (var frame = aligned; marks.Count < 64; frame = checked(frame + step))
        {
            var tick = FrameTicks(frame, rate);
            if (tick >= end) break;
            marks.Add(new(tick, Pixel(tick, from, end, width), Timecode(tick, rate)));
        }
        return marks;
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

public sealed record SequenceRulerMark(long Tick, double Left, string Label);
\npublic sealed record SequenceEditorRow(Guid Id, string Name, SequenceMediaKind Kind,
    bool Enabled, bool Muted, int Layer, IReadOnlyList<SequenceEditorClip> Clips);

public sealed record SequenceEditorClip(Guid Id, Guid TrackId, Guid AssetId,
    SequenceMediaKind Kind, long StartTick, long EndTick, long SourceIn, long SourceOut,
    bool Enabled, bool Muted, double Left, double Width,
    SequenceWaveformPresentation? Waveform = null, bool Selected = false,
    bool Pending = false, bool Invalid = false);
