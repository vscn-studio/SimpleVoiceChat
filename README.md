# SimpleVoiceChat / 简单语音对话

SimpleVoiceChat `1.2.4` is a client-and-server voice chat mod for Vintage Story `1.22.3`. It provides proximity voice, custom channels, server-hosted multi-track recording, moderation, optional speech-to-chat, and optional VS Director capture.

SimpleVoiceChat `1.2.4` 是适用于 Vintage Story `1.22.3` 的客户端/服务端语音模组，提供接近度语音、自定义频道、服务器托管多人分轨录音、管理功能、可选语音转文字聊天以及可选 VS Director 录制集成。

- [中文说明](#中文说明)
- [English](#english)
- [中文 HTML 使用与管理指南](docs/简单语音对话-使用与管理指南.html)
- [English HTML User and Administration Guide](docs/SimpleVoiceChat-User-and-Administration-Guide.html)

## 中文说明

### 主要功能

- 接近度语音支持耳语、正常说话和大喊，距离由服务器配置。
- 可将声音发送到接近度范围、当前频道或两者。
- 自定义频道支持开放、密码和隐藏可见性，以及所有者、主持人、成员、只听和封禁角色。
- 支持按键说话、语音触发通话（自由麦）、输入/输出设备选择、增益、噪声门、玩家单独音量与静音。
- 首选 Opus 编码；服务器可选择是否允许 ADPCM 回退。客户端和服务器必须使用协议 V6 兼容版本。
- 可在本机进行麦克风试听，并主动保存仅输入或输入+输出 WAV；多人分轨由服务器权威托管。
- 语音识别默认关闭，完全由玩家在客户端配置，不经过 SimpleVoiceChat 服务端。
- VS Director 是可选集成，不是前置模组，也不需要单独的集成模组。

### 安装

1. 关闭 Vintage Story 客户端和服务器。
2. 删除 `Mods` 目录中的旧版 SimpleVoiceChat 压缩包，避免同时加载多个版本。
3. 将 `SimpleVoiceChat-v1.2.4.zip` 原样放入客户端和服务器的 `Mods` 目录，不要解压模组包。
4. 启动服务器，然后启动客户端。首次按 `'` 会打开设置向导。

SimpleVoiceChat 不依赖 Simple Voice Chat、VS Director 或 `SimpleVoiceChat_VSDirectorIntegration` 等其他模组。单独安装即可使用基本语音功能。

### 默认快捷键

| 功能 | 默认按键 |
| --- | --- |
| 按住说话 | `N` |
| 按键说话/自由麦切换 | `Alt + N` |
| 切换耳语/正常/大喊 | `[` 或 `]` |
| 本地麦克风静音 | `Ctrl + -` |
| 拒听/恢复全部语音 | `;` |
| 打开设置 | `'` |
| 打开多人分轨设置（管理员） | `Ctrl + F9` |
| 语音转文字聊天 | 按住 `V` 录音，松开识别并发送 |

快捷键可在 Vintage Story 的游戏按键设置中修改。语音识别页面不另设快捷键输入框。

### 语音识别

进入 SimpleVoiceChat 设置，点击“语音识别”，选择服务商并填写当前服务商需要的配置。该功能默认关闭；开启后，按住 `V` 录音，松开后将识别文字发送到当前聊天频道。切换下拉菜单时，每个服务商的 API Key、模型、接口地址或本地路径都会分别保存。

| 服务商 | 类型 | 默认模型或路径要求 | 默认接口 |
| --- | --- | --- | --- |
| 阿里百炼 | 云端 | `qwen3-asr-flash` | `https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions` |
| 硅基流动 | 云端 | `FunAudioLLM/SenseVoiceSmall` | `https://api.siliconflow.cn/v1/audio/transcriptions` |
| Deepgram | 云端 | `nova-3` | `https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true` |
| Whisper | 本地 | Whisper.cpp GGML `.bin` 模型文件路径 | 无 |

相关文档与模型下载：

- [阿里百炼语音识别文档](https://bailian.console.aliyun.com/cn-beijing?tab=doc#/doc/?type=model&url=2989727)
- [SiliconFlow Audio Transcriptions API](https://api-docs.siliconflow.cn/docs/api/audio-transcriptions-post)
- [Deepgram API 入门](https://developers.deepgram.com/guides/fundamentals/make-your-first-api-request)
- [Whisper.cpp 模型](https://huggingface.co/ggerganov/whisper.cpp/tree/main)

Whisper 页面通常直接提供模型文件；填写实际 `.bin` 文件路径，而不是文件夹。如果浏览器下载的是压缩包，也要先解压。模型可放在任意玩家有读取权限的位置，Windows、Linux 和 macOS 均使用本平台正常路径格式。

云端服务的 API Key 以明文保存在玩家本机的 `SimpleVoiceChat.Client.json` 中，请勿分享该文件。云端识别会把本次录音直接发送给所选服务商；SimpleVoiceChat 服务端不会代理或保存该请求。Whisper 在本机运行，无需 API Key，也不会将识别音频发送给识别服务商。

### 频道和录音

频道具有稳定的 `channel-数字` ID。普通玩家默认可创建频道，服务器可关闭该权限。频道所有者可管理成员、角色、锁定状态和频道生命周期；服务器管理员使用 `controlserver` 权限执行全服管理。

主页录音按钮可保存“仅输入”或“输入+输出”WAV。Windows 默认位置为：

```text
%APPDATA%\VintagestoryData\ModData\SimpleVoiceChat
```

设置页的“麦克风测试”只保存在内存中，不会生成文件或发送到服务器。

### 可选 VS Director 集成

SimpleVoiceChat 和 VS Director 可以独立安装。两者同时存在时，SimpleVoiceChat 会在运行时检测 `VSDirectorModSystem.VoiceApi`；不需要 `SimpleVoiceChat_VSDirectorIntegration`，主程序集也不引用 VS Director。

服务器所有者需要在 `SimpleVoiceChat.Server.json` 中显式启用：

```json
{
  "EnableDirectorProximityCapture": true,
  "MaxDirectorListeners": 1,
  "MaxDirectorStreamsPerListener": 32,
  "MaxDirectorEgressKbps": 4096
}
```

VS Director 自身也必须启用对应的回放或离屏语音捕获设置。回放区域捕获期间，位于活动导演监听器回放区块范围内的所有发言都会进入导演音轨，包括频道目标语音；没有活动回放区域时，导演捕获仍按耳语/正常/大喊距离工作。

区域捕获使用独立的 `MaxDirectorEgressKbps`（默认 4096）和最多 32 条导演语音流，不会被普通玩家监听器的带宽预算截断。

SimpleVoiceChat 服务端会转发压缩语音帧，但本模组不提供端到端加密。玩家主动录音或 VS Director 录制可能保存语音内容，应遵守服务器和参与者的隐私规则。

### OBS 与多人分轨录制

录音模式提供“仅输入”、“输入+输出”和“多人分轨”。多人分轨的权威 WAV 和 `session.core.json` 由服务器写入服务器数据目录的 `ModData/SimpleVoiceChat/Recordings`；管理员客户端只保存 OBS 标记和下载缓存。每位说话人各有一条 `玩家名-UID.wav`，所有 WAV 都补齐到服务器统一时间轴。下载完成后客户端把 `obs-sync.json` 合并为 `session.json`。

#### 服务器端启用

先启动服务器一次，让 Vintage Story 生成配置文件；在服务器数据目录的 `ModConfig/SimpleVoiceChat.Server.json` 中修改现有字段。不要创建第二个同名字段，也不要用下面的片段覆盖其他服务器设置：

```json
{
  "EnableRecorderCapture": true,
  "RecorderCheckpointSeconds": 5,
  "MaxRecorderSessionMinutes": 360,
  "MaxRecorderClockSkewMilliseconds": 2000,
  "MaxRecorderDownloadKbps": 8192
}
```

保存后，以拥有 `controlserver` 的管理员执行 `/svc reload`，或重启服务器。开始前所有已握手参与者必须报告至少三个稳定的 NTP 风格时钟样本，并通过 UTC 偏差检查；状态窗口会显示就绪人数、音轨数和缺失帧。录制帧通过可靠控制通道上传，服务器解码并持续 checkpoint WAV 和 `recording-state.json`。录音管理员崩溃、断开或重连都不会停止会话；任意在线管理员都可以停止。服务器重启会修复活动会话的 WAV 头、补齐轨道并标记为 `recovered`。

多人分轨是管理员专用功能：录音客户端必须拥有 `controlserver`。按 `Ctrl + F9` 打开设置，等待参与者状态就绪后点击开始。管理员也可使用 `/svc recording start|stop|status|list|download <session-id>`。停止时服务器先完成最终写盘，再发送结束时间线和文件分块；客户端收到全部 WAV、`session.core.json` 和 `recording-state.json` 后才生成 `session.json`。不要在服务器完成前手工导出。16 kHz 单声道 PCM 每位玩家约占 115 MB/小时；请预留服务器磁盘和下载带宽。

单人游戏也可测试该流程；单人客户端的上传仍由内置服务器托管。玩家必须实际发送语音才会生成对应的 `玩家名-UID.wav`；没有任何语音帧时会话不会提供可下载 WAV。网络中断会在清单中记录连接事件和序列缺口，无法凭空恢复断线期间从未上传的音频，但不会造成其他音轨位移。OBS 的 `SimpleVoiceChat Player Voice` 仍只提供一条混合总线，不会增加 OBS 固定音轨数。

#### OBS 安装与同步

解压与系统匹配的插件包到 OBS 安装根目录：Windows 会得到 `obs-plugins/64bit/simplevoicechat_obs.dll`；Linux 保留包内的 `lib/.../obs-plugins` 路径；macOS 将 `PlugIns/simplevoicechat_obs.plugin` 放入 `OBS.app/Contents/PlugIns`。重启 OBS 后，在“来源”中添加一次 `SimpleVoiceChat Player Voice`，并在高级音频属性中把它分配给所需的 OBS 音轨。麦克风、游戏、桌面音频和音乐仍由 OBS 自己分别采集。

模组对本机 OBS 插件提供唯一的 PCM 总线：`PlayerVoice`。Windows 通过命名管道 `simplevoicechat-audiobuses` 输出 16 kHz 单声道 PCM16 帧；Linux 和 macOS 使用同名协议的本地 Unix socket，优先位于 `XDG_RUNTIME_DIR`，否则使用当前临时目录。服务器与 OBS 主机必须使用 NTP 保持 UTC 接近；多人分轨会话和 OBS 录制必须有重叠时间，启动先后不限。插件会回传实际 OBS 录制 UTC 起点，模组将其写入会话目录的 `obs-sync.json` 并合并到 `session.json` 的 `obsAlignment`。服务器崩溃后的 `recovered` 会话可用 `/svc recording list` 查看，再用 `/svc recording download <session-id>` 拉回管理员客户端。

停止 OBS 录制后，插件会取得 OBS 实际写出的原视频文件，并等待该会话的 `session.json`、`obs-sync.json` 和所有 WAV 完成。随后自动在原视频同目录生成 `<视频名>-<会话ID>-multitrack.mkv` 与同名 `.fcpxml`：MKV 保留 OBS 的原视频和原有音频流，并增加每位玩家一条原始 PCM 音频流；FCPXML 直接引用原 OBS 视频和逐玩家 WAV，以 `obs-sync.json` 的精确毫秒偏移创建独立音轨。会话目录的 `obs-export.json` 记录 `waiting`、`exporting`、`completed` 或 `failed` 状态、输出路径和错误原因。完成前请保持 OBS 打开。

在 DaVinci Resolve 中使用“文件 > 导入时间线 > 导入 FCPXML”，选择自动生成的 `.fcpxml`。不要先把 WAV 手工拖进时间线；导入后视频和每位玩家的独立音轨已经同步，可以分别调音、剪辑和设置字幕颜色。MKV 适合归档、检查或交给支持多音轨的播放器；Resolve 的推荐剪辑入口始终是 FCPXML。OBS 音轨数量不会随玩家数量增长。OBS 插件工作流会生成 Windows x64、Linux x86_64、macOS x86_64 和 macOS arm64 的独立安装包。

### 常用命令

客户端：

```text
/svc status
/svc volume <0-200>
/svc volumeplayer <玩家> <0-200>
/svc mute <玩家>
/svc unmute <玩家>
/svc channelinvite <玩家>
/svc channelleave [频道ID]
/svc channel
/svc diag
```

服务器管理员可使用 `/svc enable`、`/svc disable`、`/svc reload`、`/svc setrange`、频道管理、玩家管制、诊断、指标和审计命令。完整参数见中文 HTML 指南。

### 配置文件

- `SimpleVoiceChat.Client.json`：本机设备、音量、快捷方式、语音识别服务商配置和每服务器偏好。
- `SimpleVoiceChat.Server.json`：范围、频道、容量、路由和 VS Director 捕获策略。
- `SimpleVoiceChat.Audit.json`：服务器管理操作审计，不记录语音内容。

## English

### Features

- Proximity voice with server-configured whisper, talk, and shout ranges.
- Transmit to proximity, the selected custom channel, or both.
- Open, password-protected, and hidden channels with Owner, Moderator, Member, Listen Only, and Banned roles.
- Push-to-talk, voice activation, input/output device selection, gain, noise gate, per-player volume, and local mute.
- Opus is preferred; the server may optionally allow ADPCM fallback. Compatible V6 builds are required on both sides.
- In-memory microphone testing plus input-only, input-and-output, and server-hosted administrator multi-track WAV recording.
- Optional client-side speech-to-chat, disabled by default and never processed by the SimpleVoiceChat server.
- Optional runtime VS Director integration without a hard dependency or a separate integration mod.

### Installation

1. Stop the Vintage Story client and server.
2. Remove older SimpleVoiceChat archives from each `Mods` directory so that only one version can load.
3. Place `SimpleVoiceChat-v1.2.4.zip` unchanged in the client and server `Mods` directories. Do not extract the mod archive.
4. Start the server and client. Press `'` to open the first-run setup wizard.

SimpleVoiceChat does not require Simple Voice Chat, VS Director, or `SimpleVoiceChat_VSDirectorIntegration`. The base voice features work with this package alone.

### Default Keys

| Action | Default key |
| --- | --- |
| Hold to talk | `N` |
| Toggle push-to-talk / voice activation | `Alt + N` |
| Cycle whisper / talk / shout | `[` or `]` |
| Mute the local microphone | `Ctrl + -` |
| Deafen / restore all received voice | `;` |
| Open settings | `'` |
| Open multi-track settings (administrator) | `Ctrl + F9` |
| Speech-to-chat | Hold `V`, then release to transcribe and send |

Bindings can be changed in Vintage Story's game key settings. The Speech Recognition page does not provide a separate key-binding field.

### Speech Recognition

Open SimpleVoiceChat settings, select Speech Recognition, choose a provider, and enter that provider's configuration. Recognition is disabled by default. When enabled, hold `V` to record; releasing the key transcribes the recording and sends the text to the active chat channel. API keys, models, endpoints, and paths are retained separately when switching providers.

| Provider | Type | Default model or path | Default endpoint |
| --- | --- | --- | --- |
| Alibaba Bailian | Cloud | `qwen3-asr-flash` | `https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions` |
| SiliconFlow | Cloud | `FunAudioLLM/SenseVoiceSmall` | `https://api.siliconflow.cn/v1/audio/transcriptions` |
| Deepgram | Cloud | `nova-3` | `https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true` |
| Whisper | Local | Whisper.cpp GGML `.bin` model file | None |

Documentation and model downloads:

- [Alibaba Bailian speech recognition](https://bailian.console.aliyun.com/cn-beijing?tab=doc#/doc/?type=model&url=2989727)
- [SiliconFlow Audio Transcriptions API](https://api-docs.siliconflow.cn/docs/api/audio-transcriptions-post)
- [Deepgram API guide](https://developers.deepgram.com/guides/fundamentals/make-your-first-api-request)
- [Whisper.cpp models](https://huggingface.co/ggerganov/whisper.cpp/tree/main)

Whisper downloads are normally model files; select the actual `.bin` file, not its parent directory. Extract it first if it was distributed in an archive. Models may be stored in any readable location using normal Windows, Linux, or macOS path syntax.

Cloud API keys are stored as plain text in the local `SimpleVoiceChat.Client.json`; do not share that file. Cloud recognition sends each captured recording directly to the selected provider. The SimpleVoiceChat server neither proxies nor stores those requests. Whisper runs locally, requires no API key, and does not upload recognition audio to a provider.

### Channels and Recording

Channels use stable `channel-number` IDs. Ordinary players may create channels by default, although the server can disable that permission. Channel owners manage members, roles, locking, and lifecycle; server administrators use the `controlserver` privilege for server-wide actions.

The home-page recording button offers Input Only, Input+Output, and Multi-track speakers. Input+Output stores the microphone and received playback as separate stereo channels. Multi-track WAV files are authoritative on the server under `ModData/SimpleVoiceChat/Recordings`; the client keeps only an OBS marker and a download cache. On Windows, the default local cache is:

```text
%APPDATA%\VintagestoryData\ModData\SimpleVoiceChat
```

Microphone Test is memory-only and neither creates a file nor sends audio to the server.

### OBS and Multi-track Recording

#### Enable it on the server

Start the server once so Vintage Story creates the configuration, then edit the existing fields in `ModConfig/SimpleVoiceChat.Server.json` under the server data directory. Do not add duplicate fields or replace the server's other settings with this example:

```json
{
  "EnableRecorderCapture": true,
  "RecorderCheckpointSeconds": 5,
  "MaxRecorderSessionMinutes": 360,
  "MaxRecorderClockSkewMilliseconds": 2000,
  "MaxRecorderDownloadKbps": 8192
}
```

Save the file, then run `/svc reload` as an administrator with `controlserver`, or restart the server. Before start, every handshaken participant must report at least three stable NTP-style clock samples and pass the UTC skew check. The panel reports ready participants, tracks, and missing frames. Encoded frames travel over the reliable control channel; the server decodes them and checkpoints WAV files plus `recording-state.json`. An administrator crash, disconnect, or reconnect does not stop the session, and any online administrator can stop it. A server restart repairs WAV headers, pads tracks, and marks an interrupted session `recovered`.

Multi-track recording is administrator-only and requires `controlserver`. `Ctrl + F9` opens the panel; start only after the participant status is ready. Administrators can also use `/svc recording start|stop|status|list|download <session-id>`. The server finalizes files before sending the end timeline and chunks. The client creates `session.json` only after all WAV files, `session.core.json`, and `recording-state.json` arrive. Do not export before that point. 16 kHz mono PCM uses about 115 MB per player-hour; reserve server disk and download bandwidth.

Single-player worlds use the same server-hosted workflow. A speaker must actually transmit voice to create a `PlayerName-UID.wav`; a session with no uploaded audio has no downloadable WAV. Disconnects are recorded as connection events and sequence gaps. Audio that never reached the server cannot be reconstructed, but other tracks remain aligned. OBS `SimpleVoiceChat Player Voice` still exposes one mixed player-voice bus and never increases the fixed OBS track count.

#### Install and synchronize with OBS

Extract the platform-matched plugin package to the OBS installation root: Windows produces `obs-plugins/64bit/simplevoicechat_obs.dll`; Linux keeps the package's `lib/.../obs-plugins` path; macOS places `PlugIns/simplevoicechat_obs.plugin` in `OBS.app/Contents/PlugIns`. Restart OBS, add `SimpleVoiceChat Player Voice` once under Sources, then assign it to the desired OBS track in Advanced Audio Properties. Keep microphone, game audio, desktop audio, and music on their normal OBS sources.

The OBS plugin exposes exactly one `PlayerVoice` source through local IPC. Windows uses the `simplevoicechat-audiobuses` named pipe; Linux and macOS use the same-protocol Unix socket, preferring `XDG_RUNTIME_DIR` and otherwise the current temporary directory. The server and OBS host should use NTP so their UTC clocks remain close. The multi-track session and OBS recording must overlap, in either start order. The plugin returns the actual OBS recording UTC start; the mod writes `obs-sync.json` and `session.json.obsAlignment`. Recovered sessions can be listed with `/svc recording list` and downloaded with `/svc recording download <session-id>`.

After OBS stops recording, the plugin obtains the final OBS video file and waits for the session's finalized `session.json`, `obs-sync.json`, and player WAV files. It then creates `<video>-<session>-multitrack.mkv` and a matching `.fcpxml` beside the OBS video. The MKV retains the OBS video and existing audio streams and adds one raw PCM stream per player. The FCPXML references the original OBS video and every WAV, placing each on an independent synchronized track from the exact `obs-sync.json` offset. `obs-export.json` in the session directory records `waiting`, `exporting`, `completed`, or `failed`, output paths, and an error if applicable. Keep OBS running until the status becomes `completed`.

In DaVinci Resolve, choose **File > Import Timeline > Import FCPXML** and select the generated `.fcpxml`. The video and all player tracks arrive aligned, ready for per-player mixing, cutting, and subtitle colors. The MKV is intended for archival or multitrack playback; use the FCPXML as the Resolve editing entry point. OBS track count does not grow with the number of speakers. The OBS plugin workflow publishes separate Windows x64, Linux x86_64, macOS x86_64, and macOS arm64 packages.

### Optional VS Director Integration

SimpleVoiceChat and VS Director remain independently installable. When both are present, SimpleVoiceChat discovers `VSDirectorModSystem.VoiceApi` at runtime. `SimpleVoiceChat_VSDirectorIntegration` is not needed, and the main assembly does not reference VS Director.

The server owner must explicitly enable capture in `SimpleVoiceChat.Server.json`:

```json
{
  "EnableDirectorProximityCapture": true,
  "MaxDirectorListeners": 1,
  "MaxDirectorStreamsPerListener": 32,
  "MaxDirectorEgressKbps": 4096
}
```

The matching replay or offscreen voice option must also be enabled in VS Director. During replay-region capture, every speaker inside the active listener's replay chunk region enters director tracks, including channel-targeted audio. Without an active replay region, director capture keeps the normal whisper/talk/shout range.

The server forwards compressed voice frames, and the mod does not provide end-to-end encryption. Player-initiated or VS Director recording can preserve voice content and should follow the server's and participants' privacy rules.

### Common Commands

Client commands:

```text
/svc status
/svc volume <0-200>
/svc volumeplayer <player> <0-200>
/svc mute <player>
/svc unmute <player>
/svc channelinvite <player>
/svc channelleave [channel-id]
/svc channel
/svc diag
```

Server administrators can use `/svc enable`, `/svc disable`, `/svc reload`, `/svc setrange`, channel administration, player moderation, diagnostics, metrics, and audit commands. See the English HTML guide for complete parameters.

### Configuration Files

- `SimpleVoiceChat.Client.json`: local devices, levels, input preferences, speech-recognition provider settings, and per-server preferences.
- `SimpleVoiceChat.Server.json`: ranges, channels, capacity, routing, and VS Director capture policy.
- `SimpleVoiceChat.Audit.json`: server administration events; it does not contain voice content.

## Build and Verification

```powershell
dotnet test Tests\SimpleVoiceChat.Tests.csproj
dotnet build SimpleVoiceChat.csproj -c Release
```

Release output is written to `bin\Release\Mods\mod`. Unmanaged Whisper libraries must remain under `native/`; Vintage Story rejects unmanaged DLLs placed in the mod root.
