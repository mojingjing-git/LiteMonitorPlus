# 子智能体执行拆解计划（配套 2026-10-25 已核实问题修复计划）

日期：2026-10-25
母计划：[2026-10-25-verified-bugs-remediation-plan.md](2026-10-25-verified-bugs-remediation-plan.md)（下称"母计划"）
代码基线：`3ae64e8`（HEAD `98c9237` 仅追加文档）
用途：把母计划的 F00～F16 拆成 **18 个可独立派发的工作包**，每个包对应一个子智能体、一个分支、一份验收记录。子智能体只需阅读本文对应章节 + 包内"只读材料"，不需要通读全部验证报告。

拆分说明：F08 → F08a（CountersOnly 止血）/ F08b（精细策略）；F09 → F09a（依赖候选决策，只读）/ F09b（依赖集成）；F14 → F14a / F14b。F00 与 F16 为协调者任务，不派发。

---

## 1. 全局执行规则（每个子智能体必读）

### 1.1 分支与提交

1. 从 `master` 切出专属分支，命名 `fix/F0x-slug`（如 `fix/F01-ws-upgrade-parse`）。只在分支上工作，**不推送远端、不动 master**。
2. 提交信息格式：`fix(F0x): 摘要` / `test(F0x): 摘要` / `docs(F0x): 摘要`。实现与测试可分开提交；上游 PR 移植提交须带 `-x` 或在说明中固定来源 SHA。
3. 开工前 `git log --oneline -3` 记录基线 SHA；发现目标文件已被他人改动（与本文 §2 所列基线不符）时，停止并在报告中说明，不强行合并。

### 1.2 代码约束（继承母计划 §2，违反任何一条即返工）

1. 不得回退 fork 已有能力：单调时钟采样、单次在途采样、多 GPU、插件执行边界/发布租约、Web 会话与一次性票据、GPU 回退、窗口关闭优先级。
2. 修复包含迁移：不得把用户排序、阈值、别名、隐藏状态、历史静默绑定到另一设备。
3. 错误不能伪装成正常值：成功采到的 0 有效；缺失/失败/过期各有状态；读缓存不刷新数据寿命。
4. 恢复必须有界：去抖、退避、单次在途；不新增高频硬件轮询或无限重试线程。
5. UI 线程只投递恢复请求；硬件更新/重载/建图在采样协调器串行边界完成；关闭过程不新建 HWND、不重开硬件。
6. 时间一律 `TimeProvider`/单调时钟，测试可注入；测试禁用 `Thread.Sleep` 真实等待。
7. 应用层 catch 只补诊断，不冒充原生边界修复。

### 1.3 测试与门禁

```bash
dotnet build LiteMonitor.sln -c Debug --nologo
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build
dotnet test tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj -c Debug --no-build
dotnet test tests/LiteMonitor.Updater.Tests/LiteMonitor.Updater.Tests.csproj -c Debug --no-build
```

- **禁止修改 `.github/workflows/ci.yml` / `release.yml` 的任何阈值**（当前 `MIN_TEST_TOTAL: 1876`、`MAX_SKIPPED_TESTS: 1`）。阈值由协调者在批次集成时统一上调。
- 每包至少先写"能失败的用例"再改实现（红→绿）；优先真实调用产品路径，禁止复制产品算法自证。
- 实机探针只允许用隔离测试配置，不得覆盖用户 `settings.json`。
- 无目标硬件的场景在报告中标记"待实机"，禁止用 mock 结果关闭硬件特有问题。

### 1.4 禁改与共享文件

