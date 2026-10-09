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
$equalizerPath = Join-Path $repositoryRoot "src/Media/rtaime.Media/BoundedParametricEqualizer.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorViewModel.cs"
$operatorUiPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MainWindow.xaml"
$unitPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/AudioProductionEngineTests.cs"
$performancePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/AudioProductionPerformanceTests.cs"
$equalizerUnitContractPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/BoundedParametricEqualizerContractTests.cs"
$equalizerUnitProcessingPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/BoundedParametricEqualizerProcessingTests.cs"
$equalizerPerformancePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/BoundedParametricEqualizerPerformanceTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/AdvancedAudioProduction.md"

foreach ($path in @($contractsPath, $enginePath, $equalizerPath, $runtimePath, $operatorPath, $operatorUiPath, $unitPath, $performancePath, $equalizerUnitContractPath, $equalizerUnitProcessingPath, $equalizerPerformancePath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required advanced-audio artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$engine = Get-Content -LiteralPath $enginePath -Raw
$equalizer = Get-Content -LiteralPath $equalizerPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw
$operatorUi = Get-Content -LiteralPath $operatorUiPath -Raw
$unit = Get-Content -LiteralPath $unitPath -Raw
$performance = Get-Content -LiteralPath $performancePath -Raw
$equalizerUnitContract = Get-Content -LiteralPath $equalizerUnitContractPath -Raw
$equalizerUnitProcessing = Get-Content -LiteralPath $equalizerUnitProcessingPath -Raw
$equalizerPerformance = Get-Content -LiteralPath $equalizerPerformancePath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($contracts -match 'MaximumSources\s*=\s*8') "Advanced audio source count must remain explicitly bounded."
Assert-Condition ($contracts -match 'MaximumBuses\s*=\s*4') "Advanced audio bus count must remain explicitly bounded."
Assert-Condition ($contracts -match 'public static AudioBusId Program') "Advanced audio must retain one explicit Program bus."
Assert-Condition ($contracts -match 'AudioCrossfadeLaw' -and $contracts -match 'EqualPower') "Crossfade law must remain explicit."
Assert-Condition ($contracts -match 'AudioDuckingConfiguration') "Ducking must remain an explicit bounded contract."
Assert-Condition ($contracts -match 'AudioClipStrategy' -and $contracts -match 'HardClip') "Clipping policy must remain explicit."
Assert-Condition ($contracts -match 'AudioSourceEqualizerConfiguration' -and $contracts -match 'AudioLowShelfEqualizerBand' -and $contracts -match 'AudioBellEqualizerBand' -and $contracts -match 'AudioHighShelfEqualizerBand') "Per-source equalizer topology must remain typed and fixed to three ordered bands."
Assert-Condition ($contracts -match 'MinimumFrequencyHz\s*=\s*20' -and $contracts -match 'MaximumFrequencyHz\s*=\s*20_000' -and $contracts -match 'MinimumGainDb\s*=\s*-18' -and $contracts -match 'MaximumGainDb\s*=\s*18') "Equalizer frequency and gain limits must remain explicitly bounded."

Assert-Condition ($engine -match 'ProcessBus' -and $engine -match 'ReadOnlySpan<AudioProductionSourceBuffer>' -and $engine -match 'Span<float> destination') "Mixer hot path must retain span-based bounded processing."
Assert-Condition ($engine -match 'Math\.Cos\(progress \* Math\.PI \* 0\.5\)' -and $engine -match 'Math\.Sin\(progress \* Math\.PI \* 0\.5\)') "Equal-power crossfade law must remain deterministic."
Assert-Condition ($engine -notmatch 'DateTime|Stopwatch|Task\.Delay') "Audio DSP timing must not depend on wall clock or asynchronous timers."
Assert-Condition ($engine -notmatch 'new float\[') "The steady-state mix engine must not allocate sample buffers."
Assert-Condition ($engine -match 'StartSamplePosition' -or $engine -match 'startSamplePosition') "Crossfade progression must remain sample-position based."
Assert-Condition ($engine -match 'BoundedParametricEqualizerState' -and $engine -match 'BeginEqualizerBlock') "AudioProductionEngine must own per-source equalizer execution and multi-bus state replay."
Assert-Condition (($equalizer -match 'ProcessSample') -and (($equalizer -match 'transposed') -or ($equalizer -match 'z1'))) "Equalizer DSP must retain deterministic biquad state processing."
Assert-Condition ($equalizer -notmatch 'DateTime|Stopwatch|Task\.Delay') "Equalizer DSP must not depend on wall clock or asynchronous timers."

Assert-Condition ($runtime -match 'AudioProductionEngine') "RuntimeHost must own advanced audio processing."
Assert-Condition ($runtime -match '_audioBusMixSamples' -and $runtime -match 'ProcessConfiguredAudioBusesUnsafe') "RuntimeHost must reuse bounded per-bus audio mix buffers through one processing engine."
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
Assert-Condition ($unit -match 'Multiple_buses_mix_independently_and_allow_zero_or_multiple_assignments' -and $unit -match 'More_than_four_buses_are_rejected') "Bounded multi-bus semantics and the four-bus limit require explicit regression coverage."
Assert-Condition ($performance -match 'Maximum_eight_source_four_bus_processing_is_allocation_free_after_warmup') "Maximum 8-source x 4-bus allocation qualification is required."
Assert-Condition ($equalizerUnitContract -match 'Missing_equalizer_and_enabled_zero_db_equalizer_are_byte_equivalent' -and $equalizerUnitContract -match 'Extreme_allowed_coefficients_remain_finite') "Equalizer bypass parity and coefficient-boundary coverage are required."
Assert-Condition ($equalizerUnitProcessing -match 'Low_mid_and_high_bands_move_expected_frequency_regions' -and $equalizerUnitProcessing -match 'Processing_is_invariant_to_valid_block_splitting' -and $equalizerUnitProcessing -match 'Same_source_state_is_replayed_for_multiple_buses_in_one_sample_window') "Equalizer response, block invariance and multi-bus state coverage are required."
Assert-Condition ($equalizerPerformance -match 'Maximum_eight_source_four_bus_equalizer_processing_is_allocation_free_after_warmup') "Maximum bounded equalizer allocation qualification is required."
Assert-Condition ($documentation -match 'ControlHost owns authoritative configuration' -and $documentation -match 'No wall-clock or UI timer') "Advanced-audio documentation must retain authority and timing boundaries."
Assert-Condition ($documentation -match 'Bounded per-source equalizer' -and $documentation -match '20\.\.20,000 Hz' -and $documentation -match 'transposed direct form II') "Advanced-audio documentation must describe the bounded equalizer topology, limits and processing form."

Write-Host "Advanced audio production policy PASS"
