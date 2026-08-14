# SimpleVoiceChat OBS source

This is an OBS Studio source plugin for Windows, Linux, and macOS. It consumes
the local IPC endpoint created by the SimpleVoiceChat client and exposes one input source type:
`SimpleVoiceChat Player Voice`.

Add one source instance and route it to the desired OBS track. It contains
only received player voice. Keep OBS microphone, game capture, desktop audio,
and music as their own OBS sources. Individual player WAV files are written by
the mod for editing and subtitles, so the OBS track count never grows with the
number of speakers.

OBS cannot start the mod's multi-track session. Open the mod's multi-track
panel with `Ctrl + F9`, wait for the active recording status, and stop it
in-game to finalize the individual WAV files. For a single-player test, the
completed session contains only `local.wav`; remote player WAV files appear
only when other players speak during an active multi-track session.

The GitHub Actions workflow builds packages for Windows x64, Linux x86_64,
macOS x86_64, and macOS arm64 against OBS Studio 32.0.4. Install the
platform-matched package by extracting it into the OBS installation root:

- Windows: `obs-plugins/64bit/simplevoicechat_obs.dll`
- Linux: retain the packaged `lib/.../obs-plugins` path under the OBS prefix
- macOS: copy `PlugIns/simplevoicechat_obs.plugin` to `OBS.app/Contents/PlugIns`

Restart OBS. Add `SimpleVoiceChat Player Voice` once from the Sources menu,
then assign it to the desired recording track in Advanced Audio Properties.
The plugin and the game client must run as the same local user because their
IPC endpoint is local. The multi-track session and OBS recording must overlap;
either may start first. On OBS recording stop, the plugin waits for the
session's final WAV files and `obs-sync.json`, then automatically creates two
files beside the OBS video:

- `<video>-<session>-multitrack.mkv` keeps the OBS video and existing audio
  streams and adds one unprocessed PCM stream per player.
- `<video>-<session>-multitrack.fcpxml` references the original OBS video and
  each player WAV at the exact offset in `obs-sync.json`.

The session's `obs-export.json` reports `waiting`, `exporting`, `completed`,
or `failed`, including output paths and a failure message. Keep OBS open until
it reports `completed`. In DaVinci Resolve use **File > Import Timeline >
Import FCPXML** and select the generated FCPXML; the imported timeline already
contains a separate, synchronized player track. The MKV is an archive or
multitrack playback artifact, not the recommended Resolve editing input.

Build with an OBS Studio development environment, for example:

```powershell
cmake -S . -B build -Dlibobs_DIR=<OBS SDK CMake package directory>
cmake --build build --config Release
```

Install the generated module and its data directory using OBS's normal plugin
layout. The current workspace does not include the OBS SDK, so the plugin
source is not built by the mod project.

The IPC protocol is owned by the mod. Windows uses the
`simplevoicechat-audiobuses` named pipe. Linux and macOS use
`simplevoicechat-audiobuses.sock` in `XDG_RUNTIME_DIR` when available, else
the process temporary directory. Normal frames have a 22-byte header:
`SVCB`, protocol version 2, bus byte `0`, local monotonic timestamp (`Int64`),
sample rate (`Int32`), sample count (`Int32`), then mono PCM16 samples. A
`0x7F` bus byte indicates a recording-session marker: the continuation
contains the server-clock WAV zero, UTC zero, UTF-8 ID length and session ID,
then UTF-8 session-directory length and the absolute session directory.
The plugin replies with an `SVCA` acknowledgement containing the actual OBS
recording UTC start. The mod writes that acknowledgement to `obs-sync.json`
and merges the resulting alignment offset into the session manifest.
