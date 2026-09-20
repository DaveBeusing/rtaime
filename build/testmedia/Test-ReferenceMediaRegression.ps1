# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputDirectory = "artifacts/testmedia/reference-regression"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($null -eq $ffmpeg) {
	throw "FFmpeg is required for reference-media regression. Install the pinned CI test encoder or set PATH accordingly."
}

Write-Host "FFmpeg: $($ffmpeg.Source)"
& $ffmpeg.Source -version | Select-Object -First 1

$previousRegression = $env:RTAIME_REFERENCE_MEDIA_REGRESSION
$previousOutput = $env:RTAIME_REFERENCE_MEDIA_OUTPUT
try {
	$env:RTAIME_REFERENCE_MEDIA_REGRESSION = "1"
	$env:RTAIME_REFERENCE_MEDIA_OUTPUT = $outputRoot

	Push-Location $repositoryRoot
	try {
		$arguments = @(
			"test",
			"tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj",
			"--configuration",
			"Release",
			"--filter",
			"FullyQualifiedName~ReferenceMediaRegressionTests|FullyQualifiedName~ReferenceMediaGenerationTests"
		)
		& dotnet @arguments
		if ($LASTEXITCODE -ne 0) {
			throw "Reference-media regression failed with exit code $LASTEXITCODE."
		}
	}
	finally {
		Pop-Location
	}
}
finally {
	$env:RTAIME_REFERENCE_MEDIA_REGRESSION = $previousRegression
	$env:RTAIME_REFERENCE_MEDIA_OUTPUT = $previousOutput
}

$media = @(Get-ChildItem -LiteralPath $outputRoot -Filter "rtaime-reference-*.mp4" -File)
$manifests = @(Get-ChildItem -LiteralPath $outputRoot -Filter "rtaime-reference-*.json" -File)
if ($media.Count -ne 3 -or $manifests.Count -ne 3) {
	throw "Reference-media regression expected three MP4 files and three manifests."
}

foreach ($file in $media) {
	if ($file.Length -gt 20MB) {
		throw "Reference asset '$($file.Name)' exceeds the 20 MiB CI artifact limit."
	}
}

Write-Host "Reference media regression PASS"
Write-Host "Generated profiles: hd25, hd50, hd5994"
