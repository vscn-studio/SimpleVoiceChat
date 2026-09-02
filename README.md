# SimpleVoiceChat / 简单语音对话

SimpleVoiceChat `1.2.7-pre.2` is a client-and-server voice chat mod for Vintage Story `1.22.3`. It provides proximity voice, custom channels, server-hosted multi-track recording, moderation, and optional VS Director capture. Speech-to-chat is provided by the separate `SimpleVoiceChatASR` client mod.

SimpleVoiceChat `1.2.7-pre.2` 是适用于 Vintage Story `1.22.3` 的客户端/服务端语音模组，提供接近度语音、自定义频道、服务器托管多人分轨录音、管理功能以及可选 VS Director 录制集成。语音转文字由独立的 `SimpleVoiceChatASR` 客户端模组提供。

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
- 本发行包不内置 RNNoise 原生库；噪声抑制选项在无外部后端时自动回退到内置 AGC/噪声门处理。
- 全链路使用 48 kHz 单声道、20 ms 帧和 Opus；默认 24 Kbps，自适应范围为 12-48 Kbps。客户端和服务器必须使用协议 V10 兼容版本，旧版 V9 不互通。
- 接近度语音在客户端按距离平滑衰减到静音；频道/群组语音不受距离衰减影响，服务端转发范围不会扩大。
- 可选的公共聊天距离可视：开启后，普通公共聊天只会显示给同维度且处于“聊天可视距离”内的玩家，发言者始终能看到自己的消息。
- 可在本机进行麦克风试听，并主动保存仅输入或输入+输出 WAV；多人分轨由服务器权威托管。
- 语音识别由独立的 `SimpleVoiceChatASR` 客户端模组提供，不属于主模组。
- VS Director 是可选集成，不是前置模组，也不需要单独的集成模组。

### 安装

1. 关闭 Vintage Story 客户端和服务器。
2. 删除 `Mods` 目录中的旧版 SimpleVoiceChat 压缩包，避免同时加载多个版本。
3. 将 `SimpleVoiceChat-v1.2.7-pre.2.zip` 原样放入客户端和服务器的 `Mods` 目录，不要解压模组包。
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

快捷键可在 Vintage Story 的游戏按键设置中修改。

### 主设置窗口扩展（客户端 API）

其他客户端模组可以通过 `SimpleVoiceChatModSystem.ClientSettingsExtensions` 向主页注册文字按钮、图片按钮或自定义控件。控件显示在“显示/隐藏 HUD”所在的快捷控制行下方，按 `Order` 排序；每行会根据控件的 `PreferredWidth` 自动排列，放不下时自动换行。扩展控件高度规范在 28-96px，最小宽度为 28px；图片按钮默认使用 42px 的方形尺寸。主页高度会根据实际行高计算，超过可视区域的内容可以用鼠标滚轮滚动。`IsVisible` 可以在运行时切换显示状态。

```csharp
var voiceChat = api.ModLoader.GetModSystem<SimpleVoiceChatModSystem>();
voiceChat.ClientSettingsExtensions.RegisterButton(
    new VoiceSettingsExtensionButton(
        "example.button",
        "Example",
        () => OpenExample(),
        order: 100,
        preferredWidth: 160));
```

需要使用与首页快捷按钮一致的图片按钮时，可以直接注册 `VoiceSettingsExtensionImageButton`：

```csharp
voiceChat.ClientSettingsExtensions.RegisterControl(
    new VoiceSettingsExtensionImageButton(
        "example.image",
        new AssetLocation("example", "gui/icon.png"),
        () => OpenExample(),
        order: 100));
```

也可以注册与主窗口风格一致的独立扩展窗口。窗口由 SimpleVoiceChat 居中显示，提供标题、关闭按钮、内容裁剪和 4px 圆角背景；扩展按钮和其他控件保持直角。第三方模组只负责在 `Compose` 回调中添加内容：

```csharp
voiceChat.ClientSettingsExtensions.RegisterWindow(
    new VoiceSettingsExtensionWindow(
        "example.window",
        "Example",
        context => context.Composer.AddStaticText(
            "Content",
            CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(0, 0, context.ContentWidth, 30))));

voiceChat.ClientSettingsExtensions.ShowWindow("example.window");
```

