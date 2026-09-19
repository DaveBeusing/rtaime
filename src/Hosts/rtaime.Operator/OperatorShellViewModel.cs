// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed record OperatorWorkspaceLayoutSettings(
	double LeftPanelWidth,
	double RightPanelWidth,
	double LowerPanelHeight,
	bool IsLeftCollapsed,
	bool IsRightCollapsed,
	bool IsCenterMaximized,
	string ViewerMode)
{
	public OperatorWorkspaceLayoutSettings Normalize() => this with
	{
		LeftPanelWidth = ClampFinite(
			LeftPanelWidth,
			OperatorLayoutSettings.MinimumLeftPanelWidth,
			OperatorLayoutSettings.MaximumLeftPanelWidth,
			OperatorLayoutSettings.DefaultLeftPanelWidth),
		RightPanelWidth = ClampFinite(
			RightPanelWidth,
			OperatorLayoutSettings.MinimumRightPanelWidth,
			OperatorLayoutSettings.MaximumRightPanelWidth,
			OperatorLayoutSettings.DefaultRightPanelWidth),
		LowerPanelHeight = ClampFinite(
			LowerPanelHeight,
			OperatorLayoutSettings.MinimumLowerPanelHeight,
			OperatorLayoutSettings.MaximumLowerPanelHeight,
			OperatorLayoutSettings.DefaultLowerPanelHeight),
		ViewerMode = ViewerMode is "PREVIEW" or "PROGRAM" ? ViewerMode : "DUAL"
	};

	private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
		double.IsFinite(value)
			? Math.Clamp(value, minimum, maximum)
			: fallback;
}

