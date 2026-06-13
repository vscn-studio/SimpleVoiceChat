# 简单语音对话

作者：VSCN

`SimpleVoiceChat` 是 Vintage Story 的双端语音模组，提供接近度语音、三档说话距离、3D 空间定位、环境音效、小队频道、静音和右下角麦克风状态提示。

## 安装

1. 客户端和服务器都需要安装本模组。
2. 将 `SimpleVoiceChat-v0.1.0.zip` 放入 Vintage Story 的 `Mods` 目录。
3. 重启客户端和服务器。

## 默认快捷键

| 功能 | 默认快捷键 |
| --- | --- |
| 按住说话 | `N` |
| 持续说话开关 | `Alt + N` |
| 切换语音模式 | `[` 或 `]` |
| 本地麦克风静音/取消静音 | `Ctrl + -` |
| 全局语音开关 | `;` |
| 打开语音状态/设置窗口 | `'` |

这些键位故意选得偏一些，尽量避开 Vintage Story 和常见模组常用键位。可以在游戏的按键设置里重新绑定。

本版已切换到新的热键内部编号，旧版生成过的冲突按键不会继续覆盖这些默认值。如果你手动改过键位，请在游戏按键设置中搜索 `Simple Voice Chat` 重新绑定。

## 右下角麦克风显示

右下角不再使用大块文字 HUD，改为紧凑的麦克风图片状态：

- 麦克风启用时显示 `haojiao.png`。
- 麦克风禁用、静音、语音关闭或麦克风不可用时显示 `nohaojiao.png`。
- 图片右侧显示麦克风状态、当前中文语音模式和 UDP/麦克风状态。
- UDP/麦克风状态下方常驻显示音量：没有语音输入/接收时显示 0 音量图片，灰色表示未达到，绿色表示正常音量，柔和红色表示输入过大；音量条使用 2 倍大小、40 格带间距的透明背景预渲染 PNG 帧，并降低红色视觉重量以减少暗色 HUD 上的视觉漂移。
- 音量按当前语音模式和游戏内传播计算，耳语显示更小，正常说话居中，大喊更高；无输入或未开启语音时不显示音量条。
- 绑定小队频道后，音量下方会显示小队成员；成员正在说话时显示绿色电话图标。
- 该麦克风状态属于本地测试/控制提示，不受服务器 HUD 指示开关影响；客户端可用 `ShowMicrophoneHud` 关闭。

图片已打包在模组内：`assets/simplevoicechat/textures/gui/haojiao.png`、`assets/simplevoicechat/textures/gui/nohaojiao.png`、`assets/simplevoicechat/textures/gui/phone-volume-solid.png` 和 `assets/simplevoicechat/textures/gui/volume/volume-00.png` 到 `volume-40.png`。

## 状态/设置窗口

按 `'` 打开状态/设置窗口。窗口内可以直接调整：

- 输入设备：默认麦克风或 OpenAL 枚举到的麦克风设备。
- 播放音量：0-200%。
- 麦克风增益：10-400%。
- 噪声门：0-200/1000，数值越高越容易过滤小声和底噪。
- 右下角麦克风显示、遮挡/环境音效、性能模式开关。

切换输入设备后会自动重新打开麦克风采集；如果设备不可用，会在聊天栏提示，仍可听到其他玩家语音。

语音模式：

- 耳语：默认 8 格。
- 正常说话：默认 18 格。
- 大喊：默认 35 格。

## 客户端命令

```text
/svc status
/svc volume <0-200>
/svc mute <玩家名>
/svc unmute <玩家名>
/svc bind
/svc unbind
/svc squad
```

说明：

- `/svc status` 查看当前语音状态。
- `/svc volume <0-200>` 调整本地播放音量。
- `/svc mute <玩家名>` 屏蔽指定在线玩家。
- `/svc unmute <玩家名>` 取消屏蔽指定在线玩家。
- `/svc bind` 面对近处玩家时绑定小队频道；绑定后距离外也能听到小队成员语音。
- `/svc unbind` 离开当前小队频道。
- `/svc squad` 查看当前小队成员。

## 环境音效

当前版本已经对接收端 PCM 音频做轻量处理：

- 距离越远，声音会逐渐更闷，不只是变小。
- 隔墙/地形遮挡会降低音量并增加低通闷声。
- 水下会增加闷声和轻微失真。
- 室内/洞穴会根据周围方块包围度和天空光估算轻微回音。
- 暴风雨和大风会在露天时降低传播清晰度，增加闷声和细微抖动。
- 时间稳定性较低、中毒状态能被属性识别时，会增加轻微 pitch、闷声、抖动或失真效果。

这些效果都是轻量 MVP 实现，不依赖额外音频 DLL。

## 服务器命令

需要 `controlserver` 权限：

```text
/svc status
/svc reload
/svc enable
/svc disable
/svc setrange whisper <格数>
/svc setrange talk <格数>
/svc setrange shout <格数>
/svc adminmute <玩家名或UID>
/svc adminunmute <玩家名或UID>
/svc forceblock <玩家名或UID>
/svc unforceblock <玩家名或UID>
/svc adminmutes
```

说明：

- `/svc adminmute` 管理员全局禁言指定玩家，该玩家语音不会被服务器转发。
- `/svc forceblock` 管理员强制屏蔽指定玩家，全服不会听到该玩家。
- `adminmute` 和 `forceblock` 都会写入服务器配置并持久化。

## 配置文件

首次启动后会自动生成：

- 客户端：`SimpleVoiceChat.Client.json`
- 服务器：`SimpleVoiceChat.Server.json`

服务器可配置最大范围、三档范围、是否允许耳语/大喊、是否启用遮挡、天气影响、小队频道、管理员禁言/强制屏蔽列表和 HUD 指示等。

客户端可配置音量、麦克风增益、噪声门、麦克风图片状态、HUD、性能模式和屏蔽列表。

## 当前版本边界

- 第一版是 MVP，重点是可用的接近度语音。
- 使用游戏自带 OpenAL/OpenTK，不随模组额外打包第三方音频库。
- 录音回放、嘴部动画和完整天气传播模型留到后续版本。
- 小队频道当前是面对面 `/svc bind` 绑定后的临时语音频道，暂不做独立 UI 或持久小队管理。
- 如果客户端无法打开麦克风，会在聊天栏提示，仍可听到其他玩家语音。
