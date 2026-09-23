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
$lifecycleProviderPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/ApplicationLifecycleStateProvider.cs"
$appProgramPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/Program.cs"
$windowsBootstrapPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/WindowsConsoleBootstrap.cs"
$startupDiagnosticsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/ApplicationStartupDiagnostics.cs"
$windowsBootstrapQualificationPath = Join-Path $repositoryRoot "build/operations/Test-WindowsApplicationBootstrap.ps1"
$solutionPath = Join-Path $repositoryRoot "rtaime.slnx"
$releasePolicyPath = Join-Path $repositoryRoot "build/release/release-policy.json"
$bundlePolicyPath = Join-Path $repositoryRoot "build/release/offline-bundle-policy.json"
$bundleBuilderPath = Join-Path $repositoryRoot "build/release/New-OfflineReleaseBundle.ps1"
$readmePath = Join-Path $repositoryRoot "README.md"
$startupDocumentationPath = Join-Path $repositoryRoot "docs/ApplicationStartup.md"
$developerBuildPath = Join-Path $repositoryRoot "build/development/Invoke-DeveloperBuild.ps1"
$buildDocumentationPath = Join-Path $repositoryRoot "docs/BuildAndTest.md"

foreach ($path in @(
	$appProjectPath,
	$appCodePath,
	$lifecycleProviderPath,
	$appProgramPath,
	$windowsBootstrapPath,
	$startupDiagnosticsPath,
	$windowsBootstrapQualificationPath,
	$solutionPath,
	$releasePolicyPath,
	$bundlePolicyPath,
	$bundleBuilderPath,
	$readmePath,
	$startupDocumentationPath,
	$developerBuildPath,
	$buildDocumentationPath
)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required application-startup artifact is missing: '$path'."
}

$appProject = Get-Content -LiteralPath $appProjectPath -Raw
$appCode = Get-Content -LiteralPath $appCodePath -Raw
$lifecycleProvider = Get-Content -LiteralPath $lifecycleProviderPath -Raw
$appProgram = Get-Content -LiteralPath $appProgramPath -Raw
$windowsBootstrap = Get-Content -LiteralPath $windowsBootstrapPath -Raw
$startupDiagnostics = Get-Content -LiteralPath $startupDiagnosticsPath -Raw
$windowsBootstrapQualification = Get-Content -LiteralPath $windowsBootstrapQualificationPath -Raw
$solution = Get-Content -LiteralPath $solutionPath -Raw
$releasePolicy = Get-Content -LiteralPath $releasePolicyPath -Raw | ConvertFrom-Json
$bundlePolicy = Get-Content -LiteralPath $bundlePolicyPath -Raw | ConvertFrom-Json
$bundleBuilder = Get-Content -LiteralPath $bundleBuilderPath -Raw
$readme = Get-Content -LiteralPath $readmePath -Raw
$startupDocumentation = Get-Content -LiteralPath $startupDocumentationPath -Raw
$developerBuild = Get-Content -LiteralPath $developerBuildPath -Raw
$buildDocumentation = Get-Content -LiteralPath $buildDocumentationPath -Raw

Assert-Condition ($appProject -match '<AssemblyName>rtaime</AssemblyName>') "AppHost must build the canonical rtaime executable name."
Assert-Condition ($appProject -match '<OutputType>WinExe</OutputType>') "Canonical Windows AppHost must use the GUI subsystem so interactive launch cannot flash a console window."
$appHostReferences = [Regex]::Matches($appProject, '<ProjectReference Include="([^"]+)"')
Assert-Condition ($appHostReferences.Count -eq 1) "AppHost must have exactly one direct project reference for dependency-neutral shared diagnostics."
Assert-Condition ($appHostReferences[0].Groups[1].Value -match 'rtaime\.Core\\rtaime\.Core\.csproj$') "AppHost may reference only dependency-neutral rtaime.Core; service hosts and production implementations remain forbidden."
Assert-Condition ($solution -match 'src/Hosts/rtaime\.AppHost/rtaime\.AppHost\.csproj') "Primary solution must contain rtaime.AppHost."

