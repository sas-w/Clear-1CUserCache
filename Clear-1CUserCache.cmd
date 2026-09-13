@echo off
setlocal DisableDelayedExpansion
set "ONEC_CLEAN_SELF=%~f0"
set "ONEC_CLEAN_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "ONEC_CLEAN_PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%ONEC_CLEAN_PS%" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText($env:ONEC_CLEAN_SELF,[Text.Encoding]::UTF8); $m='#'+' POWERSHELL_PAYLOAD'; & ([scriptblock]::Create($s.Substring($s.IndexOf($m)+$m.Length)))"
set "ONEC_CLEAN_EXIT=%ERRORLEVEL%"
if not "%ONEC_CLEAN_EXIT%"=="0" pause
exit /b %ONEC_CLEAN_EXIT%
# POWERSHELL_PAYLOAD
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-CacheRoots {
    foreach ($kind in @('LocalApplicationData', 'ApplicationData')) {
        $base = [Environment]::GetFolderPath($kind)
        if ([string]::IsNullOrWhiteSpace($base)) { throw "Не удалось определить папку $kind." }
        foreach ($version in @('1Cv8', '1Cv82')) {
            Join-Path (Join-Path $base '1C') $version
        }
    }
}

function Assert-NoLinkInPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Ссылка или junction: $cursor. Каталог пропущен."
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Get-CacheCandidates([string[]]$Roots) {
    foreach ($root in ($Roots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Assert-NoLinkInPath $root
        foreach ($dir in (Get-ChildItem -LiteralPath $root -Directory -Force)) {
            if ($dir.Name -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
                $dir.FullName
            }
        }
    }
}

function Test-ProtectedCacheItem($Item) {
    if ($Item.Name -in @('1CEStart', 'licensing')) { return $true }
    if ($Item.PSIsContainer) { return $false }
    if ($Item.Extension -eq '.lic') { return $true }
    if ($Item.Extension -eq '.1cd') {
        # The caller restricts traversal to GUID cache directories.
        # vrs-cache uses the 1CD format too; it is not an information base.
        return -not ($Item.Name -eq 'cache.1cd' -and $Item.Directory.Name -eq 'vrs-cache')
    }
    return $false
}

function Assert-CleanableTree([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Test-ProtectedCacheItem $item)) {
        throw "Защищённый объект или ссылка: $Path. Каталог кэша пропущен."
    }
    if ($item.PSIsContainer) {
        foreach ($child in (Get-ChildItem -LiteralPath $Path -Force)) {
            Assert-CleanableTree $child.FullName
        }
    }
}

function Remove-CacheTree([string]$Path) {
    # Recheck each entry; never use recursive deletion that could follow a junction.
    Assert-NoLinkInPath $Path
    $item = Get-Item -LiteralPath $Path -Force
    if (Test-ProtectedCacheItem $item) {
        throw "Защищённый объект: $Path"
    }
    if ($item.PSIsContainer) {
        foreach ($child in (Get-ChildItem -LiteralPath $Path -Force)) {
            Remove-CacheTree $child.FullName
        }
        # Nonrecursive deletion fails if new files appeared in the meantime.
        [IO.Directory]::Delete($Path, $false)
    } else {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
}

function Get-Blocking1CProcesses {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $filter = "Name='1cv8.exe' OR Name='1cv8c.exe' OR Name='1cv8s.exe' OR Name='1cestart.exe' OR Name='1cv8t.exe'"
    foreach ($proc in @(Get-CimInstance Win32_Process -Filter $filter -ErrorAction Stop)) {
        try {
            $owner = Invoke-CimMethod -InputObject $proc -MethodName GetOwnerSid -ErrorAction Stop
            if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrEmpty($owner.Sid)) {
                throw 'Не удалось определить владельца процесса.'
            }
            if ($owner.Sid -eq $sid) {
                "$($proc.Name), PID $($proc.ProcessId), сеанс $($proc.SessionId)"
            }
        } catch {
            # Exited processes are harmless. An inaccessible live process is not.
            $stillRunning = @(Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.ProcessId)" -ErrorAction Stop)
            if ($stillRunning.Count -gt 0) {
                "$($proc.Name), PID $($proc.ProcessId): владелец недоступен для проверки"
            }
        }
    }
}

