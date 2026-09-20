# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$decoderPath = Join-Path $repositoryRoot "src/Media/rtaime.Media/WindowsMediaFoundationLocalMediaDecoder.cs"
$bufferPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs"
$runtimeServicePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$runtimeProcessPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs"
$controlIpcPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ControlHostIpcServer.cs"
$operatorViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorViewModel.cs"
$mediaDeckViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaDeckViewModel.cs"
$sourcePath = Join-Path $repositoryRoot "src/Media/rtaime.Media/LocalMediaFileSource.cs"
$integrationTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/LocalMediaRuntimeIntegrationTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/LocalMediaFileSource.md"
$referenceRegressionTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ReferenceMediaRegressionTests.cs"
$referenceGeneratorPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ReferenceMediaTestAsset.cs"
$referenceDocumentationPath = Join-Path $repositoryRoot "docs/ReferenceMediaRegression.md"
$referenceRegressionScriptPath = Join-Path $repositoryRoot "build/testmedia/Test-ReferenceMediaRegression.ps1"

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

foreach ($path in @($decoderPath, $bufferPath, $runtimeServicePath, $runtimeProcessPath, $controlIpcPath, $operatorViewModelPath, $mediaDeckViewModelPath, $sourcePath, $integrationTestsPath, $documentationPath, $referenceRegressionTestsPath, $referenceGeneratorPath, $referenceDocumentationPath, $referenceRegressionScriptPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required sustained local-media artifact is missing: '$path'."
}

$decoder = Get-Content -LiteralPath $decoderPath -Raw
$buffer = Get-Content -LiteralPath $bufferPath -Raw
$runtimeService = Get-Content -LiteralPath $runtimeServicePath -Raw
$runtimeProcess = Get-Content -LiteralPath $runtimeProcessPath -Raw
$controlIpc = Get-Content -LiteralPath $controlIpcPath -Raw
$operatorViewModel = Get-Content -LiteralPath $operatorViewModelPath -Raw
$mediaDeckViewModel = Get-Content -LiteralPath $mediaDeckViewModelPath -Raw
$source = Get-Content -LiteralPath $sourcePath -Raw
$integrationTests = Get-Content -LiteralPath $integrationTestsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$referenceRegressionTests = Get-Content -LiteralPath $referenceRegressionTestsPath -Raw
$referenceGenerator = Get-Content -LiteralPath $referenceGeneratorPath -Raw
$referenceDocumentation = Get-Content -LiteralPath $referenceDocumentationPath -Raw
$referenceRegressionScript = Get-Content -LiteralPath $referenceRegressionScriptPath -Raw

Assert-Condition ($decoder -match 'MfSourceReaderEnableAdvancedVideoProcessing') "Local MP4 playback must retain Media Foundation advanced video processing."
Assert-Condition ($decoder -match 'MfVideoFormatRgb32') "Local MP4 playback must normalize decoded video to RGB32 before the managed runtime boundary."
Assert-Condition ($decoder -match 'private readonly byte\[\] _rgbaFrameBuffer') "Local MP4 decoding must reuse one decoder-owned video frame buffer."
Assert-Condition ($decoder -match 'Lock2D' -and $decoder -match 'pitch') "RGB32 extraction must honor Media Foundation 2D buffer pitch."
Assert-Condition ($decoder -notmatch 'ConvertNv12ToRgba') "Local MP4 playback must not restore the per-pixel managed NV12-to-RGBA conversion."
Assert-Condition ($runtimeService -match 'SetExternalInputContent\(MediaSourceId sourceId, ReadOnlySpan<byte> rgbaPixels') "RuntimeHost must accept decoded local-media pixels without allocating a replacement frame object."
Assert-Condition ($buffer -match 'CopyPixelsFrom\(ReadOnlySpan<byte> pixels\)') "Runtime-owned RGBA buffers must support in-place pixel updates."
Assert-Condition ($runtimeProcess -notmatch 'new RgbaFrameBuffer\(runtime\.Format, boundary\.RgbaPixels\.Span\)') "Media-deck playback must not allocate a second full RGBA frame at the RuntimeHost handoff."
Assert-Condition ($controlIpc -match 'WireMediaDeckSnapshot MediaDeck') "The normal Operator snapshot must carry the already observed confirmed media-deck state."
Assert-Condition ($operatorViewModel -match 'ApplyEmbeddedMediaDeckSnapshot\(snapshot\.MediaDeck\)') "Operator synchronization must project the embedded media-deck state."
Assert-Condition ($mediaDeckViewModel -notmatch 'PeriodicTimer') "MediaDeckViewModel must not create a second polling timer alongside Operator synchronization."
Assert-Condition ($source -match 'for \(var attempt = 0; attempt < 2; attempt\+\+\)') "Local media decoding must tolerate one transient decoder read failure before entering ERROR."
Assert-Condition ($integrationTests -match 'targetFrames = 250') "Integration coverage must exercise at least 250 decoded MP4 frames across repeated playback cycles."
Assert-Condition ($documentation -match 'reusable' -and $documentation -match 'RGB32') "Local media documentation must describe reusable sustained-playback buffering and RGB32 normalization."
Assert-Condition ($referenceGenerator -match 'BroadcastTestPatternGenerator' -and $referenceGenerator -match 'MotionTimingTestSignalGenerator' -and $referenceGenerator -match 'GeneratedAudioTestSignalGenerator') "Reference media must be derived from the internal video, timing and audio test generators."
Assert-Condition ($referenceGenerator -match 'hd25' -and $referenceGenerator -match 'hd50' -and $referenceGenerator -match 'hd5994') "Reference media generator must retain the 25/50/59.94 required profile matrix."
Assert-Condition ($referenceRegressionTests -match 'Six_second_reference_stays_playing_past_five_seconds' -and $referenceRegressionTests -match '640') "Reference media regression must cover playback beyond five seconds and repeated loop boundaries."
Assert-Condition ($referenceRegressionTests -match 'Encoded_reference_preserves_visual_sync_flash_and_audio_pulse') "Reference media regression must verify encoded/decoded A/V sync-event evidence."
Assert-Condition ($referenceDocumentation -match 'hd25' -and $referenceDocumentation -match 'hd50' -and $referenceDocumentation -match 'hd5994' -and $referenceDocumentation -match 'SHA-256') "Reference media documentation must describe profiles and integrity evidence."
Assert-Condition ($referenceRegressionScript -match 'RTAIME_REFERENCE_MEDIA_REGRESSION' -and $referenceRegressionScript -match '20MB') "Reference media regression command must enable generated tests and bound CI artifact size."

Write-Host "Sustained local-media playback policy verification PASS"
Write-Host "Video decode: Media Foundation RGB32"
Write-Host "Full-frame managed allocation churn: prohibited on steady-state handoff"
Write-Host "Generated reference media: 1080p25 / 1080p50 / 1080p59.94 H.264+Aac"
Write-Host "Offline/freeze regression boundary: playback remains healthy beyond five seconds"
