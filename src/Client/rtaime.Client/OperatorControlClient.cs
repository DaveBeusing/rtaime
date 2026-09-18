using System.Collections.ObjectModel;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Client;

public sealed record OperatorSourceDescriptor
{
    public OperatorSourceDescriptor(
        string id,
        string name,
        string type = "LIVE",
        string format = "UNKNOWN",
        string health = "UNKNOWN",
        string mediaState = "—",
        TimeSpan? remaining = null,
        string? mediaFileName = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Source id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Source name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Source type is required.", nameof(type));
        if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("Source format is required.", nameof(format));
        if (string.IsNullOrWhiteSpace(health)) throw new ArgumentException("Source health is required.", nameof(health));
        if (string.IsNullOrWhiteSpace(mediaState)) throw new ArgumentException("Media state is required.", nameof(mediaState));
        if (remaining < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(remaining));

        Id = id.Trim();
        Name = name.Trim();
        Type = type.Trim().ToUpperInvariant();
        Format = format.Trim();
        Health = health.Trim().ToUpperInvariant();
        MediaState = mediaState.Trim().ToUpperInvariant();
        Remaining = remaining;
        MediaFileName = string.IsNullOrWhiteSpace(mediaFileName) ? null : mediaFileName.Trim();
    }

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string Format { get; }
    public string Health { get; }
    public string MediaState { get; }
    public TimeSpan? Remaining { get; }
    public string? MediaFileName { get; }
}

public sealed record OperatorGraphicsAsset
{
    public OperatorGraphicsAsset(string name, uint width, uint height, byte[] rgbaPixels)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Graphics asset name is required.", nameof(name));
        if (width == 0 || height == 0 || width > 384 || height > 384)
            throw new ArgumentOutOfRangeException(nameof(width), "Graphics assets must be between 1x1 and 384x384 pixels.");
        ArgumentNullException.ThrowIfNull(rgbaPixels);
        var expected = checked((int)((ulong)width * height * 4UL));
        if (rgbaPixels.Length != expected)
            throw new ArgumentException($"Graphics RGBA payload requires exactly '{expected}' bytes.", nameof(rgbaPixels));

        Name = name.Trim();
        Width = width;
        Height = height;
        RgbaPixels = rgbaPixels.ToArray();
    }

    public string Name { get; }
    public uint Width { get; }
    public uint Height { get; }
    public byte[] RgbaPixels { get; }
}

public sealed record OperatorGraphicsOverlayDescriptor(
    bool AssetLoaded,
    string? AssetName,
    uint AssetWidth,
    uint AssetHeight,
    bool Visible,
    double PositionX,
    double PositionY,
    double Scale)
{
    public static OperatorGraphicsOverlayDescriptor Empty { get; } =
        new(false, null, 0, 0, false, 0.72, 0.06, 1.0);
}

public sealed record OperatorAudioInputDescriptor
{
    public OperatorAudioInputDescriptor(
        string sourceId,
        string streamId,
        double gain,
        bool muted,
        double leftPeak,
        double rightPeak,
        double masterPeak,
        bool clipping,
        string health)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Audio source id is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(streamId)) throw new ArgumentException("Audio stream id is required.", nameof(streamId));
        if (!double.IsFinite(gain) || gain is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(gain));
        ValidatePeak(leftPeak, nameof(leftPeak));
        ValidatePeak(rightPeak, nameof(rightPeak));
        ValidatePeak(masterPeak, nameof(masterPeak));
        if (string.IsNullOrWhiteSpace(health)) throw new ArgumentException("Audio health is required.", nameof(health));

        SourceId = sourceId.Trim();
        StreamId = streamId.Trim();
        Gain = gain;
        Muted = muted;
        LeftPeak = leftPeak;
        RightPeak = rightPeak;
        MasterPeak = masterPeak;
        Clipping = clipping;
        Health = health.Trim().ToUpperInvariant();
    }

    public string SourceId { get; }
    public string StreamId { get; }
    public double Gain { get; }
    public bool Muted { get; }
    public double LeftPeak { get; }
    public double RightPeak { get; }
    public double MasterPeak { get; }
    public bool Clipping { get; }
    public string Health { get; }

    private static void ValidatePeak(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(name, "Audio peak must be finite and in the inclusive range 0..1.");
    }
}

