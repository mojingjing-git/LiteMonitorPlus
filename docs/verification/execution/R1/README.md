# R1 证据报告（F11/F08/F07）

- 日期：2026-10-27（Asia/Shanghai）
- 计划：`docs/plans/2026-10-27-repair-and-product-execution-plan.md`（v2.1）
- 范围：F11 GPU 热更新代次、F08 预览 1x/2x 主题、F07 最小化/恢复生命周期。
- 结论：**代码完成，自动回归通过**；真实 96–192 DPI、真实 GPU 30 次热更新、50 次隐藏/最小化/恢复与设备创建计数留 G1 实机矩阵。

## 1. 改动

| 缺陷 | 落点 | 处理 |
|---|---|---|
| F11 | `src/Rendering/Contracts/RenderContracts.cs`、`src/Rendering/Presentation/GpuSceneRenderer.cs`、`src/Rendering/Presentation/GpuPresentationHostController.cs`、`src/ThemeEditor/GpuThemePreviewControl.cs` | `RenderGenerations` 增加会话身份比较；`UpdateConfiguration` 接收代次快照并推进；主题代次变化不再被误判为跨会话 |
| F08 | `src/ThemeEditor/ThemePreviewControl.cs` | 预览主题按当前 DPI 解析，1x/2x 使用对应主题而不是固定缓存 |
| F07 | `src/ThemeEditor/GpuThemePreviewControl.cs` | 最小化/不可见时暂停 tick；恢复时重置调度状态并重新渲染，避免恢复后花屏或空转 |

## 2. 验证

```powershell
dotnet build LiteMonitor.csproj -c Release --no-incremental --nologo `
  -p:TreatWarningsAsErrors=true `
  -p:WarningsNotAsErrors=CS0618%3BCS0612%3BCS0619%3BCS0067%3BCS0168%3BCS0219 `
  -p:NuGetAudit=false
# exit 0；0 warnings，0 errors

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~RenderingContractTests|FullyQualifiedName~ThemeStudioGpuSessionAndFallbackTests|FullyQualifiedName~GpuThemeEditorPreflightTests|FullyQualifiedName~RenderingTransitionCoordinatorTests"
# 56/56 通过
```

证据：`r1-build.log`、`r1-tests.log`。

## 3. 未完成（G1 门禁）

- 96/120/144/192 DPI 实机截图与布局断言。
- 真实 GPU 下连续 30 次主题热更新，跨帧不闪烁、不重建设备。
- 最小化/还原/隐藏 50 次，恢复后帧与设备创建数不增长。
- 这些缺口不能由单元测试替代；R5 前必须补齐。
