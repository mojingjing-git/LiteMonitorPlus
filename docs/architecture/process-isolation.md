# 硬件采集进程隔离与 IPC

本文描述 LiteMonitor++ 中硬件采集子进程（HardwareHost）与主进程之间的隔离边界、跨进程契约、故障监督状态机与降级策略。它回答三个问题：**哪些硬件资源由谁独占持有**，**跨进程消息如何编码、校验与有界流转**，**采样子进程失效时主进程如何降级并继续响应**。

适用范围是 `src/HardwareHost`（采样子进程）、`src/Contracts`（跨进程契约库）、`src/System/Pipe`（主进程侧的管道端点、进程监督器与客户端）。界面渲染路线、网络流量采集、Web 服务、插件市场与主题工作室的内部设计不在本文范围；跨模块的总体拓扑见 `docs/architecture/overview.md`。

---

## 1. 隔离目标与边界

### 1.1 目标

隔离存在的唯一理由是**把不可靠的硬件访问从主进程的用户态路径上摘出去**：

- 驱动层死锁、native 组件崩溃、越界访问等硬件相关的故障，必须被限制在采样子进程内；主进程的窗口响应、动画、菜单、Web 服务不得因此挂起或退出。
- 故障必须是**可区分**的：主进程必须能分辨「子进程存活但数据陈旧」「子进程已崩溃」「子进程违反协议」三类情形，而不是把它们混成一句「采集失败」。
- 故障必须是**可恢复**的：子进程失效后，监督器必须在有限次重启内恢复采样；恢复不了时必须进入明确的降级态，并给出可解释的原因码。

### 1.2 边界（不承诺的部分）

- 隔离发生在**进程边界**，只能隔离用户态故障。内核态死锁、系统级 DPC 风暴、驱动导致的系统级卡死或内核崩溃，进程隔离无法提供保证。
- 「主进程仍可响应」指的是 UI 事件循环与渲染路径不被采集路径阻塞；它不等于主进程自身不持有任何硬件句柄（当前实现下主进程仍持有进程内采集器，见第 10 节）。
- 隔离不等于「全系统只有一份硬件树」。是否做到单一所有权，是所有权规则与实现现状两个层面的问题，本文档分别给出规则与现状。

---

## 2. 组件与职责

| 组件 | 位置 | 职责 | 明确不负责 |
|---|---|---|---|
| 主进程（WinExe） | `src/UI/UIController.cs` 等 | 组合根；界面、渲染、Web、配置持久化；作为管道服务端 | 不直接驱动采样节拍 |
| `HardwareHostSupervisor` | `src/System/Pipe/HardwareHostSupervisor.cs` | 子进程生命周期、故障判定、退避与崩溃循环保护；唯一有权创建与销毁 Host 进程 | 不解析业务载荷，不做 UI |
| `PipeServerEndpoint` | `src/System/Pipe/PipeServerEndpoint.cs` | 命名管道服务端点；对端身份核验；管道 ACL | 不做命令语义 |
| `PipeHardwareClient` | `src/System/Pipe/PipeHardwareClient.cs` | `IHardwareClient` 的管道实现；快照缓存、命令在途表、需求聚合、指标 | 不拥有硬件句柄，不决定采样参数 |
| `HostPipeSession` | `src/HardwareHost/HostPipeSession.cs` | Host 侧会话：握手、采样循环、心跳、单写线程、命令闸门 | 不写配置文件，不做 UI |
| `MinimalHardwareSampler` | `src/HardwareHost/MinimalHardwareSampler.cs` | Host 侧硬件访问面（CPU/GPU）、目录生成与重扫 | 不做设备启停选择的最终决策 |
| `HostJobObject` | `src/System/Pipe/HostJobObject.cs` | 作业对象（kill-on-close），约束 Host 及其后代 | 不做超时判断 |
| Contracts | `src/Contracts` | 消息信封、DTO、枚举；纯数据，零第三方依赖 | 不含任何实现 |

### 2.1 依赖方向

依赖必须单向指向 `Contracts`：

