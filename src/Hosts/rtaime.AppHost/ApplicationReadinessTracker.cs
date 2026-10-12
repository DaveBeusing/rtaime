// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.AppHost;

internal sealed class ApplicationReadinessTracker
{
	private sealed class HostObservation(string host)
	{
		public string Host { get; } = host;
		public int? ProcessId { get; set; }
		public bool? Alive { get; set; }
		public DateTimeOffset? ProcessAt { get; set; }
		public string? State { get; set; }
		public string? Health { get; set; }
		public DateTimeOffset? LifecycleAt { get; set; }
		public string? Supervision { get; set; }
		public DateTimeOffset? SupervisionAt { get; set; }
		public bool? Ipc { get; set; }
		public DateTimeOffset? IpcAt { get; set; }
		public string? Phase { get; set; }
		public string? Outcome { get; set; }
		public DateTimeOffset? PhaseAt { get; set; }
		public string? Reason { get; set; }
	}

	private readonly ApplicationHostOptions _options;
	private readonly IApplicationHostPlatform _platform;
	private readonly Dictionary<string, HostObservation> _hosts = new(StringComparer.Ordinal)
	{
		["ControlHost"] = new("ControlHost"),
		["RuntimeHost"] = new("RuntimeHost"),
		["AIHost"] = new("AIHost")
	};
	private ApplicationReadinessEvidence? _readiness;
	private string _readinessReason = "readiness.evidence.missing";
	private string _readinessPath;
	private DateTimeOffset? _readinessObservedAt;

	public ApplicationReadinessTracker(ApplicationHostOptions options, IApplicationHostPlatform platform)
	{
		_options = options;
		_platform = platform;
		_readinessPath = options.ReadinessPath;
	}

	public void Observe(int? controlProcessId, ApplicationReadinessEvidence? readiness, string? path, string reason)
	{
		_readiness = readiness;
		_readinessReason = reason;
		if (path is not null) _readinessPath = path;
		_readinessObservedAt = _platform.UtcNow;
		var control = _hosts["ControlHost"];
		SetIdentity(control, controlProcessId ?? readiness?.ProcessId);
		if (readiness is not null && control.ProcessId == readiness.ProcessId)
		{
			control.State = readiness.State;
			control.Health = readiness.Health;
			control.LifecycleAt = readiness.ReadyAtUtc;
			SetSupervision(_hosts["RuntimeHost"], readiness.RuntimeSupervision, readiness.ReadyAtUtc);
			SetSupervision(_hosts["AIHost"], readiness.AISupervision, readiness.ReadyAtUtc);
		}

		RefreshDiagnosticEvents(control.ProcessId);
	}

	public void RefreshDiagnosticEvents(int? controlProcessId)
	{
		var control = _hosts["ControlHost"];
		SetIdentity(control, controlProcessId);
		var events = _platform.ReadHostDiagnosticEvents();
		foreach (var observation in events.Where(item =>
			item.Host == "ControlHost" && item.ProcessId == control.ProcessId))
		{
			var values = observation.Dimensions;
			if (observation.Code is "controlhost.runtime-supervision-changed" or "controlhost.ai-supervision-changed")
			{
				var child = _hosts[observation.Code.Contains(".runtime-", StringComparison.Ordinal) ? "RuntimeHost" : "AIHost"];
				if (child.SupervisionAt > observation.ObservedAtUtc) continue;
				child.Supervision = values.GetValueOrDefault("state");
				child.SupervisionAt = observation.ObservedAtUtc;
				if (int.TryParse(values.GetValueOrDefault("processId"), out var pid))
					SetIdentity(child, pid);
			}
			else ApplyEvent(control, observation);
		}
		foreach (var observation in events.Where(item => item.Host != "ControlHost"))
		{
			if (_hosts.TryGetValue(observation.Host, out var host) && host.ProcessId == observation.ProcessId)
				ApplyEvent(host, observation);
		}
		RefreshProcessLiveness();
	}

	public void RefreshProcessLiveness()
	{
		foreach (var host in _hosts.Values)
			if (host.ProcessId is { } pid)
			{
				host.Alive = _platform.IsProcessAlive(pid);
				host.ProcessAt = _platform.UtcNow;
			}
	}

	public void ObservePipe(string host, bool reachable)
	{
		_hosts[host].Ipc = reachable;
		_hosts[host].IpcAt = _platform.UtcNow;
	}

	public ApplicationReadinessFailureSnapshot CaptureFailure(string reason, string detail, bool deadlineExpired)
	{
		RefreshProcessLiveness();
		return new(_platform.UtcNow, reason, detail, deadlineExpired,
			GetHosts(), GetCriteria().Where(item => !item.Satisfied).ToArray(),
			_readinessPath, _options.ControlHostDiagnosticPath);
	}