public sealed record OperatorAudioProgramDescriptor(
    string ActiveVideoSourceId,
    string ActiveStreamId,
    double Gain,
    bool Muted,
    double LeftPeak,
    double RightPeak,
    double MasterPeak,
    bool Clipping,
    string Health)
{
    public static OperatorAudioProgramDescriptor Unknown { get; } =
        new("—", "—", 1, false, 0, 0, 0, false, "UNKNOWN");
}

public sealed record OperatorRecordingDescriptor
{
    public OperatorRecordingDescriptor(
        string state,
        TimeSpan elapsed,
        string? destination,
        string? fileName,
        string? finalPath,
        ulong accepted,
        ulong written,
        ulong dropped,
        ulong rejected,
        ulong writerFailures,
        Failure? failure)
    {
        if (string.IsNullOrWhiteSpace(state))
            throw new ArgumentException("Recording state is required.", nameof(state));
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));

        State = state.Trim().ToUpperInvariant();
        Elapsed = elapsed;
        Destination = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();
        FileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
        FinalPath = string.IsNullOrWhiteSpace(finalPath) ? null : finalPath.Trim();
        Accepted = accepted;
        Written = written;
        Dropped = dropped;
        Rejected = rejected;
        WriterFailures = writerFailures;
        Failure = failure;
    }

    public string State { get; }
    public TimeSpan Elapsed { get; }
    public string? Destination { get; }
    public string? FileName { get; }
    public string? FinalPath { get; }
    public ulong Accepted { get; }
    public ulong Written { get; }
    public ulong Dropped { get; }
    public ulong Rejected { get; }
    public ulong WriterFailures { get; }
    public Failure? Failure { get; }

    public static OperatorRecordingDescriptor Unavailable { get; } =
        new("UNAVAILABLE", TimeSpan.Zero, null, null, null, 0, 0, 0, 0, 0, null);
}

public sealed record OperatorRecordingCommandResult(
    bool Succeeded,
    OperatorRecordingDescriptor Snapshot,
    Failure? Failure);

public static class OperatorHealthStates
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Unverified = "UNVERIFIED";
}

public sealed record OperatorHealthMetricDescriptor
{
    public OperatorHealthMetricDescriptor(string state, string detail)
    {
        if (state is not (OperatorHealthStates.Pass or OperatorHealthStates.Fail or OperatorHealthStates.Unverified))
            throw new ArgumentException("Health state must be PASS, FAIL or UNVERIFIED.", nameof(state));
        if (string.IsNullOrWhiteSpace(detail))
            throw new ArgumentException("Health detail is required.", nameof(detail));
        State = state;
        Detail = detail.Trim();
    }

    public string State { get; }
    public string Detail { get; }

    public static OperatorHealthMetricDescriptor Unverified(string detail) =>
        new(OperatorHealthStates.Unverified, detail);
}

public sealed record OperatorHealthDescriptor(
    OperatorHealthMetricDescriptor Engine,
    OperatorHealthMetricDescriptor Control,
    OperatorHealthMetricDescriptor Runtime,
    OperatorHealthMetricDescriptor Media,
    OperatorHealthMetricDescriptor Provider,
    OperatorHealthMetricDescriptor GpuProvider,
    string CurrentFormat,
    TimeSpan FrameTime,
    TimeSpan FrameBudget,
    ulong DroppedFrames,
    TimeSpan Uptime,
    string GpuUtilization,
    string Vram,
    DateTimeOffset ObservedAtUtc)
{
    public static OperatorHealthDescriptor Unavailable { get; } = new(
        OperatorHealthMetricDescriptor.Unverified("Health snapshot unavailable."),
        OperatorHealthMetricDescriptor.Unverified("Control health unavailable."),
        OperatorHealthMetricDescriptor.Unverified("Runtime health unavailable."),
        OperatorHealthMetricDescriptor.Unverified("Media health unavailable."),
        OperatorHealthMetricDescriptor.Unverified("Provider health unavailable."),
        OperatorHealthMetricDescriptor.Unverified("GPU provider health unavailable."),
        "UNVERIFIED",
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        TimeSpan.Zero,
        "UNVERIFIED",
        "UNVERIFIED",
        DateTimeOffset.MinValue);
}

