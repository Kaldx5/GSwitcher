$ErrorActionPreference = "Stop"

$RootDir = $PSScriptRoot
$ProjectDir = Join-Path $RootDir "source\GSwitcher.Desktop"
$ProjectFile = Join-Path $ProjectDir "GSwitcher.Desktop.csproj"
$VendorDir = Join-Path $RootDir "vendor\packages"
$OutputDir = Join-Path $RootDir "artifacts\bin\Release"
$DistDir = Join-Path $RootDir "dist\GSwitcher"
$DistExe = Join-Path $DistDir "GSwitcher.exe"
$ExistingSettingsPath = Join-Path $DistDir "settings.json"
$ExistingSettings = if (Test-Path -LiteralPath $ExistingSettingsPath) {
    [System.IO.File]::ReadAllBytes($ExistingSettingsPath)
} else { $null }
$MSBuild = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"

if (-not (Test-Path $ProjectFile)) {
    throw "Project file not found: $ProjectFile"
}

if (-not (Test-Path $MSBuild)) {
    throw "MSBuild was not found at $MSBuild"
}

if (-not (Test-Path $VendorDir)) {
    throw "Vendor packages folder was not found at $VendorDir"
}

if (Test-Path $DistExe) {
    $runningPackagedApp = Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $_.Path -eq $DistExe
            }
            catch {
                $false
            }
        }

    if ($runningPackagedApp) {
        $runningPackagedApp | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

Write-Host "Building clean portable GSwitcher..." -ForegroundColor Cyan
& $MSBuild $ProjectFile /t:Rebuild /p:Configuration=Release /p:Platform=x64 /verbosity:minimal

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild reported a failure."
}

if (Test-Path $DistDir) {
    Remove-Item -LiteralPath $DistDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
if ($null -ne $ExistingSettings) {
    [System.IO.File]::WriteAllBytes($ExistingSettingsPath, $ExistingSettings)
}

foreach ($fileName in @(
    "GSwitcher.exe",
    "GSwitcher.exe.config",
    "Microsoft.Web.WebView2.Core.dll",
    "Microsoft.Web.WebView2.Wpf.dll",
    "NvAPIWrapper.dll"
)) {
    $sourcePath = Join-Path $OutputDir $fileName
    if (-not (Test-Path $sourcePath)) {
        throw "Expected build output missing: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination $DistDir -Force
}

$wv2Loader = Join-Path $VendorDir "Microsoft.Web.WebView2.1.0.2420.47\runtimes\win-x64\native\WebView2Loader.dll"
Copy-Item -LiteralPath $wv2Loader -Destination $DistDir -Force

$uiSource = Join-Path $OutputDir "ui"
if (-not (Test-Path $uiSource)) {
    throw "UI output was not found: $uiSource"
}
Copy-Item -LiteralPath $uiSource -Destination (Join-Path $DistDir "ui") -Recurse -Force

$distScripts = Join-Path $DistDir "scripts"
New-Item -ItemType Directory -Force -Path $distScripts | Out-Null
foreach ($scriptName in @("diagnostics.ps1", "smoke_test.ps1", "test_gpu_mux_detection.ps1", "PowerSwitcher.reference.ps1", "GSwitcher_Power_Menu.ps1")) {
    $scriptPath = Join-Path (Join-Path $RootDir "scripts") $scriptName
    if (Test-Path $scriptPath) {
        Copy-Item -LiteralPath $scriptPath -Destination $distScripts -Force
    }
}

Get-ChildItem -LiteralPath $DistDir -Recurse -Force -Filter "desktop.ini" -ErrorAction SilentlyContinue |
    Remove-Item -Force

$topLevelDesktopIni = Join-Path $DistDir "desktop.ini"
if (Test-Path $topLevelDesktopIni) {
    Remove-Item -LiteralPath $topLevelDesktopIni -Force
}

$allowedTopLevel = @(
    "GSwitcher.exe",
    "GSwitcher.exe.config",
    "NvAPIWrapper.dll",
    "WebView2Loader.dll",
    "Microsoft.Web.WebView2.Core.dll",
    "Microsoft.Web.WebView2.Wpf.dll",
    "settings.json",
    "ui",
    "scripts"
)

$unexpected = Get-ChildItem -LiteralPath $DistDir -Force |
    Where-Object { $allowedTopLevel -notcontains $_.Name }

if ($unexpected) {
    $names = ($unexpected | Select-Object -ExpandProperty Name) -join ", "
    throw "Unexpected files in portable output: $names"
}

$blockedFiles = Get-ChildItem -LiteralPath $DistDir -Recurse -Force -File |
    Where-Object { $_.Name -match '\.(pdb|xml)$|^desktop\.ini$' }

if ($blockedFiles) {
    $names = ($blockedFiles | Select-Object -ExpandProperty FullName) -join ", "
    throw "Blocked files in portable output: $names"
}

Write-Host ""
Write-Host "Clean portable output ready:" -ForegroundColor Green
Write-Host "  $DistDir" -ForegroundColor Green
Write-Host ""
Write-Host "Run:" -ForegroundColor Yellow
Write-Host "  $DistExe" -ForegroundColor Yellow
