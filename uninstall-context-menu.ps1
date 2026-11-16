# Removes the "Open Claude Code here" right-click entries added by install-context-menu.ps1.
$keys = @(
    'HKCU:\Software\Classes\Directory\Background\shell\ClaudeCodeHere',
    'HKCU:\Software\Classes\Directory\shell\ClaudeCodeHere',
    'HKCU:\Software\Classes\Drive\shell\ClaudeCodeHere'
)
foreach ($key in $keys) {
    if (Test-Path $key) { Remove-Item -Path $key -Recurse -Force }
}
Write-Host "Removed 'Open Claude Code here'."
