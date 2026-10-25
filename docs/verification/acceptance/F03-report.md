# F03 验收记录 —— 网络值时效、失败恢复与自动选卡（#96，回归 #287）

## 1. 标识

- 工作包：P-F03
- 分支：`fix/F03-network-sample-freshness`
- 基线 SHA（开工时 HEAD）：`d52c0ab`（master）
- 提交 SHA 列表（`git log --oneline d52c0ab..HEAD`）：
  - `e76721e` `fix(F03): 网络样本带时效与设备代次，旧值不再无限期冻结回退 (#96)`
    （`src/System/HardwareServices/NetworkSampleFreshness.cs` 新建、`NetworkManager.cs`、`HardwareValueProvider.cs`）
  - `72f3c34` `test(F03): 网络时效全链路、读一万次不刷新 TTL、拔卡恢复、换卡无负流量`
    （`tests/LiteMonitor.Tests/NetworkSampleFreshnessTests.cs` 新建、`SamplingCacheTests.cs`、`TrafficDeltaTests.cs`）
  - `8983e97` `docs(F03): 验收记录 —— 网络值时效、失败恢复与自动选卡（#96）`
    （`docs/verification/acceptance/F03-report.md` 新建）
- 完成日期：2026-10-25
- 执行者：worker 会话 `mvs_e49dfeadf93c4de0917086821974bb12`（P-F03 外派单）

## 2. 覆盖与结论

- 覆盖 issue：**#96**（网络检测运行一段时间后数据卡住）；回归保护 **#287**（切换 Wi-Fi 后局域网 IP 未更新）。
- 结论：**■ 代码层验收通过**
- 允许关闭的 issue 与条件：
  - **#96**：代码层可关闭（冻结回退已从"永不失效"改为"保留期内有效、到期即缺失"）。
    **但仍需实机场景验收**（见 §6「未覆盖场景」）：真实网卡链路变化（Wi-Fi 断开重连 / 网线拔插 / VPN 建立）下，
    LHM 侧到底停不停更、停更后显示的是 `--` 还是残留旧值，需要用户在真机上确认。
  - **#287**：本次**未改动** `#287` 的修复路径（`NetworkChange.NetworkAddressChanged` → `_shouldResetAdapters` → IP/网卡缓存重置），
    仅在旁边新增了样本时效逻辑；相关代码路径逐行未动，列为回归保护。

### 根因与修复对照

| 层 | 修复前 | 修复后 |
|---|---|---|
| L1 `NetworkManager.ReadNetworkSensor` / `GetBestValue` | 读失败回退 `lastValidMap[key]`，**永不失效** | 回退只允许用"保留期内"的样本；到期即缺失 |
| L2 `HardwareValueProvider` 通用 `_lastValidMap` 兜底 | `key.StartsWith("NET")` 也走通用兜底 | NET 走 `skipGenericFallback`，不再进通用兜底 |
| L3 启动期缓存 `ComputeStartupValue` | 直接读 `_lastValidMap` | NET 走 `GetNetworkFallback`（受样本时效约束） |
| L4 采集中读取（抢锁失败分支） | 无锁读 `_lastValidMap` 旧值 | NET 走 `GetNetworkFallback`（受样本时效约束） |

补充修复：自动模式重评分改为「存活 + 采样成功 + 有效流量」；读取失败才触发**有限重选**（≥3 秒一次）；
空闲（0 流量）不算坏网卡；手动模式设备消失**保留选择**并显示不可用；设备删除/首选变化/硬件重载立即作废样本与速率差分基线。

## 3. 用例与命令（可从干净检出复现）

| 命令 | 目的 | 结果（发现数/通过/失败） |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | 构建 | 0 错误（警告数与基线同量级，新增文件 0 警告） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build` | 主测试 | 见 §3.1 |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/…csproj -c Debug --no-build` | GPU 非性能测试 | 见 §3.1 |
| `dotnet test tests/LiteMonitor.Updater.Tests/…csproj -c Debug --no-build` | 更新器测试 | 见 §3.1 |
| 新增用例（27 条，见 §5） | 红→绿证明 | 新增前 5 条核心断言在旧实现下会失败（旧实现无 TTL、`GetBestSample` 不存在） |

### 3.1 自验输出摘要

```
dotnet build LiteMonitor.sln -c Debug --nologo
  → 0 个错误

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build
  → 已通过! - 失败: 0，通过: 1804，已跳过: 7，总计: 1811

dotnet test tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj -c Debug --no-build
  → 已通过! - 失败: 0，通过: 19，已跳过: 0，总计: 19

dotnet test tests/LiteMonitor.Updater.Tests/LiteMonitor.Updater.Tests.csproj -c Debug --no-build
  → 已通过! - 失败: 0，通过: 73，已跳过: 0，总计: 73
```

