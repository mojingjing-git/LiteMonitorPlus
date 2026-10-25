# F01 验收记录 —— WebSocket 升级识别改结构化解析（WEB-OWS；防回归 #589）

## 1. 标识

- 工作包：P-F01
- 分支：`fix/F01-ws-upgrade-parse`
- 提交 SHA 列表（`git log --oneline d52c0ab..fix/F01-ws-upgrade-parse`）：
  - `ad90068` `test(F01): 新增 WebSocket 升级结构化解析的裸 TCP 集成用例（红）`
  - `ecee089` `fix(F01): WebSocket 升级识别改结构化解析（绿）`
  - `（本文件）` `docs(F01): 验收记录 F01`
- 基线 SHA（开工时 HEAD）：`d52c0ab`（master，F00 基线固化）
- 完成日期：2026-10-25
- 执行者：外派执行智能体（Worker 会话 `mvs_165720155f284ae8ac099f2f8d756beb`），工作目录 `G:\TOOL\LiteMonitor-fork\.worktrees\f01`
- 改动文件（均在派发单授权范围内）：
  - `src/System/WebServer/WebSessionManager.cs`（改动：+346 / −61）
  - `tests/LiteMonitor.Tests/WebSocketUpgradeParseTests.cs`（**新建**，裸 TCP 集成测试）
  - `docs/verification/acceptance/F01-report.md`（本文件，新建）
