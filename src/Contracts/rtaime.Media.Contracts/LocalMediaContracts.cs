// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Media.Contracts;

public readonly record struct MediaAssetId
{
	public MediaAssetId(Identity value)
	{
		if (value.IsEmpty)
			throw new ArgumentException("Media asset identity must not be empty.", nameof(value));

		Value = value;
	}

	public Identity Value { get; }
	public static MediaAssetId New() => new(Identity.New());
	public override string ToString() => Value.ToString();
}

public enum MediaContainerFormat
{
	Mp4 = 1
}

public enum MediaVideoCodec
{
	H264 = 1,
	Hevc = 2,
	Av1 = 3,
	Vp9 = 4,
	Mpeg4Part2 = 5,
	Vc1 = 6,
	Mjpeg = 7
}

public enum MediaAudioCodec
{
	None = 0,
	Aac = 1,
	Mp3 = 2,
	Pcm = 3
}

public sealed record LocalMediaProbe
{
	public LocalMediaProbe(
		CompatibilityVersion version,
		MediaAssetId assetId,
		MediaSourceId sourceId,
		string fileName,
		MediaContainerFormat container,
		MediaVideoCodec videoCodec,
		MediaAudioCodec audioCodec,
		VideoFormat videoFormat,
		AudioFormat audioFormat,
		TimeSpan duration)
	{
		MediaContractVersion.EnsureSupported(version);
		if (string.IsNullOrWhiteSpace(fileName))
			throw new ArgumentException("Local media file name is required.", nameof(fileName));
		if (!Enum.IsDefined(typeof(MediaContainerFormat), container))
			throw new ArgumentOutOfRangeException(nameof(container));
		if (!Enum.IsDefined(typeof(MediaVideoCodec), videoCodec))
			throw new ArgumentOutOfRangeException(nameof(videoCodec));
		if (!Enum.IsDefined(typeof(MediaAudioCodec), audioCodec))
			throw new ArgumentOutOfRangeException(nameof(audioCodec));
		if (duration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration), "Local media duration must be greater than zero.");

		Version = version;
		AssetId = assetId;
		SourceId = sourceId;
		FileName = fileName.Trim();
		Container = container;
		VideoCodec = videoCodec;
		AudioCodec = audioCodec;
		VideoFormat = videoFormat;
		AudioFormat = audioFormat;
		Duration = duration;
	}

	public CompatibilityVersion Version { get; }
	public MediaAssetId AssetId { get; }
	public MediaSourceId SourceId { get; }
	public string FileName { get; }
	public MediaContainerFormat Container { get; }
	public MediaVideoCodec VideoCodec { get; }
	public MediaAudioCodec AudioCodec { get; }
	public VideoFormat VideoFormat { get; }
	public AudioFormat AudioFormat { get; }
	public TimeSpan Duration { get; }
}
