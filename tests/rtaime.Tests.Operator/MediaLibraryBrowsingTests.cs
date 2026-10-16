// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
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
			var blocked = Path.Combine(root, "blocked");
			Directory.CreateDirectory(blocked);
			var blockedStore = new MediaSmartBinStore(blocked);
			for (var index = 0; index < 2; index++)
			{
				var failure = await Record.ExceptionAsync(() => blockedStore.SaveAsync([Bin()]).WaitAsync(TimeSpan.FromSeconds(2)));
				Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString() ?? "Expected a bounded write failure.");
				Assert.Empty(await blockedStore.LoadAsync().WaitAsync(TimeSpan.FromSeconds(2)));
			}
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
			pool.SelectedSmartBin = saved;
			pool.DeleteSmartBinCommand.Execute(null); await pool.FlushSmartBinsAsync();
			Assert.Empty(pool.SmartBins);
			Assert.Empty(await new MediaSmartBinStore(path).LoadAsync());
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
		var item = Assert.Single(pool.FilteredItems, i => i.Kind == MediaPoolItemKind.Source);
		var hash = item.GetHashCode();
		item.PropertyChanged += (_, _) => { };
		Assert.Equal(hash, item.GetHashCode());
		var resets = 0;
		pool.FilteredItems.CollectionChanged += (_, _) => resets++;
		var hover = pool.BeginHoverAsync(item);
		for (var index = 0; index < 25; index++)
		{
			var image = new DrawingImage(); image.Freeze();
			source.ApplyThumbnail(image, "monitor " + index);
			Assert.Same(image, item.Thumbnail);
			Assert.Equal(hash, item.GetHashCode());
			await Task.Delay(10);
		}
		await hover;
		Assert.Equal(0, resets);
		Assert.NotNull(pool.HoverThumbnail);
		Assert.Contains("MONITOR THUMBNAIL", pool.HoverStatus);
		Assert.Same(item, pool.FilteredItems.First(i => i.Key == item.Key));
		Assert.Equal(0, transport.Calls);
	}

	[Fact]
	public async Task Named_queries_rename_roundtrip_and_reject_duplicates_or_invalid_search_without_authority_calls()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-named-bins-" + Guid.NewGuid().ToString("N") + ".json");
		var transport = new ForbiddenTransport(); var client = new OperatorControlClient(transport);
		await using var owner = new OperatorViewModel();
		await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
		using var pool = new MediaPoolInspectorViewModel(owner, deck);
		try
		{
			await pool.LoadSmartBinsAsync(new MediaSmartBinStore(path));
			pool.SmartBinName = "Evening clips"; pool.SearchText = "type:clip -codec:hevc";
			pool.SaveSmartBinCommand.Execute(null); await pool.FlushSmartBinsAsync();
			var saved = Assert.Single(pool.SmartBins); Assert.Equal("Evening clips", saved.Name);
			pool.SmartBinName = "News clips"; pool.RenameSmartBinCommand.Execute(null); await pool.FlushSmartBinsAsync();
			var renamed = Assert.Single(pool.SmartBins);
			Assert.Equal(saved with { Name = "News clips" }, renamed);
			Assert.Equal(renamed, Assert.Single(await new MediaSmartBinStore(path).LoadAsync()));
			pool.SearchText = "type:audio"; pool.SmartBinName = "NEWS CLIPS";
			await Until(() => pool.SaveSmartBinCommand.CanExecute(null));
			pool.SaveSmartBinCommand.Execute(null); await pool.FlushSmartBinsAsync();
			Assert.Single(pool.SmartBins); Assert.Contains("already exists", pool.BinStatus);
			pool.SearchText = "tag:unsupported"; pool.SmartBinName = "Invalid";
			await Until(() => pool.SaveSmartBinCommand.CanExecute(null));
			pool.SaveSmartBinCommand.Execute(null); await pool.FlushSmartBinsAsync();
			Assert.Contains("Cannot save query", pool.BinStatus); Assert.Single(pool.SmartBins);
			Assert.Equal(renamed, Assert.Single(await new MediaSmartBinStore(path).LoadAsync()));
			Assert.Equal(0, transport.Calls);
		}
		finally { if (File.Exists(path)) File.Delete(path); }
	}

	[Fact]
	public async Task Existing_v1_preferences_preserve_valid_queries_and_report_invalid_metadata_without_rewriting()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-bin-migration-" + Guid.NewGuid().ToString("N") + ".json");
		try
		{
			var valid = Bin() with { Name = "Legacy", Search = "codec:h264" };
			var json = JsonSerializer.Serialize(new { Version = 1, Bins = new[] {
				valid, valid with { Name = "legacy" }, valid with { Name = "Unsupported", Search = "tag:news" },
				valid with { Name = "Invalid\nname" } } });
			await File.WriteAllTextAsync(path, json);
			var store = new MediaSmartBinStore(path);
			Assert.Equal(valid, Assert.Single(await store.LoadAsync())); Assert.NotNull(store.LastLoadIssue);
			Assert.Equal(json, await File.ReadAllTextAsync(path));
			Assert.False(MediaSmartBinPolicy.Matches(Item() with { SearchMetadata = null }, valid));
			Assert.False(MediaSmartBinPolicy.Matches(Item() with { SearchMetadata = null }, valid with { Search = "-codec:hevc" }));
			await store.SaveAsync([valid]); Assert.Equal(valid, Assert.Single(await store.LoadAsync())); Assert.Null(store.LastLoadIssue);
			await File.WriteAllTextAsync(path, "{\"Version\":99,\"Bins\":[]}");
			Assert.Empty(await store.LoadAsync()); Assert.Contains("Unsupported", store.LastLoadIssue);
		}
		finally { if (File.Exists(path)) File.Delete(path); }
	}

	[Fact]
	public void Hover_pressure_uses_current_Runtime_evidence_without_inventing_fresh_capacity()
	{
		var now = DateTimeOffset.UtcNow;
		var healthy = OperatorHealthDescriptor.Unavailable with { Runtime = new("PASS", "Observed"), ObservedAtUtc = now,
			FrameTime = TimeSpan.FromMilliseconds(4), FrameBudget = TimeSpan.FromMilliseconds(20) };
		Assert.False(MediaLibraryHoverPreview.IsUnderPressure(healthy, now));
		Assert.True(MediaLibraryHoverPreview.IsUnderPressure(healthy with { FrameTime = healthy.FrameBudget }, now));
		Assert.True(MediaLibraryHoverPreview.IsUnderPressure(healthy with { Runtime = new("FAIL", "Runtime failure") }, now));
		Assert.False(MediaLibraryHoverPreview.IsUnderPressure(healthy with { FrameTime = TimeSpan.FromMilliseconds(21), ObservedAtUtc = now.AddSeconds(-11) }, now));
		Assert.False(MediaLibraryHoverPreview.IsUnderPressure(healthy with { ObservedAtUtc = now.AddSeconds(1) }, now));
		Assert.False(MediaLibraryHoverPreview.IsUnderPressure(null, now));
		// Unknown pressure still cannot enable an independent decoder: Capability has no scrub mode.
		Assert.Equal(MediaHoverCapability.Unavailable, MediaLibraryHoverPreview.Capability(Item()));
	}

	[Fact]
	public async Task Disabling_preview_cancels_pending_work_and_retains_no_image_or_production_requests()
	{
		var transport = new ForbiddenTransport(); var client = new OperatorControlClient(transport);
		await using var owner = new OperatorViewModel();
		await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
		using var pool = new MediaPoolInspectorViewModel(owner, deck);
		var image = new DrawingImage(); image.Freeze();
		var item = Item() with { Thumbnail = image };
		for (var index = 0; index < 100; index++)
		{
			pool.HoverPreviewsEnabled = true; var pending = pool.BeginHoverAsync(item);
			pool.HoverPreviewsEnabled = false; await pending;
			Assert.Null(pool.HoverThumbnail);
		}
		await pool.BeginHoverAsync(item); Assert.Contains("OFF", pool.HoverStatus);
		pool.HoverPreviewsEnabled = true; await pool.BeginHoverAsync(item);
		Assert.Same(image, pool.HoverThumbnail);
		pool.CancelHover(); Assert.Null(pool.HoverThumbnail); Assert.Equal(0, transport.Calls);
	}

	[Fact]
	public async Task Runtime_pressure_arriving_during_dwell_cancels_publication_and_requires_deliberate_retry()
	{
		var transport = new PressureTransport(); var client = new OperatorControlClient(transport);
		await using var owner = new OperatorViewModel(client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
		await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
		using var pool = new MediaPoolInspectorViewModel(owner, deck);
		async Task Synchronize()
		{
			await Until(() => owner.SynchronizeCommand.CanExecute(null)); owner.SynchronizeCommand.Execute(null);
			await Until(() => owner.IsConnected && !owner.IsBusy);
		}
		await Synchronize();
		var program = owner.ProgramSourceId; var preview = owner.PreviewSourceId;
		var image = new DrawingImage(); image.Freeze();
		var item = Item() with { Thumbnail = image };
		var pending = pool.BeginHoverAsync(item);
		transport.Health = transport.Health with { FrameTime = TimeSpan.FromMilliseconds(21), ObservedAtUtc = DateTimeOffset.UtcNow };
		await client.SynchronizeAsync(); await pending;
		Assert.Null(pool.HoverThumbnail); Assert.Contains("PAUSED", pool.HoverStatus);
		await pool.BeginHoverAsync(item); Assert.Null(pool.HoverThumbnail);
		transport.Health = transport.Health with { FrameTime = TimeSpan.FromMilliseconds(2), ObservedAtUtc = DateTimeOffset.UtcNow };
		await client.SynchronizeAsync(); Assert.Null(pool.HoverThumbnail); // Recovery does not auto-admit work.
		await pool.BeginHoverAsync(item); Assert.Same(image, pool.HoverThumbnail);
		Assert.Equal(program, owner.ProgramSourceId); Assert.Equal(preview, owner.PreviewSourceId);
		Assert.Equal(0, transport.Mutations); Assert.Equal(3, transport.Reads);
	}

	private static async Task Until(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
		Assert.True(condition());
	}

	internal static async Task VerifyWindowAsync(MainWindow owner, Dispatcher dispatcher)
	{
		owner.Shell.SelectWorkspace("MEDIA");
		await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
		var combo = Assert.IsType<RtaimeComboBox>(owner.FindName("MediaSmartBins"));
        var search = Assert.IsType<RtaimeSearchBox>(owner.FindName("MediaSearchBox"));
        Assert.Equal(180, System.Windows.Data.BindingOperations.GetBindingExpression(search, TextBox.TextProperty)!.ParentBinding.Delay);
        var results = Assert.IsType<RtaimeListBox>(owner.FindName("MediaBatchResults"));
        Assert.Equal("Media batch preflight and results", AutomationProperties.GetName(results));
        Assert.True(VirtualizingPanel.GetIsVirtualizing(results));
        Assert.Contains(Descendants(owner).OfType<RtaimeButton>(), button => AutomationProperties.GetName(button) == "Confirm media batch");

		var grid = Assert.IsType<RtaimeListBox>(owner.FindName("MediaAssetGrid"));
		var list = Assert.IsType<RtaimeListBox>(owner.FindName("MediaAssetList"));
		// Hosted displays can constrain the main window to compact mode, where the shell
		// deliberately allocates no left column. Host the actual Library subtree visibly
		// within the same Window; preserve its bindings, commands, resources and handlers.
		var library = Assert.IsType<Grid>(Assert.IsType<Grid>(grid.Parent).Parent);
		var parent = Assert.IsAssignableFrom<Panel>(library.Parent);
		var root = Assert.IsType<Grid>(owner.Content);
		var originalIndex = parent.Children.IndexOf(library);
		var width = library.Width; var height = library.Height;
		var horizontal = library.HorizontalAlignment; var vertical = library.VerticalAlignment;
		var span = Grid.GetRowSpan(library); var z = Panel.GetZIndex(library);
		var preview = ((OperatorViewModel)owner.DataContext).PreviewSourceId;
		var program = ((OperatorViewModel)owner.DataContext).ProgramSourceId;
		owner.MediaPool.SetCatalogSnapshot(new Catalog().Snapshot);
		owner.MediaPool.Refresh();
		Assert.Equal(4096, owner.MediaPool.FilteredItems.Count);
		parent.Children.Remove(library);
		library.Width = 400; library.Height = 640;
		library.HorizontalAlignment = HorizontalAlignment.Left; library.VerticalAlignment = VerticalAlignment.Top;
		Grid.SetRowSpan(library, 3); Panel.SetZIndex(library, 100);
		root.Children.Add(library);
		try
		{
			await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
			Assert.True(combo.IsVisible); Assert.True(combo.ActualWidth > 0); Assert.True(combo.Focus());
			Assert.Equal("Saved Smart Bin", AutomationProperties.GetName(combo));
			Assert.Equal(Visibility.Collapsed, owner.MediaPool.MetadataFiltersVisibility);
			owner.MediaPool.ToggleMetadataFiltersCommand.Execute(null);
			await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
			Assert.Equal(Visibility.Visible, owner.MediaPool.MetadataFiltersVisibility);
			var name = Assert.IsType<RtaimeTextBox>(owner.FindName("MediaSmartBinName"));
			Assert.True(name.IsVisible); Assert.True(name.Focus()); Assert.Equal("Smart Bin name", AutomationProperties.GetName(name));
			var previousName = owner.MediaPool.SmartBinName;
			name.Text = "Inspection bin"; name.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
			Assert.Equal("Inspection bin", owner.MediaPool.SmartBinName); owner.MediaPool.SmartBinName = previousName;
			Assert.Contains(Descendants(owner).OfType<RtaimeButton>(), button =>
				AutomationProperties.GetName(button) == "Rename selected Smart Bin" && ReferenceEquals(button.Command, owner.MediaPool.RenameSmartBinCommand));
			// Leave keyboard focus on a stable visible control before collapsing the editor.
			// Focus recovery from a hidden text field can otherwise compete with later ScrollIntoView.
			Assert.True(combo.Focus());
			owner.MediaPool.ToggleMetadataFiltersCommand.Execute(null);
			var items = owner.MediaPool.FilteredItems.ToArray();
			foreach (var browser in new[] { grid, list })
			{
				if (browser == grid) owner.MediaPool.GridViewCommand.Execute(null);
				else owner.MediaPool.ListViewCommand.Execute(null);
				await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
				Assert.True(browser.IsVisible);
				Assert.True(browser.ActualWidth > 0 && browser.ActualHeight > 100);
				foreach (var index in new[] { 2048, 4095, 0, 4095 })
				{
					browser.ScrollIntoView(browser.Items[index]);
					await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
					var deadline = DateTime.UtcNow.AddSeconds(2);
					while (browser.ItemContainerGenerator.ContainerFromIndex(index) is null && DateTime.UtcNow < deadline)
					{
						await Task.Delay(20);
						await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
					}
					var wrap = Descendants(browser).OfType<VirtualizingWrapPanel>().FirstOrDefault();
					Assert.NotNull(wrap);
					Assert.Equal(browser == list, wrap.SingleColumn);
					if (browser == list)
					{
						Assert.Equal(48d, wrap.ItemHeight);
						Assert.InRange(wrap.ExtentHeight, 4096d * 48, 4096d * 48);
					}
					Console.WriteLine($"MEDIA SCROLL [{browser.Name}] index={index}; size={browser.ActualWidth}x{browser.ActualHeight}; offset={wrap?.VerticalOffset}; extent={wrap?.ExtentHeight}; realized={Descendants(browser).OfType<RtaimeListBoxItem>().Count()}; generator={browser.ItemContainerGenerator.Status}; detail={wrap?.RealizationDetail}.");
					Assert.True(browser.ItemContainerGenerator.ContainerFromIndex(index) is not null,
						$"{browser.Name}: index={index}, items={browser.Items.Count}, realized={Descendants(browser).OfType<RtaimeListBoxItem>().Count()}, generator={browser.ItemContainerGenerator.Status}, detail={wrap?.RealizationDetail}");
					Assert.InRange(Descendants(browser).OfType<RtaimeListBoxItem>().Count(), 1, 160);
				}
				Assert.True(VirtualizingPanel.GetIsVirtualizing(browser));
				Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(browser));
			}
			await owner.MediaPool.BeginHoverAsync(items[0]);
			Assert.Contains("UNSUPPORTED", owner.MediaPool.HoverStatus);
			owner.MediaPool.CancelHover();
			Assert.Equal(preview, ((OperatorViewModel)owner.DataContext).PreviewSourceId);
			Assert.Equal(program, ((OperatorViewModel)owner.DataContext).ProgramSourceId);
			Console.WriteLine("MEDIA LIBRARY PASS: actual Library subtree in bounded visible host, accessible custom-control focus, 4096-item grid/list recycling and unchanged production identities.");
		}
		finally
		{
			root.Children.Remove(library);
			library.Width = width; library.Height = height;
			library.HorizontalAlignment = horizontal; library.VerticalAlignment = vertical;
			Grid.SetRowSpan(library, span); Panel.SetZIndex(library, z);
			parent.Children.Insert(originalIndex, library);
			owner.MediaPool.SetCatalogSnapshot(MediaAssetCatalogSnapshot.Empty);
			owner.MediaPool.Refresh(); owner.MediaPool.GridViewCommand.Execute(null);
		}
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
	private sealed class PressureTransport : IOperatorControlTransport
	{
		private readonly ProductionId _production = ProductionId.New();
		private readonly ProductionSourceId _source = ProductionSourceId.New();
		public int Reads, Mutations;
		public OperatorHealthDescriptor Health = OperatorHealthDescriptor.Unavailable with {
			Runtime = new("PASS", "Observed Runtime"), ObservedAtUtc = DateTimeOffset.UtcNow,
			FrameTime = TimeSpan.FromMilliseconds(2), FrameBudget = TimeSpan.FromMilliseconds(20) };
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
		{
			Reads++;
			return ValueTask.FromResult(new OperatorStatusSnapshot(
				new(ControlContractVersion.Current, _production, new Revision(1), new(_source, _source)),
				[new(_source.ToString(), "Camera")], "READY", "READY", "READY", "UNKNOWN", "IDLE", false, 0, health: Health));
		}
		private ValueTask<OperatorMutationResponse> Forbidden() { Mutations++; throw new InvalidOperationException("Hover must not mutate production."); }
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => Forbidden();
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) => Forbidden();
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => Forbidden();
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
