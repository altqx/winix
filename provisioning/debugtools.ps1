<#
.SYNOPSIS
  Debugging, static-analysis and PE tools for the guest. Runs as SYSTEM: from bootstrap.ps1 during image build, and from
  `winix guest tools install` on an existing VM. Every component is idempotent; rerunning repairs or updates it.
.DESCRIPTION
  sdk           Windows SDK features: Debugging Tools for Windows (cdb, windbg, kd, gflags, symchk, umdh),
                Application Verifier, Windows Performance Toolkit (wpr, xperf, wpa). Microsoft-signed installer.
  sysinternals  procdump, procmon, procexp, sigcheck, handle, listdlls, vmmap, strings, accesschk, autorunsc, dbgview,
                tcpvcon (64-bit builds, each Microsoft-signed) in C:\Winix\tools\sysinternals.
  dotnet        dotnet-dump, -trace, -counters, -gcdump, -stack, -symbol and ilspycmd in C:\Winix\tools\dotnet.
  vs            Visual Studio components: Clang/LLVM (clang-cl, clang-tidy) and AddressSanitizer.
  symbols       _NT_SYMBOL_PATH with the Microsoft symbol server, cached in C:\Winix\cache\symbols.
  wer           Windows Error Reporting LocalDumps into C:\Winix\dumps\crash, with no crash dialog to block tests.
  The result is C:\Winix\debugtools.json: the tools found, the directories jobs get on PATH, the symbol path.
.EXAMPLE
  .\debugtools.ps1 -Components sdk,symbols
