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
		'tools/state-upgrade-qualification-catalog.json',
		'tools/Invoke-CoordinatedUpgrade.ps1',
		'tools/Complete-CoordinatedUpgradeRecovery.ps1',
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
	Assert-Condition (@($management[0].migrations).Count -eq 0 -and @($journal[0].migrations).Count -eq 0) "Production state catalog must not contain manufactured qualification migrations."

	$qualificationCatalogEntry = $archive.GetEntry('tools/state-upgrade-qualification-catalog.json')
	Assert-Condition ($null -ne $qualificationCatalogEntry) "Qualification-only state-upgrade catalog entry is missing."
	$reader = [System.IO.StreamReader]::new($qualificationCatalogEntry.Open(), [System.Text.Encoding]::UTF8, $true)
	try { $qualificationCatalog = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
	Assert-Condition ($qualificationCatalog.qualificationOnly -eq $true) "Packaged qualification catalog must be explicitly qualification-only."
	$qualificationKind = @($qualificationCatalog.databaseKinds | Where-Object { [string]$_.id -eq 'qualification-state' })
	Assert-Condition ($qualificationKind.Count -eq 1 -and [int]$qualificationKind[0].targetSchemaVersion -eq 2) "Qualification catalog must contain the disposable v1 -> v2 target."
	$migration = @($qualificationKind[0].migrations)
	Assert-Condition ($migration.Count -eq 1 -and [int]$migration[0].fromVersion -eq 1 -and [int]$migration[0].toVersion -eq 2) "Qualification catalog must contain exactly one v1 -> v2 migration step."
} finally {
	$archive.Dispose()
}

Write-Host 'Coordinated upgrade packaging qualification PASS'
Write-Host 'Signed production and qualification state-upgrade catalogs: present'
Write-Host 'Coordinator, recovery retirement and rollback guard: present'
Write-Host 'ControlHost maintenance executable assembly: present'
