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
$controlPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ControlHostDiagnostics.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostDiagnostics.cs"
$runtimeServicePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$frameDropPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeFrameDropCounter.cs"
$healthProjectionPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/OperatorHealthProjection.cs"
$aiPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AIHost/AIHostDiagnostics.cs"
$testsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/DiagnosticsTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/ObservabilityDiagnostics.md"

foreach ($path in @($corePath, $controlPath, $runtimePath, $runtimeServicePath, $frameDropPath, $healthProjectionPath, $aiPath, $testsPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required observability artifact is missing: '$path'."
}

$core = Get-Content -LiteralPath $corePath -Raw
$control = Get-Content -LiteralPath $controlPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$runtimeService = Get-Content -LiteralPath $runtimeServicePath -Raw
$frameDrop = Get-Content -LiteralPath $frameDropPath -Raw
$healthProjection = Get-Content -LiteralPath $healthProjectionPath -Raw
$ai = Get-Content -LiteralPath $aiPath -Raw
$tests = Get-Content -LiteralPath $testsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($core -match 'public sealed class BoundedDiagnosticBuffer') "Diagnostics must provide a bounded in-memory event buffer."
Assert-Condition ($core -match 'CurrentSchemaVersion\s*=\s*"1\.0"') "Support snapshots must have an explicit 1.0 schema marker."
Assert-Condition ($core -match 'DiagnosticRedactor') "Support diagnostics must pass through the shared redaction policy."
Assert-Condition ($core -match 'SortedDictionary<string, string>') "Support snapshot string maps must have deterministic key ordering."
Assert-Condition ($core -match 'SupportSnapshotSerializer') "Support snapshots require deterministic JSON serialization."

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
Assert-Condition ($frameDrop -match 'class RuntimeFrameDropCounter') "Runtime diagnostics must retain a dedicated O(1) dropped-frame counter."
Assert-Condition ($frameDrop -notmatch 'List<|Queue<|Dictionary<|File\.|Stream') "Dropped-frame observation must not allocate history or perform I/O on the Runtime hot path."
Assert-Condition ($healthProjection -match 'Pass[\s\S]*Fail[\s\S]*Unverified') "Runtime health projection must preserve PASS/FAIL/UNVERIFIED semantics."
Assert-Condition ($healthProjection -match 'GpuUtilizationPercent is') "Runtime health projection must expose GPU utilization only when a measured value exists."
Assert-Condition ($ai -match 'ReservedVramBytes') "AIHost support snapshots must include governed resource admission state."
Assert-Condition ($ai -match 'providerCount') "AIHost support snapshots must include provider inventory counts."

foreach ($hostProjection in @($control, $runtime, $ai)) {
	Assert-Condition ($hostProjection -notmatch 'File\.(Write|Append)|WriteAll(Bytes|Text)|FileStream') "Host snapshot projection must not perform synchronous disk writes."
}

Assert-Condition ($tests -match 'Bounded_buffer_retains_only_the_newest_events') "Diagnostics boundedness regression coverage is required."
Assert-Condition ($tests -match 'Redaction_removes_secret_dimensions') "Diagnostics secret-redaction regression coverage is required."
Assert-Condition ($tests -match 'Support_snapshot_serialization_is_deterministic') "Deterministic support serialization regression coverage is required."
Assert-Condition ($documentation -match 'No per-frame disk write') "Observability documentation must explicitly prohibit per-frame diagnostic disk writes."
Assert-Condition ($documentation -match 'raw video/audio payloads') "Observability documentation must explicitly prohibit bulk media in support snapshots."
Assert-Condition ($documentation -match 'Runtime Health & Performance HUD') "Observability documentation must record the runtime health/performance projection."
Assert-Condition ($documentation -match 'UNVERIFIED') "Observability documentation must explicitly preserve UNVERIFIED evidence for unavailable GPU telemetry."

Write-Host "Observability diagnostics policy verification PASS"
Write-Host "Support snapshot schema: 1.0"
Write-Host "Bounded diagnostics: required"
Write-Host "Secrets and bulk media: excluded"
Write-Host "Runtime health telemetry: bounded frame metrics and explicit GPU UNVERIFIED evidence"
