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

$appProjectPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/rtaime.AppHost.csproj"
$appCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/ApplicationHost.cs"
$appProgramPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/Program.cs"
$solutionPath = Join-Path $repositoryRoot "rtaime.slnx"
$releasePolicyPath = Join-Path $repositoryRoot "build/release/release-policy.json"
$bundlePolicyPath = Join-Path $repositoryRoot "build/release/offline-bundle-policy.json"
$bundleBuilderPath = Join-Path $repositoryRoot "build/release/New-OfflineReleaseBundle.ps1"
$readmePath = Join-Path $repositoryRoot "README.md"
$startupDocumentationPath = Join-Path $repositoryRoot "docs/ApplicationStartup.md"

foreach ($path in @(
	$appProjectPath,
	$appCodePath,
	$appProgramPath,
	$solutionPath,
	$releasePolicyPath,
	$bundlePolicyPath,
	$bundleBuilderPath,
	$readmePath,
	$startupDocumentationPath
)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required application-startup artifact is missing: '$path'."
}

$appProject = Get-Content -LiteralPath $appProjectPath -Raw
$appCode = Get-Content -LiteralPath $appCodePath -Raw
$appProgram = Get-Content -LiteralPath $appProgramPath -Raw
$solution = Get-Content -LiteralPath $solutionPath -Raw
$releasePolicy = Get-Content -LiteralPath $releasePolicyPath -Raw | ConvertFrom-Json
$bundlePolicy = Get-Content -LiteralPath $bundlePolicyPath -Raw | ConvertFrom-Json
$bundleBuilder = Get-Content -LiteralPath $bundleBuilderPath -Raw
$readme = Get-Content -LiteralPath $readmePath -Raw
$startupDocumentation = Get-Content -LiteralPath $startupDocumentationPath -Raw

Assert-Condition ($appProject -match '<AssemblyName>rtaime</AssemblyName>') "AppHost must build the canonical rtaime executable name."
Assert-Condition ($appProject -notmatch '<ProjectReference') "AppHost must not take direct project references to service hosts or production implementations."
Assert-Condition ($solution -match 'src/Hosts/rtaime\.AppHost/rtaime\.AppHost\.csproj') "Primary solution must contain rtaime.AppHost."

foreach ($profile in @("Interactive", "Showcase", "HeadlessEngine")) {
	Assert-Condition ($appCode -match [Regex]::Escape($profile)) "AppHost is missing startup profile '$profile'."
}

foreach ($ownership in @("EphemeralLocal", "PersistentEngine", "ExternalManaged")) {
	Assert-Condition ($appCode -match [Regex]::Escape($ownership)) "AppHost is missing lifecycle ownership '$ownership'."
}

Assert-Condition ($appProgram -match [Regex]::Escape("--windows-service")) "Canonical AppHost must expose the Windows service hosting switch."
Assert-Condition ($appProgram -match "AddWindowsService") "Canonical AppHost must use supported Windows service hosting."

foreach ($state in @("Stopped", "Starting", "Healthy", "Degraded", "Recovering", "Failed", "Stopping")) {
	Assert-Condition ($appCode -match [Regex]::Escape($state)) "AppHost is missing lifecycle state '$state'."
}

foreach ($token in @(
	"FindHealthyReadinessAsync",
	"StartControlHost",
	"StartOperator",
	"RTAIME_RUNTIME_EXECUTABLE",
	"RTAIME_AI_EXECUTABLE",
	"RTAIME_HOST_READINESS_FILE",
	"RTAIME_HOST_STOP_FILE",
	"ProbePipeAsync",
	"RuntimeSupervision",
	"AISupervision"
)) {
	Assert-Condition ($appCode -match [Regex]::Escape($token)) "AppHost is missing required startup/readiness behavior '$token'."
}

Assert-Condition ($appCode -notmatch 'StartRuntimeHost') "AppHost must not directly launch RuntimeHost."
Assert-Condition ($appCode -notmatch 'StartAIHost') "AppHost must not directly launch AIHost."
Assert-Condition ($appProgram -match 'UnifiedApplicationHost') "Canonical entry point must delegate to UnifiedApplicationHost."

$appReleaseHost = @($releasePolicy.hosts | Where-Object { [string]$_.name -eq "rtaime" })
Assert-Condition ($appReleaseHost.Count -eq 1) "Release policy must contain exactly one canonical rtaime AppHost payload."
Assert-Condition ([string]$appReleaseHost[0].outputPath -match 'rtaime\.AppHost') "Canonical rtaime release payload must come from rtaime.AppHost."

Assert-Condition ([string]$bundlePolicy.applicationEntryPoint.productDirectory -eq "rtaime") "Bundle application entry point must come from product/rtaime."
Assert-Condition ([string]$bundlePolicy.applicationEntryPoint.executable -eq "rtaime.exe") "Bundle application entry point must be rtaime.exe."
Assert-Condition ($bundleBuilder -match 'applicationEntryPoint' -and $bundleBuilder -match 'applicationHostSource') "Offline bundle builder must materialize the AppHost payload at bundle root."

foreach ($artifact in @($readme, $startupDocumentation)) {
	Assert-Condition ($artifact -match 'rtaime\.exe') "Product documentation must identify rtaime.exe as the canonical startup executable."
	Assert-Condition ($artifact -match 'ControlHost' -and $artifact -match 'RuntimeHost' -and $artifact -match 'AIHost' -and $artifact -match 'Operator') "Product documentation must preserve the multi-process topology."
}

Write-Host "Application startup policy PASS"
Write-Host "Canonical entry point: rtaime.exe"
Write-Host "Service supervision owner: ControlHost"
Write-Host "Startup profiles: Interactive, Showcase, HeadlessEngine"
Write-Host "Lifecycle ownership: EphemeralLocal, PersistentEngine, ExternalManaged"