public sealed record OperatorMutationResponse
{
    public OperatorMutationResponse(bool accepted, AuthoritativeProductionState state, Failure? failure)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        if (accepted && failure is not null)
            throw new ArgumentException("Accepted operator mutations must not carry a failure.", nameof(failure));
        if (!accepted && failure is null)
            throw new ArgumentException("Rejected operator mutations require a failure.", nameof(failure));

        Accepted = accepted;
        Failure = failure;
    }

    public bool Accepted { get; }
    public AuthoritativeProductionState State { get; }
    public Failure? Failure { get; }
}

public sealed record OperatorStatusSnapshot
{
    private readonly ReadOnlyCollection<OperatorSourceDescriptor> _sources;
    private readonly ReadOnlyCollection<OperatorAudioInputDescriptor> _audioInputs;

    public OperatorStatusSnapshot(
        AuthoritativeProductionState production,
        IReadOnlyList<OperatorSourceDescriptor> sources,
        string runtimeStatus,
        string timingStatus,
        string inputStatus,
        string aiStatus,
        string recordingStatus,
        bool visualLayerEnabled,
        double audioPeakLevel,
        OperatorGraphicsOverlayDescriptor? graphicsOverlay = null,
        IReadOnlyList<OperatorAudioInputDescriptor>? audioInputs = null,
        OperatorAudioProgramDescriptor? audioProgram = null,
        OperatorRecordingDescriptor? recording = null,
        OperatorHealthDescriptor? health = null)
    {
        Production = production ?? throw new ArgumentNullException(nameof(production));
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.Any(source => source is null))
            throw new ArgumentException("Operator snapshot requires at least one non-null source.", nameof(sources));
        if (string.IsNullOrWhiteSpace(runtimeStatus)) throw new ArgumentException("Runtime status is required.", nameof(runtimeStatus));
        if (string.IsNullOrWhiteSpace(timingStatus)) throw new ArgumentException("Timing status is required.", nameof(timingStatus));
        if (string.IsNullOrWhiteSpace(inputStatus)) throw new ArgumentException("Input status is required.", nameof(inputStatus));
        if (string.IsNullOrWhiteSpace(aiStatus)) throw new ArgumentException("AI status is required.", nameof(aiStatus));
        if (string.IsNullOrWhiteSpace(recordingStatus)) throw new ArgumentException("Recording status is required.", nameof(recordingStatus));
        if (!double.IsFinite(audioPeakLevel) || audioPeakLevel is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(audioPeakLevel));

