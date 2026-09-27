param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $Root 'source\GSwitcher.Desktop\GSwitcher.Desktop.csproj'
$app = Join-Path $Root 'source\GSwitcher.Desktop\App.xaml.cs'
$automation = Join-Path $Root 'source\GSwitcher.Desktop\Runtime\AutomationEngine.cs'
$audioShield = Join-Path $Root 'source\GSwitcher.Desktop\Runtime\AudioPrivacyShield.cs'
$networkFocus = Join-Path $Root 'source\GSwitcher.Desktop\Runtime\NetworkFocusService.cs'
$osdXaml = Join-Path $Root 'source\GSwitcher.Desktop\OsdWindow.xaml'
$osdCode = Join-Path $Root 'source\GSwitcher.Desktop\OsdWindow.xaml.cs'
$hudXaml = Join-Path $Root 'source\GSwitcher.Desktop\TelemetryHudWindow.xaml'
$hudCode = Join-Path $Root 'source\GSwitcher.Desktop\TelemetryHudWindow.xaml.cs'
$mainWindow = Join-Path $Root 'source\GSwitcher.Desktop\MainWindow.xaml.cs'
$index = Join-Path $Root 'source\GSwitcher.Desktop\ui\index.html'
$style = Join-Path $Root 'source\GSwitcher.Desktop\ui\style.css'

foreach ($path in @($project, $app, $automation, $audioShield, $networkFocus, $osdXaml, $osdCode, $hudXaml, $hudCode, $mainWindow, $index, $style)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing required file: $path"
    }
}

$projectText = Get-Content -LiteralPath $project -Raw
$appText = Get-Content -LiteralPath $app -Raw
$automationText = Get-Content -LiteralPath $automation -Raw
$audioShieldText = Get-Content -LiteralPath $audioShield -Raw
$networkFocusText = Get-Content -LiteralPath $networkFocus -Raw
$hudCodeText = Get-Content -LiteralPath $hudCode -Raw
$mainWindowText = Get-Content -LiteralPath $mainWindow -Raw
$indexText = Get-Content -LiteralPath $index -Raw
$styleText = Get-Content -LiteralPath $style -Raw
$powerManager = Join-Path $Root 'source\GSwitcher.Desktop\Hardware\PowerManager.cs'
$powerManagerText = Get-Content -LiteralPath $powerManager -Raw

$requiredProjectEntries = @(
    'Page Include="OsdWindow.xaml"',
    'Compile Include="OsdWindow.xaml.cs"',
    'Compile Include="Runtime\AutomationEngine.cs"',
    'Compile Include="Runtime\AudioPrivacyShield.cs"',
    'Compile Include="Runtime\NetworkFocusService.cs"',
    'Page Include="TelemetryHudWindow.xaml"',
    'Compile Include="TelemetryHudWindow.xaml.cs"',
    'Reference Include="System.ServiceProcess"'
)

foreach ($entry in $requiredProjectEntries) {
    if ($projectText -notlike "*$entry*") {
        throw "Project file is missing entry: $entry"
    }
}

$requiredAutomationTokens = @(
    'SettingsManager.Current.AfkDownclock',
    'SettingsManager.Current.StandbyHygiene',
    'SettingsManager.Current.BatteryBleedOut',
    'SettingsManager.Current.QuickSwitchOsd',
    'SettingsManager.Current.AudioPrivacyShield',
    'GetLastInputInfo',
    'SystemEvents.PowerModeChanged',
    'RegisterHotKey',
    'RegisterPowerSettingNotification',
    'ToggleTelemetryHud',
    'NotifyPowerModeChanged',
    'NetworkFocusService',
    'RefreshAudioPrivacyShield',
    'SwitchPower("battery"',
    'SwitchPower("daily"'
)

foreach ($token in $requiredAutomationTokens) {
    if ($automationText -notlike "*$token*") {
        throw "AutomationEngine.cs is missing required token: $token"
    }
}

if ($indexText -notlike '*incoming.type === "emergency-eco"*') {
    throw 'index.html does not handle the emergency-eco payload.'
}

if ($styleText -notlike '*data-emergency="true"*' -or $styleText -notlike '*animation: none !important*') {
    throw 'style.css does not disable reactor animations for emergency eco mode.'
}

$requiredPowerQueueTokens = @(
    'QueuePowerMode',
    'PowerManager.NormalizeMode(requestedMode)',
    '_state.PowerMode = PowerManager.DetectActiveMode();',
    'label + " requested."'
)

foreach ($token in $requiredPowerQueueTokens) {
    if ($appText -notlike "*$token*") {
        throw "App.xaml.cs is missing verified power queue token: $token"
    }
}

if ($appText -like '*RunBackendCommand("Power "*') {
    throw 'Power UI messages still use the generic backend queue; this can resend stale PowerMode during busy state.'
}