```
Contracts  ←  System/Pipe  ←  UI
Contracts  ←  HardwareHost
```

由此得到两条硬约束：

- `src/Contracts` 不得引用任何具体实现、不得引入第三方依赖；它是跨进程两侧共享的**类型契约**。
- `src/HardwareHost` 不得引用主程序集，也不得引入 WinForms；它是一个 `net8.0-windows` 的 WinExe，不含界面代码。构建上通过 `LiteMonitor.csproj` 的 `<Compile Remove="src\HardwareHost\**\*.cs" />` 与 `ReferenceOutputAssembly=false` 保证；Host 产物由 `CopyHardwareHostToOutput` 目标复制到主程序输出目录。

### 2.2 进程组合与数据源选择

`ApplicationHardwareRuntime` 在启动时确定唯一硬件源：

- **默认模式**：创建 `HardwareMonitor`，由 `HardwareClientAdapter` 发布同一快照。主进程拥有 LHM 生命周期。
- **Host 模式**：只创建 `PipeHardwareClient`，不实例化本地 `HardwareMonitor` 或其 LHM `Computer`。独立网络接口查询仅用于系统 IP，不构成第二套硬件采集树。
- 模式由 `HardwareHostMode.IsRequested()` 显式决定，并在组合根记录；`HardwareGateway` 注册的是同一个 client。任何展示数据都必须从该 client 的已发布快照读取。

Host 不可用的本地维护能力必须明确拒绝：磁盘探测、控制器探测、IP 流量排除和主进程驱动检查不执行；设置页保留原配置但禁用对应控件。Host 暂未发布的指标显示为缺失，不从主进程本地 monitor 补数。

每个消费边界固定自己的批次：UI Tick 一次读取并共享给面板、历史与告警；Web 每个响应一次读取；诊断读取各自建立索引。读取失败时不得复用旧批次生成新的历史点或告警事件。

---

## 3. 唯一所有权原则

**核心规则：每一份硬件相关的资源有且只有一个所有者，其它任何一方都不得复制它、缓存它的句柄、或并行地产生第二份访问。** 违反这条规则，进程隔离带来的稳定性收益就会被抵消。

| 资源 | 唯一所有者 | 规则 |
|---|---|---|
| 硬件对象与原生句柄 | 采样子进程 | 隔离模式下主进程不得为同一设备再开一份设备树；⇒ 硬件访问必须单点化，否则驱动侧死锁可以沿主进程路径复现 |
| 采样节拍 | 采样子进程 | `RefreshMs` 由主程序决定并通过 `--refresh-ms` 下发，Host 按该值执行；实际定时器只存在于 Host 内 |
| 探测策略 | 主程序决定，Host 执行 | `SamplingConfig.DiskProbePolicy` / `ControllerProbePolicy` 决定存储与控制器探测是否开启；策略必须在设备被打开前落定 |
| 配置写入 | 主进程 | `settings.json` 只有主进程写；Host 无权读写任何配置文件，只接受内存中的 `SamplingConfig` |
| 设备目录 | 采样子进程生成 | `HardwareCatalog`（`StableId`、`RuntimeId`、`Available`）由 Host 生成并以 `HardwareCatalog.Version` 版本化 |
| 设备自动选择 | 主程序 | 「采不采这个设备」由主程序通过 `SamplingConfig.Devices`（`DeviceSelection`：Kind / StableId / Enabled）决定；Host 不得自行改变选择集合 |
| 网络地址 | 主进程 | 隔离模式下 `GetNetworkIP()` 由主进程自身的软件查询回退提供，不构成第二份硬件树 |
| 历史与流量落盘、Web 服务、插件市场、主题 | 主进程 | 与硬件访问无关，不进入 IPC 面 |
| 子进程生命周期与终止权 | `HardwareHostSupervisor` | 只有监督器可以创建与销毁 Host；Host 不得自行重启 |

---

## 4. 管道传输协议

### 4.1 帧格式与长度上限

一帧的构成：

```
[int32 小端长度前缀（4 字节）][UTF-8 JSON 载荷（长度 = 前缀值）]
```

