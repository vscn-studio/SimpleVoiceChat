[CmdletBinding()]
param(
    [switch]$SkipWhisper,
    [switch]$SkipRnnoise,
    [string]$NativeRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($NativeRoot)) {
    $dataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
    if ([string]::IsNullOrWhiteSpace($dataRoot)) {
        $dataRoot = Join-Path $HOME '.config'
    }
    $NativeRoot = Join-Path $dataRoot 'VintagestoryData/ModData/SimpleVoiceChat/native'
}

$arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($arch -eq 'x64') { $arch = 'x64' }
elseif ($arch -eq 'arm64') { $arch = 'arm64' }
elseif ($arch -eq 'x86') { $arch = 'x86' }
elseif ($arch -eq 'arm') { $arch = 'arm' }
else { throw "Unsupported process architecture: $arch" }

if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { $platform = 'win' }
elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux)) { $platform = 'linux' }
elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { $platform = 'macos' }
else { throw 'Unsupported operating system. Use Windows, Linux, or macOS.' }

$rid = "$platform-$arch"
$whisperVersion = '1.9.1'
$whisperUrl = "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime/$whisperVersion/whisper.net.runtime.$whisperVersion.nupkg"
$whisperSha256 = 'B5224F0DAD44D5EB8233E5D83F4333A8A3FCCADC77095F50F361F06D65E0736B'
$rnnoiseVersion = '0.1.9'
$rnnoiseUrl = "https://api.nuget.org/v3-flatcontainer/yellowdogman.rrnoise.net/$rnnoiseVersion/yellowdogman.rrnoise.net.$rnnoiseVersion.nupkg"
$rnnoiseSha256 = '87D80AB74EFE86F89F7F963937D675CC7B01F68A617A3A9BAEDE4AB0FB793F14'

New-Item -ItemType Directory -Force -Path $NativeRoot | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) "SimpleVoiceChat-Native-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Save-And-VerifyPackage([string]$url, [string]$expectedSha256, [string]$name) {
    $path = Join-Path $work $name
    Invoke-WebRequest -Uri $url -OutFile $path
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($actual -ne $expectedSha256) {
        throw "$name SHA-256 mismatch. Expected $expectedSha256, got $actual."
    }
    return $path
}

try {
    Write-Host "Detected platform: $rid"
    Write-Host "Native directory: $NativeRoot"

    if (-not $SkipWhisper) {
        $package = Save-And-VerifyPackage $whisperUrl $whisperSha256 "whisper.net.runtime.$whisperVersion.nupkg"
        $extract = Join-Path $work 'whisper'
        Expand-Archive -LiteralPath $package -DestinationPath $extract -Force
        $source = Join-Path $extract "build/$platform-$arch"
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Whisper.net.Runtime $whisperVersion has no native build for $rid."
        }
        Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $NativeRoot -Force
        Write-Host "Installed Whisper.net.Runtime $whisperVersion for $rid."
    }

    if (-not $SkipRnnoise) {
        if ($platform -eq 'macos') {
            Write-Warning 'YellowDogMan.RRNoise.NET 0.1.9 does not provide a macOS native build; RNNoise was not installed.'
        } elseif ($rid -notin @('win-x64', 'win-x86', 'linux-x64', 'linux-arm64')) {
            Write-Warning "YellowDogMan.RRNoise.NET 0.1.9 does not provide a native build for $rid; RNNoise was not installed."
        } else {
            $package = Save-And-VerifyPackage $rnnoiseUrl $rnnoiseSha256 "yellowdogman.rrnoise.net.$rnnoiseVersion.nupkg"
            $extract = Join-Path $work 'rnnoise'
            Expand-Archive -LiteralPath $package -DestinationPath $extract -Force
            $source = Join-Path $extract "runtimes/$rid/native"
            if (-not (Test-Path -LiteralPath $source)) {
                throw "YellowDogMan.RRNoise.NET $rnnoiseVersion has no native build for $rid."
            }
            Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $NativeRoot -Force
            Write-Host "Installed YellowDogMan.RRNoise.NET $rnnoiseVersion for $rid (third-party build)."
        }
    }

    Write-Host 'Native runtime installation completed.'
} finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
