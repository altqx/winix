<#
.SYNOPSIS
  Removes consumer apps, background work and update traffic a build/test VM never uses.
  Everything removed is recorded in a manifest so a rebuild is reproducible and the change is auditable.
  Edge and WebView2 are kept (installers and apps may need them). Defender stays on with workspace exclusions.
#>
param([string]$Manifest = 'C:\Winix\debloat.json', [string[]]$Workspaces = @('C:\Winix', 'C:\Winix\work', 'C:\vcpkg'))
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$record = [ordered]@{ startedAt = (Get-Date).ToUniversalTime().ToString('o'); appx = @(); provisioned = @(); features = @(); capabilities = @(); services = @(); tasks = @(); policies = @(); exclusions = @(); errors = @() }
function Note($list, $item) { $record[$list] = @($record[$list]) + $item }
function Try-Step($label, [scriptblock]$step) { try { & $step } catch { Note errors "$label: $($_.Exception.Message)" } }

# 1. Provisioned Store apps (new profiles would reinstall them) and their installed copies.
$appPatterns = @(
  'Microsoft.549981C3F5F10', 'Microsoft.BingNews', 'Microsoft.BingWeather', 'Microsoft.BingSearch', 'Microsoft.GamingApp', 'Microsoft.GetHelp', 'Microsoft.Getstarted',
  'Microsoft.MicrosoftOfficeHub', 'Microsoft.MicrosoftSolitaireCollection', 'Microsoft.MicrosoftStickyNotes', 'Microsoft.People', 'Microsoft.PowerAutomateDesktop',
  'Microsoft.Todos', 'Microsoft.WindowsFeedbackHub', 'Microsoft.WindowsMaps', 'Microsoft.Xbox.TCUI', 'Microsoft.XboxGameOverlay', 'Microsoft.XboxGamingOverlay',
  'Microsoft.XboxIdentityProvider', 'Microsoft.XboxSpeechToTextOverlay', 'Microsoft.XboxApp', 'Microsoft.YourPhone', 'Microsoft.ZuneMusic', 'Microsoft.ZuneVideo',
  'Clipchamp.Clipchamp', 'Microsoft.Copilot', 'Microsoft.Windows.Ai.Copilot.Provider', 'Microsoft.Windows.DevHome', 'MicrosoftTeams', 'MSTeams',
  'Microsoft.OutlookForWindows', 'Microsoft.Windows.Photos', 'Microsoft.WindowsCamera', 'Microsoft.WindowsSoundRecorder', 'Microsoft.WindowsAlarms',
  'Microsoft.MicrosoftJournal', 'Microsoft.Whiteboard', 'Microsoft.SkypeApp', 'Microsoft.Office.OneNote', 'Microsoft.MixedReality.Portal', 'Microsoft.Wallet',
  'Microsoft.Windows.Teams', 'Microsoft.Family', 'Microsoft.StartExperiencesApp', 'Microsoft.WidgetsPlatformRuntime', 'MicrosoftWindows.Client.WebExperience',
  'MicrosoftCorporationII.QuickAssist', 'MicrosoftCorporationII.MicrosoftFamily', 'Microsoft.LinkedIn', 'Microsoft.Paint3D', 'Microsoft.3DBuilder', 'Microsoft.Print3D'
)
foreach ($pattern in $appPatterns) {
  Try-Step "provisioned $pattern" {
    Get-AppxProvisionedPackage -Online | Where-Object DisplayName -like "$pattern*" | ForEach-Object {
      Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction Stop | Out-Null; Note provisioned $_.PackageName
    }
  }
  Try-Step "appx $pattern" {
    Get-AppxPackage -AllUsers -Name "$pattern*" | ForEach-Object {
      Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction Stop; Note appx $_.PackageFullName
    }
  }
}

# 2. OneDrive and its per-user setup hook in the default profile.
Try-Step 'onedrive' {
  foreach ($setup in @("$env:SystemRoot\System32\OneDriveSetup.exe", "$env:SystemRoot\SysWOW64\OneDriveSetup.exe")) {
    if (Test-Path $setup) { Start-Process $setup '/uninstall' -Wait -WindowStyle Hidden; Note appx 'OneDriveSetup /uninstall' }
  }
  & reg.exe load 'HKLM\WinixDefaultUser' 'C:\Users\Default\NTUSER.DAT' | Out-Null
  if ($LASTEXITCODE -eq 0) {
    & reg.exe delete 'HKLM\WinixDefaultUser\Software\Microsoft\Windows\CurrentVersion\Run' /v OneDriveSetup /f 2>$null | Out-Null
    [GC]::Collect(); & reg.exe unload 'HKLM\WinixDefaultUser' | Out-Null
    Note tasks 'DefaultUser Run\OneDriveSetup'
  }
}