public sealed record OperatorWindowPlacementSettings(
	double? Left,
	double? Top,
	double Width,
	double Height,
	string State)
{
	public const double DefaultWidth = 1600;
	public const double DefaultHeight = 900;
	public const double MinimumWidth = 960;
	public const double MinimumHeight = 500;
	public const double MaximumWidth = 7680;
	public const double MaximumHeight = 4320;

	public static OperatorWindowPlacementSettings Default { get; } = new(
		null,
		null,
		DefaultWidth,
		DefaultHeight,
		"NORMAL");

	public OperatorWindowPlacementSettings Normalize() => this with
	{
		Left = NormalizeCoordinate(Left),
		Top = NormalizeCoordinate(Top),
		Width = ClampFinite(Width, MinimumWidth, MaximumWidth, DefaultWidth),
		Height = ClampFinite(Height, MinimumHeight, MaximumHeight, DefaultHeight),
		State = string.Equals(State?.Trim(), "MAXIMIZED", StringComparison.OrdinalIgnoreCase)
			? "MAXIMIZED"
			: "NORMAL"
	};

	private static double? NormalizeCoordinate(double? value) =>
		value is { } coordinate && double.IsFinite(coordinate) ? coordinate : null;

	private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
		double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public sealed record OperatorLayoutSettings
{
	public const int CurrentVersion = 3;
	public const double DefaultLeftPanelWidth = 248;
	public const double DefaultRightPanelWidth = 320;
	public const double DefaultLowerPanelHeight = 420;
	public const double MinimumLeftPanelWidth = 180;
	public const double MaximumLeftPanelWidth = 520;
	public const double MinimumRightPanelWidth = 240;
	public const double MaximumRightPanelWidth = 620;
	public const double MinimumLowerPanelHeight = 320;
	public const double MaximumLowerPanelHeight = 680;
	public const double CompactLowerPanelHeight = 220;
	public const double CompactViewportWidth = 1100;

	public int Version { get; init; } = CurrentVersion;
	public bool IsFullscreen { get; init; }
	public string SelectedWorkspace { get; init; } = OperatorWorkspaceNames.Live;
	public OperatorWindowPlacementSettings WindowPlacement { get; init; } = OperatorWindowPlacementSettings.Default;
	public Dictionary<string, OperatorWorkspaceLayoutSettings> Workspaces { get; init; } = CreateCanonicalLayouts();

	public static OperatorLayoutSettings Default => new();

	public OperatorLayoutSettings Normalize()
	{
		if (Version is < 2 or > CurrentVersion)
			return Default;

		var selected = OperatorWorkspaceNames.Normalize(SelectedWorkspace);
		var layouts = CreateCanonicalLayouts();
		if (Workspaces is not null)
		{
			if (Workspaces.TryGetValue("GRAPHICS", out var legacyGraphics) && legacyGraphics is not null)
				layouts[OperatorWorkspaceNames.Compositing] = legacyGraphics.Normalize();
			if (Workspaces.TryGetValue("SYSTEM", out var legacySystem) && legacySystem is not null)
			{
				layouts[OperatorWorkspaceNames.Outputs] = legacySystem.Normalize();
				layouts[OperatorWorkspaceNames.Settings] = legacySystem.Normalize();
			}

			foreach (var workspace in OperatorWorkspaceNames.All)
			{
				if (Workspaces.TryGetValue(workspace, out var layout) && layout is not null)
					layouts[workspace] = layout.Normalize();
			}
		}

		return this with
		{
			Version = CurrentVersion,
			SelectedWorkspace = selected,
			WindowPlacement = (WindowPlacement ?? OperatorWindowPlacementSettings.Default).Normalize(),
			Workspaces = layouts
		};
	}

	public static Dictionary<string, OperatorWorkspaceLayoutSettings> CreateCanonicalLayouts() => new(StringComparer.Ordinal)
	{
		[OperatorWorkspaceNames.Live] = new(
			220,
			300,
			340,
			false,
			false,
			false,
			"DUAL"),
		[OperatorWorkspaceNames.Edit] = new(
			280,
			350,
			560,
			false,
			false,
			false,
			"DUAL"),
		[OperatorWorkspaceNames.Media] = new(
			460,
			360,
			360,
			false,
			false,
			false,
			"PREVIEW"),
		[OperatorWorkspaceNames.Scenes] = new(
			260,
			390,
			360,
			false,
			false,
			false,
			"DUAL"),
		[OperatorWorkspaceNames.Compositing] = new(
			300,
			390,
			360,
			false,
			false,
			false,
			"DUAL"),
		[OperatorWorkspaceNames.Outputs] = new(
			220,
			420,
			340,
			true,
			false,
			false,
			"PROGRAM"),
		[OperatorWorkspaceNames.Settings] = new(
			220,
			460,
			340,
			true,
			true,
			false,
			"PROGRAM")
	};
}

public static class OperatorWorkspaceNames
{
	public const string Live = "LIVE";
	public const string Edit = "EDIT";
	public const string Media = "MEDIA";
	public const string Scenes = "SCENES";
	public const string Compositing = "COMPOSITING";
	public const string Outputs = "OUTPUTS";
	public const string Settings = "SETTINGS";

	public static IReadOnlyList<string> All { get; } =
	[
		Media,
		Edit,
		Live,
		Scenes,
		Compositing,
		Outputs,
		Settings
	];

	public static string Normalize(string? value)
	{
		var normalized = value?.Trim().ToUpperInvariant();
		return normalized switch
		{
			"GRAPHICS" => Compositing,
			"SYSTEM" => Settings,
			_ when All.Contains(normalized, StringComparer.Ordinal) => normalized!,
			_ => Live
		};
	}
}

public sealed class OperatorLayoutStore
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true
	};

	private readonly string _path;
	private readonly object _saveSync = new();
	private Task _saveTail = Task.CompletedTask;

	public OperatorLayoutStore(string? path = null)
	{
		_path = path ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"rtaime",
			"operator-layout.json");
	}

	public OperatorLayoutSettings Load()
	{
		try
		{
			if (!File.Exists(_path))
				return OperatorLayoutSettings.Default;

			var json = File.ReadAllText(_path);
			return (JsonSerializer.Deserialize<OperatorLayoutSettings>(json, SerializerOptions) ?? OperatorLayoutSettings.Default)
				.Normalize();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return OperatorLayoutSettings.Default;
		}
	}

	public Task SaveAsync(OperatorLayoutSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		var normalized = settings.Normalize();
		var json = JsonSerializer.Serialize(normalized, SerializerOptions);

		lock (_saveSync)
		{
			_saveTail = _saveTail
				.ContinueWith(
					_ => WriteAsync(json),
					CancellationToken.None,
					TaskContinuationOptions.None,
					TaskScheduler.Default)
				.Unwrap();
			return _saveTail;
		}
	}

	private async Task WriteAsync(string json)
	{
		var temporaryPath = _path + ".tmp";
		try
		{
			var directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);

			await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
			File.Move(temporaryPath, _path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Layout persistence is best-effort and must never affect production operation.
		}
		finally
		{
			try
			{
				if (File.Exists(temporaryPath))
					File.Delete(temporaryPath);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Temporary-file cleanup is best-effort for the same reason as layout persistence.
			}
		}
	}
}

