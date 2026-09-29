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
$controlRuntimeIpcPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/RuntimeHostIpcTransport.cs"
$gpuProviderPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs"
$clientPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/NamedPipeOperatorMonitoringTransport.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorMonitoringViewModel.cs"
$documentationPath = Join-Path $repositoryRoot "docs/OperatorMonitoringPlane.md"

foreach ($path in @($contractsPath, $runtimeMonitoringPath, $runtimeServicePath, $runtimeIpcPath, $controlRuntimeIpcPath, $gpuProviderPath, $clientPath, $operatorPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required monitoring-plane artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$runtimeMonitoring = Get-Content -LiteralPath $runtimeMonitoringPath -Raw
$runtimeService = Get-Content -LiteralPath $runtimeServicePath -Raw
$runtimeIpc = Get-Content -LiteralPath $runtimeIpcPath -Raw
$controlRuntimeIpc = Get-Content -LiteralPath $controlRuntimeIpcPath -Raw
$gpuProvider = Get-Content -LiteralPath $gpuProviderPath -Raw
$client = Get-Content -LiteralPath $clientPath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw

Assert-Condition ($contracts -match 'MonitoringContractVersion') "Monitoring must expose an explicit versioned contract."
Assert-Condition ($contracts -match 'MonitoringStreamKind') "Monitoring must distinguish source and Program streams."
Assert-Condition ($contracts -match 'MonitoringFrameWire') "Monitoring must use an explicit bounded wire frame rather than management JSON envelopes."
Assert-Condition ($contracts -match 'MonitoringSharedResourceDescriptor') "Monitoring must expose a provider-neutral shared GPU resource descriptor."
Assert-Condition ($contracts -match 'MonitoringResourceAccessMode' -and $contracts -match 'ReadOnly') "Shared monitoring resources must be explicitly read-only."
Assert-Condition ($contracts -notmatch 'CUDA|DevicePointer|nvcuda|cuMem|CUdevice') "Stable monitoring contracts must not expose CUDA or raw device-pointer terminology."
Assert-Condition ($contracts -notmatch 'OpaqueSurfaceHandle') "Monitoring wire contracts must publish rtaime-owned resource identity rather than provider-internal surface handles."

Assert-Condition ($runtimeMonitoring -match 'SampleStride\s*=\s*4') "V1 monitoring must remain deliberately sampled below the production frame rate."
Assert-Condition ($runtimeMonitoring -match 'MonitorWidth\s*=\s*320' -and $runtimeMonitoring -match 'MonitorHeight\s*=\s*180') "V1 monitoring must use the qualified 320x180 visual payload."
Assert-Condition ($runtimeMonitoring -match '_pending\s*=\s*sample') "Runtime monitoring must use a single pending sample that can be replaced under load."
Assert-Condition ($runtimeMonitoring -match '_dropped\+\+') "Runtime monitoring must account for dropped monitor frames under pressure."
Assert-Condition ($runtimeMonitoring -match 'capacity:\s*4') "Monitoring subscribers must retain one complete bounded source/source/Program observation set."
Assert-Condition ($runtimeMonitoring -match 'PipeDirection\.Out') "RuntimeHost monitoring must be output-only and incapable of carrying authority mutations."
Assert-Condition ($runtimeMonitoring -match 'RuntimeHostMonitoringServer') "RuntimeHost must expose a dedicated monitoring server."
Assert-Condition ($runtimeMonitoring -match 'RequiresCpuFallback') "Monitoring subscriptions must explicitly negotiate whether the CPU fallback payload is required."
Assert-Condition ($runtimeMonitoring -match 'SharedResourceCapability') "Monitoring observations must distinguish shared-resource capability from current-frame resource availability."
Assert-Condition ($runtimeMonitoring -match 'SubscriberAvailabilityChanged') "Shared monitoring resources must react to subscriber disconnect without blocking RuntimeHost."

Assert-Condition ($runtimeService -match '_monitoringTap\.TryCapture') "Committed Runtime frames must feed the monitoring tap."
Assert-Condition ($runtimeService -match '_gpu\.RentReadback\(output\)') "Program monitoring must derive from the actual post-composite Program output through reusable owned readback memory."
Assert-Condition ($runtimeMonitoring -match 'GpuReadbackLease program' -and $runtimeMonitoring -match 'program\.Retain\(\)') "Asynchronous Program monitoring must retain explicit readback ownership before the Runtime boundary can release it."
Assert-Condition ($runtimeIpc -notmatch 'MonitoringFrameWire|MonitoringFrameDescriptor|monitor\.frame') "Management Runtime IPC must not carry monitoring pixel payloads."

Assert-Condition ($client -match 'NamedPipeOperatorMonitoringTransport') "Client SDK must expose the independent monitoring transport."
Assert-Condition ($client -notmatch 'SelectPreviewAsync|CutProgramAsync|DissolveProgramAsync') "Monitoring transport must not expose production mutations."
Assert-Condition ($operator -match 'MonitoringStreamKind\.Program') "Operator must render actual Program stream frames distinctly."
Assert-Condition ($operator -match 'PreviewSourceId') "Operator Preview must select monitoring frames using authoritative control routing."
Assert-Condition ($operator -match 'SharedGpuMonitoringState') "Operator must expose the negotiated shared GPU monitoring state without requiring Direct3D presentation in this foundation."

Write-Host "Operator monitoring plane policy verification PASS"
Write-Host "Authority: none; output-only monitoring transport"
Write-Host "Backpressure: bounded and loss-tolerant"
Write-Host "Program continuity: independent from monitoring consumers"
