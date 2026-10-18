// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using rtaime.Client;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public partial class MainWindow : Window
{
	public string ProductBuildIdentity { get; } = rtaime.Core.ProductBuildInfo.FromAssembly(typeof(MainWindow).Assembly).DisplayIdentity;
	private const int DwmwaUseImmersiveDarkMode = 20;
	private const int DwmwaUseImmersiveDarkModeLegacy = 19;
	private const int DwmwaBorderColor = 34;
	private const int DwmwaCaptionColor = 35;
	private const int DwmwaTextColor = 36;

	private const int DarkCaptionColor = 0x001B1309;
	private const int DarkBorderColor = 0x003F3425;
	private const int LightCaptionTextColor = 0x00F6F3EE;

	private WorkspaceTemplatesWindow? _workspaceTemplatesWindow;
    internal WorkspaceTemplatesWindow? WorkspaceTemplatesWindow => _workspaceTemplatesWindow;
	private ShowReadinessWindow? _showReadinessWindow;
	internal ShowReadinessWindow? ShowReadinessWindow => _showReadinessWindow;
	private OperatorCommandPaletteWindow? _commandPaletteWindow;
	internal OperatorCommandPaletteWindow? CommandPaletteWindow => _commandPaletteWindow;
	private bool _shutdownStarted;
	private bool _shutdownComplete;
	private bool _fullscreenApplied;
	private WindowStyle _windowedStyle = WindowStyle.SingleBorderWindow;
	private ResizeMode _windowedResizeMode = ResizeMode.CanResize;
	private WindowState _windowedState = WindowState.Normal;
	private readonly OperatorSessionRecoveryStore _sessionRecoveryStore = new();
	private readonly OperatorSessionRecoveryResult _sessionRecovery;

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
		viewModel.ConfirmedMediaDeckSnapshot += MediaDeck.ApplyConfirmedSnapshot;
		Replay = new ReplayViewModel(client, MediaDeck);
		viewModel.ConfirmedReplaySnapshot += Replay.ApplyConfirmedSnapshot;
		DemoProduction = new DemoProductionPackageController(client, viewModel, MediaDeck);
		Monitoring = new OperatorMonitoringViewModel(
			viewModel,
			new NamedPipeOperatorMonitoringTransport(monitoringEndpoint),
			new DispatcherSynchronizationContext(Dispatcher));
		ProgramOutput = new ProgramOutputController(
			Monitoring,
			new DispatcherSynchronizationContext(Dispatcher));
		OutputHealth = new OutputRoutingHealthViewModel(viewModel, ProgramOutput);
		RuntimePerformanceStatus = new RuntimePerformanceStatusBarViewModel(viewModel, OutputHealth);
		Shell = new OperatorShellViewModel(new OperatorLayoutStore(), SetProductionFullscreen);
		_sessionRecovery = _sessionRecoveryStore.BeginSession(new OperatorSafeSessionState(Shell.SelectedWorkspace));
		if (_sessionRecovery.PreviousSessionEndedUnexpectedly)
			Shell.SelectWorkspace(_sessionRecovery.SafeState.SelectedWorkspace);
		MediaPool = new MediaPoolInspectorViewModel(viewModel, MediaDeck, client, PickLocalMediaFiles);
		NdiSources = new NdiSourceDiscoveryViewModel(client);
		Rundown = new RundownViewModel(client, viewModel, MediaPool);
		CompositingGraph = new CompositingGraphViewModel(viewModel, MediaPool);
		HealthProvider = new OperatorHealthSnapshotProvider(viewModel, MediaDeck, Monitoring, ProgramOutput, CompositingGraph);
		HealthCenter = new HealthCenterViewModel(
			HealthProvider,
			viewModel.RuntimeReadiness,
			viewModel.SynchronizeCommand,
			new DispatcherSynchronizationContext(Dispatcher));
		QuickControls = new OperatorQuickControlsViewModel(viewModel, MediaDeck, MediaPool, new OperatorQuickControlStore());
		ShowControl = new ShowControlViewModel(client);
		ProductionMacros = new ProductionMacroViewModel(client);
		viewModel.ConfirmedShowControlSnapshot += ShowControl.ApplyConfirmedSnapshot;

		Timeline.SelectionChanged += OnTimelineSelectionChanged;
		Startup = new StartupLifecycleViewModel(
			Environment.GetEnvironmentVariable("RTAIME_APPHOST_LIFECYCLE_FILE"),
			new DispatcherSynchronizationContext(Dispatcher),
			new SupportBundleExporter(HealthProvider),
			PickSupportBundleDestination);
		WorkspaceStates = new OperatorWorkspaceStateViewModel(
			viewModel,
			MediaPool,
			MediaDeck,
			Timeline,
			CompositingGraph,
			OutputHealth,
			HealthCenter,
			Startup);
		Notifications = new OperatorNotificationCenterViewModel(
			new NotificationService(),
			viewModel,
			new DispatcherSynchronizationContext(Dispatcher), NavigateFromShowChecks, client.ActionHistory);
		ShowReadiness = new ShowReadinessViewModel(client, viewModel, HealthProvider, Notifications,
			NavigateFromShowChecks, new DispatcherSynchronizationContext(Dispatcher), MediaPool);
		OpenShowChecksCommand = new NotificationCommand(OpenShowChecks, () => !_shutdownStarted);
		Notifications.NotifySessionRecovery(_sessionRecovery);
		Shell.PropertyChanged += OnShellPropertyChanged;
		InitializeComponent();
		Startup.PropertyChanged += OnStartupPropertyChanged;
		StartStartupBrandAnimation();
		ApplyWindowPlacement();
		Shell.UpdateViewportSize(
			ActualWidth > 0 ? ActualWidth : Width,
			ActualHeight > 0 ? ActualHeight : Height);
		SizeChanged += OnShellSizeChanged;
		ApplyProductionFullscreen(Shell.IsFullscreen, updateShell: false);
		DataContext = viewModel;
		Composition = new(this);
        AudioMixerView.Inspector = Composition.Inspector;
        AudioMixerView.DataContext = viewModel.AudioMixer;
		MediaLibraryView.Context = Composition.Library;
        Docking = new OperatorDockController(this, LegacyWorkspaceGrid, DockWorkspaceHost,
            new Dictionary<string, FrameworkElement>
            {
                ["TOOLS"] = LeftToolRegion, ["MONITORS"] = CenterWorkspace,
                ["INSPECTOR"] = RightInspectorRegion, ["TIMELINE"] = LowerTimelineRegion,
                ["AUDIO"] = AudioMixerView
            });
        Shell.ConfigureTemplateFiles(PickWorkspaceTemplateImport, PickWorkspaceTemplateExport, Docking.Capture);
        OpenWorkspaceTemplatesCommand = new OperatorShellCommand(OpenWorkspaceTemplates, () => !_shutdownStarted);
		OpenLibraryCommand = new OperatorShellCommand(() => OpenLibrary(), () => !_shutdownStarted);
        OpenInlineHelpCommand = new OperatorShellCommand(() => OpenLibrary("Help"), () => !_shutdownStarted);
        OpenCommandPaletteCommand = new AsyncRelayCommand(() => { OpenCommandPalette(); return Task.CompletedTask; }, () => !_shutdownStarted);
		Shortcuts = OperatorKeyboardCommandRegistry.Create(viewModel, MediaDeck, Timeline, Shell, ShowControl,
			new AsyncRelayCommand(FocusMediaSearchAsync), Docking, OpenCommandPaletteCommand);
		CommandPalette = new OperatorCommandPaletteViewModel(Shortcuts,
			OperatorCommandCatalog.Create(Shortcuts, viewModel, MediaDeck, Shell, MediaPool, OpenShowChecksCommand, OpenLibraryCommand, OpenInlineHelpCommand, OpenWorkspaceTemplatesCommand),
			@operator: viewModel, importPicker: PickShortcutImport, exportPicker: PickShortcutExport);
        InitializeLibrary(viewModel);
		Monitoring.Start();
		viewModel.StartAudioMetering();
		ContentRendered += OnContentRendered;
		Closing += OnClosingAsync;
	}

	public MainWindow(OperatorViewModel viewModel)
		: this(viewModel, new OperatorLayoutStore(), new OperatorSessionRecoveryStore())
	{
	}

	internal MainWindow(
		OperatorViewModel viewModel,
		OperatorLayoutStore layoutStore,
		OperatorSessionRecoveryStore sessionRecoveryStore,
        OperatorLibraryPreferenceStore? libraryPreferenceStore = null)
	{
		_sessionRecoveryStore = sessionRecoveryStore;
		ArgumentNullException.ThrowIfNull(viewModel);
		viewModel.SetGraphicsAssetPicker(PickGraphicsAsset);
		viewModel.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
		var client = viewModel.Client ?? new OperatorControlClient(new UnavailableOperatorControlTransport());
		MediaDeck = CreateMediaDeck(viewModel, client);
		MediaDeck.SnapshotChanged += viewModel.ApplyMediaDeckSnapshot;
		viewModel.ConfirmedMediaDeckSnapshot += MediaDeck.ApplyConfirmedSnapshot;
		Replay = new ReplayViewModel(client, MediaDeck);
		viewModel.ConfirmedReplaySnapshot += Replay.ApplyConfirmedSnapshot;
		DemoProduction = new DemoProductionPackageController(client, viewModel, MediaDeck);
		Monitoring = new OperatorMonitoringViewModel(
			viewModel,
			new NamedPipeOperatorMonitoringTransport("rtaime.v1.runtime.default.monitor"),
			new DispatcherSynchronizationContext(Dispatcher));
		ProgramOutput = new ProgramOutputController(
			Monitoring,
			new DispatcherSynchronizationContext(Dispatcher));
		OutputHealth = new OutputRoutingHealthViewModel(viewModel, ProgramOutput);
		RuntimePerformanceStatus = new RuntimePerformanceStatusBarViewModel(viewModel, OutputHealth);
		Shell = new OperatorShellViewModel(layoutStore, SetProductionFullscreen);
		_sessionRecovery = _sessionRecoveryStore.BeginSession(new OperatorSafeSessionState(Shell.SelectedWorkspace));
		if (_sessionRecovery.PreviousSessionEndedUnexpectedly)
			Shell.SelectWorkspace(_sessionRecovery.SafeState.SelectedWorkspace);
		MediaPool = new MediaPoolInspectorViewModel(viewModel, MediaDeck);
		NdiSources = new NdiSourceDiscoveryViewModel(client);
		Rundown = new RundownViewModel(client, viewModel, MediaPool);
		CompositingGraph = new CompositingGraphViewModel(viewModel, MediaPool);
		HealthProvider = new OperatorHealthSnapshotProvider(viewModel, MediaDeck, Monitoring, ProgramOutput, CompositingGraph);
		HealthCenter = new HealthCenterViewModel(
			HealthProvider,
			viewModel.RuntimeReadiness,
			viewModel.SynchronizeCommand,
			new DispatcherSynchronizationContext(Dispatcher));
		QuickControls = new OperatorQuickControlsViewModel(viewModel, MediaDeck, MediaPool, new OperatorQuickControlStore());
		ShowControl = new ShowControlViewModel(client);
		ProductionMacros = new ProductionMacroViewModel(client);
		viewModel.ConfirmedShowControlSnapshot += ShowControl.ApplyConfirmedSnapshot;

		Timeline.SelectionChanged += OnTimelineSelectionChanged;
		Startup = new StartupLifecycleViewModel(
			Environment.GetEnvironmentVariable("RTAIME_APPHOST_LIFECYCLE_FILE"),
			new DispatcherSynchronizationContext(Dispatcher),
			new SupportBundleExporter(HealthProvider),
			PickSupportBundleDestination);
		WorkspaceStates = new OperatorWorkspaceStateViewModel(
			viewModel,
			MediaPool,
			MediaDeck,
			Timeline,
			CompositingGraph,
			OutputHealth,
			HealthCenter,
			Startup);
		Notifications = new OperatorNotificationCenterViewModel(
			new NotificationService(),
			viewModel,
			new DispatcherSynchronizationContext(Dispatcher), NavigateFromShowChecks, client.ActionHistory);
		ShowReadiness = new ShowReadinessViewModel(client, viewModel, HealthProvider, Notifications,
			NavigateFromShowChecks, new DispatcherSynchronizationContext(Dispatcher), MediaPool);
		OpenShowChecksCommand = new NotificationCommand(OpenShowChecks, () => !_shutdownStarted);
		Notifications.NotifySessionRecovery(_sessionRecovery);
		Shell.PropertyChanged += OnShellPropertyChanged;
		InitializeComponent();
		Startup.PropertyChanged += OnStartupPropertyChanged;
		StartStartupBrandAnimation();
		ApplyWindowPlacement();
		Shell.UpdateViewportSize(
			ActualWidth > 0 ? ActualWidth : Width,
			ActualHeight > 0 ? ActualHeight : Height);
		SizeChanged += OnShellSizeChanged;
		ApplyProductionFullscreen(Shell.IsFullscreen, updateShell: false);
		DataContext = viewModel;
		Composition = new(this);
        AudioMixerView.Inspector = Composition.Inspector;
        AudioMixerView.DataContext = viewModel.AudioMixer;
		MediaLibraryView.Context = Composition.Library;
        Docking = new OperatorDockController(this, LegacyWorkspaceGrid, DockWorkspaceHost,
            new Dictionary<string, FrameworkElement>
            {
                ["TOOLS"] = LeftToolRegion, ["MONITORS"] = CenterWorkspace,
                ["INSPECTOR"] = RightInspectorRegion, ["TIMELINE"] = LowerTimelineRegion,
                ["AUDIO"] = AudioMixerView
            });
        Shell.ConfigureTemplateFiles(PickWorkspaceTemplateImport, PickWorkspaceTemplateExport, Docking.Capture);
        OpenWorkspaceTemplatesCommand = new OperatorShellCommand(OpenWorkspaceTemplates, () => !_shutdownStarted);
		OpenLibraryCommand = new OperatorShellCommand(() => OpenLibrary(), () => !_shutdownStarted);
        OpenInlineHelpCommand = new OperatorShellCommand(() => OpenLibrary("Help"), () => !_shutdownStarted);
        OpenCommandPaletteCommand = new AsyncRelayCommand(() => { OpenCommandPalette(); return Task.CompletedTask; }, () => !_shutdownStarted);
		Shortcuts = OperatorKeyboardCommandRegistry.Create(viewModel, MediaDeck, Timeline, Shell, ShowControl,
			new AsyncRelayCommand(FocusMediaSearchAsync), Docking, OpenCommandPaletteCommand);
		CommandPalette = new OperatorCommandPaletteViewModel(Shortcuts,
			OperatorCommandCatalog.Create(Shortcuts, viewModel, MediaDeck, Shell, MediaPool, OpenShowChecksCommand, OpenLibraryCommand, OpenInlineHelpCommand, OpenWorkspaceTemplatesCommand),
			@operator: viewModel, importPicker: PickShortcutImport, exportPicker: PickShortcutExport);
        InitializeLibrary(viewModel, libraryPreferenceStore);
		viewModel.StartAudioMetering();
		ContentRendered += OnContentRendered;
		Closing += OnClosingAsync;
	}

	public OperatorComposition Composition { get; }
	public OperatorInspectorControl InspectorView => RightInspectorRegion;
	public StartupLifecycleViewModel Startup { get; }
	public ShowReadinessViewModel ShowReadiness { get; }
	public ICommand OpenShowChecksCommand { get; }
    public ICommand OpenWorkspaceTemplatesCommand { get; }
	public OperatorNotificationCenterViewModel Notifications { get; }
	public OperatorWorkspaceStateViewModel WorkspaceStates { get; }
	public OperatorMonitoringViewModel Monitoring { get; }
	public ProgramOutputController ProgramOutput { get; }
	public OutputRoutingHealthViewModel OutputHealth { get; }
	public RuntimePerformanceStatusBarViewModel RuntimePerformanceStatus { get; }
	public OperatorHealthSnapshotProvider HealthProvider { get; }
	public HealthCenterViewModel HealthCenter { get; }
	public OperatorShellViewModel Shell { get; }
	public MediaDeckViewModel MediaDeck { get; }
	public ReplayViewModel Replay { get; }
	public DemoProductionPackageController DemoProduction { get; }
	public MediaPoolInspectorViewModel MediaPool { get; }
	public NdiSourceDiscoveryViewModel NdiSources { get; }
	public RundownViewModel Rundown { get; }
	public CompositingGraphViewModel CompositingGraph { get; }
	public OperatorQuickControlsViewModel QuickControls { get; }
	public ShowControlViewModel ShowControl { get; }
	public ProductionMacroViewModel ProductionMacros { get; }
	public OperatorKeyboardCommandRegistry Shortcuts { get; }
	public OperatorCommandPaletteViewModel CommandPalette { get; }
	public ICommand OpenCommandPaletteCommand { get; }
	public OperatorDockController Docking { get; }
	public MediaTimelineViewModel Timeline => MediaDeck.Timeline;

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		ApplyDarkWindowCaption();
        if (WindowState == WindowState.Normal)
            OperatorMonitorGeometry.Restore(this, OperatorMonitorGeometry.Capture(this));
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (Shortcuts.TryHandle(e))
			return;

		base.OnPreviewKeyDown(e);
	}

	private void ApplyDarkWindowCaption() => ApplyDarkWindowCaption(this);

	internal static void ApplyDarkWindowCaption(Window window)
	{
		var handle = new WindowInteropHelper(window).Handle;
		if (handle == IntPtr.Zero)
			return;

		var darkModeEnabled = 1;
		if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkModeEnabled, sizeof(int)) != 0)
			DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref darkModeEnabled, sizeof(int));

		var captionColor = DarkCaptionColor;
		DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));

		var borderColor = DarkBorderColor;
		DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColor, sizeof(int));

		var textColor = LightCaptionTextColor;
		DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
	}

	[DllImport("dwmapi.dll", PreserveSig = true)]
	private static extern int DwmSetWindowAttribute(
		IntPtr windowHandle,
		int attribute,
		ref int attributeValue,
		int attributeSize);

	private void OpenShowChecks()
	{
		if (_shutdownStarted) return;
		if (_showReadinessWindow is { } existing) { existing.Activate(); return; }
		var previousFocus = Keyboard.FocusedElement;
		var window = new ShowReadinessWindow(ShowReadiness) { Owner = this };
		_showReadinessWindow = window;
		window.Closed += (_, _) =>
		{
			_showReadinessWindow = null;
			if (!_shutdownStarted && previousFocus is UIElement { IsVisible: true, IsEnabled: true } previous)
				previous.Dispatcher.BeginInvoke(() =>
				{
					if (!_shutdownStarted && _showReadinessWindow is null && _commandPaletteWindow is null &&
                        _workspaceTemplatesWindow is null && LibraryWindow is null &&
						previous.IsVisible && previous.IsEnabled) previous.Focus();
				}, DispatcherPriority.Input);
		};
		window.Show();
	}
	private void NavigateFromShowChecks(string workspace)
	{
		if (_shutdownStarted || !OperatorWorkspaceNames.All.Contains(workspace, StringComparer.Ordinal)) return;
		_showReadinessWindow?.Close();
		Shell.SelectWorkspace(workspace);
		Activate();
	}

	private void StartStartupBrandAnimation()
	{
		if (Startup.HasCompletedInitialStartup || !SystemParameters.ClientAreaAnimation)
			return;

		var orbitAnimation = new DoubleAnimation
		{
			From = 0,
			To = 360,
			Duration = TimeSpan.FromMilliseconds(2200),
			RepeatBehavior = RepeatBehavior.Forever
		};
		StartupOrbitRotation.BeginAnimation(RotateTransform.AngleProperty, orbitAnimation);

		var scaleAnimation = new DoubleAnimation
		{
			From = 0.96,
			To = 1.03,
			Duration = TimeSpan.FromMilliseconds(1050),
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever
		};
		StartupBrandScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
		StartupBrandScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);

		var opacityAnimation = new DoubleAnimation
		{
			From = 0.86,
			To = 1.0,
			Duration = TimeSpan.FromMilliseconds(1050),
			AutoReverse = true,
			RepeatBehavior = RepeatBehavior.Forever
		};
		StartupBrandPulse.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
	}

	private void StopStartupBrandAnimation()
	{
		StartupOrbitRotation.BeginAnimation(RotateTransform.AngleProperty, null);
		StartupOrbitRotation.Angle = 0;
		StartupBrandScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
		StartupBrandScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		StartupBrandScale.ScaleX = 1;
		StartupBrandScale.ScaleY = 1;
		StartupBrandPulse.BeginAnimation(UIElement.OpacityProperty, null);
		StartupBrandPulse.Opacity = 1.0;
	}

	private void OnStartupPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(StartupLifecycleViewModel.HasCompletedInitialStartup) &&
			Startup.HasCompletedInitialStartup)
		{
			StopStartupBrandAnimation();
		}
	}

	private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(OperatorShellViewModel.SelectedWorkspace))
			return;

		Library?.Visit(new("workspace", "", Shell.SelectedWorkspace));
        _ = _sessionRecoveryStore.CheckpointAsync(
			new OperatorSafeSessionState(Shell.SelectedWorkspace));
	}

	private void OnCommandPaletteClick(object sender, RoutedEventArgs e) => OpenCommandPalette();

	internal void OpenCommandPalette()
	{
		if (_shutdownStarted) return;
		CommandPalette.BeginSearch();
		if (_commandPaletteWindow is { } existing) { existing.Activate(); existing.FocusSearch(); return; }
		var focusedWindow = Keyboard.FocusedElement is DependencyObject focused ? Window.GetWindow(focused) : null;
		var palette = new OperatorCommandPaletteWindow(CommandPalette) { Owner = focusedWindow is OperatorPanelWindow ? focusedWindow : this };
		_commandPaletteWindow = palette;
		palette.Closed += (_, _) => _commandPaletteWindow = null;
		palette.Show();
		_ = CommandPalette.LoadPreferencesAsync();
	}
	private static string? PickShortcutImport()
	{
		var dialog = new OpenFileDialog { Title = "Import shortcut profile", Filter = "Shortcut profile (*.json)|*.json", CheckFileExists = true };
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}
	private static string? PickShortcutExport()
	{
		var dialog = new SaveFileDialog { Title = "Export shortcut profile", Filter = "Shortcut profile (*.json)|*.json",
			DefaultExt = ".json", AddExtension = true, OverwritePrompt = true, FileName = "rtaime-shortcuts.json" };
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}

	private Task FocusMediaSearchAsync()
	{
		Shell.SelectWorkspace("MEDIA");
		Dispatcher.BeginInvoke(() =>
		{
			if (_shutdownStarted) return;
			if (Shell.LeftColumnWidth.Value <= 0 || !MediaLibraryView.IsVisible || !LeftToolRegion.IsVisible)
				Docking.Float("TOOLS");
			Dispatcher.BeginInvoke(() =>
			{
				if (_shutdownStarted) return;
				MediaLibraryView.FocusSearch();
			}, DispatcherPriority.Input);
		}, DispatcherPriority.Input);
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

	private static string? PickSupportBundleDestination()
	{
		var sessionId = Environment.GetEnvironmentVariable("RTAIME_LOG_SESSION_ID");
		var sessionSuffix = string.IsNullOrWhiteSpace(sessionId)
			? string.Empty
			: "-" + sessionId.Trim()[..Math.Min(8, sessionId.Trim().Length)];
		var dialog = new SaveFileDialog
		{
			Title = "Export rtaime support bundle",
			Filter = "rtaime Support Bundle (*.zip)|*.zip",
			DefaultExt = ".zip",
			AddExtension = true,
			OverwritePrompt = true,
			FileName = $"rtaime-support-{DateTimeOffset.Now:yyyyMMdd-HHmmss}{sessionSuffix}.zip"
		};
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}

	private static string? PickLocalMediaFile()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Open local media",
			Filter = "Local video (*.mp4;*.mov)|*.mp4;*.mov",
			CheckFileExists = true,
			Multiselect = false
		};
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}

	private static IReadOnlyList<string> PickLocalMediaFiles()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Import media into library",
			Filter = "Local video (*.mp4;*.mov)|*.mp4;*.mov",
			CheckFileExists = true,
			Multiselect = true
		};
		return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
	}

	private void OnContentRendered(object? sender, EventArgs e)
	{
		ContentRendered -= OnContentRendered;
		SynchronizeButton.Focus();
		_ = CommandPalette.LoadPreferencesAsync();
        _ = LoadLibraryAsync();
		_ = MediaPool.LoadSmartBinsAsync();
		_ = MediaPool.LoadCatalogAsync();
		_ = NdiSources.RefreshAsync();
		_ = Rundown.RefreshAsync();
		if (DataContext is OperatorViewModel viewModel &&
			viewModel.SynchronizeCommand.CanExecute(null))
		{
			viewModel.SynchronizeCommand.Execute(null);
		}
	}

	private async void OnClosingAsync(object? sender, CancelEventArgs e)
	{
		if (_shutdownComplete)
			return;

		e.Cancel = true;
		if (_shutdownStarted)
			return;

		_shutdownStarted = true;
		IsEnabled = false;
		var completedNormally = false;
		try
		{
			if (!_fullscreenApplied)
				CaptureWindowPlacement();
			await Shell.StopTemplateFilesAsync();
			_workspaceTemplatesWindow?.Close();
			_commandPaletteWindow?.Close();
            CloseLibrary();
            await Library.FlushAsync();
			await CommandPalette.FlushAsync();
			_showReadinessWindow?.Close();
			ShowReadiness.Dispose();
			CommandPalette.Dispose();
			Docking.Dispose();
			await Shell.SaveAsync();
			MediaPool.CancelHover();
			await MediaPool.FlushSmartBinsAsync();
			Shell.PropertyChanged -= OnShellPropertyChanged;
			Startup.PropertyChanged -= OnStartupPropertyChanged;
			WorkspaceStates.Dispose();
			StopStartupBrandAnimation();
			Startup.Dispose();
			HealthCenter.Dispose();
			HealthProvider.Dispose();
			RuntimePerformanceStatus.Dispose();
			OutputHealth.Dispose();
			ProgramOutput.Dispose();
			if (DataContext is OperatorViewModel viewModel)
			{
				MediaDeck.SnapshotChanged -= viewModel.ApplyMediaDeckSnapshot;
				viewModel.ConfirmedMediaDeckSnapshot -= MediaDeck.ApplyConfirmedSnapshot;
				viewModel.ConfirmedReplaySnapshot -= Replay.ApplyConfirmedSnapshot;
				Timeline.SelectionChanged -= OnTimelineSelectionChanged;
				QuickControls.Dispose();
				CompositingGraph.Dispose();
				MediaPool.Dispose();
				await Monitoring.DisposeAsync();
				await MediaDeck.DisposeAsync();
				await viewModel.DisposeAsync();
			}
			else
			{
				Timeline.SelectionChanged -= OnTimelineSelectionChanged;
				QuickControls.Dispose();
				CompositingGraph.Dispose();
				MediaPool.Dispose();
				await Monitoring.DisposeAsync();
				await MediaDeck.DisposeAsync();
			}
			completedNormally = true;
		}
		finally
		{
			Notifications.Dispose();
			if (completedNormally &&
				!(Application.Current is App { UnexpectedFailureDetected: true }))
			{
				await _sessionRecoveryStore.MarkCleanShutdownAsync(
					new OperatorSafeSessionState(Shell.SelectedWorkspace));
			}

			_shutdownComplete = true;
			Closing -= OnClosingAsync;
			SizeChanged -= OnShellSizeChanged;
			Close();
		}
	}

    private void OpenWorkspaceTemplates()
    {
        if (_workspaceTemplatesWindow is { } existing) { existing.Activate(); return; }
        var reference = Keyboard.FocusedElement is UIElement focused ? new WeakReference<UIElement>(focused) : null;
        _workspaceTemplatesWindow = new WorkspaceTemplatesWindow(this);
        _workspaceTemplatesWindow.Closed += (_, _) =>
        {
            _workspaceTemplatesWindow = null;
            if (!_shutdownStarted && reference is not null && reference.TryGetTarget(out var previous) && previous.IsVisible && previous.IsEnabled)
                Dispatcher.BeginInvoke(() =>
                {
                    var newer = Application.Current?.Windows.OfType<Window>().Any(window => window.IsVisible &&
                        window is rtaime.Operator.WorkspaceTemplatesWindow or OperatorCommandPaletteWindow or OperatorLibraryWindow or rtaime.Operator.ShowReadinessWindow) == true;
                    if (!_shutdownStarted && !newer && previous.IsVisible && previous.IsEnabled)
                    { Window.GetWindow(previous)?.Activate(); previous.Focus(); }
                }, DispatcherPriority.Input);
        };
        _workspaceTemplatesWindow.Show();
    }
    private string? PickWorkspaceTemplateImport()
    {
        var dialog = new OpenFileDialog { Title = "Import workspace layout", Filter = "Layout template (*.json)|*.json", CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
    private string? PickWorkspaceTemplateExport()
    {
        var dialog = new SaveFileDialog { Title = "Export workspace layout", Filter = "Layout template (*.json)|*.json", DefaultExt = ".json", AddExtension = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
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
		Shell.UpdateViewportSize(e.NewSize.Width, e.NewSize.Height);

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

	private void OnHelpClick(object sender, RoutedEventArgs e) => OpenLibrary("Help");

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
