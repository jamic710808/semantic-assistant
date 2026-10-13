param([switch]$KeepOpen)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
$taskExe = Join-Path $env:ProgramFiles 'Microsoft Power BI Desktop\bin\PBIDesktop.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { throw 'Power BI Desktop from the Program Files installation is required.' }
$taskDesktop = $null
$taskEngine = $null
$taskVerified = $false
Push-Location $taskRoot
try {
    & $taskSdk restore tests\SemanticAssistant.PowerBIFixture --locked-mode -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Fixture restore failed.' }
    & $taskSdk build tests\SemanticAssistant.PowerBIFixture -c Release --no-restore -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
    $taskParent = Join-Path $taskRoot 'artifacts\live-fixtures'
    [IO.Directory]::CreateDirectory($taskParent) | Out-Null
    $taskDll = Join-Path $taskRoot 'tests\SemanticAssistant.PowerBIFixture\bin\Release\net10.0\SemanticAssistant.PowerBIFixture.dll'
    $taskCreation = & $taskSdk $taskDll --create $taskParent
    if ($LASTEXITCODE -ne 0) { throw 'Fixture creation failed.' }
    $taskProject = $taskCreation[0]
    $taskFixture = Split-Path $taskProject -Parent
    $taskProject | Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts\live-fixture-current.txt') -Encoding utf8
    Write-Output ('Owned synthetic project: ' + $taskProject)
    $taskDesktop = Start-Process -FilePath $taskExe -ArgumentList ('"' + $taskProject + '"') -WindowStyle Hidden -PassThru
    $taskDesktopTicks = $taskDesktop.StartTime.ToUniversalTime().Ticks
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        if ($taskDesktop.HasExited) { throw 'New Desktop launcher exited; refusing any existing Desktop instance.' }
        $taskChildren = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($taskDesktop.Id) AND Name='msmdsrv.exe'")
        if ($taskChildren.Count -gt 1) { throw 'Unexpected engine count; refusing ambiguous ownership.' }
        if ($taskChildren.Count -eq 1 -and $taskChildren[0].CommandLine -match '-s "([^"]+)"') {
            $taskWorkspace = Split-Path $Matches[1] -Parent
            if (Test-Path -LiteralPath (Join-Path $taskWorkspace 'Data\msmdsrv.port.txt')) {
                $taskEngine = Get-Process -Id $taskChildren[0].ProcessId
                $taskEngineTicks = $taskEngine.StartTime.ToUniversalTime().Ticks
                break
            }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if (-not $taskEngine) { throw 'Owned engine did not become available; no instance fallback.' }
    # An engine port can exist before Desktop has loaded its project definition.
    $taskLoadDeadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $taskDesktop.Refresh()
        if ($taskDesktop.HasExited) { throw 'Owned Desktop exited during project load.' }
        if ($taskDesktop.MainWindowTitle.Contains('SemanticAssistantSynthetic')) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $taskLoadDeadline)
    if (-not $taskDesktop.MainWindowTitle.Contains('SemanticAssistantSynthetic')) { throw 'Owned fixture project did not finish loading; no query or write started.' }
    $taskRecord = Join-Path $taskFixture 'owned-desktop-session.json'
    [ordered]@{
        ProjectPath = $taskProject; DesktopExe = $taskExe
        DesktopProcessId = $taskDesktop.Id; DesktopStartedUtcTicks = $taskDesktopTicks
        DesktopVersion = (Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion
        EngineProcessId = $taskEngine.Id; EngineStartedUtcTicks = $taskEngineTicks; Workspace = $taskWorkspace
    } | ConvertTo-Json | Set-Content -LiteralPath $taskRecord -Encoding utf8
    & $taskSdk $taskDll --verify-owned $taskRecord
    if ($LASTEXITCODE -ne 0) { throw 'Live acceptance failed. Preserve the JSON evidence; do not retry a partially applied write.' }
    $taskVerified = $true
    if ($KeepOpen) { Write-Output ('Verified synthetic fixture retained for explicit save/reopen: ' + $taskRecord) }
} finally {
    # Only processes created for this fixture are eligible for cleanup. Their PID/start time is checked again.
    if ($taskDesktop -and -not ($KeepOpen -and $taskVerified)) {
        $taskCurrent = Get-Process -Id $taskDesktop.Id -ErrorAction SilentlyContinue
        if ($taskCurrent -and $taskCurrent.StartTime.ToUniversalTime().Ticks -eq $taskDesktopTicks) {
            [void]$taskCurrent.CloseMainWindow()
            if (-not $taskCurrent.WaitForExit(3000)) { $taskCurrent.Kill(); [void]$taskCurrent.WaitForExit(3000) }
        }
    }
    if ($taskEngine -and -not ($KeepOpen -and $taskVerified)) {
        $taskCurrentEngine = Get-Process -Id $taskEngine.Id -ErrorAction SilentlyContinue
        if ($taskCurrentEngine -and $taskCurrentEngine.StartTime.ToUniversalTime().Ticks -eq $taskEngineTicks) { $taskCurrentEngine.Kill() }
    }
    Pop-Location
}
