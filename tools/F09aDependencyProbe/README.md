# P-F09a 受控验证工程（原生依赖候选）

本目录是 **P-F09a（只读研究包）** 的受控验证工程，用于在不改动产品源码
（`src/**`、`LiteMonitor.csproj` 禁改）的前提下，用"注入故障 + 真机双卡冒烟"的方式，
比较不同原生依赖候选的**实际行为差异**。

> 本工程只被 `research/F09a-dependency-candidate` 分支引用，**不回合并 master**。

## 候选与工程对应关系

| 工程 | 引用的候选包 | 传递依赖 | 用途 |
|---|---|---|---|
| `src/Probe.Baseline` | `LibreHardwareMonitorLib 0.9.6` | DiskInfoToolkit **1.1.2** | 当前生产依赖基线 |
| `src/Probe.Fixed` | `LibreHardwareMonitorLib 0.9.7-pre767` | DiskInfoToolkit **2.2.1** | 含 PR #2539 NVML 修复的已发布预发布包 |
| `src/Probe.FixedLatest` | `LibreHardwareMonitorLib 0.9.7-pre771` | DiskInfoToolkit **2.2.2** | 当前可得的最新预发布包 |

三个工程**共用同一份探针实现** `src/Probe.Shared/ProbeRunner.cs`（以链接源文件方式编译），
因此三者之间的差异只来自被引用的候选包本身，不来自探针代码。

## 场景

| 场景 | 目的 | 关键观测 |
|---|---|---|
| `fingerprint` | 运行时结构指纹 | `NvidiaML.SyncRoot` 是否有；`NvidiaGpu` 是否仍缓存 readonly `_nvmlDevice`；是否有 `GetNvmlDevice`；DiskInfoToolkit 是 `StorageManager` 还是静态 `Storage` |
| `nvml-smoke` | 双卡真机冒烟 | 两张 NVIDIA 卡的 PCI 身份、按身份取 NVML 句柄、逐轮功耗读数、伪造 PCI 身份必须取不到句柄 |
| `nvml-stale-handle` | 句柄失效注入 | 清空传感器值 + 清空缓存句柄后，基线**再无数据**（无失效处理），修复版**下一轮即恢复** |
| `nvml-invalidate` | NVML 不可用注入 | 置 `NvidiaML.IsAvailable=false` 后，基线功耗读数**冻结成旧值**，修复版自动重新初始化并刷新 |
| `disk-thread-fault` | 库自带线程故障注入 | 往库内部状态塞 null，触发库后台线程；1.1.2 预期**进程被带走**（NRE），2.x 预期被库内吞掉 |
| `disk-callback-fault` | 设备变更回调故障注入 | 注册一个必抛异常的回调；1.1.2 预期回调在库线程上执行 → 进程被带走；2.x 预期被库内吞掉 |

## 运行

```powershell
# 全矩阵（每个场景一个独立子进程，便于观察"库线程异常带走进程"）
pwsh tools/F09aDependencyProbe/scripts/run-probe.ps1

# 只跑指定场景
pwsh -NoProfile -Command "& '.\research\F09a\DependencyProbe\scripts\run-probe.ps1' -Scenarios @('nvml-stale-handle')"

# 单个变体单个场景（手工）
dotnet tools/F09aDependencyProbe/src/Probe.Fixed/bin/Release/net8.0/Probe.Fixed.dll nvml-smoke --cycles 3
```

结果默认写入 `<仓库>/.tmp/probe-results/`（scratch，不入库）。

## 约束

- 每个场景在**独立子进程**中运行：故障注入场景会故意让基线崩溃，必须由父进程观察退出码。
- 磁盘场景会真实枚举本机磁盘（1.1.2 会探测 `\\.\PhysicalDrive0..63`）；本机无 USB/外置盘。
- 不修改产品工程；本目录不被 `LiteMonitor.csproj` 引用。