基线（`d52c0ab`）主测试为 **1777 通过 / 7 跳过 / 0 失败**；本包后为 **1804 通过 / 7 跳过 / 0 失败**
（净增 27 = 本包新增用例数；跳过数未变）。

### 3.2 验收断言逐条对照

| 验收断言 | 对应用例 | 结果 |
|---|---|---|
| 正值 → 失败 → TTL 边界 → 缺失 → 恢复全链路 | `NetworkSampleFreshnessTests.正值_读取失败_保留期内旧值_到期后缺失_恢复后重新出值` | ✅ |
| 读缓存 1 万次不刷新 TTL | `NetworkSampleFreshnessTests.读一万次不会刷新样本寿命`、`SamplingCacheTests.反复读取网络值不会刷新样本寿命` | ✅ |
| 拔卡恢复 | `NetworkSampleFreshnessTests.拔掉旧网卡后另一张网卡可以恢复` | ✅ |
| 无负流量 / 尖峰 | `TrafficDeltaTests.反复切换网卡不会产生负增量或流量尖峰`、`TrafficDeltaTests.换卡后第一轮采样不会把另一张卡的累计量算进来` | ✅ |
| 有效 0 恒为 0 | `NetworkSampleFreshnessTests.真实0流量是有效样本而不是缺失`、`SamplingCacheTests.合法的0会被缓存_不会被当成缺失`（既有，未改断言） | ✅ |
| 手动选卡不被改写 | `NetworkSampleFreshnessTests.手动选卡不会被自动改写成另一张网卡`、`…手动选卡设备消失时保留选择并显示不可用` | ✅ |
| 采样调用频率不随 UI 刷新倍增 | `NetworkSampleFreshnessTests.高频读取不会触发额外的网卡Update`（200 轮读取后 `UpdateCount == 0`） | ✅ |
| 空闲 ≠ 坏网卡 | `NetworkSampleFreshnessTests.空闲网卡不会被当成坏网卡也不会触发换卡` | ✅ |

## 4. 环境

- OS / 版本：Windows 11 专业版 10.0.26300（执行机与本包工作树同机）
- DPI / 显示器拓扑：本包全部用例为**无 UI**的纯逻辑用例（不涉及 DPI / 显示器）
- GPU / 驱动：RTX 3080 / RTX 3080 Ti（驱动 32.0.16.1088）——本包不触碰 GPU 路径
- 关键依赖版本：LibreHardwareMonitor 0.9.6（`IHardware`/`ISensor` 只用**公开基类**做桩）
- 时钟：全部用可注入 `TimeProvider` 假时钟，**无 `Thread.Sleep`**、无真实等待

## 5. 新增测试清单（27 条）

### 5.1 新文件 `tests/LiteMonitor.Tests/NetworkSampleFreshnessTests.cs`（22 条）

| 用例 | 验证了什么 |
|---|---|
| `正值_读取失败_保留期内旧值_到期后缺失_恢复后重新出值` | #96 全链路：Fresh → 读失败（Stale 旧值）→ TTL 边界 → Missing → 恢复后重新 Fresh |
| `读一万次不会刷新样本寿命` | 读缓存 1 万次后旧值仍会过期（读缓存不刷新数据寿命） |
| `同一采样轮次内重复读取只更新值不刷新成功时间` | 同一采集轮次内重复读只更新值，不刷新成功时间 |
| `真实0流量是有效样本而不是缺失` | 真实 0 流量是 Fresh，不是缺失（0 是有效数据） |
| `空闲网卡不会被当成坏网卡也不会触发换卡` | 空闲不触发换卡；两张都空闲时不来回跳 |
| `拔掉旧网卡后另一张网卡可以恢复` | 拔卡后保留期内显示旧值（Stale），到期后由另一张网卡接管 |
| `重选是有限的_不会因为持续失败而无限切换` | 持续失败时重选有节流；到期后明确缺失而不是冻结 |
| `手动选卡不会被自动改写成另一张网卡` | 手动选中的网卡读失败时仍保留手动选择 |
| `手动选卡设备消失时保留选择并显示不可用` | 手动设备消失：显示不可用且**保留**用户选择 |
| `设备删除后旧样本立即作废` | 设备删除后旧样本立即作废，不冒充当前读数 |
| `首选网卡变化会立即失效样本并推进设备代次` | 首选变化 → 样本作废 + 设备代次推进 |
| `硬件重载会清空样本登记` | 硬件重载清空样本登记 |
| `高频读取不会触发额外的网卡Update` | 取值路径不触发 `hw.Update()`（采样频率不受 UI 刷新影响） |
| `采集层更新目标网卡会推进采样轮次` | 采集层真的更新了目标网卡 → 样本轮次推进（寿命刷新点） |
| `保留窗口最多三个采样周期` | 保留窗口 = 3 × 采样周期；期内 Stale、到期 Missing |
| `慢刷新用户的保留窗口上限是十五秒` | 保留窗口上限 15 秒 |
| `采样周期按配置刷新周期推导` | 采样周期按配置刷新周期推导，非法值回退 1 秒 |
| `配置刷新周期决定实际保留窗口` | 5 秒慢刷新 → 实际保留窗口 15 秒 |
| `旧值只会被一次性采纳且寿命有界` | 兼容桥：旧值只被一次性采纳为 Stale，寿命有界 |
| `作废之后的旧值不会被重新采纳` | 作废后旧值不会被重新捡回来（冻结回退永不失效的根因） |
| `Provider层的网络值超过保留期后变成缺失` | Provider 层（UI/Web 真实取值路径）旧值到期后显示缺失 |
| `Provider层不会把网络旧值写回通用最后有效值字典` | 网络值不再进通用 `_lastValidMap`（单一事实来源） |

