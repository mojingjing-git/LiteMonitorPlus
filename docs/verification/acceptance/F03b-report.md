# F03b 验收记录 —— 网络地址缓存窗与网卡重匹配节流收口（#96 关闭条件补齐）

## 1. 标识

- 工作包：P-F03b（F03 的收尾补口，不是新功能）
- 分支：`fix/F03b-network-cache-window`
- 基线 SHA（开工时 HEAD）：`bbbfdaf`（master，已含 W1 批次集成，F03 已合并）
- 提交 SHA 列表（`git log --oneline bbbfdaf..HEAD`）：
  - `b911315` `fix(F03b): IP 地址缓存窗与重匹配节流改为事件驱动优先 + 时间窗兜底`
    （`src/System/HardwareServices/NetworkManager.cs` 改；
    `NetworkAddressCache.cs` / `NetworkChangeSource.cs` / `NetworkAdapterSource.cs` 新建）
  - `b8d2b21` `test(F03b): 热插拔立即刷新、事件丢失时间窗兜底、事件风暴有界、双网卡不串`
    （`tests/LiteMonitor.Tests/NetworkCacheWindowTests.cs` 新建，18 条用例）
  - `0f4a9b2` `docs(F03b): 验收记录 —— 网络地址缓存窗与重匹配节流收口（#96 关闭条件补齐）`
    （`docs/verification/acceptance/F03b-report.md` 新建）
  - `def716b` `docs(F03b): 验收记录补齐提交 SHA 列表与改动文件清单`（仅补本文件）
  - `e7f9b49` `docs(F03b): 验收记录补齐最终验收轮数据、已知 flaky 说明与精确行号锚点`（仅补本文件）
- 完成日期：2026-10-25
- 执行者：P-F03b 外派执行会话（worktree：`G:\TOOL\LiteMonitor-fork\.worktrees\f03b`）

## 2. 覆盖与结论

- 覆盖 issue：**#96**（关闭条件中的"缓存过期与实际网络切换/恢复通过"未闭环部分）
- 结论：**☑ 代码层验收通过**（真实网卡热插拔场景**待实机**，见 §6）
- 允许关闭的 issue 与条件：**#96 暂不可关闭**。代码层已把两个固定时间窗收口为
  "事件驱动优先 + 时间窗兜底"，但 §6 的实机场景尚未验证；按总则 §2「mock 结果不得用于关闭硬件特有问题」，
  **mock 结果不得用于关闭 #96**。

## 3. 覆盖与结论（语义对照）

### 3.1 两个时间窗的改造前后对照

| | 改造前（固定秒数） | 改造后（事件驱动优先 + 时间窗兜底） |
|---|---|---|
| **IP 地址缓存窗** | `NetworkManager.cs` 内 `_lastIPCheckTimestamp` + `IsIPCheckFresh()`，**固定 30 秒**；`GetCurrentIP()` 上方的注释却写"带 10秒 缓存"（与实际不符）。只订阅了 `NetworkChange.NetworkAddressChanged`，**可用性变更不触发失效** ⇒ Wi-Fi↔有线切换 / 链路 down-up 后最长 30 秒仍返回**旧网卡的 IP**。 | ① `NetworkChange.NetworkAddressChanged` **与** `NetworkChange.NetworkAvailabilityChanged` 任一到达 → 立即推进**网络环境代次** → 缓存值立即作废 → 下一次读取**立刻**重新取值（不等 30 秒）；② 事件丢失时，距上次成功取值满 **30 秒**仍会兜底重取；③ 两者都**有界**（单轮最多 3 次真正取值 + 最短 1 秒重试间隔 + 代次推进去抖）。 |
| **重匹配节流** | `MatchNativeNetworkAdapter()` 内 `state.HasMatchAttempt` + **固定 10 秒**；网卡消失（`GetIPStatistics()` 抛异常）、计数失配（`NativeMismatch`）时只是把 `NativeAdapter` 置空，下一次匹配仍被 10 秒节流压住。 | ① **失效信号**（网络变更事件 / 计数读取失败 / 匹配失配）→ `ForceRematch` 置位 → **立即**允许重匹配；② 连续失败但在快速重试预算（3 次）内 → 每 **1 秒**重试一次（退避阶梯第一级）；③ 预算用尽且无新失效信号 → 退避到 **10 秒**长节流，约束"无失效信号时不要反复空转"。 |

