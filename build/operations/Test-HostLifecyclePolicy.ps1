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
$recoveryTestsPath = Join-Path $repositoryRoot 'tests/rtaime.Tests.Integration/ProcessSupervisionRecoveryTests.cs'

foreach ($path in @($policyPath, $controllerPath, $qualificationPath, $controlProgramPath, $runtimeProgramPath, $aiProgramPath, $supervisorPath, $bundlePolicyPath, $workflowPath, $recoveryTestsPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required managed-host lifecycle file is missing: '$path'."
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
Assert-Condition ([string]$policy.schemaVersion -eq '1.0') "Unsupported host-lifecycle policy schema version."
Assert-Condition ([string]$policy.platform.osFamily -eq 'Windows') "Managed host lifecycle policy must remain on the V1 Windows reference platform."
Assert-Condition ([string]$policy.platform.architecture -eq 'x64') "Managed host lifecycle policy must remain x64."
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
Assert-Condition ($policy.readiness.automaticWindowsServiceRegistration -eq $false) "Managed host lifecycle must not register Windows services automatically."

$controller = Get-Content -LiteralPath $controllerPath -Raw
foreach ($token in @('Start', 'Stop', 'Restart', 'Status', 'Test-NamedPipeEndpoint', 'RTAIME_RUNTIME_EXECUTABLE', 'RTAIME_AI_EXECUTABLE', 'runtimeReadiness = ''PASS''', 'fallbackKillIsPass')) {
	Assert-Condition ($controller -match [Regex]::Escape($token)) "Managed lifecycle controller is missing required marker '$token'."
}

$controlProgram = Get-Content -LiteralPath $controlProgramPath -Raw
foreach ($token in @('RTAIME_HOST_READINESS_FILE', 'RTAIME_HOST_STOP_FILE', 'runtimeSupervision', 'aiSupervision', 'ControlHostProcessState.Ready')) {
	Assert-Condition ($controlProgram -match [Regex]::Escape($token)) "ControlHost managed lifecycle bridge is missing '$token'."
}
Assert-Condition ($controlProgram -match 'runtime\.State\s*==\s*LocalProcessSupervisionState\.Healthy' -and $controlProgram -match 'ai\.State\s*==\s*LocalProcessSupervisionState\.Healthy') "ControlHost readiness must remain fail-closed while either required child supervisor is non-Healthy."
foreach ($hostProgramPath in @($runtimeProgramPath, $aiProgramPath)) {
	$hostProgram = Get-Content -LiteralPath $hostProgramPath -Raw
	Assert-Condition ($hostProgram -match 'RTAIME_HOST_STOP_FILE') "Managed child host does not observe the graceful stop sentinel: '$hostProgramPath'."
	Assert-Condition ($hostProgram -match 'shutdown\.Cancel\(\)') "Managed child host does not convert stop sentinel into graceful cancellation: '$hostProgramPath'."
}

$supervisor = Get-Content -LiteralPath $supervisorPath -Raw
foreach ($token in @('GracefulStopTimeout', 'RTAIME_HOST_STOP_FILE', 'WaitForExitAsync', 'Kill(entireProcessTree: true)')) {
	Assert-Condition ($supervisor -match [Regex]::Escape($token)) "LocalProcessSupervisor is missing managed shutdown behavior '$token'."
}
foreach ($token in @('ReadinessGrace', 'Recovering', '_ownedReadinessLostSince', 'RecoveryGracePeriod', 'RecoverUnreadyOwnedProcessAsync')) {
	Assert-Condition ($supervisor -match [Regex]::Escape($token)) "LocalProcessSupervisor is missing owned post-healthy readiness recovery behavior '$token'."
}
Assert-Condition ($supervisor -match '_options\.RestartBackoff\s*>=\s*_options\.ProbeInterval') "Owned readiness recovery grace must reuse bounded existing supervision timing rather than add an independent unbounded timer."
Assert-Condition ($supervisor -match '_startAttempts\s*>=\s*_options\.MaxStartAttempts') "Owned readiness recovery must remain constrained by the lifetime start-attempt budget."

$recoveryTests = Get-Content -LiteralPath $recoveryTestsPath -Raw
foreach ($test in @(
	'Owned_child_persistent_readiness_loss_triggers_graceful_bounded_restart',
	'Owned_child_transient_readiness_loss_recovers_without_restart',
	'Adopted_external_readiness_loss_is_non_destructive',
	'Repeated_post_healthy_readiness_loss_stops_at_existing_start_budget'
)) {
	Assert-Condition ($recoveryTests -match [Regex]::Escape($test)) "Process supervision recovery coverage is missing '$test'."
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
Write-Host 'Owned child readiness loss: bounded grace + graceful recovery + bounded restart'
Write-Host 'Adopted child readiness loss: non-destructive observation only'
Write-Host 'Restart: explicit top-level operator action; owned child recovery remains supervisor-bounded'
Write-Host 'Forced shutdown: FAIL'
Write-Host 'Automatic Windows service registration: disabled'
