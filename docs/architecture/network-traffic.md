# 网络流量采集与按地址排除

本文描述 LiteMonitor++ 实时网速的取数路径，以及「按远端地址排除流量」功能的架构：后端选型与理由、计数事件集合口径、ETW 载荷的二进制布局契约、排除规则的过滤层位置、生命周期状态机，以及这些口径成立的前提条件与已知限制。

适用范围是 `src/System/NetworkTraffic/`（事件源、载荷解码、规则编译、窗口聚合、生命周期服务）、`src/System/HardwareMonitor.cs` 中的后端创建与采集节拍接线、`src/System/HardwareServices/NetworkManager.cs` 中的目标网卡归属接线、`src/System/HardwareServices/HardwareValueProvider.cs` 中 `NET.Up` / `NET.Down` 的取值分支，以及 `src/Core/Settings.cs` 的 `IpTrafficExclusionEnabled` / `ExcludedTrafficIps` 两项配置与 `src/UI/Settings/SystemHardwarPage.cs` 的设置卡片。会话/日累计流量的计量口径见 [../specs/hardware-sampling.md](../specs/hardware-sampling.md)，硬件服务分层见 [hardware-services.md](hardware-services.md)。

---

## 1. 模块边界

| 组件 | 位置 | 职责 | 明确不负责 |
|---|---|---|---|
| `INetworkTrafficEventSource` | `src/System/NetworkTraffic/INetworkTrafficEventSource.cs` | 事件源抽象：生命周期（Start/Stop）、源状态、丢事件计数、解码成功的传输事件回调 | 不做排除判定，不做窗口统计 |
| `EtwNetworkTrafficEventSource` | `src/System/NetworkTraffic/EtwNetworkTrafficEventSource.cs` | 私有 ETW 会话生命周期、provider 启用、载荷解码、事件分类 | 不做统计，不做排除 |
| `TrafficPayloadReader` / `NativePayloadReader` / `ArrayPayloadReader` | `src/System/NetworkTraffic/TrafficPayloadReader.cs` | 载荷读取抽象与两种实现（原生内存零拷贝 / 托管字节数组） | 不解释载荷语义 |
| `IpKey`、`NetworkTrafficEvent`、`NetworkTrafficProtocol` / `NetworkTrafficDirection` / `NetworkTrafficEventKind` / `RetransmitPolicy` / `TrafficSourceStatus` | `src/System/NetworkTraffic/NetworkTrafficModel.cs`、`INetworkTrafficEventSource.cs` | 免分配的地址键、事件记录与契约枚举 | 不含行为 |
| `IpExclusionRules` / `IpRuleError` / `IpRuleCompilationResult` | `src/System/NetworkTraffic/IpExclusionRules.cs` | 规则解析、规范化、去重、上限、编译后的精确匹配集合、逐条错误 | 不接触事件 |
| `INetworkAdapterAttributor` / `NetworkAdapterResolver` / `NetworkAdapterInfo` | `src/System/NetworkTraffic/NetworkAdapterResolver.cs` | 本地地址 → 网卡索引映射，归属歧义判定 | 不做统计 |
| `NetworkTrafficFilter` / `FilteredNetworkSnapshot` | `src/System/NetworkTraffic/NetworkTrafficFilter.cs` | 同一窗口内的总量与排除量累计、归属判定、去重、快照生成 | 不管会话生命周期 |
| `NetworkTrafficExclusionService` / `NetworkTrafficExclusionStatus` | `src/System/NetworkTraffic/NetworkTrafficExclusionService.cs` | 需求启停、退避、规则与网卡热切换、窗口切分、状态机、快照发布 | 不做载荷解码 |

依赖方向单向：`EtwNetworkTrafficEventSource` → `INetworkTrafficEventSource` → `NetworkTrafficExclusionService` → `NetworkTrafficFilter` → `IpExclusionRules`。载荷解码逻辑只有一份（`EtwNetworkTrafficEventSource.TryDecode`），零拷贝路径与拷贝路径共用它，因此两条路径的解码结果必须逐字节一致。

