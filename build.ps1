<#
.SYNOPSIS
  Builds "GTWM Model Converter.exe" (a single self-contained file) and optionally zips it.
.EXAMPLE
  .\build.ps1
  .\build.ps1 -Zip
  .\build.ps1 -SelfTest      # run the built-in tests
#>
param(
    [string]$Configuration = "Release",
    [switch]$Zip,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "Building ModelConverter" -ForegroundColor Cyan
dotnet build (Join-Path $root "src\ModelConverter\ModelConverter.csproj") -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$exe = Join-Path $root "build\ModelConverter\GTWM Model Converter.exe"
Write-Host "Built $exe" -ForegroundColor Green

if ($SelfTest) {
    $dir = Join-Path $root "build\selftest"
    New-Item -ItemType Directory -Force $dir | Out-Null
    & $exe --selftest $dir | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Self-test failed" }
}

if ($Zip) {
    $version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    $stage = Join-Path $root "dist"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    $tools = New-Item -ItemType Directory -Force (Join-Path $stage "GTWM\tools")
    New-Item -ItemType Directory -Force (Join-Path $stage "GTWM\models") | Out-Null
    Copy-Item $exe $tools
    $zipPath = Join-Path $root "GTWM-ModelConverter-$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath
    Write-Host "Zipped to $zipPath" -ForegroundColor Green
}
