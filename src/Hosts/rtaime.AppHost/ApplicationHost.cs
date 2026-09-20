// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace rtaime.AppHost;

public enum ApplicationStartupProfile
{
	Interactive,
	Showcase,
	HeadlessEngine
}

public enum ApplicationLifecycleOwnership
{
	EphemeralLocal,
	PersistentEngine,
	ExternalManaged
}

public enum ApplicationLifecycleState
{
	Stopped,
	Starting,
	Healthy,
	Degraded,
	Recovering,
	Failed,
	Stopping
}

public sealed record ApplicationEndpointSet(string Control, string Runtime, string AI);

public sealed record ApplicationLifecyclePolicy(
	ApplicationEndpointSet Endpoints,
	TimeSpan StartupTimeout,
	TimeSpan ProbeTimeout,
	TimeSpan ProbeInterval,
	TimeSpan ChildRestartBackoff,
	int ChildMaxStartAttempts,
	TimeSpan ShutdownTimeout)
{
	public static ApplicationLifecyclePolicy Load(string installRoot, string? explicitPath = null)
	{
		var candidates = new[]
		{
			explicitPath,
			Path.Combine(AppContext.BaseDirectory, "host-lifecycle-policy.json"),
			Path.Combine(installRoot, "tools", "host-lifecycle-policy.json")
		}.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!)).Distinct(StringComparer.OrdinalIgnoreCase);

		var policyPath = candidates.FirstOrDefault(File.Exists)
			?? throw new FileNotFoundException("Managed host lifecycle policy was not found.");

		using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
		var root = document.RootElement;
		if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetString() != "1.0")
			throw new InvalidDataException("Unsupported managed host lifecycle policy schema version.");

		var endpoints = root.GetProperty("endpoints");
		var startup = root.GetProperty("startup");
		var shutdown = root.GetProperty("shutdown");
		return new ApplicationLifecyclePolicy(
			new ApplicationEndpointSet(
				endpoints.GetProperty("control").GetString() ?? throw new InvalidDataException("Control endpoint is missing."),
				endpoints.GetProperty("runtime").GetString() ?? throw new InvalidDataException("Runtime endpoint is missing."),
				endpoints.GetProperty("ai").GetString() ?? throw new InvalidDataException("AI endpoint is missing.")),
			TimeSpan.FromMilliseconds(ReadInt(startup, "timeoutMs", 30000)),
			TimeSpan.FromMilliseconds(ReadInt(startup, "probeTimeoutMs", 500)),
			TimeSpan.FromMilliseconds(ReadInt(startup, "probeIntervalMs", 200)),
			TimeSpan.FromMilliseconds(ReadInt(startup, "childRestartBackoffMs", 500)),
			ReadInt(startup, "childMaxStartAttempts", 5),
			TimeSpan.FromMilliseconds(ReadInt(shutdown, "timeoutMs", 15000)));
	}

	private static int ReadInt(JsonElement element, string name, int fallback) =>
		element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
}

