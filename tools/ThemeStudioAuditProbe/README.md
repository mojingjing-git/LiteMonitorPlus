# ThemeStudioAuditProbe

主题工作室 N00–N10 独立验收的**可复现探针**。它把
`docs/verification/theme-studio/independent-review/observations.json` 里那 10 条观测点
（`target_switch` / `focus_property` / `preview_click` / `gpu_background_selection` /
`dark_mode` / `blocked_save` / `fallback_export` / `gpu_horizontal_export` / `hidden_gpu` /
`fps_update` / `invalid_update_parent` …）变成一条命令即可重跑的脚本。

## 为什么需要它

独立验收报告（`docs/reviews/2026-10-26-theme-studio-next-stage-acceptance-review.md`）
的 10 条证据当初只在一次性工作目录里生成，仓库内**没有探针脚本**，因此
「重跑观测点」无法在仓库内执行 —— 这正是整改计划 §5.0 / §6.3 G-6 的
「验收报告复验」门禁所要求的义务。本工程是它的入库版本。

## 用法

```powershell
# 输出目录会新建 settings/ themes/ drafts/ 子目录，并写出 observations.json
dotnet run --project tools/ThemeStudioAuditProbe -- .tmp\theme-studio-audit
```

输出的 `observations.json` 与仓库内既有的
`docs/verification/theme-studio/independent-review/observations.json` **同构**，可直接逐字段比对。

TRX 汇总用仓库内的 `docs/verification/theme-studio/independent-review/summarize.py`
（解析 `results/*.trx`，按程序集汇总执行/通过/失败，并单独统计 ThemeStudio 前缀用例）。

## 依赖与约束

- 独立程序集名 `ThemeStudioAuditProbe`（**不与 `LiteMonitor.Tests` 重名**）。
- 需要访问 `LiteMonitor` 的 internal 测试接缝
  （`SettingsHelper.PathOverride`、`ThemeDocumentStore.SetThemeDirForTests` /
  `SetRecoveryDirForTests`、`SettingsThemeManager.Initialize`）⇒
  `LiteMonitor.csproj` 里有对应的 `<InternalsVisibleTo Include="ThemeStudioAuditProbe" />`。
- **不在 `LiteMonitor.sln` 内**，与 `tools/` 下其它探针工程一致；因此不会进入
  `dotnet build LiteMonitor.sln` / `dotnet test` 的构建面，不会污染 CI 门禁计数。
- 需要真实 GPU 与交互式桌面会话；无 GPU 环境下 GPU 段会记录
  `gpu_probe_exception`，其余观测点仍可产出。

## DPI 量测

`docs/verification/theme-studio/measure-dpi.ps1` 打印各显示器的有效 DPI 与窗口 DPI，
用于 N08 矩阵里那些被标为 `NOT-VERIFIED` 的缩放档位（整改计划 E-1）。
