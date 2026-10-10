// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

public readonly record struct RecordingProfileId
{
	public RecordingProfileId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("Recording profile identity is required.", nameof(value));
		var normalized = value.Trim();
		if (normalized.Length > 128)
			throw new ArgumentOutOfRangeException(nameof(value), "Recording profile identity must not exceed 128 characters.");
		if (normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
			throw new ArgumentException("Recording profile identity contains unsupported characters.", nameof(value));
		Value = normalized;
	}

	public string Value { get; }
	public override string ToString() => Value;
}

public readonly record struct RecordingWriterProviderId
{
	public RecordingWriterProviderId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("Recording writer provider identity is required.", nameof(value));
		var normalized = value.Trim();
		if (normalized.Length > 128)
			throw new ArgumentOutOfRangeException(nameof(value), "Recording writer provider identity must not exceed 128 characters.");
		if (normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
			throw new ArgumentException("Recording writer provider identity contains unsupported characters.", nameof(value));
		Value = normalized;
	}

	public string Value { get; }
	public override string ToString() => Value;
}

public enum RecordingAccelerationClass
{
	Software = 1,
	Hardware = 2
}

public enum RecordingProfileEvidenceState
{
	Implemented = 1,
	Qualified = 2,
	Unverified = 3
}

public sealed record RecordingProfileDescriptor
{
	public RecordingProfileDescriptor(
		RecordingProfileId profileId,
		string displayName,
		string container,
		string fileExtension,
		string videoCodec,
		string videoProfile,
		string? videoLevel,
		string audioCodec,
		IReadOnlyList<VideoFormat> supportedInputVideoFormats,
		AudioFormat requiredInputAudioFormat,
		uint videoBitRate,
		uint audioBitRate,
		RecordingAccelerationClass accelerationClass,
		RecordingWriterProviderId providerId,
		bool available,
		string? unavailableReason,
		RecordingProfileEvidenceState evidenceState,
		string evidence)
	{
		if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Recording profile display name is required.", nameof(displayName));
		if (string.IsNullOrWhiteSpace(container)) throw new ArgumentException("Recording profile container is required.", nameof(container));
		if (string.IsNullOrWhiteSpace(fileExtension) || !fileExtension.StartsWith(".", StringComparison.Ordinal) || fileExtension.Length < 2)
			throw new ArgumentException("Recording profile file extension must begin with '.'.", nameof(fileExtension));
		if (string.IsNullOrWhiteSpace(videoCodec)) throw new ArgumentException("Recording profile video codec is required.", nameof(videoCodec));
		if (string.IsNullOrWhiteSpace(videoProfile)) throw new ArgumentException("Recording profile video profile is required.", nameof(videoProfile));
		if (string.IsNullOrWhiteSpace(audioCodec)) throw new ArgumentException("Recording profile audio codec is required.", nameof(audioCodec));
		ArgumentNullException.ThrowIfNull(supportedInputVideoFormats);
		if (supportedInputVideoFormats.Count == 0) throw new ArgumentException("Recording profile requires at least one supported input video format.", nameof(supportedInputVideoFormats));
		if (!Enum.IsDefined(accelerationClass)) throw new ArgumentOutOfRangeException(nameof(accelerationClass));
		if (!Enum.IsDefined(evidenceState)) throw new ArgumentOutOfRangeException(nameof(evidenceState));
		if (string.IsNullOrWhiteSpace(evidence)) throw new ArgumentException("Recording profile evidence is required.", nameof(evidence));
		if (available && !string.IsNullOrWhiteSpace(unavailableReason))
			throw new ArgumentException("Available recording profiles must not carry an unavailable reason.", nameof(unavailableReason));
		if (!available && string.IsNullOrWhiteSpace(unavailableReason))
			throw new ArgumentException("Unavailable recording profiles require an unavailable reason.", nameof(unavailableReason));

		ProfileId = profileId;
		DisplayName = displayName.Trim();
		Container = container.Trim();
		FileExtension = fileExtension.Trim().ToLowerInvariant();
		VideoCodec = videoCodec.Trim();
		VideoProfile = videoProfile.Trim();
		VideoLevel = string.IsNullOrWhiteSpace(videoLevel) ? null : videoLevel.Trim();
		AudioCodec = audioCodec.Trim();
		SupportedInputVideoFormats = Array.AsReadOnly(supportedInputVideoFormats.Distinct().ToArray());
		RequiredInputAudioFormat = requiredInputAudioFormat;
		VideoBitRate = videoBitRate;
		AudioBitRate = audioBitRate;
		AccelerationClass = accelerationClass;
		ProviderId = providerId;
		Available = available;
		UnavailableReason = unavailableReason?.Trim();
		EvidenceState = evidenceState;
		Evidence = evidence.Trim();
	}

