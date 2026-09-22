[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$project = Join-Path $repo 'SimpleVoiceChat.csproj'
$modInfoPath = Join-Path $repo 'modinfo.json'
$modInfo = Get-Content -LiteralPath $modInfoPath -Raw | ConvertFrom-Json
$version = [string]$modInfo.version
$stage = Join-Path $repo "bin\$Configuration\Mods\mod"
$artifactDir = Join-Path $repo 'artifacts'
$package = Join-Path $artifactDir "SimpleVoiceChat-v$version.zip"

if ([string]::IsNullOrWhiteSpace($version)) { throw 'modinfo.json does not contain a version' }
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

# Never reuse a previous package or an old staged output directory.
foreach ($path in @(
    $package,
    "$package.sha256",
    (Join-Path $artifactDir "release-$version")
)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }

dotnet clean $project --configuration $Configuration --nologo
dotnet build $project --configuration $Configuration --no-restore --nologo

if (-not (Test-Path -LiteralPath $stage)) { throw "Build output was not created: $stage" }
$forbidden = Get-ChildItem -LiteralPath $stage -Recurse -Force -File | Where-Object {
    $_.Extension -in @('.pdb', '.sha256') -or
    $_.FullName -match '\\docs(\\|$)' -or
    $_.FullName -match '\\textures\\icons\\(fontawesome|lucide)(\\|$)'
}
if ($forbidden) {
    $forbidden | ForEach-Object { Write-Error $_.FullName }
    throw 'Build output contains forbidden packaging files'
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $package -CompressionLevel Optimal -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @($zip.Entries | Where-Object { $_.Length -gt 0 })
    $badEntries = @($entries | Where-Object {
        $_.FullName -match '(^|[/\\])docs([/\\]|$)' -or
        $_.FullName -match '\.pdb$' -or
        $_.FullName -match '\.sha256$' -or
        $_.FullName -match 'textures/icons/(fontawesome|lucide)/'
    })
    if ($badEntries) {
        $badEntries | ForEach-Object { Write-Error $_.FullName }
        throw 'Package contains forbidden files'
    }

    foreach ($required in @(
        'LICENSE',
        'THIRD-PARTY-NOTICES.md',
        'modinfo.json',
        'SimpleVoiceChat.dll',
        'Concentus.dll',
        'RNNoise.NET.dll',
        'Whisper.net.dll',
        'Microsoft.Extensions.AI.Abstractions.dll',
        'assets/simplevoicechat/fonts/tabler/tabler-icons.ttf'
    )) {
        if (-not ($entries.FullName -contains $required)) { throw "Required package file missing: $required" }
    }
} finally {
    $zip.Dispose()
}

$stageHash = (Get-FileHash -LiteralPath (Join-Path $stage 'SimpleVoiceChat.dll') -Algorithm SHA256).Hash
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
Write-Output "Created: $package"
Write-Output "Version: $version"
Write-Output "Configuration: $Configuration"
Write-Output "DLL SHA256: $stageHash"
Write-Output "ZIP SHA256: $packageHash"
