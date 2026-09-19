// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using rtaime.Client;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public partial class MainWindow : Window
{
	private bool _shutdownStarted;
	private bool _shutdownComplete;
	private bool _fullscreenApplied;
	private WindowStyle _windowedStyle = WindowStyle.SingleBorderWindow;
	private ResizeMode _windowedResizeMode = ResizeMode.CanResize;
	private WindowState _windowedState = WindowState.Normal;
	private Point _mediaPoolDragStart;
	private bool _syncingMediaPoolSelection;

	public MainWindow()
	{
		var controlEndpoint = Environment.GetEnvironmentVariable("RTAIME_CONTROL_ENDPOINT");
		if (string.IsNullOrWhiteSpace(controlEndpoint))
			controlEndpoint = "rtaime.v1.control.default";

		var runtimeEndpoint = Environment.GetEnvironmentVariable("RTAIME_RUNTIME_ENDPOINT");
		if (string.IsNullOrWhiteSpace(runtimeEndpoint))
			runtimeEndpoint = "rtaime.v1.runtime.default";
		var monitoringEndpoint = Environment.GetEnvironmentVariable("RTAIME_MONITOR_ENDPOINT");
		if (string.IsNullOrWhiteSpace(monitoringEndpoint))
			monitoringEndpoint = $"{runtimeEndpoint}.monitor";

		var controlTransport = new NamedPipeOperatorControlTransport(controlEndpoint);
		var client = new OperatorControlClient(controlTransport);
		var viewModel = new OperatorViewModel(
			client,
			PickGraphicsAsset,
			new DispatcherSynchronizationContext(Dispatcher));
		MediaDeck = CreateMediaDeck(viewModel, client);
		MediaDeck.SnapshotChanged += viewModel.ApplyMediaDeckSnapshot;
		DemoProduction = new DemoProductionPackageController(client, viewModel, MediaDeck);
		Monitoring = new OperatorMonitoringViewModel(
			viewModel,
			new NamedPipeOperatorMonitoringTransport(monitoringEndpoint),
			new DispatcherSynchronizationContext(Dispatcher));
		ProgramOutput = new ProgramOutputController(
			Monitoring,
			new DispatcherSynchronizationContext(Dispatcher));
		Shell = new OperatorShellViewModel(new OperatorLayoutStore(), SetProductionFullscreen);
		MediaPool = new MediaPoolInspectorViewModel(viewModel, MediaDeck);
		QuickControls = new OperatorQuickControlsViewModel(viewModel, MediaDeck, MediaPool, new OperatorQuickControlStore());
		Shortcuts = OperatorKeyboardCommandRegistry.Create(
			viewModel,
			MediaDeck,
			Timeline,
			Shell,
			new AsyncRelayCommand(FocusMediaSearchAsync));
		Timeline.SelectionChanged += OnTimelineSelectionChanged;
		InitializeComponent();
		ApplyWindowPlacement();
		Shell.UpdateViewportWidth(ActualWidth > 0 ? ActualWidth : Width);
		SizeChanged += OnShellSizeChanged;
		ApplyProductionFullscreen(Shell.IsFullscreen, updateShell: false);
		DataContext = viewModel;
		MediaDeck.Start();
		Monitoring.Start();
		viewModel.StartAudioMetering();
		ContentRendered += OnContentRendered;
		Closing += OnClosingAsync;
	}

	public MainWindow(OperatorViewModel viewModel)
	{
		ArgumentNullException.ThrowIfNull(viewModel);
		viewModel.SetGraphicsAssetPicker(PickGraphicsAsset);
		viewModel.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
		var client = viewModel.Client ?? new OperatorControlClient(new UnavailableOperatorControlTransport());
		MediaDeck = CreateMediaDeck(viewModel, client);
		MediaDeck.SnapshotChanged += viewModel.ApplyMediaDeckSnapshot;
		DemoProduction = new DemoProductionPackageController(client, viewModel, MediaDeck);
		Monitoring = new OperatorMonitoringViewModel(
			viewModel,
			new NamedPipeOperatorMonitoringTransport("rtaime.v1.runtime.default.monitor"),
			new DispatcherSynchronizationContext(Dispatcher));
		ProgramOutput = new ProgramOutputController(
			Monitoring,
			new DispatcherSynchronizationContext(Dispatcher));
		Shell = new OperatorShellViewModel(new OperatorLayoutStore(), SetProductionFullscreen);
		MediaPool = new MediaPoolInspectorViewModel(viewModel, MediaDeck);
		QuickControls = new OperatorQuickControlsViewModel(viewModel, MediaDeck, MediaPool, new OperatorQuickControlStore());
		Shortcuts = OperatorKeyboardCommandRegistry.Create(
			viewModel,
			MediaDeck,
			Timeline,
			Shell,
			new AsyncRelayCommand(FocusMediaSearchAsync));
		Timeline.SelectionChanged += OnTimelineSelectionChanged;
		InitializeComponent();
		ApplyWindowPlacement();
		Shell.UpdateViewportWidth(ActualWidth > 0 ? ActualWidth : Width);
		SizeChanged += OnShellSizeChanged;
		ApplyProductionFullscreen(Shell.IsFullscreen, updateShell: false);
		DataContext = viewModel;
		MediaDeck.Start();
		viewModel.StartAudioMetering();
		ContentRendered += OnContentRendered;
		Closing += OnClosingAsync;
	}

	public OperatorMonitoringViewModel Monitoring { get; }
	public ProgramOutputController ProgramOutput { get; }
	public OperatorShellViewModel Shell { get; }
	public MediaDeckViewModel MediaDeck { get; }
	public DemoProductionPackageController DemoProduction { get; }
	public MediaPoolInspectorViewModel MediaPool { get; }
	public OperatorQuickControlsViewModel QuickControls { get; }
	public OperatorKeyboardCommandRegistry Shortcuts { get; }
	public MediaTimelineViewModel Timeline => MediaDeck.Timeline;

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (Shortcuts.TryHandle(e))
			return;

		base.OnPreviewKeyDown(e);
	}

	private Task FocusMediaSearchAsync()
	{
		Shell.SelectWorkspace("MEDIA");
		Dispatcher.BeginInvoke(
			() =>
			{
				MediaSearchBox.Focus();
				MediaSearchBox.SelectAll();
			},
			DispatcherPriority.Input);
		return Task.CompletedTask;
	}

	private MediaDeckViewModel CreateMediaDeck(
		OperatorViewModel viewModel,
		OperatorControlClient client) =>
		new(
			new MediaDeckController(client),
			PickLocalMediaFile,
			() => viewModel.SelectedSource?.Id,
			new DispatcherSynchronizationContext(Dispatcher));

	private static OperatorGraphicsAsset? PickGraphicsAsset()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Load graphics overlay",
			Filter = "PNG Image (*.png)|*.png",
			CheckFileExists = true,
			Multiselect = false
		};
		return dialog.ShowDialog() == true
			? GraphicsOverlayAssetLoader.LoadPng(dialog.FileName)
			: null;
	}

	private static string? PickLocalMediaFile()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Open local media",
			Filter = "MP4 Video (*.mp4)|*.mp4",
			CheckFileExists = true,
			Multiselect = false
		};
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}

	private void OnContentRendered(object? sender, EventArgs e)
	{
		ContentRendered -= OnContentRendered;
		SynchronizeButton.Focus();
		if (DataContext is OperatorViewModel viewModel &&
			viewModel.SynchronizeCommand.CanExecute(null))
		{
			viewModel.SynchronizeCommand.Execute(null);
		}
	}

	private async void OnClosingAsync(object? sender, CancelEventArgs e)
	{
		if (!_fullscreenApplied)
			CaptureWindowPlacement();
		await Shell.SaveAsync();
		if (_shutdownComplete)
			return;

		e.Cancel = true;
		if (_shutdownStarted)
			return;

		_shutdownStarted = true;
		IsEnabled = false;
		try
		{
			ProgramOutput.Dispose();
			if (DataContext is OperatorViewModel viewModel)
			{
				MediaDeck.SnapshotChanged -= viewModel.ApplyMediaDeckSnapshot;
				Timeline.SelectionChanged -= OnTimelineSelectionChanged;
				QuickControls.Dispose();
				MediaPool.Dispose();
				await Monitoring.DisposeAsync();
				await MediaDeck.DisposeAsync();
				await viewModel.DisposeAsync();
			}
			else
			{
				Timeline.SelectionChanged -= OnTimelineSelectionChanged;
				QuickControls.Dispose();
				MediaPool.Dispose();
				await Monitoring.DisposeAsync();
				await MediaDeck.DisposeAsync();
			}
		}
		finally
		{
			_shutdownComplete = true;
			Closing -= OnClosingAsync;
			SizeChanged -= OnShellSizeChanged;
			Close();
		}
	}

	private void OnMediaPoolPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
		_mediaPoolDragStart = e.GetPosition(this);

	private void OnMediaPoolPreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed)
			return;

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
		DragDrop.DoDragDrop(
			(DependencyObject)sender,
			new DataObject(typeof(MediaAssetDragPayload), payload),
			DragDropEffects.Copy);
	}

	private void OnMediaPoolSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingMediaPoolSelection || sender is not ListBox listBox || !listBox.IsVisible)
			return;

		MediaPool.UpdateSelection(listBox.SelectedItems.Cast<MediaPoolItemViewModel>());
	}

	private void OnMediaPoolViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
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

	private void OnPreviewDragOver(object sender, DragEventArgs e)
	{
		var item = GetMediaPoolDragItem(e);
		e.Effects = MediaPool.CanDropToPreview(item) ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private async void OnPreviewDrop(object sender, DragEventArgs e)
	{
		var item = GetMediaPoolDragItem(e);
		if (item is not null && MediaPool.CanDropToPreview(item))
			await MediaPool.DropToPreviewAsync(item);
		e.Handled = true;
	}

	private void OnTimelineDragOver(object sender, DragEventArgs e)
	{
		var item = GetMediaPoolDragItem(e);
		var target = sender is MediaTimelineControl control
			? control.ResolveDropTarget(e.OriginalSource)
			: null;
		var accepted = Timeline.CanAcceptMediaPoolDrop(item, target);
		e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private void OnTimelineDrop(object sender, DragEventArgs e)
	{
		var item = GetMediaPoolDragItem(e);
		var target = sender is MediaTimelineControl control
			? control.ResolveDropTarget(e.OriginalSource)
			: null;
		if (item is null || target is null || !Timeline.CanAcceptMediaPoolDrop(item, target))
		{
			e.Effects = DragDropEffects.None;
			e.Handled = true;
			return;
		}

		MediaPool.SelectedItem = item;
		if (item.Kind == MediaPoolItemKind.Clip && MediaDeck.RefreshCommand.CanExecute(null))
			MediaDeck.RefreshCommand.Execute(null);

		Timeline.ProjectMediaPoolDrop(item, target.Value);
		e.Effects = DragDropEffects.Copy;
		e.Handled = true;
	}

	private async void OnMediaPoolPreviewActionClick(object sender, RoutedEventArgs e)
	{
		var item = GetMediaPoolItemFromSender(sender);
		if (item is not null && MediaPool.CanDropToPreview(item))
			await MediaPool.DropToPreviewAsync(item);
		e.Handled = true;
	}

	private void OnMediaPoolTimelineActionClick(object sender, RoutedEventArgs e)
	{
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
			if (item.Kind == MediaPoolItemKind.Clip && MediaDeck.RefreshCommand.CanExecute(null))
				MediaDeck.RefreshCommand.Execute(null);
			Timeline.ProjectMediaPoolDrop(item, target.Value);
		}
		e.Handled = true;
	}

	private void OnMediaPoolCueActionClick(object sender, RoutedEventArgs e)
	{
		var item = GetMediaPoolItemFromSender(sender);
		if (item?.Kind == MediaPoolItemKind.Clip &&
			string.Equals(item.ReferenceId, MediaDeck.SourceId, StringComparison.Ordinal) &&
			MediaDeck.AddCueCommand.CanExecute(null))
		{
			MediaPool.SelectedItem = item;
			MediaDeck.AddCueCommand.Execute(null);
		}
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

	private static MediaPoolItemViewModel? GetMediaPoolDragItem(DragEventArgs e) =>
		e.Data.GetDataPresent(typeof(MediaAssetDragPayload))
			? (e.Data.GetData(typeof(MediaAssetDragPayload)) as MediaAssetDragPayload)?.Primary
			: null;

	private void OnTimelineSelectionChanged(TimelineSelection selection)
	{
		if (selection.Cue is { } cue)
		{
			MediaDeck.SelectedCue = MediaDeck.Cues.FirstOrDefault(candidate => candidate.Id == cue.Id);
			MediaPool.SelectTimelineCue(cue);
			return;
		}

		MediaDeck.SelectedCue = null;
		if (selection.Items.Count > 0)
		{
			MediaPool.SelectTimelineItems(selection.Items, selection.Item);
			return;
		}

		MediaPool.ClearTimelineSelection();
	}

	private void SetProductionFullscreen(bool fullscreen) =>
		ApplyProductionFullscreen(fullscreen, updateShell: true);

	private void ApplyWindowPlacement()
	{
		var placement = Shell.WindowPlacement.Normalize();
		Width = placement.Width;
		Height = placement.Height;

		if (placement.Left is { } left &&
			placement.Top is { } top &&
			IsWindowPlacementVisible(left, top, placement.Width, placement.Height))
		{
			WindowStartupLocation = WindowStartupLocation.Manual;
			Left = left;
			Top = top;
		}

		WindowState = string.Equals(placement.State, "MAXIMIZED", StringComparison.Ordinal)
			? WindowState.Maximized
			: WindowState.Normal;
	}

	private void CaptureWindowPlacement()
	{
		var bounds = RestoreBounds;
		if (bounds.Width <= 0 || bounds.Height <= 0)
			return;

		Shell.SetWindowPlacement(
			bounds.Left,
			bounds.Top,
			bounds.Width,
			bounds.Height,
			WindowState);
	}

	private static bool IsWindowPlacementVisible(double left, double top, double width, double height)
	{
		if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height))
			return false;

		var requested = new Rect(left, top, width, height);
		var virtualScreen = new Rect(
			SystemParameters.VirtualScreenLeft,
			SystemParameters.VirtualScreenTop,
			SystemParameters.VirtualScreenWidth,
			SystemParameters.VirtualScreenHeight);
		requested.Intersect(virtualScreen);
		return requested.Width >= 96 && requested.Height >= 64;
	}

	private void OnShellSizeChanged(object sender, SizeChangedEventArgs e) =>
		Shell.UpdateViewportWidth(e.NewSize.Width);

	private void ApplyProductionFullscreen(bool fullscreen, bool updateShell)
	{
		if (fullscreen == _fullscreenApplied)
		{
			if (updateShell)
				Shell.SetFullscreenState(fullscreen);
			return;
		}

		if (fullscreen)
		{
			CaptureWindowPlacement();
			_windowedStyle = WindowStyle;
			_windowedResizeMode = ResizeMode;
			_windowedState = WindowState;
			WindowState = WindowState.Normal;
			WindowStyle = WindowStyle.None;
			ResizeMode = ResizeMode.NoResize;
			WindowState = WindowState.Maximized;
		}
		else
		{
			WindowState = WindowState.Normal;
			WindowStyle = _windowedStyle;
			ResizeMode = _windowedResizeMode;
			WindowState = _windowedState == WindowState.Minimized ? WindowState.Normal : _windowedState;
		}

		_fullscreenApplied = fullscreen;
		if (updateShell)
			Shell.SetFullscreenState(fullscreen);
	}

	private void OnLayoutSplitterDragCompleted(object sender, DragCompletedEventArgs e) =>
		Shell.Save();

	private void OnShellMenuClick(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement element || element.ContextMenu is null)
			return;

		element.ContextMenu.PlacementTarget = element;
		element.ContextMenu.IsOpen = true;
	}

	private void OnHelpClick(object sender, RoutedEventArgs e)
	{
		MessageBox.Show(
			$"WORKSPACES\nMEDIA  Media preparation\nEDIT  Timeline-focused editing\nLIVE  Multiview, source/cue selection and explicit take controls\nSCENES  Scene/layer presentation using existing graphics state\nCOMPOSITING  Graphics/compositing presentation\nOUTPUTS  Output and operational evidence\nSETTINGS  Shell, status and diagnostics\n\nKEYBOARD\n{Shortcuts.ReferenceText}\n\nLIVE  Select sources/cues without activation; use SET PVW, CUT/AUTO or JUMP CUE explicitly. Double-click a multiview tile only enlarges its monitor image.\nMedia Pool  Search/filter resources; drag a Source or loaded Clip to Preview.\nTimeline  Select clips/cues for Inspector context; drag IN/OUT handles to trim.\nInspector  Use PIN on supported editable properties to add/remove LIVE Quick Controls.\nClean Program  Uses the existing Program monitoring image and never changes physical Program output.",
			"rtaime Operator — Workspace & Keyboard Reference",
			MessageBoxButton.OK,
			MessageBoxImage.Information);
	}

	private sealed class UnavailableOperatorControlTransport : IOperatorControlTransport
	{
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorStatusSnapshot>(new InvalidOperationException("Operator transport is not configured."));

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
			SelectPreviewCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Operator transport is not configured."));

		public ValueTask<OperatorMutationResponse> CutProgramAsync(
			CutProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Operator transport is not configured."));

		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
			DissolveProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Operator transport is not configured."));
	}
}
