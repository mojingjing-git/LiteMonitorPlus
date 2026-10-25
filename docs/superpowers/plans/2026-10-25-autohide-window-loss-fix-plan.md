# 自动隐藏"窗口消失"修复计划（R2 · 已执行）

日期：2026-10-25。分支：`fix/autohide-window-loss`（自 master `dd04fdd` 拉出，独立工作树 `.worktrees/autohide-fix`）。
复审：独立代理复审结论 NEEDS-REVISION（1×P1、3×P2、4×P3），全部并入本 R2；复审要点见 §6。

---

## 1. 根因（已实测确认）

用户故障：副屏操作时面板"双屏皆无、进程正常"、概率性复现。

根因 = 三重叠加（`src/UI/Helpers/MainFormBizHelper.cs`，基线锚点 :30-31/:119-164/:237-246）：

1. **泊出坐标被持久化**：`AutoHide=true` 时面板贴边（≤10px）即泊出屏幕只留 `_hideWidth=4` 物理像素边条，
   `SavePos` 会把泊出坐标写进 settings.json——用户实测 `Position.X=-1440` 正是泊出数学指纹
   （`-1152-(292-4)`，宽 292；复审订正：-1440 对应宽 292，宽 288 时为 -1436）。
2. **4px 边条肉眼不可见**：`_hideWidth=4` 不随 DPI 缩放，副屏边框/过扫描一盖即"消失"。
3. **弹出带死逻辑 + 取屏不可靠**：旧弹出要求光标同时落在 hideWidth(4px) 带与 hoverBand(30px) 带内，
   有效带宽 = 4px（hoverBand 30px 从未真正生效）；泊出后窗口中心在所有屏之外，
   `Screen.FromPoint(center)` 在其他多屏拓扑下可能取错屏。

## 2. 修复内容（已全部实现）

| 项 | 内容 |
| --- | --- |
| A | 纯函数核心 `src/UI/Helpers/AutoHideLogic.cs`：`Decide`（泊出/弹出）+ `ClampToArea` + `IntersectsAny` + `EffectiveSaveLocation` + `MetricsForDpi`；语义逐字迁移，DockEdge 统一 |
| B | 泊出屏记忆：`_parkedArea` + `_parkedScreenDevice` + `_preParkLocation`（park 时记账，弹出判定/坐标/落盘只用它们） |
| C | DPI 现算：`MetricsForDpi(deviceDpi)`——hideWidth = max(4·dpi/96, **6**)，threshold = 10·dpi/96 |
| D | 泊出治理：**timer 无条件启动**（评审 P1：旧链路关 AutoHide 即停 tick ⇒ 恢复/看门狗全死代码）；`ApplyWindowAttributes` 关闭分支改 `DisableAutoHide()`（unpark）；SavePos 泊出态用 pre-park 值且跳过 Clamp；自动弹出后 `KeepVisible(1.0)` 防抖 |
| E | 看门狗：250ms tick 追加零交集检测（连续 ≥20 tick ≈5s）→ `ClampToScreen(force)` + `KeepVisible(3.0)` + `window-watchdog.log`（try/catch）+ 首次托盘气泡；泊出态有交集不触发 |
| F | ForceShow 优先回 `_preParkLocation`（多屏对角布局"最近屏"≠泊出屏），并清泊出态 |
| G | 行为变更（有意，评审确认）：**弹出带 4px → 30px 主导**；hideWidth 4 → ≥6 |
| H | 门禁 2029 → **2051**（+22，ci/release 逐字一致） |

## 3. 验收记录（2026-10-25，本工作树实测）

- LiteMonitor.Tests：**1959**（通过 1958 / 0 失败 / 1 指定 E2E 跳过），含新增 `AutoHideLogicTests` 22 条
  （泊出公式 ×3、居中/光标内/KeepVisible 门 ×3、弹出 30px 带 ×6【含故障复现与错屏免疫】、ClampToArea ×2、
  看门狗判据 ×2、落盘守卫 ×2、DPI ×3——期望值全部由公式推导，无硬编码 -1440）。
- Updater.Tests 73/0；GpuNonperformance.Tests 19/0。总计 **2051 / 2050 / 1**，阈值 2051。
- 构建零错误；锚点重验：F04 批次未触碰 `MainFormBizHelper.cs`（`8041df8` 仅任务栏域），锚点零漂移。
- 终验：连续 2 次全量绿（见提交后补记）+ 实机冒烟（用户桌面：副屏贴边泊出 → 30px 带弹出 → 关 AutoHide 自动恢复）。

## 4. 已知边界

- 弹出带 30px 与泊出 6px 条的组合意味着"露出 6px、贴边 30px 内可弹出"——过扫描严重的显示器上建议用户
  把面板拖离边缘 10px 以上（贴边泊出本身是功能语义，不在本计划内改变）。
- 看门狗日志位于 BaseDirectory（`window-watchdog.log`）；Program Files 安装可能无写权限 ⇒ try/catch 静默缺席。

## 5. 红线遵守

不碰 integration/w1 与其他工作树；不新增 settings 字段；既有自动隐藏行为除弹出带/hideWidth 两项明示变更外零变化；
阈值只升不降；C-1 测试纪律（无新增文件型测试，核心为纯函数）。

## 6. 独立复审记录（2026-10-25，agent `agent_b054216b`）

结论 NEEDS-REVISION → 全部并入：
- **P1** D/E 挂载点死代码（关闭 AutoHide 链路 StopTimer ⇒ 恢复/看门狗永不运行）→ timer 无条件 + 内部 gate + 关闭分支 unpark；
- **P2** 泊出指纹算术（-1436 vs -1440，宽 288/292 之差）→ 测试期望值改为公式推导；
- **P2** 悬停带 30px 死逻辑（有效弹出带 = min(4,30)）→ 弹出判据 30px 主导；
- **P2** ForceShow 缺 pre-park → 已补；
- **P3** UIUtils 路径/根因表述/F 项纯函数化/SavePos 失同步/看门狗日志 try-catch 与气泡依赖 tray——逐条落实。
