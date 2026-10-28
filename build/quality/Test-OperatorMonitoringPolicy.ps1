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

$contractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Media.Contracts/MonitoringContracts.cs"
$runtimeMonitoringPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeMonitoring.cs"
$runtimeServicePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeProgramExecutionService.cs"
$runtimeIpcPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostIpcServer.cs"
$controlRuntimeIpcPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/RuntimeHostIpcTransport.cs"
$gpuProviderPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs"
$clientPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/NamedPipeOperatorMonitoringTransport.cs"
$operatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorMonitoringViewModel.cs"
$gpuPresentationPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Controls/GpuMonitorPresentationSurface.cs"
$gpuScopesPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Controls/GpuScopeAnalysisSurface.cs"
$gpuComparePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Controls/GpuMediaCompareSurface.cs"
$scopeAnalysisPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaScopeAnalysis.cs"
$pixelInspectionPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaPixelInspection.cs"
$roiOverlayPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Controls/RtaimeRoiOverlay.cs"
$monitorThemePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/Controls/RtaimeMonitorWorkspace.xaml"
$mainWindowPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MainWindow.xaml"
$cleanProgramPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramOutputWindow.xaml"
$cudaMonitoringInteropPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/CudaD3D11MonitoringInterop.cs"
$documentationPath = Join-Path $repositoryRoot "docs/OperatorMonitoringPlane.md"
$qualificationDocumentationPath = Join-Path $repositoryRoot "docs/MonitoringVisualPerformanceQualification.md"
$qualificationTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Operator/MonitoringVisualPerformanceQualificationTests.cs"
$monitoringIntegrationTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/OperatorMonitoringPlaneTests.cs"

