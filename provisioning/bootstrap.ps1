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
  foreach ($dir in @('work','jobs','inbox','exports')) {
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
  $certificate = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq 'CN=Winix Development' | Select-Object -First 1
  if (!$certificate) { $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=Winix Development' -KeyUsage DigitalSignature -FriendlyName 'Winix Development' -CertStoreLocation Cert:\LocalMachine\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}') }
  Export-Certificate -Cert $certificate -FilePath "$root\development.cer" | Out-Null
  Import-Certificate -FilePath "$root\development.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
  # The local test account may sign its own test packages; never use this certificate for release signing.
  Export-PfxCertificate -Cert $certificate -FilePath "$root\development.pfx" -Password (ConvertTo-SecureString 'winix-development-only' -AsPlainText -Force) | Out-Null
  & icacls "$root\development.pfx" /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' "*$($testUser.SID):R" | Out-Null
  if ($LASTEXITCODE) { throw 'Cannot secure the development certificate.' }
  $vs = & $vswhere -latest -products '*' -format json | ConvertFrom-Json
  @{ready=$true;visualStudio=$vs;dotnet=(& 'C:\Program Files\dotnet\dotnet.exe' --list-sdks);windows=(Get-ComputerInfo -Property WindowsVersion,OsBuildNumber);certificate=$certificate.Thumbprint} | ConvertTo-Json -Depth 12 | Set-Content "$root\toolchain.json"
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
