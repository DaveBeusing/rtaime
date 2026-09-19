// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed record OperatorLayoutSettings(
	double LeftPanelWidth,
	double RightPanelWidth,
	double LowerPanelHeight,
	bool IsLeftCollapsed,
	bool IsRightCollapsed,
	bool IsCenterMaximized,
	bool IsFullscreen,
	string SelectedWorkspace)
{
	public const double DefaultLeftPanelWidth = 248;
	public const double DefaultRightPanelWidth = 320;
	public const double DefaultLowerPanelHeight = 260;
	public const double MinimumLeftPanelWidth = 180;
	public const double MaximumLeftPanelWidth = 520;
	public const double MinimumRightPanelWidth = 240;
	public const double MaximumRightPanelWidth = 620;
	public const double MinimumLowerPanelHeight = 150;
	public const double MaximumLowerPanelHeight = 520;

	public static OperatorLayoutSettings Default { get; } = new(
		DefaultLeftPanelWidth,
		DefaultRightPanelWidth,
		DefaultLowerPanelHeight,
		false,
		false,
		false,
		false,
		"Production");

	public OperatorLayoutSettings Normalize() =>
		this with
		{
			LeftPanelWidth = ClampFinite(LeftPanelWidth, MinimumLeftPanelWidth, MaximumLeftPanelWidth, DefaultLeftPanelWidth),
			RightPanelWidth = ClampFinite(RightPanelWidth, MinimumRightPanelWidth, MaximumRightPanelWidth, DefaultRightPanelWidth),
			LowerPanelHeight = ClampFinite(LowerPanelHeight, MinimumLowerPanelHeight, MaximumLowerPanelHeight, DefaultLowerPanelHeight),
			SelectedWorkspace = string.IsNullOrWhiteSpace(SelectedWorkspace) ? "Production" : SelectedWorkspace.Trim()
		};

	private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
		double.IsFinite(value)
			? Math.Clamp(value, minimum, maximum)
			: fallback;
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
	private double _leftPanelWidth;
	private double _rightPanelWidth;
	private double _lowerPanelHeight;
	private bool _isLeftCollapsed;
	private bool _isRightCollapsed;
	private bool _isCenterMaximized;
	private bool _isFullscreen;
	private string _selectedWorkspace;
	private string _viewerMode = "DUAL";

	public OperatorShellViewModel(
		OperatorLayoutStore store,
		Action<bool> setFullscreen)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_setFullscreen = setFullscreen ?? throw new ArgumentNullException(nameof(setFullscreen));
		var settings = _store.Load().Normalize();
		_leftPanelWidth = settings.LeftPanelWidth;
		_rightPanelWidth = settings.RightPanelWidth;
		_lowerPanelHeight = settings.LowerPanelHeight;
		_isLeftCollapsed = settings.IsLeftCollapsed;
		_isRightCollapsed = settings.IsRightCollapsed;
		_isCenterMaximized = settings.IsCenterMaximized;
		_isFullscreen = settings.IsFullscreen;
		_selectedWorkspace = settings.SelectedWorkspace;

		ToggleLeftPanelCommand = new OperatorShellCommand(ToggleLeftPanel);
		ToggleRightPanelCommand = new OperatorShellCommand(ToggleRightPanel);
		ToggleCenterMaximizeCommand = new OperatorShellCommand(ToggleCenterMaximize);
		ToggleFullscreenCommand = new OperatorShellCommand(() => _setFullscreen(!IsFullscreen));
		ExitFullscreenCommand = new OperatorShellCommand(
			() => _setFullscreen(false),
			() => IsFullscreen);
		ResetLayoutCommand = new OperatorShellCommand(ResetLayout);
		MaximizePreviewCommand = new OperatorShellCommand(() => SetViewerMode("PREVIEW"));
		MaximizeProgramCommand = new OperatorShellCommand(() => SetViewerMode("PROGRAM"));
		RestoreViewersCommand = new OperatorShellCommand(() => SetViewerMode("DUAL"), () => !string.Equals(ViewerMode, "DUAL", StringComparison.Ordinal));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ICommand ToggleLeftPanelCommand { get; }
	public ICommand ToggleRightPanelCommand { get; }
	public ICommand ToggleCenterMaximizeCommand { get; }
	public ICommand ToggleFullscreenCommand { get; }
	public ICommand ExitFullscreenCommand { get; }
	public ICommand ResetLayoutCommand { get; }
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
		get => new(IsCenterMaximized || IsLeftCollapsed ? 0 : LeftPanelWidth);
		set
		{
			if (!IsCenterMaximized && !IsLeftCollapsed && value.IsAbsolute && value.Value > 0)
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
		get => new(IsCenterMaximized ? 0 : LowerPanelHeight);
		set
		{
			if (!IsCenterMaximized && value.IsAbsolute && value.Value > 0)
				LowerPanelHeight = value.Value;
		}
	}

	public double LeftSplitterWidth => IsCenterMaximized || IsLeftCollapsed ? 0 : 5;
	public double RightSplitterWidth => IsCenterMaximized || IsRightCollapsed ? 0 : 5;
	public double LowerSplitterHeight => IsCenterMaximized ? 0 : 5;

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
		set
		{
			if (string.IsNullOrWhiteSpace(value))
				return;
			Set(ref _selectedWorkspace, value.Trim());
		}
	}

	public string FullscreenLabel => IsFullscreen ? "WINDOWED  F11" : "FULLSCREEN  F11";
	public string CenterModeLabel => IsCenterMaximized ? "RESTORE PANELS" : "MAXIMIZE VIEW";

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

	public void Save() =>
		_store.Save(new OperatorLayoutSettings(
			LeftPanelWidth,
			RightPanelWidth,
			LowerPanelHeight,
			IsLeftCollapsed,
			IsRightCollapsed,
			IsCenterMaximized,
			IsFullscreen,
			SelectedWorkspace));


	private void ToggleLeftPanel()
	{
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
	}

	private void ResetLayout()
	{
		var defaults = OperatorLayoutSettings.Default;
		_leftPanelWidth = defaults.LeftPanelWidth;
		_rightPanelWidth = defaults.RightPanelWidth;
		_lowerPanelHeight = defaults.LowerPanelHeight;
		_isLeftCollapsed = defaults.IsLeftCollapsed;
		_isRightCollapsed = defaults.IsRightCollapsed;
		_isCenterMaximized = defaults.IsCenterMaximized;
		_selectedWorkspace = defaults.SelectedWorkspace;
		_viewerMode = "DUAL";
		RaiseLayoutGeometryChanged();
		OnPropertyChanged(nameof(IsLeftCollapsed));
		OnPropertyChanged(nameof(IsRightCollapsed));
		OnPropertyChanged(nameof(IsCenterMaximized));
		OnPropertyChanged(nameof(CenterModeLabel));
		OnPropertyChanged(nameof(SelectedWorkspace));
		OnPropertyChanged(nameof(ViewerMode));
		OnPropertyChanged(nameof(PreviewViewerWidth));
		OnPropertyChanged(nameof(ProgramViewerWidth));
		OnPropertyChanged(nameof(PreviewViewerVisibility));
		OnPropertyChanged(nameof(ProgramViewerVisibility));
		OnPropertyChanged(nameof(ViewerGapWidth));
		OnPropertyChanged(nameof(ViewerModeLabel));
		(RestoreViewersCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
		Save();
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
	private readonly Action _execute;
	private readonly Func<bool> _canExecute;

	public OperatorShellCommand(Action execute, Func<bool>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_canExecute = canExecute ?? (() => true);
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) => _canExecute();

	public void Execute(object? parameter)
	{
		if (CanExecute(parameter))
			_execute();
	}

	public void RaiseCanExecuteChanged() =>
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
