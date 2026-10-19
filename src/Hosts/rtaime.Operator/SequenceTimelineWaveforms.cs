// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Media;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed record SequenceWaveformPresentation(Guid ClipId, int Channels,
    long RequestedStartTicks, long RequestedEndTicks,
    MediaAnalysisWaveformBucket[] Buckets, string Status);

/// <summary>
/// One cancellable read-only waveform consumer. Runtime owns decoded PCM,
/// generation, native resource admission, cache and cancellation accounting.
/// </summary>
public sealed class SequenceTimelineWaveformService(IMediaAssetCatalogClient client) : IDisposable
{
    private readonly string _consumer = "operator-sequence-waveform-" + Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;

    public async Task<SequenceWaveformPresentation?> RequestAsync(SequenceClip clip,
        long sourceStart, long sourceEnd, int buckets, long generation, CancellationToken external = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Media.Kind != SequenceMediaKind.Audio || sourceStart < clip.SourceIn ||
            sourceEnd > clip.SourceOut || sourceEnd <= sourceStart)
            throw new ArgumentException("Waveform request must be inside the saved audio clip source range.");
        if (_disposed) return null;

        Cancel();
        _generation = generation;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(external);
        _pending = pending;
        var start = SequenceTimelineGeometry.ToMediaTicks(sourceStart);
        var requestedEnd = SequenceTimelineGeometry.ToMediaTicks(sourceEnd, ceiling: true);
        var end = Math.Min(requestedEnd, checked(start + MediaAnalysisContract.MaximumWaveformRangeTicks));
        var capped = end < requestedEnd;
        var requestedBuckets = Math.Clamp(buckets, 1, MediaAnalysisContract.MaximumBuckets);
        MediaAnalysisJobRequest? job = null;
        var finished = false;
        try
        {
            var request = new MediaAnalysisRequest(MediaAnalysisContract.Version,
                clip.Media.AssetId.ToString("D"), _consumer, generation,
                MediaAnalysisKind.Waveform, start, end, 0, 0,
                Math.Clamp((int)Math.Ceiling(Math.Log2((double)(sourceEnd - sourceStart) /
                    SequenceContract.TicksPerSecond + 1)), 0, 8), requestedBuckets);
            request.Validate();
            // Begin is not abandoned on cancellation. Its returned ID is always
            // retained for an explicit later Cancel if the job is still active.
            var begin = await client.BeginMediaAnalysisAsync(request);
            if (begin.Version != request.Version || begin.AssetId != request.AssetId ||
                begin.ConsumerId != request.ConsumerId || begin.Generation != generation ||
                !Guid.TryParse(begin.JobId, out var id) || id == Guid.Empty ||
                string.IsNullOrWhiteSpace(begin.RuntimeEpoch))
                return new(clip.ClipId, 0, start, end, [], "STALE · analysis identity unavailable");
            job = new(request.Version, begin.JobId, request.AssetId, _consumer, generation, begin.RuntimeEpoch);
            if (!Current(generation, pending.Token)) return null;
            if (begin.State is MediaAnalysisState.Unavailable or MediaAnalysisState.Stale or
                MediaAnalysisState.Failed or MediaAnalysisState.Canceled)
                return new(clip.ClipId, 0, start, end, [], "UNAVAILABLE · " + begin.Detail);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(pending.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (!deadline.IsCancellationRequested)
            {
                var observed = await client.GetMediaAnalysisAsync(job, deadline.Token);
                observed.Validate();
                if (!Current(generation, pending.Token)) return null;
                if (observed.Version != request.Version || observed.JobId != job.JobId ||
                    observed.AssetId != job.AssetId || observed.ConsumerId != job.ConsumerId ||
                    observed.Generation != generation || observed.RuntimeEpoch != job.RuntimeEpoch ||
                    observed.Kind != MediaAnalysisKind.Waveform || observed.RequestedTicks != start)
                    return new(clip.ClipId, 0, start, end, [], "STALE · source or generation changed");
                if (observed.State is MediaAnalysisState.Complete or MediaAnalysisState.Partial)
                {
                    finished = true;
                    if (observed.Waveform is not { Length: > 0 } waveform ||
                        waveform.Any(b => b is null || b.StartTicks < start || b.EndTicks > end ||
                            b.EndTicks <= b.StartTicks || b.Minimum.Any(x => !float.IsFinite(x)) ||
                            b.Maximum.Any(x => !float.IsFinite(x))))
                        return new(clip.ClipId, 0, start, end, [], "UNAVAILABLE · actual PCM buckets are absent");
                    return new(clip.ClipId, observed.ChannelCount, start, end, waveform,
                        observed.State == MediaAnalysisState.Partial || capped ||
                        waveform.Any(b => !b.HasSamples || b.Discontinuity)
                            ? "PARTIAL · actual PCM discontinuity or bounded range"
                            : "READY · source PCM waveform" + (observed.FromCache ? " · cached" : ""));
                }
                if (observed.State is not (MediaAnalysisState.Pending or MediaAnalysisState.Running))
                    return new(clip.ClipId, 0, start, end, [],
                        observed.State.ToString().ToUpperInvariant() + " · " + observed.Detail);
                await Task.Delay(150, deadline.Token);
            }
            return new(clip.ClipId, 0, start, end, [], "UNAVAILABLE · analysis deadline");
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested || external.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Current(generation, pending.Token)
                ? new(clip.ClipId, 0, start, end, [], "UNAVAILABLE · " + exception.GetType().Name)
                : null;
        }
        finally
        {
            if (job is not null && !finished)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await client.CancelMediaAnalysisAsync(job, cleanup.Token);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Native ownership remains charged until Runtime acknowledges release.
                }
            }
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }
    }

    private bool Current(long generation, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && generation == _generation;

    public void Cancel()
    {
        _generation++;
        try { _pending?.Cancel(); } catch (ObjectDisposedException) { }
        _pending = null;
    }

    public void Dispose() { _disposed = true; Cancel(); }
}

