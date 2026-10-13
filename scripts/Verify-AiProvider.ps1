param(
    [string]$ProfilePath = '',
    [string]$OutputDirectory = '',
    [switch]$Live,
    [switch]$AllowMessageEgress,
    [switch]$AllowMetadataEgress,
    [switch]$AllowQueryEgress
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if ($Live) {
    if (-not ($AllowMessageEgress -and $AllowMetadataEgress -and $AllowQueryEgress)) {
        throw 'Live verification requires explicit message, synthetic metadata and synthetic query-result egress flags.'
    }
} elseif ($AllowMessageEgress -or $AllowMetadataEgress -or $AllowQueryEgress) { throw 'Egress flags require -Live.' }
if ([string]::IsNullOrWhiteSpace($ProfilePath)) { $ProfilePath = Join-Path $taskRoot 'examples/ai-acceptance/profile.example.json' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $taskRoot ('artifacts/ai-acceptance-' + [Guid]::NewGuid().ToString('N'))
}
$taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools/dotnet10/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
$taskArgs = @('run', '--project', (Join-Path $taskRoot 'tests/SemanticAssistant.AiAcceptance'), '-c', 'Release', '--no-restore', '--',
    '--profile', [IO.Path]::GetFullPath($ProfilePath), '--output-dir', [IO.Path]::GetFullPath($OutputDirectory))
if ($Live) { $taskArgs += @('--live', '--allow-message-egress', '--allow-metadata-egress', '--allow-query-egress') }
else { $taskArgs += '--preflight' }
& $taskSdk restore (Join-Path $taskRoot 'tests/SemanticAssistant.AiAcceptance') --locked-mode -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'Locked restore for AI acceptance failed.' }
& $taskSdk @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'AI acceptance did not pass. Inspect only the bounded verification report; no provider error body is exposed.' }
