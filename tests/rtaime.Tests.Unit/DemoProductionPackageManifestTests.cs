// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace rtaime.Tests.Unit;

public sealed class DemoProductionPackageManifestTests
{
	[Fact]
	public void Bundled_demo_package_is_complete_integrity_bound_and_showcase_scoped()
	{
		var root = FindRepositoryRoot();
		var assets = Path.Combine(root, "src", "Hosts", "rtaime.Operator", "DemoAssets");
		using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "demo-production.package.json")));
		var value = manifest.RootElement;

		Assert.Equal("rtaime.demo.production-package/1", value.GetProperty("schema").GetString());
		Assert.Equal("1.0", value.GetProperty("version").GetString());
		Assert.Equal("Input A", value.GetProperty("sources").GetProperty("program").GetString());
		Assert.Equal("Input B", value.GetProperty("sources").GetProperty("productClip").GetString());

		var product = value.GetProperty("productClip");
		Assert.True(product.GetProperty("autoPlayOnProgram").GetBoolean());
		Assert.Equal("HoldLastFrame", product.GetProperty("endBehavior").GetString());
		Assert.Equal(0, product.GetProperty("inFrame").GetInt64());
		Assert.Equal("last", product.GetProperty("outFrame").GetString());
		var cues = product.GetProperty("cuePoints").EnumerateArray().ToArray();
		Assert.Equal(2, cues.Length);
		Assert.Equal(("Product Intro", 5L), (cues[0].GetProperty("name").GetString(), cues[0].GetProperty("frame").GetInt64()));
		Assert.Equal(("Product End", 20L), (cues[1].GetProperty("name").GetString(), cues[1].GetProperty("frame").GetInt64()));

		var graphics = value.GetProperty("graphics");
		Assert.Equal("PreRenderedLowerThirdWithRtaimeLogo", graphics.GetProperty("kind").GetString());
		Assert.False(graphics.GetProperty("initialVisible").GetBoolean());
		Assert.Equal(12U, value.GetProperty("transition").GetProperty("dissolveFrames").GetUInt32());
		Assert.Equal("Input B", value.GetProperty("audio").GetProperty("source").GetString());
		Assert.Equal(1.0, value.GetProperty("audio").GetProperty("gain").GetDouble());
		Assert.False(value.GetProperty("audio").GetProperty("muted").GetBoolean());
		Assert.True(value.GetProperty("aiShowcase").GetProperty("enabled").GetBoolean());
		Assert.Equal("Person Segmentation Highlight", value.GetProperty("aiShowcase").GetProperty("feature").GetString());

		var clipBytes = DecodeBundle(Path.Combine(assets, product.GetProperty("bundleFile").GetString()!));
		Assert.Equal(product.GetProperty("sha256").GetString(), Hash(clipBytes));
		Assert.True(clipBytes.Length > 12);
		Assert.Equal("ftyp", Encoding.ASCII.GetString(clipBytes, 4, 4));

		var graphicsBytes = DecodeBundle(Path.Combine(assets, graphics.GetProperty("bundleFile").GetString()!));
		Assert.Equal(graphics.GetProperty("sha256").GetString(), Hash(graphicsBytes));
		Assert.True(graphicsBytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
	}

	private static byte[] DecodeBundle(string path)
	{
		var encoded = string.Concat(
			File.ReadLines(path)
				.Select(line => line.Trim())
				.Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)));
		return Convert.FromBase64String(encoded);
	}

	private static string Hash(byte[] bytes) =>
		Convert.ToHexStringLower(SHA256.HashData(bytes));

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new DirectoryNotFoundException("Repository root containing rtaime.slnx was not found.");
	}
}
