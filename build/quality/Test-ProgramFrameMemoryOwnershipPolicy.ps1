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

$gpuPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs"
$cudaPath = Join-Path $repositoryRoot "src/Providers/rtaime.Provider.Gpu/CudaGpuProcessingBackend.cs"
$runtimePath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs"
$runtimeProcessPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs"
$monitoringPath = Join-Path $repositoryRoot "src/Hosts/rtaime.RuntimeHost/RuntimeMonitoring.cs"
$mediaIoPath = Join-Path $repositoryRoot "src/Media/rtaime.Media/MediaIoVerticalSlice.cs"
$recordingPath = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/ReferenceRecordingPayloadWriter.cs"
$mp4Path = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/WindowsMediaFoundationMp4RecordingWriter.cs"
$movPath = Join-Path $repositoryRoot "src/Recording/rtaime.Recording/ManagedQuickTimeMovRecordingWriter.cs"
$gpuTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Unit/GpuProcessingTests.cs"
$integrationTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ProgramFrameMemoryOwnershipTests.cs"
$monitoringTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/OperatorMonitoringPlaneTests.cs"
$performanceTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Performance/GpuProcessingPerformanceTests.cs"
$documentationPath = Join-Path $repositoryRoot "docs/ProgramFrameMemoryOwnership.md"

