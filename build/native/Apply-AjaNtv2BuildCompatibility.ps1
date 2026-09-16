# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$SdkRoot,
	[string]$ExpectedCommit = "aa4d482a47fdd9fd9f2883163e286206ac0d7ae7"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedRoot = [System.IO.Path]::GetFullPath($SdkRoot)
if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
	throw "AJA SDK root '$resolvedRoot' does not exist."
}
if ($ExpectedCommit -notmatch '^[0-9a-f]{40}$') {
	throw "Expected AJA SDK commit must be an exact 40-character lowercase SHA."
}

$resolvedCommit = (& git -C $resolvedRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedCommit -notmatch '^[0-9a-f]{40}$') {
	throw "Unable to resolve AJA SDK checkout commit."
}
if ($resolvedCommit -ne $ExpectedCommit) {
	throw "AJA SDK compatibility patch is qualified only for '$ExpectedCommit', but checkout is '$resolvedCommit'."
}

$sourcePath = Join-Path $resolvedRoot "ajabase/system/windows/infoimpl.cpp"
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
	throw "Expected AJA Windows system-info source is missing: '$sourcePath'."
}

$source = [System.IO.File]::ReadAllText($sourcePath)
$newLine = if ($source.Contains("`r`n")) { "`r`n" } else { "`n" }
$locatorDeclaration = "`tIWbemLocator *pLoc = NULL;"
$relocatedDeclarations = @(
	"`tIWbemServices *pSvc = NULL;",
	"`tIClientSecurity* pSecurity = NULL;",
	"`tDWORD authnSvc = 0;",
	"`tDWORD authzSvc = 0;",
	"`tLPOLESTR serverPrincName = NULL;",
	"`tDWORD authnLevel = 0;",
	"`tDWORD impLevel = 0;",
	"`tRPC_AUTH_IDENTITY_HANDLE authInfo = NULL;",
	"`tDWORD ifCapabilites = 0;",
	"`tIEnumWbemClassObject* pEnumerator = NULL;",
	"`tIWbemClassObject *pClsObj = NULL;",
	"`tULONG uReturn = 0;"
)

if ([regex]::Matches($source, [regex]::Escape($locatorDeclaration)).Count -ne 1) {
	throw "AJA compatibility source shape changed: expected exactly one IWbemLocator declaration."
}

foreach ($declaration in $relocatedDeclarations) {
	$pattern = "(?m)^" + [regex]::Escape($declaration) + "\r?\n"
	if ([regex]::Matches($source, $pattern).Count -ne 1) {
		throw "AJA compatibility source shape changed: expected exactly one declaration '$($declaration.Trim())'."
	}
	$source = [regex]::Replace($source, $pattern, "", 1)
}

$declarationBlock = (@($locatorDeclaration) + $relocatedDeclarations) -join $newLine
$source = $source.Replace($locatorDeclaration, $declarationBlock)

[System.IO.File]::WriteAllText(
	$sourcePath,
	$source,
	[System.Text.UTF8Encoding]::new($false))

$patchedSource = [System.IO.File]::ReadAllText($sourcePath)
foreach ($declaration in @($locatorDeclaration) + $relocatedDeclarations) {
	if ([regex]::Matches($patchedSource, [regex]::Escape($declaration)).Count -ne 1) {
		throw "AJA compatibility patch verification failed for '$($declaration.Trim())'."
	}
}

$locatorIndex = $patchedSource.IndexOf($locatorDeclaration, [StringComparison]::Ordinal)
foreach ($declaration in $relocatedDeclarations) {
	$index = $patchedSource.IndexOf($declaration, [StringComparison]::Ordinal)
	if ($index -lt $locatorIndex) {
		throw "AJA compatibility patch verification found an invalid declaration order."
	}
}

$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "AJA NTV2 build compatibility patch PASS"
Write-Host "Pinned SDK commit: $resolvedCommit"
Write-Host "Patched source SHA256: $sourceHash"
Write-Host "Compatibility scope: MSVC C2362 goto/declaration ordering in ajabase/system/windows/infoimpl.cpp"
