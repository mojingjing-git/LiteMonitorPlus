# F02 验收记录 —— 反代外部地址与可信代理策略（#580）

- **分支**：`fix/F02-reverse-proxy-policy`
- **基线**：`master` `af572c0`（= `2bb2e25` merge，含 P-F01 已合并的 `WebSessionManager.cs` 重构）
- **覆盖**：#580（Cloudflare Tunnel 反代访问异常）
- **执行**：工作包 P-F02（隔离 worktree `G:\TOOL\LiteMonitor-fork\.worktrees\f02`，未触碰主仓库）

---

## 1. 证据锚点（真实文件:行号）

### 1.1 修复前的缺陷（基线事实）

| 事实 | 位置 |
|---|---|
| 死设置：`WebServerTrustProxyHeaders` 只有声明/赋值，**零消费点** | `src/Core/Settings.cs:308`（修复后仍存在，但**已有真实消费点**） |
| 死设置的唯一"消费"：赋给策略对象后无人读 | 修复前 `WebSessionManager.cs:2220`（`policy.TrustProxyHeaders = cfg.WebServerTrustProxyHeaders;`） |
| 修复前 Host 白名单 + **端口强校验**（外部 443 与内部 5000 直接比） | 修复前 `WebSessionManager.cs:2248-2262`（`IsHostAllowed`） |
| 修复前 Origin 判定同样按内部监听端口比 | 修复前 `WebSessionManager.cs:2265-2284`（`IsOriginAllowed`） |
| F00 红灯基线（证据文件） | `docs/reviews/evidence/appearance-probes/WebProbe/run-output.txt`（C7=403、C12=101） |

### 1.2 本次实现的关键位置

| 位置 | 内容 |
|---|---|
| `src/Core/Settings.cs:278` | 新增 `WebServerAllowedHosts`（外部 authority 白名单） |
| `src/Core/Settings.cs:289` | 新增 `WebServerTrustedProxies`（可信代理精确 IP） |
| `src/Core/Settings.cs:308` | `WebServerTrustProxyHeaders` 文档更新（**默认值仍为 false**，语义由"死设置"改为"真实生效但需可信对端"） |
| `src/System/WebServer/WebServerAccessPolicy.cs:65` | `IsExternalAccessForbidden`（外部暴露必须配口令） |
| `src/System/WebServer/WebServerAccessPolicy.cs:101` | `TryParseAuthority`（**严格**解析：非法端口/歧义 IPv6 一律失败） |
| `src/System/WebServer/WebServerAccessPolicy.cs:180` | `TryParseAuthorityEntry`（`[scheme://]host[:port]`） |
| `src/System/WebServer/WebServerAccessPolicy.cs:246` | `MatchExternalAuthority`（外部端口**不与内部端口比**） |
| `src/System/WebServer/WebServerAccessPolicy.cs:292` | `IsTrustedProxyAddress`（精确 IP，不接受网段/代理链） |
| `src/System/WebServer/WebServerAccessPolicy.cs:350` | `BuildBoundary`（访问边界快照 + 指纹） |
| `src/System/WebServer/WebSessionManager.cs:767` | `HandleClientAsync` 取**实际 TCP 对端地址** |
| `src/System/WebServer/WebSessionManager.cs:2296` | `BuildAccessPolicy`（统一从 `BuildBoundary` 取边界） |
| `src/System/WebServer/WebSessionManager.cs:2325` | `EnsurePolicyGeneration`（边界指纹变 ⇒ 策略代次 +1） |
| `src/System/WebServer/WebSessionManager.cs:2362` | `ResolveEffectiveAuthority`（**有效 authority** 判定链） |
| `src/System/WebServer/WebSessionManager.cs:2473` | `IsOriginAllowed`（按有效外部 scheme+authority 同源判定） |
| `src/System/WebServer/WebSessionManager.cs:1607` / `:3047` | 外部访问必须口令（HTTP / WS 两处） |
| `src/System/WebServer/WebSessionManager.cs:1751` | `SendLoginSuccess`（Cookie 的 `Secure` 用已验证协议） |
| `src/System/WebServer/WebSessionManager.cs:983` / `:1145` / `:1221` / `:1239` / `:2866` | **按策略代次**撤销旧会话 / 票据 / WS |
| `src/System/WebServer/LiteWebServer.cs:268` | 启动守卫：配置了外部地址却没有口令 ⇒ 拒绝启动 |
| `src/System/WebServer/LiteWebServer.cs:122` / `:324` | 记录访问边界指纹（供 Apply 比对） |
| `src/Core/Actions/AppActions.cs:299` / `:329` / `:353` | 应用设置：外部无口令 ⇒ 停止服务；边界变更 ⇒ 重启 |
| `src/Core/Actions/AppActions.cs:464` | 新增 `WebServerApplyResult.RejectedExternalWithoutPassword` |
| `src/UI/Settings/MainPanelPage.cs:228-246` | 设置界面：外部地址 / 可信代理 / 信任开关 + 范例与错误原因 |
| `resources/lang/{de,en,es,fr,ja,ko,ru,zh-tw,zh}.json` | 各新增 8 个 key（`Menu.WebServerAllowedHosts` 等） |

