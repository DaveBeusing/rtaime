# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputDirectory = "artifacts/testmedia/reference",
	[string]$FfmpegPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
	[System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
	[System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$previousRegression = $env:RTAIME_REFERENCE_MEDIA_REGRESSION
$previousOutput = $env:RTAIME_REFERENCE_MEDIA_OUTPUT
$previousFfmpeg = $env:RTAIME_FFMPEG_PATH

try {
	$env:RTAIME_REFERENCE_MEDIA_REGRESSION = "1"
	$env:RTAIME_REFERENCE_MEDIA_OUTPUT = $outputRoot
	if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) {
		$env:RTAIME_FFMPEG_PATH = [System.IO.Path]::GetFullPath($FfmpegPath)
	}

	Push-Location $repositoryRoot
	try {
		$arguments = @(
			"test",
			"tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj",
			"--configuration",
			"Release",
			"--filter",
			"FullyQualifiedName~ReferenceMediaGenerationTests.Generate_required_reference_media_profiles_and_manifests"
		)
		& dotnet @arguments
		if ($LASTEXITCODE -ne 0) {
			throw "Reference media generation test failed with exit code $LASTEXITCODE."
		}
	}
	finally {
		Pop-Location
	}
}
finally {
	$env:RTAIME_REFERENCE_MEDIA_REGRESSION = $previousRegression
	$env:RTAIME_REFERENCE_MEDIA_OUTPUT = $previousOutput
	$env:RTAIME_FFMPEG_PATH = $previousFfmpeg
}

$manifests = @(Get-ChildItem -LiteralPath $outputRoot -Filter "rtaime-reference-*.json" -File)
if ($manifests.Count -ne 3) {
	throw "Expected exactly three required reference-media manifests, found $($manifests.Count)."
}

foreach ($manifestFile in $manifests | Sort-Object Name) {
	$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
	$mediaPath = [System.IO.Path]::ChangeExtension($manifestFile.FullName, ".mp4")
	if (-not (Test-Path -LiteralPath $mediaPath -PathType Leaf)) {
		throw "Generated media file is missing for '$($manifestFile.Name)'."
	}

	$actual = (Get-FileHash -LiteralPath $mediaPath -Algorithm SHA256).Hash.ToLowerInvariant()
	if ($actual -ne ([string]$manifest.sha256).ToLowerInvariant()) {
		throw "Generated media checksum does not match manifest for '$($manifestFile.Name)'."
	}

	$size = (Get-Item -LiteralPath $mediaPath).Length
	Write-Host "$($manifest.profile): $mediaPath"
	Write-Host "  native rate: $($manifest.nativeFrameRate)"
	Write-Host "  duration: $($manifest.requestedDurationSeconds) s"
	Write-Host "  size: $size bytes"
	Write-Host "  sha256: $actual"
}

Write-Host "Reference media generation PASS"
Write-Host "Output: $outputRoot"