---

## 2. 后端选型

### 2.1 为什么必须用 ETW 事件源

产品要提供的是「按远端地址排除某一部分流量后的实时上下行速率」。这要求一个能同时给出**远端地址**与**字节数**的逐事件数据源。

- 硬件服务层的网卡字节计数（原生适配器计数器与 LibreHardwareMonitor 的 Throughput 传感器）只有**总量**，不含任何逐对端信息。
- 性能计数器能给出的 SMB 估算量同样是总量估算，既不提供对端地址，也不能用来识别某一类应用的流量。

总量口径里没有「对端是谁」这一维 ⇒ 排除量无法从总量中推导。**排除量只能由一个独立的事件源产生**，且必须与总量同源，否则两个数字不在同一口径上，相减没有意义。

### 2.2 为什么是内核网络 ETW provider + 私有会话

- Provider：`Microsoft-Windows-Kernel-Network`，GUID `7dd42a49-5329-4832-8dfd-43d979153a88`。它是 manifest provider，可以在**普通私有 ETW 会话**上启用，不需要启动全局内核 logger。
- 会话名由本程序独占：`EtwNetworkTrafficEventSource.SessionNamePrefix` = `LiteMonitor.NetExclusion.Product.`，后接进程 ID 与启动时间戳，因此每次启动都是新名字。
- **绝不**复用、停止或影响其它会话：`NT Kernel Logger`、`EventLogger-StartTrace`、`LiteMonitor++_Golden_Session`（PresentMon / FPS 使用的会话）列在保留名单中，会话名与之冲突时直接拒绝启动。进程异常退出可能残留本程序自己的会话，`EtwNetworkTrafficEventSource.CleanupStaleSessions` 只清理带本程序前缀的会话。
- 采集这些事件需要管理员权限；主程序清单声明 `requireAdministrator`。

### 2.3 为什么用 TraceEvent 解析

`Microsoft.Diagnostics.Tracing.TraceEvent`（4.0.0，MIT）承担会话管理、事件分发与原生缓冲区访问。由此产生两条硬约束：

1. **`EnableProvider` 的返回值不可作为判据。** TraceEvent 4.0.0 对本 provider 返回 `false`，而事件照常到达。后端因此不得把 `false` 判定为「provider 未启用」；`EtwNetworkTrafficEventSource.ProviderEnableAcknowledged` 只作为诊断信息保留。可靠的失败判据是「会话在跑但收不到事件」，由服务层按 `NoEventsTimeoutSeconds` 超时判定。
2. **解码必须有零拷贝路径。** `TraceEvent.EventData()` 会为每个事件复制一份载荷，这是分配的主因（实测 227~423 B/事件）。热路径因此直接读 ETW 缓冲区的原生内存（`NativePayloadReader`），只有在零拷贝解码自检失败时才回退到拷贝路径（`ArrayPayloadReader`）。

### 2.4 载荷偏移标定

零拷贝路径要求知道用户数据相对 `TraceEvent.DataStart` 的字节偏移。实机标定结果是 `PayloadOffsetFromDataStart = 0`，即 `DataStart` 就是用户数据起点。标定不成立时解码自检会失败并自动回退到拷贝路径，功能不会静默出错。

---

## 3. 计数事件集合口径

### 3.1 事件分类契约

`EtwNetworkTrafficEventSource.IsDataEvent(task, opcode)` 是计数事件集合的唯一判定入口。

| task | opcode | 事件 | 分类（`NetworkTrafficEventKind`） | 计入上下行字节 | 理由 |
|---|---|---|---|---|---|
| 10（TCP） | 10 | SEND | `Data` | 是 | 新数据 |
| 10 | 11 | RECV | `Data` | 是 | 新数据 |
| 10 | 14 | RETRANSMIT | `Retransmit` | **否** | 该字节在对应的 SEND 事件里已经计过一次，重复计入会高估 |
| 10 | 18 | TCPCOPY | `CopyUp` | **否** | 拷贝路径（copy-up）不是新数据；把它计入会重复计数（实测多算 54%） |
| 10 | 12 / 13 / 15 / 16 / 17 | CONNECT / DISCONNECT / ACCEPT 等连接级事件 | 不解码 | 否 | 连接级事件没有字节口径 |
| 11（UDP） | 42 | SEND | `Data` | 是 | 新数据 |
| 11 | 43 | RECV | `Data` | 是 | 新数据 |
| 11 | 49 | 连接级事件 | 不解码 | 否 | 连接级事件没有字节口径 |

