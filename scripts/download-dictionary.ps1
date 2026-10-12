[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\LightTranslate'),
    [string]$CsvPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$executable = Join-Path $outputPath 'LightTranslate.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run pwsh -File .\build.ps1 first.' }
$expectedHash = '1a6947e04785db63613a92e14903cdae7954f7e84860b10e68e5c7cbb3f9c3cf'
if (-not $CsvPath) {
    $cachePath = Join-Path $repoRoot 'cache'
    New-Item -ItemType Directory -Path $cachePath -Force | Out-Null
    $CsvPath = Join-Path $cachePath 'ecdict.csv'
    if (-not (Test-Path -LiteralPath $CsvPath)) {
        $temporaryPath = $CsvPath + '.download'
        try {
            Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/skywind3000/ECDICT/bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b/ecdict.csv' -OutFile $temporaryPath
            if ((Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
                throw 'Downloaded dictionary checksum mismatch.'
            }
            Move-Item -LiteralPath $temporaryPath -Destination $CsvPath -Force
        }
        finally { if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath } }
    }
}
$csvAbsolutePath = (Resolve-Path -LiteralPath $CsvPath).Path
if ((Get-FileHash -LiteralPath $csvAbsolutePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'ECDICT checksum mismatch. Use the pinned upstream file documented in THIRD_PARTY_NOTICES.md.'
}
$builder = Start-Process -FilePath $executable -ArgumentList ('--build-dictionary "' + $csvAbsolutePath + '"') -WindowStyle Hidden -PassThru -Wait
if ($builder.ExitCode -ne 0) { throw ('Dictionary builder failed: ' + $builder.ExitCode) }
Get-Content -LiteralPath (Join-Path $outputPath 'dictionary-build.txt')
