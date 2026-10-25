# 派发单 P-F03b —— 网络地址缓存窗与网卡重匹配节流收口

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| W2（与 F02/F05/F07/F14a 并行） | **P-F03 已合并** | `fix/F03b-network-cache-window` | #96 关闭条件补齐（"实际网络切换/恢复通过"） |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-F03b 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读 `docs/plans/dispatch/README.md` 外派总则，再通读拆解计划 §4「P-F03」与母计划 §4 F03 的验收矩阵。本包是 F03 的**收尾补口**，不是新功能：F03 已把"网络值过期/冻结回退"收口到四层，但**仍有两处固定时间窗会让多网卡热插拔后的 IP 刷新滞后**——而这正是 #96 的关闭条件（"缓存过期与实际网络切换/恢复通过"）未闭环的部分。

## 证据锚点

- `src/System/HardwareServices/NetworkManager.cs:370-373` —— IP 检查缓存窗判定：
  `return _time.GetElapsedTime(_lastIPCheckTimestamp, _time.GetTimestamp()).TotalSeconds < 30;`
  ⇒ **30 秒**固定窗。网卡热插拔 / Wi-Fi 切换后，在这 30 秒内 `GetCurrentIP()` 仍可能返回**旧网卡的 IP**。
- `src/System/HardwareServices/NetworkManager.cs:375-376` —— `GetCurrentIP()`（注释写"带 10秒 缓存"，与上面 30 秒不一致，**注释本身也需要更正**）。
- `src/System/HardwareServices/NetworkManager.cs:797-803` —— `MatchNativeNetworkAdapter` 的**10 秒**重匹配节流（`state.HasMatchAttempt` + 单调时钟）。
- F03 已在 `src/System/HardwareServices/NetworkSampleFreshness.cs`（新建，317 行）与 `HardwareValueProvider.cs` 建立"样本时效/设备代次"框架；`NetworkManager.cs` 已被 F03 大改（原 +322/−?）。**派发单里的旧行号一律先 grep 重新定位再引用。**

## 要点（完整规格见拆解计划 §4 P-F03 / 母计划 F03）

1. **两个时间窗都要变成"事件驱动优先 + 时间窗兜底"**，而不是纯固定秒数：
   - IP 缓存窗：网络变更事件（`NetworkChange.NetworkAddressChanged` / `NetworkChange.NetworkAvailabilityChanged`）到达时**立即失效**缓存并重取；时间窗只作为"事件丢失"的兜底。
   - 重匹配节流：`NativeAdapter` 失效（网卡消失 / 地址变化 / 设备代次变化）时**立即允许重匹配**，节流只约束"无失效信号时不要反复空转"。
2. **不得靠缩短固定秒数解决**（把 30 秒改成 1 秒是错误解法：会让硬件枚举频率暴涨）。
3. 事件与时间窗的判定必须用**单调可注入时钟**（F03 已引入 `NetworkSampleFreshness` 的时间抽象，沿用它），**禁止 `Thread.Sleep`**。
4. 失效与重取要**有界**：单次事件触发最多 N 次重试，禁止事件风暴导致的重匹配风暴。
5. **0 是有效数据**：IP 缺失（未连接 / 无 IPv4）与"读取失败"要能区分，不得用 `0.0.0.0` 冒充。
6. 不回退 fork 已有能力：`[Fix #287]` 的"网络变更即刷 UI"链路（`InfoService.cs`）必须保持。

## 完成产物

1. 分支 + 红→绿提交；用例覆盖：**热插拔后立即刷新**（不等 30 秒）、事件丢失时时间窗兜底仍生效、事件风暴有界、Wi-Fi ↔ 有线切换、双网卡并存时选中设备不串。
2. `docs/verification/acceptance/F03b-report.md`。
3. 注明：真实网卡热插拔场景标"**待实机**"（本机只有本机网卡，需真实 USB 网卡/热插拔环境）；mock 结果不得用于关闭 #96。

## 禁改

`.github/workflows/ci.yml`、`release.yml`（阈值 `MIN_TEST_TOTAL: 2028` / `MAX_SKIPPED_TESTS: 1` **一字不动**，只由协调者在批次集成时统一上调）；`src/System/HardwareMonitor.cs`（共享热点，本包不需要改）；`src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、`src/UI/UIController.cs`（共享热点，本包不需要改）。
