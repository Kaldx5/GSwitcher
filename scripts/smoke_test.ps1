$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$PackagedExe = Join-Path $RootDir "GSwitcher.exe"
$RepoExe = Join-Path $RootDir "dist\GSwitcher\GSwitcher.exe"
$ExePath = if (Test-Path $PackagedExe) { $PackagedExe } else { $RepoExe }
$SmokeTimeoutSeconds = 45
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "GSwitcher"
$PersistentWebView2 = Join-Path $env:LOCALAPPDATA "GSwitcher\WebView2"

if (-not (Test-Path $ExePath)) {
    throw "Smoke test target not found: $ExePath"
}

Get-ChildItem -LiteralPath (Split-Path -Parent $ExePath) -Recurse -Force -Filter "desktop.ini" -ErrorAction SilentlyContinue |
    Remove-Item -Force

Write-Host "Running startup smoke tests..." -ForegroundColor Cyan

for ($i = 1; $i -le 3; $i++) {
    Write-Host ("  Pass {0}/3" -f $i) -ForegroundColor DarkCyan

    $passStartUtc = [DateTime]::UtcNow
    $existingRuntimeRoots = @(Get-ChildItem -LiteralPath $TempRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $env:__COMPAT_LAYER = "RunAsInvoker"
    $process = Start-Process -FilePath $ExePath -ArgumentList "--smoke-test" -WindowStyle Hidden -PassThru

    $timedOut = $false
    try {
        Wait-Process -Id $process.Id -Timeout $SmokeTimeoutSeconds -ErrorAction Stop
    }
    catch [System.TimeoutException] {
        $timedOut = $true
    }
    catch {
        throw
    }

    $process.Refresh()
    if ($timedOut -and -not $process.HasExited) {
        try {
            Stop-Process -Id $process.Id -Force
        }
        catch {
        }

        throw "Smoke test pass $i did not exit cleanly within ${SmokeTimeoutSeconds}s."
    }

    if (-not $process.HasExited) {
        $process.WaitForExit()
    }

    if ($process.ExitCode -ne 0) {
        throw "Smoke test pass $i exited with code $($process.ExitCode)."
    }

    if (Test-Path $PersistentWebView2) {
        throw "Smoke test pass $i found persistent WebView2 data at $PersistentWebView2."
    }

    $recentRuntimeRoots = @()
    if (Test-Path $TempRoot) {
        $recentRuntimeRoots = Get-ChildItem -Path $TempRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $existingRuntimeRoots -notcontains $_.FullName }
    }

    if ($recentRuntimeRoots.Count -gt 0) {
        $names = ($recentRuntimeRoots | Select-Object -ExpandProperty FullName) -join ", "
        throw "Smoke test pass $i left volatile runtime data behind: $names"
    }
}

Write-Host "Smoke tests passed." -ForegroundColor Green

Get-ChildItem -LiteralPath (Split-Path -Parent $ExePath) -Recurse -Force -Filter "desktop.ini" -ErrorAction SilentlyContinue |
    Remove-Item -Force
