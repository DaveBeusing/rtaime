# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$contracts = Get-Content -LiteralPath (Join-Path $root "src/Contracts/rtaime.Provider.Contracts/MediaSourceDiscovery.cs") -Raw
$provider = Get-Content -LiteralPath (Join-Path $root "src/Providers/rtaime.Provider.Ndi/NdiNetworkOutputProvider.cs") -Raw
$discovery = Get-Content -LiteralPath (Join-Path $root "src/Providers/rtaime.Provider.Ndi/NdiDiscovery.cs") -Raw
$input = Get-Content -LiteralPath (Join-Path $root "src/Providers/rtaime.Provider.Ndi/NdiInputProvider.cs") -Raw
$bridge = Get-Content -LiteralPath (Join-Path $root "src/Hosts/rtaime.RuntimeHost/RuntimeNdiInputBridge.cs") -Raw
$control = Get-Content -LiteralPath (Join-Path $root "src/Hosts/rtaime.ControlHost/MediaSourceDiscoveryAdoptionService.cs") -Raw
$operator = Get-Content -LiteralPath (Join-Path $root "src/Hosts/rtaime.Operator/NdiSourceDiscoveryViewModel.cs") -Raw
$operatorControl = Get-Content -LiteralPath (Join-Path $root "src/Hosts/rtaime.Operator/NdiSourceDiscoveryControl.xaml") -Raw
$tests = Get-Content -LiteralPath (Join-Path $root "tests/rtaime.Tests.Unit/NdiDiscoveryInputTests.cs") -Raw

Assert-Condition ($contracts -match 'MaximumRetainedResults' -and $contracts -match 'MediaInputLifecycleState') "Discovery/input contracts must remain bounded and expose lifecycle evidence."
Assert-Condition ($provider -match 'MediaSourceCapabilityKinds\.Discovery' -and $provider -match 'MediaSourceCapabilityKinds\.Input') "The shared NDI provider must expose discovery and input capabilities."
Assert-Condition ($discovery -match 'maximumRetainedResults' -and $discovery -match 'expiry' -and $discovery -match 'Take\(') "NDI discovery must retain bounded refresh/expiry semantics."
Assert-Condition ($input -match 'drop_oldest' -and $input -match 'MediaInputLifecycleState\.Lost' -and $input -match 'MediaInputLifecycleState\.Reconnecting') "NDI input must retain bounded live buffering and source-loss/reconnect evidence."
Assert-Condition ($bridge -match 'RegisterExternalSource' -and $bridge -match 'SetExternalInputContent' -and $bridge -match 'SetExternalAudioInput') "Runtime NDI input must feed the existing Runtime source/audio seams."
Assert-Condition ($bridge -notmatch 'SelectPreview|CutProgram|DissolveProgram|RouteOutputRole') "Runtime NDI input must never mutate production routing."
Assert-Condition ($control -match 'AdoptAsync' -and $control -match 'AdoptSourceAsync' -and $control -match 'routingBefore') "External-source adoption must remain explicit, durable and routing-neutral."
Assert-Condition ($operator -match 'OperatorControlClient' -and $operator -notmatch 'rtaime\.Provider\.Ndi|NativeNdi|NDIlib_') "Operator discovery must consume management evidence and never host NDI provider/native code."
Assert-Condition ($operatorControl -match 'ADOPT' -and $operatorControl -match 'RefreshCommand' -and $operatorControl -match 'Discovery is observational') "Operator UI must expose explicit refresh/adoption without route side effects."
Assert-Condition ($tests -match 'bounded' -and $tests -match 'expires' -and $tests -match 'same_source' -and $tests -match 'unsupported') "NDI discovery/input regression coverage is incomplete."

$forbiddenRoots = @(
    "src/Client/rtaime.Client",
    "src/Hosts/rtaime.Operator",
    "src/Contracts"
)
foreach ($relative in $forbiddenRoots) {
    $matches = Get-ChildItem -LiteralPath (Join-Path $root $relative) -File -Recurse -Include *.cs |
        Select-String -Pattern 'NDIlib_|NdiVideoFrameV2|NdiAudioFrameV3|Processing\.NDI\.Lib' -SimpleMatch:$false
    Assert-Condition (@($matches).Count -eq 0) "NDI SDK/native ABI types must remain inside the NDI provider/native boundary."
}

Write-Host "NDI discovery and input policy PASS"
Write-Host "Discovery authority: observational until explicit ControlHost adoption"
Write-Host "Receive execution: RuntimeHost/provider owned"
Write-Host "Operator native NDI code: prohibited"
