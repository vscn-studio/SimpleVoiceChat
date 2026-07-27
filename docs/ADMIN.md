# SimpleVoiceChat 0.2.0 管理员手册

## 活动前检查

1. 确认服务端和全部客户端均为 0.2.0，`AllowLegacyProtocol=false`、`AllowAdpcmFallback=false`。
2. 用有线网络实测服务器持续公网，上行建议至少 70-80Mbps；确认不是“下载 100M、低上行”套餐。
3. 保持 `MaxStreamsPerListener=8`、`MaxProximityStreams=6`、`MaxChannelTalkers=3`、`MaxChannelsPerPlayer=8`、`MaxChannels=256`、`MaxListenerEgressKbps=512` 和 `MaxServerEgressKbps=50000` 作为首场活动基线。
4. 创建文明、指挥、外交、工作人员和广播频道，核对 Owner/Officer/Member/ListenOnly 权限。
5. 用 `/svc channels` 检查规模和席位；用 `/svc playerdiag <玩家>` 检查握手、codec 和管制状态。
6. 完成 20、50、100 人彩排，至少覆盖 25 人同时说话、100 人异常开放麦、广播、重连和 `/svc reload`。
7. 正式活动前归档服务端配置、审计文件、日志和容量报告。

## 运行监控

- `/svc status`：开关、范围、频道和持久管理列表。
- `/svc diag`：累计收发/丢弃、P95 扇出、P95 路由和当前监听流。
- `/svc channels`：频道人数、当前发言席位和锁定状态。
- `/svc playerdiag <玩家>`：连接、codec、频道、临时禁言、强制静听和协议 strike。
- `/svc audit`：最近 10 条管理审计。
- `/svc metrics reset`：仅重置一分钟滚动窗口，不删除累计审计。

优先关注 `DroppedBudget`、`DroppedNoSlot` 和 P95 路由耗时。席位丢弃在多人同时说话时是预期背压；预算持续丢弃说明出口硬上限或实际网络不足。

玩家最多加入 8 个显式频道，接近度语音不计入；服务端默认最多保留 256 个频道。已有小队合并必须由邀请方和接受方各自的 Owner/Officer 确认，合并后目标 Owner 降为 Officer；人数、锁定、封禁或频道上限不满足时会原子拒绝，不会只迁移部分成员。

外部文明提供器最多注册 32 个。提供器失败时继续使用最后一次有效频道快照；连续失败告警按提供器节流。外部派生频道的成员和角色由提供器权威管理，窗口和服务端会拒绝本地添加、移出、改角色、离开或解散；频道锁定、语音禁言和封禁仍由语音管理员控制。若日志出现 `channel capacity was reached`，先检查是否有废弃持久频道或玩家已达到 8 频道上限，不要简单提高上限掩盖错误成员关系。

## 管制流程

- 短时扰乱：`/svc tempmute <玩家> 60`。
- 强制停止收听：`/svc deafen <玩家> 60`。
- 持久全局禁言：`/svc adminmute <玩家或UID>`。
- 持久全服屏蔽：`/svc forceblock <玩家或UID>`。
- 频道内使用 `channelmute`、`channelremove` 或 `channelban`，避免扩大影响范围。
- 紧急广播频道只给主持人 Owner/Officer，其余成员设为 ListenOnly。

管理操作写入 `SimpleVoiceChat.Audit.json`，不记录音频内容。日志和审计应按活动隐私政策控制访问。

## 故障降级

1. 环境效果异常：客户端开启性能模式，或服务端关闭遮挡/天气影响。
2. 频道系统异常：设置 `EnableChannelVoice=false`，接近度语音继续工作。
3. 广播或无线电异常：分别关闭 `EnableBroadcastChannels` / `EnableRadioChannels`。
4. 出口接近线路上限：降低 `MaxStreamsPerListener` 至 6，再降低 `MaxServerEgressKbps`；不要靠堆积旧音频解决。
5. 配置修改后执行 `/svc reload`。解析失败会保留当前运行配置；成功后现有会话限流、持久频道和客户端频道快照会同步刷新。
6. 语音整体异常：`/svc disable` 快速关闭转发，不影响游戏服务器连接。

## 升级与回滚

升级前备份：

- `SimpleVoiceChat.Server.json`
- `SimpleVoiceChat.Audit.json`
- 当前模组 zip

0.1.x 到 0.2.0 默认切换协议 V2 和 Opus，旧客户端或只提供 ADPCM 的客户端会被明确拒绝。回滚时停止服务器，恢复旧 zip 和对应配置备份，再启动；不要同时保留两个版本。不要在正式活动中用 `AllowLegacyProtocol=true` 或 `AllowAdpcmFallback=true` 代替完整客户端升级。

## 隐私边界

服务端选择性转发压缩帧，不常态解码、混音、录音或保存 payload。本模组不提供端到端加密，传输保护依赖 Vintage Story 底层连接。未来如增加录音，必须另行设计告知、授权、保留期限和访问审计。
