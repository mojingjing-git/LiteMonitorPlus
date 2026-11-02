# 情景档案规格

情景档案（Scenario Profile）是 LiteMonitor++ 中「一组设置项的命名快照」：它把外观、布局、任务栏、可见性与监控项呈现一次性切换到某个目标状态，同时把可被自动改写的设置面限制在一个显式声明的白名单内。本文描述情景档案的数据模型、字段白名单、场景库版本化与存储、预检契约、应用协调事务以及运行时投影。

一条情景档案由三部分构成：

| 组成 | 类型 | 含义 |
| --- | --- | --- |
| 身份 | `Id` / `Name` | `Id` 是稳定标识符（字母数字与 `-`、`_`），`Name` 仅用于显示 |
| 版本 | `SchemaVersion` / `DefinitionRevision` | 前者是库格式版本，后者是单条档案的乐观并发修订 |
| 内容 | `ScenarioSettingsProjection` | 白名单字段的投影 |

适用对象是在本仓库上扩展情景能力、调整白名单或排查「场景应用后画面不符合预期」问题的工程师。`settings.json` 的完整字段 schema 见 `src/Core/Settings.cs`，主题文件格式与主题工作室不属于本文范围，Web 服务与插件的安装模型同样不在范围内；本文只给出情景子系统的边界、契约与不变量。

文中出现的字段名、枚举值、错误码、状态名与事件顺序属于接口约定，必须按本文写法保持稳定。

---

## 1. 情景档案的概念与定位

### 1.1 情景档案是什么

情景档案是**纯数据**：不含运行时状态、不含凭据、不含任何可执行内容。它可以被校验、被比较、被投影回 `Settings`，也可以被持久化、被重命名、被删除。

情景档案与普通设置条目的差别不在「保存了什么值」，而在「允许被自动改写哪些值」。普通设置可以逐项修改设置窗口里的每一个字段；情景档案只能携带 `ScenarioFieldCatalog` 声明的白名单字段，其余字段在应用时保持当前值。

### 1.2 与普通设置条目的关系

| 维度 | 普通设置 | 情景档案 |
| --- | --- | --- |
| 存储位置 | `settings.json`（`SettingsHelper.FilePath`） | `profiles.json`（与 `settings.json` 同目录） |
| 字段集合 | `Settings` 的全部可持久化字段 | `ScenarioFieldCatalog.Rules` 声明的白名单字段 |
| 编辑模型 | 设置窗口草稿（draft）→ 提交 | 从当前 live 设置捕获存为场景；或直接应用既有场景 |
| 写入单位 | 整份 `Settings` 的一次保存 | 单条档案的一次保存（库文件整体重写） |
| 并发控制 | 无版本号 | `DefinitionRevision` 乐观锁，冲突时返回 `revision-conflict` |
| 应用结果 | `ApplyOperationResult`（模块级） | `ScenarioApplyOutcome`（预检 + 模块结果 + 候选设置） |

两者共用同一条落盘通道与同一条运行时应用通道（见第 5 节）。区别只有两处：候选设置的来源不同，差异集合的大小不同。

- 普通设置的候选 = 草稿的深拷贝（整份设置）。
- 情景的候选 = live 设置的深拷贝 + 仅白名单字段的覆盖。

由此得到两条不变量：

- 情景应用**不得**修改白名单以外的任何字段。
- 情景应用**不**改变 `Settings` 的 schema，也**不**引入任何新的持久化字段 ⇒ `settings.json` 中不存在记录「当前生效场景」的字段。

### 1.3 模块边界与依赖方向

`src/Core/Scenarios` 是自包含的核心库，不引用 UI、主题、任务栏或渲染类型。它通过构造函数注入三个委托获取外部能力：

| 注入点 | 签名 | 职责 |
| --- | --- | --- |
| 保存 | `Func<Settings, bool>` | 把候选设置落盘，返回是否成功 |
| 模块应用 | `Func<Settings, Settings, IReadOnlyList<ModuleApplyResult>>` | 把候选应用到 live 设置，返回模块级结果 |
| 主题判定 | `Func<string, bool>?` | 判定主题文件是否存在 |

依赖方向因此是单向的：