	public RecordingProfileId ProfileId { get; }
	public string DisplayName { get; }
	public string Container { get; }
	public string FileExtension { get; }
	public string VideoCodec { get; }
	public string VideoProfile { get; }
	public string? VideoLevel { get; }
	public string AudioCodec { get; }
	public IReadOnlyList<VideoFormat> SupportedInputVideoFormats { get; }
	public AudioFormat RequiredInputAudioFormat { get; }
	public uint VideoBitRate { get; }
	public uint AudioBitRate { get; }
	public RecordingAccelerationClass AccelerationClass { get; }
	public RecordingWriterProviderId ProviderId { get; }
	public bool Available { get; }
	public string? UnavailableReason { get; }
	public RecordingProfileEvidenceState EvidenceState { get; }
	public string Evidence { get; }
}

public sealed class RecordingProfileCatalog
{
	private readonly ReadOnlyCollection<RecordingProfileDescriptor> _profiles;
	private readonly IReadOnlyDictionary<RecordingProfileId, RecordingProfileDescriptor> _byId;

	public RecordingProfileCatalog(
		IEnumerable<RecordingProfileDescriptor> profiles,
		RecordingProfileId defaultProfileId)
	{
		ArgumentNullException.ThrowIfNull(profiles);
		var materialized = profiles.ToArray();
		if (materialized.Length == 0)
			throw new ArgumentException("Recording profile catalog must contain at least one profile.", nameof(profiles));

		var duplicates = materialized
			.GroupBy(profile => profile.ProfileId)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key.ToString())
			.ToArray();
		if (duplicates.Length != 0)
			throw new ArgumentException($"Recording profile catalog contains duplicate profile identities: {string.Join(", ", duplicates)}.", nameof(profiles));

		var byId = materialized.ToDictionary(profile => profile.ProfileId);
		if (!byId.ContainsKey(defaultProfileId))
			throw new ArgumentException("Default recording profile must exist in the catalog.", nameof(defaultProfileId));

		_profiles = Array.AsReadOnly(materialized);
		_byId = new ReadOnlyDictionary<RecordingProfileId, RecordingProfileDescriptor>(byId);
		DefaultProfileId = defaultProfileId;
	}

	public RecordingProfileId DefaultProfileId { get; }
	public IReadOnlyList<RecordingProfileDescriptor> Profiles => _profiles;

	public bool TryGet(RecordingProfileId profileId, out RecordingProfileDescriptor profile) =>
		_byId.TryGetValue(profileId, out profile!);

	public RecordingProfileDescriptor GetRequired(RecordingProfileId profileId) =>
		TryGet(profileId, out var profile)
			? profile
			: throw new KeyNotFoundException($"Recording profile '{profileId}' is not registered.");
}

public interface IProgramRecordingProfileCatalogProvider
{
	RecordingProfileCatalog ProfileCatalog { get; }
}

public static class ProfessionalRecordingFormats
{
	public static RecordingProfileId Mp4H264AacProfileId { get; } = new("mp4-h264-aac");
	public static RecordingProfileId Mov2VuyPcmProfileId { get; } = new("mov-2vuy-pcm");
	public static RecordingProfileId MxfOp1aUncompressedPcmProfileId { get; } = new("mxf-op1a-uncompressed-pcm");
	public static RecordingWriterProviderId WindowsMediaFoundationProviderId { get; } = new("windows-media-foundation");
	public static RecordingWriterProviderId ManagedQuickTimeProviderId { get; } = new("managed-quicktime");
	public static RecordingWriterProviderId ManagedMxfOp1aProviderId { get; } = new("managed-mxf-op1a");

