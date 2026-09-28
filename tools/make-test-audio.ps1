[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateSet('legal','patient','board')][string]$Template = 'board'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$speaker = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $speaker.Rate = -1
    $speaker.SetOutputToWaveFile([IO.Path]::GetFullPath($OutputPath))
    $script = Join-Path (Split-Path -Parent $PSScriptRoot) "demo-scripts\$Template.txt"
    $conversation = [IO.File]::ReadAllText($script)
    $spoken = [regex]::Replace($conversation, '(?m)^[A-Za-z][A-Za-z .''-]{1,50}:\s*', '')
    $speaker.Speak($spoken)
}
finally { $speaker.Dispose() }
Write-Output "Created fictional speech fixture: $OutputPath"
