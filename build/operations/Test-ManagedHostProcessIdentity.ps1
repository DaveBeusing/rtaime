# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('rtaime-process-identity-' + [Guid]::NewGuid().ToString('N'))
$script:ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:passed = 0
$current = [Diagnostics.Process]::GetCurrentProcess()
try { $shellPath = $current.MainModule.FileName } finally { $current.Dispose() }

function Assert-IdentityTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Write-TestJson {
    param($Value, [string]$Path)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
}

function Stop-OwnHelpers {
    $failures = [Collections.Generic.List[Exception]]::new()
    foreach ($process in $script:ownedProcesses) {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(10000)) { throw 'An owned fixture process did not exit during cleanup.' }
            }
        } catch { $failures.Add($_.Exception) }
        finally { $process.Dispose() }
    }
    $script:ownedProcesses.Clear()
    if ($failures.Count -gt 0) { throw [AggregateException]::new('Fixture cleanup failed.', $failures.ToArray()) }
}

function Start-OwnHelper {
	param($Fixture, [string]$Name, [switch]$IgnoreStop, [string]$Children = '', [string]$StopFile = '')
    $readyFile = Join-Path $Fixture.Root ($Name + '.ready')
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $shellPath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
	if (-not $StopFile) { $StopFile = $Fixture.StopPath }
	foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $helperPath,
		'-StopFile', $StopFile, '-ReadyFile', $readyFile)) {
        $info.ArgumentList.Add($argument)
    }
    if ($IgnoreStop) { $info.ArgumentList.Add('-IgnoreStop') }
    if ($Children) { $info.ArgumentList.Add('-Children'); $info.ArgumentList.Add($Children) }
    $process = [Diagnostics.Process]::Start($info)
    Assert-IdentityTest ($null -ne $process) "Could not start the owned $Name fixture."
    $script:ownedProcesses.Add($process)
    $null = $process.SafeHandle
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists($readyFile)) {
        Assert-IdentityTest (-not $process.HasExited) "The owned $Name fixture exited before its startup handshake."
        Assert-IdentityTest ($deadline.Elapsed.TotalSeconds -lt 20) "The owned $Name fixture did not complete its startup handshake."
        Start-Sleep -Milliseconds 25
    }
    return $process
}

function New-Identity {
    param([Diagnostics.Process]$Process, [string]$HostPath, [string]$RunId, [string]$LaunchId)
    try {
        $creationTime = $Process.get_StartTime()
        $executablePath = $Process.get_MainModule().FileName
    } catch {
        throw "Cannot inspect owned fixture PID $($Process.Id): $($_.Exception.Message)"
    }
    Assert-IdentityTest (-not [string]::IsNullOrWhiteSpace($executablePath)) "Owned fixture PID $($Process.Id) has no observable executable path."
    return [ordered]@{
        processId = $Process.Id
        creationTimeUtcTicks = $creationTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
        executablePath = $executablePath
        hostPath = [IO.Path]::GetFullPath($HostPath)
        launchId = $LaunchId
        lifecycleRunId = $RunId
    }
}

function New-IdentityFixture {
    param([string]$Name, [switch]$IgnoreStop)
    $root = Join-Path $tempRoot $Name
    $install = Join-Path $root 'install'
    $tools = Join-Path $install 'tools'
    $work = Join-Path $root 'work'
    $runId = [Guid]::NewGuid().ToString('N')
    $runRoot = Join-Path (Join-Path $work 'runs') $runId
    New-Item -ItemType Directory -Path $tools, $runRoot, (Join-Path $runRoot 'child-ownership'), (Join-Path $root 'state') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-ManagedHostLifecycle.ps1') -Destination $tools
    $policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'host-lifecycle-policy.json') -Raw | ConvertFrom-Json
    $policy.shutdown.timeoutMs = 3000
    $policy.startup.probeTimeoutMs = 50
    Write-TestJson $policy (Join-Path $tools 'host-lifecycle-policy.json')
    $verifier = @'
