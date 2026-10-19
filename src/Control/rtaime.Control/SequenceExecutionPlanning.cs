// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using rtaime.Control.Contracts;

namespace rtaime.Control;

/// <summary>
/// An immutable, non-authoritative description of a saved sequence revision.
/// Preparing this object cannot open a decoder or alter Program. The Runtime
/// must separately admit the required resources and commit a fenced execution.
/// </summary>
public sealed class SequenceExecutionPlan
{
    private readonly SequenceExecutionWindow[] _windows;

    internal SequenceExecutionPlan(Guid sequenceId, ulong authoringRevision, SequenceRate frameRate,
        long durationTicks, int peakDecoderOwners, int maximumSimultaneousClips,
        SequenceExecutionWindow[] windows)
    {
        SequenceId = sequenceId;
        AuthoringRevision = authoringRevision;
        FrameRate = frameRate;
        DurationTicks = durationTicks;
        PeakDecoderOwners = peakDecoderOwners;
        MaximumSimultaneousClips = maximumSimultaneousClips;
        _windows = windows.ToArray();
        Windows = Array.AsReadOnly(_windows);
    }

    public Guid SequenceId { get; }
    public ulong AuthoringRevision { get; }
    public SequenceRate FrameRate { get; }
    public long DurationTicks { get; }
    public int PeakDecoderOwners { get; }
    public int MaximumSimultaneousClips { get; }
    public IReadOnlyList<SequenceExecutionWindow> Windows { get; }

    /// <summary>Exact, non-accumulating conversion from the existing Runtime frame slot to authoring ticks.</summary>
    public long TickAtFrame(ulong zeroBasedFrame)
    {
        if (zeroBasedFrame > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(zeroBasedFrame));
        return SequenceTime.FromFrames(checked((long)zeroBasedFrame), FrameRate);
    }

    /// <summary>
    /// Fills caller-owned bounded storage. No LINQ, decoder work, IO, worker waits or
    /// heap allocations are needed on a Runtime boundary.
    /// Returns false after the end; gaps within the duration return true with zero samples.
    /// </summary>
    public bool TrySelectAtFrame(ulong zeroBasedFrame, Span<SequenceRenderSample> destination, out int count) =>
        TrySelectAtTick(TickAtFrame(zeroBasedFrame), destination, out count);

    public bool TrySelectAtTick(long tick, Span<SequenceRenderSample> destination, out int count)
    {
        count = 0;
        if (tick < 0 || tick >= DurationTicks) return false;

        var left = 0;
        var right = _windows.Length - 1;
        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            var window = _windows[middle];
            if (tick < window.StartTick) right = middle - 1;
            else if (tick >= window.EndTick) left = middle + 1;
            else
            {
                if (destination.Length < window.ClipCount)
                    throw new ArgumentException("Caller buffer cannot hold the admitted concurrent sequence clips.", nameof(destination));
                for (var index = 0; index < window.ClipCount; index++)
                {
                    var clip = window.At(index);
                    destination[index] = new SequenceRenderSample(clip.ClipId, clip.TrackId,
                        clip.AssetId, clip.Kind, clip.LayerOrder, clip.AudioBusId,
                        checked(clip.SourceIn + tick - clip.TimelineStart),
                        Weight(clip, tick), clip.TransitionKind);
                }
                count = window.ClipCount;
                return true;
            }
        }
        throw new InvalidOperationException("An admitted sequence has an uncovered execution interval.");
    }

    /// <summary>
    /// The 48 kHz half-open sample window corresponding to an existing rational Program
    /// boundary. The last window is clamped to the authored sequence end.
    /// </summary>
    public bool TryGetAudioWindow(ulong zeroBasedFrame, out SequenceAudioWindow window)
    {
        var start = TickAtFrame(zeroBasedFrame);
        if (start >= DurationTicks) { window = default; return false; }
        var end = Math.Min(DurationTicks, TickAtFrame(checked(zeroBasedFrame + 1)));
        var firstSample = checked((ulong)(start / 5));
        var lastSampleExclusive = checked((ulong)(end / 5));
        window = new SequenceAudioWindow(firstSample, checked((uint)(lastSampleExclusive - firstSample)));
        return true;
    }

    /// <summary>Exact fixed-point contribution for video and for each audio sample tick.</summary>
    public static byte Weight(SequenceScheduledClip clip, long tick)
    {
        if (clip.TransitionKind is null || clip.TransitionDuration == 0 ||
            tick < clip.TransitionStart || tick >= clip.TransitionStart + clip.TransitionDuration)
            return byte.MaxValue;
        var elapsed = tick - clip.TransitionStart;
        var towardNew = checked((byte)(((Int128)elapsed * byte.MaxValue) / clip.TransitionDuration));
        return clip.FadeIn ? towardNew : checked((byte)(byte.MaxValue - towardNew));
    }
}

public readonly record struct SequenceRenderSample(Guid ClipId, Guid TrackId, Guid AssetId,
    SequenceMediaKind Kind, int LayerOrder, string? AudioBusId,
    long SourcePositionTicks, byte Opacity, SequenceTransitionKind? TransitionKind);

public readonly record struct SequenceAudioWindow(ulong FirstSample, uint SampleCount);

public sealed record SequenceScheduledClip(Guid ClipId, Guid TrackId, Guid AssetId,
    SequenceMediaKind Kind, int LayerOrder, string? AudioBusId,
    long SourceIn, long TimelineStart,
    SequenceTransitionKind? TransitionKind, long TransitionStart, long TransitionDuration, bool FadeIn);

