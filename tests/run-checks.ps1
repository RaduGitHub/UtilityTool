param([string]$OutputDirectory = (Join-Path $env:TEMP 'UtilityTool-WpfChecks'), [switch]$SkipLock, [switch]$IncludeHiddenLock)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '..\build.ps1')
$appOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\Debug\net48'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$netstandard = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\netstandard.dll'
if (!(Test-Path -LiteralPath $netstandard)) {
    $portableSdk = Join-Path $PSScriptRoot '..\.tools\dotnet\sdk'
    $netstandard = Get-ChildItem -Path "$portableSdk\*\Microsoft\Microsoft.NET.Build.Extensions\net461\lib\netstandard.dll" | Select-Object -ExpandProperty FullName -First 1
}
if (!$netstandard -or !(Test-Path -LiteralPath $netstandard)) { throw 'The .NET Framework 4.8 reference assemblies or bundled SDK are required for these checks.' }
& $compiler /nologo /target:exe "/out:$appOutput\UtilityTool.WpfChecks.exe" "/reference:$appOutput\UtilityTool.exe" "/reference:$appOutput\Wpf.Ui.dll" "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" /reference:System.Xaml.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/reference:$appOutput\Wpf.Ui.Abstractions.dll" "/reference:$appOutput\NAudio.Core.dll" "/reference:$appOutput\NAudio.Wasapi.dll" "/reference:$netstandard" (Join-Path $PSScriptRoot 'WpfChecks.cs') (Join-Path $PSScriptRoot 'SoundboardChecks.cs') (Join-Path $PSScriptRoot 'SoundboardSettingsChecks.cs') (Join-Path $PSScriptRoot 'SoundboardEditChecks.cs') (Join-Path $PSScriptRoot 'SoundboardPageChecks.cs') (Join-Path $PSScriptRoot 'SoundDeviceChecks.cs') (Join-Path $PSScriptRoot 'SoundboardRecorderChecks.cs') (Join-Path $PSScriptRoot 'SoundboardRecordingExportChecks.cs') (Join-Path $PSScriptRoot 'SoundboardRecordingPreviewChecks.cs') (Join-Path $PSScriptRoot 'RecordSoundWindowChecks.cs') (Join-Path $PSScriptRoot 'SoundboardAssetOptimizerChecks.cs') (Join-Path $PSScriptRoot 'SoundboardAssetImportChecks.cs') (Join-Path $PSScriptRoot 'ShutdownPresetChecks.cs') (Join-Path $PSScriptRoot 'LockRunningPageChecks.cs') (Join-Path $PSScriptRoot 'RunningAppCatalogChecks.cs') (Join-Path $PSScriptRoot 'GuardLiveTargetsChecks.cs') (Join-Path $PSScriptRoot 'AppPasswordChecks.cs') (Join-Path $PSScriptRoot 'LockMainChecks.cs') (Join-Path $PSScriptRoot 'GuardReliabilityChecks.cs') (Join-Path $PSScriptRoot 'GuardFocusChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Checks could not compile.' }
& "$appOutput\UtilityTool.WpfChecks.exe" --sound-interop-check naudio-first
if ($LASTEXITCODE -ne 0) { throw 'Soundboard-first audio interoperability checks failed.' }
& "$appOutput\UtilityTool.WpfChecks.exe" --sound-interop-check utility-first
if ($LASTEXITCODE -ne 0) { throw 'Sound-first audio interoperability checks failed.' }
$checkArguments = @($OutputDirectory)
if ($IncludeHiddenLock) { $checkArguments += '--include-hidden-lock' }
if ($SkipLock) { $checkArguments += '--skip-lock' }
& "$appOutput\UtilityTool.WpfChecks.exe" @checkArguments
if ($LASTEXITCODE -ne 0) { throw "Checks failed; see $OutputDirectory\failure.txt" }