public sealed class OperatorShellViewModel : INotifyPropertyChanged
{
	private readonly OperatorLayoutStore _store;
	private readonly Action<bool> _setFullscreen;
	private readonly Dictionary<string, OperatorWorkspaceLayoutSettings> _workspaceLayouts;
	private double _leftPanelWidth;
	private double _rightPanelWidth;
	private double _lowerPanelHeight;
	private bool _isLeftCollapsed;
	private bool _isRightCollapsed;
	private bool _isCenterMaximized;
	private bool _isFullscreen;
	private bool _monitorFullscreenActive;
	private string _monitorFullscreenRestoreViewerMode = "DUAL";
	private bool _monitorFullscreenRestoreCenterMaximized;
	private string _selectedWorkspace;
	private string _viewerMode;
	private OperatorWindowPlacementSettings _windowPlacement;
	private double _viewportWidth = 1600;

	public OperatorShellViewModel(
		OperatorLayoutStore store,
		Action<bool> setFullscreen)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_setFullscreen = setFullscreen ?? throw new ArgumentNullException(nameof(setFullscreen));
		var settings = _store.Load().Normalize();
		_workspaceLayouts = new Dictionary<string, OperatorWorkspaceLayoutSettings>(
			settings.Workspaces,
			StringComparer.Ordinal);
		_isFullscreen = settings.IsFullscreen;
		_selectedWorkspace = settings.SelectedWorkspace;
		_windowPlacement = settings.WindowPlacement.Normalize();
		var layout = _workspaceLayouts[_selectedWorkspace];
		_leftPanelWidth = layout.LeftPanelWidth;
		_rightPanelWidth = layout.RightPanelWidth;
		_lowerPanelHeight = layout.LowerPanelHeight;
		_isLeftCollapsed = layout.IsLeftCollapsed;
		_isRightCollapsed = layout.IsRightCollapsed;
		_isCenterMaximized = layout.IsCenterMaximized;
		_viewerMode = layout.ViewerMode;

		ToggleLeftPanelCommand = new OperatorShellCommand(
			ToggleLeftPanel,
			() => HasLeftRegion);
		ToggleRightPanelCommand = new OperatorShellCommand(ToggleRightPanel);
		ToggleCenterMaximizeCommand = new OperatorShellCommand(ToggleCenterMaximize);
		ToggleFullscreenCommand = new OperatorShellCommand(ToggleFullscreen);
		ExitFullscreenCommand = new OperatorShellCommand(
			ExitFullscreen,
			() => IsFullscreen);
		FullscreenPreviewCommand = new OperatorShellCommand(() => EnterMonitorFullscreen("PREVIEW"));
		FullscreenProgramCommand = new OperatorShellCommand(() => EnterMonitorFullscreen("PROGRAM"));
		SaveLayoutCommand = new OperatorShellCommand(Save);
		ResetLayoutCommand = new OperatorShellCommand(ResetLayout);
		SelectWorkspaceCommand = new OperatorShellCommand(
			parameter => SelectWorkspace(parameter?.ToString()));
		MaximizePreviewCommand = new OperatorShellCommand(() => SetViewerMode("PREVIEW"));
		MaximizeProgramCommand = new OperatorShellCommand(() => SetViewerMode("PROGRAM"));
		RestoreViewersCommand = new OperatorShellCommand(
			() => SetViewerMode("DUAL"),
			() => !string.Equals(ViewerMode, "DUAL", StringComparison.Ordinal));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public IReadOnlyList<string> Workspaces => OperatorWorkspaceNames.All;
	public OperatorWindowPlacementSettings WindowPlacement => _windowPlacement;
	public bool IsCompactViewport => _viewportWidth < OperatorLayoutSettings.CompactViewportWidth;
	public double NavigationRailWidth => _viewportWidth < 1320 ? 54 : 86;
	public Visibility NavigationLabelVisibility => _viewportWidth < 1320 ? Visibility.Collapsed : Visibility.Visible;
	public Visibility SecondaryMetricVisibility => _viewportWidth < 1480 ? Visibility.Collapsed : Visibility.Visible;
	public Visibility CompactOptionalVisibility => IsCompactViewport ? Visibility.Collapsed : Visibility.Visible;

