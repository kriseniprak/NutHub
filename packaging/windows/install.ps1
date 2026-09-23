<#
.SYNOPSIS
    Installs, upgrades or removes NutHub as a Windows service.

.DESCRIPTION
    Copies nuthub.exe (found next to this script) to "C:\Program Files\NutHub", registers the "NutHub" service
    (automatic start, restarted on failure, Windows Event Log source) with "nuthub service install", optionally opens
    the NUT (TCP 3493) and web panel (TCP 8493) ports in Windows Firewall, and starts the service. Running it again
    upgrades in place; the configuration and data in %ProgramData%\NutHub are kept.

.PARAMETER AddFirewallRules
    Allow inbound TCP 3493 (NUT clients) and 8493 (web panel) for nuthub.exe in Windows Firewall.

.PARAMETER Uninstall
    Stop and remove the service, the firewall rules and the program files. The configuration and data are kept
    unless -Purge is given too.

.PARAMETER Purge
    With -Uninstall: also delete %ProgramData%\NutHub (configuration, database, logs, keys).

.PARAMETER InstallDir
    Where to install the executable (default: C:\Program Files\NutHub).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -AddFirewallRules
#>
[CmdletBinding()]
param(
    [switch] $AddFirewallRules,
    [switch] $Uninstall,
    [switch] $Purge,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'NutHub')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'NutHub'
$FirewallGroup = 'NutHub'
$DataDir = Join-Path $env:ProgramData 'NutHub'
$Exe = Join-Path $InstallDir 'nuthub.exe'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-NutHub {
    param([string[]] $Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "nuthub $($Arguments -join ' ') failed (exit code $LASTEXITCODE)."
    }
}

function Remove-FirewallRules {
    Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

function Add-FirewallRules {
    Remove-FirewallRules
    New-NetFirewallRule -Group $FirewallGroup -DisplayName 'NutHub NUT server (TCP 3493)' -Direction Inbound `
        -Protocol TCP -LocalPort 3493 -Program $Exe -Action Allow | Out-Null
    New-NetFirewallRule -Group $FirewallGroup -DisplayName 'NutHub web panel (TCP 8493)' -Direction Inbound `
        -Protocol TCP -LocalPort 8493 -Program $Exe -Action Allow | Out-Null
    Write-Host '==> Windows Firewall: inbound TCP 3493 and 8493 allowed for nuthub.exe'
}

function Stop-NutHubService {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Write-Host "==> Stopping the $ServiceName service"
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

if (-not (Test-Administrator)) {
    Write-Error 'Run this script from an elevated PowerShell (Run as administrator).'
    exit 1
}

if ($Purge -and -not $Uninstall) {
    Write-Error '-Purge is only valid with -Uninstall.'
    exit 2
}

if ($Uninstall) {
    if (Test-Path $Exe) {
        Write-Host "==> Removing the $ServiceName service"
        Invoke-NutHub @('service', 'uninstall')
    }
    elseif (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-NutHubService
        & sc.exe delete $ServiceName | Out-Null
    }

    Remove-FirewallRules
    if (Test-Path $InstallDir) {
        Write-Host "==> Removing $InstallDir"
        Remove-Item -Recurse -Force $InstallDir
    }

    if ($Purge) {
        if (Test-Path $DataDir) {
            Write-Host "==> Deleting $DataDir"
            Remove-Item -Recurse -Force $DataDir
        }
        Write-Host 'NutHub has been removed completely.'
    }
    else {
        Write-Host "NutHub has been removed. The configuration and data are kept in $DataDir (-Uninstall -Purge deletes them)."
    }
    exit 0
}

$source = Join-Path $PSScriptRoot 'nuthub.exe'
if (-not (Test-Path $source)) {
    Write-Error "nuthub.exe was not found next to this script ($PSScriptRoot)."
    exit 1
}

Stop-NutHubService

Write-Host "==> Installing nuthub.exe in $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Force $source $Exe
foreach ($doc in @('LICENSE', 'README.md')) {
    $docPath = Join-Path $PSScriptRoot $doc
    if (Test-Path $docPath) {
        Copy-Item -Force $docPath (Join-Path $InstallDir $doc)
    }
}

Write-Host "==> Registering the $ServiceName service"
Invoke-NutHub @('service', 'install', '--no-start')

if ($AddFirewallRules) {
    Add-FirewallRules
}

Write-Host "==> Starting the $ServiceName service"
Invoke-NutHub @('service', 'start')

$passwordFile = Join-Path $DataDir 'initial-admin-password.txt'
for ($i = 0; $i -lt 15 -and -not (Test-Path $passwordFile); $i++) {
    Start-Sleep -Seconds 1
}

Write-Host ''
Write-Host 'NutHub is installed.'
Write-Host '  Web panel:      http://localhost:8493/'
Write-Host '  NUT clients:    port 3493'
Write-Host "  Configuration:  $(Join-Path $DataDir 'nuthub.json')"
Write-Host "  Logs:           $(Join-Path $DataDir 'logs')"
if (Test-Path $passwordFile) {
    Write-Host ''
    Write-Host "First sign-in (you will be asked to choose a new password; then delete $passwordFile):"
    Get-Content $passwordFile | ForEach-Object { Write-Host "  $_" }
}
if (-not $AddFirewallRules) {
    Write-Host ''
    Write-Host 'Other machines reach NutHub only if Windows Firewall allows it: run again with -AddFirewallRules.'
}
