// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class GraphicsOverlayIntegrationTests
{
	[Fact]
	public async Task Graphics_overlay_preserves_alpha_position_scale_visibility_and_survives_dissolve()
	{
		await using var fixture = await Fixture.CreateAsync(new CollectingRecordingWriter());
		var baseline = fixture.Runtime.ProcessNextBoundary();

		var rgba = new byte[]
		{
			255, 0, 0, 255,
			0, 255, 0, 128,
			0, 0, 255, 255,
			255, 255, 0, 64
		};
		var loaded = fixture.Runtime.LoadGraphicsOverlay("logo.rgba", 2, 2, rgba);
		Assert.True(loaded.AssetLoaded);
		Assert.False(loaded.Visible);

		fixture.Runtime.SetGraphicsOverlay(true, 0.0, 0.0, 1.0);
		var visible = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(V1VisualLayerMode.Static, visible.VisualLayerMode);
		AssertPixel(visible.ProgramPixels, fixture.Format, 0, 0, 255, 0, 0, 255);
		var blended = Pixel(visible.ProgramPixels, fixture.Format, 1, 0);
		var baselineBlended = Pixel(baseline.ProgramPixels, fixture.Format, 1, 0);
		Assert.True(blended.Green > baselineBlended.Green);
		Assert.True(blended.Blue < baselineBlended.Blue);

		fixture.Runtime.SetGraphicsOverlay(false, 0.0, 0.0, 1.0);
		var hidden = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(Pixel(baseline.ProgramPixels, fixture.Format, 0, 0), Pixel(hidden.ProgramPixels, fixture.Format, 0, 0));

		fixture.Runtime.SetGraphicsOverlay(true, 0.5, 0.5, 2.0);
		var positioned = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(Pixel(baseline.ProgramPixels, fixture.Format, 0, 0), Pixel(positioned.ProgramPixels, fixture.Format, 0, 0));
		var originX = (int)Math.Round(0.5 * (fixture.Format.Width - 1));
		var originY = (int)Math.Round(0.5 * (fixture.Format.Height - 1));
		AssertPixel(positioned.ProgramPixels, fixture.Format, originX, originY, 255, 0, 0, 255);
		AssertPixel(positioned.ProgramPixels, fixture.Format, originX + 1, originY, 255, 0, 0, 255);

		Commit(fixture.Control.SelectPreview(new SelectPreviewCommand(Metadata(fixture), fixture.SourceB)), fixture);
		Commit(fixture.Control.DissolveProgram(new DissolveProgramCommand(Metadata(fixture), fixture.SourceB, 3)), fixture);

		for (var index = 0; index < 3; index++)
		{
			var dissolve = fixture.Runtime.ProcessNextBoundary();
			Assert.Equal(RuntimeProgramTransitionKind.Dissolve, dissolve.TransitionKind);
			AssertPixel(dissolve.ProgramPixels, fixture.Format, originX, originY, 255, 0, 0, 255);
		}

		Assert.True(fixture.Runtime.Snapshot.GraphicsOverlay.Visible);
		Assert.Equal(0.5, fixture.Runtime.Snapshot.GraphicsOverlay.PositionX, 6);
		Assert.Equal(0.5, fixture.Runtime.Snapshot.GraphicsOverlay.PositionY, 6);
		Assert.Equal(2.0, fixture.Runtime.Snapshot.GraphicsOverlay.Scale, 6);
	}

	[Fact]
	public async Task Bitmap_and_production_CG_share_one_ordered_runtime_layer_stack()
	{
		if (!OperatingSystem.IsWindows())
			return;

		await using var fixture = await Fixture.CreateAsync(new CollectingRecordingWriter());
		var baseline = fixture.Runtime.ProcessNextBoundary();
		fixture.Runtime.LoadGraphicsOverlay("stacked-logo.rgba", 1, 1, new byte[] { 255, 0, 0, 255 });
		fixture.Runtime.SetGraphicsOverlay(true, 0.0, 0.0, 1.0);
		var definition = LowerThird("STACKED CG");
		fixture.Runtime.ApplyProductionCgText(definition);

		var layers = fixture.Runtime.Snapshot.CompositingLayers ?? Array.Empty<V1CompositingLayerSnapshot>();
		var bitmap = Assert.Single(layers, layer => layer.LayerId == V1RuntimeHostService.BitmapGraphicsLayerId);
		var cg = Assert.Single(layers, layer => layer.LayerId == V1RuntimeHostService.ProductionCgLayerId);
		Assert.True(bitmap.Visible);
		Assert.True(cg.Visible);
		Assert.True(bitmap.Order < cg.Order);

		var program = fixture.Runtime.ProcessNextBoundary();
		AssertPixel(program.ProgramPixels, fixture.Format, 0, 0, 255, 0, 0, 255);
		var originX = (int)Math.Round(definition.PositionX * (fixture.Format.Width - 1));
		var originY = (int)Math.Round(definition.PositionY * (fixture.Format.Height - 1)) - (int)definition.BoxHeight;
		var sampleX = originX + checked((int)(definition.BoxWidth / 2));
		var sampleY = originY + checked((int)(definition.BoxHeight / 2));
		Assert.NotEqual(
			Pixel(baseline.ProgramPixels, fixture.Format, sampleX, sampleY),
			Pixel(program.ProgramPixels, fixture.Format, sampleX, sampleY));

		var updated = fixture.Runtime.SetCompositingLayerState(
			V1RuntimeHostService.BitmapGraphicsLayerId,
			visible: true,
			opacity: 128);
		Assert.Equal(128, Assert.Single(updated, layer => layer.LayerId == V1RuntimeHostService.BitmapGraphicsLayerId).Opacity);

		var orderedIds = updated
			.OrderBy(layer => layer.Order)
			.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
			.Select(layer => layer.LayerId)
			.Reverse()
			.ToArray();
		var reordered = fixture.Runtime.ReorderCompositingLayers(orderedIds);
		Assert.Equal(orderedIds, reordered.Select(layer => layer.LayerId));

		var hiddenCg = fixture.Runtime.SetCompositingLayerState(
			V1RuntimeHostService.ProductionCgLayerId,
			visible: false,
			opacity: byte.MaxValue);
		Assert.False(Assert.Single(hiddenCg, layer => layer.LayerId == V1RuntimeHostService.ProductionCgLayerId).Visible);
	}

	[Fact]
	public async Task Production_CG_text_is_runtime_rendered_composited_and_cached()
	{
		if (!OperatingSystem.IsWindows())
			return;

		await using var fixture = await Fixture.CreateAsync(new CollectingRecordingWriter());
		var baseline = fixture.Runtime.ProcessNextBoundary();
		var definition = LowerThird("RTAIME PRODUCTION CG");

		var first = fixture.Runtime.ApplyProductionCgText(definition);
		Assert.True(first.AssetLoaded);
		Assert.True(first.Visible);
		Assert.Equal("production-cg-text", first.AssetName);
		Assert.NotNull(fixture.Runtime.Snapshot.ProductionCgText);
		Assert.True(fixture.Runtime.Snapshot.ProductionCgText!.Active);
		Assert.False(fixture.Runtime.Snapshot.ProductionCgText.CacheHit);
		Assert.Equal(1, fixture.Runtime.ProductionCgCachedSurfaceCount);

		var program = fixture.Runtime.ProcessNextBoundary();
		var originX = (int)Math.Round(definition.PositionX * (fixture.Format.Width - 1));
		var originY = (int)Math.Round(definition.PositionY * (fixture.Format.Height - 1)) - (int)definition.BoxHeight;
		var sampleX = originX + checked((int)(definition.BoxWidth / 2));
		var sampleY = originY + checked((int)(definition.BoxHeight / 2));
		Assert.NotEqual(
			Pixel(baseline.ProgramPixels, fixture.Format, sampleX, sampleY),
			Pixel(program.ProgramPixels, fixture.Format, sampleX, sampleY));

		fixture.Runtime.ApplyProductionCgText(definition);
		Assert.True(fixture.Runtime.Snapshot.ProductionCgText!.CacheHit);
		Assert.Equal(1, fixture.Runtime.ProductionCgCachedSurfaceCount);

		var overlay = fixture.Runtime.Snapshot.GraphicsOverlay;
		fixture.Runtime.SetGraphicsOverlay(false, overlay.PositionX, overlay.PositionY, overlay.Scale);
		var hidden = fixture.Runtime.ProcessNextBoundary();
		Assert.False(fixture.Runtime.Snapshot.ProductionCgText!.Visible);
		Assert.Equal(
			Pixel(baseline.ProgramPixels, fixture.Format, sampleX, sampleY),
			Pixel(hidden.ProgramPixels, fixture.Format, sampleX, sampleY));
	}

	[Fact]
	public async Task Production_CG_font_resolution_uses_only_explicit_fallback_and_fails_closed()
	{
		if (!OperatingSystem.IsWindows())
			return;

		await using var fixture = await Fixture.CreateAsync(new CollectingRecordingWriter());
		var fallbackDefinition = LowerThird("EXPLICIT FALLBACK") with
		{
			Typeface = "rtaime-font-that-does-not-exist",
			FallbackTypeface = "Arial"
		};

		fixture.Runtime.ApplyProductionCgText(fallbackDefinition);
		Assert.Equal("Arial", fixture.Runtime.Snapshot.ProductionCgText!.ResolvedTypeface, ignoreCase: true);

		var before = fixture.Runtime.Snapshot.GraphicsOverlay;
		var missingDefinition = fallbackDefinition with
		{
			Text = "MISSING FONT",
			FallbackTypeface = "rtaime-fallback-that-does-not-exist"
		};
		var exception = Assert.Throws<InvalidOperationException>(() => fixture.Runtime.ApplyProductionCgText(missingDefinition));
		Assert.Contains("unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(before, fixture.Runtime.Snapshot.GraphicsOverlay);
		Assert.Equal("EXPLICIT FALLBACK", fixture.Runtime.Snapshot.ProductionCgText!.Text);
	}

	[Fact]
	public async Task Production_CG_surface_cache_is_bounded_under_repeated_text_updates()
	{
		if (!OperatingSystem.IsWindows())
			return;

		await using var fixture = await Fixture.CreateAsync(new CollectingRecordingWriter());
		for (var index = 0; index < 24; index++)
			fixture.Runtime.ApplyProductionCgText(LowerThird($"CACHE ENTRY {index:00}"));

		Assert.InRange(fixture.Runtime.ProductionCgCachedSurfaceCount, 1, 16);
		Assert.Equal("CACHE ENTRY 23", fixture.Runtime.Snapshot.ProductionCgText!.Text);
	}

	[Fact]
	public async Task Recording_payload_uses_the_same_post_graphics_program_pixels()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-graphics-recording", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var writer = new ReferenceRecordingPayloadWriter(root);
			await using var fixture = await Fixture.CreateAsync(writer);
			fixture.Runtime.LoadGraphicsOverlay("record-logo.rgba", 1, 1, new byte[] { 255, 0, 0, 255 });
			fixture.Runtime.SetGraphicsOverlay(true, 0.0, 0.0, 1.0);

			var outputId = RecordingOutputId.New();
			var start = await fixture.Runtime.StartRecordingAsync(RecordingSessionId.New(), outputId);
			Assert.True(start.Succeeded, start.Failure?.ToString());

			var program = fixture.Runtime.ProcessNextBoundary();
			AssertPixel(program.ProgramPixels, fixture.Format, 0, 0, 255, 0, 0, 255);

			var stop = await fixture.Runtime.StopRecordingAsync();
			Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
			Assert.NotNull(writer.FinalPath);

			var artifact = ReferenceRecordingPayloadReader.Read(writer.FinalPath!);
			var sample = Assert.Single(artifact.Samples);
			Assert.Equal(program.ProgramPixels, sample.VideoPayload);
			AssertPixel(sample.VideoPayload, fixture.Format, 0, 0, 255, 0, 0, 255);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	private static V1ProductionCgTextDefinition LowerThird(string text) => new(
		text,
		"Segoe UI",
		"Arial",
		48,
		new V1CgColor(255, 255, 255, 255),
		0.05,
		0.90,
		640,
		120,
		V1CgTextAlignment.Left,
		V1CgAnchor.BottomLeft,
		new V1CgPanelStyle(true, new V1CgColor(18, 23, 32, 224), 12, 24),
		true,
		V1CgLayer.ProgramGraphics,
		0);

	private static ControlCommandMetadata Metadata(Fixture fixture) =>
		new(
			ControlContractVersion.Current,
			CommandId.New(),
			fixture.Control.State.ProductionId,
			fixture.Control.State.Revision);

	private static void Commit(ControlHostOperationResult staged, Fixture fixture)
	{
		Assert.True(staged.Accepted, staged.Failure?.ToString());
		Assert.NotNull(staged.Execution);
		var execution = staged.Execution!;
		var applied = fixture.Runtime.ApplyExecution(
			execution.PreparedExecution,
			execution.ProgramSinkId,
			execution.ProgramTransition);
		Assert.True(applied.Committed, applied.Commit?.Failure?.ToString() ?? applied.Prepare.Failure?.ToString());
		Assert.NotNull(applied.Commit);
		var confirmed = fixture.Control.ConfirmRuntimeCommit(
			execution.PreparedExecution.PreparedExecutionId,
			applied.Commit!);
		Assert.True(confirmed.Committed, confirmed.Failure?.ToString());
	}

	private static PixelValue Pixel(byte[] pixels, VideoFormat format, int x, int y)
	{
		var offset = checked((y * (int)format.Width + x) * 4);
		return new PixelValue(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
	}

	private static void AssertPixel(
		byte[] pixels,
		VideoFormat format,
		int x,
		int y,
		byte red,
		byte green,
		byte blue,
		byte alpha) =>
		Assert.Equal(new PixelValue(red, green, blue, alpha), Pixel(pixels, format, x, y));

	private readonly record struct PixelValue(byte Red, byte Green, byte Blue, byte Alpha);

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(
			ProductionSourceId sourceB,
			VideoFormat format,
			BoundedProductionJournal journal,
			ControlHostService control,
			V1RuntimeHostService runtime)
		{
			SourceB = sourceB;
			Format = format;
			Journal = journal;
			Control = control;
			Runtime = runtime;
		}

		public ProductionSourceId SourceB { get; }
		public VideoFormat Format { get; }
		public BoundedProductionJournal Journal { get; }
		public ControlHostService Control { get; }
		public V1RuntimeHostService Runtime { get; }

		public static ValueTask<Fixture> CreateAsync(IProgramRecordingWriter writer)
		{
			var format = VideoFormat.Hd1080p50Rgba8;
			var sourceA = new ProductionSourceId(Identity.Parse("7e000000-0000-0000-0000-00000000000a"));
			var sourceB = new ProductionSourceId(Identity.Parse("7e000000-0000-0000-0000-00000000000b"));
			var specification = new ProductionSpecification(
				ControlContractVersion.Current,
				new ProductionId(Identity.Parse("7e000000-0000-0000-0000-000000000001")),
				"AP-49 Graphics Overlay",
				new[]
				{
					new ProductionSourceSpecification(sourceA, "Input 1"),
					new ProductionSourceSpecification(sourceB, "Input 2")
				},
				new ProductionRoutingState(sourceA, sourceA));

			var runtime = new V1RuntimeHostService(
				new MediaSourceId(sourceA.Value),
				new MediaSourceId(sourceB.Value),
				format,
				writer);
			var journal = new BoundedProductionJournal(64);
			var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
			var fixture = new Fixture(sourceB, format, journal, control, runtime);
			Commit(control.Initialize(), fixture);
			return ValueTask.FromResult(fixture);
		}

		public async ValueTask DisposeAsync()
		{
			await Runtime.DisposeAsync();
			await Journal.DisposeAsync();
		}
	}

	private sealed class CollectingRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
