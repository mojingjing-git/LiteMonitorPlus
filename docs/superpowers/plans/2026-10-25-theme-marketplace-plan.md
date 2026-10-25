# 主题市场（Theme Marketplace）设计与实施计划

日期：2026-10-25。状态：DRAFT / 待独立复审。
基线：master @ `70406b6`（候选工作树交付已合入：R3 渲染路线 + 独立复审整改 + C-1 确定性修复）。
前置讨论：主题编辑器重设计讨论（本计划为平行件，不依赖编辑器重构结论）。

---

## 0. 一句话

在现有插件市场的**同一套基础设施**（索引拉取、SHA-256、原子安装事务、来源白名单、契约测试）上，
增加"主题市场"：**纯 JSON 主题包 + 附加索引段 + 客户端两层现场渲染**（不带预览图、不发预览资产）。

## 1. 目标与完成标准

1. 市场页可浏览/安装/卸载市场主题；安装来源可辨、内置主题不可卸载、卸载当前主题自动切走。
2. 市场卡片**现场渲染**：懒渲染快照 + 磁盘缓存；选中主题有单个活跃实时预览窗格。
3. 插件市场既有行为零回归：既有 6 条契约测试与 E2E 原样通过。
4. 主题包安全面如实陈述并有测试背书：主题 JSON 无远程资源引用（已核实），坏主题在安装时拒收。
5. 全量测试（Debug/Release）零失败、门禁阈值同步上调；连续 3 次全量绿（C-1 纪律延续）。

## 2. 已核实事实（file:line 为 2026-10-25 master 锚点）

| # | 事实 | 位置 |
| --- | --- | --- |
| F1 | 索引 URL 硬编码 `raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/index.json` | `src/Plugins/PluginMarket.cs:115` |
| F2 | 下载前缀白名单锁死 `raw.githubusercontent.com` + `/mojingjing-git/awesome-litemonitor-plugins/main/plugins/`（防路径穿越/仿冒域） | `src/Plugins/PluginMarket.cs:174-175` |
| F3 | **客户端严格校验 `index.schema == 1`，不符整份索引拒收**（`SupportedIndexSchema = 1`） | `src/Plugins/PluginMarket.cs:111,410-413` |
| F4 | 索引模型：`plugins[]`，条目含 id/name/author/version/file/download_url/readme_url/page_url/sha256/size/status/verified | `src/Plugins/PluginMarket.cs:51-98`；样例 `tests/fixtures/plugin-repo/index.json` |
| F5 | 安装记录持久化于 `Settings.MarketPlugins`（settings.json），读写走 `PluginMarketState` | `src/Core/Settings.cs:296`；`src/Plugins/PluginMarketState.cs:29-55` |
| F6 | 安装事务：stage → journal → commit（同卷 `File.Replace`）→ 崩溃恢复（`RecoverInterruptedInstallsIn`），严格记录写入器 | `src/Plugins/PluginMarketInstallCore`；F04 批次加固 |
| F7 | **主题 JSON 无任何远程资源引用**：`BackdropConfig` 仅 mode/opacity/blur/tint/渐变/噪点/辉光，无 imageUrl/URL 字段 | `src/Core/ThemeManager.cs:301-330`；`src/System/Backdrop/` 全目录 grep 无 http |
| F8 | GPU 主题参数 `rendering.parameters`（cornerRadius/thickness/refraction/blurRadius/clearCenterRatio/surfaceOpacity/highlight/counterHighlight/shadow）+ `rendering.motion.targetFps`（1–240） | `resources/themes/LiquidGlass_GPU.json`；`ThemeRenderingConfig.Validate()`（`src/Rendering/Configuration/ThemeRenderingConfig.cs:88+`） |
| F9 | GPU 预览控件内嵌真 `GpuSceneRenderer`，**刻意不构造抓屏源**（"intentionally never constructs a capture source"） | `src/ThemeEditor/GpuThemePreviewControl.cs:15,119` |
| F10 | `GpuSceneRenderer.CaptureScenePng` 可从 GPU 帧缓冲回读 PNG；`UiSceneSnapshotAdapter` 可构建演示数据快照 | `src/Rendering/Presentation/GpuSceneRenderer.cs:767`；`src/Rendering/Scene/UiSceneSnapshotAdapter.cs` |
| F11 | legacy 预览控件走真 `UIRenderer` 管线，可画到 bitmap | `src/ThemeEditor/ThemePreviewControl.cs:299` |
| F12 | 主题目录 = `AppContext.BaseDirectory/resources/themes`（平铺，自动创建）；当前 15 个内置主题（12 legacy + 3 GPU） | `src/Core/ThemeManager.cs:441-451` |
| F13 | 契约测试 6 条 + E2E 1 条（`LITEMONITOR_E2E` / `LITEMONITOR_PLUGIN_REPO` 门控） | `tests/LiteMonitor.Tests/MarketIndexContractTests.cs`；`MarketE2ETests` |
| F14 | GPU 路线进入条件 = 主题声明 + 用户开关两者满足（市场主题不能自行打开用户的 GPU/WDA 开关） | R3 §1.2/§1.3（`RenderingRoutePolicy`） |
| F15 | master 工作树存在一批未提交的治理改动（.gitattributes 行尾治理 + csproj 本地 worktree 排除，104 文件 +624/−1022） | `git status`（2026-10-25） |