- 只允许修改本包"改动文件"清单内的文件（外加新建文件）。清单外的必要改动：写入报告"建议协调者处理"，不自行修改。
- 共享热点 `src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、`src/UI/UIController.cs`、`src/System/HardwareMonitor.cs`：仅允许做本包明确授权的最小编辑；新增设置项一律"字段 + Draft-Commit 保存"，不动他人字段。
- 语言资源 `resources/lang/*.json`：只新增 key，不改既有 key 的语义；8 种语言同步（缺失语种可回退英文并注明）。
- 通用回归集合（母计划 §6.2 的 6 条）任何包不得破坏。

### 1.5 报告与验收记录

完成后写 `docs/verification/acceptance/F0x-report.md`（模板见 §6），并在回复消息中给出：分支名 / 提交 SHA 列表 / 新增测试数 / 自验命令输出摘要 / 未覆盖场景 / 是否满足"代码层验收"。

### 1.6 派发提示词模板（协调者使用）

> 你是工作包 P-F0x 的执行智能体。工作目录 `G:\TOOL\LiteMonitor-fork`。
> 1. 通读 `docs/plans/2026-10-25-verified-bugs-remediation-subagent-plan.md` 的 §1 全局规则与 §4 中你的 P-F0x 包。
> 2. 先读完包内"只读材料"再动手。
> 3. 在分支 `fix/F0x-slug` 上实施，遵守禁改清单，不推送远端，不改 CI 阈值。
> 4. 完成后按 §1.5 写验收记录并回复摘要。

---

## 2. 文件所有权地图（已核验存在）

| 区域 | 全路径 |
|---|---|
| Web | `src/System/WebServer/WebSessionManager.cs`、`src/System/WebServer/WebServerAccessPolicy.cs`、`src/System/WebServer/LiteWebServer.cs` |
| 核心配置 | `src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs` |
| 硬件服务 | `src/System/HardwareMonitor.cs`、`src/System/HardwareServices/NetworkManager.cs`、`HardwareValueProvider.cs`、`HardwareSamplingCoordinator.cs`、`HardwareScanner.cs`、`DiskManager.cs`、`SensorMap.cs`、`FanMapper.cs`、`DriverInstaller.cs`、`GpuWarmup.cs`、`HardwareRules.cs` |
| GPU 身份 | `src/Core/GpuRegistry.cs`、`src/Core/GpuKey.cs`、`src/Core/GpuItemSync.cs` |
| 指标 | `src/Core/MetricItem.cs`、`src/Core/MetricUtils.cs`、`src/Core/HardwareHistoryLogger.cs` |
| 窗口 | `src/UI/TaskbarForm.cs`、`src/UI/Helpers/TaskbarBizHelper.cs`、`TaskbarWinHelper.cs`、`TaskbarStrategyWin10.cs`、`TaskbarStrategyWin11.cs`、`MainFormBizHelper.cs`、`src/UI/MainForm_Transparent.cs` |
| 布局/渲染 | `src/UI/HorizontalLayout.cs`、`src/UI/UIController.cs`、`src/UI/Settings/ThemePreviewService.cs` |
| 设置页 | `src/UI/SettingsForm.cs`、`src/UI/Settings/SettingsBase.cs`、`SystemHardwarPage.cs`、`PluginMarketPage.cs`、`src/UI/Controls/LiteUI.cs` |
| 插件 | `src/Plugins/PluginModels.cs`、`PluginExecutor.cs`、`src/Plugins/Native/`、`resources/plugins/Holiday.json` |
| 测试 | `tests/LiteMonitor.Tests/`（`WebSessionProtocolTests.cs`、`WebSocketRejectCloseTests.cs`、`SamplingCacheTests.cs`、`TrafficDeltaTests.cs`、`PluginExecutorSandboxTests.cs`、`GpuItemSyncTests.cs` 等） |
| 基础设施 | `scripts/run-probes.ps1`、`.github/workflows/ci.yml`、`release.yml`（**只读**）、`resources/lang/*.json` |

冲突协调：F03 与 F08a 都会小改 `HardwareMonitor.cs`（网络取值分支 vs 存储启用策略），允许并行，但双方编辑必须局限在自己区域内、每处改动 ≤ 30 行；协调者先并 F03 再 rebase F08a。硬件实现链 F07→F08b→F09b→F10→F11 **严格串行**（共享 `HardwareMonitor.cs`/`SensorMap.cs`/`DiskManager.cs`/`Settings.cs`）。

---

## 3. 调度批次

| 批次 | 可并行派发 | 说明 |
|---|---|---|
| W0（协调者） | F00 基线固化 | 见 §5.1；完成后才允许派发 |
| W1 | **F01、F03、F04、F08a、F09a、F12、F13、F15** | 最多 8 个并行；F09a/F15 为只读/工具型 |
| W2 | **F02**（前置 F01）、**F05**（前置 F04）、**F07**、**F14a** | |
| W3 | **F06**（前置 F05）、**F08b**（前置 F07+F09a）、**F14b**、F15 继续 | |
| W4 | **F09b**（前置 F09a，且在 F08b 之后串行）、**F10**（前置 F07） | 硬件链串行段开始 |
| W5 | **F11**（前置 F03+F07+F10） | |
| W6（协调者） | F16 收尾与发布记录 | 按母计划 R1～R4 切批，R1 不必等 W2 |

R1 候选 = F01+F02+F03（+独立验收的 F12/F13），在 W2 的 F02 完成后即可出，不被硬件链阻塞。

---

## 4. 工作包明细

### P-F01 — WebSocket 升级识别改结构化解析

- **批次/前置**：W1 / F00。**分支**：`fix/F01-ws-upgrade-parse`
- **覆盖**：WEB-OWS（防回归 #589）。**只读材料**：`docs/verification/2026-10-25-WEB-PROTOCOL-LIVE-VERIFICATION.md`；`src/System/WebServer/WebSessionManager.cs:777`、`:1323-1327`、`:2580` 区域。
- **改动**：`src/System/WebServer/WebSessionManager.cs`；`tests/LiteMonitor.Tests/WebSessionProtocolTests.cs`、`WebSocketRejectCloseTests.cs`；可新建裸 TCP 集成测试文件。
- **步骤**：
  1. 删除 `IsWebSocketUpgrade` 对整段请求的 `Regex.IsMatch(req, "Upgrade: websocket")` 判定，改用已有结构化请求解析结果；HTTP 路由与 WS 握手共享同一解析语义。
  2. 请求行校验真实方法/路径/HTTP 版本；头名大小写不敏感；字段值仅清理合法首尾 SP/HTAB；拒绝字段名与冒号间空白、折行、歧义输入。
  3. `Connection` 按逗号 token 判定含 `Upgrade`；`Upgrade` 按协议 token 精确匹配 `websocket`（不得子串命中 `notwebsocket`）。
  4. 保留唯一 `Host`、唯一合法 WS Key、版本、票据校验；列表头可合并与单值头禁重复采用不同规则，不用"取第一个值"隐藏冲突。
  5. 面向 WS 路由的无效握手返回明确 4xx（不静默 200 登录页）；普通网页 GET 行为不变；错误优先级固定并写测试。
- **测试/验收**：原 C1～C6 有效凭据返回 101；新增 Tab、尾部空白、Connection 多 token、TCP 拆包用例；非法/重复 Key→400；无/过期/重放票据→401；非法 Origin→403；伪造 `X-Upgrade`、正文 Upgrade 文本、非 GET、歧义长度头、超大头不进握手；#589 四种大小写用例保持通过。
- **禁改**：`WebServerAccessPolicy.cs`、`LiteWebServer.cs`、CI 文件。**报告**：`docs/verification/acceptance/F01-report.md`。

### P-F02 — 反代外部地址与可信代理策略

- **批次/前置**：W2 / **F01 已合并**（同一文件）。**分支**：`fix/F02-reverse-proxy-policy`
- **覆盖**：#580。**只读材料**：`docs/verification/2026-10-25-TASK-5-web-plugin.md` 的 #580 节；`WebSessionManager.cs:1937-2008`（现状 Host/Origin 白名单 + 端口强校验）；`Settings.cs:257`（`WebServerTrustProxyHeaders` 为零消费死设置，已核验）。
- **改动**：`WebSessionManager.cs`、`WebServerAccessPolicy.cs`、`LiteWebServer.cs`、`src/Core/Settings.cs`（仅新增两字段）、`src/UI/Settings/SystemHardwarPage.cs`、`src/Core/Actions/AppActions.cs`、`resources/lang/*.json`、`tests/LiteMonitor.Tests/`（Web 认证相关测试文件）。
- **步骤**：
  1. 新增 `WebServerAllowedHosts`（精确 host:port，无通配）与 `WebServerTrustedProxies`（精确 IP，首版不接受代理链）；默认 `WebServerTrustProxyHeaders=false` 保持；经 Draft-Commit 保存。
  2. 将实际 TCP 对端地址传入访问策略；仅当开关开且对端在可信列表才采用 `X-Forwarded-Host`/`X-Forwarded-Proto`；首版要求单一值，多值/重复/冲突→4xx；其他转发头不参与身份判定。
  3. 外部请求有效 authority 必须命中白名单；HTTPS 默认 443、HTTP 默认 80，非默认端口精确匹配；IPv6/大小写/非法端口统一严格解析；不得拿外部 443 与内部 5000 直接比较。
  4. 直连保留环回/LAN 规则；不可信对端的转发头不能改变 Host/Origin/客户端身份/安全属性。
  5. Origin 按有效外部 scheme+authority 同源判定；跨源例外仍归 `AllowedOrigins`。
  6. 登录/票据/快照/OPTIONS/WS 共用策略；对外代理即使来自环回也要求口令，不继承"环回无凭据"；cookie 的 Secure 判定用已验证协议。
  7. 白名单收紧/撤销信任后按连接代次撤销旧会话/WS；配置界面提供范例与错误原因；日志不落口令/cookie/票据原文。
- **测试/验收**：母计划 F02 验收矩阵逐行落地（本地可控反代覆盖"保留 Host"与"重写 Host"两种模式）；真实 Cloudflare Tunnel 单列为"待实机"，不冒充。
- **禁改**：`NetworkManager.cs` 等硬件文件；CI 文件。**报告**：`docs/verification/acceptance/F02-report.md`。

### P-F03 — 网络值时效、失败恢复与自动选卡

- **批次/前置**：W1 / F00。**分支**：`fix/F03-network-sample-freshness`
- **覆盖**：#96（回归 #287）。**只读材料**：`docs/verification/2026-10-25-TASK-1-performance.md` 的 #96 节；`src/System/HardwareServices/NetworkManager.cs:463-485`（`lastValidMap` 冻结回退永不失效）。
- **改动**：`NetworkManager.cs`、`HardwareValueProvider.cs`、`src/System/HardwareMonitor.cs`（**仅限网络取值分支，≤30 行**）、`HardwareSamplingCoordinator.cs`；测试 `SamplingCacheTests.cs`、`TrafficDeltaTests.cs` + 新增网络时效测试。
- **步骤**：按母计划 F03 五步执行——样本含值/设备身份/代次/成功时间/质量；审计 `ReadNetworkSensor`、`_lastValidMap`、启动缓存、采集中读取四层均不得重发布旧值；TTL 策略（≤3 个采样周期、上限 15 秒，Stale→Missing，阈值集中定义可注入时钟，真实 0 流量仍 Fresh）；设备删除/首选变化/重载立即失效缓存与差分基线；自动模式按存活+采样成功+有效流量重评分、失败触发有限重选，手动模式设备消失保留选择并显示不可用。
- **测试/验收**：正值→失败→TTL 边界→缺失→恢复全链路可测；读缓存 1 万次不刷新 TTL；有效 0 恒 0；拔旧卡后另一卡恢复；手动选卡不被改写；反复切换无负流量/尖峰；采样调用频率不随 UI 刷新倍增。
- **禁改**：磁盘/GPU 相关分支；`Settings.cs`。**报告**：`docs/verification/acceptance/F03-report.md`。

### P-F04 — 任务栏宿主生命周期与有界恢复

- **批次/前置**：W1 / F00。**分支**：`fix/F04-taskbar-host-lifecycle`
- **覆盖**：#553、#573、#583、#539。**只读材料**：`docs/verification/2026-10-25-TASK-4-window-display.md`；母计划 §5（PR #495 head `21308ec1`、#555 head `90d822d`，#555 为 **Draft**）。
- **改动**：`src/UI/TaskbarForm.cs`、`src/UI/Helpers/TaskbarBizHelper.cs`、`TaskbarWinHelper.cs`、`src/UI/MainForm_Transparent.cs`；可新建单一"任务栏恢复协调器"文件。
- **步骤**：按母计划 F04 六步——固定 SHA 审查两 PR 的 diff 后在隔离分支移植（冲突对照 fork 的关闭优先级/GPU HWND 代次/显示变化/纯任务栏行为）；状态机（等待/已挂载/恢复中/关闭中）；恢复调度由独立于子 HWND 的 UI 宿主拥有；校验根句柄+内部子句柄+实际父子关系+目标显示器+句柄代次；`OnHandleDestroyed` 区分外部失效/受控重建/应用关闭，恢复排队到消息循环；合并 Explorer 重启/唤醒/电源/锁屏/拓扑/DPI/任务栏设置事件统一去抖（参考 2 秒×10 秒恢复期 + 低频退避等待目标重现，不永久停止）；副屏未就绪保持等待不静默绑定主屏；纯任务栏模式与关闭期语义（取消排队、注销事件、禁止复活）；保留 fork 的 GPU 显示变化通知与主窗位置夹取。
- **测试/验收**：真实 Win32 测试宿主三场景（销毁父窗口/保留根重建子层/延迟创建目标屏宿主）均恢复；恢复期点击退出不重建；100 次宿主重建后面板/定时器单份、句柄不增长。锁屏/睡眠/Explorer 重启/RDP 标记**待实机**，逐项记录。
- **禁改**：`TaskbarStrategyWin10/11.cs`（归 F05）、`MainFormBizHelper.cs`（归 F06）。**报告**：`docs/verification/acceptance/F04-report.md`。

### P-F05 — 任务栏方向、坐标与占位幂等

- **批次/前置**：W2 / **F04 已合并**。**分支**：`fix/F05-taskbar-geometry`
- **覆盖**：#568、#539、#573、#583。**只读材料**：TASK-4 报告的 Win10 路径实测（任务列表 1200→640、面板 X 600→900）；`src/UI/Helpers/TaskbarStrategyWin10.cs`（无 Dock 方向枚举，靠宽高比猜）。
- **改动**：`TaskbarStrategyWin10.cs`、`TaskbarStrategyWin11.cs`、`TaskbarWinHelper.cs`、`TaskbarBizHelper.cs`。
- **步骤**：按母计划 F05 五步——显式 Top/Bottom/Left/Right 方向 + 任务栏相对所属显示器物理矩形解析；坐标契约（几何查询物理像素→最终定位显式转父客户区坐标，入口标注坐标空间；不盲采 PR #495 的 20～100px 高度启发式）；几何缓存键含根/子 HWND 代次、显示器、矩形、DPI、方向、面板尺寸、任务栏设置；Win10 占位维护基线+本次占位量、Tick 幂等、变更重取基线、禁用/退出/重挂撤销；第三方任务栏标注兼容性待验。
- **测试/验收**：Win10 四方向、连续切换、重复 Tick 无累计漂移；100 次启停不缩窄任务列表；100/150/200% 混合 DPI、负屏幕原点、非默认工作区无重叠/越界；复测 1200→640 与 600→900 场景；分辨率变化恢复占位、退出恢复 Explorer 布局。
- **禁改**：`TaskbarForm.cs`（F04 已合并后的区域）、`MainFormBizHelper.cs`。**报告**：`docs/verification/acceptance/F05-report.md`。

### P-F06 — 同级 Z 序与主窗置顶分别维护

- **批次/前置**：W3 / **F05 已合并**。**分支**：`fix/F06-zorder-topmost`
- **覆盖**：#599、#612。**只读材料**：TASK-4 报告（`TaskbarStrategyWin11.cs:64` 恒 `SWP_NOZORDER`；`MainFormBizHelper.cs:91-105` 只查样式位、`!_form.Visible` 早退）；母计划 §5（PR #600 head `e4ed1bd`、#373 head `1c279c6` 为 **CONFLICTING，只作参考手工适配**）。
- **改动**：`TaskbarStrategyWin11.cs`、`src/UI/TaskbarForm.cs`（仅 Z 序维护挂钩点）、`src/UI/Helpers/MainFormBizHelper.cs`、相关 Win32 封装。
- **步骤**：按母计划 F06 四步——按需 `HWND_TOP`（不移动/不改大小/不激活，仅当可见合成层确在前方）；显示变化立即检查 + 低成本后续检查覆盖迟到合成层，不每帧无条件置顶/遍历/重挂；PR #373 意图手工适配（样式位+实际层级双判，尊重用户关闭置顶/自动隐藏/全屏，不压安全桌面）；任务栏维护独立于主窗 `Visible`。
- **测试/验收**：模拟新建可见合成兄弟窗口后下一维护周期恢复、矩形焦点不变；主窗样式在但层级异常可恢复；关 TopMost 后不被看门狗重开；纯任务栏/虚拟桌面/全屏进出分别验证（部分标记待实机）。
- **禁改**：`TaskbarStrategyWin10.cs`、网络/硬件文件。**报告**：`docs/verification/acceptance/F06-report.md`。

### P-F07 — 稳定设备身份与兼容迁移

- **批次/前置**：W2 / F00（**硬件链串行段起点**）。**分支**：`fix/F07-stable-device-identity`
- **覆盖**：#290、#538（基础 for #577）。**只读材料**：总表交叉发现 A（`HardwareScanner.cs:85` 按显示名 `Distinct()`；`ListAllGpuOptions` 已有 `Identifier`+`#index` 正确范式）；`docs/verification/2026-10-25-TASK-2-gpu-sensors.md`、`TASK-3`。
- **改动**：`src/System/HardwareServices/HardwareScanner.cs`、`DiskManager.cs`、`SensorMap.cs`、`FanMapper.cs`、`src/Core/GpuRegistry.cs`、`GpuKey.cs`、`GpuItemSync.cs`、`src/Core/Settings.cs`（新增迁移字段）、设置页与监控项同步代码；测试 `GpuItemSyncTests.cs` + 新建迁移测试。
- **步骤**：按母计划 F07 六步——设备目录（StableId/RuntimeId/DisplayName/ParentId/Kind/IsPresent，GPU/磁盘/风扇同原则不强行同 API）；展示槽位 vs 设备身份分离，保留 `GPU.{index}.*` 公开 Key + 持久化槽位绑定稳定 ID（不把裸 PCI 路径拼进 Key）；验证 LHM Identifier 跨枚举/重启稳定性，优先 PnP 实例/总线信息，LUID/枚举号/显示名不可单独作跨重启身份，不可靠时显式标记不稳定；磁盘选择项 Label+Value、同名加后缀、Windows 计数器实例单独映射、**枚举不得先做 SMART 探测**（不得绕过 F08 策略）；旧配置仅唯一匹配迁移、歧义保留旧值+提示，GPU 槽位按可证明映射迁移，迁移幂等/原子/备份；手动首选卡消失保持绑定显示离线、自动模式可重选但公开选择、`MonitoredGpuCount=1` 语义不变。
- **测试/验收**：双同型号 SSD/GPU、枚举顺序互换、设备短暂消失、重启、驱动更新后标识变化的迁移用例（用可注入枚举工厂模拟）；无法证明同一设备不复用历史绑定；旧配置重复加载不重复新增指标、排序与阈值不漂移。
- **禁改**：`HardwareMonitor.cs`、`NetworkManager.cs`、CI 文件。**报告**：`docs/verification/acceptance/F07-report.md`。

### P-F08a — 磁盘探测 CountersOnly 止血模式

- **批次/前置**：W1 / F00。**分支**：`fix/F08a-disk-counters-only`
- **覆盖**：#466、#601（止血部分）、#245（降低暴露面）。**只读材料**：TASK-3 报告（`IsStorageEnabled=true` 无条件；HWiNFO ISA Lock 点名截图）；总表交叉发现 G（历史计划标注"已实施"但 `DiskProbePolicy` 零匹配）。
- **改动**：`src/System/HardwareMonitor.cs`（**仅存储启用策略区域，≤30 行**）、`src/System/HardwareServices/DiskManager.cs`、`HardwareScanner.cs`、`DriverInstaller.cs`、`src/Core/Settings.cs`（新增策略字段）、`src/UI/Settings/SystemHardwarPage.cs`；新建 `DiskProbePolicy`。
- **步骤**（仅母计划 F08 的第一、二、五步中的 CountersOnly 部分）：
  1. `CountersOnly` 模式：第一次 `Computer.Open()` 前 `IsStorageEnabled=false`；速度/活动率来自 Windows 计数器；温度/SMART 显示策略禁用、不显示 0。
  2. 设置页扫描、后台维护、重载全走同一策略；新安装默认 CountersOnly；旧配置缺字段进入显式迁移状态并提示，首次打开硬件前仍 CountersOnly，保留原磁盘偏好；用户选择后持久化。
  3. 模式变更经采样协调器：停采样→释放旧对象→重建代次→恢复；失败保持计数器数据并显示状态，不反复全盘扫描；关闭/移除释放句柄。
- **测试/验收**：假设备工厂断言"禁止设备构造次数为 0"；策略状态机用例；`Process Monitor` 观察项与 HWiNFO/DAPLink/USB 安全移除全部列**待实机**。
- **禁改**：`SensorMap.cs`、`FanMapper.cs`、GPU 文件（除读取策略字段必需的一行外）。**报告**：`docs/verification/acceptance/F08a-report.md`。

### P-F08b — 精细允许列表与控制器策略（含库能力 spike）

- **批次/前置**：W3 / **F07、F09a 已合并**。**分支**：`fix/F08b-disk-allowlist`
- **覆盖**：#466、#601、#245。**只读材料**：F08a 的验收记录；F09a 的依赖决策记录；上游 LHM 存储设备创建路径文档/源码。
- **改动**：与 F08a 同一文件集 + 可能的库入口补丁。
- **步骤**：
  1. **Spike（先行，产出决策记录进报告）**：验证 LHM 能否"仅创建/打开允许列表内磁盘"——排除判定必须发生在库创建设备与打开句柄**之前**；若现有库做不到，修库入口或**暂不开放该模式**（不得构造后过滤冒充隔离），结论写入报告供协调者裁决。
  2. Spike 通过才实施：仅内部盘/明确允许列表的 SMART 模式。
  3. #245 独立审计 controller/HID/USB 枚举路径，提供控制器探测开关 + 按可靠标识排除 DAPLink 等设备；磁盘策略不冒充 #245 完整修复。
- **测试/验收**：假设备工厂证明排除设备构造次数为 0；允许列表增删实时生效；DAPLink/HWiNFO/USB 移除**待实机**。
- **禁改**：GPU 映射文件。**报告**：`docs/verification/acceptance/F08b-report.md`（**必须含 spike 结论**）。

### P-F09a — 原生依赖候选验证（只读研究）

- **批次/前置**：W1 / F00。**分支**：`research/F09a-dependency-candidate`（允许试验性 csproj 改动，**不回合并 master**）
- **覆盖**：#502、#579 决策前置。**只读材料**：TASK-2 报告（LHM 0.9.6 `_nvmlDevice` readonly 缓存字段 IL 级取证）；TASK-3（DiskInfoToolkit 1.1.2 `DevicesChangedListener()` 自建线程）；LHM 上游 [PR #2539](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/2539)（merge SHA `3aa9c6c`，已合并不等于已发布进 NuGet 包）。
- **产出（不改产品源码）**：`docs/verification/acceptance/F09a-report.md`，内容为决策记录：
  1. 逐候选（固定源码补丁 vs 包含修复的已发布包 vs DiskInfoToolkit 2.2.1 vs LHM 预发布）验证：实际程序集/源码是否含 NVML 句柄重取与失效处理；API 面、PawnIO、Intel GCL、磁盘访问行为差异；可重现构建步骤。
  2. 用一个小型受控验证工程（引用候选包，模拟句柄失效序列）给出证据，不凭版本号推断。
  3. 明确推荐组合 + 回退组合；风险（主版本跨越、传递依赖覆盖）逐条列出。
- **禁改**：`src/**` 产品文件（验证工程放 `.tmp/` 或独立目录）、CI。**报告路径即上述文件**。

### P-F09b — 原生依赖修复集成

- **批次/前置**：W4 / **F09a 决策 + F08b 已合并**（硬件链串行）。**分支**：`fix/F09b-native-dependency`
- **覆盖**：#502、#579（支持 #290、#582 诊断）。**改动**：`LiteMonitor.csproj`、依赖锁定/构建配置、必要的固定源码补丁、`src/System/HardwareMonitor.cs`、`HardwareErrorLog.cs`、依赖集成测试。
- **步骤**：按母计划 F09 的 3～6 步——NVML 按设备身份重取句柄并处理驱动/设备变化（覆盖双卡/同型号，不按枚举索引）；DiskInfoToolkit 库侧逐项隔离设备事件/构造/回调边界，单设备失败有界退避、其他设备继续、退出可停止监听（应用层 catch 不替代）；源码补丁固定来源 SHA/补丁文件/许可证/构建步骤/产物摘要，或包锁定版本+传递依赖；日志含设备代次/操作/来源版本/错误类型，限频、不落序列号；无法库内隔离时**另立辅助进程隔离设计文档**，不在本轮引半套 IPC。
- **测试/验收**：受控子进程注入设备构造/回调异常主程序存活；采样中移除/恢复 GPU、无功耗能力、NVML 暂不可用均可恢复；磁盘事件监听停止后无残留线程。NVIDIA 实机（#502 关闭）与磁盘事件场景（#579 关闭）**待实机**；#582 缺原堆栈不声称覆盖。
- **报告**：`docs/verification/acceptance/F09b-report.md`。

### P-F10 — GPU 运行时映射自愈

- **批次/前置**：W4 / **F07 已合并**（在 F09b 之后或与其协调串行）。**分支**：`fix/F10-gpu-map-selfheal`
- **覆盖**：#434、#450、#596（区分 #592）。**只读材料**：TASK-2 报告（上游 `SensorMap.cs:141-143` + `HardwareMonitor.cs:587-622` 的 `RepairGpuSensorMap`，fork 无此机制只有 `GpuWarmup`）；总表 #592/#596 行。
- **改动**：`src/System/HardwareMonitor.cs`、`src/System/HardwareServices/SensorMap.cs`、`GpuWarmup.cs`、`HardwareRules.cs`、`HardwareValueProvider.cs` + GPU 测试。
- **步骤**：按母计划 F10 四步——区分"预期 Key 缺失"（触发串行重建）与"Key 在但 Value null"（仅记录，不重建）；保留预热，设备到达/配置变化/恢复事件投递重建需求，退避 1/2/5/10/30 秒最多 5 次、成功清零、耗尽等新事件（单调时钟可注入）；当前代次内建完整映射+GPU 注册快照原子发布、过期代次丢弃、重建不并发调 Update/Open/Close、不重置用户选卡；#592 的 GCL 不可用显示明确缺失原因+诊断入口，不以 D3D 显存冒充负载、不把 0 当恢复。
- **测试/验收**：传感器晚注册无需切卡即出现；Key 持续 null 无重建风暴；超时/关闭/代次变化后旧结果不发布；自动单卡/多卡/手动指定语义保持。Intel 核显与 B580 实测**待实机**；B580 缺 GCL 的正确缺失展示只算降级改善。
- **禁改**：`FanMapper.cs`、`MetricItem.cs`（F11 范围）。**报告**：`docs/verification/acceptance/F10-report.md`。

### P-F11 — 缺失/过期状态、多风扇与显存口径

- **批次/前置**：W5 / **F03、F07、F10 已合并**。**分支**：`fix/F11-missing-semantics-multifan`
- **覆盖**：#514、#577、#499、#592。**只读材料**：总表交叉发现 D（`value ?? 0.0f`）；TASK-2 的 #499/#514 节。
- **改动**：`src/Core/MetricItem.cs`、`MetricUtils.cs`、`src/System/HardwareServices/FanMapper.cs`、`SensorMap.cs`、`src/UI/UIController.cs`、GDI+/GPU/任务栏渲染文件、Web 快照/页面、`src/Core/HardwareHistoryLogger.cs`、`resources/lang/*.json`。
- **步骤**：按母计划 F11 五步——Fresh/Stale/Missing + reason（初始化/无传感器/策略禁用/读取失败/设备离线），数值与显示插值分离，旧 `DisplayValue=0` 不决定缺失语义；统一占位符+原因，真实 0 RPM/0%/0 B/s 保留，过期值不动画/不报警/不入统计与历史（历史用缺口）；Web 以兼容的新增状态/时间字段传达、数值缺失时为 null、同步内置客户端并记录外部消费者兼容要求；风扇按父设备+传感器稳定身份多槽位、`CASE.Fan` 保留兼容别名、歧义不随机覆盖、防双显；显存标识来源与口径（专用/共享/合计、分母一致），GPU 负载 0 显存非 0 属正常，来源切换不混代次。
- **测试/验收**：各状态在竖排/横排/任务栏/GPU 主题/Web 一致；双同名机箱风扇独立变化、重启不互换；旧主题/配置可用；#499 需对照截图与同口径解释，**确认显示缺陷才改计算**（报告中单列）。
- **报告**：`docs/verification/acceptance/F11-report.md`。

### P-F12 — 字体生命周期与 DPI 测量

- **批次/前置**：W1 / F00。**分支**：`fix/F12-font-measure-safety`
- **覆盖**：#574 结构风险（原崩溃待日志）。**只读材料**：TASK-4 报告（fork `HorizontalLayout.cs:57` + `UIController.cs:1909` 在绘制路径重新引入 `GetHeight`；上游全树零匹配）。
- **改动**：`src/UI/HorizontalLayout.cs`、`src/UI/UIController.cs`（仅字体测量路径）及主题/字体资源持有代码。
- **步骤**：按母计划 F12 三步——排查全部 `GetHeight`/`CreateGraphics` 调用与字体 Dispose 顺序（不按旧行号盲改）；测量移到受控布局阶段、有效字体+稳定 DPI 下计算并缓存（缓存键：字体/字号/单位/样式/DPI/主题代次），绘制帧只消费；不假设 `Font.Height` 等价 `GetHeight(graphics)`，先核对缩放与资源所有权；句柄不存在/销毁中推迟测量保留最小布局。
- **测试/验收**：高频切主题/缩放/跨屏/关闭重建句柄不访问已释放对象；100/150/200% 无行高裁切；空状态最小高度保留。#574 原崩溃**待日志对应**，本包只关闭结构风险。
- **禁改**：`UIController.cs` 中非字体测量区域。**报告**：`docs/verification/acceptance/F12-report.md`。

### P-F13 — 插件步骤失败兜底与倒计时恢复

- **批次/前置**：W1 / F00。**分支**：`fix/F13-plugin-fallback`
- **覆盖**：#611（回归 #235）。**只读材料**：总表交叉发现 F（fork 三处能力净减少：`continue_on_error` 删除→`native://newyear` 删除→`Holiday.json` 降级 v1.0 且 `https`→`http`）；TASK-5 报告 #611 节。
- **改动**：`src/Plugins/PluginModels.cs`、`PluginExecutor.cs`、`src/Plugins/Native/`、`resources/plugins/Holiday.json`、插件开发文档、`tests/LiteMonitor.Tests/PluginExecutorSandboxTests.cs`。
- **步骤**：按母计划 F13 五步——恢复步骤级 `continue_on_error`（默认 false，仅业务可恢复失败进入下一步）；外层取消/总截止/沙箱违规不可吞掉，区分单请求超时与总预算耗尽，备用步骤同预算；临时 context、成功才合并变量、失败不留半成品误触发 `skip_if_set`；回补 `native://newyear`（本地日期+可注入时钟）、恢复 Holiday HTTPS 主源与元旦倒计时备用输出（不冒充法定节假日数据）；主源恢复自动回切、兼容既有配置与插件身份、不绕过 native 白名单与资源限制。
- **测试/验收**：主源正常/null/超时/非法 JSON/备用成败/主源恢复全矩阵；取消与沙箱违规仍终止；跨年 12-31/1-1、闰年、时区、变量提交测试；#235 既有测试全部保持。
- **报告**：`docs/verification/acceptance/F13-report.md`。

### P-F14a — 市场页缓存与切页性能

- **批次/前置**：W2 / F00。**分支**：`fix/F14a-market-page-cache`
- **覆盖**：#437 卡顿半项。**只读材料**：TASK-1 报告 #437 节（fork `PluginMarketPage.cs:65-90` 每次 `OnShow` 全量重建）；注意**当前基线已含主题市场**，页面同时承载插件/主题两类资产。
- **改动**：`src/UI/Settings/PluginMarketPage.cs`、`ThemePreviewService.cs`。
- **步骤**：按母计划 F14-A 三步——内容签名（资产类型/索引版本/安装状态/选中项/语言/DPI/配色/请求代次），相同签名重进页面不销毁重建控件；固定控件/列表数据/进度错误态/预览生命周期分离，安装卸载只更新受影响行；插件+主题两页签都覆盖，离开页面暂停/释放唯一活跃 GPU 预览、返回恢复，旧异步回调不得写入新页面代次。
- **测试/验收**：缓存命中时控件创建/销毁计数为 0；状态/语言/DPI 变化及时刷新；100 次切页控件/句柄无持续增长；离开页面无活跃实时预览；固定 100 项 fixture 记录首次与重复打开耗时，重复打开 UI 阻塞 p95 ≤ 100ms 且同机前后对比留证。
- **禁改**：`LiteUI.cs`（归 F14b）。**报告**：`docs/verification/acceptance/F14a-report.md`。

### P-F14b — 设置页明暗主题

- **批次/前置**：W3 / F00。**分支**：`fix/F14b-settings-theme`
- **覆盖**：#437 暗色半项。**只读材料**：TASK-1 报告（`LiteUI.cs:9-25` 暗色未实现）。
- **改动**：`src/UI/Controls/LiteUI.cs`、`src/UI/Settings/SettingsBase.cs`、`src/UI/SettingsForm.cs`、语言与设置配置文件。
- **步骤**：提取设置控件统一配色；跟随系统/浅色/深色三态；与悬浮窗皮肤区分；覆盖禁用/选中/错误/提示/菜单/主题预览边界；Draft-Commit；8 语种资源同步。
- **测试/验收**：全部设置页 + 系统配色切换实测；不以"只改页面背景色"认定完成；#437 两个半项分别验收、分别提交。
- **禁改**：`PluginMarketPage.cs`。**报告**：`docs/verification/acceptance/F14b-report.md`。

### P-F15 — 取证工具与长跑 harness（工具型，不含结论）

- **批次/前置**：W1 起持续 / F00。**分支**：`tools/F15-forensics-harness`
- **覆盖**：#416、#78、#582、#574、#592 的**取证能力**（不是修复）。
- **改动**：新建 `tools/forensics/`（或并入 `scripts/`）：私有字节/句柄/GDI/线程 72h 采样脚本与时间线记录格式（#78，工作集仅并行记录不作依据）；WPA/xperf 采集脚本与分场景开关清单（#416：应用关闭/默认功能/SMB/FPS/GPU 采样/ETW 排除）；`LiteMonitor++_Error.log` 收集 runbook 含旧日志名兼容查找（#574/#582）；B580 检查清单（驱动/GCL 能力/LHM `GetReport()` 采集步骤，#592）。
- **约束**：输出不含口令/票据/完整序列号；所有脚本可从干净检出运行；长跑与实机步骤写明由人在目标机执行，计划文档完成 ≠ 72h 观察完成。
- **验收**：每份脚本/runbook 在本机干跑一次（不采集真实 72h 数据），语法与参数自检通过。
- **报告**：`docs/verification/acceptance/F15-report.md`（列出每个 issue 的下一步采集责任人动作）。

---

## 5. 协调者任务（不派发）

### 5.1 F00 基线固化（W0，派发前）—— ✅ 已完成 2026-10-25，产物见 [F00-baseline.md](../verification/acceptance/F00-baseline.md)

1. ✅ 版本存档：HEAD `98c9237`；`2a1c57d..HEAD` 源码改动仅主题市场 M5/M6 的 3 个文件，33 个任务目标文件中 32 个与验证基线逐字节一致，唯一漂移 `ThemePreviewService.cs`（F14a 须重定位行号）。
2. ✅ 探针入库：WebProbe 迁至 `tests/WebProbe/`（原样未改），`run-probes.ps1` 新增 `web` 组（构建主工程→跑 12 用例→证据落 `docs/reviews/evidence/appearance-probes/WebProbe/`）；冒烟 PASS，C5/C6=200、C7=403 的"修复前"锚点已落盘。Win32 探针在 `.tmp` 中本不存在，由 P-F04/F05/F06 自建测试宿主。
3. ✅ `docs/verification/acceptance/` + [TEMPLATE.md](../verification/acceptance/TEMPLATE.md) 已建。
4. ✅ 静态门禁值核验：`MIN_TEST_TOTAL=1876`（ci.yml:90 / release.yml:67）、`MAX_SKIPPED_TESTS=1`（ci.yml:100 / release.yml:70）。

### 5.2 批次集成（每批）

1. 按依赖序合并分支；`HardwareMonitor.cs`/`Settings.cs` 冲突由协调者裁决。
2. 每合入一批：Debug+Release 构建、三个测试项目全量跑、`scripts/run-probes.ps1` 相应组；按新增用例数**统一上调**两个 workflow 的 `MIN_TEST_TOTAL`（CI 与 Release 逐字一致），`MAX_SKIPPED_TESTS` 不得放宽。
3. 更新母计划 §7 覆盖表状态为：代码完成 / 待实机 / 已验证 / 已缓解 / 仍需调查。

### 5.3 F16 收尾

更新覆盖表、已知限制、发布说明；待实机清单逐条列给用户；不把"待实机"写成"已修复"。

---

## 6. 验收记录模板

```markdown
# F0x 验收记录 —— <标题>
- 分支 / 提交 SHA：
- 基线 SHA（开工时 HEAD）：
- 覆盖 issue：
- 用例命令（可从干净检出复现）：
- 环境（OS/DPI/GPU/关键依赖版本）：
- 实际结果（含测试发现数变化）：
- 未覆盖场景（待实机清单）：
- 结论：□ 代码层验收通过 □ 部分通过（原因） □ 阻塞（原因）
- 允许关闭的 issue 与条件：/ 不允许关闭的原因：
```

---

## 7. 与母计划的差异清单（本拆解引入的决定）

1. F08/F09/F14 各拆两半；F08b 内置库能力 spike 决策门；F09a 为纯研究包，产物是决策记录而非代码。
2. 明确 F03 与 F08a 可并行但 `HardwareMonitor.cs` 编辑限域 ≤30 行、先并 F03 再 rebase F08a；硬件实现链 F07→F08b→F09b→F10→F11 严格串行。
3. CI 阈值调整权收归协调者批次集成时，避免并行包互相冲突或私自放宽。
4. 全部文件补全为绝对路径（母计划为裸文件名，已逐一核验存在）。
5. 验收记录落位 `docs/verification/acceptance/`；F15 定位为取证工具交付，实机数据采集由用户在目标机执行。
