# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$ProfilePath = "qualification/reference-platform/reference-platform.json",
	[string]$OutputRoot = "artifacts/qualification/reference-platform",
	[string]$BindingRoot = "artifacts/qualification/bindings",
	[string]$SourceCommit = "",
	[switch]$NoBuild,
	[switch]$RequirePass
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$bindingVerifier = Join-Path $PSScriptRoot "Test-QualificationEvidenceBinding.ps1"
$performanceGenerator = Join-Path $PSScriptRoot "New-SupportedPerformanceEvidence.ps1"
$resultVerifier = Join-Path $PSScriptRoot "Test-ReferencePlatformQualificationResult.ps1"
$validStatuses = @("PASS", "FAIL", "NOT_APPLICABLE", "UNVERIFIED")

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) {
		return [System.IO.Path]::GetFullPath($Path)
	}
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Get-RepositoryRelativePath {
	param([Parameter(Mandatory)][string]$Path)
	$relative = [System.IO.Path]::GetRelativePath($repositoryRoot, [System.IO.Path]::GetFullPath($Path)).Replace('\', '/')
	if ($relative -eq ".." -or $relative.StartsWith("../", [StringComparison]::Ordinal)) {
		throw "Qualification evidence must stay inside the repository workspace."
	}
	return $relative
}

function Write-JsonFile {
	param(
		[Parameter(Mandatory)]$Value,
		[Parameter(Mandatory)][string]$Path
	)
	$directory = Split-Path -Parent $Path
	if (-not (Test-Path -LiteralPath $directory)) {
		New-Item -ItemType Directory -Path $directory -Force | Out-Null
	}
	$json = $Value | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
	param(
		[Parameter(Mandatory)][AllowEmptyString()][AllowEmptyCollection()][string[]]$Lines,
		[Parameter(Mandatory)][string]$Path
	)
	$directory = Split-Path -Parent $Path
	if (-not (Test-Path -LiteralPath $directory)) {
		New-Item -ItemType Directory -Path $directory -Force | Out-Null
	}
	[System.IO.File]::WriteAllLines($Path, $Lines, [System.Text.UTF8Encoding]::new($false))
}

function Get-SourceCommit {
	param([string]$Requested)
	if (-not [string]::IsNullOrWhiteSpace($Requested)) {
		return $Requested.Trim().ToLowerInvariant()
	}
	try {
		$output = @(& git -C $repositoryRoot rev-parse HEAD 2>$null)
		$exitCode = $LASTEXITCODE
		if ($exitCode -eq 0 -and $output.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$output[0])) {
			return ([string]$output[0]).Trim().ToLowerInvariant()
		}
	} catch {
		# Environment evidence remains useful even when Git metadata is unavailable.
	}
	return "UNAVAILABLE"
}

function Get-AggregatedStatus {
	param([Parameter(Mandatory)][object[]]$Items)
	$mandatory = @($Items | Where-Object { [bool]$_.mandatory })
	if (@($mandatory | Where-Object { [string]$_.status -eq "FAIL" }).Count -gt 0) { return "FAIL" }
	if (@($mandatory | Where-Object { [string]$_.status -eq "UNVERIFIED" }).Count -gt 0) { return "UNVERIFIED" }
	if (@($mandatory | Where-Object { [string]$_.status -notin @("PASS", "NOT_APPLICABLE") }).Count -gt 0) { return "FAIL" }
	return "PASS"
}

function Get-SafeFileName {
	param([Parameter(Mandatory)][string]$Value)
	return (($Value.ToLowerInvariant() -replace '[^a-z0-9._-]', '-') -replace '-+', '-').Trim('-')
}

$resolvedProfile = Resolve-RepositoryPath -Path $ProfilePath
if (-not (Test-Path -LiteralPath $resolvedProfile -PathType Leaf)) { throw "Reference-platform profile is missing: '$resolvedProfile'." }
if (-not (Test-Path -LiteralPath $bindingVerifier -PathType Leaf)) { throw "Qualification binding verifier is missing." }
if (-not (Test-Path -LiteralPath $performanceGenerator -PathType Leaf)) { throw "Supported-performance evidence generator is missing." }
if (-not (Test-Path -LiteralPath $resultVerifier -PathType Leaf)) { throw "Reference-platform result verifier is missing." }

$profile = Get-Content -LiteralPath $resolvedProfile -Raw | ConvertFrom-Json
if ([string]$profile.schemaVersion -ne "1.0") { throw "Reference-platform profile schema must be 1.0." }
if ([string]$profile.profile -ne "rtaime-v1-reference-platform") { throw "Unexpected reference-platform profile identity." }
if (@($profile.softwareScenarios).Count -ne 10) { throw "Reference-platform profile must contain Q01-Q10." }
if (@($profile.hardwareRequirements).Count -ne 12) { throw "Reference-platform profile must contain the twelve explicit V1 physical evidence requirements." }

