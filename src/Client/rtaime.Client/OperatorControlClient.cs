using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Client;

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
    public OperatorStatusSnapshot(
        AuthoritativeProductionState production,
        string runtimeStatus,
        string timingStatus,
        string inputStatus,
        string aiStatus,
        string recordingStatus,
        bool visualLayerEnabled,
        double audioPeakLevel)
    {
        Production = production ?? throw new ArgumentNullException(nameof(production));
        if (string.IsNullOrWhiteSpace(runtimeStatus)) throw new ArgumentException("Runtime status is required.", nameof(runtimeStatus));
        if (string.IsNullOrWhiteSpace(timingStatus)) throw new ArgumentException("Timing status is required.", nameof(timingStatus));
        if (string.IsNullOrWhiteSpace(inputStatus)) throw new ArgumentException("Input status is required.", nameof(inputStatus));
        if (string.IsNullOrWhiteSpace(aiStatus)) throw new ArgumentException("AI status is required.", nameof(aiStatus));
        if (string.IsNullOrWhiteSpace(recordingStatus)) throw new ArgumentException("Recording status is required.", nameof(recordingStatus));
        if (!double.IsFinite(audioPeakLevel) || audioPeakLevel is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(audioPeakLevel));

        RuntimeStatus = runtimeStatus.Trim();
        TimingStatus = timingStatus.Trim();
        InputStatus = inputStatus.Trim();
        AIStatus = aiStatus.Trim();
        RecordingStatus = recordingStatus.Trim();
        VisualLayerEnabled = visualLayerEnabled;
        AudioPeakLevel = audioPeakLevel;
    }

    public AuthoritativeProductionState Production { get; }
    public string RuntimeStatus { get; }
    public string TimingStatus { get; }
    public string InputStatus { get; }
    public string AIStatus { get; }
    public string RecordingStatus { get; }
    public bool VisualLayerEnabled { get; }
    public double AudioPeakLevel { get; }
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
