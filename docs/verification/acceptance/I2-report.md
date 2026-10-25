# P-I2 验收记录 —— HardwareHost 进程隔离原型（Plan A I2 行）

## 1. 标识

- 工作包：[P-I2 v2](../../plans/dispatch/P-I2.md)；Plan A §7 I2 行（隔离原型）
- 分支：`pkg/I2-hardware-host`（独立工作树 `G:\TOOL\LiteMonitor-fork\.worktrees\i2-host`，基点 `1fd475c` = master）
- 执行日期：2026-10-26
- 边界遵守：P-B3 §1 表内文件零改动（`UIController.cs` 构造缝为唯一登记例外，净 +5 行、
  全限定名引用未新增 using）；`HardwareMonitor.cs`/`HardwareServices/**`/`src/Core/**`（除无）/`WebServer` 零改动；
  `settings.json` 零变化（未新增任何字段）；`MIN_TEST_TOTAL`、`architecture-baseline.json`、
  `.github/workflows`、`scripts/architecture-*` 一字未动。
- 环境提要：QINGLING（MS-7C35）/ Ryzen 7 5800X（8C/16T）/ 63.9 GB / RTX 3080 + 3080 Ti /
  Win11 专业版 build 26300 / .NET SDK 8.0.426 / 提权运行。

## 2. 交付内容（提交清单）

| 提交 | 内容 |
|---|---|
| `aabd045` feat(I2-1) | HardwareHost WinExe（最小真实 LHM 采样器 + `--selftest`）；主工程 Compile Remove 手术 + ReferenceOutputAssembly=false 构建顺序 + CopyHardwareHostToOutput；sln 挂接 |
| `570e549` feat(I2-2) | `src/Contracts/IpcMessages.cs`（消息 DTO + 协议常量）与 `IpcCodec.cs`（长度前缀帧编解码 + 流式读取器/串行写入器） |
| `d1c71b6` feat(I2-3) | Host 侧 `HostPipeSession`（会话客户端）+ 主程序侧 `PipeServerEndpoint`（管道服务端：随机会话名、ACL 限当前用户、握手验 PID/会话/版本） |
| `47fe852` feat(I2-4+5) | `HardwareHostMode`（开关）、`HostJobObject`（kill-on-close）、`HardwareHostSupervisor`（状态机/退避/暂停）、`PipeHardwareClient`（IHardwareClient 实现）+ UIController 构造缝 |
| `77ee7a1` perf(I2-7) | `perf-baseline.ps1` `-LaunchArgs` 透传 + 双进程合计统计 + IPC 客户端指标采集 |
| `fix(I2-7)` | CopyHardwareHostToOutput 跟随 BaseOutputPath（perf-run 产物缺 DLL 修复，A/B 首轮实测暴露） |
| docs(I2) | 本报告 |

新增测试文件（仅三个，派工单 §2.6 落位）：`tests/LiteMonitor.Tests/I2HostSupervisionTests.cs`
（含 Host 产物定位器与 selftest 冒烟 3 例 + 监督故障矩阵 8 例）、`I2PipeProtocolTests.cs`
（编解码 10 例 + 端点集成 7 例）、`I2SnapshotAtomicityTests.cs`（5 例）。
主测试工程 **+33 用例**：2322 → 2354 发现 / 2347 通过 / 7 跳过（既有口径零变化）；
Updater 73、GpuNonperformance 19 零变化。

## 3. 故障矩阵（Plan A §8，真实 Host 进程注入故障）

监督期限类参数在测试中收紧（机制同、时限短）；产品默认值随
`HardwareHostSupervisorOptions` 交付（握手 5s、心跳 2s×3、采样无进展 10s、初始化 60s、
退避 1/3/10s、5 分钟 3 败暂停、关闭 3s），测试断言的是机制而非等待 10s/60s 本身。

| 故障注入（真实 Host `--fault`） | 验证用例 | 结果 | 依据 |
|---|---|---|---|
| FailFast 崩溃（`Environment.FailFast`） | FailFast与握手超时…三败暂停 | ✅ | 主进程（测试进程）存活；3 败后 Degraded("crash-loop-paused")，无孤儿 Host |
| 握手后立即 FailFast | crash-after-handshake（端点级 welcome 后）| ✅ | 会话断开 → 退避 → 重启路径同 "进程被杀" 用例 |
| 采样线程无限阻塞（心跳照发） | 采样线程无限阻塞…判死 | ✅ | "sampling-stalled" 裁决（心跳仍活也不能无限卡采样，§6.1） |
| 心跳静默失联（快照照发） | 心跳失联…判死并暂停 | ✅ | "heartbeat-missed" 裁决——只看心跳时戳，快照流动不掩盖控制通道失联；最终 crash-loop 暂停 |
| 进程被外部结束 | 进程被杀…重启恢复 | ✅ | 断线→退避→**确认旧进程退出后**重启新会话（新 PID、新管道名、新会话令牌）；同一时刻仅 1 个 Host 进程 |
| 握手超时（8s 后才连接） | 握手超时…有界返回 | ✅ | 期限到返回 null 不挂死；计入失败退避 |
| 拆包/超大长度/非法 JSON/错误版本/伪造连接/迟到响应 | 协议测试 17 例 | ✅ | 见 §4 |
| 主程序异常退出 | JobObject 句柄关闭…终止 | ✅ | 仅关作业句柄（模拟主程序死亡）后 Host 被系统终止 |
| 连续 100 次启停 | 连续100次…无孤儿 | ✅ | 全程无孤儿 Host；句柄 0→+0（≤+300 余量，实测远低）、线程 ≤+30 |

