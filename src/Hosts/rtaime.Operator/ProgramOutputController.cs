// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using FormsScreen = System.Windows.Forms.Screen;

namespace rtaime.Operator;

/// <summary>
/// Controls the local clean-feed presentation surface. The controller never creates
/// Program pixels; it presents the same frozen ProgramImage published by the existing
/// RuntimeHost monitoring plane.
/// </summary>
public sealed class ProgramOutputController : INotifyPropertyChanged, IDisposable
{
	private const uint SwpNoActivate = 0x0010;
	private const uint SwpFrameChanged = 0x0020;
	private const uint SwpShowWindow = 0x0040;
	private static readonly nint HwndTopMost = (nint)(-1);
	private static readonly nint HwndNotTopMost = (nint)(-2);

	private readonly OperatorMonitoringViewModel _monitoring;
	private readonly SynchronizationContext _uiContext;
	private ProgramOutputWindow? _window;
	private ProgramOutputDisplay? _selectedDisplay;
	private bool _isRunning;
	private bool _isFullscreen;
	private bool _fallbackActive;
	private bool _disposed;
	private string? _placementError;
	private string _health = "STOPPED";
	private string _detail = "Program Output is stopped.";

	public ProgramOutputController(
		OperatorMonitoringViewModel monitoring,
		SynchronizationContext? uiContext = null)
	{
		_monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
		_uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();

		Displays = new ObservableCollection<ProgramOutputDisplay>();
		StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning && SelectedDisplay is not null);
		StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning);
		ToggleFullscreenCommand = new AsyncRelayCommand(ToggleFullscreenAsync, () => SelectedDisplay is not null);

		_monitoring.PropertyChanged += MonitoringPropertyChanged;
		SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
		RefreshDisplays();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<ProgramOutputDisplay> Displays { get; }
	public ICommand StartCommand { get; }
	public ICommand StopCommand { get; }
	public ICommand ToggleFullscreenCommand { get; }

	public ProgramOutputDisplay? SelectedDisplay
	{
		get => _selectedDisplay;
		set
		{
			if (!Set(ref _selectedDisplay, value)) return;
			_fallbackActive = false;
			_placementError = null;
			if (IsRunning)
				ApplyPlacement();
			UpdateHealth();
			RaiseCommandState();
		}
	}

	public bool IsRunning
	{
		get => _isRunning;
		private set
		{
			if (!Set(ref _isRunning, value)) return;
			OnPropertyChanged(nameof(RunState));
			RaiseCommandState();
		}
	}

	public bool IsFullscreen
	{
		get => _isFullscreen;
		private set
		{
			if (!Set(ref _isFullscreen, value)) return;
			OnPropertyChanged(nameof(FullscreenAction));
			OnPropertyChanged(nameof(Mode));
		}
	}

	public string Health { get => _health; private set => Set(ref _health, value); }
	public string Detail { get => _detail; private set => Set(ref _detail, value); }
	public string RunState => IsRunning ? "ON AIR" : "STOPPED";
	public string Mode => IsFullscreen ? "FULLSCREEN" : "WINDOWED";
	public string FullscreenAction => IsFullscreen ? "WINDOWED" : "FULLSCREEN";

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
		_monitoring.PropertyChanged -= MonitoringPropertyChanged;
		CloseWindow();
	}

	private Task StartAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_window is not null || SelectedDisplay is null)
			return Task.CompletedTask;

		_fallbackActive = false;
		_placementError = null;
		var window = new ProgramOutputWindow
		{
			DataContext = _monitoring
		};
		window.Closed += OutputWindowClosed;
		_window = window;
		IsRunning = true;
		window.Show();
		ApplyPlacement();
		UpdateHealth();
		return Task.CompletedTask;
	}

	private Task StopAsync()
	{
		CloseWindow();
		UpdateHealth();
		return Task.CompletedTask;
	}

	private Task ToggleFullscreenAsync()
	{
		_fallbackActive = false;
		_placementError = null;
		IsFullscreen = !IsFullscreen;
		if (IsRunning)
			ApplyPlacement();
		UpdateHealth();
		return Task.CompletedTask;
	}

	private void CloseWindow()
	{
		var window = _window;
		if (window is null)
		{
			IsRunning = false;
			return;
		}

		_window = null;
		window.Closed -= OutputWindowClosed;
		window.Close();
		IsRunning = false;
		_fallbackActive = false;
		_placementError = null;
	}

	private void OutputWindowClosed(object? sender, EventArgs e)
	{
		if (!ReferenceEquals(sender, _window)) return;
		if (_window is not null)
			_window.Closed -= OutputWindowClosed;
		_window = null;
		IsRunning = false;
		_fallbackActive = false;
		_placementError = null;
		UpdateHealth();
	}

	private void DisplaySettingsChanged(object? sender, EventArgs e)
	{
		_uiContext.Post(_ =>
		{
			if (_disposed) return;
			RefreshDisplays();
		}, null);
	}

	private void RefreshDisplays()
	{
		var previousId = _selectedDisplay?.Id;
		var displays = FormsScreen.AllScreens
			.Select(CreateDisplay)
			.OrderByDescending(display => display.IsPrimary)
			.ThenBy(display => display.Id, StringComparer.Ordinal)
			.ToArray();

		Displays.Clear();
		foreach (var display in displays)
			Displays.Add(display);

		var retained = previousId is null
			? null
			: displays.FirstOrDefault(display => string.Equals(display.Id, previousId, StringComparison.Ordinal));
		var next = retained ?? displays.FirstOrDefault(display => display.IsPrimary) ?? displays.FirstOrDefault();
		var selectedDisplayRemoved = IsRunning && previousId is not null && retained is null;

		if (!EqualityComparer<ProgramOutputDisplay?>.Default.Equals(_selectedDisplay, next))
		{
			_selectedDisplay = next;
			OnPropertyChanged(nameof(SelectedDisplay));
		}

		if (selectedDisplayRemoved)
		{
			_fallbackActive = true;
			_placementError = null;
			IsFullscreen = false;
		}

		if (IsRunning && next is not null)
			ApplyPlacement();

		UpdateHealth();
		RaiseCommandState();
	}

	private static ProgramOutputDisplay CreateDisplay(FormsScreen screen)
	{
		var bounds = screen.Bounds;
		var working = screen.WorkingArea;
		var label = $"{screen.DeviceName} • {bounds.Width}×{bounds.Height}{(screen.Primary ? " • PRIMARY" : string.Empty)}";
		return new ProgramOutputDisplay(
			screen.DeviceName,
			label,
			screen.Primary,
			bounds.X,
			bounds.Y,
			bounds.Width,
			bounds.Height,
			working.X,
			working.Y,
			working.Width,
			working.Height);
	}

	private void ApplyPlacement()
	{
		var window = _window;
		var display = SelectedDisplay;
		if (window is null || display is null)
			return;

		window.WindowState = WindowState.Normal;
		window.WindowStyle = IsFullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
		window.ResizeMode = IsFullscreen ? ResizeMode.NoResize : ResizeMode.CanResize;
		window.Topmost = IsFullscreen;

		var (left, top, width, height) = IsFullscreen
			? (display.Left, display.Top, display.Width, display.Height)
			: GetWindowedBounds(display);

		var handle = new WindowInteropHelper(window).Handle;
		var insertAfter = IsFullscreen ? HwndTopMost : HwndNotTopMost;
		if (!SetWindowPos(
			handle,
			insertAfter,
			left,
			top,
			width,
			height,
			SwpNoActivate | SwpFrameChanged | SwpShowWindow))
		{
			_placementError = $"SetWindowPos failed with Win32 error {Marshal.GetLastWin32Error()}.";
		}
		else
		{
			_placementError = null;
		}

		UpdateHealth();
	}

	private static (int Left, int Top, int Width, int Height) GetWindowedBounds(ProgramOutputDisplay display)
	{
		var maximumWidth = Math.Max(320, (int)Math.Floor(display.WorkWidth * 0.8));
		var maximumHeight = Math.Max(180, (int)Math.Floor(display.WorkHeight * 0.8));
		var width = Math.Min(1280, maximumWidth);
		var height = checked((int)Math.Round(width * 9.0 / 16.0));
		if (height > maximumHeight)
		{
			height = maximumHeight;
			width = checked((int)Math.Round(height * 16.0 / 9.0));
		}

		var left = display.WorkLeft + Math.Max(0, (display.WorkWidth - width) / 2);
		var top = display.WorkTop + Math.Max(0, (display.WorkHeight - height) / 2);
		return (left, top, width, height);
	}

	private void MonitoringPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is not (nameof(OperatorMonitoringViewModel.State)
			or nameof(OperatorMonitoringViewModel.HasProgram)
			or nameof(OperatorMonitoringViewModel.ProgramImage)))
		{
			return;
		}

		UpdateHealth();
	}

	private void UpdateHealth()
	{
		if (!IsRunning)
		{
			Health = "STOPPED";
			Detail = "Program Output is stopped.";
			return;
		}

		if (SelectedDisplay is null)
		{
			Health = "ERROR";
			Detail = "No Windows display is available for Program Output.";
			return;
		}

		if (_placementError is not null)
		{
			Health = "ERROR";
			Detail = _placementError;
			return;
		}

		if (_fallbackActive)
		{
			Health = "FALLBACK";
			Detail = $"Selected display was removed. Clean Feed moved to {SelectedDisplay.Label} in WINDOWED mode.";
			return;
		}

		if (string.Equals(_monitoring.State, "STALE", StringComparison.Ordinal))
		{
			Health = "STALE";
			Detail = "Program monitoring is stale; production continuity remains owned by RuntimeHost.";
			return;
		}

		if (!_monitoring.HasProgram)
		{
			Health = "WAITING";
			Detail = $"Waiting for the RuntimeHost Program monitor on {SelectedDisplay.Label}.";
			return;
		}

		Health = string.Equals(_monitoring.State, "LIVE", StringComparison.Ordinal)
			? "LIVE"
			: _monitoring.State;
		Detail = $"{SelectedDisplay.Label} • {Mode} • same RuntimeHost Program monitor frame as the Operator Program surface.";
	}

	private void RaiseCommandState()
	{
		(StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleFullscreenCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(
		nint hWnd,
		nint hWndInsertAfter,
		int x,
		int y,
		int cx,
		int cy,
		uint uFlags);
}

public sealed record ProgramOutputDisplay(
	string Id,
	string Label,
	bool IsPrimary,
	int Left,
	int Top,
	int Width,
	int Height,
	int WorkLeft,
	int WorkTop,
	int WorkWidth,
	int WorkHeight);
