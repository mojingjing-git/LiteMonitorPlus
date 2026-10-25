# P-E05 验收记录 —— 设置明暗主题用户路径（W1 三路并行之一）

## 1. 标识

- 工作包：[P-E05](../../plans/dispatch/P-E05.md)；完善计划 §9；审查报告 G1 缺口收口
- 执行者：主会话（SettingsForm 为 B3 选择性应用既有领地，同一负责人）；日期：2026-10-26
- 审查说明：W1 原定经审查 agent 复核后执行——agent 配额耗尽（两次即时失败），降级为**主会话自审**（四问自查：三包零交集✓、E05×B3 联动注明✓、R1 无设备分界✓、验收可判定✓），修正已并入派工单（`098020a`）；独立复核义务由 P-E00 承担（其报告将覆盖本包的联动面）

## 2. 交付

1. **构建前初始化**：`SettingsForm` 构造中 `Initialize(_cfg.SettingThemeMode)` 位于 `InitializeComponent` 之前——窗体第一帧即正确明暗，无"先浅后深"闪烁。
2. **选择器**：`MainPanelPage` 外观组（Skin 下拉之后）新增"设置配色"三态下拉（跟随系统/浅色/深色）——选择即写草稿（`Config.SettingThemeMode` 立即绑定）+ `BeginPreview` 即时预览（`ApplyTo`+`Apply` 双层重绘：全局取色表推送 + 槽位重映射兜底历史控件）。
3. **保存/取消/关闭**：`ApplySettings` 末尾 `SyncFromSettings(_cfg)` 提交（与 Draft-Commit 语义对齐；无变化时幂等）；`OnFormClosing` 统一 `CancelPreview`（保存后关闭为空操作）；窗体销毁注销两处订阅。
4. **系统配色**：`SystemEvents.UserPreferenceChanged` 仅响应 `General` 类；`BeginInvoke` 切 UI 线程；`IsDisposed/IsHandleCreated` 防护迟到消息；经 `ApplySystemColorChange` 只影响"跟随系统"模式。
5. **与 B3 联动**：`SettingThemeMode` 归 Appearance 组=保存时整组界面重建（与 Skin 同待遇）；测试护栏锁定采样/Web/插件动作未触发。
6. **语言资源**：9 语言 × 4 键（SettingsThemeMode/System/Light/Dark）。

## 3. 证据

| 项 | 结果 |
|---|---|
| 构建 | 0 错误 |
| 全量 | **2438 执行 / 0 失败**（发现 2445，含 5 例接线护栏） |
| 既有 f14b 状态机 52 条 | 全绿（未改引擎语义） |
| architecture-assert | 12/12，基线零变化 |
| 行为 fixture 181 | 全绿（显示格式零影响） |

护栏（`E05ThemeWiringGuardTests`，5 例）：初始化先于 UI 构建、双层重绘+跨线程调度、保存提交/取消还原/退出注销、General 过滤+释放防护、选择器写草稿+即时预览。

## 4. 状态变更

审查 G1"引擎完成、用户功能待接线" → **"引擎完成 + 用户路径接线完成"**；#437 暗色半项的 UI 侧可关，最终关闭待真实界面验收（下节）。

## 5. 待实机清单（用户）

1. 首次打开：窗体首帧即为当前模式配色（无闪烁）；
2. 三态选择即时预览；保存→重开保持；取消/右上角关闭→恢复已提交模式；
3. Windows 设置切"应用颜色"：跟随系统模式实时翻转；显式浅/深不受影响；
4. 混合 DPI、多设置页往返配色一致；输入框/禁用控件/菜单/市场页/错误提示覆盖检查；
5. 与悬浮窗 Skin 独立：切设置主题不影响主面板皮肤/GPU 材质，采样与读数不断流。
