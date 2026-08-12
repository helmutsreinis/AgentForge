[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ResultsDirectory,
    [double]$MinimumOverallPercent = 82,
    [double]$MinimumCriticalPercent = 90
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ResultsDirectory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Coverage directory does not exist: $root"
}

$reports = @(Get-ChildItem -LiteralPath $root -Recurse -Filter coverage.cobertura.xml -File)
if ($reports.Count -lt 5) {
    throw "At least five coverage reports are required; found $($reports.Count)."
}

$lines = @{}
foreach ($report in $reports) {
    [xml]$document = [IO.File]::ReadAllText($report.FullName)
    foreach ($class in $document.coverage.packages.package.classes.class) {
        $path = ([string]$class.filename).Replace('/', '\')
        $sourceIndex = $path.IndexOf('AgentForge.', [StringComparison]::Ordinal)
        if ($sourceIndex -lt 0) { continue }
        $path = $path.Substring($sourceIndex)
        if ($path -match '^AgentForge\.(UnitTests|IntegrationTests|ArchitectureTests|SecurityTests|CrossPlatformTests|EndToEndTests)' -or
            $path -match '\\(Migrations|Entities)\\' -or $path.EndsWith('.Designer.cs', [StringComparison]::Ordinal)) {
            continue
        }

        foreach ($line in $class.lines.line) {
            $key = "$path`:$($line.number)"
            $hits = [int]$line.hits
            if (-not $lines.ContainsKey($key) -or $hits -gt $lines[$key].Hits) {
                $lines[$key] = [pscustomobject]@{ Path = $path; Hits = $hits }
            }
        }
    }
}

if ($lines.Count -lt 10000) {
    throw "Coverage contained only $($lines.Count) product lines; the report set is incomplete."
}

function Assert-Coverage([string]$Name, [object[]]$Values, [double]$Minimum) {
    if ($Values.Count -eq 0) { throw "Coverage group '$Name' selected no product lines." }
    $covered = @($Values | Where-Object Hits -gt 0).Count
    $percent = [Math]::Round(100 * $covered / $Values.Count, 2)
    Write-Output "$Name coverage: $covered/$($Values.Count) ($percent%, minimum $Minimum%)."
    if ($percent -lt $Minimum) {
        throw "$Name line coverage $percent% is below the required $Minimum%."
    }
}

$productLines = @($lines.Values)
Assert-Coverage 'Overall product' $productLines $MinimumOverallPercent

$criticalGroups = [ordered]@{
    'Policy and approval' = 'Security\\.*(Policy|Authorization|Approval)'
    'State machines' = 'StateMachine|StateMachineRecords|GovernanceRecords|LearningRecords|CandidateRecords|SkillBundleRecords'
    'Audit and trajectory' = 'Audit|Trajectory'
    'Promotion and rollback' = 'Governance|Promotion|Rollback|Restorer'
}
foreach ($group in $criticalGroups.GetEnumerator()) {
    $selected = @($productLines | Where-Object Path -match $group.Value)
    Assert-Coverage $group.Key $selected $MinimumCriticalPercent
}
