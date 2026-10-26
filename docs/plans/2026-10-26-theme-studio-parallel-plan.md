# 主题工作室剩余批次并行执行计划

- 日期：2026-10-26。
- 基线：master @ `832d357`（M1 完成：TE00/TE01/TE02/TE03 目录 + 入口/预览/GPU 保护隔离）。
- 依据：[重做计划](2026-10-26-theme-studio-rebuild-plan.md) §9/§11、[TE00 能力合同](../design/theme-studio/TE00-capability-contract.md)、[实施记录](../design/theme-studio/implementation-log.md)。
- 目标：在不制造共享文件冲突的前提下，用 1 条串行集成线 + 3 条独立并行线推进 TE03(GPU)/TE04/TE07/TE08。

## 1. 并行结构与文件所有权

### Lane I（集成线，串行，本人执行）

唯一允许修改以下文件的线：

- `src/ThemeEditor/ThemeStudioForm.cs`
- `src/ThemeEditor/PropertyInspector.cs`
- `src/ThemeEditor/ThemePropertyCatalog.cs`（先改 `partial`）
- `src/ThemeEditor/ThemeDocumentSession.cs`（仅必要扩展）
- `src/UI/MenuManager.cs`、`src/Core/Actions/AppActions.cs`、`src/UI/UIController.cs`、`src/UI/Settings/*`
- `src/ThemeEditor/ThemePreviewControl.cs`、`GpuThemePreviewControl.cs`（预览宿主与 GPU 生命周期）

交付：
1. `ThemePropertyCatalog` 改 `partial`，GPU 描述符落新文件 `ThemePropertyCatalog.Gpu.cs`（本线）。
2. TE03 GPU 视图：编辑目标（主效果/兼容外观）切换、共享项标注、fallbackStyle 六槽编辑（消费 TE00 矩阵）。
3. 集成 Lane A/B 交付物：画布预览形态切换、导出菜单。
4. 订阅 `PropertyInspector.PropertyEdited` 的 Rendering 失效级别（GPU 参数变更走重建/降级策略）。

### Lane A（并行，独立工作树）：横向与任务栏预览控件（TE04 部分）

**只允许新增文件**（禁止改任何既有文件）：

- `src/ThemeEditor/HorizontalPreviewControl.cs`（或 `ThemePreviewViews.cs`）
- `tests/LiteMonitor.Tests/ThemeStudioPreviewFormsTests.cs`

交付：
- 横向预览：固定模拟列（`Column` + `MetricItem` 直接构造，复用 `HorizontalRenderer.Render` 与 `HorizontalLayout.Build`），固定尺寸/DPI/主题 revision。
- 任务栏预览：`TaskbarRenderer.Render(g, cols, light)`；**必须显式标注**"任务栏颜色由设置驱动、布局随主题"，不得宣称颜色随主题变化（事实见 §4 风险 1）。
- GPU 横向：若可控则复用 `UiSceneSnapshotAdapter.BuildHorizontal` + 既有 `GpuThemePreviewControl`；不可控则明确不做并记录原因。
- 离屏证据图测试（PNG 非空 + 尺寸断言），不建捕获源。

验收命令（在 Lane A 工作树内）：
```
dotnet build LiteMonitor.csproj
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj --filter "FullyQualifiedName~ThemeStudioPreviewFormsTests"
```

### Lane B（并行，独立工作树）：导出与投稿准备（TE07）

**只允许新增文件**：

- `src/ThemeEditor/ThemeExportService.cs`
- `tests/LiteMonitor.Tests/ThemeExportServiceTests.cs`

交付：
- JSON 导出：取 `ThemeDocumentSession.BuildPayload()`（当前 `internal`，Lane B **不得改会话文件**；若需访问，先在本线用 `InternalsVisibleTo` 已覆盖的测试，产品调用通过新增 public 包装 `ExportJson(path)` 留在本文件——不改会话源码）。
- 效果图导出：经典走 `ThemePreviewControl.RenderPreview` 静态重载；GPU 走 `GpuThemePreviewControl.TryCaptureOnce`（固定输入、固定背景），GPU 不可用 → 失败或显式选择兼容图，文件名/伴随资料标注路线。
- 投稿资料：最终 theme.json 的 SHA-256/长度/名称/作者/版本/材质/能力 + 路线说明 + 投稿入口文案。
- **不得**声称通过市场校验：`ThemeMarket.ValidateThemeContent` 为 private（ThemeMarket.cs:229），本线只做结构与 `rendering.Validate()` 校验并输出"待市场 CI/维护者审核"。

验收命令（Lane B 工作树内）：
```
dotnet build LiteMonitor.csproj
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj --filter "FullyQualifiedName~ThemeExportServiceTests"
```