> 两个时间窗的**数值都未被缩短**（30 秒仍是兜底窗，10 秒仍是长节流）。改动的是**触发语义**：
> 固定秒数从"唯一的放行条件"降级为"事件丢失时的兜底"。

### 3.2 代码约束七条自检

| 约束 | 落实情况 |
|---|---|
| 1. 不回退 fork 已有能力 | `[Fix #287]` 链路保留：`InfoService.cs` 未改动，仍监听 `NetworkChange.NetworkAddressChanged` 并立即刷 UI；`NetworkManager` 侧**新增**了可用性变更监听（叠加，非替换）。F03 的四层时效框架（`NetworkSampleFreshness.cs`）**一字未改**。 |
| 2. 修复含迁移 | 旧字段 `_staticIPCache` / `_lastIPCheckTimestamp` / `_hasIPCheck` / `_shouldResetAdapters` 全部删除，改为 `NetworkAddressCache`（值 + 代次）+ `NetworkRefreshGate`（窗口 + 预算）。旧构造签名保持**向后兼容**（只追加可选参数）。 |
| 3. 0 是有效数据 | 新增 `NetworkAddressStatus`：`Missing`（网卡在但无 IPv4，**有效事实**）与 `Failed`（枚举网卡抛异常，**故障**）可区分；两者都**不返回** `0.0.0.0` 占位。`GetCurrentIPResult()` 是带状态的完整结果，`GetCurrentIP()` 保持既有契约（无地址时返回 null）。 |
| 4. 恢复有界 | ① 地址刷新：单轮最多 `DefaultMaxAttemptsPerRound = 3` 次真正取值，两次之间 ≥ 1 秒；代次推进去抖（1 秒内同波事件只推进一次）。② 重匹配：失效信号立即放行，之后 1 秒快速重试 ≤ 3 次，再退避 10 秒。 |
| 5. UI 线程只投递恢复请求 | 本包不触碰 UI 线程模型；恢复全部发生在 `NetworkManager` 的读取路径内（UI 只"读"）。 |
| 6. 时钟一律可注入 | `NetworkAddressCache` / `NetworkRefreshGate` / `NetworkManager` 全部使用 `TimeProvider`（`_time`）；用例使用 `FakeTimeProvider`，**零 `Thread.Sleep`**。 |
| 7. 应用层 catch 不冒充原生边界 | 边界判定在**原生网卡枚举/计数**处完成（`INetworkAdapterSource` / `INetworkAdapterSnapshot`），应用层只消费状态；`Failed` 状态明确来自枚举异常，不与"无地址"混同。 |

## 4. 用例与命令（可从干净检出复现）

环境变量：

```powershell
$env:NUGET_PACKAGES = 'G:\TOOL\LiteMonitor-fork\.nuget-packages'
$env:DOTNET_CLI_HOME = 'G:\TOOL\LiteMonitor-fork\.dotnet-cli-home'
Set-Location 'G:\TOOL\LiteMonitor-fork\.worktrees\f03b'
```

