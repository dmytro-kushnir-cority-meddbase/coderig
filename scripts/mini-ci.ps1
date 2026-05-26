param(
    [string]$Configuration = "Release",
    [string]$ToolVersion = "",
    [switch]$SkipTests,
    [switch]$SkipToolInstall
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solution = Join-Path $repoRoot "RuntimeIntelligenceGraph.slnx"
$toolProject = Join-Path $repoRoot "src/Rig/Rig.csproj"
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
    dotnet restore $solution
    dotnet build $solution -c $Configuration /p:UseSharedCompilation=false -warnaserror

    if (-not $SkipTests) {
        dotnet test $solution -c $Configuration --no-build /p:UseSharedCompilation=false
    }

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
