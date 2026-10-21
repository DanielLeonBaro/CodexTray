$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ /out:"$PSScriptRoot\CodexTray.exe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\CodexTray.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$check = Start-Process -FilePath "$PSScriptRoot\CodexTray.exe" -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if ($check.ExitCode -ne 0) { throw 'Self-test failed. See error.txt.' }
Get-Content "$PSScriptRoot\self-test.txt"
Get-Item "$PSScriptRoot\CodexTray.exe" | Select-Object Name,Length
