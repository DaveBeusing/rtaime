# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$BundlePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$bundle = [System.IO.Path]::GetFullPath($BundlePath)
Assert-Condition ((Test-Path -LiteralPath $bundle -PathType Leaf) -or (Test-Path -LiteralPath $bundle -PathType Container)) "Qualification bundle was not found at '$bundle'."

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-investor-demo-{0}" -f [Guid]::NewGuid().ToString("N"))
$install = Join-Path $root "install"
$stateRoot = Join-Path $root "state"
$workRoot = Join-Path $root "work"
$readyFile = Join-Path $root "operator-ready.json"
$instanceId = "showcase-$([Guid]::NewGuid().ToString('N'))"
$lifecycle = $null

try {
	New-Item -ItemType Directory -Path $root -Force | Out-Null
	& (Join-Path $repositoryRoot "build/release/Install-OfflineRelease.ps1") -BundlePath $bundle -InstallPath $install

	$launcher = Join-Path $install "tools/Invoke-InvestorDemo.ps1"
	$lifecycle = Join-Path $install "tools/Invoke-ManagedHostLifecycle.ps1"
	Assert-Condition (Test-Path -LiteralPath $launcher -PathType Leaf) "Installed bundle does not contain the investor demo launcher."
	Assert-Condition (Test-Path -LiteralPath $lifecycle -PathType Leaf) "Installed bundle does not contain the managed lifecycle controller."
	Assert-Condition (Test-Path -LiteralPath (Join-Path $install "tools/Start-rtaime-Showcase.cmd") -PathType Leaf) "Installed bundle does not contain the one-click showcase entry point."
	Assert-Condition (Test-Path -LiteralPath (Join-Path $install "Start-rtaime-Showcase.cmd") -PathType Leaf) "Installed bundle root does not expose the one-click showcase entry point."

	$result = & $launcher `
		-InstallPath $install `
		-StateRoot $stateRoot `
		-WorkPath $workRoot `
		-InstanceId $instanceId `
		-QualificationMode `
		-HeadlessAcceptance `
		-HeadlessReadyFile $readyFile

	Assert-Condition ([string]$result.status -eq "PASS") "Investor demo launcher did not return PASS."
	Assert-Condition ([string]$result.serviceReadiness -eq "PASS") "Investor demo launcher did not qualify service readiness."
	Assert-Condition ([string]$result.operatorReadiness -eq "PASS") "Investor demo launcher did not qualify Operator readiness."
	Assert-Condition ([int]$result.operatorExitCode -eq 0) "One-shot Operator did not exit successfully."
	Assert-Condition ($result.lifecycleOwnedByLauncher -eq $true) "Clean packaged showcase must own the lifecycle it starts."
	Assert-Condition ([string]$result.lifecycleStopStatus -eq "PASS") "Packaged showcase did not stop its owned lifecycle cleanly."
	Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $workRoot "lifecycle-state.json") -PathType Leaf)) "Managed lifecycle state remained after packaged showcase completion."

	Assert-Condition (Test-Path -LiteralPath $readyFile -PathType Leaf) "Operator readiness file was not produced."
	$ready = Get-Content -LiteralPath $readyFile -Raw | ConvertFrom-Json
	Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$ready.HostInstanceId)) "Operator readiness did not identify the authoritative ControlHost session."
	Assert-Condition ([string]$ready.RuntimeStatus -eq "READY") "Operator readiness did not observe Runtime READY."

	$receiptPath = Join-Path $workRoot "showcase-receipt-latest.json"
	Assert-Condition (Test-Path -LiteralPath $receiptPath -PathType Leaf) "Showcase acceptance receipt was not produced."
	$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
	Assert-Condition ([string]$receipt.status -eq "PASS") "Showcase acceptance receipt did not report PASS."

	Write-Host "Packaged investor demo acceptance PASS"
	Write-Host "Sequence: install -> managed lifecycle -> Operator readiness -> graceful shutdown"
	Write-Host "Manual service start: NOT REQUIRED"
	Write-Host "Terminal configuration: NOT REQUIRED"
} finally {
	if ($null -ne $lifecycle -and (Test-Path -LiteralPath $lifecycle -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $workRoot "lifecycle-state.json") -PathType Leaf)) {
		try {
			& $lifecycle -Action Stop -InstallPath $install -WorkPath $workRoot -InstanceId $instanceId -QualificationMode | Out-Null
		} catch { }
	}
	for ($attempt = 0; $attempt -lt 10 -and (Test-Path -LiteralPath $root); $attempt++) {
		try {
			Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop
		} catch {
			Start-Sleep -Milliseconds 200
		}
	}
}