---

## 2. 改动清单

### 2.1 `src/Core/Settings.cs`（**只新增 2 个字段**）

| 字段 | 默认值 | 说明 |
|---|---|---|
| `WebServerAllowedHosts` | `""` | 外部 authority 白名单，语法 `[scheme://]host[:port]`，逗号/分号/换行分隔，**精确匹配无通配** |
| `WebServerTrustedProxies` | `""` | 可信代理的**精确 IP**，首版不接受 CIDR / 代理链 |

既有字段语义未改（`WebServerTrustProxyHeaders` 仅**更新文档**，默认值 `false` 不变；它是本次修复的"死设置"，其语义必须被明确，否则无法消费）。

**迁移**：两个新字段为**新增**，旧配置反序列化后为 `""`（= 不接受任何外部 authority / 不信任任何代理）⇒ **既有行为逐字不变**。
已经手工把 `WebServerTrustProxyHeaders` 改成 `true` 的老用户，升级后**仍然不会被采信转发头**（`WebServerTrustedProxies` 为空 ⇒ 没有可信对端）⇒ 不会因为升级就突然开始信任伪造头。

### 2.2 `src/System/WebServer/WebServerAccessPolicy.cs`（纯函数层）

- `TryParseAuthority`：**严格**解析 `host` / `host:port` / `[v6]:port`；非法端口（空/非数字/越界）与多冒号 IPv6 一律失败。
  ★ 旧实现在 `Host: 127.0.0.1:abc` 时会**丢掉端口只按主机名放行** ⇒ 那是个绕过口，本包关闭。
- `TryParseAuthorityEntry`：白名单条目语法 `[scheme://]host[:port]`；固定协议时**由白名单决定有效协议**（解决"外部是 HTTPS、内部只看到明文 HTTP"）。
- `MatchExternalAuthority`：端口口径 = 请求带显式端口 ⇒ 必须与条目**精确一致**（条目没写端口则只接受该协议默认端口）；请求没带端口 ⇒ 条目的端口就是有效端口。**绝不拿外部 443 与内部 5000 比**。
- `IsTrustedProxyAddress`：**精确 IP** 匹配（IPv4/IPv6 不做 mapped 归一 —— 归一会把 `::ffff:127.0.0.1` 变成 `127.0.0.1`，等于凭空多授信任）。
- `BuildBoundary`：一次性产出访问边界快照（允许主机 / 跨源 / 外部 authority / 可信代理 / 边界指纹）。
  ★ 指纹**刻意不含**访问口令（口令走既有的"口令指纹"通道）与跨源白名单（改跨源例外不影响"谁能连上来"）。

### 2.3 `src/System/WebServer/WebSessionManager.cs`（判定链）

`ResolveEffectiveAuthority` 的顺序（错误优先级固定）：

1. **对端可信吗？** = `TrustProxyHeaders` 开 **且** 对端 IP ∈ `WebServerTrustedProxies`。
   **不**可信 ⇒ 转发头**完全不参与**判定（连"它是不是歧义"都不看：不可信对端的头既不能帮忙也不能捣乱）。
2. 可信时才读转发头：`X-Forwarded-Proto` / `X-Forwarded-Host` 必须**唯一**（重复头、逗号多值 ⇒ **400**；协议值非法 ⇒ 400）。
3. 取 authority：可信且带 `X-Forwarded-Host` ⇒ 用它；否则用 `Host` 头。`Host` 缺失 / 重复 / 端口非法 ⇒ **403**。
4. **本机 / 局域网** ⇒ 保持 fork 既有的严格校验（显式端口必须与真实监听端口一致），**不回退已有能力**。
5. **外部** ⇒ 必须命中 `WebServerAllowedHosts`，否则 **403**（并给出可操作的中文原因）。

