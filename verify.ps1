[CmdletBinding()]
param([switch]$SkipAudio)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& dotnet test (Join-Path $root 'tests\RoomNotes.Tests\RoomNotes.Tests.csproj') -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
$smokeProject = Join-Path $root 'tools\RoomNotes.Smoke\RoomNotes.Smoke.csproj'
& dotnet build $smokeProject -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Showcase runner build failed.' }
$runner = Join-Path $root 'tools\RoomNotes.Smoke\bin\Release\net9.0-windows10.0.26100.0\win-arm64\RoomNotes.Smoke.exe'
$model = Join-Path $root 'models\ggml-base.en.bin'
& $runner $model --text-only
if ($LASTEXITCODE -ne 0) { throw 'A text showcase scenario failed.' }
if (-not $SkipAudio) {
    $fixtures = Join-Path ([IO.Path]::GetTempPath()) ('RoomNotes-synthetic-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixtures | Out-Null
    try {
        foreach ($template in 'legal','patient','board') {
            & powershell.exe -NoProfile -File (Join-Path $root 'tools\make-test-audio.ps1') `
                -Template $template -OutputPath (Join-Path $fixtures "$template.wav")
            if ($LASTEXITCODE -ne 0) { throw "Unable to synthesize $template script." }
        }
        & $runner $model $fixtures
        if ($LASTEXITCODE -ne 0) { throw 'An audio showcase scenario failed.' }
    }
    finally {
        foreach ($template in 'legal','patient','board') {
            $fixture = Join-Path $fixtures "$template.wav"
            if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture }
        }
        Remove-Item -LiteralPath $fixtures
    }
}
Write-Output 'PASS: all requested showcase passes completed.'
