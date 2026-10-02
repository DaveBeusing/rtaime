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

$catalogPath = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/ProfessionalRecordingFormats.cs"
$providersPath = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/RecordingWriterProviders.cs"
$contractsPath = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/RecordingContracts.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$runtimeIpcPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostIpcServer.cs"
$controlTransportPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/RuntimeHostIpcTransport.cs"
$clientPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/OperatorControlClient.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorViewModel.cs"
$operatorSurfacePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MainWindow.xaml"
$unitTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/ProfessionalRecordingFormatTests.cs"
$operatorTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Operator/RecordingProfileOperatorTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/RecordingProfileCatalog.md"
$capabilityPath = Join-Path $repositoryRoot "docs/qualification/RecordingCapabilityCatalog.json"

foreach ($path in @(
	$catalogPath,$providersPath,$contractsPath,$runtimePath,$runtimeIpcPath,$controlTransportPath,
	$clientPath,$operatorPath,$operatorSurfacePath,$unitTestsPath,$operatorTestsPath,$documentationPath,$capabilityPath
)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Recording profile artifact is missing: '$path'."
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw
$providers = Get-Content -LiteralPath $providersPath -Raw
$contracts = Get-Content -LiteralPath $contractsPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$runtimeIpc = Get-Content -LiteralPath $runtimeIpcPath -Raw
$controlTransport = Get-Content -LiteralPath $controlTransportPath -Raw
$client = Get-Content -LiteralPath $clientPath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw
$operatorSurface = Get-Content -LiteralPath $operatorSurfacePath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$capability = Get-Content -LiteralPath $capabilityPath -Raw

Assert-Condition ($catalog -match 'RecordingProfileId' -and $catalog -match 'RecordingWriterProviderId' -and $catalog -match 'RecordingProfileCatalog') "Recording profiles require stable provider-neutral profile/provider identities and a catalog."
Assert-Condition ($catalog -match 'mp4-h264-aac' -and $catalog -match 'windows-media-foundation' -and $catalog -match 'RecordingAccelerationClass.Software') "Qualified MP4 must remain the software-classified Windows Media Foundation default profile."
Assert-Condition ($providers -match 'RecordingWriterProviderRegistry' -and $providers -match 'IProgramRecordingWriterProvider' -and $providers -match 'ProfileSelectingProgramRecordingWriter') "Recording writer selection must remain behind the bounded provider/factory seam."
Assert-Condition ($providers -notmatch 'Assembly.Load|Activator.CreateInstance|Type.GetType|Process.Start') "Recording writer providers must not use arbitrary runtime plugin/process discovery."
Assert-Condition ($contracts -match 'RecordingProfileId? profileId = null') "Recording start must preserve backward compatibility through an optional profile identity."
Assert-Condition ($runtime -match 'recording.profile.unknown' -and $runtime -match 'recording.profile.unavailable') "Runtime must reject unknown and unavailable profiles before recording starts."
Assert-Condition ($runtimeIpc -notmatch 'WindowsMediaFoundationMp4RecordingWriter' -and $controlTransport -notmatch 'WindowsMediaFoundationMp4RecordingWriter' -and $client -notmatch 'WindowsMediaFoundationMp4RecordingWriter') "Runtime IPC, Control and Client boundaries must not expose concrete recording writer types."
Assert-Condition ($operator -match 'recording.Profiles' -and $operator -match 'SelectedRecordingProfile?.ProfileId' -and $operatorSurface -match 'ItemsSource="{Binding RecordingProfiles}"') "Operator recording profile choice must derive from confirmed capability data."
Assert-Condition ($operatorSurface -notmatch '(?i)>[^<]*(MOV|MXF|ProRes|DNxHR|AVC-Intra)[^<]*<') "Operator must not advertise future professional formats without concrete providers."
Assert-Condition ($documentation -match 'MP4' -and $documentation -match 'MOV' -and $documentation -match 'not available') "Recording profile documentation must distinguish current MP4 capability from future unavailable formats."
Assert-Condition ($capability -match '"profileId": "mp4-h264-aac"' -and $capability -match '"accelerationClass": "software"' -and $capability -notmatch '"profileId": "(mov|mxf)') "Machine-readable capability evidence must publish only implemented production profiles and must not claim hardware acceleration."

Write-Host "Recording Profile Catalog and Provider Boundary policy PASS"
