# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$QualificationType,
	[Parameter(Mandatory)][string]$EvidencePath,
	[Parameter(Mandatory)][string]$SourceCommit,
	[Parameter(Mandatory)][string]$RunId,
	[Parameter(Mandatory)][int]$RunAttempt,
	[string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$policyPath = Join-Path $PSScriptRoot "qualification-evidence-policy.json"
$buildPropsPath = Join-Path $repositoryRoot "Directory.Build.props"

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) {
		return [System.IO.Path]::GetFullPath($Path)
	}
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Get-RepositoryRelativePath {
	param([Parameter(Mandatory)][string]$Path)
	$relative = [System.IO.Path]::GetRelativePath($repositoryRoot, $Path).Replace('\', '/')
	if ($relative -eq ".." -or $relative.StartsWith("../", [StringComparison]::Ordinal)) {
		throw "Qualification evidence must be stored inside the repository workspace."
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
	$json = $Value | ConvertTo-Json -Depth 32
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Assert-PassedPayload {
	param(
		[Parameter(Mandatory)][string]$Type,
		[Parameter(Mandatory)]$Evidence
	)

	if ([string]$Evidence.status -ne "PASSED") {
		throw "Qualification payload status is '$($Evidence.status)', expected PASSED."
	}

	switch ($Type) {
		"CUDA_REFERENCE" {
			$cases = @($Evidence.cases)
			if ($cases.Count -ne 8) { throw "CUDA qualification binding requires exactly eight cases." }
			if (@($Evidence.failures).Count -ne 0) { throw "CUDA qualification binding rejects payloads with failures." }
			foreach ($case in $cases) {
				if (-not [bool]$case.pixelCorrect -or -not [bool]$case.surfaceLifetimeCorrect -or -not [bool]$case.timingBudgetMet) {
					throw "CUDA qualification binding rejects an incomplete functional/lifetime/timing case."
				}
			}
		}
		"MEDIA_IO_REFERENCE" {
			if ([string]$Evidence.ajaSdkRevision -notmatch '^[0-9a-f]{40}$') { throw "Media I/O qualification must identify an exact AJA SDK commit." }
			if ([string]$Evidence.transferMode -ne "PinnedHostLease") { throw "Media I/O qualification must prove PinnedHostLease." }
			if ([uint64]$Evidence.statistics.CaptureFailures -ne 0) { throw "Media I/O qualification recorded capture failures." }
			if ([uint64]$Evidence.statistics.OutputRejected -ne 0) { throw "Media I/O qualification recorded hard Program-output failures." }
		}
		"TIMING_REFERENCE_SOAK" {
			if ([int]$Evidence.soakSeconds -lt 1800) { throw "Timing qualification binding requires at least 1800 seconds of soak evidence." }
			if (-not [bool]$Evidence.reference.referenceLossObserved) { throw "Timing qualification binding requires reference-loss evidence." }
			if (-not [bool]$Evidence.reference.referenceRelockObserved) { throw "Timing qualification binding requires reference re-lock evidence." }
			if ([string]$Evidence.reference.finalOutput -ne "Locked") { throw "Timing qualification binding requires a final locked Program output." }
			if ([uint64]$Evidence.statistics.CaptureFailures -ne 0) { throw "Timing qualification binding rejects capture failures during soak." }
			if ([uint64]$Evidence.statistics.OutputRejected -ne 0) { throw "Timing qualification binding rejects hard Program-output failures during soak." }
			if ([uint64]$Evidence.statistics.CapturedA -lt [uint64]$Evidence.minimumContinuityFrames -or [uint64]$Evidence.statistics.CapturedB -lt [uint64]$Evidence.minimumContinuityFrames -or [uint64]$Evidence.statistics.OutputAccepted -lt [uint64]$Evidence.minimumContinuityFrames) { throw "Timing qualification binding requires sustained capture and Program-output cadence." }
			if ([int]$Evidence.hostCycle.sampleCount -lt 10 -or [double]$Evidence.hostCycle.p95Milliseconds -gt [double]$Evidence.hostCycle.maximumAllowedP95Milliseconds) { throw "Timing qualification binding requires measured host-cycle timing within its declared bound." }
			if ([string]$Evidence.telemetry.status -ne "PASSED" -or [int]$Evidence.telemetry.sampleCount -lt 30 -or [int]$Evidence.telemetry.completeSampleCount -ne [int]$Evidence.telemetry.sampleCount) { throw "Timing qualification binding requires continuous CPU/RAM/GPU/VRAM telemetry." }
			if ([string]::IsNullOrWhiteSpace([string]$Evidence.telemetry.gpuDriverVersion)) { throw "Timing qualification binding requires NVIDIA driver identity." }
			if ([string]$Evidence.physicalEndToEndLatency.status -ne "PASSED") { throw "Timing qualification binding requires PASSED physical end-to-end latency evidence." }
			if ([int]$Evidence.physicalEndToEndLatency.sampleCount -lt 30) { throw "Timing qualification binding requires at least 30 physical latency samples." }
			if ([string]$Evidence.audioVideoSynchronization.status -ne "PASSED") { throw "Timing qualification binding requires PASSED physical A/V synchronization evidence." }
			if ([int]$Evidence.audioVideoSynchronization.sampleCount -lt 30) { throw "Timing qualification binding requires at least 30 physical A/V synchronization samples." }
		}
		default {
			throw "Unsupported qualification type '$Type'."
		}
	}
}

if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw "Qualification evidence policy is missing." }
if (-not (Test-Path -LiteralPath $buildPropsPath -PathType Leaf)) { throw "Directory.Build.props is missing." }
if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw "SourceCommit must be an exact 40-character Git SHA." }
if ([string]::IsNullOrWhiteSpace($RunId)) { throw "RunId is required." }
if ($RunAttempt -le 0) { throw "RunAttempt must be positive." }

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$bindingPolicy = @($policy.bindings | Where-Object { [string]$_.qualificationType -eq $QualificationType })
if ($bindingPolicy.Count -ne 1) { throw "Qualification type '$QualificationType' is not uniquely declared by policy." }
$bindingPolicy = $bindingPolicy[0]

$resolvedEvidence = Resolve-RepositoryPath -Path $EvidencePath
if (-not (Test-Path -LiteralPath $resolvedEvidence -PathType Leaf)) { throw "Qualification evidence is missing: '$resolvedEvidence'." }
$relativeEvidence = Get-RepositoryRelativePath -Path $resolvedEvidence
$evidence = Get-Content -LiteralPath $resolvedEvidence -Raw | ConvertFrom-Json
if ([string]$evidence.schemaVersion -ne [string]$bindingPolicy.payloadSchemaVersion) {
	throw "Qualification payload schema '$($evidence.schemaVersion)' does not match policy '$($bindingPolicy.payloadSchemaVersion)'."
}
Assert-PassedPayload -Type $QualificationType -Evidence $evidence

[xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$productVersion = $buildProps.SelectSingleNode("//RtaimeProductVersion")?.InnerText
$releaseStage = $buildProps.SelectSingleNode("//RtaimeReleaseStage")?.InnerText
if ([string]::IsNullOrWhiteSpace($productVersion) -or [string]::IsNullOrWhiteSpace($releaseStage)) {
	throw "Product version and release stage must be declared in Directory.Build.props."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
	$fileName = $QualificationType.ToLowerInvariant().Replace('_', '-') + ".binding.json"
	$OutputPath = Join-Path "artifacts/qualification/bindings" $fileName
}
$resolvedOutput = Resolve-RepositoryPath -Path $OutputPath
[void](Get-RepositoryRelativePath -Path $resolvedOutput)

$payloadHash = (Get-FileHash -LiteralPath $resolvedEvidence -Algorithm SHA256).Hash.ToLowerInvariant()
$binding = [ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	repository = [string]$policy.repository
	qualificationType = $QualificationType
	status = "PASSED"
	product = [ordered]@{
		version = $productVersion
		releaseStage = $releaseStage
	}
	sourceCommit = $SourceCommit.ToLowerInvariant()
	workflow = [ordered]@{
		runId = $RunId.Trim()
		runAttempt = $RunAttempt
	}
	payload = [ordered]@{
		path = $relativeEvidence
		schemaVersion = [string]$evidence.schemaVersion
		sha256 = $payloadHash
	}
	releaseRequirements = @($bindingPolicy.releaseRequirements)
	generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}

Write-JsonFile -Value $binding -Path $resolvedOutput
Write-Host "Qualification evidence binding PASS"
Write-Host "Type: $QualificationType"
Write-Host "Source commit: $($binding.sourceCommit)"
Write-Host "Payload SHA256: $payloadHash"
Write-Host "Binding: $resolvedOutput"
