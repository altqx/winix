<#
.SYNOPSIS
  Records idle CPU, memory, disk usage, process count and installed package counts, so a debloat or image
  change is measured rather than assumed. Optionally times a reference script (e.g. a build) under -ReferenceScript.
#>
param([Parameter(Mandatory)][string]$Output, [string]$Label = '', [string]$ReferenceScript = '', [int]$Seconds = 20)
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$cpu = $null
try { $samples = Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples ([Math]::Max(1, [int]($Seconds / 2))); $cpu = [Math]::Round(($samples.CounterSamples | Measure-Object CookedValue -Average).Average, 1) } catch { }
$os = Get-CimInstance Win32_OperatingSystem
$disk = Get-PSDrive C
$result = [ordered]@{
  label = $Label; measuredAt = (Get-Date).ToUniversalTime().ToString('o'); sampleSeconds = $Seconds
  idleCpuPercent = $cpu
  memoryUsedMiB = [int](($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1024); memoryTotalMiB = [int]($os.TotalVisibleMemorySize / 1024)
  diskUsedGiB = [Math]::Round($disk.Used / 1GB, 2); diskFreeGiB = [Math]::Round($disk.Free / 1GB, 2)
  processes = (Get-Process).Count; services = @(Get-Service | Where-Object Status -eq 'Running').Count
  provisionedAppx = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue).Count
  scheduledTasksReady = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object State -eq 'Ready').Count
  uptimeMinutes = [int]((Get-Date) - $os.LastBootUpTime).TotalMinutes
}
if ($ReferenceScript -and (Test-Path $ReferenceScript)) {
  $watch = [Diagnostics.Stopwatch]::StartNew()
  try { & $ReferenceScript | Out-Null; $result.referenceExitCode = $LASTEXITCODE } catch { $result.referenceError = $_.Exception.Message }
  $result.referenceSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
}
$result | ConvertTo-Json -Depth 3 | Set-Content -Path $Output -Encoding UTF8
Write-Output "Measured $Label -> $Output"
