# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$LibraryPath,
	[string]$OutputDirectory = "artifacts/qualification/srt-native"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
if (-not $IsWindows) { throw "Native SRT/MF qualification requires Windows x64." }
if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
	throw "Native SRT/MF qualification requires an x64 process."
}
$nativeLibrary = (Resolve-Path -LiteralPath $LibraryPath).Path
if (-not (Test-Path -LiteralPath $nativeLibrary -PathType Leaf)) { throw "Supply the installed SRT 1.5.7-or-newer DLL." }
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$expectedFiles = @("srt-native-1080p50.json", "srt-native-1080p59.94.json")
foreach ($name in $expectedFiles) {
	if (Test-Path -LiteralPath (Join-Path $outputRoot $name)) { throw "Qualification output already exists; use a fresh OutputDirectory." }
}
$source = (git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $source -notmatch '^[0-9a-f]{40}$') { throw "Unable to resolve source commit." }
$dirty = @(git -C $repositoryRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw "Source-bound native qualification requires a clean checkout." }
if ((dotnet --version).Trim() -ne "10.0.401") { throw "Expected .NET SDK 10.0.401." }
$nativeHash = (Get-FileHash -LiteralPath $nativeLibrary -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$values = @{
	RTAIME_SRT_NATIVE_QUALIFICATION = "1"
	RTAIME_SRT_NATIVE_LIBRARY = $nativeLibrary
	RTAIME_SRT_NATIVE_EVIDENCE = $outputRoot
	RTAIME_SRT_NATIVE_SOURCE = $source
}
$previous = @{}
foreach ($name in $values.Keys) {
	$previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
	[Environment]::SetEnvironmentVariable($name, $values[$name], "Process")
}
Push-Location $repositoryRoot
try {
	dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release `
		--filter FullyQualifiedName~SrtNativeLoopbackQualificationTests --blame-hang-timeout 5m `
		--results-directory $outputRoot --logger "trx;LogFileName=srt-native.trx" --logger "console;verbosity=detailed"
	if ($LASTEXITCODE -ne 0) { throw "Native SRT/MF loopback failed; no peer qualification PASS may be claimed." }
	foreach ($name in $expectedFiles) {
		$path = Join-Path $outputRoot $name
		if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Native qualification evidence is missing: $name" }
		$evidence = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
		if ($evidence.status -ne "PASS" -or $evidence.sourceCommit -ne $source -or $evidence.nativeLibrarySha256 -ne $nativeHash) {
			throw "Native qualification evidence has stale or incorrect source/runtime identity: $name"
		}
		if ([Convert]::ToUInt32($evidence.runtimeVersion.Substring(2), 16) -lt 0x010507 -or
			$evidence.maximumEncoderDrain -le 1316 -or $evidence.maximumMessage -gt 1316 -or
			$evidence.messagesReceived -le 0 -or $evidence.encodedBytes -le 1316 -or
			$evidence.payloadSha256 -notmatch '^[0-9A-F]{64}$') {
			throw "Native qualification is incomplete: $name"
		}
	}
	Write-Host "Native SRT/MF local loopback PASS at $source. Evidence: $outputRoot"
	Write-Host "External receiver decode, WAN, encryption and physical latency remain UNVERIFIED."
}
finally {
	Pop-Location
	foreach ($name in $values.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], "Process") }
}
