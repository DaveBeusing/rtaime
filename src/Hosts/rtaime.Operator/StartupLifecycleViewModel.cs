// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
	private FileSystemWatcher? _watcher;
	private bool _detailsVisible;
	private bool _initialStartupCompleted;
	private string _activeStageName = "Application Bootstrap";
	private string _summary = "Waiting for application lifecycle evidence.";
	private string _observedAt = "—";
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
	public string EvidencePath => _evidencePath ?? "Direct Operator startup; AppHost lifecycle evidence is unavailable.";
	public bool HasEvidence => _evidencePath is not null;
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
		Stages.Any(stage =>
			stage.Requirement != StartupLifecycleRequirement.Optional &&
			string.Equals(stage.Status, "FAILED", StringComparison.Ordinal));
	public bool HasCompletedInitialStartup => !HasEvidence || _initialStartupCompleted;
	public string StartupPhaseLabel => HasStartupFailure
		? "STARTUP ATTENTION REQUIRED"
		: HasCompletedInitialStartup
			? "PRODUCTION RUNTIME READY"
			: "STARTING PRODUCTION RUNTIME";
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
			return $"{readyStages} / {requiredStages.Length} REQUIRED STAGES READY";
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

		Stages.Clear();
		foreach (var stage in parsed)
			Stages.Add(stage);

		var activeStage = parsed.FirstOrDefault(stage => stage.IsActive);
		var failedStage = parsed.FirstOrDefault(stage => string.Equals(stage.Status, "FAILED", StringComparison.Ordinal));
		var degradedStage = parsed.FirstOrDefault(stage => string.Equals(stage.Status, "DEGRADED", StringComparison.Ordinal));
		ActiveStageName = activeStage?.DisplayName ??
			failedStage?.DisplayName ??
			degradedStage?.DisplayName ??
			"Production Readiness";
		Summary = failedStage is not null
			? failedStage.FailureReason ?? $"{failedStage.DisplayName} failed."
			: degradedStage is not null
				? degradedStage.StatusText ?? $"{degradedStage.DisplayName} is degraded."
				: activeStage is not null
					? activeStage.StatusText ?? $"{activeStage.DisplayName} is starting."
					: "All published startup stages are complete.";
		ObservedAt = observedAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";

		if (!_initialStartupCompleted)
		{
			var requiredStages = parsed
				.Where(stage => stage.Requirement != StartupLifecycleRequirement.Optional)
				.ToArray();
			_initialStartupCompleted =
				requiredStages.Length > 0 &&
				requiredStages.All(stage => string.Equals(stage.Status, "READY", StringComparison.Ordinal));
		}

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
		RaiseProgressiveStateProperties();
	}

	private void RaiseProgressiveStateProperties()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShellAvailable)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCriticalFailure)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasStartupFailure)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCompletedInitialStartup)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartupPhaseLabel)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadinessSummary)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDeferredInitialization)));
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
