// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO.Compression;

namespace rtaime.Tests.Integration;

internal sealed class LocalMediaTestAsset : IDisposable
{
	private bool _disposed;

	private LocalMediaTestAsset(string path)
	{
		Path = path;
	}

	public string Path { get; }

	public static LocalMediaTestAsset ExtractReference1080p50()
	{
		var repositoryRoot = FindRepositoryRoot();
		var compressedPath = System.IO.Path.Combine(
			repositoryRoot,
			"tests",
			"TestAssets",
			"media",
			"reference-1080p50-h264-aac-1s.mp4.gz");
		if (!File.Exists(compressedPath))
			throw new Xunit.Sdk.XunitException($"Compressed local-media reference asset was not found: {compressedPath}");

		var extractedPath = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			$"rtaime-reference-1080p50-{Guid.NewGuid():N}.mp4");
		using (var input = File.OpenRead(compressedPath))
		using (var gzip = new GZipStream(input, CompressionMode.Decompress))
		using (var output = File.Create(extractedPath))
			gzip.CopyTo(output);

		return new LocalMediaTestAsset(extractedPath);
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		if (File.Exists(Path))
			File.Delete(Path);
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(System.IO.Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}

		throw new Xunit.Sdk.XunitException("Repository root containing rtaime.slnx could not be located.");
	}
}
