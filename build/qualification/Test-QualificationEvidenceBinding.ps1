# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$BindingPath,
	[string]$ExpectedSourceCommit,
	[string]$ExpectedQualificationType
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

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { throw "Qualification evidence policy is missing." }
if (-not (Test-Path -LiteralPath $buildPropsPath -PathType Leaf)) { throw "Directory.Build.props is missing." }

$resolvedBinding = Resolve-RepositoryPath -Path $BindingPath
if (-not (Test-Path -LiteralPath $resolvedBinding -PathType Leaf)) { throw "Qualification evidence binding is missing: '$resolvedBinding'." }
$binding = Get-Content -LiteralPath $resolvedBinding -Raw | ConvertFrom-Json
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json

Assert-Condition ([string]$binding.schemaVersion -eq "1.0") "Qualification evidence binding schemaVersion must be 1.0."
Assert-Condition ([string]$binding.repository -eq [string]$policy.repository) "Qualification evidence repository identity does not match policy."
Assert-Condition ([string]$binding.status -eq "PASSED") "Only PASSED qualification evidence may be bound into release evidence."
Assert-Condition ([string]$binding.sourceCommit -match '^[0-9a-f]{40}$') "Qualification binding sourceCommit must be an exact lowercase Git SHA."
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$binding.workflow.runId)) "Qualification binding workflow runId is required."
Assert-Condition ([int]$binding.workflow.runAttempt -gt 0) "Qualification binding workflow runAttempt must be positive."
Assert-Condition ([string]$binding.payload.sha256 -match '^[0-9a-f]{64}$') "Qualification binding payload SHA-256 is invalid."

if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceCommit)) {
	Assert-Condition ($ExpectedSourceCommit -match '^[0-9a-fA-F]{40}$') "ExpectedSourceCommit must be an exact Git SHA."
	Assert-Condition ([string]$binding.sourceCommit -eq $ExpectedSourceCommit.ToLowerInvariant()) "Qualification binding source commit does not match the expected release source commit."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedQualificationType)) {
	Assert-Condition ([string]$binding.qualificationType -eq $ExpectedQualificationType) "Qualification binding type does not match the expected qualification type."
}

$bindingPolicy = @($policy.bindings | Where-Object { [string]$_.qualificationType -eq [string]$binding.qualificationType })
Assert-Condition ($bindingPolicy.Count -eq 1) "Qualification binding type is not uniquely declared by policy."
$bindingPolicy = $bindingPolicy[0]

[xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$productVersion = $buildProps.SelectSingleNode("//RtaimeProductVersion")?.InnerText
$releaseStage = $buildProps.SelectSingleNode("//RtaimeReleaseStage")?.InnerText
Assert-Condition (-not [string]::IsNullOrWhiteSpace($productVersion)) "RtaimeProductVersion is missing."
Assert-Condition (-not [string]::IsNullOrWhiteSpace($releaseStage)) "RtaimeReleaseStage is missing."
Assert-Condition ([string]$binding.product.version -eq $productVersion) "Qualification binding product version does not match the source tree."
Assert-Condition ([string]$binding.product.releaseStage -eq $releaseStage) "Qualification binding release stage does not match the source tree."

$requirements = @($binding.releaseRequirements)
$policyRequirements = @($bindingPolicy.releaseRequirements)
Assert-Condition ($requirements.Count -eq $policyRequirements.Count) "Qualification binding release-requirement count differs from policy."
foreach ($requirement in $policyRequirements) {
	Assert-Condition ($requirements -contains [string]$requirement) "Qualification binding is missing release requirement '$requirement'."
}

$payloadRelative = [string]$binding.payload.path
Assert-Condition (-not [System.IO.Path]::IsPathRooted($payloadRelative)) "Qualification payload path must remain repository-relative."
$resolvedPayload = Resolve-RepositoryPath -Path $payloadRelative
$relativeCheck = [System.IO.Path]::GetRelativePath($repositoryRoot, $resolvedPayload).Replace('\', '/')
Assert-Condition ($relativeCheck -ne ".." -and -not $relativeCheck.StartsWith("../", [StringComparison]::Ordinal)) "Qualification payload escapes the repository workspace."
Assert-Condition (Test-Path -LiteralPath $resolvedPayload -PathType Leaf) "Qualification payload referenced by the binding is missing."
$payloadHash = (Get-FileHash -LiteralPath $resolvedPayload -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Condition ($payloadHash -eq [string]$binding.payload.sha256) "Qualification payload SHA-256 does not match its binding."

$payload = Get-Content -LiteralPath $resolvedPayload -Raw | ConvertFrom-Json
Assert-Condition ([string]$payload.schemaVersion -eq [string]$bindingPolicy.payloadSchemaVersion) "Qualification payload schema does not match policy."
Assert-Condition ([string]$payload.schemaVersion -eq [string]$binding.payload.schemaVersion) "Qualification payload schema does not match the binding."
Assert-Condition ([string]$payload.status -eq "PASSED") "Qualification payload is not PASSED."

switch ([string]$binding.qualificationType) {
	"CUDA_REFERENCE" {
		Assert-Condition (@($payload.cases).Count -eq 8) "CUDA qualification requires exactly eight retained cases."
		Assert-Condition (@($payload.failures).Count -eq 0) "CUDA qualification contains failures."
		foreach ($case in @($payload.cases)) {
			Assert-Condition ([bool]$case.pixelCorrect) "CUDA qualification contains a failed pixel check."
			Assert-Condition ([bool]$case.surfaceLifetimeCorrect) "CUDA qualification contains a failed surface-lifetime check."
			Assert-Condition ([bool]$case.timingBudgetMet) "CUDA qualification contains a failed timing-budget check."
		}
	}
	"MEDIA_IO_REFERENCE" {
		Assert-Condition ([string]$payload.ajaSdkRevision -match '^[0-9a-f]{40}$') "Media I/O qualification does not identify an exact AJA SDK commit."
		Assert-Condition ([string]$payload.transferMode -eq "PinnedHostLease") "Media I/O qualification did not prove PinnedHostLease."
		Assert-Condition ([uint64]$payload.statistics.CaptureFailures -eq 0) "Media I/O qualification recorded capture failures."
		Assert-Condition ([uint64]$payload.statistics.OutputRejected -eq 0) "Media I/O qualification recorded hard Program-output failures."
	}
	"TIMING_REFERENCE_SOAK" {
		Assert-Condition ([int]$payload.soakSeconds -ge 1800) "Timing qualification does not contain the required long soak."
		Assert-Condition ([bool]$payload.reference.referenceLossObserved) "Timing qualification lacks reference-loss evidence."
		Assert-Condition ([bool]$payload.reference.referenceRelockObserved) "Timing qualification lacks reference re-lock evidence."
		Assert-Condition ([string]$payload.reference.finalOutput -eq "Locked") "Timing qualification did not finish with locked Program output."
		Assert-Condition ([string]$payload.physicalEndToEndLatency.status -eq "PASSED") "Timing qualification lacks PASSED physical end-to-end latency evidence."
		Assert-Condition ([int]$payload.physicalEndToEndLatency.sampleCount -ge 30) "Timing qualification has too few physical latency samples."
	}
	default {
		throw "Unsupported qualification type '$($binding.qualificationType)'."
	}
}

Write-Host "Qualification evidence binding verification PASS"
Write-Host "Type: $($binding.qualificationType)"
Write-Host "Source commit: $($binding.sourceCommit)"
Write-Host "Payload SHA256: $payloadHash"