- 长度前缀不计入自身长度；`IpcProtocol.LengthPrefixBytes = 4`。
- 载荷长度上限 `IpcProtocol.MaxMessageBytes = 1024 * 1024`（1 MiB），因此单帧最大为 1 MiB + 4 字节。
- 读取必须**先校验后分配**：`IpcFrameReader` 只有在 `0 < length <= MaxMessageBytes` 成立时才分配或复用 1 MiB 缓冲；超限或非法长度立即按协议违规处理，绝不按对端声明的长度预分配内存。
- 写入必须一帧一写并 flush；读取必须容忍任意拆包与粘包。
- 管道自身缓冲为 64 KiB，超过该值的帧分多次传输是合法的，帧边界由长度前缀而非传输次数决定。

### 4.2 信封与版本

每条消息都是一个 `IpcEnvelope`：`IpcHeader` + `Payload`。`IpcHeader` 的字段构成协议的全部元信息：

| 字段 | 含义 |
|---|---|
| `Major` / `Minor` | 协议主版本 / 次版本，当前 `IpcProtocol.MajorVersion = 1`、`MinorVersion = 1` |
| `Type` | 消息类型，取值必须在类型白名单内 |
| `SessionId` | 会话令牌，必须等于当前会话的令牌 |
| `RequestId` | 请求关联号；命令与其应答复用同一个值 |
| `Sequence` | 帧序号，接收侧据此观测缺口 |

**主版本裁决发生在端点握手层**：主进程在握手时比对 hello 的 `Major`，不一致直接拒绝并断开。次版本不参与拒绝判定，⇒ 次版本可以向后兼容地演进。

### 4.3 消息类型白名单

`IpcProtocol.KnownTypes` 是唯一合法的类型集合：

`hello`、`welcome`、`snapshot`、`heartbeat`、`command`、`response`、`goodbye`

信封 `Type` 为空或不在该集合内 ⇒ `IpcProtocolException` ⇒ 视为协议违规，会话按违规处理（终止并回收）。

### 4.4 命令白名单

命令面由 `IpcCommandMessage.Operation` 定位，参数放在 `ArgumentJson`（JSON 字符串），结果放在 `IpcResponseMessage.ResultJson`。

| 操作名 | 参数 | 结果 | 说明 |
|---|---|---|---|
| `apply-config` | `SamplingConfig` | `SamplingApplyResult { Applied, Revision, FailureReason }` | 应用采样配置 |
| `submit-demand-v2` | `SubmitDemandArgs { ConsumerId, Demand }` | `DemandRegisterResult { Registered }` | 登记消费者需求 |
| `release-demand` | `ReleaseDemandArgs { ConsumerId }` | `DemandReleaseResult { Released }` | 释放需求 |
| `sampling-due` | 无 | `SamplingDueResult { Triggered }` | 请求立即采样；同一采样周期内的重复请求合并为一次触发 |
| `rescan-full-v2` | 无 | `HardwareOperationResult { Accepted, OperationId, State, FailureReason }` | 完整重扫，独立操作 ID 与状态机 |
| `operation-status` | `OperationStatusArgs { OperationId }` | `HardwareOperationResult` | 查询显式操作的真实结局 |
| `shutdown` | 无 | 应答后走退出流程 | 终止子进程 |
| `get-catalog`、`rescan`、`submit-demand` | 兼容形状 | 兼容结果 | 兼容桥操作名，同样在白名单内 |

- 显式操作的状态机为 `HardwareOperationState { Queued, Running, Completed, Failed, Cancelled, Rejected }`。
- **未知操作必须有界拒绝**：Host 应答 `unknown-operation:{op}`，不断连、不静默丢弃。
- 客户端侧的兼容入口（`ApplyConfig`、`RequestRescanAsync`、`SubmitDemand`）必须映射到上表中的白名单命令。

### 4.5 未知字段与未知类型策略

三条规则彼此独立，不可互相替代：

