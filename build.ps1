$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll /win32manifest:"$PSScriptRoot\app.manifest" /win32icon:"$PSScriptRoot\reconnector.ico" "/resource:$PSScriptRoot\cat-base.png,CatBase" "/resource:$PSScriptRoot\cat-paws.png,CatPaws" /out:"$PSScriptRoot\HearthstoneReconnector.exe" "$PSScriptRoot\Reconnector.cs" "$PSScriptRoot\Firewall.cs" "$PSScriptRoot\GameConnections.cs" "$PSScriptRoot\RuneArt.cs" "$PSScriptRoot\LayeredSurface.cs" "$PSScriptRoot\DirectReconnect.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }

