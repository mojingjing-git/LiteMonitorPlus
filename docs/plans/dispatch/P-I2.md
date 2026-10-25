# 派发单 P-I2 —— HardwareHost 进程隔离原型（I2，与 P-B3 并行）【v2，按评审返工】

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| I2（原型） | 首批已验收（`fd3b425`，C1 契约与 `IHardwareClient` 在 master） | `pkg/I2-hardware-host`（独立工作树） | Plan A §5/§6/§7 I2 行 |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-I2 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读 `docs/plans/dispatch/README.md` 外派总则，再通读 [Plan A](../2026-10-26-hardware-process-isolation-plan.md) §5~§8（IPC 设计、监督参数、验收矩阵）、[P-B1B2 验收记录](../../verification/acceptance/B1B2-report.md)（契约与适配器现状）与 [P-B3 §1 文件所有权表](P-B3.md)——**越界改表内 P-B3 专属文件=冲突，开工前先确认边界**。
> 开工前建独立工作树：`git worktree add .worktrees/i2-host -b pkg/I2-hardware-host master`，全程在 worktree 内工作；主工作树有并行会话（P-B3）在改 `src/Core`、`src/UI`。
> 环境变量（绝对路径，直接照抄）：`NUGET_PACKAGES=G:\TOOL\LiteMonitor-fork\.nuget-packages`、`DOTNET_CLI_HOME=G:\TOOL\LiteMonitor-fork\.dotnet-cli-home`（共用主仓包缓存，勿指向 worktree）。产品构建/测试在自己的 worktree 内串行执行即可（各 worktree 独立 obj/）。
> 本地核对 TRX 发现数：`dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug` 后看总计行；三个测试工程总计应 ≥2414 发现数（主 2322 + Updater 73 + GPU 19）。

## 1. 与 P-B3 并行的文件所有权（v2 修订）

| 区域 | P-B3 | P-I2 |
|---|---|---|
| `src/Core/**`、`src/UI/**`、`src/ThemeEditor/**` | ✔ | ✖，**唯一例外**：`UIController.cs` 构造缝（`_hwClient = new HardwareClientAdapter(...)` 一处 ±5 行的最小模式分流改动，见 §2.4；P-B3 rebase 时知悉） |
| `src/System/HardwareMonitor.cs` | ✔（仅阶段三 Instance 降 internal；I3 时整体移交） | 本包零改动（生产采集器迁移属 I3，见 §2.1 设计说明） |
| `src/System/HardwareServices/**` | ✖ | 本包零改动 |
| `src/System/WebServer/**`、`InfoService/**`、`src/System` 其余 | ✔（内容级等本包落地后） | ✖ |
| **`src/System/Pipe/**`（新，主程序侧管道客户端）** | ✖ | ✔ |
| `src/Contracts/**` | ✖（只消费） | ✔（新增 `IpcMessages.cs`，纯数据） |
| `src/HardwareHost/**`（新 WinExe）、`Program.cs`、`LiteMonitor.csproj`、sln | ✖ | ✔ |
| `tests/**` | 各自新增各自文件（见 §2.6 落位），互不修改对方文件 |
| `.github/workflows/**`、`scripts/architecture-*`、`architecture-baseline.json` | 双方均不动，集成人收口 |

## 2. 范围与步骤

1. **HardwareHost 新 WinExe（最小真实采样器，不搬生产采集器）**：`src/HardwareHost/LiteMonitor.HardwareHost.csproj`（net8.0-windows，引用 LibreHardwareMonitorLib 与 LiteMonitor.Contracts）。内置最小采样服务：直接用 LHM `Computer` 打开 CPU/GPU，按 `SamplingConfig.RefreshMs` 循环，产出现约 8~15 个真实指标（CPU 包负载/温度/时钟、各 GPU 负载/温度/风扇）的 `HardwareSnapshot`。**设计说明**：生产 `HardwareMonitor` 依赖 `Settings`/`MetricUtils`/WinForms（DriverInstaller/DownloadContext），整体迁入类库属 Plan A §7 的 I3 行（配置注入改造+全量资源迁移）——本包不做、不搬、不改它；原型以"真实 LHM 读数"证明隔离与 IPC 语义为目标。此决定的后果（Host 读数集 ≠ 主程序全量指标集）写入报告为已知边界。
   **工程手术（必做，否则主工程双入口 CS0017）**：`LiteMonitor.csproj` 加 `<Compile Remove="src\HardwareHost\**\*.cs" />`（现有 Remove 集只覆盖 `src\Contracts`），Host 产物以内容/复制项进入主输出目录（发布打包属 I5，本包只保证 Debug 输出可运行）。
   **新文件命名空间固定**（architecture-assert 按目录推导，写错平级会误触 C-3 绊线）：`src/HardwareHost/**` → `LiteMonitor.HardwareHost`；`src/System/Pipe/**` → `LiteMonitor.SystemServices.Pipe`；Contracts 新文件 → `LiteMonitor.Contracts`。
