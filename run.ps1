[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dist\RoomNotes-win-arm64\RoomNotes.App.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Run setup-model.ps1 and publish.ps1 first.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw "RoomNotes exited with code $LASTEXITCODE." }