「无法退出则标记隔离失败并禁止启动重复采集器」：StopAsync 强杀失败路径已实现
（`IsolationFailed` → Degraded，不再重启），但**无自动化用例**（需要制造不可杀进程，
不进 CI）——待实机项。

## 4. 协议测试结果（Plan A §5）

| 类别 | 用例数 | 关键断言 |
|---|---|---|
| 帧往返/值语义 | 1 | 真实 0=Good+0、缺失=Missing+原因、批次/单位字段不丢失 |
| 拆包 | 1 | 每读 1 字节的限流流上完整重组帧 |
| 超大长度 | 3 | 长度先校验后分配（读侧只碰 4 字节前缀即拒）；编码侧 >1MiB 拒绝 |
| 非法 JSON / 未知类型 / 缺会话 | 3 | `IpcProtocolException` 有界拒绝，不重同步 |
| 错误版本 | 2 | 编解码层放行可读出对端版本；端点握手层回发 `version-mismatch` 拒绝（可解释，非静默） |
| 伪造连接（PID/会话） | 3 | 真实 PID 以 `GetNamedPipeClientProcessId` 核验（不信任载荷）；会话令牌随机不可猜；拒绝后继续等待、期限内有界返回 |
| 完整会话 | 1 | 真实 Host：握手→快照(12 样本)→命令应答（请求 ID 对齐）→goodbye→退出码 0 |
| 迟到响应 | 2 | 未知请求 ID 丢弃+计数；后续命令配对不受污染；并发同操作在飞合并 |

管道拓扑按 Plan A §5.2：**主程序为服务端**（随机会话名、ACL 当前用户、本地命名空间禁远程），
Host 为客户端（继承主程序环境与权限，首版不改变提权模型）。

## 5. A/B 资源对照（§8 候选阈值评估：额外私有字节≤50MiB、额外 CPU≤5ms/s、接收 p95≤100ms）

**采集方法与偏离说明**：测量期间用户自有 LiteMonitor++ 实例（PID 48460，master 构建，
04:41 起）在运行——`perf-baseline.ps1` 的既有实例门禁**正确拒绝**了 GUI 采集（预期行为）。
为不杀用户进程，A/B 用侧证工具（`%TEMP%\i2-ab-harness.ps1`，一次提权）按**精确 PID** 归属
采样：同机同配置背靠背 4 轮（单进程 ×2、`--hardware-host` ×2），每轮 60s@1s；
主进程按启动句柄、Host 按进程名发现；Release、bin/perf-run 全新默认配置。背景噪声
（用户实例同机采样）对两模式同等存在。原始 JSON：`docs/verification/baseline/i2-ab-20261026.json`。

| 轮 | 模式 | 主 CPU (ms/s) | 主私有字节稳态 | Host CPU (ms/s) | Host 私有字节稳态 | 合计 CPU (ms/s) | 合计私有字节稳态 | Host 线程/句柄 |
|---|---|---|---|---|---|---|---|---|
| 1 | 单进程 | 168.7 | 85.2 MB | — | — | 168.7 | 85.2 MB | — |
| 2 | 单进程 | 166.1 | 77.9 MB | — | — | 166.1 | 77.9 MB | — |
| 1 | --hardware-host | 169.2 | 82.9 MB | 64.9 | 36.3 MB | 234.1 | 119.2 MB | 16–17 / ~306 |
| 2 | --hardware-host | 149.6 | 84.3 MB | 67.0 | 36.0 MB | 216.6 | 120.3 MB | 16–17 / ~308 |

- **额外私有字节 = +34.0 / +42.4 MB**（均值 +38 MB；Host 自身稳态 36 MB，主进程无显著增长）
  → **≤50 MiB 候选阈值：达标**。
