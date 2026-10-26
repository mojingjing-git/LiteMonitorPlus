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
- `src/ThemeEditor/ThemeFileService.cs`（G7：新增 public 路径级载荷入口 `WriteThemeJsonToPath`）
- `src/ThemeEditor/ThemeDocumentStore.cs`、`src/UI/Settings/ThemePreviewService.cs`（GPU 快照方法提 internal 供 Lane B 调用）
- `docs/design/theme-studio/implementation-log.md`（每批次的实施记录归属）

交付：
1. `ThemePropertyCatalog` 改 `partial`，GPU 描述符落新文件 `ThemePropertyCatalog.Gpu.cs`（本线）。
2. TE03 GPU 视图：编辑目标（主效果/兼容外观）切换、共享项标注、fallbackStyle 六槽编辑（消费 TE00 矩阵）。
3. 集成 Lane A/B 交付物：画布预览形态切换、导出菜单。
4. 订阅 `PropertyInspector.PropertyEdited` 的 Rendering 失效级别（GPU 参数变更走重建/降级策略）。
5. TE03 GPU 视图验收硬规则（复审 M4，逐条测试）：材质分列可见性（TE00 §2.1）；
   standard/cyber_punk 固定前景不得显示主题字体/颜色控件（TE00 §2.3）；
   `fallbackStyle.accent` 显示为"标题强调色"（TE00 §2.5）；六槽支持清除显式覆盖；
   **切换编辑目标后 `rendering.backend` 字节不变**。

### Lane A（并行，独立工作树）：横向与任务栏预览控件（TE04 部分）

**只允许新增文件**（禁止改任何既有文件）：

- `src/ThemeEditor/HorizontalPreviewControl.cs`（或 `ThemePreviewViews.cs`）
- `tests/LiteMonitor.Tests/ThemeStudioPreviewFormsTests.cs`

交付：
- 横向预览：固定模拟列（`Column` + `MetricItem` 直接构造，复用 `HorizontalRenderer.Render` 与 `HorizontalLayout.Build`），固定尺寸/DPI/主题 revision。
- 任务栏预览：`TaskbarRenderer.Render(g, cols, light)`；**必须显式标注**"任务栏颜色由设置驱动、布局随主题"，不得宣称颜色随主题变化（事实见 §4 风险 1）。
- **GPU 横向不在本线范围**（复审确认：`GpuThemePreviewControl.UpdateScene` 私有且固定走竖向 `UiSceneSnapshotAdapter.Build`，构造只接 `List<GroupLayoutInfo>`）。接缝需求登记给 Lane I：为 GPU 预览控件增加横向场景注入 API，属后续 TE04 批次。
- 确定性要求（复审新增）：渲染前显式 `TaskbarRenderer.ReloadStyle(cfg)`；`HorizontalLayout` 构造传显式 `new Settings()`，禁止依赖 `Settings.Load()` 读用户磁盘配置；PNG 断言不得基于颜色像素（机器/用户相关）。
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
- JSON 导出：`ThemeExportService` 与 `ThemeDocumentSession` **同属 LiteMonitor++ 程序集**，直接调用 `internal BuildPayload()`（复审确认，无需包装、无需 InternalsVisibleTo）；API 固定为 `ExportJson(ThemeDocumentSession session, string path)`——必须带 session 入参，**禁止静态/环境态草稿**。**禁用 `SaveAs`**（会改写会话身份）。路径级原子写入由 Lane I 提供 `ThemeFileService.WriteThemeJsonToPath(path, bytes)`（门禁 G7），本线只准调用，不得自写替换逻辑。
- 效果图导出：经典走 `ThemePreviewControl.RenderPreview` 静态重载；GPU 走 `GpuThemePreviewControl.TryCaptureOnce`（固定输入、固定背景）。GPU 前置（复审新增）：主题必须带非空 `Rendering`（否则控件 StartRenderer 空引用），并注入**临时目录 `GpuStartupGuard`**（构造参数已支持），不得污染 `%LOCALAPPDATA%`。GPU 不可用 → 失败或显式选择兼容图，文件名/伴随资料标注路线；无 GPU 机器上证据记为"未验证"。
- 投稿资料：最终 theme.json 的 SHA-256/长度/名称/作者/版本/材质/能力 + 路线说明 + 投稿入口文案。
- **不得**声称通过市场校验：`ThemeMarket.ValidateThemeContent` 为 private（ThemeMarket.cs:229），仓库无其它 public 校验入口（复审确认），本线只做结构与 `rendering.Validate()` 校验并输出"待市场 CI/维护者审核"。

