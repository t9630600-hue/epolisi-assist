# Сборка EPOLISI-operator.exe встроенным компилятором .NET Framework (ничего устанавливать не нужно)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

$exe = Join-Path $here "EPOLISI-operator.exe"
$icoSrc = Join-Path $here "..\agent\icon.ico"
$args = @("/nologo", "/target:winexe", "/optimize+", "/platform:anycpu", "/out:$exe")
if (Test-Path $icoSrc) { $args += "/win32icon:$icoSrc" }
$args += "/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:$fw\System.Web.Extensions.dll"
$args += (Join-Path $here "Operator.cs")

& "$fw\csc.exe" @args
if ($LASTEXITCODE -ne 0) { throw "csc failed" }
Get-Item $exe | Select-Object Name, Length
