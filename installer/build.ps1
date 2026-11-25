# Сборка EPOLISI-assist-Setup.exe встроенным компилятором .NET Framework
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

$exe = Join-Path $here "EPOLISI-assist-Setup.exe"
$icoSrc = Join-Path $here "..\agent\icon.ico"
$args = @("/nologo", "/target:winexe", "/optimize+", "/platform:anycpu", "/out:$exe")
if (Test-Path $icoSrc) { $args += "/win32icon:$icoSrc" }
$args += "/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll"
$args += (Join-Path $here "Installer.cs")

& "$fw\csc.exe" @args
if ($LASTEXITCODE -ne 0) { throw "csc failed" }
Get-Item $exe | Select-Object Name, Length