口径约定：

- 「计入」= 同时计入 `SourceSendBytes` / `SourceReceiveBytes` 与 `ExcludedSendBytes` / `ExcludedReceiveBytes`（命中排除规则时）。
- 重传字节与拷贝字节即便被排除规则命中也**不计入**任何一侧；它们单独上报为 `RetransmitBytesObserved` 与 `CopyEventsObserved`，只用于诊断，不参与速率计算。
- `RetransmitPolicy` 定义重传是否计入总量的两种取值（`ExcludeRetransmitFromTotal` / `IncludeRetransmitInTotal`）；产品路径固定使用 `ExcludeRetransmitFromTotal`——`NetworkTrafficExclusionService` 的全部规则下推点都传入该值。
- 非数据事件被单独计入 `ControlEventsObserved`，用于观察连接级事件而不混入字节口径。

### 3.2 聚合口径契约

- **同源相减**：`filteredSendBytes = sourceSendBytes - excludedSendBytes`，`filteredReceiveBytes = sourceReceiveBytes - excludedReceiveBytes`；速率 = 过滤后字节 ÷ 真实间隔秒。总量与排除量必须来自同一事件源、同一个窗口、同一张网卡，三者缺一则相减无效。
- **一个事件最多被排除一次**。重复规则、多条规则命中同一事件，都只扣一次。
- **一个事件最多被计入一次**。事件身份由时间戳、协议、方向、远端地址、本地地址、远端端口、本地端口、字节数共同确定；去重表上限 `MaxDedupEntries = 50000`，表满后不再做去重判定，重复事件会被再次计入，去重表在窗口开启与规则换代时清空。
- **归属不唯一的事件不参与统计**。无法唯一映射到网卡的事件计入 `UnattributedEvents` 并被丢弃；若该本地地址同时出现在多张网卡上，另置 `AdapterAmbiguous`。
- **非选中网卡的事件不参与统计**，计入 `OtherAdapterEvents`。
- **窗口不完整时快照无效**，而不是报 0 流量：`FilteredNetworkSnapshot.IsValid` 为 false 并给出 `InvalidReason`。
- **非负钳制只是边界保护**。`filteredSendBytes` / `filteredReceiveBytes` 为负时钳到 0 并置 `NegativeClamped`；钳制不掩盖窗口或网卡不匹配，那由 `IsValid` 与 `UnattributedEvents` 表达。

---

## 4. 载荷二进制布局契约

`Microsoft-Windows-Kernel-Network` 的事件载荷是二进制结构，manifest 没有命名字段，布局由受控流量标定并逐字段验证：

```
IPv4 : PID(4) size(4) remoteAddr(4)  localAddr(4)  remotePort(2) localPort(2) [seq(4) conn(4)] pad(8)
IPv6 : PID(4) size(4) remoteAddr(16) localAddr(16) remotePort(2) localPort(2) [seq(4) conn(4)] pad(8)
```

| 字段 | 偏移 | 字节序 | 说明 |
|---|---|---|---|
| PID | 0 | 小端 uint32 | 发起方进程 ID，不参与统计 |
| size | 4 | 小端 uint32 | 传输层负载字节数；**唯一**参与字节统计的字段 |
| remoteAddr | 8 | 网络字节序 | IPv4 为 4 字节，IPv6 为 16 字节 |
| localAddr | 8 + addrLen | 网络字节序 | 同上 |
| remotePort | 8 + 2×addrLen | 网络字节序 uint16 | |
| localPort | 8 + 2×addrLen + 2 | 网络字节序 uint16 | |
| seq / conn | 端口之后 | — | 存在与否由载荷长度决定，**不参与**解码 |

