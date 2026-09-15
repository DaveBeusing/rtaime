using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Client;

public sealed record OperatorMutationResponse(
    bool Accepted,
    AuthoritativeProductionState State,
    Failure? Failure)
{
    public OperatorMutationResponse
    {
        ArgumentNullException.ThrowIfNull(State);
        if (Accepted && Failure is not null)
            throw new ArgumentException("Accepted operator mutations must not carry a failure.", nameof(Failure));
        if (!Accepted && Failure is null)
            throw new ArgumentException("Rejected operator mutations require a failure.", nameof(Failure));
    }
}

public sealed record OperatorStatusSnapshot(
    AuthoritativeProductionState Production,
    string RuntimeStatus,
    string TimingStatus,
    string InputStatus,
    string AIStatus,
    string RecordingStatus,
    bool VisualLayerEnabled,
    double AudioPeakLevel)
{
    public OperatorStatusSnapshot
    {
        ArgumentNullException.ThrowIfNull(Production);
        if (string.IsNullOrWhiteSpace(RuntimeStatus)) throw new ArgumentException("Runtime status is required.", nameof(RuntimeStatus));
        if (string.IsNullOrWhiteSpace(TimingStatus)) throw new ArgumentException("Timing status is required.", nameof(TimingStatus));
        if (string.IsNullOrWhiteSpace(InputStatus)) throw new ArgumentException("Input status is required.", nameof(InputStatus));
        if (string.IsNullOrWhiteSpace(AIStatus)) throw new ArgumentException("AI status is required.", nameof(AIStatus));
        if (string.IsNullOrWhiteSpace(RecordingStatus)) throw new ArgumentException("Recording status is required.", nameof(RecordingStatus));
        if (!double.IsFinite(AudioPeakLevel) || AudioPeakLevel is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(AudioPeakLevel));
    }
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

    public void Disconnect() => _snapshot = null;

    private OperatorStatusSnapshot RequireSnapshot() =>
        _snapshot ?? throw new InvalidOperationException("Operator client must synchronize authoritative state before issuing commands.");

    private static ControlCommandMetadata Metadata(AuthoritativeProductionState state) =>
        new(ControlContractVersion.Current, CommandId.New(), state.ProductionId, state.Revision);
}
