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
$dynamicsPath = Join-Path $repositoryRoot "src/Media/rtaime.Media/BoundedBusDynamics.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$mediaIoVerticalSlicePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeMediaIoVerticalSlice.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorViewModel.cs"
$operatorUiPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MainWindow.xaml"
$operatorMixerUiPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/AudioMixerControl.xaml"
$operatorMixerViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorAudioMixerViewModel.cs"
$unitPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/AudioProductionEngineTests.cs"
$performancePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/AudioProductionPerformanceTests.cs"
$equalizerUnitContractPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/BoundedParametricEqualizerContractTests.cs"
$equalizerUnitProcessingPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/BoundedParametricEqualizerProcessingTests.cs"
$equalizerPerformancePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/BoundedParametricEqualizerPerformanceTests.cs"
$dynamicsUnitContractPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/BoundedBusDynamicsContractTests.cs"
$dynamicsUnitProcessingPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/BoundedBusDynamicsProcessingTests.cs"
$dynamicsPerformancePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/BoundedBusDynamicsPerformanceTests.cs"
$recordingIntegrationPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ReferenceRecordingPayloadTests.cs"
$productionIpcIntegrationPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ProductionIpcIntegrationTests.cs"
$showProjectPersistenceIntegrationPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ShowProjectPersistenceIntegrationTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/AdvancedAudioProduction.md"
$qualificationUnitPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/AdvancedAudioProcessingQualificationTests.cs"
$qualificationIntegrationPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/AdvancedAudioQualificationIntegrationTests.cs"
$qualificationPerformancePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/AdvancedAudioProcessingQualificationPerformanceTests.cs"
$timingPerformancePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/AudioFollowVideoPerformanceTests.cs"
$networkOutputTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/NetworkOutputFoundationTests.cs"
$recordingFailurePath = Join-Path $repositoryRoot "tests/rtaime.Tests.Failure/RecordingFailureIsolationTests.cs"
$audioMixerOperatorTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Operator/AudioMixerOperatorSurfaceTests.cs"
$qualificationDocumentationPath = Join-Path $repositoryRoot "docs/AdvancedAudioProcessingQualification.md"

