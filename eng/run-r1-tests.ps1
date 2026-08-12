[CmdletBinding()]
param(
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '..\TestResults\r1-windows'),
    [string]$DotNetCommand = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$results = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Force -Path $results | Out-Null
Push-Location $repositoryRoot
try {
    & $DotNetCommand restore AgentForge.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $DotNetCommand build AgentForge.slnx --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $projects = @(
        'tests/AgentForge.UnitTests/AgentForge.UnitTests.csproj',
        'tests/AgentForge.IntegrationTests/AgentForge.IntegrationTests.csproj',
        'tests/AgentForge.ArchitectureTests/AgentForge.ArchitectureTests.csproj',
        'tests/AgentForge.SecurityTests/AgentForge.SecurityTests.csproj',
        'tests/AgentForge.CrossPlatformTests/AgentForge.CrossPlatformTests.csproj',
        'tests/AgentForge.EndToEndTests/AgentForge.EndToEndTests.csproj',
        'spikes/AgentFrameworkSpike/AgentFrameworkSpike.csproj'
    )
    foreach ($project in $projects) {
        $name = [IO.Path]::GetFileNameWithoutExtension($project)
        & $DotNetCommand test $project --configuration Release --no-build `
            --logger "trx;LogFileName=$name.trx" --results-directory $results
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}
