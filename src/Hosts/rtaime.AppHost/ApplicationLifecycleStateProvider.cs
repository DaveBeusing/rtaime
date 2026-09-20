// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;

namespace rtaime.AppHost;

public enum LifecycleStageStatus
{
	Pending,
	Starting,
	Ready,
	Degraded,
	Failed
}

public static class ApplicationLifecycleStages
{
	public const string ApplicationBootstrap = "application-bootstrap";
	public const string Configuration = "configuration";
	public const string OperatorInterface = "operator-interface";
	public const string ControlHost = "control-host";
	public const string RuntimeHost = "runtime-host";
	public const string AIHost = "ai-host";
	public const string ProductionReadiness = "production-readiness";
}

public sealed record LifecycleStageSnapshot(
	string Id,
	string DisplayName,
	LifecycleStageStatus Status,
	DateTimeOffset? StartedAt,
	DateTimeOffset? CompletedAt,
	string? StatusText,
	string? FailureReason,
	bool CanRetry);

public sealed class LifecycleStateChangedEventArgs(
	LifecycleStageSnapshot? activeStage,
	IReadOnlyList<LifecycleStageSnapshot> stages) : EventArgs
{
	public LifecycleStageSnapshot? ActiveStage { get; } = activeStage;
	public IReadOnlyList<LifecycleStageSnapshot> Stages { get; } = stages;
}

public interface IApplicationLifecycleStateProvider
{
	IReadOnlyList<LifecycleStageSnapshot> Stages { get; }
	LifecycleStageSnapshot? ActiveStage { get; }
	event EventHandler<LifecycleStateChangedEventArgs>? StateChanged;
}

public sealed class ApplicationLifecycleStateProvider : IApplicationLifecycleStateProvider
{
	private readonly object _gate = new();
	private readonly IApplicationHostPlatform _platform;
	private readonly string _evidencePath;
	private readonly bool _requireAI;
	private List<LifecycleStageSnapshot> _stages = [];

	public ApplicationLifecycleStateProvider(
		string evidencePath,
		bool requireAI,
		IApplicationHostPlatform platform)
	{
		if (string.IsNullOrWhiteSpace(evidencePath))
			throw new ArgumentException("Lifecycle evidence path is required.", nameof(evidencePath));

		_platform = platform ?? throw new ArgumentNullException(nameof(platform));
		_evidencePath = Path.GetFullPath(evidencePath);
		_requireAI = requireAI;
		ResetStages();
	}

	public IReadOnlyList<LifecycleStageSnapshot> Stages
	{
		get
		{
			lock (_gate)
				return _stages.ToArray();
		}
	}

	public LifecycleStageSnapshot? ActiveStage
	{
		get
		{
			lock (_gate)
				return ResolveActiveStage(_stages);
		}
	}

	public event EventHandler<LifecycleStateChangedEventArgs>? StateChanged;

	public void Initialize()
	{
		lock (_gate)
		{
			ResetStages();
			PublishLocked();
		}
		RaiseStateChanged();
	}

	public void StartStage(string id, string statusText, bool canRetry = false) =>
		UpdateStage(
			id,
			stage => stage with
			{
				Status = LifecycleStageStatus.Starting,
				StartedAt = stage.StartedAt ?? _platform.UtcNow,
				CompletedAt = null,
				StatusText = statusText,
				FailureReason = null,
				CanRetry = canRetry
			});

	public void CompleteStage(string id, string statusText) =>
		UpdateStage(
			id,
			stage => stage with
			{
				Status = LifecycleStageStatus.Ready,
				StartedAt = stage.StartedAt ?? _platform.UtcNow,
				CompletedAt = _platform.UtcNow,
				StatusText = statusText,
				FailureReason = null,
				CanRetry = false
			});

	public void DegradeStage(string id, string statusText, string? failureReason = null, bool canRetry = false) =>
		UpdateStage(
			id,
			stage => stage with
			{
				Status = LifecycleStageStatus.Degraded,
				StartedAt = stage.StartedAt ?? _platform.UtcNow,
				CompletedAt = null,
				StatusText = statusText,
				FailureReason = failureReason,
				CanRetry = canRetry
			});

