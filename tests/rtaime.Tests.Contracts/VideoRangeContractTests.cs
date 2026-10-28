// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class VideoRangeContractTests
{
	[Theory]
	[InlineData(8, 16, 235, 5, 246, 1, 254)]
	[InlineData(10, 64, 940, 20, 984, 4, 1019)]
	public void R103_code_band_profile_is_single_shared_definition(
		int depth, int nominalMin, int nominalMax, int preferredMin, int preferredMax, int totalMin, int totalMax)
	{
		Assert.True(VideoRangeLimits.TryGet(depth, out var limits));
		Assert.Equal(new VideoRangeLimits(nominalMin, nominalMax, preferredMin, preferredMax, totalMin, totalMax), limits);
		Assert.False(VideoRangeLimits.TryGet(12, out _));
	}

	[Fact]
	public void Untrusted_or_incompatible_metadata_remains_unverified_and_native_depth_mismatch_unavailable()
	{
		var correct = new ColorDescription(
			ColorPrimaries.Rec709, ColorTransfer.Rec709, ColorMatrix.IdentityRgb,
			NominalRange.Limited, 8, AlphaMode.Opaque, ColorMetadataAuthority.Authoritative);
		var profile = VideoRangeProfile.ForRgba8(correct, 8, 4);
		Assert.Null(profile.UnverifiedReason);
		Assert.Equal(32, profile.ActiveImage.PixelCount);
		Assert.Contains("UNVERIFIED", (profile with
		{
			Color = new ColorDescription(ColorPrimaries.Rec709, ColorTransfer.Rec709, ColorMatrix.IdentityRgb,
				NominalRange.Full, 8, AlphaMode.Opaque, ColorMetadataAuthority.Authoritative)
		}).UnverifiedReason, StringComparison.Ordinal);
		Assert.Contains("UNAVAILABLE", (profile with
		{
			Color = new ColorDescription(ColorPrimaries.Rec709, ColorTransfer.Rec709, ColorMatrix.IdentityRgb,
				NominalRange.Limited, 10, AlphaMode.Opaque, ColorMetadataAuthority.Authoritative)
		}).UnverifiedReason, StringComparison.Ordinal);
	}

	[Fact]
	public void Active_source_area_is_bounded_to_parent_raster()
	{
		new VideoActiveImageRegion(1, 1, 2, 2).Validate(4, 4);
		Assert.Throws<ArgumentOutOfRangeException>(() => new VideoActiveImageRegion(3, 3, 2, 2).Validate(4, 4));
		Assert.Throws<ArgumentOutOfRangeException>(() => new VideoActiveImageRegion(0, 0, 0, 4).Validate(4, 4));
	}
}
