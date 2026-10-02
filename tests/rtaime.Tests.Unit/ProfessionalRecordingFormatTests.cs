// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class ProfessionalRecordingFormatTests
{
	[Fact]
	public void Mp4_profile_preserves_the_qualified_delivery_contract()
	{
		var profile = ProfessionalRecordingFormats.Mp4H264Aac;

		Assert.Equal("mp4-h264-aac", profile.ProfileId.ToString());
		Assert.Equal("windows-media-foundation", profile.ProviderId.ToString());
		Assert.Equal("ISO Base Media File Format (MP4)", profile.Container);
		Assert.Equal("H.264/AVC", profile.VideoCodec);
		Assert.Equal("Main", profile.VideoProfile);
		Assert.Equal("AAC-LC", profile.AudioCodec);
		Assert.Equal(".mp4", profile.FileExtension);
		Assert.Equal(20_000_000U, profile.VideoBitRate);
		Assert.Equal(192_000U, profile.AudioBitRate);
		Assert.Equal(AudioFormat.Stereo48kFloat32, profile.RequiredInputAudioFormat);
		Assert.Equal(
			new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
			profile.SupportedInputVideoFormats);
		Assert.Equal(RecordingAccelerationClass.Software, profile.AccelerationClass);
		Assert.Equal(RecordingProfileEvidenceState.Qualified, profile.EvidenceState);
	}

	[Fact]
	public void Mov_profile_defines_the_managed_uncompressed_delivery_contract()
	{
		var profile = ProfessionalRecordingFormats.Mov2VuyPcm;

		Assert.Equal("mov-2vuy-pcm", profile.ProfileId.ToString());
		Assert.Equal("managed-quicktime", profile.ProviderId.ToString());
		Assert.Equal("QuickTime Movie (MOV)", profile.Container);
		Assert.Equal("Uncompressed YUV 4:2:2", profile.VideoCodec);
		Assert.Equal("2vuy 8-bit", profile.VideoProfile);
		Assert.Equal("PCM S16LE", profile.AudioCodec);
		Assert.Equal(".mov", profile.FileExtension);
		Assert.Equal(1_988_667_333U, profile.VideoBitRate);
		Assert.Equal(1_536_000U, profile.AudioBitRate);
		Assert.Equal(AudioFormat.Stereo48kFloat32, profile.RequiredInputAudioFormat);
		Assert.Equal(
			new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
			profile.SupportedInputVideoFormats);
		Assert.Equal(RecordingAccelerationClass.Software, profile.AccelerationClass);
		Assert.Equal(RecordingProfileEvidenceState.Implemented, profile.EvidenceState);
		Assert.True(profile.Available);
		Assert.Null(profile.UnavailableReason);
	}

	[Fact]
	public void Mp4_availability_is_fail_closed_outside_Windows()
	{
		var profile = ProfessionalRecordingFormats.Mp4H264Aac;

		Assert.Equal(OperatingSystem.IsWindows(), profile.Available);
		if (!OperatingSystem.IsWindows())
			Assert.NotNull(profile.UnavailableReason);
		else
			Assert.Null(profile.UnavailableReason);
	}

	[Fact]
	public void Profile_catalog_rejects_duplicate_stable_ids()
	{
		var profile = CreateProfile("test-profile", "provider-a", available: true);

		Assert.Throws<ArgumentException>(() => new RecordingProfileCatalog(
			new[] { profile, profile },
			profile.ProfileId));
	}

	[Fact]
	public void Provider_registry_supports_multiple_profiles_and_selects_default()
	{
		var a = CreateProfile("profile-a", "provider-a", available: true);
		var b = CreateProfile("profile-b", "provider-b", available: true);
		var registry = new RecordingWriterProviderRegistry(
			new IProgramRecordingWriterProvider[]
			{
				new TestProvider(a),
				new TestProvider(b)
			},
			b.ProfileId);

		Assert.Equal(2, registry.ProfileCatalog.Profiles.Count);
		Assert.Equal(b.ProfileId, registry.Resolve().Profile.ProfileId);
		Assert.Equal(a.ProfileId, registry.Resolve(a.ProfileId).Profile.ProfileId);
	}

	[Fact]
	public void Provider_registry_rejects_unknown_and_unavailable_profiles()
	{
		var available = CreateProfile("profile-a", "provider-a", available: true);
		var unavailable = CreateProfile("profile-b", "provider-b", available: false);
		var registry = new RecordingWriterProviderRegistry(
			new IProgramRecordingWriterProvider[]
			{
				new TestProvider(available),
				new TestProvider(unavailable)
			},
			available.ProfileId);

		Assert.Throws<RecordingOutputUnavailableException>(() => registry.Resolve(new RecordingProfileId("missing")));
		Assert.Throws<RecordingOutputUnavailableException>(() => registry.Resolve(unavailable.ProfileId));
	}

	[Fact]
	public void Profile_selecting_writer_uses_requested_provider_and_normalizes_target()
	{
		var a = CreateProfile("profile-a", "provider-a", available: true, extension: ".aaa");
		var b = CreateProfile("profile-b", "provider-b", available: true, extension: ".bbb");
		var providerA = new TestProvider(a);
		var providerB = new TestProvider(b);
		var registry = new RecordingWriterProviderRegistry(
			new IProgramRecordingWriterProvider[] { providerA, providerB },
			a.ProfileId);
		var writer = new ProfileSelectingProgramRecordingWriter(registry);
		var root = Path.Combine(Path.GetTempPath(), "rtaime-profile-selection", Guid.NewGuid().ToString("N"));

		var normalized = writer.ConfigureTarget(b.ProfileId, root, "program");

		Assert.Equal("program.bbb", normalized);
		Assert.Equal(b.ProfileId, providerB.LastCreatedProfileId);
		Assert.Null(providerA.LastCreatedProfileId);
	}

	[Fact]
	public async Task Profile_selecting_writer_creates_one_concrete_writer_per_session()
	{
		var profile = CreateProfile("profile-a", "provider-a", available: true);
		var provider = new TestProvider(profile);
		var writer = new ProfileSelectingProgramRecordingWriter(
			new RecordingWriterProviderRegistry(new[] { provider }, profile.ProfileId));
		var output = new RecordingOutputDescriptor(RecordingOutputId.New(), MediaSinkId.New(), "Program");

		await writer.OpenAsync(new RecordingStartRequest(
			RecordingContractVersion.Current,
			RecordingSessionId.New(),
			output), CancellationToken.None);
		await writer.AbortAsync(CancellationToken.None);
		await writer.OpenAsync(new RecordingStartRequest(
			RecordingContractVersion.Current,
			RecordingSessionId.New(),
			output), CancellationToken.None);
		await writer.AbortAsync(CancellationToken.None);

		Assert.Equal(2, provider.CreatedCount);
		Assert.NotNull(writer.ActiveProfile);
		Assert.Equal(profile.ProfileId, writer.ActiveProfile!.ProfileId);
		Assert.True(writer.ActiveProviderId.HasValue);
		Assert.Equal(profile.ProviderId, writer.ActiveProviderId.Value);
	}

	[Fact]
	public void Mp4_writer_normalizes_only_the_mp4_target_extension()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-mp4-target", Guid.NewGuid().ToString("N"));
		var writer = new WindowsMediaFoundationMp4RecordingWriter(root);

		Assert.Equal("program.mp4", writer.ConfigureTarget(root, "program"));
		Assert.Equal("program.mp4", writer.ConfigureTarget(root, "program.mp4"));
		Assert.Throws<ArgumentException>(() => writer.ConfigureTarget(root, "program.mov"));
		Assert.Throws<ArgumentException>(() => writer.ConfigureTarget(root, Path.Combine("nested", "program.mp4")));
	}

	[Fact]
	public void Mov_writer_normalizes_only_the_mov_target_extension()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-mov-target", Guid.NewGuid().ToString("N"));
		var writer = new ManagedQuickTimeMovRecordingWriter(root);

		Assert.Equal("program.mov", writer.ConfigureTarget(root, "program"));
		Assert.Equal("program.mov", writer.ConfigureTarget(root, "program.mov"));
		Assert.Throws<ArgumentException>(() => writer.ConfigureTarget(root, "program.mxf"));
		Assert.Throws<ArgumentException>(() => writer.ConfigureTarget(root, Path.Combine("nested", "program.mov")));
	}

	[Fact]
	public void Mov_rgba_to_2vuy_conversion_is_deterministic_and_bounded()
	{
		byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255];
		var output = new byte[4];

		ManagedQuickTimeMovRecordingWriter.ConvertRgbaTo2Vuy(rgba, output, 2, 1);

		Assert.Equal(4, output.Length);
		Assert.InRange(output[0], (byte)16, (byte)240);
		Assert.InRange(output[1], (byte)16, (byte)235);
		Assert.InRange(output[2], (byte)16, (byte)240);
		Assert.InRange(output[3], (byte)16, (byte)235);
		Assert.Throws<ArgumentException>(() => ManagedQuickTimeMovRecordingWriter.ConvertRgbaTo2Vuy(rgba, new byte[3], 2, 1));
	}

	[Fact]
	public void Reference_writer_keeps_the_deterministic_reference_extension_without_becoming_a_profile()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-reference-target", Guid.NewGuid().ToString("N"));
		var writer = new ReferenceRecordingPayloadWriter(root);

		Assert.Equal("evidence.rtaime-recording", writer.ConfigureTarget(root, "evidence"));
		Assert.DoesNotContain(typeof(IProgramRecordingProfileCatalogProvider), writer.GetType().GetInterfaces());
	}

	[Fact]
	public void Mov_writer_rejects_invalid_failure_quota()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-mov-quota", Guid.NewGuid().ToString("N"));

		Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedQuickTimeMovRecordingWriter(root, 0));
	}

	[Fact]
	public void Mp4_writer_rejects_invalid_failure_quota()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-mp4-quota", Guid.NewGuid().ToString("N"));

		Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsMediaFoundationMp4RecordingWriter(root, 0));
	}

	private static RecordingProfileDescriptor CreateProfile(
		string profileId,
		string providerId,
		bool available,
		string extension = ".test") =>
		new(
			new RecordingProfileId(profileId),
			$"Test {profileId}",
			"Test Container",
			extension,
			"Test Video",
			"Test Profile",
			null,
			"Test Audio",
			new[] { VideoFormat.Hd1080p50Rgba8 },
			AudioFormat.Stereo48kFloat32,
			1_000_000,
			128_000,
			RecordingAccelerationClass.Software,
			new RecordingWriterProviderId(providerId),
			available,
			available ? null : "Synthetic provider unavailable.",
			RecordingProfileEvidenceState.Unverified,
			"Synthetic test-only evidence.");

	private sealed class TestProvider : IProgramRecordingWriterProvider
	{
		private readonly RecordingProfileDescriptor _profile;

		public TestProvider(RecordingProfileDescriptor profile) => _profile = profile;
		public RecordingWriterProviderId ProviderId => _profile.ProviderId;
		public string DisplayName => $"Test {ProviderId}";
		public IReadOnlyList<RecordingProfileDescriptor> Profiles => new[] { _profile };
		public RecordingProfileId? LastCreatedProfileId { get; private set; }
		public int CreatedCount { get; private set; }

		public IProgramRecordingPayloadWriter CreateWriter(RecordingProfileId profileId)
		{
			if (profileId != _profile.ProfileId)
				throw new RecordingOutputUnavailableException("Unknown synthetic profile.");
			LastCreatedProfileId = profileId;
			CreatedCount++;
			return new TestWriter(_profile.FileExtension);
		}
	}

	private sealed class TestWriter : IProgramRecordingPayloadWriter, IConfigurableProgramRecordingWriter
	{
		private readonly string _extension;
		public TestWriter(string extension) => _extension = extension;
		public string? FinalPath { get; private set; }

		public string ConfigureTarget(string destinationDirectory, string fileName)
		{
			var extension = Path.GetExtension(fileName);
			if (string.IsNullOrEmpty(extension))
				return fileName + _extension;
			if (!string.Equals(extension, _extension, StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException("Synthetic writer extension mismatch.", nameof(fileName));
			return fileName;
		}

		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public void StagePayload(ulong sequenceNumber, IProgramRecordingPayloadLease videoPayload, ReadOnlyMemory<byte> audioPayload) => videoPayload.Dispose();
		public void DiscardPayload(ulong sequenceNumber) { }
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
