# Adds "Open Claude Code here" to the File Explorer right-click menu (current user only, no admin).
# On Windows 11 it appears under "Show more options" (or Shift+Right-click).
$ErrorActionPreference = 'Stop'

$label = 'Open Claude Code here'
$wt = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
$ico = Join-Path $PSScriptRoot 'assets\assistail.ico'
if (-not (Test-Path $ico)) { & (Join-Path $PSScriptRoot 'make-icon.ps1') }
$icon = "`"$ico`""

if (Test-Path $wt) {
    # "%V\." avoids a trailing backslash (e.g. C:\) escaping the closing quote.
    $command = "`"$wt`" -d `"%V\.`" cmd.exe /k claude"
} else {
    $command = "cmd.exe /k `"cd /d `"%V`" && claude`""
}

$targets = @(
    'HKCU:\Software\Classes\Directory\Background\shell\ClaudeCodeHere', # right-click empty space in a folder
    'HKCU:\Software\Classes\Directory\shell\ClaudeCodeHere',            # right-click a folder
    'HKCU:\Software\Classes\Drive\shell\ClaudeCodeHere'                 # right-click a drive
)

foreach ($key in $targets) {
    New-Item -Path "$key\command" -Force | Out-Null
    Set-ItemProperty -Path $key -Name '(default)' -Value $label
    Set-ItemProperty -Path $key -Name 'Icon' -Value $icon
    Set-ItemProperty -Path "$key\command" -Name '(default)' -Value $command
}

Write-Host "Installed '$label'."
Write-Host "Command: $command"