	public ICommand ToggleLeftPanelCommand { get; }
	public ICommand ToggleRightPanelCommand { get; }
	public ICommand ToggleCenterMaximizeCommand { get; }
	public ICommand ToggleFullscreenCommand { get; }
	public ICommand ExitFullscreenCommand { get; }
	public ICommand SaveLayoutCommand { get; }
	public ICommand ResetLayoutCommand { get; }
	public ICommand SelectWorkspaceCommand { get; }
	public ICommand MaximizePreviewCommand { get; }
	public ICommand MaximizeProgramCommand { get; }
	public ICommand RestoreViewersCommand { get; }
	public ICommand FullscreenPreviewCommand { get; }
	public ICommand FullscreenProgramCommand { get; }

	public double LeftPanelWidth
	{
		get => _leftPanelWidth;
		private set
		{
			var normalized = Math.Clamp(value, OperatorLayoutSettings.MinimumLeftPanelWidth, OperatorLayoutSettings.MaximumLeftPanelWidth);
			if (Set(ref _leftPanelWidth, normalized))
				OnPropertyChanged(nameof(LeftColumnWidth));
		}
	}

	public double RightPanelWidth
	{
		get => _rightPanelWidth;
		private set
		{
			var normalized = Math.Clamp(value, OperatorLayoutSettings.MinimumRightPanelWidth, OperatorLayoutSettings.MaximumRightPanelWidth);
			if (Set(ref _rightPanelWidth, normalized))
				OnPropertyChanged(nameof(RightColumnWidth));
		}
	}

	public double LowerPanelHeight
	{
		get => _lowerPanelHeight;
		private set
		{
			var normalized = Math.Clamp(value, OperatorLayoutSettings.MinimumLowerPanelHeight, OperatorLayoutSettings.MaximumLowerPanelHeight);
			if (Set(ref _lowerPanelHeight, normalized))
				OnPropertyChanged(nameof(LowerRowHeight));
		}
	}

	public GridLength LeftColumnWidth
	{
		get => new(IsCenterMaximized || IsLeftCollapsed || !HasLeftRegion || IsCompactViewport ? 0 : LeftPanelWidth);
		set
		{
			if (!IsCompactViewport && !IsCenterMaximized && !IsLeftCollapsed && HasLeftRegion && value.IsAbsolute && value.Value > 0)
				LeftPanelWidth = value.Value;
		}
	}

	public GridLength RightColumnWidth
	{
		get => new(IsCenterMaximized || IsRightCollapsed || IsCompactViewport ? 0 : RightPanelWidth);
		set
		{
			if (!IsCompactViewport && !IsCenterMaximized && !IsRightCollapsed && value.IsAbsolute && value.Value > 0)
				RightPanelWidth = value.Value;
		}
	}

	public GridLength LowerRowHeight
	{
		get => new(IsCenterMaximized || !HasTimelineRegion ? 0 : IsCompactViewport ? Math.Min(LowerPanelHeight, OperatorLayoutSettings.CompactLowerPanelHeight) : LowerPanelHeight);
		set
		{
			if (!IsCompactViewport && !IsCenterMaximized && HasTimelineRegion && value.IsAbsolute && value.Value > 0)
				LowerPanelHeight = value.Value;
		}
	}

	public double LeftSplitterWidth => IsCenterMaximized || IsLeftCollapsed || !HasLeftRegion || IsCompactViewport ? 0 : 5;
	public double RightSplitterWidth => IsCenterMaximized || IsRightCollapsed || IsCompactViewport ? 0 : 5;
	public double LowerSplitterHeight => IsCenterMaximized || !HasTimelineRegion || IsCompactViewport ? 0 : 5;

