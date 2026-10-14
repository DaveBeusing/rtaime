// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaLibraryBrowsingTests
{
	private static MediaSmartBin Bin(MediaDurationFilter duration = MediaDurationFilter.Any,
		MediaReadinessFilter readiness = MediaReadinessFilter.Any) => new("Test", "", "All", "All", duration, readiness);
	private static MediaPoolItemViewModel Item(double? seconds = null, bool online = true, bool ready = true, string key = "clip:test") =>
		new(key, "Clips", MediaPoolItemKind.Clip, "CLIP.mp4", "display text is not metadata", "H264", online ? "ONLINE" : "MISSING",
			key, null, online, ready, @"Z:\media\clip.mp4", seconds is { } value ? TimeSpan.FromSeconds(value) : null);

	[Theory]
	[InlineData(59.999, MediaDurationFilter.UnderOneMinute)]
	[InlineData(60, MediaDurationFilter.OneToTenMinutes)]
	[InlineData(599.999, MediaDurationFilter.OneToTenMinutes)]
	[InlineData(600, MediaDurationFilter.TenMinutesOrLonger)]
	public void Duration_boundaries_use_typed_metadata_and_never_display_text(double seconds, MediaDurationFilter expected)
	{
		foreach (var duration in Enum.GetValues<MediaDurationFilter>())
			Assert.Equal(duration == MediaDurationFilter.Any || duration == expected, MediaSmartBinPolicy.Matches(Item(seconds), Bin(duration)));
		Assert.False(MediaSmartBinPolicy.Matches(Item(), Bin(expected)));
		Assert.True(MediaSmartBinPolicy.Matches(Item(), Bin(MediaDurationFilter.Unknown)));
	}

	[Fact]
	public void Availability_readiness_and_search_are_independent_and_deterministic()
	{
		var offline = Item(12, online: false, ready: false);
		Assert.True(MediaSmartBinPolicy.Matches(offline, Bin(readiness: MediaReadinessFilter.NotReady) with { Filter = "Offline", Search = "z:\\MEDIA" }));
		Assert.False(MediaSmartBinPolicy.Matches(offline, Bin(readiness: MediaReadinessFilter.Ready)));
		Assert.False(MediaSmartBinPolicy.Matches(offline, Bin() with { Category = "Audio" }));
		Assert.False(MediaSmartBinPolicy.Matches(offline, Bin() with { Filter = "Online" }));
		Assert.False(MediaSmartBinPolicy.Matches(offline, Bin() with { Search = new string('x', 257) }));
		Assert.Contains("MISSING", offline.ReadinessLabel);
		Assert.Contains("NOT READY", Item(online: true, ready: false).ReadinessLabel);
	}

	[Fact]
	public async Task Preferences_roundtrip_are_bounded_and_reject_malformed_future_or_oversized_documents()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-bins-" + Guid.NewGuid().ToString("N"));
		var path = Path.Combine(root, "bins.json");
		var store = new MediaSmartBinStore(path);
		try
		{
			Assert.Empty(await store.LoadAsync());
			await store.SaveAsync(Enumerable.Range(0, 40).Select(i => Bin() with { Name = "Bin " + i }));
			var restored = await store.LoadAsync();
			Assert.Equal(16, restored.Count);
			Assert.Equal(Bin() with { Name = "Bin 0" }, restored[0]);
			Assert.Single(Directory.GetFiles(root));
			await File.WriteAllTextAsync(path, "{bad");
			Assert.Empty(await store.LoadAsync());
			await File.WriteAllTextAsync(path, "{\"Version\":99,\"Bins\":[]}");
			Assert.Empty(await store.LoadAsync());
			await File.WriteAllTextAsync(path, new string('x', 65537));
			Assert.Empty(await store.LoadAsync());
			await store.SaveAsync([Bin(), Bin(), Bin() with { Name = "Invalid", Search = new string('x', 257) }]);
			Assert.Single(await store.LoadAsync());
			using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([], cancelled.Token));
			Assert.Single(await store.LoadAsync());
		}
		finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Rapid_hover_keeps_only_latest_and_cancel_or_dispose_cannot_publish_stale_images()
	{
		using var hover = new MediaLibraryHoverPreview();
		var requests = Enumerable.Range(0, 1000).Select(index => hover.RequestAsync(Item(key: "clip:" + index))).ToArray();
		var result = await Task.WhenAll(requests);
		Assert.All(result[..^1], preview => Assert.Null(preview));
		Assert.Equal("clip:999", result[^1]!.Key);
		Assert.Null(result[^1]!.Thumbnail);
		Assert.Contains("UNSUPPORTED", result[^1]!.Status);
		var pending = hover.RequestAsync(Item()); hover.Cancel();
		Assert.Null(await pending);
		pending = hover.RequestAsync(Item()); hover.Dispose();
		Assert.Null(await pending);
		Assert.Null(await hover.RequestAsync(Item()));
	}

	[Fact]
	public async Task Offline_and_existing_thumbnail_capabilities_never_invent_scrub_support()
	{
		var image = new DrawingImage(); image.Freeze();
		var item = Item() with { Thumbnail = image };
		Assert.Equal(MediaHoverCapability.ExistingThumbnail, MediaLibraryHoverPreview.Capability(item));
		using var hover = new MediaLibraryHoverPreview();
		var preview = await hover.RequestAsync(item);
		Assert.Same(image, preview!.Thumbnail);
		Assert.Contains("scrubbing unsupported", preview.Status);
		preview = await hover.RequestAsync(item with { IsOnline = false });
		Assert.Null(preview!.Thumbnail); Assert.Contains("OFFLINE", preview.Status);
	}

	[Fact]
	public async Task Large_catalogue_search_saved_filters_and_bulk_selection_have_no_production_transport_calls()
	{
		var transport = new ForbiddenTransport();
		var client = new OperatorControlClient(transport);
		await using var owner = new OperatorViewModel();
		await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
		var catalog = new Catalog();
		using var pool = new MediaPoolInspectorViewModel(owner, deck, catalog);
		var path = Path.Combine(Path.GetTempPath(), "rtaime-bin-" + Guid.NewGuid().ToString("N") + ".json");
		try
		{
			await pool.LoadCatalogAsync();
			Assert.InRange(pool.FilteredItems.Count, 4000, 4096);
			Assert.All(pool.FilteredItems.Where(i => i.Kind == MediaPoolItemKind.Clip), item => Assert.NotNull(item.KnownDuration));
			var program = owner.ProgramSourceId;
			var preview = owner.PreviewSourceId;
			var revision = catalog.Snapshot.Revision;
			await pool.LoadSmartBinsAsync(new MediaSmartBinStore(path));
			pool.SelectedCategory = "Clips"; pool.DurationFilter = MediaDurationFilter.UnderOneMinute;
			pool.ReadinessFilter = MediaReadinessFilter.Ready; pool.SearchText = "clip00012";
			Assert.Single(pool.FilteredItems);
			pool.SaveSmartBinCommand.Execute(null); await pool.FlushSmartBinsAsync();
			Assert.Single(pool.SmartBins);
			Assert.True(pool.DeleteSmartBinCommand.CanExecute(null));
			var saved = pool.SmartBins[0];
			pool.SearchText = "missing-query"; Assert.Empty(pool.FilteredItems); Assert.Null(pool.SelectedSmartBin);
			pool.SelectedSmartBin = saved; Assert.Single(pool.FilteredItems);
			pool.SearchText = ""; pool.DurationFilter = MediaDurationFilter.Any; pool.ReadinessFilter = MediaReadinessFilter.Any;
			for (var index = 0; index < 100; index++)
			{
				pool.SearchText = index % 2 == 0 ? "clip000" : "";
				Assert.InRange(pool.FilteredItems.Count, 1, 4096);
			}
			pool.UpdateSelection(pool.FilteredItems.Take(3)); Assert.True(pool.HasMultipleSelection);
			pool.SearchText = "no-matches"; Assert.Empty(pool.SelectedItems);
			pool.SearchText = "";
			var hover = pool.BeginHoverAsync(pool.FilteredItems[0]);
			for (var index = 0; index < 5; index++) { pool.Refresh(); await Task.Delay(10); }
			await hover;
			Assert.Contains("UNSUPPORTED", pool.HoverStatus);
			pool.CancelHover();
			Assert.Null(pool.HoverThumbnail);
			Assert.Equal(program, owner.ProgramSourceId); Assert.Equal(preview, owner.PreviewSourceId);
			Assert.Equal(revision, catalog.Snapshot.Revision); Assert.Equal(0, catalog.Mutations);
			Assert.Equal(0, transport.Calls);
		}
		finally { if (File.Exists(path)) File.Delete(path); }
	}

	[Fact]
	public async Task Monitoring_frame_arrivals_update_shared_images_without_resetting_library_or_starving_hover()
	{
		var transport = new ForbiddenTransport();
		var client = new OperatorControlClient(transport);
		await using var owner = new OperatorViewModel();
		await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
		using var pool = new MediaPoolInspectorViewModel(owner, deck);
		var source = new OperatorSourceTileViewModel(new OperatorSourceDescriptor("source-monitor", "Camera"));
		owner.Sources.Add(source);
		var item = Assert.Single(pool.FilteredItems.Where(i => i.Kind == MediaPoolItemKind.Source));
		var resets = 0;
		pool.FilteredItems.CollectionChanged += (_, _) => resets++;
		var hover = pool.BeginHoverAsync(item);
		for (var index = 0; index < 25; index++)
		{
			var image = new DrawingImage(); image.Freeze();
			source.ApplyThumbnail(image, "monitor " + index);
			Assert.Same(image, item.Thumbnail);
			await Task.Delay(10);
		}
		await hover;
		Assert.Equal(0, resets);
		Assert.NotNull(pool.HoverThumbnail);
		Assert.Contains("MONITOR THUMBNAIL", pool.HoverStatus);
		Assert.Same(item, pool.FilteredItems.First(i => i.Key == item.Key));
		Assert.Equal(0, transport.Calls);
	}

	internal static async Task VerifyWindowAsync(MainWindow owner, Dispatcher dispatcher)
	{
		owner.Shell.SelectWorkspace("MEDIA");
		if (owner.Shell.IsLeftCollapsed) owner.Shell.ToggleLeftPanelCommand.Execute(null);
		await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
		var combo = Assert.IsType<RtaimeComboBox>(owner.FindName("MediaSmartBins"));
		Assert.True(combo.IsVisible); Assert.True(combo.Focus());
		Assert.Equal("Saved Smart Bin", AutomationProperties.GetName(combo));
		var grid = Assert.IsType<RtaimeListBox>(owner.FindName("MediaAssetGrid"));
		var list = Assert.IsType<RtaimeListBox>(owner.FindName("MediaAssetList"));
		var preview = ((OperatorViewModel)owner.DataContext).PreviewSourceId;
		var program = ((OperatorViewModel)owner.DataContext).ProgramSourceId;
		var items = Enumerable.Range(0, 4096).Select(index => Item(index, key: "clip:" + index)).ToArray();
		owner.MediaPool.FilteredItems.ReplaceWith(items);
		try
		{
			foreach (var browser in new[] { grid, list })
			{
				if (browser == grid) owner.MediaPool.GridViewCommand.Execute(null);
				else owner.MediaPool.ListViewCommand.Execute(null);
				await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
				Assert.True(browser.IsVisible);
				browser.ScrollIntoView(items[^1]);
				await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
				Assert.InRange(Descendants(browser).OfType<RtaimeListBoxItem>().Count(), 1, 160);
				Assert.True(VirtualizingPanel.GetIsVirtualizing(browser));
				Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(browser));
			}
			await owner.MediaPool.BeginHoverAsync(items[0]);
			Assert.Contains("UNSUPPORTED", owner.MediaPool.HoverStatus);
			owner.MediaPool.CancelHover();
			Assert.Equal(preview, ((OperatorViewModel)owner.DataContext).PreviewSourceId);
			Assert.Equal(program, ((OperatorViewModel)owner.DataContext).ProgramSourceId);
			Console.WriteLine("MEDIA LIBRARY PASS: real custom controls, accessible keyboard focus, 4096-item grid/list recycling and unchanged production identities.");
		}
		finally { owner.MediaPool.Refresh(); owner.MediaPool.GridViewCommand.Execute(null); }
	}
	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i); yield return child;
			foreach (var descendant in Descendants(child)) yield return descendant;
		}
	}
	private sealed class Catalog : IMediaAssetCatalogClient
	{
		public int Mutations;
		public MediaAssetCatalogSnapshot Snapshot { get; } = new(MediaContractVersion.Current, 7,
			Enumerable.Range(0, 30000).Select(i =>
			{
				var timestamp = new UtcTimestamp(DateTimeOffset.UnixEpoch);
				return new MediaAssetDescriptor(new MediaAssetId(Identity.Parse($"55000000-0000-0000-0000-{i:000000000000}")),
					MediaAssetOriginKind.LocalFile, $@"Z:\media\clip{i:00000}.mp4", $"clip{i:00000}.mp4",
					MediaContainerFormat.Mp4, MediaVideoCodec.H264, MediaAudioCodec.Aac,
					VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32, TimeSpan.FromSeconds(i + 1),
					1024, i.ToString("x64"), timestamp, timestamp, MediaAssetAvailability.Online);
			}).ToArray());
		public ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
		public ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(MediaAssetId id, string path, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
		public ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(MediaAssetId id, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
	}
	private sealed class ForbiddenTransport : IOperatorControlTransport
	{
		public int Calls;
		private ValueTask<T> Forbidden<T>() { Calls++; return ValueTask.FromException<T>(new InvalidOperationException("Browsing must not call production transport.")); }
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Forbidden<OperatorStatusSnapshot>();
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => Forbidden<OperatorMutationResponse>();
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) => Forbidden<OperatorMutationResponse>();
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => Forbidden<OperatorMutationResponse>();
	}
}
