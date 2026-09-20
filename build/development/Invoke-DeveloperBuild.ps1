# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[ValidateSet("Debug", "Release")]
	[string]$Configuration = "Release",

	[switch]$NoRestore,

	[switch]$SkipProcessCleanup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$solutionPath = Join-Path $repositoryRoot "rtaime.slnx"
$repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$developmentProcessNames = @(
	"rtaime",
	"rtaime.Operator",
	"rtaime.ControlHost",
	"rtaime.RuntimeHost",
	"rtaime.AIHost"
)

function Test-RepositoryProcessPath {
	param([string]$ExecutablePath)

	if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
		return $false
	}

	try {
		$fullPath = [System.IO.Path]::GetFullPath($ExecutablePath)
	}
	catch {
		return $false
	}

	return $fullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-RepositoryDevelopmentProcesses {
	$matches = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

	foreach ($processName in $developmentProcessNames) {
		foreach ($process in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
			$executablePath = $null
			try {
				$executablePath = $process.Path
			}
			catch {
				continue
			}

			if (Test-RepositoryProcessPath -ExecutablePath $executablePath) {
				$matches.Add($process)
			}
		}
	}

	return @($matches)
}

function Stop-RepositoryDevelopmentProcesses {
	# Stop the AppHost first so it cannot supervise or restart repository-local child hosts
	# while the remaining development processes are being terminated.
	foreach ($pass in 1..2) {
		$processes = @(Get-RepositoryDevelopmentProcesses | Sort-Object {
			if ($_.ProcessName -eq "rtaime") { 0 } else { 1 }
		})

		if ($processes.Count -eq 0) {
			return
		}

		foreach ($process in $processes) {
			Write-Host "Stopping repository development process $($process.ProcessName) (PID $($process.Id))"
			try {
				Stop-Process -Id $process.Id -Force -ErrorAction Stop
			}
			catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
				if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
					throw
				}
			}
		}

		foreach ($process in $processes) {
			Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
		}
	}

	$remaining = @(Get-RepositoryDevelopmentProcesses)
	if ($remaining.Count -gt 0) {
		$description = ($remaining | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ", "
		throw "Repository development processes are still running and may lock build outputs: $description"
	}
}

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
	throw "Solution is missing: '$solutionPath'."
}

if (-not $SkipProcessCleanup) {
	Stop-RepositoryDevelopmentProcesses
}

Push-Location $repositoryRoot
try {
	if (-not $NoRestore) {
		Write-Host "Developer build: restore"
		& dotnet restore $solutionPath
		if ($LASTEXITCODE -ne 0) {
			throw "dotnet restore failed with exit code $LASTEXITCODE."
		}
	}

	Write-Host "Developer build: $Configuration"
	$arguments = @("build", $solutionPath, "--configuration", $Configuration)
	if (-not $NoRestore) {
		$arguments += "--no-restore"
	}

	& dotnet @arguments
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet build failed with exit code $LASTEXITCODE."
	}

	Write-Host "Developer build PASS"
}
finally {
	Pop-Location
}
