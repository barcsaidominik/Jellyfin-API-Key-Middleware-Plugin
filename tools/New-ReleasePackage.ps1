# Builds the release zip and adds its entry to manifest.json (the Jellyfin plugin repository file).
#   .\tools\New-ReleasePackage.ps1 -Changelog 'What changed'
# Output: artifacts/api-key-middleware_<version>.zip. Upload that exact file to the GitHub release
# v<version>: the manifest holds its MD5 checksum, so a rebuilt zip would not match.
# The plugin version is <jellyfin major.minor.patch>.<plugin revision>, so targetAbi is derived from it.
param(
    [Parameter(Mandatory)] [string]$Changelog,
    [string]$Repository = 'barcsaidominik/Jellyfin-API-Key-Middleware-Plugin'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Jellyfin.Plugin.ApiKeyMiddleware'
$manifestPath = Join-Path $root 'manifest.json'

dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$dll = Join-Path $project 'bin\Release\net10.0\Jellyfin.Plugin.ApiKeyMiddleware.dll'
$version = (Get-Item $dll).VersionInfo.FileVersion
$tag = 'v' + $version
$TargetAbi = ($version -replace '\.\d+$', '') + '.0'
$zipName = "api-key-middleware_$version.zip"
$artifacts = Join-Path $root 'artifacts'
$zip = Join-Path $artifacts $zipName

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$plugin = @($manifest)[0]
if (@($plugin.versions | Where-Object version -eq $version).Count -gt 0) {
    throw "manifest.json already has version $version; bump AssemblyVersion/FileVersion first"
}

New-Item -ItemType Directory -Force $artifacts | Out-Null
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $dll -DestinationPath $zip
$checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant() # DevSkim: ignore DS126858 (Jellyfin's plugin manifest format requires an MD5 checksum)

$entry = [ordered]@{
    version   = $version
    changelog = $Changelog
    targetAbi = $TargetAbi
    sourceUrl = "https://github.com/$Repository/releases/download/$tag/$zipName"
    checksum  = $checksum
    timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$plugin.versions = @([pscustomobject]$entry) + @($plugin.versions)
ConvertTo-Json @($plugin) -Depth 5 | Set-Content $manifestPath -Encoding utf8NoBOM

Write-Host "Package:  $zip"
Write-Host "Checksum: $checksum"
Write-Host "Release:  gh release create $tag `"$zip`" --title $tag --notes `"$Changelog`""
