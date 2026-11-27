#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.0',
    [string]$OutputDirectory,
    [string]$PublicKeyPem,
    [string]$SigningKeyPem,
    [version]$MinimumRuntime = '1.0.0',
    [version]$MaximumRuntimeExclusive = '2.0.0'
)

$ErrorActionPreference = 'Stop'
if (([version]$Version).ToString(3) -cne $Version) { throw 'Use canonical major.minor.patch numbering.' }
if ($MinimumRuntime -ge $MaximumRuntimeExclusive) { throw 'The runtime range must be increasing.' }
if ($SigningKeyPem -and !$PublicKeyPem) { throw 'Signing requires the matching public key to be embedded.' }
$paths = @()
if ($PublicKeyPem) {
    $PublicKeyPem = (Resolve-Path -LiteralPath $PublicKeyPem).ProviderPath
    if (![IO.File]::ReadAllText($PublicKeyPem).Contains('-----BEGIN PUBLIC KEY-----')) {
        throw 'Use a SubjectPublicKeyInfo public key PEM.'
    }
    $paths += "-p:QuasarPublicKey=$PublicKeyPem"
}

$signer = $null
$public = $null
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot "artifacts/package-$Version-$([guid]::NewGuid().ToString('N'))"
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$work = Join-Path $output 'build'
$appFiles = @('Quasar.Launcher.exe', 'Quasar.Launcher.dll', 'Quasar.Launcher.deps.json',
    'Quasar.Launcher.runtimeconfig.json', 'Core.dll')
$bootstrapFiles = @('ProjectQuasar.exe', 'ProjectQuasar.dll', 'ProjectQuasar.deps.json',
    'ProjectQuasar.runtimeconfig.json', 'Core.dll')

function Publish([string]$Project, [string]$Name) {
    $destination = Join-Path $work $Name
    & dotnet publish (Join-Path $PSScriptRoot $Project) -c Release --self-contained false --artifacts-path (Join-Path $work 'dotnet') "-p:Version=$Version" -p:DebugType=None -o $destination @paths
    if ($LASTEXITCODE -ne 0) { throw "Publishing $Name failed with exit code $LASTEXITCODE." }
}

function Add-ArchiveFiles($Zip, [string]$From, [string]$Prefix, $Names) {
    foreach ($name in $Names) {
        $file = Join-Path $From $name
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Zip, $file, "$Prefix$name") | Out-Null
    }
}

try {
    if ($PublicKeyPem) {
        $public = [Security.Cryptography.RSA]::Create()
        $public.ImportFromPem([IO.File]::ReadAllText($PublicKeyPem))
        if ($public.KeySize -ne 3072) { throw 'Release signing requires an RSA-3072 public key.' }
    }
    if ($SigningKeyPem) {
        $signer = [Security.Cryptography.RSA]::Create()
        $signer.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $SigningKeyPem).ProviderPath))
        if ($signer.ExportSubjectPublicKeyInfoPem() -ne $public.ExportSubjectPublicKeyInfoPem()) {
            throw 'The private signing key does not match the embedded public key.'
        }
    }

    Publish 'App/App.csproj' app
    Publish 'Bootstrap/Bootstrap.csproj' bootstrap
    $app = Join-Path $work 'app'
    $bootstrap = Join-Path $work 'bootstrap'
    $portable = Join-Path $output "quasar-portable-$Version-win-x64.zip"
    $asset = "quasar-launcher-$Version-win-x64.zip"
    $update = Join-Path $output $asset
    $bundle = [IO.Compression.ZipFile]::Open($portable, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-ArchiveFiles $bundle $app 'bundled/' $appFiles
        Add-ArchiveFiles $bundle $bootstrap '' $bootstrapFiles
    } finally { $bundle.Dispose() }
    $bundle = [IO.Compression.ZipFile]::Open($update, [IO.Compression.ZipArchiveMode]::Create)
    try { Add-ArchiveFiles $bundle $app '' $appFiles }
    finally { $bundle.Dispose() }

    $expanded = ($appFiles | ForEach-Object { (Get-Item -LiteralPath (Join-Path $app $_)).Length } | Measure-Object -Sum).Sum
    $manifest = [ordered]@{
        schema = 1
        platform = 'win-x64'
        contract = 1
        minimumRuntime = $MinimumRuntime.ToString()
        maximumRuntimeExclusive = $MaximumRuntimeExclusive.ToString()
        launcher = [ordered]@{
            version = $Version
            asset = $asset
            size = (Get-Item -LiteralPath $update).Length
            expandedSize = [long]$expanded
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $update).Hash
        }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllBytes((Join-Path $output 'quasar-win-x64.json'), $bytes)
    if ($signer) {
        $signature = $signer.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
        [IO.File]::WriteAllBytes((Join-Path $output 'quasar-win-x64.sig'), $signature)
    }
    Write-Output "Launcher packages are in $output"
} finally {
    if ($public) { $public.Dispose() }
    if ($signer) { $signer.Dispose() }
}
