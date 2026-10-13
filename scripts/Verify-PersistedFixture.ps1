param([Parameter(Mandatory)][string]$OriginalSessionPath,
      [Parameter(Mandatory)][string]$ReopenedSessionPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
$taskOriginal = [IO.Path]::GetFullPath($OriginalSessionPath)
$taskReopened = [IO.Path]::GetFullPath($ReopenedSessionPath)
Push-Location $taskRoot
try {
    & $taskSdk restore tests\SemanticAssistant.PowerBIFixture --locked-mode -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Fixture restore failed.' }
    & $taskSdk build tests\SemanticAssistant.PowerBIFixture -c Release --no-restore -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
    & $taskSdk tests\SemanticAssistant.PowerBIFixture\bin\Release\net10.0\SemanticAssistant.PowerBIFixture.dll --verify-persisted $taskReopened $taskOriginal
    if ($LASTEXITCODE -ne 0) { throw 'Persistence acceptance failed. Preserve the generated evidence; no write or refresh was attempted.' }
} finally { Pop-Location }
