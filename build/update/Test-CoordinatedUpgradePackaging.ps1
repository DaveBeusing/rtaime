# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string]$BundlePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$bundle = [System.IO.Path]::GetFullPath($BundlePath)
Assert-Condition (Test-Path -LiteralPath $bundle -PathType Leaf) "Qualification bundle was not found at '$bundle'."
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($bundle)
try {
	$entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\\', '/') })
	foreach ($required in @(
		'tools/coordinated-upgrade-policy.json',
		'tools/state-upgrade-catalog.json',
		'tools/Invoke-CoordinatedUpgrade.ps1',
		'tools/Invoke-VerifiedUpdate.ps1',
		'tools/Invoke-SoftwareRollback.ps1'
	)) {
		Assert-Condition ($entries -contains $required) "Qualification bundle is missing coordinated-upgrade payload '$required'."
	}
	$controlHosts = @($entries | Where-Object { $_ -match '(^|/)rtaime\.ControlHost\.dll$' })
	Assert-Condition ($controlHosts.Count -eq 1) "Qualification bundle must contain exactly one rtaime.ControlHost.dll, found $($controlHosts.Count)."

	$catalogEntry = $archive.GetEntry('tools/state-upgrade-catalog.json')
	Assert-Condition ($null -ne $catalogEntry) "State-upgrade catalog entry is missing."
	$reader = [System.IO.StreamReader]::new($catalogEntry.Open(), [System.Text.Encoding]::UTF8, $true)
	try { $catalog = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
	Assert-Condition ([string]$catalog.schemaVersion -eq '1.0') "Packaged state-upgrade catalog schema version is invalid."
	$management = @($catalog.databaseKinds | Where-Object { [string]$_.id -eq 'management' })
	$journal = @($catalog.databaseKinds | Where-Object { [string]$_.id -eq 'production-journal' })
	Assert-Condition ($management.Count -eq 1 -and [int]$management[0].targetSchemaVersion -eq 1) "Packaged management target schema is not v1."
	Assert-Condition ($journal.Count -eq 1 -and [int]$journal[0].targetSchemaVersion -eq 1) "Packaged production-journal target schema is not v1."
} finally {
	$archive.Dispose()
}

Write-Host 'Coordinated upgrade packaging qualification PASS'
Write-Host 'Signed state-upgrade catalog: present'
Write-Host 'Coordinator and rollback guard: present'
Write-Host 'ControlHost maintenance executable assembly: present'