- 未触碰：`WebServerAccessPolicy.cs`、`LiteWebServer.cs`、`.github/workflows/ci.yml`、`release.yml`、`resources/lang/*.json`、`src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、`src/UI/UIController.cs`、`src/System/HardwareMonitor.cs`。

## 2. 覆盖与结论

- 覆盖 issue：
  - **WEB-OWS**（新发现，fork 回归）：Upgrade 头的 OWS 容忍度退化 —— 合法请求 `Upgrade:websocket` / `Upgrade:  websocket` / `Upgrade:<TAB>websocket` 被静默降级成 **200 + 登录页 HTML**，浏览器侧表现为「网页端连不上 / 灰屏」。
  - **#589 防回归**：`Sec-WebSocket-Key` 头名大小写不敏感（既有修复必须保持）。
  - 附带覆盖（拆解计划 §4 P-F01 第 2～5 步的规格项）：请求行校验、头名/取值规范化、折行与冒号前空白拒绝、`Connection` 令牌判定、`Upgrade` 协议令牌精确匹配、列表头合并 vs 单值头禁重复、歧义长度头、无效握手回明确 4xx。
- 结论：**☑ 代码层验收通过**
- 允许关闭的 issue 与条件：
  - WEB-OWS：**代码层已闭环**（C5/C6 由 200 变 101，且新增 OWS/Tab/尾部空白/多令牌/拆包用例锁死）。
  - #589：**保持既有修复**（四种头名大小写全部 101）。
  - ⚠️ **"代码层验收通过" ≠ "可关闭上游 issue"**：WEB-OWS 是 fork 内部新发现编号，无对应上游 issue；#589 属上游 issue，关闭与否仍按母计划 §7 由协调者/用户裁定。
- 不允许关闭的原因：n/a（本包不含"待实机"项）。

## 3. 用例与命令（可从干净检出复现）

### 3.1 端到端 WebProbe（修复前 → 修复后对照）

命令：`pwsh scripts/run-probes.ps1 -Groups web -SkipUnitTests`
证据：`docs/reviews/evidence/appearance-probes/WebProbe/run-output.txt`（`docs/reviews/evidence/` 被 `.gitignore` 忽略，证据随跑随落盘）

| 用例 | 修复前（F00 基线） | 修复后（本包） | 判定 |
|---|---|---|---|
| C1 基准（标准头） | 101 | **101** | 保持 |
| C2 #589 头名全小写 | 101 | **101** | 保持 |
| C3 #589 头名全大写 | 101 | **101** | 保持 |
| C4 #589 头名混合大小写 | 101 | **101** | 保持 |
| **C5 Upgrade 无空格** | **200 + 登录页 HTML** | **101 Switching Protocols** | ✅ **修复（WEB-OWS 红灯转绿）** |
| **C6 Upgrade 两个空格** | **200 + 登录页 HTML** | **101 Switching Protocols** | ✅ **修复（WEB-OWS 红灯转绿）** |
| C7 #580 Host=隧道域名 | 403 | 403 | 保持（**P-F02 的红灯基线，不在本包范围**） |
| C8 Origin=外部站点 | 403 | 403 | 保持 |
| C9 Key 形态非法 | 400 | 400 | 保持 |
| C10 Key 头重复 | 400 | 400 | 保持 |
| C11 无票据 | 401 | 401 | 保持 |
| C12 X-Forwarded-Host=隧道域名 | 101 | 101 | 保持（转发头仍被忽略 ⇒ **P-F02** 的范围） |
| `host_diag`（8 个 Host 变体） | 401×5 / 403×3 | 401×5 / 403×3 | 逐行一致 |
| `ws_host_diag`（4 个 Host 变体） | 401×3 / 403×1 | 401×3 / 403×1 | 逐行一致 |

### 3.2 构建与测试

| 命令 | 目的 | 结果（发现数/通过/失败） |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | 构建 | 0 错误（301 警告，与基线同量级，均为既有 nullable/未等待告警） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build` | 主测试 | **1830 总数 / 1823 通过 / 0 失败 / 7 跳过**（跳过项全在 `MarketIndexContractTests` / `MarketE2ETests`，需外网，与本包无关） |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/… --no-build` | GPU 非性能测试 | **19 / 19 通过 / 0 失败** |
| `dotnet test tests/LiteMonitor.Updater.Tests/… --no-build` | Updater 测试 | **73 / 73 通过 / 0 失败** |
| `dotnet test … --filter "FullyQualifiedName~WebSocketUpgradeParseTests"` | 本包新增用例 | **46 / 46 通过 / 0 失败** |
| `pwsh scripts/run-probes.ps1 -Groups web -SkipUnitTests` | Web 协议级端到端 | **PASS 1 / FAIL 0 / SKIP 1**（SKIP = `-SkipUnitTests` 指定的单测项） |

### 3.3 红→绿证明（新增用例在**修复前**的实现上确实失败）

做法：`git stash push -- src/System/WebServer/WebSessionManager.cs`（只回退产品实现，保留新用例）→ 重新构建 → 只跑新用例。

- 修复前：**46 个用例中 20 个失败 / 26 个通过**（失败清单见下）
- 修复后：**46 个用例全部通过**

修复前失败（红）的 20 条（逐条都是"旧实现确实错"的证据）：

| 失败用例 | 旧实现的实际行为 |
|---|---|
| `升级头的合法OWS形态一律进入握手(upgradeLine: "Upgrade:websocket")` | 200 + 登录页 HTML（**WEB-OWS 核心**） |
| `升级头的合法OWS形态一律进入握手(upgradeLine: "Upgrade:  websocket")` | 200 + 登录页 HTML（**WEB-OWS 核心**） |
| `升级头的合法OWS形态一律进入握手(upgradeLine: "Upgrade:\twebsocket")` | 200 + 登录页 HTML |
| `升级头的合法OWS形态一律进入握手(upgradeLine: "Upgrade: \t websocket")` | 200 + 登录页 HTML |
| `升级头的合法OWS形态一律进入握手(upgradeLine: "upgrade:WebSocket")` | 200 + 登录页 HTML（头名/令牌大小写不敏感未生效） |
| `畸形升级请求返回明确4xx(variant: "notWebSocketProtocol")` | 200 + 登录页 HTML（协议令牌被子串命中） |
| `畸形升级请求返回明确4xx(variant: "spaceBeforeColon")` | 200 + 登录页 HTML（冒号前空白未拒绝） |
| `畸形升级请求返回明确4xx(variant: "obsFold")` | 200 + 登录页 HTML（折行未拒绝） |
| `畸形升级请求返回明确4xx(variant: "noConnection")` | 101（**Connection 令牌完全没校验**） |
| `畸形升级请求返回明确4xx(variant: "noUpgradeHeader")` | 200 + 登录页 HTML |
| `畸形升级请求返回明确4xx(variant: "http10")` | 101（**HTTP 版本完全没校验**） |
| `畸形升级请求返回明确4xx(variant: "duplicateContentLength")` | 101（歧义长度头未拒绝） |
| `畸形升级请求返回明确4xx(variant: "transferEncoding")` | 101 |
| `畸形升级请求返回明确4xx(variant: "unsupportedVersion")` | 101（版本未校验） |
| `畸形升级请求返回明确4xx(variant: "duplicateVersion")` | 101（单值头重复未拒绝） |
| `伪造的XUpgrade头不得进握手` | **101**（伪造头子串命中 ⇒ 直接拿到 101） |
| `非GET方法的升级请求不得进握手` | 200（静默降级，非明确 4xx） |
| `报文里含GET字样的非GET请求不得进握手` | **101**（"GET" 子串匹配 ⇒ 非 GET 请求进了握手） |
| `超大头不得进握手` | **101**（8 KB 截断的头块仍被扫出 `Upgrade: websocket`） |
| `普通网页GET带XUpgrade头仍是200` | **101**（普通网页 GET 被误判成升级请求！） |

> ⚠️ 最后一行的旧行为值得单独记录：修复前**普通网页 GET 只要带一个 `X-Upgrade: websocket` 头，就会被当成 WebSocket 升级**并回 101 —— 这比 C5/C6 的"连不上"更隐蔽（前端会以为握手成功，然后卡在帧解析）。

## 4. 环境

- OS / 版本：Windows 11 专业版（10.0.26300.0）
- DPI / 显示器拓扑：本包不涉及（纯协议层；含 GameViewer / Meta / MuMu / Todesk 虚拟显示器）
- GPU / 驱动：本包不涉及（NVIDIA RTX 3080 + RTX 3080 Ti 及其他虚拟适配器，列此仅为与 F00 基线对齐）
- 关键依赖版本：.NET SDK 8.0.426；pwsh 7.6.6（探针必须用 pwsh 7）；LHM 0.9.6 + DiskInfoToolkit 1.1.2（本包未触碰）
- NuGet / CLI 隔离：`NUGET_PACKAGES=G:\TOOL\LiteMonitor-fork\.nuget-packages`、`DOTNET_CLI_HOME=G:\TOOL\LiteMonitor-fork\.dotnet-cli-home`
- 探针沙箱：`SettingsHelper.PathOverride` 指向 `docs/reviews/evidence/appearance-probes/WebProbe/settings`，**未触碰用户 settings.json**

## 5. 新增测试清单

文件：`tests/LiteMonitor.Tests/WebSocketUpgradeParseTests.cs`（**新建**，裸 TCP 集成测试）
新增发现数：**46**（`LiteMonitor.Tests` 总数 1784 → 1830）
全部走真实产品路径：裸 TCP → 真 `LiteWebServer` → 真 `WebSessionManager`；**不复制、不复刻任何产品判定逻辑**；不使用 `Thread.Sleep`（拆包用例靠"两次 write"天然分段）；每个用例有 90 s 外部超时保护；只连 127.0.0.1 回环；挂 `cfgfile` 集合串行执行。

| # | 用例 | 验证了什么 |
|---|---|---|
| 1 | `升级头的合法OWS形态一律进入握手`（9 个 InlineData） | 冒号后 0/1/2 空格、Tab、空格+Tab+空格、尾部 Tab、尾部双空格、头名大写、头名小写+协议令牌混合大小写 ⇒ 全部 101 且 Accept 与 RFC 示例一致。**旧实现其中 5 种回 200** |
| 2 | `Connection多令牌仍能进入握手`（5 个 InlineData） | `Connection` 列表头按逗号令牌判定：`Upgrade` / `keep-alive, Upgrade` / `Upgrade, keep-alive` / `keep-alive,Upgrade` / 三令牌 ⇒ 101 |
| 3 | `Connection拆成两个头行仍能进入握手` | 多个 `Connection` 头行按 RFC 7230 §7 合并 ⇒ 101 |
| 4 | `请求被拆成两包仍能进入握手` | TCP 拆包（拆点落在**头行中间**）⇒ 101；结构化解析不得依赖"一次读拿全报文" |
| 5 | `SecWebSocketKey头名大小写不敏感仍能进入握手`（4 个 InlineData） | **#589 防回归**：四种头名大小写 ⇒ 101 + Accept 正确 |
| 6 | `非法SecWebSocketKey返回400`（3 个 InlineData） | 非 Base64 / 3 字节 / 空值 ⇒ 400 |
| 7 | `重复的SecWebSocketKey返回400` | 单值头重复 ⇒ 400（不取第一个） |
| 8 | `缺失SecWebSocketKey返回400` | 头缺失 ⇒ 400 |
| 9 | `无票据的握手返回401` | 票据链：缺失 ⇒ 401 |
| 10 | `无效票据的握手返回401` | 票据链：不存在 ⇒ 401 |
| 11 | `票据重放返回401` | 票据链：用过即废（第二次 ⇒ 401） |
| 12 | `伪造的XUpgrade头不得进握手` | `X-Upgrade: websocket` ⇒ **不得**进握手（旧实现回 101） |
| 13 | `正文含Upgrade文本不得进握手` | 请求体里写 `Upgrade: websocket` ⇒ 不得进握手（仍走普通 HTTP 路由） |
| 14 | `非GET方法的升级请求不得进握手` | `POST` + 升级头 ⇒ 405（不进握手） |
| 15 | `报文里含GET字样的非GET请求不得进握手` | 路径含 "GET" 的非 GET 请求 ⇒ 405（旧实现回 101） |
| 16 | `超大头不得进握手` | 头块 > 8 KB（读取上限）⇒ 431，不进握手（旧实现回 101） |
| 17 | `畸形升级请求返回明确4xx`（10 个 InlineData） | `notwebsocket`（子串不得命中）/ 冒号前空白 / 折行 / 缺 Connection / 缺 Upgrade / HTTP/1.0 / 重复 Content-Length / Transfer-Encoding / 版本非 13 / 版本重复 ⇒ 全部 400（明确 4xx，不静默 200） |
| 18 | `非法Origin的升级请求返回403` | 错误优先级：Host/Origin(403) 先于协议形态(400) |
| 19 | `普通网页GET行为不变` | 普通 GET ⇒ 200 + 登录页 HTML（结构化分流不得误伤普通网页） |
| 20 | `普通网页GET带XUpgrade头仍是200` | 普通 GET + `X-Upgrade` 头 ⇒ 仍是 200（旧实现回 101） |

**统计口径**：46 个新增发现 = 9 + 5 + 1 + 1 + 4 + 3 + 1 + 1 + 1 + 1 + 1 + 1 + 1 + 1 + 1 + 1 + 10 + 1 + 1 + 1。
供协调者按批次集成时统一上调两个 workflow 的 `MIN_TEST_TOTAL`（**本包未改任何 CI 阈值**）。

## 6. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| 真实浏览器（Chrome / Edge / Firefox）经 `ws://127.0.0.1:5000/ws?ticket=…` 建连的端到端行为 | 实机浏览器 + 真实 LiteMonitor 实例 | 用户 |
| 真实反向代理（Cloudflare Tunnel 等）把 `Upgrade`/`Connection` 头改写后的形态 | 真实隧道 | 用户（P-F02 范围） |
| HTTP/2（h2）承载下的 WebSocket（RFC 8441） | 本服务是纯 HTTP/1.1，不支持 h2 ⇒ 不适用 | — |
| 客户端在 `Upgrade`/`Connection` 头里使用**非 ASCII** 字节（协议要求 ASCII） | 属非法输入，已按 400 覆盖 | — |
| 票据**过期**（TTL 30 s 到期）⇒ 401 | 由既有用例 `WebSessionProtocolTests.A3_票据过期后被拒` 覆盖（假时钟，无实机依赖） | — |
| 超大头（> 8 KB）的**普通 HTTP** 请求（无升级头）⇒ 当前仍走普通路由 | 属 HTTP 层策略，本包只约束"不得进握手"；是否改为 431 建议协调者裁定 | 协调者 |

