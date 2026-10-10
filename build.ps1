param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$sdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) {
    $installedSdk = Get-Command dotnet -ErrorAction SilentlyContinue
    if (!$installedSdk) { throw 'Install a .NET SDK (8 or later), or use Visual Studio with the .NET desktop development workload.' }
    $sdk = $installedSdk.Source
}
& $sdk build (Join-Path $PSScriptRoot 'UtilityTool.sln') --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Run: $PSScriptRoot\bin\$Configuration\net48\UtilityTool.exe"
