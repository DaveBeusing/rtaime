# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
# Outer qualification inventory only. Never called by the Program boundary.
Set-StrictMode -Version Latest
function Get-SsdStorageTopology($Storage) {
    $items = @($Storage)
    if ($items.Count -ne 2 -or @($items | Where-Object { $_.mapping -cne "OBSERVED" }).Count -gt 0) { return "UNVERIFIED" }
    # Disk numbers are scoped to this capture; compare both the number and stable identity.
    if ($items[0].deviceKey -ceq $items[1].deviceKey) {
        if ($items[0].diskNumber -ne $items[1].diskNumber) { return "UNVERIFIED" }
        return "SAME_DEVICE"
    }
    if ($items[0].diskNumber -eq $items[1].diskNumber) { return "UNVERIFIED" }
    return "SEPARATE_DEVICES"
}
function Get-SsdFileStorage([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @{ mapping = "UNVERIFIED"; reason = "Generated reference location belongs to the existing fixture." }
    }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Input file is missing: $full" }
    try {
        # Reject reparse points, including directory junctions and mounted volumes.
        # Drive-letter partition mapping would otherwise identify the wrong device.
        $item = Get-Item -LiteralPath $full -Force
        $fileBytes = $item.Length
        $lastWriteUtc = $item.LastWriteTimeUtc.ToString("O")
        $parent = $item
        while ($null -ne $parent) {
            if (($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse/mounted path requires independent device mapping." }
            $parent = if ($parent -is [IO.FileInfo]) { $parent.Directory } else { $parent.Parent }
        }
        $drive = [IO.Path]::GetPathRoot($full).TrimEnd('\').TrimEnd(':')
        if ($drive.Length -ne 1) { throw "Only a direct local volume is mapped." }
        $partitions = @(Get-Partition -DriveLetter $drive -ErrorAction Stop)
        if ($partitions.Count -ne 1) { throw "Partition mapping is ambiguous." }
        $disk = Get-Disk -Number $partitions[0].DiskNumber -ErrorAction Stop
        $volume = Get-Volume -DriveLetter $drive -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace([string]$disk.UniqueId)) { throw "Disk has no stable inventory identity." }
        $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes(([string]$disk.UniqueId).Trim()))).ToLowerInvariant()
        $native = @(Get-CimInstance Win32_DiskDrive -Filter "Index = $($disk.Number)" -ErrorAction Stop)
        if ($native.Count -ne 1) { throw "Native disk mapping is ambiguous." }
        $drivers = @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop)
        $controllers = @(Get-CimAssociatedInstance -InputObject $native[0] -Association Win32_SCSIControllerDevice -ResultClassName Win32_SCSIController -ErrorAction Stop)
        if ($controllers.Count -gt 16) { throw "Controller inventory exceeds bounded capture." }
        $controllerInventory = @($controllers | Sort-Object PNPDeviceID | ForEach-Object {
            $controller = $_
            $matched = @($drivers | Where-Object { $_.DeviceID -eq $controller.PNPDeviceID })
            @{ name = [string]$controller.Name; pnpDeviceId = [string]$controller.PNPDeviceID
                drivers = @($matched | Sort-Object DriverVersion, InfName | Select-Object DriverVersion, DriverDate, InfName, DriverProviderName) }
        })
        $diskDrivers = @($drivers | Where-Object { $_.DeviceID -eq $native[0].PNPDeviceID } |
            Select-Object DriverVersion, DriverDate, InfName, DriverProviderName)
        # Never infer SSD media from SATA/NVMe bus type or matching drive letters.
        $medium = "UNVERIFIED"
        if (-not [string]::IsNullOrWhiteSpace([string]$disk.SerialNumber)) {
            $physical = @(Get-PhysicalDisk -ErrorAction Stop | Where-Object {
                ([string]$_.SerialNumber).Trim() -ceq ([string]$disk.SerialNumber).Trim() })
            if ($physical.Count -eq 1 -and [string]$physical[0].MediaType -eq "SSD") { $medium = "SSD" }
        }
        return @{ mapping = "OBSERVED"; deviceKey = $key; diskNumber = [int]$disk.Number
            model = [string]$disk.FriendlyName; busType = [string]$disk.BusType
            firmware = [string]$native[0].FirmwareRevision; storageClass = $medium
            partitionNumber = [int]$partitions[0].PartitionNumber; partitionOffset = [long]$partitions[0].Offset
            fileSystem = [string]$volume.FileSystem; volumeSize = [long]$volume.Size
            volumeFreeBytes = [long]$volume.SizeRemaining; fileBytes = $fileBytes; lastWriteUtc = $lastWriteUtc
            diskDrivers = $diskDrivers; controllers = $controllerInventory
            controllerStatus = if ($controllerInventory.Count -gt 0 -and
                @($controllerInventory | Where-Object { @($_.drivers).Count -ne 1 }).Count -eq 0) { "OBSERVED" } else { "UNVERIFIED" }
            # Device keys are local correlation hashes, not hardware attestations.
            physicalStatus = "UNVERIFIED" }
    } catch { return @{ mapping = "UNVERIFIED"; reason = $_.Exception.Message } }
}
