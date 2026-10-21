// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;

namespace rtaime.Operator;

public partial class OperatorMediaLibraryView : UserControl
{
    public static readonly DependencyProperty ContextProperty = DependencyProperty.Register(
        nameof(Context), typeof(OperatorMediaLibraryContext), typeof(OperatorMediaLibraryView));
    public OperatorMediaLibraryContext? Context
    {
        get => (OperatorMediaLibraryContext?)GetValue(ContextProperty);
        set => SetValue(ContextProperty, value);
    }
    private MediaPoolInspectorViewModel MediaPool => Context!.MediaPool;
    private MediaDeckViewModel MediaDeck => Context!.MediaDeck;
    private MediaTimelineViewModel Timeline => Context!.Timeline;
    private Point _mediaPoolDragStart;
    private bool _syncingMediaPoolSelection;
    public OperatorMediaLibraryView() => InitializeComponent();
    public void FocusSearch()
    {
        MediaSearchBox.Focus();
        MediaSearchBox.SelectAll();
    }

    private void OnMediaLibraryToggleToolsClick(object sender, RoutedEventArgs e)
    {
        MediaAdvancedTools.Visibility = MediaAdvancedTools.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        e.Handled = true;
    }

    private void OnMediaLibraryInspectClick(object sender, RoutedEventArgs e)
    {
        if (MediaPool.CanInspectLibrarySelection)
            MediaSelectedDetails.Visibility = MediaSelectedDetails.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        e.Handled = true;
    }

    private async void OnMediaLibraryOpenSelectedPreviewClick(object sender, RoutedEventArgs e)
    {
        if (MediaPool.CanOpenLibraryPreview)
            await MediaPool.OpenSelectedLibraryPreviewAsync();
        e.Handled = true;
    }

    private async void OnMediaLibraryRelinkSelectedClick(object sender, RoutedEventArgs e)
    {
        if (MediaPool.CanRelinkLibrarySelection && MediaPool.SelectedItem is { } selected)
        {
            var path = PickRelinkMediaFile();
            if (MediaPool.CanRelinkLibrarySelection && MediaPool.SelectedItem?.Key == selected.Key &&
                !string.IsNullOrWhiteSpace(path))
                await MediaPool.RelinkAssetAsync(selected, path);
        }
        e.Handled = true;
    }

