# SimpleVoiceChat 1.2.7

## 中文

- 设置、向导、频道和玩家详情、邀请、HUD 全部迁移到 RmlUi，沿用原有窗口内容及逐级返回逻辑。
- 保留原有 PNG 图标，原 SVG 控件使用 Tabler 图标；修正按钮与下拉框文字的垂直对齐。
- 右下角 HUD 移除频道和玩家名称，缩小到 92dp 高度，取消边框，恢复约 34% 不透明度的背景；邀请窗口也取消边框。
- 继承 1.2.7-pre.3 的装备语音效果和配置重载修复。

安装：客户端与服务器均需安装 `VSRmlUi (vsrmlui) 1.0.1` 或更新兼容版本。替换旧模组包后重启游戏和服务器。Vintage Story 版本为 `1.22.3`，语音协议仍为 V10。

扩展 API：自定义界面扩展改用 `VoiceRmlForm`，需要重新编译；文字与图片按钮的注册接口保持不变。

## English

- Migrated settings, setup, channel/player details, invitations, and the HUD to RmlUi while retaining the original window contents and return navigation.
- Preserved PNG icons, replaced SVG controls with Tabler icons, and centered button and dropdown text vertically.
- Removed channel/player names from the HUD, reduced its height to 92dp, removed its border, and restored the background to approximately 34% opacity. Invitations are also borderless.
- Includes the equipment voice-effect and configuration-reload fixes from 1.2.7-pre.3.

Install `VSRmlUi (vsrmlui) 1.0.1` or a newer compatible version on both client and server. Replace the old archive and restart both. Requires Vintage Story `1.22.3`; voice protocol remains V10.

Extension API: custom UI extensions must rebuild against `VoiceRmlForm`; text/image button registration interfaces are unchanged.
