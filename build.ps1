$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
& $compiler /nologo /target:winexe /optimize+ /reference:System.Security.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/out:$PSScriptRoot\UtilityTool.exe" "$PSScriptRoot\Program.cs" "/recurse:$PSScriptRoot\Frontend\*.cs" "/recurse:$PSScriptRoot\Backend\*.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
