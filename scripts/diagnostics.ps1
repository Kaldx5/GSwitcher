$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$PackagedExe = Join-Path $RootDir "GSwitcher.exe"
$RepoExe = Join-Path $RootDir "dist\GSwitcher\GSwitcher.exe"
$ExePath = if (Test-Path $PackagedExe) { $PackagedExe } else { $RepoExe }

if (-not (Test-Path $ExePath)) {
    throw "Diagnostics target not found: $ExePath"
}

Write-Host "Running GSwitcher diagnostics..." -ForegroundColor Cyan

Write-Host ""
Write-Host "Power schemes before:" -ForegroundColor DarkCyan
powercfg /L | Out-Host
powercfg /getactivescheme | Out-Host

Write-Host ""
Write-Host "Launching diagnostics mode (as-invoker)..." -ForegroundColor DarkCyan
$env:__COMPAT_LAYER = "RunAsInvoker"
$p = Start-Process -FilePath $ExePath -ArgumentList "--diagnostics" -PassThru
$p.WaitForExit()

Write-Host ""
Write-Host ("Diagnostics exit code: {0}" -f $p.ExitCode) -ForegroundColor DarkCyan

Write-Host ""
Write-Host "Power schemes after:" -ForegroundColor DarkCyan
powercfg /L | Out-Host
powercfg /getactivescheme | Out-Host

Write-Host ""
Write-Host "Diagnostics finished. Runtime logs and evidence are volatile unless exported from the app." -ForegroundColor Green
