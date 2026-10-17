// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Media;

// Internal, versioned metadata seam; no bulk payload or Windows/COM types cross it.
internal interface ILocalMediaProbe
{
    int Version { get; }
    LocalMediaStreamMetadata Read(string path);
}

internal sealed record LocalMediaStreamMetadata(
    MediaContainerFormat Container,
    uint Width, uint Height,
    long FrameRateNumerator, long FrameRateDenominator,
    TimeSpan Duration)
{
    public const int CurrentVersion = 1;
    public string SampleEntry { get; init; } = "";
    public string AudioSampleEntry { get; init; } = "";
    public MediaVideoCodec? VideoCodec { get; init; }
    public int? Profile { get; init; }
    public int? BitDepth { get; init; }
    public int? ChromaFormat { get; init; }
    public bool VariableFrameRate { get; init; }
    public string ColorDescription { get; init; } = "unspecified";
}

internal sealed class Mp4LocalMediaProbe : ILocalMediaProbe
{
    public int Version => LocalMediaStreamMetadata.CurrentVersion;
    public LocalMediaStreamMetadata Read(string path) => IsoBaseMediaMetadataReader.Read(path, MediaContainerFormat.Mp4);
}

internal sealed class QuickTimeLocalMediaProbe : ILocalMediaProbe
{
    public int Version => LocalMediaStreamMetadata.CurrentVersion;
    public LocalMediaStreamMetadata Read(string path) => IsoBaseMediaMetadataReader.Read(path, MediaContainerFormat.QuickTimeMov);
}

internal sealed class LocalMediaProbeException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