$resolvedOutputRoot = Resolve-RepositoryPath -Path $OutputRoot
[void](Get-RepositoryRelativePath -Path $resolvedOutputRoot)
$logsRoot = Join-Path $resolvedOutputRoot "logs"
if (Test-Path -LiteralPath $resolvedOutputRoot) {
	Remove-Item -LiteralPath $resolvedOutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null

$capturedAtUtc = [DateTimeOffset]::UtcNow
$resolvedSourceCommit = Get-SourceCommit -Requested $SourceCommit
if ($resolvedSourceCommit -notmatch '^[0-9a-f]{40}$') {
	throw "Reference-platform qualification requires an exact 40-character source commit."
}
$dotnetVersion = "UNAVAILABLE"
try {
	$dotnetOutput = @(& dotnet --version 2>$null)
	$dotnetExitCode = $LASTEXITCODE
	if ($dotnetExitCode -eq 0 -and $dotnetOutput.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$dotnetOutput[0])) {
		$dotnetVersion = ([string]$dotnetOutput[0]).Trim()
	}
} catch {
	$dotnetVersion = "UNAVAILABLE"
}

$windowsPlatformDetected = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$platformChecks = @(
	[ordered]@{
		name = "OperatingSystemFamily"
		expected = [string]$profile.platform.operatingSystemFamily
		actual = if ($windowsPlatformDetected) { "Windows" } else { [System.Runtime.InteropServices.RuntimeInformation]::OSDescription }
		status = if ($windowsPlatformDetected -and [string]$profile.platform.operatingSystemFamily -eq "Windows") { "PASS" } else { "FAIL" }
	},
	[ordered]@{
		name = "Architecture"
		expected = [string]$profile.platform.architecture
		actual = $processArchitecture
		status = if ($processArchitecture -eq "X64" -and [string]$profile.platform.architecture -eq "x64") { "PASS" } else { "FAIL" }
	},
	[ordered]@{
		name = "DotNetSdk"
		expected = [string]$profile.platform.dotnetSdk
		actual = $dotnetVersion
		status = if ($dotnetVersion -eq [string]$profile.platform.dotnetSdk) { "PASS" } else { "FAIL" }
	}
)
$platformStatus = if (@($platformChecks | Where-Object { [string]$_.status -eq "FAIL" }).Count -eq 0) { "PASS" } else { "FAIL" }

$environmentPath = Join-Path $resolvedOutputRoot "environment.json"
$environment = [ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	capturedAtUtc = $capturedAtUtc.ToString("O")
	profile = [string]$profile.profile
	sourceCommit = $resolvedSourceCommit
	osDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
	osArchitecture = $osArchitecture
	processArchitecture = $processArchitecture
	frameworkDescription = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
	dotnetSdk = $dotnetVersion
	processorCount = [Environment]::ProcessorCount
	platformStatus = $platformStatus
	platformChecks = $platformChecks
}
Write-JsonFile -Value $environment -Path $environmentPath

