# Writes the winget manifest for a published release, ready to submit to microsoft/winget-pkgs.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\winget-manifest.ps1 -Version 3.2.0
# It downloads TidyDesk-Setup.exe from that GitHub release to compute its SHA256 (winget requires it),
# then writes packaging\winget\odmcc.TidyDesk\<version>\*.yaml. Submitting is a separate, manual step (see README).
param(
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = "Stop"

$id = "odmcc.TidyDesk"
$url = "https://github.com/odmcc/TidyDesk/releases/download/v$Version/TidyDesk-Setup.exe"
$out = Join-Path $PSScriptRoot "..\packaging\winget\$id\$Version"
$tmp = Join-Path ([IO.Path]::GetTempPath()) "TidyDesk-Setup-$Version.exe"

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
$sha = (Get-FileHash $tmp -Algorithm SHA256).Hash
Remove-Item $tmp

New-Item -ItemType Directory -Force $out | Out-Null
$schema = "1.9.0"
$utf8 = New-Object System.Text.UTF8Encoding $false

[IO.File]::WriteAllText((Join-Path $out "$id.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@, $utf8)

# Inno Setup installer, per-user (no administrator rights); the AppId comes from installer\TidyDesk.iss.
[IO.File]::WriteAllText((Join-Path $out "$id.installer.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: inno
Scope: user
InstallModes:
  - interactive
  - silent
  - silentWithProgress
UpgradeBehavior: install
ProductCode: '{A24AD7C8-FB3D-4BF1-B2B4-5F9582BCD775}_is1'
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@, $utf8)

[IO.File]::WriteAllText((Join-Path $out "$id.locale.en-US.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: odmcc
PublisherUrl: https://github.com/odmcc
PublisherSupportUrl: https://github.com/odmcc/TidyDesk/issues
PackageName: TidyDesk
PackageUrl: https://odmcc.github.io/TidyDesk/
License: MIT
LicenseUrl: https://github.com/odmcc/TidyDesk/blob/main/LICENSE
ShortDescription: Tidies up your Desktop, Downloads and Documents in one click. Nothing is deleted, every run can be undone.
Description: TidyDesk sorts the files in the folders you pick into folders like PDF, Documents, Spreadsheets and Images. It never deletes a file, never goes online, and every run can be undone. English and Turkish.
Tags:
  - desktop
  - files
  - organizer
  - cleanup
  - downloads
ReleaseNotesUrl: https://github.com/odmcc/TidyDesk/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@, $utf8)

Write-Host "Wrote $out (SHA256 $sha)"
Write-Host "Check it with:  winget validate --manifest `"$out`""
