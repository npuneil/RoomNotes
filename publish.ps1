[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\RoomNotes.App\RoomNotes.App.csproj'
$output = Join-Path $PSScriptRoot 'dist\RoomNotes-win-arm64'
$model = Join-Path $PSScriptRoot 'models\ggml-base.en.bin'
if (-not (Test-Path -LiteralPath $model)) { throw 'Run setup-model.ps1 before publishing.' }
& dotnet publish $project -c Release -p:Platform=ARM64 -r win-arm64 --self-contained true -o $output --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "RoomNotes publish failed (exit $LASTEXITCODE)." }
$modelOutput = Join-Path $output 'models'
New-Item -ItemType Directory -Path $modelOutput -Force | Out-Null
Copy-Item -LiteralPath $model -Destination $modelOutput -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'demo-scripts') -Destination $output -Recurse -Force
foreach ($file in 'RoomNotes.App.exe', 'RoomNotes.App.pri', 'models\ggml-base.en.bin') {
    if (-not (Test-Path -LiteralPath (Join-Path $output $file))) { throw "Publish is missing $file." }
}
Write-Output "Native ARM64 app: $output\RoomNotes.App.exe"
Write-Output 'Distribute the entire folder. Foundry Local and the cached QNN model remain prerequisites.'
