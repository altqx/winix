param([switch]$Schedule)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = 'C:\Winix'
if ($Schedule) {
  $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\Winix\bootstrap.ps1'
  $trigger = New-ScheduledTaskTrigger -AtStartup
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  Register-ScheduledTask -TaskName 'WinixProvision' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
  Start-ScheduledTask -TaskName 'WinixProvision'
  exit
}
Start-Transcript -Path "$root\provision.log" -Append
function Status($phase, $errorText = $null) {
  @{ phase=$phase; error=$errorText; updatedAt=(Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json | Set-Content "$root\provision.json"
}
function CheckedProcess($file, $arguments) {
  $p = Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru
  if ($p.ExitCode -notin @(0,3010,1641)) { throw "$file exited $($p.ExitCode)" }
}
try {
  $settings = Get-Content "$root\settings.json" -Raw | ConvertFrom-Json
  Status 'drivers'
  $drivers = Get-Volume | Where-Object { $_.DriveLetter -and (Test-Path ($_.DriveLetter + ':\virtio-win-gt-x64.msi')) } | Select-Object -First 1
  if (!(Test-Path "$root\drivers.done")) {
    if (!$drivers) { throw 'VirtIO driver ISO is not mounted.' }
    $letter = $drivers.DriveLetter
    CheckedProcess 'msiexec.exe' "/i ${letter}:\virtio-win-gt-x64.msi /qn /norestart /L*v C:\Winix\virtio.log"
    if (Test-Path "${letter}:\guest-agent\qemu-ga-x86_64.msi") {
      CheckedProcess 'msiexec.exe' "/i ${letter}:\guest-agent\qemu-ga-x86_64.msi /qn /norestart"
    }
    # The guest-tools MSI leaves QXL and virtio-gpu on the Basic Display Adapter; install their display drivers directly.
    foreach ($display in @('qxldod', 'viogpudo')) {
      $inf = Get-ChildItem "${letter}:\$display" -Recurse -Filter *.inf -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\(w11|w10|2k22|2k19)\\amd64\\' } |
        Sort-Object { switch -Regex ($_.FullName) { '\\w11\\' { 0 } '\\w10\\' { 1 } '\\2k22\\' { 2 } default { 3 } } } | Select-Object -First 1
      if ($inf) {
        & pnputil.exe /add-driver $inf.FullName /install | Out-Null
        if ($LASTEXITCODE -notin @(0, 259, 3010)) { Write-Warning "$display driver install exited $LASTEXITCODE" }
      }
    }
    New-Item "$root\drivers.done" -ItemType File -Force | Out-Null
  }
  Status 'ssh'
  if (!(Get-LocalUser -Name winix -ErrorAction SilentlyContinue)) {
    New-LocalUser -Name winix -Password (ConvertTo-SecureString $settings.password -AsPlainText -Force) -PasswordNeverExpires | Out-Null
  }
  $testUser = Get-LocalUser -Name winix
  if (!(Get-LocalGroupMember -SID 'S-1-5-32-545' | Where-Object SID -eq $testUser.SID)) {
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $testUser
  }
  & icacls $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*$($testUser.SID):(OI)(CI)RX" | Out-Null
  if ($LASTEXITCODE) { throw 'Cannot secure the guest installation directory.' }
  & icacls "$root\authorized_keys" /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "*$($testUser.SID):R" | Out-Null
  if ($LASTEXITCODE) { throw 'Cannot secure the SSH authorized key.' }
  foreach ($dir in @('work','jobs','inbox','exports','elevated','dumps','profiles','cache','launch','tools')) {
    New-Item -ItemType Directory "$root\$dir" -Force | Out-Null
    & icacls "$root\$dir" /grant "*$($testUser.SID):(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE) { throw "Cannot grant the test user access to $dir." }
  }
  if (!(Get-Service sshd -ErrorAction SilentlyContinue)) {
    # Microsoft-published package, pinned independently of Windows Update/FOD downloads.
    $sshUrl = 'https://github.com/PowerShell/Win32-OpenSSH/releases/download/10.0.0.0p2-Preview/OpenSSH-Win64-v10.0.0.0.msi'
    Invoke-WebRequest $sshUrl -OutFile "$root\openssh.msi" -UseBasicParsing
    if ((Get-FileHash "$root\openssh.msi" -Algorithm SHA256).Hash -ne 'ddec9c53864280759cf9f74791cefd387100e3946aa849a1c138a4ed1b96b7d9') { throw 'OpenSSH package checksum validation failed.' }
    if ((Get-AuthenticodeSignature "$root\openssh.msi").Status -ne 'Valid') { throw 'OpenSSH package signature validation failed.' }
    $sshMode = if (Test-Path 'C:\Program Files\OpenSSH\sshd.exe') { '/fa' } else { '/i' }
    CheckedProcess 'msiexec.exe' "$sshMode C:\Winix\openssh.msi ADDLOCAL=Server /qn /norestart /L*v C:\Winix\openssh.log"
  }
  $sshDirectory = if (Test-Path 'C:\Program Files\OpenSSH\sshd.exe') { 'C:\Program Files\OpenSSH' } else { 'C:\Windows\System32\OpenSSH' }
  $machinePath = [Environment]::GetEnvironmentVariable('Path','Machine')
  if ($machinePath.Split(';') -notcontains $sshDirectory) { [Environment]::SetEnvironmentVariable('Path', "$machinePath;$sshDirectory", 'Machine') }
  Start-Service sshd
  Set-Service sshd -StartupType Automatic
  @"
Port 22
PubkeyAuthentication yes
PasswordAuthentication no
AllowUsers winix
AuthorizedKeysFile C:/Winix/authorized_keys
Subsystem sftp "$sshDirectory\sftp-server.exe"
"@ | Set-Content 'C:\ProgramData\ssh\sshd_config' -Encoding ascii
  Restart-Service sshd
  if (!(Get-NetFirewallRule -Name 'WinixSSH' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name WinixSSH -DisplayName 'Winix SSH' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow | Out-Null
  }
  Status 'visualstudio'
  if (!(Test-Path "$root\visualstudio.done")) {
    $url = 'https://aka.ms/vs/18/stable/vs_' + $settings.edition.ToLowerInvariant() + '.exe'
    Invoke-WebRequest $url -OutFile "$root\vs-installer.exe" -UseBasicParsing
    if ((Get-AuthenticodeSignature "$root\vs-installer.exe").Status -ne 'Valid') { throw 'Visual Studio installer signature validation failed.' }
    CheckedProcess "$root\vs-installer.exe" "--quiet --wait --norestart --nocache --installPath C:\VisualStudio --config C:\Winix\desktop.vsconfig --includeRecommended"
    New-Item "$root\visualstudio.done" -ItemType File -Force | Out-Null
  }
  $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
  $requiredComponents = (Get-Content "$root\desktop.vsconfig" -Raw | ConvertFrom-Json).components
  $missingComponents = @($requiredComponents | Where-Object { !(& $vswhere -latest -products '*' -requires $_ -property installationPath) })
  if ($missingComponents.Count) { throw "Visual Studio is missing required components: $($missingComponents -join ', ')" }
  Status 'debloat'
  if (!(Test-Path "$root\debloat.done")) {
    & "$root\measure.ps1" -Output "$root\measure-before.json" -Label 'before-debloat'
    & "$root\debloat.ps1" -Manifest "$root\debloat.json"
    & "$root\measure.ps1" -Output "$root\measure-after.json" -Label 'after-debloat'
    New-Item "$root\debloat.done" -ItemType File -Force | Out-Null
  }
  Status 'desktop'
  New-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Force | Out-Null
  New-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -Value 1 -PropertyType DWord -Force | Out-Null
  & powercfg /change monitor-timeout-ac 0
  & powercfg /change standby-timeout-ac 0
  & powercfg /change hibernate-timeout-ac 0
  & powercfg /hibernate off
  $logon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
  foreach ($pair in @{AutoAdminLogon='1';DefaultUserName='winix';DefaultDomainName=$env:COMPUTERNAME;DefaultPassword=$settings.password}.GetEnumerator()) {
    Set-ItemProperty $logon -Name $pair.Key -Value $pair.Value
  }
  Remove-ItemProperty $logon -Name AutoLogonCount -ErrorAction SilentlyContinue
  $action = New-ScheduledTaskAction -Execute "$root\agent\winix-guest.exe" -Argument 'worker' -WorkingDirectory $root
  $trigger = New-ScheduledTaskTrigger -AtLogOn -User $testUser.SID.Value
  $principal = New-ScheduledTaskPrincipal -UserId $testUser.SID.Value -LogonType Interactive -RunLevel Limited
  $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName WinixDesktop -Action $action -Trigger $trigger -Principal $principal -Settings $taskSettings -Force | Out-Null
  # The explicit elevated path: an on-demand SYSTEM task the test user may start (GRGX in its security descriptor)
  # but not edit. It runs winix-guest.exe elevated, which handles requests queued under C:\Winix\elevated.
  $scheduler = New-Object -ComObject Schedule.Service; $scheduler.Connect()
  $definition = $scheduler.NewTask(0)
  $definition.RegistrationInfo.Description = 'Winix elevated runner (tasks flagged elevated: true, winix guest driver add)'
  $definition.Principal.UserId = 'SYSTEM'; $definition.Principal.LogonType = 5; $definition.Principal.RunLevel = 1
  $definition.Settings.ExecutionTimeLimit = 'PT4H'; $definition.Settings.MultipleInstances = 2; $definition.Settings.AllowDemandStart = $true
  $definition.Settings.DisallowStartIfOnBatteries = $false; $definition.Settings.StopIfGoingOnBatteries = $false; $definition.Settings.StartWhenAvailable = $true
  $elevatedAction = $definition.Actions.Create(0); $elevatedAction.Path = "$root\agent\winix-guest.exe"; $elevatedAction.Arguments = 'elevated'; $elevatedAction.WorkingDirectory = $root
  $sddl = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$($testUser.SID.Value))"
  $scheduler.GetFolder('\').RegisterTaskDefinition('WinixElevated', $definition, 6, 'SYSTEM', $null, 5, $sddl) | Out-Null
  if ($settings.nvda -and $settings.nvda.url) {
    & "$root\nvda.ps1" -Url $settings.nvda.url -Sha256 $settings.nvda.sha256 -InstallDir "$root\tools\nvda" -User winix
  }
  $certificate = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq 'CN=Winix Development' | Select-Object -First 1
  if (!$certificate) { $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=Winix Development' -KeyUsage DigitalSignature -FriendlyName 'Winix Development' -CertStoreLocation Cert:\LocalMachine\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}') }
  Export-Certificate -Cert $certificate -FilePath "$root\development.cer" | Out-Null
  Import-Certificate -FilePath "$root\development.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
  # The local test account may sign its own test packages; never use this certificate for release signing.
  Export-PfxCertificate -Cert $certificate -FilePath "$root\development.pfx" -Password (ConvertTo-SecureString 'winix-development-only' -AsPlainText -Force) | Out-Null
  & icacls "$root\development.pfx" /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' "*$($testUser.SID):R" | Out-Null
  if ($LASTEXITCODE) { throw 'Cannot secure the development certificate.' }
  $vs = & $vswhere -latest -products '*' -format json | ConvertFrom-Json
  $debloat = if (Test-Path "$root\debloat.json") { Get-Content "$root\debloat.json" -Raw | ConvertFrom-Json } else { $null }
  $measure = @{}; foreach ($m in @('before', 'after')) { if (Test-Path "$root\measure-$m.json") { $measure[$m] = Get-Content "$root\measure-$m.json" -Raw | ConvertFrom-Json } }
  $displayDrivers = @(Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | Select-Object FriendlyName, Status)
  $nvda = if (Test-Path "$root\nvda.json") { Get-Content "$root\nvda.json" -Raw | ConvertFrom-Json } else { $null }
  @{ready=$true;visualStudio=$vs;dotnet=(& 'C:\Program Files\dotnet\dotnet.exe' --list-sdks);windows=(Get-ComputerInfo -Property WindowsVersion,OsBuildNumber);certificate=$certificate.Thumbprint;
    elevated='WinixElevated';displayDrivers=$displayDrivers;debloat=$debloat;measure=$measure;nvda=$nvda} | ConvertTo-Json -Depth 12 | Set-Content "$root\toolchain.json"
  Status 'complete'
  Unregister-ScheduledTask WinixProvision -Confirm:$false
  Disable-LocalUser WinixSetup
  Remove-Item "$root\Autounattend.xml","$root\settings.json" -Force -ErrorAction SilentlyContinue
  Stop-Transcript
  Restart-Computer -Force
} catch {
  Status 'failed' $_.Exception.Message
  Write-Error $_ -ErrorAction Continue
  Stop-Transcript
  exit 1
}