`IsOriginAllowed`：**同源 = 有效外部 scheme + 主机 + 端口逐项相等**；跨源例外仍归 `AllowedOrigins`；本机/局域网请求额外保留既有口径（Origin 主机在允许集合内且端口与监听端口一致）⇒ 不回退已有能力。

**Cookie 的 `Secure`**：仅当有效 authority 是**外部的 https** 时加 `; Secure`（本机 HTTP 不加，否则浏览器不回传）；注销时的清 Cookie 属性与之**一致**（否则浏览器可能不删旧 Cookie）。

**策略代次**：边界指纹变 ⇒ 代次 +1；会话 / 票据 / WS 连接都记住诞生时的代次 ⇒ 白名单收紧或撤销代理信任后**旧会话、旧票据、旧 WS 按代次失效**（会话在取用时判定，WS 在广播循环里被断开）。

### 2.4 `src/System/WebServer/LiteWebServer.cs`

- `Start()`：配置了外部 address 却**没有有效口令** ⇒ **拒绝启动**（与局域网模式同口径，反代从环回连进来也不豁免）。
- 记录启动那一刻的访问边界指纹（供 Apply 比对）。

### 2.5 `src/Core/Actions/AppActions.cs`

- 外部地址 + 无口令 ⇒ **停止服务**（新增独立标记 `RejectedExternalWithoutPassword`，不与局域网的标记混用）。
- 访问边界（外部地址 / 可信代理 / 信任开关 / 监听范围 / 端口）变更 ⇒ **重启**服务 ⇒ 旧会话、旧票据、旧 WS 全部撤销。

### 2.6 `src/UI/Settings/MainPanelPage.cs` + `resources/lang/*.json`

设置界面新增三项（与其它网页服务设置同卡片、同 Draft-Commit 保存）：

- `Menu.WebServerAllowedHosts`（允许的外部地址）
- `Menu.WebServerTrustedProxies`（可信代理 IP）
- `Menu.WebServerTrustProxyHeaders`（信任转发头开关）

并给出**范例**与**错误原因**（`Menu.WebServerReverseProxyExamples` / `Menu.WebServerReverseProxyErrors`）。9 个语言包（de/en/es/fr/ja/ko/ru/zh-tw/zh）**全部**新增同样 8 个 key，**无缺失语种**，既有 key 语义未动。

### 2.7 测试

- **新增** `tests/LiteMonitor.Tests/WebReverseProxyPolicyTests.cs`：**21** 条用例（裸 TCP 集成 + 本地可控反向代理 + 纯函数单测）。
- **修改** `tests/WebProbe/Program.cs`（**清单外，见 §6 偏离**）：探针沙箱配置补上外部 authority 白名单与访问口令，并新增 C13 / C14 两个判别用例。

---

## 3. 红 → 绿证明

### 3.1 为什么用"关闭新增行为"而不是"回退整个实现"来取红态

本包新增了 `Settings.WebServerAllowedHosts` / `Settings.WebServerTrustedProxies` 两个字段与
`WebServerAccessPolicy.TryParseAuthority` 等公开 API；**修复前的代码里它们不存在**，
新测试文件在修复前**无法编译**（编译期红，而非断言期红）。
因此红态取法是：**保留新增 API 表面，只把新增行为关掉**（等价于修复前的判定语义）：

```
// 临时 ①：转发头零消费（修复前 WebServerTrustProxyHeaders 是死设置）
bool peerTrusted = false && policy.TrustProxyHeaders && ...;

// 临时 ②：外部白名单不生效（修复前根本没有外部白名单）
MatchExternalAuthority(new List<WebAuthorityEntry>(), ...)   // 传空表 ⇒ 外部 authority 一律 403
```

同一份测试文件、同一批断言，红态输出：

```
失败 WebReverseProxyPolicyTests.本地反向代理保留Host时完整链路走通
失败 WebReverseProxyPolicyTests.本地反向代理重写Host时完整链路走通
失败 WebReverseProxyPolicyTests.转发头多值或重复时明确拒绝
失败 WebReverseProxyPolicyTests.外部https链路的Cookie带Secure
失败 WebReverseProxyPolicyTests.白名单收紧后旧会话失效
失败 WebReverseProxyPolicyTests.外部Origin同源放行跨源拒绝
失败 WebReverseProxyPolicyTests.白名单配置后外部Host的完整链路走通
失败!  - 失败: 7，通过: 12，已跳过: 0，总计: 19
```