1. **信封层属性名大小写敏感**：`IpcCodec.Options.PropertyNameCaseInsensitive = false`。
2. **未知 JSON 成员按 System.Text.Json 默认行为忽略**，不构成违规；⇒ 不得用「忽略未知字段」来掩盖主版本冲突——主版本冲突必须在握手层被拒绝。
3. **未知消息类型是违规**（见 4.3），**未知命令不是违规**（见 4.4）。两者的处置不同：前者断连，后者可解释应答。

命令面的参数与结果 DTO 走另一套约定：写出用 camelCase、读取大小写不敏感。客户端侧 `PipeCommandJson.Options` 与 Host 侧 `HostCommandJson.Options` 是**同构**的两份实现（分别持有各自的 DTO），因此命令面的细微演进不必触碰 `Contracts`。

### 4.6 事件顺序

一次正常会话的时序：

1. 主进程生成随机管道名与会话令牌，创建命名管道服务端点并开始监听。
2. 主进程启动 Host 进程，命令行携带 `--pipe <管道名> --session <令牌> --parent-pid <pid> --refresh-ms <ms> --heartbeat-ms <ms> --session-epoch <纪元>`。
3. Host 连接 `"."`（仅本机），发送 `hello`：`IpcHelloMessage { HostProcessId, HostStartTickMs, HostVersion }`。
4. 主进程完成身份核验（见第 5 节）：通过则回 `welcome { Accepted = true }`；不通过则回 `welcome { Accepted = false, Reason }` 并断开。
5. Host 进入采样循环：按 `--refresh-ms` 产出快照并发送 `snapshot`（`IpcSnapshotMessage { Snapshot, PublishTickMs }`）；按 `--heartbeat-ms` 发送 `heartbeat`（`IpcHeartbeatMessage { State, CurrentOperation, ProgressSeq, DiagnosticReason, TickMs }`）。
6. 主进程按需发送 `command`；Host 应答 `response { Ok, Error, ResultJson }`，两者以 `RequestId` 配对。
7. 终止：主进程发送 `shutdown`；Host **先应答** `response`，再发送 `goodbye { Reason }`，然后退出。若宽限期后仍未退出，主进程直接终止进程树。

约定：握手是唯一的前置环节，快照与心跳可任意穿插；`command` 与 `response` 一一配对；`goodbye` 之后不得再有帧。

### 4.7 有界性与背压

- Host 侧只有**一个写线程**；控制队列优先于快照槽位。
- 快照槽位只保留最新一次快照，尚未发送的旧快照被新快照替换 ⇒ 慢消费者不会阻塞采样线程。
- 控制队列容量 256，满时丢弃新项并计数；需求表上限 64，超限答 `demand-capacity-exceeded`；操作表容量 32，超限答 `operation-capacity-exceeded`。
- 客户端待应答表上限 64，满时拒绝新命令，原因码 `command-capacity-exceeded`；需求消费者上限 64。
- 客户端操作记录表容量 32、TTL 2 分钟。
- **所有队列、集合、缓冲都必须有界**，任何表都不得随运行时长线性增长。

---

## 5. 身份与进程约束

### 5.1 命名空间与令牌

