# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$paths = @{
	contracts = "src/Contracts/rtaime.Media.Contracts/TimedTextContracts.cs"
	codec = "src/Client/rtaime.Client/TimedTextInterchangeCodec.cs"
	store = "src/Client/rtaime.Client/TimedTextCueStore.cs"
	review = "src/Hosts/rtaime.Operator/TimedTextReviewViewModel.cs"
	timeline = "src/Hosts/rtaime.Operator/MediaTimelineViewModel.cs"
	control = "src/Hosts/rtaime.Operator/MediaTimelineControl.xaml.cs"
	xaml = "src/Hosts/rtaime.Operator/MediaTimelineControl.xaml"
	unit = "tests/rtaime.Tests.Unit/TimedTextInterchangeTests.cs"
	contractTests = "tests/rtaime.Tests.Contracts/TimedTextContractTests.cs"
	operatorTests = "tests/rtaime.Tests.Operator/TimedTextReviewTests.cs"
	documentation = "docs/TimedTextInterchange.md"
	timelineDocumentation = "docs/MediaTimelineSeeker.md"
}
$source = @{}
foreach ($item in $paths.GetEnumerator()) {
	$path = Join-Path $root $item.Value
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Timed Text evidence artifact missing: $($item.Value)"
	$source[$item.Key] = Get-Content -LiteralPath $path -Raw
}

Assert-Condition ($source.contracts.Contains('EbuTtDistribution101') -and
	$source.contracts.Contains('EbuTtLive10') -and
	$source.contracts.Contains('TimedTextCue') -and
	$source.contracts.Contains('MaxCues = 256') -and
	$source.contracts.Contains('MaxXmlBytes = 262_144')) "Timed Text contracts must be separately versioned, bounded and not alias media markers or CG."
Assert-Condition ($source.codec.Contains('urn:ebu:tt:distribution:2018-04') -and
	$source.codec.Contains('urn:ebu:tt:live:2017-05')) "EBU-TT-D 1.0.1 and Live 1.0 profile identifiers must be pinned."
Assert-Condition ($source.codec.Contains('DtdProcessing = DtdProcessing.Prohibit') -and
	$source.codec.Contains('XmlResolver = null') -and
	$source.codec.Contains('MaxCharactersInDocument = TimedTextDocument.MaxXmlBytes') -and
	$source.codec.Contains('new UTF8Encoding(false, true)')) "Timed Text importer must explicitly prohibit XXE and unbounded/malformed XML."
Assert-Condition ($source.codec.Contains('AllowAttributes(') -and
	$source.codec.Contains('Unknown metadata could conceal') -and
	$source.codec.Contains('Mixed inline styling requires')) "Unqualified TTML semantics must reject rather than silently change caption content."
Assert-Condition ($source.codec.Contains('XmlWriter.Create') -and
	$source.codec.Contains('document.Save(writer)')) "Timed Text must implement deterministic bounded XML export."
Assert-Condition ($source.store.Contains('decimal.Ceiling') -and
	$source.store.Contains('TimeSpan.TicksPerSecond * (decimal)rate.Denominator') -and
	$source.store.Contains('FirstFrameAtOrAfter(cue.EndTicks')) "Frame windows must use exact rational half-open interval scheduling."
Assert-Condition ($source.store.Contains('RequiresResynchronization') -and
	$source.store.Contains('SignalGap') -and
	$source.store.Contains('transportUncertain') -and
	$source.store.Contains('TimedTextTimeBasis.Media')) "Live gaps and unknown clock/transport must fail closed and not retain stale captions."
Assert-Condition ($source.store.Contains('public bool IsProgramOutput => false;') -and
	$source.review.Contains('public bool ProgramOverlayAdmitted => false;')) "Local caption preview must not be misrepresented as Program compositing."
Assert-Condition ($source.review.Contains('TimedTextSourceContext') -and
	$source.review.Contains('ContextEpoch') -and
	$source.review.Contains('caption.font_missing') -and
	$source.review.Contains('caption.glyph_missing')) "Operator preview must be source-fenced and show font/glyph failures."
Assert-Condition ($source.control.Contains('FileOptions.Asynchronous') -and
	$source.control.Contains('TimedTextReview.Import(bytes, profile, epoch)') -and
	$source.xaml.Contains('Binding TimedTextReview.PreviewText') -and
	$source.xaml.Contains('RtaimeButton Content="IMPORT EBU-TT')) "Real Timeline must provide bounded local import and custom read-only caption preview."
foreach ($key in @('review','control','store','codec')) {
	Assert-Condition ($source[$key] -notmatch 'ApplyProductionCgText\s*\(|CutProgramAsync\s*\(|RouteOutputRoleAsync\s*\(|ApplyCommittedExecution\s*\(') "Timed Text must not bypass Production Authority from $key."
}
foreach ($test in @(
	'Exact_60000_over_1001_timing_has_no_rounded_59_94_drift',
	'Xxe_and_external_entity_declarations_are_rejected_without_network_or_file_access',
	'Unsupported_inline_style_external_resources_and_images_are_not_silently_dropped',
	'Missing_segment_duplicate_gap_and_discontinuity_require_explicit_resynchronization',
	'Source_bound_preview_reacts_to_pause_seek_transport_unknown_and_source_replacement'
)) {
	Assert-Condition ($source.unit.Contains($test)) "Timed Text functional/failure test missing: $test"
}
Assert-Condition ($source.operatorTests.Contains('Actual_timeline_caption_preview_is_source_fenced') -and
	$source.operatorTests.Contains('ProgramOverlayAdmitted')) "Operator must qualify source identity, confirmed frame and Program isolation."
Assert-Condition ($source.documentation.Contains('UNVERIFIED') -and
	$source.documentation.Contains('EBU-TT-D') -and
	$source.documentation.Contains('EBU-TT Live') -and
	$source.documentation.Contains('prepare/commit') -and
	$source.timelineDocumentation.Contains('Timed Text')) "Documentation must distinguish software subset from standards/hardware and authority evidence."

Write-Host "Timed Text secure XML, source-bound preview and Program isolation policy PASS"
Write-Host "EBU-TT-D v1.0.1 / EBU-TT Live v1.0 subset; native Program/SDI output and complete EBU conformance UNVERIFIED"