```
ScenarioPage / SettingsForm（UI，组合根）
        │  注入委托
        ▼
ScenarioService ──▶ ScenarioApplyCoordinator ──▶ ScenarioPreflight
        │                                            ScenarioProjection
        │                                            ScenarioFieldCatalog
        ▼
ScenarioProfileStore（只触碰文件系统）
```

- `ScenarioService`（`src/Core/Scenarios/ScenarioService.cs`）是生产侧门面：库路径固定，创建、重命名、删除、应用都从这里进入。
- `ScenarioApplyCoordinator`（`src/Core/Scenarios/ScenarioApplyCoordinator.cs`）只做编排，不含任何 UI 知识。
- `ScenarioProfileStore`（`src/Core/Scenarios/ScenarioProfileStore.cs`）只负责场景库的读写与自愈。

---

## 2. 字段白名单

### 2.1 目录模型

`ScenarioFieldCatalog`（`src/Core/Scenarios/ScenarioFieldCatalog.cs`）用 `ScenarioFieldRule` 声明每个允许字段的契约：`Name`、`Type`、`Module`、可选 `Min` / `Max` 边界、可选 `NullableString`。

- `ScenarioModule`：`Appearance`、`MonitorItems`、`Taskbar`、`Placement`、`Refresh`、`Visibility`。
- `ScenarioFieldType`：`String`、`Bool`、`Int`、`Double`、`Point`、`MonitorItems`。

白名单是**正向**的：任何未在 `ScenarioFieldCatalog.Rules` 中声明的 `Settings` 字段都不会被捕获、不会被应用、也不会出现在差异中。判定入口是 `ScenarioFieldCatalog.IsAllowed(name)`；新增设置字段默认不进入情景，必须显式加入目录。

### 2.2 白名单内容

外观（`ScenarioModule.Appearance`）：

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `Skin` | String | 长度 ≤ 4096 |
| `TopMost` | Bool | — |
| `HorizontalMode` | Bool | — |
| `PanelWidth` | Int | 120 – 2000 |
| `UIScale` | Double | 0.5 – 3.0 |
| `Opacity` | Double | 0.1 – 1.0 |

位置与屏幕锚点（`ScenarioModule.Placement`）：

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `Position` | Point | 必须是 `System.Drawing.Point` |
| `ScreenDevice` | String | 长度 ≤ 4096 |
| `TaskbarMonitorDevice` | String | 长度 ≤ 4096 |

刷新与动画（`ScenarioModule.Refresh`）：

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `RefreshMs` | Int | 250 – 60000 |
| `AnimationSpeed` | Double | 0 – 1.0 |

可见性（`ScenarioModule.Visibility`）：

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `ClickThrough` | Bool | — |
| `AutoHide` | Bool | — |
| `ClampToScreen` | Bool | — |
| `HideMainForm` | Bool | — |

任务栏（`ScenarioModule.Taskbar`）：

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `ShowTaskbar` | Bool | — |
| `HorizontalFollowsTaskbar` | Bool | — |
| `TaskbarAlignLeft` | Bool | — |
| `TaskbarPresetStyle` | Int | -1 – 1 |
| `TaskbarCustomLayout` | Bool | — |
| `TaskbarFontFamily` | String | 长度 ≤ 4096 |
| `TaskbarFontSize` | Double | 6 – 48 |
| `TaskbarFontBold` | Bool | — |
| `TaskbarItemSpacing` | Int | 0 – 64 |
| `TaskbarInnerSpacing` | Int | 0 – 64 |
| `TaskbarVerticalPadding` | Int | -16 – 64 |
| `HorizontalItemSpacing` | Int | 0 – 64 |
| `HorizontalInnerSpacing` | Int | 0 – 64 |
| `HorizontalSingleLine` | Bool | — |
| `TaskbarClickThrough` | Bool | — |
| `TaskbarSingleLine` | Bool | — |
| `TaskbarHoverShowAll` | Bool | — |
| `TaskbarManualOffset` | Int | -4096 – 4096 |
| `TaskbarCustomStyle` | Bool | — |
| `TaskbarColorLabel` | String | 长度 ≤ 4096 |
| `TaskbarColorSafe` | String | 长度 ≤ 4096 |
| `TaskbarColorWarn` | String | 长度 ≤ 4096 |
| `TaskbarColorCrit` | String | 长度 ≤ 4096 |
| `TaskbarColorBg` | String | 长度 ≤ 4096 |
| `TaskbarDoubleClickAction` | Int | 0 – 2 |

