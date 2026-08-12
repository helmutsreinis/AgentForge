[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$required = @(
    '.github/workflows/release.yml',
    'Dockerfile',
    'packaging/linux/agentforge.service',
    'packaging/linux/install-service.sh',
    'packaging/windows/install-service.ps1',
    'packaging/windows/uninstall-service.ps1',
    'docs/UPGRADE.md'
)
foreach ($relative in $required) {
    $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $relative))
    if (-not $path.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release asset is missing: $relative" }
}
$systemd = Get-Content -LiteralPath (Join-Path $repositoryRoot 'packaging/linux/agentforge.service') -Raw
foreach ($control in @('NoNewPrivileges=true', 'ProtectSystem=strict', 'CapabilityBoundingSet=', 'WantedBy=default.target')) {
    if (-not $systemd.Contains($control, [StringComparison]::Ordinal)) { throw "systemd control is missing: $control" }
}
$docker = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Dockerfile') -Raw
if (-not $docker.Contains('USER $APP_UID', [StringComparison]::Ordinal) -or
    $docker.Contains('0.0.0.0', [StringComparison]::Ordinal)) { throw 'Container security defaults are invalid.' }
$workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/release.yml') -Raw
foreach ($control in @('attest-build-provenance', 'sbom: true', 'win-x64', 'linux-x64')) {
    if (-not $workflow.Contains($control, [StringComparison]::Ordinal)) { throw "Release workflow control is missing: $control" }
}
Write-Output '{"status":"valid"}'
