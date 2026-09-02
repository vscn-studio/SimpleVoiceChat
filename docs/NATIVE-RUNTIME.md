# Native runtime installation

SimpleVoiceChat keeps managed dependencies in the main mod archive, but does not bundle platform native libraries. Install only libraries built for the running Vintage Story process under:

```text
%APPDATA%\VintagestoryData\ModData\SimpleVoiceChat\native\
```

The loader accepts either a flat layout or a RID layout:

```text
native\rnnoise.dll
native\whisper.dll
native\ggml-base-whisper.dll
native\ggml-cpu-whisper.dll
native\ggml-whisper.dll

native\runtimes\win-x64\...
native\runtimes\linux-x64\...
native\runtimes\osx-x64\...
native\runtimes\osx-arm64\...
```

Use these file names for RNNoise:

| Platform | Library |
| --- | --- |
| Windows x64 | `rnnoise.dll` |
| Linux x64 | `librnnoise.so` |
| macOS x64/arm64 | `librnnoise.dylib` |

The RNNoise library must export `rnnoise_create`, `rnnoise_destroy`, and `rnnoise_process_frame`. It receives native 48 kHz mono frames: two 10 ms blocks of 480 samples for each 20 ms voice frame.

Whisper requires `whisper` plus all matching `ggml-*` dependencies from the same build. Select the actual GGML `.bin` model file in the settings page; the model is separate from the native runtime.

## Download sources and installer

- Whisper native libraries: [Whisper.net.Runtime 1.9.1](https://www.nuget.org/packages/Whisper.net.Runtime/1.9.1). This is the version referenced by the mod and supplies Windows, Linux, and macOS libraries.
- RNNoise native libraries: [YellowDogMan.RRNoise.NET 0.1.9](https://www.nuget.org/packages/YellowDogMan.RRNoise.NET/0.1.9). This is a third-party build, not an official RNNoise release. It supplies Windows and Linux builds only; macOS RNNoise must be obtained and verified separately.

On PowerShell 7, run the installer included with the mod or repository:

```powershell
pwsh -ExecutionPolicy Bypass -File .\tools\Install-NativeRuntimes.ps1
```

It downloads the fixed package versions directly from NuGet, verifies their pinned SHA-256 values before extraction, detects the current platform and architecture, and installs only compatible files. Use `-SkipWhisper` or `-SkipRnnoise` to install one runtime. The script never writes native files into the mod archive.

If a library is missing, has the wrong architecture, or cannot load one of its dependencies, voice chat continues with the built-in processing and the affected optional feature remains disabled. Remove duplicate old mod archives before testing a runtime. The V10 protocol requires the same `1.2.7-pre.2` mod on both client and server.
