[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z][A-Za-z0-9.-]{0,63}$')]
    [string]$ServiceName = 'AgentForge',
    [Management.Automation.PSCredential]$Credential,
    [switch]$Start
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this installer from an elevated PowerShell session.'
}
$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$hostExecutable = [IO.Path]::GetFullPath((Join-Path $packageRoot 'host/AgentForge.Host.exe'))
if (-not $hostExecutable.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) {
    throw 'The self-contained AgentForge host is missing from this package.'
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Uninstall it before reinstalling."
}
if ($null -eq $Credential) {
    $Credential = Get-Credential -UserName $identity.Name -Message 'AgentForge service account (must be this operator)'
}
if (-not [string]::Equals($Credential.UserName, $identity.Name, [StringComparison]::OrdinalIgnoreCase)) {
    throw "R1 requires the Windows service to run as the installing operator '$($identity.Name)' so DPAPI remains recoverable."
}
$dataDirectory = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'AgentForge'))
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
& icacls.exe $dataDirectory /inheritance:r /grant:r "$($identity.User.Value):(OI)(CI)M" '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to protect the AgentForge data directory.' }
$workerExecutable = [IO.Path]::GetFullPath((Join-Path $packageRoot 'worker/agentforge-plugin-worker.exe'))
if (-not (Test-Path -LiteralPath $workerExecutable -PathType Leaf)) { throw 'The plugin worker is missing.' }
$binaryPath = ('"{0}" --AgentForge:Plugins:PluginWorkerExecutable="{1}"' -f $hostExecutable, $workerExecutable)
New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'AgentForge' `
    -Description 'Local security-first AgentForge harness' -StartupType Automatic -Credential $Credential | Out-Null
& sc.exe config $ServiceName start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to configure the AgentForge service identity.' }
if ($Start) { Start-Service -Name $ServiceName }
Write-Output "Installed $ServiceName. Data: $dataDirectory"
