$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../onec-cache-cleaner'
Add-Type -Path (Join-Path $source 'CacheEngine.cs'), (Join-Path $source 'EngineTests.cs')
$temp = [IO.Path]::GetTempPath()
if (Test-Path /private/tmp) { $temp = '/private/tmp' }
[EngineTests]::Run($temp)

# Real filesystem link: never traverse into its target.
$sandbox = Join-Path $temp ('onec-exe-link-' + [guid]::NewGuid())
$root = Join-Path $sandbox 'Local/1C/1Cv8'
$cache = Join-Path $root ([guid]::NewGuid().ToString())
$outside = Join-Path $sandbox 'outside'
$link = Join-Path $cache 'link'
try {
    [void][IO.Directory]::CreateDirectory($cache)
    [void][IO.Directory]::CreateDirectory($outside)
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'preserve')
    $kind = if ($env:OS -eq 'Windows_NT') { 'Junction' } else { 'SymbolicLink' }
    [void](New-Item -ItemType $kind -Path $link -Target $outside)
    $result = [OneCCacheCleaner.CacheEngine]::Clean([string[]]@($root), [string[]]@($cache),
        [Func[System.Collections.Generic.List[string]]]{ return ,([System.Collections.Generic.List[string]]::new()) },
        [Action[string]]{ param($s) }, [Action[int]]{ param($n) }, [Threading.CancellationToken]::None)
    if ($result.Failed -ne 1 -or -not (Test-Path (Join-Path $outside 'keep.txt'))) { throw 'Link protection failed' }
    Write-Host 'PASS: link/junction target preserved.'
} finally {
    if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
}