param([string]$BundlePath, [switch]$RequireTrustedProductionKey)
[IO.File]::WriteAllText((Join-Path $BundlePath 'unexpected-verifier.txt'), 'called')
throw 'The identity regression must not start a new installation.'
'@
    [IO.File]::WriteAllText((Join-Path $tools 'Test-OfflineReleaseBundle.ps1'), $verifier)
    $hosts = @{}
    foreach ($role in @('Control', 'Runtime', 'AI')) {
        $folder = Join-Path $install ("product/rtaime.$($role)Host")
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $hosts[$role] = Join-Path $folder ("rtaime.$($role)Host.dll")
        [IO.File]::WriteAllText($hosts[$role], 'Disposable identity fixture; never executed.')
    }
    $fixture = [pscustomobject]@{
        Root = $root; Install = $install; Work = $work; RunId = $runId
        Instance = ('identity-' + [Guid]::NewGuid().ToString('N'))
        Controller = (Join-Path $tools 'Invoke-ManagedHostLifecycle.ps1')
        StatePath = (Join-Path $work 'lifecycle-state.json')
        ReadinessPath = (Join-Path $runRoot 'readiness.json')
        StopPath = (Join-Path $runRoot 'stop.signal')
        OwnershipPath = (Join-Path (Join-Path $runRoot 'child-ownership') 'ownership.json')
        ReceiptPath = (Join-Path $work 'operational-receipt-latest.json')
        Hosts = $hosts; State = $null; Readiness = $null; Journal = $null; Processes = @{}; MalformedReadiness = $false
    }
    $runtime = Start-OwnHelper $fixture 'runtime' -IgnoreStop:$IgnoreStop
    $ai = Start-OwnHelper $fixture 'ai' -IgnoreStop:$IgnoreStop
    $control = Start-OwnHelper $fixture 'control' -IgnoreStop:$IgnoreStop -Children "$($runtime.Id),$($ai.Id)"
    $fixture.Processes = @{ Control = $control; Runtime = $runtime; AI = $ai }
    $controlIdentity = New-Identity $control $hosts.Control $runId $runId
    $runtimeIdentity = New-Identity $runtime $hosts.Runtime $runId ([Guid]::NewGuid().ToString('N'))
    $aiIdentity = New-Identity $ai $hosts.AI $runId ([Guid]::NewGuid().ToString('N'))
    $endpoints = [ordered]@{}
    foreach ($role in @('control', 'runtime', 'ai')) {
        $endpoints[$role] = ([string]$policy.endpoints.$role) -replace '\.default$', ('.' + $fixture.Instance)
    }
    $fixture.State = [ordered]@{
        schemaVersion = '1.0'; status = 'RUNNING'; runId = $runId
        installPath = $install; stateRoot = (Join-Path $root 'state'); instanceId = $fixture.Instance
        controlProcessId = $control.Id; runtimeProcessId = $runtime.Id; aiProcessId = $ai.Id
        controlIdentity = $controlIdentity; runtimeIdentity = $runtimeIdentity; aiIdentity = $aiIdentity
        readinessPath = $fixture.ReadinessPath; stopPath = $fixture.StopPath; endpoints = $endpoints
        childOwnershipPath = $fixture.OwnershipPath
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); readyAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $fixture.Readiness = [ordered]@{
        processId = $control.Id; lifecycleRunId = $runId; state = 'READY'; health = 'HEALTHY'
        runtimeSupervision = [ordered]@{ processId = $runtime.Id; state = 'HEALTHY'; startAttempts = 1; processIdentity = $runtimeIdentity }
        aiSupervision = [ordered]@{ processId = $ai.Id; state = 'HEALTHY'; startAttempts = 1; processIdentity = $aiIdentity }
    }
    $journal = [ordered]@{
        schemaVersion = '1.0'; lifecycleRunId = $runId; controlIdentity = $controlIdentity
        children = @(
            [ordered]@{ role = 'runtime'; name = 'RuntimeHost'; endpoint = $endpoints.runtime; hostPath = $hosts.Runtime; maxStartAttempts = 5 },
            [ordered]@{ role = 'ai'; name = 'AIHost'; endpoint = $endpoints.ai; hostPath = $hosts.AI; maxStartAttempts = 5 }
        )
        generations = @(
            [ordered]@{ role = 'runtime'; launchId = $runtimeIdentity.launchId; attempt = 1; state = 'RUNNING'; processIdentity = $runtimeIdentity },
            [ordered]@{ role = 'ai'; launchId = $aiIdentity.launchId; attempt = 1; state = 'RUNNING'; processIdentity = $aiIdentity }
        )
    }
    # Independent copies keep corrupt startup/readiness fixtures from changing authoritative launch evidence.
    $fixture.Journal = $journal | ConvertTo-Json -Depth 32 | ConvertFrom-Json -Depth 32
    return $fixture
}