foreach ($profile in @("Interactive", "Showcase", "HeadlessEngine")) {
	Assert-Condition ($appCode -match [Regex]::Escape($profile)) "AppHost is missing startup profile '$profile'."
}

foreach ($ownership in @("EphemeralLocal", "PersistentEngine", "ExternalManaged")) {
	Assert-Condition ($appCode -match [Regex]::Escape($ownership)) "AppHost is missing lifecycle ownership '$ownership'."
}

Assert-Condition ($appProgram -match [Regex]::Escape("--windows-service")) "Canonical AppHost must expose the Windows service hosting switch."
Assert-Condition ($appProgram -match "AddWindowsService") "Canonical AppHost must use supported Windows service hosting."
Assert-Condition ($appCode -match 'profile == ApplicationStartupProfile\.HeadlessEngine') "HeadlessEngine must be the only profile that defaults to persistent lifecycle ownership."
Assert-Condition ($appCode -match '\? ApplicationLifecycleOwnership\.PersistentEngine\s+: ApplicationLifecycleOwnership\.EphemeralLocal') "Interactive and Showcase must default to EphemeralLocal ownership."

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
	"IsEndpointLeaseHeld",
	"ControlHostDiagnosticPath",
	"DiagnosticLogPath",
	"FlushProcessDiagnosticsAsync",
	"BuildControlHostExitDetail",
	"RuntimeSupervision",
	"AISupervision"
)) {
	Assert-Condition ($appCode -match [Regex]::Escape($token)) "AppHost is missing required startup/readiness behavior '$token'."
}

Assert-Condition ($appCode -notmatch 'StartRuntimeHost') "AppHost must not directly launch RuntimeHost."
Assert-Condition ($appCode -notmatch 'StartAIHost') "AppHost must not directly launch AIHost."
Assert-Condition ($appCode -match 'WaitForLeasedControlReadinessAsync' -and $appCode -match 'remains owned by an existing process') "AppHost must wait on an existing ControlHost endpoint lease instead of starting a competing host."
Assert-Condition ($appCode -match 'RedirectStandardOutput = true' -and $appCode -match 'RedirectStandardError = true' -and $appCode -match 'exitCode=') "AppHost must persist ControlHost stdout, stderr and exit-code diagnostics."