| 命令 | 目的 | 结果（发现数/通过/失败） |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo --no-incremental` | 全解构建 | **0 错误**（529 警告全部为仓库既有警告；本包新增/修改文件 **0 警告**） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build --nologo` | 主测试（CI 等价环境） | **1954 发现 / 1953 通过 / 0 失败 / 1 跳过** |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/… -c Debug --no-build --nologo` | GPU 非性能 | **19 发现 / 19 通过 / 0 失败** |
| `dotnet test tests/LiteMonitor.Updater.Tests/… -c Debug --no-build --nologo` | Updater | **73 发现 / 73 通过 / 0 失败** |
| `合计` | | **2046 发现**（开工基线 2028 **+18**；注意 master 期间已前进到 `dd04fdd`，其基线为 2029，集成后应为 **2047**） |

> 主测试工程共跑 **4 轮**（含一次 `--no-incremental` 全量重建后的最终验收轮）：
> ① 1947 通过 / 7 跳过 / 0 失败（本机默认环境）→ ② 1953 通过 / 1 跳过 / 0 失败（CI 等价环境）→
> ③ **1952 通过 / 1 跳过 / 1 失败**（§4.2 的已知 flaky 偶发）→ ④ 重跑 **1953 通过 / 1 跳过 / 0 失败**。
> 除 ③ 那一类外，**本包新增的 18 条用例在每一轮都全绿**。

### 4.1 跳过用例说明（重要）

本机默认环境（未设 `LITEMONITOR_PLUGIN_REPO`）实测 **7 条跳过**：
`MarketE2ETests.端到端_拉索引_安装_校验_卸载`（1 条，联网 E2E，门控 `LITEMONITOR_E2E=1`）
+ `MarketIndexContractTests`（6 条，门控 `LITEMONITOR_PLUGIN_REPO`）。

按 CI 等价环境（补设 `LITEMONITOR_PLUGIN_REPO=<worktree>/tests/fixtures/plugin-repo`，
与 `.github/workflows/ci.yml:125` 一致）复测：

| 环境 | 发现 | 通过 | 失败 | 跳过 |
|---|---|---|---|---|
| 本机默认（无 `LITEMONITOR_PLUGIN_REPO`） | 1954 | 1947 | **0** | 7 |
| **CI 等价**（有 `LITEMONITOR_PLUGIN_REPO`） | **1954** | **1953** | **0** | **1** |
| 协调者基线（master `bbbfdaf`） | 1936 | 1935 | — | 1 |

- 两种环境**发现总数相同**（1936 → 1954，**+18**），差异只在通过/跳过分布，**与本包改动无关**。
- CI 门禁 `MAX_SKIPPED_TESTS: 1` 在 CI 等价环境下**仍然成立**（跳过数仍为 1）。
- ⚠️ 集成提示（阈值）：本包开工时 master 的门禁是 `MIN_TEST_TOTAL: 2028`；
  **期间 master 已前进到 `dd04fdd`**（F08a 集成），两个 workflow 的阈值现为 **`MIN_TEST_TOTAL: 2029`**。
  集成本包后需要上调到 **2029 + 18 = 2047**（ci.yml 与 release.yml 逐字一致），
  `MAX_SKIPPED_TESTS: 1` **不得放宽**。
  本包自身**一字未动**两个 workflow。

### 4.2 已知 flaky（与本包无关，未修）

第 ③ 轮全量跑出现 **1 条**失败：

- `LiteMonitor.Tests.ReviewMarketPersistenceRegressionTests.ExtendedJournalMetadata_IsValidatedBeforeRecovery`
  （`tests/LiteMonitor.Tests/ReviewMarketPersistenceRegressionTests.cs:437`）

- 归因：**派发单已记录**的既有 flaky —— `src/Plugins/PluginMarket.cs:1038` 的
  `File.Move(stagePath, targetPath, overwrite: true)` 偶发返回 Win32 `ERROR_ACCESS_DENIED`
  （`UnauthorizedAccessException: Access to the path is denied`）；受害用例随机轮换。
- 证据链：
  1. 本包**未触及**插件市场代码（改动文件清单见 §9，全在 `src/System/HardwareServices/` 与 `tests/LiteMonitor.Tests/NetworkCacheWindowTests.cs`）；
  2. 同一份提交的第 ② 轮与第 ④ 轮**都全绿**（0 失败），第 ③ 轮才偶发 ⇒ 间歇性；
  3. 派发单记录协调者已在**纯净基线 `d52c0ab` 上复现**同一现象 ⇒ 与任何新改动无关。
- 处理：按派发单要求**重跑一次确认**（第 ④ 轮 0 失败），**不修**（属主题市场工作线）。
- 本包新增的 18 条用例在 4 轮中**始终全绿**。

### 4.3 红 → 绿证明（变异测试）

由于本包是在既有实现上"改窗口语义"，先写测试再改实现无法产生"编译期红"，
因此采用**变异验证**：把实现逐项改回旧语义，确认对应用例**确实变红**，再恢复。

| 变异 | 模拟的旧行为 | 变红的用例 | 结果 |
|---|---|---|---|
| **A** | `OnNetworkChanged` 只处理 `Address`，忽略 `Availability`（= 旧实现只订阅 `NetworkAddressChanged`） | `网卡热插拔后立即刷新_不等三十秒缓存窗`、`WiFi切有线后立即刷新到有线的地址`、`网络变更后不再返回旧网卡的IP`、`双网卡并存时地址归属于选中的设备`、`网络变更事件后立即允许重新匹配` | **13 通过 / 5 失败** |
| **B** | `MatchNativeNetworkAdapter` 恒定按 10 秒节流（不看 `ForceRematch`） | `原生网卡失效后立即重新匹配_不等十秒节流`、`网络变更事件后立即允许重新匹配` | **16 通过 / 2 失败** |
| **C** | `NetworkRefreshGate` 去掉事件驱动分支，退回**纯固定 30 秒窗** | `网卡热插拔后立即刷新_不等三十秒缓存窗`、`地址变更事件后立即刷新_不等三十秒缓存窗`、`WiFi切有线后立即刷新到有线的地址`、`双网卡并存时地址归属于选中的设备`、`刷新闸门在事件驱动下立即放行` | **13 通过 / 5 失败** |
| 恢复后 | 正式实现 | — | **18 通过 / 0 失败** |

> 变异 A/B/C 与恢复后的每一次都重新 `dotnet build` 后再 `dotnet test --no-build` 执行。

## 5. 新增测试清单（18 条，全部在 `tests/LiteMonitor.Tests/NetworkCacheWindowTests.cs`）

| 行号 | 用例名 | 验证了什么 |
|---|---|---|
| 256 | `网卡热插拔后立即刷新_不等三十秒缓存窗` | 可用性变更事件后**不推进时钟**即取到新网卡 IP（**不等 30 秒**） |
| 273 | `地址变更事件后立即刷新_不等三十秒缓存窗` | 地址变更事件后立刻取到新地址 |
| 289 | `WiFi切有线后立即刷新到有线的地址` | Wi-Fi 链路 down / 有线 up 后立即反映有线地址 |
| 309 | `网络变更后不再返回旧网卡的IP` | 网卡被拔掉后**不返回旧 IP**，状态为 `Missing` |
| 332 | `事件丢失时时间窗兜底仍然生效` | **不发事件**时，29 秒内复用缓存、31 秒后兜底重取 |
| 353 | `时间窗内不会重复枚举网卡` | 30 秒窗口内 50 次读取只枚举网卡 **1 次**（不拉爆频率） |
| 376 | `事件风暴不会导致重取风暴` | 100 次事件 + 200 次读取 ⇒ 网卡枚举次数 **≤ 3**（单轮预算） |
| 403 | `持续事件下重取频率仍然受时间约束` | 每 1.5 秒一个事件、20 次 ⇒ 枚举次数 **≤ 20**（每事件至多 1 次） |
| 429 | `双网卡并存时地址归属于选中的设备` | 选中的 A 卡显示 A 的地址；A 拔掉后**不串**到 A 的旧 IP |
| 465 | `网卡没有IPv4是缺失而不是读取失败` | 无 IPv4 ⇒ `Missing`，且**不是** `0.0.0.0` |
| 479 | `枚举网卡失败是读取失败而不是缺失` | 枚举抛异常 ⇒ `Failed`（与 `Missing` 可区分） |
| 499 | `原生网卡失效后立即重新匹配_不等十秒节流` | 计数读取失败后 **1 秒**内即重新匹配，并建立在新网卡实例的基线上 |
| 537 | `无失效信号时重匹配仍然被节流` | 无失效信号时 0.5 秒内**不**反复枚举（节流未被削弱） |
| 557 | `网络变更事件后立即允许重新匹配` | 网络变更事件 ⇒ 不推进时钟即重新匹配 |
| 584 | `地址缓存在代次推进后立即失效` | 代次推进后 `HasValidValue=false`，但仍可取回"上次已知值" |
| 604 | `地址缓存的代次推进有去抖` | 1 秒内重复事件只推进一次代次 |
| 620 | `刷新闸门在事件驱动下立即放行` | 闸门层：代次前进立即放行；同代次重复读受最短重试间隔约束 |
| 634 | `刷新闸门的时间窗兜底有界` | 闸门层：窗口到期放行，但单轮放行次数 == 3（预算封顶） |

> 全部用例**不启动 LHM、不碰驱动、不访问外网**；网卡用 `INetworkAdapterSnapshot` 假实现、
> 事件用 `INetworkChangeSource` 假实现、时钟用 `FakeTimeProvider`（**零 `Thread.Sleep`**）。

## 6. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| **真实 USB 网卡热插拔**：拔出后 30 秒内面板 IP 必须立刻变成新网卡（或显示缺失），不得停留在旧 IP | 真实 USB 千兆网卡 + 热插拔 hub | 用户 |
| **Wi-Fi ↔ 有线真实切换**（Windows 移动热点 / 双网卡笔记本）：`NetworkAvailabilityChanged` 的真实触发时序与本包的去抖窗口（1 秒）是否兼容 | 双网卡笔记本 | 用户 |
| **双网卡并存 + 首选网卡切换**：真实 `NetworkInterface` 实例与 LHM 网卡的名称/描述匹配（`SetNativeAdapter` 的模糊匹配分支） | 双网卡机器 | 用户 |
| **真实网卡计数读取失败后的重匹配**：`GetIPStatistics()` 在真实驱动上抛异常的时机 | 网卡被禁用/拔出 | 用户 |
| **DHCP 续租导致的地址变更**：真实 `NetworkAddressChanged` 事件到达时 IP 是否已经可读（本包假设"事件到达即可取到"） | 路由器 DHCP 短租 | 用户 |
| **事件风暴的真实形态**：一次物理热插拔在真实驱动上会连发几个事件、间隔多久（决定 1 秒去抖窗口是否合适） | 真实热插拔 | 用户 |

> ⚠️ 上述均为 **mock 不可替代**的场景。**mock 结果不得用于关闭 #96**。

## 7. 偏离与遗留

### 7.1 与派发单的偏离

| 项 | 说明 |
|---|---|
| 改动范围 | 全部落在授权清单内：`src/System/HardwareServices/NetworkManager.cs`（改）+ **新建** 3 个同目录文件（`NetworkAddressCache.cs` / `NetworkChangeSource.cs` / `NetworkAdapterSource.cs`）+ `tests/LiteMonitor.Tests/NetworkCacheWindowTests.cs`（新建）。 |
| `NetworkSampleFreshness.cs` | **未改动**（派发单允许"若确需扩展"，实测不需要：F03 的样本时效框架与本包的"地址刷新闸门"是两条正交链路）。 |
| `HardwareValueProvider.cs` | **未改动**（派发单允许"若确需"，实测不需要）。 |
| `src/System/HardwareMonitor.cs` | **未改动**（禁改清单）。`HardwareMonitor.GetNetworkIP()` 签名与行为保持不变。 |
| `.github/workflows/ci.yml` / `release.yml` | **一字未动**（`MIN_TEST_TOTAL: 2028` / `MAX_SKIPPED_TESTS: 1` 保持原样）。集成时需由协调者把 `MIN_TEST_TOTAL` 上调到 **2046**。 |
| `GetCurrentIP()` 的返回契约 | 保持"无地址时返回 null"（旧实现 `_staticIPCache = null` 的行为），未改成 `""`，以免影响 `LiteWebServer.cs:487` 的 `?? "127.0.0.1"` 兜底。新的 `GetCurrentIPResult()` 才返回 `""` + `Missing` 状态。 |
| **基线漂移** | 本分支基于 `bbbfdaf`。完工时 master 已前进到 `dd04fdd`（F08a 集成 + F17 派发单），改动文件为 `DiskProbePolicy.cs` / `DiskProbePolicyTests.cs` / `DiskProbeSettingsMigrationTests.cs` / 两个 workflow —— **与本包的 6 个改动文件无重叠**，可直接合并，无需 rebase 冲突处理。 |

### 7.2 发现的相邻问题（不属于本包，建议归属）

1. **`InfoService.cs:72` 只监听 `NetworkChange.NetworkAddressChanged`，未监听 `NetworkAvailabilityChanged`**
   ⇒ Wi-Fi ↔ 有线切换时，"网络变更即刷 UI"（`[Fix #287]`）**不会**被立即触发，只能等 InfoService 自己的轮询间隔。
   本包在 `NetworkManager` 侧已补上可用性变更监听，但 InfoService 侧**未改**（不在授权清单内）。
   → 建议归属：网络/UI 工作线。
2. **`src/System/WebServer/LiteWebServer.cs:487` 的 `hw.GetNetworkIP() ?? "127.0.0.1"`**：
   `GetNetworkIP()` 既可能返回 null 也可能返回 `""`（旧实现如此），`??` 只对 null 生效；
   无 IP 时 web 端 `sys.ip` 会显示空串。→ 建议归属：Web 工作线（与 F01/F02 批次相邻）。
3. **`src/System/InfoService/InfoService.cs:32` 的 `DEFAULT_IP = "0.0.0.0"`**：
   这是"用 0.0.0.0 冒充有效地址"的既有写法（与本包代码约束第 3 条精神相悖）。
   本包未改（不在授权清单内），但**建议**后续统一为缺失占位（如 `--`）。→ 建议归属：UI/信息层。
4. **基线 flaky（与本包无关）**：`tests/LiteMonitor.Tests/ReviewMarketPersistenceRegressionTests.cs` 间歇失败
   （`src/Plugins/PluginMarket.cs:1038` 的 `File.Move(..., overwrite: true)` 偶发 Win32 `ERROR_ACCESS_DENIED`）。
   本包**未触及**插件市场代码（改动文件清单见 §9）。实测情况见 §4.3。
   → 归属：主题市场工作线（按派发单要求**不修**）。

## 8. 证据锚点（真实文件 : 行号）

### 8.1 新建文件

| 文件:行 | 内容 |
|---|---|
| `src/System/HardwareServices/NetworkAddressCache.cs:14` | `enum NetworkAddressStatus { Ok, Missing, Failed }` —— "缺失"与"失败"可区分 |
| `src/System/HardwareServices/NetworkAddressCache.cs:69` | `class NetworkAddressCache` —— **值 + 网络环境代次**（进程级共享） |
| `src/System/HardwareServices/NetworkAddressCache.cs:96` | `NetworkAddressCache.Shared` —— 生产共享单例（测试注入独立实例） |
| `src/System/HardwareServices/NetworkAddressCache.cs:110` | `AdvanceGeneration()` —— 事件到达推进代次 + **1 秒去抖**（旧值立即失效） |
| `src/System/HardwareServices/NetworkAddressCache.cs:135/147` | `GetValidValue()` / `Store()` —— 值与代次绑定 |
| `src/System/HardwareServices/NetworkAddressCache.cs:181` | `class NetworkRefreshGate` —— **事件驱动优先 + 时间窗兜底 + 有界** |
| `src/System/HardwareServices/NetworkAddressCache.cs:220` | `IsWindowExpired` —— 30 秒兜底窗判据（单调时钟） |
| `src/System/HardwareServices/NetworkAddressCache.cs:241` | `ShouldRefresh(cacheValid, generation)` —— 事件路径 + 兜底路径 + 单轮预算 |
| `src/System/HardwareServices/NetworkAddressCache.cs:274/296` | `MarkRefreshSucceeded()` / `Consume()` —— 成功即结束本轮、预算封顶 |
| `src/System/HardwareServices/NetworkChangeSource.cs:7/21` | `enum NetworkChangeKind` / `interface INetworkChangeSource` |
| `src/System/HardwareServices/NetworkChangeSource.cs:33` | `class SystemNetworkChangeSource` —— **同时**转发地址变更与可用性变更，进程内只订阅一次 |
| `src/System/HardwareServices/NetworkAdapterSource.cs:13/40` | `interface INetworkAdapterSnapshot` / `interface INetworkAdapterSource` —— 网卡抽象（可注入假网卡） |
| `src/System/HardwareServices/NetworkAdapterSource.cs:47` | `class SystemNetworkAdapterSource` —— 真实 `NetworkInterface` 包装 |

### 8.2 改动文件 `src/System/HardwareServices/NetworkManager.cs`

| 行号 | 内容 |
|---|---|
| 25–37 | `NetworkState`：`NativeAdapter` 改为 `INetworkAdapterSnapshot?`（:25）；新增 `ForceRematch`（:33）/ `ConsecutiveMatchFailures`（:35）/ `AppliedInvalidationGeneration`（:37） |
| 57–65 | 新字段：`_addressCache`（:57）/ `_refreshGate`（:59）/ `_adapters`（:61）/ `_changeSource`（:63）/ `_adapterInvalidationGeneration`（:65）（旧 `_staticIPCache` / `_lastIPCheckTimestamp` / `_hasIPCheck` / `_shouldResetAdapters` 已删除） |
| 116–134 | 新的重匹配退避阶梯常量：`RematchRetryIntervalSeconds = 1.0`（:121）/ `MaxImmediateRematchAttempts = 3`（:127）/ `RematchThrottleSeconds = 10.0`（:133） |
| 136–168 | 构造签名：**只追加** 3 个可选参数（`adapterSource` / `changeSource` / `addressCache`），旧调用点全部兼容 |
| 173–185 | `OnNetworkChanged()`（:173）—— **事件驱动**入口：推进代次（旧 IP 立即作废）+ 作废已锁定的原生网卡（重匹配立即放行） |
| 423–436 | `ApplyAdapterInvalidation()`（:423）—— 按代次作废单个网卡的原生实例（旧的 `_shouldResetAdapters` 全局标志的精确化替代） |
| 441–449 | `GetCurrentIP()`（:441）—— 保持既有契约（无地址返回 null），委托给新 API |
| 454–494 | `GetCurrentIPResult()`（:454）—— **新**：缓存命中 → 闸门判定（有界）→ 真正取值 → 区分 Missing / Failed / Retained |
| 496–570 | `ResolveCurrentIP()`（:496）—— 策略 A（已锁定网卡）/ 策略 B（枚举兜底）；**枚举异常 ⇒ `Failed`**、无 IPv4 ⇒ `Missing`（:569） |
| 572–605 | `GetIPv4FromAdapter(INetworkAdapterSnapshot)`（:577）—— 保留既有语义（忽略 APIPA、优先 192.168.x.x） |
| 850–872 | `AccumulateTraffic`：计数读取失败 ⇒ 作废网卡 + `ForceRematch`（立即重匹配），并修正 `UpdateExclusionTarget` 的空引用风险 |
| 893–899 | `NativeMismatch`（:893）⇒ 作废网卡 + `ForceRematch` |
| 919–992 | `MatchNativeNetworkAdapter()`（:919）—— **退避阶梯**：失效信号立即 / 1 秒快速重试 ≤3 次 / 退避 10 秒 |
| 1028–1038 | `SetNativeAdapter(INetworkAdapterSnapshot, …)`（:1028）—— 计数不可用即视为未匹配 |

### 8.3 测试文件 `tests/LiteMonitor.Tests/NetworkCacheWindowTests.cs`

- 假时钟 `FakeTimeProvider`（:40）、假网卡 `FakeAdapter`（:57）、假网卡源 `FakeAdapterSource`（:101，含枚举计数）、
  假事件源 `FakeChangeSource`（:121）、LHM 桩 `FakeNetworkHardware`（:172）、夹具 `NewFixture`（:214，注入独立缓存与事件源）。
- 18 条用例见 §5。

## 9. 提交

```
b911315 fix(F03b): IP 地址缓存窗与重匹配节流改为事件驱动优先 + 时间窗兜底
b8d2b21 test(F03b): 热插拔立即刷新、事件丢失时间窗兜底、事件风暴有界、双网卡不串
0f4a9b2 docs(F03b): 验收记录 —— 网络地址缓存窗与重匹配节流收口（#96 关闭条件补齐）
def716b docs(F03b): 验收记录补齐提交 SHA 列表与改动文件清单
e7f9b49 docs(F03b): 验收记录补齐最终验收轮数据、已知 flaky 说明与精确行号锚点
```

改动文件（`git diff --name-status bbbfdaf..HEAD`）：

```
A  src/System/HardwareServices/NetworkAdapterSource.cs
A  src/System/HardwareServices/NetworkAddressCache.cs
A  src/System/HardwareServices/NetworkChangeSource.cs
M  src/System/HardwareServices/NetworkManager.cs
A  tests/LiteMonitor.Tests/NetworkCacheWindowTests.cs
A  docs/verification/acceptance/F03b-report.md
```

> 禁改清单核对：`.github/workflows/ci.yml`、`.github/workflows/release.yml`、
> `src/System/HardwareMonitor.cs`、`src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、
> `src/UI/UIController.cs` —— **均未出现在改动列表中**（一字未动）。

## 10. 环境

- OS：Microsoft Windows 11 专业版 10.0.26300（build 26300）
- 运行时：.NET SDK 8.0.426；目标框架 net8.0-windows
- 依赖：LibreHardwareMonitorLib 0.9.6
- GPU：本机为虚拟显示器（GameViewer Virtual Display Adapter / Meta Virtual Monitor），**无真实 NVIDIA 卡**；
  本包不涉及 GPU 路径（GPU 非性能测试 19/19 绿仅证明未回归）
- 网卡：本机仅本机网卡，**无 USB 网卡 / 热插拔环境** ⇒ §6 全部为"待实机"
- 工作区：独立 worktree `G:\TOOL\LiteMonitor-fork\.worktrees\f03b`（分支 `fix/F03b-network-cache-window`），主仓库未被写入
