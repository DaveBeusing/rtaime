// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

public sealed class OutputRoutingHealthViewModel : INotifyPropertyChanged, IDisposable
{
	private const string Unavailable = "UNAVAILABLE";

	private readonly OperatorViewModel _control;
	private readonly ProgramOutputController _programOutput;
	private readonly OutputStatusViewModel _program;
	private readonly OutputStatusViewModel _cleanProgram;
	private readonly Dictionary<string, PerformanceMetricViewModel> _metrics;
	private OutputStatusViewModel? _selectedOutput;
	private bool _disposed;

	public OutputRoutingHealthViewModel(
		OperatorViewModel control,
		ProgramOutputController programOutput)
	{
		_control = control ?? throw new ArgumentNullException(nameof(control));
		_programOutput = programOutput ?? throw new ArgumentNullException(nameof(programOutput));

		_program = new OutputStatusViewModel("program", "PROGRAM");
		_cleanProgram = new OutputStatusViewModel("clean-program", "CLEAN PROGRAM MONITOR");
		Outputs = new ObservableCollection<OutputStatusViewModel>
		{
			_program,
			_cleanProgram
		};
		SelectedOutput = _program;

		_metrics = new Dictionary<string, PerformanceMetricViewModel>(StringComparer.Ordinal)
		{
			["cpu"] = new("CPU"),
			["gpu"] = new("GPU"),
			["memory"] = new("MEMORY"),
			["vram"] = new("VRAM"),
			["render"] = new("RENDER TIME"),
			["dropped"] = new("DROPPED FRAMES"),
			["fps"] = new("OUTPUT FPS"),
			["disk"] = new("DISK"),
			["network"] = new("NETWORK")
		};
		Metrics = new ObservableCollection<PerformanceMetricViewModel>(_metrics.Values);

		_control.PropertyChanged += ControlPropertyChanged;
		_programOutput.PropertyChanged += ProgramOutputPropertyChanged;
		Refresh(sampleHistory: true);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<OutputStatusViewModel> Outputs { get; }
	public ObservableCollection<PerformanceMetricViewModel> Metrics { get; }
	public ICommand RoutePreviewToProgramCommand => _control.CutCommand;

	public OutputStatusViewModel? SelectedOutput
	{
		get => _selectedOutput;
		set => Set(ref _selectedOutput, value);
	}

	public bool IsReadOnly => !RoutePreviewToProgramCommand.CanExecute(null);
	public string AccessState => IsReadOnly ? "SAFE READ-ONLY" : "ROUTING ENABLED";
	public string AccessEvidenceState => IsReadOnly ? "UNVERIFIED" : "PASS";
	public string AccessDetail => ResolveAccessDetail();

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_control.PropertyChanged -= ControlPropertyChanged;
		_programOutput.PropertyChanged -= ProgramOutputPropertyChanged;
	}

