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
    dotnet csharpier format .
    if ($LASTEXITCODE -ne 0) { throw "csharpier format failed (exit $LASTEXITCODE)." }
    
    dotnet build $solution -c $Configuration 
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE) - not testing/packing." }

    if (-not $SkipTests) {
        dotnet test $solution -c $Configuration --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE) - not packing/installing." }
    }
    
    dotnet pack $toolProject `
        -c $Configuration `
        -o $packageOutput `
        /p:PackageVersion=$ToolVersion `
        /p:Version=$ToolVersion
    
    if ($LASTEXITCODE -ne 0) { throw "Pack failed (exit $LASTEXITCODE) - not installing." }

    if (-not $SkipToolInstall) {
        
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        dotnet tool uninstall --global rig *> $null
        $ErrorActionPreference = $previousErrorActionPreference


        dotnet tool install --global rig `
            --add-source $packageOutput `
            --version $ToolVersion
        if ($LASTEXITCODE -ne 0) {
            throw "Global tool install failed (exit $LASTEXITCODE). A running rig process (e.g. 'rig web') can lock the tool store - stop it and re-run."
        }
        
        $installedVersion = rig --version
        if ($LASTEXITCODE -ne 0 -or -not "$installedVersion".StartsWith($ToolVersion)) {
            throw "Installed rig reports '$installedVersion', expected $ToolVersion - global tool did not update."
        }
        $installedVersion
        
    }
}
finally {
    Pop-Location
}