### Lane C（并行，独立工作树）：性能与资源基线（TE08 前半）

**只允许新增文件**：

- `tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs`（或 tests/LiteMonitor.PerformanceProbe 下一个新文件）
- `docs/verification/theme-studio/perf-baseline.md`（新目录）

交付：
- 开关工作室 50 次、编辑事务 1000 次、切换主题 200 次的资源计数（Private Bytes / GDI / USER 句柄 / 托管堆），给出"不线性累积"断言或明确的观测报告（阈值先记录，不硬编码失败线）。
- 记录基准机器与场景，输出可复跑的脚本/命令。

验收命令（Lane C 工作树内）：
```
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj --filter "FullyQualifiedName~ThemeStudioResourceTests"
```

## 2. 工作树与构建隔离

同 bin 目录并行 build/test 会互相锁 DLL（本轮已三次遇到 `MSB3027 testhost 锁定`）。执行机制：

1. 每个并行线一个 `git worktree`：`G:/TOOL/LiteMonitor-fork.wt-laneA`（分支 `lane/te04-previews`）、`.wt-laneB`（`lane/te07-export`）、`.wt-laneC`（`lane/te08-perf`）。
2. 各线只在自己的工作树内 build/test/commit；每线提交信息前缀 `lane-A:` / `lane-B:` / `lane-C:`。
3. 合流顺序：Lane C（零产品文件）→ Lane B（新文件）→ Lane A（新文件）→ Lane I 集成提交。
4. 合并后主工作树跑全量回归；并行线不直接 push。

## 3. 依赖与门禁

| 门禁 | 说明 |
|---|---|
| G1 | `ThemePropertyCatalog` 改 partial 由 Lane I 先行完成（一行），Lane A/B 不依赖它 |
| G2 | Lane A 的 GPU 横向截图若依赖 GPU 会话复用，允许只交付 GDI 横向 + 任务栏，GPU 部分记录为 TE04 后续 |
| G3 | Lane B 的 GPU 效果图依赖 `TryCaptureOnce`，该 API 已存在；不依赖 Lane A |
| G4 | 任何线发现需要改白名单外文件：停止该改动，记录到交付说明，由 Lane I 收口 |
| G5 | 全量回归失败在主线修复，不在并行线互相打补丁 |

## 4. 风险登记（复审重点）

1. **任务栏颜色与主题无关**：`TaskbarRenderer.ReloadStyle(cfg)` 取 Settings 颜色（`cfg.TaskbarColor*`），`TaskbarForm` 的布局用 `ThemeManager.Current`（TaskbarForm.cs:242）。预览与文案必须按此事实，不得宣称任务栏颜色随主题变化。
2. **市场校验 API 是 private**（ThemeMarket.cs:229）：投稿准备只能做本地结构校验 + 声明待审核，禁止伪造"已通过市场校验"。
3. **MetricItem 依赖静态解析器**（`DefaultPluginColorResolver`/`DefaultConfigLookup`，MetricItem.cs:22-29）：预览构造 mock 列时必须显式设置或验证空值路径不抛异常。
4. **GPU 预览每次 SetTheme 重建控件**（TE00 §3）：Lane A 若复用必须按 revision 门控，避免证据图测试把重建成本算进性能断言。
5. **历史/恢复并发**：Lane B 只读会话，不得引入第二份草稿状态。
6. **测试宿主锁**：任何并行线测试失败先看 `testhost 锁定`，等待或重试，不改产品代码绕过。

## 5. 回滚

- 各线独立分支与工作树，删除工作树 + 分支即回滚该线。
- Lane I 的 catalog partial 与 GPU 描述符独立提交，可单独 revert。
- 合流后全量回归不过：先回滚最近一次合流提交，再定位。

## 6. 复审清单（给复审 agent）

- [ ] 每条线的"允许新增文件"是否真的与主线零交集（用 `git grep` 核对不被引用的既有文件）。
- [ ] 计划引用的 API（`HorizontalRenderer.Render`、`TaskbarRenderer.Render`、`HorizontalLayout.Build`、`TryCaptureOnce`、`RenderPreview`、`BuildPayload`）签名、可见性是否与计划一致。
- [ ] §4 风险 1/2 的事实是否成立；若不成立，计划断言需修正。
- [ ] Lane B 在"不得改会话文件"约束下能否拿到导出载荷；`internal` 可见性方案是否可行。
- [ ] Lane C 的资源断言是否可稳定运行（现有测试基础设施是否支持句柄计数）。
- [ ] 合流顺序与门禁是否存在遗漏（例如 `ThemePropertyCatalog` 的 GPU 描述符被谁消费）。
- [ ] 是否存在未列出的共享文件冲突（测试文件命名、docs 目录、csproj 变更）。