上述 API 仅在客户端可用；注册 ID 只能包含字母、数字、`.`、`_` 和 `-`。关闭主设置窗口时，已打开的扩展窗口也会被释放。

### 语音转文字

如需语音转文字，请额外安装 `SimpleVoiceChatASR`。它负责本地 Whisper、模型路径和 `V` 快捷键，不会增加主模组的云端服务或配置负担。

### 频道和录音

频道具有稳定的 `channel-<number>` ID。普通玩家默认可创建频道，服务器可关闭该权限。频道所有者可管理成员、角色、锁定状态和频道生命周期；服务器管理员使用 `controlserver` 权限执行全服管理。

主页录音按钮可保存“仅输入”或“输入+输出”WAV。Windows 默认位置为：

```text
%APPDATA%\VintagestoryData\ModData\SimpleVoiceChat
```

设置页的“麦克风测试”只保存在内存中，不会生成文件或发送到服务器。

### 水下与装备语音效果

水下状态和头盔/面具规则由服务器判定。装备规则只保存在服务器的 `ModConfig/SimpleVoiceChat.Server.json`，不会发送给客户端，也不能由普通玩家修改。服务器生成的默认规则如下；`Slot` 的 `0/1/2` 分别表示 `Head/Face/ArmorHead`，`Effect` 的 `0/1` 分别表示 `Helmet/Mask`，规则按顺序首个命中生效：

```json
{
  "EnableEnvironmentalVoiceEffects": true,
  "ApplyUnderwaterEffectsToChannels": false,
  "EquipmentVoiceEffectRules": [
    { "Slot": 2, "ItemCodePattern": "armor-head-*", "Effect": 0 },
    { "Slot": 1, "ItemCodePattern": "clothes-face-*mask*", "Effect": 1 }
  ]
}
```

物品代码支持 `*` 和 `?` 通配符；不写域名时会匹配任意域名下的物品路径。修改后执行 `/svc reload` 或重启服务器。水下默认只影响接近度语音；玩家设置中的“环境语音效果”只控制本机播放，不会改变服务器装备规则。多人分轨 WAV 保留未处理语音。

### 接近度距离渐变

接近度语音在客户端播放时使用距离增益：近距离保持正常音量，接近模式范围边界时逐渐降低，到达边界时静音。服务端仍只转发配置范围内的接收者（空间查询带约 1 格缓冲），因此不会增加网络流量。频道语音不使用距离渐变；选择“接近度和当前频道”时，服务端对同时满足两种条件的接收者优先选择频道路径，其他接近度接收者使用渐变。距离增益会与总音量、玩家音量、静音/拒听和环境效果相乘。

### 公共聊天距离可视

管理员可在管理员设置页打开“仅显示附近聊天”，并设置“聊天可视距离”（1-128 格）。也可以在 `SimpleVoiceChat.Server.json` 中设置 `EnableProximityChatText` 和 `ProximityChatRange`。该功能只影响普通公共聊天；频道聊天、命令、系统通知和管理员消息不受影响。

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

多人分轨是管理员专用功能：录音客户端必须拥有 `controlserver`。按 `Ctrl + F9` 打开设置，等待参与者状态就绪后点击开始。管理员也可使用 `/svc recording start|stop|status|list|download <session-id>`。停止时服务器先完成最终写盘，再发送结束时间线和文件分块；客户端收到全部 WAV、`session.core.json` 和 `recording-state.json` 后才生成 `session.json`。不要在服务器完成前手工导出。48 kHz 单声道 PCM 每位玩家约占 345 MB/小时；请预留服务器磁盘和下载带宽。

单人游戏也可测试该流程；单人客户端的上传仍由内置服务器托管。玩家必须实际发送语音才会生成对应的 `玩家名-UID.wav`；没有任何语音帧时会话不会提供可下载 WAV。网络中断会在清单中记录连接事件和序列缺口，无法凭空恢复断线期间从未上传的音频，但不会造成其他音轨位移。OBS 的 `SimpleVoiceChat Player Voice` 仍只提供一条混合总线，不会增加 OBS 固定音轨数。

#### OBS 安装与同步