public sealed record ApplicationHostOptions(
	ApplicationStartupProfile Profile,
	string InstallRoot,
	string StateRoot,
	string WorkRoot,
	string InstanceId,
	ApplicationLifecycleOwnership Ownership,
	bool WindowsService,
	string WindowsServiceName,
	string OperatorPipeSid,
	bool RequireAI,
	bool DisposableInteractiveSession,
	ApplicationLifecyclePolicy Policy)
{
	public string ReadinessPath => Path.Combine(WorkRoot, "control-readiness.json");
	public string StopPath => Path.Combine(WorkRoot, "control-stop.signal");
	public string ShutdownEvidencePath => Path.Combine(WorkRoot, "apphost-shutdown.json");
	public string ControlHostDiagnosticPath => Path.Combine(WorkRoot, "controlhost-process.log");
	public string ServiceReadinessEvidencePath => Path.Combine(WorkRoot, "apphost-readiness.json");
	public string LifecycleEvidencePath => Path.Combine(WorkRoot, "apphost-lifecycle.json");
	public string LegacyLifecycleStatePath => Path.Combine($"{InstallRoot}.host-lifecycle", "lifecycle-state.json");

	public ApplicationEndpointSet Endpoints
	{
		get
		{
			if (InstanceId == "default") return Policy.Endpoints;
			return new ApplicationEndpointSet(
				WithInstance(Policy.Endpoints.Control),
				WithInstance(Policy.Endpoints.Runtime),
				WithInstance(Policy.Endpoints.AI));
		}
	}

	public static ApplicationHostOptions Load(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);
		var values = args
			.Where(argument => argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains('='))
			.Select(argument => argument[2..].Split('=', 2))
			.ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

		string Resolve(string name, string environment, string fallback) =>
			values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
				? value
				: Environment.GetEnvironmentVariable(environment) is { Length: > 0 } configured ? configured : fallback;

		var profileText = Resolve("profile", "RTAIME_STARTUP_PROFILE", "Interactive");
		if (!Enum.TryParse<ApplicationStartupProfile>(profileText, true, out var profile))
			throw new ArgumentException($"Unknown startup profile '{profileText}'.", nameof(args));

		var installRoot = Path.GetFullPath(Resolve("install-root", "RTAIME_INSTALL_ROOT", AppContext.BaseDirectory));
		var instanceId = Resolve("instance-id", "RTAIME_INSTANCE_ID", "default");
		if (!System.Text.RegularExpressions.Regex.IsMatch(instanceId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"))
			throw new ArgumentException("InstanceId must contain only letters, digits, '.', '_' or '-' and be at most 64 characters.", nameof(args));

		var windowsService = args.Contains("--windows-service", StringComparer.OrdinalIgnoreCase);
		var windowsServiceName = Resolve("service-name", "RTAIME_WINDOWS_SERVICE_NAME", "rtaime-engine");
		if (!System.Text.RegularExpressions.Regex.IsMatch(windowsServiceName, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
			throw new ArgumentException("Windows service name contains unsupported characters.", nameof(args));
		var operatorPipeSid = Resolve("operator-pipe-sid", "RTAIME_OPERATOR_PIPE_SID", string.Empty);
		if (!string.IsNullOrWhiteSpace(operatorPipeSid) &&
			!System.Text.RegularExpressions.Regex.IsMatch(operatorPipeSid, "^S-1-(?:[0-9]+-){1,14}[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
			throw new ArgumentException("Operator pipe SID is not a valid Windows SID string.", nameof(args));
		var disposable = args.Contains("--disposable", StringComparer.OrdinalIgnoreCase);
		var defaultOwnership = profile == ApplicationStartupProfile.HeadlessEngine
			? ApplicationLifecycleOwnership.PersistentEngine
			: ApplicationLifecycleOwnership.EphemeralLocal;
		var ownershipText = Resolve("ownership", "RTAIME_LIFECYCLE_OWNERSHIP", defaultOwnership.ToString());
		if (!Enum.TryParse<ApplicationLifecycleOwnership>(ownershipText, true, out var ownership))
			throw new ArgumentException($"Unknown lifecycle ownership '{ownershipText}'.", nameof(args));
		if (disposable && ownership != ApplicationLifecycleOwnership.EphemeralLocal)
			throw new ArgumentException("--disposable requires EphemeralLocal lifecycle ownership.", nameof(args));
		if (windowsService && profile != ApplicationStartupProfile.HeadlessEngine)
			throw new ArgumentException("--windows-service requires the HeadlessEngine startup profile.", nameof(args));
		if (windowsService && ownership != ApplicationLifecycleOwnership.PersistentEngine)
			throw new ArgumentException("--windows-service requires PersistentEngine lifecycle ownership.", nameof(args));
		if (windowsService && string.IsNullOrWhiteSpace(operatorPipeSid))
			throw new ArgumentException("--windows-service requires --operator-pipe-sid for explicit Operator IPC authorization.", nameof(args));

		var stateFallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "rtaime");
		var stateRoot = Path.GetFullPath(Resolve("state-root", "RTAIME_STATE_ROOT", stateFallback));
		var workFallback = windowsService || ownership == ApplicationLifecycleOwnership.ExternalManaged
			? Path.Combine(stateRoot, "service", instanceId)
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtaime", "apphost", instanceId);
		var workRoot = Path.GetFullPath(Resolve("work-root", "RTAIME_APPHOST_WORK_ROOT", workFallback));
		var policyPath = Resolve("policy", "RTAIME_LIFECYCLE_POLICY", string.Empty);
		var policy = ApplicationLifecyclePolicy.Load(installRoot, string.IsNullOrWhiteSpace(policyPath) ? null : policyPath);
		var requireAI = !args.Contains("--no-ai", StringComparer.OrdinalIgnoreCase);

		return new ApplicationHostOptions(profile, installRoot, stateRoot, workRoot, instanceId, ownership, windowsService, windowsServiceName, operatorPipeSid, requireAI, disposable, policy);
	}

	private string WithInstance(string endpoint) =>
		endpoint.EndsWith(".default", StringComparison.Ordinal)
			? endpoint[..^".default".Length] + "." + InstanceId
			: endpoint + "." + InstanceId;
}

public sealed record ApplicationProcessSpec(
	string ArtifactPath,
	string WorkingDirectory,
	IReadOnlyList<string> Arguments,
	IReadOnlyDictionary<string, string> Environment,
	bool CreateNoWindow)
{
	public string? DiagnosticLogPath { get; init; }
}

public interface IApplicationHostPlatform
{
	DateTimeOffset UtcNow { get; }
	string FindProductArtifact(string installRoot, string baseName);
	int StartProcess(ApplicationProcessSpec spec);
	bool IsProcessAlive(int processId);
	bool IsEndpointLeaseHeld(string endpoint) => false;
	Task FlushProcessDiagnosticsAsync(int processId, CancellationToken cancellationToken) => Task.CompletedTask;
	void KillProcessTree(int processId);
	bool FileExists(string path);
	string ReadAllText(string path);
	void WriteAllText(string path, string content);
	void DeleteFile(string path);
	void CreateDirectory(string path);
	Task<bool> ProbePipeAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken);
	Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemApplicationHostPlatform : IApplicationHostPlatform
{
	private readonly ConcurrentDictionary<int, Process> _diagnosticProcesses = new();
	private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _diagnosticCompletions = new();

	public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

	public string FindProductArtifact(string installRoot, string baseName)
	{
		var productRoot = Directory.Exists(Path.Combine(installRoot, "product")) ? Path.Combine(installRoot, "product") : installRoot;
		if (!Directory.Exists(productRoot)) throw new DirectoryNotFoundException($"Product payload was not found at '{productRoot}'.");

		foreach (var extension in new[] { ".exe", ".dll" })
		{
			var matches = Directory.EnumerateFiles(productRoot, baseName + extension, SearchOption.AllDirectories).ToArray();
			if (matches.Length == 1) return matches[0];
			if (matches.Length > 1) throw new InvalidOperationException($"Expected one '{baseName + extension}' in product payload; found {matches.Length}.");
		}

		var developmentArtifact = FindRepositoryBuildArtifact(installRoot, baseName);
		if (developmentArtifact is not null) return developmentArtifact;

		throw new FileNotFoundException(
			$"Product artifact '{baseName}' was not found below '{productRoot}'. " +
			"Build the complete rtaime.slnx solution or use an installed/offline bundle.");
	}

	private static string? FindRepositoryBuildArtifact(string installRoot, string baseName)
	{
		var repositoryRoot = FindRepositoryRoot(installRoot);
		if (repositoryRoot is null) return null;

		var relativeInstallRoot = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(installRoot));
		var segments = relativeInstallRoot.Split(
			new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
			StringSplitOptions.RemoveEmptyEntries);
		var binIndex = Array.FindIndex(segments, segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase));
		if (binIndex < 0 || binIndex + 1 >= segments.Length) return null;

		var configuration = segments[binIndex + 1];
		var targetFramework = string.Equals(baseName, "rtaime.Operator", StringComparison.Ordinal)
			? "net10.0-windows"
			: "net10.0";
		var artifactRoot = Path.Combine(repositoryRoot, "src", "Hosts", baseName, "bin", configuration, targetFramework);
		if (!Directory.Exists(artifactRoot)) return null;

		foreach (var extension in new[] { ".exe", ".dll" })
		{
			var candidate = Path.Combine(artifactRoot, baseName + extension);
			if (File.Exists(candidate)) return candidate;
		}

		return null;
	}

	private static string? FindRepositoryRoot(string startPath)
	{
		DirectoryInfo? directory = new(Path.GetFullPath(startPath));
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}
		return null;
	}

	public int StartProcess(ApplicationProcessSpec spec)
	{
		var startInfo = new ProcessStartInfo
		{
			UseShellExecute = false,
			CreateNoWindow = spec.CreateNoWindow,
			WorkingDirectory = spec.WorkingDirectory
		};
		if (string.Equals(Path.GetExtension(spec.ArtifactPath), ".dll", StringComparison.OrdinalIgnoreCase))
		{
			startInfo.FileName = "dotnet";
			startInfo.ArgumentList.Add(spec.ArtifactPath);
		}
		else
		{
			startInfo.FileName = spec.ArtifactPath;
		}

		foreach (var argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
		foreach (var pair in spec.Environment) startInfo.Environment[pair.Key] = pair.Value;

		if (string.IsNullOrWhiteSpace(spec.DiagnosticLogPath))
		{
			using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{spec.ArtifactPath}'.");
			return process.Id;
		}

		var diagnosticPath = Path.GetFullPath(spec.DiagnosticLogPath);
		var diagnosticDirectory = Path.GetDirectoryName(diagnosticPath);
		if (!string.IsNullOrWhiteSpace(diagnosticDirectory)) Directory.CreateDirectory(diagnosticDirectory);
		try { if (File.Exists(diagnosticPath)) File.Delete(diagnosticPath); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }

		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		var writeGate = new object();
		var processWithDiagnostics = new Process { StartInfo = startInfo };
		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var processId = 0;
		var finalized = 0;

		void AppendDiagnostic(string stream, string? line)
		{
			if (line is null) return;
			try
			{
				lock (writeGate)
					File.AppendAllText(diagnosticPath, $"{DateTimeOffset.UtcNow:O} stream={stream} {line}{Environment.NewLine}");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}

		void FinalizeDiagnostic()
		{
			if (Interlocked.Exchange(ref finalized, 1) != 0) return;
			try
			{
				processWithDiagnostics.WaitForExit();
			}
			catch (InvalidOperationException)
			{
			}

			try
			{
				AppendDiagnostic("process", $"exitCode={processWithDiagnostics.ExitCode}");
			}
			catch (InvalidOperationException)
			{
				AppendDiagnostic("process", "exitCode=unavailable");
			}

			if (processId != 0)
			{
				_diagnosticProcesses.TryRemove(processId, out _);
				_diagnosticCompletions.TryRemove(processId, out _);
			}
			completion.TrySetResult(true);
			processWithDiagnostics.Dispose();
		}

		processWithDiagnostics.OutputDataReceived += (_, eventArgs) => AppendDiagnostic("stdout", eventArgs.Data);
		processWithDiagnostics.ErrorDataReceived += (_, eventArgs) => AppendDiagnostic("stderr", eventArgs.Data);
		processWithDiagnostics.Exited += (_, _) =>
		{
			_ = Task.Run(FinalizeDiagnostic);
		};

		try
		{
			if (!processWithDiagnostics.Start())
				throw new InvalidOperationException($"Failed to start '{spec.ArtifactPath}'.");
			processId = processWithDiagnostics.Id;
			_diagnosticProcesses[processId] = processWithDiagnostics;
			_diagnosticCompletions[processId] = completion;
			processWithDiagnostics.BeginOutputReadLine();
			processWithDiagnostics.BeginErrorReadLine();
			processWithDiagnostics.EnableRaisingEvents = true;
			return processId;
		}
		catch
		{
			if (processId != 0)
			{
				_diagnosticProcesses.TryRemove(processId, out _);
				_diagnosticCompletions.TryRemove(processId, out _);
			}
			completion.TrySetResult(true);
			processWithDiagnostics.Dispose();
			throw;
		}
	}

	public async Task FlushProcessDiagnosticsAsync(int processId, CancellationToken cancellationToken)
	{
		if (!_diagnosticCompletions.TryGetValue(processId, out var completion)) return;
		await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public bool IsProcessAlive(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	public bool IsEndpointLeaseHeld(string endpoint)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) return false;
		var normalized = endpoint.Trim();
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
		var name = "rtaime.endpoint." + Convert.ToHexString(hash).ToLowerInvariant();
		try
		{
			using var existing = Mutex.OpenExisting(name);
			return true;
		}
		catch (WaitHandleCannotBeOpenedException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return true;
		}
	}

	public void KillProcessTree(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			if (!process.HasExited) process.Kill(entireProcessTree: true);
		}
		catch (ArgumentException)
		{
		}
	}

	public bool FileExists(string path) => File.Exists(path);
	public string ReadAllText(string path) => File.ReadAllText(path);

	public void WriteAllText(string path, string content)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
		File.WriteAllText(path, content);
	}

	public void DeleteFile(string path)
	{
		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch (IOException)
		{
		}
	}

	public void CreateDirectory(string path) => Directory.CreateDirectory(path);

	public async Task<bool> ProbePipeAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
		try
		{
			await pipe.ConnectAsync((int)Math.Max(1, timeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
			return pipe.IsConnected;
		}
		catch (Exception exception) when (exception is IOException or TimeoutException)
		{
			return false;
		}
	}

	public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record ApplicationSupervisionEvidence(string State, int? ProcessId);

public sealed record ApplicationReadinessEvidence(
	int ProcessId,
	string State,
	string Health,
	string ControlEndpoint,
	string RuntimeEndpoint,
	string AIEndpoint,
	ApplicationSupervisionEvidence? RuntimeSupervision,
	ApplicationSupervisionEvidence? AISupervision)
{
	public static bool TryParse(string json, out ApplicationReadinessEvidence? evidence)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			evidence = new ApplicationReadinessEvidence(
				root.GetProperty("processId").GetInt32(),
				root.GetProperty("state").GetString() ?? string.Empty,
				root.GetProperty("health").GetString() ?? string.Empty,
				root.GetProperty("controlEndpoint").GetString() ?? string.Empty,
				root.GetProperty("runtimeEndpoint").GetString() ?? string.Empty,
				root.GetProperty("aiEndpoint").GetString() ?? string.Empty,
				ReadSupervision(root, "runtimeSupervision"),
				ReadSupervision(root, "aiSupervision"));
			return true;
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
		{
			evidence = null;
			return false;
		}
	}

	private static ApplicationSupervisionEvidence? ReadSupervision(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
		int? processId = value.TryGetProperty("processId", out var process) && process.ValueKind == JsonValueKind.Number ? process.GetInt32() : null;
		return new ApplicationSupervisionEvidence(value.GetProperty("state").GetString() ?? string.Empty, processId);
	}
}

public sealed record ApplicationHostRunResult(
	bool Success,
	ApplicationStartupProfile Profile,
	bool AdoptedControlHost,
	int ControlProcessId);

public sealed class UnifiedApplicationHost
{
	private readonly ApplicationHostOptions _options;
	private readonly IApplicationHostPlatform _platform;
	private readonly ApplicationLifecycleStateProvider _lifecycle;
	private int? _ownedControlProcessId;
	private int? _controlProcessId;
	private int? _operatorProcessId;
	private string? _activeReadinessPath;
	private bool _adopted;

	public UnifiedApplicationHost(ApplicationHostOptions options, IApplicationHostPlatform platform)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_platform = platform ?? throw new ArgumentNullException(nameof(platform));
		_lifecycle = new ApplicationLifecycleStateProvider(_options.LifecycleEvidencePath, _options.RequireAI, _platform);
	}

	public ApplicationLifecycleState State { get; private set; } = ApplicationLifecycleState.Stopped;
	public bool OwnsControlLifecycle => _ownedControlProcessId is not null;
	public IApplicationLifecycleStateProvider Lifecycle => _lifecycle;
	public event Action<ApplicationLifecycleState>? StateChanged;

	public async Task<ApplicationHostRunResult> RunAsync(CancellationToken cancellationToken = default)
	{
		Transition(ApplicationLifecycleState.Starting);
		try
		{
			_platform.CreateDirectory(_options.WorkRoot);
			_platform.CreateDirectory(_options.StateRoot);
			_platform.DeleteFile(_options.ShutdownEvidencePath);
			_platform.DeleteFile(_options.ServiceReadinessEvidencePath);

			_lifecycle.Initialize();
			_lifecycle.StartStage(ApplicationLifecycleStages.ApplicationBootstrap, "Preparing the application host.");
			_lifecycle.CompleteStage(ApplicationLifecycleStages.ApplicationBootstrap, "Application host initialized.");
			_lifecycle.StartStage(ApplicationLifecycleStages.Configuration, "Applying startup profile and lifecycle policy.");
			_lifecycle.CompleteStage(ApplicationLifecycleStages.Configuration, "Startup profile and lifecycle policy loaded.");

			if (_options.Profile == ApplicationStartupProfile.HeadlessEngine)
			{
				_lifecycle.CompleteStage(ApplicationLifecycleStages.OperatorInterface, "Operator interface is not required by the HeadlessEngine profile.");
			}
			else
			{
				_lifecycle.StartStage(ApplicationLifecycleStages.OperatorInterface, "Launching the Operator startup experience.");
				_operatorProcessId = StartOperator();
				_lifecycle.CompleteStage(ApplicationLifecycleStages.OperatorInterface, "Operator startup experience is running.");
			}

			_lifecycle.StartStage(ApplicationLifecycleStages.ControlHost, "Discovering qualified ControlHost readiness.");
			var ready = await FindHealthyReadinessAsync(cancellationToken).ConfigureAwait(false);
			if (ready is null)
			{
				if (_options.Ownership == ApplicationLifecycleOwnership.ExternalManaged)
				{
					_lifecycle.StartStage(ApplicationLifecycleStages.ControlHost, "Waiting for externally managed ControlHost readiness.");
					ready = await WaitForExternalReadinessAsync(cancellationToken).ConfigureAwait(false);
					_adopted = true;
					_controlProcessId = ready.Value.Evidence.ProcessId;
					_activeReadinessPath = ready.Value.Path;
				}
				else
				{
					ready = await WaitForLeasedControlReadinessAsync(cancellationToken).ConfigureAwait(false);
					if (ready is not null)
					{
						_adopted = true;
						_controlProcessId = ready.Value.Evidence.ProcessId;
						_activeReadinessPath = ready.Value.Path;
					}
					else
					{
						StartControlHost();
						_lifecycle.StartStage(ApplicationLifecycleStages.ControlHost, "ControlHost process started; waiting for qualified readiness.");
						_lifecycle.StartStage(ApplicationLifecycleStages.RuntimeHost, "Awaiting supervised RuntimeHost readiness.");
						if (_options.RequireAI)
							_lifecycle.StartStage(ApplicationLifecycleStages.AIHost, "Awaiting supervised AIHost readiness.");
						ready = await WaitForReadinessAsync(cancellationToken).ConfigureAwait(false);
					}
				}
			}
			else
			{
				_adopted = true;
				_controlProcessId = ready.Value.Evidence.ProcessId;
				_activeReadinessPath = ready.Value.Path;
				if (_options.WindowsService)
				{
					if (!string.Equals(Path.GetFullPath(ready.Value.Path), Path.GetFullPath(_options.ReadinessPath), StringComparison.OrdinalIgnoreCase))
						throw new InvalidOperationException("Windows service refused to claim a lifecycle outside its deterministic service readiness root.");
					_ownedControlProcessId = ready.Value.Evidence.ProcessId;
				}
			}

			var qualifiedReadiness = ready ?? throw new InvalidOperationException("Engine startup completed without qualified readiness evidence.");
			_lifecycle.CompleteStage(
				ApplicationLifecycleStages.ControlHost,
				_adopted ? "Existing qualified ControlHost adopted." : "ControlHost is qualified and ready.");
			_lifecycle.CompleteStage(ApplicationLifecycleStages.RuntimeHost, "RuntimeHost supervision is healthy and qualified.");
			_lifecycle.CompleteStage(
				ApplicationLifecycleStages.AIHost,
				_options.RequireAI ? "AIHost supervision is healthy and qualified." : "AIHost is not required by the selected startup profile.");
			_lifecycle.StartStage(ApplicationLifecycleStages.ProductionReadiness, "Qualifying the complete production runtime.");
			TrackReadiness(qualifiedReadiness);
			Transition(ApplicationLifecycleState.Healthy);
			_lifecycle.CompleteStage(ApplicationLifecycleStages.ProductionReadiness, "Production runtime is qualified and ready.");

			if (_options.Profile == ApplicationStartupProfile.HeadlessEngine)
			{
				await ObserveHeadlessAsync(cancellationToken).ConfigureAwait(false);
			}
			else
			{
				var operatorProcessId = _operatorProcessId
					?? throw new InvalidOperationException("Operator process was not started for an interactive profile.");
				await ObserveOperatorAsync(operatorProcessId, cancellationToken).ConfigureAwait(false);
				if (_options.Ownership == ApplicationLifecycleOwnership.EphemeralLocal)
					await StopOwnedControlAsync(CancellationToken.None).ConfigureAwait(false);
			}

			Transition(ApplicationLifecycleState.Stopped);
			return new ApplicationHostRunResult(true, _options.Profile, _adopted, _controlProcessId ?? 0);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (ShouldStopOwnedControlOnHostExit())
				await StopOwnedControlAsync(CancellationToken.None).ConfigureAwait(false);
			Transition(ApplicationLifecycleState.Stopped);
			return new ApplicationHostRunResult(true, _options.Profile, _adopted, _controlProcessId ?? 0);
		}
		catch (Exception exception)
		{
			_lifecycle.FailActiveStages(exception.Message);
			if (State == ApplicationLifecycleState.Starting || ShouldStopOwnedControlOnHostExit())
				await StopOwnedControlAsync(CancellationToken.None).ConfigureAwait(false);
			Transition(ApplicationLifecycleState.Failed);
			throw;
		}
	}

	private void StartControlHost()
	{
		var endpoints = _options.Endpoints;
		var controlArtifact = _platform.FindProductArtifact(_options.InstallRoot, "rtaime.ControlHost");
		var runtimeArtifact = _platform.FindProductArtifact(_options.InstallRoot, "rtaime.RuntimeHost");
		var aiArtifact = _options.RequireAI ? _platform.FindProductArtifact(_options.InstallRoot, "rtaime.AIHost") : string.Empty;

		_platform.DeleteFile(_options.ReadinessPath);
		_platform.DeleteFile(_options.StopPath);
		_platform.DeleteFile(_options.ControlHostDiagnosticPath);

		var environment = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["RTAIME_CONTROL_ENDPOINT"] = endpoints.Control,
			["RTAIME_RUNTIME_ENDPOINT"] = endpoints.Runtime,
			["RTAIME_AI_ENDPOINT"] = endpoints.AI,
			["RTAIME_CONTROL_DURABILITY_ROOT"] = _options.StateRoot,
			["RTAIME_RUNTIME_EXECUTABLE"] = runtimeArtifact,
			["RTAIME_HOST_READINESS_FILE"] = _options.ReadinessPath,
			["RTAIME_HOST_STOP_FILE"] = _options.StopPath,
			["RTAIME_SUPERVISION_PROBE_TIMEOUT_MS"] = ((int)_options.Policy.ProbeTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
			["RTAIME_SUPERVISION_PROBE_INTERVAL_MS"] = ((int)_options.Policy.ProbeInterval.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
			["RTAIME_SUPERVISION_RESTART_BACKOFF_MS"] = ((int)_options.Policy.ChildRestartBackoff.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
			["RTAIME_SUPERVISION_MAX_START_ATTEMPTS"] = _options.Policy.ChildMaxStartAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
		};
		if (_options.RequireAI) environment["RTAIME_AI_EXECUTABLE"] = aiArtifact;
		if (!string.IsNullOrWhiteSpace(_options.OperatorPipeSid))
			environment["RTAIME_OPERATOR_PIPE_SID"] = _options.OperatorPipeSid;

		var processId = _platform.StartProcess(new ApplicationProcessSpec(
			controlArtifact,
			Path.GetDirectoryName(controlArtifact) ?? _options.InstallRoot,
			Array.Empty<string>(),
			environment,
			true)
		{
			DiagnosticLogPath = _options.ControlHostDiagnosticPath
		});

		_ownedControlProcessId = processId;
		_controlProcessId = processId;
		_activeReadinessPath = _options.ReadinessPath;
	}

	private int StartOperator()
	{
		var endpoints = _options.Endpoints;
		var operatorArtifact = _platform.FindProductArtifact(_options.InstallRoot, "rtaime.Operator");
		var environment = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["RTAIME_CONTROL_ENDPOINT"] = endpoints.Control,
			["RTAIME_RUNTIME_ENDPOINT"] = endpoints.Runtime,
			["RTAIME_AI_ENDPOINT"] = endpoints.AI,
			["RTAIME_MONITOR_ENDPOINT"] = endpoints.Runtime + ".monitor",
			["RTAIME_APPHOST_LIFECYCLE_FILE"] = _options.LifecycleEvidencePath
		};
		return _platform.StartProcess(new ApplicationProcessSpec(
			operatorArtifact,
			Path.GetDirectoryName(operatorArtifact) ?? _options.InstallRoot,
			Array.Empty<string>(),
			environment,
			false));
	}

	private async Task<(string Path, ApplicationReadinessEvidence Evidence)?> WaitForLeasedControlReadinessAsync(CancellationToken cancellationToken)
	{
		var endpoint = _options.Endpoints.Control;
		if (!_platform.IsEndpointLeaseHeld(endpoint)) return null;

		_lifecycle.StartStage(
			ApplicationLifecycleStages.ControlHost,
			$"ControlHost endpoint '{endpoint}' is already owned; waiting for qualified readiness instead of starting a competing host.");

		var deadline = _platform.UtcNow + _options.Policy.StartupTimeout;
		while (_platform.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var ready = await FindHealthyReadinessAsync(cancellationToken).ConfigureAwait(false);
			if (ready is not null) return ready;
			if (!_platform.IsEndpointLeaseHeld(endpoint)) return null;
			await _platform.DelayAsync(_options.Policy.ProbeInterval, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"ControlHost endpoint '{endpoint}' remains owned by an existing process that did not publish qualified readiness before the configured startup timeout.");
	}

	private async Task<(string Path, ApplicationReadinessEvidence Evidence)?> WaitForReadinessAsync(CancellationToken cancellationToken)
	{
		var deadline = _platform.UtcNow + _options.Policy.StartupTimeout;
		while (_platform.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_controlProcessId is { } controlPid && !_platform.IsProcessAlive(controlPid))
			{
				if (_platform.IsEndpointLeaseHeld(_options.Endpoints.Control))
				{
					_ownedControlProcessId = null;
					var adopted = await WaitForLeasedControlReadinessAsync(cancellationToken).ConfigureAwait(false);
					if (adopted is not null)
					{
						_adopted = true;
						_controlProcessId = adopted.Value.Evidence.ProcessId;
						_activeReadinessPath = adopted.Value.Path;
						return adopted;
					}
				}

				await _platform.FlushProcessDiagnosticsAsync(controlPid, cancellationToken).ConfigureAwait(false);
				throw new InvalidOperationException(BuildControlHostExitDetail("ControlHost exited before qualified readiness."));
			}

			var ready = await FindHealthyReadinessAsync(cancellationToken).ConfigureAwait(false);
			if (ready is not null)
			{
				_controlProcessId = ready.Value.Evidence.ProcessId;
				_activeReadinessPath = ready.Value.Path;
				return ready;
			}
			await _platform.DelayAsync(_options.Policy.ProbeInterval, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException("rtaime engine did not reach qualified readiness before the configured startup timeout.");
	}

	private async Task<(string Path, ApplicationReadinessEvidence Evidence)> WaitForExternalReadinessAsync(CancellationToken cancellationToken)
	{
		var deadline = _platform.UtcNow + _options.Policy.StartupTimeout;
		while (_platform.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var ready = await FindHealthyReadinessAsync(cancellationToken).ConfigureAwait(false);
			if (ready is not null) return ready.Value;
			await _platform.DelayAsync(_options.Policy.ProbeInterval, cancellationToken).ConfigureAwait(false);
		}

		throw new TimeoutException("Externally managed rtaime engine did not reach qualified readiness before the configured startup timeout.");
	}

	private async Task<(string Path, ApplicationReadinessEvidence Evidence)?> FindHealthyReadinessAsync(CancellationToken cancellationToken)
	{
		foreach (var path in GetReadinessCandidates())
		{
			if (!_platform.FileExists(path)) continue;
			if (!ApplicationReadinessEvidence.TryParse(_platform.ReadAllText(path), out var evidence) || evidence is null) continue;
			if (await IsHealthyAsync(evidence, cancellationToken).ConfigureAwait(false)) return (path, evidence);
		}
		return null;
	}

	private IEnumerable<string> GetReadinessCandidates()
	{
		yield return _options.ReadinessPath;
		if (!_platform.FileExists(_options.LegacyLifecycleStatePath)) yield break;

		string? legacyReadiness = null;
		try
		{
			using var document = JsonDocument.Parse(_platform.ReadAllText(_options.LegacyLifecycleStatePath));
			if (document.RootElement.TryGetProperty("readinessPath", out var value))
				legacyReadiness = value.GetString();
		}
		catch (JsonException)
		{
		}

		if (!string.IsNullOrWhiteSpace(legacyReadiness))
			yield return Path.GetFullPath(legacyReadiness);
	}

	private async Task<bool> IsHealthyAsync(ApplicationReadinessEvidence evidence, CancellationToken cancellationToken)
	{
		var endpoints = _options.Endpoints;
		if (!_platform.IsProcessAlive(evidence.ProcessId)) return false;
		if (!string.Equals(evidence.State, "READY", StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(evidence.Health, "HEALTHY", StringComparison.OrdinalIgnoreCase)) return false;
		if (!string.Equals(evidence.ControlEndpoint, endpoints.Control, StringComparison.Ordinal) ||
			!string.Equals(evidence.RuntimeEndpoint, endpoints.Runtime, StringComparison.Ordinal) ||
			!string.Equals(evidence.AIEndpoint, endpoints.AI, StringComparison.Ordinal)) return false;
		if (evidence.RuntimeSupervision is null ||
			!string.Equals(evidence.RuntimeSupervision.State, "HEALTHY", StringComparison.OrdinalIgnoreCase)) return false;
		if (evidence.RuntimeSupervision.ProcessId is { } runtimePid && !_platform.IsProcessAlive(runtimePid)) return false;
		if (_options.RequireAI)
		{
			if (evidence.AISupervision is null ||
				!string.Equals(evidence.AISupervision.State, "HEALTHY", StringComparison.OrdinalIgnoreCase)) return false;
			if (evidence.AISupervision.ProcessId is { } aiPid && !_platform.IsProcessAlive(aiPid)) return false;
		}

		if (!await _platform.ProbePipeAsync(endpoints.Control, _options.Policy.ProbeTimeout, cancellationToken).ConfigureAwait(false)) return false;
		if (_options.Ownership == ApplicationLifecycleOwnership.ExternalManaged)
			return true;

		if (!await _platform.ProbePipeAsync(endpoints.Runtime, _options.Policy.ProbeTimeout, cancellationToken).ConfigureAwait(false)) return false;
		return !_options.RequireAI ||
			await _platform.ProbePipeAsync(endpoints.AI, _options.Policy.ProbeTimeout, cancellationToken).ConfigureAwait(false);
	}

	private async Task ObserveOperatorAsync(int operatorProcessId, CancellationToken cancellationToken)
	{
		DateTimeOffset? degradedSince = null;
		while (_platform.IsProcessAlive(operatorProcessId))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var readiness = await FindHealthyReadinessAsync(cancellationToken).ConfigureAwait(false);
			if (readiness is null)
			{
				degradedSince ??= _platform.UtcNow;
				_platform.DeleteFile(_options.ServiceReadinessEvidencePath);
				_lifecycle.DegradeStage(
					ApplicationLifecycleStages.ProductionReadiness,
					"Production readiness was lost; recovery is active.",
					"Qualified engine readiness is currently unavailable.");
				Transition(ApplicationLifecycleState.Degraded);
				if (_platform.UtcNow - degradedSince >= _options.Policy.StartupTimeout)
					throw new TimeoutException("Engine readiness did not recover within the configured recovery window.");
			}
			else
			{
				TrackReadiness(readiness.Value);
				if (degradedSince is not null)
				{
					Transition(ApplicationLifecycleState.Recovering);
					_lifecycle.StartStage(ApplicationLifecycleStages.ProductionReadiness, "Requalifying recovered engine readiness.");
					degradedSince = null;
					Transition(ApplicationLifecycleState.Healthy);
					_lifecycle.CompleteStage(ApplicationLifecycleStages.ProductionReadiness, "Production runtime recovered and is qualified.");
				}
			}
			await _platform.DelayAsync(_options.Policy.ProbeInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task ObserveHeadlessAsync(CancellationToken cancellationToken)
	{
		DateTimeOffset? degradedSince = null;
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_controlProcessId is { } controlPid && !_platform.IsProcessAlive(controlPid))
			{
				await _platform.FlushProcessDiagnosticsAsync(controlPid, cancellationToken).ConfigureAwait(false);
				throw new InvalidOperationException(BuildControlHostExitDetail("ControlHost stopped while HeadlessEngine profile was active."));
			}

			var readiness = await FindHealthyReadinessAsync(cancellationToken).ConfigureAwait(false);
			if (readiness is null)
			{
				degradedSince ??= _platform.UtcNow;
				_platform.DeleteFile(_options.ServiceReadinessEvidencePath);
				_lifecycle.DegradeStage(
					ApplicationLifecycleStages.ProductionReadiness,
					"Production readiness was lost; recovery is active.",
					"Qualified engine readiness is currently unavailable.");
				Transition(ApplicationLifecycleState.Degraded);
				if (_platform.UtcNow - degradedSince >= _options.Policy.StartupTimeout)
					throw new TimeoutException("Engine readiness did not recover within the configured recovery window.");
			}
			else
			{
				TrackReadiness(readiness.Value);
				if (degradedSince is not null)
				{
					Transition(ApplicationLifecycleState.Recovering);
					_lifecycle.StartStage(ApplicationLifecycleStages.ProductionReadiness, "Requalifying recovered engine readiness.");
					degradedSince = null;
				}
				Transition(ApplicationLifecycleState.Healthy);
				_lifecycle.CompleteStage(ApplicationLifecycleStages.ProductionReadiness, "Production runtime is qualified and ready.");
			}

			await _platform.DelayAsync(_options.Policy.ProbeInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	private string BuildControlHostExitDetail(string prefix)
	{
		if (_ownedControlProcessId is null) return prefix;
		if (!_platform.FileExists(_options.ControlHostDiagnosticPath)) return prefix;
		try
		{
			var diagnostic = _platform.ReadAllText(_options.ControlHostDiagnosticPath).Trim();
			if (diagnostic.Length == 0) return prefix;
			const int maximumDetailLength = 4096;
			if (diagnostic.Length > maximumDetailLength)
				diagnostic = diagnostic[^maximumDetailLength..];
			return $"{prefix} ControlHost diagnostic: {diagnostic}";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return prefix;
		}
	}

	private void TrackReadiness((string Path, ApplicationReadinessEvidence Evidence) readiness)
	{
		var previousControlProcessId = _controlProcessId;
		_controlProcessId = readiness.Evidence.ProcessId;
		_activeReadinessPath = readiness.Path;
		if (previousControlProcessId != _controlProcessId &&
			_ownedControlProcessId is { } ownedProcessId &&
			ownedProcessId != _controlProcessId &&
			!_platform.IsProcessAlive(ownedProcessId))
		{
			_ownedControlProcessId = null;
			_adopted = true;
		}

		PublishServiceReadinessEvidence(readiness.Evidence);
	}

	private void PublishServiceReadinessEvidence(ApplicationReadinessEvidence evidence)
	{
		if (!_options.WindowsService) return;

		var payload = JsonSerializer.Serialize(new
		{
			copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
			schemaVersion = "1.0",
			status = "PASS",
			lifecycleOwnership = ApplicationLifecycleOwnership.PersistentEngine.ToString(),
			serviceName = _options.WindowsServiceName,
			instanceId = _options.InstanceId,
			serviceProcessId = Environment.ProcessId,
			controlProcessId = evidence.ProcessId,
			runtimeProcessId = evidence.RuntimeSupervision?.ProcessId,
			aiProcessId = _options.RequireAI ? evidence.AISupervision?.ProcessId : null,
			controlEndpoint = evidence.ControlEndpoint,
			runtimeEndpoint = evidence.RuntimeEndpoint,
			aiEndpoint = evidence.AIEndpoint,
			internalPipeQualification = "PASS",
			verifiedAtUtc = _platform.UtcNow
		});
		_platform.WriteAllText(_options.ServiceReadinessEvidencePath, payload + Environment.NewLine);
	}

	private bool ShouldStopOwnedControlOnHostExit() =>
		_options.Ownership == ApplicationLifecycleOwnership.EphemeralLocal ||
		(_options.Profile == ApplicationStartupProfile.HeadlessEngine &&
			_options.Ownership == ApplicationLifecycleOwnership.PersistentEngine);

	private async Task StopOwnedControlAsync(CancellationToken cancellationToken)
	{
		if (_ownedControlProcessId is not { } processId) return;
		Transition(ApplicationLifecycleState.Stopping);
		var requestedAtUtc = _platform.UtcNow;
		var processAliveAtRequest = _platform.IsProcessAlive(processId);
		if (processAliveAtRequest)
		{
			_platform.WriteAllText(_options.StopPath, $"stopRequestedAtUtc={requestedAtUtc:O}{Environment.NewLine}");
			var deadline = _platform.UtcNow + _options.Policy.ShutdownTimeout;
			while (_platform.UtcNow < deadline && _platform.IsProcessAlive(processId))
				await _platform.DelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
		}

		var forcedTermination = processAliveAtRequest && _platform.IsProcessAlive(processId);
		if (forcedTermination) _platform.KillProcessTree(processId);
		var graceful = processAliveAtRequest && !forcedTermination;

		var evidence = JsonSerializer.Serialize(new
		{
			copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
			schemaVersion = "1.0",
			status = graceful ? "PASS" : "FAIL",
			graceful,
			forcedTermination,
			processAliveAtRequest,
			controlProcessId = processId,
			requestedAtUtc,
			completedAtUtc = _platform.UtcNow
		});
		_platform.WriteAllText(_options.ShutdownEvidencePath, evidence + Environment.NewLine);
		_platform.DeleteFile(_options.ReadinessPath);
		_platform.DeleteFile(_options.StopPath);
		_platform.DeleteFile(_options.ServiceReadinessEvidencePath);
		_ownedControlProcessId = null;
	}

	private void Transition(ApplicationLifecycleState next)
	{
		if (State == next) return;
		State = next;
		StateChanged?.Invoke(next);
	}
}
