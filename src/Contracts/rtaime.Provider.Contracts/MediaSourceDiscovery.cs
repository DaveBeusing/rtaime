// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.Contracts;

public static class MediaSourceCapabilityKinds
{
    public const string Discovery = "media.source.discovery";
    public const string Input = "media.input";
}

public readonly record struct DiscoveredMediaSourceId
{
    public DiscoveredMediaSourceId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Discovered media source identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public override string ToString() => Value.ToString();
}

public sealed class DiscoveredMediaSourceDescriptor
{
    private readonly ReadOnlyCollection<VideoFormat> _videoFormats;

    public DiscoveredMediaSourceDescriptor(
        DiscoveredMediaSourceId sourceId,
        ProviderId providerId,
        string displayName,
        string safeSourceIdentity,
        ProviderAvailability availability,
        IReadOnlyList<VideoFormat> videoFormats,
        AudioFormat? audioFormat,
        UtcTimestamp lastSeenAt)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 256)
            throw new ArgumentException("Discovered source display name is required and must not exceed 256 characters.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(safeSourceIdentity) || safeSourceIdentity.Length > 512)
            throw new ArgumentException("Safe discovered source identity is required and must not exceed 512 characters.", nameof(safeSourceIdentity));
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(videoFormats);
        if (videoFormats.Count > 16)
            throw new ArgumentOutOfRangeException(nameof(videoFormats), "A discovered source may expose at most 16 video formats.");

        SourceId = sourceId;
        ProviderId = providerId;
        DisplayName = displayName.Trim();
        SafeSourceIdentity = safeSourceIdentity.Trim();
        Availability = availability;
        _videoFormats = Array.AsReadOnly(videoFormats.Distinct().ToArray());
        AudioFormat = audioFormat;
        LastSeenAt = lastSeenAt;
    }

    public DiscoveredMediaSourceId SourceId { get; }
    public ProviderId ProviderId { get; }
    public string DisplayName { get; }
    public string SafeSourceIdentity { get; }
    public ProviderAvailability Availability { get; }
    public IReadOnlyList<VideoFormat> VideoFormats => _videoFormats;
    public AudioFormat? AudioFormat { get; }
    public UtcTimestamp LastSeenAt { get; }
}

public sealed class MediaSourceDiscoverySnapshot
{
    private readonly ReadOnlyCollection<DiscoveredMediaSourceDescriptor> _sources;

    public MediaSourceDiscoverySnapshot(
        ProviderId providerId,
        ProviderAvailability availability,
        UtcTimestamp observedAt,
        IReadOnlyList<DiscoveredMediaSourceDescriptor> sources,
        int maximumRetainedResults)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(sources);
        if (maximumRetainedResults is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedResults));
        if (sources.Count > maximumRetainedResults)
            throw new ArgumentException("Discovery snapshot exceeds its declared retention bound.", nameof(sources));
        if (sources.Any(source => source is null))
            throw new ArgumentException("Discovery snapshot must not contain null sources.", nameof(sources));
        if (sources.Any(source => source.ProviderId != providerId))
            throw new ArgumentException("Every discovered source must belong to the snapshot provider.", nameof(sources));
        if (sources.Select(source => source.SourceId).Distinct().Count() != sources.Count)
            throw new ArgumentException("Discovered source identities must be unique within a snapshot.", nameof(sources));

        ProviderId = providerId;
        Availability = availability;
        ObservedAt = observedAt;
        MaximumRetainedResults = maximumRetainedResults;
        _sources = Array.AsReadOnly(sources.ToArray());
    }

    public ProviderId ProviderId { get; }
    public ProviderAvailability Availability { get; }
    public UtcTimestamp ObservedAt { get; }
    public int MaximumRetainedResults { get; }
    public IReadOnlyList<DiscoveredMediaSourceDescriptor> Sources => _sources;
}


public enum MediaInputLifecycleState
{
    Disabled = 1,
    Connecting = 2,
    Connected = 3,
    Reconnecting = 4,
    Lost = 5,
    Faulted = 6,
    Stopping = 7
}

public readonly record struct MediaInputStatistics(
    ulong VideoFramesReceived,
    ulong AudioFramesReceived,
    ulong DroppedFrames,
    ulong RejectedFrames,
    ulong ReconnectCount,
    int QueueDepth,
    int MaximumQueueDepth);

public sealed record MediaInputHealthSnapshot
{
    public MediaInputHealthSnapshot(
        MediaSourceId sourceId,
        ProviderId providerId,
        DiscoveredMediaSourceId discoveredSourceId,
        string safeSourceIdentity,
        MediaInputLifecycleState lifecycle,
        bool connected,
        VideoFormat? videoFormat,
        AudioFormat? audioFormat,
        MediaInputStatistics statistics,
        UtcTimestamp? lastMediaAt,
        Failure? failure)
    {
        if (string.IsNullOrWhiteSpace(safeSourceIdentity))
            throw new ArgumentException("Safe source identity is required.", nameof(safeSourceIdentity));
        if (!Enum.IsDefined(lifecycle))
            throw new ArgumentOutOfRangeException(nameof(lifecycle));
        if (lifecycle == MediaInputLifecycleState.Connected && !connected)
            throw new ArgumentException("Connected lifecycle requires connected input state.", nameof(connected));
        if (lifecycle == MediaInputLifecycleState.Faulted && failure is null)
            throw new ArgumentException("Faulted media input requires failure evidence.", nameof(failure));

        SourceId = sourceId;
        ProviderId = providerId;
        DiscoveredSourceId = discoveredSourceId;
        SafeSourceIdentity = safeSourceIdentity.Trim();
        Lifecycle = lifecycle;
        Connected = connected;
        VideoFormat = videoFormat;
        AudioFormat = audioFormat;
        Statistics = statistics;
        LastMediaAt = lastMediaAt;
        Failure = failure;
    }

    public MediaSourceId SourceId { get; }
    public ProviderId ProviderId { get; }
    public DiscoveredMediaSourceId DiscoveredSourceId { get; }
    public string SafeSourceIdentity { get; }
    public MediaInputLifecycleState Lifecycle { get; }
    public bool Connected { get; }
    public VideoFormat? VideoFormat { get; }
    public AudioFormat? AudioFormat { get; }
    public MediaInputStatistics Statistics { get; }
    public UtcTimestamp? LastMediaAt { get; }
    public Failure? Failure { get; }
}