$requiredFastPowerTokens = @(
    'public static OperationResult ApplyFast',
    'PowerManager.ApplyFast(mode)',
    'ScheduleDeferredHardwareRefresh'
)

foreach ($token in $requiredFastPowerTokens) {
    $source = if ($token -eq 'public static OperationResult ApplyFast') { $powerManagerText } else { $appText }
    if ($source -notlike "*$token*") {
        throw "Fast power path is missing required token: $token"
    }
}

if ($powerManagerText -notlike '*var verifiedMode = DetectActiveMode();*' -or
    $powerManagerText -notlike '*if (verifiedMode != mode)*') {
    throw 'PowerManager.cs must verify the active scheme after switching.'
}

$forbiddenCaptureTokens = @(
    'CaptureEvidenceAsync',
    'EvidenceRecorder',
    'CaptureDashboardAsync',
    'CapturePreviewAsync',
    'evidence:capture',
    'evidence:export',
    'EvidenceRootDirectory',
    'ExportDirectory'
)

foreach ($token in $forbiddenCaptureTokens) {
    if ($appText -like "*$token*" -or
        $mainWindowText -like "*$token*" -or
        $projectText -like "*$token*" -or
        $indexText -like "*$token*" -or
        $styleText -like "*$token*") {
        throw "Capture/evidence mechanism is still present: $token"
    }
}

$evidenceRecorderPath = Join-Path $Root 'source\GSwitcher.Desktop\Evidence\EvidenceRecorder.cs'
if (Test-Path -LiteralPath $evidenceRecorderPath) {
    throw 'EvidenceRecorder.cs still exists in source.'
}

if ($indexText -notlike '*setMotionProfile(power)*' -or $indexText -notlike '*--orbit-speed-a*') {
    throw 'index.html is missing dynamic reactor motion profile updates.'
}

if ($styleText -notlike '*--orbit-speed-a*' -or $styleText -notlike '*pulseGpu*') {
    throw 'style.css is missing dynamic reactor speed variables or GPU pulse animation.'
}

if ($mainWindowText -notlike '*WebMessageReceived -= CoreWebView2_WebMessageReceived*' -or
    $mainWindowText -notlike '*NavigationCompleted -= CoreWebView2_NavigationCompleted*') {
    throw 'MainWindow.xaml.cs must detach WebView2 event handlers before disposing the control.'
}

if ($audioShieldText -notlike '*IMMNotificationClient*' -or
    $audioShieldText -notlike '*IAudioEndpointVolume*' -or
    $audioShieldText -notlike '*SetMute(true*' -or
    $audioShieldText -like '*NAudio*') {
    throw 'AudioPrivacyShield.cs must use dependency-free CoreAudio endpoint notifications and mute support.'
}

if ($networkFocusText -notlike '*ServiceController("BITS")*' -or
    $networkFocusText -notlike '*ResumeIfOwned*' -or
    $networkFocusText -notlike '*CanPauseAndContinue*') {
    throw 'NetworkFocusService.cs must safely pause/resume BITS only when supported.'
}

if ($networkFocusText -notlike '*powerMode != "tested-ultra"*' -or
    $automationText -notlike '*currentMode == "tested-ultra"*' -or
    $automationText -notlike '*mode == "tested-ultra"*') {
    throw 'Automation mode comparisons must match normalized power mode names.'
}

$stressText = Get-Content -LiteralPath (Join-Path $Root 'source\GSwitcher.Desktop\Hardware\CpuStressTestService.cs') -Raw
$smartText = Get-Content -LiteralPath (Join-Path $Root 'source\GSwitcher.Desktop\Hardware\SmartCoolingService.cs') -Raw
if ($stressText -match 'StartStressWorkers\(Environment\.ProcessorCount\)' -or
    $stressText -notlike '*StartStressWorkers(Math.Min(4,*') {
    throw 'Stress test worker count is not capped at four.'
}
if ($smartText -like '*proc.ProcessorAffinity = (IntPtr)1*' -or
    $smartText -like '*proc.PriorityClass = ProcessPriorityClass.Idle*') {
    throw 'Automatic optimization must not leave other processes with modified scheduling.'
}

if ($automationText -notlike '*BA3E0F4D-B817-4094-A2D1-D56379E6A0F3*' -or
    $automationText -notlike '*IsPowerModeBlocked*') {
    throw 'Lid failsafe must subscribe to GUID_LIDSWITCH_STATE_CHANGE and expose Ultra blocking.'
}

if ($hudCodeText -notlike '*GlobalMemoryStatusEx*' -or
    $hudCodeText -notlike '*PerformanceCounter*' -or
    $hudCodeText -notlike '*WsExTransparent*') {
    throw 'Telemetry HUD must be lightweight, click-through, and native-counter based.'
}

Write-Host 'Automation pack static verification passed.'
