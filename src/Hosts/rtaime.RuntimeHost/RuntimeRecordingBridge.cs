using rtaime.Media.Contracts;
using rtaime.Recording;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

/// <summary>
/// Outer composition boundary that connects the committed Program binding to the failure-isolated recorder.
/// Recording observes committed execution; it never mutates or owns production authority.
/// </summary>
public sealed class RuntimeRecordingBridge
{
    private readonly ProgramRecorder _recorder;

    public RuntimeRecordingBridge(ProgramRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    public RecordingEnqueueResult TryRecordCommittedProgram(
        CommittedRuntimeExecution execution,
        FrameDescriptor video,
        AudioBufferDescriptor? audio = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(video);

        var output = _recorder.Snapshot.Output;
        if (output is null)
        {
            return RecordingEnqueueResult.Rejected(new rtaime.Core.Failure(
                "recording.runtime.no_output",
                "No active recording output is configured."));
        }

        var programBindings = execution.PreparedExecution.Bindings
            .Where(binding => binding.MediaSinkId == output.ProgramSinkId)
            .ToArray();

        if (programBindings.Length != 1)
        {
            return RecordingEnqueueResult.Rejected(new rtaime.Core.Failure(
                "recording.runtime.program_binding_invalid",
                "Committed execution must contain exactly one Program binding for the recording output."));
        }

        var committedSource = programBindings[0].MediaSourceId;
        if (committedSource is null)
        {
            return RecordingEnqueueResult.Rejected(new rtaime.Core.Failure(
                "recording.runtime.program_binding_invalid",
                "Committed Program binding requires a media source."));
        }

        if (committedSource.Value != video.SourceId)
        {
            return RecordingEnqueueResult.Rejected(new rtaime.Core.Failure(
                "recording.runtime.source_mismatch",
                "Program frame source does not match the committed Program binding."));
        }

        return _recorder.TryEnqueue(video, audio);
    }
}
