param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk=(Get-Command dotnet -ErrorAction Stop).Source }
$taskParent = Join-Path $taskRoot 'artifacts\live-fixtures'
[IO.Directory]::CreateDirectory($taskParent) | Out-Null
Push-Location $taskRoot
try {
    & $taskSdk restore tests\SemanticAssistant.PowerBIFixture --locked-mode -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Fixture restore failed.' }
    & $taskSdk build tests\SemanticAssistant.PowerBIFixture -c Release --no-restore -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
    & $taskSdk run --project tests\SemanticAssistant.PowerBIFixture -c Release --no-build -- --create $taskParent
    if ($LASTEXITCODE -ne 0) { throw 'Fixture creation failed.' }
} finally { Pop-Location }