关键断言的实际失败值（红态）：

| 用例 | 断言 | 红态实际 | 绿态实际 |
|---|---|---|---|
| 白名单配置后外部Host的完整链路走通 | `Assert.Contains("200", FirstLine(resp))` | `HTTP/1.1 403 Forbidden` | `HTTP/1.1 200 OK` |
| 本地反向代理重写Host时完整链路走通 | `Assert.Contains("200", …)` | `HTTP/1.1 403 Forbidden` | `HTTP/1.1 200 OK` |
| 转发头多值或重复时明确拒绝 | `Assert.Contains("400", …)` | `HTTP/1.1 401 Unauthorized` | `HTTP/1.1 400 Bad Request` |

恢复实现后（把两处临时改回原样，文件内已无 `RED-STATE` 标记）：

```
已通过! - 失败: 0，通过: 19，已跳过: 0，总计: 19
```

（补测 2 条后为 21 条，见 §4。）

### 3.2 绿态（最终）

```
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build --nologo
  → 失败: 0，通过: 1950，已跳过: 7，总计: 1957
```

---

## 4. 新增用例数

| 项 | 数 |
|---|---|
| 新增用例 | **21**（`tests/LiteMonitor.Tests/WebReverseProxyPolicyTests.cs`） |
| master 基线总数 | 1936 |
| 合并后总数 | **1957** = 1936 + 21 ✓ |

用例清单（21 条）：

| # | 用例 | 覆盖的验收矩阵行 |
|---|---|---|
| 1 | `authority解析对非法端口与歧义IPv6一律失败` | 非法端口 |
| 2 | `白名单条目支持协议与端口` | 白名单语法 |
| 3 | `外部authority按白名单条目取端口而不是内部监听端口` | 外部 443 ≠ 内部 5000 |
| 4 | `可信代理按精确IP匹配` | 可信代理 |
| 5 | `环回直连的既有行为不变` | 环回直连行为保持 |
| 6 | `未配置白名单时外部Host被拒绝` | 未授权域名 |
| 7 | `非法端口的Host被拒绝` | 非法端口 |
| 8 | `白名单配置后外部Host的完整链路走通` | **#580 核心**（登录→取票→WS101→快照） |
| 9 | `外部访问未配置口令时被拒绝` | 对外代理要求口令 |
| 10 | `配置外部白名单但没有口令时服务拒绝启动` | 冷启动守卫 |
| 11 | `可信对端加转发头时采用外部authority` | 可信对端 + 信任开关 + 单值转发头 |
| 12 | `不可信对端伪造转发头不能放行` | 不可信对端伪造转发头 |
| 13 | `信任开关关闭时转发头不参与判定` | 信任开关关闭 |
| 14 | `转发头多值或重复时明确拒绝` | 代理头歧义 |
| 15 | `外部Origin同源放行跨源拒绝` | 恶意 Origin / 同源判定 |
| 16 | `白名单收紧后旧会话失效` | 撤销后原连接按策略失效 |
| 17 | `外部https链路的Cookie带Secure` | Cookie Secure |
| 18 | `本地反向代理保留Host时完整链路走通` | 保留 Host 模式（本地可控反代） |
| 19 | `本地反向代理重写Host时完整链路走通` | 重写 Host 模式（本地可控反代） |
| 20 | `运行中清除口令时配置了外部地址的服务必须停止` | 应用设置路径 |
| 21 | `外部白名单变更时应用设置会重启服务并撤销旧会话` | 撤销后原连接按策略失效 |

---

## 5. 验收矩阵逐项结论

母计划 F02 验收矩阵（`docs/plans/2026-10-25-verified-bugs-remediation-plan.md:150-159`）：