#>
param([string[]]$Components = @('sdk', 'sysinternals', 'dotnet', 'vs', 'symbols', 'wer'), [string]$User = 'winix')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = 'C:\Winix'
$Components = @($Components | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$known = @('sdk', 'sysinternals', 'dotnet', 'vs', 'symbols', 'wer')
$unknown = @($Components | Where-Object { $known -notcontains $_ })
if ($unknown.Count) { throw "Unknown debug tool components: $($unknown -join ', '). Use $($known -join ', ')." }

$kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
$debuggers = "$kits\Debuggers\x64"
$wpt = "$kits\Windows Performance Toolkit"
$sysinternals = "$root\tools\sysinternals"
$dotnetTools = "$root\tools\dotnet"
$crashDumps = "$root\dumps\crash"
$symbolCache = "$root\cache\symbols"
$vsComponents = @('Microsoft.VisualStudio.Component.VC.Llvm.Clang', 'Microsoft.VisualStudio.Component.VC.Llvm.ClangToolset', 'Microsoft.VisualStudio.Component.VC.ASAN')
$account = Get-LocalUser -Name $User -ErrorAction SilentlyContinue

function Checked($file, $arguments, $allowed = @(0, 3010, 1641)) {
  $p = Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru
  if ($p.ExitCode -notin $allowed) { throw "$file exited $($p.ExitCode)" }
}
function MicrosoftSigned($path) {
  $signature = Get-AuthenticodeSignature $path
  $signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -match 'O=Microsoft Corporation'
}
function GrantUser($path) {
  if ($account) { & icacls $path /grant "*$($account.SID):(OI)(CI)M" | Out-Null }
}
# Values for the test user's HKCU: the live hive when it is loaded, its NTUSER.DAT otherwise, and the default profile for
# accounts created later.
function SetUserValue($subkey, $name, $value) {
  $hives = @()
  if ($account -and (Test-Path "Registry::HKEY_USERS\$($account.SID.Value)")) { $hives += @{ key = "Registry::HKEY_USERS\$($account.SID.Value)"; file = $null } }
  elseif ($account) {
    $profileDir = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$($account.SID.Value)" -ErrorAction SilentlyContinue).ProfileImagePath
    if ($profileDir -and (Test-Path "$profileDir\NTUSER.DAT")) { $hives += @{ key = 'Registry::HKEY_USERS\WinixToolsUser'; file = "$profileDir\NTUSER.DAT" } }
  }
  $hives += @{ key = 'Registry::HKEY_USERS\WinixToolsDefault'; file = "$env:SystemDrive\Users\Default\NTUSER.DAT" }
  foreach ($hive in $hives) {
    if ($hive.file) {
      if (!(Test-Path $hive.file)) { continue }
      & reg.exe load ($hive.key -replace '^Registry::HKEY_USERS', 'HKU') $hive.file | Out-Null
      if ($LASTEXITCODE) { Write-Warning "Cannot load $($hive.file)"; continue }
    }
    try {
      New-Item "$($hive.key)\$subkey" -Force | Out-Null
      New-ItemProperty "$($hive.key)\$subkey" -Name $name -Value $value -PropertyType DWord -Force | Out-Null
    } finally {
      if ($hive.file) { [GC]::Collect(); [GC]::WaitForPendingFinalizers(); & reg.exe unload ($hive.key -replace '^Registry::HKEY_USERS', 'HKU') | Out-Null }
    }
  }
}

if ($Components -contains 'sdk') {
  if (!(Test-Path "$debuggers\cdb.exe") -or !(Test-Path "$wpt\wpr.exe") -or !(Test-Path "$env:SystemRoot\System32\appverif.exe")) {
    # Windows SDK for Windows 11 10.0.26100.9457 (September 2026), the SDK line the Visual Studio component list installs.
    $sdkUrl = 'https://go.microsoft.com/fwlink/?linkid=2382321'
    $installer = "$root\winsdksetup.exe"
    Invoke-WebRequest $sdkUrl -OutFile $installer -UseBasicParsing
    if (!(MicrosoftSigned $installer)) { throw 'Windows SDK installer signature validation failed.' }
    Checked $installer '/features OptionId.WindowsDesktopDebuggers OptionId.AvrfExternal OptionId.WindowsPerformanceToolkit /quiet /norestart /ceip off /log C:\Winix\winsdk.log'
    Remove-Item $installer -Force -ErrorAction SilentlyContinue
    if (!(Test-Path "$debuggers\cdb.exe")) { throw "Debugging Tools for Windows did not install; see C:\Winix\winsdk.log." }
  }
}

if ($Components -contains 'sysinternals') {
  New-Item -ItemType Directory $sysinternals -Force | Out-Null
  $staging = "$sysinternals.new"; if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
  New-Item -ItemType Directory $staging -Force | Out-Null
  foreach ($tool in @('procdump64', 'procmon64', 'procexp64', 'sigcheck64', 'handle64', 'Listdlls64', 'vmmap64', 'strings64', 'accesschk64', 'autorunsc64', 'dbgview64', 'tcpvcon64')) {
    $file = "$staging\$tool.exe"
    Invoke-WebRequest "https://live.sysinternals.com/$tool.exe" -OutFile $file -UseBasicParsing
    if (!(MicrosoftSigned $file)) { throw "Sysinternals $tool signature validation failed." }
  }
  Copy-Item "$staging\*" $sysinternals -Force
  Remove-Item $staging -Recurse -Force
  Get-ChildItem $sysinternals -Filter *.exe | ForEach-Object { Unblock-File $_.FullName }
  GrantUser $sysinternals
  # Tools that predate the global switch still take -accepteula on the command line.
  SetUserValue 'Software\Sysinternals' 'EulaAccepted' 1
}

if ($Components -contains 'dotnet') {
  $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
  if (!(Test-Path $dotnet)) { throw 'The .NET SDK is not installed; install Visual Studio first.' }
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
  New-Item -ItemType Directory $dotnetTools -Force | Out-Null
  foreach ($tool in @('dotnet-dump', 'dotnet-trace', 'dotnet-counters', 'dotnet-gcdump', 'dotnet-stack', 'dotnet-symbol', 'ilspycmd')) {
    # update installs a missing tool too (.NET 8 and later); NuGet verifies package signatures.
    & $dotnet tool update $tool --tool-path $dotnetTools | Out-Host
    if ($LASTEXITCODE) { throw "dotnet tool update $tool exited $LASTEXITCODE" }
  }
  GrantUser $dotnetTools
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if ($Components -contains 'vs') {
  if (!(Test-Path $vswhere)) { throw 'Visual Studio is not installed.' }
  $installPath = & $vswhere -latest -products '*' -property installationPath
  $missing = @($vsComponents | Where-Object { !(& $vswhere -latest -products '*' -requires $_ -property installationPath) })
  if ($missing.Count) {
    $add = ($missing | ForEach-Object { "--add $_" }) -join ' '
    Checked "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe" "modify --installPath `"$installPath`" $add --quiet --norestart --nocache"
    $missing = @($vsComponents | Where-Object { !(& $vswhere -latest -products '*' -requires $_ -property installationPath) })
    if ($missing.Count) { throw "Visual Studio is still missing: $($missing -join ', ')" }
  }
}

if ($Components -contains 'symbols') {
  New-Item -ItemType Directory $symbolCache -Force | Out-Null
  GrantUser $symbolCache
  if (![Environment]::GetEnvironmentVariable('_NT_SYMBOL_PATH', 'Machine')) {
    [Environment]::SetEnvironmentVariable('_NT_SYMBOL_PATH', "srv*$symbolCache*https://msdl.microsoft.com/download/symbols", 'Machine')
  }
}

if ($Components -contains 'wer') {
  New-Item -ItemType Directory $crashDumps -Force | Out-Null
  GrantUser $crashDumps
  $wer = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting'
  New-Item "$wer\LocalDumps" -Force | Out-Null
  New-ItemProperty "$wer\LocalDumps" -Name DumpFolder -Value $crashDumps -PropertyType ExpandString -Force | Out-Null
  New-ItemProperty "$wer\LocalDumps" -Name DumpType -Value 2 -PropertyType DWord -Force | Out-Null   # full dumps
  New-ItemProperty "$wer\LocalDumps" -Name DumpCount -Value 10 -PropertyType DWord -Force | Out-Null
  # A crash dialog would block the desktop for every following UI action.
  New-ItemProperty $wer -Name DontShowUI -Value 1 -PropertyType DWord -Force | Out-Null
}

# The inventory reflects what is on disk, whichever components this run touched.
$vsPath = if (Test-Path $vswhere) { & $vswhere -latest -products '*' -property installationPath } else { $null }
$llvm = if ($vsPath) { "$vsPath\VC\Tools\Llvm\x64\bin" } else { $null }
$dumpbin = if ($vsPath) { Get-ChildItem "$vsPath\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 } else { $null }
$candidates = [ordered]@{
  cdb = "$debuggers\cdb.exe"; windbg = "$debuggers\windbg.exe"; kd = "$debuggers\kd.exe"; gflags = "$debuggers\gflags.exe"; symchk = "$debuggers\symchk.exe"; umdh = "$debuggers\umdh.exe"
  appverif = "$env:SystemRoot\System32\appverif.exe"; wpr = "$wpt\wpr.exe"; xperf = "$wpt\xperf.exe"; wpa = "$wpt\wpa.exe"
  dumpbin = $(if ($dumpbin) { $dumpbin.FullName } else { $null }); clangTidy = $(if ($llvm) { "$llvm\clang-tidy.exe" } else { $null }); clangCl = $(if ($llvm) { "$llvm\clang-cl.exe" } else { $null })
}
foreach ($t in @('procdump64', 'procmon64', 'procexp64', 'sigcheck64', 'handle64', 'Listdlls64', 'vmmap64', 'strings64', 'accesschk64', 'autorunsc64', 'dbgview64', 'tcpvcon64')) { $candidates[$t.ToLowerInvariant()] = "$sysinternals\$t.exe" }
foreach ($t in @('dotnet-dump', 'dotnet-trace', 'dotnet-counters', 'dotnet-gcdump', 'dotnet-stack', 'dotnet-symbol', 'ilspycmd')) { $candidates[$t] = "$dotnetTools\$t.exe" }
$tools = [ordered]@{}
foreach ($entry in $candidates.GetEnumerator()) { if ($entry.Value -and (Test-Path $entry.Value)) { $tools[$entry.Key] = $entry.Value } }
$paths = @($debuggers, $wpt, $sysinternals, $dotnetTools, $llvm | Where-Object { $_ -and (Test-Path $_) })
$werDumps = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps' -ErrorAction SilentlyContinue).DumpFolder
@{ updatedAt = (Get-Date).ToUniversalTime().ToString('o'); components = $Components; tools = $tools; paths = $paths
   symbolPath = [Environment]::GetEnvironmentVariable('_NT_SYMBOL_PATH', 'Machine'); crashDumps = $werDumps } | ConvertTo-Json -Depth 4 | Set-Content "$root\debugtools.json" -Encoding UTF8
Write-Output "Debug tools: $($tools.Keys -join ', ')"
