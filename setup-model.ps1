[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$modelDirectory = Join-Path $PSScriptRoot 'models'
$destination = Join-Path $modelDirectory 'ggml-base.en.bin'
$partial = "$destination.partial"
$expected = 'A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002'
if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $expected) {
    Write-Output "Verified local Whisper model: $destination"
    exit 0
}
New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null
Write-Output 'Downloading Whisper Base English (148 MB). This setup step needs internet; transcription does not.'
$url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.en.bin'
try {
    & curl.exe --fail --location --retry 2 --silent --show-error --output $partial $url
    if ($LASTEXITCODE -ne 0) { throw "Model download failed (exit $LASTEXITCODE)." }
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $expected) {
        throw 'Model checksum mismatch. The downloaded file will not be used.'
    }
    Move-Item -LiteralPath $partial -Destination $destination -Force
}
finally {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
}
Write-Output "Verified local Whisper model: $destination"