2. **IPC 契约扩展**：`src/Contracts/IpcMessages.cs`——长度前缀+UTF-8 JSON 的消息 DTO 与协议常量（主/次版本、sessionId、请求 ID、序号、单条上限 1MiB），纯数据无依赖。
3. **Host 侧管道服务端**（§5.1/§5.2/§5.3）：控制响应优先、快照槽位新替旧、命令有应答/超时/取消语义、TTL 用接收侧单调时钟+样本 age；管道名随机会话、ACL 限当前用户、握手验 PID/会话。
4. **主程序侧**：`src/System/Pipe/PipeHardwareClient.cs`（实现 `IHardwareClient`：连接、原子替换快照缓存、断线拒绝新操作）。模式开关 = **命令行 `--hardware-host` 或环境变量 `LITEMONITOR_HARDWARE_HOST=1`，仅此两种**（settings.json 零变化、不新增字段；设置项开关属 I5）。接线方式：`UIController` 构造缝（§1 表唯一 UI 例外）**在缝内自读同一开关**（Program 静态旗标或环境变量，不改 `MainForm`、不穿参数）。**已知边界（写入报告，原型期接受）**：Host 模式下仅显示链路经 `PipeHardwareClient` 取数；`_mon` 直读点（告警/历史/GPU 计数约 10 处）与构造行 `new HardwareMonitor(cfg)` 维持进程内现状——原型目标是证明 IPC/监督/资源代价，"同一时刻最多一个采集器"的完整语义由 I3 交付，本包不得宣称已达成。进程内 `HardwareClientAdapter` 保持默认路径（§9：正式发布不自动回退同进程模式）。
5. **最小监督器**（§6.1 候选参数：握手 5s；心跳 2s×3；采样无进展 10s；初始化 60s；退避 1/3/10s、5 分钟 3 败暂停；关闭 3s）+ Job Object 绑定 Host 及其子进程，主程序退出不留孤儿。
6. **测试落位**：`tests/LiteMonitor.Tests/` 新增 `I2HostSupervisionTests.cs`、`I2PipeProtocolTests.cs`、`I2SnapshotAtomicityTests.cs`（xunit；假 Host 用专门的小测试 WinExe 或以本包 Host 工程产物 + 注入故障参数运行——产物路径从 `src/HardwareHost/bin/Debug/**` 取，测试内用相对路径定位 repo 根）。覆盖：FailFast 退出/采样线程无限阻塞/进程被杀/握手超时；拆包/超大长度/非法 JSON/错误版本/伪造连接/迟到响应；连续 100 次启停无孤儿与句柄增长；快照原子切换不混代次。**真实驱动重置/拔插不进 CI**（§8）。
7. **A/B 资源对照**：`scripts/perf-baseline.ps1` 现为单进程口径——**本包授权为其新增 `-LaunchArgs` 参数透传与双进程合计统计**（进程发现与聚合按主+Host 两进程相加，不破坏既有单进程默认行为）；"首样延迟/接收延迟"为新增仪表，采集方法（如客户端日志时间戳）在报告中写明。对照 §8 候选阈值（额外私有字节≤50MiB、额外 CPU≤5ms/s、接收 p95≤100ms）——评估阈值，达不到不删项、如实报数。
8. `architecture-assert` 预期：本包**零文件搬移**，C/A 断言应零变化；若意外变化，停下在报告中说明而不是改基线。

## 3. 边界与禁改

- P-B3 §1 表内文件零改动（`UIController` 构造缝为唯一登记例外）；`MetricUtils`/`MetricItem`/格式化零改动；`settings.json` 零变化（本包不新增任何字段）。
- 不动 `MIN_TEST_TOTAL`、`architecture-baseline.json`、CI/packaging（I5）。
- 不做 Sampling 工程拆分、不迁移生产采集器、不做 I3~I5（主程序内 LHM 初始化路径原样保留为进程内模式）。

## 4. 完成产物

1. 分支与分阶段提交；§2.6 测试全绿；两模式 A/B 数字表。
2. `docs/verification/acceptance/I2-report.md`：故障矩阵结果、协议测试结果、资源对照、最小采样器与生产采集器的指标集差异、I2 后产品评估点材料（是否继续 I3 的建议与代价）、偏离与遗留。
3. 合入顺序：与 P-B3 独立；先完成者直接合 master，后者 rebase。
