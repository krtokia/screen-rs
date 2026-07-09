<#
.SYNOPSIS
    Publishes the sender as a single self-contained exe with the receiver address baked in.

.DESCRIPTION
    REQUIREMENTS.md §6.1 — the sender has no runtime configuration. Changing where it points
    means rebuilding it here and reinstalling it on the remote machine.

.EXAMPLE
    .\build-sender.ps1 -ReceiverHost 192.168.0.50 -DeviceId SENDER-01
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReceiverHost,
    [int]$ReceiverPort = 45871,
    [string]$DeviceId = 'SENDER-01',
    [int]$CaptureFps = 3,
    [int]$JpegQuality = 60,
    [double]$CaptureScale = 1.0,
    [string]$OutDir = "$PSScriptRoot\..\dist\sender"
)

$ErrorActionPreference = 'Stop'

# DEVICE_ID is a fixed 16-byte field on the wire (§7.1).
$idBytes = [System.Text.Encoding]::UTF8.GetByteCount($DeviceId)
if ($idBytes -gt 16) { throw "DeviceId '$DeviceId' is $idBytes UTF-8 bytes; the protocol allows 16." }

$project = Join-Path $PSScriptRoot '..\src\Monitor.Sender\Monitor.Sender.csproj'

dotnet publish $project `
    -c Release `
    -o $OutDir `
    -p:ReceiverHost=$ReceiverHost `
    -p:ReceiverPort=$ReceiverPort `
    -p:DeviceId=$DeviceId `
    -p:CaptureFps=$CaptureFps `
    -p:JpegQuality=$JpegQuality `
    -p:CaptureScale=$CaptureScale

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Built ScreenSender.exe -> $ReceiverHost`:$ReceiverPort  (id=$DeviceId, ${CaptureFps}fps, q$JpegQuality, scale $CaptureScale)"
Write-Host "Output: $OutDir"
Write-Host "Copy ScreenSender.exe to the sender machine and double-click it. It installs itself"
Write-Host "(copy to %LOCALAPPDATA%, register logon auto-start) and shows the manual setup steps."
