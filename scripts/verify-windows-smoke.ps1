$ErrorActionPreference = 'Stop'

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$dataDirectory = Join-Path $temporaryRoot ('agentforge-smoke-' + [Guid]::NewGuid().ToString('N'))
$dataDirectory = [IO.Directory]::CreateDirectory($dataDirectory).FullName
$leafName = [IO.Path]::GetFileName($dataDirectory)
if (-not $dataDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $leafName.StartsWith('agentforge-smoke-', [StringComparison]::Ordinal)) {
    throw 'Refusing to use an unsafe smoke-test directory.'
}

$previousDataDirectory = $env:AgentForge__Installation__DataDirectory
$previousPooling = $env:AgentForge__Persistence__EnableConnectionPooling
$env:AgentForge__Installation__DataDirectory = $dataDirectory
$env:AgentForge__Persistence__EnableConnectionPooling = 'false'
$stdout = Join-Path $dataDirectory 'host.out.log'
$stderr = Join-Path $dataDirectory 'host.err.log'
$process = $null

try {
    $process = Start-Process dotnet `
        -ArgumentList @('src/AgentForge.Host/bin/Release/net10.0/AgentForge.Host.dll') `
        -WorkingDirectory (Get-Location) `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru `
        -WindowStyle Hidden

    $liveStatus = 0
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        try {
            $liveStatus = (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5047/health/live -TimeoutSec 1).StatusCode
            if ($liveStatus -eq 200) {
                break
            }
        }
        catch {
            # Startup is expected to refuse connections until migration is complete.
        }

        Start-Sleep -Milliseconds 250
    }

    if ($liveStatus -ne 200) {
        Get-Content $stdout, $stderr -ErrorAction SilentlyContinue
        throw "Windows host did not become live (status=$liveStatus)."
    }

    $setupStatus = (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5047/api/v1/setup/status).StatusCode
    $sandboxResponse = Invoke-RestMethod -Method Get -Uri http://127.0.0.1:5047/api/v1/sandbox/capabilities
    $previousEndpoint = $env:AGENTFORGE_ENDPOINT
    $env:AGENTFORGE_ENDPOINT = 'http://127.0.0.1:5047'
    try {
        $cliOutput = & dotnet 'src/AgentForge.Cli/bin/Release/net10.0/agentforge.dll' sandbox capabilities
        if ($LASTEXITCODE -ne 0) {
            throw "Sandbox capability CLI failed (exit=$LASTEXITCODE)."
        }

        $cliSandbox = $cliOutput | ConvertFrom-Json
    }
    finally {
        $env:AGENTFORGE_ENDPOINT = $previousEndpoint
    }

    try {
        Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5047/api/v1/runtime/ping -ErrorAction Stop | Out-Null
        $runtimeStatus = 200
    }
    catch {
        $runtimeStatus = [int]$_.Exception.Response.StatusCode
    }

    Write-Output "live=$liveStatus setup=$setupStatus runtime=$runtimeStatus sandbox=$($sandboxResponse.kind) cliSandbox=$($cliSandbox.kind)"
    if ($setupStatus -ne 200 -or $runtimeStatus -ne 503 -or
        $sandboxResponse.kind -ne 'RestrictedHost' -or $cliSandbox.kind -ne 'RestrictedHost') {
        throw 'The host did not retain its fail-closed setup posture.'
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }

    $env:AgentForge__Installation__DataDirectory = $previousDataDirectory
    $env:AgentForge__Persistence__EnableConnectionPooling = $previousPooling

    if (Test-Path -LiteralPath $dataDirectory) {
        $verifiedPath = [IO.Path]::GetFullPath($dataDirectory)
        $verifiedLeaf = [IO.Path]::GetFileName($verifiedPath)
        if ($verifiedPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            $verifiedLeaf.StartsWith('agentforge-smoke-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $verifiedPath -Recurse -Force
        }
        else {
            throw 'Refusing to clean up an unsafe smoke-test directory.'
        }
    }
}
