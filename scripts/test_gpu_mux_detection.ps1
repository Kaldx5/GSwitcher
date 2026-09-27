$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$PackagedExe = Join-Path $RootDir "GSwitcher.exe"
$RepoExe = Join-Path $RootDir "dist\GSwitcher\GSwitcher.exe"
$ExePath = if (Test-Path $PackagedExe) { $PackagedExe } else { $RepoExe }
$DistDir = Split-Path -Parent $ExePath
$NvApiPath = Join-Path $DistDir "NvAPIWrapper.dll"

if (-not (Test-Path $ExePath)) {
    throw "Test target not found: $ExePath"
}

$awccClass = Get-CimClass -Namespace root\wmi -ClassName AWCCWmiMethodFunction -ErrorAction SilentlyContinue
if (-not $awccClass) {
    Write-Host "AWCC WMI class not present on this machine. Skipping." -ForegroundColor Yellow
    exit 0
}

$muxMethod = $awccClass.CimClassMethods["Set_OCUIBIOSControl"]
if (-not $muxMethod) {
    $muxMethod = $awccClass.CimClassMethods["MUXSwitch"]
}
if (-not $muxMethod) {
    Write-Host "Known AWCC MUX method not present on this machine. Skipping." -ForegroundColor Yellow
    exit 0
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    Write-Host "Session is elevated. This regression check is only for the blocked native MUX path." -ForegroundColor Yellow
    exit 0
}

$resolveHandler = [System.ResolveEventHandler]{
    param($sender, $args)

    $assemblyName = ([System.Reflection.AssemblyName]$args.Name).Name + ".dll"
    $candidatePath = Join-Path $DistDir $assemblyName
    if (Test-Path $candidatePath) {
        return [Reflection.Assembly]::LoadFrom($candidatePath)
    }

    return $null
}

[AppDomain]::CurrentDomain.add_AssemblyResolve($resolveHandler)

if (Test-Path $NvApiPath) {
    [Reflection.Assembly]::LoadFrom($NvApiPath) | Out-Null
}

[Reflection.Assembly]::LoadFrom($ExePath) | Out-Null

$gpuManager = [AppDomain]::CurrentDomain.GetAssemblies() |
    Where-Object { $_.GetName().Name -eq "GSwitcher" } |
    Select-Object -First 1

if (-not $gpuManager) {
    throw "Could not load GSwitcher assembly."
}

$gpuManagerType = $gpuManager.GetType("GSwitcher.Backend.GpuManager", $true)
$gpuManagerType.GetMethod("RefreshController").Invoke($null, @()) | Out-Null
$controlPath = $gpuManagerType.GetProperty("CurrentControlPath").GetValue($null)
$routeControlPath = $gpuManagerType.GetProperty("CurrentRouteControlPath").GetValue($null)
$preferenceControlPath = $gpuManagerType.GetProperty("CurrentPreferenceControlPath").GetValue($null)
$availableModes = [string]::Join(",", $gpuManagerType.GetMethod("GetAvailableModes").Invoke($null, @()))
$availableRouteModes = [string]::Join(",", $gpuManagerType.GetMethod("GetAvailableRouteModes").Invoke($null, @()))
$availablePreferenceModes = [string]::Join(",", $gpuManagerType.GetMethod("GetAvailablePreferenceModes").Invoke($null, @()))
$routeSnapshot = $gpuManagerType.GetMethod("DetectCurrentRouteSnapshot").Invoke($null, @())
$routeMode = $routeSnapshot.GetType().GetProperty("Mode").GetValue($routeSnapshot)
$routeSource = $routeSnapshot.GetType().GetProperty("Source").GetValue($routeSnapshot)
$routeEvidence = $routeSnapshot.GetType().GetProperty("Evidence").GetValue($routeSnapshot)

Write-Host ("Detected control path: {0}" -f $controlPath) -ForegroundColor DarkCyan
Write-Host ("Route control path: {0}" -f $routeControlPath) -ForegroundColor DarkCyan
Write-Host ("Preference control path: {0}" -f $preferenceControlPath) -ForegroundColor DarkCyan
Write-Host ("Available GPU modes: {0}" -f $availableModes) -ForegroundColor DarkCyan
Write-Host ("Available route modes: {0}" -f $availableRouteModes) -ForegroundColor DarkCyan
Write-Host ("Available preference modes: {0}" -f $availablePreferenceModes) -ForegroundColor DarkCyan
Write-Host ("Detected current route: {0}" -f $routeMode) -ForegroundColor DarkCyan
Write-Host ("Route source: {0}" -f $routeSource) -ForegroundColor DarkCyan
Write-Host ("Route evidence: {0}" -f $routeEvidence) -ForegroundColor DarkCyan

if ($controlPath -ne "awcc-mux-locked" -and $controlPath -ne "nvidia-panel") {
    throw "Expected a truthful route controller when AWCC MUX exists in a non-elevated session. Actual: $controlPath"
}

if ($routeControlPath -ne "awcc-mux-locked" -and $routeControlPath -ne "nvidia-panel") {
    throw "Expected CurrentRouteControlPath to stay on a real route controller. Actual: $routeControlPath"
}

if ($availableRouteModes -notmatch "dgpu" -or $availableRouteModes -notmatch "hybrid") {
    throw "Expected native route modes to include dgpu and hybrid. Actual: $availableRouteModes"
}

if ($routeMode -eq "unknown") {
    throw "Expected truthful current route detection instead of unknown."
}

if ($controlPath -eq "nvidia-panel" -and $routeEvidence -notmatch "Native Dell MUX") {
    throw "Expected route evidence to mention the detected native Dell MUX when NVIDIA fallback is active."
}

Write-Host "MUX truth-model detection passed." -ForegroundColor Green
