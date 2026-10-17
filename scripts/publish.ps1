# Builds the portable single-file TidyDesk.exe. .NET does not need to be installed on the target PC.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
param(
    [string]$Runtime = "win-x64",
    [string]$Output = (Join-Path $PSScriptRoot "..\dist")
)
$ErrorActionPreference = "Stop"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $dotnet = $userDotnet }
if (-not $dotnet) { throw ".NET SDK not found. See 'Build from source' in the README." }

$project = Join-Path $PSScriptRoot "..\src\TidyDesk\TidyDesk.csproj"
& $dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Get-ChildItem $Output -File | Format-Table Name, @{ n = "Size (MB)"; e = { [math]::Round($_.Length / 1MB, 1) } }