## 3. 设计决策

### D1 仓库（待拍板，本计划默认 A）
- **A（默认）**：复用 `awesome-litemonitor-plugins` —— 加 `themes/` 目录；**索引顶层 `schema` 保持 `1`**（见 D3），新增附加段 `themes: [...]`。一个域名白名单、一套 CI 索引生成、一个入口。
- **B**：新仓库（如 `better-litemonitor-themes`）—— 客户端需要第二份索引 URL 常量 + 第二份下载前缀白名单（复制 F2 机制），改动多一处且市场页需要双源合并。
- 用户曾提及"better lite monitor"近似仓库名；若选 B，仓库名由用户定，本计划其余部分不变。

### D3 索引兼容策略（由 F3 决定，无选择空间）
**顶层 `schema` 保持 `1`，`themes` 作为附加段加入。** 老客户端（已发布版本）用 System.Text.Json 反序列化已知字段，未知字段忽略、schema 仍为 1 ⇒ 行为完全不变。**禁止**把顶层 schema 升到 2 —— 会立刻打死所有已发布客户端（F3 是整份索引拒收，不是警告）。未来真有不兼容变更再议 schema 版本策略。

### D4 包格式与资产（已与用户确认）
**纯 JSON 单文件主题包**（直链 raw 下载，与插件 `file` 字段同构），**不带预览图/zip**。预览由客户端现场渲染（D5）。索引可选保留 `preview_url`（可空）作为"作者自愿提供渲染效果图"的扩展位，第一期不使用。

### D5 两层现场渲染（已与用户确认）
- **卡片层**：首次滚入可见区 → 共享渲染路径离屏渲一帧 → PNG → 磁盘缓存（key = `themeId + sha256`，版本变更自动失效）。GPU 主题走 `GpuSceneRenderer.CaptureScenePng`（F10）；legacy 主题走 `UIRenderer` 画 bitmap（F11）。
- **选中层**：市场页**唯一**一个活跃实时预览窗格（`GpuThemePreviewControl`，F9 —— 刻意无抓屏源），换选中即换参数重建场景；同一时刻全市场只有一个 GPU 呈现表面。
- **诚实性**：预览一律固定程序化背景 + `UiSceneSnapshotAdapter` 演示数据，**不抓真实桌面**；页面注明"预览为固定背景，实际效果含实时桌面折射"。
- **无 GPU 回退**：`TryCreate` 失败（老显卡/远程会话）→ legacy `UIRenderer` 渲示意帧 + "完整效果需 DirectX 11" 标注。

### D6 安装目标与来源标记
- 安装目标：`resources/themes/`（F12，平铺；程序目录写入需管理员——与插件市场同一注意事项，程序本身提权运行）。
- 来源标记：`Settings.Themes`（新增 `List<MarketThemeRecord>`，镜像 `MarketPlugins`/F5 的持久化与读写路径）。**内置主题 = 不在该列表里的主题**；市场页/卸载入口只对列表内主题可用。
- 不引入主题子目录、不改 `ThemeManager.ThemeDir` 枚举逻辑（零迁移）。

### D7 卸载守卫
卸载的目标主题若为当前 `Skin` ⇒ **先切换到默认主题**（复用 `AppActions.ApplyThemeAndLayout`）再删文件；删除失败（文件被占/IO）⇒ 记录保持、如实报错，不得出现"列表已删文件还在"或反之。

## 4. 索引 `themes` 附加段（schema 1 附加扩展）

```json
{
  "schema": 1,
  "updated_at": "...", "repo": "...", "total": 2, "plugins": [ ... 原样不动 ... ],
  "themes_total": 2,
  "themes": [
    {
      "id": "market-aurora-gpu",
      "name": "Aurora GPU", "name_en": "Aurora GPU",
      "description": "...", "description_en": "...",
      "author": "...", "version": "1.0.0",
      "file": "market-aurora-gpu.json",
      "download_url": "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/themes/market-aurora-gpu.json",
      "page_url": "https://github.com/.../themes/market-aurora-gpu",
      "sha256": "...", "size": 1234,
      "route": "gpu",                       // gpu | legacy
      "material": "liquid_glass",           // liquid_glass | cyber_punk | legacy
      "status": "ok", "verified": true
    }
  ]
}
```