## 7. 偏离与遗留

- 与派发单的偏离（含禁改清单例外申请及理由）：
  - **未修改 `tests/LiteMonitor.Tests/WebSessionProtocolTests.cs` 与 `WebSocketRejectCloseTests.cs`**：派发单把它们列为"可改"，但全部新增用例集中放进新建的裸 TCP 集成文件（授权范围内的"可新建裸 TCP 集成测试文件"），避免在既有大文件里插队、便于评审。
  - **未修改 `tests/WebProbe/Program.cs`**：探针程序不在本包"改动文件"清单内 ⇒ 只用它跑回归（未加用例）。探针的 12 用例矩阵在 C5/C6 上已足以证明红灯转绿；新增形态（Tab / 尾部空白 / 多令牌 / 拆包 / 伪造头 / 超大头）由新集成测试文件承载。
  - **新增 `Sec-WebSocket-Version` 校验（存在且非 13 ⇒ 400；重复 ⇒ 400；缺失 ⇒ 放行）**：拆解计划 §4 P-F01 第 4 步要求"版本校验"。**缺失放行**是为了不掐断既有兼容面 —— 既有 `WebAuthHardeningTests` 的合法握手用例本就**不发** `Sec-WebSocket-Version`（该文件不在本包授权清单内，不能改）。
  - **新增 431（Request Header Fields Too Large）**：头块被 8 KB 上限截断时的明确状态码。为此在 `GetReasonPhrase` 增加了一行 `case 431`。属同一文件同一区域，未越界。
  - **未改任何 CI 阈值、未改 `resources/lang/*.json`**（本包无用户可见文案 ⇒ 无新增 i18n key）。
- 发现的相邻问题（不属于本包，建议归属）：
  1. **`Sec-WebSocket-Version` 缺失目前仍被放行**：RFC 6455 §4.1 要求客户端必须带该头。既有 `WebAuthHardeningTests` 的握手构造器不发它，故本包不能收紧；建议由协调者决定是否统一收紧（届时需同步修改 `WebAuthHardeningTests.cs`）。
  2. **`X-Forwarded-Host` 等转发头仍被完全忽略**（探针 C12 仍回 101）：`WebServerTrustProxyHeaders` 是零消费死设置 ⇒ 属 **P-F02** 范围，本包未动。
  3. **普通 HTTP 路由对超大头块（> 8 KB）仍走普通路由并可能回 200 登录页**：本包只保证"不得进握手"；是否统一 431 建议协调者裁定（会影响 HTTP 语义）。
  4. **头块被截断时（`HeaderBlockComplete == false`）普通 HTTP 请求同样未被显式拒绝**：同上，建议协调者统一口径。
  5. **`ParseRequest` 现在拒绝"字段名与冒号之间有空白"的头**：这会让此前被"默默修正"的客户端（如 `Host : x`）变成缺 Host ⇒ 403。属 RFC 收敛方向，但**行为变化**，建议在发布说明里点名。
