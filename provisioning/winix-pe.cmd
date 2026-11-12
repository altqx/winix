@echo off
rem The Windows PE stage of image build, run by the answer file in place of Windows Setup's own disk and image steps:
rem partition disk 0, apply Windows 11 Pro, add the VirtIO storage and network drivers, turn Microsoft Defender off in
rem the offline SYSTEM hive (unless called with keep-defender), hand the answer file to the installed system, reboot.
rem Defender is switched off before Windows ever boots, so Tamper Protection never applies; it cannot be undone from a
rem running Windows by a script. A failure stops here with the log at X:\winix-pe.log (and W:\winix-pe.log once W: exists).
setlocal EnableExtensions
set LOG=X:\winix-pe.log
set SEED=
set MEDIA=
set VIRTIO=
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
  if not defined SEED if exist %%d:\winix-pe.cmd set SEED=%%d:
  if not defined MEDIA if exist %%d:\sources\install.wim set MEDIA=%%d:\sources\install.wim
  if not defined MEDIA if exist %%d:\sources\install.esd set MEDIA=%%d:\sources\install.esd
  if not defined VIRTIO if exist %%d:\viostor\w11\amd64 set VIRTIO=%%d:
)
echo seed=%SEED% media=%MEDIA% virtio=%VIRTIO% defender=%1 > %LOG%
if not defined SEED (echo The Winix seed volume was not found. >> %LOG% & goto fail)
if not defined MEDIA (echo No sources\install.wim or install.esd was found. >> %LOG% & goto fail)
if not defined VIRTIO (echo The VirtIO driver ISO was not found. >> %LOG% & goto fail)

rem The VM's only disk; the same layout Windows Setup made: EFI, MSR, Windows.
(
  echo select disk 0
  echo clean
  echo convert gpt
  echo create partition efi size=260
  echo format quick fs=fat32 label=System
  echo assign letter=S
  echo create partition msr size=16
  echo create partition primary
  echo format quick fs=ntfs label=Windows
  echo assign letter=W
) > X:\winix-disk.txt
diskpart /s X:\winix-disk.txt >> %LOG% 2>&1 || goto fail
dism /Apply-Image /ImageFile:%MEDIA% /Name:"Windows 11 Pro" /ApplyDir:W:\ >> %LOG% 2>&1 || goto fail
bcdboot W:\Windows /s S: /f UEFI >> %LOG% 2>&1 || goto fail
dism /Image:W:\ /Add-Driver /Driver:%VIRTIO%\viostor\w11\amd64 /Driver:%VIRTIO%\NetKVM\w11\amd64 >> %LOG% 2>&1 || goto fail

if /i "%1"=="keep-defender" goto handoff
reg load HKLM\WinixOffline W:\Windows\System32\config\SYSTEM >> %LOG% 2>&1 || goto fail
for %%s in (WinDefend WdNisSvc WdNisDrv WdFilter WdBoot Sense MDCoreSvc) do (
  reg query HKLM\WinixOffline\ControlSet001\Services\%%s >nul 2>&1 && reg add HKLM\WinixOffline\ControlSet001\Services\%%s /v Start /t REG_DWORD /d 4 /f >> %LOG% 2>&1
)
reg query HKLM\WinixOffline\ControlSet001\Services\WinDefend /v Start 2>> %LOG% | find "0x4" >nul
if errorlevel 1 (echo WinDefend was not disabled. >> %LOG% & reg unload HKLM\WinixOffline >> %LOG% 2>&1 & goto fail)
reg unload HKLM\WinixOffline >> %LOG% 2>&1 || goto fail
echo Defender services disabled offline. >> %LOG%

:handoff
rem The specialize and oobeSystem passes read the answer file from Panther once the installed system boots.
mkdir W:\Windows\Panther 2>nul
copy /y %SEED%\Autounattend.xml W:\Windows\Panther\unattend.xml >> %LOG% 2>&1 || goto fail
echo done >> %LOG%
copy /y %LOG% W:\winix-pe.log >nul
wpeutil reboot
exit /b 0

:fail
echo FAILED >> %LOG%
if exist W:\ copy /y %LOG% W:\winix-pe.log >nul
exit /b 1