验收命令（Lane B 工作树内）：
```
dotnet build LiteMonitor.csproj
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj --filter "FullyQualifiedName~ThemeExportServiceTests"
```

### Lane C（并行，独立工作树）：性能与资源基线（TE08 前半）

**只允许新增文件**：

- `tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs`（**落点锁定**；`LiteMonitor.PerformanceProbe` 是 Exe 工程且不在 sln，`dotnet test --filter` 无法匹配——复审结论）
- `docs/verification/theme-studio/perf-baseline.md`（新目录）

交付：
- 开关工作室 50 次、编辑事务 1000 次、**切换编辑目标 200 次**（重建计划原场景；若集成时尚无该路径则先测主题切换并注明替代）的资源计数（Private Bytes / GDI / USER 句柄 / 托管堆）。
- **基线提交固定 master@832d357**：数值仅作 M1 参考值，非验收值；Lane I 集成后按门禁 G6 重跑并追加第二组数据。
- 计数方法（复审新增）：同一 testhost 进程内**基线 vs 之后差值** + 独立采样窗口；`GetGuiResources` P/Invoke 自带（仅新增文件，参考 `tests/AppearanceResourceProbe/Program.cs:55`）；托管堆用 `GC.GetTotalMemory(false)`；UI 驱动照抄 `ThemeStudioSmokeTests.cs` 的 STA 线程范式。
- 阈值先记录，不硬编码失败线；记录基准机器与场景。

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
| G6 | Lane I 集成完成后必须重跑 Lane C 资源基线（含"切换目标 200 次"），追加第二组数据 |
| G7 | Lane I 先提供 `ThemeFileService.WriteThemeJsonToPath(path, bytes)` 与 GPU 快照 internal 暴露，Lane B 才可开工 |

## 4. 风险登记（复审重点）

1. **任务栏颜色与主题无关**：`TaskbarRenderer.ReloadStyle(cfg)` 取 Settings 颜色（`cfg.TaskbarColor*`），`TaskbarForm` 的布局用 `ThemeManager.Current`（TaskbarForm.cs:242）。预览与文案必须按此事实，不得宣称任务栏颜色随主题变化。
2. **市场校验 API 是 private**（ThemeMarket.cs:229）：投稿准备只能做本地结构校验 + 声明待审核，禁止伪造"已通过市场校验"。
3. **MetricItem 依赖静态解析器**（`DefaultPluginColorResolver`/`DefaultConfigLookup`，MetricItem.cs:22-29）：预览构造 mock 列时必须显式设置或验证空值路径不抛异常。
4. **GPU 预览每次 SetTheme 重建控件**（TE00 §3）：Lane A 若复用必须按 revision 门控，避免证据图测试把重建成本算进性能断言。
5. **历史/恢复并发**：Lane B 只读会话，不得引入第二份草稿状态。
6. **测试宿主锁**：任何并行线测试失败先看 `testhost 锁定`，等待或重试，不改产品代码绕过。
7. **TaskbarRenderer 静态副作用**（复审新增）：`Render` 在静态字体为空时会自动 `ReloadStyle(Settings.Load())` 并重建/Dispose 静态字体；预览必须显式 `ReloadStyle`，证据图不得验证颜色像素。
8. **预览 guard 残留**（复审新增）：`GpuThemePreviewControl.PreviewGuard` 为进程级 Lazy，会写 `%LOCALAPPDATA%` 状态；测试/导出必须注入临时目录 guard。
9. **非 GPU 机器**（复审新增）：GPU 横向/效果图证据记为"未验证"，不得写成通过。

## 4.1 本计划范围外（复审 M3，显式声明）

- 回退同源预览（TE00 §3 要求）：承接 TE04 后续批次。
- 预览点选定位属性：承接 TE04 后续批次。
- GPU 预览会话复用（替换每次 SetTheme 重建）：承接 TE04 后续批次。
- TE05 明暗工作区/混合 DPI 实机矩阵/辅助功能：独立批次。
- TE06 应用结果细分（已保存/兼容生效/失败）与同名重载验证：独立批次。
- TE09 默认入口收口与旧编辑器移除：全部完成后。

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
