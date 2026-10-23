# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

function Get-InstallationSnapshot {
	param([string]$Path)
	$snapshot = @{}
	foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse -Force) {
		$relative = [System.IO.Path]::GetRelativePath($Path, $file.FullName)
		$snapshot[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
	}
	return $snapshot
}

function Assert-InstallationSnapshot {
	param([string]$Path, [hashtable]$Expected, [string]$Description)
	Assert-Condition (Test-Path -LiteralPath $Path -PathType Container) "$Description is missing at '$Path'."
	$actual = Get-InstallationSnapshot -Path $Path
	Assert-Condition ($actual.Count -eq $Expected.Count) "$Description file count changed."
	foreach ($name in $Expected.Keys) {
		Assert-Condition ($actual.ContainsKey($name) -and $actual[$name] -eq $Expected[$name]) "$Description bytes changed for '$name'."
	}
}

function New-InstallationFixture {
	param([string]$Path, [string]$Identity, [byte[]]$Payload)
	New-Item -ItemType Directory -Path (Join-Path $Path 'tools') -Force | Out-Null
	[System.IO.File]::WriteAllBytes((Join-Path $Path 'payload.bin'), $Payload)
	$manifest = [ordered]@{
		identity = $Identity
		payloadSha256 = (Get-FileHash -LiteralPath (Join-Path $Path 'payload.bin') -Algorithm SHA256).Hash
	}
	[System.IO.File]::WriteAllText((Join-Path $Path 'bundle-manifest.json'), ($manifest | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
	$verifier = @'
param([string]$BundlePath, [switch]$RequireTrustedProductionKey)
Test-RollbackFixtureVerification -Path $BundlePath
'@
	[System.IO.File]::WriteAllText((Join-Path $Path 'tools/Test-OfflineReleaseBundle.ps1'), $verifier, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-RecoveryCase {
	param(
		[string]$Name,
		[int[]]$FailMoves = @(),
		[int[]]$FailVerifications = @(),
		[string]$ExpectedRecovery = '',
		[string]$CurrentLocation = 'rollback',
		[string]$PreviousLocation = 'active',
		[switch]$CoordinatedRecovery,
		[switch]$StartInInstallation
	)

	$caseRoot = Join-Path $tempRoot $Name
	$activePath = Join-Path $caseRoot 'rtaime'
	$rollbackPath = "$activePath.rollback"
	New-InstallationFixture -Path $activePath -Identity 'current' -Payload ([byte[]](1..64))
	New-InstallationFixture -Path $rollbackPath -Identity 'previous' -Payload ([byte[]](65..128))
	$currentSnapshot = Get-InstallationSnapshot -Path $activePath
	$previousSnapshot = Get-InstallationSnapshot -Path $rollbackPath
	$rollbackCaseState = @{
		moveCount = 0
		verificationCount = 0
		failMoves = $FailMoves
		failVerifications = $FailVerifications
	}

	# Shadow only the filesystem move used by the production script. Successful
	# calls still perform real directory moves; no installation content is mocked.
	function Move-Item {
		[CmdletBinding()]
		param([string]$LiteralPath, [string]$Destination)
		$rollbackCaseState.moveCount++
		if ($rollbackCaseState.failMoves -contains $rollbackCaseState.moveCount) {
			throw [System.IO.IOException]::new("Injected move failure $($rollbackCaseState.moveCount).")
		}
		Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
	}

	function Test-RollbackFixtureVerification {
		param([string]$Path)
		$rollbackCaseState.verificationCount++
		$manifest = Get-Content -LiteralPath (Join-Path $Path 'bundle-manifest.json') -Raw | ConvertFrom-Json
		$actualHash = (Get-FileHash -LiteralPath (Join-Path $Path 'payload.bin') -Algorithm SHA256).Hash
		Assert-Condition ($actualHash -eq [string]$manifest.payloadSha256) 'Fixture payload does not match its manifest.'
		if ($rollbackCaseState.failVerifications -contains $rollbackCaseState.verificationCount) {
			throw [System.IO.IOException]::new("Injected verification failure $($rollbackCaseState.verificationCount).")
		}
	}

	$arguments = @{
		InstallPath = $activePath
		AcknowledgeProcessesStopped = $true
		QualificationMode = $true
	}
	if ($CoordinatedRecovery) {
		$recoveryRoot = "$activePath.upgrade-recovery"
		New-Item -ItemType Directory -Path $recoveryRoot -Force | Out-Null
		[System.IO.File]::WriteAllText((Join-Path $recoveryRoot 'retained-state.json'), '{"status":"retained"}')
		$arguments.CoordinatedStateHandled = $true
	}
	$initialLocation = (Get-Location).Path
	$failure = $null
	try {
		if ($StartInInstallation) { Set-Location -LiteralPath $activePath }
		try { & $rollbackScript @arguments } catch { $failure = $_ }
		if ($StartInInstallation) {
			$expectedLocation = if (Test-Path -LiteralPath $activePath -PathType Container) { $activePath } else { $caseRoot }
			Assert-Condition ((Get-Location).Path -eq $expectedLocation) "$Name did not restore a usable working directory."
		}
	} finally {
		Set-Location -LiteralPath $initialLocation
	}

	$swaps = @(Get-ChildItem -LiteralPath $caseRoot -Directory -Force | Where-Object { $_.Name -like '.rtaime.rtaime-rollback-swap-*' })
	$receipts = @(Get-ChildItem -LiteralPath $caseRoot -File -Force | Where-Object { $_.Name -like '.rtaime.rtaime-rollback-swap-*.failure.json' })
	$locations = @{ active = $activePath; rollback = $rollbackPath }
	if ($CurrentLocation -eq 'swap') {
		Assert-Condition ($swaps.Count -eq 1) "$Name did not preserve exactly one original-installation swap."
		$locations.swap = $swaps[0].FullName
	} else {
		Assert-Condition ($swaps.Count -eq 0) "$Name left an unexpected swap directory."
	}
	Assert-InstallationSnapshot -Path $locations[$CurrentLocation] -Expected $currentSnapshot -Description "$Name original current installation"
	Assert-InstallationSnapshot -Path $locations[$PreviousLocation] -Expected $previousSnapshot -Description "$Name original rollback installation"

	if ([string]::IsNullOrEmpty($ExpectedRecovery)) {
		Assert-Condition ($null -eq $failure) "$Name unexpectedly failed: $failure"
		Assert-Condition ($receipts.Count -eq 0) "$Name produced a failure receipt on success."
		Assert-Condition ($rollbackCaseState.verificationCount -eq 4) "$Name did not verify both installations before and after the swap."
	} else {
		Assert-Condition ($null -ne $failure) "$Name unexpectedly succeeded."
		Assert-Condition ($failure.Exception -is [System.AggregateException]) "$Name did not preserve the original exception and any recovery exceptions."
		Assert-Condition ($receipts.Count -eq 1) "$Name did not retain its failure receipt."
		$receipt = Get-Content -LiteralPath $receipts[0].FullName -Raw | ConvertFrom-Json
		Assert-Condition ([string]$receipt.status -eq 'FAIL' -and [string]$receipt.recoveryStatus -eq $ExpectedRecovery) "$Name reported the wrong recovery state."
		Assert-Condition ([string]$receipt.installPath -eq $activePath -and [string]$receipt.rollbackPath -eq $rollbackPath) "$Name lost installation paths from its receipt."
		Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$receipt.originalError)) "$Name lost the primary failure from its receipt."
		Assert-Condition ($failure.Exception.Message.Contains([string]$receipt.originalError)) "$Name lost the primary failure from its exception."
		Assert-Condition ($failure.Exception.Message.Contains($receipts[0].FullName)) "$Name did not report the receipt location."
		Assert-Condition ($failure.Exception.InnerExceptions.Count -eq (1 + @($receipt.recoveryErrors).Count)) "$Name lost an exception while reporting failure."
		foreach ($move in $FailMoves) {
			Assert-Condition ($failure.Exception.Message.Contains("Injected move failure $move.")) "$Name omitted move failure $move."
		}
		foreach ($verification in $FailVerifications) {
			Assert-Condition ($failure.Exception.Message.Contains("Injected verification failure $verification.")) "$Name omitted verification failure $verification."
		}
		if ($ExpectedRecovery -eq 'FAILED') {
			Assert-Condition (@($receipt.recoveryErrors).Count -gt 0) "$Name omitted its compensation failure."
			if ($CurrentLocation -eq 'swap') {
				Assert-Condition ([string]$receipt.swapPath -eq $locations.swap) "$Name did not identify the retained original installation."
			}
		} else {
			Assert-Condition (@($receipt.recoveryErrors).Count -eq 0) "$Name recorded an unexpected compensation failure."
		}
	}
	if ($CoordinatedRecovery) {
		Assert-Condition ((Get-Content -LiteralPath (Join-Path $recoveryRoot 'retained-state.json') -Raw) -eq '{"status":"retained"}') "$Name modified coordinated persistent-state recovery evidence."
	}
	Write-Host "Software rollback recovery case PASS: $Name"
}

$rollbackScript = Join-Path $PSScriptRoot 'Invoke-SoftwareRollback.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('rtaime-software-rollback-recovery-{0}' -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
	Invoke-RecoveryCase -Name 'ManualRollbackSuccess' -StartInInstallation
	Invoke-RecoveryCase -Name 'CoordinatedRollbackSuccess' -CoordinatedRecovery
	Invoke-RecoveryCase -Name 'ActivationMoveFailureRestored' -FailMoves @(2) -ExpectedRecovery 'RESTORED' -CurrentLocation 'active' -PreviousLocation 'rollback'
	Invoke-RecoveryCase -Name 'ActivationVerificationFailureRestored' -FailVerifications @(3) -ExpectedRecovery 'RESTORED' -CurrentLocation 'active' -PreviousLocation 'rollback' -CoordinatedRecovery
	Invoke-RecoveryCase -Name 'RetainedSlotVerificationFailureRestored' -FailVerifications @(4) -ExpectedRecovery 'RESTORED' -CurrentLocation 'active' -PreviousLocation 'rollback'
	Invoke-RecoveryCase -Name 'ActivationAndOriginalRestoreMoveFailures' -FailMoves @(2, 3) -ExpectedRecovery 'FAILED' -CurrentLocation 'swap' -PreviousLocation 'rollback' -StartInInstallation
	Invoke-RecoveryCase -Name 'ActivatedRollbackReturnMoveFailure' -FailMoves @(3) -FailVerifications @(3) -ExpectedRecovery 'FAILED' -CurrentLocation 'swap' -PreviousLocation 'active'
	Invoke-RecoveryCase -Name 'OriginalRestoreMoveFailure' -FailMoves @(4) -FailVerifications @(3) -ExpectedRecovery 'FAILED' -CurrentLocation 'swap' -PreviousLocation 'rollback' -CoordinatedRecovery
	Invoke-RecoveryCase -Name 'FinalVerificationFirstCompensationMoveFailure' -FailMoves @(4) -FailVerifications @(4) -ExpectedRecovery 'FAILED' -CurrentLocation 'rollback' -PreviousLocation 'active'
	Invoke-RecoveryCase -Name 'RetainedSlotCompensationMoveFailure' -FailMoves @(5) -FailVerifications @(4) -ExpectedRecovery 'FAILED' -CurrentLocation 'swap' -PreviousLocation 'active'
	Invoke-RecoveryCase -Name 'FinalVerificationLastCompensationMoveFailure' -FailMoves @(6) -FailVerifications @(4) -ExpectedRecovery 'FAILED' -CurrentLocation 'swap' -PreviousLocation 'rollback'
	Invoke-RecoveryCase -Name 'RestoredInstallationVerificationFailure' -FailVerifications @(3, 4) -ExpectedRecovery 'FAILED' -CurrentLocation 'active' -PreviousLocation 'rollback'
	Write-Host 'Software rollback recovery qualification PASS: 12 cases'
} finally {
	if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
