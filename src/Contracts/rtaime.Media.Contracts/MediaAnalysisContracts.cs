// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Media.Contracts;

/// <summary>
/// Observation-only media analysis. Results never authorize playback, persistence or routing.
/// The separate capability version evolves independently of the existing media contract.
/// </summary>
public static class MediaAnalysisContract
{
    public const string Version = "rtaime.media-analysis.v1";
    public const int MaximumFrameWidth = 320;
    public const int MaximumFrameHeight = 180;
    public const int MaximumBuckets = 256;
    public const int MaximumConsumers = 8;
    public const int MaximumConsumerLength = 128;
    public const long MaximumWaveformRangeTicks = 120L * TimeSpan.TicksPerSecond;
    public const int MaximumFrameBytes = MaximumFrameWidth * MaximumFrameHeight * 4;
}

public enum MediaAnalysisKind { Frame = 1, Waveform = 2 }

public enum MediaAnalysisState
{
    Pending = 1,
    Running = 2,
    Complete = 3,
    Partial = 4,
    Canceled = 5,
    Stale = 6,
    Unavailable = 7,
    Failed = 8
}

/// <summary>All positions are TimeSpan ticks, not frame indices or floating point seconds.</summary>
public sealed record MediaAnalysisRequest(
    string Version,
    string AssetId,
    string ConsumerId,
    long Generation,
    MediaAnalysisKind Kind,
    long PositionTicks,
    long EndTicks,
    int Width,
    int Height,
    int Level,
    int BucketCount)
{
    public void Validate()
    {
        if (Version != MediaAnalysisContract.Version)
            throw new ArgumentException("Unsupported media analysis contract version.");
        if (!Guid.TryParse(AssetId, out var asset) || asset == Guid.Empty)
            throw new ArgumentException("A known nonempty catalogue asset identity is required.");
        if (string.IsNullOrWhiteSpace(ConsumerId) || ConsumerId.Length > MediaAnalysisContract.MaximumConsumerLength ||
            ConsumerId.Any(char.IsControl))
            throw new ArgumentException("Analysis consumer identity is invalid.");
        if (Generation < 0 || !Enum.IsDefined(Kind) || PositionTicks < 0 || PositionTicks > TimeSpan.MaxValue.Ticks)
            throw new ArgumentException("Analysis kind, generation or position is invalid.");
        if (Kind == MediaAnalysisKind.Frame)
        {
            if (Width is < 1 or > MediaAnalysisContract.MaximumFrameWidth ||
                Height is < 1 or > MediaAnalysisContract.MaximumFrameHeight ||
                checked((long)Width * Height * 4) > MediaAnalysisContract.MaximumFrameBytes ||
                EndTicks != 0 || Level != 0 || BucketCount != 0)
                throw new ArgumentException("Frame analysis requires a bounded, single-position RGBA geometry.");
        }
        else if (Kind == MediaAnalysisKind.Waveform)
        {
            if (EndTicks <= PositionTicks || EndTicks > TimeSpan.MaxValue.Ticks ||
                EndTicks - PositionTicks > MediaAnalysisContract.MaximumWaveformRangeTicks ||
                Width != 0 || Height != 0 || Level is < 0 or > 8 ||
                BucketCount is < 1 or > MediaAnalysisContract.MaximumBuckets)
                throw new ArgumentException("Waveform analysis requires a bounded range and bucket level.");
        }
    }
}

public sealed record MediaAnalysisFileIdentity(string FingerprintSha256, long LengthBytes, long LastWriteUtcTicks)
{
    public void Validate()
    {
        if (FingerprintSha256 is null || FingerprintSha256.Length != 64 ||
            !FingerprintSha256.All(Uri.IsHexDigit) || LengthBytes < 1 || LastWriteUtcTicks <= 0)
            throw new ArgumentException("Analysis requires a valid catalogue file identity.");
    }
}

public sealed record MediaAnalysisBegin(
    string Version, string JobId, string AssetId, string ConsumerId, long Generation,
    string RuntimeEpoch, MediaAnalysisState State, string Detail);

/// <summary>
/// Each bucket is an exact range of samples; HasSamples=false and Discontinuity
/// preserve holes rather than fabricating a zero-amplitude waveform.
/// </summary>
public sealed record MediaAnalysisWaveformBucket(
    long StartTicks,
    long EndTicks,
    ulong FirstSample,
    uint SampleCount,
    bool HasSamples,
    bool Discontinuity,
    float[] Minimum,
    float[] Maximum,
    float[]? Rms);

public sealed record MediaAnalysisObservation(
    string Version,
    string JobId,
    string AssetId,
    string ConsumerId,
    long Generation,
    string RuntimeEpoch,
    MediaAnalysisKind Kind,
    MediaAnalysisState State,
    long RequestedTicks,
    long? ActualTicks,
    int Width,
    int Height,
    byte[]? RgbaPixels,
    int SampleRate,
    int ChannelCount,
    MediaAnalysisWaveformBucket[]? Waveform,
    bool FromCache,
    bool CancellationAcknowledged,
    string? ErrorCode,
    string Detail)
{
    public void Validate()
    {
        if (Version != MediaAnalysisContract.Version || !Guid.TryParse(JobId, out var job) || job == Guid.Empty ||
            !Guid.TryParse(AssetId, out var asset) || asset == Guid.Empty ||
            string.IsNullOrWhiteSpace(RuntimeEpoch) || string.IsNullOrWhiteSpace(ConsumerId) ||
            ConsumerId.Length > MediaAnalysisContract.MaximumConsumerLength ||
            Generation < 0 || !Enum.IsDefined(Kind) || !Enum.IsDefined(State))
            throw new InvalidDataException("Analysis observation identity, version or state is invalid.");
        if (RgbaPixels is not null && (Kind != MediaAnalysisKind.Frame ||
            Width is < 1 or > MediaAnalysisContract.MaximumFrameWidth ||
            Height is < 1 or > MediaAnalysisContract.MaximumFrameHeight ||
            RgbaPixels.Length != checked(Width * Height * 4)))
            throw new InvalidDataException("Analysis frame payload exceeds its declared bounds.");
        if (Waveform is { } buckets && (Kind != MediaAnalysisKind.Waveform ||
            buckets.Length > MediaAnalysisContract.MaximumBuckets ||
            SampleRate is < 1 or > 192000 || ChannelCount is < 1 or > 8 ||
            buckets.Any(b => b is null || b.Minimum.Length != ChannelCount ||
                b.Maximum.Length != ChannelCount ||
                b.Rms is { } rms && rms.Length != ChannelCount)))
            throw new InvalidDataException("Analysis waveform payload exceeds its declared bounds.");
        if (State is MediaAnalysisState.Pending or MediaAnalysisState.Running &&
            (RgbaPixels is not null || Waveform is not null))
            throw new InvalidDataException("Incomplete analysis must not publish data.");
    }
}
