// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Recording;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class SsdPlaybackMemoryDiagnosticTests
{
    [Fact]
    public void Sparse_memory_observations_remain_bounded_and_report_writer_ownership()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-replay-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var diagnostic = new SsdPlaybackMemoryDiagnostic(null, null, "smoke", "1080p50",
                [new { bytes = 4, sha256 = new string('a', 64) }]);
            var reads = 0;
            for (var sample = 0; sample < 6; sample++)
            {
                diagnostic.TryCapture("sample", TimeSpan.FromSeconds(sample * 120), () =>
                {
                    reads++;
                    return null;
                }, "no-host", () => new ReplayCaptureWriterLifetimeStatistics(
                    2, 2, 2, 2, 0, 0, 0), () => new ReplayCaptureQueueStatistics(0, 2, 8));
            }
            diagnostic.TryWrite(root, playbackFailed: true, cleanupSucceeded: true);
            Assert.Equal(5, reads);
            var file = Path.Combine(root, "ssd-playback-memory-diagnostic.json");
            Assert.InRange(new FileInfo(file).Length, 1, 64 * 1024);
            using var report = JsonDocument.Parse(File.ReadAllText(file));
            var data = report.RootElement;
            Assert.Equal("PARTIAL", data.GetProperty("status").GetString());
            Assert.Equal(5, data.GetProperty("points").GetArrayLength());
            Assert.Equal(1, data.GetProperty("omittedPoints").GetInt32());
            Assert.True(data.GetProperty("playbackFailed").GetBoolean());
            Assert.Equal(2UL, data.GetProperty("points")[0].GetProperty("writerLifetime")
                .GetProperty("Opened").GetUInt64());
            Assert.Equal(8, data.GetProperty("points")[0].GetProperty("captureQueue")
                .GetProperty("QueueCapacity").GetInt32());
            var native = data.GetProperty("points")[0].GetProperty("nativeWriterLifetime");
            Assert.True(native.GetProperty("ActiveSinks").GetInt64() >= 0);
            Assert.True(native.GetProperty("ActiveScratchBuffers").GetInt64() >= 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
