#requires -Version 7
<#
.SYNOPSIS
  Demonstrates the rig features shipped on 2026-06-14, driven entirely against the
  already-built unified MedDBase store (NO re-mine — queries only, plus a fast targeted
  test run). Each section prints the command it runs, then the output.

.DESCRIPTION
  Features exercised:
    1. Whole-store headline ......... derive totals over the unified 12-solution store
    2. Multi-solution storage ....... assembly registry + solution_membership tables
    3. Cross-solution stitching (F5)  Dapper effect fires from the audits solution
    4. Effect-rule fix .............. Xero read/write split (one client, two effects)
    5. F# project-ref fix ........... refs into the F# MedDBase.Pathways.DSL resolve
    6. Source-fidelity fixes (F1) ... candidate-symbol + query-clause capture, http-arg fallback
    7. Flag-whitelist fix ........... --merge / --no-tests accepted by `rig index`

.NOTES
  Read-only. The only thing that writes is section 1's `rig derive`, which refreshes the
  store's materialized effect/entry-point tables in <1s (it does NOT re-index source).
  Pass -NoPause to run straight through without the between-section prompts.
#>

param([switch]$NoPause)

$ErrorActionPreference = 'Stop'

# --- paths (edit here if your checkout differs) -----------------------------------------
$Store   = 'C:\Git\meddbase-analysis'                       # cwd that owns .rig\rig.db
$Db      = Join-Path $Store '.rig\rig.db'
$Rules   = Join-Path $Store 'rig.rules.json'
$CodeRig = 'C:\Git\coderig'                                  # for the test run
$Sqlite  = 'sqlite3'

# --- helpers ----------------------------------------------------------------------------
function Section([string]$n, [string]$title) {
    Write-Host ''
    Write-Host ('═' * 78) -ForegroundColor DarkCyan
    Write-Host (" {0}  {1}" -f $n, $title) -ForegroundColor Cyan
    Write-Host ('═' * 78) -ForegroundColor DarkCyan
}
function Cmd([string]$text) { Write-Host "`n  `$ $text" -ForegroundColor Yellow }
function Pause-Section { if (-not $NoPause) { Write-Host "`n  [enter to continue]" -ForegroundColor DarkGray; [void](Read-Host) } }

# --- preflight --------------------------------------------------------------------------
Section '0' 'Preflight — tool + store present'
Cmd 'rig  (no args -> usage)'
$null = Get-Command rig -ErrorAction Stop
if (-not (Test-Path $Db))    { throw "Store not found: $Db" }
if (-not (Test-Path $Rules)) { throw "Rules not found: $Rules" }
$dbInfo = Get-Item $Db
Write-Host ("  rig:    {0}" -f (Get-Command rig).Source) -ForegroundColor Green
Write-Host ("  store:  {0}  ({1:N0} MB, built {2})" -f $Db, ($dbInfo.Length/1MB), $dbInfo.LastWriteTime) -ForegroundColor Green
Set-Location $Store
Pause-Section

# --- 1. whole-store headline ------------------------------------------------------------
Section '1' 'Whole-store headline — derive over the unified 12-solution store'
Write-Host '  `derive` re-derives effects + entry points from the stored facts and prints totals.' -ForegroundColor DarkGray
Write-Host '  Fast (<1s): it reads facts, it does NOT re-index source.' -ForegroundColor DarkGray
Cmd 'rig derive --rules rig.rules.json'
rig derive --rules $Rules 2>&1 |
    Select-String -Pattern 'Effects re-derived|Observations|looped_|lock_held_across|transaction_spans|Entry points re-derived|Handoff entry' |
    ForEach-Object { Write-Host "  $_" }
Pause-Section

