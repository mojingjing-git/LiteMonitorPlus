# 派发单 P-B1B2 —— C1 契约工程 + 进程内适配器 + 消费方迁移

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| B1+B2（≙Plan A 的 I1） | master `98c0799` 之后；P-B0b 合入前可动工（fixture 合入后须复跑全绿） | `pkg/B1-c1-contract`（B1）→ `pkg/B2-consumers`（B2） | Plan B §4.1/§5 B1/B2、Plan A §4/§7 I1 |

## 启动指令

> 本包由主会话执行（契约与调用方迁移的同一负责人条款，Plan B §7）。在主工作树工作；`src/` 热点文件自本包起由 B 线独占，其他线改动前先对交接 SHA。

## C1 契约（字段级固定，B1 按此实现，不再改名）

程序集 `LiteMonitor.Contracts`，`src/Contracts/`，`net8.0`，无 WinForms/LHM/Vortice 依赖，命名空间 `LiteMonitor.Contracts`（恰好满足 architecture-assert 的目录推导规则，不触基线）。

```csharp
public enum SampleQuality { Good, Stale, Missing, Error }
public enum HardwareState { Stopped, Starting, Ready, Degraded, Unresponsive, Stopping }

public interface IHardwareClient : IDisposable
{
    HardwareSnapshot GetLatestSnapshot();                    // 同步读最新完整快照，不触发采样
    Task<DeviceCatalog> GetCatalogAsync(CancellationToken ct); // 完整版本目录；不向调用方暴露对象锁
    SamplingApplyResult ApplyConfig(SamplingConfig config);   // 返回生效 revision 或明确失败
    void SubmitDemand(SamplingDemand demand);                 // UI/任务栏/Web/历史/传感器窗口需求合并
    HardwareHealth GetHealth();
    Task<bool> RequestRescanAsync(CancellationToken ct);      // 有界重扫；重复请求合并
}

public sealed record SamplingConfig(int Revision, int RefreshMs,
    ControllerProbePolicyData ControllerProbe, DiskProbePolicyData DiskProbe,
    IReadOnlyList<DeviceSelection> Devices);   // 语义清单：现有设置 + F08b ControllerProbePolicy + DiskProbePolicy

public sealed record SamplingDemand(bool Ui, bool Taskbar, bool Web, bool History, bool SensorWindow);

public sealed record MetricSample(string Key, string StableDeviceId, float? Value, string Unit,
    SampleQuality Quality, string? MissingReason, double SampleAgeMs);

public sealed record HardwareSnapshot(long BatchId, long CatalogVersion, int ConfigRevision,
    IReadOnlyList<MetricSample> Samples, TimeSpan SamplingDuration,
    double TimestampMonoMs);                    // 批次原子替换；真实0=Good+0，缺失=Missing+原因，过期=Stale

public sealed record DeviceCatalogEntry(string StableId, string RuntimeId, string? ParentStableId,
    string DeviceType, string? SensorType, bool Available);   // StableId 承载 F07 稳定身份语义

public sealed record DeviceCatalog(long Version, IReadOnlyList<DeviceCatalogEntry> Entries);

public sealed record HardwareHealth(HardwareState State, string? CurrentOperation,
    long ProgressSeq, string? DiagnosticReason);

public sealed record SamplingApplyResult(bool Applied, int? Revision, string? FailureReason);
```

首版接口围绕盘点确认的 4 类现有调用需求（Plan B §4.1），不做超集；`GPU.{index}.*` 等外部 Key 保持不变，由 `StableDeviceId` 解释。

## B1 步骤（契约 + 适配器，行为零变化）

1. 建 `src/Contracts/LiteMonitor.Contracts.csproj`（`net8.0`、`Nullable=enable`、`ImplicitUsings=enable`）与上述类型文件。
2. 主工程手术（Plan B §3 的坑）：`LiteMonitor.csproj` 加 `<Compile Remove="src\Contracts\**\*.cs" />` + `<ProjectReference Include="src\Contracts\LiteMonitor.Contracts.csproj" />`；确认同一类型不重复编译进 WinExe；`LiteMonitor.sln` 加入新工程。
3. 进程内适配器 `src/System/HardwareServices/HardwareClientAdapter.cs`：包装现有 `HardwareMonitor`，快照/目录/配置/需求全部委托现状实现，**不引入额外轮询**；`Instance` 单例暂留（B2 收敛消费面）。
4. `UIController` 构造处组装适配器并持有 `IHardwareClient`（最小接缝，Tick 采样暂走旧路径，B2 切换）。
5. 验证：全解决方案构建零错误；全量测试 ≥2233 发现数；`scripts/architecture-assert.ps1` 全绿（预期零基线变化）；测试数有增则报协调者上调门禁。

## B2 步骤（消费方迁移，按盘点清单）

1. **11 个 `HardwareMonitor.Instance` 消费文件**（B0/I0 盘点 §1.2）逐个改为注入/取 `IHardwareClient`；`UIController.cs:1056` 与 `LiteWebServer.cs:224` 两个采样入口统一走契约方法。
2. **LHM 泄漏 3 文件**：`SystemHardwarPage.cs`（`ComputerInstance`+`SyncLock`）、`HardwareInfoForm.cs`（`ComputerInstance`+LHM using）改走 `GetCatalogAsync` + 显式登记/释放 `SensorWindow` 临时需求；`LiteTreeView.cs` 去 LHM using，树节点用 `DeviceCatalogEntry` 平台无关类型枚举。
3. 收敛 `HardwareMonitor.Instance`：迁移完成后将其降为适配器私有（或 internal），公共面仅 `IHardwareClient`。
4. 每文件一个提交；`MetricItem` 内部、格式化行为、采样时序、settings.json 形状**一律不改**（那是 B3/B4 范围）。
5. 验证：P-B0b fixture 全绿（合入后）；全量测试；断言脚本 A-2 命中数应显著下降（允许下调基线并记录）；Plan B §6.2 架构检查第 2 条达成（UI/Web 无 LHM/ComputerInstance/SyncLock 引用，启动组装例外除外）。

## 完成产物

1. 分支与分阶段提交（B1 一组、B2 按文件一组）；每步红→绿。
2. `docs/verification/acceptance/B1B2-report.md`：迁移前后调用面对照、行为等价证据（fixture+全量测试）、断言基线变化记录、遗留例外清单。
3. 完成后触发：Plan B §9 的 B0～B2 首批验收 + 向 Plan A I2 交接（契约已就位，可开 HardwareHost 原型）。
