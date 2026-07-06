param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [string]$PackageCertificateKeyFile = "",

    [switch]$SignPackage,

    [string]$AppxBundle = "Never",

    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\DeskBox\DeskBox.csproj"
$dotnetFromRepo = Join-Path $repoRoot ".codex-temp\dotnet10\dotnet.exe"
$dotnet = if (Test-Path $dotnetFromRepo) { $dotnetFromRepo } else { "dotnet" }
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\store-msix\"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$signingEnabled = if ($SignPackage.IsPresent) { "true" } else { "false" }

$properties = @(
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    "-p:DeskBoxDistribution=Store",
    "-p:DeskBoxCreateMsixPackage=true",
    "-p:SelfContained=true",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:AppxBundle=$AppxBundle",
    "-p:AppxPackageDir=$OutputDir",
    "-p:AppxPackageSigningEnabled=$signingEnabled"
)

if (-not [string]::IsNullOrWhiteSpace($PackageCertificateKeyFile)) {
    $properties += "-p:PackageCertificateKeyFile=$PackageCertificateKeyFile"
}

& $dotnet publish $project -c $Configuration @properties -v:minimal

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Store MSIX output:" -ForegroundColor Cyan
Get-ChildItem -Path $OutputDir -Recurse -Include *.msix,*.msixbundle,*.msixupload |
    Sort-Object LastWriteTime -Descending |
    Select-Object FullName, Length, LastWriteTime
