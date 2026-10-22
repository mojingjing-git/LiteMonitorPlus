# #610：按指定 IP 排除实时网速实施计划

## 1. 目标和第一版边界

[原始需求](https://github.com/Diorser/LiteMonitor/issues/610) 指出，约 200KB/s 的局域网 RTP 音频被计入上传速度，希望按设备 IP 排除。

第一版交付：用户配置明确 IP 地址，在支持的采集环境下，从 `NET.Up`、`NET.Down` 实时网速中排除对应流量。UDP/RTP 是必测项，不能只支持 TCP。

### 固定的产品决定

- `IpTrafficExclusionEnabled=false`，`ExcludedTrafficIps=[]`；关闭/无有效规则时，不启动额外事件采集。
- 首版支持精确 IP，不扩展 CIDR、域名、自动整个内网过滤、进程黑名单或拦截网络包。
- 支持 IPv4、IPv6 解析与规范化；多播/链路本地地址的实际归属和覆盖在 N0 验证。无法支持的类型应在配置阶段明确拒绝并解释，不能接受后不生效。
- 发送按远端目的地址匹配，接收按远端源地址匹配。多播的组地址作为单独匹配目标，不能与单播远端规则混淆。
- **只改变实时网速。** 本次不改写会话/日累计历史，也不从累计字节再扣一次 IP 流量；设置说明“累计流量沿用原有口径”。
- 保留 `IgnoreSmbTraffic` 及其现有累计估算语义。该开关不是通用 IP 过滤器，不自动把 RTP 归作 SMB。
- 功能记录用户所选 IP 规则，不记录包内容、逐包日志或持续跟踪对端历史。

累计流量也按 IP 排除需另立任务，届时必须统一统计基准并解决与 SMB 的交集，不能叠加两个估算扣减。

## 2. 当前架构事实

| 路径 | 当前用途 | 实施约束 |
|---|---|---|
| `NetworkManager.ProcessUpdate` | 在既有硬件采样节奏中更新网卡 | 不增加第二套 LHM Update |
| `NetworkManager.AccumulateTraffic` / `TrafficAccumulator` | 原生累计字节、真实间隔、SMB 估算、历史累计 | 首版保持累计语义 |
| `HardwareValueProvider.GetValue` | NET 键可直接读取 `_manualSensorCache` | 开启过滤时必须在该捷径之前接入新快照 |
| `PerformanceCounterManager.GetEstimatedSmbBytes` | 按计数器估算 SMB 累计量 | 不能提供对端 IP，也不能用来过滤 RTP |
| `SystemHardwarPage` | 网卡和 SMB 配置 | 增加规则及后端状态说明 |

原生网卡字节计数/LHM 速率不包含逐对端信息。不能仅根据对端“处于内网”或端口号，从总量猜出应排除多少。

## 3. 技术路线与必须先过的门槛

首选验证 Windows ETW 网络事件方案。官方 TCP/UDP 事件含地址与包大小等字段，存在 IPv4/IPv6 事件；但该事实不证明在所有系统上都能可靠恢复所选适配器的完整网速。

来源：[TCP 事件](https://learn.microsoft.com/en-us/windows/win32/etw/tcpip-typegroup1)、[UDP IPv4](https://learn.microsoft.com/en-us/windows/win32/etw/udpip-typegroup1)、[UDP IPv6](https://learn.microsoft.com/en-us/windows/win32/etw/udpip-typegroup2)。会话权限及系统 logger 限制见 [StartTrace](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracea)。

### N0：独立验证探针，先不接产品开关

建议目录：`tests/LiteMonitor.NetworkExclusionProbe/`，与产品源码隔离，并在项目 glob 中明确排除。

1. 固定 Windows 版本、权限、ETW provider、事件版本及解码库版本；如果使用 TraceEvent，核对官方包、许可、.NET 8 与发布方式。
2. 实测 IPv4/IPv6 的 TCP/UDP 发送和接收。不能把断开、重连、拷贝、重传等事件都当新增数据，必须明确计数事件集合及重传口径。
3. 明确事件 `size` 的层级，验证 offload、合包、分段以及批处理会影响什么。以同一事件源生成总量和排除量，不能把不同层级的 ETW 字节直接从原生网卡总字节硬扣。
4. 确认本地/远端地址、方向和选中适配器的可靠关联。官方基础事件没有直接承诺完整网卡 ID；若靠本地地址映射，验证多网卡、相同地址、VPN、隧道、IPv6 scope 和地址变化。
5. 验证能收到 issue 中的 UDP/RTP，包括需要的多播情况；用可控发送端序号/字节数和独立采集结果对照。
6. 记录非管理员的真实失败码、缺事件/事件丢失、会话冲突、睡眠恢复和关闭行为。
7. 测量空闲、200KB/s RTP、较高速 TCP、多小包压力下的 CPU、分配、内存、事件丢失。

### 进入正式实施的门槛

- 目标 UDP 场景可见、方向正确、协议覆盖清楚；所选网卡可可靠识别或明确限制并拒绝不支持的配置。
- 同口径总量/排除量在可控流量下可核验，能报告事件丢失。
- 关闭后没有本程序创建的会话或后台消费任务残留；不干扰 PresentMon 或其他程序的 trace。
- 确定权限、事件覆盖、包体积与性能预算，形成 `network-exclusion-backend-decision.md`。

若 ETW 不能满足这些条件，N0 的交付是“不可行证据及替代方案”，不是一个默默估算的产品开关。替代的签名抓包驱动路线需要明确依赖、安装/权限和打包方案后另行选定；不得在实施中自动安装 Npcap/WinDivert 或开发拦截驱动来绕过门槛。

## 4. 正式模块设计（N0 通过后）

建议在 `src/System/NetworkTraffic/` 新增小型模块：

| 模块 | 职责 |
|---|---|
| `IpExclusionRules` | IP 解析、规范化、去重、上限、编译后的精确匹配 |
| `INetworkTrafficEventSource` | 可注入的事件与采集状态，屏蔽 ETW 细节 |
| `NetworkTrafficFilter` | 在同一源/适配器/时间窗中累计总量和排除量 |
| `FilteredNetworkSnapshot` | 不可变的区间字节、速率、规则版本、会话代次和有效状态 |
| `NetworkTrafficExclusionService` | 需求、启停、退避、规则热切换和快照发布 |

字段/名称可由集成 owner 调整，但必须保留规则版本、所选适配器、单调时间窗、数据源和有效状态。

### 统计约定

```text
filteredSendBytes = sourceSendBytes - excludedSendBytes
filteredReceiveBytes = sourceReceiveBytes - excludedReceiveBytes
rate = filteredIntervalBytes / realIntervalSeconds
```

同一个事件只计一次；多条重复规则也只排除一次。字节在窗口内使用 64 位计数，非负输出钳制仅作边界保护，不能掩盖窗口/适配器不匹配。

第一版开启时显示的是经验证事件源的 TCP/UDP 统计口径，不承诺与网卡链路层原始速率完全相同。N0 若发现协议覆盖不足以满足产品网速定义，必须扩大采集能力或阻止该模式发布。

## 5. 实施任务

### N1：规则与过滤核心

- IPv4/IPv6 解析、规范化、重复去重；统一 IPv4-mapped IPv6 的匹配策略。
- 拒绝含端口、域名、通配符、CIDR、非法/不支持地址；逐条标注错误，不把错误行静默忽略为“已生效”。
- 限制规则数量，例如 256 条；用编译后的集合匹配，禁止逐包字符串格式化和 DNS 查询。
- 双向统计、时间窗切分、适配器选择、单调时间和计数归零均用纯输入测试。

### N2：后端生命周期与状态

- 按需求启动：功能开启、有有效规则，且面板/任务栏或合法 Web 需要 NET 实时数据。只显示 DATA 累计项不需要启动过滤后端。
- 独立唯一会话名，只管理自己创建的会话；不复用/停止全局 Kernel Logger 或 PresentMon 会话。
- 回调只做必要解析和计数；有界状态/缓冲，不在包线程写文件或更新 UI。
- 启动失败按单调时间退避；无权限、事件缺失、接口不明、丢包和规则更新有明确状态。
- 规则/适配器变化原子切换快照，下一窗口重新建立基线；旧回调不能写到新窗口。
- Stop 可恢复，Dispose 最终关闭；启动准入与释放共享同步，等待/撤销的有界语义明确。复用已修复 FPS 生命周期的教训，不能只在入口检查 disposed。

### N3：实时取值接线与 UI

- 开启且有效时，`HardwareValueProvider` 的 NET.Up/Down 优先消费新快照，覆盖手动网卡传感器直读路径。
- 同一代次 UI/Web 共用快照；序列化和绘制不创建会话，也不触发采样。
- 准备/错误/降级状态不伪装成过滤后的 0。建议该模式显示缺失及“IP 排除未生效”；用户关闭功能后恢复旧数据源。
- 首窗口有完整有效区间后再给速率；规则变化、睡眠长间隔、网卡切换不出现尖峰或负值。
- 保留原始累计路径，界面说明实时网速与累计范围的区别；不要改写既有历史。
- 设置提供开关、多行 IP 列表、验证结果、当前状态和权限提示。默认不提权；若需要权限，用户明确发起已有授权流程。
- 9 个语言包同步。Web 可选增加过滤是否生效/状态字段，数值仍遵守原 JSON 类型约定。

## 6. 自动化验收

| 场景 | 必须断言 |
|---|---|
| IP A 的 UDP 上传、IP B 的 TCP 上传 | 仅 A 被排除，B 完整保留 |
| 对应下载、IPv6、大小写/规范化等价 IP | 方向和地址匹配正确 |
| 重复规则、同包满足多条规则 | 只扣一次 |
| 两个网卡含相同远端 IP | 只作用于选中且可识别的网卡 |
| 无效 IP、空规则、关闭功能 | 校验明确；不创建采集会话 |
| 规则热更新、旧事件迟到、Stop/Start/Dispose | 代次隔离，不复活，不泄漏 |
| 无权限、后端启动失败、事件丢失 | 明确非生效状态，不伪造净流量 |
| 手动选择网卡＋NET 传感器缓存存在 | 不绕过过滤快照 |
| UI 100 ms＋多个 Web 读者 | 不增加事件会话或 LHM 更新次数 |
| IgnoreSmbTraffic 开/关＋IP 排除开/关 | IP 不改变旧累计结果，不发生二次扣减 |

建议测试：`IpExclusionRulesTests.cs`、`NetworkTrafficFilterTests.cs`、`NetworkExclusionLifecycleTests.cs`、`NetworkExclusionWiringTests.cs`。

变异至少覆盖 UDP 分支删除、恢复 NET 手动传感器直读旁路、移除旧代次检查；对应测试必须变红。保留无规则、未命中 IP 的阳性对照。

## 7. 实机与性能验收

最低矩阵：指定设备约 200KB/s UDP/RTP、混合未排除下载/上传、IPv4/IPv6、多网卡、VPN/不可归属场景、开关切换、睡眠恢复、非管理员。

可控流量稳定运行 60 秒以上，用相同字节口径核对；建议目标：指定测试流量排除误差不超过 5%，未排除流量误差不超过 5%。对低速残留同时设绝对量检查，具体阈值由 N0 证据确定并冻结，不能验收时临时放宽。

性能建议门槛在 N0 冻结：关闭时零新增会话/逐包处理；200KB/s 典型场景增量整机 CPU 目标不超过 0.1 个百分点，内存有界。高包率记录 CPU、队列、丢事件和退出时长，超过可接受预算先优化或缩小支持范围，不掩盖为“硬件差异”。

验收不采集包内容、不扫描内网、不自动修改防火墙。测试只访问明确受控的端点。

## 8. 完成条件与提交顺序

1. N0 可行性和支持矩阵已通过，权限/层级/网卡归属没有未说明的假设。
2. 规则与过滤核心 → 生命周期后端 → 设置/取值接线 → Web/语言及实测证据，分别提交。
3. 当前全量和新测试强制重建通过；UDP/RTP 已真实验证，不能只报模拟测试。
4. 无真实 UDP 设备或权限时，标记实验未完成，不宣称 #610 已解决；#137/#166 可照常独立交付。
5. 关闭功能即可恢复既有实时网速路径，设置和数据迁移可逆，累计历史没有被重算。