foreach ($path in @($contractsPath, $runtimeMonitoringPath, $runtimeServicePath, $runtimeIpcPath, $controlRuntimeIpcPath, $gpuProviderPath, $clientPath, $operatorPath, $gpuPresentationPath, $gpuScopesPath, $gpuComparePath, $scopeAnalysisPath, $pixelInspectionPath, $roiOverlayPath, $monitorThemePath, $mainWindowPath, $cleanProgramPath, $cudaMonitoringInteropPath, $documentationPath, $qualificationDocumentationPath, $qualificationTestsPath, $monitoringIntegrationTestsPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required monitoring-plane artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$runtimeMonitoring = Get-Content -LiteralPath $runtimeMonitoringPath -Raw
$runtimeService = Get-Content -LiteralPath $runtimeServicePath -Raw
$runtimeIpc = Get-Content -LiteralPath $runtimeIpcPath -Raw
$controlRuntimeIpc = Get-Content -LiteralPath $controlRuntimeIpcPath -Raw
$gpuProvider = Get-Content -LiteralPath $gpuProviderPath -Raw
$client = Get-Content -LiteralPath $clientPath -Raw
$operator = Get-Content -LiteralPath $operatorPath -Raw
$gpuPresentation = Get-Content -LiteralPath $gpuPresentationPath -Raw
$gpuScopes = Get-Content -LiteralPath $gpuScopesPath -Raw
$gpuCompare = Get-Content -LiteralPath $gpuComparePath -Raw
$scopeAnalysis = Get-Content -LiteralPath $scopeAnalysisPath -Raw
$pixelInspection = Get-Content -LiteralPath $pixelInspectionPath -Raw
$roiOverlay = Get-Content -LiteralPath $roiOverlayPath -Raw
$monitorTheme = Get-Content -LiteralPath $monitorThemePath -Raw
$mainWindow = Get-Content -LiteralPath $mainWindowPath -Raw
$cleanProgram = Get-Content -LiteralPath $cleanProgramPath -Raw
$cudaMonitoringInterop = Get-Content -LiteralPath $cudaMonitoringInteropPath -Raw
$qualificationDocumentation = Get-Content -LiteralPath $qualificationDocumentationPath -Raw
$qualificationTests = Get-Content -LiteralPath $qualificationTestsPath -Raw
$monitoringIntegrationTests = Get-Content -LiteralPath $monitoringIntegrationTestsPath -Raw

Assert-Condition ($contracts -match 'MonitoringContractVersion') "Monitoring must expose an explicit versioned contract."
Assert-Condition ($contracts -match 'MonitoringStreamKind') "Monitoring must distinguish source and Program streams."
Assert-Condition ($contracts -match 'MonitoringFrameWire') "Monitoring must use an explicit bounded wire frame rather than management JSON envelopes."
Assert-Condition ($contracts -match 'MonitoringSharedResourceDescriptor') "Monitoring must expose a provider-neutral shared GPU resource descriptor."
Assert-Condition ($contracts -match 'MonitoringResourceAccessMode' -and $contracts -match 'ReadOnly') "Shared monitoring resources must be explicitly read-only."
Assert-Condition ($contracts -notmatch 'CUDA|DevicePointer|nvcuda|cuMem|CUdevice') "Stable monitoring contracts must not expose CUDA or raw device-pointer terminology."
Assert-Condition ($contracts -notmatch 'OpaqueSurfaceHandle') "Monitoring wire contracts must publish rtaime-owned resource identity rather than provider-internal surface handles."

Assert-Condition ($runtimeMonitoring -match 'SampleStride\s*=\s*4') "V1 monitoring must remain deliberately sampled below the production frame rate."
Assert-Condition ($runtimeMonitoring -match 'MonitorWidth\s*=\s*320' -and $runtimeMonitoring -match 'MonitorHeight\s*=\s*180') "V1 monitoring must use the qualified 320x180 visual payload."
Assert-Condition ($runtimeMonitoring -match '_pending\s*=\s*sample') "Runtime monitoring must use a single pending sample that can be replaced under load."
Assert-Condition ($runtimeMonitoring -match '_dropped\+\+') "Runtime monitoring must account for dropped monitor frames under pressure."
Assert-Condition ($runtimeMonitoring -match 'capacity:\s*4') "Monitoring subscribers must retain one complete bounded source/source/Program observation set."
Assert-Condition ($runtimeMonitoring -match 'PipeDirection\.Out') "RuntimeHost monitoring must be output-only and incapable of carrying authority mutations."
Assert-Condition ($runtimeMonitoring -match 'RuntimeHostMonitoringServer') "RuntimeHost must expose a dedicated monitoring server."
Assert-Condition ($runtimeMonitoring -match 'RequiresCpuFallback') "Monitoring subscriptions must explicitly negotiate whether the CPU fallback payload is required."
Assert-Condition ($runtimeMonitoring -match 'SharedResourceCapability') "Monitoring observations must distinguish shared-resource capability from current-frame resource availability."
Assert-Condition ($runtimeMonitoring -match 'SubscriberAvailabilityChanged') "Shared monitoring resources must react to subscriber disconnect without blocking RuntimeHost."

Assert-Condition ($runtimeService -match '_monitoringTap\.TryCapture') "Committed Runtime frames must feed the monitoring tap."
Assert-Condition ($runtimeService -match 'using var mappedOutput = TryRenderMappedOutput\(output,' -and $runtimeService -match 'var presentedOutput = mappedOutput \?\? output;' -and $runtimeService -match '_gpu\.RentReadback\(presentedOutput\)') "Program monitoring must derive from the actual post-mapping Program output through reusable owned readback memory."
Assert-Condition ($runtimeMonitoring -match 'GpuReadbackLease program' -and $runtimeMonitoring -match 'program\.Retain\(\)') "Asynchronous Program monitoring must retain explicit readback ownership before the Runtime boundary can release it."
Assert-Condition ($runtimeIpc -notmatch 'MonitoringFrameWire|MonitoringFrameDescriptor|monitor\.frame') "Management Runtime IPC must not carry monitoring pixel payloads."

Assert-Condition ($client -match 'NamedPipeOperatorMonitoringTransport') "Client SDK must expose the independent monitoring transport."
Assert-Condition ($client -notmatch 'SelectPreviewAsync|CutProgramAsync|DissolveProgramAsync') "Monitoring transport must not expose production mutations."
Assert-Condition ($operator -match 'MonitoringStreamKind\.Program') "Operator must render actual Program stream frames distinctly."
Assert-Condition ($operator -match 'PreviewSourceId') "Operator Preview must select monitoring frames using authoritative control routing."
Assert-Condition ($operator -match 'SharedGpuMonitoringState') "Operator must expose the negotiated shared GPU monitoring state."
Assert-Condition ($gpuProvider -match 'PublishedMonitoringResourceSetSize\s*=\s*2' -and $gpuProvider -match 'SharedMonitoringResourceCapacity\s*=\s*PublishedMonitoringResourceSetSize\s*\*\s*2') "Shared GPU monitoring must retain one bounded replacement pair of headroom while keeping the published Preview/Program set fixed at two resources."
Assert-Condition ($gpuPresentation -match 'OpenSharedResource' -and $gpuPresentation -match 'WindowsGraphicsSharedHandle') "Operator GPU presentation must open the negotiated read-only Windows graphics resource rather than materialize another frame."
Assert-Condition ($gpuPresentation -notmatch 'BitmapSource|CopyPixels|CopySubresourceRegion') "Operator GPU inspection must not materialize a CPU bitmap or copy ROI/full-frame pixels to CPU staging."
Assert-Condition ($gpuPresentation -match 'CSMain' -and $gpuPresentation -match 'MaxAnalysisSamples\s*=\s*262_144' -and $gpuPresentation -match 'AnalysisResultCount\s*=\s*16') "ROI analysis must remain GPU-reduced and explicitly bounded."
Assert-Condition ($gpuPresentation -match 'TimeSpan\.FromMilliseconds\(200\)') "ROI analysis must remain rate-limited independently from presentation cadence."
Assert-Condition ($gpuPresentation -match 'AnalysisDuration\s*=\s*Stopwatch\.GetElapsedTime\(started\)' -and $gpuPresentation -match 'ResultTransferBytes\s*=\s*AnalysisResultCount\s*\*\s*sizeof\(uint\)' -and $gpuPresentation -match 'ManagedAllocationBytes\s*=\s*GC\.GetAllocatedBytesForCurrentThread') "ROI analysis must expose measured duration, fixed result transfer and managed-allocation qualification evidence."
Assert-Condition ($pixelInspection -match 'TimeSpan AnalysisDuration' -and $pixelInspection -match 'int ResultTransferBytes' -and $pixelInspection -match 'long ManagedAllocationBytes') "ROI statistics must retain bounded performance evidence in the Operator model."
Assert-Condition ($roiOverlay -match 'MediaInspectionRoi' -and $roiOverlay -match 'SourceWidth' -and $roiOverlay -match 'SourceHeight') "ROI overlay must project source-space inspection coordinates."
Assert-Condition ($scopeAnalysis -match 'MaxScopeSamples\s*=\s*262_144' -and $scopeAnalysis -match 'ScopeUpdateInterval\s*=\s*TimeSpan\.FromMilliseconds\(200\)') "GPU scope analysis must remain bounded to the qualified sample count and at most 5 Hz."
Assert-Condition ($scopeAnalysis -match 'MediaScopeProcessingPath\.CpuFallback' -and $scopeAnalysis -match 'MediaScopeProcessingPath\.GpuSharedResource') "Scope snapshots must identify GPU analysis versus CPU fallback explicitly."
Assert-Condition ($scopeAnalysis -match 'unknown color metadata is not guessed' -and $scopeAnalysis -match 'VectorscopeAvailable') "Scopes and Difference must reject or mark unavailable unknown color semantics rather than guessing."
Assert-Condition ($gpuScopes -match 'OpenSharedResource' -and $gpuScopes -match 'RWStructuredBuffer<uint> Results' -and $gpuScopes -match 'CopyResource\(_readback!, _results!\)') "GPU scopes must analyze the read-only shared resource and return only the fixed derived result buffer."
Assert-Condition ($gpuScopes -notmatch 'BitmapSource|CopyPixels|CopySubresourceRegion') "GPU scopes must not materialize or stage a full CPU frame."
Assert-Condition ($gpuCompare -match 'OpenSharedResource' -and $gpuCompare -match 'abs\(a\.rgb - b\.rgb\)') "GPU A/B Difference must execute on the shared presentation resources."
Assert-Condition ($gpuCompare -notmatch 'BitmapSource|CopyPixels|CopyResource|CopySubresourceRegion') "GPU A/B compare must remain presentation-only without CPU frame materialization."
Assert-Condition ($operator -match 'cpuScopesRequired\s*=\s*!descriptor\.HasSharedResource\s*\|\|' -and $operator -match 'SetGpuScopesUnavailable' -and $operator -match 'SetGpuComparisonState') "Operator scheduling must prefer GPU analysis and activate CPU fallback only when the GPU path is unavailable."
Assert-Condition ($mainWindow -match 'GpuScopeAnalysisSurface' -and $mainWindow -match 'GpuMediaCompareSurface' -and $mainWindow -match 'RtaimeMediaCompareView') "Operator monitoring UI must layer GPU scopes/compare over the retained CPU fallback."
Assert-Condition ($monitorTheme -match 'GpuMonitorPresentationSurface' -and $monitorTheme -match 'InspectionChannel' -and $monitorTheme -match 'RtaimeRoiOverlay' -and $monitorTheme -match 'IsGpuPresentationActive' -and $monitorTheme -match 'BitmapScalingMode="HighQuality"') "Shared monitor presentation must retain one GPU surface, channel/ROI inspection and the qualified WPF fallback."
Assert-Condition ($cleanProgram -match 'GpuFrame="{Binding ProgramGpuFrame}"' -and $cleanProgram -match 'Source="{Binding ProgramImage}"') "Clean Program must reuse the existing Program GPU resource and CPU fallback projection."
Assert-Condition ($cleanProgram -notmatch 'RtaimePixelGridOverlay|DiagnosticsHud|ShowGrid|ShowSafeArea|ShowCenterMark|GpuScopeAnalysisSurface|GpuMediaCompareSurface|RtaimeMediaScope|RtaimeMediaCompareView') "Clean Program must not add Operator diagnostic, scope, compare or inspection overlays."
Assert-Condition ($cudaMonitoringInterop -match 'cuMemcpy2D_v2' -and $cudaMonitoringInterop -notmatch 'cuMemcpyDtoH|cuMemcpyDtoHAsync') "CUDA/D3D11 monitor export must remain GPU-resident and must not introduce a GPU-to-CPU readback."
Assert-Condition ($runtimeService -match 'previewSharedMonitoringResource' -and $runtimeService -match 'programSharedMonitoringResource') "RuntimeHost must publish bounded shared Preview and post-composite Program resources through the existing monitoring tap."
Assert-Condition ($qualificationTests -match 'Gpu_and_wpf_paths_share_the_same_physical_geometry' -and $qualificationTests -match 'GC\.GetAllocatedBytesForCurrentThread' -and $qualificationTests -match 'Sustained_resize_and_dpi_churn') "Unified monitoring qualification must cover GPU/WPF geometry parity, steady-state allocation and sustained DPI/resize churn."
Assert-Condition ($monitoringIntegrationTests -match 'Dedicated_named_pipe_monitoring_transport_reconnects_after_server_restart' -and $monitoringIntegrationTests -match 'Sustained_shared_monitoring_replacement_remains_bounded_and_returns_to_baseline' -and $monitoringIntegrationTests -match 'RTAIME_MONITORING_QUALIFICATION_SOAK_LONG') "Monitoring integration qualification must cover reconnect and bounded shared-resource soak behavior."
Assert-Condition ($qualificationDocumentation -match 'Software performance envelope' -and $qualificationDocumentation -match 'Physical.*UNVERIFIED' -and $qualificationDocumentation -match 'RTAIME_MONITORING_QUALIFICATION_SOAK_LONG') "Monitoring qualification documentation must separate software evidence from physical qualification and document sustained qualification."

# R 103 extends this existing monitoring quality gate, not a second security framework.
$rangeContractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Media.Contracts/VideoRangeContracts.cs"
$rangePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/VideoSignalRangeAnalysis.cs"
$rangeTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Operator/VideoSignalRangeAnalysisTests.cs"
$reviewViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ColorReviewViewModel.cs"
$reviewXamlPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ColorReviewWorkspaceControl.xaml"
$rangeDocumentationPath = Join-Path $repositoryRoot "docs/VideoSignalRangeCompatibility.md"
$colorDocumentationPath = Join-Path $repositoryRoot "docs/ColorAccurateMediaMonitoring.md"
foreach ($path in @($rangeContractsPath, $rangePath, $rangeTestsPath, $reviewViewModelPath, $reviewXamlPath, $rangeDocumentationPath, $colorDocumentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "R 103 code-range evidence artifact is missing: $path"
}
$rangeContracts = Get-Content -LiteralPath $rangeContractsPath -Raw
$range = Get-Content -LiteralPath $rangePath -Raw
$rangeTests = Get-Content -LiteralPath $rangeTestsPath -Raw
$reviewModel = Get-Content -LiteralPath $reviewViewModelPath -Raw
$reviewXaml = Get-Content -LiteralPath $reviewXamlPath -Raw
$rangeDocumentation = Get-Content -LiteralPath $rangeDocumentationPath -Raw
$colorDocumentation = Get-Content -LiteralPath $colorDocumentationPath -Raw

Assert-Condition ($rangeContracts.Contains("8 => new(16, 235, 5, 246, 1, 254)") -and $rangeContracts.Contains("10 => new(64, 940, 20, 984, 4, 1019)")) "R 103 v3 numeric nominal/preferred/total thresholds must retain the normative 8/10-bit code bands."
Assert-Condition ($range.Contains("(long)union.OutsidePreferred * 100 > samples") -and $range.Contains("(long)AnyComponent.OutsidePreferred * 100 > SampleCount")) "Out-of-gamut threshold must be strictly greater than 1% of sampled active picture area."
Assert-Condition ($rangeContracts.Contains("ColorMetadataAuthority.Authoritative") -and $rangeContracts.Contains("NominalRange.Full") -and $rangeContracts.Contains("ObservedCodeBitDepth") -and $rangeContracts.Contains("ActiveImage")) "R 103 must reject unsupported or untrusted range/precision metadata and exclude non-active image."
Assert-Condition ($range.Contains("SampledOutsideTotal") -and $range.Contains("ChromaStatus") -and $range.Contains("UNAVAILABLE")) "R 103 must distinguish total excursion and unavailable native chroma evidence."
Assert-Condition ($scopeAnalysis.Contains("signalCounts.Add") -and $scopeAnalysis.Contains("pixels[offset]") -and $scopeAnalysis.Contains("SignalRange = signalCounts.Finish")) "CPU scope should count source codes on the existing sampled frame before presentation conversion."
Assert-Condition ($scopeAnalysis.Contains("CombinedResultBytes") -and $scopeAnalysis.Contains("SignalResultCount") -and $scopeAnalysis.Contains("ScopeResultCount")) "New raw-code counters must be additive to the bounded existing scope shape."
Assert-Condition ($gpuScopes.Contains("CountRawExcursion") -and $gpuScopes.Contains("uint rawR") -and $gpuScopes.Contains("SignalEnabled") -and $gpuScopes.Contains("CombinedResultBytes")) "GPU scope must count raw code values only on eligible source semantics in its existing fixed compute pass."
Assert-Condition ($gpuScopes.IndexOf("uint rawR") -lt $gpuScopes.IndexOf("sampleColor.r = TransformComponent")) "GPU raw-code range analysis must precede display transform."
Assert-Condition ($gpuScopes -notmatch 'BitmapSource|CopyPixels|CopySubresourceRegion' -and $cleanProgram -notmatch 'VideoSignalRange|SignalRange') "Signal measurement must never introduce a CPU frame readback or Program output overlay."
Assert-Condition ($reviewModel.Contains("SignalRangeStatusLabel") -and $reviewModel.Contains("SelectedScopes?.SignalRange") -and $reviewXaml.Contains("Binding SignalRangeStatusLabel")) "Color Review must show source-fenced range results instead of creating another scope owner."
foreach ($test in @(
	"Ebu_r103_eight_bit_threshold_edges_use_original_limited_codes",
	"Ebu_r103_ten_bit_threshold_edges_do_not_quantize_to_eight_bits",
	"Out_of_gamut_threshold_exceeds_one_percent_of_sampled_active_image_not_one_bad_pixel",
	"Active_image_region_excludes_code_excursions_in_surrounding_border",
	"Unknown_inferred_full_range_and_hdr_like_color_are_not_a_legal_level_pass",
	"Eight_bit_monitoring_of_ten_bit_source_reports_unavailable_not_fake_ten_bit_legal_check",
	"Signal_counters_round_trip_through_fixed_additive_gpu_scope_buffer"
)) {
	Assert-Condition ($rangeTests.Contains($test)) "R 103 negative test is missing: $test"
}
Assert-Condition ($rangeDocumentation.Contains("UNVERIFIED") -and $rangeDocumentation.Contains("UNAVAILABLE") -and $rangeDocumentation.Contains("86,076") -and $colorDocumentation.Contains("unfiltered sparse samples")) "R 103 evidence must document native precision, scope sample bounds and lack of full-frame compliance."

Write-Host "EBU R 103 v3 sampled signal-range structural qualification PASS"
Write-Host "Original Program pixels remain read-only; R 103 full-frame/hardware conformity UNVERIFIED"

Write-Host "Operator monitoring plane policy verification PASS"
Write-Host "Authority: none; output-only monitoring transport"
Write-Host "Backpressure: bounded and loss-tolerant"
Write-Host "Program continuity: independent from monitoring consumers"
