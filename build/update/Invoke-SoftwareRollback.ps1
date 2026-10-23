# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$InstallPath,
	[switch]$AcknowledgeProcessesStopped,
	[switch]$QualificationMode,
	[switch]$CoordinatedStateHandled
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Verify-Install {
	param([string]$Path, [bool]$Qualification)
	$verifier = Join-Path $Path 'tools/Test-OfflineReleaseBundle.ps1'
	Assert-Condition (Test-Path -LiteralPath $verifier -PathType Leaf) "Installation '$Path' does not contain its verifier."
	if ($Qualification) { & $verifier -BundlePath $Path } else { & $verifier -BundlePath $Path -RequireTrustedProductionKey }
}

function Move-Installation {
	param([string]$Source, [string]$Destination)
	Assert-Condition (Test-Path -LiteralPath $Source -PathType Container) "Installation to move was not found at '$Source'."
	Assert-Condition (-not (Test-Path -LiteralPath $Destination)) "Installation move destination '$Destination' already exists; existing recovery material will not be overwritten."
	Move-Item -LiteralPath $Source -Destination $Destination
}

function Test-PathWithin {
	param([string]$Path, [string]$Root)
	$fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
	$fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
	return $fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith($fullRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

Assert-Condition $AcknowledgeProcessesStopped "Rollback requires explicit acknowledgement that rtaime processes/services using the software installation have been stopped."
$target = [System.IO.Path]::GetFullPath($InstallPath)
$policyPathCandidates = @((Join-Path $PSScriptRoot 'update-policy.json'), (Join-Path $PSScriptRoot '../update/update-policy.json'))
$policyPath = $policyPathCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
Assert-Condition (-not [string]::IsNullOrWhiteSpace($policyPath)) "Update policy is unavailable."
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$rollback = "$target$([string]$policy.replacement.rollbackSlotSuffix)"
$coordinatedRecoverySuffix = if ($policy.replacement.PSObject.Properties.Name -contains 'coordinatedRecoverySuffix') { [string]$policy.replacement.coordinatedRecoverySuffix } else { '.upgrade-recovery' }
$coordinatedRecoveryRoot = "$target$coordinatedRecoverySuffix"
if ((Test-Path -LiteralPath $coordinatedRecoveryRoot) -and -not $CoordinatedStateHandled) {
	throw "Software-only rollback is blocked because coordinated persistent-state recovery evidence exists at '$coordinatedRecoveryRoot'. Use the coordinated recovery path so software and state cannot diverge."
}

Assert-Condition (Test-Path -LiteralPath $target -PathType Container) "Current installation was not found."
Assert-Condition (Test-Path -LiteralPath $rollback -PathType Container) "Rollback installation was not found at '$rollback'."
Verify-Install -Path $target -Qualification $QualificationMode
Verify-Install -Path $rollback -Qualification $QualificationMode

$parent = Split-Path -Parent $target
Assert-Condition (-not [string]::IsNullOrWhiteSpace($parent)) "Install path must have a parent directory."
$originalLocation = (Get-Location).Path
$relocatedLocation = $false
if ((Test-PathWithin -Path $originalLocation -Root $target) -or (Test-PathWithin -Path $originalLocation -Root $rollback)) {
	Set-Location -LiteralPath $parent
	$relocatedLocation = $true
}

$swap = Join-Path $parent ('.{0}.rtaime-rollback-swap-{1}' -f [System.IO.Path]::GetFileName($target), [Guid]::NewGuid().ToString('N'))
$targetMoved = $false
$rollbackActivated = $false
$previousRetainedInRollback = $false
try {
	Move-Installation -Source $target -Destination $swap
	$targetMoved = $true
	Move-Installation -Source $rollback -Destination $target
	$rollbackActivated = $true
	Verify-Install -Path $target -Qualification $QualificationMode
	Move-Installation -Source $swap -Destination $rollback
	$targetMoved = $false
	$previousRetainedInRollback = $true
	Verify-Install -Path $rollback -Qualification $QualificationMode

	Write-Host "Software rollback PASS"
	Write-Host "Active installation: $target"
	Write-Host "Previous active version retained in rollback slot: $rollback"
	Write-Host "Persistent state migration performed: false"
} catch {
	$originalError = $_
	$recoveryErrors = [System.Collections.Generic.List[Exception]]::new()
	$recoveryStatus = 'FAILED'
	try {
		if ($previousRetainedInRollback) {
			Move-Installation -Source $rollback -Destination $swap
			$targetMoved = $true
			$previousRetainedInRollback = $false
		}
		if ($rollbackActivated) {
			Move-Installation -Source $target -Destination $rollback
			$rollbackActivated = $false
		}
		if ($targetMoved) {
			Move-Installation -Source $swap -Destination $target
			$targetMoved = $false
		}
		Verify-Install -Path $target -Qualification $QualificationMode
		Verify-Install -Path $rollback -Qualification $QualificationMode
		$recoveryStatus = 'RESTORED'
	} catch {
		$recoveryErrors.Add($_.Exception)
	}

	$receiptPath = "$swap.failure.json"
	$receipt = [ordered]@{
		copyright = 'Copyright (c) Dave Beusing <david.beusing@gmail.com>.'
		schemaVersion = '1.0'
		operation = 'SOFTWARE_ROLLBACK'
		status = 'FAIL'
		recoveryStatus = $recoveryStatus
		installPath = $target
		rollbackPath = $rollback
		swapPath = $swap
		originalError = $originalError.Exception.Message
		recoveryErrors = @($recoveryErrors | ForEach-Object { $_.Message })
		completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
	}
	$receiptDetail = "Failure receipt: '$receiptPath'."
	try {
		[System.IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 16) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
	} catch {
		$recoveryErrors.Add($_.Exception)
		$receiptDetail = "Failure receipt could not be written to '$receiptPath': $($_.Exception.Message)."
	}
	$failureExceptions = [System.Collections.Generic.List[Exception]]::new()
	$failureExceptions.Add($originalError.Exception)
	foreach ($recoveryError in $recoveryErrors) { $failureExceptions.Add($recoveryError) }
	$recoveryDetail = if ($recoveryErrors.Count -eq 0) { 'Previous active and rollback installations were restored and verified.' } else { 'Recovery errors: ' + (($recoveryErrors | ForEach-Object { $_.Message }) -join '; ') }
	$message = "Software rollback failed. Original error: $($originalError.Exception.Message). Recovery status: $recoveryStatus. $recoveryDetail Active path: '$target'; rollback path: '$rollback'; retained swap path: '$swap'. $receiptDetail"
	throw [System.AggregateException]::new($message, $failureExceptions.ToArray())
} finally {
	# A remaining swap may be the only copy of the previous active installation.
	# Successful activation or compensation moves it into a verified installation slot.
	if ($relocatedLocation) {
		if (Test-Path -LiteralPath $originalLocation -PathType Container) { Set-Location -LiteralPath $originalLocation } else { Set-Location -LiteralPath $parent }
	}
}
