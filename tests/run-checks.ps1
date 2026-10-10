param([string]$OutputDirectory = (Join-Path $env:TEMP 'UtilityTool-WpfChecks'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '..\build.ps1')
$appOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\Debug\net48'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
& $compiler /nologo /target:exe "/out:$appOutput\UtilityTool.WpfChecks.exe" "/reference:$appOutput\UtilityTool.exe" "/reference:$appOutput\Wpf.Ui.dll" "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" /reference:System.Xaml.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/reference:$appOutput\Wpf.Ui.Abstractions.dll" (Join-Path $PSScriptRoot 'WpfChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Checks could not compile.' }
& "$appOutput\UtilityTool.WpfChecks.exe" $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Checks failed; see $OutputDirectory\failure.txt" }
