# Builds release\Assistail-<version>.zip: self-contained exe (.NET bundled) + installer scripts.
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$dotnet  = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$stage = Join-Path $root 'release\Assistail'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

& $dotnet publish (Join-Path $root 'app\Assistail.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -o (Join-Path $root 'release\publish')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $root 'release\publish\Assistail.exe') $stage
Copy-Item (Join-Path $root 'assets\assistail.ico') $stage
Copy-Item (Join-Path $root 'installer\*') $stage

$version = (Get-Item (Join-Path $stage 'Assistail.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $root "release\Assistail-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item (Join-Path $root 'release\publish') -Recurse -Force

Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
