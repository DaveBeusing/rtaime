# Copyright (c) Dave Beusing <david.beusing@gmail.com>.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('rtaime-versioning-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
function Invoke-TestGit($directory, [string[]]$arguments) {
    $result = & git -C $directory @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git failed: $arguments : $result" }
    return $result
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Reject([scriptblock]$action, $expected) {
    try { & $action | Out-Null } catch {
        Assert ($_.Exception.Message -match $expected) "Unexpected rejection: $_"
        return
    }
    throw "Expected rejection: $expected"
}
$remote = Join-Path $fixture 'registry.git'
$source = Join-Path $fixture 'source'
Invoke-TestGit $fixture @('init', '--bare', $remote) | Out-Null
Invoke-TestGit $remote @('symbolic-ref', 'HEAD', 'refs/heads/master') | Out-Null
Invoke-TestGit $fixture @('init', '--initial-branch=master', $source) | Out-Null
Invoke-TestGit $source @('config', 'user.name', 'Version test') | Out-Null
Invoke-TestGit $source @('config', 'user.email', 'version-test@rtaime.local') | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $source 'build/release')) | Out-Null
foreach ($file in @('Reserve-PrereleaseVersion.ps1', 'Resolve-ReleaseIdentity.ps1', 'release-channels.json')) {
    Copy-Item (Join-Path $PSScriptRoot $file) (Join-Path $source "build/release/$file")
}
Copy-Item (Join-Path $root 'Directory.Build.props') (Join-Path $source 'Directory.Build.props')
# The reservation fixture uses its own declared target, independent of the product's current planned version.
[xml]$fixtureProps = Get-Content -LiteralPath (Join-Path $source 'Directory.Build.props') -Raw
$declaredPrefix = $fixtureProps.SelectSingleNode('//VersionPrefix')
Assert ($null -ne $declaredPrefix -and $declaredPrefix.InnerText -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') 'The current planned release target must be valid SemVer.'
$declaredPrefix.InnerText = '0.1.0'
$fixtureProps.Save((Join-Path $source 'Directory.Build.props'))
[IO.File]::WriteAllText((Join-Path $source '.gitignore'), "artifacts/`n")
Invoke-TestGit $source @('add', '.') | Out-Null
Invoke-TestGit $source @('commit', '-m', 'Define release target') | Out-Null
Invoke-TestGit $source @('remote', 'add', 'origin', $remote) | Out-Null
Invoke-TestGit $source @('push', 'origin', 'master') | Out-Null
$reserve = Join-Path $source 'build/release/Reserve-PrereleaseVersion.ps1'
$resolve = Join-Path $source 'build/release/Resolve-ReleaseIdentity.ps1'
$sha = (Invoke-TestGit $source @('rev-parse', 'HEAD')).Trim()
$first = & $reserve -Channel PREVIEW
$second = & $reserve -Channel PREVIEW
$rc = & $reserve -Channel RC
Assert ($first.productVersion -eq '0.1.0-preview.1') 'Preview must start at 1.'
Assert ($second.productVersion -eq '0.1.0-preview.2') 'Same-source rebuild must reserve a new version.'
Assert ($rc.productVersion -eq '0.1.0-rc.1') 'RC uses an independent sequence.'
Assert ((Invoke-TestGit $source @('cat-file', '-t', "refs/tags/$($first.tag)")).Trim() -eq 'tag') 'Reservation must be annotated.'
$identity = & $resolve -Channel PREVIEW -Tag $first.tag
Assert ($identity.productVersion -eq $first.productVersion -and $identity.sourceCommit -eq $sha) 'Resolved preview identity mismatch.'
Reject { & $resolve -Channel PREVIEW -Tag $first.tag -BuildId 'another-build' } 'belongs to another build'
$candidate = & $resolve -Channel RC -Tag $rc.tag
Assert ($candidate.releaseStage -eq 'RELEASE_CANDIDATE') 'RC stage mapping mismatch.'
$savedPr = $env:RTAIME_PR_NUMBER
$savedSequence = $env:RTAIME_VALIDATION_SEQUENCE
try {
    $env:RTAIME_PR_NUMBER = '123'; $env:RTAIME_VALIDATION_SEQUENCE = '42.1'
    $pr = & $resolve -Channel QUALIFICATION
    Assert ($pr.productVersion -eq '0.1.0-pr.123.42.1' -and -not $pr.signingPolicy.publicationEligible) 'PR builds must be isolated from publication.'
} finally { $env:RTAIME_PR_NUMBER = $savedPr; $env:RTAIME_VALIDATION_SEQUENCE = $savedSequence }
Invoke-TestGit $source @('tag', 'v0.1.0-preview.99') | Out-Null
Reject { & $resolve -Channel PREVIEW -Tag 'v0.1.0-preview.99' } 'annotated'
Invoke-TestGit $source @('tag', '-a', 'v0.1.0-preview.0', '-m', 'Invalid number') | Out-Null
Reject { & $resolve -Channel PREVIEW -Tag 'v0.1.0-preview.0' } 'invalid|pattern'
Invoke-TestGit $source @('tag', '-a', 'v0.2.0-preview.1', '-m', 'Wrong base') | Out-Null
Reject { & $resolve -Channel PREVIEW -Tag 'v0.2.0-preview.1' } 'target VersionPrefix'
Invoke-TestGit $source @('tag', '-d', 'v0.2.0-preview.1') | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'untracked.txt'), 'dirty')
Reject { & $reserve -Channel PREVIEW } 'clean source tree'
Reject { & $resolve -Channel PREVIEW -Tag $first.tag } 'clean source tree'
Remove-Item -LiteralPath (Join-Path $source 'untracked.txt')
# Two allocators compete against the same durable registry.
$raceProcesses = @()
foreach ($index in 1..2) {
    $race = Join-Path $fixture "race$index"
    Invoke-TestGit $fixture @('clone', $remote, $race) | Out-Null
    $raceScript = Join-Path $race 'build/release/Reserve-PrereleaseVersion.ps1'
    $raceProcesses += Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile', '-File', ('"' + $raceScript + '"'), '-Channel', 'PREVIEW') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $fixture "race$index.out") -RedirectStandardError (Join-Path $fixture "race$index.err")
}
foreach ($process in $raceProcesses) { $process.WaitForExit() }
$successful = @($raceProcesses | Where-Object ExitCode -eq 0).Count
Assert ($successful -ge 1) 'At least one parallel allocator must succeed.'
$previewRefs = @(Invoke-TestGit $source @('ls-remote', '--tags', '--refs', 'origin', 'refs/tags/v0.1.0-preview.*'))
Assert ($previewRefs.Count -eq 2 + $successful) 'Parallel builds must produce unique reservations without overwrites.'
# An existing stable target closes the allocation cycle.
Invoke-TestGit $source @('tag', '-a', 'v0.1.0', '-m', 'Stable fixture') | Out-Null
Invoke-TestGit $source @('push', 'origin', 'refs/tags/v0.1.0') | Out-Null
$stable = & $resolve -Channel STABLE -Tag 'v0.1.0'
Assert ($stable.productVersion -eq '0.1.0' -and $stable.signingPolicy.requireTrustedProductionKey) 'Stable tag must resolve exactly and retain production trust.'
Reject { & $reserve } 'already released'
$propsPath = Join-Path $source 'Directory.Build.props'
[IO.File]::WriteAllText($propsPath, ([IO.File]::ReadAllText($propsPath).Replace('0.1.0', '0.2.0')))
Invoke-TestGit $source @('add', 'Directory.Build.props') | Out-Null
Invoke-TestGit $source @('commit', '-m', 'Select next target') | Out-Null
Invoke-TestGit $source @('push', 'origin', 'master') | Out-Null
$reset = & $reserve
Assert ($reset.productVersion -eq '0.2.0-preview.1') 'A new base version must restart its sequence at 1.'
Write-Host 'Unified versioning qualification PASS: sequence/reset, parallel allocation, PR isolation, tag/cleanliness validation, immutable stable target.'