| 场景 | 预期 | 实测 | 结论 |
|---|---|---|---|
| 环回直连、现有有效认证 | 行为保持 | 用例 5：登录→票据→**WS 101**→快照 **200**；探针 C1–C6 全部 101；探针 `H`/`W` 诊断行与基线逐行一致 | ✅ |
| 已配置外部 Host，代理保留 Host，HTTPS443→内部5000 | 登录→取票→WS101→快照完整成功 | 用例 8（直连源站）与用例 18（**本地可控反代·保留 Host**）：登录 200 → 票据 200 → **WS 101** → 快照 **200** | ✅ |
| 可信对端 + 信任开关 + 合法单值转发头 | 使用外部 authority；同上 | 用例 11（直连 + 单值转发头）与用例 19（**本地可控反代·重写 Host**）：**WS 101** + 快照 200 | ✅ |
| 不可信对端伪造转发头 | 转发头不能使原本不允许的请求通过 | 用例 12：对端 IP 不在可信列表（`10.11.12.13`）+ `Host=未授权外部域名` + `X-Forwarded-Host=本机` ⇒ **403** | ✅ |
| 信任开关关闭 | 转发头不参与策略 | 用例 13：开关关 + 对端在可信列表 + `X-Forwarded-Host=本机` + `Host=未授权外部域名` ⇒ **403**；探针 C14（转发头=未授权域名，Host=本机）⇒ **101**（说明转发头根本没被采信） | ✅ |
| 未授权域名、恶意Origin、重复Host、非法端口、代理头歧义 | 明确拒绝，不回退为200登录页 | 未授权域名 ⇒ 403（用例 6、探针 C13）；恶意 Origin ⇒ 403（用例 15、探针 C8）；非法端口 ⇒ 403（用例 7）；代理头歧义 ⇒ **400**（用例 14）；重复 Host ⇒ 403（实现层：`GetSingleHeader` 重复即 null ⇒ "Host 头重复"） | ✅ |
| 已连接后撤销白名单/代理信任 | 原连接按策略失效 | 用例 16（不改配置、只改白名单 ⇒ 旧会话 401）与用例 21（应用设置 ⇒ 重启 ⇒ 旧会话 401）；WS 侧：广播循环按策略代次断开过期连接（`WebSessionManager.cs:2866`） | ✅ |

补充：**外部访问必须口令**（母计划 §6 第 6 条）——用例 9（请求层 403）、用例 10（启动层拒绝启动）、用例 20（应用设置层停止服务）三处都覆盖。

### 5.1 WebProbe C7 前后对照

同一台机器、同一 worktree、同一命令 `pwsh scripts/run-probes.ps1 -Groups web -SkipUnitTests`：

| 用例 | 修复前（协调者基线 / 本包首次实跑） | 修复后 | 说明 |
|---|---|---|---|
| **C7** `#580 Host=隧道域名` | **403 Forbidden** | **101 Switching Protocols** ✅ | 配置外部白名单后放行 |
| C1–C4 | 101 | 101 | 不变 |
| C5 / C6 | 101 | 101 | 不变 |
| C8 `Origin=外部站点` | 403 | 403 | 不变 |
| C9 / C10 | 400 | 400 | 不变 |
| C11 `无票据` | 401 | 401 | 不变 |
| C12 `X-Forwarded-Host=隧道域名` | 101 | 101 | 信任开关**关闭** ⇒ 转发头不参与判定（语义已明确，不再是"死设置"） |
| **C13** `#580 Host=未授权外部域名` | （无此用例） | **403 Forbidden** | 白名单精确匹配，未授权域名拒绝 |
| **C14** `X-Forwarded-Host=未授权域名(信任开关关闭)` | （无此用例） | **101 Switching Protocols** | 转发头指向未授权域名却仍 101 ⇒ 证明转发头**根本没被采信** |
| `LOGIN /api/auth/login` | 200 | 200 | 不变 |
| `H` / `W` 诊断行 | 8 行 / 4 行 | 逐行一致 | 不变 |

未授权外部域名的 403 正文给出了**可操作原因**（界面同款文案）：
`Host 不在允许的外部地址白名单内（请在【设置 → 网页服务】里配置「允许的外部地址」）。`

### 5.2 三个测试工程 + 探针的实测结果

| 工程 / 探针 | 发现 | 通过 | 失败 | 跳过 |
|---|---|---|---|---|
| `LiteMonitor.Tests`（合并后） | 1957 | **1950** | **0** | 7 |
| `LiteMonitor.Tests`（master 基线） | 1936 | 1935 | 0 | 1 |
| `LiteMonitor.GpuNonperformance.Tests` | 19 | 19 | 0 | 0 |
| `LiteMonitor.Updater.Tests` | 73 | 73 | 0 | 0 |
| WebProbe（`-Groups web`） | 14 个用例 + 12 行诊断 | 全量通过（`[PASS] WebProbe`） | 0 | 0 |

