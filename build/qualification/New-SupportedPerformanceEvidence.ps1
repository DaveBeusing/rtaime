# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$SourceCommit,
	[string]$BindingRoot = "artifacts/qualification/bindings",
	[string]$OutputPath = "artifacts/qualification/supported-performance.json",
	[string]$MarkdownPath = "artifacts/qualification/supported-performance.md"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$bindingVerifier = Join-Path $PSScriptRoot "Test-QualificationEvidenceBinding.ps1"
$buildPropsPath = Join-Path $repositoryRoot "Directory.Build.props"

function Resolve-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
	return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Assert-RepositoryPath {
	param([Parameter(Mandatory)][string]$Path)
	$relative = [System.IO.Path]::GetRelativePath($repositoryRoot, [System.IO.Path]::GetFullPath($Path)).Replace('\', '/')
	if ($relative -eq ".." -or $relative.StartsWith("../", [StringComparison]::Ordinal)) {
		throw "Supported-performance evidence must remain inside the repository workspace."
	}
}

function Write-JsonFile {
	param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
	$directory = Split-Path -Parent $Path
	if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
	$json = $Value | ConvertTo-Json -Depth 32
	[System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Add-Measurement {
	param(
		[Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Target,
		[Parameter(Mandatory)][string]$Metric,
		[Parameter(Mandatory)][double]$Value,
		[Parameter(Mandatory)][string]$Unit,
		[Parameter(Mandatory)][string]$QualificationType,
		[Parameter(Mandatory)][string]$PayloadSha256,
		[string]$Format = "",
		[string]$Operation = "",
		[int]$SampleCount = 0
	)
	if (-not [double]::IsFinite($Value)) { throw "Measured value for '$Metric' must be finite." }
	$row = [ordered]@{
		metric = $Metric
		value = $Value
		unit = $Unit
		qualificationType = $QualificationType
		payloadSha256 = $PayloadSha256
	}
	if (-not [string]::IsNullOrWhiteSpace($Format)) { $row.format = $Format }
	if (-not [string]::IsNullOrWhiteSpace($Operation)) { $row.operation = $Operation }
	if ($SampleCount -gt 0) { $row.sampleCount = $SampleCount }
	$Target.Add($row)
}

if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw "SourceCommit must be an exact 40-character Git SHA." }
$SourceCommit = $SourceCommit.ToLowerInvariant()
if (-not (Test-Path -LiteralPath $bindingVerifier -PathType Leaf)) { throw "Qualification binding verifier is missing." }
if (-not (Test-Path -LiteralPath $buildPropsPath -PathType Leaf)) { throw "Directory.Build.props is missing." }

[xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$productVersion = $buildProps.SelectSingleNode("//RtaimeProductVersion")?.InnerText
$releaseStage = $buildProps.SelectSingleNode("//RtaimeReleaseStage")?.InnerText
if ([string]::IsNullOrWhiteSpace($productVersion) -or [string]::IsNullOrWhiteSpace($releaseStage)) {
	throw "Product version and release stage must be declared in Directory.Build.props."
}

$bindingRootFull = Resolve-RepositoryPath -Path $BindingRoot
Assert-RepositoryPath -Path $bindingRootFull
$outputFull = Resolve-RepositoryPath -Path $OutputPath
$markdownFull = Resolve-RepositoryPath -Path $MarkdownPath
Assert-RepositoryPath -Path $outputFull
Assert-RepositoryPath -Path $markdownFull

$measurements = [System.Collections.Generic.List[object]]::new()
$hardware = [ordered]@{}
$verifiedTypes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

if (Test-Path -LiteralPath $bindingRootFull -PathType Container) {
	foreach ($bindingFile in @(Get-ChildItem -LiteralPath $bindingRootFull -Filter "*.binding.json" -File | Sort-Object Name)) {
		$binding = Get-Content -LiteralPath $bindingFile.FullName -Raw | ConvertFrom-Json
		$type = [string]$binding.qualificationType
		& $bindingVerifier -BindingPath $bindingFile.FullName -ExpectedSourceCommit $SourceCommit -ExpectedQualificationType $type | Out-Null
		if (-not $verifiedTypes.Add($type)) { throw "Duplicate qualification binding type '$type'." }

		$payloadPath = Resolve-RepositoryPath -Path ([string]$binding.payload.path)
		$payload = Get-Content -LiteralPath $payloadPath -Raw | ConvertFrom-Json
		$payloadSha = [string]$binding.payload.sha256
		switch ($type) {
			"CUDA_REFERENCE" {
				$hardware.gpuDevice = [string]$payload.detectedDeviceName
				foreach ($case in @($payload.cases)) {
					Add-Measurement -Target $measurements -Metric "gpu.frame_latency.p50" -Value ([double]$case.p50Milliseconds) -Unit "ms" -QualificationType $type -PayloadSha256 $payloadSha -Format ([string]$case.format) -Operation ([string]$case.operation) -SampleCount ([int]$case.samples)
					Add-Measurement -Target $measurements -Metric "gpu.frame_latency.p95" -Value ([double]$case.p95Milliseconds) -Unit "ms" -QualificationType $type -PayloadSha256 $payloadSha -Format ([string]$case.format) -Operation ([string]$case.operation) -SampleCount ([int]$case.samples)
					Add-Measurement -Target $measurements -Metric "gpu.frame_latency.maximum" -Value ([double]$case.maximumMilliseconds) -Unit "ms" -QualificationType $type -PayloadSha256 $payloadSha -Format ([string]$case.format) -Operation ([string]$case.operation) -SampleCount ([int]$case.samples)
				}
			}
			"MEDIA_IO_REFERENCE" {
				$hardware.mediaIoAdapter = [string]$payload.detectedAdapter
				$hardware.mediaIoDriverVersion = [string]$payload.driverVersion
				$hardware.ajaSdkRevision = [string]$payload.ajaSdkRevision
				Add-Measurement -Target $measurements -Metric "media_io.capture_a.frames" -Value ([double]$payload.statistics.CapturedA) -Unit "frames" -QualificationType $type -PayloadSha256 $payloadSha
				Add-Measurement -Target $measurements -Metric "media_io.capture_b.frames" -Value ([double]$payload.statistics.CapturedB) -Unit "frames" -QualificationType $type -PayloadSha256 $payloadSha
				Add-Measurement -Target $measurements -Metric "media_io.output_accepted.frames" -Value ([double]$payload.statistics.OutputAccepted) -Unit "frames" -QualificationType $type -PayloadSha256 $payloadSha
			}
			"TIMING_REFERENCE_SOAK" {
				$hardware.gpuDevice = [string]$payload.telemetry.gpuDeviceName
				$hardware.gpuDriverVersion = [string]$payload.telemetry.gpuDriverVersion
				Add-Measurement -Target $measurements -Metric "media_io.host_cycle.p95" -Value ([double]$payload.hostCycle.p95Milliseconds) -Unit "ms" -QualificationType $type -PayloadSha256 $payloadSha -Format ([string]$payload.format) -SampleCount ([int]$payload.hostCycle.sampleCount)
				Add-Measurement -Target $measurements -Metric "physical.end_to_end_latency.p95" -Value ([double]$payload.physicalEndToEndLatency.p95Milliseconds) -Unit "ms" -QualificationType $type -PayloadSha256 $payloadSha -Format ([string]$payload.format) -SampleCount ([int]$payload.physicalEndToEndLatency.sampleCount)
				Add-Measurement -Target $measurements -Metric "audio_video_sync.absolute_offset.p95" -Value ([double]$payload.audioVideoSynchronization.p95AbsoluteOffsetMilliseconds) -Unit "ms" -QualificationType $type -PayloadSha256 $payloadSha -Format ([string]$payload.format) -SampleCount ([int]$payload.audioVideoSynchronization.sampleCount)
				Add-Measurement -Target $measurements -Metric "telemetry.complete_samples" -Value ([double]$payload.telemetry.completeSampleCount) -Unit "samples" -QualificationType $type -PayloadSha256 $payloadSha -SampleCount ([int]$payload.telemetry.sampleCount)
				Add-Measurement -Target $measurements -Metric "soak.duration" -Value ([double]$payload.soakSeconds) -Unit "s" -QualificationType $type -PayloadSha256 $payloadSha -Format ([string]$payload.format)
				Add-Measurement -Target $measurements -Metric "media_io.output_accepted.frames" -Value ([double]$payload.statistics.OutputAccepted) -Unit "frames" -QualificationType $type -PayloadSha256 $payloadSha -Format ([string]$payload.format)
			}
			default {
				throw "Unsupported qualification type '$type'."
			}
		}
	}
}

$requiredTypes = @("CUDA_REFERENCE", "MEDIA_IO_REFERENCE", "TIMING_REFERENCE_SOAK")
$status = if (@($requiredTypes | Where-Object { -not $verifiedTypes.Contains($_) }).Count -eq 0) { "PASS" } else { "UNVERIFIED" }
$orderedMeasurements = @($measurements | Sort-Object metric, format, operation, qualificationType)

$evidence = [ordered]@{
	copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>."
	schemaVersion = "1.0"
	profile = "rtaime-v1-reference-platform"
	sourceCommit = $SourceCommit
	product = [ordered]@{
		version = $productVersion
		releaseStage = $releaseStage
	}
	generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
	status = $status
	hardware = $hardware
	measurements = $orderedMeasurements
}
Write-JsonFile -Value $evidence -Path $outputFull

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->")
$lines.Add("")
$lines.Add("# Supported Performance Matrix")
$lines.Add("")
$lines.Add("- Profile: ``rtaime-v1-reference-platform``")
$lines.Add("- Source commit: ``$SourceCommit``")
$lines.Add("- Product: ``$productVersion`` / ``$releaseStage``")
$lines.Add("- Evidence status: **$status**")
$lines.Add("")
$lines.Add("> Only values measured by verified physical qualification payloads are listed. Missing physical evidence produces no performance value and never creates a supported claim.")
$lines.Add("")
if ($orderedMeasurements.Count -eq 0) {
	$lines.Add("No source-bound physical performance measurements are available for this source commit.")
} else {
	$lines.Add("| Metric | Format | Operation | Value | Samples | Evidence |")
	$lines.Add("| --- | --- | --- | ---: | ---: | --- |")
	foreach ($row in $orderedMeasurements) {
		$format = if ($row.Contains("format")) { [string]$row.format } else { "-" }
		$operation = if ($row.Contains("operation")) { [string]$row.operation } else { "-" }
		$samples = if ($row.Contains("sampleCount")) { [string]$row.sampleCount } else { "-" }
		$lines.Add("| $($row.metric) | $format | $operation | $($row.value) $($row.unit) | $samples | ``$($row.qualificationType)`` |")
	}
}
$directory = Split-Path -Parent $markdownFull
if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
[System.IO.File]::WriteAllLines($markdownFull, $lines, [System.Text.UTF8Encoding]::new($false))

Write-Host "Supported performance evidence generated"
Write-Host "Status: $status"
Write-Host "Measurements: $($orderedMeasurements.Count)"
Write-Host "JSON: $outputFull"
Write-Host "Markdown: $markdownFull"
