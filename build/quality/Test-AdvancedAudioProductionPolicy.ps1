# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

$contractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Media.Contracts/AdvancedAudioContracts.cs"
$enginePath = Join-Path $repositoryRoot "src/Media/rtaime.Media/AudioProduction.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorViewModel.cs"
$operatorUiPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MainWindow.xaml"
$unitPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/AudioProductionEngineTests.cs"
$performancePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/AudioProductionPerformanceTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/AdvancedAudioProduction.md"

foreach ($path in @($contractsPath, $enginePath, $runtimePath, $operatorPath, $operatorUiPath, $unitPath, $performancePath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required advanced-audio artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$engine = Get-Content -LiteralPath $enginePath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw
$operatorUi = Get-Content -LiteralPath $operatorUiPath -Raw
$unit = Get-Content -LiteralPath $unitPath -Raw
$performance = Get-Content -LiteralPath $performancePath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($contracts -match 'MaximumSources\s*=\s*8') "Advanced audio source count must remain explicitly bounded."
Assert-Condition ($contracts -match 'MaximumBuses\s*=\s*4') "Advanced audio bus count must remain explicitly bounded."
Assert-Condition ($contracts -match 'AudioBusId Program') "Advanced audio must retain one explicit Program bus."
Assert-Condition ($contracts -match 'AudioCrossfadeLaw' -and $contracts -match 'EqualPower') "Crossfade law must remain explicit."
Assert-Condition ($contracts -match 'AudioDuckingConfiguration') "Ducking must remain an explicit bounded contract."
Assert-Condition ($contracts -match 'AudioClipStrategy' -and $contracts -match 'HardClip') "Clipping policy must remain explicit."

Assert-Condition ($engine -match 'ProcessBus' -and $engine -match 'ReadOnlySpan<AudioProductionSourceBuffer>' -and $engine -match 'Span<float> destination') "Mixer hot path must retain span-based bounded processing."
Assert-Condition ($engine -match 'Math\.Cos\(progress \* Math\.PI \* 0\.5\)' -and $engine -match 'Math\.Sin\(progress \* Math\.PI \* 0\.5\)') "Equal-power crossfade law must remain deterministic."
Assert-Condition ($engine -notmatch 'DateTime|Stopwatch|Task\.Delay') "Audio DSP timing must not depend on wall clock or asynchronous timers."
Assert-Condition ($engine -notmatch 'new float\[') "The steady-state mix engine must not allocate sample buffers."
Assert-Condition ($engine -match 'StartSamplePosition' -or $engine -match 'startSamplePosition') "Crossfade progression must remain sample-position based."

Assert-Condition ($runtime -match 'AudioProductionEngine') "RuntimeHost must own advanced audio processing."
Assert-Condition ($runtime -match '_programAudioMixSamples') "RuntimeHost must reuse a Program audio mix buffer."
Assert-Condition ($runtime -match 'TryRecordCommittedProgram\(execution, output\.Descriptor, programAudioBuffer\)') "Recording must consume the final Program audio descriptor."
Assert-Condition ($runtime -match 'programAudioBuffer,\s*programAudioPayload') "Output paths must consume the same final Program audio payload."
Assert-Condition ($runtime -notmatch 'ControlHost') "Runtime audio processing must not depend on ControlHost implementation."

Assert-Condition ($operator -match 'SetAudioProductionAsync') "Operator advanced-audio mutations must use the governed client/control path."
Assert-Condition ($operatorUi -match 'ADVANCED PROGRAM MIX' -and $operatorUi -match 'StartAudioCrossfadeCommand' -and $operatorUi -match 'ToggleAudioDuckingCommand') "Operator must expose the confirmed advanced mixer through established custom controls."
Assert-Condition ($operatorUi -notmatch '<Slider|<Button\b|<TextBox\b') "Advanced audio UI must not introduce WPF default Button/TextBox/Slider controls."

Assert-Condition ($unit -match 'Steady_state_processing_does_not_allocate_per_block') "Advanced audio unit coverage must retain an allocation regression guard."
Assert-Condition ($unit -match 'Equal_power_crossfade_has_exact_endpoints_and_expected_midpoint') "Crossfade deterministic sample tests are required."
Assert-Condition ($unit -match 'Ducking_attack_hold_release_and_sidechain_loss_are_sample_deterministic') "Ducking deterministic sample tests are required."
Assert-Condition ($performance -match 'Maximum_input_mix_with_crossfade_and_ducking_is_bounded_and_allocation_free_after_warmup') "Maximum-input performance qualification is required."
Assert-Condition ($documentation -match 'ControlHost owns authoritative configuration' -and $documentation -match 'No wall-clock or UI timer') "Advanced-audio documentation must retain authority and timing boundaries."

Write-Host "Advanced audio production policy PASS"
