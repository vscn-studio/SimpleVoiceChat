# SimpleVoiceChat / 简单语音对话

SimpleVoiceChat `1.0.14` is a client-and-server voice chat mod for Vintage Story `1.22.3`. It provides proximity voice, custom channels, local recording, moderation, optional speech-to-chat, and optional VS Director capture.

SimpleVoiceChat `1.0.14` 是适用于 Vintage Story `1.22.3` 的客户端/服务端语音模组，提供接近度语音、自定义频道、本地录音、管理功能、可选语音转文字聊天以及可选 VS Director 录制集成。

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
- 首选 Opus 编码；服务器可选择是否允许 ADPCM 回退。客户端和服务器必须使用协议 V3 兼容版本。
- 可在本机进行麦克风试听并主动保存输入或输入+输出 WAV 录音。
- 语音识别默认关闭，完全由玩家在客户端配置，不经过 SimpleVoiceChat 服务端。
- VS Director 是可选集成，不是前置模组，也不需要单独的集成模组。

### 安装

1. 关闭 Vintage Story 客户端和服务器。
2. 删除 `Mods` 目录中的旧版 SimpleVoiceChat 压缩包，避免同时加载多个版本。
3. 将 `SimpleVoiceChat_1.0.14.zip` 原样放入客户端和服务器的 `Mods` 目录，不要解压模组包。
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
| 语音转文字聊天 | 按住 `V` 录音，松开识别并发送 |

快捷键可在 Vintage Story 的游戏按键设置中修改。语音识别页面不另设快捷键输入框。

### 语音识别

进入 SimpleVoiceChat 设置，点击“语音识别”，选择服务商并填写当前服务商需要的配置。该功能默认关闭；开启后，按住 `V` 录音，松开后将识别文字发送到当前聊天频道。切换下拉菜单时，每个服务商的 API Key、模型、接口地址或本地路径都会分别保存。

| 服务商 | 类型 | 默认模型或路径要求 | 默认接口 |
| --- | --- | --- | --- |
| 阿里百炼 | 云端 | `qwen3-asr-flash` | `https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions` |
| 硅基流动 | 云端 | `FunAudioLLM/SenseVoiceSmall` | `https://api.siliconflow.cn/v1/audio/transcriptions` |
| Deepgram | 云端 | `nova-3` | `https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true` |
| Vosk | 本地 | 解压后的模型文件夹路径 | 无 |
| Whisper | 本地 | Whisper.cpp GGML `.bin` 模型文件路径 | 无 |

相关文档与模型下载：

- [阿里百炼语音识别文档](https://bailian.console.aliyun.com/cn-beijing?tab=doc#/doc/?type=model&url=2989727)
- [SiliconFlow Audio Transcriptions API](https://api-docs.siliconflow.cn/docs/api/audio-transcriptions-post)
- [Deepgram API 入门](https://developers.deepgram.com/guides/fundamentals/make-your-first-api-request)
- [Vosk 模型](https://alphacephei.com/vosk/models)
- [Whisper.cpp 模型](https://huggingface.co/ggerganov/whisper.cpp/tree/main)

Vosk 下载得到的压缩包必须先完整解压，然后填写包含 `am`、`conf` 等内容的模型目录。Whisper 页面通常直接提供模型文件；填写实际 `.bin` 文件路径，而不是文件夹。如果浏览器下载的是压缩包，也要先解压。模型可放在任意玩家有读取权限的位置，Windows、Linux 和 macOS 均使用本平台正常路径格式。

云端服务的 API Key 以明文保存在玩家本机的 `SimpleVoiceChat.Client.json` 中，请勿分享该文件。云端识别会把本次录音直接发送给所选服务商；SimpleVoiceChat 服务端不会代理或保存该请求。Vosk 和 Whisper 在本机运行，无需 API Key，也不会将识别音频发送给识别服务商。

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
- Opus is preferred; the server may optionally allow ADPCM fallback. Compatible V3 builds are required on both sides.
- In-memory microphone testing and explicit input-only or input-and-output WAV recording.
- Optional client-side speech-to-chat, disabled by default and never processed by the SimpleVoiceChat server.
- Optional runtime VS Director integration without a hard dependency or a separate integration mod.

### Installation

1. Stop the Vintage Story client and server.
2. Remove older SimpleVoiceChat archives from each `Mods` directory so that only one version can load.
3. Place `SimpleVoiceChat_1.0.14.zip` unchanged in the client and server `Mods` directories. Do not extract the mod archive.
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
| Speech-to-chat | Hold `V`, then release to transcribe and send |

Bindings can be changed in Vintage Story's game key settings. The Speech Recognition page does not provide a separate key-binding field.

### Speech Recognition

Open SimpleVoiceChat settings, select Speech Recognition, choose a provider, and enter that provider's configuration. Recognition is disabled by default. When enabled, hold `V` to record; releasing the key transcribes the recording and sends the text to the active chat channel. API keys, models, endpoints, and paths are retained separately when switching providers.

| Provider | Type | Default model or path | Default endpoint |
| --- | --- | --- | --- |
| Alibaba Bailian | Cloud | `qwen3-asr-flash` | `https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions` |
| SiliconFlow | Cloud | `FunAudioLLM/SenseVoiceSmall` | `https://api.siliconflow.cn/v1/audio/transcriptions` |
| Deepgram | Cloud | `nova-3` | `https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true` |
| Vosk | Local | Extracted model directory | None |
| Whisper | Local | Whisper.cpp GGML `.bin` model file | None |

Documentation and model downloads:

- [Alibaba Bailian speech recognition](https://bailian.console.aliyun.com/cn-beijing?tab=doc#/doc/?type=model&url=2989727)
- [SiliconFlow Audio Transcriptions API](https://api-docs.siliconflow.cn/docs/api/audio-transcriptions-post)
- [Deepgram API guide](https://developers.deepgram.com/guides/fundamentals/make-your-first-api-request)
- [Vosk models](https://alphacephei.com/vosk/models)
- [Whisper.cpp models](https://huggingface.co/ggerganov/whisper.cpp/tree/main)

Extract the complete Vosk archive and select the model directory containing folders such as `am` and `conf`. Whisper downloads are normally model files; select the actual `.bin` file, not its parent directory. Extract it first if it was distributed in an archive. Models may be stored in any readable location using normal Windows, Linux, or macOS path syntax.

Cloud API keys are stored as plain text in the local `SimpleVoiceChat.Client.json`; do not share that file. Cloud recognition sends each captured recording directly to the selected provider. The SimpleVoiceChat server neither proxies nor stores those requests. Vosk and Whisper run locally, require no API key, and do not upload recognition audio to a provider.

### Channels and Recording

Channels use stable `channel-number` IDs. Ordinary players may create channels by default, although the server can disable that permission. Channel owners manage members, roles, locking, and lifecycle; server administrators use the `controlserver` privilege for server-wide actions.

The home-page recording button saves input-only or input-and-output WAV files. On Windows, the default location is:

```text
%APPDATA%\VintagestoryData\ModData\SimpleVoiceChat
```

Microphone Test is memory-only and neither creates a file nor sends audio to the server.

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

Release output is written to `bin\Release\Mods\mod`. Unmanaged Whisper and Vosk libraries must remain under `native/`; Vintage Story rejects unmanaged DLLs placed in the mod root.
