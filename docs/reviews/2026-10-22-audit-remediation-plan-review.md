# LiteMonitor++ 全面修复与工程加固执行计划：独立评审

日期：2026-10-22。评审结论：**NEEDS-REVISION**——技术判断整体可靠、根因描述准确，但有 6 项阻断级缺陷必须先修订，否则按此执行会产生覆盖率倒退、静默空跑和返工。

审查对象：用户提交的《LiteMonitor++ 全面修复与工程加固执行计划》（本轮附件）。该计划文本 SHA256：`2EDBE814345406CACA0CE2A37D5C5CF7A09EBE9A5FAB8412C48C845189EC21F7`。

审查边界：本次只读源码、配置与既有文档，并实际运行了测试与构建；**未修改任何产品源码、未提交、未启动 GUI、未提权、未访问网络仓库**。下述行号对应审计基准 `9ffb4219cad9b468ec748c547039da22ab9e83be`（已核实该 SHA 等于本地 `master` 等于 `origin/master`）。

---

## 一、结论摘要

计划的技术直觉是对的：我逐条核对了它点名的每一处缺陷，**根因描述准确、没有臆造缺陷**，这在计划类文档里并不常见，值得保留。但计划建立在一个错误的仓库现状认知上——它认为"测试项目丢失、需要从旧仓库恢复"，而实际是"测试套件完整存在且全部通过，只是没有进版本控制、也没进解决方案"。照 Task 0.2 字面执行会把 49 个本地测试文件的优势让给旧仓库的 13 个文件。

另外有 3 个问题会直接导致"假绿"：解决方案级 `dotnet test` 静默空跑且退出码为 0；Release 构建被运行中的实例锁死而无对应步骤；客户端验签早已实现而计划要求"实现"它。

| 类别 | 数量 | 条目 |
|---|---|---|
| 阻断级 | 6 | A1–A6 |
| 建议补充 | 8 | B1–B8 |
| 技术断言核对 | 11 条 | 8 条属实、2 条部分属实/需改写、1 条已过时 |

---

## 二、技术断言核对

计划没有编造缺陷，这是它最大的优点。以下逐条给出实测依据。

| 计划条目 | 计划描述 | 实测依据 | 结论 |
|---|---|---|---|
| Task 1.1 | 计时器 `AutoReset=false` 导致调度永久停止 | `src/Plugins/PluginManager.cs:363-364` 设 `AutoReset = false`；`:371-375` 不可见分支在 `try` 之前直接 `return`；而重启计时器的 `finally` 位于 `:417-423`。不可见路径永不进入 try/finally，计时器就此停摆 | **属实，根因描述精确** |
| Task 1.2 | 合并键基于 `GetHashCode()` | `src/Plugins/PluginExecutor.cs:284` `(url + "\|" + body).GetHashCode().ToString("X")` | **部分属实**：真实缺口是方法/Header/代理未入键 + 32 位进程内非稳定哈希；但 `:285` 的键**已含** `inst.Id` 与 `step.Id`，计划把"插件实例/步骤身份"列为待加项属于误判 |
| Task 1.3 | `HttpResponseMessage` 未释放 | `src/Plugins/PluginExecutor.cs:509` `var response = await client.SendAsync(request, token);` 无 `using` | **属实** |
| Task 2.1 | 动态 HTML 注入入口 | `WebPageContent.cs:483` 直接插值 `${grp.name}`；`:518`、`:547`、`:581` 插值 `${item.n}` | **属实** |
| Task 2.2 | 监听范围过宽 | `LiteWebServer.cs:190` `IPv6Any`、`:197` `IPAddress.Any`；`WebSessionManager.cs:894` 对**所有**响应返回 `Access-Control-Allow-Origin: *`；全文件无 Origin / Host 校验 | **属实** |
| Task 2.2 | "HTTP 和 WebSocket 都执行统一鉴权" | `WebSessionManager.cs:747`（HTTP 分支）与 `:1235`（WS 握手）均已调用 `CheckAuth(request)`，失败返回 401 | **已过时，无需修复**——改写为"已统一，本项仅做回归保护" |
| Task 2.3 | 密码写入 localStorage 与查询串 | `WebPageContent.cs:303` 读、`:351` 写 `localStorage['ws_pwd']`；`:335`、`:362`、`:393` 走 `?pwd=` | **属实** |
| Task 3.1 | 下载地址校验不足 | `PluginMarket.cs:145` 仅 `url.StartsWith(prefix, OrdinalIgnoreCase)`，无 `Uri` 解析、无路径规范化；`:128-131` 静态 `HttpClient` 默认跟随重定向且不复核最终目标；`:286` `GetByteArrayAsync` 无大小上限 | **属实** |
| Task 3.2 | 插件需强制 SHA-256 | `PluginMarket.cs:290` 已在校验，但条件是 `!string.IsNullOrEmpty(entry.Sha256)`——即**可选** | **已存在**，任务是"可选改必填"，不是"从无到有" |
| Task 6.1 | 需实现签名与完整性校验 | `src/System/UpdateChecker.cs:115-160` 已实现 ECDSA P-256 验签且失败即拒，下载后比对期望摘要；`tests/LiteMonitor.Tests/UpdateSignatureTests.cs`（14 KB）已覆盖。真实缺口是 `resources/version.json` 的 `sha256` 与 `signature` **均为空字符串** | **已过时**——客户端侧无需重写，缺口在"未签署发布" |
| Phase 7 | 主程序 `requireAdministrator` | `app.manifest` 确为 `level="requireAdministrator"` | **属实** |

