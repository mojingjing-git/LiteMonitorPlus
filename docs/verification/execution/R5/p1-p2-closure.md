# 实施审查 P1/P2 关闭矩阵（R01–R20）

- 日期：2026-10-27（Asia/Shanghai）
- 来源：`docs/reviews/2026-10-27-execution-implementation-review.md`
- 结论：**2026-10-27 复核后：R01–R06/R09 等 13 个原编号可关闭；R07/R08/R10 由 C01/C02/C03 修复，N01–N03 复核问题已修并全量通过；P2 的 R13–R20 关闭，C04–C07 已修复；R11/R12 仍开放。修复方提交复核，最终签字仍以独立复核为准。**
- 回归：N01–N03 修复后全量 **2759 通过 / 8 跳过 / 0 失败（2767）**，7m59s，`r5-full-tests-recheck.log`；Solution CI 门禁 2 构建 44 warnings / 0 errors，`r5-solution-build-p2.log`。复核后定向 23/23（C01/C02/C04/C05/C06）、C03 父/子进程 2/2。

## P1

| 编号 | 结论 | 主要落点 / 证据 |
|---|---|---|
| R01 | 已修复 | `GpuThemePreviewControl.ResizeRenderer` 同尺寸不再单边递增 Size 代次；真实切换 200 次 + R10 出帧门禁 |
| R02 | 已修复 | `ThemeStudioForm.OnGpuPreviewUnavailable` 只切画布，不触发 `SetTheme` 重试闭环 |
| R03 | 已修复 | `ThemeManager` 改用 `File.ReadAllText`，BOM/UTF-16 主题可读；SHA256 仍按原始字节 |
| R04 | 已修复 | v2 `TrendHistoryPointRecord` / `TrendBucket` 保留 Max/Min/Count，写入/读回/导入完整 |
| R05 | 已修复 | `ScenarioProfileStore` 主库缺失/损坏时从 `.bak` 恢复；恢复失败记 `LastLoadWarning` |
| R06 | 已修复 | `ScenarioPreflight` 增加 `interactive-entry` 阻塞：隐藏主窗体/穿透/隐藏托盘同时成立 |
| R07 | 复核后重新打开，C01 修复待复核 | C01 让 `AlertRuntime` 使用快照源时间/设备身份；设备身份短暂缺失时按原设备中断计时，不把 UI 消费延迟当采样时间 |
| R08 | 复核后重新打开，C02 修复待复核 | C02 同名主题按提交哈希判断是否真正提交，恢复按原文件路径而非显示名；`ThemeRuntimeCommitTests` 覆盖 |
| R09 | 已修复 | Web 模块消费 `ApplyWebServerWithResult`；拒绝/未启动返回可重试失败，不再误报成功 |
| R10 | 复核后重新打开，C03 修复待复核 | C03 把 200 次真实 GPU 切换放入子 testhost；子进程启用 GPU 并断言 `IsGpuPreviewActive`/首帧/200 次期间帧推进/设备不重建；`vstest` 当前程序集 + sentinel 拒绝零匹配；父测试 9 分钟超时后 Kill 整个进程树；父/子各 1/1 通过，3m12s |

## P2

| 编号 | 结论 | 说明 |
|---|---|---|
| R11 | 开放 | 短时秒桶、缺口段、源时间去重仍需实现；当前短时缓冲以 UI Tick 记录 |
| R12 | 开放 | 长期历史仍按指标键聚合，未保存设备身份与质量；需评估 schema 兼容后再改 |
| R13 | 已修复 | 高版本 profiles.json 只读、不隔离、不覆盖；`ScenarioProfileStore` 测试覆盖 |
| R14 | 已修复 | 生产告警重载不因规则 List 引用变化重建 evaluator；旧重载保留兼容语义 |
| R15 | 已修复 | Active/Recovering 且 IncidentId 非空的采样缺口进入 InputUnavailable |
| R16 | 已修复 | `AlertRuleMigration.SyncGpuRules` 增补 `temp.gpu.N` 并保留已有用户规则；Settings.Load 与 GPU 数量回调接入 |
| R17 | 已修复 | 诊断事件合并增加 Severity/OperationId/SessionId 与 ruleId/deviceId/incidentId/eventKind/batchId 身份键 |
| R18 | 已修复 | 诊断脱敏覆盖 Windows 任意盘符路径、Unix 绝对路径、IPv4/IPv6 |
| R19 | 已修复 | 诊断页不可见时停 1 Hz Timer；刷新保留选中模块与详情 |
| R20 | 已修复 | `PowerPolicy` 的 Auto/Eco 用 `Math.Min(normalFps, cap)`，低刷新率不升速 |
| C04 | 已修复 | 规则内容变化（阈值/方向/持续/回差/冷却等签名）重置旧计时；`ChangingRuleContentResetsPendingTimer` 覆盖 |
| C05 | 已修复 | 诊断包脱敏补 IPv6、Windows 任意盘符与 Unix 路径；IPv4 仅识别 4 段点分，`512`/`42.0`/`1200` 等普通数字保留；`R18_任意盘符路径与IP不进入上下文` 覆盖 |
| C06 | 已修复 | 诊断页按 `Parent/Visible` 实际切页停 1 Hz Timer，不再仅依赖 VisibleChanged |
| C07 | 已修复 | 首次 GPU 失败回退到 GDI 时立即套用 `LegacyFallbackStyleResolver.BuildDerivedTheme`，不等下一次模式切换 |

## 复现命令

```powershell
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~ScenarioProfileStore|FullyQualifiedName~DiagnosticEventStore|FullyQualifiedName~PowerPolicy|FullyQualifiedName~DiagnosticPage|FullyQualifiedName~AlertRuntime|FullyQualifiedName~AlertEvaluator"
# 33/33

$env:LITEMONITOR_GPU_TESTS='1'
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~R10GpuFrameGate"
# 1/1

$env:LITEMONITOR_GPU_TESTS='1'
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~场景4_真实GPU主题真实目标切换200次"
# 父/子合计 2 项通过，3m12s；子 testhost 2m24s；超时 Kill 进程树
```

## 未关闭的发布项

R11/R12 属 P2，不阻断 P1 关闭；G1 的真实 DPI/会话/供电、R3 模块级故障注入、R4 真实代理/DNS、G0 独立复核仍使 G1 不能签字。