        _sources = Array.AsReadOnly(sources.ToArray());
        _audioInputs = Array.AsReadOnly((audioInputs ?? Array.Empty<OperatorAudioInputDescriptor>()).ToArray());
        RuntimeStatus = runtimeStatus.Trim();
        TimingStatus = timingStatus.Trim();
        InputStatus = inputStatus.Trim();
        AIStatus = aiStatus.Trim();
        RecordingStatus = recordingStatus.Trim();
        VisualLayerEnabled = visualLayerEnabled;
        AudioPeakLevel = audioPeakLevel;
        GraphicsOverlay = graphicsOverlay ?? OperatorGraphicsOverlayDescriptor.Empty;
        AudioProgram = audioProgram ?? OperatorAudioProgramDescriptor.Unknown;
        Recording = recording ?? OperatorRecordingDescriptor.Unavailable;
        Health = health ?? OperatorHealthDescriptor.Unavailable;
    }

    public AuthoritativeProductionState Production { get; }
    public IReadOnlyList<OperatorSourceDescriptor> Sources => _sources;
    public string RuntimeStatus { get; }
    public string TimingStatus { get; }
    public string InputStatus { get; }
    public string AIStatus { get; }
    public string RecordingStatus { get; }
    public bool VisualLayerEnabled { get; }
    public double AudioPeakLevel { get; }
    public OperatorGraphicsOverlayDescriptor GraphicsOverlay { get; }
    public IReadOnlyList<OperatorAudioInputDescriptor> AudioInputs => _audioInputs;
    public OperatorAudioProgramDescriptor AudioProgram { get; }
    public OperatorRecordingDescriptor Recording { get; }
    public OperatorHealthDescriptor Health { get; }
}

