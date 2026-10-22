[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\LightTranslate'),
    [switch]$Online
)
$ErrorActionPreference = 'Stop'
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$exe = Join-Path $outputPath 'LightTranslate.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the candidate first.' }
$argument = if ($Online) { '--test-index-live' } else { '--self-test-index' }
$process = Start-Process -FilePath $exe -ArgumentList $argument -WindowStyle Hidden -PassThru
# Only waits for this test process; never starts or stops the normal application.
$limit = if ($Online) { 190000 } else { 30000 }
if (-not $process.WaitForExit($limit)) { throw ('Test still running; inspect PID ' + $process.Id) }
if ($process.ExitCode -ne 0) { throw ('Index test failed; see ' + (Join-Path $outputPath 'status.log')) }
$name = if ($Online) { 'index-live-test.json' } else { 'index-test.json' }
Get-Content -LiteralPath (Join-Path $outputPath $name) -Raw -Encoding UTF8
