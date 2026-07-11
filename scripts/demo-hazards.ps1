#requires -Version 7
<#
.SYNOPSIS
  Demo of rig's Hazards detection layer (built 2026-06-21): the third analysis layer on top of
  Effects + Entry points. Surfaces pattern findings — race_window (TOCTOU/lost-update),
  lazy_init_race, n_plus_1, unserializable_payload, dual_write — as disclosed CANDIDATES
  (never verdicts), named / counted / confidence-tiered, consumable by an LLM reviewer.

.DESCRIPTION
  1. Rebuilds + reinstalls the global `rig` tool from current source. REQUIRED unless you've already
     reinstalled since the last commit: the Hazards view, dual_write, and impact hazard-delta are
     query-side code committed AFTER the last `mini-ci` pack, so a stale tool won't render them.
  2. Runs `rig derive` against the indexed MedDBase store and shows the Hazards summary + sample sites,
     computed from the machine-readable `hazard` tsv rows.
  3. Drills into ONE representative finding with `rig tree <ep> --hazards` (the inline per-EP surface).
  4. Optionally runs the per-EP `rig impact` hazard-delta if two store refs are supplied.

  Hazards are an over-approximation: they narrow where a human/agent looks, they do not prove a bug.

.PARAMETER RepoDir       coderig source root (has scripts/mini-ci.ps1).
.PARAMETER AnalysisDir   meddbase-analysis dir (holds .rig store + rig.rules.json + deployments.json).
                         All `rig` queries run from here.
.PARAMETER SkipBuild     Skip the rebuild+reinstall (only if the installed tool is already current).
.PARAMETER ImpactBase    Optional base store ref (sha / short-sha / store-id) for the impact demo.
.PARAMETER ImpactHead    Optional head store ref for the impact demo. Both must be indexed with the
                         CURRENT extraction (reads-as-effects etc.) or the delta is misleading.

.EXAMPLE
  pwsh -File scripts/demo-hazards.ps1
.EXAMPLE
  pwsh -File scripts/demo-hazards.ps1 -SkipBuild -ImpactBase 378635d0d4df -ImpactHead ae082702cf4c-dirty
#>
param(
    [string]$RepoDir = 'C:\Git\coderig',
    [string]$AnalysisDir = 'C:\Git\meddbase-analysis',
    [switch]$SkipBuild,
    [string]$ImpactBase,
    [string]$ImpactHead
)

$ErrorActionPreference = 'Stop'

function Section($title) { Write-Host "`n=== $title ===" -ForegroundColor Cyan }
function Note($msg) { Write-Host "  $msg" -ForegroundColor DarkGray }