---

## 三、阻断级问题（A1–A6）

### A1 · 阻断：Task 0.2 的前提是错的，照做会造成覆盖率倒退

计划要求"重点检查旧仓库 `mojingjing-git/LiteMonitor/tests/LiteMonitor.Tests/`，对可复用的测试进行筛选、迁移和适配"。实测：

- 旧仓库（`fork-source/master`）该目录只有 **13 个 `.cs`**（15 个文件，含 csproj 与 xunit.runner.json）。
- 本地工作区 `tests/LiteMonitor.Tests` 已有 **64 个 `.cs`**。
- **旧仓库的 15 个文件全部已存在于本地**（逐文件比对，无一缺失）。

也就是说本地套件是旧仓库的超集，且已额外覆盖 `WebAuthHardeningTests.cs`（29 KB）、`UpdateSignatureTests.cs`、`SamplingCoordinatorTests.cs`（44 KB）、`PluginMarketTests.cs`（21 KB）、`UpdaterRecoveryTests.cs`、`WebSamplingDemandTests.cs`（103 KB）等大量与 Phase 1–6 直接相关的文件。按"从旧仓库筛选迁移"执行，最坏情况是用 13 个文件覆盖掉 64 个。

**最小修订：** Task 0.2 主路径改为"把现有 `tests/`、`scripts/`、`docs/` 纳入版本控制"，删除"从旧仓库迁移/筛选测试"作为主路径的表述；旧仓库仅作为"缺失项补充"来源。计划 0.2 中"不允许盲目复制旧测试"一句是对的，但结论应改为"本地测试是权威来源"。

### A2 · 阻断：`LiteMonitor.sln` 不含测试工程，解决方案级 `dotnet test` 是静默空跑

`LiteMonitor.sln` 只有两个 `Project` 条目：`LiteMonitor` 与 `LiteMonitor.Updater`。实测 `dotnet test LiteMonitor.sln`：

```
EXITCODE=0
（无任何测试输出）
```

即**零测试被发现，且退出码为 0**。后果有三：

1. 计划 0.2 的验收条件"`dotnet test` 能真实发现并运行测试"当前无法通过；
2. Task 6.3 CI 第 3 步 "Unit Test" 将**恒为绿但不测任何东西**——这正是计划自己警惕的"以 exit 0 判通过"陷阱，plan 却没有对应的实施步骤；
3. 计划全篇没有任何任务负责把测试工程加入 `.sln`。

**最小修订：** Task 0.2 增加显式子步骤"将 `tests/LiteMonitor.Tests`、`tests/LiteMonitor.Updater.Tests`、`tests/LiteMonitor.GpuNonperformance.Tests` 加入 `LiteMonitor.sln`"；把验收条件改为可机器判定的形式——"`dotnet test LiteMonitor.sln` 输出中必须出现 990 量级的测试计数，0 发现即判 FAIL"。

### A3 · 阻断：基线分支与在途工作流的关系完全未交代

