[CmdletBinding()]
param(
    [string]$Version = '0.1.0-alpha.2'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$distRoot = Join-Path $repoRoot 'dist'
$outputPath = Join-Path $distRoot ('package-' + $Version + '\LightTranslate')
if (Test-Path -LiteralPath $outputPath) { throw 'This package directory already exists. Use a fresh version or remove it explicitly before rebuilding.' }
& (Join-Path $repoRoot 'build.ps1') -OutputDirectory $outputPath
& (Join-Path $PSScriptRoot 'download-dictionary.ps1') -OutputDirectory $outputPath
& (Join-Path $PSScriptRoot 'test.ps1') -OutputDirectory $outputPath
foreach ($generatedName in @('status.log','self-test.json','preview.png','dictionary-build.txt')) {
    $generatedPath = Join-Path $outputPath $generatedName
    if (Test-Path -LiteralPath $generatedPath) { Remove-Item -LiteralPath $generatedPath }
}
$archivePath = Join-Path $distRoot ('LightTranslate-' + $Version + '-windows-x64.zip')
if (Test-Path -LiteralPath $archivePath) { throw 'Archive already exists.' }
Compress-Archive -LiteralPath $outputPath -DestinationPath $archivePath -CompressionLevel Optimal
$digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archivePath + '.sha256') -Value ($digest + '  ' + [System.IO.Path]::GetFileName($archivePath)) -Encoding utf8NoBOM
Write-Output $archivePath