	private static readonly ReadOnlyCollection<VideoFormat> Mp4InputFormats =
		Array.AsReadOnly(new[]
		{
			VideoFormat.Hd1080p50Rgba8,
			VideoFormat.Hd1080p59_94Rgba8
		});

	public static RecordingProfileDescriptor Mp4H264Aac => CreateMp4H264AacDescriptor();
	public static RecordingProfileDescriptor Mov2VuyPcm => CreateMov2VuyPcmDescriptor();
	public static RecordingProfileDescriptor MxfOp1aUncompressedPcm => CreateMxfOp1aUncompressedPcmDescriptor();

	/// <summary>
	/// A constrained OP1a profile. The default descriptor remains unavailable.
	/// RuntimeHost may explicitly provision a verified BMX/FFprobe backend.
	/// </summary>
	public static RecordingProfileDescriptor CreateMxfOp1aUncompressedPcmDescriptor() =>
		CreateMxfOp1aUncompressedPcmDescriptor(false);

	public static RecordingProfileDescriptor CreateMxfOp1aUncompressedPcmDescriptor(bool available) =>
		new(
			MxfOp1aUncompressedPcmProfileId,
			"MXF OP1a · Uncompressed 4:2:2 · PCM",
			"Material eXchange Format OP1a",
			".mxf",
			"Uncompressed YUV 4:2:2",
			"8-bit UYVY 4:2:2 progressive",
			null,
			"PCM S16LE 48 kHz Stereo",
			Mp4InputFormats,
			AudioFormat.Stereo48kFloat32,
			1_988_667_333,
			1_536_000,
			RecordingAccelerationClass.Software,
			ManagedMxfOp1aProviderId,
			available,
			available ? null : "MXF OP1a requires explicitly provisioned BMX and FFprobe binaries.",
			available ? RecordingProfileEvidenceState.Implemented : RecordingProfileEvidenceState.Unverified,
			available
				? "External BMX muxer with independently executed FFprobe media inspection, structural validation and validated atomic publication; physical storage throughput and external vendor interchange are unverified."
				: "Backend disabled until MXF OP1a binaries are provisioned and the profile is explicitly enabled.");


	public static RecordingProfileDescriptor CreateMp4H264AacDescriptor()
	{
		var available = OperatingSystem.IsWindows();
		return new RecordingProfileDescriptor(
			Mp4H264AacProfileId,
			"MP4 · H.264 Main · AAC-LC",
			"ISO Base Media File Format (MP4)",
			".mp4",
			"H.264/AVC",
			"Main",
			null,
			"AAC-LC",
			Mp4InputFormats,
			AudioFormat.Stereo48kFloat32,
			20_000_000,
			192_000,
			RecordingAccelerationClass.Software,
			WindowsMediaFoundationProviderId,
			available,
			available ? null : "MP4 H.264/AAC recording requires Windows Media Foundation.",
			RecordingProfileEvidenceState.Qualified,
			"Windows Media Foundation MP4 H.264/AAC software-interoperability path with independent reopen/decode qualification.");
	}

	public static RecordingProfileDescriptor CreateMov2VuyPcmDescriptor() =>
		new(
			Mov2VuyPcmProfileId,
			"MOV · Uncompressed 4:2:2 · PCM",
			"QuickTime Movie (MOV)",
			".mov",
			"Uncompressed YUV 4:2:2",
			"2vuy 8-bit",
			null,
			"PCM S16LE",
			Mp4InputFormats,
			AudioFormat.Stereo48kFloat32,
			1_988_667_333,
			1_536_000,
			RecordingAccelerationClass.Software,
			ManagedQuickTimeProviderId,
			true,
			null,
			RecordingProfileEvidenceState.Implemented,
			"Managed QuickTime MOV writer with 2vuy uncompressed video, sowt PCM16 audio and independent structural/sample-table probe validation.");
}