仓库有 4 个 remote（`fork-source` / `origin`=LiteMonitorPlus / `upstream`=Diorser / 本地 master），`master == origin/master == 9ffb4219`，与计划的审计基准一致——这点没问题。但：

- 已有分支 `codex/full-fork-remediation`，与 master 相差 **484 个文件（+114,530 / −21,869）**，其中包含 `fix(security): 恢复 TLS 证书校验 + Web 认证加固`、`feat(updater): 更新包签名校验 + 覆盖失败回滚`、`fix(system): validate plugin ownership` 等与本计划高度重叠的提交；
- 另有 `codex/performance-sampling` 及**在途 worktree** `.worktrees/codex-integrate-performance-sampling`。

计划却写"在 `master` 上创建 `fix/audit-remediation-2026` 分支"，等于默认从"缺少 484 文件修复的 master"切出新分支。

**好消息（已核实）：** remediation 分支与本计划 5 个目标文件 **零差异**——`git diff master codex/full-fork-remediation -- src/Plugins/PluginManager.cs src/System/WebServer/WebPageContent.cs src/Plugins/PluginExecutor.cs src/System/WebServer/LiteWebServer.cs src/Plugins/PluginMarket.cs` 全部无差异。**无直接冲突**，两条线可并行。

**最小修订：** Task 0.1 写明三件事——(a) 以哪个分支/提交为修复基线；(b) 是否先合并 `codex/full-fork-remediation`，或明确接受"该分支修复不进本轮"的后果；(c) 在途 worktree 与本轮是否冲突、谁先落地。另需明确 `origin`（LiteMonitorPlus）才是推送目标。

### A4 · 阻断：Task 6.1 误判现状，会诱发重写已验证代码

客户端验签在 master 里**已完整实现**：`UpdateChecker.cs:115-160`（ECDSA P-256，失败即拒，不降级）、`:404-410`（验签失败即拒该源）、`:285`+`:307`（下载后比对期望摘要），配套 `UpdateSignatureTests.cs` 14 KB。

计划却把"客户端继续拒绝空摘要、无效签名和不匹配的 ZIP"写成待办要求，字面读起来像"尚未实现"，执行者可能重写这块已被充分测试的代码——这与计划 0.2 "禁止无关重构"自相矛盾。

真实缺口只有两个：已发布的 `resources/version.json` 的 `sha256`/`signature` 为空；没有真实签名产物。

**最小修订：** Task 6.1 拆为：

- **6.1a（低成本，应提前）** 用本地已存在的 `secrets/update-signing-key.pkcs8` + `scripts/sign-release.ps1` 对真实 Release 产物签名，产出非空 `version.json` 并发布。注意 `scripts/` 目前被 gitignore，签名脚本不在版本控制内。
- **6.1b（Phase 6）** CI 中接入签名 + 增加"`version.json` 摘要与签名非空且可验签通过"的发布前自检。
- 明确写入：**不得改动 `UpdateChecker` 现有验签实现**，其行为以 `UpdateSignatureTests.cs` 为回归基准。

### A5 · 阻断：Release 构建被运行中的实例锁死，计划无对应步骤

实测：`LiteMonitor++.exe`（PID 24936，18:00:35 启动）正在运行并锁定 `bin/Release/net8.0-windows/LiteMonitor++.exe`，`dotnet build -c Release` 重试 10 次后失败：

```
error MSB3027: 无法将 apphost.exe 复制到 LiteMonitor++.exe。超出了重试计数 10
error MSB3021: 文件被"LiteMonitor++.exe (24936)"锁定
```

计划 0.2 的验收要求"`dotnet build LiteMonitor.sln -c Release` 能完成"，Task 6.3 又需要 Release publish + 打包 + 签名，但 Task 0.1/0.2 **没有任何"处理运行中实例"的步骤**。

**最小修订：** Task 0.1 增加"确认并停止占用 Release 输出的实例，或改用独立发布目录（如 `.artifacts/publish`）"，并把该项列为 Phase 0 的前置条件。

### A6 · 阻断：Task 6.1 的验收条件不可验证

验收写的是"**旧版客户端**能够验证新版本"。但产品当前 `version.json` 版本为 `1.0.0`，且从未发布过已签名版本——**不存在"旧版客户端"这个基线**，该条无法执行、无法举证。

