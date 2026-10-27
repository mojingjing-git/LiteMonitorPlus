# S01–S04 场景字段、版本化库、预检与应用核心

日期：2026-10-27  
状态：S01/S02/S03/S04 核心完成；S05–S06 与生产接线未完成。

## 交付

- `src/Core/Scenarios/ScenarioFieldCatalog.cs`
  - 正向白名单，字段带类型、边界与模块归属；未声明字段默认不进入场景。
  - 明确排除 Web、插件/凭据、启动/更新、探测策略、ETW/IP、硬件来源、GPU 捕获授权等。
  - 安全可见性：不包含 `HideTrayIcon`，避免场景把唯一恢复入口隐藏。
- `src/Core/Scenarios/ScenarioProjection.cs`
  - `Capture(Settings)` 只读取允许字段，保留 `UnitPanel/UnitTaskbar` 的 null 与空串差异。
  - `ApplyTo(Settings, projection)` 只应用允许字段；不添加/删除监控项，不触碰排除字段。
- `src/Core/Scenarios/ScenarioProfile.cs`
  - SchemaVersion、稳定 Id、名称、DefinitionRevision、创建/更新时间、受限投影。
  - `[JsonExtensionData]` 保留未知字段但不应用；未知高版本 `IsSupportedVersion=false`。
- `src/Core/Scenarios/ScenarioProfileStore.cs`
  - 固定 `profiles.json`；首次无文件为空库。
  - 上限 32 个场景 / 2 MiB；同目录临时文件、回读验证、`profiles.json.bak`、替换。
  - 新建 `expectedRevision=0`；更新必须匹配当前修订，冲突不写盘。
  - 损坏主库隔离为 `.corrupt-*`，回退到上一次成功提交的备份；超限文件也隔离而不加载。
  - 文件读写只持有配置锁，不依赖采样线程。
- `src/Core/Scenarios/ScenarioPreflight.cs`
  - `ScenarioPreflight.Inspect(profile, current, themeExists)`：版本/Id/名称/投影合法性、主题存在性、监控项接缝。
  - 阻塞问题（unsupported schema、missing theme、invalid projection）与警告（当前没有目标监控项、场景未定义监控项）分开。
  - `ScenarioDiff.Build` 只比较允许字段；排除字段即使当前值不同也不会进入差异。
  - 差异包含字段名、模块、当前值、目标值；不读写任何排除字段。
- `src/Core/Scenarios/ScenarioApplyCoordinator.cs`
  - 固定修订校验 → 预检 → 只修改允许字段的 `DeepClone` 候选 → 注入保存 → 注入模块应用 → `ApplyOperationResult`。
  - 保存失败不调用模块；部分模块失败显式返回 `Partial`；无差异不写盘，返回 `Applied/no-change`。
  - 不声称原子回滚；失败时保留当前 live 设置，候选/已保存事实由结果表达。

## 验证

- 构建：`s01-s04-build.log`，`LiteMonitor.csproj` Release，exit 0，0 warnings / 0 errors。
- 测试：`s01-s04-tests.log`，Scenario 定向 19/19 通过。
  - 允许字段捕获/应用、排除字段不变、null/空单位、未知监控项不添加、边界校验。
  - 创建/更新/修订冲突/删除、32 上限、损坏回退、超 2 MiB 拒绝、未知字段往返。
  - 预检：主题缺失/不支持版本阻塞；允许字段差异；排除字段不进入差异；监控项缺失仅警告。
  - 应用：保存后模块应用、保存失败不调用模块、部分失败 `Partial`、陈旧修订拒绝、无差异不写盘。
- 测试工程构建：`s01-s04-testbuild.log`，exit 0，44 warnings / 0 errors（既有测试分析器警告，无新增错误）。

## 未完成边界

- S04 仅完成 Core 编排；尚未接入 Settings 的“请求场景 Id/修订”、MainForm 启动流程和 E03 模块应用器组合根。
- S05 管理界面/托盘、S06 故障与拓扑验收未开始。
- 真实 DPI/多屏/设备离线验证留 S06/G4。

## S04 生产门面补充（2026-10-27）

- 新增 `src/Core/Scenarios/ScenarioService.cs`：
  - 默认固定 `profiles.json`、`SettingsHelper.TrySave` 为保存口；
  - `CreateFromCurrent`、`Rename`、`Delete`、`Apply`；
  - 运行时模块应用可注入；默认仅合并候选到 live，供无 UI 宿主使用。
- 新增 `tests/LiteMonitor.Tests/ScenarioServiceTests.cs` 5 个用例（创建/重命名/应用注入/保存失败/删除）。
- 场景定向 24/24 通过；`LiteMonitor.csproj` Release 0 warnings / 0 errors。
- 证据：`s04-service-tests.log`、`s04-service-build.log`。
- 仍未完成：设置页/托盘 UI（S05）、Settings 请求场景 Id/修订与 MainForm 启动接线。

## S05 场景管理页（2026-10-27）

- 新增 `src/UI/Settings/ScenarioPage.cs`：
  - 场景列表（名称/修订/更新时间）、新建、重命名、删除、差异预览、应用、刷新；
  - 新建从当前设置捕获允许字段；应用动作由 `SettingsForm` 注入，页面不直接改 live；
  - 删除二次确认；预览只展示允许字段差异。
- `SettingsForm.InitPages` 增加 `Scenarios` 导航，并注入 `ApplyScenarioFromPage`：
  - 使用 `ScenarioService` + `SettingsHelper.TrySave`；
  - 应用成功后把 live 设置回写 draft 并同步主题状态；
  - 保存/模块失败返回具体模块原因。
- 9 个 `resources/lang/*.json` 增加 `Menu.Scenarios`。
- 场景定向 24/24 通过；`LiteMonitor.csproj` Release 0 warnings / 0 errors。
- 证据：`s05-scenario-page-tests.log`、`s05-scenario-page-build.log`。
- 仍未完成：托盘入口、Settings 请求场景 Id/修订、MainForm 启动场景应用、S06。
