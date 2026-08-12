[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',
    [ValidateSet('win-x64', 'linux-x64')]
    [string[]]$RuntimeIdentifiers = @('win-x64', 'linux-x64'),
    [string]$OutputDirectory = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/release'))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$prefix = $allowedRoot + [IO.Path]::DirectorySeparatorChar
if ($outputRoot -ne $allowedRoot -and -not $outputRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must remain inside artifacts/release.'
}
if (Test-Path -LiteralPath $outputRoot) {
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $outputRoot).Path)
    if ($resolved -ne $outputRoot) { throw 'Release output resolved to an unexpected path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null

$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40,64}$') { throw 'Unable to resolve the release commit.' }
$created = (& git -C $repositoryRoot show -s --format=%cI $commit).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve the release timestamp.' }
$projects = [ordered]@{
    host = 'src/AgentForge.Host/AgentForge.Host.csproj'
    cli = 'src/AgentForge.Cli/AgentForge.Cli.csproj'
    worker = 'src/AgentForge.PluginWorker/AgentForge.PluginWorker.csproj'
}

Push-Location $repositoryRoot
try {
    foreach ($rid in $RuntimeIdentifiers) {
        $ridRoot = Join-Path $outputRoot $rid
        New-Item -ItemType Directory -Path $ridRoot | Out-Null
        foreach ($component in $projects.GetEnumerator()) {
            & dotnet restore $component.Value --locked-mode
            if ($LASTEXITCODE -ne 0) { throw "Locked restore failed for $($component.Key)/$rid." }
            $destination = Join-Path $ridRoot $component.Key
            & dotnet publish $component.Value --configuration Release --runtime $rid --self-contained true `
                --no-restore --output $destination -p:PublishSingleFile=true `
                -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
                -p:Version=$Version -p:ContinuousIntegrationBuild=true
            if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($component.Key)/$rid." }
        }
        Copy-Item -LiteralPath 'packaging/common/README.md' -Destination (Join-Path $ridRoot 'README.md')
        if ($rid -eq 'win-x64') {
            Copy-Item -LiteralPath 'packaging/windows/appsettings.Production.json' -Destination (Join-Path $ridRoot 'host/appsettings.Production.json')
            Copy-Item -LiteralPath 'packaging/windows/install-service.ps1' -Destination (Join-Path $ridRoot 'install-service.ps1')
            Copy-Item -LiteralPath 'packaging/windows/uninstall-service.ps1' -Destination (Join-Path $ridRoot 'uninstall-service.ps1')
            $archive = Join-Path $outputRoot "AgentForge-$Version-win-x64.zip"
            $format = 'zip'
        }
        else {
            Copy-Item -LiteralPath 'packaging/linux/appsettings.Production.json' -Destination (Join-Path $ridRoot 'host/appsettings.Production.json')
            Copy-Item -LiteralPath 'packaging/linux/agentforge.service' -Destination (Join-Path $ridRoot 'agentforge.service')
            Copy-Item -LiteralPath 'packaging/linux/install-service.sh' -Destination (Join-Path $ridRoot 'install-service.sh')
            $archive = Join-Path $outputRoot "AgentForge-$Version-linux-x64.tar.gz"
            $format = 'tar.gz'
        }
        & dotnet run --project tools/AgentForge.Release/AgentForge.Release.csproj --configuration Release --no-restore -- `
            archive --source-directory $ridRoot --output-path $archive --format $format --created $created
        if ($LASTEXITCODE -ne 0) { throw "Archive creation failed for $rid." }
    }
    & dotnet run --project tools/AgentForge.Release/AgentForge.Release.csproj --configuration Release --no-restore -- `
        manifest --release-directory $outputRoot --repository-root $repositoryRoot --version $Version `
        --commit $commit --created $created
    if ($LASTEXITCODE -ne 0) { throw 'Release manifest creation failed.' }
    & dotnet run --project tools/AgentForge.Release/AgentForge.Release.csproj --configuration Release --no-restore -- `
        verify --release-directory $outputRoot
    if ($LASTEXITCODE -ne 0) { throw 'Release manifest verification failed.' }
}
finally {
    Pop-Location
}
