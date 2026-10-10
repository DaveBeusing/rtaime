// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class BmxOp1aRecordingWriterTests
{
    [Fact]
    public async Task Missing_muxer_fails_closed_before_creating_any_recording_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-bmx-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "missing-raw2bmx.exe");
        var writer = new BmxOp1aRecordingWriter(root, executable, Path.Combine(root, "missing-ffprobe.exe"));
        var output = new RecordingOutputDescriptor(RecordingOutputId.New(), MediaSinkId.New(), "Program");
        var start = new RecordingStartRequest(
            RecordingContractVersion.Current,
            RecordingSessionId.New(),
            output,
            ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId);
        await Assert.ThrowsAsync<RecordingOutputUnavailableException>(
            async () => await writer.OpenAsync(start, CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void MXF_target_rejects_non_mxf_and_path_traversal()
    {
        var writer = new BmxOp1aRecordingWriter(Path.GetTempPath(), "missing-raw2bmx.exe", "missing-ffprobe.exe");
        Assert.Throws<ArgumentException>(() => writer.ConfigureTarget(Path.GetTempPath(), "../take.mxf"));
        Assert.Throws<ArgumentException>(() => writer.ConfigureTarget(Path.GetTempPath(), "take.mov"));
        Assert.Equal("take.mxf", writer.ConfigureTarget(Path.GetTempPath(), "take"));
    }
}
