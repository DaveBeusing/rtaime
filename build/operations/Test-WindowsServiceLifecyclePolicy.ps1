# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

$applicationCodePath = Join-Path $repositoryRoot 'src/Hosts/rtaime.AppHost/ApplicationHost.cs'
$programPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.AppHost/Program.cs'
$projectPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.AppHost/rtaime.AppHost.csproj'
$serviceLifecyclePath = Join-Path $repositoryRoot 'build/operations/Invoke-WindowsServiceLifecycle.ps1'
$serviceUpdatePath = Join-Path $repositoryRoot 'build/update/Invoke-ServiceManagedUpdate.ps1'
$serviceRollbackPath = Join-Path $repositoryRoot 'build/update/Invoke-ServiceManagedRollback.ps1'
$bundlePolicyPath = Join-Path $repositoryRoot 'build/release/offline-bundle-policy.json'
$documentationPath = Join-Path $repositoryRoot 'docs/WindowsProductionLifecycle.md'
$controlPipeFactoryPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.ControlHost/OperatorPipeServerFactory.cs'
$runtimePipeFactoryPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.RuntimeHost/OperatorPipeServerFactory.cs'
$operatorControlTransportPath = Join-Path $repositoryRoot 'src/Client/rtaime.Client/NamedPipeOperatorControlTransport.cs'
$operatorMonitoringTransportPath = Join-Path $repositoryRoot 'src/Client/rtaime.Client/NamedPipeOperatorMonitoringTransport.cs'
$runtimeIpcServerPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.RuntimeHost/RuntimeHostIpcServer.cs'
$aiIpcServerPath = Join-Path $repositoryRoot 'src/Hosts/rtaime.AIHost/AIHostIpcServer.cs'

foreach ($path in @(
	$applicationCodePath,
	$programPath,
	$projectPath,
	$serviceLifecyclePath,
	$serviceUpdatePath,
	$serviceRollbackPath,
	$bundlePolicyPath,
	$documentationPath,
	$controlPipeFactoryPath,
	$runtimePipeFactoryPath,
	$operatorControlTransportPath,
	$operatorMonitoringTransportPath,
	$runtimeIpcServerPath,
	$aiIpcServerPath
)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required Windows production lifecycle artifact is missing: '$path'."
}

$applicationCode = Get-Content -LiteralPath $applicationCodePath -Raw
$program = Get-Content -LiteralPath $programPath -Raw
$project = Get-Content -LiteralPath $projectPath -Raw
$serviceLifecycle = Get-Content -LiteralPath $serviceLifecyclePath -Raw
$serviceUpdate = Get-Content -LiteralPath $serviceUpdatePath -Raw
$serviceRollback = Get-Content -LiteralPath $serviceRollbackPath -Raw
$bundlePolicy = Get-Content -LiteralPath $bundlePolicyPath -Raw | ConvertFrom-Json
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$controlPipeFactory = Get-Content -LiteralPath $controlPipeFactoryPath -Raw
$runtimePipeFactory = Get-Content -LiteralPath $runtimePipeFactoryPath -Raw
$operatorControlTransport = Get-Content -LiteralPath $operatorControlTransportPath -Raw
$operatorMonitoringTransport = Get-Content -LiteralPath $operatorMonitoringTransportPath -Raw
$runtimeIpcServer = Get-Content -LiteralPath $runtimeIpcServerPath -Raw
$aiIpcServer = Get-Content -LiteralPath $aiIpcServerPath -Raw

foreach ($ownership in @('EphemeralLocal', 'PersistentEngine', 'ExternalManaged')) {
	Assert-Condition ($applicationCode -match [Regex]::Escape($ownership)) "Application lifecycle is missing explicit ownership '$ownership'."
}

Assert-Condition ($applicationCode -match 'WaitForExternalReadinessAsync') "ExternalManaged must use an adopt-only readiness path."
Assert-Condition ($applicationCode -match 'windowsService \|\| ownership == ApplicationLifecycleOwnership\.ExternalManaged') "ExternalManaged must default to the persistent service work root."
Assert-Condition ($program -match [Regex]::Escape('--windows-service')) "AppHost must expose Windows service mode."
Assert-Condition ($program -match 'AddWindowsService') "AppHost must use supported .NET Windows service hosting."
Assert-Condition ($program -match 'options\.WindowsServiceName') "Windows service hosting must use the exact configured SCM service identity."
Assert-Condition ($serviceLifecycle -match [Regex]::Escape('--service-name=')) "Service registration must pass the SCM service identity to AppHost."
Assert-Condition ($applicationCode -match 'OperatorPipeSid') "AppHost must carry the explicit Operator pipe SID."
Assert-Condition ($applicationCode -match [Regex]::Escape('apphost-readiness.json')) "AppHost must persist service-owned readiness evidence."
Assert-Condition ($serviceLifecycle -match 'OperatorPrincipal') "Service installation must expose an explicit Operator principal."
Assert-Condition ($serviceLifecycle -match [Regex]::Escape('--operator-pipe-sid=')) "Service registration must pass the authorized Operator SID to AppHost."
Assert-Condition ($serviceLifecycle -match [Regex]::Escape('apphost-readiness.json')) "Service qualification must consume AppHost-owned readiness evidence."
Assert-Condition ($program -match 'WindowsEngineBackgroundService') "AppHost must host the persistent lifecycle in a background service."
Assert-Condition ($project -match 'Microsoft\.Extensions\.Hosting\.WindowsServices') "AppHost must reference the Windows service hosting package."
Assert-Condition ($project -notmatch '<UseWPF>true</UseWPF>') "Windows service AppHost must not take a WPF dependency."

