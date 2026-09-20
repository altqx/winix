$ErrorActionPreference = 'Stop'
function Checked($file, [string[]]$arguments) {
  & $file @arguments
  if ($LASTEXITCODE -ne 0) { throw "$file exited $LASTEXITCODE" }
}
New-Item -ItemType Directory out -Force | Out-Null
Checked cmake @('-S','native','-B','out/native','-A','x64')
Checked cmake @('--build','out/native','--config','Debug')
Checked ctest @('--test-dir','out/native','-C','Debug','--output-on-failure','--output-junit','native-results.xml')
Checked cl @('/nologo','/EHsc','/MD','/D_UNICODE','/DUNICODE','/D_AFXDLL','interop/mfc.cpp','/Fe:out/Mfc.exe','/Fo:out/mfc.obj','/link','/SUBSYSTEM:WINDOWS','/ENTRY:wWinMainCRTStartup')
Checked cl @('/nologo','/clr','/MD','interop/cli.cpp','/Fe:out/Interop.exe','/Fo:out/cli.obj')
Checked dotnet @('build','wpf/Wpf.csproj','-o','out/wpf')
Checked dotnet @('build','winforms/WinForms.csproj','-o','out/winforms')
Checked msbuild @('winui/WinUI.csproj','/restore','/v:minimal','/p:Configuration=Debug','/p:Platform=x64','/p:OutputPath=..\out\winui\','/bl:out/winui.binlog')
Checked msbuild @('uwp/Uwp.csproj','/restore','/v:minimal','/p:Configuration=Debug','/p:Platform=x64','/p:AppxPackageDir=..\out\uwp\','/bl:out/uwp.binlog')
exit 0
