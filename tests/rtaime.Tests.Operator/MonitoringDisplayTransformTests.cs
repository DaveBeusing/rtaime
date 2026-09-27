// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Operator.Tests;

public sealed class MonitoringDisplayTransformTests
{
	[Fact]
	public void Unknown_metadata_is_passthrough_except_rgba_to_bgra_channel_order()
	{
		var source = new byte[] { 10, 20, 30, 255 };
		var target = new byte[4];

		MonitoringDisplayTransform.ConvertRgbaToBgra(source, target, ColorDescription.UnknownRgba8);

		Assert.Equal(new byte[] { 30, 20, 10, 255 }, target);
		Assert.Contains("PASSTHROUGH", MonitoringDisplayTransform.Describe(ColorDescription.UnknownRgba8));
	}

	[Fact]
	public void Full_range_srgb_is_identity_except_channel_order()
	{
		var source = new byte[] { 64, 128, 192, 255 };
		var target = new byte[4];

		MonitoringDisplayTransform.ConvertRgbaToBgra(source, target, ColorDescription.SrgbFullRgba8);

		Assert.Equal(new byte[] { 192, 128, 64, 255 }, target);
	}

	[Fact]
	public void Limited_range_rec709_maps_reference_black_and_white_to_display_endpoints()
	{
		var color = new ColorDescription(
			ColorPrimaries.Rec709,
			ColorTransfer.Rec709,
			ColorMatrix.IdentityRgb,
			NominalRange.Limited,
			8,
			AlphaMode.Straight,
			ColorMetadataAuthority.Authoritative);
		var source = new byte[] { 16, 16, 16, 255, 235, 235, 235, 255 };
		var target = new byte[8];

		MonitoringDisplayTransform.ConvertRgbaToBgra(source, target, color);

		Assert.Equal((byte)0, target[0]);
		Assert.Equal((byte)0, target[1]);
		Assert.Equal((byte)0, target[2]);
		Assert.Equal((byte)255, target[4]);
		Assert.Equal((byte)255, target[5]);
		Assert.Equal((byte)255, target[6]);
	}
}
