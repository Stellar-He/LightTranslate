[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'LightTranslate.exe'
$startupDirectory = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDirectory 'LightTranslate.lnk'
if ($Remove) {
    if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath }
    Write-Output 'LightTranslate startup shortcut removed.'
    return
}
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run the startup script from the extracted portable application folder.' }
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = 'LightTranslate automatic selection translation'
$shortcut.IconLocation = $executable + ',0'
$shortcut.Save()
Write-Output ('Startup shortcut created: ' + $shortcutPath)
