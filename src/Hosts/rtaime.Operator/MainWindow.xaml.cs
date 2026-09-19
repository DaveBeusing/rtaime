// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Windows;
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
		Timeline.SelectionChanged += OnTimelineSelectionChanged;
		InitializeComponent();
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
		Timeline.SelectionChanged += OnTimelineSelectionChanged;
		InitializeComponent();
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
	public MediaTimelineViewModel Timeline => MediaDeck.Timeline;

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
		Shell.Save();
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
				MediaPool.Dispose();
				await Monitoring.DisposeAsync();
				await MediaDeck.DisposeAsync();
				await viewModel.DisposeAsync();
			}
			else
			{
				Timeline.SelectionChanged -= OnTimelineSelectionChanged;
				MediaPool.Dispose();
				await Monitoring.DisposeAsync();
				await MediaDeck.DisposeAsync();
			}
		}
		finally
		{
			_shutdownComplete = true;
			Closing -= OnClosingAsync;
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

		DragDrop.DoDragDrop(
			(DependencyObject)sender,
			new DataObject(typeof(MediaPoolItemViewModel), item),
			DragDropEffects.Copy);
	}

	private void OnPreviewDragOver(object sender, DragEventArgs e)
	{
		var item = e.Data.GetDataPresent(typeof(MediaPoolItemViewModel))
			? e.Data.GetData(typeof(MediaPoolItemViewModel)) as MediaPoolItemViewModel
			: null;
		e.Effects = MediaPool.CanDropToPreview(item) ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private async void OnPreviewDrop(object sender, DragEventArgs e)
	{
		var item = e.Data.GetDataPresent(typeof(MediaPoolItemViewModel))
			? e.Data.GetData(typeof(MediaPoolItemViewModel)) as MediaPoolItemViewModel
			: null;
		if (item is not null && MediaPool.CanDropToPreview(item))
			await MediaPool.DropToPreviewAsync(item);
		e.Handled = true;
	}

	private void OnTimelineDragOver(object sender, DragEventArgs e)
	{
		var item = e.Data.GetDataPresent(typeof(MediaPoolItemViewModel))
			? e.Data.GetData(typeof(MediaPoolItemViewModel)) as MediaPoolItemViewModel
			: null;
		var target = sender is MediaTimelineControl control
			? control.ResolveDropTarget(e.OriginalSource)
			: null;
		var accepted = Timeline.CanAcceptMediaPoolDrop(item, target);
		e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private void OnTimelineDrop(object sender, DragEventArgs e)
	{
		var item = e.Data.GetDataPresent(typeof(MediaPoolItemViewModel))
			? e.Data.GetData(typeof(MediaPoolItemViewModel)) as MediaPoolItemViewModel
			: null;
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

	private void OnTimelineSelectionChanged(TimelineSelection selection)
	{
		if (selection.Cue is { } cue)
		{
			MediaDeck.SelectedCue = MediaDeck.Cues.FirstOrDefault(candidate => candidate.Id == cue.Id);
			MediaPool.SelectTimelineCue(cue);
			return;
		}

		MediaDeck.SelectedCue = null;
		if (selection.Item is { } item)
		{
			MediaPool.SelectTimelineItem(item);
			return;
		}

		MediaPool.ClearTimelineSelection();
	}

	private void SetProductionFullscreen(bool fullscreen) =>
		ApplyProductionFullscreen(fullscreen, updateShell: true);

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

	private void OnHelpClick(object sender, RoutedEventArgs e)
	{
		MessageBox.Show(
			"F11  Fullscreen / Windowed\nEsc  Exit fullscreen\nCtrl+1  Maximize Preview\nCtrl+2  Maximize Program\nCtrl+0  Restore dual view\nF5  Synchronize\nCtrl+P  Set selected source to Preview\nSpace  CUT Preview to Program\nCtrl+Space  AUTO Preview to Program\nP / S  Media Play-Pause / Stop\nI / O / M  IN / OUT / Cue\nPageUp / PageDown  Previous / Next Cue\nCtrl++ / Ctrl+-  Timeline Zoom\nCtrl+Shift+F  Timeline Fit\nMedia Pool  Search/filter resources; drag a Source or loaded Clip to Preview.\nTimeline  Select clips/cues for Inspector context; drag IN/OUT handles to trim.\nInspector  METADATA is read-only; DESIRED edits require APPLY before COMMITTED confirmation.",
			"rtaime Operator — Keyboard Reference",
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