Assert-Condition ($appCode -match 'RTAIME_APPHOST_LIFECYCLE_FILE' -and $appCode -match 'LifecycleEvidencePath') "Interactive startup must expose AppHost lifecycle evidence to the Operator."
Assert-Condition ($appProgram -match 'WindowsConsoleBootstrap\.Initialize' -and $appProgram -match 'ApplicationStartupDiagnostics\.TryPersistFailure') "Canonical entry point must configure Windows console behavior before lifecycle startup and persist bootstrap failures."
Assert-Condition ($windowsBootstrap -match '--show-console' -and $windowsBootstrap -match 'HeadlessEngine' -and $windowsBootstrap -match 'AttachConsole' -and $windowsBootstrap -match 'AllocConsole') "Windows bootstrap must keep explicit and headless console paths while interactive startup remains GUI-native."
Assert-Condition ($windowsBootstrap -match '--windows-service' -and $windowsBootstrap -match 'ConsoleAvailable: false') "Windows service startup must remain non-interactive and must not allocate a console."
Assert-Condition ($startupDiagnostics -match 'apphost-startup\.log' -and $startupDiagnostics -match 'File\.AppendAllText') "Bootstrap failures must persist deterministic local diagnostics."
Assert-Condition ($startupDiagnostics -match 'DiagnosticRedactor\.RedactText' -and $startupDiagnostics -match 'DiagnosticRedactor\.RedactExceptionDetail') "Persisted bootstrap failures must use the shared diagnostics redaction policy."
Assert-Condition ($appProgram -notmatch 'ShowWindow|HideConsoleWindow') "Interactive startup must not rely on hiding an already-created console window."
Assert-Condition ($windowsBootstrapQualification -match 'WINDOWS_GUI' -and $windowsBootstrapQualification -match 'RedirectStandardError' -and $windowsBootstrapQualification -match 'QualifySingleFile') "Windows bootstrap qualification must cover GUI subsystem, redirected diagnostics and single-file publishing."
foreach ($stage in @("ApplicationBootstrap", "Configuration", "OperatorInterface", "ControlHost", "RuntimeHost", "AIHost", "ProductionReadiness")) {
	Assert-Condition ($lifecycleProvider -match [Regex]::Escape($stage)) "AppHost lifecycle evidence is missing stage '$stage'."
}
foreach ($status in @("Pending", "Starting", "Ready", "Degraded", "Failed")) {
	Assert-Condition ($lifecycleProvider -match [Regex]::Escape($status)) "AppHost lifecycle evidence is missing stage status '$status'."
}
Assert-Condition ($lifecycleProvider -match 'StartedAt' -and $lifecycleProvider -match 'CompletedAt' -and $lifecycleProvider -match 'CanRetry') "Lifecycle stage evidence must retain timing and retry metadata."
Assert-Condition ($lifecycleProvider -match 'diagnosticPath = _diagnosticPath' -and $appCode -match '_options\.ControlHostDiagnosticPath') "AppHost lifecycle evidence must publish the stable ControlHost diagnostic path for read-only Operator diagnostics."
Assert-Condition ($lifecycleProvider -match 'enum LifecycleStageRequirement' -and $lifecycleProvider -match 'Critical' -and $lifecycleProvider -match 'RequiredForProduction' -and $lifecycleProvider -match 'Optional') "Lifecycle evidence must classify startup dependencies by operational requirement."
Assert-Condition ($lifecycleProvider -match 'requirement = stage\.Requirement\.ToString\(\)') "Published lifecycle evidence must include dependency requirement classification."
Assert-Condition ($lifecycleProvider -match '_requireAI \? LifecycleStageRequirement\.RequiredForProduction : LifecycleStageRequirement\.Optional') "AIHost must be optional unless the selected startup profile explicitly requires it."
Assert-Condition ($lifecycleProvider -notmatch 'PeriodicTimer|Task\.Delay|percentage|percent') "AppHost lifecycle evidence must never synthesize timer-driven or percentage progress."
Assert-Condition ($appProgram -match 'UnifiedApplicationHost') "Canonical entry point must delegate to UnifiedApplicationHost."

$parseTokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($developerBuildPath, [ref]$parseTokens, [ref]$parseErrors)
Assert-Condition (@($parseErrors).Count -eq 0) "Developer build script must parse as valid PowerShell."

$bootstrapParseTokens = $null
$bootstrapParseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($windowsBootstrapQualificationPath, [ref]$bootstrapParseTokens, [ref]$bootstrapParseErrors)
Assert-Condition (@($bootstrapParseErrors).Count -eq 0) "Windows bootstrap qualification script must parse as valid PowerShell."

foreach ($processName in @("rtaime", "rtaime.Operator", "rtaime.ControlHost", "rtaime.RuntimeHost", "rtaime.AIHost")) {
	Assert-Condition ($developerBuild -match [Regex]::Escape('"' + $processName + '"')) "Developer build cleanup is missing repository process '$processName'."
}
Assert-Condition ($developerBuild -match '\$process\.Path' -and $developerBuild -match '\$repositoryPrefix' -and $developerBuild -match 'StartsWith\(\$repositoryPrefix') "Developer build cleanup must scope process termination to executables inside the repository."
Assert-Condition ($developerBuild -match 'if \(\$_.ProcessName -eq "rtaime"\) \{ 0 \} else \{ 1 \}') "Developer build cleanup must stop the AppHost before supervised development hosts."
Assert-Condition ($developerBuild -match 'dotnet restore' -and $developerBuild -match '"build", \$solutionPath') "Developer build script must retain restore and complete-solution build behavior."
Assert-Condition ($buildDocumentation -match 'build/development/Invoke-DeveloperBuild\.ps1' -and $readme -match 'build/development/Invoke-DeveloperBuild\.ps1') "Canonical build documentation must direct developers to the lock-safe build entry point."

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
