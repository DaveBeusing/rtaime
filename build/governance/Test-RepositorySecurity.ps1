# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$RepositoryRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
	[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
} else {
	[System.IO.Path]::GetFullPath($RepositoryRoot)
}

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

function Invoke-GitGrepForPrivateKeyMarkers {
	$nativePreferenceVariable = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
	$previousNativePreference = $null
	if ($null -ne $nativePreferenceVariable) {
		$previousNativePreference = [bool]$nativePreferenceVariable.Value
		Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false
	}
	try {
		$output = @(& git -C $repositoryRoot grep -I -n -E -- '-----BEGIN (RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----' -- . 2>$null)
		$exitCode = $LASTEXITCODE
		return [ordered]@{ output = $output; exitCode = $exitCode }
	} finally {
		if ($null -ne $nativePreferenceVariable) {
			Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $previousNativePreference
		}
	}
}

Assert-Condition (Test-Path -LiteralPath $repositoryRoot -PathType Container) "Repository root '$repositoryRoot' does not exist."

$trackedFiles = @(& git -C $repositoryRoot ls-files)
Assert-Condition ($LASTEXITCODE -eq 0) "Unable to enumerate tracked repository files."
Assert-Condition ($trackedFiles.Count -gt 0) "Tracked repository file inventory is empty."

$forbiddenExtensions = @(".pem", ".key", ".pfx", ".p12")
$forbiddenFiles = @($trackedFiles | Where-Object {
	$extension = [System.IO.Path]::GetExtension($_).ToLowerInvariant()
	$extension -in $forbiddenExtensions
})
Assert-Condition ($forbiddenFiles.Count -eq 0) "Repository contains forbidden tracked private-key-like file(s): $($forbiddenFiles -join ', ')."

$grepResult = Invoke-GitGrepForPrivateKeyMarkers
if ([int]$grepResult.exitCode -eq 0) {
	throw "Repository contains PEM private-key material: $(@($grepResult.output) -join '; ')."
}
Assert-Condition ([int]$grepResult.exitCode -eq 1) "Private-key material scan failed with git grep exit code $($grepResult.exitCode)."

$artifactFiles = @($trackedFiles | Where-Object { $_ -like "artifacts/*" -and $_ -ne "artifacts/.gitkeep" })
Assert-Condition ($artifactFiles.Count -eq 0) "Generated artifact content must not be committed: $($artifactFiles -join ', ')."

$trustStorePath = Join-Path $repositoryRoot "build/release/trusted-release-keys.json"
Assert-Condition (Test-Path -LiteralPath $trustStorePath -PathType Leaf) "Trusted release key store is missing."
$trustStore = Get-Content -LiteralPath $trustStorePath -Raw | ConvertFrom-Json
Assert-Condition ([string]$trustStore.schemaVersion -eq "1.0") "Unsupported trusted release key store schema version."
$seenFingerprints = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($key in @($trustStore.keys)) {
	$fingerprint = [string]$key.fingerprint
	Assert-Condition ($fingerprint -match '^[0-9a-f]{64}$') "Trusted release key contains invalid SHA-256 fingerprint '$fingerprint'."
	Assert-Condition ($seenFingerprints.Add($fingerprint)) "Trusted release key store contains duplicate fingerprint '$fingerprint'."
	Assert-Condition ([string]$key.purpose -eq "SOFTWARE_RELEASE") "Trusted release key '$fingerprint' has unsupported purpose '$($key.purpose)'."
	Assert-Condition ([string]$key.status -in @("ACTIVE", "REVOKED")) "Trusted release key '$fingerprint' has unsupported status '$($key.status)'."
}

$requiredWorkflowPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"
Assert-Condition (Test-Path -LiteralPath $requiredWorkflowPath -PathType Leaf) "Required gates workflow is missing."
$requiredWorkflow = Get-Content -LiteralPath $requiredWorkflowPath -Raw
Assert-Condition ($requiredWorkflow -notmatch '(?im)^\s*permissions:\s*write-all\s*$') "Required gates workflow must not use write-all permissions."
Assert-Condition ($requiredWorkflow -notmatch '(?im)^\s*contents:\s+write\s*$') "Required gates workflow must not request contents: write."
Assert-Condition ($requiredWorkflow -notmatch '(?im)^\s*actions:\s+write\s*$') "Required gates workflow must not request actions: write."

$productSecurityPolicy = Join-Path $PSScriptRoot "Test-ProductSecurityPolicy.ps1"
Assert-Condition (Test-Path -LiteralPath $productSecurityPolicy -PathType Leaf) "Product-security policy verifier is missing."
& $productSecurityPolicy

Write-Host "Repository security governance PASS"
Write-Host "Tracked files scanned: $($trackedFiles.Count)"
Write-Host "Private key files: none"
Write-Host "PEM private key markers: none"
Write-Host "Committed generated artifacts: none"
Write-Host "Trusted release key metadata: structurally valid"
Write-Host "Product security policy: PASS"
Write-Host "Required gates workflow permissions: read-only"
exit 0