/// <summary>
/// Transport seam for the versioned remote-control path. Implementations may use IPC/network transports;
/// tests may use an in-process adapter, but the Operator never gains production authority.
/// </summary>
public interface IOperatorControlTransport
{
    ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default);
    ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default);
    ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default);

    ValueTask<OperatorAudioInputDescriptor> SetAudioInputStateAsync(
        string sourceId,
        double gain,
        bool muted,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorAudioInputDescriptor>(new NotSupportedException("Operator transport does not expose audio input control."));

    ValueTask<OperatorGraphicsOverlayDescriptor> LoadGraphicsOverlayAsync(
        OperatorGraphicsAsset asset,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorGraphicsOverlayDescriptor>(new NotSupportedException("Operator transport does not expose graphics overlay control."));

    ValueTask<OperatorGraphicsOverlayDescriptor> SetGraphicsOverlayAsync(
        bool visible,
        double positionX,
        double positionY,
        double scale,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorGraphicsOverlayDescriptor>(new NotSupportedException("Operator transport does not expose graphics overlay control."));

    ValueTask<OperatorGraphicsOverlayDescriptor> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorGraphicsOverlayDescriptor>(new NotSupportedException("Operator transport does not expose graphics overlay control."));

    ValueTask<OperatorRecordingCommandResult> StartRecordingAsync(
        string destinationDirectory,
        string fileName,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorRecordingCommandResult>(new NotSupportedException("Operator transport does not expose recording control."));

    ValueTask<OperatorRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorRecordingCommandResult>(new NotSupportedException("Operator transport does not expose recording control."));

    ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(MediaDeckOpenRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<MediaDeckSnapshot> ApplyMediaDeckTransportAsync(MediaTransportCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(MediaMarkerCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));
}

public sealed class OperatorControlClient
{
    private readonly IOperatorControlTransport _transport;
    private OperatorStatusSnapshot? _snapshot;

    public OperatorControlClient(IOperatorControlTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public OperatorStatusSnapshot? Snapshot => _snapshot;
    public bool Connected => _snapshot is not null;

    public async ValueTask<OperatorStatusSnapshot> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        _snapshot = await _transport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return _snapshot;
    }

    public ValueTask<OperatorMutationResponse> SelectPreviewAsync(string sourceId, CancellationToken cancellationToken = default) =>
        SelectPreviewAsync(ParseSourceId(sourceId), cancellationToken);

    public async ValueTask<OperatorMutationResponse> SelectPreviewAsync(
        ProductionSourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        var current = RequireSnapshot();
        var command = new SelectPreviewCommand(Metadata(current.Production), sourceId);
        var result = await _transport.SelectPreviewAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
            await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<OperatorMutationResponse> CutAsync(string sourceId, CancellationToken cancellationToken = default) =>
        CutAsync(ParseSourceId(sourceId), cancellationToken);

    public ValueTask<OperatorMutationResponse> CutPreviewAsync(CancellationToken cancellationToken = default) =>
        CutAsync(RequireSnapshot().Production.Routing.PreviewSourceId, cancellationToken);

    public async ValueTask<OperatorMutationResponse> CutAsync(
        ProductionSourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        var current = RequireSnapshot();
        var command = new CutProgramCommand(Metadata(current.Production), sourceId);
        var result = await _transport.CutProgramAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
            await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<OperatorMutationResponse> DissolveAsync(
        string sourceId,
        uint durationFrames,
        CancellationToken cancellationToken = default) =>
        DissolveAsync(ParseSourceId(sourceId), durationFrames, cancellationToken);

    public ValueTask<OperatorMutationResponse> DissolvePreviewAsync(
        uint durationFrames,
        CancellationToken cancellationToken = default) =>
        DissolveAsync(RequireSnapshot().Production.Routing.PreviewSourceId, durationFrames, cancellationToken);

    public async ValueTask<OperatorMutationResponse> DissolveAsync(
        ProductionSourceId sourceId,
        uint durationFrames,
        CancellationToken cancellationToken = default)
    {
        var current = RequireSnapshot();
        var command = new DissolveProgramCommand(Metadata(current.Production), sourceId, durationFrames);
        var result = await _transport.DissolveProgramAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
            await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorAudioInputDescriptor> SetAudioInputStateAsync(
        string sourceId,
        double gain,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("Audio source id is required.", nameof(sourceId));
        if (!double.IsFinite(gain) || gain is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(gain), "Audio gain must be finite and in the inclusive range 0..4.");

        RequireSnapshot();
        var result = await _transport
            .SetAudioInputStateAsync(sourceId.Trim(), gain, muted, cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorGraphicsOverlayDescriptor> LoadGraphicsOverlayAsync(
        OperatorGraphicsAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var result = await _transport.LoadGraphicsOverlayAsync(asset, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorGraphicsOverlayDescriptor> SetGraphicsOverlayAsync(
        bool visible,
        double positionX,
        double positionY,
        double scale,
        CancellationToken cancellationToken = default)
    {
        var result = await _transport.SetGraphicsOverlayAsync(visible, positionX, positionY, scale, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorGraphicsOverlayDescriptor> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default)
    {
        var result = await _transport.ClearGraphicsOverlayAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorRecordingCommandResult> StartRecordingAsync(
        string destinationDirectory,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Recording destination directory is required.", nameof(destinationDirectory));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Recording file name is required.", nameof(fileName));

        RequireSnapshot();
        var result = await _transport
            .StartRecordingAsync(destinationDirectory.Trim(), fileName.Trim(), cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default) =>
        _transport.GetMediaDeckSnapshotAsync(cancellationToken);

    public ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(
        string path,
        MediaSourceId sourceId,
        CancellationToken cancellationToken = default) =>
        _transport.OpenMediaDeckAsync(
            new MediaDeckOpenRequest(MediaContractVersion.Current, sourceId, path),
            cancellationToken);

    public ValueTask<MediaDeckSnapshot> ApplyMediaDeckTransportAsync(
        MediaTransportCommand command,
        CancellationToken cancellationToken = default) =>
        _transport.ApplyMediaDeckTransportAsync(command, cancellationToken);

    public ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(
        MediaMarkerCommand command,
        CancellationToken cancellationToken = default) =>
        _transport.ApplyMediaDeckMarkerAsync(command, cancellationToken);

    public ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default) =>
        _transport.CloseMediaDeckAsync(cancellationToken);

    public void Disconnect() => _snapshot = null;

    private OperatorStatusSnapshot RequireSnapshot() =>
        _snapshot ?? throw new InvalidOperationException("Operator client must synchronize authoritative state before issuing commands.");

    private static ProductionSourceId ParseSourceId(string sourceId) =>
        new(Identity.Parse(sourceId));

    private static ControlCommandMetadata Metadata(AuthoritativeProductionState state) =>
        new(ControlContractVersion.Current, CommandId.New(), state.ProductionId, state.Revision);
}