	private void ControlPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed)
			return;

		Refresh(sampleHistory: string.Equals(e.PropertyName, nameof(OperatorViewModel.HealthObserved), StringComparison.Ordinal));
	}

	private void ProgramOutputPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed)
			return;

		Refresh(sampleHistory: false);
	}

	private void Refresh(bool sampleHistory)
	{
		var format = ParseFormat(_control.CurrentFormat);
		var runtimeEvidence = NormalizeEvidence(_control.RuntimeHealth);
		var runtimeStatus = ToOperatorStatus(runtimeEvidence);
		var runtimeDetail = runtimeEvidence == "PASS"
			? "Program routing reflects the authoritative Runtime state."
			: _control.EngineHealthDetail;

		_program.Update(
			target: "Runtime Program",
			assignedSource: FormatSource(_control.ProgramSourceName, _control.ProgramSourceId),
			resolution: format.Resolution,
			frameRate: format.FrameRate,
			pixelFormat: format.PixelFormat,
			colorSpace: Unavailable,
			status: runtimeStatus,
			evidenceState: runtimeEvidence,
			detail: runtimeDetail,
			recordingStatus: NormalizeAvailability(_control.RecordingStatus),
			streamingStatus: Unavailable);

		var cleanEvidence = NormalizeOutputEvidence(_programOutput.Health);
		var selectedDisplay = _programOutput.SelectedDisplay;
		_cleanProgram.Update(
			target: selectedDisplay?.Label ?? "No display selected",
			assignedSource: FormatSource(_control.ProgramSourceName, _control.ProgramSourceId),
			resolution: selectedDisplay is null ? Unavailable : $"{selectedDisplay.Width}×{selectedDisplay.Height}",
			frameRate: Unavailable,
			pixelFormat: Unavailable,
			colorSpace: Unavailable,
			status: ToOperatorStatus(cleanEvidence),
			evidenceState: cleanEvidence,
			detail: _programOutput.Detail,
			recordingStatus: NormalizeAvailability(_control.RecordingStatus),
			streamingStatus: Unavailable);

		var renderSample = TryParseLeadingDouble(_control.FrameTime);
		var droppedSample = TryParseUnsigned(_control.DroppedFrames);
		var gpuSample = TryParsePercentage(_control.GpuUtilization);

		UpdateMetric(
			"cpu",
			Unavailable,
			"UNVERIFIED",
			"No CPU telemetry is published by the current health contract.",
			null,
			sampleHistory);
		UpdateMetric(
			"gpu",
			NormalizeAvailability(_control.GpuUtilization),
			NormalizeEvidence(_control.GpuProviderHealth),
			"Runtime-published GPU utilization evidence. No local probing is performed.",
			gpuSample,
			sampleHistory);
		UpdateMetric(
			"memory",
			Unavailable,
			"UNVERIFIED",
			"No system-memory telemetry is published by the current health contract.",
			null,
			sampleHistory);
		UpdateMetric(
			"vram",
			NormalizeAvailability(_control.Vram),
			NormalizeEvidence(_control.GpuProviderHealth),
			"Runtime-published GPU memory evidence.",
			null,
			sampleHistory);
		UpdateMetric(
			"render",
			NormalizeRenderTime(_control.FrameTime),
			runtimeEvidence,
			$"Authoritative frame processing time and budget: {NormalizeAvailability(_control.FrameTime)}.",
			renderSample,
			sampleHistory);
		UpdateMetric(
			"dropped",
			NormalizeDroppedFrames(_control.DroppedFrames, runtimeEvidence),
			runtimeEvidence,
			"Bounded dropped-frame counter from the Runtime performance snapshot.",
			droppedSample,
			sampleHistory && runtimeEvidence != "UNVERIFIED");
		UpdateMetric(
			"fps",
			Unavailable,
			"UNVERIFIED",
			$"Configured frame rate is {format.FrameRate}; measured Output FPS is not currently published.",
			null,
			sampleHistory);
		UpdateMetric(
			"disk",
			Unavailable,
			"UNVERIFIED",
			"No disk telemetry is published by the current health contract.",
			null,
			sampleHistory);
		UpdateMetric(
			"network",
			Unavailable,
			"UNVERIFIED",
			"No network telemetry is published by the current health contract.",
			null,
			sampleHistory);

		OnPropertyChanged(nameof(IsReadOnly));
		OnPropertyChanged(nameof(AccessState));
		OnPropertyChanged(nameof(AccessEvidenceState));
		OnPropertyChanged(nameof(AccessDetail));
	}

	private void UpdateMetric(
		string key,
		string value,
		string evidenceState,
		string detail,
		double? sample,
		bool sampleHistory) =>
		_metrics[key].Update(
			value,
			evidenceState,
			ToOperatorStatus(evidenceState),
			detail,
			sampleHistory ? sample : null);

	private string ResolveAccessDetail()
	{
		if (!IsReadOnly)
			return "Preview-to-Program routing uses the existing authoritative CUT command.";

		if (!_control.IsConnected)
			return "Runtime control is unavailable. Output routing is read-only.";
		if (_control.IsStale)
			return "Authoritative state is stale. Output routing is read-only.";
		if (string.Equals(_control.ProgramSafety, OperatorProgramSafetyStates.Blocked, StringComparison.Ordinal))
			return "Program safety blocks mutations. Output routing is read-only.";
		if (!string.Equals(_control.RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase))
			return $"Runtime state is {_control.RuntimeStatus}. Output routing is read-only.";

		return "The existing Preview-to-Program command is not currently enabled.";
	}

	private static string FormatSource(string name, string id)
	{
		if (string.IsNullOrWhiteSpace(name) || name == "—")
			return Unavailable;
		return string.IsNullOrWhiteSpace(id) || id == "—" ? name : $"{name} · {id}";
	}

	private static string NormalizeAvailability(string? value) =>
		string.IsNullOrWhiteSpace(value) || value is "UNVERIFIED" or "UNKNOWN" or "—"
			? Unavailable
			: value;

	private static string NormalizeDroppedFrames(string? value, string evidenceState) =>
		evidenceState == "UNVERIFIED" ? Unavailable : NormalizeAvailability(value);

	private static string NormalizeRenderTime(string? value)
	{
		var normalized = NormalizeAvailability(value);
		if (normalized == Unavailable)
			return normalized;

		var separator = normalized.IndexOf('/');
		return separator < 0 ? normalized : normalized[..separator].Trim();
	}

	private static string NormalizeEvidence(string? value) =>
		value?.Trim().ToUpperInvariant() switch
		{
			"PASS" => "PASS",
			"FAIL" => "FAIL",
			_ => "UNVERIFIED"
		};

	private static string NormalizeOutputEvidence(string? value) =>
		value?.Trim().ToUpperInvariant() switch
		{
			"LIVE" => "PASS",
			"ERROR" => "FAIL",
			_ => "UNVERIFIED"
		};

	private static string ToOperatorStatus(string evidenceState) =>
		evidenceState switch
		{
			"PASS" => "HEALTHY",
			"FAIL" => "FAULTED",
			_ => "WARNING"
		};

	private static ParsedFormat ParseFormat(string? value)
	{
		var normalized = NormalizeAvailability(value);
		if (normalized == Unavailable)
			return new ParsedFormat(Unavailable, Unavailable, Unavailable);

		var parts = normalized.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return new ParsedFormat(
			parts.Length > 0 ? parts[0] : Unavailable,
			parts.Length > 1 ? parts[1] : Unavailable,
			parts.Length > 2 ? parts[2] : Unavailable);
	}

	private static double? TryParseLeadingDouble(string? value)
	{
		var normalized = NormalizeAvailability(value);
		if (normalized == Unavailable)
			return null;

		var end = normalized.IndexOf(' ');
		var token = end > 0 ? normalized[..end] : normalized;
		return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: null;
	}

	private static double? TryParseUnsigned(string? value) =>
		ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: null;

	private static double? TryParsePercentage(string? value)
	{
		var normalized = NormalizeAvailability(value);
		if (normalized == Unavailable)
			return null;

		var token = normalized.TrimEnd('%').Trim();
		return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: null;
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;

		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	private sealed record ParsedFormat(string Resolution, string FrameRate, string PixelFormat);
}

