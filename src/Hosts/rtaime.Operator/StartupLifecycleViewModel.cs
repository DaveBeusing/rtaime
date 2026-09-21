// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;

namespace rtaime.Operator;

public enum StartupLifecycleRequirement
{
	Critical,
	RequiredForProduction,
	Optional
}

public sealed class StartupLifecycleViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly SynchronizationContext _synchronizationContext;
	private readonly string? _evidencePath;
	private readonly AsyncRelayCommand _copyDiagnosticsCommand;
	private readonly AsyncRelayCommand _openDiagnosticsCommand;
	private FileSystemWatcher? _watcher;
	private bool _detailsVisible;
	private bool _initialStartupCompleted;
	private string _activeStageName = "Application Bootstrap";
	private string _summary = "Waiting for application lifecycle evidence.";
	private string _nextStep = "Waiting for authoritative AppHost lifecycle evidence.";
	private string _observedAt = "—";
	private string? _diagnosticPath;
	private string _diagnosticActionStatus = string.Empty;
	private string? _latchedFailureStageId;
	private string? _latchedFailureStageName;
	private string? _latchedFailureReason;
	private bool _disposed;

	public StartupLifecycleViewModel(
		string? evidencePath,
		SynchronizationContext? synchronizationContext = null)
	{
		_synchronizationContext = synchronizationContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
		_evidencePath = string.IsNullOrWhiteSpace(evidencePath) ? null : Path.GetFullPath(evidencePath);
		Stages = new ObservableCollection<StartupLifecycleStageViewModel>();
		ToggleDetailsCommand = new AsyncRelayCommand(
			() =>
			{
				DetailsVisible = !DetailsVisible;
				return Task.CompletedTask;
			},
			() => true);
		_copyDiagnosticsCommand = new AsyncRelayCommand(
			CopyDiagnosticsAsync,
			() => HasEvidence || Stages.Count > 0);
		_openDiagnosticsCommand = new AsyncRelayCommand(
			OpenDiagnosticsAsync,
			() => CanOpenDiagnostics);
		CopyDiagnosticsCommand = _copyDiagnosticsCommand;
		OpenDiagnosticsCommand = _openDiagnosticsCommand;

		if (_evidencePath is null)
		{
			ApplyFallback();
			return;
		}

		TryRefresh();
		StartWatching();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<StartupLifecycleStageViewModel> Stages { get; }
	public ICommand ToggleDetailsCommand { get; }
	public ICommand CopyDiagnosticsCommand { get; }
	public ICommand OpenDiagnosticsCommand { get; }
	public string EvidencePath => _evidencePath ?? "Direct Operator startup; AppHost lifecycle evidence is unavailable.";
	public string DiagnosticPath => _diagnosticPath ?? "Not published by AppHost.";
	public bool HasEvidence => _evidencePath is not null;
	public bool HasDiagnosticPath => !string.IsNullOrWhiteSpace(_diagnosticPath);
	public bool CanOpenDiagnostics => ResolveDiagnosticOpenTarget() is not null;
	public string DiagnosticActionLabel => File.Exists(_diagnosticPath) ? "OPEN DIAGNOSTIC LOG" : "OPEN DIAGNOSTICS FOLDER";
	public string DiagnosticActionStatus { get => _diagnosticActionStatus; private set => Set(ref _diagnosticActionStatus, value); }
	public string NextStep { get => _nextStep; private set => Set(ref _nextStep, value); }
	public bool IsShellAvailable
	{
		get
		{
			if (!HasEvidence)
				return true;

			var criticalStages = Stages
				.Where(stage => stage.Requirement == StartupLifecycleRequirement.Critical)
				.ToArray();
			return criticalStages.Length > 0 &&
				criticalStages.All(stage => string.Equals(stage.Status, "READY", StringComparison.Ordinal));
		}
	}
	public bool HasCriticalFailure =>
		HasEvidence &&
		Stages.Any(stage =>
			stage.Requirement == StartupLifecycleRequirement.Critical &&
			string.Equals(stage.Status, "FAILED", StringComparison.Ordinal));
	public bool HasStartupFailure =>
		HasEvidence &&
		!_initialStartupCompleted &&
		(_latchedFailureStageId is not null ||
		 Stages.Any(stage =>
			stage.Requirement != StartupLifecycleRequirement.Optional &&
			string.Equals(stage.Status, "FAILED", StringComparison.Ordinal)));
	public bool HasStartupDegradation =>
		HasEvidence &&
		!_initialStartupCompleted &&
		!HasStartupFailure &&
		Stages.Any(stage =>
			string.Equals(stage.Status, "DEGRADED", StringComparison.Ordinal) ||
			(stage.Requirement == StartupLifecycleRequirement.Optional &&
			 string.Equals(stage.Status, "FAILED", StringComparison.Ordinal)));
	public bool HasCompletedInitialStartup => !HasEvidence || _initialStartupCompleted;
	public string StartupPhaseLabel => HasStartupFailure
		? "STARTUP ATTENTION REQUIRED"
		: HasStartupDegradation
			? "STARTUP DEGRADED"
			: HasCompletedInitialStartup
				? "PRODUCTION RUNTIME READY"
				: "STARTING PRODUCTION RUNTIME";
	public string StartupVisualState => HasStartupFailure
		? "FAILED"
		: HasStartupDegradation
			? "DEGRADED"
			: HasCompletedInitialStartup
				? "READY"
				: Stages.Any(stage => string.Equals(stage.Status, "STARTING", StringComparison.Ordinal))
					? "STARTING"
					: "PENDING";
	public string ReadinessSummary
	{
		get
		{
			if (!HasEvidence)
				return "DIRECT OPERATOR SESSION";

			var requiredStages = Stages
				.Where(stage => stage.Requirement != StartupLifecycleRequirement.Optional)
				.ToArray();
			var readyStages = requiredStages.Count(stage =>
				string.Equals(stage.Status, "READY", StringComparison.Ordinal));
			var prefix = HasStartupFailure
				? "FAILED · "
				: HasStartupDegradation
					? "DEGRADED · "
					: string.Empty;
			return $"{prefix}{readyStages} / {requiredStages.Length} REQUIRED STAGES READY";
		}
	}
	public bool HasDeferredInitialization =>
		HasEvidence &&
		IsShellAvailable &&
		Stages.Any(stage =>
			stage.Requirement != StartupLifecycleRequirement.Critical &&
			!string.Equals(stage.Status, "READY", StringComparison.Ordinal));
	public string ActiveStageName { get => _activeStageName; private set => Set(ref _activeStageName, value); }
	public string Summary { get => _summary; private set => Set(ref _summary, value); }
	public string ObservedAt { get => _observedAt; private set => Set(ref _observedAt, value); }
	public bool DetailsVisible { get => _detailsVisible; private set => Set(ref _detailsVisible, value); }
	public string DetailsAction => DetailsVisible ? "HIDE DETAILS" : "TECHNICAL DETAILS";

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		if (_watcher is not null)
		{
			_watcher.EnableRaisingEvents = false;
			_watcher.Changed -= OnEvidenceChanged;
			_watcher.Created -= OnEvidenceChanged;
			_watcher.Renamed -= OnEvidenceRenamed;
			_watcher.Dispose();
			_watcher = null;
		}
	}

	private void StartWatching()
	{
		if (_evidencePath is null) return;
		var directory = Path.GetDirectoryName(_evidencePath);
		var fileName = Path.GetFileName(_evidencePath);
		if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
			return;

		try
		{
			Directory.CreateDirectory(directory);
			_watcher = new FileSystemWatcher(directory, fileName)
			{
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
				IncludeSubdirectories = false,
				EnableRaisingEvents = true
			};
			_watcher.Changed += OnEvidenceChanged;
			_watcher.Created += OnEvidenceChanged;
			_watcher.Renamed += OnEvidenceRenamed;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Summary = $"Lifecycle evidence watcher unavailable: {exception.Message}";
		}
	}

	private void OnEvidenceChanged(object sender, FileSystemEventArgs e) => QueueRefresh();

	private void OnEvidenceRenamed(object sender, RenamedEventArgs e) => QueueRefresh();

	private void QueueRefresh()
	{
		if (_disposed) return;
		_synchronizationContext.Post(
			_ =>
			{
				if (!_disposed)
					TryRefresh();
			},
			null);
	}

	private void TryRefresh()
	{
		if (_evidencePath is null)
		{
			ApplyFallback();
			return;
		}

		try
		{
			if (!File.Exists(_evidencePath))
			{
				if (Stages.Count == 0)
					ApplyFallback();
				return;
			}

			using var stream = new FileStream(
				_evidencePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			using var document = JsonDocument.Parse(stream);
			ApplyEvidence(document.RootElement);
		}
		catch (Exception exception) when (exception is IOException or JsonException)
		{
			if (Stages.Count == 0)
				Summary = $"Waiting for stable lifecycle evidence: {exception.Message}";
		}
	}

	private void ApplyEvidence(JsonElement root)
	{
		if (!root.TryGetProperty("schemaVersion", out var schema) ||
			!string.Equals(schema.GetString(), "1.0", StringComparison.Ordinal))
		{
			Summary = "Unsupported AppHost lifecycle evidence format.";
			return;
		}

		var activeStageId = root.TryGetProperty("activeStageId", out var active) &&
			active.ValueKind == JsonValueKind.String
				? active.GetString()
				: null;
		var observedAt = ReadTimestamp(root, "observedAtUtc");
		_diagnosticPath = ReadNullableString(root, "diagnosticPath");
		var parsed = new List<StartupLifecycleStageViewModel>();

		if (root.TryGetProperty("stages", out var stages) && stages.ValueKind == JsonValueKind.Array)
		{
			foreach (var stage in stages.EnumerateArray())
			{
				var id = ReadString(stage, "id", "unknown");
				var displayName = ReadString(stage, "displayName", id);
				var status = ReadString(stage, "status", "Pending");
				var statusText = ReadNullableString(stage, "statusText");
				var requirement = ResolveRequirement(
					id,
					ReadNullableString(stage, "requirement"),
					statusText);
				var startedAt = ReadTimestamp(stage, "startedAtUtc");
				var completedAt = ReadTimestamp(stage, "completedAtUtc");
				var failureReason = ReadNullableString(stage, "failureReason");
				var canRetry = stage.TryGetProperty("canRetry", out var retry) &&
					retry.ValueKind is JsonValueKind.True or JsonValueKind.False &&
					retry.GetBoolean();
				parsed.Add(new StartupLifecycleStageViewModel(
					id,
					displayName,
					requirement,
					status.ToUpperInvariant(),
					statusText,
					failureReason,
					startedAt,
					completedAt,
					canRetry,
					string.Equals(id, activeStageId, StringComparison.Ordinal)));
			}
		}

		if (parsed.Count == 0)
		{
			Summary = "AppHost lifecycle evidence contains no visible stages.";
			return;
		}

		UpdateFailureLatch(parsed);

		Stages.Clear();
		foreach (var stage in parsed)
			Stages.Add(stage);

		if (!_initialStartupCompleted)
		{
			var requiredStages = parsed
				.Where(stage => stage.Requirement != StartupLifecycleRequirement.Optional)
				.ToArray();
			_initialStartupCompleted =
				_latchedFailureStageId is null &&
				requiredStages.Length > 0 &&
				requiredStages.All(stage => string.Equals(stage.Status, "READY", StringComparison.Ordinal));
		}

		var activeStage = parsed.FirstOrDefault(stage => stage.IsActive);
		var failedStage = parsed.FirstOrDefault(stage =>
			stage.Requirement != StartupLifecycleRequirement.Optional &&
			string.Equals(stage.Status, "FAILED", StringComparison.Ordinal));
		var degradedStage = parsed.FirstOrDefault(stage =>
			string.Equals(stage.Status, "DEGRADED", StringComparison.Ordinal) ||
			(stage.Requirement == StartupLifecycleRequirement.Optional &&
			 string.Equals(stage.Status, "FAILED", StringComparison.Ordinal)));
		var latchedStage = _latchedFailureStageId is null
			? null
			: parsed.FirstOrDefault(stage => string.Equals(stage.Id, _latchedFailureStageId, StringComparison.Ordinal));

		ActiveStageName = _latchedFailureStageId is not null
			? _latchedFailureStageName ?? latchedStage?.DisplayName ?? "Startup Failure"
			: activeStage?.DisplayName ??
				failedStage?.DisplayName ??
				degradedStage?.DisplayName ??
				"Production Readiness";
		Summary = _latchedFailureStageId is not null
			? ResolveLatchedFailureSummary(latchedStage)
			: failedStage is not null
				? failedStage.FailureReason ?? $"{failedStage.DisplayName} failed."
				: degradedStage is not null
					? degradedStage.FailureReason ?? degradedStage.StatusText ?? $"{degradedStage.DisplayName} is degraded."
					: activeStage is not null
						? activeStage.StatusText ?? $"{activeStage.DisplayName} is starting."
						: "All published startup stages are complete.";
		ObservedAt = observedAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";
		NextStep = ResolveNextStep(activeStage, degradedStage, latchedStage);
		DiagnosticActionStatus = string.Empty;

		RaiseProgressiveStateProperties();
	}

	private void ApplyFallback()
	{
		if (Stages.Count != 0) return;
		Stages.Add(new StartupLifecycleStageViewModel(
			"operator-interface",
			"Operator Interface",
			StartupLifecycleRequirement.Critical,
			"STARTING",
			"Waiting for AppHost lifecycle evidence.",
			null,
			null,
			null,
			false,
			true));
		ActiveStageName = "Operator Interface";
		Summary = "Waiting for AppHost lifecycle evidence.";
		NextStep = HasEvidence
			? "Waiting for authoritative AppHost lifecycle evidence."
			: "Direct Operator startup has no AppHost startup evidence; runtime recovery remains available through the normal workspace surfaces.";
		RaiseProgressiveStateProperties();
	}

	private void RaiseProgressiveStateProperties()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShellAvailable)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCriticalFailure)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStartupFailure)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStartupDegradation)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCompletedInitialStartup)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartupPhaseLabel)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartupVisualState)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadinessSummary)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDeferredInitialization)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiagnosticPath)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDiagnosticPath)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenDiagnostics)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiagnosticActionLabel)));
		_copyDiagnosticsCommand.RaiseCanExecuteChanged();
		_openDiagnosticsCommand.RaiseCanExecuteChanged();
	}

	private void UpdateFailureLatch(IReadOnlyList<StartupLifecycleStageViewModel> stages)
	{
		if (_initialStartupCompleted)
			return;

		if (_latchedFailureStageId is not null)
		{
			var recoveryStage = stages.FirstOrDefault(stage =>
				string.Equals(stage.Id, _latchedFailureStageId, StringComparison.Ordinal));
			if (recoveryStage is not null &&
				string.Equals(recoveryStage.Status, "READY", StringComparison.Ordinal))
			{
				_latchedFailureStageId = null;
				_latchedFailureStageName = null;
				_latchedFailureReason = null;
			}
			else
			{
				return;
			}
		}

		var failedStage = stages.FirstOrDefault(stage =>
			stage.Requirement != StartupLifecycleRequirement.Optional &&
			string.Equals(stage.Status, "FAILED", StringComparison.Ordinal));
		if (failedStage is null)
			return;

		_latchedFailureStageId = failedStage.Id;
		_latchedFailureStageName = failedStage.DisplayName;
		_latchedFailureReason = failedStage.FailureReason ?? failedStage.StatusText ?? $"{failedStage.DisplayName} failed.";
	}

	private string ResolveLatchedFailureSummary(StartupLifecycleStageViewModel? stage)
	{
		if (stage is not null &&
			(string.Equals(stage.Status, "STARTING", StringComparison.Ordinal) ||
			 string.Equals(stage.Status, "DEGRADED", StringComparison.Ordinal)))
		{
			return $"{stage.DisplayName} recovery is in progress. Original failure: {_latchedFailureReason ?? "Startup stage failed."}";
		}

		return _latchedFailureReason ?? $"{_latchedFailureStageName ?? "Startup stage"} failed.";
	}

	private string ResolveNextStep(
		StartupLifecycleStageViewModel? activeStage,
		StartupLifecycleStageViewModel? degradedStage,
		StartupLifecycleStageViewModel? latchedStage)
	{
		if (_initialStartupCompleted)
			return "Initial production qualification completed. Later runtime changes stay in the workspace recovery surfaces.";

		if (_latchedFailureStageId is not null)
		{
			if (latchedStage is not null &&
				(string.Equals(latchedStage.Status, "STARTING", StringComparison.Ordinal) ||
				 string.Equals(latchedStage.Status, "DEGRADED", StringComparison.Ordinal)))
			{
				return "Recovery is being driven by the existing AppHost/ControlHost lifecycle. Keep this screen open and review diagnostics if readiness does not return.";
			}

			return "Review technical details and diagnostics, correct the reported cause, then recover through the supported application lifecycle. No Operator retry is exposed without a safe recovery command.";
		}

		if (degradedStage is not null)
		{
			return degradedStage.Requirement == StartupLifecycleRequirement.Optional
				? "An optional startup stage is unavailable. Required startup can continue; review diagnostics if that capability is needed."
				: "Wait for authoritative AppHost/ControlHost recovery. The Operator does not start a second supervisory recovery path.";
		}

		return activeStage is not null
			? $"Waiting for {activeStage.DisplayName} to publish authoritative readiness."
			: "Waiting for complete production readiness evidence.";
	}

	private Task CopyDiagnosticsAsync()
	{
		try
		{
			System.Windows.Clipboard.SetText(BuildDiagnosticsText());
			DiagnosticActionStatus = "Diagnostics copied to clipboard.";
		}
		catch (ExternalException exception)
		{
			DiagnosticActionStatus = $"Unable to copy diagnostics: {exception.Message}";
		}

		return Task.CompletedTask;
	}

	private Task OpenDiagnosticsAsync()
	{
		var target = ResolveDiagnosticOpenTarget();
		if (target is null)
		{
			DiagnosticActionStatus = "Published diagnostics are not available on this machine.";
			return Task.CompletedTask;
		}

		try
		{
			Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
			DiagnosticActionStatus = File.Exists(target)
				? "Diagnostic log opened."
				: "Diagnostics folder opened.";
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
		{
			DiagnosticActionStatus = $"Unable to open diagnostics: {exception.Message}";
		}

		return Task.CompletedTask;
	}

	private string? ResolveDiagnosticOpenTarget()
	{
		if (string.IsNullOrWhiteSpace(_diagnosticPath))
			return null;
		if (File.Exists(_diagnosticPath))
			return _diagnosticPath;

		var directory = Path.GetDirectoryName(_diagnosticPath);
		return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
			? directory
			: null;
	}

	private string BuildDiagnosticsText()
	{
		var builder = new StringBuilder();
		builder.AppendLine("rtaime startup diagnostics");
		builder.AppendLine($"Phase: {StartupPhaseLabel}");
		builder.AppendLine($"Observed: {ObservedAt}");
		builder.AppendLine($"Evidence: {EvidencePath}");
		builder.AppendLine($"Diagnostics: {DiagnosticPath}");
		builder.AppendLine($"Active stage: {ActiveStageName}");
		builder.AppendLine($"Summary: {Summary}");
		builder.AppendLine($"Next step: {NextStep}");
		builder.AppendLine();

		foreach (var stage in Stages)
		{
			builder.AppendLine($"[{stage.Status}] {stage.DisplayName}");
			builder.AppendLine($"  {stage.TechnicalDetail}");
			if (!string.IsNullOrWhiteSpace(stage.StatusText))
				builder.AppendLine($"  Status: {stage.StatusText}");
			if (!string.IsNullOrWhiteSpace(stage.FailureReason))
				builder.AppendLine($"  Failure: {stage.FailureReason}");
		}

		return builder.ToString().TrimEnd();
	}

	private static StartupLifecycleRequirement ResolveRequirement(
		string id,
		string? value,
		string? statusText)
	{
		if (!string.IsNullOrWhiteSpace(value) &&
			Enum.TryParse<StartupLifecycleRequirement>(value, ignoreCase: true, out var parsed))
		{
			return parsed;
		}

		if (string.Equals(id, "application-bootstrap", StringComparison.Ordinal) ||
			string.Equals(id, "configuration", StringComparison.Ordinal) ||
			string.Equals(id, "operator-interface", StringComparison.Ordinal))
		{
			return StartupLifecycleRequirement.Critical;
		}

		if (string.Equals(id, "ai-host", StringComparison.Ordinal) &&
			statusText?.Contains("not required", StringComparison.OrdinalIgnoreCase) == true)
		{
			return StartupLifecycleRequirement.Optional;
		}

		return StartupLifecycleRequirement.RequiredForProduction;
	}

	private static string ReadString(JsonElement element, string name, string fallback) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? fallback
			: fallback;

	private static string? ReadNullableString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
			return null;
		return DateTimeOffset.TryParse(
			value.GetString(),
			System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.RoundtripKind,
			out var parsed)
				? parsed
				: null;
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		if (propertyName == nameof(DetailsVisible))
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsAction)));
		return true;
	}
}

public sealed record StartupLifecycleStageViewModel(
	string Id,
	string DisplayName,
	StartupLifecycleRequirement Requirement,
	string Status,
	string? StatusText,
	string? FailureReason,
	DateTimeOffset? StartedAt,
	DateTimeOffset? CompletedAt,
	bool CanRetry,
	bool IsActive)
{
	public string StatusSummary
	{
		get
		{
			var duration = Duration;
			return duration is null
				? Status
				: $"{Status} · {FormatDuration(duration.Value)}";
		}
	}

	public string TechnicalDetail
	{
		get
		{
			var started = StartedAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";
			var completed = CompletedAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";
			return $"Id {Id} · {Requirement} · started {started} · completed {completed}";
		}
	}

	public TimeSpan? Duration =>
		StartedAt is { } started && CompletedAt is { } completed && completed >= started
			? completed - started
			: null;

	private static string FormatDuration(TimeSpan duration) =>
		duration.TotalSeconds >= 1
			? $"{duration.TotalSeconds:0.00} s"
			: $"{Math.Max(0, duration.TotalMilliseconds):0} ms";
}