foreach ($path in @(
	$gpuPath,
	$cudaPath,
	$runtimePath,
	$runtimeProcessPath,
	$monitoringPath,
	$mediaIoPath,
	$recordingPath,
	$mp4Path,
	$movPath,
	$gpuTestsPath,
	$integrationTestsPath,
	$monitoringTestsPath,
	$performanceTestsPath,
	$documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required Program frame ownership artifact is missing: '$path'."
}

$gpu = Get-Content -LiteralPath $gpuPath -Raw
$cuda = Get-Content -LiteralPath $cudaPath -Raw
$runtime = Get-Content -LiteralPath $runtimePath -Raw
$runtimeProcess = Get-Content -LiteralPath $runtimeProcessPath -Raw
$monitoring = Get-Content -LiteralPath $monitoringPath -Raw
$mediaIo = Get-Content -LiteralPath $mediaIoPath -Raw
$recording = Get-Content -LiteralPath $recordingPath -Raw
$mp4 = Get-Content -LiteralPath $mp4Path -Raw
$mov = Get-Content -LiteralPath $movPath -Raw
$gpuTests = Get-Content -LiteralPath $gpuTestsPath -Raw
$integrationTests = Get-Content -LiteralPath $integrationTestsPath -Raw
$monitoringTests = Get-Content -LiteralPath $monitoringTestsPath -Raw
$performanceTests = Get-Content -LiteralPath $performanceTestsPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($gpu -match 'public sealed class GpuReadbackLease : IDisposable') "GPU provider must expose explicit disposable Program readback ownership."
Assert-Condition ($gpu -match 'class GpuReadbackBufferPool : IDisposable' -and $gpu -match 'ExhaustedRents') "GPU readback memory must use a fixed-capacity pool with exhaustion evidence."
Assert-Condition ($gpu -match 'void ReadbackInto\(SurfaceId surfaceId, VideoFormat format, Span<byte> destination\)') "GPU backend contract must support caller-supplied readback memory."
Assert-Condition ($gpu -match 'RentReadback\(GpuFrame frame\)' -and $gpu -match 'ReadbackPoolStatistics') "GPU provider must expose leased readback and bounded pool statistics."

Assert-Condition ($cuda -match 'public void ReadbackInto' -and $cuda -match 'cuMemcpyDtoH_v2\(ref destinationReference') "CUDA readback must copy directly into caller-supplied host memory."
Assert-Condition ($cuda -match 'cuMemcpyDtoH_v2\(ref byte destination') "CUDA P/Invoke must use a call-lifetime managed by-reference rather than requiring a fresh managed array."

Assert-Condition ($runtime -match 'public sealed class V1ProgramBoundaryResult : IDisposable') "Runtime Program boundaries must own disposable Program readback memory."
Assert-Condition ($runtime -match 'ReadOnlyMemory<byte> ProgramPixels') "Runtime Program pixels must expose read-only leased memory."
Assert-Condition ($runtime -match 'ProgramReadbackBufferCapacity\s*=\s*ProgramRecorder\.DefaultQueueCapacity \+ 3') "RuntimeHost readback capacity must cover bounded queue backlog, active recording write, current Program boundary and monitoring ownership."
Assert-Condition ($runtime -match '_gpu\.RentReadback\(output\)' -and $runtime -notmatch '_gpu\.Readback\(output\)') "RuntimeHost Program hot path must use reusable leased readback rather than allocating compatibility readback."
Assert-Condition ($runtime -match 'GpuRecordingPayloadLease\(pixels\.Retain\(\)\)') "Recording handoff must retain Program readback ownership explicitly."
Assert-Condition ($runtime -match 'readback\.ActiveBuffers != 0') "Runtime shutdown must fail closed if Program readback leases remain active."
Assert-Condition ($runtimeProcess -match 'using var boundary = runtime\.ProcessNextBoundary\(\)') "Production RuntimeHost loop must dispose each Program boundary deterministically."

Assert-Condition ($monitoring -match 'GpuReadbackLease program' -and $monitoring -match 'program\.Retain\(\)') "Asynchronous monitoring must retain Program memory before the primary owner can release it."
Assert-Condition ($monitoring -match 'replaced\?\.Program\.Dispose\(\)' -and $monitoring -match 'using \(sample\.Program\)') "Monitoring replacement and processing must release retained Program memory."

Assert-Condition ($mediaIo -match 'ReadOnlyMemory<byte> rgbaPixels' -and $mediaIo -match 'MemoryMarshal\.TryGetArray') "Physical Program output must borrow the array-backed leased Program buffer."
Assert-Condition ($mediaIo -match 'GCHandle\.Alloc\(videoSegment\.Array, GCHandleType\.Pinned\)') "Physical Program output must pin only the borrowed existing Program array for synchronous submit."

Assert-Condition ($recording -match 'public interface IProgramRecordingPayloadLease : IDisposable') "Recording payload ownership must be explicit and subsystem-local."
Assert-Condition ($recording -match 'StagePayload\(ulong sequenceNumber, IProgramRecordingPayloadLease videoPayload') "Recording payload writer must accept owned Program video leases."
Assert-Condition ($recording -match 'payload\.VideoLease\.Dispose\(\)' -and $recording -match 'ReleaseStagedPayloadsUnsafe') "Reference recording writer must release payload ownership on write and cleanup."
Assert-Condition ($mp4 -match 'payload\.VideoLease\.Dispose\(\)' -and $mp4 -match 'ReleaseStagedPayloadsUnsafe') "MP4 recording writer must release payload ownership on write and cleanup."
Assert-Condition ($mov -match 'payload\.VideoLease\.Dispose\(\)' -and $mov -match 'ReleaseStagedPayloadsUnsafe') "MOV recording writer must release payload ownership on write and cleanup."

Assert-Condition ($gpuTests -match 'Readback_lease_reuses_a_bounded_buffer_after_release') "GPU lease reuse regression coverage is required."
Assert-Condition ($gpuTests -match 'Retained_readback_memory_is_not_reused_or_mutated_by_a_later_frame') "GPU retained-memory aliasing regression coverage is required."
Assert-Condition ($gpuTests -match 'Readback_failure_returns_the_rented_buffer_to_the_pool') "GPU readback failure cleanup regression coverage is required."
Assert-Condition ($integrationTests -match 'Repeated_program_boundaries_reuse_a_bounded_readback_buffer') "Runtime long-run readback reuse regression coverage is required."
Assert-Condition ($integrationTests -match 'Asynchronous_recording_keeps_its_program_lease_until_the_writer_finishes') "Asynchronous recording ownership regression coverage is required."
Assert-Condition ($monitoringTests -match 'programPixels\.Dispose\(\)') "Monitoring integration coverage must prove the primary readback owner can release before async monitoring completes."
Assert-Condition ($performanceTests -match 'Reusable_1080p_readback_has_no_full_frame_per_iteration_managed_allocation') "1080p allocated-byte regression coverage is required."
Assert-Condition ($performanceTests -match 'GC\.GetAllocatedBytesForCurrentThread') "Allocation regression must measure managed allocated bytes after warmup."

Assert-Condition ($documentation -match 'last reference returns host buffer to bounded readback pool') "Program frame ownership documentation must describe final-reference pool return."
Assert-Condition ($documentation -match 'current recording queue capacity is 64' -and $documentation -match 'V1 bound is 67 buffers') "Documentation must record the bounded V1 pool capacity rationale."
Assert-Condition ($documentation -match 'CUDA reference hardware remains UNVERIFIED') "Documentation must preserve the physical CUDA evidence boundary."

Write-Host "Program frame memory ownership policy verification PASS"
Write-Host "Program readback: reusable bounded leases"
Write-Host "Async consumers: explicit retained ownership"
Write-Host "Physical CUDA qualification: UNVERIFIED unless dedicated evidence passes"