	public IReadOnlyList<ApplicationHostReadinessObservation> GetHosts() => _hosts.Values.Select(host =>
	{
		var required = host.Host != "AIHost" || _options.RequireAI;
		var directlyFailed = host.Alive == false ||
			EqualsState(host.State, "Failed") || EqualsState(host.Supervision, "Failed");
		var healthy = host.Host == "ControlHost"
			? EqualsState(host.State, "Ready") && EqualsState(host.Health, "Healthy") &&
				_readiness is not null && _readiness.ProcessId == host.ProcessId &&
				_readiness.ControlEndpoint == _options.Endpoints.Control
			: EqualsState(host.Supervision, "Healthy");
		var privatePipeRequired = _options.Ownership != ApplicationLifecycleOwnership.ExternalManaged ||
			host.Host == "ControlHost";
		var qualified = healthy && host.Alive != false && (!privatePipeRequired || host.Ipc == true);
		var degraded = EqualsState(host.State, "Degraded") || EqualsState(host.Health, "Degraded") ||
			EqualsState(host.Supervision, "Recovering") || EqualsState(host.Supervision, "ReadinessGrace") ||
			host.Ipc == false;
		return new ApplicationHostReadinessObservation(
			host.Host, host.ProcessId, host.Alive, host.ProcessAt,
			host.State, host.Health, host.LifecycleAt, host.Supervision, host.SupervisionAt,
			host.Ipc, host.IpcAt, host.Phase, host.Outcome, host.PhaseAt,
			!required ? "NotRequired" : directlyFailed ? "Failed" : qualified ? "Ready" : degraded ? "Degraded" : "Pending",
			directlyFailed ? host.Alive == false ? "process.exited" : "host.failed" : host.Reason);
	}).ToArray();

	private IReadOnlyList<ApplicationReadinessCriterion> GetCriteria()
	{
		var criteria = new List<ApplicationReadinessCriterion>();
		void Add(string host, string code, bool satisfied, DateTimeOffset? at, string detail) =>
			criteria.Add(new(host, code, satisfied, at, detail));
		Add("ControlHost", _readinessReason, _readiness is not null, _readinessObservedAt,
			_readiness is null ? "Current qualified readiness document is unavailable or invalid." : "Readiness document was observed.");
		var control = _hosts["ControlHost"];
		Add("ControlHost", "control.lifecycle.ready", EqualsState(control.State, "Ready"), control.LifecycleAt,
			$"Observed lifecycle: {control.State ?? "unknown"}.");
		Add("ControlHost", "control.health.healthy", EqualsState(control.Health, "Healthy"), control.LifecycleAt,
			$"Observed health: {control.Health ?? "unknown"}.");
		Add("ControlHost", "control.binding.qualified", _readiness is not null &&
			EqualsState(_readiness.State, "Ready") && EqualsState(_readiness.Health, "Healthy"), control.PhaseAt,
			$"Last published binding phase: {control.Phase ?? "unknown"} / {control.Outcome ?? "unknown"}.");
		var endpoints = _options.Endpoints;
		Add("ControlHost", "readiness.endpoints.match", _readiness is not null &&
			_readiness.ControlEndpoint == endpoints.Control && _readiness.RuntimeEndpoint == endpoints.Runtime &&
			_readiness.AIEndpoint == endpoints.AI, _readinessObservedAt, "Readiness endpoint identities must match this instance.");
		Add("ControlHost", "readiness.process.match", _readiness is not null &&
			_readiness.ProcessId == control.ProcessId, _readinessObservedAt, "Readiness must describe the observed ControlHost process.");
		foreach (var host in _hosts.Values.Where(item => item.Host != "AIHost" || _options.RequireAI))
		{
			Add(host.Host, "process.alive", host.Host == "ControlHost" ? host.Alive == true : host.Alive != false &&
				host.Supervision is not null, host.ProcessAt, host.Alive is null
					? "No owned PID observed; endpoint-adopted children require healthy supervision." : $"Process alive: {host.Alive}.");
			if (host.Host != "ControlHost")
				Add(host.Host, "supervision.healthy", EqualsState(host.Supervision, "Healthy"), host.SupervisionAt,
					$"Observed supervision: {host.Supervision ?? "unknown"}.");
			if (_options.Ownership != ApplicationLifecycleOwnership.ExternalManaged || host.Host == "ControlHost")
				Add(host.Host, "ipc.reachable", host.Ipc == true, host.IpcAt,
					host.Ipc is null ? "Endpoint was not observed." : $"Endpoint reachable: {host.Ipc}.");
		}
		return criteria;
	}

	private static bool EqualsState(string? actual, string expected) =>
		string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

	private static void SetIdentity(HostObservation host, int? pid)
	{
		if (pid is null || host.ProcessId == pid) return;
		host.ProcessId = pid;
		host.Alive = null;
		host.ProcessAt = null;
		host.State = null;
		host.Health = null;
		host.LifecycleAt = null;
		host.Phase = null;
		host.Outcome = null;
		host.PhaseAt = null;
		host.Reason = null;
		host.Ipc = null;
		host.IpcAt = null;
	}

	private static void SetSupervision(HostObservation host, ApplicationSupervisionEvidence? evidence, DateTimeOffset? at)
	{
		if (evidence is null) return;
		SetIdentity(host, evidence.ProcessId);
		host.Supervision = evidence.State;
		host.SupervisionAt = at;
	}

	private static void ApplyEvent(HostObservation host, ApplicationHostDiagnosticEvent observation)
	{
		var values = observation.Dimensions;
		if (observation.Code.StartsWith("controlhost.binding.", StringComparison.Ordinal))
		{
			if (host.PhaseAt > observation.ObservedAtUtc) return;
			host.Phase = values.GetValueOrDefault("phase");
			host.Outcome = values.GetValueOrDefault("outcome");
			host.Reason = values.GetValueOrDefault("reason");
			host.PhaseAt = observation.ObservedAtUtc;
		}
		else if (observation.Code.EndsWith("state-changed", StringComparison.Ordinal) ||
			observation.Code.EndsWith(".completed", StringComparison.Ordinal))
		{
			if (host.LifecycleAt > observation.ObservedAtUtc) return;
			host.State = values.GetValueOrDefault("state");
			host.Health = values.GetValueOrDefault("health");
			host.LifecycleAt = observation.ObservedAtUtc;
		}
	}
}
