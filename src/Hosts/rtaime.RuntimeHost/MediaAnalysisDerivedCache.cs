// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Regenerable, content-addressed thumbnail/peak cache. Manifests are committed
/// last by atomic rename; no original media or authoritative asset state is stored.
/// </summary>
internal sealed class MediaAnalysisDerivedCache
{
    private const string Format = "rtaime.analysis-cache.v1";
    internal const int MaximumEntries = 64;
    internal const long MaximumBytes = 32L * 1024 * 1024;
    internal const int MaximumBlobBytes = 768 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly object _ioGate = new();

    private sealed record Manifest(string Format, string Key, string Checksum, int BlobLength);
    internal MediaAnalysisDerivedCache(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Cache root is required.", nameof(root));
        _root = Path.GetFullPath(root);
    }

    internal MediaAnalysisObservation? Read(string key, MediaAnalysisRequest request)
    {
        lock (_ioGate) return ReadCore(key, request);
    }

    private MediaAnalysisObservation? ReadCore(string key, MediaAnalysisRequest request)
    {
        try
        {
            var identity = Hash(Encoding.UTF8.GetBytes(key));
            var manifestPath = Path.Combine(_root, identity + ".json");
            if (!File.Exists(manifestPath)) return null;
            var info = new FileInfo(manifestPath);
            if (info.Length is < 1 or > 2048) return null;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestPath), JsonOptions);
            if (manifest is null || manifest.Format != Format || manifest.Key != identity ||
                manifest.BlobLength is < 1 or > MaximumBlobBytes ||
                manifest.Checksum is null || manifest.Checksum.Length != 64 ||
                !manifest.Checksum.All(Uri.IsHexDigit))
                return null;
            var blobPath = Path.Combine(_root, identity + ".blob");
            var blobInfo = new FileInfo(blobPath);
            if (!blobInfo.Exists || blobInfo.Length != manifest.BlobLength) return null;
            var blob = File.ReadAllBytes(blobPath);
            if (blob.Length != manifest.BlobLength ||
                !string.Equals(Hash(blob), manifest.Checksum, StringComparison.Ordinal))
                return null;
            var result = JsonSerializer.Deserialize<MediaAnalysisObservation>(blob, JsonOptions);
            if (result is null || result.State != MediaAnalysisState.Complete || result.Kind != request.Kind ||
                result.RequestedTicks != request.PositionTicks ||
                (request.Kind == MediaAnalysisKind.Frame &&
                    (result.Width != request.Width || result.Height != request.Height || result.RgbaPixels is null)) ||
                (request.Kind == MediaAnalysisKind.Waveform &&
                    (result.Waveform is null || result.Waveform.Length != request.BucketCount ||
                     result.Waveform[0].StartTicks != request.PositionTicks ||
                     result.Waveform[^1].EndTicks != request.EndTicks ||
                     result.Waveform.Any(bucket => bucket is null ||
                         bucket.EndTicks <= bucket.StartTicks ||
                         bucket.Minimum is null || bucket.Maximum is null ||
                         bucket.Minimum.Any(value => !float.IsFinite(value)) ||
                         bucket.Maximum.Any(value => !float.IsFinite(value)) ||
                         bucket.Rms is { } rms && rms.Any(value => !float.IsFinite(value))))))
                return null;
            result.Validate();
            File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    internal void Write(string key, MediaAnalysisObservation result)
    {
        lock (_ioGate) WriteCore(key, result);
    }

    private void WriteCore(string key, MediaAnalysisObservation result)
    {
        if (result.State != MediaAnalysisState.Complete) return;
        try
        {
            result.Validate();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
            if (bytes.Length is < 1 or > MaximumBlobBytes) return;
            Directory.CreateDirectory(_root);
            var keyHash = Hash(Encoding.UTF8.GetBytes(key));
            var baseName = Path.Combine(_root, keyHash);
            var blobTemporary = baseName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var manifestTemporary = baseName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteAndFlush(blobTemporary, bytes);
                File.Move(blobTemporary, baseName + ".blob", overwrite: true);
                var manifest = new Manifest(Format, keyHash, Hash(bytes), bytes.Length);
                WriteAndFlush(manifestTemporary, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
                File.Move(manifestTemporary, baseName + ".json", overwrite: true);
            }
            finally
            {
                TryDelete(blobTemporary);
                TryDelete(manifestTemporary);
            }
            Prune();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException)
        {
            // Derived cache write failures never affect the actual media analysis result.
        }
    }

    private void Prune()
    {
        var directory = new DirectoryInfo(_root);
        if (!directory.Exists) return;
        var manifests = directory.GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long retained = 0;
        for (var i = 0; i < manifests.Length; i++)
        {
            var manifest = manifests[i];
            var blob = Path.Combine(_root, Path.GetFileNameWithoutExtension(manifest.Name) + ".blob");
            var blobSize = File.Exists(blob) ? new FileInfo(blob).Length : 0;
            var size = manifest.Length + blobSize;
            if (i < MaximumEntries && manifest.Length is > 0 and <= 2048 &&
                blobSize is > 0 and <= MaximumBlobBytes && retained + size <= MaximumBytes)
            {
                retained += size;
                continue;
            }
            TryDelete(manifest.FullName);
            TryDelete(blob);
        }
        // Each retained manifest and blob counts toward the persistent disk quota.
        // A crashed pre-manifest write must not retain orphaned large binary observations.
        foreach (var file in directory.GetFiles("*.tmp"))
            if (file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1))
                TryDelete(file.FullName);
        foreach (var blob in directory.GetFiles("*.blob"))
            if (!File.Exists(Path.ChangeExtension(blob.FullName, ".json")))
                TryDelete(blob.FullName);
    }

    private static void WriteAndFlush(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
