# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$OutputPath = "artifacts/release-evidence"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$verifier = Join-Path $PSScriptRoot "Test-ReleaseAttestation.ps1"
$sourceRoot = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
	[System.IO.Path]::GetFullPath($OutputPath)
} else {
	[System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
}

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
	throw "Release evidence directory was not found at '$sourceRoot'."
}

function Invoke-ExpectVerificationFailure {
	param(
		[Parameter(Mandatory)][string]$CaseName,
		[Parameter(Mandatory)][scriptblock]$Mutate,
		[switch]$RequireTrustedProductionKey
	)

	$caseRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-release-signing-{0}" -f [Guid]::NewGuid().ToString("N"))
	try {
		Copy-Item -LiteralPath $sourceRoot -Destination $caseRoot -Recurse -Force
		& $Mutate $caseRoot

		$arguments = @("-NoProfile", "-File", $verifier, "-OutputPath", $caseRoot)
		if ($RequireTrustedProductionKey) {
			$arguments += "-RequireTrustedProductionKey"
		}
		& pwsh @arguments *> $null
		if ($LASTEXITCODE -eq 0) {
			throw "Negative release-signing case '$CaseName' unexpectedly verified successfully."
		}
		Write-Host "Negative case PASS: $CaseName"
	} finally {
		if (Test-Path -LiteralPath $caseRoot) {
			Remove-Item -LiteralPath $caseRoot -Recurse -Force
		}
	}
}

Invoke-ExpectVerificationFailure -CaseName "tampered release evidence" -Mutate {
	param($caseRoot)
	Add-Content -LiteralPath (Join-Path $caseRoot "release-evidence.json") -Value " " -Encoding utf8
}

Invoke-ExpectVerificationFailure -CaseName "tampered signature" -Mutate {
	param($caseRoot)
	$path = Join-Path $caseRoot "release-attestation.json"
	$attestation = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
	$attestation.signature = [Convert]::ToBase64String((New-Object byte[] 64))
	$json = $attestation | ConvertTo-Json -Depth 64
	[System.IO.File]::WriteAllText($path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

Invoke-ExpectVerificationFailure -CaseName "private key material in evidence bundle" -Mutate {
	param($caseRoot)
	[System.IO.File]::WriteAllText(
		(Join-Path $caseRoot "forbidden-private.pem"),
		"TEST PRIVATE KEY MATERIAL MUST NEVER BE ACCEPTED",
		[System.Text.UTF8Encoding]::new($false))
}

Invoke-ExpectVerificationFailure -CaseName "ephemeral signer rejected as production trust" -RequireTrustedProductionKey -Mutate {
	param($caseRoot)
}

Write-Host "Release signing failure qualification PASS"
