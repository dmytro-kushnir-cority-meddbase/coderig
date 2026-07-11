#!/usr/bin/env pwsh
# Code coverage for the TUnit / Microsoft.Testing.Platform test suite.
#
# NON-TRIVIAL because rig's tests run on Microsoft.Testing.Platform (MTP), NOT vstest — so the usual
# `dotnet test --collect "XPlat Code Coverage"` (a vstest data collector) does nothing here. MTP instead
# exposes a `--coverage` flag on the test app itself, provided by the Microsoft.Testing.Extensions.
# CodeCoverage package (referenced by tests/Rig.Tests). Scoping is automatic: only the first-party Rig.*
# assemblies ship PDBs into the test output, so MS coverage instruments exactly those (third-party
# packages and the test assembly are excluded with no settings file needed).
#
# Usage:  pwsh -File scripts/coverage.ps1 [-NoBuild]
# Output: tests/Rig.Tests/bin/.../TestResults/coverage.cobertura.xml  (+ a per-assembly summary on stdout)
[CmdletBinding()]
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'tests/Rig.Tests/Rig.Tests.csproj'

$runArgs = @('run', '--project', $proj)
if ($NoBuild) { $runArgs += '--no-build' }
$runArgs += @('--', '--coverage', '--coverage-output-format', 'cobertura', '--coverage-output', 'coverage.cobertura.xml')

& dotnet @runArgs
if ($LASTEXITCODE -ne 0) { throw "test run failed (exit $LASTEXITCODE)" }

$cov = Get-ChildItem -Recurse -Filter 'coverage.cobertura.xml' (Join-Path $repo 'tests/Rig.Tests/bin') |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $cov) { throw 'coverage.cobertura.xml not found' }

[xml]$c = Get-Content $cov.FullName
Write-Host ''
Write-Host ('Coverage   line {0:P1}   branch {1:P1}' -f [double]$c.coverage.'line-rate', [double]$c.coverage.'branch-rate')
$c.coverage.packages.package | Sort-Object { [double]$_.'line-rate' } -Descending | ForEach-Object {
    Write-Host ('  {0,7:P1} line  {1,7:P1} branch   {2}' -f [double]$_.'line-rate', [double]$_.'branch-rate', $_.name)
}
Write-Host ''
Write-Host "cobertura: $($cov.FullName)"