监控项（`ScenarioModule.MonitorItems`）：

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `MonitorItems` | MonitorItems | 承载 `ScenarioMonitorItem` 列表，见 2.5 |

### 2.3 明确排除的字段类别

以下类别的设置不进入情景，既不被情景读取，也不被情景改写：

| 类别 | 代表字段 |
| --- | --- |
| Web 服务端点与凭据 | `WebServerEnabled`、`WebServerPort`、`WebServerPassword`、`WebServerAllowedHosts` |
| 插件与市场 | `PluginInstances` |
| 启动与更新 | `AutoStart`、`AutoCheckUpdate` |
| 采集探针策略 | `DiskProbeMode`、`ControllerProbeMode`、`UseWinPerCounters`、`IgnoreSmbTraffic` |
| GPU 渲染策略与授权 | `GpuThemeEffectsEnabled`、`GpuLiveBackgroundEnabled`、`GpuWdaCapturePolicyAccepted` |
| 托盘图标可见性 | `HideTrayIcon` |
| 语言与设置页主题 | `Language`、`SettingThemeMode` |
| 告警 | `AlertRules`、`AlertMuteAll` |

排除的理由是这些字段要么落在安全与凭据面（口令、允许主机），要么是进程级开关（自启、探针策略），要么是策略确认项（GPU 授权）——批量切换会让用户失去对进程行为的掌控。`HideTrayIcon` 尤其关键：托盘是唯一的兜底可交互入口，情景**不得**关闭它（入口存活约束见 4.3）。

### 2.4 值校验

`ScenarioFieldCatalog.ValidateValue` 对每个白名单字段执行：

- String 必须是 `string`，且长度 ≤ 4096。
- Bool 必须是 `bool`。
- Int 必须是 `int`，且落在规则的 `Min` / `Max` 区间内。
- Double 必须是有限 `double`（拒绝 `NaN` 与无穷），且落在规则的 `Min` / `Max` 区间内。
- Point 必须是 `System.Drawing.Point`。

`ScenarioProjection.Validate` 对整个投影执行同样的逐字段校验，并对监控项集合附加三条约束：每个条目必须有非空 `Key`；`Key` 在集合内不得重复；`Key` 长度 ≤ 128。校验失败即拒绝该档案，错误码为 `invalid-settings`。

### 2.5 监控项投影语义

`ScenarioMonitorItem` 的字段与 `MonitorItemConfig` 的持久化字段一一对应：`Key`、`UserLabel`、`TaskbarLabel`、`UnitPanel`、`UnitTaskbar`、`VisibleInPanel`、`VisibleInTaskbar`、`SortIndex`、`TaskbarSortIndex`。

- 捕获（`ScenarioProjection.Capture`）只读取白名单字段，并按 `SortIndex`、`Key` 排序复制监控项。
- 应用（`ScenarioProjection.ApplyTo`）按 `Key` 匹配目标设置中已有的监控项，只覆盖已存在项的字段。

由此得到三条不变量：

- 情景**不得**新增监控项。
- 情景**不得**删除监控项。
- 情景**不得**改写 `Key` 未出现在情景中的监控项。

`MonitorItemConfig` 上的运行态标签（`DynamicLabel`、`DynamicTaskbarLabel`）是 `[JsonIgnore]` 字段，不属于投影，也不参与捕获与应用。

---

## 3. 场景库与版本化

### 3.1 存储布局

| 路径 | 用途 |
| --- | --- |
| `profiles.json` | 场景库主文件，与 `settings.json` 同目录（`AppContext.BaseDirectory`） |
| `profiles.json.bak` | 上一个可用的主文件 |
| `profiles.json.<pid>.tmp` | 写入临时文件 |
| `profiles.json.corrupt-<yyyyMMddHHmmss>` | 损坏主文件的隔离副本 |