# --- 1. Rebuild + reinstall the global tool (the Hazards surfaces are post-last-pack code) -----------
if (-not $SkipBuild) {
    Section 'Rebuild + reinstall rig from current source (scripts/mini-ci.ps1)'
    Note 'csharpier + build + all tests + pack + reinstall. ~1-2 min.'
    & pwsh -File (Join-Path $RepoDir 'scripts/mini-ci.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'mini-ci failed - fix before demoing.' }
}
else {
    Note 'Skipping rebuild (-SkipBuild). If the Hazards section is empty below, the installed tool is stale - rerun without -SkipBuild.'
}

Write-Host "`nrig: $((rig --version) 2>$null | Select-Object -Last 1)" -ForegroundColor DarkGray

# All `rig` queries run from the analysis dir (it supplies the store + rules + deployment map).
Push-Location $AnalysisDir
try {
    # --- 2. Derive once; pull the machine-readable hazard rows (authoritative for counts/samples) -----
    Section 'rig derive — deriving effects + hazards over the whole store'
    $tsv = rig derive --format tsv 2>$null

    $hazards = $tsv |
        Where-Object { $_ -like "hazard`t*" } |
        ForEach-Object {
            $p = $_ -split "`t"
            [pscustomobject]@{
                Type       = $p[1]
                Confidence = $p[2]
                Reason     = $p[3]
                Cell       = $p[4]
                Enclosing  = $p[5]
                File       = $p[6]
                Line       = $p[7]
            }
        }

    if (-not $hazards) {
        Write-Host '  No `hazard` tsv rows found.' -ForegroundColor Red
        Note 'The installed tool predates the Hazards view. Rerun WITHOUT -SkipBuild.'
        return
    }

    # Context: the effect families the hazards stand on.
    $effects = $tsv | Where-Object { $_ -like "effect`t*" } | ForEach-Object { ($_ -split "`t") }
    $reads = ($effects | Where-Object { $_[1] -eq 'shared_state' -and $_[2] -eq 'read' }).Count
    $mut = ($effects | Where-Object { $_[1] -eq 'shared_state' -and $_[2] -eq 'mutate' }).Count
    Note "underlying effects: shared_state:read=$reads  shared_state:mutate=$mut  (the TOCTOU check + act)"

    # --- 3. Hazard summary: named, counted, confidence-tiered (the LLM-facing triage list) -----------
    Section 'Hazards — by type and confidence'
    $hazards |
        Group-Object Type |
        Sort-Object Count -Descending |
        ForEach-Object {
            $tiers = ($_.Group | Group-Object Confidence |
                Sort-Object @{ e = { @{ high = 0; medium = 1; low = 2 }[$_.Name] } } |
                ForEach-Object { "$($_.Name) $($_.Count)" }) -join ', '
            '{0,-24} {1,5}   ({2})' -f $_.Name, $_.Count, $tiers
        }
    Note "total hazard findings: $($hazards.Count)"

    # --- 4. Sample sites per hazard type (the 'full deal' for a few, on demand) -----------------------
    Section 'Sample sites (up to 3 per hazard type)'
    foreach ($g in ($hazards | Group-Object Type | Sort-Object Name)) {
        Write-Host ("  {0}" -f $g.Name) -ForegroundColor Yellow
        $g.Group | Select-Object -First 3 | ForEach-Object {
            $file = if ($_.File) { Split-Path $_.File -Leaf } else { '?' }
            '    [{0,-6}] {1}  <- {2}  ({3}:{4})  {5}' -f $_.Confidence, $_.Cell, $_.Enclosing, $file, $_.Line, $_.Reason
        }
    }

    # --- 5. Drill into ONE entry point: the inline tree surface (the 'full deal' for one EP) ----------
    Section 'rig tree <ep> --hazards — inline drill-in for one entry point'
    # Pick a representative race_window (else any) finding and reduce its enclosing DocID to the dotted
    # Type.Method pattern `tree` matches (strip the `M:` kind prefix + the parameter list, keep the last
    # two dotted segments). The drill-in re-derives THIS closure with the static-field refs threaded in,
    # so it shows the field-fed shared_state:read/mutate effects a plain `tree` omits.
    $drill = ($hazards | Where-Object Type -eq 'race_window' | Select-Object -First 1)
    if (-not $drill) { $drill = $hazards | Select-Object -First 1 }
    if ($drill -and $drill.Enclosing) {
        $sig = $drill.Enclosing -replace '^M:', '' -replace '\(.*$', ''
        $parts = $sig -split '\.'
        $pattern = if ($parts.Count -ge 2) { ($parts[-2..-1]) -join '.' } else { $sig }
        Note "drilling into '$pattern' (carries a $($drill.Type)); ⚠ marks each hazard-bearing node + a summary follows"
        rig tree $pattern --hazards 2>$null | Select-Object -First 12
    }
    else {
        Note 'No finding with an enclosing method to drill into.'
    }

    # --- 6. Optional: per-EP hazard delta between two indexed commits (the CI surface) ----------------
    Section 'rig impact — per-EP hazard delta (base -> head)'
    if ($ImpactBase -and $ImpactHead) {
        Note "diffing $ImpactBase -> $ImpactHead (hazard +/- lines below; full effect/structural diff omitted)"
        rig impact --base $ImpactBase --head $ImpactHead 2>$null |
            Select-String -Pattern 'hazard delta|hazard ', -SimpleMatch
    }
    else {
        Note 'Skipped - pass -ImpactBase <ref> -ImpactHead <ref> (both indexed with current extraction).'
        Note 'It reports per EP: + hazard race_window (high) <cell>  /  - hazard ...  base->head.'
        Note 'Stores available:'
        Get-ChildItem (Join-Path $AnalysisDir '.rig') -Directory -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name | ForEach-Object { Note "    $_" }
    }

    Write-Host "`nDemo complete. Hazards are disclosed candidates for review, not verdicts." -ForegroundColor Green
}
finally {
    Pop-Location
}