**最小修订：** 改为三条可执行断言：(a) 用仓库内公钥对发布的 `version.json` 验签通过；(b) 空摘要、错误签名、错配摘要必然被拒（已有 `UpdateSignatureTests.cs` 可扩展）；(c) 下载后的文件摘要与元数据一致。

---

## 四、建议补充项（B1–B8）

### B1 · 缺缺陷台账，优先级标签不完整
8.3 要求交付"全部缺陷的状态矩阵"，但计划正文**没有任何稳定缺陷 ID**，只能靠 Task 编号近似替代。建议为每条缺陷分配 ID（R-01…）并把每个 Task 挂到 ID 上。同时补齐优先级标签：Task 1.3、2.3、3.2、3.3、6.2、6.3 **完全没有优先级**，导致计划 0.3 的 P0–P3 定义无法机械推导 M0–M6 的执行顺序。

### B2 · Task 1.2 缺实现落点与联动说明
"取消一个等待者不应误取消其他等待者"要求在途请求登记表维护**等待者计数**，计划未提出该设计要求。另一处联动未提：`PluginManager.cs:391-396` 在连续失败第 3 次时调用 `ResetNetworkClients()` + `ClearCache()`，这会打断其他实例共享的在途请求；Task 1.2 改动键之后共享面会变化，两处需一并设计。

### B3 · Task 2.1 的验收缺测试载体
`WebPageContent` 是 C# 字符串拼出的 JS，仓库**没有 DOM 测试设施**。计划的测试输入清单（HTML 标签、事件属性载荷等）无法直接断言"只能作为文本显示"。须三选一并写死：(a) 结构级断言（生成的 JS 中不得存在对用户数据的 `innerHTML` 插值）；(b) 引入 JSDOM/Node 执行片段；(c) 用 Playwright 跑真实页面。不写死则"验收"极易被形式化通过。可用的现成回归网是 `tests/LiteMonitor.Tests/WebThemeCssTests.cs`（24 KB）。

### B4 · Task 2.2 是破坏性默认变更，却缺配置迁移条目
`Settings.cs:170` 的 `WebServerPassword` 默认为空串，`WebServerEnabled` 默认为 `false`（计划"现有 Web 默认关闭保持不变"这句**属实**）。但存量用户若 `WebServerEnabled=true` 且未设密码，切换到"默认仅环回"后远程访问直接失效。计划 0.2 明确要求"保留原有配置兼容性，必要时提供可回退的配置迁移"，而 Task 2.2 没有对应的迁移任务。

**建议：** 新增配置项（如 `WebServerBindMode` / `WebServerLanEnabled`），并定义双默认——存量配置维持旧行为、新安装默认环回；LAN 模式缺密码时拒绝启动并给出明确提示。

### B5 · Task 2.3 需要票据设计与日志脱敏
浏览器 WebSocket 无法自定义 Authorization 头，计划提出"短期会话票据"方向**正确**，但需写明：票据 TTL、是否一次性、绑定与失效规则、服务端校验路径、票据不得进入访问日志与 `Referer`。同时要给出"仅知道密码的老客户端"的兼容路径。另注意 `WebSessionManager.cs:380` 的 `PwdQueryRegex` 同时接受 `?pwd=` 与 `&pwd=` 两种形态（`:858` 已拒重复参数），改票据后这套解析需一并收敛，避免留下两套鉴权口径。

### B6 · Phase 4 / 5 多数条目是"验证"而非"修复"，缺可判定的交付物
4.2 的 9 行场景矩阵、4.3 的 10 项指标、5.1、5.2 都可能全部 PASS（本来就没坏）。若不定义"确认无缺陷"的合格交付物，执行者会为了产出 FAIL 去"修"没坏的地方——**正好违反计划 0.2 的"禁止无关重构"**。建议 Phase 4 拆为：(a) 调用图与生产路径清单（产物化）；(b) 逐条确认后的缺陷修复任务；(c) 验证矩阵，未覆盖硬件的场景标 `NOT_VERIFIED`。另 4.3 的"GPU 显存占用""USER Objects"需指定具体计数器/API，否则不可测。

