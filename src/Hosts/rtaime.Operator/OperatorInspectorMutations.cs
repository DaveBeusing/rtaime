// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

// Stable-target adapters share the existing mutation/lifecycle owner and Client; selection is never changed to submit an edit.
public sealed partial class OperatorViewModel
{
    internal Task SynchronizeInspectorAsync() => SynchronizeAsync();
    internal async Task ApplyInspectorGainAsync(string sourceId, string streamId, double gain)
    {
        if (!CanControl() || _client is null ||
            _client.Snapshot?.AudioInputs.FirstOrDefault(input => input.SourceId == sourceId && input.StreamId == streamId) is not { } input) return;
        await ExecuteAsync("AUDIO GAIN", async () =>
        {
            await _client.SetAudioInputStateAsync(sourceId, gain, input.Muted);
            ApplyAudio(_client.Snapshot!, preserveSelectedGainEdit: true);
            CommandStatus = "AUDIO CONFIRMED";
        });
    }
    internal async Task<MediaDeckSnapshot?> ApplyInspectorCueAsync(string assetId, string cueId, string name)
    {
        if (!CanControl() || _client is null) return null;
        MediaDeckSnapshot? result = null;
        await ExecuteAsync("MEDIA MARKER", async () =>
        {
            result = await _client.ApplyMediaDeckMarkerAsync(new MediaMarkerCommand(MediaContractVersion.Current,
                new MediaAssetId(Identity.Parse(assetId)), MediaMarkerCommandKind.RenameCuePoint,
                cuePointId: new MediaCuePointId(Identity.Parse(cueId)), name: name));
            ConfirmedMediaDeckSnapshot?.Invoke(result);
            CommandStatus = "MARKER CONFIRMED";
        });
        return result;
    }
}
