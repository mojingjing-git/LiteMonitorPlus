# #137：内存“已提交”监控实施计划

## 1. 目标与口径

新增可选择的“内存已提交”监控项，面板、任务栏和 Web 显示一致，支持百分比及当前提交量/提交上限。

[需求来源](https://github.com/Diorser/LiteMonitor/issues/137)。Windows `PERFORMANCE_INFORMATION` 的 `CommitTotal`、`CommitLimit` 以页为单位，`PageSize` 给出字节换算；提交上限可随分页文件变化。依据：[结构文档](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information)、[GetPerformanceInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getperformanceinfo)。

```text
CommittedBytes = CommitTotal × PageSize
CommitLimitBytes = CommitLimit × PageSize
CommitLoadPercent = 100 × CommitTotal / CommitLimit
```

这表示系统提交压力，与物理内存使用率互补。不能表述为“精确预测应用何时崩溃”：分页文件增长、进程地址空间及其他限制都会影响实际分配。

### 产品决定

- 新指标键：`MEM.Commit.Load`，默认面板/任务栏隐藏；用户可开启它并关闭原内存项实现替换显示。
- `MEM.Load` 继续表示物理内存，不改变旧配置、旧历史数据及 VRAM 指标的含义。
- 新指标默认归属 HOST，与现有内存项相邻；Key 缓存路径和 UIGroup 回退路径保持一致。
- 百分比模式显示例如 `79.0%`；容量模式显示例如 `76.0 / 96.0 GB`，分母为当前提交上限。
- 尊重单项 `%` 单位覆盖、隐藏单位及自动单位约定，复用格式规则；若沿用全局容量模式，必须对新键显式分流。

## 2. 已检查的实现与风险点

| 文件 | 当前行为 | 需要做的事 |
|---|---|---|
| `src/System/HardwareServices/PerformanceCounterManager.cs` | Available MBytes 与静态物理总量计算 MEM.Load | 新提交量 reader 独立于计数器初始化 |
| `src/System/HardwareServices/HardwareValueProvider.cs` | 代次缓存和启动 TTL；MEM.Load 有旧回退 | 新键消费同代次提交快照 |
| `src/Core/MetricUtils.cs` | 凡 MEM/VRAM 视为 Memory，并用 DetectedRamTotalGB 换算 | 为提交内存建立明确的格式分支，不能乘物理内存总量 |
| `src/Core/Settings.cs` | MEM.Load 特判 HOST，默认其他 MEM 键归 MEM | 两条分组路径统一支持新键 |
| `src/Core/SettingsHelper.cs` | 默认监控项及旧配置迁移 | 新项幂等补齐，保留旧排序与用户标签 |
| `src/System/WebServer/LiteWebServer.cs` | 内存主项及数值/单位格式化 | 新指标原始 pct 与文本使用同一快照 |
| `src/Core/HardwareHistoryLogger.cs` | LoadKeys 固定记录旧内存序列 | 本版保持旧格式和物理内存序列 |
| `tests/LiteMonitor.Tests/MetricUtilsMemoryUnitFormatTests.cs` | 覆盖容量/百分比单位兼容 | 保留并新增提交内存的同类测试 |

以上路径相对于仓库根 `G:/TOOL/LiteMonitor-fork`。实现前重新核对冻结基线。

## 3. 数据设计

建议新增小模块 `src/System/HardwareServices/CommittedMemoryReader.cs`，封装 `GetPerformanceInfo` / `K32GetPerformanceInfo`。不需要管理员权限或硬件驱动；P/Invoke 失败仍必须正常处理。

建议快照包含：`CommittedBytes`、`LimitBytes`、`LoadPercent`、采集代次、单调时间戳、有效性/错误状态。字节保留 64 位整数，比例使用 double，最后适配现有 float 取值接口。

- `SIZE_T` 按指针宽度声明，`DWORD` 为 32 位；校验结构布局和大小。
- 使用 checked 或显式溢出检测进行页数到字节换算。`PageSize=0`、`CommitLimit=0`、结构或 API 返回失败均为缺失。
- 提交上限每轮重新读取，不能缓存为启动时物理内存大小。
- 合法的提交量 0 是有效值。异常大于上限的样本保留诊断，展示上限按现有进度条规则钳制，不覆盖真实字节值。
- 新 reader 不依赖 `UseWinPerCounters` 或其 `IsInitialized`；该开关关闭也应能使用系统提交量。
- 快照由 HardwareMonitor 的真实采集代次发布，同一代次所有使用者复用。启动阶段若提前可用，复用现有启动 TTL，不能由绘制频率触发 Win32 调用。

格式化从发布后的结构化快照取当前量和上限，不再通过百分比乘静态总量反推字节。formatter、Web、任务栏不能各自重新调用系统 API。

## 4. 实施任务

### M0：冻结行为和失败状态

- 确认新键、HOST 分组、默认隐藏、模式和单项单位行为。
- 定义 reader 可注入接口及不可变快照；记录缺失/有效 0 的区别。
- 明确历史格式本版不扩展，新的提交量不会写入旧 `MEM.Load` 列。

交付：接口说明和公式/单位测试表，尚不接 UI。

### M1：原生采集及代次接线

- 实现 native adapter 和纯计算层，支持注入固定原始值/API 失败。
- 在现有采集入口按需求生成提交快照；Web 已鉴权需求保持其全指标语义。
- 区分物理内存需求与提交量需求，不能让 `IsAnyEnabled("MEM")` 的前缀匹配把新项绑死在性能计数器开关或额外 LHM 内存更新上。
- 接入 `Get("MEM.Commit.Load")`，失败返回 null，不回退为物理内存率。
- 同代次读多次只采一次；异常、重载、配置切换及释放不泄漏旧代次结果。

交付：reader、快照、采集/取值测试；不增加第二个轮询定时器。

### M2：配置迁移与统一格式

- 增加一个默认隐藏监控项，重复迁移不重复添加。
- 保留现有 MemoryDisplayMode、用户自定义标签、排序、单位及可见性。
- 针对提交内存实现百分比和当前量/上限格式，避免影响 MEM.Load、GPU.VRAM。
- 单位从同一倍率得到分子分母，避免 `76 GB / 98304 MB` 或容量数配 `%`。
- 为快照未就绪、API 失败和过期统一显示缺失状态。

### M3：端到端显示与语言

- 监控项设置能找到“内存已提交”，可独立用于面板和任务栏。
- Web、任务栏提示和当前可用的 GDI/GPU 显示适配使用相同 presenter；不把全部显示逻辑塞入新渲染后端。
- 9 个语言包同步添加标签、短标签和“提交量/当前提交上限”的说明。
- 新旧内存项同时显示时标签清晰，旧历史图仍明确表示物理内存。

## 5. 必须新增的自动化测试

| 输入/场景 | 必须断言 |
|---|---|
| 物理内存 32 GiB；提交 48/64 GiB | 75%，不能算成 150%；容量 48/64 |
| 提交上限从 64 变 80 GiB，提交量不变 | 下一代次比例和分母同步变化 |
| PageSize=4096，页数很大 | 64 位换算正确，不经过 float 丢失字节精度 |
| 0 提交、合法上限 | 有效 0，不标为缺失 |
| API 失败、0 上限、0 页大小、溢出 | null/缺失，不伪造 0 或套物理内存 |
| UseWinPerCounters=false / 计数器未初始化 | 新 API 路径仍可用 |
| UI 100 ms + 多个 Web 读者 | 每代次 native read 一次 |
| 容量模式＋单项 `%`、空单位、自动单位 | 数值和单位配对，旧内存单位测试全通过 |
| 旧配置重复迁移 | 新项只出现一次，旧项及排序不变 |
| Web、面板、任务栏、场景适配 | 同一快照口径一致，缺失不显示 0 |

建议测试文件：`CommittedMemoryReaderTests.cs`、`CommittedMemoryFormattingTests.cs`、`CommittedMemoryWiringTests.cs`。

至少做两条变异：把分母改成物理总内存；把读取移到每次 Get。对应测试必须红。正常物理内存与 VRAM 格式用例提供阳性对照。

## 6. 实机验收

1. 用任务管理器“性能→内存→已提交”的分子、分母对照，记录采集时差和显示舍入。建议误差门槛：稳定负载下百分比不超过 1 个百分点，字节差异按同时刻系统调用核实。
2. 同时显示物理率和提交率，确认两者可以不同。
3. 普通用户与计数器开关关闭场景均验证。
4. 分页文件动态变化用假快照覆盖；本任务不自动更改用户分页文件配置。
5. 使用适量已有负载，不以耗尽系统提交量或制造真实 OOM 作为验收方法。

## 7. 完成条件

- 正确系统口径、百分比/分数、旧配置兼容和所有显示出口均完成。
- 新路径不增加高频采集、硬件初始化或驱动依赖。
- 原测试及新增回归强制重建后通过；记录提交和实际测试总数。
- 真实系统 API 已对照；未测的环境和分页文件变化明确列出。
- 提交按“reader/测试 → 接线/配置 → 展示/语言”组织，可独立回滚本需求。
