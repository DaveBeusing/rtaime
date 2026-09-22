// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OutputRoutingHealthViewModel : INotifyPropertyChanged, IDisposable
{
	private const string Unavailable = "UNAVAILABLE";
	private static readonly HashSet<string> RelevantControlProperties = new(StringComparer.Ordinal)
	{
		nameof(OperatorViewModel.ProgramSourceName),
		nameof(OperatorViewModel.ProgramSourceId),
		nameof(OperatorViewModel.PreviewSourceName),
		nameof(OperatorViewModel.PreviewSourceId),
		nameof(OperatorViewModel.RecordingStatus),
		nameof(OperatorViewModel.EngineHealth),
		nameof(OperatorViewModel.EngineHealthDetail),
		nameof(OperatorViewModel.ControlHealth),
		nameof(OperatorViewModel.RuntimeHealth),
		nameof(OperatorViewModel.MediaHealth),
		nameof(OperatorViewModel.GpuProviderHealth),
		nameof(OperatorViewModel.CurrentFormat),
		nameof(OperatorViewModel.FrameTime),
		nameof(OperatorViewModel.OutputFps),
		nameof(OperatorViewModel.DroppedFrames),
		nameof(OperatorViewModel.CpuDeviceName),
		nameof(OperatorViewModel.CpuUtilization),
		nameof(OperatorViewModel.SystemMemory),
		nameof(OperatorViewModel.GpuDeviceName),
		nameof(OperatorViewModel.GpuUtilization),
		nameof(OperatorViewModel.Vram),
		nameof(OperatorViewModel.HealthObserved),
		nameof(OperatorViewModel.IsConnected),
		nameof(OperatorViewModel.IsStale),
		nameof(OperatorViewModel.IsBusy),
		nameof(OperatorViewModel.ProgramSafety),
		nameof(OperatorViewModel.RuntimeStatus),
		nameof(OperatorViewModel.GlobalReadinessState),
		nameof(OperatorViewModel.PerformanceVerificationState),
		nameof(OperatorViewModel.PerformanceVerificationDetail),
		nameof(OperatorViewModel.OutputRoles)
	};

	private readonly OperatorViewModel _control;
	private readonly ProgramOutputController _programOutput;
	private readonly OutputStatusViewModel _program;
	private readonly OutputStatusViewModel _preview;
	private readonly OutputStatusViewModel _aux;
	private readonly OutputStatusViewModel _cleanProgram;
	private readonly Dictionary<string, PerformanceMetricViewModel> _metrics;
	private readonly Dictionary<string, SystemHealthStatusViewModel> _systemHealth;
	private OutputStatusViewModel? _selectedOutput;
	private bool _disposed;

	public OutputRoutingHealthViewModel(
		OperatorViewModel control,
		ProgramOutputController programOutput)
	{
		_control = control ?? throw new ArgumentNullException(nameof(control));
		_programOutput = programOutput ?? throw new ArgumentNullException(nameof(programOutput));

		_program = new OutputStatusViewModel("program", "PROGRAM");
		_preview = new OutputStatusViewModel("preview", "PREVIEW");
		_aux = new OutputStatusViewModel("aux", "AUX");
		_cleanProgram = new OutputStatusViewModel("clean-program", "CLEAN FEED");
		Outputs = new ObservableCollection<OutputStatusViewModel>
		{
			_program,
			_preview,
			_aux,
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
			["network"] = new("NETWORK"),
			["temperature"] = new("TEMPERATURE")
		};
		Metrics = new ObservableCollection<PerformanceMetricViewModel>(_metrics.Values);

		_systemHealth = new Dictionary<string, SystemHealthStatusViewModel>(StringComparer.Ordinal)
		{
			["engine"] = new("ENGINE"),
			["control"] = new("CONTROL"),
			["runtime"] = new("RUNTIME"),
			["media"] = new("MEDIA"),
			["hardware"] = new("HARDWARE"),
			["gpu"] = new("GPU")
		};
		SystemHealth = new ObservableCollection<SystemHealthStatusViewModel>(_systemHealth.Values);

		_control.PropertyChanged += ControlPropertyChanged;
		_programOutput.PropertyChanged += ProgramOutputPropertyChanged;
		Refresh(sampleHistory: true);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<OutputStatusViewModel> Outputs { get; }
	public ObservableCollection<PerformanceMetricViewModel> Metrics { get; }
	public ObservableCollection<SystemHealthStatusViewModel> SystemHealth { get; }
	public string CpuDeviceName => NormalizeAvailability(_control.CpuDeviceName);
	public string GpuDeviceName => NormalizeAvailability(_control.GpuDeviceName);
	public PerformanceMetricViewModel CpuMetric => _metrics["cpu"];
	public PerformanceMetricViewModel GpuMetric => _metrics["gpu"];
	public PerformanceMetricViewModel MemoryMetric => _metrics["memory"];
	public PerformanceMetricViewModel VramMetric => _metrics["vram"];
	public PerformanceMetricViewModel RenderMetric => _metrics["render"];
	public PerformanceMetricViewModel FpsMetric => _metrics["fps"];
	public PerformanceMetricViewModel DroppedMetric => _metrics["dropped"];
	public PerformanceMetricViewModel DiskMetric => _metrics["disk"];
	public PerformanceMetricViewModel NetworkMetric => _metrics["network"];
	public PerformanceMetricViewModel TemperatureMetric => _metrics["temperature"];
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
		if (!string.IsNullOrEmpty(e.PropertyName) && !RelevantControlProperties.Contains(e.PropertyName))
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
		var performanceEvidence = ResolvePerformanceEvidence(runtimeEvidence);
		var runtimeStatus = ToOperatorStatus(runtimeEvidence);
		var runtimeDetail = runtimeEvidence == "PASS"
			? "Program routing reflects the authoritative Runtime state."
			: _control.EngineHealthDetail;

		UpdateSystemHealth("engine", _control.EngineHealth);
		UpdateSystemHealth("control", _control.ControlHealth);
		UpdateSystemHealth("runtime", _control.RuntimeHealth);
		UpdateSystemHealth("media", _control.MediaHealth);
		UpdateSystemHealth("hardware", ResolveHardwareEvidence());
		UpdateSystemHealth("gpu", _control.GpuProviderHealth);

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

		_preview.Update(
			target: "Control Preview",
			assignedSource: FormatSource(_control.PreviewSourceName, _control.PreviewSourceId),
			resolution: format.Resolution,
			frameRate: format.FrameRate,
			pixelFormat: format.PixelFormat,
			colorSpace: Unavailable,
			status: runtimeStatus,
			evidenceState: runtimeEvidence,
			detail: "Preview role reflects the authoritative Control routing snapshot.",
			recordingStatus: Unavailable,
			streamingStatus: Unavailable);

		var aux = _control.OutputRoles.FirstOrDefault(role =>
			string.Equals(role.RoleId, "aux", StringComparison.Ordinal));
		if (aux is null)
		{
			_aux.Update(
				target: Unavailable,
				assignedSource: Unavailable,
				resolution: Unavailable,
				frameRate: Unavailable,
				pixelFormat: Unavailable,
				colorSpace: Unavailable,
				status: "WARNING",
				evidenceState: "UNVERIFIED",
				detail: "Aux output role is not configured by authoritative Control state.",
				recordingStatus: Unavailable,
				streamingStatus: Unavailable);
		}
		else
		{
			var source = _control.Sources.FirstOrDefault(candidate =>
				string.Equals(candidate.Id, aux.SourceId, StringComparison.Ordinal));
			var evidence = NormalizeEvidence(aux.HealthState);
			var resolution = aux.Width is { } width && aux.Height is { } height
				? $"{width}×{height}"
				: Unavailable;
			var target = aux.AuthoritativeActive
				? $"{aux.ProviderId} · {aux.TargetId}"
				: $"{aux.TargetId} · {aux.ProviderId}";
			var detail = aux.Error is null
				? aux.Evidence
				: $"{aux.Evidence} {aux.Error.Value.Code}: {aux.Error.Value.Message}";
			_aux.Update(
				target: target,
				assignedSource: FormatSource(source?.Name ?? aux.SourceId, aux.SourceId),
				resolution: resolution,
				frameRate: NormalizeAvailability(aux.FrameRate),
				pixelFormat: NormalizeAvailability(aux.PixelFormat),
				colorSpace: Unavailable,
				status: ToOperatorStatus(evidence),
				evidenceState: evidence,
				detail: detail,
				recordingStatus: Unavailable,
				streamingStatus: Unavailable);
		}

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

		var hasRuntimePerformance = NormalizeAvailability(_control.CurrentFormat) != Unavailable &&
			NormalizeAvailability(_control.FrameTime) != Unavailable;
		var renderSample = hasRuntimePerformance ? TryParseLeadingDouble(_control.FrameTime) : null;
		var frameBudgetSample = hasRuntimePerformance ? TryParseFrameBudget(_control.FrameTime) : null;
		var droppedSample = hasRuntimePerformance ? TryParseUnsigned(_control.DroppedFrames) : null;
		var cpuSample = TryParsePercentage(_control.CpuUtilization);
		var gpuSample = TryParsePercentage(_control.GpuUtilization);
		var memorySample = TryParseLeadingPercentage(_control.SystemMemory);
		var cpuValue = NormalizeAvailability(_control.CpuUtilization);
		var gpuValue = NormalizeAvailability(_control.GpuUtilization);
		var memoryValue = NormalizeAvailability(_control.SystemMemory);
		var vramValue = NormalizeAvailability(_control.Vram);

		UpdateMetric(
			"cpu",
			cpuValue,
			ResolveHardwareMetricEvidence(cpuValue),
			$"{NormalizeAvailability(_control.CpuDeviceName)}. Runtime-published total CPU utilization.",
			cpuSample,
			sampleHistory);
		UpdateMetric(
			"gpu",
			gpuValue,
			ResolveHardwareMetricEvidence(gpuValue, _control.GpuProviderHealth),
			$"{NormalizeAvailability(_control.GpuDeviceName)}. Runtime-published GPU utilization evidence.",
			gpuSample,
			sampleHistory);
		UpdateMetric(
			"memory",
			memoryValue,
			ResolveHardwareMetricEvidence(memoryValue),
			"Runtime-published system RAM utilization and physical-memory capacity.",
			memorySample,
			sampleHistory);
		UpdateMetric(
			"vram",
			vramValue,
			ResolveMetricEvidence(vramValue, _control.GpuProviderHealth),
			"Runtime-published GPU memory evidence.",
			null,
			sampleHistory);
		_metrics["render"].Update(
			NormalizeRenderTime(_control.FrameTime),
			performanceEvidence,
			ResolveRenderStatus(renderSample, frameBudgetSample, performanceEvidence),
			$"Core render time. Engineering target is ≤ 3.00 ms; the hardware P95 qualification ceiling is 5.00 ms; full pipeline timing still uses the frame budget. {_control.PerformanceVerificationDetail} Current render/budget: {NormalizeAvailability(_control.FrameTime)}.",
			sampleHistory ? renderSample : null);
		var droppedValue = hasRuntimePerformance ? NormalizeAvailability(_control.DroppedFrames) : Unavailable;
		var droppedEvidence = droppedValue == Unavailable ? "UNVERIFIED" : runtimeEvidence;
		_metrics["dropped"].Update(
			droppedValue,
			droppedEvidence,
			droppedEvidence == "FAIL"
				? "FAULTED"
				: droppedSample is > 0
					? "WARNING"
					: ToOperatorStatus(droppedEvidence),
			"Bounded cumulative dropped-frame evidence from Runtime scheduler cadence and output backpressure/rejection counters.",
			sampleHistory && hasRuntimePerformance ? droppedSample : null);
		var outputFpsValue = NormalizeAvailability(_control.OutputFps);
		var outputFpsSample = TryParseLeadingDouble(_control.OutputFps);
		UpdateMetric(
			"fps",
			outputFpsValue,
			outputFpsValue == Unavailable ? "UNVERIFIED" : runtimeEvidence,
			$"Measured Program output cadence from Runtime boundary observations. Configured frame rate is {format.FrameRate}.",
			outputFpsSample,
			sampleHistory && hasRuntimePerformance);
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
		UpdateMetric(
			"temperature",
			Unavailable,
			"UNVERIFIED",
			"No temperature telemetry is published by the current health contract.",
			null,
			sampleHistory);

		OnPropertyChanged(nameof(CpuDeviceName));
		OnPropertyChanged(nameof(GpuDeviceName));
		OnPropertyChanged(nameof(IsReadOnly));
		OnPropertyChanged(nameof(AccessState));
		OnPropertyChanged(nameof(AccessEvidenceState));
		OnPropertyChanged(nameof(AccessDetail));
	}

	private void UpdateSystemHealth(string key, string evidenceState)
	{
		var normalized = NormalizeEvidence(evidenceState);
		_systemHealth[key].Update(normalized, ToOperatorStatus(normalized));
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

	private string ResolveHardwareEvidence()
	{
		if (!_control.IsConnected || _control.IsStale)
			return "UNVERIFIED";

		if (NormalizeAvailability(_control.CpuUtilization) == Unavailable ||
			NormalizeAvailability(_control.SystemMemory) == Unavailable ||
			NormalizeAvailability(_control.GpuUtilization) == Unavailable)
			return "UNVERIFIED";

		var runtimeEvidence = NormalizeEvidence(_control.RuntimeHealth);
		if (runtimeEvidence == "FAIL")
			return "FAIL";

		var gpuEvidence = NormalizeEvidence(_control.GpuProviderHealth);
		if (gpuEvidence == "FAIL")
			return "FAIL";

		if (string.Equals(_control.PerformanceVerificationState, "VERIFIED", StringComparison.Ordinal))
			return "PASS";

		return runtimeEvidence == "PASS" && gpuEvidence == "PASS"
			? "PASS"
			: "UNVERIFIED";
	}

	private string ResolveHardwareMetricEvidence(string value, string? providerEvidence = null)
	{
		if (!_control.IsConnected || _control.IsStale || value == Unavailable || value.Contains("UNVERIFIED", StringComparison.OrdinalIgnoreCase))
			return "UNVERIFIED";

		var runtimeEvidence = NormalizeEvidence(_control.RuntimeHealth);
		if (runtimeEvidence == "FAIL")
			return "FAIL";

		if (providerEvidence is not null)
		{
			var normalizedProvider = NormalizeEvidence(providerEvidence);
			if (normalizedProvider == "FAIL")
				return "FAIL";
			if (normalizedProvider != "PASS" &&
				!string.Equals(_control.PerformanceVerificationState, "VERIFIED", StringComparison.Ordinal))
				return "UNVERIFIED";
		}

		if (string.Equals(_control.PerformanceVerificationState, "VERIFIED", StringComparison.Ordinal))
			return "PASS";

		return runtimeEvidence;
	}

	private string ResolvePerformanceEvidence(string runtimeEvidence)
	{
		if (runtimeEvidence == "FAIL")
			return "FAIL";
		return string.Equals(_control.PerformanceVerificationState, "VERIFIED", StringComparison.Ordinal)
			? "PASS"
			: "UNVERIFIED";
	}

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


	private static string NormalizeRenderTime(string? value)
	{
		var normalized = NormalizeAvailability(value);
		if (normalized == Unavailable)
			return normalized;

		var separator = normalized.IndexOf('/');
		return separator < 0 ? normalized : normalized[..separator].Trim();
	}

	private static string ResolveMetricEvidence(string value, string providerEvidence) =>
		value == Unavailable || value.Contains("UNVERIFIED", StringComparison.OrdinalIgnoreCase)
			? "UNVERIFIED"
			: NormalizeEvidence(providerEvidence);

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

	private static double? TryParseFrameBudget(string? value)
	{
		var normalized = NormalizeAvailability(value);
		if (normalized == Unavailable)
			return null;

		var separator = normalized.IndexOf('/');
		if (separator < 0 || separator + 1 >= normalized.Length)
			return null;

		return TryParseLeadingDouble(normalized[(separator + 1)..].Trim());
	}

	private static string ResolveRenderStatus(double? renderMilliseconds, double? frameBudgetMilliseconds, string runtimeEvidence)
	{
		if (runtimeEvidence == "FAIL")
			return "FAULTED";
		if (runtimeEvidence != "PASS")
			return "WARNING";
		if (renderMilliseconds is not { } render || !double.IsFinite(render))
			return "WARNING";
		if (frameBudgetMilliseconds is { } budget && double.IsFinite(budget) && render > budget)
			return "FAULTED";
		return render <= 3.0 ? "HEALTHY" : "WARNING";
	}

	private static double? TryParseUnsigned(string? value) =>
		ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: null;

	private static double? TryParseLeadingPercentage(string? value)
	{
		var normalized = NormalizeAvailability(value);
		if (normalized == Unavailable)
			return null;

		var percentIndex = normalized.IndexOf('%');
		if (percentIndex <= 0)
			return null;

		return double.TryParse(normalized[..percentIndex].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: null;
	}

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

public sealed class SystemHealthStatusViewModel : INotifyPropertyChanged
{
	private string _evidenceState = "UNVERIFIED";
	private string _status = "WARNING";

	public SystemHealthStatusViewModel(string name)
	{
		Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Health name is required.", nameof(name)) : name;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Name { get; }
	public string EvidenceState { get => _evidenceState; private set => Set(ref _evidenceState, value); }
	public string Status { get => _status; private set => Set(ref _status, value); }

	internal void Update(string evidenceState, string status)
	{
		EvidenceState = evidenceState;
		Status = status;
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
	private PointCollection _historyPoints = new();
	private double _gaugeValue;
	private bool _hasGaugeSample;

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
	public double GaugeValue { get => _gaugeValue; private set => Set(ref _gaugeValue, value); }
	public bool HasGaugeSample { get => _hasGaugeSample; private set => Set(ref _hasGaugeSample, value); }
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
		if (sample is { } gauge && double.IsFinite(gauge))
		{
			HasGaugeSample = true;
			GaugeValue = Math.Clamp(gauge, 0, 100);
		}
		else
		{
			HasGaugeSample = false;
			GaugeValue = 0;
		}

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
			return new PointCollection();

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
