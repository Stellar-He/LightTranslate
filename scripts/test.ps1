[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\LightTranslate'),
    [switch]$Online
)

$ErrorActionPreference = 'Stop'
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$executable = Join-Path $outputPath 'LightTranslate.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the application first.' }
if (-not (Test-Path -LiteralPath (Join-Path $outputPath 'lexicon.idx'))) { throw 'Build the dictionary first.' }
$testArgument = if ($Online) { '--self-test' } else { '--self-test-offline' }
$testProcess = Start-Process -FilePath $executable -ArgumentList $testArgument -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(60000)) {
    Stop-Process -Id $testProcess.Id -Force
    throw 'Self-test exceeded 60 seconds.'
}
if ($testProcess.ExitCode -ne 0) { throw ('Self-test failed. See ' + (Join-Path $outputPath 'status.log')) }
Get-Content -LiteralPath (Join-Path $outputPath 'self-test.json')