function Wait-ForClosed1C {
    while ($true) {
        $blocking = @(Get-Blocking1CProcesses)
        if ($blocking.Count -eq 0) { return $true }
        $message = "Перед очисткой закройте 1С:Предприятие, Конфигуратор и окно запуска 1С.`r`nПроверьте также другие сеансы Windows этой учётной записи.`r`n`r`n" + ($blocking -join "`r`n") + "`r`n`r`nПосле закрытия нажмите «Повтор». Процессы автоматически не завершаются."
        $answer = [Windows.Forms.MessageBox]::Show($message, 'Очистка кэша 1С', 'RetryCancel', 'Warning')
        if ($answer -eq 'Cancel') { return $false }
    }
}

function Invoke-CacheCleanup {
    Add-Type -AssemblyName System.Windows.Forms
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    Write-Host "Очистка кэша 1С — $($identity.Name)"
    if (-not (Wait-ForClosed1C)) { return 0 }
    $roots = @(Get-CacheRoots)
    $candidates = @(Get-CacheCandidates $roots)
    if ($candidates.Count -eq 0) {
        [void][Windows.Forms.MessageBox]::Show('Каталоги кэша с GUID-именами в Local и Roaming не найдены.', 'Очистка кэша 1С', 'OK', 'Information')
        return 0
    }
    Write-Host ($candidates -join "`r`n")
    $question = "Пользователь: $($identity.Name)`r`nНайдено каталогов кэша: $($candidates.Count).`r`n`r`n" + ($roots -join "`r`n") + "`r`n`r`nБудут удалены только вложенные каталоги с GUID-именами.`r`n1CEStart и licensing сохраняются.`r`nНе открывайте 1С до завершения очистки.`r`nПервый запуск после очистки может занять больше времени.`r`n`r`nОчистить кэш?"
    if ([Windows.Forms.MessageBox]::Show($question, 'Очистка кэша 1С', 'YesNo', 'Question', 'Button2') -ne 'Yes') { return 0 }
    if (-not (Wait-ForClosed1C)) { return 0 }
    $deleted = 0
    $errors = New-Object 'System.Collections.Generic.List[string]'
    foreach ($path in $candidates) {
        # Detect a client started after confirmation, before each cache directory.
        if (@(Get-Blocking1CProcesses).Count -gt 0) {
            $errors.Add('Во время очистки появилась 1С или процесс с непроверенным владельцем. Дальнейшая очистка остановлена.')
            break
        }
        try {
            Assert-NoLinkInPath $path
            Assert-CleanableTree $path
            Remove-CacheTree $path
            $deleted++
            Write-Host "Удалено: $path"
        } catch {
            $detail = "$path — $($_.Exception.Message)"
            $errors.Add($detail)
            Write-Host $detail -ForegroundColor Yellow
        }
    }
    $summary = "Полностью удалено каталогов: $deleted из $($candidates.Count)."
    if ($errors.Count -gt 0) {
        $summary += "`r`nОчистка выполнена не полностью. Некоторые каталоги могли очиститься частично.`r`n`r`n" + ($errors -join "`r`n")
        [void][Windows.Forms.MessageBox]::Show($summary, 'Очистка кэша 1С', 'OK', 'Warning')
        return 2
    }
    [void][Windows.Forms.MessageBox]::Show("$summary`r`nМожно запускать 1С.", 'Очистка завершена', 'OK', 'Information')
    return 0
}

# MAIN_ENTRY: tests load functions without invoking the Windows UI.
try {
    exit (Invoke-CacheCleanup)
} catch {
    $message = "Очистка остановлена: $($_.Exception.Message)"
    Write-Host $message -ForegroundColor Red
    try { [void][Windows.Forms.MessageBox]::Show($message, 'Ошибка очистки кэша 1С', 'OK', 'Error') } catch {}
    exit 1
}
