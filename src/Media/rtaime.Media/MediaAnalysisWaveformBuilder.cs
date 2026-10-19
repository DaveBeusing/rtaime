// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Runtime.InteropServices;
using rtaime.Media.Contracts;

namespace rtaime.Media;

/// <summary>
/// Bounded, exact sample-position accumulator for presentation waveforms. No sample
/// history is retained, and absent PCM is always represented as a discontinuity.
/// </summary>
public sealed class MediaAnalysisWaveformBuilder
{
    private readonly long _start;
    private readonly long _end;
    private readonly int _rate;
    private readonly int _channels;
    private readonly Bucket[] _buckets;
    private ulong _nextSample;
    private bool _hasObserved;
    private bool _hasGap;

    private sealed class Bucket(int channels)
    {
        internal readonly float[] Minimum = Enumerable.Repeat(float.PositiveInfinity, channels).ToArray();
        internal readonly float[] Maximum = Enumerable.Repeat(float.NegativeInfinity, channels).ToArray();
        internal readonly double[] SumSquares = new double[channels];
        internal ulong FirstSample;
        internal uint Count;
        internal bool Discontinuity;
    }

    public MediaAnalysisWaveformBuilder(long startTicks, long endTicks, int bucketCount, int sampleRate, int channelCount)
    {
        if (startTicks < 0 || endTicks <= startTicks || endTicks - startTicks > MediaAnalysisContract.MaximumWaveformRangeTicks)
            throw new ArgumentOutOfRangeException(nameof(endTicks));
        if (bucketCount is < 1 or > MediaAnalysisContract.MaximumBuckets || sampleRate is < 1 or > 192_000 ||
            channelCount is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bucketCount));
        _start = startTicks;
        _end = endTicks;
        _rate = sampleRate;
        _channels = channelCount;
        _buckets = Enumerable.Range(0, bucketCount).Select(_ => new Bucket(channelCount)).ToArray();
        _nextSample = (ulong)((decimal)startTicks * sampleRate / TimeSpan.TicksPerSecond);
    }

    public bool HasSamples => _hasObserved;
    public bool HasDiscontinuities => _hasGap;

    public void Observe(AudioBufferDescriptor? descriptor, ReadOnlyMemory<byte> payload)
    {
        if (descriptor is null || payload.IsEmpty)
        {
            _hasGap = true;
            return;
        }
        if (descriptor.Format.SampleRate != _rate || descriptor.Format.ChannelCount != _channels ||
            payload.Length != checked((int)descriptor.Timing.SampleCount * _channels * sizeof(float)))
            throw new InvalidDataException("Analysis audio sample count, rate or layout changed.");
        var position = descriptor.Timing.SamplePosition;
        var samples = MemoryMarshal.Cast<byte, float>(payload.Span);
        if (position > _nextSample)
            MarkMissing(_nextSample, position);
        for (var frame = 0; frame < descriptor.Timing.SampleCount; frame++)
        {
            var samplePosition = checked(position + (uint)frame);
            if (samplePosition < _nextSample) continue; // Native seek overlap is not double-counted.
            var ticks = SampleTicks(samplePosition);
            if (ticks >= _end) break;
            if (ticks < _start) continue;
            var index = IndexOf(ticks);
            var bucket = _buckets[index];
            if (bucket.Count == 0) bucket.FirstSample = samplePosition;
            for (var channel = 0; channel < _channels; channel++)
            {
                var value = samples[checked(frame * _channels + channel)];
                if (!float.IsFinite(value)) throw new InvalidDataException("Decoded PCM contains a nonfinite sample.");
                bucket.Minimum[channel] = Math.Min(bucket.Minimum[channel], value);
                bucket.Maximum[channel] = Math.Max(bucket.Maximum[channel], value);
                bucket.SumSquares[channel] += (double)value * value;
            }
            bucket.Count++;
            _hasObserved = true;
        }
        _nextSample = Math.Max(_nextSample, checked(position + descriptor.Timing.SampleCount));
    }

    public void MarkMissing(long fromTicks, long toTicks)
    {
        if (toTicks <= fromTicks || toTicks <= _start || fromTicks >= _end) return;
        _hasGap = true;
        var first = IndexOf(Math.Max(_start, fromTicks));
        var last = IndexOf(Math.Min(_end - 1, toTicks - 1));
        for (var i = first; i <= last; i++) _buckets[i].Discontinuity = true;
    }

    public void Finish()
    {
        var tail = SampleTicks(_nextSample);
        if (tail < _end) MarkMissing(Math.Max(_start, tail), _end);
    }

    public MediaAnalysisWaveformBucket[] Build(bool withRms = true)
    {
        return _buckets.Select((bucket, index) =>
        {
            var start = checked(_start + (long)((decimal)(_end - _start) * index / _buckets.Length));
            var end = checked(_start + (long)((decimal)(_end - _start) * (index + 1) / _buckets.Length));
            var hasSamples = bucket.Count > 0;
            return new MediaAnalysisWaveformBucket(start, end, bucket.FirstSample, bucket.Count, hasSamples,
                bucket.Discontinuity || !hasSamples,
                hasSamples ? bucket.Minimum.ToArray() : new float[_channels],
                hasSamples ? bucket.Maximum.ToArray() : new float[_channels],
                withRms ? Enumerable.Range(0, _channels).Select(channel =>
                    hasSamples ? (float)Math.Sqrt(bucket.SumSquares[channel] / bucket.Count) : 0f).ToArray() : null);
        }).ToArray();
    }

    private long SampleTicks(ulong position) => checked((long)decimal.Round(
        (decimal)position * TimeSpan.TicksPerSecond / _rate, 0, MidpointRounding.AwayFromZero));

    private void MarkMissing(ulong first, ulong last)
    {
        if (last <= first) return;
        MarkMissing(SampleTicks(first), SampleTicks(last));
    }

    private int IndexOf(long ticks)
    {
        var scaled = ((decimal)(ticks - _start) * _buckets.Length) / (_end - _start);
        return Math.Clamp((int)scaled, 0, _buckets.Length - 1);
    }
}