解压与系统匹配的插件包到 OBS 安装根目录：Windows 会得到 `obs-plugins/64bit/simplevoicechat_obs.dll`；Linux 保留包内的 `lib/.../obs-plugins` 路径；macOS 将 `PlugIns/simplevoicechat_obs.plugin` 放入 `OBS.app/Contents/PlugIns`。重启 OBS 后，在“来源”中添加一次 `SimpleVoiceChat Player Voice`，并在高级音频属性中把它分配给所需的 OBS 音轨。麦克风、游戏、桌面音频和音乐仍由 OBS 自己分别采集。

模组对本机 OBS 插件提供唯一的 PCM 总线：`PlayerVoice`。Windows 通过命名管道 `simplevoicechat-audiobuses` 输出 48 kHz 单声道 PCM16 帧；Linux 和 macOS 使用同名协议的本地 Unix socket，优先位于 `XDG_RUNTIME_DIR`，否则使用当前临时目录。服务器与 OBS 主机必须使用 NTP 保持 UTC 接近；多人分轨会话和 OBS 录制必须有重叠时间，启动先后不限。插件会回传实际 OBS 录制 UTC 起点，模组将其写入会话目录的 `obs-sync.json` 并合并到 `session.json` 的 `obsAlignment`。服务器崩溃后的 `recovered` 会话可用 `/svc recording list` 查看，再用 `/svc recording download <session-id>` 拉回管理员客户端。

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

- `SimpleVoiceChat.Client.json`：本机设备、音量、快捷方式和每服务器偏好。ASR 使用独立的 `SimpleVoiceChatASR.Client.json`。
- `SimpleVoiceChat.Server.json`：范围、公共聊天可视距离、频道、容量、路由和 VS Director 捕获策略。
- 频道名称创建/修改限制由服务端 `MaxChannelNameLength` 控制，默认 24，范围 1-128；调整只作用于之后的创建和重命名，不会改动已有频道名称。也可使用 `/svc channelnamelength <1-128>` 修改并广播配置。
- `SimpleVoiceChat.Audit.json`：服务器管理操作审计，不记录语音内容。

## English

### Features

- Proximity voice with server-configured whisper, talk, and shout ranges.
- Transmit to proximity, the selected custom channel, or both.
- Open, password-protected, and hidden channels with Owner, Moderator, Member, Listen Only, and Banned roles.
- Push-to-talk, voice activation, input/output device selection, gain, noise gate, per-player volume, and local mute.
- This release does not bundle an RNNoise native library; noise suppression falls back to the built-in AGC/gate processing when no external backend is available.
- The V10 protocol uses 48 kHz mono Opus only; compatible V10 builds are required on both sides. V9 clients are rejected.
- Proximity playback applies a client-side distance fade to silence at the configured boundary; channel/group voice bypasses that fade and server forwarding does not expand.
- Optional proximity visibility for public chat can limit ordinary chat messages to players in the same dimension and within a server-configured range; the sender always sees their own message.
- In-memory microphone testing plus input-only, input-and-output, and server-hosted administrator multi-track WAV recording.
- Speech-to-chat is provided by the separate `SimpleVoiceChatASR` client mod.
- Optional runtime VS Director integration without a hard dependency or a separate integration mod.

### Installation

1. Stop the Vintage Story client and server.
2. Remove older SimpleVoiceChat archives from each `Mods` directory so that only one version can load.
3. Place `SimpleVoiceChat-v1.2.7-pre.2.zip` unchanged in the client and server `Mods` directories. Do not extract the mod archive.
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

Bindings can be changed in Vintage Story's game key settings.

### Main-window extensions (client API)

Client-side mods can register buttons or custom controls through `SimpleVoiceChatModSystem.ClientSettingsExtensions`. They appear below the home-page quick-control row that contains the HUD visibility button. Controls are sorted by `Order`, sized from `PreferredWidth` and the measured button text, and wrapped to new rows when the available width is full. The home page is capped at 650px; additional rows are scrollable with the mouse wheel so controls cannot overlap or extend outside the window. `IsVisible` can be changed at runtime.

```csharp
var voiceChat = api.ModLoader.GetModSystem<SimpleVoiceChatModSystem>();
voiceChat.ClientSettingsExtensions.RegisterButton(
    new VoiceSettingsExtensionButton(
        "example.button",
        "Example",
        () => OpenExample(),
        order: 100,
        preferredWidth: 160));
```

