# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$policyPath = Join-Path $PSScriptRoot 'host-lifecycle-policy.json'
$controllerPath = Join-Path $PSScriptRoot 'Invoke-ManagedHostLifecycle.ps1'
$qualificationPath = Join-Path $PSScriptRoot 'Test-ManagedHostLifecycle.ps1'
$controlProgramPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.ControlHost/Program.cs'
$runtimeProgramPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.RuntimeHost/Program.cs'
$aiProgramPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.AIHost/Program.cs'
$supervisorPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.ControlHost/LocalProcessSupervisor.cs'
$bundlePolicyPath = Join-Path $repositoryRoot 'build/release/offline-bundle-policy.json'
$workflowPath = Join-Path $repositoryRoot '.github/workflows/required-gates.yml'

foreach ($path in @($policyPath, $controllerPath, $qualificationPath, $controlProgramPath, $runtimeProgramPath, $aiProgramPath, $supervisorPath, $bundlePolicyPath, $workflowPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required managed-host lifecycle file is missing: '$path'."
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq '1.0') "Unsupported host-lifecycle policy schema version."
Assert-Condition ([string]$policy.platform.osFamily -eq 'Windows') "AP-26 lifecycle policy must remain on the V1 Windows reference platform."
Assert-Condition ([string]$policy.platform.architecture -eq 'x64') "AP-26 lifecycle policy must remain x64."
Assert-Condition ([string]$policy.authority.topLevelHost -eq 'ControlHost') "ControlHost must remain the top-level managed service host."
Assert-Condition ([string]$policy.authority.childSupervisionOwner -eq 'ControlHost') "ControlHost must own local RuntimeHost/AIHost child supervision."
Assert-Condition (@($policy.authority.managedChildren).Count -eq 2) "Exactly RuntimeHost and AIHost must be managed children."
Assert-Condition (@($policy.authority.managedChildren) -contains 'RuntimeHost') "RuntimeHost managed child is missing."
Assert-Condition (@($policy.authority.managedChildren) -contains 'AIHost') "AIHost managed child is missing."
Assert-Condition ($policy.authority.operatorClientManaged -eq $false) "Operator must remain an interactive client, not a background managed service host."
Assert-Condition ($policy.startup.requireControlReadyHealthy -eq $true) "ControlHost Ready/Healthy is mandatory."
Assert-Condition ($policy.startup.requireRuntimeSupervisorHealthy -eq $true) "RuntimeHost supervisor health is mandatory."
Assert-Condition ($policy.startup.requireAiSupervisorHealthy -eq $true) "AIHost supervisor health is mandatory."
Assert-Condition ($policy.startup.requireAllNamedPipesReachable -eq $true) "All service-host Named Pipe endpoints must be externally reachable."
Assert-Condition ([string]$policy.shutdown.managedStopSignal -eq 'FILE_SENTINEL') "Managed stop must use the explicit file sentinel."
Assert-Condition ($policy.shutdown.fallbackKillAllowed -eq $true) "Emergency process-tree termination must remain available for cleanup."
Assert-Condition ($policy.shutdown.fallbackKillIsPass -eq $false) "Forced termination must never be reported as PASS."
Assert-Condition ([string]$policy.restart.mode -eq 'EXPLICIT_OPERATOR_ACTION') "Restart must remain an explicit operator action."
Assert-Condition ($policy.restart.requireSuccessfulStopBeforeStart -eq $true) "Restart must require a successful stop before start."
Assert-Condition ($policy.readiness.passRequiresAllServiceHosts -eq $true) "Readiness PASS must require all three service hosts."
Assert-Condition ([string]$policy.readiness.runtimeReadinessOnPass -eq 'PASS') "Successful host qualification must emit runtimeReadiness PASS."
Assert-Condition ($policy.readiness.automaticWindowsServiceRegistration -eq $false) "AP-26 must not register Windows services automatically."

$controller = Get-Content -LiteralPath $controllerPath -Raw
foreach ($token in @('Start', 'Stop', 'Restart', 'Status', 'Test-NamedPipeEndpoint', 'RTAIME_RUNTIME_EXECUTABLE', 'RTAIME_AI_EXECUTABLE', 'runtimeReadiness = ''PASS''', 'fallbackKillIsPass')) {
	Assert-Condition ($controller -match [Regex]::Escape($token)) "Managed lifecycle controller is missing required marker '$token'."
}

$controlProgram = Get-Content -LiteralPath $controlProgramPath -Raw
foreach ($token in @('RTAIME_HOST_READINESS_FILE', 'RTAIME_HOST_STOP_FILE', 'runtimeSupervision', 'aiSupervision', 'ControlHostProcessState.Ready')) {
	Assert-Condition ($controlProgram -match [Regex]::Escape($token)) "ControlHost managed lifecycle bridge is missing '$token'."
}
foreach ($hostProgramPath in @($runtimeProgramPath, $aiProgramPath)) {
	$hostProgram = Get-Content -LiteralPath $hostProgramPath -Raw
	Assert-Condition ($hostProgram -match 'RTAIME_HOST_STOP_FILE') "Managed child host does not observe the graceful stop sentinel: '$hostProgramPath'."
	Assert-Condition ($hostProgram -match 'shutdown\.Cancel\(\)') "Managed child host does not convert stop sentinel into graceful cancellation: '$hostProgramPath'."
}

$supervisor = Get-Content -LiteralPath $supervisorPath -Raw
foreach ($token in @('GracefulStopTimeout', 'RTAIME_HOST_STOP_FILE', 'WaitForExitAsync', 'Kill(entireProcessTree: true)')) {
	Assert-Condition ($supervisor -match [Regex]::Escape($token)) "LocalProcessSupervisor is missing managed shutdown behavior '$token'."
}

$bundlePolicy = Get-Content -LiteralPath $bundlePolicyPath -Raw | ConvertFrom-Json
foreach ($tool in @('build/operations/host-lifecycle-policy.json', 'build/operations/Invoke-ManagedHostLifecycle.ps1')) {
	Assert-Condition (@($bundlePolicy.offlineTools) -contains $tool) "Offline release bundle does not carry required lifecycle tool '$tool'."
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-Condition ($workflow -match 'Test-HostLifecyclePolicy\.ps1') "Quality gate must validate managed host lifecycle policy."
Assert-Condition ($workflow -match 'Test-ManagedHostLifecycle\.ps1') "Packaged E2E must execute managed host lifecycle qualification."

Write-Host 'Managed host lifecycle policy PASS'
Write-Host 'Top-level host: ControlHost'
Write-Host 'Managed children: RuntimeHost, AIHost'
Write-Host 'Readiness: lifecycle + supervision + Named Pipes'
Write-Host 'Restart: explicit operator action'
Write-Host 'Forced shutdown: FAIL'
Write-Host 'Automatic Windows service registration: disabled'
