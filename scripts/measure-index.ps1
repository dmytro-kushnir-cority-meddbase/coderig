#requires -Version 7
<#
.SYNOPSIS
  Interactive rig-index measurement: live milestone output (no "Saved N/M" spam) + a per-phase
  timing summary. Each run uses a throwaway working dir, so it never touches a real .rig store.

.DESCRIPTION
  Phase splits are derived from wall-clock stamps at rig's own milestone lines:
    BuildWS     = closure resolve + all design-time builds (start -> "Loaded N C# project(s)")
    Analyze     = the per-project compile/read/extract pass ("Loaded..." -> "Analysis phase done")
    Save        = fact write ("Analysis phase done" -> "Save phase done")
    Graph       = derived edge/FTS build ("Save phase done" -> "Graph: ...")
  BuildWS is the noisy one (out-of-process MSBuild) — use -Runs 3+ and read the medians.

.EXAMPLE
  ./scripts/measure-index.ps1                      # MedDBase Pages closure, 1 run
  ./scripts/measure-index.ps1 -Runs 3 -Label fused # 3 runs, median row
  ./scripts/measure-index.ps1 -Cli D:\baseline\Rig.Cli.dll -Label baseline   # A/B a second build
  ./scripts/measure-index.ps1 -From C:\path\Other.csproj -Rules ''           # different entry, no rules
#>
[CmdletBinding()]
param(
    [string]$Solution = 'C:\git\meddbase-main-application-2\MedDBase.slnx',
    [string]$From     = 'C:\git\meddbase-main-application-2\src\main\MedDBase.Pages\MedDBase.Pages.csproj',
    [string]$Rules    = 'C:\git\meddbase-analysis\rig.rules.json',
    [string]$Cli      = 'C:\Git\coderig\src\Rig.Cli\bin\Debug\net10.0\Rig.Cli.dll',
    [int]$Runs        = 1,
    [string]$Label    = 'run'
)

function Invoke-OneRun([int]$n) {
    $wd = Join-Path $env:TEMP ('rig-measure-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $wd | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $m = [ordered]@{ closure = 0; projects = 0; build = 0.0; analysis = 0.0; save = 0.0; graph = 0.0; symbols = 0; refs = 0; errors = 0 }

    Write-Host "`n=== $Label #$n  (wd=$wd) ===" -ForegroundColor Cyan
    $cliArgs = @('index', $Solution, '--from', $From, '--time')
    if ($Rules) { $cliArgs += @('--rules', $Rules) }

    Push-Location $wd
    try {
        & dotnet $Cli @cliArgs *>&1 | ForEach-Object {
            $line = ("$_" -replace '^Progress:\s*', '')
            $t = $sw.Elapsed.TotalSeconds
            switch -Regex ($line) {
                'Scoped to (\d+) project' { $m.closure = [int]$Matches[1] }
                'Loaded (\d+) C# project' { $m.build = $t; $m.projects = [int]$Matches[1] }
                'Analysis phase done'     { $m.analysis = $t }
                'Save phase done'         { $m.save = $t }
                'Symbols:\s*(\d+)'        { $m.symbols = [int]$Matches[1] }
                'References:\s*(\d+)'     { $m.refs = [int]$Matches[1] }
                '(\d+) compilation error' { $m.errors = [int]$Matches[1] }
                'Graph:.* in '            { $m.graph = $t }
            }
            # live milestones only — drop the per-batch "Saved N/M" spam
            if ($line -notmatch 'Saved \d' -and
                $line -match 'phase|project \d+0?/|Loaded|Scoped|Symbols:|References:|Graph:|error|Warning|Wired') {
                Write-Host ("{0,7:n1}s  {1}" -f $t, $line)
            }
        }
    }
    finally { Pop-Location }

    [pscustomobject]@{
        Run         = $n
        Projects    = $m.projects
        BuildWS     = [math]::Round($m.build, 1)
        Analyze     = [math]::Round($m.analysis - $m.build, 1)
        AnalysisTot = [math]::Round($m.analysis, 1)
        Save        = [math]::Round($m.save - $m.analysis, 1)
        Graph       = [math]::Round($m.graph - $m.save, 1)
        Total       = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Symbols     = $m.symbols
        Refs        = $m.refs
    }
}

$results = foreach ($i in 1..$Runs) { Invoke-OneRun $i }

Write-Host "`n=== summary: $Label ($Runs run(s)) ===" -ForegroundColor Green
$results | Format-Table -AutoSize

if ($Runs -gt 1) {
    Write-Host 'medians (s):' -ForegroundColor Green
    foreach ($c in 'BuildWS', 'Analyze', 'AnalysisTot', 'Save', 'Graph', 'Total') {
        $v = @($results.$c | Sort-Object)
        '  {0,-12} {1}' -f $c, $v[[int][math]::Floor($v.Count / 2)]
    }
}