	private static string? PickRelinkMediaFile()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Relink media asset",
			Filter = "Local video (*.mp4;*.mov)|*.mp4;*.mov",
			CheckFileExists = true,
			Multiselect = false
		};
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}

	private void OnMediaPoolClearSelectionClick(object sender, RoutedEventArgs e)
	{
		MediaPool.ClearLibrarySelectionCommand.Execute(null);
		e.Handled = true;
	}
	private void OnMediaPoolRefreshClick(object sender, RoutedEventArgs e)
	{
		if (MediaPool.RefreshCatalogCommand.CanExecute(null)) MediaPool.RefreshCatalogCommand.Execute(null);
		e.Handled = true;
	}
	private static bool IsInScrubArea(FrameworkElement surface, MouseEventArgs e)
	{
		var position = e.GetPosition(surface);
		return surface is Grid ? position.X is >= 0 and <= 56 && position.Y >= 0 && position.Y <= surface.ActualHeight
			: position.Y is >= 0 and <= 68 && position.X >= 0 && position.X <= surface.ActualWidth;
	}
	private static double PointerScrubFraction(FrameworkElement surface, MouseEventArgs e)
	{
		var width = surface is Grid ? Math.Min(56, surface.ActualWidth) : surface.ActualWidth;
		if (width <= 0) return 0.5;
		return Math.Clamp(e.GetPosition(surface).X / width, 0, 1);
	}
	private async void OnMediaLibraryHover(object sender, MouseEventArgs e)
	{
		if (e.LeftButton == MouseButtonState.Released &&
			sender is FrameworkElement { DataContext: MediaPoolItemViewModel item } target &&
			(!MediaLibraryHoverPreview.CanAnalyze(item) || IsInScrubArea(target, e)))
			await MediaPool.BeginHoverAsync(item,
				MediaLibraryHoverPreview.CanAnalyze(item) ? PointerScrubFraction(target, e) : 0.5);
	}
	private async void OnMediaLibraryScrubMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Released || !MediaPool.HoverPreviewsEnabled ||
			sender is not FrameworkElement { DataContext: MediaPoolItemViewModel item } target ||
			!MediaLibraryHoverPreview.CanAnalyze(item)) return;
		// The Grid thumbnail occupies the top 68 DIPs; list-row thumbnails use the first 56 DIPs.
		if (!IsInScrubArea(target, e)) return;
		await MediaPool.BeginHoverAsync(item, PointerScrubFraction(target, e));
	}
	private async void OnMediaLibraryScrubKeyDown(object sender, KeyEventArgs e)
	{
		if (Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.Left or Key.Right) ||
			e.OriginalSource is TextBoxBase) return;
		var item = (e.OriginalSource as FrameworkElement)?.DataContext as MediaPoolItemViewModel
			?? MediaPool.SelectedItem;
		if (item is null || !MediaLibraryHoverPreview.CanAnalyze(item) || !MediaPool.HoverPreviewsEnabled)
			return;
		e.Handled = true; // Preserve ordinary arrows for ListBox navigation.
		await MediaPool.StepHoverAsync(item, e.Key == Key.Right ? 1 : -1);
	}
	private async void OnMediaLibraryFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (e.NewFocus is FrameworkElement { DataContext: MediaPoolItemViewModel item })
			await MediaPool.BeginHoverAsync(item);
	}
	private void OnMediaLibraryLeave(object sender, MouseEventArgs e) => MediaPool.CancelHover();
	private void OnMediaLibraryItemLeave(object sender, MouseEventArgs e) => MediaPool.CancelHover();
	private void OnMediaLibraryLostFocus(object sender, KeyboardFocusChangedEventArgs e) => MediaPool.CancelHover();
	private void OnMediaLibraryContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement target) PrepareMediaLibraryContextMenu(target);
    }

    internal void PrepareMediaLibraryContextMenu(FrameworkElement target)
	{
		MediaPool.CancelHover();
		if (target.ContextMenu is not { } menu) return;
		var item = target.DataContext as MediaPoolItemViewModel;
		var multi = MediaPool.HasMultipleSelection;
		foreach (var entry in menu.Items.OfType<Controls.RtaimeMenuItem>())
		{
			var action = entry.Header?.ToString();
			entry.IsEnabled = action switch
			{
				"Prepare selected assets…" => true, // The actual Prepare command enforces the bounded eligible selection.
				"Open in Preview" => !multi && MediaPool.CanDropToPreview(item),
				"Add to Timeline" => !multi && item is not null && Timeline.CanAcceptMediaPoolDrop(item,
					item.Kind switch { MediaPoolItemKind.Clip => TimelineTrackCategory.Video,
						MediaPoolItemKind.Audio => TimelineTrackCategory.Audio,
						MediaPoolItemKind.Graphics => TimelineTrackCategory.Overlay,
						_ => (TimelineTrackCategory?)null }),
				"Add Cue at Playhead" => !multi && item?.Kind == MediaPoolItemKind.Clip &&
					string.Equals(item.ReferenceId, MediaDeck.AssetId, StringComparison.Ordinal),
				"Reveal in Explorer" => !multi && item?.CanRevealInExplorer == true &&
					item.LocalPath is { } path && File.Exists(path),
				"Relink Media…" or "Remove from Library" => !multi && item?.Kind == MediaPoolItemKind.Clip &&
					Guid.TryParse(item.ReferenceId, out _),
				"Properties" => !multi && item is not null,
				_ => !multi
			};
			entry.ToolTip = entry.IsEnabled ? null : multi
				? "This action needs one asset. Review multi-selection with the bounded batch controls."
				: "Unavailable for this asset or current production context.";
		}
	}

	private void OnMediaPoolPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
		_mediaPoolDragStart = e.GetPosition(this);

	private void OnMediaPoolPreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed)
			return;

		MediaPool.CancelHover();
		var position = e.GetPosition(this);
		if (Math.Abs(position.X - _mediaPoolDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
			Math.Abs(position.Y - _mediaPoolDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
		{
			return;
		}

		if (e.OriginalSource is not FrameworkElement element ||
			element.DataContext is not MediaPoolItemViewModel item)
		{
			return;
		}

		var selected = MediaPool.SelectedItems.Any(candidate => string.Equals(candidate.Key, item.Key, StringComparison.Ordinal))
			? MediaPool.SelectedItems.ToArray()
			: [item];
		var payload = new MediaAssetDragPayload(item, selected);
		MediaPool.ShowDragIntent(payload);
		try
		{
			DragDrop.DoDragDrop(
				(DependencyObject)sender,
				new DataObject(typeof(MediaAssetDragPayload), payload),
				DragDropEffects.Copy);
		}
		finally { MediaPool.ClearDragIntent(); }
	}

	private void OnMediaPoolSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingMediaPoolSelection || sender is not ListBox listBox || !listBox.IsVisible)
			return;

		if (!MatchesSelection(listBox))
			MediaPool.UpdateSelection(listBox.SelectedItems.Cast<MediaPoolItemViewModel>());
	}

	private bool MatchesSelection(ListBox listBox) =>
        listBox.SelectedItems.Count == MediaPool.SelectedItems.Count &&
        listBox.SelectedItems.Cast<MediaPoolItemViewModel>().All(item =>
            MediaPool.SelectedItems.Any(selected => ReferenceEquals(selected, item)));

	private void OnMediaPoolViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		MediaPool.CancelHover();
		if (sender is not ListBox listBox || e.NewValue is not true)
			return;

		var selectedKeys = MediaPool.SelectedItems
			.Select(item => item.Key)
			.ToHashSet(StringComparer.Ordinal);
		if (selectedKeys.Count == 0 && MediaPool.SelectedItem is { } primary)
			selectedKeys.Add(primary.Key);

		_syncingMediaPoolSelection = true;
		try
		{
			listBox.SelectedItems.Clear();
			foreach (var item in listBox.Items.Cast<MediaPoolItemViewModel>())
			{
				if (selectedKeys.Contains(item.Key))
					listBox.SelectedItems.Add(item);
			}
		}
		finally
		{
			_syncingMediaPoolSelection = false;
		}

		MediaPool.UpdateSelection(listBox.SelectedItems.Cast<MediaPoolItemViewModel>());
	}

	private void OnMediaPoolPrepareClick(object sender, RoutedEventArgs e)
    {
        MediaAdvancedTools.Visibility = Visibility.Visible;
        MediaPool.ShowPreparation();
        e.Handled = true;
    }

	private void OnMediaPoolAddClick(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { ContextMenu: { } menu } target)
			return;

		menu.PlacementTarget = target;
		menu.Placement = PlacementMode.Bottom;
		menu.IsOpen = true;
		e.Handled = true;
	}

	private void OnMediaPoolImportMenuClick(object sender, RoutedEventArgs e)
	{
		if (MediaPool.ImportCommand.CanExecute(null))
			MediaPool.ImportCommand.Execute(null);
		e.Handled = true;
	}

	private void OnMediaPoolTestSignalMenuClick(object sender, RoutedEventArgs e)
	{
		if (MediaPool.AddTestSignalCommand.CanExecute(null))
			MediaPool.AddTestSignalCommand.Execute(null);
		e.Handled = true;
	}

	private async void OnMediaPoolPreviewActionClick(object sender, RoutedEventArgs e)
	{
		if (MediaPool.HasMultipleSelection) { e.Handled = true; return; }
		var item = GetMediaPoolItemFromSender(sender);
        // Virtualized tiles may be recycled between pointer-down and command execution.
        var current = item is null ? null :
            MediaPool.FilteredItems.FirstOrDefault(asset => asset.Key == item.Key);
        if (current is not null && current.ReferenceId == item!.ReferenceId &&
            current.LocalPath == item.LocalPath)
        {
            MediaPool.UpdateSelection([current]);
            if (MediaPool.CanOpenLibraryPreview)
                await MediaPool.OpenSelectedLibraryPreviewAsync();
        }
		e.Handled = true;
	}

	private void OnMediaPoolTimelineActionClick(object sender, RoutedEventArgs e)
	{
		if (MediaPool.HasMultipleSelection) { e.Handled = true; return; }
		var item = GetMediaPoolItemFromSender(sender);
		var target = item?.Kind switch
		{
			MediaPoolItemKind.Clip => TimelineTrackCategory.Video,
			MediaPoolItemKind.Audio => TimelineTrackCategory.Audio,
			MediaPoolItemKind.Graphics => TimelineTrackCategory.Overlay,
			_ => (TimelineTrackCategory?)null
		};
		if (item is not null && target is not null && Timeline.CanAcceptMediaPoolDrop(item, target))
		{
			MediaPool.SelectedItem = item;
			if (item.Kind == MediaPoolItemKind.Clip &&
				string.Equals(item.ReferenceId, MediaDeck.AssetId, StringComparison.Ordinal) &&
				MediaDeck.RefreshCommand.CanExecute(null))
				MediaDeck.RefreshCommand.Execute(null);
			Timeline.ProjectMediaPoolDrop(item, target.Value);
		}
		e.Handled = true;
	}

	private void OnMediaPoolCueActionClick(object sender, RoutedEventArgs e)
	{
		if (MediaPool.HasMultipleSelection) { e.Handled = true; return; }
		var item = GetMediaPoolItemFromSender(sender);
		if (item?.Kind == MediaPoolItemKind.Clip &&
			string.Equals(item.ReferenceId, MediaDeck.AssetId, StringComparison.Ordinal) &&
			MediaDeck.AddCueCommand.CanExecute(null))
		{
			MediaPool.SelectedItem = item;
			MediaDeck.AddCueCommand.Execute(null);
		}
		e.Handled = true;
	}

	private async void OnMediaPoolRelinkActionClick(object sender, RoutedEventArgs e)
	{
		if (MediaPool.HasMultipleSelection) { e.Handled = true; return; }
		var item = GetMediaPoolItemFromSender(sender);
		if (item?.Kind == MediaPoolItemKind.Clip)
		{
			var path = PickRelinkMediaFile();
			if (!string.IsNullOrWhiteSpace(path))
				await MediaPool.RelinkAssetAsync(item, path);
		}
		e.Handled = true;
	}

	private async void OnMediaPoolRemoveActionClick(object sender, RoutedEventArgs e)
	{
		if (MediaPool.HasMultipleSelection) { e.Handled = true; return; }
		var item = GetMediaPoolItemFromSender(sender);
		if (item?.Kind == MediaPoolItemKind.Clip)
			await MediaPool.RemoveAssetAsync(item);
		e.Handled = true;
	}

	private void OnMediaPoolRevealActionClick(object sender, RoutedEventArgs e)
	{
		var item = GetMediaPoolItemFromSender(sender);
		if (item?.CanRevealInExplorer == true && item.LocalPath is { } path && File.Exists(path))
		{
			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = "explorer.exe",
					Arguments = $"/select,\"{path}\"",
					UseShellExecute = true
				});
			}
			catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
			{
				// Explorer integration is presentation-only; failure must not affect production state.
			}
		}
		e.Handled = true;
	}

	private void OnMediaPoolPropertiesActionClick(object sender, RoutedEventArgs e)
	{
		var item = GetMediaPoolItemFromSender(sender);
		if (item is not null)
			MediaPool.SelectedItem = item;
		e.Handled = true;
	}

	private static MediaPoolItemViewModel? GetMediaPoolItemFromSender(object sender)
	{
		if (sender is FrameworkElement element && element.DataContext is MediaPoolItemViewModel item)
			return item;

		if (sender is MenuItem menuItem &&
			ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu contextMenu &&
			contextMenu.PlacementTarget is FrameworkElement target &&
			target.DataContext is MediaPoolItemViewModel targetItem)
		{
			return targetItem;
		}

		return null;
	}

}
