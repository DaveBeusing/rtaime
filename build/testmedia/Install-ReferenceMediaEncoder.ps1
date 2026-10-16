[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

# Test-only encoder. Hash independently published by Microsoft WinGet:
# https://github.com/microsoft/winget-pkgs/blob/master/manifests/g/Gyan/FFmpeg/7.1.1/Gyan.FFmpeg.installer.yaml
$version = '7.1.1'
$expectedHash = 'D760E1B3574402ED18B4865851F87D87E73965A982E6453212DF8621FED1C508'
$url = 'https://github.com/GyanD/codexffmpeg/releases/download/7.1.1/ffmpeg-7.1.1-full_build.zip'
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$destination = Join-Path $temporaryRoot ('rtaime-reference-encoder-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $destination | Out-Null
$archive = Join-Path $destination 'ffmpeg.zip'
Invoke-WebRequest -Uri $url -OutFile $archive
if ((Get-Item -LiteralPath $archive).Length -gt 200MB) { throw 'Reference encoder archive exceeds the 200 MiB limit.' }
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw "Reference encoder SHA256 mismatch: $actualHash." }
Expand-Archive -LiteralPath $archive -DestinationPath $destination
$bin = Join-Path $destination 'ffmpeg-7.1.1-full_build/bin'
foreach ($name in @('ffmpeg', 'ffprobe')) {
    $executable = Join-Path $bin ($name + '.exe')
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing reference tool: $name." }
    $output = & $executable -version
    if ($LASTEXITCODE -ne 0 -or $output[0] -notmatch ("^" + $name + " version 7\.1\.1(?:-|\s)")) {
        throw "Unexpected reference tool version: $($output[0])."
    }
    Write-Host $output[0]
}
Write-Host "Reference encoder $version archive SHA256: $actualHash"
$env:PATH = $bin + [IO.Path]::PathSeparator + $env:PATH
if ($env:GITHUB_PATH) { Add-Content -LiteralPath $env:GITHUB_PATH -Value $bin -Encoding utf8 }
