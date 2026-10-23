# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$CoordinatorPath = (Join-Path $PSScriptRoot 'Invoke-CoordinatedUpgrade.ps1'),
	[string]$Case = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('rtaime-update-workspace-tests-{0}' -f [Guid]::NewGuid().ToString('N'))
$admissionMarker = 'Workspace admission reached bundle verification.'
$script:passed = 0
$script:skipped = 0
$script:skipCurrentCase = $false

function Assert-WorkspaceCondition {
	param([bool]$Condition, [string]$Message)
	if (-not $Condition) { throw $Message }
}

function New-WorkspaceFixture {
	param([string]$Name)
	$root = Join-Path $tempRoot $Name
	$fixture = [pscustomobject]@{
		Root = $root
		Install = (Join-Path $root 'install')
		State = (Join-Path $root 'state')
		Rollback = (Join-Path $root 'install.rollback')
		Recovery = (Join-Path $root 'install.upgrade-recovery')
		Bundle = (Join-Path $root 'candidate.zip')
	}
	New-Item -ItemType Directory -Path (Join-Path $fixture.Install 'tools'), $fixture.State -Force | Out-Null
	[System.IO.File]::WriteAllBytes((Join-Path $fixture.State 'state-sentinel.db'), [byte[]]@(0, 255, 17, 93, 42))
	[System.IO.File]::WriteAllText((Join-Path $fixture.Install 'rtaime.ControlHost.dll'), 'Disposable admission fixture; never executed.')
	[System.IO.File]::WriteAllText((Join-Path $fixture.Install 'bundle-manifest.json'), '{"fixture":"original-installation"}')
	[System.IO.File]::WriteAllText($fixture.Bundle, 'Admission stops before archive extraction.')
	[System.IO.File]::WriteAllText((Join-Path $fixture.Install 'tools/Test-OfflineReleaseBundle.ps1'), @'
param([string]$BundlePath, [switch]$RequireTrustedProductionKey)
throw 'Workspace admission reached bundle verification.'
'@)
	return $fixture
}

function Get-ProtectedTreeSignature {
	param($Fixture)
	$entries = [System.Collections.Generic.List[string]]::new()
	foreach ($name in @('Install', 'State', 'Rollback', 'Recovery')) {
		$root = [string]$Fixture.$name
		if (-not (Test-Path -LiteralPath $root)) {
			$entries.Add("${name}:MISSING")
			continue
		}
		$entries.Add("${name}:DIRECTORY")
		foreach ($item in @(Get-ChildItem -LiteralPath $root -Force -Recurse | Sort-Object FullName)) {
			$relative = [System.IO.Path]::GetRelativePath($root, $item.FullName)
			$value = if ($item.PSIsContainer) { 'DIRECTORY' } else { (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
			$entries.Add("${name}:${relative}:$value")
		}
	}
	return $entries -join "`n"
}

function Invoke-WorkspaceAdmission {
	param($Fixture, [hashtable]$Overrides, [string]$Entrypoint = $CoordinatorPath)
	$arguments = @{
		InstallPath = $Fixture.Install
		StateRoot = $Fixture.State
		Channel = 'PREVIEW'
		AcknowledgeProcessesStopped = $true
		QualificationMode = $true
		QualificationBundlePath = $Fixture.Bundle
	}
	foreach ($key in $Overrides.Keys) { $arguments[$key] = $Overrides[$key] }
	try {
		& $Entrypoint @arguments | Out-Null
		throw 'Disposable workspace fixture unexpectedly completed an update.'
	} catch {
		return $_.Exception.Message
	}
}

function Test-RejectedWorkspace {
	param($Fixture, [hashtable]$Overrides, [string]$Pattern, [string]$Entrypoint = $CoordinatorPath)
	$before = Get-ProtectedTreeSignature $Fixture
	$message = Invoke-WorkspaceAdmission -Fixture $Fixture -Overrides $Overrides -Entrypoint $Entrypoint
	Assert-WorkspaceCondition ((Get-ProtectedTreeSignature $Fixture) -ceq $before) 'Rejected workspace changed protected directories or bytes.'
	Assert-WorkspaceCondition ($message -match $Pattern) "Unexpected rejection: '$message'; expected '$Pattern'."
	Assert-WorkspaceCondition ($message -ne $admissionMarker) 'An unsafe workspace reached bundle verification.'
}

function Test-AdmittedWorkspace {
	param($Fixture, [hashtable]$Overrides)
	$before = Get-ProtectedTreeSignature $Fixture
	$message = Invoke-WorkspaceAdmission -Fixture $Fixture -Overrides $Overrides
	Assert-WorkspaceCondition ($message -eq $admissionMarker) "Valid workspace was rejected: '$message'."
	Assert-WorkspaceCondition ((Get-ProtectedTreeSignature $Fixture) -ceq $before) 'Workspace admission changed protected directories or bytes.'
}

function Invoke-WorkspaceCase {
	param([string]$Name, [scriptblock]$Action)
	if (-not [string]::IsNullOrWhiteSpace($Case) -and $Case -ne $Name) { return }
	$fixture = New-WorkspaceFixture $Name
	$script:skipCurrentCase = $false
	& $Action $fixture
	if ($script:skipCurrentCase) { $script:skipped++; return }
	$script:passed++
	Write-Host "Workspace safety PASS: $Name"
}

New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
	foreach ($protectedName in @('State', 'Install', 'Rollback', 'Recovery')) {
		foreach ($relation in @('Equal', 'Child', 'Parent')) {
			Invoke-WorkspaceCase -Name "$protectedName$relation" -Action {
				param($fixture)
				$protected = [string]$fixture.$protectedName
				if ($protectedName -in @('Rollback', 'Recovery')) {
					New-Item -ItemType Directory -Path $protected | Out-Null
					[System.IO.File]::WriteAllText((Join-Path $protected 'recovery-sentinel.bin'), 'Required recovery material.')
				}
				$work = switch ($relation) {
					'Equal' { $protected }
					'Child' { Join-Path $protected 'work' }
					'Parent' { $fixture.Root }
				}
				Test-RejectedWorkspace $fixture @{ WorkPath = $work } 'overlap'
			}
		}
	}

	Invoke-WorkspaceCase 'VerifiedUpdateEntryPoint' {
		param($fixture)
		Test-RejectedWorkspace $fixture @{ WorkPath = $fixture.State } 'overlap' (Join-Path $PSScriptRoot 'Invoke-VerifiedUpdate.ps1')
	}
	Invoke-WorkspaceCase 'ExistingForeignDirectory' {
		param($fixture)
		$work = Join-Path $fixture.Root 'foreign-work'
		New-Item -ItemType Directory -Path $work | Out-Null
		$sentinel = Join-Path $work 'unrelated-data.bin'
		[System.IO.File]::WriteAllBytes($sentinel, [byte[]]@(15, 0, 255, 1))
		$hash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
		Test-RejectedWorkspace $fixture @{ WorkPath = $work } 'already exists'
		Assert-WorkspaceCondition ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $hash) 'Existing WorkPath content was removed or changed.'
	}
	Invoke-WorkspaceCase 'ExistingForeignFile' {
		param($fixture)
		$work = Join-Path $fixture.Root 'foreign-work.bin'
		[System.IO.File]::WriteAllBytes($work, [byte[]]@(0, 5, 255))
		$hash = (Get-FileHash -LiteralPath $work -Algorithm SHA256).Hash
		Test-RejectedWorkspace $fixture @{ WorkPath = $work } 'already exists|directory'
		Assert-WorkspaceCondition ((Get-FileHash -LiteralPath $work -Algorithm SHA256).Hash -eq $hash) 'Existing WorkPath file was removed or changed.'
	}
	Invoke-WorkspaceCase 'NormalizedDotSegments' {
		param($fixture)
		Test-RejectedWorkspace $fixture @{ WorkPath = (Join-Path $fixture.State '../state/./') } 'overlap'
	}
	Invoke-WorkspaceCase 'SiblingWithSharedPrefix' {
		param($fixture)
		$work = "$($fixture.State)-workspace"
		Test-AdmittedWorkspace $fixture @{ WorkPath = $work }
		Assert-WorkspaceCondition (Test-Path -LiteralPath $work -PathType Container) 'Valid sibling workspace was not created.'
	}
	Invoke-WorkspaceCase 'MissingParent' {
		param($fixture)
		$parent = Join-Path $fixture.Root 'missing-parent'
		Test-RejectedWorkspace $fixture @{ WorkPath = (Join-Path $parent 'work') } 'parent'
		Assert-WorkspaceCondition (-not (Test-Path -LiteralPath $parent)) 'Rejected workspace created its missing parent.'
	}
	Invoke-WorkspaceCase 'DefaultRetainsEarlierEvidence' {
		param($fixture)
		$oldWork = "$($fixture.Install).coordinated-update-state"
		New-Item -ItemType Directory -Path $oldWork | Out-Null
		$oldEvidence = Join-Path $oldWork 'previous-run.json'
		[System.IO.File]::WriteAllText($oldEvidence, '{"retain":true}')
		$hash = (Get-FileHash -LiteralPath $oldEvidence -Algorithm SHA256).Hash
		Test-AdmittedWorkspace $fixture @{}
		Test-AdmittedWorkspace $fixture @{}
		$workspaces = @(Get-ChildItem -LiteralPath $fixture.Root -Directory | Where-Object { $_.Name -like 'install.coordinated-update-state-*' })
		Assert-WorkspaceCondition ($workspaces.Count -eq 2) 'Default runs did not create separate workspaces.'
		Assert-WorkspaceCondition ((Get-FileHash -LiteralPath $oldEvidence -Algorithm SHA256).Hash -eq $hash) 'A default run changed earlier evidence.'
	}
	foreach ($protectedParameter in @('WorkPath', 'InstallPath', 'StateRoot')) {
		Invoke-WorkspaceCase "Reparse$protectedParameter" {
			param($fixture)
			$target = if ($protectedParameter -eq 'InstallPath') { $fixture.Install } else { $fixture.State }
			$alias = Join-Path $fixture.Root 'directory-alias'
			$linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
			New-Item -ItemType $linkType -Path $alias -Target $target | Out-Null
			$overrides = @{ WorkPath = (Join-Path $fixture.Root 'work') }
			$overrides[$protectedParameter] = if ($protectedParameter -eq 'WorkPath') { Join-Path $alias 'work' } else { $alias }
			Test-RejectedWorkspace $fixture $overrides 'reparse'
		}
	}
	if ($IsWindows) {
		if (-not ('Rtaime.Maintenance.WorkspaceQualificationPaths' -as [type])) {
			Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace Rtaime.Maintenance
{
    public static class WorkspaceQualificationPaths
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint capacity);
        public static string ShortName(string path)
        {
            var buffer = new StringBuilder(32768);
            uint length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length >= buffer.Capacity) throw new InvalidOperationException("Short-name fixture path exceeds its buffer.");
            return buffer.ToString();
        }
    }
}
'@
		}
		Invoke-WorkspaceCase 'WindowsCaseAlias' {
			param($fixture)
			Test-RejectedWorkspace $fixture @{ WorkPath = $fixture.State.ToUpperInvariant() } 'overlap'
		}
		Invoke-WorkspaceCase 'WindowsSeparatorAlias' {
			param($fixture)
			Test-RejectedWorkspace $fixture @{ WorkPath = $fixture.State.Replace('\', '/') + '/' } 'overlap'
		}
		foreach ($suffix in @('.', ' ', ':stream')) {
			$name = switch ($suffix) { '.' { 'TrailingDot' } ' ' { 'TrailingSpace' } ':stream' { 'AlternateStream' } }
			Invoke-WorkspaceCase $name {
				param($fixture)
				Test-RejectedWorkspace $fixture @{ WorkPath = $fixture.State + $suffix } 'unsupported|ambiguous|invalid|local|colon|stream'
			}
		}
		Invoke-WorkspaceCase 'WindowsDeviceNamespace' {
			param($fixture)
			Test-RejectedWorkspace $fixture @{ WorkPath = '\\?\' + $fixture.State } 'unsupported|device|local'
		}
		Invoke-WorkspaceCase 'WindowsNetworkNamespace' {
			param($fixture)
			Test-RejectedWorkspace $fixture @{ WorkPath = '\\localhost\rtaime-workspace-test\work' } 'unsupported|network|local|UNC'
		}
		Invoke-WorkspaceCase 'WindowsSubstAlias' {
			param($fixture)
			$used = @([System.IO.Directory]::GetLogicalDrives())
			$drive = @('Z:', 'Y:', 'X:', 'W:', 'V:', 'U:') | Where-Object { "$($_)\" -notin $used } | Select-Object -First 1
			Assert-WorkspaceCondition (-not [string]::IsNullOrWhiteSpace($drive)) 'No unused drive is available for SUBST regression.'
			& subst.exe $drive $fixture.State
			Assert-WorkspaceCondition ($LASTEXITCODE -eq 0) 'Could not create disposable SUBST alias.'
			try { Test-RejectedWorkspace $fixture @{ WorkPath = "$drive\work" } 'overlap' }
			finally { & subst.exe $drive /D; Assert-WorkspaceCondition ($LASTEXITCODE -eq 0) 'Could not remove disposable SUBST alias.' }
		}
		Invoke-WorkspaceCase 'WindowsShortNameAlias' {
			param($fixture)
			$shortPath = [Rtaime.Maintenance.WorkspaceQualificationPaths]::ShortName($fixture.State)
			if ([string]::Equals($shortPath, $fixture.State, [StringComparison]::OrdinalIgnoreCase)) {
				$script:skipCurrentCase = $true
				Write-Host 'Workspace safety SKIP: WindowsShortNameAlias (the fixture volume does not expose an 8.3 alias).'
				return
			}
			Test-RejectedWorkspace $fixture @{ WorkPath = (Join-Path $shortPath 'work') } 'overlap'
		}
	} else {
		Write-Host 'Windows drive, namespace and case alias regressions require the Windows CI runner.'
	}
	Assert-WorkspaceCondition (($script:passed + $script:skipped) -gt 0) "No workspace case matched '$Case'."
	Write-Host "Coordinated upgrade workspace safety PASS ($script:passed passed, $script:skipped skipped)."
} finally {
	if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