# 3. Optional features and capabilities a headless-ish build VM never uses.
foreach ($feature in @('WindowsMediaPlayer', 'WorkFolders-Client', 'Printing-XPSServices-Features', 'Recall', 'MediaPlayback', 'Printing-Foundation-InternetPrinting-Client')) {
  Try-Step "feature $feature" {
    $state = Get-WindowsOptionalFeature -Online -FeatureName $feature -ErrorAction SilentlyContinue
    if ($state -and $state.State -eq 'Enabled') { Disable-WindowsOptionalFeature -Online -FeatureName $feature -NoRestart -Remove -ErrorAction Stop | Out-Null; Note features $feature }
  }
}
foreach ($capability in @('App.StepsRecorder~~~~0.0.1.0', 'Media.WindowsMediaPlayer~~~~0.0.12.0', 'Microsoft.Windows.WordPad~~~~0.0.1.0', 'Print.Fax.Scan~~~~0.0.1.0',
    'Browser.InternetExplorer~~~~0.0.11.0', 'MathRecognizer~~~~0.0.1.0', 'Hello.Face.20134~~~~0.0.1.0', 'Language.Handwriting~~~en-US~0.0.1.0', 'Language.Speech~~~en-US~0.0.1.0', 'Microsoft.Windows.MSPaint~~~~0.0.1.0')) {
  Try-Step "capability $capability" {
    $state = Get-WindowsCapability -Online -Name $capability -ErrorAction SilentlyContinue
    if ($state -and $state.State -eq 'Installed') { Remove-WindowsCapability -Online -Name $capability -ErrorAction Stop | Out-Null; Note capabilities $capability }
  }
}

# 4. Background services that compete with builds for CPU and disk. Error Reporting stays (crash dumps are evidence).
foreach ($service in @('DiagTrack', 'dmwappushservice', 'SysMain', 'DoSvc', 'MapsBroker', 'RetailDemo', 'XblAuthManager', 'XblGameSave', 'XboxNetApiSvc', 'XboxGipSvc', 'WpnService', 'wisvc', 'edgeupdate', 'edgeupdatem', 'MicrosoftEdgeElevationService')) {
  Try-Step "service $service" {
    $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
    if ($svc) { Stop-Service $svc -Force -ErrorAction SilentlyContinue; Set-Service $svc.Name -StartupType Disabled -ErrorAction Stop; Note services $svc.Name }
  }
}

# 5. Scheduled tasks that wake up at sign-in or on a timer.
foreach ($pattern in @('MicrosoftEdgeUpdate*', 'OneDrive*', '\Microsoft\Windows\Application Experience\*', '\Microsoft\Windows\Customer Experience Improvement Program\*',
    '\Microsoft\Windows\Windows Error Reporting\QueueReporting', '\Microsoft\Windows\Feedback\Siuf\*', '\Microsoft\Windows\Maps\*', '\Microsoft\XblGameSave\*', '\Microsoft\Windows\Autochk\Proxy')) {
  Try-Step "task $pattern" {
    $tasks = if ($pattern.StartsWith('\')) { $folder = Split-Path $pattern; $leaf = Split-Path $pattern -Leaf; Get-ScheduledTask -TaskPath "$folder\" -TaskName $leaf -ErrorAction SilentlyContinue } else { Get-ScheduledTask -TaskName $pattern -ErrorAction SilentlyContinue }
    foreach ($task in @($tasks)) { if ($task.State -ne 'Disabled') { Disable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null; Note tasks ($task.TaskPath + $task.TaskName) } }
  }
}

# 6. Policies: no automatic Windows Update or Store updates mid-job (run them during image build), no peer delivery, no consumer content.
$policies = @(
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'; Name = 'NoAutoUpdate'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization'; Name = 'DODownloadMode'; Value = 0 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore'; Name = 'AutoDownload'; Value = 2 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent'; Name = 'DisableWindowsConsumerFeatures'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent'; Name = 'DisableConsumerAccountStateContent'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent'; Name = 'DisableSoftLanding'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh'; Name = 'AllowNewsAndInterests'; Value = 0 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot'; Name = 'TurnOffWindowsCopilot'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI'; Name = 'DisableAIDataAnalysis'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection'; Name = 'AllowTelemetry'; Value = 0 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'; Name = 'EnableActivityFeed'; Value = 0 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Explorer'; Name = 'DisableSearchBoxSuggestions'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name = 'HideSCAMeetNow'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive'; Name = 'DisableFileSyncNGSC'; Value = 1 },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR'; Name = 'AllowGameDVR'; Value = 0 }
)
foreach ($policy in $policies) {
  Try-Step "policy $($policy.Path)\$($policy.Name)" {
    New-Item -Path $policy.Path -Force | Out-Null
    New-ItemProperty -Path $policy.Path -Name $policy.Name -Value $policy.Value -PropertyType DWord -Force | Out-Null
    Note policies "$($policy.Path)\$($policy.Name)=$($policy.Value)"
  }
}

# 7. Keep Defender, but keep it out of the build tree; keep search indexing off the workspace.
foreach ($workspace in $Workspaces) {
  Try-Step "exclusion $workspace" {
    if (Test-Path $workspace) {
      Add-MpPreference -ExclusionPath $workspace -ErrorAction Stop; Note exclusions $workspace
      & attrib.exe +I $workspace /S /D 2>$null | Out-Null
    }
  }
}
Try-Step 'defender schedule' { Set-MpPreference -ScanScheduleDay 8 -DisableCatchupFullScan $true -DisableCatchupQuickScan $true -ScanAvgCPULoadFactor 20 -ErrorAction Stop; Note policies 'Defender: no scheduled scans, 20% CPU cap' }

$record.finishedAt = (Get-Date).ToUniversalTime().ToString('o')
$record | ConvertTo-Json -Depth 4 | Set-Content -Path $Manifest -Encoding UTF8
Write-Output "Debloat recorded in $Manifest ($($record.appx.Count) apps, $($record.provisioned.Count) provisioned packages, $($record.services.Count) services, $($record.errors.Count) errors)."
