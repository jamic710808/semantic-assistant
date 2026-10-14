param([string]$DotNetPath)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = Split-Path -Parent $PSScriptRoot
if (-not $DotNetPath) {
    $taskBundledDotNet = Join-Path (Split-Path -Parent $taskProjectRoot) '.tools\dotnet10\dotnet.exe'
    $DotNetPath = if (Test-Path -LiteralPath $taskBundledDotNet) { $taskBundledDotNet } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$taskProjectFile = Join-Path $taskProjectRoot 'src\SemanticAssistant.Desktop\SemanticAssistant.Desktop.csproj'
$taskAppDirectory = Join-Path $taskProjectRoot 'app'
& $DotNetPath publish $taskProjectFile --configuration Release --output $taskAppDirectory --self-contained false --disable-build-servers --nologo -m:1 '-p:AppHostRelativeDotNet=../../.tools/dotnet10'
exit $LASTEXITCODE