### 5.2 扩展 `tests/LiteMonitor.Tests/SamplingCacheTests.cs`（+3 条，既有断言未改）

| 用例 | 验证了什么 |
|---|---|
| `网络旧值在保留期内仍然是有效读数` | 保留期内旧值仍是有效读数（0 不被当成缺失） |
| `网络旧值超过保留期后不再被发布` | 跨采集代次：超过保留期后网络旧值不再被发布 |
| `反复读取网络值不会刷新样本寿命` | 1 万次跨代次读取不刷新样本寿命 |

> 既有 `合法的0会被缓存_不会被当成缺失` 的**断言未改**（仍断言 0 是有效值而非缺失）；只把 fixture 的
> `NetworkManager` 改为与 Provider 共用同一个假时钟（否则 TTL 断言无法用假时钟驱动）。

### 5.3 扩展 `tests/LiteMonitor.Tests/TrafficDeltaTests.cs`（+2 条）

| 用例 | 验证了什么 |
|---|---|
| `反复切换网卡不会产生负增量或流量尖峰` | 反复换卡：切换后首轮增量为 0，稳态增量等于真实差值，无负值/尖峰 |
| `换卡后第一轮采样不会把另一张卡的累计量算进来` | 新设备不继承旧设备累计量 |

## 6. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| 真实网卡链路变化（Wi-Fi 断开重连 / 网线拔插 / VPN 建立）后，LHM 传感器是停更（NaN）还是**继续返回同一个旧值** | 真机：开面板 NET.Up/NET.Down，拔插网线 / 断连 Wi-Fi，对照任务管理器观察 | 用户 |
| 停更后面板从旧值变成 `--` 的**实际等待时间**（应为 ≤ 3 个采样周期） | 真机：慢刷新用户（5 秒/次）应为 ≤ 15 秒 | 用户 |
| 拔掉当前网卡后自动切到另一张网卡的**实机**表现（选优顺序、是否来回跳） | 真机：双网卡（有线 + Wi-Fi） | 用户 |
| 手动选卡后拔掉该网卡，面板是否显示缺失且设置页仍保留该项 | 真机：设置页选网卡 → 拔线 | 用户 |
| #287 回归：切换 Wi-Fi 后局域网 IP 是否仍会更新 | 真机：切换 Wi-Fi → 检查设置页/Web 的 IP | 用户 |
| 真实 ETW（IP 排除）开启时的网速口径与本次时效逻辑的叠加 | 真机：开启 IP 排除 + 造流量 | 用户 |
| 休眠 / 唤醒、锁屏 / 解锁后的网络样本行为（唤醒后第一轮可能显示缺失） | 真机：休眠 10 分钟后唤醒 | 用户 |
| 多网卡 + 虚拟网卡（VPN/WSL/Hyper-V）共存时的选优顺序 | 真机：开 VPN | 用户 |

> 说明：本包全部用例使用**假网卡桩 + 假时钟**，不接触驱动、不访问外网；
> `NetworkManager.ProcessUpdate` 用例只会**只读枚举**本机网卡（假网卡名不会命中任何真实网卡，不会调用 `GetIPStatistics()`）。

