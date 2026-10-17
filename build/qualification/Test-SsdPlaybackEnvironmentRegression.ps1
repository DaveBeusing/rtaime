# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "SsdPlaybackStorage.ps1")
$root = Join-Path ([IO.Path]::GetTempPath()) ("rtaime-ssd-environment-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($root)
$environmentPath = Join-Path $root "environment.json"
$outcomePath = Join-Path $root "ssd-playback.outcome.json"
$sha = "a" * 40
$now = [DateTimeOffset]::UtcNow
$media = @(@{ sha256 = "b"*64; bytes = 1000 }, @{ sha256 = "c"*64; bytes = 1000 })
$outcome = @{ sourceCommit = $sha; buildCommit = $sha; capturedAt = $now.ToString("O")
    result = @{ sourceCommit = $sha; buildCommit = $sha; media = $media; startedAt = $now.AddHours(-1).ToString("O") } }
$disk = @{ mapping = "OBSERVED"; deviceKey = "d"*64; diskNumber = 0; partitionNumber = 1
    partitionOffset = 1048576; model = "synthetic"; firmware = "fixture"; busType = "SATA"
    fileSystem = "NTFS"; volumeSize = 100000; volumeFreeBytes = 10000; fileBytes = 1000
    lastWriteUtc = $now.AddDays(-1).ToString("O"); storageClass = "SSD"; physicalStatus = "UNVERIFIED"
    controllers = @(@{ pnpDeviceId = "synthetic"; drivers = @(@{ DriverVersion = "fixture" }) })
    controllerStatus = "OBSERVED" }
$base = @{ schemaVersion = "1.1"; sourceCommit = $sha; capturedAt = $now.AddHours(-2).ToString("O")
    completedAt = $now.AddMinutes(1).ToString("O"); captureStatus = "FINAL"
    storage = @($disk,$disk); storageAfter = @($disk,$disk); topology = "SAME_DEVICE"
    media = $media; outcomeSha256 = ""; physicalStatus = "UNVERIFIED"
    cache = "UNVERIFIED"; pressure = "UNVERIFIED"; selectedDecoder = "UNVERIFIED"
    fileOnlyReadLatency = "UNVERIFIED"; physicalOutput = "UNVERIFIED"; gpuTelemetry = "UNVERIFIED" }
function Write-Json($Value, [string]$Path) { $Value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Copy-Fixture { $base | ConvertTo-Json -Depth 32 | ConvertFrom-Json -AsHashtable }
function Verify($Value) {
    Write-Json $Value $environmentPath
    & (Join-Path $PSScriptRoot "Test-SsdPlaybackEnvironment.ps1") -EnvironmentPath $environmentPath -ExpectedSourceCommit $sha -OutcomePath $outcomePath
}
$count = 0
function Rejected($Value, [string]$Reason) {
    $rejected = $false
    try { Verify $Value } catch { $rejected = $true }
    if (-not $rejected) { throw "Inventory accepted invalid fixture: $Reason" }
    $script:count++
}
try {
    Write-Json $outcome $outcomePath
    $base.outcomeSha256 = (Get-FileHash -LiteralPath $outcomePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Verify $base
    $v = Copy-Fixture
    $v.storage[1].deviceKey = "e"*64; $v.storage[1].diskNumber = 1
    $v.storageAfter[1].deviceKey = "e"*64; $v.storageAfter[1].diskNumber = 1
    $v.topology = "SEPARATE_DEVICES"; Verify $v
    $unknown = Copy-Fixture
    $unknown.storage = @(@{ mapping = "UNVERIFIED"; reason = "Unavailable" }, @{ mapping = "UNVERIFIED"; reason = "Unavailable" })
    $unknown.storageAfter = $unknown.storage; $unknown.topology = "UNVERIFIED"; Verify $unknown
    $v = Copy-Fixture; $v.sourceCommit = "f"*40; Rejected $v "foreign source"
    $v = Copy-Fixture; $v.outcomeSha256 = "f"*64; Rejected $v "changed report bytes"
    $v = Copy-Fixture; $v.schemaVersion = "1.0"; Rejected $v "old environment is not a new capture"
    $v = Copy-Fixture; $v.captureStatus = "INITIAL"; Rejected $v "incomplete run"
    $v = Copy-Fixture; $v.topology = "SEPARATE_DEVICES"; Rejected $v "drive letters cannot invent devices"
    $v = Copy-Fixture; $v.storage[1].diskNumber = 1; Rejected $v "duplicate identity on different disks"
    $v = Copy-Fixture; $v.storage[1].deviceKey = "e"*64; Rejected $v "different identity on same disk"
    $v = Copy-Fixture; $v.storage[0].deviceKey = "fake-device"; Rejected $v "unhashed device identity"
    $v = Copy-Fixture; $v.storageAfter[0].firmware = "changed"; Rejected $v "firmware changed"
    $v = Copy-Fixture; $v.storageAfter[0].lastWriteUtc = $now.ToString("O"); Rejected $v "file changed"
    $v = Copy-Fixture; $v.media[1].sha256 = "f"*64; Rejected $v "media substitution"
    $v = Copy-Fixture; $v.storage[0].fileBytes = 999; Rejected $v "wrong file size"
    $v = Copy-Fixture; $v.storage[0].volumeFreeBytes = 100001; Rejected $v "invalid free capacity"
    $v = Copy-Fixture; $v.storage[0].controllers = @(); Rejected $v "fabricated controller observation"
    $v = Copy-Fixture; $v.storage[0].controllers[0].drivers[0].DriverVersion = ""; Rejected $v "missing driver version"
    $v = Copy-Fixture; $v.capturedAt = $now.ToString("O"); Rejected $v "inventory after playback"
    $v = Copy-Fixture; $v.completedAt = $now.AddMinutes(-1).ToString("O"); Rejected $v "missing final capture"
    foreach ($name in @("physicalStatus","cache","pressure","selectedDecoder","fileOnlyReadLatency","physicalOutput","gpuTelemetry")) {
        $v = Copy-Fixture; $v[$name] = "PASS"; Rejected $v "false physical claim $name"
    }
    # Exercise the real collector against synthetic Storage/CIM responses.
    # These fixtures test mapping code, not hardware or installed driver support.
    function Get-Partition { param($DriveLetter,$ErrorAction) [pscustomobject]@{ DiskNumber=0; PartitionNumber=1; Offset=1048576 } }
    function Get-Disk { param($Number,$ErrorAction) [pscustomobject]@{ Number=0; UniqueId="synthetic"; SerialNumber="fixture"; FriendlyName="fixture"; BusType="NVMe" } }
    function Get-Volume { param($DriveLetter,$ErrorAction) [pscustomobject]@{ FileSystem="NTFS"; Size=100000; SizeRemaining=10000 } }
    function Get-PhysicalDisk { param($ErrorAction) [pscustomobject]@{ SerialNumber="fixture"; MediaType="SSD" } }
    function Get-CimInstance {
        param($ClassName,$Filter,$ErrorAction)
        if ($ClassName -eq "Win32_DiskDrive") { return [pscustomobject]@{ PNPDeviceID="disk"; FirmwareRevision="fixture" } }
        if ($ClassName -eq "Win32_PnPSignedDriver") { return [pscustomobject]@{ DeviceID="controller"; DriverVersion="fixture"; DriverDate="fixture"; InfName="fixture"; DriverProviderName="fixture" } }
        throw "Unexpected CIM query"
    }
    function Get-CimAssociatedInstance { param($InputObject,$Association,$ResultClassName,$ErrorAction) [pscustomobject]@{ Name="fixture"; PNPDeviceID="controller" } }
    $file = Join-Path $root "input.bin"; [IO.File]::WriteAllBytes($file, [byte[]]::new(1000))
    $observed = Get-SsdFileStorage $file
    if ($observed.mapping -cne "OBSERVED" -or $observed.controllerStatus -cne "OBSERVED" -or
        $observed.storageClass -cne "SSD" -or $observed.busType -cne "NVMe") { throw "Collector mapping regression: $($observed | ConvertTo-Json -Depth 10)" }
    function Get-PhysicalDisk { param($ErrorAction) @() }
    $ambiguous = Get-SsdFileStorage $file
    if ($ambiguous.mapping -cne "OBSERVED" -or $ambiguous.storageClass -cne "UNVERIFIED") { throw "Bus type was substituted for SSD medium." }
    function Get-Partition { param($DriveLetter,$ErrorAction) throw "Device unavailable" }
    $lost = Get-SsdFileStorage $file
    if ($lost.mapping -cne "UNVERIFIED" -or (Get-SsdStorageTopology @($observed,$lost)) -cne "UNVERIFIED") { throw "Unavailable device was qualified." }
    Write-Host "SSD environment: 3 valid synthetic inventories, $count rejected fixtures, collector/medium/unavailable regressions PASS; physical UNVERIFIED."
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike "rtaime-ssd-environment-*") { throw "Unsafe temporary cleanup target." }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
