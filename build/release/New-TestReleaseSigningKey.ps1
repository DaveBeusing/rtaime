# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$PrivateKeyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$privateKeyFullPath = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if ($privateKeyFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
	throw "Test release private keys must be generated outside the repository."
}
if (Test-Path -LiteralPath $privateKeyFullPath) {
	throw "Refusing to overwrite existing private key '$privateKeyFullPath'."
}

$directory = Split-Path -Parent $privateKeyFullPath
if (-not (Test-Path -LiteralPath $directory)) {
	New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
try {
	$privatePem = $ecdsa.ExportPkcs8PrivateKeyPem()
	$publicKey = $ecdsa.ExportSubjectPublicKeyInfo()
	$fingerprint = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($publicKey)).ToLowerInvariant()

	$stream = [System.IO.File]::Open($privateKeyFullPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
	try {
		$writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
		try {
			$writer.Write($privatePem)
			$writer.Flush()
		} finally {
			$writer.Dispose()
		}
	} finally {
		if ($null -ne $stream) {
			$stream.Dispose()
		}
	}

	Write-Host "Generated TEST_EPHEMERAL release signing key outside the repository."
	Write-Host "Public key fingerprint (SHA-256): $fingerprint"
} finally {
	$ecdsa.Dispose()
}