**关于 7 条跳过**：6 条是 `MarketIndexContractTests`，它按环境变量 `LITEMONITOR_PLUGIN_REPO` 指向的插件市场仓库是否存在来跳过（本机该仓库不存在 ⇒ 整组跳过）；第 7 条是基线里本来就有的联网 E2E。**与本包无关**。
**跳过数因此比协调者基线多 6**：这是**环境差异**（本会话 shell 未设置 `LITEMONITOR_PLUGIN_REPO`，该目录在本机也不存在），不是本包引入的。集成时若 CI 环境设置了该变量，跳过数会回到 1。

**关于基线 flaky**：`tests/LiteMonitor.Tests/ReviewMarketPersistenceRegressionTests.cs` 在某次全量跑里出现 1 条间歇失败
（`Recovery_LockedJournalRemainsPending`，断言第 53 字节 `50` vs `49`），**与本包改动无关**（该类属市场持久化，与 Web 无交集）。
按派发单要求重跑该类确认：**33/33 全绿**。本包**未修改**该文件。

---

## 6. 偏离与遗留

### 6.1 偏离（清单外改动，均为达成验收所必需）

| # | 偏离 | 原因 | 风险 |
|---|---|---|---|
| D1 | **修改** `src/UI/Settings/MainPanelPage.cs`（授权清单写的是 `src/UI/Settings/SystemHardwarPage.cs`） | 派发单列的 `SystemHardwarPage.cs` 是"系统与硬件"页，**全文件不含任何网页服务设置**（已 grep 核实：`WebServer` 零命中）；网页服务的设置卡片实际在 `MainPanelPage.CreateWebCard()`。把反代设置放到硬件页既不合逻辑也无人能找到。该文件**未被任何其它分支相对其 merge-base 修改过**（已逐分支核实），无冲突风险。 | 低。协调者如认为必须回到 `SystemHardwarPage.cs`，请裁定（技术上不建议）。 |
| D2 | **修改** `tests/WebProbe/Program.cs`（授权清单只列了 `tests/LiteMonitor.Tests/`） | 探针的沙箱 `settings.json` 由探针自己生成，**不含外部白名单** ⇒ C7 不可能变成 101；要满足"C7 在配置白名单后必须走通"这条核心验收，只能改探针的沙箱配置。另需给沙箱配访问口令（外部暴露必须口令 ⇒ 否则服务拒绝启动）。改动共 34 行，且**只写目标程序集真实存在的字段**（`Has(...)` 探测）⇒ 对上游程序集行为不变。 | 低。但探针的**基线语义**变了：沙箱从 L0（无口令）变成 L1（有口令），`settings.json` 行会显示 `"WebServerPassword": "***"`（打印脱敏）。C1–C12 的状态码全部与基线一致，仅 C7 由 403 变 101，并新增 C13 / C14。 |
| D3 | `WebServerTrustProxyHeaders` 的 **XML 文档**被改写 | 该字段原文档写的是"信任 `X-Forwarded-For` / `X-Real-IP` / `Forwarded`"，而本包按母计划要求消费的是 `X-Forwarded-Host` / `X-Forwarded-Proto`。不改文档 = 文档与实现不符。**字段名、类型、默认值（`false`）均未变**；该字段此前**零消费点**，所以没有任何行为依赖旧语义。 | 无（纯文档）。 |

### 6.2 遗留 / 未自决

| # | 事项 | 状态 |
|---|---|---|
| L1 | **白名单条目的协议前缀（`https://`）是本包自定的扩展语法**（母计划只写了"精确 host:port"） | 已实现并写进文档/界面/语言包。**理由**：不固定协议时，"外部是 HTTPS、内部只看到明文 HTTP"就没法表达，浏览器同源判定（scheme 必须相等）会必然失败。固定协议时**有效协议由白名单决定**（用户的权威声明），请求侧协议不同也不影响匹配。若协调者要求严格限定为纯 `host:port`，请裁定（届时需另设一个"外部协议"设置项）。 |
| L2 | 转发头的**代理链**（`X-Forwarded-For` 多跳） | 首版按母计划**明确不支持**：`X-Forwarded-Host` / `X-Forwarded-Proto` 必须单值，多值/重复 ⇒ 400。`X-Forwarded-For` / `Forwarded` / `X-Real-IP` **完全不参与**身份判定。 |
| L3 | `WebServerAllowedHosts` 的条目非法时**静默丢弃**（不整体拒绝） | 一条写错的地址不该让整份配置失效；被丢弃 = 不在白名单 = 拒绝。界面给了语法范例。 |
| L4 | 外部 authority 的**端口精确匹配**在"请求带显式端口"时较严：条目 `https://tunnel.example.com` 只接受 443（默认端口），条目 `tunnel.example.com:8443` 只接受 8443 | 符合母计划"非默认端口必须精确匹配"。 |

