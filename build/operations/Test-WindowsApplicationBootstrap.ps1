# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param(
	[string]$Configuration = "Release",
	[switch]$QualifySingleFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$appProjectPath = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/rtaime.AppHost.csproj"
$lifecyclePolicyPath = Join-Path $repositoryRoot "build/operations/host-lifecycle-policy.json"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rtaime-windows-bootstrap-" + [Guid]::NewGuid().ToString("N"))

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

function Get-PeSubsystem {
	param([Parameter(Mandatory)][string]$Path)

	$bytes = [System.IO.File]::ReadAllBytes($Path)
	Assert-Condition ($bytes.Length -gt 512) "Executable '$Path' is too small to contain a valid PE header."
	$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
	Assert-Condition ($peOffset -gt 0 -and ($peOffset + 96) -lt $bytes.Length) "Executable '$Path' has an invalid PE header offset."
	Assert-Condition (
		$bytes[$peOffset] -eq 0x50 -and
		$bytes[$peOffset + 1] -eq 0x45 -and
		$bytes[$peOffset + 2] -eq 0x00 -and
		$bytes[$peOffset + 3] -eq 0x00
	) "Executable '$Path' is not a PE image."

	$optionalHeaderOffset = $peOffset + 24
	return [BitConverter]::ToUInt16($bytes, $optionalHeaderOffset + 68)
}

function Invoke-BootstrapFailureProbe {
	param(
		[Parameter(Mandatory)][string]$Executable,
		[Parameter(Mandatory)][string[]]$Arguments,
		[Parameter(Mandatory)][string]$WorkRoot,
		[Parameter(Mandatory)][string]$ExpectedDetail
	)

	New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

	$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = $Executable
	$startInfo.UseShellExecute = $false
	$startInfo.CreateNoWindow = $true
	$startInfo.RedirectStandardOutput = $true
	$startInfo.RedirectStandardError = $true
	foreach ($argument in $Arguments) {
		$startInfo.ArgumentList.Add($argument)
	}
	$startInfo.ArgumentList.Add("--work-root=$WorkRoot")

	$process = [System.Diagnostics.Process]::new()
	$process.StartInfo = $startInfo
	Assert-Condition ($process.Start()) "Unable to start bootstrap qualification executable '$Executable'."
	$stdoutTask = $process.StandardOutput.ReadToEndAsync()
	$stderrTask = $process.StandardError.ReadToEndAsync()
	if (-not $process.WaitForExit(15000)) {
		try { $process.Kill($true) } catch {}
		throw "Bootstrap qualification process did not exit within 15 seconds."
	}

	$stdout = $stdoutTask.GetAwaiter().GetResult()
	$stderr = $stderrTask.GetAwaiter().GetResult()
	Assert-Condition ($process.ExitCode -eq 1) "Bootstrap qualification expected exit code 1, got $($process.ExitCode). stdout='$stdout' stderr='$stderr'."
	Assert-Condition ($stderr -match 'app=rtaime state=FAILED') "Bootstrap failure was not preserved on redirected stderr."
	Assert-Condition ($stderr -match [Regex]::Escape($ExpectedDetail)) "Bootstrap stderr did not contain expected failure detail '$ExpectedDetail'."

	$diagnosticPath = Join-Path $WorkRoot "apphost-startup.log"
	Assert-Condition (Test-Path -LiteralPath $diagnosticPath -PathType Leaf) "Bootstrap failure did not persist '$diagnosticPath'."
	$diagnosticLines = @(Get-Content -LiteralPath $diagnosticPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
	Assert-Condition ($diagnosticLines.Count -gt 0) "Bootstrap diagnostic evidence is empty."
	$diagnosticRecord = $diagnosticLines[-1] | ConvertFrom-Json
	Assert-Condition ([string]$diagnosticRecord.state -eq "FAILED") "Bootstrap diagnostic evidence does not contain FAILED state."
	Assert-Condition ([string]$diagnosticRecord.message -match [Regex]::Escape($ExpectedDetail)) "Bootstrap diagnostic evidence does not contain expected failure detail '$ExpectedDetail'."
}

try {
	New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

	$builtExecutable = Join-Path $repositoryRoot "src/Hosts/rtaime.AppHost/bin/$Configuration/net10.0/rtaime.exe"
	Assert-Condition (Test-Path -LiteralPath $builtExecutable -PathType Leaf) "Built AppHost executable was not found at '$builtExecutable'."
	Assert-Condition ((Get-PeSubsystem -Path $builtExecutable) -eq 2) "Canonical Windows AppHost must use the WINDOWS_GUI PE subsystem."

	$showConsoleArgs = @("--profile=InvalidProfile", "--show-console")
	Invoke-BootstrapFailureProbe -Executable $builtExecutable -Arguments $showConsoleArgs -WorkRoot (Join-Path $tempRoot "show-console") -ExpectedDetail "Unknown startup profile"

	$headlessInstallRoot = Join-Path $tempRoot "empty-install"
	New-Item -ItemType Directory -Path $headlessInstallRoot -Force | Out-Null
	$headlessArgs = @(
		"--profile=HeadlessEngine",
		"--install-root=$headlessInstallRoot",
		"--state-root=$(Join-Path $tempRoot "headless-state")",
		"--policy=$lifecyclePolicyPath",
		"--no-ai"
	)
	Invoke-BootstrapFailureProbe -Executable $builtExecutable -Arguments $headlessArgs -WorkRoot (Join-Path $tempRoot "headless") -ExpectedDetail "Product artifact 'rtaime.ControlHost' was not found"

	if ($QualifySingleFile) {
		$publishRoot = Join-Path $tempRoot "single-file"
		$publishArguments = @(
			"publish",
			$appProjectPath,
			"--configuration", $Configuration,
			"--runtime", "win-x64",
			"--self-contained", "true",
			"-p:PublishSingleFile=true",
			"-p:IncludeNativeLibrariesForSelfExtract=true",
			"-p:DebugType=None",
			"-p:DebugSymbols=false",
			"--output", $publishRoot
		)
		& dotnet @publishArguments
		Assert-Condition ($LASTEXITCODE -eq 0) "Self-contained single-file AppHost publish failed."

		$singleFileExecutable = Join-Path $publishRoot "rtaime.exe"
		Assert-Condition (Test-Path -LiteralPath $singleFileExecutable -PathType Leaf) "Single-file AppHost executable was not produced."
		Assert-Condition ((Get-PeSubsystem -Path $singleFileExecutable) -eq 2) "Single-file Windows AppHost must retain the WINDOWS_GUI PE subsystem."

		$singleFileArgs = @("--profile=InvalidProfile", "--show-console")
		Invoke-BootstrapFailureProbe -Executable $singleFileExecutable -Arguments $singleFileArgs -WorkRoot (Join-Path $tempRoot "single-file-probe") -ExpectedDetail "Unknown startup profile"
	}

	Write-Host "Windows application bootstrap qualification PASS"
	Write-Host "PE subsystem: WINDOWS_GUI"
	Write-Host "Redirected diagnostics: PASS"
	Write-Host "Headless startup diagnostics: PASS"
	Write-Host "Single-file qualification: $(if ($QualifySingleFile) { 'PASS' } else { 'NOT_REQUESTED' })"
}
finally {
	if (Test-Path -LiteralPath $tempRoot) {
		Remove-Item -LiteralPath $tempRoot -Recurse -Force
	}
}