# --- 2. multi-solution storage ----------------------------------------------------------
Section '2' 'Multi-solution storage — assembly registry + solution membership (new tables)'
Write-Host '  One unified store; each assembly stored once and deduped by content-hash;' -ForegroundColor DarkGray
Write-Host '  solutions are membership views over that shared assembly set.' -ForegroundColor DarkGray
Cmd 'sqlite3 rig.db "SELECT COUNT(*) FROM assemblies; SELECT solutions, rows FROM (membership)"'
$asmCount = & $Sqlite $Db "SELECT COUNT(*) FROM assemblies;"
$memAgg   = & $Sqlite $Db "SELECT COUNT(DISTINCT SolutionPath) || ' solutions, ' || COUNT(*) || ' membership rows' FROM solution_membership;"
Write-Host ("  distinct assemblies : {0}" -f $asmCount) -ForegroundColor Green
Write-Host ("  membership          : {0}" -f $memAgg) -ForegroundColor Green
Write-Host ("  (gap = {0} shared assemblies deduped across solutions, e.g. Audits.Contracts in master + audits)" -f ([int]($memAgg -replace '^\d+ solutions, (\d+).*','$1') - [int]$asmCount)) -ForegroundColor DarkGray
Cmd 'sqlite3 rig.db "SELECT solution, COUNT(*) ... GROUP BY solution"'
& $Sqlite $Db "SELECT REPLACE(SolutionPath,'C:\Git\meddbase-main-application\','') || '  ->  ' || COUNT(*) FROM solution_membership GROUP BY SolutionPath ORDER BY COUNT(*) DESC;" |
    ForEach-Object { Write-Host "  $_" }
Pause-Section

# --- 3. cross-solution stitching (F5) ---------------------------------------------------
Section '3' 'Cross-solution stitching (F5) — Dapper effect fires from the audits solution'
Write-Host '  audits was a SEPARATE solution; in the unified store its calls stitch by DocID.' -ForegroundColor DarkGray
Write-Host '  Dapper was invisible before the merge — now the detector fires across the boundary.' -ForegroundColor DarkGray
Cmd 'rig reaches "Audits.AuditsRepository.SubmitEvent" --rules rig.rules.json'
rig reaches 'Audits.AuditsRepository.SubmitEvent' --rules $Rules 2>&1 |
    Select-String -Pattern 'From:|Direct effects|dapper|db_connection' |
    ForEach-Object { Write-Host "  $_" }
Pause-Section

# --- 4. effect-rule fix: Xero read/write split ------------------------------------------
Section '4' 'Effect-rule fix — Xero read vs write (one client, two distinct effects)'
Write-Host '  The old single Xero rule was wrong; split into xero:read (6 Get*) + xero:write (11 Create/Update/Delete*).' -ForegroundColor DarkGray
Cmd 'rig reaches "Xero2ClientIO.GetInvoices" --rules rig.rules.json     # expect: xero read'
rig reaches 'Xero2ClientIO.GetInvoices' --rules $Rules 2>&1 |
    Select-String -Pattern 'xero (read|write)' | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
Cmd 'rig reaches "Xero2ClientIO.CreateInvoice" --rules rig.rules.json   # expect: xero write'
rig reaches 'Xero2ClientIO.CreateInvoice' --rules $Rules 2>&1 |
    Select-String -Pattern 'xero (read|write)' | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
Pause-Section

# --- 5. F# project-reference fix --------------------------------------------------------
Section '5' 'F# project-ref fix — refs into the F# MedDBase.Pathways.DSL resolve'
Write-Host '  rig''s C# workspace cannot compile the F# .fsproj; we substitute its built DLL as a' -ForegroundColor DarkGray
Write-Host '  metadata reference, so cross-language edges no longer drop as CS0012.' -ForegroundColor DarkGray
Cmd 'sqlite3 rig.db "SELECT COUNT(*) FROM reference_facts WHERE TargetAssembly=''MedDBase.Pathways.DSL''"'
$dslRefs = & $Sqlite $Db "SELECT COUNT(*) FROM reference_facts WHERE TargetAssembly='MedDBase.Pathways.DSL';"
Write-Host ("  references into MedDBase.Pathways.DSL : {0}  (were CS0012-dropped before the fix)" -f $dslRefs) -ForegroundColor Green
Pause-Section

# --- 6. source-fidelity fixes (F1) ------------------------------------------------------
Section '6' 'Source-fidelity fixes (F1) — targeted test run'
Write-Host '  F1a: http_argument resource falls back to receiver/declaring type when no template.' -ForegroundColor DarkGray
Write-Host '  F1b: invocations resolved only to a candidate symbol, and inside query clauses, are now captured.' -ForegroundColor DarkGray
Cmd 'dotnet run -c Release --project tests/Rig.Tests --treenode-filter "/*/*/FactExtractorCaptureTests/*"'
Push-Location $CodeRig
try {
    dotnet run -c Release --project tests/Rig.Tests --treenode-filter '/*/*/FactExtractorCaptureTests/*' 2>&1 |
        Select-String -Pattern 'Passed!|Failed!|total:|succeeded:|failed:|error' | ForEach-Object { Write-Host "  $_" }
    Cmd 'dotnet run -c Release --project tests/Rig.Tests --treenode-filter "/*/*/FactDerivationTests/*"'
    dotnet run -c Release --project tests/Rig.Tests --treenode-filter '/*/*/FactDerivationTests/*' 2>&1 |
        Select-String -Pattern 'Passed!|Failed!|total:|succeeded:|failed:|error' | ForEach-Object { Write-Host "  $_" }
}
finally { Pop-Location }
Pause-Section