	public void FailStage(string id, string failureReason, bool canRetry = false) =>
		UpdateStage(
			id,
			stage => stage with
			{
				Status = LifecycleStageStatus.Failed,
				StartedAt = stage.StartedAt ?? _platform.UtcNow,
				CompletedAt = _platform.UtcNow,
				StatusText = "Startup stage failed.",
				FailureReason = failureReason,
				CanRetry = canRetry
			});

	public void FailActiveStages(string failureReason)
	{
		lock (_gate)
		{
			var now = _platform.UtcNow;
			_stages = _stages
				.Select(stage => stage.Status == LifecycleStageStatus.Starting
					? stage with
					{
						Status = LifecycleStageStatus.Failed,
						CompletedAt = now,
						StatusText = "Startup stage failed.",
						FailureReason = failureReason,
						CanRetry = false
					}
					: stage)
				.ToList();
			PublishLocked();
		}
		RaiseStateChanged();
	}

	private void UpdateStage(
		string id,
		Func<LifecycleStageSnapshot, LifecycleStageSnapshot> update)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentNullException.ThrowIfNull(update);

		lock (_gate)
		{
			var index = _stages.FindIndex(stage => string.Equals(stage.Id, id, StringComparison.Ordinal));
			if (index < 0)
				throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown application lifecycle stage.");

			_stages[index] = update(_stages[index]);
			PublishLocked();
		}
		RaiseStateChanged();
	}

	private void ResetStages()
	{
		_stages =
		[
			Pending(ApplicationLifecycleStages.ApplicationBootstrap, "Application Bootstrap"),
			Pending(ApplicationLifecycleStages.Configuration, "Configuration"),
			Pending(ApplicationLifecycleStages.OperatorInterface, "Operator Interface"),
			Pending(ApplicationLifecycleStages.ControlHost, "ControlHost"),
			Pending(ApplicationLifecycleStages.RuntimeHost, "RuntimeHost"),
			Pending(
				ApplicationLifecycleStages.AIHost,
				"AIHost",
				_requireAI ? "Waiting for startup." : "Not required by the selected startup profile."),
			Pending(ApplicationLifecycleStages.ProductionReadiness, "Production Readiness")
		];
	}

	private static LifecycleStageSnapshot Pending(string id, string displayName, string statusText = "Waiting for startup.") =>
		new(
			id,
			displayName,
			LifecycleStageStatus.Pending,
			null,
			null,
			statusText,
			null,
			false);

	private void PublishLocked()
	{
		var activeStage = ResolveActiveStage(_stages);
		var payload = JsonSerializer.Serialize(
			new
			{
				copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
				schemaVersion = "1.0",
				observedAtUtc = _platform.UtcNow,
				activeStageId = activeStage?.Id,
				stages = _stages.Select(stage => new
				{
					id = stage.Id,
					displayName = stage.DisplayName,
					status = stage.Status.ToString(),
					startedAtUtc = stage.StartedAt,
					completedAtUtc = stage.CompletedAt,
					statusText = stage.StatusText,
					failureReason = stage.FailureReason,
					canRetry = stage.CanRetry
				})
			},
			new JsonSerializerOptions { WriteIndented = true });

		_platform.WriteAllText(_evidencePath, payload + Environment.NewLine);
	}

	private void RaiseStateChanged()
	{
		var stages = Stages;
		StateChanged?.Invoke(this, new LifecycleStateChangedEventArgs(ResolveActiveStage(stages), stages));
	}

	private static LifecycleStageSnapshot? ResolveActiveStage(IReadOnlyList<LifecycleStageSnapshot> stages) =>
		stages.FirstOrDefault(stage => stage.Status == LifecycleStageStatus.Failed) ??
		stages.FirstOrDefault(stage => stage.Status == LifecycleStageStatus.Degraded) ??
		stages.FirstOrDefault(stage => stage.Status == LifecycleStageStatus.Starting);
}
