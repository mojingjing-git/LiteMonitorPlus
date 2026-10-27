# R2 证据报告（F04/F05/F06/F09）

- 日期：2026-10-27（Asia/Shanghai）
- 计划：`docs/plans/2026-10-27-repair-and-product-execution-plan.md`（v2.1）
- 范围：F04 主效果 GDI 同源回退、F05 横向形态保持、F06 任务栏设置快照、F09 独立任务栏导出。
- 结论：**代码完成，自动回归通过**；工作室截图/像素级视觉验收留 G1 实机矩阵。

## 1. 改动

| 缺陷 | 落点 | 处理 |
|---|---|---|
| F04 | `src/ThemeEditor/ThemePreviewControl.cs` | 主效果目标在 GPU 关闭/运行期失败时，用 `LegacyFallbackStyleResolver.BuildDerivedTheme` 生成派生主题；GDI 兜底不再直接画主效果原始背景 |
| F05 | `src/ThemeEditor/ThemePreviewControl.cs`、`src/ThemeEditor/ThemeStudioForm.cs` | 新增 `GpuPreviewUnavailable` 事件与横向形态保持；GPU 不可用时横向画布切到 `HorizontalPreviewControl`，不退成竖向 `UILayout` |
| F06 | `src/ThemeEditor/ThemeStudioForm.cs`、`src/ThemeEditor/ThemePreviewViews.cs` | 任务栏预览创建/刷新时调用 `ApplySettings(_appSettings.DeepClone())` 并同步明暗，不再沿用控件构造时的空 Settings |
| F09 | `src/ThemeEditor/ThemeExportService.cs`、`src/ThemeEditor/ThemeStudioForm.cs` | 新增 `ThemeExportImageFormFactor.Taskbar` 与专用 `ExportTaskbarImage`（400×32）；任务栏导出不再落入 Vertical；GPU 路线明确失败而非静默换形态 |

## 2. 验证

```powershell
dotnet build LiteMonitor.csproj -c Release --no-incremental --nologo `
  -p:TreatWarningsAsErrors=true `
  -p:WarningsNotAsErrors=CS0618%3BCS0612%3BCS0619%3BCS0067%3BCS0168%3BCS0219 `
  -p:NuGetAudit=false
# exit 0；0 warnings，0 errors

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~RenderingContractTests|FullyQualifiedName~ThemeStudioGpuSessionAndFallbackTests|FullyQualifiedName~ThemeStudioPreviewFormsTests|FullyQualifiedName~ThemeExportServiceTests|FullyQualifiedName~ThemeStudioSmokeTests|FullyQualifiedName~R2PreviewExportRegressionTests"
# 46/46 通过
```

新增 `tests/LiteMonitor.Tests/R2PreviewExportRegressionTests.cs` 5 项：

- F04 主效果 GPU 关闭时派生背景为 `#010203`，不是主效果 `#101010`。
- F05 横向形态在 GPU 关闭时仍为 `Horizontal`，且使用派生主题。
- F06 `TaskbarPreviewControl.ApplySettings` 带入单行、颜色来源、字体。
- F09 任务栏导出固定 400×32，读取 PNG 尺寸断言。
- F09 GPU 路线导出任务栏明确失败，不生成静默竖向文件。

证据：`r1-r2-tests.log`、`r2-build.log`。

## 3. 未完成（G1 门禁）

- 工作室真实窗口截图/像素对比（zh/en、明/暗、96–192 DPI）。
- 生产样式不变与投稿默认 orientation 不变的回归截图。
- 真实 GPU 下横向预览切换与任务栏预览的实机走查。