## 7. 偏离与遗留

### 7.1 与派发单的偏离

| 项 | 派发单要求 | 实际 | 理由 |
|---|---|---|---|
| `src/System/HardwareMonitor.cs` | 编辑 ≤30 行且仅限网络取值分支 | **0 行改动** | 时钟与策略都能在 `NetworkManager` / `HardwareValueProvider` 内部闭环：`NetworkManager` 已持有 `TimeProvider`，采样周期由 `Settings.RefreshMs` 推导，无需改动采集层。**这同时把与 P-F08a 的冲突面降到 0**，F08a 可直接 rebase。 |
| `src/System/HardwareServices/HardwareSamplingCoordinator.cs` | 在改动清单内 | **0 行改动** | 协调器已保证「单次在途 + 周期门限」，本包只在其边界内新增"样本轮次"推进，不改协调器本身即满足"硬件采样调用频率不得随 UI 刷新倍增"。 |
| 保留期与"旧值"口径 | 计划写"最多保留 3 个采样周期、上限 15 秒" | 实现为 **min(3 × 采样周期, 15 秒)** | 计划原文是"3 个采样周期、**上限** 15 秒"，取二者较小值才符合"上限"语义。 |
| 旧值兼容桥 | 计划未提 | `_lastValidMap` 中"从未登记过"的网络旧值会被**一次性采纳**为 Stale（寿命有界） | 不采纳会破坏既有 `SamplingCacheTests.合法的0会被缓存_不会被当成缺失`（它直接预置 `_lastValidMap["NET.Up"]=0`）。采纳是**一次性**的、质量标 Stale、寿命有界，且作废后不可再采纳（有专门用例守住）。 |
| 禁改清单 | 不改 `ci.yml` / `release.yml` 阈值 | 未改 | 按要求由协调者在批次集成时统一上调。 |

### 7.2 建议协调者处理（**本包未自行修改**）

1. **CI 阈值**：`MIN_TEST_TOTAL` 需从 `1876` 上调到 **≥ 1804 + 其余两个项目的用例数之和**，
   `MAX_SKIPPED_TESTS` 不得放宽（本包跳过数未变，仍为 7 / 1）。
2. **`HardwareMonitor.cs` 冲突面**：本包 0 行改动 → P-F08a 无需 rebase 即可合并；批次集成顺序仍建议 **F03 先并**（保持派发单约定）。
3. **与 P-F11 的衔接**：本包已提供 `NetworkSampleQuality.Fresh/Stale/Missing` 三态与
   `NetworkManager.Samples` 公共入口，但**面板/网页目前仍只区分"有值/无值"**——
   Stale（保留期内的旧值）在界面上仍显示为数字。F11「缺失/过期状态」可据此把 Stale 渲染成可辨识状态（如灰显/`--`），无需再改本包的取值层。
4. **相邻问题（非本包，建议归属）**：
   - `NetworkManager.GetCurrentIP()` 的 30 秒 IP 缓存窗口（`IsIPCheckFresh`）在多网卡热插拔时可能滞后；
     #287 的 `_shouldResetAdapters` 已在网络变更事件里清掉 `_staticIPCache`，但 `MarkIPChecked` 的窗口仍在，
     建议由后续包统一复核（**未在本包改动**）。
   - `NetworkManager.MatchNativeNetworkAdapter` 的 10 秒重匹配节流，在网卡刚插入时会让原生计数晚 10 秒才接上
     （既有行为，本包未动）。

### 7.3 风险与限制

- **采样轮次依赖采集层**：样本寿命只在「目标网卡真的被 `hw.Update()` 过」时刷新。
  若 `HardwareUpdateRequirements.NeedNet == false`（用户没启用任何 NET 指标且无 Web 需求），
  网卡不会被采集，NET 取值将在一段时间后显示缺失——这是"没有数据就不显示"的正确语义，但**面板仍显示 NET 指标时必然 `NeedNet == true`**，不会触发。
- **换卡瞬间的空窗**：自动选优切到另一张网卡后，新网卡的样本要等下一次采集轮次才登记，
  期间该值显示缺失（≤1 个采集周期）。这是刻意的：**宁可显示缺失，也不显示另一张卡的旧值**。
- **"一卡缺方向"的极端组合**：若两张网卡中一张只有单向传感器且都空闲，选优可能在两者间来回。
  已用「分数相同保持当前选择」把绝大多数情况收敛住，但该极端组合仍属既有实现的遗留行为，本包未彻底消除。