$softwareExecutions = @{}
$scenarioResults = [System.Collections.Generic.List[object]]::new()
foreach ($scenario in @($profile.softwareScenarios)) {
	$id = [string]$scenario.id
	$group = [string]$scenario.evidenceGroup
	$projectRelative = [string]$scenario.testProject
	$filter = [string]$scenario.filter
	if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($group) -or [string]::IsNullOrWhiteSpace($projectRelative) -or [string]::IsNullOrWhiteSpace($filter)) {
		throw "Reference-platform scenario metadata is incomplete."
	}

	$key = $group
	if ($softwareExecutions.ContainsKey($key)) {
		$cached = $softwareExecutions[$key]
		if ([string]$cached.testProject -ne $projectRelative -or [string]$cached.filter -ne $filter) {
			throw "Evidence group '$group' maps to inconsistent test evidence."
		}
	} else {
		$projectPath = Resolve-RepositoryPath -Path $projectRelative
		[void](Get-RepositoryRelativePath -Path $projectPath)
		$logPath = Join-Path $logsRoot ("software-{0}.log" -f (Get-SafeFileName -Value $group))
		$started = [System.Diagnostics.Stopwatch]::StartNew()
		$exitCode = 1
		$lines = [System.Collections.Generic.List[string]]::new()
		if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
			$lines.Add("Test project is missing: $projectRelative")
		} elseif ($dotnetVersion -eq "UNAVAILABLE") {
			$lines.Add("dotnet SDK is unavailable.")
		} else {
			$arguments = @("test", $projectPath, "--configuration", "Release", "--filter", $filter)
			if ($NoBuild) { $arguments += "--no-build" }
			$lines.Add("dotnet " + ($arguments -join " "))
			try {
				$commandOutput = @(& dotnet @arguments 2>&1)
				$exitCode = $LASTEXITCODE
				foreach ($line in $commandOutput) { $lines.Add([string]$line) }
			} catch {
				$exitCode = 1
				$lines.Add($_.Exception.ToString())
			}
		}
		$started.Stop()
		Write-TextFile -Lines $lines.ToArray() -Path $logPath
		$cached = [ordered]@{
			evidenceGroup = $group
			testProject = $projectRelative
			filter = $filter
			status = if ($exitCode -eq 0) { "PASS" } else { "FAIL" }
			exitCode = $exitCode
			durationMilliseconds = [Math]::Round($started.Elapsed.TotalMilliseconds, 3)
			evidencePath = Get-RepositoryRelativePath -Path $logPath
		}
		$softwareExecutions[$key] = $cached
	}

	$status = [string]$cached.status
	$detail = if ($status -eq "PASS") { "Mapped CI-safe software evidence passed." } else { "Mapped CI-safe software evidence failed." }
	if ($id -eq "Q01" -and $platformStatus -ne "PASS") {
		$status = "FAIL"
		$detail = "Platform validation failed before bootstrap qualification could be accepted."
	}
	$scenarioResults.Add([ordered]@{
		id = $id
		name = [string]$scenario.name
		mandatory = [bool]$scenario.mandatory
		evidenceGroup = $group
		status = $status
		detail = $detail
		testProject = $projectRelative
		filter = $filter
		durationMilliseconds = [double]$cached.durationMilliseconds
		evidencePath = [string]$cached.evidencePath
		proves = @($scenario.proves)
	})
}

$resolvedBindingRoot = Resolve-RepositoryPath -Path $BindingRoot
[void](Get-RepositoryRelativePath -Path $resolvedBindingRoot)
$physicalExecutions = @{}
$hardwareResults = [System.Collections.Generic.List[object]]::new()
foreach ($requirement in @($profile.hardwareRequirements)) {
	$type = [string]$requirement.qualificationType
	$bindingFile = [string]$requirement.bindingFile
	$key = "$type|$bindingFile"
	if (-not $physicalExecutions.ContainsKey($key)) {
		$bindingPath = Join-Path $resolvedBindingRoot $bindingFile
		$bindingRelative = Get-RepositoryRelativePath -Path $bindingPath
		$status = "UNVERIFIED"
		$detail = "Source-bound physical qualification evidence is not present."
		if (Test-Path -LiteralPath $bindingPath -PathType Leaf) {
			if ($resolvedSourceCommit -notmatch '^[0-9a-f]{40}$') {
				$detail = "Physical binding exists, but the exact source commit is unavailable; evidence remains UNVERIFIED."
			} else {
				try {
					& $bindingVerifier -BindingPath $bindingPath -ExpectedSourceCommit $resolvedSourceCommit -ExpectedQualificationType $type | Out-Null
					$status = "PASS"
					$detail = "Exact source-bound physical qualification binding verified."
				} catch {
					$status = "FAIL"
					$detail = "Physical qualification binding failed verification: $($_.Exception.Message)"
				}
			}
		}
		$physicalExecutions[$key] = [ordered]@{
			status = $status
			detail = $detail
			evidencePath = if (Test-Path -LiteralPath $bindingPath -PathType Leaf) { $bindingRelative } else { "" }
		}
	}
	$physical = $physicalExecutions[$key]
	$hardwareResults.Add([ordered]@{
		id = [string]$requirement.id
		requirement = [string]$requirement.requirement
		mandatory = [bool]$requirement.mandatory
		qualificationType = $type
		status = [string]$physical.status
		detail = [string]$physical.detail
		evidencePath = [string]$physical.evidencePath
	})
}

$supportedPerformancePath = Join-Path $resolvedOutputRoot "supported-performance.json"
$supportedPerformanceMarkdownPath = Join-Path $resolvedOutputRoot "supported-performance.md"
& $performanceGenerator `
	-SourceCommit $resolvedSourceCommit `
	-BindingRoot $BindingRoot `
	-OutputPath (Get-RepositoryRelativePath -Path $supportedPerformancePath) `
	-MarkdownPath (Get-RepositoryRelativePath -Path $supportedPerformanceMarkdownPath)
$supportedPerformance = Get-Content -LiteralPath $supportedPerformancePath -Raw | ConvertFrom-Json

