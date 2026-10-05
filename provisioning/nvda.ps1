<#
.SYNOPSIS
  Optional recipe: a portable NVDA screen reader for accessibility gates. The installer is downloaded from the URL
  you supply and verified against the SHA-256 you supply (Winix pins nothing here because NVDA releases move).
  The portable copy speaks through the silence synthesizer and writes an io-level log, so tests read what NVDA
  would have said without any audio.
.EXAMPLE
  .\nvda.ps1 -Url https://www.nvaccess.org/download/nvda/releases/2026.2/nvda_2026.2.exe -Sha256 <hash> -InstallDir C:\Winix\tools\nvda
#>
param([Parameter(Mandatory)][string]$Url, [Parameter(Mandatory)][string]$Sha256, [string]$InstallDir = 'C:\Winix\tools\nvda', [string]$User = 'winix')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$installer = Join-Path $env:TEMP 'nvda-installer.exe'
Invoke-WebRequest $Url -OutFile $installer -UseBasicParsing
$actual = (Get-FileHash $installer -Algorithm SHA256).Hash
if ($actual -ne $Sha256.ToUpperInvariant()) { throw "NVDA installer checksum mismatch: expected $Sha256, got $actual" }
$signature = Get-AuthenticodeSignature $installer
if ($signature.Status -ne 'Valid') { throw "NVDA installer signature is $($signature.Status)" }
New-Item -ItemType Directory $InstallDir -Force | Out-Null
$process = Start-Process $installer -ArgumentList "--create-portable --portable-path=`"$InstallDir`" --minimal --config-path=`"$InstallDir\userConfig`"" -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "NVDA portable creation exited $($process.ExitCode)" }
New-Item -ItemType Directory "$InstallDir\userConfig" -Force | Out-Null
@"
schemaVersion = 14
[general]
	loggingLevel = IO
	showWelcomeDialogAtStartup = False
	playStartAndExitSounds = False
[update]
	autoCheck = False
	startupNotification = False
[speech]
	synth = silence
[audio]
	soundVolume = 0
"@ | Set-Content "$InstallDir\userConfig\nvda.ini" -Encoding ascii
$account = Get-LocalUser -Name $User -ErrorAction SilentlyContinue
if ($account) { & icacls $InstallDir /grant "*$($account.SID):(OI)(CI)M" | Out-Null }
Remove-Item $installer -Force -ErrorAction SilentlyContinue
@{ installDir = $InstallDir; url = $Url; sha256 = $Sha256; log = "$InstallDir\userConfig\nvda.log"; launch = "$InstallDir\nvda.exe -m -c $InstallDir\userConfig" } | ConvertTo-Json | Set-Content 'C:\Winix\nvda.json' -Encoding UTF8
Write-Output "NVDA portable at $InstallDir (silence synth, IO log). Launch with: winix ui launch --exe $InstallDir\nvda.exe --arg -m --arg -c --arg $InstallDir\userConfig"
