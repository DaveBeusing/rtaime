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

foreach ($path in @(
	$applicationCodePath,
	$programPath,
	$projectPath,
	$serviceLifecyclePath,
	$serviceUpdatePath,
	$serviceRollbackPath,
	$bundlePolicyPath,
	$documentationPath
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

foreach ($ownership in @('EphemeralLocal', 'PersistentEngine', 'ExternalManaged')) {
	Assert-Condition ($applicationCode -match [Regex]::Escape($ownership)) "Application lifecycle is missing explicit ownership '$ownership'."
}

Assert-Condition ($applicationCode -match 'WaitForExternalReadinessAsync') "ExternalManaged must use an adopt-only readiness path."
Assert-Condition ($applicationCode -match 'windowsService \|\| ownership == ApplicationLifecycleOwnership\.ExternalManaged') "ExternalManaged must default to the persistent service work root."
Assert-Condition ($program -match [Regex]::Escape('--windows-service')) "AppHost must expose Windows service mode."
Assert-Condition ($program -match 'AddWindowsService') "AppHost must use supported .NET Windows service hosting."
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
	'Stop-ServiceGracefully'
)) {
	Assert-Condition ($serviceLifecycle -match [Regex]::Escape($token)) "Windows service lifecycle is missing required behavior '$token'."
}

Assert-Condition ($serviceLifecycle -notmatch 'Stop-Process') "Windows service lifecycle must not bypass graceful service stop with Stop-Process."
Assert-Condition ($serviceLifecycle -notmatch '\.Kill\(') "Windows service lifecycle must not classify direct process killing as service shutdown."

foreach ($maintenance in @(
	@($serviceUpdate, 'Invoke-VerifiedUpdate.ps1', 'SERVICE_MANAGED_UPDATE'),
	@($serviceRollback, 'Invoke-SoftwareRollback.ps1', 'SERVICE_MANAGED_ROLLBACK')
)) {
	Assert-Condition ($maintenance[0] -match [Regex]::Escape('Invoke-WindowsServiceLifecycle.ps1')) "Service maintenance must use the Windows service lifecycle authority."
	Assert-Condition ($maintenance[0] -match [Regex]::Escape($maintenance[1])) "Service maintenance must delegate to '$($maintenance[1])'."
	Assert-Condition ($maintenance[0] -match [Regex]::Escape($maintenance[2])) "Service maintenance must emit '$($maintenance[2])' evidence."
	Assert-Condition ($maintenance[0] -match "-Action Stop") "Service maintenance must stop the engine before maintenance."
	Assert-Condition ($maintenance[0] -match "-Action Start") "Service maintenance must restart the engine only after maintenance."
	Assert-Condition ($maintenance[0] -match "-Action Qualify") "Service maintenance must qualify readiness after restart."
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
