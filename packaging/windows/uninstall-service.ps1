[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z][A-Za-z0-9.-]{0,63}$')]
    [string]$ServiceName = 'AgentForge'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this uninstaller from an elevated PowerShell session.'
}
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) { Write-Output "Service $ServiceName is not installed."; exit 0 }
if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
& sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Unable to remove service $ServiceName." }
Write-Output "Removed $ServiceName. The data directory was preserved for recovery."
