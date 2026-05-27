param(
    [switch]$Check
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

Push-Location $repoRoot
try {
    dotnet tool restore

    if ($Check) {
        dotnet csharpier check .
    }
    else {
        dotnet csharpier format .
    }
}
finally {
    Pop-Location
}
