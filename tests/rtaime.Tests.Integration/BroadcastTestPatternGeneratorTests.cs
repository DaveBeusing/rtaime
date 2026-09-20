// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Integration;

public sealed class BroadcastTestPatternGeneratorTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Generator_produces_exact_deterministic_payload_for_supported_formats(bool use5994)
	{
		var format = use5994
			? VideoFormat.Hd1080p59_94Rgba8
			: VideoFormat.Hd1080p50Rgba8;
		var first = new BroadcastTestPatternGenerator(new BroadcastTestPatternConfiguration(format));
		var second = new BroadcastTestPatternGenerator(new BroadcastTestPatternConfiguration(format));

		Assert.Equal(BroadcastTestPatternGenerator.RequiredByteLength(format), first.Pixels.Length);
		Assert.Equal(first.Pixels.Span.ToArray(), second.Pixels.Span.ToArray());
		Assert.Equal(
			SHA256.HashData(first.Pixels.Span),
			SHA256.HashData(second.Pixels.Span));
	}

	[Fact]
	public void Generator_exposes_reference_color_and_near_black_regions()
	{
		var format = VideoFormat.Hd1080p50Rgba8;
		var generator = new BroadcastTestPatternGenerator(new BroadcastTestPatternConfiguration(format));

		var whiteBar = generator.GetPixel(40, 100);
		Assert.Equal((byte)235, whiteBar.Red);
		Assert.Equal((byte)235, whiteBar.Green);
		Assert.Equal((byte)235, whiteBar.Blue);
		Assert.Equal((byte)255, whiteBar.Alpha);

		var cyanBar = generator.GetPixel(672, 100);
		Assert.Equal((byte)16, cyanBar.Red);
		Assert.Equal((byte)235, cyanBar.Green);
		Assert.Equal((byte)235, cyanBar.Blue);

		var redRamp = generator.GetPixel(961, 700);
		Assert.InRange(redRamp.Red, (byte)127, (byte)129);
		Assert.Equal((byte)0, redRamp.Green);
		Assert.Equal((byte)0, redRamp.Blue);

		var greenRamp = generator.GetPixel(961, 735);
		Assert.Equal((byte)0, greenRamp.Red);
		Assert.InRange(greenRamp.Green, (byte)127, (byte)129);
		Assert.Equal((byte)0, greenRamp.Blue);

		var blueRamp = generator.GetPixel(961, 770);
		Assert.Equal((byte)0, blueRamp.Red);
		Assert.Equal((byte)0, blueRamp.Green);
		Assert.InRange(blueRamp.Blue, (byte)127, (byte)129);

		var nearBlack = generator.GetPixel(384, 820);
		Assert.Equal((byte)16, nearBlack.Red);
		Assert.Equal((byte)16, nearBlack.Green);
		Assert.Equal((byte)16, nearBlack.Blue);
	}

	[Fact]
	public void Generated_source_reuses_static_pixels_and_preserves_normal_frame_timing()
	{
		var format = VideoFormat.Hd1080p50Rgba8;
		var sourceA = new MediaSourceId(Identity.Parse("c1000000-0000-0000-0000-000000000001"));
		var sourceB = new MediaSourceId(Identity.Parse("c1000000-0000-0000-0000-000000000002"));
		var provider = new VirtualMediaReferenceProvider(sourceA, sourceB, format);
		var pattern = new BroadcastTestPatternGenerator(new BroadcastTestPatternConfiguration(format));
		var generated = new VirtualGeneratedVideoSource(provider.SourceA, pattern.Pixels);

		var first = generated.GenerateFrame(41);
		var second = generated.GenerateFrame(42);

		Assert.Equal(sourceA, first.Descriptor.SourceId);
		Assert.Equal(format, first.Descriptor.Surface.Format);
		Assert.Equal((ulong)41, first.Descriptor.Timing.SequenceNumber);
		Assert.Equal((ulong)42, second.Descriptor.Timing.SequenceNumber);
		Assert.Equal(new Timebase(1, 50), first.Descriptor.Timing.Timebase);
		Assert.True(first.Pixels.Equals(second.Pixels));
		Assert.Equal(pattern.Pixels.Span.ToArray(), first.Pixels.Span.ToArray());
	}

	[Fact]
	public void Configuration_rejects_blank_color_space_label()
	{
		Assert.Throws<ArgumentException>(() =>
			new BroadcastTestPatternConfiguration(VideoFormat.Hd1080p50Rgba8, colorSpace: " "));
	}
}