- **随机会话命名空间**：管道名为 `LiteMonitor.HardwareHost.{Guid:N}`，每次会话重新生成。命名不含 `Global\` 前缀 ⇒ 管道只存在于当前登录会话的命名空间内，远程机器不可达；不同用户会话之间也不会互相可见。
- **会话令牌**：每次会话生成一个 `Guid:N` 令牌，随 `--session` 传给 Host。所有消息的 `SessionId` 必须等于该令牌；不匹配即拒绝（原因码 `session-mismatch`）。
- **会话纪元**：`--session-epoch` 在每次新建会话时递增，快照携带 `HardwareSnapshot.SessionEpoch`。客户端丢弃纪元不匹配的快照 ⇒ 前一会话的在途数据不会污染当前会话。

### 5.2 对端身份核验

主进程在握手时执行三道核验，缺一不可：

1. **对端真实 PID**：用 `GetNamedPipeClientProcessId` 读取管道对端的真实进程 ID，与启动 Host 时记录的期望 PID 比对。不一致 ⇒ `pid-mismatch`。**hello 载荷里的 `HostProcessId` 不作为信任来源**——载荷可以被伪造，管道句柄的真实属主不行。
2. **会话令牌**：hello 的 `SessionId` 必须等于当前令牌，否则 `session-mismatch`。
3. **协议主版本**：hello 的 `Major` 必须等于 `IpcProtocol.MajorVersion`，否则 `version-mismatch: host {x}, main {y}`。

客户端侧对服务端的信任来自「随机管道名 + 会话令牌 + 只连本机」这组约束：管道名每次会话重新生成且不可预测，ACL 只授予当前用户，客户端只连接 `"."`，服务端身份由命名空间与访问控制共同保证。

拒绝原因码是契约的一部分，必须可解释：

| 原因码 | 含义 |
|---|---|
| `expected-hello` | 首帧不是 hello |
| `invalid-hello` | hello 载荷不可解析 |
| `pid-mismatch` | 对端真实 PID 与期望不符 |
| `session-mismatch` | 会话令牌不符 |
| `version-mismatch: host {x}, main {y}` | 协议主版本不匹配 |

### 5.3 访问控制

- 管道 DACL **只授予当前登录用户的 SID** `ReadWrite | CreateNewInstance`，不授予 Everyone、Users 或 Administrators ⇒ 同一台机器上的其它用户无法连接。
- 管道为**单实例**：第二个创建者创建失败，不会出现两条并发会话。
- 客户端**只连接 `"."`**，禁止任何远程连接。
- Host 及其全部后代被绑定到作业对象（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）。作业句柄关闭或主进程退出 ⇒ Host 及其后代立即终止 ⇒ 不留孤儿进程。
- 作业对象建立失败必须按**致命错误**处理，不得回退到「不隔离地继续运行」——那会让主进程重新直接持有设备句柄，隔离的意义荡然无存。

---

## 6. 时效与背压语义

### 6.1 接收侧单调时钟

新鲜度一律以**接收侧的** `Environment.TickCount64` 计算：`MetricSample.SampleAgeMs`、`HardwareSnapshot.TimestampMonoMs`、客户端的过期阈值都基于本地单调钟。发送侧时钟只作为诊断参考，**不参与过期判定** ⇒ 两端时钟漂移不会影响新鲜度结论。

过期阈值为 `StaleThresholdMs = max(3000, RefreshMs * 3)`。

### 6.2 过期样本不重置新鲜度

接收侧维护的是「最后收到有效快照的本地时刻」。一个过期样本到达时，只更新质量标记（`SampleQuality.Stale`），**不得刷新新鲜度基准** ⇒ 断流不会被迟到的旧样本伪装成新鲜数据。

会话后的**第一份快照若在接收时已超过阈值，直接丢弃**：不置新鲜、不作为首样、计入过期计数。

### 6.3 快照原子替换

快照以不可变引用整体替换。读取方要么看到完整的一份旧快照，要么看到完整的一份新快照，**不得出现半更新**。由此保证 `HardwareSnapshot.BatchId` 单调递增，且不出现跨代次混合的样本集合。

### 6.4 缺口可观察

两类缺口分别计数并作为指标暴露：

| 缺口 | 依据 | 意义 |
|---|---|---|
| 帧序号缺口 | `IpcHeader.Sequence` | 传输层丢帧 |
| 采样批缺口 | `HardwareSnapshot.BatchId` | 采样层丢批 |

缺口是**可观测指标而不是错误**：正常运行（心跳穿插、快照与心跳交错）下两者都应为零；非零即说明链路或采样出现丢失。

### 6.5 迟到应答与会话结束

- 命令超时后到达的 `response` 按**未知请求**丢弃并计数（迟到应答计数），**不得与后续命令配对**。
- 会话结束时：清空快照缓存、重置序号与采样批基准、在途操作以 `host-session-ended:{reason}` 失败；非当前会话纪元的应答同样丢弃并计入过期会话应答计数。

---

## 7. 故障监督状态机

监督器 `HardwareHostSupervisor` 负责从「启动子进程」到「判定失效并重启」的完整循环。

### 7.1 状态集合

状态取值来自 `HardwareState`：

`Stopped`、`Starting`、`Ready`、`Degraded`、`Unresponsive`、`Stopping`

注意：**「断连」与「退避」不是独立状态**，而是由状态加上原因串表达——断连表现为 `Unresponsive` + `host-disconnected:*`，退避表现为 `Degraded` + `backoff-{n}s:{reason}`。任何按状态枚举统计断连次数的外部监控，都必须同时解析原因串。

### 7.2 状态迁移

| 起始 | 目标 | 触发条件 | 原因串 |
|---|---|---|---|
| — | `Starting` | 监督循环开始一轮会话 | `spawn` |
| `Starting` | `Ready` | 握手通过 | 无 |
| `Starting` | `Unresponsive` | 进程启动失败 / 握手超时 | `spawn-error:{ExceptionTypeName}`、`handshake-timeout` |
| `Ready` | `Unresponsive` | 心跳缺失 / 采样停滞 / 初始化未完成 / 对端 EOF / 协议违规 | `heartbeat-missed-3x2000ms`、`sampling-stalled`、`init-timeout`、`host-disconnected:eof`、`session-error:IpcProtocolException` |
| `Unresponsive` | `Degraded` | 进入退避等待 | `backoff-{n}s:{reason}` |
| `Degraded` | `Starting` | 退避到期，开始下一轮会话 | — |
| `Unresponsive` | `Degraded` | 连续失败达到暂停阈值，停止重启 | `crash-loop-paused` |
| 任意 | `Stopping` | 收到停止请求 | `stop-requested` |
| `Stopping` | `Stopped` | 子进程已退出 | `stopped` |
| `Stopping` | `Degraded` | 宽限期与强制终止后仍未退出 | `isolation-failed:cannot-terminate` |
| 任意 | `Stopped` | 监督循环正常结束 | `stopped` |

判定细节：

- **心跳判死只看心跳时戳**，不看快照；即使快照仍在照常发送，心跳缺失同样判死 ⇒ 采样线程阻塞与心跳线程分离的场景可以被识别为「采样停滞」。
- **采样停滞**：`now - LastProgressTick > SamplingProgressTimeout` ⇒ `sampling-stalled`。
- **初始化未完成**：握手后未见首个快照且超过 `InitializationTimeout` ⇒ `init-timeout`。
- **合法握手后的协议违规**走 `session-error:IpcProtocolException`（携带异常类型名）；**对端正常关闭**走 `host-disconnected:eof`。二者语义不同，不可合并。

### 7.3 超时期限

`HardwareHostSupervisorOptions` 的默认期限与语义：

| 项 | 默认值 | 语义 |
|---|---|---|
| `RefreshMs` | 1000 ms | 期望的采样节拍 |
| `HandshakeTimeout` | 5 s | 等待 hello 的上限，超时 ⇒ `handshake-timeout` |
| `HeartbeatInterval` | 2 s | 心跳周期 |
| `HeartbeatMissLimit` | 3 | 连续缺失的心跳数上限 ⇒ `heartbeat-missed-3x2000ms` |
| `SamplingProgressTimeout` | 10 s | 采样进度停滞上限 ⇒ `sampling-stalled` |
| `InitializationTimeout` | 60 s | 首个快照的最长等待 ⇒ `init-timeout` |
| `BackoffSequence` | 1 s / 3 s / 10 s | 逐轮退避时长 |
| `BackoffWindow` | 5 min | 失败计数的观察窗口 |
| `BackoffPauseThreshold` | 3 | 窗口内连续失败达到该值 ⇒ 暂停重启（`crash-loop-paused`） |
| `ShutdownTimeout` | 3 s | `shutdown` 后的宽限期，超时强制终止进程树；再等 3 s 仍未退出 ⇒ `isolation-failed:cannot-terminate` |
| `CommandTimeout` | 5 s | 命令应答上限（与 `IpcProtocol.DefaultCommandTimeout` 一致） |
| `HealthySessionResetInterval` | 30 s | 健康会话持续该时长后重置失败计数 |

### 7.4 恢复规则

- 重连成功后，客户端必须**重放最新期望配置与需求** ⇒ 采样参数不随崩溃丢失。
- 崩溃循环保护：达到暂停阈值后监督器不再重启 Host，主进程保持可用，并把原因暴露为 `crash-loop-paused`。
- 停止流程是幂等的且有界的：`shutdown` 命令 → 宽限期 → 终止进程树 → 二次等待；任一环节超时都必须落到明确状态，不得悬挂。

---

## 8. 降级原则

- **缺失优先**：单个指标不可用时以 `SampleQuality.Missing` 加 `MissingReason`（如 `no-sensor`）表达，而不是让整批失败。界面按缺失渲染占位，不得因单点缺失阻断其它指标。
- **过期优先于伪装**：数据超期必须降级为 `Stale`；`GetHealth()` 在快照超期时给出 `snapshot-stale:{ms}ms`；监督器处于 `Ready` 但尚无快照时，`GetHealth()` 必须报 `Starting` 而不是 `Ready`。
- **断线后拒绝新操作，快照按缺失处理**：会话结束时客户端清空快照缓存，读取接口返回空快照而不是抛出异常；在途操作以 `host-session-ended:{reason}` 失败，新命令被拒绝。⇒ 界面走缺失占位路径，**不得以陈旧值冒充实时值**。
- **降级原因必须区分「已定位」与「待诊断」**：固定语义码（`handshake-timeout`、`heartbeat-missed-*`、`sampling-stalled`、`init-timeout`、`host-disconnected:eof`）表示原因已经定位；携带异常类型名的码（`spawn-error:{ExceptionTypeName}`、`session-error:{ExceptionTypeName}`）表示只定位到失败点、仍需诊断。两类必须以不同形态编码，**不得一律折叠成「未知错误」**。
- **配置版本单序生效**：同 revision 且内容相同的配置请求可以合并；不同 revision 排队执行；排队中的请求被更新版本取代时，必须以 `superseded-by-revision:{N}` 显式回绝，而不是静默丢弃。只有确认生效才回写当前生效的 revision ⇒ 界面不得显示尚未生效的配置。
- **不在主进程建第二份硬件树**：隔离模式下主进程不得为同一设备再开一份设备树。纯软件查询（例如网络地址）由主进程自身提供，这不构成第二份硬件树。
- **安全模式在打开硬件前禁用**：主程序的 `--safe` / `--safe-mode` / `--disable-gpu` 作用于渲染路线；采集侧的探测开关由 `SamplingConfig.DiskProbePolicy` / `ControllerProbePolicy` 承载，并在硬件对象被打开前落定 ⇒ 关闭某类探测不需要先打开对应设备。

---

## 9. 验收不变量

### 9.1 故障维度

| 不变量 | 验证点 |
|---|---|
| 硬件访问异常、驱动死锁或 native 崩溃时，主进程继续响应 UI 与渲染 | 采样子进程被强杀后主进程窗口仍可交互 |
| 采样线程阻塞（心跳仍在）必须被判死 | 判死原因为 `sampling-stalled` |
| 心跳停止必须被判死 | 判死原因为 `heartbeat-missed-3x2000ms` |
| 连续失败达到阈值后停止重启 | 状态进入 `Degraded`，原因 `crash-loop-paused` |
| 子进程不可终止属于隔离失败 | 状态 `Degraded`，原因 `isolation-failed:cannot-terminate` |

### 9.2 协议维度

| 不变量 | 验证点 |
|---|---|
| 帧长度先校验后分配 | 超过 1 MiB 的帧被有界拒绝，且不产生超大内存分配 |
| 拆包与粘包被正确重组 | 分片写入的帧仍被完整解出 |
| 未知消息类型被拒绝并断连 | 非法 `Type` 导致会话终止 |
| 令牌不符、主版本不符、对端 PID 不符的连接被拒绝 | 伪造连接无法建立会话，拒绝原因可解释 |
| 未知命令被有界拒绝 | 应答 `unknown-operation:{op}`，会话继续 |
| 非当前会话纪元的消息被丢弃并计数 | 过期会话应答计数增长，且不污染当前配对 |

### 9.3 恢复维度

| 不变量 | 验证点 |
|---|---|
| 重连后重放最新配置与需求 | 重启后采样参数与重启前一致 |
| 会话结束使在途操作以可解释原因失败 | 失败原因形如 `host-session-ended:{reason}` |
| 迟到应答不与后续命令配对 | 迟到应答计数增长，后续配对不受影响 |
| 非幂等操作超时后不重复发起 | 新请求复用同一 `OperationId` 或返回忙 |
| 配置应用单序生效，被取代者显式回绝 | 取代原因 `superseded-by-revision:{N}` |

### 9.4 资源维度

| 不变量 | 验证点 |
|---|---|
| 连续启停不产生孤儿进程 | 作业对象覆盖 Host 及其后代 |
| 句柄与线程数不随运行时长持续增长 | 长时间反复启停后计数稳定 |
| 队列、集合、缓冲全部有界 | 快照槽位新替旧；控制队列、需求表、操作表、待应答表均超限即拒绝 |

### 9.5 一致性维度

| 不变量 | 验证点 |
|---|---|
| 快照整体替换，无半更新 | 并发读取不出现混合代次 |
| 采样批 ID 单调递增 | 快照 `BatchId` 严格递增 |
| 过期样本不重置新鲜度 | 首帧已过期不得置为新鲜 |
| 缺口可观察且正常运行下为零 | 帧序号缺口与采样批缺口在心跳穿插场景下均为零 |
| 两种采集模式语义一致 | 管道客户端与进程内适配器对同一组语义用例给出相同结论 |

### 9.6 部署维度

| 不变量 | 验证点 |
|---|---|
| Host 产物随主程序输出分发 | 由 `CopyHardwareHostToOutput` 复制到主程序输出目录 |
| Host 缺失时明确失败 | 原因码 `spawn-error:FileNotFoundException`，不得静默回退 |
| 主/子进程协议主版本必须匹配 | 不匹配以 `version-mismatch: host {x}, main {y}` 拒绝 |
| Host 子进程不含界面依赖 | Host 工程不引入 WinForms，不引用主程序集 |
| 隔离模式只能显式开启 | `--hardware-host` 或 `LITEMONITOR_HARDWARE_HOST=1|true`，首次读取后固定 |

---

## 10. 已知限制

以下均为当前客观存在的限制：

- **主进程在隔离模式下仍持有第二份硬件树。** 组合根无条件构造进程内采集器并打开 LHM 硬件树，因此「全系统只存在一份硬件树」尚未成立；驱动侧故障仍可能经由主进程自己持有的设备句柄产生影响。系统层面的「硬件对象唯一所有者」规则因此只对 Host 侧成立。
- **`--safe-mode` / `--disable-gpu` 不作用于采样子进程。** 它们只决定渲染路线，Host 子进程不接受这些参数，当前也不存在「安全模式 ⇒ 采样子进程不打开硬件」的行为。采集侧的探测开关只有 `SamplingConfig` 的存储/控制器探测策略一项。
- **Host 侧采样面只覆盖 CPU 与 GPU。** 采样子进程只启用 CPU 与 GPU 两类设备，存储、控制器、网络、内存等设备面不在其内；网络地址由主进程回退提供。
- **隔离模式默认不启用。** 必须显式给出命令行开关或环境变量，两种采集模式长期并存于代码库中。
- **状态枚举不足以直接区分断连与退避。** `HardwareState` 没有独立的断连或退避状态，二者只能通过原因串区分。
- **退避与崩溃循环暂停期间没有替代数据源。** 会话结束即清空快照缓存，界面只能得到空快照（缺失占位）；只有在会话仍然存活而采样停滞时，最后一份快照才会以 `Stale` 形态继续可用。
