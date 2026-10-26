# 主题工作室实施记录（M1 批次）

- 日期：2026-10-26。
- 对应计划：[主题工作室重做计划](../../plans/2026-10-26-theme-studio-rebuild-plan.md)。
- 能力合同：[TE00-capability-contract.md](TE00-capability-contract.md)。

## 已完成

### TE00 能力合同与基线

- 逐字段矩阵（GPU 参数按材质分列、floatingToolbar 前景 6 字体 + 12 颜色映射、fallbackStyle 六槽、共享范围修正）。
- 视图表修正：**横向面板在 GPU 主题下走独立 GPU 场景路线**（`UiSceneSnapshotAdapter.BuildHorizontal`），任务栏恒为常规绘制。
- GPU 启动保护归属：预览复用 `GpuStartupGuard.Shared` 会双向污染生产保守模式。
- 测试基线：全量 2472 通过 / 0 失败 / 7 跳过（另有 1 例 `I2HostSupervisionTests` 在长套件满载下偶发，单跑稳定通过，与本计划无关）。
- 布局探针 `tools/ThemeStudioLayoutProbe/`：原生标题栏三栏原型，175% 实测两档尺寸正确；
  记录事实：PMv2 下 `AutoScaleMode.Dpi` 不重缩放显式 ClientSize，必须单一 `Dip()` 入口。

### TE01 文档模型（`ThemeDocumentSession` / `ThemeDocumentHistory` / `ThemeDocumentStore`）

- 补丁式保存：源字节 + 覆盖层；未编辑保存逐字节不变；编辑只写被触碰的键；
  未知扩展键原样保留；未触碰键大小写不变；恢复默认删除键（缺省不物化）。
- 事务历史：合并窗口 800ms、撤销/重做、100 事务 + 8 MiB 双上限（打开事务计入）、保存不清理历史。
- 冲突：外部摘要检测；覆盖/另存副本/取消三路；恢复副本必须另存。
- 恢复草稿：`%LocalAppData%\LiteMonitor++\theme-studio\drafts`，10 份 / 8 MiB 上限，摘要不一致打开恢复副本。
- `ThemeFileService.WriteThemeJson` 载荷入口复用 SafeWrite 核心。

### TE02 视觉壳（`ThemeStudioForm`）

- 原生窗口：系统标题栏/边框/最小化/最大化/关闭，无自绘非客户区。
- 三栏：资料库（新建/复制/删除、GPU/经典副标题、折叠按钮）、画布、检查器。
- 文档工具栏：撤销/重做/保存/另存为/应用；快捷键 Ctrl+S / Ctrl+Shift+S / Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z。
- 状态区：保存状态 + **实际预览路线**（GPU 同源 / GPU 不可用原因 / 经典同源）。
- 关闭与切换文档前保存确认；空闲 2 秒持久化恢复草稿。

### TE03 属性目录与检查器

- `ThemePropertyCatalog`：经典 45 字段（配色 15 / 文字 7 / 布局 15 / 阈值 8），
  路径、范围、模型缺省、JSON 映射、作用域、失效级别冻结；可空配色 null 语义保持。
- `PropertyInspector`：分组渲染、显式覆盖显示"恢复默认"、字体失效重建运行期字体并同步刷新预览。

### M1 接线与预览（TE04 首批）

- `ThemeStudioLauncher`：菜单默认打开新工作室；Shift+点击保留旧编辑器（过渡期对照）。
- 应用网关：`IThemeApplyGateway` → `AppSettings.Save` + `AppActions.ApplyThemeAndLayout`，**无反射**。
- 预览：画布接入 `ThemePreviewControl`（经典 + GPU 同源），150ms 防抖；字体编辑同步刷新。
- **预览 GPU 启动保护隔离**：`GpuPreviewControl.PreviewGuard` 使用独立状态目录
  `%LocalAppData%\LiteMonitor++\gpu-startup-preview`，不读写生产崩溃标记（TE00 第 5 节硬前置已解除）。

## 测试

- 新增 28 例：会话 19 + 属性目录 6 + 工作室烟雾 3（含 guard 隔离断言）。
- 相关套件 73 例、全量 2507 例（1 例无关偶发）验证。

## 未完成（按计划批次）

- TE04 完整版：横向 / 任务栏预览（复用 `BuildHorizontal` / `TaskbarRenderer`）、点选定位、GPU 会话复用
  （当前每次 SetTheme 重建 GPU 控件，已用防抖缓解）、预览失败态细化。
- TE03 GPU 视图：主效果 / 兼容外观属性页（TE00 矩阵已冻结；共享项标注、fallbackStyle 六槽编辑）。
- TE05：明暗工作区、混合 DPI 实机矩阵、紧凑布局阈值冻结、辅助功能。
- TE06：应用结果细分（已保存/兼容生效/失败）、同名重载验证、设置写入失败路径。
- TE07：导出 JSON / 效果图 / 投稿资料。
- TE08/TE09：性能与资源验收、入口切换收口、旧编辑器退场。

## 已知限制

- 预览与生产的关键路径一致，但横向/任务栏尚未接入工作室画布。
- 工作区仅浅色；暗色接 E05 的 `SettingsThemePalette`。
- 检查器分栏宽度固定（220/300 DIP），调整与紧凑阈值留 TE05。