foreach ($token in @(
	"'Install'",
	"'Uninstall'",
	"'Start'",
	"'Stop'",
	"'Restart'",
	"'Status'",
	"'Qualify'",
	'--profile=HeadlessEngine',
	'--ownership=PersistentEngine',
	"'failure'",
	"'failureflag'",
	'Wait-RuntimeReadiness',
	'Stop-ServiceGracefully',
	'apphost-shutdown.json',
	'forcedTermination'
)) {
	Assert-Condition ($serviceLifecycle -match [Regex]::Escape($token)) "Windows service lifecycle is missing required behavior '$token'."
}

Assert-Condition ($serviceLifecycle -notmatch 'Stop-Process') "Windows service lifecycle must not bypass graceful service stop with Stop-Process."
Assert-Condition ($serviceLifecycle -notmatch '\.Kill\(') "Windows service lifecycle must not classify direct process killing as service shutdown."
Assert-Condition ($serviceLifecycle -notmatch 'Test-NamedPipeEndpoint') "Administrative qualification must not require direct access to service-account-only Runtime/AI pipes."

foreach ($factory in @($controlPipeFactory, $runtimePipeFactory)) {
	Assert-Condition ($factory -match 'NamedPipeServerStreamAcl\.Create') "Operator-facing service pipes must use explicit Windows ACL creation."
	Assert-Condition ($factory -match 'RTAIME_OPERATOR_PIPE_SID') "Operator-facing service pipes must consume the explicitly authorized SID."
	Assert-Condition ($factory -match 'PipeAccessRights\.ReadWrite') "Authorized Operator SID must receive bounded read/write access."
	Assert-Condition ($factory -match 'PipeOptions\.CurrentUserOnly') "Local non-service operation must preserve CurrentUserOnly fallback."
	Assert-Condition ($factory -notmatch 'WorldSid|AuthenticatedUserSid|Everyone') "Operator pipe ACL must not grant broad all-user access."
}

Assert-Condition ($operatorControlTransport -notmatch 'PipeOptions\.CurrentUserOnly') "Operator control client must allow server-ACL-authorized cross-session service IPC."
Assert-Condition ($operatorMonitoringTransport -notmatch 'PipeOptions\.CurrentUserOnly') "Operator monitoring client must allow server-ACL-authorized cross-session service IPC."
Assert-Condition ($runtimeIpcServer -match 'PipeOptions\.CurrentUserOnly') "Runtime management IPC must remain service-account-local."
Assert-Condition ($aiIpcServer -match 'PipeOptions\.CurrentUserOnly') "AI management IPC must remain service-account-local."

$maintenanceChecks = @(
	[pscustomobject]@{ Content = $serviceUpdate; Delegate = 'Invoke-VerifiedUpdate.ps1'; Evidence = 'SERVICE_MANAGED_UPDATE' },
	[pscustomobject]@{ Content = $serviceRollback; Delegate = 'Invoke-SoftwareRollback.ps1'; Evidence = 'SERVICE_MANAGED_ROLLBACK' }
)
foreach ($maintenance in $maintenanceChecks) {
	Assert-Condition ($maintenance.Content -match [Regex]::Escape('Invoke-WindowsServiceLifecycle.ps1')) "Service maintenance must use the Windows service lifecycle authority."
	Assert-Condition ($maintenance.Content -match [Regex]::Escape($maintenance.Delegate)) "Service maintenance must delegate to '$($maintenance.Delegate)'."
	Assert-Condition ($maintenance.Content -match [Regex]::Escape($maintenance.Evidence)) "Service maintenance must emit '$($maintenance.Evidence)' evidence."
	Assert-Condition ($maintenance.Content -match '-Action Stop') "Service maintenance must stop the engine before maintenance."
	Assert-Condition ($maintenance.Content -match '-Action Start') "Service maintenance must restart the engine only after maintenance."
	Assert-Condition ($maintenance.Content -match '-Action Qualify') "Service maintenance must qualify readiness after restart."
}

$requiredOfflineTools = @(
	'build/operations/Invoke-WindowsServiceLifecycle.ps1',
	'build/update/Invoke-ServiceManagedUpdate.ps1',
	'build/update/Invoke-ServiceManagedRollback.ps1'
)
foreach ($tool in $requiredOfflineTools) {
	Assert-Condition (@($bundlePolicy.offlineTools) -contains $tool) "Offline bundle policy must package '$tool'."
}

foreach ($token in @(
	'PersistentEngine',
	'ExternalManaged',
	'Service Control Manager',
	'Invoke-WindowsServiceLifecycle.ps1',
	'Invoke-ServiceManagedUpdate.ps1',
	'Invoke-ServiceManagedRollback.ps1',
	'OperatorPrincipal',
	'apphost-readiness.json',
	'UNVERIFIED',
	'no WPF dependency'
)) {
	Assert-Condition ($documentation -match [Regex]::Escape($token)) "Windows production lifecycle documentation is missing '$token'."
}

Write-Host 'Windows production lifecycle policy PASS'
Write-Host 'Lifecycle ownership: EphemeralLocal, PersistentEngine, ExternalManaged'
Write-Host 'Persistent engine host: Windows Service Control Manager'
Write-Host 'Runtime/AI supervision owner: ControlHost'
Write-Host 'Physical reboot/service recovery qualification: UNVERIFIED until reference-platform execution'
