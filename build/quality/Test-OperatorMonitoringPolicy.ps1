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

$contractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Media.Contracts/MonitoringContracts.cs"
$runtimeMonitoringPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeMonitoring.cs"
$runtimeServicePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$runtimeIpcPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostIpcServer.cs"
$clientPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/NamedPipeOperatorMonitoringTransport.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorMonitoringViewModel.cs"
$documentationPath = Join-Path $repositoryRoot "docs/OperatorMonitoringPlane.md"

foreach ($path in @($contractsPath, $runtimeMonitoringPath, $runtimeServicePath, $runtimeIpcPath, $clientPath, $operatorPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required monitoring-plane artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$runtimeMonitoring = Get-Content -LiteralPath $runtimeMonitoringPath -Raw
$runtimeService = Get-Content -LiteralPath $runtimeServicePath -Raw
$runtimeIpc = Get-Content -LiteralPath $runtimeIpcPath -Raw
$client = Get-Content -LiteralPath $clientPath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw

Assert-Condition ($contracts -match 'MonitoringContractVersion') "Monitoring must expose an explicit versioned contract."
Assert-Condition ($contracts -match 'MonitoringStreamKind') "Monitoring must distinguish source and Program streams."
Assert-Condition ($contracts -match 'MonitoringFrameWire') "Monitoring must use an explicit bounded wire frame rather than management JSON envelopes."

Assert-Condition ($runtimeMonitoring -match 'SampleStride\s*=\s*4') "V1 monitoring must remain deliberately sampled below the production frame rate."
Assert-Condition ($runtimeMonitoring -match 'MonitorWidth\s*=\s*320' -and $runtimeMonitoring -match 'MonitorHeight\s*=\s*180') "V1 monitoring must use the qualified 320x180 visual payload."
Assert-Condition ($runtimeMonitoring -match '_pending\s*=\s*sample') "Runtime monitoring must use a single pending sample that can be replaced under load."
Assert-Condition ($runtimeMonitoring -match '_dropped\+\+') "Runtime monitoring must account for dropped monitor frames under pressure."
Assert-Condition ($runtimeMonitoring -match 'capacity:\s*2') "Monitoring subscribers must be bounded."
Assert-Condition ($runtimeMonitoring -match 'PipeDirection\.Out') "RuntimeHost monitoring must be output-only and incapable of carrying authority mutations."
Assert-Condition ($runtimeMonitoring -match 'RuntimeHostMonitoringServer') "RuntimeHost must expose a dedicated monitoring server."

Assert-Condition ($runtimeService -match '_monitoringTap\.TryCapture') "Committed Runtime frames must feed the monitoring tap."
Assert-Condition ($runtimeService -match '_gpu\.Readback\(output\)') "Program monitoring must derive from the actual post-composite Program output."
Assert-Condition ($runtimeIpc -notmatch 'MonitoringFrameWire|MonitoringFrameDescriptor|monitor\.frame') "Management Runtime IPC must not carry monitoring pixel payloads."

Assert-Condition ($client -match 'NamedPipeOperatorMonitoringTransport') "Client SDK must expose the independent monitoring transport."
Assert-Condition ($client -notmatch 'SelectPreviewAsync|CutProgramAsync|DissolveProgramAsync') "Monitoring transport must not expose production mutations."
Assert-Condition ($operator -match 'MonitoringStreamKind\.Program') "Operator must render actual Program stream frames distinctly."
Assert-Condition ($operator -match 'PreviewSourceId') "Operator Preview must select monitoring frames using authoritative control routing."

Write-Host "Operator monitoring plane policy verification PASS"
Write-Host "Authority: none; output-only monitoring transport"
Write-Host "Backpressure: bounded and loss-tolerant"
Write-Host "Program continuity: independent from monitoring consumers"
