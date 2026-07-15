<#
.SYNOPSIS
    Removes everything the self-installing sender put on this machine.

.DESCRIPTION
    REQUIREMENTS.md §9, §13.7. The sender installs itself by copying into
    %LOCALAPPDATA%\ScreenSender and registering a per-user logon auto-start (HKCU\...\Run).
    This reverses exactly those, in the order that matters:

        1. stop the running ScreenSender process (else its exe stays file-locked)
        2. delete the HKCU Run auto-start value (the part a plain folder-delete leaves behind)
        3. delete the install folder (exe, log, settings.ini)

    No admin rights needed — everything it touches is per-user (HKCU, LOCALAPPDATA).
    It does NOT touch the machine settings the installer only *reminded* about
    (auto-login, sleep, lock) — those were never ours to change.

.PARAMETER WhatIf
    Show what would be removed without removing anything.

.EXAMPLE
    .\uninstall-sender.ps1

.EXAMPLE
    .\uninstall-sender.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'

$installDir  = Join-Path $env:LOCALAPPDATA 'ScreenSender'
$runKeyPath  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueNam = 'ScreenSender'

$removed = @()
$missing = @()

# 1. Stop the process, whatever path it runs from, so nothing holds the exe open.
$procs = Get-Process -Name 'ScreenSender' -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) {
        if ($PSCmdlet.ShouldProcess("PID $($p.Id)  ($($p.Path))", 'Stop process')) {
            try { $p.Kill(); $p.WaitForExit(3000); $removed += "process PID $($p.Id)" }
            catch { Write-Warning "could not stop PID $($p.Id): $($_.Exception.Message)" }
        }
    }
} else {
    $missing += 'process (not running)'
}

# 2. Auto-start entry — the piece deleting the folder alone would leave dangling.
$runValue = (Get-ItemProperty -Path $runKeyPath -Name $runValueNam -ErrorAction SilentlyContinue).$runValueNam
if ($null -ne $runValue) {
    if ($PSCmdlet.ShouldProcess("$runKeyPath\$runValueNam", 'Remove auto-start value')) {
        Remove-ItemProperty -Path $runKeyPath -Name $runValueNam
        $removed += "auto-start value ($runValueNam)"
    }
} else {
    $missing += 'auto-start value (not set)'
}

# 3. Install folder.
if (Test-Path $installDir) {
    if ($PSCmdlet.ShouldProcess($installDir, 'Delete folder')) {
        # A just-killed process can briefly keep a handle; retry a few times before giving up.
        for ($i = 1; $i -le 5; $i++) {
            try { Remove-Item -Path $installDir -Recurse -Force; break }
            catch {
                if ($i -eq 5) { throw }
                Start-Sleep -Milliseconds 400
            }
        }
        $removed += "folder ($installDir)"
    }
} else {
    $missing += "folder (not present: $installDir)"
}

Write-Host ''
if ($removed.Count -gt 0) {
    Write-Host 'Removed:' -ForegroundColor Green
    $removed | ForEach-Object { Write-Host "  - $_" }
}
if ($missing.Count -gt 0) {
    Write-Host 'Nothing to remove:' -ForegroundColor DarkGray
    $missing | ForEach-Object { Write-Host "  - $_" }
}
Write-Host ''
Write-Host 'Sender uninstalled. Auto-login / sleep / lock settings were never ours to change and are left as-is.'