不变量：

- **两个方向上 #1 恒为远端、#2 恒为本地。** SEND 事件的远端是目的地址，RECV 事件的远端是源地址。排除规则据此按「远端」匹配。
- **载荷长度同时决定地址族与是否带 seq/conn**：28 = IPv4 基础，36 = IPv4 + seq/conn，52 = IPv6 基础，60 = IPv6 + seq/conn。长度不在这四个值之内的载荷一律解码失败。
- **IPv4-mapped IPv6 归一化**：`::ffff:a.b.c.d` 与 `a.b.c.d` 是同一个匹配目标（`IpKey.FromBytes` 把高 64 位为 0 且低 64 位高 32 位为 `0xFFFF` 的地址折成 IPv4）。
- **用户数据起点相对 `DataStart` 的偏移为 0**（`PayloadOffsetFromDataStart`）。
- 解码失败（长度非法、`DataStart` 为零、拷贝路径也失败）时事件被计入 `EventsUndecodable` 并丢弃，绝不按 0 字节参与统计。

解码入口有两个，语义相同：`EtwNetworkTrafficEventSource.TryDecodePayload`（拷贝载荷，供测量工具与测试使用）与内部 `TryDecode`（两条路径共用）。

---

## 5. 按远端地址排除的过滤层位置

### 5.1 过滤在聚合侧，不在采集侧

- **采集侧（`EtwNetworkTrafficEventSource`）**只做三件事：开/关会话、解码载荷、把解码成功的事件原样上抛。它不查询规则、不做归属判定、不决定事件是否计入字节。
- **聚合侧（`NetworkTrafficFilter`）**承担全部判定：去重、事件类别分流（`CopyUp` / `Retransmit`）、本地地址→网卡归属、选中网卡筛选、排除规则匹配、总量与排除量累计、窗口快照生成。

这样分层的理由是排除量必须可被观测。若在采集侧按规则直接丢弃事件，被排除的字节量就无从产生，也就无法在同一口径下与总量相减；同理，归属歧义、网卡切换、重传与拷贝事件的分流也都需要在能看到「该事件全部字段」的地方做决策。事件源保持与规则无关，因此规则集热切换不触碰 ETW 会话。

### 5.2 排除规则与流量统计的交互

| 规则维度 | 约定 |
|---|---|
| 匹配对象 | 远端地址。SEND 按目的地址匹配，RECV 按源地址匹配 |
| 地址族 | 精确 IPv4 / IPv6；IPv4-mapped IPv6 已归一化，与等价 IPv4 规则命中同一目标 |
| 单播与多播 | 分属两个集合，单播远端规则与多播组地址不混淆 |
| 命中次数 | 一个事件最多被排除一次 |
| 规则上限 | `IpExclusionRules.MaxRuleCount = 256`；超限时整个规则集标记 `LimitExceeded` 并**整体不可用**，不得半生效 |
| 支持的写法 | 仅精确地址。CIDR / 前缀、通配符、IPv6 scope id、端口或 host:port 形式、主机名均逐条拒绝并给出原因 |
| 错误报告 | `IpRuleError(LineNumber, Text, Reason)` 逐条返回；被拒绝的行绝不静默忽略 |
| 匹配开销 | 匹配在 `IpKey` 上做哈希查找；热路径不做字符串格式化，不做 DNS 查询 |
| 规则换代 | `IpExclusionRules.Version` 单调递增；规则或目标网卡变化时前一窗口的字节作废，下一窗口重新建立基线 |

### 5.3 排除作用在哪些数字上

