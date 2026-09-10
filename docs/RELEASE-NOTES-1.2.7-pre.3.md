# SimpleVoiceChat 1.2.7-pre.3

## 中文

- 修复服务端计算装备语音效果时遍历未初始化的创造库存，导致空引用异常、语音无法转发及日志刷屏的问题。现在只读取角色装备栏，装备栏缺失时返回无装备音效。
- 修复 `EquipmentVoiceEffectRules: []` 无法清空默认规则的问题。自定义规则现在会替换默认列表，多次执行 `/svc reload` 不再重复追加规则；未配置该字段时仍使用默认规则。
- 新增 7 项回归测试，覆盖装备读取、空规则、配置迁移和重复重载。

安装：替换旧模组包后重启服务器。`/svc reload` 只能重载配置，不能加载新的 DLL。如果之前临时关闭了 `EnableEnvironmentalVoiceEffects`，更新后可以恢复为 `true`；将 `EquipmentVoiceEffectRules` 设为 `[]` 可仅关闭装备音效，保留水下效果。

验证：Release 构建成功，全部 95 项自动化测试通过。实际联机语音仍需在服务器更新后验证。

## English

- Fixed a server-side null reference when equipment voice effects scanned an uninitialized creative inventory, interrupting voice forwarding and flooding the log. Equipment checks now read only the character inventory and return no equipment effect when it is unavailable.
- Fixed `EquipmentVoiceEffectRules: []` failing to clear the default rules. Custom rules now replace the default list, and repeated `/svc reload` calls no longer append duplicate rules. Omitting the field still uses the defaults.
- Added 7 regression tests covering equipment access, empty rules, configuration migration, and repeated reloads.

Installation: Replace the old mod archive and restart the server. `/svc reload` reloads configuration only; it cannot load the new DLL. If `EnableEnvironmentalVoiceEffects` was temporarily disabled, it can be restored to `true` after updating. Set `EquipmentVoiceEffectRules` to `[]` to disable equipment effects while retaining underwater effects.

Validation: Release build succeeded and all 95 automated tests passed. Live voice communication still needs verification after updating the server.