	public bool IsLeftCollapsed
	{
		get => _isLeftCollapsed;
		private set
		{
			if (!Set(ref _isLeftCollapsed, value))
				return;
			RaiseLayoutGeometryChanged();
		}
	}

	public bool IsRightCollapsed
	{
		get => _isRightCollapsed;
		private set
		{
			if (!Set(ref _isRightCollapsed, value))
				return;
			RaiseLayoutGeometryChanged();
		}
	}

	public bool IsCenterMaximized
	{
		get => _isCenterMaximized;
		private set
		{
			if (!Set(ref _isCenterMaximized, value))
				return;
			RaiseLayoutGeometryChanged();
			OnPropertyChanged(nameof(CenterModeLabel));
		}
	}

	public bool IsFullscreen
	{
		get => _isFullscreen;
		private set
		{
			if (!Set(ref _isFullscreen, value))
				return;
			OnPropertyChanged(nameof(FullscreenLabel));
			(ExitFullscreenCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		}
	}

	public string SelectedWorkspace
	{
		get => _selectedWorkspace;
		private set
		{
			if (!Set(ref _selectedWorkspace, OperatorWorkspaceNames.Normalize(value)))
				return;
			RaiseWorkspaceChanged();
		}
	}

	public string WorkspaceTitle => $"{SelectedWorkspace} WORKSPACE";
	public string FullscreenLabel => IsFullscreen ? "WINDOWED  F11" : "FULLSCREEN  F11";
	public string CenterModeLabel => IsCenterMaximized ? "RESTORE PANELS" : "MAXIMIZE VIEW";

	public bool IsLiveWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Live, StringComparison.Ordinal);
	public bool IsEditWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Edit, StringComparison.Ordinal);
	public bool IsMediaWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Media, StringComparison.Ordinal);
	public bool IsScenesWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Scenes, StringComparison.Ordinal);
	public bool IsCompositingWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Compositing, StringComparison.Ordinal);
	public bool IsOutputsWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Outputs, StringComparison.Ordinal);
	public bool IsSettingsWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Settings, StringComparison.Ordinal);

	public bool HasLeftRegion => IsLiveWorkspace || IsEditWorkspace || IsMediaWorkspace || IsScenesWorkspace || IsCompositingWorkspace;
	public bool HasTimelineRegion => IsEditWorkspace || IsMediaWorkspace || IsScenesWorkspace || IsCompositingWorkspace;
	public Visibility LeftRegionVisibility => HasLeftRegion ? Visibility.Visible : Visibility.Collapsed;
	public Visibility TimelineRegionVisibility => HasTimelineRegion ? Visibility.Visible : Visibility.Collapsed;
	public bool HasAuxiliaryWorkspaceColumn => IsScenesWorkspace || IsCompositingWorkspace || IsOutputsWorkspace || IsSettingsWorkspace;
	public GridLength AuxiliaryWorkspaceColumnWidth => HasAuxiliaryWorkspaceColumn && !IsCompactViewport ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
	public double AuxiliaryWorkspaceColumnMinWidth => HasAuxiliaryWorkspaceColumn && !IsCompactViewport ? 300 : 0;
	public double AuxiliaryWorkspaceGapWidth => HasAuxiliaryWorkspaceColumn && !IsCompactViewport ? 14 : 0;

	public Visibility MultiviewVisibility => IsLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility CompositingGraphVisibility => IsCompositingWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility StandardViewerVisibility => IsLiveWorkspace || IsCompositingWorkspace ? Visibility.Collapsed : Visibility.Visible;
	public Visibility QuickControlsVisibility => IsLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility ProductionControlsVisibility => IsEditWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility MediaDeckVisibility => IsEditWorkspace || IsMediaWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility SourceBinVisibility => IsMediaWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility MediaLibraryLeftVisibility => IsLiveWorkspace ? Visibility.Collapsed : Visibility.Visible;
	public Visibility LiveSceneCueVisibility => IsLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility InspectorVisibility => IsLiveWorkspace ? Visibility.Collapsed : Visibility.Visible;
	public Visibility LiveControlsVisibility => IsLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility OutputRoutingVisibility => IsOutputsWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility SystemWorkspaceVisibility => IsSettingsWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility SystemStatusVisibility => IsSettingsWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility MonitoringVisibility => IsOutputsWorkspace || IsSettingsWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility GraphicsVisibility => IsScenesWorkspace || IsCompositingWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility AudioVisibility => IsOutputsWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility RecordingVisibility => IsOutputsWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility AIVisibility => IsCompositingWorkspace || IsSettingsWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility OperatorStateVisibility => IsSettingsWorkspace ? Visibility.Visible : Visibility.Collapsed;

	public string ViewerMode
	{
		get => _viewerMode;
		private set
		{
			if (!Set(ref _viewerMode, value))
				return;
			OnPropertyChanged(nameof(PreviewViewerWidth));
			OnPropertyChanged(nameof(ProgramViewerWidth));
			OnPropertyChanged(nameof(PreviewViewerVisibility));
			OnPropertyChanged(nameof(ProgramViewerVisibility));
			OnPropertyChanged(nameof(ViewerGapWidth));
			OnPropertyChanged(nameof(ViewerModeLabel));
			(RestoreViewersCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		}
	}

	public GridLength PreviewViewerWidth => ViewerMode switch
	{
		"PREVIEW" => new GridLength(1, GridUnitType.Star),
		"PROGRAM" => new GridLength(0),
		_ => new GridLength(0.85, GridUnitType.Star)
	};

	public GridLength ProgramViewerWidth => ViewerMode switch
	{
		"PREVIEW" => new GridLength(0),
		"PROGRAM" => new GridLength(1, GridUnitType.Star),
		_ => new GridLength(1.15, GridUnitType.Star)
	};

	public Visibility PreviewViewerVisibility =>
		string.Equals(ViewerMode, "PROGRAM", StringComparison.Ordinal) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility ProgramViewerVisibility =>
		string.Equals(ViewerMode, "PREVIEW", StringComparison.Ordinal) ? Visibility.Collapsed : Visibility.Visible;

	public double ViewerGapWidth => string.Equals(ViewerMode, "DUAL", StringComparison.Ordinal) ? 12 : 0;
	public string ViewerModeLabel => ViewerMode switch
	{
		"PREVIEW" => "PREVIEW MAXIMIZED",
		"PROGRAM" => "PROGRAM MAXIMIZED",
		_ => "DUAL VIEW"
	};

	public void SetFullscreenState(bool fullscreen)
	{
		IsFullscreen = fullscreen;
		if (!_monitorFullscreenActive)
			Save();
	}

	public void SetWindowPlacement(double? left, double? top, double width, double height, WindowState state)
	{
		_windowPlacement = new OperatorWindowPlacementSettings(
			left,
			top,
			width,
			height,
			state == WindowState.Maximized ? "MAXIMIZED" : "NORMAL").Normalize();
		OnPropertyChanged(nameof(WindowPlacement));
	}

	public void UpdateViewportWidth(double width)
	{
		if (!double.IsFinite(width) || width <= 0 || Math.Abs(_viewportWidth - width) < 0.5)
			return;

		var previousCompactNavigation = _viewportWidth < 1320;
		var previousCompactWorkspace = IsCompactViewport;
		var previousSecondaryMetrics = _viewportWidth < 1480;
		_viewportWidth = width;

		if (previousCompactNavigation != (_viewportWidth < 1320))
		{
			OnPropertyChanged(nameof(NavigationRailWidth));
			OnPropertyChanged(nameof(NavigationLabelVisibility));
		}
		if (previousCompactWorkspace != IsCompactViewport)
		{
			OnPropertyChanged(nameof(IsCompactViewport));
			OnPropertyChanged(nameof(CompactOptionalVisibility));
			OnPropertyChanged(nameof(AuxiliaryWorkspaceColumnWidth));
			OnPropertyChanged(nameof(AuxiliaryWorkspaceColumnMinWidth));
			OnPropertyChanged(nameof(AuxiliaryWorkspaceGapWidth));
			RaiseLayoutGeometryChanged();
		}
		if (previousSecondaryMetrics != (_viewportWidth < 1480))
			OnPropertyChanged(nameof(SecondaryMetricVisibility));
	}

	public void SelectWorkspace(string? workspace)
	{
		var normalized = OperatorWorkspaceNames.Normalize(workspace);
		if (string.Equals(normalized, SelectedWorkspace, StringComparison.Ordinal))
			return;

		CaptureCurrentWorkspace();
		SelectedWorkspace = normalized;
		ApplyWorkspaceLayout(_workspaceLayouts[SelectedWorkspace]);
		Save();
	}

	public void Save() => _ = SaveAsync();

	public Task SaveAsync()
	{
		CaptureCurrentWorkspace();
		return _store.SaveAsync(new OperatorLayoutSettings
		{
			Version = OperatorLayoutSettings.CurrentVersion,
			IsFullscreen = IsFullscreen,
			SelectedWorkspace = SelectedWorkspace,
			WindowPlacement = _windowPlacement,
			Workspaces = new Dictionary<string, OperatorWorkspaceLayoutSettings>(_workspaceLayouts, StringComparer.Ordinal)
		});
	}

	private void ToggleLeftPanel()
	{
		if (!HasLeftRegion)
			return;
		IsLeftCollapsed = !IsLeftCollapsed;
		Save();
	}

	private void ToggleRightPanel()
	{
		IsRightCollapsed = !IsRightCollapsed;
		Save();
	}

	private void ToggleCenterMaximize()
	{
		IsCenterMaximized = !IsCenterMaximized;
		Save();
	}

	private void ToggleFullscreen()
	{
		if (IsFullscreen)
		{
			ExitFullscreen();
			return;
		}

		_setFullscreen(true);
	}

	private void EnterMonitorFullscreen(string mode)
	{
		if (!_monitorFullscreenActive)
		{
			_monitorFullscreenRestoreViewerMode = ViewerMode;
			_monitorFullscreenRestoreCenterMaximized = IsCenterMaximized;
		}

		_monitorFullscreenActive = true;
		ViewerMode = mode is "PREVIEW" or "PROGRAM" ? mode : "DUAL";
		IsCenterMaximized = true;
		_setFullscreen(true);
	}

	private void ExitFullscreen()
	{
		if (!_monitorFullscreenActive)
		{
			_setFullscreen(false);
			return;
		}

		_setFullscreen(false);
		ViewerMode = _monitorFullscreenRestoreViewerMode;
		IsCenterMaximized = _monitorFullscreenRestoreCenterMaximized;
		_monitorFullscreenActive = false;
		Save();
	}

	private void SetViewerMode(string mode)
	{
		ViewerMode = mode is "PREVIEW" or "PROGRAM" ? mode : "DUAL";
		Save();
	}

	private void ResetLayout()
	{
		var defaults = OperatorLayoutSettings.CreateCanonicalLayouts()[SelectedWorkspace];
		_workspaceLayouts[SelectedWorkspace] = defaults;
		ApplyWorkspaceLayout(defaults);
		Save();
	}

	private void CaptureCurrentWorkspace() =>
		_workspaceLayouts[SelectedWorkspace] = new OperatorWorkspaceLayoutSettings(
			LeftPanelWidth,
			RightPanelWidth,
			LowerPanelHeight,
			IsLeftCollapsed,
			IsRightCollapsed,
			IsCenterMaximized,
			ViewerMode).Normalize();

	private void ApplyWorkspaceLayout(OperatorWorkspaceLayoutSettings layout)
	{
		var normalized = layout.Normalize();
		_leftPanelWidth = normalized.LeftPanelWidth;
		_rightPanelWidth = normalized.RightPanelWidth;
		_lowerPanelHeight = normalized.LowerPanelHeight;
		_isLeftCollapsed = normalized.IsLeftCollapsed;
		_isRightCollapsed = normalized.IsRightCollapsed;
		_isCenterMaximized = normalized.IsCenterMaximized;
		_viewerMode = normalized.ViewerMode;
		RaiseLayoutGeometryChanged();
		OnPropertyChanged(nameof(IsLeftCollapsed));
		OnPropertyChanged(nameof(IsRightCollapsed));
		OnPropertyChanged(nameof(IsCenterMaximized));
		OnPropertyChanged(nameof(CenterModeLabel));
		OnPropertyChanged(nameof(ViewerMode));
		OnPropertyChanged(nameof(PreviewViewerWidth));
		OnPropertyChanged(nameof(ProgramViewerWidth));
		OnPropertyChanged(nameof(PreviewViewerVisibility));
		OnPropertyChanged(nameof(ProgramViewerVisibility));
		OnPropertyChanged(nameof(ViewerGapWidth));
		OnPropertyChanged(nameof(ViewerModeLabel));
		(RestoreViewersCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
	}

	private void RaiseWorkspaceChanged()
	{
		OnPropertyChanged(nameof(WorkspaceTitle));
		OnPropertyChanged(nameof(HasLeftRegion));
		OnPropertyChanged(nameof(HasTimelineRegion));
		OnPropertyChanged(nameof(LeftRegionVisibility));
		OnPropertyChanged(nameof(TimelineRegionVisibility));
		OnPropertyChanged(nameof(IsLiveWorkspace));
		OnPropertyChanged(nameof(IsEditWorkspace));
		OnPropertyChanged(nameof(IsMediaWorkspace));
		OnPropertyChanged(nameof(IsScenesWorkspace));
		OnPropertyChanged(nameof(IsCompositingWorkspace));
		OnPropertyChanged(nameof(IsOutputsWorkspace));
		OnPropertyChanged(nameof(IsSettingsWorkspace));
		OnPropertyChanged(nameof(HasAuxiliaryWorkspaceColumn));
		OnPropertyChanged(nameof(AuxiliaryWorkspaceColumnWidth));
		OnPropertyChanged(nameof(AuxiliaryWorkspaceColumnMinWidth));
		OnPropertyChanged(nameof(AuxiliaryWorkspaceGapWidth));
		OnPropertyChanged(nameof(MultiviewVisibility));
		OnPropertyChanged(nameof(CompositingGraphVisibility));
		OnPropertyChanged(nameof(StandardViewerVisibility));
		OnPropertyChanged(nameof(QuickControlsVisibility));
		OnPropertyChanged(nameof(ProductionControlsVisibility));
		OnPropertyChanged(nameof(MediaDeckVisibility));
		OnPropertyChanged(nameof(SourceBinVisibility));
		OnPropertyChanged(nameof(MediaLibraryLeftVisibility));
		OnPropertyChanged(nameof(LiveSceneCueVisibility));
		OnPropertyChanged(nameof(InspectorVisibility));
		OnPropertyChanged(nameof(LiveControlsVisibility));
		OnPropertyChanged(nameof(OutputRoutingVisibility));
		OnPropertyChanged(nameof(SystemWorkspaceVisibility));
		OnPropertyChanged(nameof(SystemStatusVisibility));
		OnPropertyChanged(nameof(MonitoringVisibility));
		OnPropertyChanged(nameof(GraphicsVisibility));
		OnPropertyChanged(nameof(AudioVisibility));
		OnPropertyChanged(nameof(RecordingVisibility));
		OnPropertyChanged(nameof(AIVisibility));
		OnPropertyChanged(nameof(OperatorStateVisibility));
		RaiseLayoutGeometryChanged();
		(ToggleLeftPanelCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
	}

	private void RaiseLayoutGeometryChanged()
	{
		OnPropertyChanged(nameof(LeftColumnWidth));
		OnPropertyChanged(nameof(RightColumnWidth));
		OnPropertyChanged(nameof(LowerRowHeight));
		OnPropertyChanged(nameof(LeftSplitterWidth));
		OnPropertyChanged(nameof(RightSplitterWidth));
		OnPropertyChanged(nameof(LowerSplitterHeight));
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class OperatorShellCommand : ICommand
{
	private readonly Action<object?> _execute;
	private readonly Func<bool> _canExecute;

	public OperatorShellCommand(Action execute, Func<bool>? canExecute = null)
		: this(_ => execute(), canExecute)
	{
		ArgumentNullException.ThrowIfNull(execute);
	}

	public OperatorShellCommand(Action<object?> execute, Func<bool>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_canExecute = canExecute ?? (() => true);
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) => _canExecute();

	public void Execute(object? parameter)
	{
		if (CanExecute(parameter))
			_execute(parameter);
	}

	public void RaiseCanExecuteChanged() =>
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
