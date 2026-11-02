# 总体架构

LiteMonitor++ 是面向 Windows 的桌面硬件监控器，技术栈为 .NET 8 / C# / WinForms + 自绘渲染，硬件采样底层依赖 LibreHardwareMonitorLib，项目派生自开源项目 Diorser/LiteMonitor（MIT 许可）。

本文描述当前生效的架构：程序集与进程拓扑、主程序内部逻辑分层与依赖方向、跨进程数据流与契约定义、配置与状态的持久化位置、渲染路线与背景捕获链的划分。适用对象是在本仓库上开发、评审与排障的工程师；界面控件的具体绘制细节、单个传感器的采集算法、打包与发布流程不属于本文范围。

---

## 1. 总体拓扑

### 1.1 程序集清单

| 程序集 | 目标框架 / 输出 | 职责 | 对外依赖 |
| --- | --- | --- | --- |
| `LiteMonitor++`（仓库根 `LiteMonitor.csproj`） | `net8.0-windows` / WinExe | 监控面板、任务栏、设置、主题工作室、Web 服务、进程内采集、插件与诊断的宿主 | `LiteMonitor.Contracts`（项目引用）；`LiteMonitor.HardwareHost`（项目引用，`ReferenceOutputAssembly=false`） |
| `LiteMonitor.Contracts`（`src/Contracts/`） | `net8.0` / 类库 | 跨进程共享的接口与数据传输对象 | 无任何 NuGet 包依赖 |
| `LiteMonitor.HardwareHost`（`src/HardwareHost/`） | `net8.0-windows` / WinExe，无 WinForms | 独立的硬件采样子进程 | `LiteMonitor.Contracts`、LibreHardwareMonitorLib |
| `LiteMonitor++.Updater`（`LiteMonitor.Updater/`） | `net8.0-windows` / WinExe，自包含 + 单文件，`win-x64` | 独立更新器，负责在主程序不运行的窗口内替换程序文件 | 无项目引用 |
| `LiteMonitor.Cli`（`src/Cli/`） | `net8.0-windows` / 控制台 Exe | 主题市场包的命令行校验工具 | 无项目引用；以 `Compile Include` 共享源文件 |

### 1.2 生效的依赖约束

- **`LiteMonitor.Contracts` 不得引入任何 NuGet 包。** 它是主程序与采样子进程共同加载的类型来源，第三方包会把版本协商问题带进 IPC 边界。
- **`LiteMonitor.HardwareHost` 不得引用主程序。** 采样进程承载 LibreHardwareMonitorLib 的驱动访问，引用主程序会把 WinForms 与界面依赖拖入采样路径。
- **主程序对 `LiteMonitor.HardwareHost` 的项目引用设置 `ReferenceOutputAssembly=false`。** 主程序不在编译期链接 Host 程序集，两者只经命名管道交换契约消息；构建顺序与产物落位由 `LiteMonitor.csproj` 的 `CopyHardwareHostToOutput` 目标负责。
- **`LiteMonitor.Cli` 不得引用主程序。** 主程序是 WinExe 且拖入 System.Drawing；CLI 必须在无 WinForms 依赖的环境中运行，因此它启用 `InvariantGlobalization=true`，并用 `THEME_MARKET_CLI` 编译符号排除依赖 System.Drawing 的类型解析分支。
- **主题校验规则必须保持单一来源。** CLI 通过 `Compile Include` 引用 `src/Plugins/ThemeMarketValidator.cs` 与 `src/Rendering/Configuration/ThemeRenderingConfig.cs`，复制一份校验逻辑属于违规。

### 1.3 进程关系

- 主程序以可执行文件路径派生的 `Global\` 命名互斥量保证单实例。
- `LiteMonitor.HardwareHost` 是按需拉起的子进程，仅在 Host 采样模式下存在，生命周期由主程序侧的监督器管理（见 §4.6）。
- `LiteMonitor++.Updater` 是自包含单文件可执行程序，随主程序一同发布；主程序的更新检查流程在需要替换文件时调用它。
- `LiteMonitor.Cli` 不随主程序启动，由人工或外部流水线调用。

### 1.4 程序集与进程拓扑图

```
        ┌──────────────────────────────────────┐
        │  LiteMonitor++.Updater               │  自包含单文件，独立进程
        │  （无项目引用）                        │
        └──────────────────────────────────────┘

   ┌────────────────────────────────────────────────────────────┐
   │  LiteMonitor++   （主程序，WinExe）                          │
   │  src/UI   src/Core   src/System   src/Rendering            │
   │  src/Plugins   src/Diagnostics   src/ThemeEditor            │
   └───────────┬──────────────────────────┬──────────────────────┘
               │ ProjectReference          │ ProjectReference
               │ (ReferenceOutputAssembly=false)
               ▼                           ▼
   ┌───────────────────────┐   ┌────────────────────────────────┐
   │ LiteMonitor.Contracts │◀──│ LiteMonitor.HardwareHost       │
   │ net8.0 / 零包依赖      │   │ net8.0-windows / WinExe         │
   └───────────────────────┘   └────────────────────────────────┘
              ▲  契约类型                        │  命名管道（IPC）
              │                                 ▼
              └────────── 主程序的 IHardwareClient 实现 ◀────┘

   ┌───────────────────────────┐
   │ LiteMonitor.Cli           │  控制台 Exe
   │ Compile Include:          │──▶ src/Plugins/ThemeMarketValidator.cs
   │   ThemeMarketValidator.cs │    src/Rendering/Configuration/ThemeRenderingConfig.cs
   │   ThemeRenderingConfig.cs │
   └───────────────────────────┘
