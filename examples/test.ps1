$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory results -Force | Out-Null
function UI($method, $arguments) {
  $request = @{ method=$method; arguments=$arguments } | ConvertTo-Json -Depth 10 -Compress
  $response = $request | & C:\Winix\agent\winix-guest.exe ui
  if ($LASTEXITCODE -ne 0) { throw "UI operation failed: $response" }
  return $response | ConvertFrom-Json
}
$results = @()
function Exercise($name, $launch, $button, $field) {
  Write-Host "Testing $name UI"
  $app = UI launch $launch
  try {
    UI wait @{pid=$app.pid;automationId=$button;timeoutMs=30000} | Out-Null
    UI click @{pid=$app.pid;automationId=$button} | Out-Null
    UI assert @{pid=$app.pid;automationId=$field;text='Passed'} | Out-Null
    $screen = UI screenshot @{}
    [IO.File]::WriteAllBytes((Join-Path (Get-Location) "results/$name.png"), [Convert]::FromBase64String($screen.data))
    Write-Host "Passed $name UI"
    return @{name=$name;passed=$true}
  } finally { if ($app.pid) { UI close @{pid=$app.pid} | Out-Null } }
}
try {
  $root = (Get-Location).Path
  $results += Exercise 'win32' @{exe="$root\out\native\Debug\WinixNative.exe"} '100' '101'
  $results += Exercise 'wpf' @{exe="$root\out\wpf\Wpf.exe"} 'Change' 'Result'
  $results += Exercise 'winforms' @{exe="$root\out\winforms\WinForms.exe"} 'Change' 'Result'
  $results += Exercise 'winui' @{exe="$root\out\winui\WinUI.exe"} 'Change' 'Result'
  $mfc = UI launch @{exe="$root\out\Mfc.exe"}
  try {
    UI wait @{pid=$mfc.pid;timeoutMs=30000} | Out-Null
    $results += @{name='mfc-atl';passed=$true}
    Write-Host 'Passed MFC/ATL window smoke test'
  } finally { UI close @{pid=$mfc.pid} | Out-Null }
  & "$root\out\Interop.exe"
  if ($LASTEXITCODE -ne 0) { throw 'C++/CLI test failed.' }
  $results += @{name='cpp-cli';passed=$true}
  Write-Host 'Passed C++/CLI runtime test'
  $packages = @(Get-ChildItem out/uwp -Recurse -File | Where-Object Extension -In @('.appx','.msix'))
  $package = $packages | Where-Object FullName -NotMatch 'Dependencies' | Select-Object -First 1
  if (!$package) { throw 'UWP package was not produced.' }
  $dependencies = @($packages | Where-Object { $_.FullName -match 'Dependencies\\(x64|neutral)\\' } | Select-Object -ExpandProperty FullName)
  UI deploy @{package=$package.FullName;dependencies=$dependencies} | Out-Null
  $installed = Get-AppxPackage Winix.Smoke
  $results += Exercise 'uwp' @{appId="$($installed.PackageFamilyName)!App"} 'Change' 'Result'
  UI uninstall @{packageFullName=$installed.PackageFullName} | Out-Null
  $results | ConvertTo-Json -Depth 5 | Set-Content results/results.json
  exit 0
} catch {
  @{error=$_.Exception.Message;completed=$results} | ConvertTo-Json -Depth 6 | Set-Content results/results.json
  throw
}
