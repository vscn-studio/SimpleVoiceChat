# 简单语音对话

作者：VSCN-Studio

`SimpleVoiceChat 0.3.0` 是 Vintage Story 双端语音模组，面向大型文明模拟活动。它提供接近度语音、文明/指挥/外交/小队/工作人员/广播/无线电频道、Opus、3D 定位、环境传播、权限管理和容量保护。

## 安装与升级

1. 客户端和服务器都安装 `SimpleVoiceChat-v0.3.0.zip`。
2. 删除旧版 zip，避免同时加载两个版本；保留配置文件即可迁移。
3. 重启客户端和服务器。

协议 V2 默认不兼容 0.1.x 客户端。紧急回滚时可恢复旧模组和备份配置；`AllowLegacyProtocol` 只用于短期开发兼容，活动服不建议开启。

## 默认快捷键

| 功能 | 默认快捷键 |
| --- | --- |
| 按住说话 | `N` |
| 持续说话开关 | `Alt + N` |
| 切换耳语/正常/大喊 | `[` 或 `]` |
| 本地麦克风静音 | `Ctrl + -` |
| 全局停止收听 | `;` |
| 打开语音设置窗口 | `'` |

快捷键可在游戏按键设置的 `Simple Voice Chat` 分类中重绑。服务器关闭持续说话时，客户端会自动退回 PTT 并提示。

## 语音设置窗口

进入世界后按 `'` 打开默认居中的独立语音窗口。窗口完全使用 Vintage Story 原版背景、标题栏、按钮、下拉框、滑块、开关、文本框和滚动条，左侧菜单包含“音频”“频道与玩家”“状态”，提供：

- 当前频道、麦克风设备和发送目标。
- 语音总音量、频道音量、麦克风增益与发言阈值。
- 麦克风静音、停止收听、持续发言、传播效果、性能模式和自适应 jitter。
- 可关闭或恢复右下角麦克风 HUD；HUD 显示当前状态、模式、发送目标、音量、UDP 状态和小队成员发言状态。
- 最多 100 名在线玩家的滚动列表，可逐人调整音量或静音。

所有玩家都会注册该快捷键。只有服务器确认具备 `controlserver` 权限后，左侧才额外显示“管理”菜单；其中可选择玩家和频道，执行临时/持久管制、频道锁定、成员与角色管理、频道改名，以及创建文明/指挥/外交/工作人员/广播/无线电频道。服务端仍会逐项鉴权，外部文明提供器管理的成员和频道名称保持只读。

小队邀请只在待处理时显示于右下角语音 HUD 上方，玩家可用鼠标接受或拒绝；10 秒内没有选择时客户端与服务端都会将邀请视为拒绝。完整界面约束见 `docs/UI-DESIGN.md`。

基础采集链路包含高通、attack/release AGC、软限幅、噪声门和 VAD hangover。采集设备失败后每 10 秒进行一次静默重连，恢复时提示玩家；仍需在活动使用的 Windows/Linux 设备上做拔插验证。当前发行包没有经过验证的跨平台 WebRTC APM，因此窗口中的 NS/AEC 开关会禁用，不会用普通噪声门冒充。

## 频道与发送目标

语音窗口的“频道与玩家”页可选择三种发送目标：仅接近度、仅当前频道、接近度和当前频道。活动前应让玩家确认发送目标，避免误发到文明或工作人员频道。

| 频道 | 默认策略 |
| --- | --- |
| 接近度 | 耳语 8 格、正常 18 格、大喊 35 格；每听众最多 6 路 |
| 文明 | 成员可发言，默认 3 个并发席位 |
| 指挥 | 只有 Owner/Officer 可发言，默认 2 个席位 |
| 外交 | 授权成员可发言，默认 3 个席位 |
| 小队 | 邀请后接受，默认最多 12 人、3 个席位 |
| 工作人员 | 成员可发言，拥有较高路由优先级 |
| 广播 | 只有 Owner/Officer 可发言，单席位抢占式优先 |
| 无线电 | 由管理员或外部 group provider 提供成员/频率语义 |

频道有 Owner、Officer、Member、ListenOnly、Banned 权限。发言席位会按角色优先并公平轮转，持续开放麦不会永久占据普通席位。单听众默认最多 8 路，硬上限 12 路；同一玩家经多个路径到达时只发送一次。

文明模组可通过 `SimpleVoiceChat.Integration.IVoiceGroupProvider` 提供权威文明/角色快照。最多注册 32 个 provider；ID、组、频道、成员、角色和名称均有上限，单个派生频道最多 100 人、12 个发言席位。provider 抛异常或返回不可枚举数据时保留最后一次有效快照，告警按 provider 节流，不会把全部玩家误并入同一频道。派生频道不允许本地添加、移出、改角色、离开或解散；语音管理员仍可锁定、频道禁言或封禁。没有集成时使用管理员手工频道。

## 客户端命令

```text
/svc status
/svc volume <0-200>
/svc volumeplayer <玩家名> <0-200>
/svc mute|unmute <玩家名>
/svc bind [玩家名]
/svc unbind
/svc squad
/svc accept|decline
/svc diag
```

