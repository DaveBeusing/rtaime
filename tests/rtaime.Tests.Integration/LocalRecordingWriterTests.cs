using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Integration;

public sealed class LocalRecordingWriterTests
{
    [Fact]
    public async Task Local_recording_is_partial_until_orderly_finalize_then_promoted_atomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-recording-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outputId = RecordingOutputId.New();
            var writer = new LocalRecordingManifestWriter(root);
            await using var recorder = new ProgramRecorder(writer, capacity: 4);
            var request = new RecordingStartRequest(
                RecordingContractVersion.Current,
                RecordingSessionId.New(),
                new RecordingOutputDescriptor(outputId, MediaSinkId.New(), "Program"));

            Assert.True((await recorder.StartAsync(request)).Succeeded);
            var partial = Path.Combine(root, outputId + ".partial");
            var final = Path.Combine(root, outputId + ".rtaime-recording");
            Assert.True(File.Exists(partial));
            Assert.False(File.Exists(final));

            Assert.True(recorder.TryEnqueue(Frame(0)).Accepted);
            Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);

            Assert.False(File.Exists(partial));
            Assert.True(File.Exists(final));
            var lines = await File.ReadAllLinesAsync(final);
            Assert.StartsWith("BEGIN|", lines[0], StringComparison.Ordinal);
            Assert.Contains(lines, line => line.StartsWith("SAMPLE|sequence=0|", StringComparison.Ordinal));
            Assert.Equal("END|state=finalized", lines[^1]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static FrameDescriptor Frame(ulong sequence) => new(
        MediaContractVersion.Current,
        MediaSourceId.New(),
        new SurfaceDescriptor(
            SurfaceId.New(),
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Shared,
            SurfaceOwnership.SharedLease,
            new SurfaceLifetimeDescriptor(new Generation(sequence), Identity.New()),
            new OpaqueSurfaceHandle("local.recording.test", $"surface-{sequence}")),
        new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)));
}
