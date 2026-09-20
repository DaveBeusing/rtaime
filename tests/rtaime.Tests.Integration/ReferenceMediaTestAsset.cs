// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Integration;

internal sealed record ReferenceMediaProfile(
	string Id,
	FrameRate NativeFrameRate,
	TimeSpan Duration)
{
	public static ReferenceMediaProfile Hd25 { get; } = new("hd25", FrameRate.Fps25, TimeSpan.FromSeconds(6));
	public static ReferenceMediaProfile Hd50 { get; } = new("hd50", FrameRate.Fps50, TimeSpan.FromSeconds(6));
	public static ReferenceMediaProfile Hd5994 { get; } = new("hd5994", FrameRate.Fps59_94, TimeSpan.FromSeconds(6));

	public static IReadOnlyList<ReferenceMediaProfile> Required { get; } =
	[
		Hd25,
		Hd50,
		Hd5994
	];
}

internal sealed record ReferenceMediaManifest(
	string SchemaVersion,
	string Profile,
	string Container,
	string VideoCodec,
	string AudioCodec,
	uint Width,
	uint Height,
	string NativeFrameRate,
	uint AudioSampleRate,
	uint AudioChannels,
	double RequestedDurationSeconds,
	long GeneratedVideoFrames,
	ulong GeneratedAudioSamples,
	string VideoSignal,
	string AudioSignal,
	string Sha256);

internal sealed class ReferenceMediaTestAsset : IDisposable
{
	private const int GeneratorWidth = 640;
	private const int GeneratorHeight = 360;
	private const uint AudioSampleRate = 48_000;
	private const int AudioChannels = 2;
	private const int AudioChunkFrames = 1_024;
	private const string SchemaVersion = "1.0";

	private static readonly object GenerationGate = new();
	private static readonly Dictionary<string, CachedAsset> Cache = new(StringComparer.Ordinal);

	private readonly bool _deleteOnDispose;
	private bool _disposed;

	private ReferenceMediaTestAsset(
		string path,
		string manifestPath,
		ReferenceMediaManifest manifest,
		bool deleteOnDispose)
	{
		Path = path;
		ManifestPath = manifestPath;
		Manifest = manifest;
		_deleteOnDispose = deleteOnDispose;
	}

	public string Path { get; }
	public string ManifestPath { get; }
	public ReferenceMediaManifest Manifest { get; }

	public static bool IsRegressionEnabled =>
		string.Equals(
			Environment.GetEnvironmentVariable("RTAIME_REFERENCE_MEDIA_REGRESSION"),
			"1",
			StringComparison.Ordinal);

