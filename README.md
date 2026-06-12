# 简单语音对话

作者：VSCN

`SimpleVoiceChat` 是 Vintage Story 的双端语音模组，提供接近度语音、三档说话距离、基础 3D 空间定位、静音和右下角麦克风状态提示。

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
- UDP/麦克风状态下方显示分段音量块：灰色表示未达到，绿色表示正常音量，红色表示输入过大。
- 该麦克风状态属于本地测试/控制提示，不受服务器 HUD 指示开关影响；客户端可用 `ShowMicrophoneHud` 关闭。

图片已打包在模组内：`assets/simplevoicechat/textures/gui/haojiao.png` 和 `assets/simplevoicechat/textures/gui/nohaojiao.png`。

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
```

说明：

- `/svc status` 查看当前语音状态。
- `/svc volume <0-200>` 调整本地播放音量。
- `/svc mute <玩家名>` 屏蔽指定在线玩家。
- `/svc unmute <玩家名>` 取消屏蔽指定在线玩家。

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
```

## 配置文件

首次启动后会自动生成：

- 客户端：`SimpleVoiceChat.Client.json`
- 服务器：`SimpleVoiceChat.Server.json`

服务器可配置最大范围、三档范围、是否允许耳语/大喊、是否启用遮挡和 HUD 指示等。

客户端可配置音量、麦克风增益、噪声门、麦克风图片状态、HUD、性能模式和屏蔽列表。

## 当前版本边界

- 第一版是 MVP，重点是可用的接近度语音。
- 使用游戏自带 OpenAL/OpenTK，不随模组额外打包第三方音频库。
- 群组频道、录音回放、嘴部动画和完整天气传播模型留到后续版本。
- 如果客户端无法打开麦克风，会在聊天栏提示，仍可听到其他玩家语音。
