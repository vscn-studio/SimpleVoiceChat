# SimpleVoiceChat OBS source

This is a Windows OBS Studio source plugin. It consumes the local named pipe
created by the SimpleVoiceChat client and exposes one input source type:
`SimpleVoiceChat Player Voice`.

Add one source instance and route it to the desired OBS track. It contains
only received player voice. Keep OBS microphone, game capture, desktop audio,
and music as their own OBS sources. Individual player WAV files are written by
the mod for editing and subtitles, so the OBS track count never grows with the
number of speakers.

Build with an OBS Studio development environment, for example:

```powershell
cmake -S . -B build -Dlibobs_DIR=<OBS SDK CMake package directory>
cmake --build build --config Release
```

Install the generated module and its data directory using OBS's normal plugin
layout. The current workspace does not include the OBS SDK, so the plugin
source is not built by the mod project.

The pipe protocol is owned by the mod. Normal frames have a 22-byte header:
`SVCB`, protocol version 1, bus byte `0`, local monotonic timestamp (`Int64`),
sample rate (`Int32`), sample count (`Int32`), then mono PCM16 samples. A
`0x7F` bus byte indicates a recording-session marker: the continuation
contains the server-clock WAV zero, UTC zero, UTF-8 ID length, and session ID.
The plugin replies with an `SVCA` acknowledgement containing the actual OBS
recording UTC start. The mod writes that acknowledgement to `obs-sync.json`
and merges the resulting alignment offset into the session manifest.