JSON 约定由 `ScenarioProfileJson.Options` 固定：缩进输出、属性名大小写不敏感、`DefaultIgnoreCondition.Never`。`ScenarioProfile.ExtensionData` 是 `[JsonExtensionData]` 容器：未知字段在 JSON 往返中原样保留，但**不**参与捕获、校验与应用。

库容量常量：`ScenarioFieldCatalog.MaxProfiles = 32`、`ScenarioFieldCatalog.MaxFileBytes = 2 MiB`。

### 3.2 两类版本号

| 版本号 | 作用域 | 语义 |
| --- | --- | --- |
| `SchemaVersion` | 库格式 | 当前值 `ScenarioFieldCatalog.CurrentSchemaVersion = 1`。`ScenarioProfile.IsSupportedVersion` 仅在 `SchemaVersion == CurrentSchemaVersion` 时为真 |
| `DefinitionRevision` | 单条档案 | 乐观并发修订。新建为 1，每次保存成功递增 1 |

两者互不相关：`SchemaVersion` 用于格式兼容判定，`DefinitionRevision` 用于并发冲突检测。`DefinitionRevision` 递增在每次成功保存时发生，与该次保存是否改变了内容无关。

### 3.3 升级与降级行为

`ScenarioProfileStore.LoadAll` 的判定顺序：

1. **主文件缺失**：仍检查备份。备份可读则返回备份内容并把备份回写为主文件；备份含未知高版本则进入只读模式并返回空集合。
2. **主文件可读且全部条目版本受支持**：正常返回。
3. **主文件含未知高版本**：**不**隔离、**不**覆盖，保留原文件，库进入只读模式（`IsReadOnly = true`），`LoadAll` 返回空集合，`LastLoadWarning` 说明原因。未知高版本不等于损坏，覆盖会摧毁只有更高版本程序才能读取的数据。
4. **主文件损坏**（解析失败、含空对象、存在重复 Id、条目数超限、字节数超限）：隔离为 `profiles.json.corrupt-<时间戳>`，随后尝试读取备份；备份可用则回退并把备份回写为主文件。

只读模式是终态保护：只读期间 `Save` 与 `Delete` 一律返回 `read-only`，原文件不被改动。

### 3.4 错误码

`ScenarioStoreResult.ErrorCode` 的取值是稳定契约：

| 错误码 | 含义 |
| --- | --- |
| `invalid-profile` | 档案对象为空 |
| `invalid-name` | 名称为空、超过 80 字符或含控制字符 |
| `invalid-id` | Id 为空、超过 64 字符，或含字母数字与 `-`、`_` 以外的字符 |
| `unsupported-schema` | `SchemaVersion` 不受支持，或库中存在未知高版本条目 |
| `invalid-settings` | 投影未通过 `ScenarioProjection.Validate` |
| `revision-conflict` | `expectedRevision` 与库中当前修订不一致；新建场景要求 `expectedRevision = 0` |
| `too-many-profiles` | 条目数超过 `MaxProfiles` |
| `too-large` | 序列化后的字节数超过 `MaxFileBytes` |
| `io-error` | 写盘失败 |
| `read-only` | 库处于只读模式 |
| `not-found` | 按 Id 删除时目标不存在 |

保存成功后条目被规范化：`Name` 去除首尾空白，`DefinitionRevision` 置为当前修订 + 1（新建为 1），`SchemaVersion` 置为当前受支持版本，`UpdatedAtUtc` 置为 UTC 当前时刻，`CreatedAtUtc` 在新建时初始化、在更新时保持原值。

### 3.5 原子写盘

`ScenarioProfileStore.AtomicWrite` 的顺序是：写同目录临时文件 → 回读并反序列化验证（条目数仍在上限内）→ 若主文件存在则复制为 `.bak` → `File.Move` 覆盖主文件 → finally 删除临时文件。

临时文件名带进程号，避免多进程共用固定临时路径。场景库的读写使用独立的静态锁，**不**与采样线程共享锁；调用方必须在 UI 线程或后台配置线程上使用。

---

## 4. 预检

### 4.1 契约

