// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Media.Contracts;

public enum MediaDeckState
{
	Unloaded = 1,
	Loading = 2,
	Ready = 3,
	Playing = 4,
	Paused = 5,
	Ended = 6,
	Error = 7
}


public sealed record MediaDeckRuntimeSnapshot
{
	public MediaDeckRuntimeSnapshot(
		CompatibilityVersion version,
		MediaDeckState state,
		MediaSourceId? sourceId = null,
		LocalMediaProbe? probe = null,
		MediaTransportSnapshot? transport = null,
		Failure? failure = null)
	{
		MediaContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaDeckState), state))
			throw new ArgumentOutOfRangeException(nameof(state));

		var loaded = state is MediaDeckState.Ready or MediaDeckState.Playing or MediaDeckState.Paused or MediaDeckState.Ended;
		if (loaded && (sourceId is null || probe is null || transport is null))
			throw new ArgumentException("Loaded runtime media-deck states require source, probe and transport snapshots.");
		if (state == MediaDeckState.Error && failure is null)
			throw new ArgumentException("Error runtime media-deck state requires failure details.", nameof(failure));
		if (state != MediaDeckState.Error && failure is not null)
			throw new ArgumentException("Only error runtime media-deck state may carry failure details.", nameof(failure));

		Version = version;
		State = state;
		SourceId = sourceId;
		Probe = probe;
		Transport = transport;
		Failure = failure;
	}

	public CompatibilityVersion Version { get; }
	public MediaDeckState State { get; }
	public MediaSourceId? SourceId { get; }
	public LocalMediaProbe? Probe { get; }
	public MediaTransportSnapshot? Transport { get; }
	public Failure? Failure { get; }

	public static MediaDeckRuntimeSnapshot Unloaded { get; } =
		new(MediaContractVersion.Current, MediaDeckState.Unloaded);
}

public sealed record MediaDeckSnapshot
{
	public MediaDeckSnapshot(
		CompatibilityVersion version,
		MediaDeckState state,
		MediaSourceId? sourceId = null,
		LocalMediaProbe? probe = null,
		MediaTransportSnapshot? transport = null,
		MediaMarkerSnapshot? markers = null,
		Failure? failure = null)
	{
		MediaContractVersion.EnsureSupported(version);
		if (!Enum.IsDefined(typeof(MediaDeckState), state))
			throw new ArgumentOutOfRangeException(nameof(state));

		var loaded = state is MediaDeckState.Ready or MediaDeckState.Playing or MediaDeckState.Paused or MediaDeckState.Ended;
		if (loaded && (sourceId is null || probe is null || transport is null || markers is null))
			throw new ArgumentException("Loaded media-deck states require source, probe, transport and marker snapshots.");
		if (state == MediaDeckState.Error && failure is null)
			throw new ArgumentException("Error media-deck state requires failure details.", nameof(failure));
		if (state != MediaDeckState.Error && failure is not null)
			throw new ArgumentException("Only error media-deck state may carry failure details.", nameof(failure));
		if (probe is not null && sourceId is not null && probe.SourceId != sourceId.Value)
			throw new ArgumentException("Media-deck source identity must match the probe source.");

		Version = version;
		State = state;
		SourceId = sourceId;
		Probe = probe;
		Transport = transport;
		Markers = markers;
		Failure = failure;
	}

	public CompatibilityVersion Version { get; }
	public MediaDeckState State { get; }
	public MediaSourceId? SourceId { get; }
	public LocalMediaProbe? Probe { get; }
	public MediaTransportSnapshot? Transport { get; }
	public MediaMarkerSnapshot? Markers { get; }
	public Failure? Failure { get; }
	public bool IsLoaded => Probe is not null && Transport is not null && Markers is not null;

	public static MediaDeckSnapshot Unloaded { get; } =
		new(MediaContractVersion.Current, MediaDeckState.Unloaded);

	public static MediaDeckSnapshot Failed(string code, string message) =>
		new(
			MediaContractVersion.Current,
			MediaDeckState.Error,
			failure: new Failure(code, message));
}

public sealed record MediaDeckOpenRequest
{
	public MediaDeckOpenRequest(
		CompatibilityVersion version,
		MediaSourceId sourceId,
		string path)
	{
		MediaContractVersion.EnsureSupported(version);
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("Media-deck path is required.", nameof(path));

		Version = version;
		SourceId = sourceId;
		Path = path.Trim();
	}

	public CompatibilityVersion Version { get; }
	public MediaSourceId SourceId { get; }
	public string Path { get; }
}
