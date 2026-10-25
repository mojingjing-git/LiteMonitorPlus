# 派发单 P-F14b —— 设置页明暗主题

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| W3 | 无（F00 已固化）；与 P-F14a 分别交付 | `fix/F14b-settings-theme` | #437 暗色半项 |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-F14b 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读 `docs/plans/dispatch/README.md` 外派总则，再通读拆解计划 §4「P-F14b」。

## 证据锚点

- `src/UI/Controls/LiteUI.cs:9-25`：暗色未实现（验证报告确认，F00 核验未漂移）。
- #437 的两个半项（卡顿/暗色）分别验收分别提交——本包只做暗色；`PluginMarketPage.cs` 禁改（P-F14a 领域，若其在途按合并后状态rebase）。

## 要点（完整步骤见拆解计划 §4 P-F14b）

1. 提取设置控件统一配色；提供跟随系统/浅色/深色三态；**与悬浮窗皮肤体系区分**。
2. 覆盖禁用、选中、错误、提示、菜单与主题预览边界状态；遵守 Draft-Commit（未保存不生效，取消回退）。
3. `resources/lang/*.json` 8 语种同步新增 key。

## 完成产物

1. 分支 + 红→绿提交；断言：全部设置页在三种模式下的渲染一致性（可用探针截图对比）；系统配色切换实时跟随；Draft-Commit 取消回退。
2. 验收明示：不以"只改页面背景色"认定完成。
3. `docs/verification/acceptance/F14b-report.md`。
