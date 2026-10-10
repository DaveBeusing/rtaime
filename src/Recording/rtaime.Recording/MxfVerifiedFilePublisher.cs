// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Recording;

/// <summary>
/// Publishes a completed recording on the same volume only after a separate
/// read-only validator accepts the flushed partial artifact.
/// </summary>
internal static class MxfVerifiedFilePublisher
{
    internal static void Publish(
        string partialPath,
        string finalPath,
        Action<string> independentProbe)
    {
        if (string.IsNullOrWhiteSpace(partialPath))
            throw new ArgumentException("Partial recording path is required.", nameof(partialPath));
        if (string.IsNullOrWhiteSpace(finalPath))
            throw new ArgumentException("Final recording path is required.", nameof(finalPath));
        ArgumentNullException.ThrowIfNull(independentProbe);

        var partial = Path.GetFullPath(partialPath);
        var final = Path.GetFullPath(finalPath);
        if (!string.Equals(Path.GetDirectoryName(partial), Path.GetDirectoryName(final), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verified MXF promotion requires a single destination directory.");
        if (!final.EndsWith(".mxf", StringComparison.OrdinalIgnoreCase) ||
            !partial.EndsWith(".partial.mxf", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(partial[..^".partial.mxf".Length], final[..^".mxf".Length], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MXF partial and final output names do not match.");
        if (!File.Exists(partial))
            throw new FileNotFoundException("Partial MXF artifact does not exist.", partial);
        if (File.Exists(final))
            throw new IOException("The final MXF artifact already exists.");

        // A read-only, independently implemented verifier must throw on all
        // incomplete/invalid streams. This method never swallows its failure.
        independentProbe(partial);
        File.Move(partial, final, overwrite: false);
    }
}