public sealed class OutputStatusViewModel : INotifyPropertyChanged
{
	private string _target = "UNAVAILABLE";
	private string _assignedSource = "UNAVAILABLE";
	private string _resolution = "UNAVAILABLE";
	private string _frameRate = "UNAVAILABLE";
	private string _pixelFormat = "UNAVAILABLE";
	private string _colorSpace = "UNAVAILABLE";
	private string _status = "WARNING";
	private string _evidenceState = "UNVERIFIED";
	private string _detail = "Output state is unavailable.";
	private string _recordingStatus = "UNAVAILABLE";
	private string _streamingStatus = "UNAVAILABLE";

	public OutputStatusViewModel(string id, string name)
	{
		Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Output id is required.", nameof(id)) : id;
		Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Output name is required.", nameof(name)) : name;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Id { get; }
	public string Name { get; }
	public string Target { get => _target; private set => Set(ref _target, value); }
	public string AssignedSource { get => _assignedSource; private set => Set(ref _assignedSource, value); }
	public string Resolution { get => _resolution; private set => Set(ref _resolution, value); }
	public string FrameRate { get => _frameRate; private set => Set(ref _frameRate, value); }
	public string PixelFormat { get => _pixelFormat; private set => Set(ref _pixelFormat, value); }
	public string ColorSpace { get => _colorSpace; private set => Set(ref _colorSpace, value); }
	public string Status { get => _status; private set => Set(ref _status, value); }
	public string EvidenceState { get => _evidenceState; private set => Set(ref _evidenceState, value); }
	public string Detail { get => _detail; private set => Set(ref _detail, value); }
	public string RecordingStatus { get => _recordingStatus; private set => Set(ref _recordingStatus, value); }
	public string StreamingStatus { get => _streamingStatus; private set => Set(ref _streamingStatus, value); }

	internal void Update(
		string target,
		string assignedSource,
		string resolution,
		string frameRate,
		string pixelFormat,
		string colorSpace,
		string status,
		string evidenceState,
		string detail,
		string recordingStatus,
		string streamingStatus)
	{
		Target = target;
		AssignedSource = assignedSource;
		Resolution = resolution;
		FrameRate = frameRate;
		PixelFormat = pixelFormat;
		ColorSpace = colorSpace;
		Status = status;
		EvidenceState = evidenceState;
		Detail = detail;
		RecordingStatus = recordingStatus;
		StreamingStatus = streamingStatus;
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}

public sealed class PerformanceMetricViewModel : INotifyPropertyChanged
{
	private const int HistoryLimit = 48;
	private readonly Queue<double> _history = new();
	private string _value = "UNAVAILABLE";
	private string _evidenceState = "UNVERIFIED";
	private string _status = "WARNING";
	private string _detail = "Telemetry is unavailable.";
	private PointCollection _historyPoints = [];

	public PerformanceMetricViewModel(string name)
	{
		Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Metric name is required.", nameof(name)) : name;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Name { get; }
	public string Value { get => _value; private set => Set(ref _value, value); }
	public string EvidenceState { get => _evidenceState; private set => Set(ref _evidenceState, value); }
	public string Status { get => _status; private set => Set(ref _status, value); }
	public string Detail { get => _detail; private set => Set(ref _detail, value); }
	public PointCollection HistoryPoints { get => _historyPoints; private set => Set(ref _historyPoints, value); }

	internal void Update(
		string value,
		string evidenceState,
		string status,
		string detail,
		double? sample)
	{
		Value = value;
		EvidenceState = evidenceState;
		Status = status;
		Detail = detail;

		if (sample is not { } numeric || !double.IsFinite(numeric))
			return;

		_history.Enqueue(numeric);
		while (_history.Count > HistoryLimit)
			_history.Dequeue();
		HistoryPoints = BuildPoints(_history);
	}

	private static PointCollection BuildPoints(IEnumerable<double> values)
	{
		var history = values.ToArray();
		if (history.Length == 0)
			return [];

		const double width = 92;
		const double height = 24;
		var min = history.Min();
		var max = history.Max();
		var span = max - min;
		var points = new PointCollection(history.Length);
		for (var index = 0; index < history.Length; index++)
		{
			var x = history.Length == 1 ? width : index * width / (history.Length - 1d);
			var y = span <= double.Epsilon
				? height / 2d
				: height - ((history[index] - min) / span * height);
			points.Add(new Point(x, y));
		}
		return points;
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}
