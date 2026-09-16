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
$aiPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AIHost/AIHostDiagnostics.cs"
$testsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/DiagnosticsTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/ObservabilityDiagnostics.md"

foreach ($path in @($corePath, $controlPath, $runtimePath, $aiPath, $testsPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required observability artifact is missing: '$path'."
}

$core = Get-Content -LiteralPath $corePath -Raw
$control = Get-Content -LiteralPath $controlPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
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

Write-Host "Observability diagnostics policy verification PASS"
Write-Host "Support snapshot schema: 1.0"
Write-Host "Bounded diagnostics: required"
Write-Host "Secrets and bulk media: excluded"