	public static ReferenceMediaTestAsset Generate(ReferenceMediaProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var retainedOutput = Environment.GetEnvironmentVariable("RTAIME_REFERENCE_MEDIA_OUTPUT");
		var retain = !string.IsNullOrWhiteSpace(retainedOutput);
		var root = retain
			? System.IO.Path.GetFullPath(retainedOutput!)
			: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rtaime-reference-media-regression");

		lock (GenerationGate)
		{
			Directory.CreateDirectory(root);
			var cacheKey = $"{root}|{profile.Id}";
			if (Cache.TryGetValue(cacheKey, out var cached) &&
				File.Exists(cached.Path) &&
				File.Exists(cached.ManifestPath))
			{
				return new ReferenceMediaTestAsset(
					cached.Path,
					cached.ManifestPath,
					cached.Manifest,
					deleteOnDispose: false);
			}

			EnsureFfmpegAvailable();
			var generated = GenerateUnsafe(profile, root);
			Cache[cacheKey] = generated;
			return new ReferenceMediaTestAsset(
				generated.Path,
				generated.ManifestPath,
				generated.Manifest,
				deleteOnDispose: false);
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		if (!_deleteOnDispose)
			return;

		TryDelete(Path);
		TryDelete(ManifestPath);
	}

	private static CachedAsset GenerateUnsafe(ReferenceMediaProfile profile, string outputRoot)
	{
		var workRoot = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			$"rtaime-reference-media-build-{profile.Id}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(workRoot);

		var rawVideoPath = System.IO.Path.Combine(workRoot, "video.rgba");
		var rawAudioPath = System.IO.Path.Combine(workRoot, "audio.f32le");
		var outputPath = System.IO.Path.Combine(outputRoot, $"rtaime-reference-{profile.Id}-h264-aac.mp4");
		var manifestPath = System.IO.Path.Combine(outputRoot, $"rtaime-reference-{profile.Id}-h264-aac.json");

		try
		{
			var frameCount = GenerateRawVideo(profile, rawVideoPath);
			var sampleCount = GenerateRawAudio(profile, rawAudioPath);
			Encode(profile, rawVideoPath, rawAudioPath, outputPath);

			var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath))).ToLowerInvariant();
			var manifest = new ReferenceMediaManifest(
				SchemaVersion,
				profile.Id,
				"MP4",
				"H.264/AVC",
				"AAC",
				1920,
				1080,
				profile.NativeFrameRate.ToString(),
				AudioSampleRate,
				AudioChannels,
				profile.Duration.TotalSeconds,
				frameCount,
				sampleCount,
				"BroadcastTestPatternGenerator + MotionTimingTestSignalGenerator",
				"GeneratedAudioTestSignalGenerator(Pulse, 1000 Hz, peak 0.25)",
				hash);
			File.WriteAllText(
				manifestPath,
				JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
			return new CachedAsset(outputPath, manifestPath, manifest);
		}
		finally
		{
			TryDelete(rawVideoPath);
			TryDelete(rawAudioPath);
			try
			{
				if (Directory.Exists(workRoot))
					Directory.Delete(workRoot, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	private static long GenerateRawVideo(ReferenceMediaProfile profile, string path)
	{
		var format = new VideoFormat(
			GeneratorWidth,
			GeneratorHeight,
			profile.NativeFrameRate,
			PixelFormat.Rgba8,
			ScanMode.Progressive);
		var staticPattern = new BroadcastTestPatternGenerator(
			new BroadcastTestPatternConfiguration(format));
		var staticPixels = staticPattern.Pixels.ToArray();
		var framePixels = new byte[staticPixels.Length];
		var motion = new MotionTimingTestSignalGenerator(format, AudioSampleRate);
		var frameCount = CalculateFrameCount(profile.NativeFrameRate, profile.Duration);
		var timebase = new Timebase(
			profile.NativeFrameRate.Denominator,
			profile.NativeFrameRate.Numerator);

		using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
		for (long index = 0; index < frameCount; index++)
		{
			staticPixels.CopyTo(framePixels, 0);
			var timing = new FrameTiming(checked((ulong)index), index, timebase);
			var region = motion.Render(timing);
			var destinationOffset = checked(region.Y * GeneratorWidth * 4);
			region.Pixels.Span.CopyTo(framePixels.AsSpan(destinationOffset));
			output.Write(framePixels);
		}

		return frameCount;
	}

	private static ulong GenerateRawAudio(ReferenceMediaProfile profile, string path)
	{
		var totalSamples = checked((ulong)Math.Round(
			profile.Duration.TotalSeconds * AudioSampleRate,
			MidpointRounding.AwayFromZero));
		var generator = new GeneratedAudioTestSignalGenerator(
			new GeneratedAudioTestSignalConfiguration(
				AudioFormat.Stereo48kFloat32,
				GeneratedAudioTestSignalMode.Pulse,
				1_000,
				0.25));
		var samples = new float[AudioChunkFrames * AudioChannels];

		using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);
		for (ulong position = 0; position < totalSamples;)
		{
			var count = checked((uint)Math.Min((ulong)AudioChunkFrames, totalSamples - position));
			var valueCount = checked((int)count * AudioChannels);
			var span = samples.AsSpan(0, valueCount);
			generator.FillInterleavedFloat32(
				new AudioBufferTiming(
					position,
					count,
					checked((long)position),
					new Timebase(1, AudioSampleRate)),
				span);
			output.Write(MemoryMarshal.AsBytes(span));
			position += count;
		}

		return totalSamples;
	}

	private static void Encode(
		ReferenceMediaProfile profile,
		string rawVideoPath,
		string rawAudioPath,
		string outputPath)
	{
		TryDelete(outputPath);
		var rate = profile.NativeFrameRate.ToString();
		var arguments = string.Join(
			" ",
			"-y",
			"-hide_banner",
			"-loglevel error",
			"-f rawvideo",
			"-pixel_format rgba",
			$"-video_size {GeneratorWidth}x{GeneratorHeight}",
			$"-framerate {rate}",
			$"-i {Quote(rawVideoPath)}",
			"-f f32le",
			$"-ar {AudioSampleRate}",
			$"-ac {AudioChannels}",
			$"-i {Quote(rawAudioPath)}",
			"-vf scale=1920:1080:flags=neighbor",
			"-c:v libx264",
			"-preset veryfast",
			"-crf 30",
			"-profile:v high",
			"-pix_fmt yuv420p",
			"-g 60",
			"-c:a aac",
			"-b:a 96k",
			$"-ar {AudioSampleRate}",
			$"-ac {AudioChannels}",
			"-map_metadata -1",
			"-metadata creation_time=1970-01-01T00:00:00Z",
			$"-metadata title=rtaime-reference-{profile.Id}",
			"-movflags +faststart",
			"-shortest",
			Quote(outputPath));

		var result = RunFfmpeg(arguments, TimeSpan.FromMinutes(4));
		if (result.ExitCode != 0 || !File.Exists(outputPath))
		{
			throw new Xunit.Sdk.XunitException(
				$"Reference media generation failed for {profile.Id}. Exit={result.ExitCode}. {result.Error}");
		}
	}

	private static long CalculateFrameCount(FrameRate rate, TimeSpan duration)
	{
		var numerator = checked(
			(decimal)duration.Ticks * rate.Numerator);
		var denominator = checked(
			(decimal)TimeSpan.TicksPerSecond * rate.Denominator);
		return checked((long)Math.Ceiling(numerator / denominator));
	}

	private static void EnsureFfmpegAvailable()
	{
		var result = RunFfmpeg("-version", TimeSpan.FromSeconds(15));
		if (result.ExitCode != 0)
		{
			throw new Xunit.Sdk.XunitException(
				"FFmpeg is required for reference-media regression generation. " +
				"Set RTAIME_FFMPEG_PATH when ffmpeg is not on PATH.");
		}
	}

	private static ProcessResult RunFfmpeg(string arguments, TimeSpan timeout)
	{
		var executable = Environment.GetEnvironmentVariable("RTAIME_FFMPEG_PATH");
		if (string.IsNullOrWhiteSpace(executable))
			executable = "ffmpeg";

		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			Arguments = arguments,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		using var process = Process.Start(startInfo)
			?? throw new Xunit.Sdk.XunitException("Unable to start FFmpeg.");
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
		{
			try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
			throw new Xunit.Sdk.XunitException($"FFmpeg exceeded the {timeout} generation timeout.");
		}

		Task.WaitAll([standardOutput, standardError], TimeSpan.FromSeconds(5));
		return new ProcessResult(
			process.ExitCode,
			standardOutput.IsCompletedSuccessfully ? standardOutput.Result.Trim() : string.Empty,
			standardError.IsCompletedSuccessfully ? standardError.Result.Trim() : string.Empty);
	}

	private static string Quote(string value) =>
		$"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException)
		{
		}
	}

	private sealed record CachedAsset(
		string Path,
		string ManifestPath,
		ReferenceMediaManifest Manifest);

	private readonly record struct ProcessResult(
		int ExitCode,
		string Output,
		string Error);
}