public sealed class SequenceExecutionWindow
{
    private readonly SequenceScheduledClip[] _clips;
    internal SequenceExecutionWindow(long startTick, long endTick, SequenceScheduledClip[] clips, int decoderOwners)
    {
        StartTick = startTick;
        EndTick = endTick;
        _clips = clips.ToArray();
        Clips = Array.AsReadOnly(_clips);
        DecoderOwners = decoderOwners;
    }
    public long StartTick { get; }
    public long EndTick { get; }
    public IReadOnlyList<SequenceScheduledClip> Clips { get; }
    public int DecoderOwners { get; }
    public int ClipCount => _clips.Length;
    internal SequenceScheduledClip At(int index) => _clips[index];
}

public sealed record SequenceExecutionCompilation(SequenceExecutionPlan? Plan, SequenceIssue[] Issues)
{
    public bool Succeeded => Plan is not null && Issues.Length == 0;
}

/// <summary>
/// Converts validated, catalogue-bound authoring into finite half-open execution slices.
/// This is Control-side planning only: real decoder/pool admission remains Runtime-owned.
/// </summary>
public static class SequenceExecutionCompiler
{
    public static SequenceExecutionCompilation Compile(SequenceDefinition definition, ulong authoringRevision,
        IReadOnlyCollection<SequenceAsset> assets, IReadOnlyCollection<string> audioBuses,
        int decoderOwnerCapacity)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(audioBuses);
        if (decoderOwnerCapacity is < 1 or > 4)
            return Reject("sequence.execution.profile", "decoderOwnerCapacity",
                "Select an explicit finite Runtime decoder profile (1–4 owners).");

        var validation = SequenceModel.Validate(definition, authoringRevision, assets, audioBuses);
        if (!validation.IsValid) return new(null, validation.Issues);
        if (definition.Clips.Length == 0)
            return Reject("sequence.execution.empty", "clips", "An empty sequence cannot be prepared for playback.");

        // Disabled and muted audio does not acquire a decoder or contribute a sample.
        // A future authoring edit is a new plan and never mutates this snapshot.
        var tracks = definition.Tracks.ToDictionary(t => t.TrackId);
        var clips = definition.Clips.Where(c => c.Enabled && tracks[c.TrackId].Enabled &&
            !(tracks[c.TrackId].Kind == SequenceMediaKind.Audio && (c.Muted || tracks[c.TrackId].Muted)))
            .ToArray();
        if (clips.Length == 0)
            return Reject("sequence.execution.empty", "clips", "The sequence has no enabled playable clips.");

        var duration = definition.Clips.Max(c => checked(c.TimelineStart + c.Duration));
        var transitionLookup = definition.Transitions
            .Where(t => t.Kind != SequenceTransitionKind.Cut)
            .SelectMany(t => new[] { (Id: t.FromClipId, Transition: t, FadeIn: false),
                                     (Id: t.ToClipId, Transition: t, FadeIn: true) })
            .ToLookup(x => x.Id);
        var boundaries = definition.Clips.SelectMany(c =>
            new[] { c.TimelineStart, checked(c.TimelineStart + c.Duration) })
            .Concat(definition.Transitions.SelectMany(t => new[] { t.Start, checked(t.Start + t.Duration) }))
            .Append(0).Append(duration)
            .Distinct().Order().ToArray();
        var windows = new List<SequenceExecutionWindow>(boundaries.Length);
        var peak = 0;
        var maxClips = 0;

        for (var index = 0; index < boundaries.Length - 1; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            if (end <= start) continue;
            var active = clips.Where(c => c.TimelineStart <= start && start < c.TimelineStart + c.Duration)
                .OrderBy(c => tracks[c.TrackId].LayerOrder)
                .ThenBy(c => c.TrackId)
                .ThenBy(c => c.TimelineStart)
                .ThenBy(c => c.ClipId)
                .ToArray();
            // One worker/native owner can be shared only for matching asset and source
            // timeline offset. Two independent offsets from one file need two owners.
            var required = active.Where(c => c.Media.Kind != SequenceMediaKind.Graphics)
                .Select(c => (c.Media.AssetId, Offset: c.SourceIn - c.TimelineStart))
                .Distinct().Count();
            if (required > decoderOwnerCapacity)
                return Reject("sequence.execution.overbudget", $"intervals/{start}",
                    $"Sequence requires {required} concurrent media owners but this Runtime profile admits {decoderOwnerCapacity}. No reservation was made.");

            var scheduled = active.Select(c =>
            {
                var matched = transitionLookup[c.ClipId]
                    .Where(t => t.Transition.Start <= start &&
                        start < t.Transition.Start + t.Transition.Duration)
                    .ToArray();
                if (matched.Length > 1)
                    throw new InvalidOperationException("Validated transitions cannot overlap for a clip.");
                var fade = matched.Length == 1 ? matched[0] : default;
                return new SequenceScheduledClip(c.ClipId, c.TrackId, c.Media.AssetId, c.Media.Kind,
                    tracks[c.TrackId].LayerOrder, tracks[c.TrackId].AudioBusId,
                    c.SourceIn, c.TimelineStart, matched.Length == 1 ? fade.Transition.Kind : null,
                    matched.Length == 1 ? fade.Transition.Start : 0,
                    matched.Length == 1 ? fade.Transition.Duration : 0,
                    matched.Length == 1 && fade.FadeIn);
            }).ToArray();
            windows.Add(new(start, end, scheduled, required));
            peak = Math.Max(peak, required);
            maxClips = Math.Max(maxClips, scheduled.Length);
        }

        // Every interval, including silence/black gaps, is represented. The caller
        // never has to scan mutable authoring entities on a media boundary.
        return new(new(definition.SequenceId, authoringRevision, definition.FrameRate,
            duration, peak, maxClips, windows.ToArray()), []);
    }

    private static SequenceExecutionCompilation Reject(string code, string path, string message) =>
        new(null, [new(code, path, message)]);
}
