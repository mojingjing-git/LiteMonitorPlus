# D01–D04 诊断核心证据

- 日期：2026-10-27（Asia/Shanghai）
- 范围：D01 模块事实适配、D02 有界事件收集、D03 诊断窗口基础页、D04 导出服务。
- 结论：**D01/D02/D04 核心合同与单元测试完成，D03 基础页已接入但未做 1 Hz/键盘/明暗验收，D05 未完成，因此本目录不构成 G2 签收。**

## 1. 已落地

| 工作包 | 交付 | 文件 | 状态 |
|---|---|---|---|
| D01 | 快照/模块/Provider 合同；硬件/配置结果/渲染/Web/插件/IPC Provider；可选 Delegate Provider | `src/Diagnostics/DiagnosticSnapshot.cs`、`DiagnosticProviders.cs`、`DiagnosticProviders.Extended.cs`、`MainForm_Transparent.cs` | 五个模块均已接组合根；插件/IPC 健康明确标记未仪表化 |
| D02 | 1024 条滚动、约 2 MiB、脱敏、限频合并、丢弃计数；应用结果/启动事件接线；上次运行正常退出标记 | `DiagnosticEvent.cs`、`DiagnosticRedaction.cs`、`DiagnosticEventStore.cs`、`DiagnosticHub.cs`、`DiagnosticRunMarker.cs`、`AppActions.cs`、`MainForm_Transparent.cs` | 核心完成；上次运行事件已入时间线，UI 仅以事件展示 |
| D03 | 模块/事件/详情/导出四区域基础页，1 Hz Timer，设置侧栏入口，9 语言 `Menu.Diagnostics`，模块/事件过滤与 Esc 清空 | `src/UI/Settings/DiagnosticPage.cs`、`SettingsForm.cs` | 基础页+过滤完成；1 Hz 资源/慢模块/明暗验收未做 |
| D04 | 允许列表投影、同源预览、ZIP（diagnostic.json + summary.txt）、临时文件原子完成、10 MiB 截断 | `src/Diagnostics/DiagnosticPackageService.cs` | 核心完成；设置页导出入口已由 D03 页面提供，未做用户确认文案验收 |

## 2. 验证

```text
dotnet build LiteMonitor.csproj -c Release --no-restore
→ 0 warnings, 0 errors

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build
  --filter "FullyQualifiedName~DiagnosticRunMarkerTests|FullyQualifiedName~DiagnosticPackageServiceTests|FullyQualifiedName~DiagnosticProviderContractTests|FullyQualifiedName~DiagnosticEventStoreContractTests"
→ 21 passed, 0 failed, 0 skipped
```

日志：`d01-d04-tests.log`。

追加（2026-10-27）：`RenderingDiagnosticProvider` 读取 `RenderingRouteStatusSnapshot`（主题是否声明 GPU、演示/实时背景/回退码/分层窗口），`WebDiagnosticProvider` 读取 `LiteWebServer` 的启停、端口、认证连接数与 Web 数据需求；`PluginDiagnosticProvider` 只读实例计数，`IpcDiagnosticProvider` 明确标记传输健康未仪表化。五个模块均已在 `MainForm` 组合根接入。`DiagnosticProviderContractTests` 新增 6 例，连同导出与事件存储定向 19/19 通过；主项目 Release 0 warnings、0 errors。插件单实例健康与 IPC 传输健康仍未仪表化，快照中以 `health_not_instrumented` 明示，不能声称全模块健康可用。

覆盖点：

- 快照构建逐 Provider 隔离，失败模块变 `provider-failed`，不拖垮整张快照。
- 事件存储容量/上下文预算/合并/丢弃计数/敏感键过滤。
- 应用结果事件记录不抛异常、不改变应用结果。
- 诊断包预览与 ZIP 使用同一投影模型；事件上下文和快照事实均经允许列表过滤；令牌、路径、URL 不进入 ZIP。
- 超过 10 MiB 的包返回 `package-too-large`，不留下目标文件或临时文件。

## 3. 未完成 / 风险

- D03 四区域基础页、模块/事件过滤与 Esc 清空已接入；慢模块后台补齐、1 Hz 资源曲线和真正的明暗主题适配未做，不能算 D03 完成。
- D01 五个模块均已接组合根；渲染/Web 为真实状态，插件仅实例计数、IPC 只有“未仪表化”标记，不能宣称插件/IPC 健康事实完整。
- D02 “上次运行是否正常退出”已用 `%LocalAppData%\LiteMonitor++\diagnostics\last-run.json` 标记，并在启动时写入 `previous-run-clean/unclean` 事件；标记文件损坏/不可写时静默降级，不阻断启动。
- D04 设置页导出入口与用户确认未接；ZIP 的取消路径只有 API，未做 UI。
- D05 已在诊断页接入“Rescan”手动硬件重扫（`RequestFullRescanAsync`，结果与失败均入事件）；重试失败应用与 GPU 重试仍未接。
- D06 真实故障矩阵、50 次开关、资源曲线：未开始。

## 4. 不能据此声称

- 不能声称 G2 已通过或诊断中心可交付给用户。
- 不能声称崩溃后仍有诊断证据；当前事件只驻留进程内。
- 不能声称所有模块都有来源/时间/原因码；组合根接入前只有合同能力。
