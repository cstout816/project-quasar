param([string]$PreviewDirectory, [string]$PortablePackage)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Invoke-DotNet build (Join-Path $root 'App/App.csproj') -c Release
Invoke-DotNet build (Join-Path $root 'Bootstrap/Bootstrap.csproj') -c Release
Invoke-DotNet build (Join-Path $root 'PatchTool/PatchTool.csproj') -c Release
$baseline = Join-Path $root 'Fixture/bin/baseline'
$upgrade = Join-Path $root 'Fixture/bin/upgrade'
Invoke-DotNet build (Join-Path $root 'Fixture/Fixture.csproj') -c Release -p:Version=0.1.0 -o $baseline
Invoke-DotNet build (Join-Path $root 'Fixture/Fixture.csproj') -c Release -p:Version=0.2.0 -o $upgrade
$fixtureArgs = @($baseline, $upgrade, (Join-Path $root 'Bootstrap/bin/Release/net10.0-windows'))
if ($PreviewDirectory) {
    $fixtureArgs += @('--preview', $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PreviewDirectory))
}
if ($PortablePackage) {
    $fixtureArgs += @('--portable', (Resolve-Path -LiteralPath $PortablePackage).ProviderPath)
}
Invoke-DotNet run --project (Join-Path $root 'Tests/Tests.csproj') -c Release -- @fixtureArgs
