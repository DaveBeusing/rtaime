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

public sealed record OperatorLayoutSettings
{
	public const int CurrentVersion = 2;
	public const double DefaultLeftPanelWidth = 248;
	public const double DefaultRightPanelWidth = 320;
	public const double DefaultLowerPanelHeight = 420;
	public const double MinimumLeftPanelWidth = 180;
	public const double MaximumLeftPanelWidth = 520;
	public const double MinimumRightPanelWidth = 240;
	public const double MaximumRightPanelWidth = 620;
	public const double MinimumLowerPanelHeight = 320;
	public const double MaximumLowerPanelHeight = 680;

	public int Version { get; init; } = CurrentVersion;
	public bool IsFullscreen { get; init; }
	public string SelectedWorkspace { get; init; } = OperatorWorkspaceNames.Live;
	public Dictionary<string, OperatorWorkspaceLayoutSettings> Workspaces { get; init; } = CreateCanonicalLayouts();

	public static OperatorLayoutSettings Default => new();

	public OperatorLayoutSettings Normalize()
	{
		if (Version != CurrentVersion)
			return Default;

		var selected = OperatorWorkspaceNames.Normalize(SelectedWorkspace);
		var layouts = CreateCanonicalLayouts();
		if (Workspaces is not null)
		{
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
		[OperatorWorkspaceNames.Graphics] = new(
			300,
			390,
			360,
			false,
			false,
			false,
			"DUAL"),
		[OperatorWorkspaceNames.System] = new(
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
	public const string Graphics = "GRAPHICS";
	public const string System = "SYSTEM";

	public static IReadOnlyList<string> All { get; } =
	[
		Live,
		Edit,
		Media,
		Graphics,
		System
	];

	public static string Normalize(string? value)
	{
		var normalized = value?.Trim().ToUpperInvariant();
		return All.Contains(normalized, StringComparer.Ordinal) ? normalized! : Live;
	}
}

public sealed class OperatorLayoutStore
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true
	};

	private readonly string _path;

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

	public void Save(OperatorLayoutSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		try
		{
			var normalized = settings.Normalize();
			var directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);
			File.WriteAllText(_path, JsonSerializer.Serialize(normalized, SerializerOptions));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Layout persistence is best-effort and must never affect production operation.
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
	private string _selectedWorkspace;
	private string _viewerMode;

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
		ToggleFullscreenCommand = new OperatorShellCommand(() => _setFullscreen(!IsFullscreen));
		ExitFullscreenCommand = new OperatorShellCommand(
			() => _setFullscreen(false),
			() => IsFullscreen);
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
		get => new(IsCenterMaximized || IsLeftCollapsed || !HasLeftRegion ? 0 : LeftPanelWidth);
		set
		{
			if (!IsCenterMaximized && !IsLeftCollapsed && HasLeftRegion && value.IsAbsolute && value.Value > 0)
				LeftPanelWidth = value.Value;
		}
	}

	public GridLength RightColumnWidth
	{
		get => new(IsCenterMaximized || IsRightCollapsed ? 0 : RightPanelWidth);
		set
		{
			if (!IsCenterMaximized && !IsRightCollapsed && value.IsAbsolute && value.Value > 0)
				RightPanelWidth = value.Value;
		}
	}

	public GridLength LowerRowHeight
	{
		get => new(IsCenterMaximized || !HasTimelineRegion ? 0 : LowerPanelHeight);
		set
		{
			if (!IsCenterMaximized && HasTimelineRegion && value.IsAbsolute && value.Value > 0)
				LowerPanelHeight = value.Value;
		}
	}

	public double LeftSplitterWidth => IsCenterMaximized || IsLeftCollapsed || !HasLeftRegion ? 0 : 5;
	public double RightSplitterWidth => IsCenterMaximized || IsRightCollapsed ? 0 : 5;
	public double LowerSplitterHeight => IsCenterMaximized || !HasTimelineRegion ? 0 : 5;

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

	public bool HasLeftRegion => !string.Equals(SelectedWorkspace, OperatorWorkspaceNames.System, StringComparison.Ordinal);
	public bool HasTimelineRegion => !string.Equals(SelectedWorkspace, OperatorWorkspaceNames.System, StringComparison.Ordinal);
	public Visibility LeftRegionVisibility => HasLeftRegion ? Visibility.Visible : Visibility.Collapsed;
	public Visibility TimelineRegionVisibility => HasTimelineRegion ? Visibility.Visible : Visibility.Collapsed;

	public bool IsLiveWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Live, StringComparison.Ordinal);
	public bool IsEditWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Edit, StringComparison.Ordinal);
	public bool IsMediaWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Media, StringComparison.Ordinal);
	public bool IsGraphicsWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.Graphics, StringComparison.Ordinal);
	public bool IsSystemWorkspace => string.Equals(SelectedWorkspace, OperatorWorkspaceNames.System, StringComparison.Ordinal);
	public bool HasAuxiliaryWorkspaceColumn => IsLiveWorkspace || IsGraphicsWorkspace || IsSystemWorkspace;
	public GridLength AuxiliaryWorkspaceColumnWidth => HasAuxiliaryWorkspaceColumn ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
	public double AuxiliaryWorkspaceColumnMinWidth => HasAuxiliaryWorkspaceColumn ? 300 : 0;
	public double AuxiliaryWorkspaceGapWidth => HasAuxiliaryWorkspaceColumn ? 14 : 0;

	public Visibility MultiviewVisibility => IsLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility StandardViewerVisibility => IsLiveWorkspace ? Visibility.Collapsed : Visibility.Visible;
	public Visibility QuickControlsVisibility => IsLiveWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility ProductionControlsVisibility => IsLiveWorkspace || IsEditWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility MediaDeckVisibility => IsLiveWorkspace || IsEditWorkspace || IsMediaWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility SourceBinVisibility => IsLiveWorkspace || IsMediaWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility SystemWorkspaceVisibility => IsSystemWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility SystemStatusVisibility => IsSystemWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility MonitoringVisibility => IsSystemWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility GraphicsVisibility => IsGraphicsWorkspace || IsSystemWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility AudioVisibility => IsLiveWorkspace || IsSystemWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility RecordingVisibility => IsLiveWorkspace || IsSystemWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility AIVisibility => IsGraphicsWorkspace || IsSystemWorkspace ? Visibility.Visible : Visibility.Collapsed;
	public Visibility OperatorStateVisibility => IsSystemWorkspace ? Visibility.Visible : Visibility.Collapsed;

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
		Save();
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

	public void Save()
	{
		CaptureCurrentWorkspace();
		_store.Save(new OperatorLayoutSettings
		{
			Version = OperatorLayoutSettings.CurrentVersion,
			IsFullscreen = IsFullscreen,
			SelectedWorkspace = SelectedWorkspace,
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
		OnPropertyChanged(nameof(IsGraphicsWorkspace));
		OnPropertyChanged(nameof(IsSystemWorkspace));
		OnPropertyChanged(nameof(HasAuxiliaryWorkspaceColumn));
		OnPropertyChanged(nameof(AuxiliaryWorkspaceColumnWidth));
		OnPropertyChanged(nameof(AuxiliaryWorkspaceColumnMinWidth));
		OnPropertyChanged(nameof(AuxiliaryWorkspaceGapWidth));
		OnPropertyChanged(nameof(MultiviewVisibility));
		OnPropertyChanged(nameof(StandardViewerVisibility));
		OnPropertyChanged(nameof(QuickControlsVisibility));
		OnPropertyChanged(nameof(ProductionControlsVisibility));
		OnPropertyChanged(nameof(MediaDeckVisibility));
		OnPropertyChanged(nameof(SourceBinVisibility));
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