`ScenarioPreflight.Inspect(profile, current, themeExists)` 是**只读**检查：不持久化、不应用设置、不修改当前窗口。返回 `ScenarioPreflightResult`：

| 成员 | 含义 |
| --- | --- |
| `CanApply` | 当且仅当所有 issue 的 `Blocking` 为 false |
| `TargetRevision` | 档案的 `DefinitionRevision` |
| `Issues` | `ScenarioPreflightIssue` 列表，字段为 `Field`、`Code`、`Message`、`Blocking` |
| `Changes` | `ScenarioFieldChange` 列表，字段为 `Field`、`Module`、`CurrentValue`、`TargetValue` |

差异由 `ScenarioDiff.Build` 产出：逐条比对 `ScenarioFieldCatalog.Rules` 中的字段；`MonitorItems` 以按 `Key` 排序后的全字段比较，其 `CurrentValue` / `TargetValue` 记录为条目数而非集合内容。

### 4.2 问题码

| Code | Field | 阻断 | 含义 |
| --- | --- | --- | --- |
| `profile-missing` | — | 是 | 档案不存在 |
| `schema-unsupported` | — | 是 | `SchemaVersion` 不受支持 |
| `id-empty` | — | 是 | 档案缺少稳定 Id |
| `name-empty` | — | 是 | 名称为空 |
| `projection-invalid` | — | 是 | 投影未通过字段校验，校验异常信息作为 message |
| `interactive-entry` | `ClickThrough` | 是 | 应用后将不存在任何可交互入口 |
| `skin-empty` | `Skin` | 是 | 情景未指定主题 |
| `theme-missing` | `Skin` | 是 | 情景指定的主题文件不存在 |
| `monitor-item-unavailable` | `MonitorItems.<Key>` | 否 | 情景中的监控项在当前设置中不存在，应用时被跳过 |
| `monitor-item-undefined` | `MonitorItems.<Key>` | 否 | 当前设置中存在情景未定义的监控项，应用时保留当前值 |
| `diff-failed` | — | 是 | 差异计算抛出异常 |

`profile-missing` 与 `projection-invalid` 会提前返回（后者在产生其余 issue 前终止检查），其余 issue 全部产生后统一判定 `CanApply`。

阻断类与提示类的划分依据是「该问题是否会让应用结果与用户预期不符」：结构、版本、主题与入口问题会让画面进入用户无法操作的状态，因此阻断；监控项集合不对称只会让部分条目保持当前值，因此提示但放行。

### 4.3 可交互入口约束

预检在候选设置（当前设置的深拷贝 + 情景投影）上判定入口存活，判定式为：

```
(HideMainForm || ClickThrough) && (!ShowTaskbar || TaskbarClickThrough) && HideTrayIcon
```

该式成立即表示主窗口不可交互、任务栏不可交互、托盘图标隐藏，用户将失去全部常规操作入口，此时以 `interactive-entry` 阻断。

约束之所以要计入「托盘当前已隐藏」这一状态：即使情景不能直接设置 `HideTrayIcon`，托盘可能已经处于隐藏状态；若情景再把两个窗口都设为隐藏或穿透，结果同样是无入口的桌面。该判定式与 `AppActions.ApplyVisibility` 中的兜底逻辑一致 ⇒ 预检通过意味着运行时不会被迫改写托盘状态。

### 4.4 主题依赖

`skin-empty` 与 `theme-missing` 都是阻断项：主题是情景效果的载体，主题缺失会让「应用成功」与「画面生效」脱节。

主题是否存在由注入的 `themeExists` 判定，UI 侧的实现是检查 `ThemeManager.ThemeDir` 下是否存在 `<name>.json`。`themeExists` 未提供时只检查 `skin-empty`。

---

## 5. 应用协调

### 5.1 参与者与结果类型

`ScenarioApplyCoordinator.Apply(profile, current, requestRevision, operationId?)` 编排整个应用事务，返回 `ScenarioApplyOutcome`（`Preflight`、`Operation`、`Candidate`）。

`Operation` 是通用的 `ApplyOperationResult`：

