# Removes Assistail and everything the installer added.
param([switch]$Quiet)
$ErrorActionPreference = 'Continue'
Set-Location $env:TEMP   # don't hold the install folder open

$dest = Join-Path $env:LOCALAPPDATA 'Programs\Assistail'

Write-Host "Uninstalling Assistail..." -ForegroundColor DarkYellow
Get-Process Assistail -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

foreach ($key in 'HKCU:\Software\Classes\Directory\Background\shell\ClaudeCodeHere',
                 'HKCU:\Software\Classes\Directory\shell\ClaudeCodeHere',
                 'HKCU:\Software\Classes\Drive\shell\ClaudeCodeHere',
                 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Assistail') {
    if (Test-Path $key) { Remove-Item -Path $key -Recurse -Force }
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Assistail' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Assistail.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Assistail') -Recurse -Force -ErrorAction SilentlyContinue  # settings + log
Remove-Item (Join-Path $env:LOCALAPPDATA ('Claude' + 'Buddy')) -Recurse -Force -ErrorAction SilentlyContinue  # leftovers from the old name

Write-Host "Assistail has been removed." -ForegroundColor Green
if (-not $Quiet) { Start-Sleep -Seconds 2 }