- **额外 CPU = +65.4 / +50.6 ms/s**（均值 +58 ms/s）→ **≤5 ms/s 候选阈值：未达标（约 10 倍）**。
  归因（如实报数，不删项）：Host 每秒执行一次完整 LHM `Update()`，本机（5800X + 双 NVIDIA）
  单轮 ~140ms 墙钟 / ~65ms CPU——额外 CPU ≈ 恰好一次/秒的硬件轮询成本，**不是 IPC 开销**
  （IPC 接收 p95 仅 15ms）。且这是"双采集器原型"口径：主进程内既有 LHM 对象树仍按现状
  构造（I2 已知边界），I3 移除进程内采集后 Host 轮询是**替换**而非叠加；5ms/s 候选值只对
  "纯 IPC 增量"有意义。I3 复测时建议阈值拆为"采集成本（随轮询频率）+ IPC 成本（≤5ms/s）"。
- **快照接收 p95 = 15ms（max 31ms）**（发布→接收，客户端接收循环时间戳）→ **≤100ms 候选阈值：达标**。
- **首样延迟 = 937ms**（客户端构造 → 首个 Fresh 快照；含 Host 进程启动+握手+首轮采样）。
- 稳态窗口无重启（SessionCount=1 / RestartCount=0）、无迟到响应、无命令超时；
  SampleToReceive p95 = 172ms（样本时间戳取采样开始时刻，含 ~140ms 采样耗时本身，非传输延迟）。
- 序号缺口计数 86：当前口径把心跳/应答占用的信封序号也计为"缺口"（快照 BatchId 连续无回退，
  51 快照 ≈ 窗口/周期，无真实丢帧）——该指标口径需在 I3 收紧为"按消息类型分别计数"，遗留项。

**阈值评估**（评估阈值，达不到不删项、如实报数）：见上表"结论"列。

## 6. 最小采样器与生产采集器的指标集差异（已知边界，I3 偿还）

Host 侧最小采样器（`MinimalHardwareSampler`）**刻意不搬生产采集器**（派工单 §2.1 设计说明：
`HardwareMonitor` 依赖 `Settings`/`MetricUtils`/WinForms（DriverInstaller 等），整体迁入属 I3）。
实机（3080 + 3080 Ti，Ring0 驱动已装）自测：12 键 24/24 有值，单轮采样耗时 ~140ms。

| 维度 | 最小采样器（I2） | 生产采集器（进程内现状） |
|---|---|---|
| 指标键 | `CPU.Load/Temp/Clock` + `GPU.{i}.Load/Temp/Fan`（i=0 另带 `GPU.*` 别名），N 卡 = 3+3N+3 | 默认表 ~30+ 键（CPU×7、GPU×6、MEM/Commit、FPS、MOBO/DISK.Temp、CASE.Fan、SYS.Power、BAT×4、DISK.Read/Write、NET.Up/Down、DATA.*、DASH.*） |
| 设备面 | 仅 CPU/GPU | 主板/存储/网络/内存控制器/电池/控制器（F08b）全量 |
| 传感器选择启发式 | 负载取 Total/Core、温度/时钟取最大、风扇取最大（保守首版） | `SensorMap`+`SensorMatcher`+设备绑定（F07）全量语义 |
| Unit / StableDeviceId / SampleAgeMs | null / null / 0（与进程内适配器 B1 占位口径一致，B2/B4 做实） | 部分已做实（CPU.Voltage、MEM.Commit） |
| 静态发布器（CPU 电压/提交内存/GPU 注册） | 无（I3 随完整迁移） | 有 |
| ApplyConfig 生效面 | RefreshMs + Revision 真实生效；设备选择/探测策略接受并记无操作 | 全量（控制器/磁盘探测策略、设备开关） |
| GetNetworkIP | 回退进程内 `HardwareMonitor`（构造缝注入委托） | 原生 |
| 驱动安装 | 不装（沿用系统已装 Ring0；未装环境温度读数为 Missing——真实 0 与缺失有区分） | DriverInstaller（PawnIO 检查） |

## 7. I2 后产品评估点材料（是否继续 I3）

## 7. I2 后产品评估点材料（是否继续 I3）

**收益实测**（本机 + 注入故障，§3 全矩阵通过）：

- 采集进程 FailFast/被杀/采样线程无限阻塞/心跳静默失联时，**主进程不退出、状态机正确
  转换、退避重启在 1 个周期内恢复新会话**——隔离与监督语义成立。
- 协议层有界拒绝全部通过（超限/非法/伪造/迟到），ACL+随机会话+PID 核验三重身份约束落地。
- Job Object 兜底：主程序死亡后 Host 被系统终止，100 次启停无孤儿、无句柄泄漏。

**代价实测**（§5）：Host 进程稳态 ~36MB 私有字节、16–17 线程、~306 句柄；IPC 接收 p95 15ms；
额外 CPU ≈ 一次/秒的硬件轮询成本（~65ms/s 本机双 NV；单卡/无卡环境显著更低）。