| 成员 | 含义 |
| --- | --- |
| `OperationId` | 默认 `"scenario-" + Guid.NewGuid().ToString("N")` |
| `RequestRevision` | 调用方持有的修订 |
| `EffectiveRevision` | 成功应用时的修订 |
| `OverallStatus` | `Applied` / `Partial` / `Pending` / `Failed` / `Cancelled` / `Unknown` |
| `Modules` | `ModuleApplyResult` 列表 |
| `FailureCode` | 整体失败原因 |

`OverallStatus` 由模块结果推导：全部成功 ⇒ `Applied`；部分成功 ⇒ `Partial`；全部失败 ⇒ `Failed`；模块集合为空 ⇒ `Unknown`。

`ModuleApplyResult` 的字段为 `Module`、`Applied`、`SavedState`、`EffectiveState`、`FailureReason`、`ModuleRevision`、`Retryable`。`SavedState` 表达「是否已落盘」，`EffectiveState` 表达「运行态是否已生效」——两者的组合是失败语义的基础（见 5.4）。

### 5.2 事务顺序

应用是一个固定顺序的状态机，每一步的退出条件是下一步的进入条件：

1. **预检**：`CanApply = false` ⇒ 返回 `FailureCode = "preflight-blocked"`，模块结果为 `Fail("Preflight", "not-applied", "live-unchanged", …)`；不写盘、不改 live。
2. **修订校验**：`profile.DefinitionRevision != requestRevision` ⇒ 返回 `FailureCode = "stale-revision"`，失败原因形如 `stale-revision expected=<n> actual=<m>`；不写盘、不改 live。该步骤保证「读到的档案」与「被应用的档案」是同一个版本。
3. **空差异**：差异集合为空 ⇒ 返回成功结果（模块 `Scenario`，`SavedState = "current"`，`EffectiveState = "no-change"`），**不**执行保存。
4. **构造候选**：`candidate = current.DeepClone()`，随后由 `ScenarioProjection.ApplyTo` 覆盖白名单字段。候选始终从 live 派生 ⇒ 白名单以外的字段在候选中保持当前值。
5. **保存**：`save(candidate)` 返回 false ⇒ 模块结果为 `Fail("Settings", "not-saved", "live-unchanged", "settings-write-failed")`，整体 `FailureCode = "settings-write-failed"`；live 不变，磁盘不变。
6. **模块应用**：`applyModules(current, candidate)` 返回模块结果；抛出异常 ⇒ 模块结果为 `Fail("Runtime", "saved", "live-partial", <异常消息>, retryable: true)`，即已保存但运行态只部分生效。
7. **汇总**：`ApplyOperationResult.FromModules(operationId, requestRevision, modules, effectiveRevision: requestRevision)`。

### 5.3 与普通设置保存事务的合并

情景应用与普通设置提交共用同一条落盘通道与同一条运行时通道。

**落盘通道**：`SettingsHelper.TrySave(candidate)` 把候选写入 `settings.json`，采用同目录临时文件 → 回读 → 备份 → 替换的写盘序列；写盘失败**不**抛异常，而是以返回值或 `SettingsSaveOutcome` 表达。

**运行时通道**（`src/UI/SettingsForm.cs` 中的接线）：

```
before = live.CloneForDiff();
SettingsChanger.Merge(live, candidate);
AppActions.ApplySettingsSelectivelyWithResults(before, live, _mainForm, _ui, false);
```

- `SettingsChanger.Merge` 把候选的字段复制进 live。
- `SettingsChanger.ComputeDiff(before, live)` 计算实际变化并按 `SettingsModule` 分类。
- `AppActions.ApplySettingsSelectivelyWithResults` 按差异分组执行运行时副作用并逐组产出 `ModuleApplyResult`。

由此得到两条推论：

- 情景应用实际触达的运行时效果集合是「白名单 ∩ 实际差异」；白名单之外的设置即使在情景里有值也不会生效，因为它们根本不在候选里。
- 运行时差异同时受 `SettingsChanger` 的运行时黑名单约束（见 6.3）。

