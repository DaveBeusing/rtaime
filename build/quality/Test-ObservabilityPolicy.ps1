# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

$corePath = Join-Path $repositoryRoot "src/rtaime.Core/Diagnostics.cs"
$hostLogPath = Join-Path $repositoryRoot "src/rtaime.Core/HostLogging.cs"
$appHostProgramPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/Program.cs"
$controlProgramPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/Program.cs"
$runtimeProgramPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/Program.cs"
$aiProgramPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AIHost/Program.cs"
$operatorAppPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/App.xaml.cs"
$controlPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ControlHostDiagnostics.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostDiagnostics.cs"
$runtimeServicePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$runtimeProcessPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs"
$hardwareTelemetryPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/SystemHardwareTelemetry.cs"
$frameDropPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeFrameDropCounter.cs"
$healthProjectionPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/OperatorHealthProjection.cs"
$runtimeReadinessPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/RuntimeReadinessService.cs"
$controlIpcPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ControlHostIpcServer.cs"
$aiPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AIHost/AIHostDiagnostics.cs"
$testsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/DiagnosticsTests.cs"
$runtimeReadinessTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/RuntimeReadinessServiceTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/ObservabilityDiagnostics.md"
$runtimeReadinessDocumentationPath = Join-Path $repositoryRoot "docs/RuntimeReadiness.md"

