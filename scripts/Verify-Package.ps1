param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
$taskDll = Join-Path $taskRoot 'artifacts\desktop\SemanticAssistant.Desktop.dll'
if (-not (Test-Path -LiteralPath $taskDll)) { throw 'Publish the desktop project to artifacts/desktop first.' }
# Only our packaged application and self-created in-memory synthetic models are exercised.
foreach ($taskScenario in @(
    @{Flag='--smoke-cloudlogin';Expected='wpf-loaded;cloud-cache-opt-in-default-off=ok;dpapi-roundtrip=ok;clear-from-form=ok;no-restored-consent=ok;synthetic-fixture=true'},
    @{Flag='--smoke-settings';Expected='wpf-loaded;settings-restart=ok;no-restored-consent=ok;saved-budget=ok;synthetic-fixture=true'},
    @{Flag='--smoke-importedtools';Expected='wpf-loaded;mcp-explicit-import=ok;no-auto-grant=ok;per-call-refusal=ok;ai-tool-roundtrip=ok;owned-process-cleanup=ok;synthetic-fixture=true'},
    @{Flag='--smoke-history';Expected='wpf-loaded;history-opt-in=ok;summary-artifact-index=ok;no-restored-authority=ok;clear-keeps-html=ok;synthetic-fixture=true'},
    @{Flag='--smoke-modes';Expected='wpf-loaded;five-mode-routes=ok;mode-revocation=ok;separate-consent=ok;synthetic-fixture=true'},
    @{Flag='--smoke-dashboard';Expected='wpf-loaded;dashboard-preview=ok;write-query-export-consent=ok;html-report=ok;dynamic-html-write=ok;demo-reset=ok;synthetic-fixture=true'},
    @{Flag='--smoke-proposal';Expected='wpf-loaded;ai-dashboard-proposal=ok;no-auto-write-query=ok;per-operation-consent=ok;dynamic-html-write=ok;synthetic-fixture=true'},
    @{Flag='--smoke-mcp';Expected='wpf-loaded;mcp-stdio=ok;per-call-consent=ok;synthetic-fixture=true'}
)) {
    $taskReport = Join-Path $taskRoot ('artifacts\package-current-' + $taskScenario.Flag.TrimStart('-') + '-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $taskArguments = @('"' + $taskDll + '"', $taskScenario.Flag, '"' + $taskReport + '"')
    if ($taskScenario.Flag -in @('--smoke-mcp', '--smoke-importedtools')) {
        $taskFixture = Join-Path $taskRoot 'tests\SemanticAssistant.FixtureHost\bin\Release\net10.0\SemanticAssistant.FixtureHost.dll'
        if (-not (Test-Path -LiteralPath $taskFixture)) { throw 'Build the self-authored FixtureHost before package verification.' }
        $taskArguments += '"' + $taskFixture + '"'
    }
    $taskProcess = Start-Process -FilePath $taskSdk -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
    if (-not $taskProcess.WaitForExit(30000)) { $taskProcess.Kill(); throw 'Packaged desktop smoke timed out.' }
    if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskReport)) { throw 'Packaged desktop smoke failed.' }
    if ((Get-Content -LiteralPath $taskReport -Raw) -ne $taskScenario.Expected) { throw 'Packaged desktop result invalid.' }
    Write-Output $taskScenario.Expected
}