- 排除**只改变实时网速**。`NET.Up` / `NET.Down` 在排除被请求时优先消费过滤快照；此时 `HardwareValueProvider` 仍会让 `NetworkManager` 解析出目标网卡（只读传感器值，不触发额外采集），以保证自动选网卡能选出目标、归属可解析。
- 过滤快照不可用（准备中、失败、降级、无完整窗口）时，`NET.Up` / `NET.Down` 报**缺失**，绝不显示 0，也绝不回退到未过滤的过期采样值——回退等于假装排除已生效。
- 手动选卡且存在 NET 传感器缓存时，**不得**绕过过滤快照直读缓存。
- **会话/日累计流量沿用原有口径**：原生网卡字节计数加 `IgnoreSmbTraffic` 的 SMB 估算路径不受排除影响，不发生二次扣减。`IgnoreSmbTraffic` 不是通用 IP 过滤器，不会把某类流量归作 SMB。
- 网络值不走通用「保留过期值」兜底：过期值必须受样本时效约束，网卡停更时过期值不得被当成新样本。

---

## 6. 生命周期与状态机

### 6.1 状态

`NetworkTrafficExclusionState`：`Disabled`（功能关闭）、`Idle`（已开启但无有效规则或无需求）、`Preparing`（会话启动中，或处于预热/首窗口）、`Running`（排除已生效）、`Degraded`（ETW 报告丢事件，或存在无法归属的事件，数据不完整）、`Failed`（后端启动失败、规则有误、网卡归属歧义、收不到事件）。

对外状态快照 `NetworkTrafficExclusionStatus` 携带状态、生效标志、原因键、明细、规则版本、规则条数、会话代次、丢事件数、不可解码事件数、已应用事件数、无法归属事件数与逐条规则错误。`Effective` 只有在存在**有效**快照时才为 true。

原因键（`Menu.IpExclusionState*`）分别对应：未启用、待命、准备中、运行中、降级、配置有误、没有有效规则、网卡归属歧义、后端启动失败、预热中、收不到事件。

### 6.2 会话准入

创建 ETW 会话必须同时满足：功能已开启、规则集可用、无网卡归属拒绝、目标网卡已识别、存在实时网速需求（`NotifyRealtimeDemand` 的存活期 `DemandTimeoutSeconds = 30`）、启动退避已到期。任一条不满足 ⇒ 不创建任何会话；功能关闭或没有有效规则时不保留任何额外采集。

启动失败按**单调时钟**退避：首次 `InitialBackoffSeconds = 5`，每次失败翻倍，上限 `MaxBackoffSeconds = 300`。消费线程异常退出（事件源转为 `Failed`）同样进入退避重试，不会停在半死状态。

`Stop` 可恢复；`Dispose` 最终关闭，并一并释放事件源及其 ETW 会话；释放顺序是先停排除后端，再释放硬件。

### 6.3 窗口切分与代次隔离

- 窗口长度 `DefaultWindowSeconds = 1.0` 秒，由共享采集协调器限频（≤ 1 次/秒）推进，不引入第二套采集轮询。
- 超过 `MaxWindowSeconds = 30` 秒的窗口按「长间隔」丢弃并重建基线，不补造尖峰（睡眠/挂起场景）。
- 会话启动后 `WarmupGuardSeconds = 2.0` 秒为预热保护期；新会话开头存在数百毫秒到一秒多的事件空窗，该窗口丢弃，不产出速率。
- 每个窗口带 `SessionGeneration`。事件回调线程只把当前代次的事件交给过滤器；非当前代次的迟到事件被丢弃，绝不写进新窗口。
- 规则换代或目标网卡切换时前一快照作废，下一窗口重新建立基线，因此不会出现尖峰或负值。

### 6.4 线程与锁

事件回调运行在 ETW 消费线程上，回调路径**不得**获取服务锁（服务侧的加锁顺序是 service → source，反向获取会形成锁反转）；会话代次用无锁读写。过滤器自身持锁完成计数聚合。

---

## 7. 网卡归属

ETW 载荷里没有网卡 ID，归属只能靠**本地地址映射**。

