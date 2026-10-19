// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using rtaime.Media.Contracts;

namespace rtaime.Client;

/// <summary>
/// Finite, local, read-only raw RGBA observation transport.
/// This stream cannot open, seek, modify or reserve production media resources.
/// </summary>
internal static class MediaAnalysisFramePipeReader
{
    internal static async ValueTask<byte[]> ReadAsync(
        string endpoint, string jobId, string token, int width, int height,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 256 ||
            !Guid.TryParseExact(jobId, "D", out _) || token.Length != 64 ||
            !token.All(Uri.IsHexDigit) || width is < 1 or > MediaAnalysisContract.MaximumFrameWidth ||
            height is < 1 or > MediaAnalysisContract.MaximumFrameHeight)
            throw new InvalidDataException("Frame observation token, endpoint or bounds are invalid.");

        var request = Encoding.ASCII.GetBytes(jobId + token);
        if (request.Length != 100)
            throw new InvalidDataException("Frame observation request frame is invalid.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var stream = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4)) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4)) != width ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4)) != height)
            throw new IOException("Media analysis frame was withheld or became stale.");
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
        if (payloadLength != checked(width * height * 4) ||
            payloadLength > MediaAnalysisContract.MaximumFrameBytes)
            throw new InvalidDataException("Media analysis frame response exceeded bounded size.");
        var pixels = new byte[payloadLength];
        await stream.ReadExactlyAsync(pixels, timeout.Token).ConfigureAwait(false);
        return pixels;
    }
}