function Save-IdentityFixture {
    param($Fixture)
    Write-TestJson $Fixture.State $Fixture.StatePath
    if ($Fixture.MalformedReadiness) { [IO.File]::WriteAllText($Fixture.ReadinessPath, '{invalid') }
    else { Write-TestJson $Fixture.Readiness $Fixture.ReadinessPath }
    Write-TestJson $Fixture.Journal $Fixture.OwnershipPath
}

function Invoke-FixtureLifecycle {
    param($Fixture, [string]$Action)
    & $Fixture.Controller -Action $Action -InstallPath $Fixture.Install -StateRoot (Join-Path $Fixture.Root 'state') -WorkPath $Fixture.Work -InstanceId $Fixture.Instance -QualificationMode
}

function Assert-StaleIdentityRejected {
    param($Fixture, [string[]]$Actions = @('Status', 'Stop', 'Restart'))
    Save-IdentityFixture $Fixture
    $stateHash = (Get-FileHash -LiteralPath $Fixture.StatePath -Algorithm SHA256).Hash
    $readinessHash = (Get-FileHash -LiteralPath $Fixture.ReadinessPath -Algorithm SHA256).Hash
    $alive = @($Fixture.Processes.Values | Where-Object { -not $_.HasExited })
    foreach ($action in $Actions) {
        $failure = $null
        $result = $null
        try { $result = Invoke-FixtureLifecycle $Fixture $action } catch { $failure = $_ }
        if ($action -eq 'Status') {
            Assert-IdentityTest ($null -eq $failure) 'Status must report stale identity as a controlled result.'
        } else {
            Assert-IdentityTest ($null -ne $failure) "$action unexpectedly accepted stale identity."
            Assert-IdentityTest (Test-Path -LiteralPath $Fixture.ReceiptPath -PathType Leaf) "$action did not write a failure receipt."
            $result = Get-Content -LiteralPath $Fixture.ReceiptPath -Raw | ConvertFrom-Json
        }
        Assert-IdentityTest ([string]$result.status -eq 'FAIL' -and [string]$result.reasonCode -eq 'STALE_PROCESS_IDENTITY') "$action did not report STALE_PROCESS_IDENTITY."
        Assert-IdentityTest (-not (Test-Path -LiteralPath $Fixture.StopPath)) "$action signalled a stale process."
        Assert-IdentityTest (-not (Test-Path -LiteralPath (Join-Path $Fixture.Install 'unexpected-verifier.txt'))) "$action reached installation startup."
        Assert-IdentityTest ((Get-FileHash -LiteralPath $Fixture.StatePath -Algorithm SHA256).Hash -eq $stateHash) "$action changed stale state evidence."
        Assert-IdentityTest ((Get-FileHash -LiteralPath $Fixture.ReadinessPath -Algorithm SHA256).Hash -eq $readinessHash) "$action changed stale readiness evidence."
        foreach ($process in $alive) {
            $process.Refresh()
            Assert-IdentityTest (-not $process.HasExited) "$action terminated a process whose recorded identity was not trustworthy."
        }
    }
}

function Invoke-IdentityCase {
    param([string]$Name, [scriptblock]$Action, [switch]$IgnoreStop)
    try {
        $fixture = New-IdentityFixture $Name -IgnoreStop:$IgnoreStop
        & $Action $fixture
        $script:passed++
        Write-Host "Managed process identity PASS: $Name"
    } finally { Stop-OwnHelpers }
}

