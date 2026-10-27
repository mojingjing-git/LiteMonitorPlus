# R3 证据报告（F02 分阶段加载/应用结果）

- 日期：2026-10-27（Asia/Shanghai）
- 计划：`docs/plans/2026-10-27-repair-and-product-execution-plan.md`（v2.1）
- 范围：F02 真实加载结果、分阶段应用结果、部分成功、实际主题/路线/修订。
- 结论：**核心路径已实现，自动回归通过；六类故障注入矩阵尚未全部覆盖，R3 未关闭。**

## 1. 改动

| 落点 | 处理 |
|---|---|
| `src/Core/ThemeManager.cs` | 新增 `ThemeLoadResult`（请求名、源路径、SHA-256 摘要、实际主题、Success/UsedDefault/Error）、`Inspect`（只读预检，不改 `Current`）、`LoadWithResult`、`LastLoad`；旧 `Load` 保留缺省主题容错语义 |
| `src/ThemeEditor/ThemeStudioLauncher.cs` | 保存前预检主题文件，坏 JSON/缺失文件不落配置；运行时提交后核对 `LastLoad` 请求身份；改用 `ApplyThemeAndLayoutWithResult`；主题已生效但菜单/任务栏失败时返回 `Partial`，不回滚 `Skin` 谎称旧主题仍在 |
| `src/Core/Actions/AppActions.cs` | 新增 `ApplyThemeAndLayoutWithResult`，按 ThemeRuntime/Menus/Taskbar/Layout 阶段返回 `ApplyOperationResult`；旧 `ApplyThemeAndLayout` 保留抛错语义 |
| `src/ThemeEditor/ThemeStudioForm.cs` | `ThemeApplyResult` 增加 `Partial` 与 `Modules`；UI 明示「主题已生效，但部分模块失败；不存在原子回滚」 |

## 2. 验证

```powershell
dotnet build LiteMonitor.csproj -c Release --no-incremental --nologo `
  -p:TreatWarningsAsErrors=true `
  -p:WarningsNotAsErrors=CS0618%3BCS0612%3BCS0619%3BCS0067%3BCS0168%3BCS0219 `
  -p:NuGetAudit=false
# exit 0；0 warnings，0 errors

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~ThemeStudio|FullyQualifiedName~ThemeExport|FullyQualifiedName~R2PreviewExportRegressionTests|FullyQualifiedName~R3ThemeLoadContractTests"
# 65/65 通过（2026-10-27 初跑）

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-restore --nologo `
  --filter "FullyQualifiedName~R3ThemeLoadContractTests|FullyQualifiedName~ThemeStudioNextStageWorkflowTests"
# 10/10 通过（新增 Settings 写失败、Partial 呈现用例）
```

新增 `tests/LiteMonitor.Tests/R3ThemeLoadContractTests.cs` 4 项：

- 缺失文件：预检失败、`UsedDefault=true`、`Theme=null`，且不修改 `ThemeManager.Current`。
- 坏 JSON：预检失败、错误非空，且不修改 `Current`。
- 同名主题修改后：两次预检摘要不同，且不修改 `Current`。
- 有效文件：`Success=true`、源摘要为 64 位 SHA-256、返回主题对象。

## 3. 未覆盖（R3 关闭前必须补）

- Settings 写入失败：已由 `F02_Settings写失败_不进入运行时且回滚Skin` 覆盖（网关级 `GlobalBlockSave`，不调用运行时）。
- 菜单/任务栏失败：工作室 UI 已由 `N06` 的 Partial 用例覆盖；`ApplyThemeAndLayoutWithResult` 的可控模块注入仍未覆盖，模块重试留 G1。
- GPU 首帧失败：仍走现有呈现事件，R3 未新增 Pending/Unknown 注入测试。
- 显式恢复旧主题再次失败：两次结果保留语义尚未测试。
- 同名主题摘要已由 `R3ThemeLoadContractTests` 覆盖；网关运行时“新摘要进入运行时”断言留 G1 真机/集成。
