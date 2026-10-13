param([switch]$Cli, [switch]$AllowQuery)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $taskRoot
try {
    $taskProject = if ($Cli) { 'src\SemanticAssistant.Cli' } else { 'src\SemanticAssistant.Desktop' }
    $taskArgs = @('run', '--project', $taskProject, '--configuration', 'Release')
    if ($Cli -and $AllowQuery) { $taskArgs += @('--', '--allow-query') }
    & $taskSdk @taskArgs
    if ($LASTEXITCODE -ne 0) { throw 'Demo execution failed.' }
} finally { Pop-Location }