New-Item -ItemType Directory -Path $tempRoot | Out-Null
$helperPath = Join-Path $tempRoot 'owned-process.ps1'
[IO.File]::WriteAllText($helperPath, @'
param([string]$StopFile, [string]$ReadyFile, [switch]$IgnoreStop, [string]$Children = '')
$ErrorActionPreference = 'Stop'
$handles = [Collections.Generic.List[Diagnostics.Process]]::new()
try {
    foreach ($childId in $Children.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
        $child = [Diagnostics.Process]::GetProcessById([int]$childId)
        $null = $child.SafeHandle
        $handles.Add($child)
    }
    [IO.File]::WriteAllText($ReadyFile, 'ready')
    while ($IgnoreStop -or -not [IO.File]::Exists($StopFile)) { Start-Sleep -Milliseconds 25 }
    foreach ($child in $handles) {
        if (-not $child.WaitForExit(10000)) { throw 'Owned child did not stop.' }
    }
} finally {
    foreach ($child in $handles) { $child.Dispose() }
}
'@)

try {
    Invoke-IdentityCase 'ReusedControlPid' {
        param($f)
        $f.State.controlIdentity.creationTimeUtcTicks = ([long]$f.State.controlIdentity.creationTimeUtcTicks - 1).ToString([Globalization.CultureInfo]::InvariantCulture)
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'ReusedChildPidAfterControlExit' {
        param($f)
        $f.Processes.Control.Kill($true)
        Assert-IdentityTest ($f.Processes.Control.WaitForExit(10000)) 'Owned Control fixture did not exit.'
        $f.State.runtimeIdentity.creationTimeUtcTicks = ([long]$f.State.runtimeIdentity.creationTimeUtcTicks - 1).ToString([Globalization.CultureInfo]::InvariantCulture)
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'ExecutableMismatch' {
        param($f)
        $f.State.controlIdentity.executablePath = Join-Path $f.Root 'different-executable'
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'HostAssemblyMismatch' {
        param($f)
        $f.State.runtimeIdentity.hostPath = $f.Hosts.AI
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'SessionMismatch' {
        param($f)
        $f.State.aiIdentity.lifecycleRunId = [Guid]::NewGuid().ToString('N')
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'ControlLaunchMismatch' {
        param($f)
        $f.State.controlIdentity.launchId = [Guid]::NewGuid().ToString('N')
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'LegacyMissingIdentity' {
        param($f)
        foreach ($name in @('runId', 'controlIdentity', 'runtimeIdentity', 'aiIdentity')) { $f.State.Remove($name) }
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'ReadinessSessionMismatch' {
        param($f)
        $f.Readiness.lifecycleRunId = [Guid]::NewGuid().ToString('N')
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'MalformedReadiness' {
        param($f)
        $f.MalformedReadiness = $true
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'StartingWithoutChildOwnershipJournal' {
        param($f)
        $f.State.status = 'STARTING'
        foreach ($role in @('runtime', 'ai')) {
            $f.State[$role + 'ProcessId'] = $null
            $f.State[$role + 'Identity'] = $null
        }
        Save-IdentityFixture $f
        Remove-Item -LiteralPath $f.ReadinessPath
        Remove-Item -LiteralPath $f.OwnershipPath
        $f.Processes.Control.Kill($true)
        Assert-IdentityTest ($f.Processes.Control.WaitForExit(10000)) 'Owned Control fixture did not exit.'
        $stateHash = (Get-FileHash -LiteralPath $f.StatePath -Algorithm SHA256).Hash
        $failure = $null
        try { Invoke-FixtureLifecycle $f 'Stop' | Out-Null } catch { $failure = $_ }
        Assert-IdentityTest ($null -ne $failure) 'Stop accepted a starting lifecycle with unknown child identities.'
        Assert-IdentityTest (Test-Path -LiteralPath $f.ReceiptPath -PathType Leaf) 'Stop did not retain a failure receipt for unknown children.'
        $receipt = Get-Content -LiteralPath $f.ReceiptPath -Raw | ConvertFrom-Json
        Assert-IdentityTest ([string]$receipt.status -eq 'FAIL' -and [string]$receipt.reasonCode -eq 'STALE_PROCESS_IDENTITY') 'Unknown child identities did not report STALE_PROCESS_IDENTITY.'
        Assert-IdentityTest ((Get-FileHash -LiteralPath $f.StatePath -Algorithm SHA256).Hash -eq $stateHash) 'Stop changed state evidence for unknown children.'
        Assert-IdentityTest (-not (Test-Path -LiteralPath $f.StopPath)) 'Stop signalled a run with unknown children.'
        foreach ($role in @('Runtime', 'AI')) {
            $f.Processes[$role].Refresh()
            Assert-IdentityTest (-not $f.Processes[$role].HasExited) 'Stop terminated a child without recorded ownership evidence.'
        }
    }
    Invoke-IdentityCase 'ReplacementRecoveredWithoutReadinessAfterControlCrash' {
        param($f)
        $replacementLaunch = [Guid]::NewGuid().ToString('N')
        $replacementStop = Join-Path (Split-Path -Parent $f.OwnershipPath) ("runtime-$replacementLaunch.stop")
        $replacement = Start-OwnHelper $f 'runtime-replacement' -StopFile $replacementStop
        $replacementIdentity = New-Identity $replacement $f.Hosts.Runtime $f.RunId $replacementLaunch
        foreach ($role in @('Runtime', 'AI', 'Control')) {
            $f.Processes[$role].Kill($false)
            Assert-IdentityTest ($f.Processes[$role].WaitForExit(10000)) "Initial $role fixture did not exit."
        }
        foreach ($generation in $f.Journal.generations) { $generation.state = 'EXITED' }
        $f.Journal.generations += [pscustomobject]@{ role = 'runtime'; launchId = $replacementLaunch; attempt = 2; state = 'RUNNING'; processIdentity = $replacementIdentity }
        Save-IdentityFixture $f
        Remove-Item -LiteralPath $f.ReadinessPath
        $bystander = Start-OwnHelper $f 'unrelated' -IgnoreStop
        $receipt = Invoke-FixtureLifecycle $f 'Stop'
        Assert-IdentityTest ($replacement.WaitForExit(10000)) 'Owned replacement remained alive after successful recovery.'
        Assert-IdentityTest ([string]$receipt.status -eq 'PASS' -and $receipt.recoveredOwnedChildren -eq 1) 'Stop did not recover the journalled replacement after readiness and owner loss.'
        Assert-IdentityTest (-not $bystander.HasExited) 'Recovery terminated an unrelated process.'
        Assert-IdentityTest (-not (Test-Path -LiteralPath $f.StatePath)) 'Successful recovery retained active lifecycle state.'
        Assert-IdentityTest (Test-Path -LiteralPath $f.OwnershipPath) 'Recovery discarded the durable generation audit.'
    }
    Invoke-IdentityCase 'IncompleteLaunchOutcomeCannotProveShutdown' {
        param($f)
        $replacementLaunch = [Guid]::NewGuid().ToString('N')
        $replacement = Start-OwnHelper $f 'unrecorded-replacement' -IgnoreStop
        $f.Journal.generations += [pscustomobject]@{ role = 'runtime'; launchId = $replacementLaunch; attempt = 2; state = 'LAUNCH_PENDING'; processIdentity = $null }
        foreach ($role in @('Runtime', 'AI', 'Control')) {
            $f.Processes[$role].Kill($false)
            Assert-IdentityTest ($f.Processes[$role].WaitForExit(10000)) "Initial $role fixture did not exit."
        }
        Assert-StaleIdentityRejected $f
        Assert-IdentityTest (-not $replacement.HasExited) 'An unknown launch outcome authorized process termination.'
        Assert-IdentityTest (Test-Path -LiteralPath $f.StatePath) 'Unknown launch outcome discarded recovery state.'
    }
    Invoke-IdentityCase 'ReplacementIdentityProtectsUnrelatedLiveProcess' {
        param($f)
        $f.Processes.Runtime.Kill($false)
        Assert-IdentityTest ($f.Processes.Runtime.WaitForExit(10000)) 'Initial Runtime fixture did not exit.'
        $f.Journal.generations[0].state = 'EXITED'
        $bystander = Start-OwnHelper $f 'reused-replacement-id' -IgnoreStop
        $launch = [Guid]::NewGuid().ToString('N')
        $identity = New-Identity $bystander $f.Hosts.Runtime $f.RunId $launch
        $identity.creationTimeUtcTicks = ([long]$identity.creationTimeUtcTicks - 1).ToString([Globalization.CultureInfo]::InvariantCulture)
        $f.Journal.generations += [pscustomobject]@{ role = 'runtime'; launchId = $launch; attempt = 2; state = 'RUNNING'; processIdentity = $identity }
        Assert-StaleIdentityRejected $f
        Assert-IdentityTest (-not $bystander.HasExited) 'Replacement recovery killed a reused PID with mismatched creation identity.'
    }
    Invoke-IdentityCase 'OldReadinessCannotQualifyReplacement' {
        param($f)
        $f.Processes.Runtime.Kill($false)
        Assert-IdentityTest ($f.Processes.Runtime.WaitForExit(10000)) 'Initial Runtime fixture did not exit.'
        $f.Journal.generations[0].state = 'EXITED'
        $launch = [Guid]::NewGuid().ToString('N')
        $stop = Join-Path (Split-Path -Parent $f.OwnershipPath) ("runtime-$launch.stop")
        $replacement = Start-OwnHelper $f 'current-runtime' -StopFile $stop
        $identity = New-Identity $replacement $f.Hosts.Runtime $f.RunId $launch
        $f.Journal.generations += [pscustomobject]@{ role = 'runtime'; launchId = $launch; attempt = 2; state = 'RUNNING'; processIdentity = $identity }
        Save-IdentityFixture $f
        $status = Invoke-FixtureLifecycle $f 'Status'
        Assert-IdentityTest ($status.checks.processIdentityVerified -and $status.checks.childProcessesRunning) 'The replacement process identities were not observable.'
        Assert-IdentityTest (-not $status.checks.controlReadinessEvidence) 'Readiness for the exited generation qualified its replacement.'
        $receipt = Invoke-FixtureLifecycle $f 'Stop'
        Assert-IdentityTest ([string]$receipt.status -eq 'PASS' -and $replacement.WaitForExit(10000)) 'Stale but correlated readiness prevented verified replacement cleanup.'
    }
    Invoke-IdentityCase 'OwnershipJournalParentMismatch' {
        param($f)
        $f.Journal.controlIdentity.creationTimeUtcTicks = ([long]$f.Journal.controlIdentity.creationTimeUtcTicks - 1).ToString([Globalization.CultureInfo]::InvariantCulture)
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'OwnershipJournalRoleCannotEscapeRun' {
        param($f)
        $f.Journal.generations[0].role = '../../foreign'
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'OwnershipJournalRejectsDuplicateAttempt' {
        param($f)
        $f.Journal.generations += [pscustomobject]@{ role = 'runtime'; launchId = [Guid]::NewGuid().ToString('N'); attempt = 1; state = 'NOT_STARTED'; processIdentity = $null }
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'OwnershipJournalRejectsOversizedEvidence' {
        param($f)
        $f.Journal | Add-Member -NotePropertyName diagnostic -NotePropertyValue ('x' * (4MB))
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'ChildStopSignalMustBeRegularFile' {
        param($f)
        $path = Join-Path (Split-Path -Parent $f.OwnershipPath) ("runtime-$($f.State.runtimeIdentity.launchId).stop")
        New-Item -ItemType Directory -Path $path | Out-Null
        Assert-StaleIdentityRejected $f
    }
    Invoke-IdentityCase 'AllRecordedProcessesExited' {
        param($f)
        Save-IdentityFixture $f
        foreach ($process in $f.Processes.Values) {
            if (-not $process.HasExited) { $process.Kill($true) }
            Assert-IdentityTest ($process.WaitForExit(10000)) 'Owned fixture did not exit.'
        }
        $receipt = Invoke-FixtureLifecycle $f 'Stop'
        Assert-IdentityTest ([string]$receipt.status -eq 'PASS') 'Already exited recorded processes prevented a safe Stop.'
        foreach ($path in @($f.StatePath, $f.ReadinessPath, $f.StopPath)) {
            Assert-IdentityTest (-not (Test-Path -LiteralPath $path)) 'Successful Stop retained active lifecycle state.'
        }
    }
    Invoke-IdentityCase 'GracefulOwnedStop' {
        param($f)
        Save-IdentityFixture $f
        $receipt = Invoke-FixtureLifecycle $f 'Stop'
        Assert-IdentityTest ([string]$receipt.status -eq 'PASS' -and $receipt.graceful) 'Owned graceful stop did not pass.'
        foreach ($process in $f.Processes.Values) { Assert-IdentityTest ($process.WaitForExit(10000)) 'Owned process remained after graceful Stop.' }
        foreach ($path in @($f.StatePath, $f.ReadinessPath, $f.StopPath)) {
            Assert-IdentityTest (-not (Test-Path -LiteralPath $path)) 'Successful Stop retained active lifecycle state.'
        }
    }
    Invoke-IdentityCase 'ForcedOwnedStop' {
        param($f)
        Save-IdentityFixture $f
        $bystander = Start-OwnHelper $f 'bystander' -IgnoreStop
        $failure = $null
        try { Invoke-FixtureLifecycle $f 'Stop' | Out-Null } catch { $failure = $_ }
        Assert-IdentityTest ($null -ne $failure) 'Forced Stop must fail qualification.'
        $receipt = Get-Content -LiteralPath $f.ReceiptPath -Raw | ConvertFrom-Json
        Assert-IdentityTest ([string]$receipt.status -eq 'FAIL' -and [string]$receipt.reasonCode -eq 'FORCEFUL_TERMINATION') 'Forced Stop lost its failure reason.'
        Assert-IdentityTest (Test-Path -LiteralPath $f.StatePath -PathType Leaf) 'Forced Stop discarded recovery evidence.'
        foreach ($process in $f.Processes.Values) { Assert-IdentityTest ($process.WaitForExit(10000)) 'Owned process remained after forced Stop.' }
        Assert-IdentityTest (-not $bystander.HasExited) 'Forced Stop terminated an unrelated owned test fixture.'
    } -IgnoreStop
    Invoke-IdentityCase 'ExitDuringIdentityVerification' {
        param($f)
        $bystander = Start-OwnHelper $f 'bystander' -IgnoreStop
        & {
            param($fixture, $unrelatedProcess)
            $parseErrors = $null
            $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile($fixture.Controller, [ref]$tokens, [ref]$parseErrors)
            Assert-IdentityTest ($parseErrors.Count -eq 0) 'Could not parse the controller for the exit-race test.'
            foreach ($name in @('Assert-Condition', 'Get-PropertyValue', 'Assert-ProcessIdentity',
                'Test-SamePath', 'Get-ProcessExecutablePath', 'Open-VerifiedProcess',
                'Wait-ForExit', 'Stop-ProcessTreeForcefully')) {
                $definitions = @($ast.EndBlock.Statements | Where-Object {
                    $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name
                })
                Assert-IdentityTest ($definitions.Count -eq 1) "Expected one production definition of $name."
                . ([scriptblock]::Create($definitions[0].Extent.Text))
            }
            $realImageGetter = (Get-Item Function:Get-ProcessExecutablePath).ScriptBlock
            $ownedControl = $fixture.Processes.Control
            $observation = [pscustomobject]@{ Calls = 0; Process = $null }
            # Exit after the real image observation, before ownership verification returns.
            # This override exists only inside this isolated invocation scope.
            function Get-ProcessExecutablePath {
                param([Diagnostics.Process]$Process)
                $imagePath = & $realImageGetter -Process $Process
                Assert-IdentityTest ($Process.Id -eq $ownedControl.Id) 'The exit hook received an unexpected process.'
                $observation.Calls++
                $observation.Process = $Process
                $ownedControl.Kill($true)
                Assert-IdentityTest ($ownedControl.WaitForExit(10000)) 'Owned Control did not exit during identity verification.'
                return $imagePath
            }
            $verified = $null
            try {
                $verified = Open-VerifiedProcess -Identity $fixture.State.controlIdentity -ProcessId $ownedControl.Id -HostPath $fixture.Hosts.Control -RunId $fixture.RunId -LaunchId $fixture.RunId
                Assert-IdentityTest ($observation.Calls -eq 1) 'The process did not exit at the intended verification boundary.'
                if ($null -ne $verified) {
                    Assert-IdentityTest ([object]::ReferenceEquals($verified, $observation.Process)) 'Verification replaced the observed process instance.'
                    Assert-IdentityTest $verified.HasExited 'Verification returned a live process after the observed exit.'
                    Assert-IdentityTest (Wait-ForExit -Process $verified -TimeoutMs 1000) 'Waiting on the retained exited process did not complete.'
                    Stop-ProcessTreeForcefully -Process $verified -TimeoutMs 1000
                }
                $unrelatedProcess.Refresh()
                Assert-IdentityTest (-not $unrelatedProcess.HasExited) 'Exit-race handling terminated an unrelated owned test fixture.'
            } finally {
                if ($null -ne $verified) { $verified.Dispose() }
            }
        } $f $bystander
    }
    Write-Host "Managed host process identity qualification PASS ($script:passed cases)."
} finally {
    Stop-OwnHelpers
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
