// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfOp1aRecordingCatalogTests
{
    [Fact]
    public void Precise_mxf_profile_is_unavailable_until_independently_qualified()
    {
        var profile = ProfessionalRecordingFormats.MxfOp1aUncompressedPcm;
        Assert.Equal("mxf-op1a-uncompressed-pcm", profile.ProfileId.Value);
        Assert.Equal(".mxf", profile.FileExtension);
        Assert.Equal(ProfessionalRecordingFormats.ManagedMxfOp1aProviderId, profile.ProviderId);
        Assert.False(profile.Available);
        Assert.Equal(RecordingProfileEvidenceState.Unverified, profile.EvidenceState);
        Assert.Equal(RecordingAccelerationClass.Software, profile.AccelerationClass);
        Assert.Equal(AudioFormat.Stereo48kFloat32, profile.RequiredInputAudioFormat);
        Assert.Equal(
            new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
            profile.SupportedInputVideoFormats);
        Assert.NotNull(profile.UnavailableReason);
    }

    [Fact]
    public void Catalog_rejects_mxf_selection_before_writer_creation()
    {
        var registry = new RecordingWriterProviderRegistry(
            [new ManagedQuickTimeRecordingWriterProvider(Path.GetTempPath()),
             new ManagedMxfOp1aRecordingWriterProvider()],
            ProfessionalRecordingFormats.Mov2VuyPcmProfileId);
        Assert.True(registry.ProfileCatalog.TryGet(
            ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId, out var profile));
        Assert.False(profile.Available);
        Assert.Throws<RecordingOutputUnavailableException>(() =>
            registry.Resolve(ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId));
        Assert.Throws<RecordingOutputUnavailableException>(() =>
            registry.CreateWriter(ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId));
    }

    [Fact]
    public void Mxf_provider_does_not_create_unverified_writer()
    {
        var provider = new ManagedMxfOp1aRecordingWriterProvider();
        Assert.Throws<RecordingOutputUnavailableException>(() =>
            provider.CreateWriter(ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId));
    }
}