foreach ($path in @($contractsPath, $enginePath, $equalizerPath, $dynamicsPath, $runtimePath, $mediaIoVerticalSlicePath, $operatorPath, $operatorUiPath, $operatorMixerUiPath, $operatorMixerViewModelPath, $unitPath, $performancePath, $equalizerUnitContractPath, $equalizerUnitProcessingPath, $equalizerPerformancePath, $dynamicsUnitContractPath, $dynamicsUnitProcessingPath, $dynamicsPerformancePath, $recordingIntegrationPath, $productionIpcIntegrationPath, $showProjectPersistenceIntegrationPath, $documentationPath, $qualificationUnitPath, $qualificationIntegrationPath, $qualificationPerformancePath, $timingPerformancePath, $networkOutputTestsPath, $recordingFailurePath, $audioMixerOperatorTestsPath, $qualificationDocumentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required advanced-audio artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$engine = Get-Content -LiteralPath $enginePath -Raw
$equalizer = Get-Content -LiteralPath $equalizerPath -Raw
$dynamics = Get-Content -LiteralPath $dynamicsPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$mediaIoVerticalSlice = Get-Content -LiteralPath $mediaIoVerticalSlicePath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw
$operatorUi = Get-Content -LiteralPath $operatorUiPath -Raw
$operatorMixerUi = Get-Content -LiteralPath $operatorMixerUiPath -Raw
$operatorMixerViewModel = Get-Content -LiteralPath $operatorMixerViewModelPath -Raw
$unit = Get-Content -LiteralPath $unitPath -Raw
$performance = Get-Content -LiteralPath $performancePath -Raw
$equalizerUnitContract = Get-Content -LiteralPath $equalizerUnitContractPath -Raw
$equalizerUnitProcessing = Get-Content -LiteralPath $equalizerUnitProcessingPath -Raw
$equalizerPerformance = Get-Content -LiteralPath $equalizerPerformancePath -Raw
$dynamicsUnitContract = Get-Content -LiteralPath $dynamicsUnitContractPath -Raw
$dynamicsUnitProcessing = Get-Content -LiteralPath $dynamicsUnitProcessingPath -Raw
$dynamicsPerformance = Get-Content -LiteralPath $dynamicsPerformancePath -Raw
$recordingIntegration = Get-Content -LiteralPath $recordingIntegrationPath -Raw
$productionIpcIntegration = Get-Content -LiteralPath $productionIpcIntegrationPath -Raw
$showProjectPersistenceIntegration = Get-Content -LiteralPath $showProjectPersistenceIntegrationPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$qualificationUnit = Get-Content -LiteralPath $qualificationUnitPath -Raw
$qualificationIntegration = Get-Content -LiteralPath $qualificationIntegrationPath -Raw
$qualificationPerformance = Get-Content -LiteralPath $qualificationPerformancePath -Raw
$timingPerformance = Get-Content -LiteralPath $timingPerformancePath -Raw
$networkOutputTests = Get-Content -LiteralPath $networkOutputTestsPath -Raw
$recordingFailure = Get-Content -LiteralPath $recordingFailurePath -Raw
$audioMixerOperatorTests = Get-Content -LiteralPath $audioMixerOperatorTestsPath -Raw
$qualificationDocumentation = Get-Content -LiteralPath $qualificationDocumentationPath -Raw

Assert-Condition ($contracts -match 'MaximumSources\s*=\s*8') "Advanced audio source count must remain explicitly bounded."
Assert-Condition ($contracts -match 'MaximumBuses\s*=\s*4') "Advanced audio bus count must remain explicitly bounded."
Assert-Condition ($contracts -match 'public static AudioBusId Program') "Advanced audio must retain one explicit Program bus."
Assert-Condition ($contracts -match 'AudioCrossfadeLaw' -and $contracts -match 'EqualPower') "Crossfade law must remain explicit."
Assert-Condition ($contracts -match 'AudioDuckingConfiguration') "Ducking must remain an explicit bounded contract."
Assert-Condition ($contracts -match 'AudioClipStrategy' -and $contracts -match 'HardClip') "Clipping policy must remain explicit."
Assert-Condition ($contracts -match 'AudioSourceEqualizerConfiguration' -and $contracts -match 'AudioLowShelfEqualizerBand' -and $contracts -match 'AudioBellEqualizerBand' -and $contracts -match 'AudioHighShelfEqualizerBand') "Per-source equalizer topology must remain typed and fixed to three ordered bands."
Assert-Condition ($contracts -match 'MinimumFrequencyHz\s*=\s*20' -and $contracts -match 'MaximumFrequencyHz\s*=\s*20_000' -and $contracts -match 'MinimumGainDb\s*=\s*-18' -and $contracts -match 'MaximumGainDb\s*=\s*18') "Equalizer frequency and gain limits must remain explicitly bounded."
Assert-Condition ($contracts -match 'AudioBusCompressorConfiguration' -and $contracts -match 'AudioBusSamplePeakLimiterConfiguration' -and $contracts -match 'AudioBusDynamicsConfiguration') "Bus dynamics topology must remain typed and fixed to compressor then sample-peak limiter."
Assert-Condition ($contracts -match 'MinimumCompressorThresholdDbFs\s*=\s*-60' -and $contracts -match 'MaximumCompressorRatio\s*=\s*20' -and $contracts -match 'MinimumAttackMilliseconds\s*=\s*0\.1' -and $contracts -match 'MaximumReleaseMilliseconds\s*=\s*5_000' -and $contracts -match 'MinimumLimiterCeilingDbFs\s*=\s*-24') "Bus dynamics parameters must remain explicitly bounded."
Assert-Condition ($contracts -notmatch 'AJA|NTV2|DeckLink|Blackmagic|MediaFoundation|SrtNetwork') "Stable advanced-audio contracts must remain provider-neutral."

Assert-Condition ($engine -match 'ProcessBus' -and $engine -match 'ReadOnlySpan<AudioProductionSourceBuffer>' -and $engine -match 'Span<float> destination') "Mixer hot path must retain span-based bounded processing."
Assert-Condition ($engine -match 'Math\.Cos\(progress \* Math\.PI \* 0\.5\)' -and $engine -match 'Math\.Sin\(progress \* Math\.PI \* 0\.5\)') "Equal-power crossfade law must remain deterministic."
Assert-Condition ($engine -notmatch 'DateTime|Stopwatch|Task\.Delay') "Audio DSP timing must not depend on wall clock or asynchronous timers."
Assert-Condition ($engine -notmatch 'new float\[') "The steady-state mix engine must not allocate sample buffers."
Assert-Condition ($engine -match 'StartSamplePosition' -or $engine -match 'startSamplePosition') "Crossfade progression must remain sample-position based."
Assert-Condition ($engine -match 'BoundedParametricEqualizerState' -and $engine -match 'BeginEqualizerBlock') "AudioProductionEngine must own per-source equalizer execution and multi-bus state replay."
Assert-Condition (($equalizer -match 'ProcessSample') -and (($equalizer -match 'transposed') -or ($equalizer -match 'z1'))) "Equalizer DSP must retain deterministic biquad state processing."
Assert-Condition ($equalizer -notmatch 'DateTime|Stopwatch|Task\.Delay') "Equalizer DSP must not depend on wall clock or asynchronous timers."
Assert-Condition ($dynamics -match 'StereoPeak' -and $dynamics -match 'AudioBusCompressorConfiguration' -and $dynamics -match 'AudioBusSamplePeakLimiterConfiguration') "Bus dynamics must remain stereo-linked and use the bounded typed processors."
Assert-Condition ($dynamics -match 'SampleRate\s*=\s*48_000' -and $dynamics -match 'Math\.Exp\(-1d / samples\)') "Bus dynamics envelope timing must remain sample-domain based at the qualified 48 kHz baseline."
Assert-Condition ($dynamics -notmatch 'DateTime|Stopwatch|Task\.Delay') "Bus dynamics DSP must not depend on wall clock or asynchronous timers."
Assert-Condition ($engine -match 'preDynamicsPeak' -and $engine -match 'dynamics\.Process\(left, right\)' -and $engine -match 'preClipPeak') "Bus dynamics must remain between bus master processing and the final safety clamp."
Assert-Condition ($engine -match 'ReconcileDynamicsStates' -and $engine -match 'oldBus\.BusId' -and $engine -match 'Equals\(oldBus\.Dynamics, bus\.Dynamics\)') "Per-bus dynamics state must remain keyed by stable bus identity and reset on incompatible dynamics changes."

Assert-Condition ($runtime -match 'AudioProductionEngine') "RuntimeHost must own advanced audio processing."
Assert-Condition ($runtime -match '_audioBusMixSamples' -and $runtime -match 'ProcessConfiguredAudioBusesUnsafe') "RuntimeHost must reuse bounded per-bus audio mix buffers through one processing engine."
Assert-Condition ($runtime -match '(?s)TryRecordCommittedProgram\(\s*execution\s*,\s*output\.Descriptor\s*,\s*programRecordingAudioBuffer\s*[,)]') "Recording must consume the final Program audio descriptor."
Assert-Condition ($runtime -match 'programAudioBuffer,\s*programAudioPayload') "Output paths must consume the same final Program audio payload."
Assert-Condition ($mediaIoVerticalSlice -match 'boundary\.ProgramAudioPayload' -and $mediaIoVerticalSlice -match 'boundary\.ProgramAudioBuffer\.Timing' -and $mediaIoVerticalSlice -match 'TrySubmitProgram') "Physical Media I/O Program output must consume the final Runtime-owned Program audio payload without an independent remix."
Assert-Condition ($runtime -notmatch 'ControlHost') "Runtime audio processing must not depend on ControlHost implementation."

Assert-Condition ($operator -match 'SetAudioProductionAsync') "Operator advanced-audio mutations must use the governed client/control path."
Assert-Condition ($operatorUi -match '<local:AudioMixerControl' -and $operatorMixerUi -match 'AUDIO MIXER' -and $operatorMixerUi -match 'Owner.StartAudioCrossfadeCommand' -and $operatorMixerUi -match 'Owner.ToggleAudioDuckingCommand') "Operator must expose the confirmed bounded advanced mixer through established custom controls."
Assert-Condition (($operatorMixerViewModel -match 'AudioSourceEqualizerConfiguration' -and $operatorMixerViewModel -match 'AudioBusDynamicsConfiguration') -and (($operatorMixerViewModel -match 'SetAudioProductionAsync') -or ($operator -match 'ApplyAudioMixerConfigurationAsync[\s\S]+SetAudioProductionAsync'))) "Operator mixer must edit typed EQ/dynamics only through the governed production configuration seam."
Assert-Condition ($operatorMixerUi -notmatch '<Slider|<Button\b|<TextBox\b') "Advanced audio mixer UI must not introduce WPF default Button/TextBox/Slider controls."

Assert-Condition ($unit -match 'Steady_state_processing_does_not_allocate_per_block') "Advanced audio unit coverage must retain an allocation regression guard."
Assert-Condition ($unit -match 'Equal_power_crossfade_has_exact_endpoints_and_expected_midpoint') "Crossfade deterministic sample tests are required."
Assert-Condition ($unit -match 'Ducking_attack_hold_release_and_sidechain_loss_are_sample_deterministic') "Ducking deterministic sample tests are required."
Assert-Condition ($performance -match 'Maximum_input_mix_with_crossfade_and_ducking_is_bounded_and_allocation_free_after_warmup') "Maximum-input performance qualification is required."
Assert-Condition ($unit -match 'Multiple_buses_mix_independently_and_allow_zero_or_multiple_assignments' -and $unit -match 'More_than_four_buses_are_rejected') "Bounded multi-bus semantics and the four-bus limit require explicit regression coverage."
Assert-Condition ($performance -match 'Maximum_eight_source_four_bus_processing_is_allocation_free_after_warmup') "Maximum 8-source x 4-bus allocation qualification is required."
Assert-Condition ($equalizerUnitContract -match 'Missing_equalizer_and_enabled_zero_db_equalizer_are_byte_equivalent' -and $equalizerUnitContract -match 'Extreme_allowed_coefficients_remain_finite') "Equalizer bypass parity and coefficient-boundary coverage are required."
Assert-Condition ($equalizerUnitProcessing -match 'Low_mid_and_high_bands_move_expected_frequency_regions' -and $equalizerUnitProcessing -match 'Processing_is_invariant_to_valid_block_splitting' -and $equalizerUnitProcessing -match 'Same_source_state_is_replayed_for_multiple_buses_in_one_sample_window') "Equalizer response, block invariance and multi-bus state coverage are required."
Assert-Condition ($equalizerPerformance -match 'Maximum_eight_source_four_bus_equalizer_processing_is_allocation_free_after_warmup') "Maximum bounded equalizer allocation qualification is required."
Assert-Condition ($dynamicsUnitContract -match 'Compressor_and_sample_peak_limiter_accept_documented_boundaries' -and $dynamicsUnitContract -match 'rejects_non_finite_and_out_of_range_parameters') "Bounded dynamics contract limits and rejection coverage are required."
Assert-Condition ($dynamicsUnitProcessing -match 'Disabled_dynamics_is_exactly_bypass_equivalent' -and $dynamicsUnitProcessing -match 'Threshold_and_ratio_produce_expected_steady_state_level' -and $dynamicsUnitProcessing -match 'Compressor_attack_and_release_progress_per_sample' -and $dynamicsUnitProcessing -match 'Sample_peak_limiter_never_exceeds_ceiling_and_is_stereo_linked') "Dynamics bypass, compressor envelope and sample-peak limiter qualification are required."
Assert-Condition ($dynamicsUnitProcessing -match 'Dynamics_state_is_invariant_to_contiguous_block_splitting' -and $dynamicsUnitProcessing -match 'Equivalent_configuration_retains_state_and_dynamics_change_resets_it' -and $dynamicsUnitProcessing -match 'Reconstructed_engine_resets_runtime_dynamics_envelope_state' -and $dynamicsUnitProcessing -match 'Multiple_buses_keep_independent_dynamics_envelopes') "Dynamics continuity, deterministic configuration/restart reset and bus-isolation coverage are required."
Assert-Condition ($dynamicsPerformance -match 'Maximum_eight_source_four_bus_dynamics_processing_is_allocation_free_after_warmup') "Maximum 8-source x 4-bus bounded dynamics allocation qualification is required."
Assert-Condition ($recordingIntegration -match 'Recording_persists_the_final_multi_source_Program_mix' -and $recordingIntegration -match 'LimiterHitCount') "Recording integration must prove reuse of final dynamics-processed Program audio."
Assert-Condition ($productionIpcIntegration -match 'programDynamics' -and $productionIpcIntegration -match 'recoveredDynamics') "Client/Control/Runtime transport and Runtime restart must retain bounded dynamics configuration."
Assert-Condition ($showProjectPersistenceIntegration -match 'Audio_bus_dynamics_round_trip_through_durable_show_project') "Durable show-project persistence must round-trip bounded bus dynamics configuration."
Assert-Condition ($documentation -match 'ControlHost owns authoritative configuration' -and $documentation -match 'No wall-clock or UI timer') "Advanced-audio documentation must retain authority and timing boundaries."
Assert-Condition ($documentation -match 'Bounded per-source equalizer' -and $documentation -match '20\.\.20,000 Hz' -and $documentation -match 'transposed direct form II') "Advanced-audio documentation must describe the bounded equalizer topology, limits and processing form."
Assert-Condition ($documentation -match 'Bounded per-bus dynamics' -and $documentation -match 'sample-peak limiter' -and $documentation -match 'There is no look-ahead, oversampling or true-peak claim') "Advanced-audio documentation must retain bounded compressor/sample-peak semantics without a true-peak claim."
Assert-Condition ($qualificationUnit -match 'Complete_processing_order_matches_golden_vector' -and $qualificationUnit -match '0\.5011872f' -and $qualificationUnit -match 'LimiterHitCount' -and $qualificationUnit -match 'SafetyClippedSampleValues') "Integrated advanced-audio qualification must retain an independent complete-order golden sample vector."
Assert-Condition ($qualificationIntegration -match 'Full_authoritative_configuration_crosses_Client_Control_and_Runtime' -and $qualificationIntegration -match 'AudioProductionLimits\.MaximumBuses' -and $qualificationIntegration -match 'RouteOutputRoleAsync' -and $qualificationIntegration -match 'control\.audio\.production\.revision_conflict') "Integrated qualification must cross Client/Control/Runtime with four buses, selected output routing and stale-revision rejection."
Assert-Condition ($qualificationPerformance -match 'Full_eight_source_four_bus_processing_remains_bounded_allocation_free_and_finite' -and $qualificationPerformance -match 'AudioProductionLimits\.MaximumSources' -and $qualificationPerformance -match 'AudioProductionLimits\.MaximumBuses' -and $qualificationPerformance -match 'GC\.GetAllocatedBytesForCurrentThread' -and $qualificationPerformance -match 'block < 500') "Integrated maximum-load qualification must exercise 8 sources x 4 buses with zero steady-state allocation."
Assert-Condition ($qualificationPerformance -match 'AudioSourceEqualizerConfiguration' -and $qualificationPerformance -match 'AudioCrossfadeConfiguration' -and $qualificationPerformance -match 'AudioDuckingConfiguration' -and $qualificationPerformance -match 'AudioBusDynamicsConfiguration') "Integrated maximum-load qualification must keep EQ, crossfade, ducking and bus dynamics active together."
Assert-Condition ($timingPerformance -match 'InlineData\(false, 30_000\)' -and $timingPerformance -match 'InlineData\(true, 36_000\)' -and $timingPerformance -match '28_800_000' -and $timingPerformance -match '28_828_800') "Advanced-audio qualification must retain exact long-run 50 fps and 60000/1001 sample-position evidence."
Assert-Condition ($networkOutputTests -match 'Saturated_queue_drops_oldest_complete_sample_without_blocking_submitter' -and $networkOutputTests -match 'Transport_failure_is_observational_and_does_not_reject_future_submission_synchronously') "Advanced-audio qualification must retain bounded output-backpressure and failure-isolation evidence."
Assert-Condition ($recordingFailure -match 'Recording_writer_failure_does_not_change_committed_runtime_or_stop_program_frames') "Advanced-audio qualification must retain recording failure isolation from committed Program execution."
Assert-Condition ($audioMixerOperatorTests -match 'Mixer_projects_maximum_eight_sources_and_four_buses_from_confirmed_Runtime_evidence' -and $audioMixerOperatorTests -match 'RMS NOT EXPOSED') "Operator qualification must retain bounded mixer projection and must not synthesize unsupported RMS evidence."
Assert-Condition ($qualificationDocumentation -match 'software qualification' -and $qualificationDocumentation -match 'physical embedded-audio continuity' -and $qualificationDocumentation -match 'DMA/device-driver latency' -and $qualificationDocumentation -match 'hardware clock/genlock' -and $qualificationDocumentation -match 'UNVERIFIED') "Qualification documentation must distinguish software evidence from unverified physical audio claims."
Assert-Condition ($documentation -match 'Advanced Audio Processing Qualification' -and $documentation -match 'UNVERIFIED') "Advanced-audio documentation must link the integrated qualification and preserve physical-evidence boundaries."

Write-Host "Advanced audio production policy PASS"