foreach ($path in @($corePath, $hostLogPath, $appHostProgramPath, $controlProgramPath, $runtimeProgramPath, $aiProgramPath, $operatorAppPath, $controlPath, $runtimePath, $runtimeServicePath, $runtimeProcessPath, $hardwareTelemetryPath, $frameDropPath, $healthProjectionPath, $runtimeReadinessPath, $controlIpcPath, $aiPath, $testsPath, $runtimeReadinessTestsPath, $documentationPath, $runtimeReadinessDocumentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required observability artifact is missing: '$path'."
}

$core = Get-Content -LiteralPath $corePath -Raw
$hostLog = Get-Content -LiteralPath $hostLogPath -Raw
$appHostProgram = Get-Content -LiteralPath $appHostProgramPath -Raw
$controlProgram = Get-Content -LiteralPath $controlProgramPath -Raw
$runtimeProgram = Get-Content -LiteralPath $runtimeProgramPath -Raw
$aiProgram = Get-Content -LiteralPath $aiProgramPath -Raw
$operatorApp = Get-Content -LiteralPath $operatorAppPath -Raw
$control = Get-Content -LiteralPath $controlPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$runtimeService = Get-Content -LiteralPath $runtimeServicePath -Raw
$runtimeProcess = Get-Content -LiteralPath $runtimeProcessPath -Raw
$hardwareTelemetry = Get-Content -LiteralPath $hardwareTelemetryPath -Raw
$frameDrop = Get-Content -LiteralPath $frameDropPath -Raw
$healthProjection = Get-Content -LiteralPath $healthProjectionPath -Raw
$runtimeReadiness = Get-Content -LiteralPath $runtimeReadinessPath -Raw
$controlIpc = Get-Content -LiteralPath $controlIpcPath -Raw
$ai = Get-Content -LiteralPath $aiPath -Raw
$tests = Get-Content -LiteralPath $testsPath -Raw
$runtimeReadinessTests = Get-Content -LiteralPath $runtimeReadinessTestsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$runtimeReadinessDocumentation = Get-Content -LiteralPath $runtimeReadinessDocumentationPath -Raw

Assert-Condition ($core -match 'public sealed class BoundedDiagnosticBuffer') "Diagnostics must provide a bounded in-memory event buffer."
Assert-Condition ($core -match 'CurrentSchemaVersion\s*=\s*"1\.0"') "Support snapshots must have an explicit 1.0 schema marker."
Assert-Condition ($core -match 'DiagnosticRedactor') "Support diagnostics must pass through the shared redaction policy."
Assert-Condition ($core -match 'SortedDictionary<string, string>') "Support snapshot string maps must have deterministic key ordering."
Assert-Condition ($core -match 'SupportSnapshotSerializer') "Support snapshots require deterministic JSON serialization."

Assert-Condition ($hostLog -match 'public sealed class HostLog') "Diagnostics must expose the shared structured host logger."
Assert-Condition ($hostLog -match 'CurrentSchemaVersion\s*=\s*"1\.0"') "Structured host logs must carry an explicit schema version."
Assert-Condition ($hostLog -match 'DefaultMaxFileBytes\s*=\s*16L \* 1024L \* 1024L') "Structured host log rotation must default to 16 MiB."
Assert-Condition ($hostLog -match 'DefaultRetentionDays\s*=\s*14') "Structured host log retention must default to 14 days."
foreach ($environmentName in @("RTAIME_LOG_ROOT", "RTAIME_LOG_SESSION_ID", "RTAIME_LOG_LEVEL", "RTAIME_LOG_MAX_FILE_MB", "RTAIME_LOG_RETENTION_DAYS")) {
	Assert-Condition ($hostLog -match [Regex]::Escape($environmentName)) "Structured host logging is missing configuration '$environmentName'."
}
Assert-Condition ($hostLog -match 'DiagnosticRedactor\.RedactText' -and $hostLog -match 'DiagnosticRedactor\.Sanitize') "Structured host logs must use the shared redaction policy."
Assert-Condition ($hostLog -match 'UnhandledException' -and $hostLog -match 'UnobservedTaskException') "Structured host logs must capture process and unobserved-task failures."
Assert-Condition ($hostLog -match 'FileShare\.ReadWrite \| FileShare\.Delete' -and $hostLog -match 'FileOptions\.SequentialScan') "Structured host logs must remain readable while hosts are running."
Assert-Condition ($hostLog -notmatch 'PeriodicTimer|Task\.Run|new Thread') "Structured host logging must not add polling loops or background threads."

$hostEntryPoints = @{
	"AppHost" = $appHostProgram
	"ControlHost" = $controlProgram
	"RuntimeHost" = $runtimeProgram
	"AIHost" = $aiProgram
	"Operator" = $operatorApp
}
foreach ($hostName in $hostEntryPoints.Keys) {
	$entryPoint = $hostEntryPoints[$hostName]
	Assert-Condition ($entryPoint -match ('HostLog\.Open\("' + [Regex]::Escape($hostName) + '"')) "Host '$hostName' must initialize the shared structured host log."
	Assert-Condition ($entryPoint -match 'AttachProcessFailureHandlers') "Host '$hostName' must attach common process-failure logging."
}
Assert-Condition ($controlProgram -match 'controlhost\.runtime-supervision-changed' -and $controlProgram -match 'controlhost\.ai-supervision-changed') "ControlHost logging must expose managed child supervision transitions."
Assert-Condition ($runtimeProgram -match 'runtimehost\.state-changed' -and $runtimeProgram -match 'runtimehost\.ready') "RuntimeHost logging must expose lifecycle and readiness transitions."
Assert-Condition ($aiProgram -match 'aihost\.state-changed' -and $aiProgram -match 'aihost\.ready') "AIHost logging must expose lifecycle and readiness transitions."
Assert-Condition ($operatorApp -match 'operator\.dispatcher-unhandled-exception' -and $operatorApp -match 'operator\.reconnect-failure') "Operator logging must capture UI failures and reconnect transitions."

$snapshotDeclaration = [Regex]::Match($core, 'public sealed record SupportSnapshot\((?s:.*?)\);')
Assert-Condition $snapshotDeclaration.Success "SupportSnapshot declaration could not be located."
Assert-Condition ($snapshotDeclaration.Value -notmatch 'byte\[\]|ReadOnlyMemory<byte>|Memory<byte>') "SupportSnapshot must never carry bulk media byte payloads."

Assert-Condition ($control -match 'journal\.Entries\.TakeLast') "ControlHost support snapshots must reuse the bounded production journal rather than inventing a parallel authority log."
Assert-Condition ($runtime -match 'MonitoringStatistics') "RuntimeHost support snapshots must include monitoring capture/drop diagnostics."
Assert-Condition ($runtime -match 'recording\.Statistics') "RuntimeHost support snapshots must include recording counters."
Assert-Condition ($runtime -match 'snapshot\.Performance') "RuntimeHost support snapshots must reuse the live performance snapshot."
Assert-Condition ($runtime -match 'runtime\.droppedFrames') "Runtime support diagnostics must include the bounded dropped-frame counter."
Assert-Condition ($runtime -match 'gpu\.utilizationPercent' -and $runtime -match 'UNVERIFIED') "GPU utilization diagnostics must remain UNVERIFIED when no measured value exists."
Assert-Condition ($runtimeService -match 'V1RuntimePerformanceSnapshot') "RuntimeHost must expose a bounded runtime performance snapshot."
Assert-Condition ($runtimeService -match 'GpuUtilizationPercent[\s\S]*GpuVramUsedBytes') "Runtime performance telemetry must keep optional GPU utilization and VRAM fields explicit."
Assert-Condition ($runtimeService -match 'CpuUtilizationPercent[\s\S]*SystemMemoryUsedBytes[\s\S]*SystemMemoryTotalBytes') "Runtime performance telemetry must expose optional CPU and system-memory measurements explicitly."
Assert-Condition ($runtimeService -match 'OutputFramesPerSecond') "Runtime performance telemetry must expose measured Program output cadence explicitly."
Assert-Condition ($runtimeProcess -match '_frameDropCounter\.OutputFramesPerSecond' -and $runtimeProcess -match 'SetPerformanceObservations') "RuntimeHost must feed output cadence from existing Program-boundary timing observations into the performance snapshot."
Assert-Condition ($frameDrop -match 'OutputRateSmoothingFactor\s*=\s*0\.2' -and $frameDrop -match 'OutputFramesPerSecond') "Program output cadence must use bounded constant-space smoothing."
Assert-Condition ($runtime -match 'performance\.outputFramesPerSecond') "Runtime support diagnostics must expose measured output cadence when available."
Assert-Condition ($hardwareTelemetry -match 'SampleInterval\s*=\s*TimeSpan\.FromMilliseconds\(500\)') "Hardware telemetry sampling must remain bounded and cached."
Assert-Condition ($hardwareTelemetry -match 'SampleRetentionInterval\s*=\s*TimeSpan\.FromSeconds\(3\)') "Transient hardware read misses must retain recent qualified samples for a bounded three-second window."
Assert-Condition ($hardwareTelemetry -notmatch 'PeriodicTimer|Task\.Run|new Thread') "Hardware telemetry must remain snapshot-driven and must not create an independent polling loop."
Assert-Condition ($controlIpc -match 'RuntimeObservationRetention\s*=\s*TimeSpan\.FromSeconds\(2\)' -and $controlIpc -match 'RuntimeObservation\(_lastRuntimeSnapshot, false, _lastRuntimeSnapshotAtUtc\)') "ControlHost must retain recent Runtime observations briefly instead of clearing operator performance state on one IPC miss."
Assert-Condition ($healthProjection -match 'runtimeObservationFresh' -and $healthProjection -match 'recent retained Runtime observation') "Retained Runtime telemetry must remain explicitly non-fresh in health projection."
Assert-Condition ($runtimeService -match 'var hardware = _hardwareTelemetry\.Sample\(\);\s*lock \(_gate\)') "Hardware probes must run before the Runtime state lock so management telemetry cannot block Program processing through the shared gate."
Assert-Condition ($hardwareTelemetry -match 'GetSystemTimes' -and $hardwareTelemetry -match 'GlobalMemoryStatusEx') "Windows CPU and system-memory telemetry must use bounded OS measurements."
Assert-Condition ($hardwareTelemetry -match 'nvmlDeviceGetUtilizationRates' -and $hardwareTelemetry -match 'nvmlDeviceGetMemoryInfo') "NVIDIA telemetry must use driver-provided NVML measurements."
Assert-Condition ($frameDrop -match 'class RuntimeFrameDropCounter') "Runtime diagnostics must retain a dedicated O(1) dropped-frame counter."
Assert-Condition ($frameDrop -notmatch 'List<|Queue<|Dictionary<|File\.|Stream') "Dropped-frame observation must not allocate history or perform I/O on the Runtime hot path."
Assert-Condition ($frameDrop -notmatch 'Stopwatch|DateTime|DateTimeOffset|PeriodicTimer|Task\.Run|new Thread') "Output cadence observation must reuse supplied scheduler-boundary timestamps and create no independent timing or polling source."
Assert-Condition ($healthProjection -match 'Pass[\s\S]*Fail[\s\S]*Unverified') "Runtime health projection must preserve PASS/FAIL/UNVERIFIED semantics."
Assert-Condition ($healthProjection -match 'GpuUtilizationPercent is') "Runtime health projection must expose GPU utilization only when a measured value exists."
Assert-Condition ($healthProjection -match 'CpuUtilizationPercent is' -and $healthProjection -match 'SystemMemoryUsedBytes is') "Runtime health projection must expose CPU and system-memory values only from measured evidence."
Assert-Condition ($runtimeReadiness -match 'public interface IRuntimeReadinessService' -and $runtimeReadiness -match 'RuntimeReadinessSnapshot Current' -and $runtimeReadiness -match 'RuntimeReadinessChangedEventArgs') "Global Runtime readiness must expose one observable Current snapshot source."
foreach ($state in @("Initializing", "Ready", "Degraded", "NotReady", "Recovering", "Failed")) {
	Assert-Condition ($runtimeReadiness -match [Regex]::Escape($state)) "Global Runtime readiness is missing state '$state'."
}
Assert-Condition ($runtimeReadiness -match 'DefaultPerformanceValidity\s*=\s*TimeSpan\.FromSeconds\(2\)') "Performance verification validity must remain aligned with the two-second retained Runtime observation window."
foreach ($reason in @("HardwareChanged", "PipelineChanged", "MeasurementExpired", "RuntimeFault", "Explicit")) {
	Assert-Condition ($runtimeReadiness -match [Regex]::Escape($reason)) "Performance verification is missing invalidation reason '$reason'."
}
Assert-Condition ($runtimeReadiness -match 'lock \(_gate\)' -and $runtimeReadiness -match 'Changed\?\.Invoke') "Global Runtime readiness transitions must be serialized and observable."
Assert-Condition ($runtimeReadiness -notmatch 'PeriodicTimer|DispatcherTimer|Task\.Run|new Thread') "Global Runtime readiness must reuse existing observations and must not create a polling loop or background thread."
Assert-Condition ($runtimeReadiness -match 'RuntimePerformanceVerificationState\.Verified' -and $runtimeReadiness -match 'retainedUntil') "A still-valid verified performance result must survive transient retained Runtime observations."
Assert-Condition ($runtimeReadinessTests -match 'Global_states_cover_initializing_ready_degraded_not_ready_recovering_and_failed') "Runtime readiness must test all global states."
Assert-Condition ($runtimeReadinessTests -match 'Multiple_degradation_reasons_are_retained_together') "Runtime readiness must test simultaneous degradation reasons."
Assert-Condition ($runtimeReadinessTests -match 'Recovered_required_subsystem_returns_automatically_to_ready') "Runtime readiness must test automatic recovery to Ready."
Assert-Condition ($runtimeReadinessTests -match 'Verified_performance_survives_transient_retained_runtime_observation') "Runtime readiness must test persistent performance verification."
Assert-Condition ($runtimeReadinessTests -match 'Explicit_invalidation_requires_a_newer_measurement_before_reverification') "Runtime readiness must test explicit invalidation."
Assert-Condition ($runtimeReadinessTests -match 'Rebinding_subscribers_does_not_reset_current_verification') "Runtime readiness must test UI subscriber rebinding without state loss."
Assert-Condition ($runtimeReadinessTests -match 'Parallel_state_observations_are_thread_safe') "Runtime readiness must test concurrent state events."
Assert-Condition ($runtimeReadinessTests -match 'Disposed_service_rejects_late_state_updates') "Runtime readiness must test disposal behavior."
Assert-Condition ($runtimeReadinessDocumentation -match 'one persistent application-level view' -and $runtimeReadinessDocumentation -match 'No independent polling loop') "Runtime readiness documentation must describe the single persistent state source and polling boundary."
Assert-Condition ($ai -match 'ReservedVramBytes') "AIHost support snapshots must include governed resource admission state."
Assert-Condition ($ai -match 'providerCount') "AIHost support snapshots must include provider inventory counts."

foreach ($hostProjection in @($control, $runtime, $ai)) {
	Assert-Condition ($hostProjection -notmatch 'File\.(Write|Append)|WriteAll(Bytes|Text)|FileStream') "Host snapshot projection must not perform synchronous disk writes."
}

Assert-Condition ($tests -match 'Bounded_buffer_retains_only_the_newest_events') "Diagnostics boundedness regression coverage is required."
Assert-Condition ($tests -match 'Redaction_removes_secret_dimensions') "Diagnostics secret-redaction regression coverage is required."
Assert-Condition ($tests -match 'Support_snapshot_serialization_is_deterministic') "Deterministic support serialization regression coverage is required."
Assert-Condition ($tests -match 'Host_log_writes_structured_redacted_json_lines') "Structured host log serialization/redaction regression coverage is required."
Assert-Condition ($tests -match 'Host_log_keeps_process_failures_best_effort') "Structured host process-failure regression coverage is required."
Assert-Condition ($tests -match 'Host_log_file_failure_does_not_fail_the_calling_host') "Structured host log storage failures must be regression-tested as non-fatal."
Assert-Condition ($documentation -match 'No per-frame disk write') "Observability documentation must explicitly prohibit per-frame diagnostic disk writes."
Assert-Condition ($documentation -match 'raw video/audio payloads') "Observability documentation must explicitly prohibit bulk media in support snapshots."
Assert-Condition ($documentation -match 'Runtime Health & Performance HUD') "Observability documentation must record the runtime health/performance projection."
Assert-Condition ($documentation -match 'performance\.outputFramesPerSecond' -and $documentation -match 'exponentially smoothed Output-FPS scalar') "Observability documentation must record the measured output-cadence path and constant-space smoothing boundary."
Assert-Condition ($documentation -match 'UNVERIFIED') "Observability documentation must explicitly preserve UNVERIFIED evidence for unavailable GPU telemetry."
Assert-Condition ($documentation -match '## Structured host logs' -and $documentation -match 'RTAIME_LOG_SESSION_ID' -and $documentation -match '16 MiB' -and $documentation -match '14 days') "Observability documentation must describe host-log correlation, rotation and retention."
Assert-Condition ($documentation -match 'AppHost' -and $documentation -match 'ControlHost' -and $documentation -match 'RuntimeHost' -and $documentation -match 'AIHost' -and $documentation -match 'Operator') "Observability documentation must cover every executable host."

Write-Host "Observability diagnostics policy verification PASS"
Write-Host "Support snapshot schema: 1.0"
Write-Host "Bounded diagnostics: required"
Write-Host "Structured host logs: AppHost, ControlHost, RuntimeHost, AIHost, Operator"
Write-Host "Secrets and bulk media: excluded"
Write-Host "Runtime health telemetry: bounded frame metrics and explicit GPU UNVERIFIED evidence"
