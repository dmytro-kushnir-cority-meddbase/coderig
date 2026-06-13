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
    dotnet csharpier check .
    dotnet restore $solution
    dotnet build $solution -c $Configuration /p:UseSharedCompilation=false -warnaserror

    if (-not $SkipTests) {
        dotnet test $solution -c $Configuration --no-build /p:UseSharedCompilation=false
    }

    dotnet pack $toolProject `
        -c $Configuration `
        -o $packageOutput `
        -r $HostRid `
        -p:PublishReadyToRun=true `
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