`/svc bind` 发送 10 秒小队邀请，目标必须在 `SquadBindRange` 内。两个已有小队只有在邀请方和接受方都是各自小队的 Owner/Officer、合并后不超过人数/频道上限时才会合并；不能单方面绑定。

## 管理命令

以下命令需要 `controlserver` 权限：

```text
/svc status
/svc diag
/svc channels
/svc playerdiag <玩家>
/svc metrics reset
/svc audit
/svc reload
/svc enable|disable
/svc setrange whisper|talk|shout <格数>
/svc tempmute <玩家> [秒]
/svc deafen <玩家> [秒]
/svc adminmute|adminunmute <玩家或UID>
/svc forceblock|unforceblock <玩家或UID>
/svc adminmutes
/svc channelcreate civilization|command|diplomacy|staff|broadcast|radio <名称>
/svc channeladd|channelremove <频道ID> <玩家或UID>
/svc channelrole <频道ID> <玩家或UID> listenonly|member|officer
/svc channellock|channelunlock <频道ID>
/svc channelmute|channelunmute <频道ID> <玩家或UID>
/svc channelban|channelunban <频道ID> <玩家或UID>
```

`reload` 先加载并归一化新配置，成功后才替换运行快照；解析失败会保留旧配置。管理操作写入 `SimpleVoiceChat.Audit.json`，记录时间、操作者、目标、范围和原因，不记录语音内容。

## 百人容量与带宽

默认容量模型：100 人在线、正常峰值 25 人同时上行、异常峰值 100 人持续上行。系统不向每人转发其余 99 路，而是通过空间索引、频道席位、每听众流仲裁、限包/限字节、每听众出口预算和全服出口 token bucket 保持资源有界。

默认 `MaxListenerEgressKbps=512`、`MaxServerEgressKbps=50000`，即单听众约 512Kbps、全服约 50Mbps 的语音出口硬预算。100M 独享对称上行通常足以支持 50 人聚集、约 10 人同时说话；实际流量通常明显低于 50Mbps。仍需为 Vintage Story 游戏同步、TCP/UDP/IP 开销和波动留余量，建议可用上行长期不少于 70-80Mbps，避免共享线路、运营商上行限速和 Wi-Fi 回程。

若“100M”只是下载 100Mbps、上行很低，则不满足要求。部署前应测服务器实际公网持续上行、丢包和抖动，而不是只看套餐名义带宽。

## 音频与网络实现

- 协议 V2 使用服务器连接身份与 connection epoch；客户端不能声明发送者 UID、实体或坐标。
- 默认 codec 为 16kHz 单声道、20ms、20kbps Opus，启用 VBR、DTX、in-band FEC 和 PLC；正式默认 `AllowAdpcmFallback=false`，只有开发兼容时显式开启才允许 ADPCM 降级。
- 每流使用编码帧重排和 40-120ms 自适应 jitter，队列过载时丢旧帧。
- 客户端最多创建 12 个远端 OpenAL 流；所有待处理队列均有界。
- 语音使用游戏当前 OpenAL context，不修改共享的全局 distance model。
- 遮挡、水下、风雨和玩家状态按流缓存；不再每帧序列化实体属性。

## 配置文件

- 客户端：`SimpleVoiceChat.Client.json`
- 服务器：`SimpleVoiceChat.Server.json`
- 管理审计：`SimpleVoiceChat.Audit.json`

服务器配置包含协议/codec 兼容、范围、频道功能开关、100 人资源上限、单听众/全服出口预算、连续说话策略、持久频道、管理列表和自动生成的服务器实例 ID。默认 `MaxChannels=256`、`MaxChannelsPerPlayer=8`，接近度语音不计入玩家频道数；两者分别归一化到 16-512 和 1-8。客户端按服务器实例 ID 分别保存频道音量、单玩家覆盖、静音、发送目标、选中频道、遮挡和 jitter 设置；设备、主音量、麦克风处理与 HUD 等本机偏好保持全局。重连后会在首个频道快照中验证并恢复仍有权限进入的频道。旧版全局客户端配置会迁移到首次进入的服务器档案。配置包含独立 `ConfigVersion` 并在加载时归一化；`/svc reload` 还会同步现有会话限流、重建出口预算并重发频道快照。

## 隐私与发布边界

- 服务端只转发压缩语音，不常态解码、混音或录音，也不保存 payload。
- 本模组不提供端到端加密；传输保护取决于 Vintage Story 底层连接。
- Concentus 许可证见随包的 `THIRD-PARTY-NOTICES.md`。
- 54 项自动测试覆盖 100 人等效路由、总流/接近度/字节预算、25/100 路上行边界、小队合并、外部提供器权威边界、权限、生命周期、限流、Opus 畸形输入隔离、jitter 回绕、0/2/5/10% 丢包与 20-100ms 抖动矩阵、协议随机输入、配置迁移、频道重命名和 UI 回调防递归。
- 自动仿真不能替代真实活动验收。正式宣称某台服务器满足 100 人目标前，仍应完成 20/50/100 人彩排、网络故障矩阵和 8 小时长稳测试，并保存 Tick、P95 路由、内存、句柄和出口流量报告。
