<#
.SYNOPSIS
    Installs ScreenSender.exe on the sender machine and registers it to start at logon.

.DESCRIPTION
    REQUIREMENTS.md §9. Run this ON THE SENDER MACHINE, as Administrator, once.

    No firewall rule is created: the sender only makes outbound connections, and Windows Firewall
    allows outbound by default (§13.1). The receiver is the side that prompts for an inbound rule.

    This script does NOT enable auto-login — that is a deliberate manual step, since it weakens the
    machine's security posture. Without it the machine stops at the lock screen after a reboot and
    the sender captures nothing but black (§9, §14).
#>
[CmdletBinding()]
param(
    [string]$SourceExe = "$PSScriptRoot\..\dist\sender\ScreenSender.exe",
    [string]$InstallDir = "$env:LOCALAPPDATA\ScreenSender",
    [string]$TaskName = 'ScreenSender'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SourceExe)) { throw "Not found: $SourceExe. Run build-sender.ps1 first." }

Write-Host "Installing to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

$targetExe = Join-Path $InstallDir 'ScreenSender.exe'
Get-Process -Name 'ScreenSender' -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item $SourceExe $targetExe -Force

Write-Host "Registering scheduled task '$TaskName' (at logon)"
try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop } catch {}

# Capture requires an interactive desktop, so this runs in the logged-on user's session,
# not as a Windows service (§9).
$action  = New-ScheduledTaskAction -Execute $targetExe
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest

# The remote machine may boot before the network is up, and it may sit idle or on battery for
# months. None of that may stop or throttle the task.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -DontStopOnIdleEnd `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings | Out-Null

Start-ScheduledTask -TaskName $TaskName

Write-Host ""
Write-Host "Installed. Log: $env:LOCALAPPDATA\ScreenSender\sender.log"
Write-Host ""
Write-Host "REMAINING MANUAL STEPS on this machine (goal: stay logged-in and awake forever):" -ForegroundColor Yellow
Write-Host "  1. Auto-login (netplwiz): reboot must land on the desktop, not the password screen."
Write-Host "  2. Never sleep: Settings > System > Power > Sleep = Never."
Write-Host "     (A sleeping PC stops the app entirely. Monitor power-off alone is fine.)"
Write-Host "  3. Never lock: Settings > Accounts > Sign-in options > 'Require sign-in' = Never,"
Write-Host "     and if a screen saver is enabled, untick 'On resume, display logon screen'."
Write-Host "     (A locked session captures as a black screen.)"