### B7 · 未复用既有资产
本地已有 `.review-scratch/full-review/updater-fault/UpdaterFault.csproj`（正是 Task 6.2 的故障注入）、`.review-scratch/full-review/runtime-repros/`，以及 `scripts/` 下的 `sign-release.ps1`、`pack-release.ps1`、`run-probes.ps1`、`run-gpu-contracts.ps1`。Task 6.2、6.3 应改写为"改造并纳入版本控制"，而不是"从零建立"。

### B8 · 计划完全未提及的检查项
- Web API 若存在写操作则需 CSRF 评估（当前快照接口为 GET 读取，风险低，但应在计划中显式记录该结论而非留白）；
- `?pwd=` 的 Referer 外泄（页面若加载任何外部资源，完整 URL 会被带出去）；
- CI 中签名密钥的分发方式（目前只有本地 `secrets/update-signing-key.pkcs8`，GitHub Actions 需明确 secret 或受信任发布流程）；
- Task 0.1 未指定以哪个 remote 作为 master 的真相源。

---

## 五、验证结果与边界

| 检查 | 方式 | 结果 |
|---|---|---|
| 测试基线 | `dotnet test tests/LiteMonitor.Tests -c Debug` | 通过 **983** / 失败 0 / 跳过 7 / 共 990（连续两次一致，1m18s–1m21s） |
| 跳过用例归属 | 同上输出 | 7 条全部是 `MarketIndexContractTests` / `MarketE2ETests`，依赖真实插件索引仓库，本机无仓库上下文而跳过——**Task 3.1 目前无生效回归覆盖** |
| Release 构建 | `dotnet test -c Release` | **失败**：`LiteMonitor++.exe (24936)` 锁定产物，MSB3021 / MSB3027 |
| 解决方案级测试发现 | `dotnet test LiteMonitor.sln` | **EXITCODE=0，零测试输出**（静默空跑） |
| 旧仓库测试规模 | `git ls-tree fork-source/master -- tests` | 13 个 `.cs`；15 个文件**全部已在本地** |
| 本地测试规模 | `tests/LiteMonitor.Tests` | 64 个 `.cs` |
| remediation 分支冲突 | `git diff master codex/full-fork-remediation -- <5 个目标文件>` | 全部无差异，**无直接冲突** |
| 基准一致性 | `git rev-parse master origin/master` | 均为 `9ffb4219` = 计划审计基准 |
| 工具链 | `dotnet --version` | 8.0.426（net8.0-windows） |

**未验证边界（不得当作通过）：**

- Phase 4 的 GPU / 桌面捕获实机路径、DPI、多显示器、待机唤醒——本次**完全未验证**，且当前 Release 构建被锁死；
- Phase 7 的权限拆分——未验证，且不在本轮范围；
- 7 条依赖真实插件索引仓库的用例——覆盖为零；
- 首次 Debug 构建曾返回非零退出码，因输出过滤未捕获到内容（最可能由其前一次失败编译遗留的中间产物引起），**该次失败原因未取证**，但随后两次连续运行完全一致，故基线判定为稳定。

---

## 六、修订后建议执行顺序

| 里程碑 | 内容 | 说明 |
|---|---|---|
| M0' 修订 | A1–A6 全部改写 + B1 缺陷台账 | 先改计划，再动手 |
| M0 基线 | `tests/` `scripts/` `docs/` 入库 + `.sln` 补测试工程 + 记录 983/7 基线 + 停止占用 Release 的实例 | A1/A2/A5 |
| M1 | Task 1.1 | 唯一确认的 P1 功能缺陷，根因最清晰，可独立验收 |
| M2 | Task 2.2 + 2.3（含配置迁移）、Task 6.1a | P1 提前；6.1a 成本低且不依赖 GPU 工作 |
| M3 | Task 2.1 / 1.2 / 1.3 / 3.1 / 3.2 | 安全输入面收口 |
| M4 | Phase 4 / 5 | 验证型，产出逐条判定结论而非修复 |
| M5 | Task 6.2 / 6.3 | 复用既有故障注入与脚本 |
| M6 | Phase 7 | 独立设计与独立测试 |

计划其余部分（证据驱动、测试先行、失败即拒绝、`NOT_VERIFIED` 标注、禁止无关重构、交付报告格式）**建议原样保留**——这些是本计划最有价值的部分，与 A/B 各项冲突不大。
