$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Clear-1CUserCache.cmd'))
$payload = $source.Substring($source.IndexOf('# POWERSHELL_PAYLOAD') + '# POWERSHELL_PAYLOAD'.Length)
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($payload, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors -join "`n") }
. ([scriptblock]::Create(($payload -split '# MAIN_ENTRY:')[0]))

function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $thrown = $false
    try { & $Action } catch { $thrown = $true }
    Assert $thrown $Message
}

$testTemp = [IO.Path]::GetTempPath()
if (Test-Path -LiteralPath '/private/tmp') { $testTemp = '/private/tmp' }
$sandbox = Join-Path $testTemp ('onec-cache-test-' + [guid]::NewGuid())
try {
    # Resolve OS temp aliases before testing the deliberate reparse-point guard.
    [void][IO.Directory]::CreateDirectory($sandbox)
    $roots = @()
    foreach ($branch in @('Local', 'Roaming')) {
        foreach ($version in @('1Cv8', '1Cv82')) {
            $root = Join-Path $sandbox "$branch/1C/$version"
            $roots += $root
            $cache = Join-Path $root ([guid]::NewGuid().ToString())
            [void][IO.Directory]::CreateDirectory((Join-Path $cache 'Config/nested'))
            [IO.File]::WriteAllText((Join-Path $cache 'Config/nested/cache.bin'), 'cache')
            $vrs = Join-Path $cache (([guid]::NewGuid().ToString()) + '/vrs-cache')
            [void][IO.Directory]::CreateDirectory($vrs)
            [IO.File]::WriteAllText((Join-Path $vrs 'cache.1CD'), 'cache, not an information base')
            foreach ($keep in @('1CEStart', 'licensing', 'tmplts', 'not-a-guid')) {
                [void][IO.Directory]::CreateDirectory((Join-Path $root $keep))
                [IO.File]::WriteAllText((Join-Path $root "$keep/keep.txt"), 'preserve')
            }
        }
    }
    $candidates = @(Get-CacheCandidates $roots)
    Assert ($candidates.Count -eq 4) 'Expected caches in both branches and platform folders.'
    foreach ($cache in $candidates) { Assert-CleanableTree $cache; Remove-CacheTree $cache }
    Assert (@(Get-CacheCandidates $roots).Count -eq 0) 'Cache was not removed.'
    foreach ($root in $roots) {
        foreach ($keep in @('1CEStart', 'licensing', 'tmplts', 'not-a-guid')) {
            Assert (Test-Path -LiteralPath (Join-Path $root "$keep/keep.txt")) "Protected folder lost: $keep"
        }
    }
    foreach ($protected in @('licensing/license.txt', '1CEStart/ibases.v8i', 'data.1CD', 'license.lic', 'cache.1CD', 'Config/cache.1CD', 'vrs-cache/1Cv8.1CD', 'vrs-cache/license.lic')) {
        $cache = Join-Path $roots[0] ([guid]::NewGuid().ToString())
        $file = Join-Path $cache $protected
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
        [IO.File]::WriteAllText($file, 'preserve')
        Assert-Throws { Assert-CleanableTree $cache } "Did not reject protected object: $protected"
        Assert-Throws { Remove-CacheTree $cache } "Deletion accepted protected object: $protected"
        Assert (Test-Path -LiteralPath $file) 'Protected object lost.'
    }
    $outside = Join-Path $sandbox 'outside'
    [void][IO.Directory]::CreateDirectory($outside)
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'preserve')
    $linkCache = Join-Path $roots[0] ([guid]::NewGuid().ToString())
    [void][IO.Directory]::CreateDirectory($linkCache)
    $link = Join-Path $linkCache 'linked'
    if ($env:OS -eq 'Windows_NT') {
        [void](New-Item -ItemType Junction -Path $link -Target $outside)
    } else {
        [void](New-Item -ItemType SymbolicLink -Path $link -Target $outside)
    }
    Assert-Throws { Assert-CleanableTree $linkCache } 'Did not reject a nested link.'
    Assert-Throws { Remove-CacheTree $link } 'Deletion accepted a link.'
    Assert (Test-Path -LiteralPath (Join-Path $outside 'keep.txt')) 'Link target lost.'
    Remove-Item -LiteralPath $link -Force
    Write-Host 'PASS: syntax, four roots, GUID selection, vrs-cache/cache.1CD deletion, exclusions, license/database protection, links.'
} finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
}
