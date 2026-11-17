# Assistail installer: current user only, no admin rights needed.
# Installs to %LOCALAPPDATA%\Programs\Assistail and adds:
#   - right-click "Open Claude Code here" in File Explorer
#   - Start menu shortcut
#   - entry in Settings > Apps (for uninstalling)
#   - optional: start with Windows
#   - needs Microsoft's free .NET Desktop Runtime 8 (or newer); offers to install it if it's missing
#     (Microsoft's installer asks for admin rights once; Assistail itself never does)
# The app's built-in updater runs this with -Quiet.
param([switch]$Quiet, [switch]$StartWithWindows)
$ErrorActionPreference = 'Stop'

$src     = $PSScriptRoot
$dest    = Join-Path $env:LOCALAPPDATA 'Programs\Assistail'
$exe     = Join-Path $dest 'Assistail.exe'
$ico     = Join-Path $dest 'assistail.ico'
$version = (Get-Item (Join-Path $src 'Assistail.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''

Write-Host ""
Write-Host "Installing Assistail $version" -ForegroundColor DarkYellow
Write-Host "  -> $dest"

# 0. The .NET Desktop Runtime (8 or newer, x64) must be installed.
function Test-DesktopRuntime {
    $roots = @()
    if ($env:DOTNET_ROOT) { $roots += $env:DOTNET_ROOT }
    $reg = Get-ItemProperty 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -Name InstallLocation -ErrorAction SilentlyContinue
    if ($reg) { $roots += $reg.InstallLocation }
    $roots += (Join-Path $env:ProgramFiles 'dotnet')
    foreach ($r in $roots) {
        $dir = Join-Path $r 'shared\Microsoft.WindowsDesktop.App'
        if (-not (Test-Path $dir)) { continue }
        foreach ($v in Get-ChildItem $dir -Directory) {
            $major = 0
            if ([int]::TryParse(($v.Name -split '\.')[0], [ref]$major) -and $major -ge 8) { return $true }
        }
    }
    return $false
}

if (-not (Test-DesktopRuntime)) {
    $runtimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'
    Write-Host ""
    Write-Host "  Assistail needs Microsoft's .NET Desktop Runtime 8 (free, about 55 MB)." -ForegroundColor Yellow
    if ($Quiet) {
        Write-Host "  It isn't installed. Get it from https://dotnet.microsoft.com/download/dotnet/8.0 and run the installer again." -ForegroundColor Yellow
        exit 1
    }
    $answer = Read-Host "  Download and install it from Microsoft now? Windows will ask for permission. (Y/n)"
    if ($answer -match '^(n|no)$') {
        Write-Host "  Install it later from https://dotnet.microsoft.com/download/dotnet/8.0 (Desktop Runtime, x64), then run Install.cmd again."
        Read-Host "Press Enter to close" | Out-Null
        exit 1
    }
    $setup = Join-Path $env:TEMP 'windowsdesktop-runtime-8-win-x64.exe'
    try {
        Write-Host "  Downloading from Microsoft..."
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $runtimeUrl -OutFile $setup -UseBasicParsing
        $sig = Get-AuthenticodeSignature $setup
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw "The download isn't signed by Microsoft."
        }
        Write-Host "  Installing (accept the Windows prompt)..."
        Start-Process -FilePath $setup -ArgumentList '/install', '/passive', '/norestart' -Wait
    } catch {
        Write-Host "  ! Couldn't install the runtime: $($_.Exception.Message)" -ForegroundColor Yellow
    } finally {
        Remove-Item $setup -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-DesktopRuntime)) {
        Write-Host "  ! The .NET Desktop Runtime still isn't installed. Get it from https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
        Write-Host "    (Desktop Runtime, x64), then run Install.cmd again."
        Read-Host "Press Enter to close" | Out-Null
        exit 1
    }
    Write-Host "  + .NET Desktop Runtime installed"
}

# 1. Stop a running copy (so upgrades can overwrite the exe)
Get-Process Assistail -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 1b. Upgrade from the app's earlier name ("Claude Buddy"): remove the old install.
#     Settings are carried over by the app itself on first start.
$oldName = 'Claude' + 'Buddy'
$oldRun = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $oldName -ErrorAction SilentlyContinue
if ((Get-Process $oldName -ErrorAction SilentlyContinue) -or (Test-Path (Join-Path $env:LOCALAPPDATA "Programs\$oldName"))) {
    Write-Host "  ~ Replacing the older version (Claude Buddy)"
    Get-Process $oldName -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Remove-Item (Join-Path $env:LOCALAPPDATA "Programs\$oldName") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Claude Buddy.lnk') -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$oldName" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $oldName -ErrorAction SilentlyContinue
    if ($oldRun) { $StartWithWindows = $true }   # keep "start with Windows" if it was on
}

