param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [ValidateSet("Direct", "Store")]
    [string]$Distribution = "Direct",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier,

    [switch]$Build,

    [switch]$NoStop
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\DeskBox\DeskBox.csproj"
$dotnetFromRepo = Join-Path $repoRoot ".codex-temp\dotnet10\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $dotnetFromRepo) { $dotnetFromRepo } else { "dotnet" }

[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$targetFramework = $projectXml.Project.PropertyGroup |
    Where-Object { $_.TargetFramework } |
    Select-Object -First 1 -ExpandProperty TargetFramework

if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "Unable to read TargetFramework from $project"
}

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $RuntimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
}

if (-not $NoStop.IsPresent) {
    Get-Process -Name DeskBox -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

if ($Build.IsPresent) {
    & $dotnet build $project `
        -c $Configuration `
        -p:Platform=$Platform `
        -p:RuntimeIdentifier=$RuntimeIdentifier `
        -p:DeskBoxDistribution=$Distribution `
        -v:minimal

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$outputDir = Join-Path $repoRoot "src\DeskBox\bin\$Platform\$Configuration\$targetFramework\$RuntimeIdentifier"
$exe = Join-Path $outputDir "DeskBox.exe"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "DeskBox.exe was not found at $exe. Run this script with -Build first."
}

$process = Start-Process `
    -FilePath $exe `
    -WorkingDirectory $outputDir `
    -WindowStyle Hidden `
    -PassThru

Start-Sleep -Seconds 3
$runningProcess = Get-Process -Id $process.Id -ErrorAction SilentlyContinue

[PSCustomObject]@{
    Exe = $exe
    ProcessId = $process.Id
    Running = [bool]$runningProcess
    StartTime = if ($runningProcess) { $runningProcess.StartTime } else { $null }
}