```

---

## 2. 主程序内部逻辑分层

### 2.1 分层职责

| 层 | 目录 | 职责 | 代表类型 |
| --- | --- | --- | --- |
| 呈现层 | `src/UI` | WinForms 窗口、自绘渲染、菜单与设置页、面板与任务栏的绘制和交互 | `UIController`、`MainForm_Transparent`、`UIRenderer`、`HorizontalRenderer`、`TaskbarRenderer`、`UILayout`、`AppearanceRuntime`、`LiveGlassController`、`MenuManager`、`Decor/*` |
| 渲染层 | `src/Rendering` | Direct3D 11 / Direct2D 的 GPU 渲染路线：设备与适配器管理、场景与材质构建、呈现调度、背景帧获取、失败降级 | `GpuSceneRenderer`、`GpuPresentationHostController`、`D3D11DeviceService`、`GpuAdapterSelector`、`GpuResourceLedger`、`DesktopDuplicationFrameSource`、`GpuFailureFallbackScheduler`、`CapturePolicyCleanupPolicy` |
| 领域层 | `src/Core` | 设置模型与持久化、指标 Key 与单位口径、GPU 分组与标题解析、主题解析与生效、历史记录、告警与趋势、动作编排 | `Settings`、`SettingsHelper`、`SettingsProjection`、`MetricItem`、`MetricUtils`、`MetricLabelResolver`、`GpuRegistry`、`ThemeManager`、`ThemeColorParser`、`TrafficLogger`、`HardwareHistoryLogger`、`LanguageManager`、`Actions/*`、`Alerts/*`、`Trends/*`、`Scenarios/*` |
| 采集层 | `src/System` | 硬件采样与快照发布、采样需求登记、跨进程管道客户端与监督器、网络流量采集、Web 服务、背景捕获、InfoService | `HardwareMonitor`、`HardwareClientAdapter`、`SnapshotPublisher`、`DemandRegistry`、`HardwareSamplingCoordinator`、`PipeHardwareClient`、`HardwareHostSupervisor`、`EtwNetworkTrafficEventSource`、`LiteWebServer` |
| 插件层 | `src/Plugins` | 声明式 JSON 插件的加载与执行、插件市场与主题市场的安装、发布与校验 | `PluginManager`、`PluginExecutor`、`PluginProcessor`、`PluginMarket`、`ThemeMarket`、`ThemeMarketValidator` |
| 诊断层 | `src/Diagnostics` | 进程内有界诊断事件、状态快照、敏感信息脱敏、诊断包导出 | `DiagnosticHub`、`DiagnosticEventStore`、`DiagnosticSnapshot`、`DiagnosticRedaction`、`DiagnosticPackageService` |
| 主题工作室 | `src/ThemeEditor` | 主题的可视化编辑、属性目录、实时预览、撤销历史、导出与提交市场 | `ThemeStudioForm`、`ThemeStudioLauncher`、`ThemeEditorForm`、`ThemePropertyCatalog`、`ThemeDocumentStore`、`ThemePreviewControl`、`GpuThemePreviewControl`、`ThemeExportService` |

### 2.2 依赖方向图

箭头指向被依赖方。

```
        ┌──────────────────────────────────────────┐
        │  呈现层  src/UI   （组合根 UIController）   │
        └───┬───────────────┬───────────────┬──────┘
            │               │               │
            ▼               ▼               ▼
   ┌────────────────┐ ┌─────────────┐ ┌────────────────┐
   │ 领域层          │ │ 渲染层       │ │ 插件层          │
   │ src/Core       │ │ src/Rendering│ │ src/Plugins    │
   └───────┬────────┘ └──────┬──────┘ └───────┬────────┘
           │                 │                │
           │                 │                ▼
           │                 │        ┌────────────────┐
           │                 └───────▶│ 采集层          │
           │                          │ src/System     │
           │                          └───────┬────────┘
           │                                  │
           ▼                                  ▼
   ┌───────────────────────────────────────────────────┐
   │  诊断层 src/Diagnostics   （读取运行时状态）        │
   └───────────────────────────────────────────────────┘

   ┌───────────────────────────────────────────────────┐
   │  契约层  LiteMonitor.Contracts   （零包依赖）        │
   └───────────────────────────────────────────────────┘
                              ▲
                              │ 命名管道
                   ┌──────────┴───────────┐
                   │ LiteMonitor.HardwareHost │
                   └──────────────────────┘

   ┌───────────────────────────────────────────────────┐
   │  主题工作室  src/ThemeEditor                      │
   │  依赖 领域层 + 渲染层（预览）+ 背景捕获链（预览）    │
   └───────────────────────────────────────────────────┘
```

### 2.3 允许的横向引用

下列引用存在于当前代码中，属于分层图之外的既有耦合：

- **领域层 ↔ 插件层。** `Settings` 与 `SettingsHelper` 引用插件命名空间（插件相关配置随设置一起持久化），`PluginManager` 等插件类型引用领域层的指标与主题工具。
- **领域层 → 渲染配置。** `ThemeManager` 与 `ThemeColorParser` 使用 `LiteMonitor.Rendering.Configuration` 中的类型，作为主题描述与渲染参数之间的交接对象。
- **领域层 → 采集层。** `MetricUtils` 引用 `src/System` 与 `src/System/InfoService` 的少量类型。
- **采集层 → Web 服务。** `src/System/HardwareMonitor.cs` 引用 `LiteMonitor.src.WebServer`。
- **诊断层 → 采集层 / Web 服务 / 设置页。** 诊断 Provider 需要读取运行时状态与当前设置。

---

## 3. 契约层

`LiteMonitor.Contracts` 是唯一被主程序与采样子进程共同引用的程序集。以下类型构成跨进程约定的全部表面。

### 3.1 `IHardwareClient`

`namespace LiteMonitor.Contracts`，实现 `IDisposable`。它把消费方需要的能力分为两面：

**快照面（读）**

| 成员 | 语义 |
| --- | --- |
| `HardwareSnapshot GetLatestSnapshot()` | 同步读取已发布的完整快照。读取不触发采样，不推进 `BatchId`，不改变时效字段。 |
| `Task<HardwareCatalog> GetCatalogAsync(CancellationToken)` | 读取硬件目录（设备结构）。 |
| `HardwareHealth GetHealth()` | 读取采集侧的运行健康状态。 |
| `string? GetNetworkIP()` | 读取本机网络 IP。 |

**命令面（写）**

| 成员 | 语义 |
| --- | --- |
| `Task<SamplingApplyResult> ApplyConfigAsync(SamplingConfig, CancellationToken)` | 下发采样配置。 |
| `Task<bool> RequestSamplingAsync(CancellationToken)` | 请求一次采样；到期才采，同周期内的并发请求共享同一个协调器。 |
| `Task<HardwareOperationResult> RequestFullRescanAsync(CancellationToken)` | 请求全量重扫，返回带操作 ID 的有界操作记录。 |
| `void SubmitDemand(string consumerId, SamplingDemand)` | 按消费者身份登记采样需求。 |
| `bool ReleaseDemand(string consumerId)` | 释放某个消费者的需求。 |

**兼容桥成员**：`ApplyConfig(SamplingConfig)`、`RequestRescanAsync(CancellationToken)`、`SubmitDemand(SamplingDemand)`。默认实现抛 `NotSupportedException` 或桥接旧的单桶需求模型。

### 3.2 数据类型

```
MetricSample(string Key, string? StableDeviceId, float? Value, string? Unit,
             SampleQuality Quality, string? MissingReason, double SampleAgeMs)

HardwareSnapshot(long BatchId, long CatalogVersion, int ConfigRevision,
                 IReadOnlyList<MetricSample> Samples, TimeSpan SamplingDuration,
                 double TimestampMonoMs, int SessionEpoch = 0)

HardwareCatalog(long Version, IReadOnlyList<HardwareCatalogEntry> Entries)

HardwareCatalogEntry(string StableId, string RuntimeId, string? ParentStableId,
                     string DeviceType, string? SensorType, bool Available,
                     string Name, float? Value = null, float? Max = null)
```

`SampleQuality` 的取值与含义：

| 值 | 含义 |
| --- | --- |
| `Good` | 采到了有效读数。真实读数为 0 时同样是 `Good` + `Value = 0`。 |
| `Stale` | 读数已超出时效窗口；时效字段不被重置。 |
| `Missing` | 该次采样没有读到值，`MissingReason` 给出原因。 |
| `Error` | 采集过程中发生错误。 |

`HardwareState` 的取值：`Stopped`、`Starting`、`Ready`、`Degraded`、`Unresponsive`、`Stopping`。

`HardwareHealth(HardwareState State, string? CurrentOperation, long ProgressSeq, string? DiagnosticReason)`。

`HardwareOperationState` 的取值：`Queued`、`Running`、`Completed`、`Failed`、`Cancelled`、`Rejected`。

`HardwareOperationResult(bool Accepted, string OperationId, HardwareOperationState State, string? FailureReason = null)`，提供 `Ok()` 与 `Reject()` 两个工厂方法。

### 3.3 采样需求

```
SamplingDemand(bool Ui, bool Taskbar, bool Web, bool History, bool SensorWindow)

[Flags] SamplingChannels { None = 0, Ui = 1, Taskbar = 2, Web = 4,
                            History = 8, SensorWindow = 16 }

ConsumerSamplingDemand(string ConsumerId, bool Active, int MaxIntervalMs,
                        bool Degradable, string Reason,
                        SamplingChannels Channels = SamplingChannels.None)
```

`ConsumerSamplingDemand` 提供 `ToLegacyDemand()` 与 `FromLegacy(SamplingDemand)`，用于在显式需求模型与旧的五布尔模型之间转换。

```
SamplingConfig(int Revision, int RefreshMs, ControllerProbePolicyData,
               DiskProbePolicyData, IReadOnlyList<DeviceSelection> Devices)

SamplingApplyResult(bool Applied, int? Revision, string? FailureReason)

DeviceSelection(string Kind, string? StableId, bool Enabled)

ControllerProbePolicyData(int ModeRaw, IReadOnlyList<string> ExcludedIds)

DiskProbePolicyData(int ModeRaw)
```

### 3.4 IPC 协议常量与消息

`IpcProtocol` 定义：

| 常量 | 值 |
| --- | --- |
| `MajorVersion` | 1 |
| `MinorVersion` | 1 |
| `MaxMessageBytes` | 1 MiB |
| `LengthPrefixBytes` | 4 |
| `DefaultCommandTimeout` | 5 秒 |

消息类型（`KnownTypes` 集合）：`hello`、`welcome`、`snapshot`、`heartbeat`、`command`、`response`、`goodbye`。**收到集合之外的消息类型即为协议违规，连接必须断开。**

命令操作：v1 为 `apply-config`、`submit-demand`、`rescan`、`get-catalog`、`shutdown`；v2 增量为 `sampling-due`、`rescan-full-v2`、`submit-demand-v2`、`release-demand`、`operation-status`。

消息信封与载荷：

```
IpcHeader(int Major, int Minor, string Type, string SessionId, long RequestId, long Sequence)
IpcEnvelope(IpcHeader Header, object? Payload)

IpcHelloMessage(HostProcessId, HostStartTickMs, HostVersion)
IpcWelcomeMessage(Accepted, Reason?)
IpcSnapshotMessage(Snapshot, PublishTickMs)
IpcHeartbeatMessage(State, CurrentOperation?, ProgressSeq, DiagnosticReason?, TickMs)
IpcCommandMessage(Operation, ArgumentJson?)
IpcResponseMessage(Ok, Error?, ResultJson?)
IpcGoodbyeMessage(Reason)
```

---

## 4. 跨进程数据流

### 4.1 两种采样模式

`IHardwareClient` 有两个实现，由组合根按模式二选一：

| 模式 | 实现 | 数据来源 |
| --- | --- | --- |
| 进程内模式 | `HardwareClientAdapter`（`src/System/HardwareServices/`） | 组合根在主进程内创建的 `HardwareMonitor` |
| Host 模式 | `PipeHardwareClient`（`src/System/Pipe/`） | 独立进程 `LiteMonitor.HardwareHost` |

模式判定由 `HardwareHostMode.IsRequested()` 给出，入口只有两个：命令行 `--hardware-host`，或环境变量 `LITEMONITOR_HARDWARE_HOST=1`（或 `true`）。判定结果在首次读取后固定；`settings.json` 不保存该字段，模式属于启动期决策。

### 4.2 应用级组合根与快照消费边界

`ApplicationHardwareRuntime`（`src/System/HardwareServices/`）是硬件源的唯一组合根，`UIController` 只持有该运行时提供的 `IHardwareClient`：

- **Host 模式**只创建 `PipeHardwareClient`。主进程不会调用 `new HardwareMonitor(cfg)`，也不会打开 LHM `Computer`；网络 IP 使用独立的系统网络接口查询。
- **默认模式**创建 `HardwareMonitor`，再由 `HardwareClientAdapter` 暴露同一个已发布快照。运行时拥有并释放二者。
- `HardwareGateway` 注册的是同一个 client；指标、FPS、GPU 数量和健康状态都以 client 快照为准。本地 monitor 只作为默认模式下的维护能力入口，不能成为第二套展示数据源。
- `UIController.Dispose` 使用 client 身份校验后注销 Gateway，再由运行时一次性释放所属资源，避免旧 controller 释放新实例。

快照消费边界如下：

- **UI Tick**：采样请求完成后只调用一次 `GetLatestSnapshot()`，并围绕该快照建立不可变 `HardwareSnapshotValueIndex`。基础面板、横屏、任务栏、初始值、历史、告警和 GPU 数量共享同一个 `BatchId`。读取失败时允许保留上一帧用于画面显示，但不会把旧批次再次写入历史或告警。
- **Web 响应**：每个 HTTP/WS 响应只读取一次 client 快照，全部指标和 FPS 判断使用同一个索引。不同响应可以落在不同批次。
- **健康与诊断**：健康状态统一读取 `IHardwareClient.GetHealth()`；诊断读取若需要快照，固定使用该次读取自己的批次。
- **取值语义**：`Good` 与 `Stale` 可以显示；历史与告警只消费 `Good`。`Missing` 与 `Error` 不转换成数值 `0`，也不能触发本地硬件补数。

Host 的 GPU 标题来自 client 目录：`SessionEpoch` 变化时先清空 `GpuRegistry`，`CatalogVersion` 变化时重建；目录失败或尚无批次时使用稳定的 `GPU.{index}` 回退标题，不复用旧会话的卡名。

首批进程隔离只覆盖 CPU/GPU 指标一致性。Host 目录没有提供的磁盘、网络、风扇、主板温度或 CPU 电压来源会保持配置原值但禁用；磁盘探测、控制器探测、IP 流量排除和主进程驱动检查在 Host 模式明确不可用。扩展 Host 传感器范围属于后续工作。
### 4.3 IPC 通道

- **传输**：Windows 命名管道，管道名按会话随机生成，每次新会话使用新管道名。
- **帧格式**：4 字节长度前缀 + UTF-8 JSON 信封 `IpcEnvelope`。单条消息上限 1 MiB，超限视为协议违规。
- **版本协商**：信封头携带 `Major` / `Minor`，主程序据此判定兼容性；版本不匹配时拒绝会话。
- **会话令牌**：每次会话生成独立令牌，随握手与命令往返传递；**旧会话的应答不得完成新会话的命令**。
- **序列号**：`Sequence` 用于检测快照缺口；`RequestId` 用于命令应答配对。

**事件顺序（一次正常会话）**

1. 主程序建立管道服务端，生成管道名、会话令牌与会话纪元，启动 `LiteMonitor.HardwareHost` 进程，命令行携带 `--pipe`、`--session`、`--parent-pid`、`--refresh-ms`、`--heartbeat-ms`、`--session-epoch`。
2. Host 连接管道并发送 `hello`。
3. 主程序校验协议版本与会话令牌，通过后回复 `welcome`，会话进入 `Ready`。
4. Host 周期性发送 `snapshot` 与 `heartbeat`。
5. 主程序按需发送 `command`；Host 回应 `response`，重扫类命令通过 `operation-status` 汇报进度。
6. 关闭时主程序发送 `shutdown`，Host 回复 `goodbye` 后退出。

**Host 命令行**

| 参数 | 含义 |
| --- | --- |
| `--pipe <name>` | 管道名 |
| `--session <token>` | 会话令牌 |
| `--parent-pid <pid>` | 父进程 PID |
| `--refresh-ms <n>` | 采样周期 |
| `--heartbeat-ms <n>` | 心跳周期（下限 100 毫秒） |
| `--session-epoch <n>` | 会话纪元 |
| `--test-pattern` / `--stale-first` / `--fault <name>` / `--selftest <rounds>` | 故障注入与自测参数 |

未知参数或缺少 `--pipe` / `--session` 时，Host 以退出码 `64` 结束并输出用法行；正常会话退出码为 `0`。

**Host 可执行文件查找顺序**：主程序输出目录下的同名文件，其次是仓库开发布局下的 `src/HardwareHost/bin/<config>/net8.0-windows/`。

### 4.4 快照的不可变批次语义

`SnapshotPublisher`（`src/System/HardwareServices/`）是进程内快照的唯一发布点，管道路径由 `PipeHardwareClient` 承担同样的发布-订阅职责。两者遵循同一组不变量：

- **只有采集层写快照，且只在一次真实采样完成后写一次。** 消费方只读已发布的内容。
- **发布是一次原子替换。** `BatchId` 在发布时递增，快照引用通过 `Volatile.Write` 整体替换，读者要么看到旧批、要么看到新批，绝不会看到半截状态。
- **读取不改变内容。** 读取不推进 `BatchId`，不改变时效字段；同一批数据读多少次都返回同一份内容。
- **尚未完成第一次真实采集时返回空批次**（`BatchId = 0`，样本集为空），批次级缺失原因为 `initializing`。空批次不伪造成功批次。
- **采集失败发布失败批次**：全部已知 Key 标记为 `Missing`，缺失原因为 `collection-failed`。**失败不得保活上一批的有效值**，陈旧读数不得被无限期当作有效数据。
- **值与目录不混代次。** 快照同时携带 `CatalogVersion` 与 `ConfigRevision`，目录结构与样本值同批发布。
- **`SessionEpoch` 保证跨会话隔离。** 进程内在采集器重建时递增；Host 模式下每次新会话递增，旧会话的快照数据不得混入新会话。
- **`ConfigRevision` 只在配置真正生效后回写**，未应用过任何配置时为 `-1`。

**数据一致性不变量**

| 规则 | 说明 |
| --- | --- |
| 真实 0 是有效值 | `SampleQuality.Good` + `Value = 0`。绝不用 0 冒充"没读到"。 |
| 缺失一律带原因 | 没有读到值时为 `SampleQuality.Missing` + `MissingReason`，缺省原因为 `no-value`。 |
| 缺失样本不带单位 | `Unit` 为 null，与显示端口径一致。 |
| 单位口径与面板一致 | 单位由 `MetricUtils.GetUnitStr` 按 `MetricUtils.UnitContext.Panel` 解析；解析异常退化为无单位，但样本本身仍然发布。 |
| 流量缺口不补算 | 采样失败不以历史值填补。 |

**管道路径的附加规则**（`PipeHardwareClient`）

- 快照缓存以 volatile 引用原子替换，读侧永不撕裂。
- 时效判定使用接收侧单调钟 `Environment.TickCount64` 加上随消息传来的样本时间戳；**首次收到的已过期样本不得被置为新鲜**。
- 命令超时**不等于**底层操作已取消：超时的非幂等操作保留有界操作记录，新请求得到明确的忙碌状态或操作 ID，不重复发起底层非幂等操作。
- **一个等待方取消不取消共享操作**，取消只作用于各自的等待。
- 迟到的应答按未知请求 ID 丢弃并计数。
- 断线后拒绝新操作；快照仍可读，过期的快照标记为 `Stale`。
- 断线重连后重放最新期望配置与有效需求。
- 同一操作的并发请求合并为同一个在途任务。
- 待应答表容量上限 64，需求消费者数量上限 64，需求命令独立期限 2 秒，命令等待兜底宽限 250 毫秒，操作轮询间隔 100 毫秒。

### 4.5 采样需求登记

`DemandRegistry`（`src/System/HardwareServices/`）是纯数据的需求租约表，不接触硬件也不启动线程。

- 每个消费者一个槽位；同一消费者重复登记属于更新自身需求，不影响其他消费者。
- **聚合需求** = 所有 `Active` 消费者的通道按位或；没有登记任何活跃消费者时聚合结果为全 false，即"没有需求就不采样"。
- **释放一个消费者不影响其他消费者**；释放未登记过的消费者返回 `false`。
- **聚合最大允许间隔**取所有 `Active` 消费者声明的最小正值，下限 1000 毫秒；声明值为 0 表示未声明，不参与聚合。
- `HasNonDegradable` 表示存在 `Active` 且不可降级的消费者，采集层据此拒绝把采样频率降到该消费者请求之下。
- **兼容桶**：`HardwareClientAdapter.LegacyConsumerId = "legacy"` 承接旧的单桶需求调用；`HardwareClientAdapter.WebConsumerId = "web"` 承接 Web 需求。

### 4.6 监督状态机

`HardwareHostSupervisor`（`src/System/Pipe/`）由主程序拥有，负责启动、握手、监督、重启与回收 Host。

```
   Stopped ──▶ Starting ──▶ Ready ──┬──▶ Unresponsive ──┐
      ▲                            │                      │
      │                            └──▶ Disconnected ────┤
      │                                                   ▼
      └──────────── Starting ◀──── Backoff ◀───────────────┘
```

另有 `Degraded`（服务可用但功能受限）与 `Stopping`（关闭中）两个状态。状态迁移会触发 `Transitioned` 事件，记录原因与 Host PID。

**期限与阈值**（`HardwareHostSupervisorOptions` 的默认值）

| 项 | 默认值 |
| --- | --- |
| 握手期限 `HandshakeTimeout` | 5 秒 |
| 心跳间隔 `HeartbeatInterval` | 2 秒 |
| 心跳失联上限 `HeartbeatMissLimit` | 3 次 |
| 采样无进展期限 `SamplingProgressTimeout` | 10 秒 |
| 初始化上限 `InitializationTimeout` | 60 秒 |
| 退避序列 `BackoffSequence` | 1 秒 / 3 秒 / 10 秒 |
| 退避窗口 `BackoffWindow` | 5 分钟 |
| 窗口内连续失败阈值 `BackoffPauseThreshold` | 3 次 |
| 关闭宽限 `ShutdownTimeout` | 3 秒 |
| 命令期限 `CommandTimeout` | 5 秒（`IpcProtocol.DefaultCommandTimeout`） |
| 健康会话重置间隔 `HealthySessionResetInterval` | 30 秒 |

**生效的不变量**

- 旧进程确认退出后才启动新会话。
- 恢复循环由主程序拥有，Host 侧不承担重启自身。
- **Job Object（kill-on-close）绑定 Host 及其子进程**，主程序异常退出时不留孤儿进程。Job Object 创建失败属于致命错误，不允许降级为无 Job 兜底。
- 心跳照发但采样无进展同样判定为失活。
- 短命会话不重置连续失败计数，只有达到健康会话重置间隔的会话才清零退避计数。

**客户端指标**（`PipeClientMetrics`）记录首样延迟、快照计数、序列号缺口、快照批次缺口、迟到应答数、命令超时数、会话数、重启数、发布到接收的延迟分位、过期会话应答数。

---

## 5. 配置与状态持久化

### 5.1 便携布局

配置与状态文件与程序同目录，根路径为 `AppContext.BaseDirectory`（`SettingsHelper.ResolveRoot`）。`SettingsHelper.PathOverride` 是测试用的整体重定向接缝，置位后全部文件落入该沙箱目录。

### 5.2 文件清单

| 文件 | 内容 | 写入方 | 备份 / 临时 |
| --- | --- | --- | --- |
| `settings.json` | 应用设置：皮肤、刷新周期、监控项、窗口几何、主题模式、语言、Web 服务与凭据、插件配置 | `SettingsHelper` | `settings.json.bak`、`settings.json.{ProcessId}.tmp` |
| `TrafficHistory.json` | 网络流量历史 | `TrafficLogger` | `.bak`、`.tmp` |
| `HardwareHistory.json` | 硬件历史。v2 JSONL 是唯一写入路径，旧 JSON 仅作只读导入来源 | `HardwareHistoryLogger` | `.bak`、`.tmp` |
| `device-bindings.json` | 稳定设备身份绑定 | `DeviceBindingStore`（`src/System/HardwareServices/`） | `.bak` |
| `resources/themes/*.json` | 主题定义 | `ThemeManager`（目录为 `ThemeManager.ThemeDir`） | — |
| `resources/plugins/*.json` | 插件声明 | `PluginManager` | — |
| `LiteMonitor++_Error.log` | 未捕获异常日志 | `src/System/Program.cs` | — |
| `gpu-diagnostics.log` | GPU 排障日志，体积超过 512 KB 时重开 | `src/UI/UIController.cs` | — |

### 5.3 写入不变量

- **写入必须走"临时文件 + 原子替换"。** 临时文件名带进程号（`settings.json.{ProcessId}.tmp`），避免多个写者接管同一个半截临时文件；加载时清扫崩溃残留。
- **同一进程的写入必须串行化。** `SettingsHelper` 用 `_ioLock` 保证。
- **凭据不得以明文落盘。** Web 访问口令只保存 PBKDF2-SHA256 哈希（base64）与盐（迭代 100000 次，盐 16 字节，哈希 32 字节）。写入时由 `WebPasswordJsonConverter` 强制把明文字段写成空串。
- **凭据保护失败时整体保存取消，磁盘不发生写入。** 保存结果以四态表达：`SettingsSaveOutcome.Saved`、`Blocked`、`CredentialProtectionFailed`、`WriteFailed`。
- **禁写标记必须记录持有者。** `GlobalBlockSave` 用 `SaveBlockOwner` 记录当前持有者，一次成功读取只撤销自己置上的那一次，不解除外部置上的禁写。

---

## 6. 渲染路线与背景捕获

### 6.1 GPU 渲染路线

`src/Rendering` 实现 Direct3D 11 / Direct2D 渲染路线：

- **设备层**：`D3D11DeviceService`、`GpuDeviceLifecycle`、`GpuAdapterSelector`、`GpuResourceLedger`。
- **几何与前景**：`PanelGeometry`、`ForegroundDrawList`、`Direct2DForegroundPainter`。
- **场景与材质**：`src/Rendering/Scene`、`src/Rendering/Materials`。
- **呈现调度**：`GpuSceneRenderer`、`GpuPresentationHostController`、`PresentationSession`、`GpuPresentationSessionOwner`、`FrameScheduling`。
- **失败降级**：`GpuFailureFallbackScheduler`、`CapturePolicyCleanupPolicy`。

### 6.2 两条背景捕获链互不共享

仓库中存在两条彼此独立的背景帧获取链。它们在命名空间、捕获 API、帧载荷类型和消费者上都不重合，**不得共享帧缓冲，也不得互相回退到对方的帧类型**。

| 维度 | 链 A：`src/System/Backdrop/` | 链 B：`src/Rendering/Capture/` |
| --- | --- | --- |
| 命名空间 | `LiteMonitor.src.SystemServices.Backdrop` | `LiteMonitor.Rendering.Capture` |
| 捕获 API | GDI+ / Windows 屏幕捕获 | DXGI Desktop Duplication |
| 抽象 | `IBackdropFrameSource` | `IBackgroundFrameSource` / `IBackgroundFrameLease` |
| 帧载荷 | `System.Drawing.Image` | `IBackgroundFrameLease`（`GpuDesktopFrameLease` 持有 `ID3D11Texture2D`） |
| 核心实现 | `ScreenCaptureFrameSource`（只作单次抓屏原语）、`BackdropCaptureService`、`BackdropCaptureAdapters`、`BackdropCaptureNative` | `DesktopDuplicationFrameSource` |
| 消费者 | `src/UI/LiveGlassController` → `src/UI/AppearanceRuntime` | `src/Rendering/Presentation/GpuSceneRenderer` |

**链 A 的帧所有权契约**（`IBackdropFrameSource`）

- `TryGetFrame` 返回的帧是**借用**引用，调用方与背景渲染器均不得释放它，释放责任始终在生产者（后台捕获服务）一侧。
- 该接口不启动任何抓屏、计时器或后台任务；何时抓屏由实现方决定。
- 实现方必须能报告不可用原因（`UnavailableReason`），否则降级无法诊断。
- `IsPreview` 必须如实声明：真实捕获后端返回 `false`，固定样例图提供者返回 `true`。诊断中底图来源据此区分 `LiveFrame` 与 `PreviewFrame`。

**链 B 的结果枚举**（`DesktopCaptureResult`）：`FrameAvailable`、`LastFrameRetained`、`NoFrameYet`、`AccessLost`、`Failed`、`StaleGeneration`、`Disposed`。

---

## 7. 插件层

### 7.1 声明式插件

插件是 JSON 声明文件，存放于 `resources/plugins/`，由 `PluginManager.LoadPlugins` 按 `PluginConstants.CONFIG_EXT` 扫描加载。文件结构：

| 段 | 内容 |
| --- | --- |
| `id` | 插件标识 |
| `meta` | 名称、版本、作者、描述 |
| `inputs` | 插件参数：`key`、`label`、`type`（`text` / `select` 等）、`default`、`placeholder`、`scope`、`options` |
| `execution` | 执行定义：`type`（链式）、`interval`（秒）、`steps` |
| `outputs` | 输出项：`key`、`label`、`short_label`、`format_val`、`color`、`unit` |

每个 step 具备 `id`、`url`、`response_format`、`response_encoding`、`cache_minutes`、`extract`、`process`；`url` 允许 `http(s)://` 与应用内建的 `native://` 协议。`process` 支持正则替换、映射、模板解析、阈值开关等变换。

### 7.2 边界与市场

- **出网策略由独立类型集中判定**：`PluginAddressPolicy`、`PluginAddressPreflight`、`PluginLocalNetworkAuthorizer`、`PluginProxyAuthorizer`、`PluginHostPolicy`、`DynamicHostAuthorizer`。
- **插件 Key 空间独立**：插件监控项使用 `PluginConstants.DASH_PREFIX` 前缀，`PluginKeyOwnership` 负责归属判定。
- **市场事务具备发布租约与中断恢复**：`PluginPublicationLease`、`PluginMarket`、`ThemeMarket`；`PluginManager.RecoverMarketTransactions` 负责恢复被中断的安装事务。
- **主题市场校验规则单一来源**：`ThemeMarketValidator` 与 `src/Rendering/Configuration/ThemeRenderingConfig.cs` 同时供主程序与 CLI 使用。

---

## 8. 诊断层

- `DiagnosticHub` 是应用级诊断事件出口，保留进程内有界事件，不逐条写盘。`DiagnosticHub.Current` 为单例，带 `SessionId`。
- `DiagnosticEventStore` 承载事件，`DiagnosticSnapshot` 是诊断窗口的读取入口；`DiagnosticHub.SnapshotProvider` 可由组合根替换为真实 Provider 集合，默认返回空快照，保证窗口在未接线时不抛异常。
- `DiagnosticRedaction` 负责敏感字段脱敏，`DiagnosticPackageService` 导出诊断包，`DiagnosticRunMarker` 标记运行批次。
- **诊断窗口关闭只释放订阅，不清空事件历史。**
- 记录接口 `DiagnosticHub.Record(module, severity, reasonCode, operationId?, context?, utcTime?)` 要求 `module` 非空。

---

## 9. 主题工作室

- **入口**：`ThemeStudioLauncher.Open(MainForm, Settings, UIController?)`，由 `src/UI/MenuManager.cs` 调用。
- **应用路径复用既有能力**：工作室通过 `ThemeStudioForm.IThemeApplyGateway` 接口把"保存后生效"回注主程序，应用动作复用 `AppActions.ApplyThemeAndLayout`；不反射 `MainForm` 的私有字段，也不另设一套设置写入路径。
- **持久化前必须预检**：主题文件先经 `ThemeManager.Inspect` 检视，坏 JSON 或缺失文件不得先落盘再静默套用默认主题。
- **文档模型**：`ThemeDocumentStore`、`ThemeDocumentSession`、`ThemeDocumentHistory`、`ThemeEditHistory` 支撑编辑与撤销。
- **属性目录**：`ThemePropertyCatalog` 与 `ThemePropertyCatalog.Gpu` 描述可编辑属性集合。
- **预览**：`ThemePreviewControl` 走链 A 的背景帧，`GpuThemePreviewControl` 走链 B 的 GPU 预览。
- **对外产出**：`ThemeExportService`、`ThemeFileService` 负责导出与文件读写，`ThemeSubmissionDialog` 负责提交到主题市场。

---

## 10. Web 服务

- `src/System/WebServer/` 提供 `LiteWebServer`（TCP 监听 + WebSocket 会话）、`WebSessionManager`、`WebServerAccessPolicy`、`WebThemeCss`、`WebPageContent`。
- **数据来源统一走 `HardwareGateway`**：`NetworkIP`、`GetSampleValue`、`RequestSamplingAsync`、`TrySetWebDataDemand`、`RegisteredTarget`。
- **Web 需求以独立消费者身份登记。** `HardwareClientAdapter` 使用 `WebConsumerId = "web"` 作为需求桶，并且只有该桶驱动采集层的 `SetWebDataDemand`；其它消费者登记需求不会误关 Web 通道。释放某个消费者的需求只影响该消费者。
- **凭据规则**：口令只存 PBKDF2-SHA256 哈希与盐；凭据指纹 `GetWebPasswordFingerprint` 与 `LiteWebServer.CurrentCredentialFingerprint` 用于识别凭据变更。
- **暴露规则由 `WebServerAccessPolicy` 单点判定**，三个执行点共用同一套凭据口径：冷启动时（`LiteWebServer.Start`）、运行中凭据被清空时（`AppActions.ApplyWebServer` 必须停止监听）、经反向代理的外部暴露时。局域网暴露或外部暴露在没有有效凭据时一律拒绝监听。

---

## 11. 网络流量采集

`src/System/NetworkTraffic/` 实现基于 ETW 的流量统计：

- `EtwNetworkTrafficEventSource` 是事件源，`INetworkTrafficEventSource` 是抽象。
- `NetworkAdapterResolver` 解析网卡，`NetworkTrafficFilter` 与 `IpExclusionRules` 处理排除规则，`NetworkTrafficExclusionService` 对外暴露排除状态 `NetworkTrafficExclusionStatus`。
- `TrafficPayloadReader` 负责载荷读取，`NetworkTrafficModel` 定义数据模型。

---

## 12. 架构门禁

- `scripts/architecture-assert.ps1` 采用**棘轮模式**：实际值与 `scripts/architecture-baseline.json` 基线比对，实际值不超过基线即通过，只拦截指标变差，不拦截改善。
- **A 组断言**：领域层不得引用 WinForms 与 System.Drawing 的类型引用（命中口径为剔除注释与字符串字面量后的类型引用）。
- **C 组断言**：目录到命名空间的映射一致性。允许的历史差异白名单为 `src/System` → `LiteMonitor.SystemServices`、`src/System/HardwareServices` → `LiteMonitor.SystemServices`、`src/System/WebServer` → `LiteMonitor.WebServer`、`src/UI/Settings` → `LiteMonitor.UI.SettingsPage`。
- **Release 构建开启 `TreatWarningsAsErrors`**，覆盖主程序、`LiteMonitor.Contracts`、`LiteMonitor.HardwareHost` 与 `LiteMonitor.Updater`。

---

## 13. 已知限制

- **两种采样模式并存。** Host 模式只由命令行 `--hardware-host` 或环境变量 `LITEMONITOR_HARDWARE_HOST=1` 显式开启，默认运行在进程内模式。
- **两条背景捕获链并存且不共享。** 启用 GPU 渲染路线并不意味着 GDI+ 背景链被替换，两条链各自维护可用性判定与降级路径。
- **`HardwareGateway` 的便捷方法读取进程内采集器。** `GetSampleValue`、`NetworkIP`、`HasFpsData` 以及清理内存、驱动体检、重配硬件、探测策略等交互动作不经契约客户端；跨进程统一的数据面目前覆盖的是面板、趋势、网络 IP 与全量重扫。
- **Host 产物依赖构建目标落位。** `LiteMonitor.HardwareHost.exe` 由 `CopyHardwareHostToOutput` 复制到主程序输出目录；脱离该布局运行时，Host 的查找会落到仓库开发布局的 `src/HardwareHost/bin/` 路径。
- **领域层与插件层存在双向引用。** 插件配置随设置持久化，插件实现又复用领域层的指标与主题工具，这一耦合在当前代码中存在。
- **Web 口令明文在生产形态下为空串。** 凭据变更的判定完全依赖哈希指纹，不能通过比较明文发现变更。