**复杂度增量**：+1 个 WinExe 工程、+2 程序集内命名空间（Pipe/Host）、约 2.1k 行新增代码
（含测试）、1 处主程序构造缝；发布/更新链路（I5）尚未动。

**建议**：**继续 I3**，理由——(1) 故障隔离收益已实证；(2) 资源代价可接受且大头是可解释的
采集成本（I3 后为替换关系）；(3) C1 契约 + 管道实现可直接复用，I3 主要是搬移与做实
（Unit/StableDeviceId/静态发布器/流量连续性），风险集中在 `HardwareMonitor` 对
`Settings`/WinForms 的解耦。**代价估计更新**：I3（配置注入改造+全量资源迁移+全量指标集）
按 Plan A §10 原估 7–13 人日维持，另加 I2 实测提示的两项——序号缺口口径收紧、
"额外 CPU"阈值拆分复测。若决定**不继续**，I2 交付物自洽可独立保留（默认路径零行为变化，
开关默认关闭，随时可整体 revert）。

## 8. 偏离与遗留

### 偏离（保守决策，均可回滚）

1. **A/B 用侧证工具而非 perf-baseline.ps1 实跑**：用户自有实例在场，脚本门禁正确拒绝
   （B0b 同情形先例）。脚本本身的 `-LaunchArgs`/双进程统计已交付并在单进程冒烟下验证；
   干净机器上复跑 `pwsh scripts/perf-baseline.ps1 -LaunchArgs '--hardware-host'` 即得脚本口径。
2. **"Host 侧管道服务端"标题与 Plan A §5.2 的对齐**：派工单 §2.3 标题表述为"Host 侧管道
   服务端"，但 Plan A §5.2 明文"主程序创建随机会话名的管道服务端后启动 HardwareHost"。
   本包按计划执行（主程序=服务端，Host=客户端），§2.3 列举的全部要求（随机会话名、ACL、
   握手验 PID/会话、控制响应优先、快照槽位新替旧、命令应答/超时/取消、TTL）均已落地。
3. **监督期限在测试中收紧**：机制与默认值一致，测试参数化验证（见 §3 表头说明）。
4. **"无法退出标记隔离失败"无自动化用例**：需制造不可杀进程，列入待实机清单。
5. **LM-024 依赖清单核对范围**：`generate-dependency-manifest.ps1` 的产品工程清单不含
   HardwareHost（其依赖 LHM 0.9.6/Contracts 与主工程同版本，NOTICE 已覆盖）；是否把
   HardwareHost 纳入清单核对范围由集成人决定（脚本不在本包授权清单内，未动）。
6. **Program.cs 未改**：开关解析落在 `src/System/Pipe/HardwareHostMode.cs`（命令行+环境变量
   自读），构造缝内直接调用——比"Program 静态旗标"少一处主入口改动（派工单两案任选其一）。

### 待实机清单（不进 CI，Plan A §8）

- GUI 目视：`LiteMonitor++.exe --hardware-host` 启动后主面板/任务栏读数、传感器窗口
  （HardwareCatalog 经管道）、托盘气泡、健康状态；已知边界——Host 模式下面板/任务栏数值
  仍读进程内 `_mon`（本包禁改的显示路径），Host 侧真实读数经 `GetLatestSnapshot`/传感器窗口可见。
- 真实驱动重置/设备拔插/睡眠唤醒（§8 恢复类）。
- 干净机器（无 Ring0 驱动、单卡、无卡 CI runner）下 Host 读数降级形态（Missing 而非 0）。
- 杀不死的 Host 进程（隔离失败路径）。

### 遗留（I3~I5 范围，本包不宣称已达成）

- "同一时刻最多一个采集器"：Host 模式下进程内 `HardwareMonitor` 仍按现状构造与初始化
  （构造行与 `_mon` 直读点 ~10 处维持进程内现状，派工单 §2.4 已知边界）——完整语义由 I3 交付。
- 发布打包（I5）：Host 以复制项进入主输出；发布文件清单/更新器事务未动。
- 设置项开关（I5）：仅命令行/环境变量两种入口。
- 需求合流（B3）：`SubmitDemand` 在 Host 侧为记录型（与进程内适配器 V1 口径一致）。

## 9. 行为等价证据

| 项 | 结果 |
|---|---|
| 构建 | Debug/Release 0 错误（警告为存量） |
| 主测试工程 | 2347 通过 / 0 失败 / 7 跳过（跳过为既有口径） |
| Updater / GpuNonperformance | 73 / 19 全绿 |
| architecture-assert | 12/12 全绿，基线零变化（C-3 未触发：三个新命名空间按目录推导钉死） |
| 默认路径 | `HardwareHostMode.IsRequested()` 为 false 时代码路径与 I2 前逐字节等价（构造缝三元短路）；进程内 `HardwareClientAdapter` 保持默认 |
| settings.json | 零变化（无新字段、无新读写） |
