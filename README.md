# 简单语音对话

作者：VSCN-Studio

`SimpleVoiceChat 0.2.0` 是 Vintage Story 双端语音模组，面向大型文明模拟活动。它提供接近度语音、文明/指挥/外交/小队/工作人员/广播/无线电频道、Opus、3D 定位、环境传播、权限管理和容量保护。

## 安装与升级

1. 客户端和服务器都安装 `SimpleVoiceChat-v0.2.0.zip`。
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
| 打开状态/设置窗口 | `'` |

快捷键可在游戏按键设置的 `Simple Voice Chat` 分类中重绑。服务器关闭持续说话时，客户端会自动退回 PTT 并提示。

## 设置窗口

按 `'` 打开较矮的“音频 / 频道 / 状态”分页窗口，可直接操作：

- 选择和重新初始化麦克风设备。
- 调整总音量、频道音量、单玩家音量和单玩家静音。
- 调整麦克风增益与噪声门。
- 切换麦克风静音、全局语音关闭和服务器允许时的持续发言。
- 切换 HUD、传播效果、性能模式和 40-120ms 自适应 jitter。
- 查看当前频道、发送目标、分页成员、邀请和频道管理操作。
- 具备服务器管理权限时，可从频道页创建文明/指挥/外交/工作人员/广播/无线电频道，并执行临时或持久语音管制。
- 接受/拒绝小队邀请，离开或解散频道。
- 录制并回放 3 秒本地麦克风测试；测试内容不会上传。
- 查看协议、codec、连接 epoch、真实 UDP 存活、RTT、探测丢包、队列、target delay、迟到、PLC/FEC 和路由诊断。

基础采集链路包含高通、attack/release AGC、软限幅、噪声门和 VAD hangover。采集设备失败后每 10 秒进行一次静默重连，恢复时提示玩家；仍需在活动使用的 Windows/Linux 设备上做拔插验证。当前发行包没有经过验证的跨平台 WebRTC APM，因此 NS/AEC 会明确显示为不可用，不会用普通噪声门冒充。

## 频道与发送目标

窗口可选择三种发送目标：仅接近度、仅当前频道、接近度和当前频道。HUD 始终显示实际目标，降低误发到文明或工作人员频道的风险。

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

`/svc bind` 发送 30 秒小队邀请，目标必须在 `SquadBindRange` 内。两个已有小队只有在邀请方和接受方都是各自小队的 Owner/Officer、合并后不超过人数/频道上限时才会合并；不能单方面绑定。

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

服务器配置包含协议/codec 兼容、范围、频道功能开关、100 人资源上限、单听众/全服出口预算、连续说话策略、持久频道和管理列表。默认 `MaxChannels=256`、`MaxChannelsPerPlayer=8`，接近度语音不计入玩家频道数；两者分别归一化到 16-512 和 1-8。客户端配置包含设备、音量、单玩家覆盖、静音、发送目标、选中频道和 jitter 设置。配置包含独立 `ConfigVersion` 并在加载时归一化；`/svc reload` 还会同步现有会话限流、重建出口预算并重发频道快照。

## 隐私与发布边界

- 服务端只转发压缩语音，不常态解码、混音或录音，也不保存 payload。
- 本模组不提供端到端加密；传输保护取决于 Vintage Story 底层连接。
- Concentus 许可证见随包的 `THIRD-PARTY-NOTICES.md`。
- 52 项自动测试覆盖 100 人等效路由、总流/接近度/字节预算、25/100 路上行边界、小队合并、外部提供器权威边界、权限、生命周期、限流、Opus 畸形输入隔离、jitter 回绕、0/2/5/10% 丢包与 20-100ms 抖动矩阵、协议随机输入和配置迁移。
- 自动仿真不能替代真实活动验收。正式宣称某台服务器满足 100 人目标前，仍应完成 20/50/100 人彩排、网络故障矩阵和 8 小时长稳测试，并保存 Tick、P95 路由、内存、句柄和出口流量报告。