Mods may also register an independently opened window. SimpleVoiceChat supplies the centered panel, title, close button, clipping, and the same 4px rounded background; extension buttons and other controls remain square, and the mod only composes the content:

```csharp
voiceChat.ClientSettingsExtensions.RegisterWindow(
    new VoiceSettingsExtensionWindow(
        "example.window",
        "Example",
        context => context.Composer.AddStaticText(
            "Content",
            CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(0, 0, context.ContentWidth, 30))));

voiceChat.ClientSettingsExtensions.ShowWindow("example.window");
```

These APIs are client-only. Registration IDs may contain letters, digits, `.`, `_`, and `-`. Closing the main settings dialog also releases an open extension window.

### Speech-to-Chat

For speech-to-chat, install `SimpleVoiceChatASR` separately. It owns local Whisper, model configuration, and the `V` binding; the main mod remains focused on real-time voice chat.

### Channels and Recording

Channels use stable `channel-<number>` IDs. Ordinary players may create channels by default, although the server can disable that permission. Channel owners manage members, roles, locking, and lifecycle; server administrators use the `controlserver` privilege for server-wide actions.

The home-page recording button offers Input Only, Input+Output, and Multi-track speakers. Input+Output stores the microphone and received playback as separate stereo channels. Multi-track WAV files are authoritative on the server under `ModData/SimpleVoiceChat/Recordings`; the client keeps only an OBS marker and a download cache. On Windows, the default local cache is:

```text
%APPDATA%\VintagestoryData\ModData\SimpleVoiceChat
```

Microphone Test is memory-only and neither creates a file nor sends audio to the server.

### Underwater and Equipment Voice Effects

The server determines underwater state and helmet/mask rules. Equipment rules exist only in the server's `ModConfig/SimpleVoiceChat.Server.json`; they are not sent to clients and ordinary players cannot change them. The generated defaults are shown below. `Slot` values `0/1/2` mean `Head/Face/ArmorHead`, `Effect` values `0/1` mean `Helmet/Mask`, and the first matching rule wins:

```json
{
  "EnableEnvironmentalVoiceEffects": true,
  "ApplyUnderwaterEffectsToChannels": false,
  "EquipmentVoiceEffectRules": [
    { "Slot": 2, "ItemCodePattern": "armor-head-*", "Effect": 0 },
    { "Slot": 1, "ItemCodePattern": "clothes-face-*mask*", "Effect": 1 }
  ]
}
```

Item codes support `*` and `?` wildcards. A pattern without a domain matches the item path in any domain. Run `/svc reload` or restart the server after editing. Underwater effects apply only to proximity voice by default. The player's Environmental Voice Effects switch controls local playback only and cannot alter server equipment rules. Multi-track WAV files retain unprocessed speech.

### Proximity Fade

During client playback, proximity voice keeps normal volume nearby, fades smoothly near the configured mode range, and reaches silence at the boundary. The server still forwards only within the configured range (with an approximately one-block spatial-query buffer), so the fade does not increase network traffic. Channel voice bypasses distance fading; with Proximity and Channel, recipients matching both conditions use the higher-priority channel path while other proximity recipients fade. Distance gain is multiplied with master/player volume, mute/deafen, and environment effects.

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

Multi-track recording is administrator-only and requires `controlserver`. `Ctrl + F9` opens the panel; start only after the participant status is ready. Administrators can also use `/svc recording start|stop|status|list|download <session-id>`. The server finalizes files before sending the end timeline and chunks. The client creates `session.json` only after all WAV files, `session.core.json`, and `recording-state.json` arrive. Do not export before that point. 48 kHz mono PCM uses about 345 MB per player-hour; reserve server disk and download bandwidth.

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

- `SimpleVoiceChat.Client.json`: local devices, levels, input preferences, and per-server preferences. ASR uses `SimpleVoiceChatASR.Client.json`.
- `SimpleVoiceChat.Server.json`: ranges, public-chat visibility, channels, capacity, routing, and VS Director capture policy.
- `SimpleVoiceChat.Audit.json`: server administration events; it does not contain voice content.

## Build and Verification

```powershell
dotnet test Tests\SimpleVoiceChat.Tests.csproj
dotnet build SimpleVoiceChat.csproj -c Release
```

The release is written to `bin\Release\Mods\mod`. Optional ASR is delivered by the separate `SimpleVoiceChatASR` client mod.
