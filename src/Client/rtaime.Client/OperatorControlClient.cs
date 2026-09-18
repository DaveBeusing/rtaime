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
        if (width == 0 || height == 0 || width > 512 || height > 512)
            throw new ArgumentOutOfRangeException(nameof(width), "Graphics assets must be between 1x1 and 512x512 pixels.");
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
        OperatorGraphicsOverlayDescriptor? graphicsOverlay = null)
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
        RuntimeStatus = runtimeStatus.Trim();
        TimingStatus = timingStatus.Trim();
        InputStatus = inputStatus.Trim();
        AIStatus = aiStatus.Trim();
        RecordingStatus = recordingStatus.Trim();
        VisualLayerEnabled = visualLayerEnabled;
        AudioPeakLevel = audioPeakLevel;
        GraphicsOverlay = graphicsOverlay ?? OperatorGraphicsOverlayDescriptor.Empty;
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