---

## 7. 待实机清单（**严禁**用等价请求冒充）

| # | 场景 | 为什么不能在本机闭环 | 状态 |
|---|---|---|---|
| W1 | **真实 Cloudflare Tunnel 完整链路**（cloudflared → `127.0.0.1:5000`，对外 `https://<隧道域名>`） | 本机没有 cloudflared 与隧道域名；本地可控反代**不是** Cloudflare Tunnel（隧道会改写 Host、会发 `X-Forwarded-Proto: https`、会带自己的会话/认证层）。**本包没有用任何等价请求冒充它。** | **待实机**（协调者/用户实机验收） |
| W2 | 真实隧道下的**浏览器端**行为：登录页 → 登录 → 票据 → WS 推送 → 快照轮询，且 `Cookie` 的 `Secure` 属性被浏览器接受 | 需要真实 TLS 终结与真实浏览器 | **待实机** |
| W3 | 真实隧道下 `Origin: https://<隧道域名>` 的**同源**判定 | 同上（本地反代已覆盖判定逻辑，但不能代表隧道的头改写细节） | **待实机** |
| W4 | 多级反代（隧道 → nginx → 源站） | 首版明确不支持代理链（多值转发头 ⇒ 400） | 设计上不支持，非缺陷 |
| W5 | 真实隧道断线/重连后旧会话与旧 WS 的撤销 | 需要真实隧道生命周期 | 代码层已覆盖（策略代次 + 重启），实机行为**待实机** |

---

## 8. 相邻问题（发现但不在本包范围）

| # | 问题 | 归属建议 |
|---|---|---|
| A1 | `Host: 127.0.0.1:abc`（非法端口）**修复前**会被"丢掉端口后按主机名放行" —— 本包已在新判定链里关闭（用例 7），但 `WebServerAccessPolicy` 之外仍有旧口径的残留风险 | 本包已覆盖，无需另开 |
| A2 | 设置界面**没有** `WebServerTrustProxyHeaders` 的入口（此前它是纯死设置，无人能在界面上改） | 本包已补（三项新增控件之一） |
| A3 | `AppActions.ApplyWebServerWithResult` 的"监听范围变更 ⇒ 重启"只比较 `CurrentBindMode`；若用户改了**局域网地址清单**（`WebServerLanAddresses`），访问边界指纹会变 ⇒ 本包已把它纳入重启条件 | 本包已覆盖 |
| A4 | 探针沙箱里**没有**"信任开关打开 + 可信代理"的配置 ⇒ 探针无法直接演示"转发头被采信"的路径 | 该路径由单测（用例 11 / 19）覆盖；探针若要覆盖需再扩一次沙箱配置（**建议下个包**处理，避免本包继续动清单外的探针） |
| A5 | `MarketIndexContractTests` 在没有 `LITEMONITOR_PLUGIN_REPO` 时整组跳过 ⇒ CI 的 `MAX_SKIPPED_TESTS: 1` 在该环境会红 | **协调者裁定**：本包未改 CI；跳过数变化是环境差异（见 §5.2） |

---

## 9. 复现命令

```powershell
$env:NUGET_PACKAGES = 'G:\TOOL\LiteMonitor-fork\.nuget-packages'
$env:DOTNET_CLI_HOME = 'G:\TOOL\LiteMonitor-fork\.dotnet-cli-home'
Set-Location 'G:\TOOL\LiteMonitor-fork\.worktrees\f02'
dotnet build LiteMonitor.sln -c Debug --nologo                                   # 0 错误
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build --nologo
dotnet test tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj -c Debug --no-build --nologo
dotnet test tests/LiteMonitor.Updater.Tests/LiteMonitor.Updater.Tests.csproj -c Debug --no-build --nologo
pwsh scripts/run-probes.ps1 -Groups web -SkipUnitTests
```