# --- 7. flag surface (whitelist fix + unification) --------------------------------------
Section '7' 'Flag surface — --merge / --include-tests accepted; mode conflicts rejected'
Write-Host '  --merge/--include-tests are whitelisted (were once rejected up front); test projects now' -ForegroundColor DarkGray
Write-Host '  excluded by default; mutually-exclusive tree modes are caught before any store access.' -ForegroundColor DarkGray
Cmd 'rig   (usage shows the unified flags)'
rig 2>&1 | Select-String -Pattern '--merge|--include-tests|--orphans' | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
Cmd 'rig index C:/does-not-exist.slnx --merge --include-tests   # known flags -> clean "Failed to load", not "Unknown option"'
$out = rig index 'C:/does-not-exist.slnx' --merge --include-tests 2>&1
if ($out -match 'Unknown option') { Write-Host "  REGRESSED: rejected as unknown option" -ForegroundColor Red }
else { Write-Host ("  OK: flags accepted; failed later on the missing solution -> {0}" -f (($out | Select-String 'Failed to load') -replace '^\s+','')) -ForegroundColor Green }
Cmd 'rig tree X --full --summary   # mutually-exclusive modes rejected up front'
$conflict = rig tree X --full --summary 2>&1
Write-Host ("  {0}" -f (($conflict | Select-String "can't be combined") -replace '^\s+','')) -ForegroundColor Green

Pause-Section

# --- 8. tree views + the new --full rendering -------------------------------------------
Section '8' 'Tree views — incl. the new --full maximal-fidelity rendering'
Write-Host '  `tree` walks calls from a method toward effects. Flavors: --full (everything), --effects' -ForegroundColor DarkGray
Write-Host '  (effectful nodes only), --summary (rollup counts), default (inline {tags}), --depth to cap.' -ForegroundColor DarkGray

Cmd 'rig tree "Audits.AuditsRepository.SubmitEvent" --full --rules rig.rules.json   # NEW: effects as ⚡ leaves + unresolved lib calls as ·'
rig tree 'Audits.AuditsRepository.SubmitEvent' --full --rules $Rules 2>&1 | ForEach-Object { Write-Host "  $_" }

Cmd 'rig tree "Xero2ClientIO.CreateInvoice" --effects --rules rig.rules.json   # effect leaf'
rig tree 'Xero2ClientIO.CreateInvoice' --effects --rules $Rules 2>&1 | ForEach-Object { Write-Host "  $_" }

Cmd 'rig tree "Xero2ClientIO.CreateInvoice" --depth 2 --rules rig.rules.json   # skeleton + ↺seen cycle marker'
rig tree 'Xero2ClientIO.CreateInvoice' --depth 2 --rules $Rules 2>&1 | ForEach-Object { Write-Host "  $_" }

Cmd 'rig tree "Audits.AuditsRepository.SubmitEvent" --effects --rules rig.rules.json   # cross-solution: dapper + db_connection + throw⚠️'
rig tree 'Audits.AuditsRepository.SubmitEvent' --effects --rules $Rules 2>&1 | ForEach-Object { Write-Host "  $_" }

Cmd 'rig tree "Audits.AuditsRepository.SubmitEvent" --summary --depth 3 --rules rig.rules.json   # rollup'
rig tree 'Audits.AuditsRepository.SubmitEvent' --summary --depth 3 --rules $Rules 2>&1 | ForEach-Object { Write-Host "  $_" }
Pause-Section

Write-Host ''
Write-Host ('═' * 78) -ForegroundColor DarkCyan
Write-Host ' Demo complete.' -ForegroundColor Cyan
Write-Host ('═' * 78) -ForegroundColor DarkCyan