# 2. Copy files (and clear the "downloaded from the internet" mark)
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in 'Assistail.exe', 'assistail.ico', 'uninstall.ps1', 'Uninstall.cmd', 'Assistail Guide.html') {
    Copy-Item (Join-Path $src $f) (Join-Path $dest $f) -Force
}
Get-ChildItem $dest | Unblock-File

# 3. Right-click menu: "Open Claude Code here"
$wt = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
if (Test-Path $wt) { $command = "`"$wt`" -d `"%V\.`" cmd.exe /k claude" }
else               { $command = "cmd.exe /k `"cd /d `"%V`" && claude`"" }
foreach ($key in 'HKCU:\Software\Classes\Directory\Background\shell\ClaudeCodeHere',
                 'HKCU:\Software\Classes\Directory\shell\ClaudeCodeHere',
                 'HKCU:\Software\Classes\Drive\shell\ClaudeCodeHere') {
    New-Item -Path "$key\command" -Force | Out-Null
    Set-ItemProperty -Path $key -Name '(default)' -Value 'Open Claude Code here'
    Set-ItemProperty -Path $key -Name 'Icon' -Value "`"$ico`""
    Set-ItemProperty -Path "$key\command" -Name '(default)' -Value $command
}
Write-Host "  + Right-click menu: 'Open Claude Code here'"

# 4. Start menu shortcut
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Assistail.lnk'
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $dest
$sc.IconLocation = "$ico,0"
$sc.Description = 'Assistail - quick Claude chat and Claude Code launcher'
$sc.Save()
Write-Host "  + Start menu shortcut"

# 5. Settings > Apps entry
$un = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Assistail'
New-Item -Path $un -Force | Out-Null
$props = @{
    DisplayName     = 'Assistail'
    DisplayVersion  = $version
    Publisher       = 'Assistail (personal)'
    DisplayIcon     = $ico
    InstallLocation = $dest
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
    NoModify        = 1
    NoRepair        = 1
}
foreach ($k in $props.Keys) { Set-ItemProperty -Path $un -Name $k -Value $props[$k] }
Write-Host "  + Listed in Settings > Apps"

# 6. Start with Windows (optional)
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not $Quiet -and -not $StartWithWindows) {
    $answer = Read-Host "  Start Assistail automatically when you sign in? (y/N)"
    $StartWithWindows = $answer -match '^(y|yes)$'
}
if ($StartWithWindows) {
    Set-ItemProperty -Path $runKey -Name 'Assistail' -Value "`"$exe`""
    Write-Host "  + Starts with Windows"
} elseif (Get-ItemProperty -Path $runKey -Name 'Assistail' -ErrorAction SilentlyContinue) {
    # Keep an existing startup entry pointing at the new location
    Set-ItemProperty -Path $runKey -Name 'Assistail' -Value "`"$exe`""
}

# 7. Check requirements
Write-Host ""
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Write-Host "  ! Claude Code CLI not found. Quick chat and 'Open Claude Code here' need it." -ForegroundColor Yellow
    Write-Host "    Install it (see https://claude.com/claude-code), then run 'claude' once to sign in." -ForegroundColor Yellow
}
if (-not (Test-Path $wt)) {
    Write-Host "  i Windows Terminal not found; Claude Code will open in Command Prompt instead." -ForegroundColor DarkGray
}

# 8. Launch. The very first start of a freshly copied exe can be held up while Windows Defender scans it,
#    so try a few times over ~30 s.
$started = $false
for ($attempt = 1; $attempt -le 3 -and -not $started; $attempt++) {
    Start-Process $exe
    for ($i = 0; $i -lt 10 -and -not $started; $i++) {
        Start-Sleep -Seconds 1
        $started = [bool](Get-Process Assistail -ErrorAction SilentlyContinue)
    }
}
if (-not $started) {
    Write-Host "  ! Assistail didn't start yet (Windows may still be scanning it). Start it from the Start menu in a moment." -ForegroundColor Yellow
}
Write-Host "Done! Assistail is running in the system tray." -ForegroundColor Green
Write-Host "Press the hotkey shown in the notification (usually Ctrl+Alt+Space) to chat."
Write-Host ""
if (-not $Quiet) { Read-Host "Press Enter to close" | Out-Null }
