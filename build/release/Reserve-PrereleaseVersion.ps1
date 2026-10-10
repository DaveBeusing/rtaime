# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param(
    [ValidateSet('PREVIEW', 'RC')][string]$Channel = 'PREVIEW',
    [string]$Remote = 'origin',
    [string]$SourceCommit = '',
    [string]$BuildId = $env:RTAIME_BUILD_ID,
    [string]$OutputPath = 'artifacts/version-reservation.json'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$base = $props.SelectSingleNode('//VersionPrefix').InnerText.Trim()
if ($base -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid target VersionPrefix.' }
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve HEAD.' }
if ($SourceCommit -eq '') { $SourceCommit = $head }
if ($SourceCommit -ne $head) { throw 'Reservation must use the exact checked-out source commit.' }
if ([string]::IsNullOrWhiteSpace($BuildId)) { $BuildId = "local-$($head.Substring(0, 12))" }
if ($BuildId -notmatch '^[A-Za-z0-9._-]+$') { throw 'Reservation BuildId must be a single safe identifier.' }
$dirty = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'Prerelease reservation requires a clean source tree.' }
$suffix = $Channel.ToLowerInvariant()
$released = @(& git -C $root ls-remote --tags --refs $Remote "refs/tags/v$base")
if ($LASTEXITCODE -ne 0) { throw 'Cannot check released target versions.' }
if ($released.Count -ne 0) { throw 'Target version is already released. Select the next release scope before allocating prereleases.' }
$prefix = "v$base-$suffix."
# Remote ref creation is the durable atomic reservation. Never force, delete or reuse it.
# Concurrent allocators may race; a rejected creation fails closed and must be retried as a new run.
$refs = @(& git -C $root ls-remote --tags --refs $Remote "refs/tags/$prefix*")
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the durable remote version registry.' }
$highest = [long]0
foreach ($entry in $refs) {
    $name = ($entry -split '\s+')[-1]
    $match = [regex]::Match($name, '^refs/tags/' + [regex]::Escape($prefix) + '([1-9][0-9]*)$')
    if ($match.Success) { $highest = [Math]::Max($highest, [long]::Parse($match.Groups[1].Value)) }
}
if ($highest -eq [long]::MaxValue) { throw 'Prerelease sequence exhausted.' }
$version = "$base-$suffix.$($highest + 1)"
$tag = "v$version"
$reservationId = [Guid]::NewGuid().ToString('N')
& git -C $root -c user.name='rtaime release' -c user.email='release@rtaime.local' tag -a $tag $SourceCommit -m "Reserve $version ($reservationId)" -m "BuildId: $BuildId"
if ($LASTEXITCODE -ne 0) { throw "Version/tag collision: $tag" }
& git -C $root push $Remote "refs/tags/${tag}:refs/tags/$tag"
if ($LASTEXITCODE -ne 0) { throw "Atomic version reservation rejected for $tag. Do not overwrite or reuse it." }
$result = [ordered]@{ productVersion = $version; channel = $Channel; tag = $tag; sourceCommit = $SourceCommit }
$path = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $root $OutputPath }
[IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
[IO.File]::WriteAllText($path, ($result | ConvertTo-Json) + "`n", [Text.UTF8Encoding]::new($false))
if ($env:GITHUB_OUTPUT) { "tag=$tag" | Out-File $env:GITHUB_OUTPUT -Encoding utf8 -Append }
return $result
