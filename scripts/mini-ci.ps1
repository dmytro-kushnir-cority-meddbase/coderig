param(
    [string]$Configuration = "Release",
    [string]$ToolVersion = "",
    [switch]$SkipTests,
    [switch]$SkipToolInstall
)

$ErrorActionPreference = "Stop"

function Get-HostRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $archPart = switch ($arch) {
        "x64"   { "x64" }
        "x86"   { "x86" }
        "arm64" { "arm64" }
        "arm"   { "arm" }
        default { "x64" }
    }
    if ($IsWindows) { return "win-$archPart" }
    if ($IsMacOS)   { return "osx-$archPart" }
    if ($IsLinux)   { return "linux-$archPart" }
    # Windows PowerShell 5.1 has no $IsWindows; assume Windows
    return "win-$archPart"
}

$HostRid = Get-HostRid

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solution = Join-Path $repoRoot "RuntimeIntelligenceGraph.slnx"
$toolProject = Join-Path $repoRoot "src/Rig.Cli/Rig.Cli.csproj"
$packageOutput = Join-Path $repoRoot ".rig-nupkg"

if ([string]::IsNullOrWhiteSpace($ToolVersion)) {
    [xml]$toolProjectXml = Get-Content $toolProject
    $baseVersion = $toolProjectXml.Project.PropertyGroup.Version |
        Select-Object -First 1
    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    $ToolVersion = "$baseVersion-ci.$stamp"
}

New-Item -ItemType Directory -Force -Path $packageOutput | Out-Null

Push-Location $repoRoot
try {
    dotnet tool restore
    # Native tools report failure via EXIT CODE, not a terminating error, so $ErrorActionPreference="Stop"
    # does NOT halt on them (same trap as the test gate below). Gate the CHEAP checks explicitly so a
    # formatting drift or compile break fails in seconds — BEFORE the expensive build/test/pack/install —
    # instead of sailing through to a buried failure at the end.
    dotnet csharpier check .
    if ($LASTEXITCODE -ne 0) { throw "csharpier check failed - run 'dotnet csharpier format .' then re-run." }
    dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw "Restore failed (exit $LASTEXITCODE)." }
    dotnet build $solution -c $Configuration /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE) - not testing/packing." }

    if (-not $SkipTests) {
        # `dotnet test` (Microsoft.Testing.Platform) reports failures via EXIT CODE, not a terminating
        # error, so $ErrorActionPreference="Stop" does NOT halt on a red suite. Gate explicitly, or a
        # failing test silently sails through to pack + global reinstall ("green" that isn't).
        dotnet test $solution -c $Configuration --no-build /p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE) - not packing/installing." }
    }

    # PORTABLE pack — do NOT add `-r <rid>`/`-p:PublishReadyToRun=true`. A RID-specific / ReadyToRun
    # publish of the tool silently breaks Buildalyzer's design-time builds of .NET FRAMEWORK (net4x)
    # projects: they return no result and are DROPPED from the index (net48 web/Pages vanish, ~half the
    # symbols lost), while netstandard/modern projects still index. The loader code is fine — only the
    # packaging triggers it. Verified on playgrounds/LegacyNet48Web: portable = 408 symbols, R2R = 35.
    # See memory `feedback_coderig_r2r_publish_net48`.
    dotnet pack $toolProject `
        -c $Configuration `
        -o $packageOutput `
        /p:PackageVersion=$ToolVersion `
        /p:Version=$ToolVersion

    if (-not $SkipToolInstall) {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        dotnet tool uninstall --global rig *> $null
        $ErrorActionPreference = $previousErrorActionPreference

        dotnet tool install --global rig `
            --add-source $packageOutput `
            --version $ToolVersion

        rig --version
    }
}
finally {
    Pop-Location
}
