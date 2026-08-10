$ErrorActionPreference = 'Stop'

$patterns = @(
    'sk-[A-Za-z0-9_-]{20,}',
    'gh[pousr]_[A-Za-z0-9_]{20,}',
    'AKIA[0-9A-Z]{16}',
    'xox[baprs]-[A-Za-z0-9-]{15,}',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
)
$textExtensions = @(
    '.config', '.cs', '.csproj', '.json', '.md', '.props', '.ps1', '.sh',
    '.slnx', '.targets', '.toml', '.xml', '.yaml', '.yml'
)
$findings = [Collections.Generic.List[string]]::new()
$trackedFiles = @(git ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not enumerate tracked files for the secret scan.'
}

foreach ($relativePath in $trackedFiles) {
    $extension = [IO.Path]::GetExtension($relativePath)
    if ($extension -notin $textExtensions -or -not (Test-Path -LiteralPath $relativePath -PathType Leaf)) {
        continue
    }

    $lines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $relativePath))
    for ($index = 0; $index -lt $lines.Length; $index++) {
        foreach ($pattern in $patterns) {
            if ($lines[$index] -notmatch $pattern) {
                continue
            }

            $isDetectorSignature =
                $relativePath -eq 'src/AgentForge.Security/StructuredSensitiveDataRedactor.cs' -and
                $lines[$index].Contains('Contains("-----BEGIN', [StringComparison]::Ordinal)
            if (-not $isDetectorSignature) {
                $findings.Add("$relativePath`:$($index + 1): matches a credential signature")
            }
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Error $_ }
    throw "Secret scan found $($findings.Count) potential credential(s)."
}

Write-Output "Secret scan passed across $($trackedFiles.Count) tracked files."
