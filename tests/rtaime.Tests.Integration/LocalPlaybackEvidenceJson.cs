// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text.Json;
using rtaime.Runtime;

namespace rtaime.Tests.Integration;

internal static class LocalPlaybackEvidenceJson
{
    internal const int MaximumBytes = 256 * 1024;

    internal static string Write(string path, object report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("Playback evidence exceeds the constant report bound.");
        File.WriteAllBytes(path, bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static object Summarize(RuntimePipelineTimingSnapshot timing) => new {
        timing.Capacity, timing.RecordedSamples, timing.OverwrittenSamples,
        timing.ContentionDroppedSamples, timing.Stages
    };
}