/// <summary>
/// View-only WPF drawing over verified decoded PCM buckets. Each channel is
/// rendered independently. Absent/discontinuous samples draw a gap, never noise.
/// </summary>
public sealed class SequenceWaveformGlyph : FrameworkElement
{
    public static readonly DependencyProperty PresentationProperty =
        DependencyProperty.Register(nameof(Presentation), typeof(SequenceWaveformPresentation),
            typeof(SequenceWaveformGlyph), new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public SequenceWaveformPresentation? Presentation
    {
        get => (SequenceWaveformPresentation?)GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var snapshot = Presentation;
        if (snapshot is null || snapshot.Channels is < 1 or > 8 ||
            snapshot.RequestedEndTicks <= snapshot.RequestedStartTicks ||
            ActualWidth <= 0 || ActualHeight <= 0) return;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(104, 205, 190)), 1);
        pen.Freeze();
        var midHeight = ActualHeight / snapshot.Channels;
        foreach (var bucket in snapshot.Buckets.Take(MediaAnalysisContract.MaximumBuckets))
        {
            if (!bucket.HasSamples || bucket.Discontinuity || bucket.StartTicks < snapshot.RequestedStartTicks ||
                bucket.EndTicks > snapshot.RequestedEndTicks || bucket.Minimum.Length < snapshot.Channels ||
                bucket.Maximum.Length < snapshot.Channels)
                continue;
            var center = (bucket.StartTicks + (bucket.EndTicks - bucket.StartTicks) / 2.0 -
                snapshot.RequestedStartTicks) /
                (snapshot.RequestedEndTicks - (double)snapshot.RequestedStartTicks);
            var x = Math.Clamp(center * ActualWidth, 0, ActualWidth);
            for (var channel = 0; channel < snapshot.Channels; channel++)
            {
                var minimum = Math.Clamp(bucket.Minimum[channel], -1f, 1f);
                var maximum = Math.Clamp(bucket.Maximum[channel], -1f, 1f);
                if (!float.IsFinite(minimum) || !float.IsFinite(maximum)) continue;
                var middle = (channel + 0.5) * midHeight;
                var amplitude = midHeight * 0.46;
                drawingContext.DrawLine(pen,
                    new Point(x, middle - maximum * amplitude),
                    new Point(x, middle - minimum * amplitude));
            }
        }
    }
}