客户端校验规则（安装前）：`download_url` 必须命中白名单前缀的 **themes/** 变体；`route/material` 与主题 JSON 内容**互验**（声明 gpu 但 JSON 缺 rendering 块 ⇒ 拒收）；GPU 主题必须过 `ThemeRenderingConfig.Validate()`；`sha256/size` 与原始字节一致（复用插件口径）；`id` 不得与内置 15 主题重名（镜像插件"不与内置重名"契约）。

## 5. 客户端工作分解

| 包 | 内容 | 主要文件 |
| --- | --- | --- |
| **M0 前置** | 确认 F15 未提交治理批的处置（提交或搁置），冻结执行基线 | — |
| **M1 资产类型泛化** | `PluginMarketInstallCore` 抽出"资产类型"（目标目录、白名单前缀、安装后校验回调）；插件路径行为零变化（既有测试全绿为证） | `src/Plugins/PluginMarketInstallCore*.cs` |
| **M2 主题索引与安装** | `ThemeMarket`（或 `PluginMarket` 扩展）：拉索引（同 URL）、解析 `themes` 段、白名单/互验/Validate、沙箱安装到 `resources/themes/`；复用 C-1 的 settings 沙箱纪律 | `src/Plugins/`（新文件或扩展） |
| **M3 状态与守卫** | `Settings.Themes` + `ThemeMarketState`（镜像 F5）；卸载守卫（D7） | `src/Core/Settings.cs`；`src/Plugins/` |
| **M4 市场 UI + 两层渲染** | `PluginMarketPage` 加"插件/主题"分类；主题卡片懒快照 + 磁盘缓存；单活跃实时预览窗格；无 GPU 回退（D5） | `src/UI/Settings/PluginMarketPage.cs`；新 `ThemePreviewService` |
| **M5 契约测试** | 镜像插件 6 条的主题版（拉索引/解析/sha256/白名单/不与内置重名/安装校验）+ 主题特有 2~3 条（route 与内容互验、Validate 拒收坏主题、当前主题卸载守卫） | `tests/LiteMonitor.Tests/`；`tests/fixtures/theme-repo/` |
| **M6 E2E + CI** | E2E 扩展主题流（拉索引_安装_应用_卸载）；`LITEMONITOR_THEME_REPO` 门控（镜像现有环境变量口径）；门禁阈值随新增用例上调（ci/release 逐字一致） | `.github/workflows/` |

## 6. 仓库侧工作（awesome-litemonitor-plugins）

1. `themes/` 目录 + 主题 JSON 提交流程（PR 模板：必填字段/渲染截图自愿）。
2. CI 索引生成扩展：扫描 `themes/` → 生成 `themes` 段（复用插件索引生成器；`preview_url` 字段留空位不用）。
3. 夹具：`tests/fixtures/theme-repo/`（1 个 GPU 主题 + 1 个 legacy 主题，含坏主题负例）。

## 7. 执行顺序

`M0 基线冻结` → `M1 资产泛化（插件零回归为闸）` → `M2 主题索引安装` → `M3 状态守卫` → `M4 UI 渲染` → `M5 契约测试` → `M6 E2E/CI` → 终验（双配置连续 3 次全量零失败 + 哈希冻结，C-1 纪律）。
每包单独提交；M1 的"插件零回归"是后续所有步骤的闸门。

## 8. 风险与红线

| 风险 | 缓解 |
| --- | --- |
| 动 `PluginMarketInstallCore` 破坏插件市场 | M1 以"既有插件契约/E2E 原样全绿"为合并闸；资产类型默认值 = 插件现行为 |
| 顶层 schema 升 2 打死已发布客户端 | D3 明令禁止；契约测试加"schema 必须仍为 1"断言 |
| 市场页多 GPU 表面 | D5 结构性约束：卡片=静态快照、实时=单实例；性能脚本"其他 GPU 窗口"口径不变 |
| 坏主题（参数越界/伪造 route） | 安装前 Validate + 互验拒收；越界表现评估列入 M2 验收 |
| 卸载当前主题半途失败 | D7 两阶段：先切主题成功才删文件，失败如实报错 |
| 测试沙箱纪律退化 | 延续 C-1：新文件型测试挂 cfgfile 或用 `PathOverride` 沙箱；MUST_EXECUTE 钉住主题契约类 |
| 与 F15 未提交批冲突 | M0 先处置（提交或明确搁置），本计划在其上执行 |

## 9. 开放问题（待拍板）

1. **D1 仓库**：A 复用（默认）/ B 新仓库（名字待定，如 better-litemonitor-themes）。
2. 预览快照的缓存目录与上限（建议 `bin` 外的 `%LocalAppData%\LiteMonitor++\theme-previews\`，上限 ~50MB LRU）——实现时确认。
3. 市场主题是否允许 `backdrop.mode` 为 glass（legacy 玻璃）——建议允许（与内置主题能力一致，无新增安全面）。

## 10. 验收矩阵（摘要）

- [ ] 插件市场既有 6 契约 + E2E 零变化通过
- [ ] 主题契约 8~9 条全绿（含负例：坏 JSON、伪造 route、重名、越界参数）
- [ ] E2E：拉索引_安装_应用_卸载（含当前主题卸载守卫）
- [ ] 市场页：卡片快照与包 sha256 绑定；单活跃实时预览；无 GPU 机器回退可用
- [ ] Debug/Release 连续 3 次全量零失败；门禁阈值上调且 ci/release 逐字一致
- [ ] 交付文件 SHA-256 冻结入 `docs/reviews/evidence/`