**草稿对齐**：应用结束后，只要整体状态为 `Applied` 或 `Partial`，`SettingsForm` 就把设置窗口的草稿重新对齐到 live（`SettingsChanger.Merge(_draftCfg, _cfg)`），并调用 `SettingsThemeManager.SyncFromSettings(_cfg)`。这保证打开中的设置窗口不会继续持有与 live 相反的旧草稿值。`Partial` 同样执行对齐，因为此时 live 已是权威值。

### 5.4 失败语义与回滚边界

情景应用**不提供全局原子回滚**，这是显式契约。

| 失败点 | 已落盘 | live 状态 | 结果标记 |
| --- | --- | --- | --- |
| 预检阻断 | 否 | 不变 | `preflight-blocked` / `live-unchanged` |
| 修订过期 | 否 | 不变 | `stale-revision` / `live-unchanged` |
| 保存失败 | 否 | 不变 | `settings-write-failed` / `live-unchanged` |
| 模块应用抛出异常 | 是 | 可能部分生效 | `live-partial`，`Retryable = true` |
| 部分模块失败 | 是 | 部分生效 | `OverallStatus = Partial` |

唯一具备完整回滚语义的情形是保存失败：此时磁盘与 live 都没有变化。保存成功之后的任何失败都**不**回滚已落盘的候选设置——理由是运行态与磁盘状态的原子性无法在 WinForms 加自绘的运行时边界上保证，声称回滚只会给出与画面不符的报告。

主题是唯一具备独立恢复路径的模块：`AppActions.ApplyThemeAndLayoutWithResult` 在主题已提交后失败时，会按来源身份与内容摘要（`SourcePath` / `SourceHash`）尝试恢复上一主题，而不是仅比较主题显示名；恢复成功记为失败但标注已恢复，恢复失败记为 `Partial` 且 `FailureCode = "theme-runtime-partial"`。恢复失败时其后的菜单与任务栏**不得**按「全部失败」处理，否则调用方会回滚 `Skin` 而画面仍是新主题。

失败模块以 `"模块: 原因"` 的形式回传给界面；只有整体状态为 `Applied` 时界面才视为成功。

### 5.5 门面能力

`ScenarioService` 提供生产侧能力：

| 方法 | 语义 |
| --- | --- |
| `List()` | 读取全部档案 |
| `CreateFromCurrent(name, current)` | 从当前 live 设置捕获白名单字段，以 `expectedRevision = 0` 保存 |
| `Rename(profile, newName)` | 以 `profile.DefinitionRevision` 为预期修订保存副本 |
| `Delete(profile)` | 按 Id 与修订删除 |
| `Apply(profile, current)` | 以 `profile.DefinitionRevision` 为请求修订发起应用 |

`ScenarioService` 的默认模块应用实现只把候选合并进 live 并返回单个成功模块，适用于无法注入 UI 的宿主；桌面端由 `SettingsForm` 注入完整的模块应用实现。

---

## 6. 运行时投影

### 6.1 落点

一次成功的应用产生三处变化：

1. **live 设置**：`Settings` 实例的白名单字段被场景值覆盖；监控项按 `Key` 匹配后逐字段覆盖。
2. **磁盘设置**：`settings.json` 保存候选设置。候选从 live 克隆，因此白名单以外的字段以当前值原样重新落盘。
3. **运行时模块**：由 `SettingsChanger.ComputeDiff` 的分类驱动 `AppActions.ApplySettingsSelectivelyWithResults` 的分组。

场景库本身（`profiles.json`）**不**因应用而改写：应用只读取场景定义，写入只发生在 `ScenarioService` 的创建、重命名、删除路径上。

### 6.2 运行时分组与差异分类

`SettingsChanger.Classify` 把变化字段归入 `SettingsModule`：`RefreshMs`、`MonitorItems`、`HorizontalFollowsTaskbar` 归入 `Sampling`；`Skin` 归入 `Appearance`；以 `Taskbar` 开头的字段归入 `Taskbar`；其余归入 `Other`。

`AppActions.ApplySettingsSelectivelyWithResults` 依此形成三组：

