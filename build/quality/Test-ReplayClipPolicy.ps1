# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$contractsPath = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/ReplayContracts.cs"
$capturePath = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/ReplayCaptureEngine.cs"
$storePath = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/RollingReplaySegmentStore.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeReplayService.cs"
$materializerPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/ReplayClipMaterializer.cs"
$controlPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ReplayControlService.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ReplayViewModel.cs"
$docPath = Join-Path $repositoryRoot "docs/ReplayClipProduction.md"
$unitTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/ReplaySegmentStoreTests.cs"
$operatorTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Operator/ReplayOperatorTests.cs"

foreach ($path in @($contractsPath,$capturePath,$storePath,$runtimePath,$materializerPath,$controlPath,$operatorPath,$docPath,$unitTestsPath,$operatorTestsPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Replay/Clip artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$capture = Get-Content -LiteralPath $capturePath -Raw
$store = Get-Content -LiteralPath $storePath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$materializer = Get-Content -LiteralPath $materializerPath -Raw
$control = Get-Content -LiteralPath $controlPath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw
$doc = Get-Content -LiteralPath $docPath -Raw

Assert-Condition ($contracts -match 'ReplayBufferPolicy' -and $contracts -match 'MaximumStorageBytes' -and $contracts -match 'RetentionDuration') "Replay retention must remain explicitly bounded."
Assert-Condition ($capture -match 'DefaultQueueCapacity' -and $capture -match 'queue_full' -and $capture -match 'discontinuity') "Replay capture must retain bounded backpressure and discontinuity evidence."
Assert-Condition ($store -match 'PinRange' -and $store -match 'EvictUnsafe' -and $store -match 'MaximumStorageBytes') "Replay encoded retention must protect pinned ranges and enforce storage bounds."
Assert-Condition ($capture -notmatch 'List<byte\[\]>' -and $store -notmatch 'RGBA|RgbaPixels') "Replay must not introduce an unbounded raw full-resolution history ring."
Assert-Condition ($runtime -match 'ReplayCaptureEngine' -and $materializer -match 'LocalMediaFileProvider') "RuntimeHost must own capture while materialized clips reuse the existing media capability."
Assert-Condition ($materializer -match 'WindowsMediaFoundationMp4RecordingWriter' -and $materializer -match 'SHA256') "Replay clips must use the shared encoded MP4 path and publish integrity evidence."
Assert-Condition ($control -match 'MediaAssetCatalogService' -and $control -match 'ImportAsync') "Valid replay clips must enter the normal Media Library through ControlHost."
Assert-Condition ($operator -match 'OpenCatalogAssetAsync' -and $operator -match 'SelectPreviewAsync') "Replay playback must reuse Media Deck and normal Preview authority."
Assert-Condition ($operator -notmatch 'RuntimeHost|Provider\.') "Replay Operator must not call RuntimeHost or providers directly."
Assert-Condition ($doc -match 'There is no direct replay-to-Program path' -and $doc -match 'normal-speed only') "Replay documentation must preserve playback and V1 scope boundaries."

Write-Host "Replay and Clip Production policy PASS"