- `NetworkAdapterResolver` 基于 `GetAdaptersAddresses` 建立「本地地址 → 网卡索引」映射；该调用失败时退回托管 API（`NetworkInterface`），功能降级但不崩溃。映射按需重建（`ResolverRebuildIntervalSeconds`），地址热变更后刷新。
- 同一本地地址出现在多张网卡上 ⇒ **归属歧义**，必须拒绝该配置，不得猜测。`NetworkManager.UpdateExclusionTarget` 把目标网卡上的歧义地址汇总为拒绝理由。
- 目标网卡是 VPN / 隧道类型（`NetworkInterfaceType.Tunnel`）⇒ 拒绝，隧道上的归属没有可靠依据。
- 归属解析不唯一（本地地址未知）的事件不参与统计，计入 `UnattributedEvents`。

---

## 8. 成立条件与已知限制

### 8.1 口径成立的前提

排除生效（`Effective == true`）要求全部满足：

1. 进程具备管理员权限；
2. 规则集可用：无错误行、至少一条有效规则、未超 256 条上限；
3. 目标网卡已识别，且不存在归属歧义、不属于 VPN/隧道类型；
4. 存在实时网速需求；
5. ETW 会话已建立，且预热保护期之后确实收到了事件（超过 `NoEventsTimeoutSeconds = 10` 仍无事件 ⇒ `Failed`）；
6. 已完成至少一个完整有效的测量窗口。

### 8.2 已知限制

- **协议覆盖**：只统计 TCP 与 UDP 的传输层负载。ICMP 及其它协议、链路层开销、协议头开销都不在口径内。
- **口径层级**：`size` 是传输层负载字节，不是链路层字节。TCP 分段、大发送 offload / GRO、UDP offload 会使事件数与字节分布偏离网卡视角；它与网卡原始速率是不同口径。
- **同源约束**：排除量必须与总量来自同一事件源，不能与原生网卡字节计数混算，也不与网卡原始速率做差。
- **丢事件**：ETW 报告丢事件（`EventsLost > 0`）时状态降级为 `Degraded`，该窗口数值不完整但仍被使用。
- **解码失败**：长度不在 28 / 36 / 52 / 60 之内的载荷被计入 `EventsUndecodable` 并丢弃。
- **回退路径**：零拷贝解码自检失败时回退到拷贝路径，回退计数可观测；两条路径都失败才算不可解码。
- **环回与本机投递**：发往本机地址的流量由本地协议栈处理，不经过物理网卡，但仍会产生事件；这类事件按其本地地址归属参与统计。
- **多播**：多播组地址作为独立匹配目标，与单播远端规则不混淆；同一事件只可能被排除一次。
- **网卡切换**：目标网卡切换或地址热变更后需要重建基线，期间快照无效。
- **规则能力**：只支持精确 IP；不支持 CIDR / 前缀、通配符、域名（不做名字解析）、端口、进程维度。
- **作用范围**：排除只作用于实时上下行速率与选中网卡；会话/日累计流量、`IgnoreSmbTraffic` 语义、其它网卡的流量都不受影响。
- **安全边界**：只读取本程序自己创建的 ETW 会话，不采集包内容、不扫描局域网、不修改防火墙规则、不安装抓包或拦截驱动。

---

## 9. 相关文件

- `src/System/NetworkTraffic/EtwNetworkTrafficEventSource.cs`
- `src/System/NetworkTraffic/INetworkTrafficEventSource.cs`
- `src/System/NetworkTraffic/TrafficPayloadReader.cs`
- `src/System/NetworkTraffic/NetworkTrafficModel.cs`
- `src/System/NetworkTraffic/IpExclusionRules.cs`
- `src/System/NetworkTraffic/NetworkAdapterResolver.cs`
- `src/System/NetworkTraffic/NetworkTrafficFilter.cs`
- `src/System/NetworkTraffic/NetworkTrafficExclusionService.cs`
- `src/System/HardwareMonitor.cs`
- `src/System/HardwareServices/NetworkManager.cs`
- `src/System/HardwareServices/HardwareValueProvider.cs`
- `src/Core/Settings.cs`
- `src/UI/Settings/SystemHardwarPage.cs`
- `tests/LiteMonitor.NetworkExclusionProbe/`（受控流量与载荷口径标定用的测量工具，与产品程序集分离）
