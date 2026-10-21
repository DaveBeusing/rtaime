# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$EvidencePath,
  [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$descriptor = Join-Path $repo "qualification/performance/realtime-playout-matrix.json"
$d = Get-Content -LiteralPath $descriptor -Raw | ConvertFrom-Json
$file = Get-Item -LiteralPath $EvidencePath -ErrorAction Stop
if ($file.Length -le 0 -or $file.Length -gt 2MB) { throw "Invalid matrix file size." }
$e = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 64
$root = [IO.Path]::GetFullPath((Split-Path -Parent $file.FullName))
$prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
function Require([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }
function Bound-File($reference) {
  $name = [string]$reference.path
  Require ($name -cmatch '^[a-zA-Z0-9._/-]+$' -and
    -not $name.Contains("..", [StringComparison]::Ordinal) -and
    -not [IO.Path]::IsPathRooted($name)) "Unsafe evidence path."
  $path = [IO.Path]::GetFullPath((Join-Path $root $name))
  Require ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) "Artifact escapes root."
  $item = Get-Item -LiteralPath $path -ErrorAction Stop
  Require ($item.Length -gt 0 -and $item.Length -le 32MB -and
    ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Invalid producer artifact."
  Require ([string]$reference.sha256 -cmatch '^[0-9a-f]{64}$' -and
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq
    [string]$reference.sha256) "Artifact SHA256 mismatch."
  return $path
}
function Check-Trx([string]$path, [string[]]$names) {
  [xml]$doc = Get-Content -LiteralPath $path -Raw
  $count = $doc.TestRun.ResultSummary.Counters
  $rows = @($doc.SelectNodes("//*[local-name()='UnitTestResult']"))
  Require ([int]$count.executed -ge $names.Count -and
    [int]$count.executed -eq [int]$count.passed -and
    [int]$count.failed -eq 0 -and [int]$count.notExecuted -eq 0 -and
    $rows.Count -eq [int]$count.executed) "Failed, missing or skipped test results."
  foreach ($name in $names) {
    Require (@($rows | Where-Object {
      [string]$_.testName -like "*$name*" -and [string]$_.outcome -ceq "Passed"
    }).Count -ge 1) "Required test $name did not pass."
  }
}
Require ($d.schemaVersion -ceq "1.0" -and
  $d.qualification -ceq "rtaime-realtime-playout-software-matrix" -and
  $d.sdk -ceq "10.0.401" -and
  (@($d.cadence | ForEach-Object format) -join ",") -ceq "1080p25,1080p50,1080p59.94") "Unsupported descriptor."
Require ($e.schemaVersion -ceq "1.0" -and $e.qualification -ceq $d.qualification -and
  $e.completion -ceq "SOFTWARE_COLLECTION_VERIFIED_NOT_REALTIME_APPROVAL" -and
  $e.sourceCommit -ceq $ExpectedSourceCommit -and
  $e.buildCommit -ceq $ExpectedSourceCommit -and $e.sdk -ceq "10.0.401") "Unbound software completion."
Require ($e.descriptorSha256 -ceq
  (Get-FileHash -LiteralPath $descriptor -Algorithm SHA256).Hash.ToLowerInvariant()) "Descriptor hash mismatch."
$profile = @($d.executionProfiles | Where-Object name -CEQ $e.profile)
Require ($profile.Count -eq 1 -and $e.runner.architecture -ceq "X64") "Wrong source runner or profile."
foreach ($name in @("actualNative25Fps","gpuDriverVram","physicalStorageLatencyAndCache",
  "physicalSdiNdiGenlock","physicalAvSyncAndEndToEnd","physicalDeviceLoss",
  "continuousNativeMultitrackSoak","allPhysicalAcceptance")) {
  Require ($e.$name -ceq "UNVERIFIED") "Software-only evidence cannot qualify $name."
}
$managedPath = Bound-File $e.managedManifest
& (Join-Path $PSScriptRoot "Test-ManagedAllocationGcEvidence.ps1") -EvidencePath $managedPath -ExpectedSourceCommit $ExpectedSourceCommit
$managed = Get-Content -LiteralPath $managedPath -Raw | ConvertFrom-Json
Require ($managed.profile -ceq $profile[0].managedGc -and @($e.program).Count -eq 2) "Managed producer is incomplete."
for ($i = 0; $i -lt 2; $i++) {
  $format = @("1080p50","1080p59.94")[$i]
  $period = if ($i -eq 0) { 20.0 } else { 1001.0 / 60.0 }
  $row = $e.program[$i]
  Require ($row.format -ceq $format -and
    [int]$row.consumerCases -eq 8 * @($managed.runs).Count -and
    [int]$row.samplesPerCase -eq 12 -and
    [Math]::Abs([double]$row.frameBudgetMilliseconds - $period) -lt 0.000001 -and
    $row.measurementScope -ceq "MANAGED_REFERENCE_CPU_WALL_NOT_PHYSICAL_SCANOUT" -and
    $row.numericalRegressionApproval -ceq "UNVERIFIED_NO_APPROVED_MANAGED_THRESHOLD") "Invalid Program scope."
  foreach ($n in @($row.maximumObservedCpuP99Milliseconds,
      $row.maximumObservedCallerThreadBytesPerBoundary)) {
    Require ([double]::IsFinite([double]$n) -and [double]$n -ge 0) "Invalid Program metric."
  }
  Require ([bool]$row.sampledCpuP99ExceedsNominalFramePeriod -eq
      ([double]$row.maximumObservedCpuP99Milliseconds -gt $period)) "False deadline comparison."
}
Require ($e.nativeDecodeCopy.status -ceq "ISOLATED_H264_AAC_DECODER_AND_WORKER_NOT_COMMITTED_PROGRAM" -and
  $e.nativeDecodeCopy.sourceCommit -ceq $ExpectedSourceCommit -and
  $e.nativeDecodeCopy.physicalGpuTransfer -ceq "UNVERIFIED") "Native copy is not a physical/Program qualification."
Check-Trx (Bound-File $e.nativeDecodeCopy.trx) @(
  "Native_decoder_copy_stages_report_source_bound_allocations_and_runtime_input_copy",
  "Native_decode_worker_attributes_slot_copies_without_releasing_retained_frames")
$copyLog = Get-Content -LiteralPath (Bound-File $e.nativeDecodeCopy.log) -Raw
foreach ($label in @("NATIVE DECODE COPY SOFTWARE PROFILE: ", "NATIVE WORKER SLOT COPY SOFTWARE PROFILE: ")) {
  $matching = @($copyLog -split "[
]+" | Where-Object { $_.Contains($label) })
  Require ($matching.Count -eq 1) "Missing or duplicated native decoder copy summary."
  $json = $matching[0].Substring($matching[0].IndexOf($label, [StringComparison]::Ordinal) + $label.Length) | ConvertFrom-Json
  Require ($json.sourceCommit -ceq $ExpectedSourceCommit) "Native reader/worker source identity mismatch."
  if ($label.StartsWith("NATIVE DECODE", [StringComparison]::Ordinal)) {
    Require ([ulong]$json.rgb32ToScratch.Operations -gt 0 -and
      [ulong]$json.rgbaSwizzle.Operations -gt 0 -and
      [ulong]$json.nativePcmPacket.Operations -gt 0 -and
      [ulong]$json.reproducedRuntimeSourceCopy.Operations -eq 32) "Native reader pixel/audio copy counts are incomplete."
  } else {
    Require ([ulong]$json.videoCopy.Operations -gt 0 -and
      [ulong]$json.audioCopy.Operations -gt 0 -and
      $json.hardware -ceq "UNVERIFIED") "Native worker copies were not evidenced."
  }
}
Require ($e.distinctNativeSequence.status -ceq "SOURCE_BOUND_INTEGRATION_ASSERTIONS" -and
  $e.distinctNativeSequence.actualSourceFormat -ceq "1080p50" -and
  (@($e.distinctNativeSequence.independentToneFrequenciesHz) -join ",") -ceq "440,880") "Incorrect native source scope."
Check-Trx (Bound-File $e.distinctNativeSequence.trx) @(
  "Two_distinct_native_colors_and_tones_reach_monitoring_Preview_and_committed_Program_PCM",
  "Two_native_video_and_two_audio_tracks_reach_committed_program_pixel_and_pcm_sink",
  "RuntimeHost_observations_and_recent_snapshot_cost_remain_bounded",
  "Uhd_and_physical_output_are_not_inferred_from_current_HD_or_software_evidence")
Require ($e.monitoring.status -ceq "GEOMETRY_AND_ALLOCATION_ASSERTIONS_NOT_DISPATCHER_P99") "Unverified WPF latency."
Check-Trx (Bound-File $e.monitoring.trx) @(
  "Steady_state_geometry_calculation_has_a_bounded_managed_allocation_budget",
  "Sustained_resize_and_dpi_churn_does_not_create_unbounded_target_revisions")
Require (@($e.dualDeck).Count -eq 2) "Both native deck formats are mandatory."
for ($i=0; $i -lt 2; $i++) {
  $format = @("1080p50","1080p59.94")[$i]
  $deck = $e.dualDeck[$i]
  Require ($deck.format -ceq $format) "Wrong deck format."
  $outcome = Bound-File $deck.outcome
  $environment = Bound-File $deck.environment
  $manifest = Bound-File $deck.manifest
  & (Join-Path $PSScriptRoot "Test-SsdPlaybackEvidence.ps1") -EvidencePath $outcome -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedProfile $profile[0].ssd -ExpectedFormat $format
  & (Join-Path $PSScriptRoot "Test-SsdPlaybackEnvironment.ps1") -EnvironmentPath $environment -ExpectedSourceCommit $ExpectedSourceCommit -OutcomePath $outcome
  $raw = Get-Content -LiteralPath $outcome -Raw | ConvertFrom-Json
  $native = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
  $missed = [ulong]$raw.result.missedDelta
  $state = if ($missed -gt 0) { "OBSERVED_PROGRAM_MISSES" } else { "SOFTWARE_CAPTURED_TIMING_NOT_PHYSICALLY_QUALIFIED" }
  Require ($native.sourceCommit -ceq $ExpectedSourceCommit -and
    $native.programFormat -ceq $format -and $native.softwareStatus -ceq "PASS" -and
    $deck.status -ceq $state -and $deck.cleanup -ceq "PASS" -and
    [ulong]$deck.missedProgramSlots -eq $missed -and
    [ulong]$deck.decodedFramesA -eq [ulong]$raw.result.firstPlayback.DecodedFrames -and
    [ulong]$deck.decodedFramesB -eq [ulong]$raw.result.secondPlayback.DecodedFrames -and
    [long]$deck.privateByteGrowth -eq [long]$raw.result.privateGrowth -and
    [int]$deck.handleGrowth -eq [int]$raw.result.handleGrowth) "Dual-deck measured values differ."
  Require (@($deck.media).Count -eq 2 -and
    $deck.media[0].sha256 -ceq $raw.result.media[0].sha256 -and
    $deck.media[1].sha256 -ceq $raw.result.media[1].sha256) "Media fingerprints differ."
}
Require ($e.stagedMixed.status -ceq "STAGED_SOFTWARE_SESSIONS_NOT_ONE_CONTINUOUS_PROGRAM") "Invalid continuous-soak claim."
$mixed = Bound-File $e.stagedMixed.evidence
$manifest = Bound-File $e.stagedMixed.manifest
& (Join-Path $PSScriptRoot "Test-SustainedMixedLoadEvidence.ps1") -EvidencePath $mixed -DescriptorPath (Join-Path $repo "qualification/soak/sustained-mixed-load.json") -ExpectedSourceCommit $ExpectedSourceCommit -ExpectedProfile $profile[0].mixed
$raw = Get-Content -LiteralPath $mixed -Raw | ConvertFrom-Json
$meta = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
Require ($meta.sourceCommit -ceq $ExpectedSourceCommit -and
  $meta.profile -ceq $profile[0].mixed -and
  [ulong]$e.stagedMixed.frames -eq [ulong]$raw.frames -and
  [ulong]$e.stagedMixed.recoveries -eq [ulong]$raw.recoveries -and
  [Math]::Abs([double]$e.stagedMixed.p99UpperBinMilliseconds - [double]$raw.latency.p99) -lt 0.000001) "Mixed workload derived values differ."
Write-Host "Exact-source real-time playout software evidence verified; physical acceptance UNVERIFIED."