| 组 | 触发条件 | 执行内容 |
| --- | --- | --- |
| 界面重建组 | 存在非 Web、非 Plugins 的差异 | `ApplyLanguage` → `ApplyWindowAttributes` → `ApplyThemeAndLayout` → `ApplyMonitorLayout` → `ApplyTaskbarStyle` → `ApplyVisibility`，顺序固定 |
| Web 组 | 存在 `SettingsModule.WebServer` 差异 | 重启监听并消费 `WebServerApplyResult` |
| 插件组 | 存在 `SettingsModule.Plugins` 差异 | `PluginManager.Instance.Reload` |

对情景应用而言，白名单排除了全部 Web 与插件字段 ⇒ 只有界面重建组可能被触发。界面重建组是**整组**重应用的：任一环节抛异常时，该组涉及的所有模块全部记为失败，**绝不**把未执行的模块报成成功。

界面重建组内的覆盖范围与白名单字段的对应关系：主题、缩放、面板宽度、横竖模式由 `ApplyThemeAndLayout` 承担；监控项标签与排序由 `ApplyMonitorLayout` 承担；任务栏样式由 `ApplyTaskbarStyle` 承担；置顶、鼠标穿透、自动隐藏、透明度由 `ApplyWindowAttributes` 承担；主窗体、托盘与任务栏的显示状态由 `ApplyVisibility` 承担。`AutoStart` 不在白名单内，情景应用路径也不传递自启参数 ⇒ 自启开关**不**可能被情景改变。

界面重建组中的 `ApplyMonitorLayout` 会按实际检测到的显卡数同步 GPU 监控项（`GpuItemSync.Ensure`）。这是运行时模块的职责，与情景白名单无关：`MonitoredGpuCount` 不在白名单内，场景无法改变它；但模块重建后的 live 监控项集合可能与已落盘的候选存在差异。

### 6.3 不落盘与不覆盖的部分

- **白名单以外的字段**：在候选中保持当前值，属于原样回写，`settings.json` 中这些字段的值不因应用而变化。
- **`ScreenDevice`**：它在白名单内，会随场景值进入候选并写入 `settings.json`；但 `SettingsChanger` 的运行时黑名单包含 `ScreenDevice`，`Merge` 不会用它覆盖 live，`ComputeDiff` 也不把它计为变化。因此场景的目标屏幕只作为落盘记录存在，运行态的 `ScreenDevice` 保持由主窗体移动逻辑维护的值，场景**不**驱动窗口迁移。
- **运行态标签**：`MonitorItemConfig` 上的 `DynamicLabel`、`DynamicTaskbarLabel` 是 `[JsonIgnore]` 运行态字段，不在投影内，也不随候选回写。
- **生效来源**：系统**不**记录「当前生效的是哪个场景」。`settings.json` 中没有对应字段，应用后无法从设置本身区分「这套值来自场景」与「这套值来自逐项修改」。
- **监控项集合的增删**：场景中不存在的监控项不被创建；live 中存在但场景未定义的监控项不被改写。

### 6.4 用户入口

唯一入口是设置窗口的 `Scenarios` 导航页（`SettingsForm.AddNav("Scenarios", …)`）。`ScenarioPage`（`src/UI/Settings/ScenarioPage.cs`）提供列表与新建、重命名、删除、差异预览、应用六个动作，列表双击等价于应用。

页面自身**不**改 live 设置：预览只调用 `ScenarioPreflight.Inspect` 并展示阻断码与字段级差异（`字段: 当前 → 目标`），实际应用通过 `ApplyHandler` 委托给 `SettingsForm`。

---

## 7. 已知限制

- 系统**不**持久化「当前生效场景」，场景之间也**不**存在栈式撤销；回到某个既有布局只能通过再次应用对应场景。
- `ScreenDevice` 的落盘值与运行态值可以分叉（见 6.3），场景**不**能驱动窗口跨屏迁移。
- 托盘图标可见性不在白名单内，场景无法隐藏托盘；但预检会在应用后不存在任何可交互入口时阻断。
- 场景库进入只读模式时 `LoadAll` 返回空集合，界面无法区分「库为空」与「库不可写」，只能通过状态栏的 `LastLoadWarning` 区分。
- 场景页的界面文案是英文常量；导航标题取自 `LanguageManager.T("Menu.Scenarios")`，语言资源未定义该键时按 `LanguageManager` 的缺键规则回退为键后缀 `Scenarios`。
