# SimpleVoiceChat / 简单语音对话

Copyright © 2026 VSCN-Studio. `HansJack` is the founder of the VSCN-Studio team. See [LICENSE](LICENSE) for the project license.

SimpleVoiceChat `1.2.8-pre.1` is a client-and-server voice chat mod for Vintage Story `1.22.3`. It provides proximity voice, custom channels, server-hosted multi-track recording, moderation, optional speech-to-chat, and optional VS Director capture. The separate `SimpleVoiceChatASR` client package supplies Whisper runtime dependencies for local speech-to-chat.

SimpleVoiceChat `1.2.8-pre.1` 是适用于 Vintage Story `1.22.3` 的客户端/服务端语音模组，提供接近度语音、自定义频道、服务器托管多人分轨录音、管理功能、可选语音转文字以及可选 VS Director 录制集成。`SimpleVoiceChatASR` 客户端依赖包只提供本地语音识别所需的 Whisper 运行时文件。

- [中文说明](#中文说明)
- [English](#english)

## 中文说明

### 功能清单

以下清单按当前 `1.2.8-pre.1` 代码实现整理。默认值指新建配置；已有配置和服务器策略可能不同。OBS 插件、LauncherGo 和本地 Whisper 依赖包需要单独安装。

#### 语音通话与播放

- 支持按键说话和语音触发通话（自由麦），可分别调整噪声门和触发阈值。
- 支持耳语、正常说话和大喊；默认范围为 8、18、35 格，服务器最大范围默认 40 格。
- 可发送到接近度范围、当前频道或两者；同时满足频道与附近条件的接收者优先走频道路径，避免重复播放。
- 接近度语音具有空间定位和距离衰减，到范围边界时静音；频道语音不随距离衰减。
- 支持本地麦克风静音、拒听全部语音、指定玩家静音，以及总音量、频道音量和玩家单独音量。

#### 音频设备与处理

- 可选择默认或指定输入/输出设备；OpenAL 设备名按 UTF-8 枚举和打开，支持中文名称，并尝试精确迁移旧版保存的乱码名称。
- 麦克风采集包含高通滤波、自动增益、手动增益、软限幅和噪声门；可选 RNNoise 降噪。
- 降噪默认关闭。发行包内置 `YellowDogMan.RRNoise.NET 0.1.9` 的 Windows x64/x86、Linux x64/arm64 原生库；macOS 未内置该库，使用基础处理，也可从 `VintagestoryData\ModData\SimpleVoiceChat\native` 加载兼容外部库。
- 麦克风测试支持内存录音和试听，本地设备测试不生成文件、不向服务器发送音频；网页麦克风测试需经过网页桥接。
- 设备采集失败后会尝试恢复；指定播放设备不可用时回退到游戏音频设备。
- 当前未实现回声消除，保留的 `EnableEchoCancellation` 配置字段不代表该功能可用。

#### 环境效果与性能选项

- 支持方块遮挡、水下、头盔/面具、天气，以及根据实体状态计算的低时间稳定度和中毒音效。
- 水下与装备状态由服务器判定，装备匹配规则支持通配符；客户端可控制本机遮挡和环境效果，服务器可强制遮挡效果。
- 性能模式默认关闭；启用后，遮挡采样由 9 次降为 5 次，环境状态缓存由 150 ms 延长为 250 ms。它不改变音频驱动、48 kHz 采样率或 Opus 编码复杂度，也不扩大设备兼容范围。

#### 频道、玩家与界面

- RmlUi 提供首次设置向导、主页、音频设置、语音识别、频道、玩家、管理员、状态和录音界面；保留逐级返回逻辑。
- HUD 显示语音状态、模式和音量，采用紧凑半透明无边框设计；可显示/隐藏，拖动调整 HUD 和邀请提示位置，并在原按钮确认。
- 频道列表支持搜索、创建、加入、选择和退出；成员与玩家列表支持分页，玩家详情提供音量、静音和频道操作。
- 自定义频道支持开放、密码和隐藏可见性，以及所有者、主持人、成员、只听和封禁角色。
- 根据权限支持邀请、移除成员、角色调整、禁言/解禁、封禁/解封、锁定/解锁、转让所有权、重命名和解散；持久频道及成员信息可跨服务器重启保存。
- 邀请提示支持接受、拒绝和超时；玩家可拒收邀请、在普通玩家列表中隐藏自己或隐藏模组聊天提示。
- 按服务器保存当前频道、发送目标、频道音量、玩家音量/静音、遮挡、环境效果和抖动缓冲偏好。

#### 网络、容量与诊断

- 使用游戏网络通道传输语音和控制消息；V10 语音采用 48 kHz 单声道、20 ms 帧和 Opus，默认 24 Kbps，支持 12-48 Kbps 自适应码率。
- 支持客户端码率偏好、服务器码率指导、自适应抖动缓冲、Opus 前向纠错和丢帧补偿；解码在后台任务执行，PCM 缓冲池减少重复分配。
- 服务器通过空间索引、同时发言准入、包速率/字节限流和出口带宽预算控制转发。默认每个听众最多 8 路语音，其中附近语音最多 6 路；每频道最多 3 人同时发言。
- 可选兼容 `Downed` 2.7.4：启用服务器配置 `EnableDownedVoiceSilence` 后，昏迷玩家不能发送或接收任何语音；默认关闭，未安装 `Downed` 时不会生效。
- 默认每频道最多 100 名成员、每玩家最多 8 个频道、全服最多 256 个频道；名称长度默认 24 字符。管理员可调整这些限制。
- 提供握手与连接状态、往返延迟、丢包、码率、抖动/纠错统计，以及服务器转发量、丢弃原因、路由耗时、玩家诊断和操作审计。
- V9 与 V10 不互通。虽然保留 ADPCM 编解码代码和 `AllowAdpcmFallback` 字段，当前 V10 握手与网络校验只接受 Opus。

#### 管理员与服务器配置

- 拥有 `controlserver` 权限的管理员可管理频道、全服禁言和强制阻止发送；命令还支持临时禁言与临时拒听。
- 管理员窗口支持修改语音开关、距离、公共聊天可视范围、频道容量、码率、带宽、录音和导演捕获设置。
- “保存并应用”写盘并立即生效；“从文件重载”读取磁盘配置；“刷新配置”获取当前生效值。网页麦克风监听参数仍需修改文件并重启服务器，装备规则也在服务器文件中维护。
- 管理员配置中的“昏迷玩家禁用语音”对应服务器字段 `EnableDownedVoiceSilence`，默认关闭；该设置只在服务器端控制。
- 可选公共聊天距离限制仅影响普通公共聊天，按同维度和距离筛选接收者；默认关闭。
- 提供无效数据包校验、重复违规自动暂停语音、管理审计和滚动指标重置；审计文件不保存语音内容。

#### 网页麦克风与语音转文字

- 可选 LauncherGo 网页麦克风，通过一次性短时凭证接入服务器；游戏控制按键说话、自由麦、静音、模式和发送目标，其他玩家声音仍由游戏播放。
- 网页桥接默认关闭；启用后，模组默认监听 `127.0.0.1:15082`，提供 `/voice` 和 `/health`，与 LauncherGo 的网页服务分开。
- 语音转文字支持阿里云、硅基流动、Deepgram 和本地 Whisper，按服务商保存密钥、模型和接口地址；启用后按住 `V` 录音，松开发送识别文字到聊天。
- 云端识别需要对应服务的凭证与网络；本地 Whisper 需要 `SimpleVoiceChatASR` 依赖包和本地模型。语音识别默认关闭。

#### 录音、OBS 与 VS Director

- 支持本地“仅输入”WAV，以及将输入和接收音频分别写入左右声道的“输入+输出”WAV，可播放最近的本地录音。
- 管理员多人分轨录音由服务器托管，为每位发言者保存独立 WAV、统一时间轴、参与者状态和缺帧记录；需先启用 `EnableRecorderCapture`。
- 支持录音会话查询、停止、分块下载、定期 checkpoint 和服务器重启后的会话恢复；管理员客户端断线不结束服务器录音。
- 仓库内的独立 OBS 插件提供 `SimpleVoiceChat Player Voice` 混合音源，通过本机管道/Unix socket 接收玩家语音；同步 OBS 起点后，可导出多音轨 MKV 和 FCPXML。插件需另行安装到 OBS。
- 可选 VS Director 集成通过运行时 API 提供语音捕获，支持距离和回放区域路由、独立流数与带宽预算；需服务器与 VS Director 同时开启相应功能。

#### 开发者扩展

- `ClientSettingsExtensions` 支持文字按钮、图片按钮、自定义 RML 控件和独立扩展窗口。
- `RegisterVoiceChannelProvider` / `IVoiceChannelProvider` 允许其他模组提供由外部管理的频道、成员和角色快照。
- `ClientAudioBuses` 暴露客户端玩家语音总线；OBS 原生插件源码和 RmlUi/容量验证工具包含在仓库中。

#### 默认开关速查

| 功能 | 新建配置默认状态 | 条件 |
| --- | --- | --- |
| 语音、频道、玩家创建频道 | 开启 | 客户端与服务器协议兼容 |
| 按键说话 / 自由麦 | 按键说话 | 自由麦还需服务器允许 |
| HUD、遮挡、环境效果、自适应码率/抖动缓冲 | 开启 | 效果受服务器策略控制 |
| RNNoise 降噪、语音识别、性能模式 | 关闭 | 客户端按需开启 |
| 网页麦克风 | 关闭 | 服务器配置并重启，另需网页客户端 |
| 公共聊天距离限制 | 关闭 | 服务器开启 |
| 服务器多人分轨录音 | 关闭 | 服务器开启，管理员操作 |
| VS Director 捕获 | 关闭 | 服务器开启，另需 VS Director |

代码入口：[客户端控制器](ClientVoiceController.cs)、[服务器控制器](ServerVoiceController.cs)、[客户端配置](Config/SimpleVoiceChatClientConfig.cs)、[服务器配置](Config/SimpleVoiceChatServerConfig.cs)、[音频处理](Audio)、[界面](Gui)、[语音识别](SpeechRecognition)、[扩展](Integration)、[OBS 插件](ObsPlugin)。

### 网页麦克风（LauncherGo）

网页麦克风默认关闭。服务端配置 `EnableWebMicrophone` 为 `true` 后，模组才会在 `127.0.0.1:15082` 提供 `/voice` WebSocket 接入与 `/health` 状态检查。语音网页由 LauncherGo 的独立进程提供（默认 `http://127.0.0.1:5082/`），其启停不影响游戏服务器；游戏关闭时网页仍可打开。玩家在客户端语音设置中选择“网页麦克风（LauncherGo）”后，游戏会弹出 Token；将 Token 填入网页，网页通过同源 WebSocket 转发到模组。Token 认证、PCM 转 Opus、附近语音和频道权限均由模组处理。

服务端配置文件 `SimpleVoiceChat.Server.json` 可设置 `EnableWebMicrophone`、`WebMicrophoneBindAddress` 和 `WebMicrophonePort`。默认值为 `false`、`127.0.0.1` 和 `15082`；启用后可通过后两个字段修改模组接入地址和端口。LauncherGo 网页服务默认使用 `5082`，与模组接入端口分开。旧版把模组接入配置为 `5082` 的，需要保存为 `15082` 后重启游戏服务器一次。远程访问麦克风时，在 LauncherGo 网页服务配置 HTTPS 证书，或通过支持 WebSocket 的 HTTPS 反向代理访问；Token 只在短时间内有效。

音频设置的网页麦克风旁及凭证弹窗均提供“获取凭证”。凭证由服务端生成，须在 10 分钟内用于首次认证，仅能成功连接一次；断线后需要重新获取。认证成功后，连接不受凭证有效期限制。重新获取、切换输入设备、退出游戏或重新握手会撤销旧连接。网页显示本次连接时间。游戏内按键说话、静音、耳语/正常/大喊，以及附近/所选频道/两者的发送目标均控制网页音频；自由麦使用声音阈值。网页只采集麦克风，其他玩家声音仍由游戏播放。客户端、服务端和 LauncherGo 网页需一并更新。

### 安装

1. 关闭 Vintage Story 客户端和服务器。
2. 删除 `Mods` 目录中的旧版 SimpleVoiceChat 压缩包，避免同时加载多个版本。
3. 将 `SimpleVoiceChat-v1.2.8-pre.1.zip` 原样放入客户端和服务器的 `Mods` 目录，不要解压模组包。
4. 在客户端和服务器的 `Mods` 目录同时安装 `VSRmlUi (vsrmlui) 1.0.2` 或更新兼容版本。
5. 启动服务器，然后启动客户端。首次按 `'` 会打开设置向导。

SimpleVoiceChat 的设置、向导、邀请和 HUD 使用 RmlUi，需安装 `vsrmlui` 前置。VS Director 仅为可选集成与界面样式参考，无需安装。

主页保留原有快捷操作和窗口入口；设置、语音识别、频道、管理及详情按原有层级切换。只显示当前窗口的内容，关闭详情返回上一层，关闭设置页返回主页。

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
| 接受频道邀请 | `Ctrl + F8` |
| 拒绝频道邀请 | `F7` |

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

也可以注册与主窗口风格一致的独立扩展窗口。窗口由 SimpleVoiceChat 居中显示，提供标题、关闭按钮、可滚动内容区及与 VS Director 一致的深色圆角样式。第三方模组只负责在 `Compose` 回调中添加内容：

```csharp
voiceChat.ClientSettingsExtensions.RegisterWindow(
    new VoiceSettingsExtensionWindow(
        "example.window",
        "Example",
        context => context.Composer.AddStaticText(
            "Content",
            VoiceRmlFont.WhiteSmallText(),
            ElementBounds.Fixed(0, 0, context.ContentWidth, 30))));

voiceChat.ClientSettingsExtensions.ShowWindow("example.window");
```

自定义控件的 `Compose` 参数及窗口上下文的 `Composer` 现为 `SimpleVoiceChat.Gui.VoiceRmlForm`，不再使用 `GuiComposer`；已有自定义扩展需重新编译并迁移到 RML。可调用 `AddMarkup` 添加 RML，并用 `BindElement` 返回事件订阅；订阅会随表单刷新释放。文字按钮和图片按钮的注册方式保持一致。

上述 API 仅在客户端可用；注册 ID 只能包含字母、数字、`.`、`_` 和 `-`。关闭主设置窗口时，已打开的扩展窗口也会被释放。

### 语音转文字

打开 SimpleVoiceChat 主页，点击“语音识别”进入配置窗口，可选择阿里云、硅基流动、Deepgram 或本地 Whisper。启用后按住 `V` 录音，松开后将识别文字发送到聊天。云端服务在主模组中配置密钥、模型和接口地址即可；仅使用本地 Whisper 时需要额外安装 `SimpleVoiceChatASR` 依赖包，并自行下载模型、填写本地模型路径。

### 频道和录音

频道具有稳定的 `channel-<number>` ID。普通玩家默认可创建频道，服务器可关闭该权限。频道所有者可管理成员、角色、锁定状态和频道生命周期；服务器管理员使用 `controlserver` 权限执行全服管理。

主页录音按钮可保存“仅输入”或“输入+输出”WAV。Windows 默认位置为：

```text
%APPDATA%\VintagestoryData\ModData\SimpleVoiceChat
```

设置页的“麦克风测试”只保存在内存中，不会生成文件。本地设备测试不发送音频到服务器；网页麦克风测试经过服务器桥接回传测试音频，不向其他玩家广播。

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

服务器管理员可使用 `/svc enable`、`/svc disable`、`/svc reload`、`/svc setrange`、频道管理、玩家管制、诊断、指标和审计命令。可用子命令及参数以 [ServerVoiceController.cs](ServerVoiceController.cs) 的命令处理为准。

### 配置文件

- `SimpleVoiceChat.Client.json`：本机设备、音量、快捷方式、语音识别服务商配置和每服务器偏好。
- `SimpleVoiceChat.Server.json`：范围、公共聊天可视距离、频道、容量、路由和 VS Director 捕获策略。

拥有 `controlserver` 权限的管理员可在管理员窗口向下滚动到“服务器配置”，直接修改语音开关、距离、频道限制、码率、流量和录音设置。“保存并应用”写入服务器配置并立即生效，无需重启；“从文件重载”读取服务器配置文件并热重载；“刷新配置”读取当前生效值。后两项会替换界面中未保存的修改。网页麦克风开关、监听地址和端口仍通过服务器文件管理，变更后需要重启服务器；装备规则继续在服务器文件中编辑。
- 频道名称创建/修改限制由服务端 `MaxChannelNameLength` 控制，默认 24，范围 1-128；调整只作用于之后的创建和重命名，不会改动已有频道名称。也可使用 `/svc channelnamelength <1-128>` 修改并广播配置。
- `SimpleVoiceChat.Audit.json`：服务器管理操作审计，不记录语音内容。

## English

### Features

The list below describes the current `1.2.8-pre.1` implementation. Defaults refer to newly created configuration files; existing client settings and server policy may differ. The OBS plugin, LauncherGo web client, and local Whisper dependency package are installed separately.

#### Voice communication and playback

- Supports push-to-talk and voice activation, with separately configurable noise gate and activation threshold.
- Supports whisper, talk, and shout. Default ranges are 8, 18, and 35 blocks; the default server maximum is 40 blocks.
- Voice can target proximity, the selected channel, or both. A recipient who qualifies for both channel and proximity delivery is sent the channel path once to avoid duplicate playback.
- Proximity voice is spatialized and attenuated by distance, fading to silence at the range boundary. Channel voice does not fade with distance.
- Provides local microphone mute, deafen, per-player mute, master and channel volume, and per-player volume.

#### Audio devices and processing

- Selects the default or a specific input/output device. OpenAL device names are enumerated and opened as UTF-8, including Chinese names; the mod also attempts to migrate an exact match for a previously saved garbled device name.
- Microphone capture includes high-pass filtering, automatic and manual gain, soft limiting, and a noise gate; RNNoise suppression is optional.
- Noise suppression is off by default. The archive bundles `YellowDogMan.RRNoise.NET 0.1.9` native libraries for Windows x64/x86 and Linux x64/arm64. No macOS library is bundled, so macOS uses basic processing or may load a compatible external library from `VintagestoryData\ModData\SimpleVoiceChat\native`.
- Microphone tests support in-memory recording and playback. Local device tests create no files and send no audio to the server; web microphone tests use the web bridge.
- Capture attempts recovery after device failures; unavailable selected playback devices fall back to the game's audio device.
- Echo cancellation is not implemented. The retained `EnableEchoCancellation` configuration field does not make the feature available.

#### Environmental effects and performance

- Supports block occlusion, underwater, helmet/mask, weather, low temporal stability, and poisoning effects based on entity state.
- The server determines underwater and equipment states. Equipment matching supports wildcards; clients control local occlusion and environmental playback while the server can force occlusion.
- Performance mode is off by default. When enabled, it reduces occlusion samples from 9 to 5 and extends the environmental-state cache from 150 ms to 250 ms. It does not change the audio driver, 48 kHz sample rate, or Opus complexity, and does not expand device compatibility.

#### Channels, players, and interface

- RmlUi screens include first-run setup, home, audio settings, speech recognition, channels, players, administration, status, and recording, with hierarchical navigation.
- The HUD shows voice state, mode, and volume in a compact translucent borderless layout. It can be shown or hidden; the HUD and invitation prompt can be repositioned and confirmed with the same adjustment button.
- Channel lists support search, creation, joining, selection, and leaving. Member and player lists are paginated; player details provide volume, mute, and channel actions.
- Custom channels support open, password-protected, and hidden visibility, plus Owner, Moderator, Member, Listen Only, and Banned roles.
- Depending on permissions, channel actions include inviting/removing members, changing roles, muting/unmuting, banning/unbanning, locking/unlocking, transferring ownership, renaming, and disbanding. Persistent channels and membership survive server restarts.
- Invitation prompts support accept, decline, and timeout. Players can reject invitations, hide themselves from ordinary player lists, or hide mod chat notices.
- Per-server preferences include the selected channel, transmit target, channel/player volume and mute, occlusion, environmental effects, and jitter-buffer settings.

#### Networking, capacity, and diagnostics

- Voice and control messages use the game's network channel. V10 voice is 48 kHz mono, 20 ms Opus frames at 24 Kbps by default, with adaptive rates from 12 to 48 Kbps.
- Supports client bitrate preference, server bitrate guidance, adaptive jitter buffering, Opus forward error correction, and packet-loss concealment. Decoding runs in background tasks and a PCM buffer pool reduces repeated allocations.
- Server forwarding uses spatial indexing, concurrent-speaker admission, packet/byte rate limits, and egress budgets. Defaults allow up to 8 voice streams per listener (6 proximity streams maximum) and 3 simultaneous speakers per channel.
- Optional `Downed` 2.7.4 compatibility is available through `EnableDownedVoiceSilence`. When enabled, downed players cannot transmit or receive voice; it is off by default and has no effect when `Downed` is absent.
- Defaults allow 100 members per channel, 8 channels per player, 256 channels server-wide, and 24 characters per channel name. Administrators can change these limits.
- Diagnostics include handshake/connection state, round-trip latency, packet loss, bitrate, jitter/FEC statistics, forwarding volume, drop reasons, routing time, player diagnostics, and administrative audit.
- V9 and V10 are incompatible. Although ADPCM codec code and the `AllowAdpcmFallback` field remain, the current V10 handshake and packet validation accept Opus only.

#### Administration and server configuration

- Administrators with `controlserver` can manage channels, server-wide mute, and forced transmit blocking; commands also support temporary mute and deafen.
- The admin window can edit voice enablement, ranges, public-chat visibility range, channel capacity, bitrate, bandwidth, recording, and Director capture settings.
- Save and Apply writes changes and applies them immediately; Reload from File reads the disk configuration; Refresh Configuration fetches the current active values. Web microphone listener settings still require editing the server file and restarting, and equipment rules are maintained in that file.
- The administrator setting `Silence Downed players` maps to the server-only `EnableDownedVoiceSilence` field and is disabled by default.
- Optional public-chat distance filtering affects ordinary public chat only and filters recipients by dimension and distance; it is off by default.
- Includes invalid-packet validation, automatic voice suspension for repeated violations, administrative audit, and rolling metric reset. Audit files do not store voice content.

#### Web microphone and speech-to-chat

- Optional LauncherGo web microphone connects to the server using a short-lived, one-time credential. In-game controls still govern push-to-talk, voice activation, mute, mode, and transmit target; other players' voices remain in game playback.
- The web microphone bridge is disabled by default. When enabled, the mod listens on `127.0.0.1:15082` by default and exposes `/voice` and `/health`; this is separate from the LauncherGo web service.
- Speech-to-chat supports Aliyun, SiliconFlow, Deepgram, and local Whisper. Credentials, model, and endpoint are configured per provider. Hold `V` to record; releasing it sends the recognized text to chat.
- Cloud recognition requires provider credentials and network access. Local Whisper requires the `SimpleVoiceChatASR` dependency package and a local model. Speech recognition is off by default.

#### Recording, OBS, and VS Director

- Local recording supports input-only WAV and input/output WAV split between left and right channels, with playback of recent local recordings.
- Administrator multi-track recording is hosted by the server and writes a separate WAV per speaker on one shared timeline, with participant state and missing-frame records. Enable `EnableRecorderCapture` first.
- Recording sessions support listing, stopping, chunked downloads, periodic checkpoints, and recovery after a server restart. Disconnecting the administrator client does not end a server recording.
- The separate OBS plugin in this repository provides a `SimpleVoiceChat Player Voice` mixed source over a local pipe/Unix socket. After synchronizing the OBS start time, it can export multi-track MKV and FCPXML. Install the plugin separately into OBS.
- Optional VS Director integration uses its runtime API for voice capture, proximity/replay-area routing, and independent stream and bandwidth budgets. The corresponding features must be enabled in both the server and VS Director.

#### Developer extensions

- `ClientSettingsExtensions` supports text buttons, image buttons, custom RML controls, and standalone extension windows.
- `RegisterVoiceChannelProvider` / `IVoiceChannelProvider` lets other mods provide externally managed channel, member, and role snapshots.
- `ClientAudioBuses` exposes the client player-voice bus. The repository also contains OBS native plugin source and RmlUi/capacity validation tools.

#### Default settings at a glance

| Feature | New configuration default | Condition |
| --- | --- | --- |
| Voice, channels, player-created channels | Enabled | Client and server protocols must be compatible |
| Push-to-talk / voice activation | Push-to-talk | Voice activation also requires server permission |
| HUD, occlusion, environmental effects, adaptive bitrate/jitter buffer | Enabled | Subject to server policy |
| RNNoise, speech recognition, performance mode | Disabled | Enable on the client as needed |
| Web microphone | Disabled | Enable in server config and restart; separate web client required |
| Public-chat distance filtering | Disabled | Enable on the server |
| Server multi-track recording | Disabled | Enable on server; administrator operation required |
| VS Director capture | Disabled | Enable on server; VS Director also required |

### Installation

1. Stop the Vintage Story client and server.
2. Remove older SimpleVoiceChat archives from each `Mods` directory so that only one version can load.
3. Place `SimpleVoiceChat-v1.2.8-pre.1.zip` unchanged in the client and server `Mods` directories. Do not extract the mod archive.
4. Install `VSRmlUi (vsrmlui) 1.0.2` or a newer compatible version in both client and server `Mods` directories.
5. Start the server and client. Press `'` to open the first-run setup wizard.

Settings, setup, invitations, and the HUD require the `vsrmlui` mod. VS Director remains an optional integration and visual reference, not a dependency.

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
| Accept channel invitation | `Ctrl + F8` |
| Decline channel invitation | `F7` |

Bindings can be changed in Vintage Story's game key settings.

### Main-window extensions (client API)

Client-side mods can register buttons or custom controls through `SimpleVoiceChatModSystem.ClientSettingsExtensions`. They appear below the home-page quick-control row that contains the HUD visibility button. Controls are sorted by `Order`, sized from `PreferredWidth` and the measured button text, and wrapped to new rows when the available width is full. The settings window fits the viewport; additional rows are scrollable with the mouse wheel. `IsVisible` can be changed at runtime.

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

Mods may also register an independently opened window. SimpleVoiceChat supplies a centered RmlUi panel, title, close button, scrolling content, and dark rounded controls styled after VS Director. The extension supplies the content:

```csharp
voiceChat.ClientSettingsExtensions.RegisterWindow(
    new VoiceSettingsExtensionWindow(
        "example.window",
        "Example",
        context => context.Composer.AddStaticText(
            "Content",
            VoiceRmlFont.WhiteSmallText(),
            ElementBounds.Fixed(0, 0, context.ContentWidth, 30))));

voiceChat.ClientSettingsExtensions.ShowWindow("example.window");
```

These APIs are client-only. Registration IDs may contain letters, digits, `.`, `_`, and `-`. Closing the main settings dialog also releases an open extension window.

### Speech-to-Chat

Open Speech Recognition from the SimpleVoiceChat home page to select Aliyun, SiliconFlow, Deepgram, or local Whisper. When enabled, hold `V` to record and release it to transcribe and send text to chat. Configure cloud credentials, model, and endpoint in the main mod. Only local Whisper requires the separate client-only `SimpleVoiceChatASR` runtime package and a downloaded model with its local path configured.

### Channels and Recording

Channels use stable `channel-<number>` IDs. Ordinary players may create channels by default, although the server can disable that permission. Channel owners manage members, roles, locking, and lifecycle; server administrators use the `controlserver` privilege for server-wide actions.

The home-page recording button offers Input Only, Input+Output, and Multi-track speakers. Input+Output stores the microphone and received playback as separate stereo channels. Multi-track WAV files are authoritative on the server under `ModData/SimpleVoiceChat/Recordings`; the client keeps only an OBS marker and a download cache. On Windows, the default local cache is:

```text
%APPDATA%\VintagestoryData\ModData\SimpleVoiceChat
```

Microphone Test is memory-only and creates no files. Local device tests send no audio to the server; web microphone tests return test audio through the server bridge without broadcasting to other players.

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

Server administrators can use `/svc enable`, `/svc disable`, `/svc reload`, `/svc setrange`, channel administration, player moderation, diagnostics, metrics, and audit commands.

### Configuration Files

- `SimpleVoiceChat.Client.json`: local devices, levels, input preferences, speech-recognition provider settings, and per-server preferences.
- `SimpleVoiceChat.Server.json`: ranges, public-chat visibility, channels, capacity, routing, and VS Director capture policy.

Administrators with `controlserver` can scroll to Server configuration in the administrator window to edit voice options, ranges, channel limits, bitrate, bandwidth, and recording settings. Save and apply persists changes and activates them immediately. Reload from file hot-reloads the server file; Refresh config fetches the currently active values. Both replace unsaved edits. Web microphone enable/address/port settings remain in the server file and require a server restart; equipment rules also remain file-managed.
- `SimpleVoiceChat.Audit.json`: server administration events; it does not contain voice content.

## Build and Verification

```powershell
dotnet test Tests\SimpleVoiceChat.Tests.csproj
dotnet build SimpleVoiceChat.csproj -c Release
./Package-Mod.ps1
```

`Package-Mod.ps1` always deletes the previous `SimpleVoiceChat-v<version>.zip`, any matching `.sha256` file, stale `release-<version>` output, and the Release staging directory before building. It writes the fresh package to `artifacts\SimpleVoiceChat-v<version>.zip` and validates that documentation, debug symbols, and unused legacy icon assets are not included. The release staging directory is `bin\Release\Mods\mod`. Install `SimpleVoiceChatASR` on clients that use local Whisper; it supplies dependencies only and does not add a second settings button.

### RmlUi development and validation

Build against `../VintageStory_RmlUi/artifacts/sdk/VSRmlUi.dll`, or set `-p:RmlUiSdkPath=<path>`.
The reference is not copied into the mod: install the RmlUi mod separately on both sides.
All UI runs on the client thread; the server continues to use the existing voice services.

Custom extensions now compose with `SimpleVoiceChat.Gui.VoiceRmlForm` instead of `GuiComposer` and must be rebuilt.
Use `AddMarkup` for RML and `BindElement` to return an event subscription, which is released on form refresh.
Existing text/image button registration signatures are unchanged. PNG assets are preserved; former SVG controls use bundled Tabler icons.

```powershell
dotnet build SimpleVoiceChat.csproj
dotnet test Tests/SimpleVoiceChat.Tests.csproj
dotnet run --project Tools/RmlHarness/RmlHarness.csproj
```

The native harness requires the adjacent game and RmlUi SDK/native build. It opens a hidden OpenGL window,
checks real RML controls, layout, input, and disposal, and saves previews under `artifacts/rml-*.png`, including channel and player windows and full-screen/cropped HUD images.
