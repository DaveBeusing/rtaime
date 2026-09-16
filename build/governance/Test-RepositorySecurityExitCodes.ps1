# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$securityScript = Join-Path $PSScriptRoot "Test-RepositorySecurity.ps1"
$pwshPath = (Get-Process -Id $PID).Path

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

function Invoke-SecurityCheck {
	param([Parameter(Mandatory)][string]$RepositoryPath)
	$nativePreferenceVariable = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
	$previousNativePreference = $null
	if ($null -ne $nativePreferenceVariable) {
		$previousNativePreference = [bool]$nativePreferenceVariable.Value
		Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false
	}
	try {
		& $pwshPath -NoLogo -NoProfile -File $securityScript -RepositoryRoot $RepositoryPath
		return $LASTEXITCODE
	} finally {
		if ($null -ne $nativePreferenceVariable) {
			Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $previousNativePreference
		}
	}
}

$validExitCode = Invoke-SecurityCheck -RepositoryPath $repositoryRoot
Assert-Condition ($validExitCode -eq 0) "Valid repository security governance must exit 0, observed $validExitCode."

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-security-exit-{0}" -f [Guid]::NewGuid().ToString("N"))
try {
	New-Item -ItemType Directory -Path (Join-Path $fixtureRoot ".github/workflows") -Force | Out-Null
	New-Item -ItemType Directory -Path (Join-Path $fixtureRoot "build/release") -Force | Out-Null
	[System.IO.File]::WriteAllText(
		(Join-Path $fixtureRoot ".github/workflows/required-gates.yml"),
		"# Copyright (c) Dave Beusing <david.beusing@gmail.com>.`nname: fixture`npermissions:`n  contents: read`n",
		[System.Text.UTF8Encoding]::new($false))
	[System.IO.File]::WriteAllText(
		(Join-Path $fixtureRoot "build/release/trusted-release-keys.json"),
		'{"copyright":"Copyright (c) Dave Beusing <david.beusing@gmail.com>.","schemaVersion":"1.0","keys":[]}' + [Environment]::NewLine,
		[System.Text.UTF8Encoding]::new($false))
	[System.IO.File]::WriteAllText(
		(Join-Path $fixtureRoot "forbidden-test.pem"),
		"fixture content only`n",
		[System.Text.UTF8Encoding]::new($false))

	& git init --quiet $fixtureRoot
	if ($LASTEXITCODE -ne 0) { throw "Unable to initialize repository security failure fixture." }
	& git -C $fixtureRoot add .
	if ($LASTEXITCODE -ne 0) { throw "Unable to stage repository security failure fixture." }

	$invalidExitCode = Invoke-SecurityCheck -RepositoryPath $fixtureRoot
	Assert-Condition ($invalidExitCode -ne 0) "Repository security policy violation must exit non-zero."
} finally {
	if (Test-Path -LiteralPath $fixtureRoot) {
		Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
	}
}

Write-Host "Repository security exit-code regression PASS"
Write-Host "Valid repository exit: 0"
Write-Host "Policy violation exit: non-zero"
exit 0
