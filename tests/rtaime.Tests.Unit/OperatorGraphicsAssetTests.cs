// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class OperatorGraphicsAssetTests
{
	[Fact]
	public void Graphics_asset_requires_bounded_exact_rgba_payload()
	{
		var valid = new OperatorGraphicsAsset(
			"logo.rgba",
			2,
			1,
			new byte[] { 255, 0, 0, 255, 0, 255, 0, 128 });

		Assert.Equal("logo.rgba", valid.Name);
		Assert.Equal(2U, valid.Width);
		Assert.Equal(1U, valid.Height);
		Assert.Equal(8, valid.RgbaPixels.Length);

		Assert.Throws<ArgumentException>(() =>
			new OperatorGraphicsAsset("logo.rgba", 2, 1, new byte[7]));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new OperatorGraphicsAsset("logo.rgba", 513, 1, new byte[513 * 4]));
		Assert.Throws<ArgumentException>(() =>
			new OperatorGraphicsAsset(" ", 1, 1, new byte[4]));
	}
}