$softwareStatus = Get-AggregatedStatus -Items $scenarioResults.ToArray()
$hardwareStatus = Get-AggregatedStatus -Items $hardwareResults.ToArray()
$combined = @($scenarioResults.ToArray()) + @($hardwareResults.ToArray())
$overallStatus = Get-AggregatedStatus -Items $combined
$counts = [ordered]@{
	PASS = @($combined | Where-Object { [string]$_.status -eq "PASS" }).Count
	FAIL = @($combined | Where-Object { [string]$_.status -eq "FAIL" }).Count
	NOT_APPLICABLE = @($combined | Where-Object { [string]$_.status -eq "NOT_APPLICABLE" }).Count
	UNVERIFIED = @($combined | Where-Object { [string]$_.status -eq "UNVERIFIED" }).Count
}

$resultPath = Join-Path $resolvedOutputRoot "qualification-result.json"
$summaryPath = Join-Path $resolvedOutputRoot "qualification-summary.md"
$result = [ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	profile = [string]$profile.profile
	capturedAtUtc = $capturedAtUtc.ToString("O")
	sourceCommit = $resolvedSourceCommit
	status = $overallStatus
	softwareStatus = $softwareStatus
	hardwareStatus = $hardwareStatus
	platformStatus = $platformStatus
	counts = $counts
	environmentPath = Get-RepositoryRelativePath -Path $environmentPath
	supportedPerformance = [ordered]@{
		status = [string]$supportedPerformance.status
		path = Get-RepositoryRelativePath -Path $supportedPerformancePath
		markdownPath = Get-RepositoryRelativePath -Path $supportedPerformanceMarkdownPath
	}
	scenarios = $scenarioResults.ToArray()
	hardwareRequirements = $hardwareResults.ToArray()
}
Write-JsonFile -Value $result -Path $resultPath

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->")
$summary.Add("")
$summary.Add("# Reference Platform Qualification Summary")
$summary.Add("")
$summary.Add("- Profile: ``$($profile.profile)``")
$summary.Add("- Source commit: ``$resolvedSourceCommit``")
$summary.Add("- Captured: ``$($capturedAtUtc.ToString('O'))``")
$summary.Add("- Overall: **$overallStatus**")
$summary.Add("- Software: **$softwareStatus**")
$summary.Add("- Hardware: **$hardwareStatus**")
$summary.Add("- Platform checks: **$platformStatus**")
$summary.Add("- Supported performance evidence: **$($supportedPerformance.status)**")
$summary.Add("- Supported performance matrix: ``$(Get-RepositoryRelativePath -Path $supportedPerformanceMarkdownPath)``")
$summary.Add("")
$summary.Add("> `UNVERIFIED` is not `PASS`. GitHub-hosted CI may prove the software profile while the full reference platform remains UNVERIFIED until exact source-bound physical evidence exists.")
$summary.Add("")
$summary.Add("## Software scenarios")
$summary.Add("")
$summary.Add("| ID | Scenario | Status | Evidence |")
$summary.Add("| --- | --- | --- | --- |")
foreach ($scenario in $scenarioResults) {
	$summary.Add("| $($scenario.id) | $($scenario.name) | $($scenario.status) | ``$($scenario.evidencePath)`` |")
}
$summary.Add("")
$summary.Add("## Physical requirements")
$summary.Add("")
$summary.Add("| Requirement | Qualification | Status | Evidence |")
$summary.Add("| --- | --- | --- | --- |")
foreach ($item in $hardwareResults) {
	$evidence = if ([string]::IsNullOrWhiteSpace([string]$item.evidencePath)) { "not supplied" } else { "``$($item.evidencePath)``" }
	$summary.Add("| $($item.requirement) | $($item.qualificationType) | $($item.status) | $evidence |")
}
$summary.Add("")
$summary.Add("## Counts")
$summary.Add("")
$summary.Add("- PASS: $($counts.PASS)")
$summary.Add("- FAIL: $($counts.FAIL)")
$summary.Add("- NOT_APPLICABLE: $($counts.NOT_APPLICABLE)")
$summary.Add("- UNVERIFIED: $($counts.UNVERIFIED)")
Write-TextFile -Lines $summary.ToArray() -Path $summaryPath

& $resultVerifier -ResultPath $resultPath -ExpectedProfile ([string]$profile.profile) -ExpectedSourceCommit $resolvedSourceCommit

Write-Host "Reference-platform qualification complete"
Write-Host "Overall: $overallStatus"
Write-Host "Software: $softwareStatus"
Write-Host "Hardware: $hardwareStatus"
Write-Host "Result: $resultPath"
Write-Host "Summary: $summaryPath"

if ($overallStatus -eq "FAIL") {
	throw "Reference-platform qualification contains one or more mandatory FAIL results."
}
if ($RequirePass -and $overallStatus -ne "PASS") {
	throw "Reference-platform qualification requires PASS but completed as '$overallStatus'."
}
