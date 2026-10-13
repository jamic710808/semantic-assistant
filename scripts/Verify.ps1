param([switch]$SkipDesktopSmoke)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $taskRoot
try {
    & $taskSdk restore SemanticAssistant.slnx --locked-mode -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $taskSdk build SemanticAssistant.slnx -c Release --no-restore -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $taskSdk test tests\SemanticAssistant.Application.Tests\SemanticAssistant.Application.Tests.csproj -c Release --no-build -m:1 -nr:false --logger 'trx;LogFileName=m1.trx' --results-directory artifacts\test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $taskSdk run --project src\SemanticAssistant.Cli -c Release --no-build -- --allow-query
    if ($LASTEXITCODE -ne 0) { throw 'CLI smoke failed.' }
    if (-not $SkipDesktopSmoke) {
        foreach ($taskScenario in @(
            @{Flag='--smoke-cloudlogin';Prefix='wpf-cloudlogin';Expected='wpf-loaded;cloud-cache-opt-in-default-off=ok;dpapi-roundtrip=ok;clear-from-form=ok;no-restored-consent=ok;synthetic-fixture=true';Image='cloudlogin-preview.png'},
            @{Flag='--smoke-test';Prefix='wpf-smoke';Expected='wpf-loaded;demo-query=ok;synthetic=true';Image='desktop-preview.png'},
            @{Flag='--smoke-settings';Prefix='wpf-settings';Expected='wpf-loaded;settings-restart=ok;no-restored-consent=ok;saved-budget=ok;synthetic-fixture=true';Image='settings-preview.png'},
            @{Flag='--smoke-importedtools';Prefix='wpf-importedtools';Expected='wpf-loaded;mcp-explicit-import=ok;no-auto-grant=ok;per-call-refusal=ok;ai-tool-roundtrip=ok;owned-process-cleanup=ok;synthetic-fixture=true';Image='importedtools-preview.png'},
            @{Flag='--smoke-history';Prefix='wpf-history';Expected='wpf-loaded;history-opt-in=ok;summary-artifact-index=ok;no-restored-authority=ok;clear-keeps-html=ok;synthetic-fixture=true';Image='history-preview.png'},
                @{Flag='--smoke-modes';Prefix='wpf-modes';Expected='wpf-loaded;five-mode-routes=ok;mode-revocation=ok;separate-consent=ok;synthetic-fixture=true';Image='modes-preview.png'},
            @{Flag='--smoke-writer';Prefix='wpf-writer';Expected='wpf-loaded;write-preview=ok;per-operation-consent=ok;readback=ok;synthetic-fixture=true';Image='writer-preview.png'},
            @{Flag='--smoke-objects';Prefix='wpf-objects';Expected='wpf-loaded;mixed-preview=ok;column-relationship-write=ok;readback=ok;synthetic-fixture=true';Image='objects-preview.png'},
            @{Flag='--smoke-dashboard';Prefix='wpf-dashboard';Expected='wpf-loaded;dashboard-preview=ok;write-query-export-consent=ok;html-report=ok;dynamic-html-write=ok;demo-reset=ok;synthetic-fixture=true';Image='dashboard-preview.png'},
            @{Flag='--smoke-proposal';Prefix='wpf-proposal';Expected='wpf-loaded;ai-dashboard-proposal=ok;no-auto-write-query=ok;per-operation-consent=ok;dynamic-html-write=ok;synthetic-fixture=true';Image='proposal-preview.png'},
            @{Flag='--smoke-offline';Prefix='wpf-offline';Expected='wpf-loaded;offline-tmdl=ok;search=ok;details=ok;reload=ok;demo-reset=ok;synthetic-fixture=true';Image='offline-preview.png'}
        )) {
            $taskReport = Join-Path $taskRoot ('artifacts\' + $taskScenario.Prefix + '-' + [Guid]::NewGuid().ToString('N') + '.txt')
            $taskDll = Join-Path $taskRoot 'src\SemanticAssistant.Desktop\bin\Release\net10.0-windows\SemanticAssistant.Desktop.dll'
            $taskArguments = @('"' + $taskDll + '"', $taskScenario.Flag, '"' + $taskReport + '"')
            if ($taskScenario.Flag -eq '--smoke-importedtools') {
                $taskFixture = Join-Path $taskRoot 'tests\SemanticAssistant.FixtureHost\bin\Release\net10.0\SemanticAssistant.FixtureHost.dll'
                $taskArguments += '"' + $taskFixture + '"'
            }
            $taskProcess = Start-Process -FilePath $taskSdk -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
            if (-not $taskProcess.WaitForExit(30000)) { $taskProcess.Kill(); throw 'Desktop smoke timed out.' }
            if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskReport)) { throw 'Desktop smoke failed.' }
            $taskResult = Get-Content -LiteralPath $taskReport -Raw
            if ($taskResult -ne $taskScenario.Expected) { throw 'Desktop smoke result invalid.' }
            Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($taskReport, '.png')) -Destination (Join-Path $taskRoot ('artifacts\' + $taskScenario.Image))
            if ($taskScenario.Flag -eq '--smoke-dashboard') {
                Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($taskReport, '.dashboard.png')) -Destination (Join-Path $taskRoot 'artifacts\dashboard-flow-preview.png')
            }
            if ($taskScenario.Flag -eq '--smoke-proposal') {
                Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($taskReport, '.proposal.png')) -Destination (Join-Path $taskRoot 'artifacts\proposal-flow-preview.png')
            }
            if ($taskScenario.Flag -eq '--smoke-cloudlogin') {
                Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($taskReport, '.cloudlogin.png')) -Destination (Join-Path $taskRoot 'artifacts\cloudlogin-form-preview.png')
            }
            Write-Output $taskResult
        }
        $taskReport = Join-Path $taskRoot ('artifacts\wpf-mcp-' + [Guid]::NewGuid().ToString('N') + '.txt')
        $taskFixture = Join-Path $taskRoot 'tests\SemanticAssistant.FixtureHost\bin\Release\net10.0\SemanticAssistant.FixtureHost.dll'
        $taskProcess = Start-Process -FilePath $taskSdk -ArgumentList @('"' + $taskDll + '"', '--smoke-mcp', '"' + $taskReport + '"', '"' + $taskFixture + '"') -WindowStyle Hidden -PassThru
        if (-not $taskProcess.WaitForExit(30000)) { $taskProcess.Kill(); throw 'MCP desktop smoke timed out.' }
        if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskReport)) { throw 'MCP desktop smoke failed.' }
        $taskResult = Get-Content -LiteralPath $taskReport -Raw
        if ($taskResult -ne 'wpf-loaded;mcp-stdio=ok;per-call-consent=ok;synthetic-fixture=true') { throw 'MCP desktop result invalid.' }
        Write-Output $taskResult
    }
} finally { Pop-Location }
