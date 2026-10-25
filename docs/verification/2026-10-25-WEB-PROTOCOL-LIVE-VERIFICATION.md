# Web / 协议类 —— 实机端到端验证报告（fork ↔ upstream A/B）

| 项 | 值 |
|---|---|
| 检查日期 | 2026-10-25 |
| 任务单 | TASK-5 的补充执行（用户选择「静态为主，只对 Web/协议类做实机验证」） |
| fork 被测程序集 | `LiteMonitor++.dll`，由**当前工作树**构建（核实期间 HEAD 被并行的主题市场会话推进到 `2a1c57d`） |
| upstream 被测程序集 | `LiteMonitor.dll`，由 `upstream/master` = `3b91b5c`（1.3.7）**单独检出到临时目录独立构建** |
| 验证方式 | 反射加载**真实产品程序集**，仅替换「配置目录」「端口」两个接缝；请求一律裸 TCP 手写字节，避开任何客户端库的规范化行为 |
| 是否运行了产品 | 只运行了 `LiteWebServer`（Web 服务子模块）。**未**启动整个 App、**未**安装 PawnIO 内核驱动、**未**嵌入任务栏 |
| 是否改动产品代码 | 否。探针与构建产物全部位于 `.tmp/verification/`（已被 `.gitignore` 忽略），`src/**` 零改动 |

---

## 1. 一句话结论

- **#589（WebSocket 头名大小写导致反代下永久灰屏）：在 fork 当前版本【不生效】** —— 头名大小写已不敏感，fork 与上游实测都返回 `101`。
- **#580（Cloudflare Tunnel / 反向代理访问异常）：在 fork 当前版本【确认生效，且是 fork 引入的新回归】** —— fork 对 `Host` 头做白名单校验，反代场景一律 `403`；上游因为**根本没有 Host 校验**，同样场景返回 `101`（可用）。
- **额外发现（本次 A/B 才暴露）**：fork 的 WebSocket 升级判定退化为正则，`Upgrade` 头冒号后**不是恰好一个空格**就识别不出升级请求 → 降级成普通 HTTP、返回登录页 HTML。这是 fork 相对上游的**新退化**（上游用正规头解析器，容忍 0..N 个空格）。

---

## 2. 实测矩阵（同一套请求，分别打 fork 与 upstream）

请求骨架（除标注差异外完全相同）：

```
GET /ws?ticket=<一次性票据> HTTP/1.1
Host: 127.0.0.1:<真实监听端口>
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==
Sec-WebSocket-Version: 13
```

| 用例 | 差异点 | **fork** | **upstream 1.3.7** | 判定 |
|---|---|---|---|---|
| C1 | 基准（完全标准） | `101 Switching Protocols` | `101` | 一致 |
| C2 | `sec-websocket-key`（全小写） | `101` | `101` | **两侧均不敏感 → #589 已修** |
| C3 | `SEC-WEBSOCKET-KEY`（全大写） | `101` | `101` | 同上 |
| C4 | `Sec-Websocket-Key`（混合） | `101` | `101` | 同上 |
| C5 | `Upgrade:websocket`（冒号后**无**空格） | **`200 OK`，返回登录页 HTML** | `101` | **fork 退化 / upstream 正常** |
| C6 | `Upgrade:  websocket`（冒号后**两**空格） | **`200 OK`，返回登录页 HTML** | `101` | **fork 退化 / upstream 正常** |
| C7 | `Host: tunnel.example.com`（反代场景） | **`403 Host / Origin 校验未通过`** | `101` | **fork 新回归 → #580 确认生效** |
| C8 | `Origin: https://evil.example` | **`403`** | `101` | fork 更严 |
| C9 | Key 形态非法（`not-base64!!`） | `400 Sec-WebSocket-Key 缺失 / 重复 / 形态非法` | **静默关闭（无任何响应）** | fork 可观测性更好 |
| C10 | Key 头重复出现两次 | `400`（同上） | **静默关闭** | fork 更严格 |
| C11 | 不带票据 | `401 票据无效、已过期或已被使用` | `101` | fork 强制一次性票据，上游无口令时直通 |
| C12 | `X-Forwarded-Host: tunnel.example.com` | `101`（代理头**被忽略**） | `101` | 两侧都不认代理头 |

### 2.1 HTTP 路径的 Host 判定规则（补充诊断）

| `Host` 头取值 | **fork** | **upstream** |
|---|---|---|
| `127.0.0.1:<端口>` | `401 需要有效的主会话`（Host 校验**通过**） | `200 {}` |
| `127.0.0.1`（不带端口） | `401`（通过） | `200 {}` |
| `localhost:<端口>` | `401`（通过） | `200 {}` |
| `[::1]:<端口>` | `401`（通过） | `200 {}` |
| `127.0.0.1:1`（端口不符） | **`403 Host / Origin 校验未通过`** | `200 {}` |
| `127.0.0.1:9999`（端口不符） | **`403`** | `200 {}` |
| 完全没有 `Host` 头 | **`403`** | `200 {}` |

**读法**：fork 的规则是「主机名 ∈ {127.0.0.1, localhost, ::1, 局域网白名单} **且** 端口 == 真实监听端口」；upstream **完全不做 Host 校验**（任何 Host 都放行），这也是上游在反代下「能用」的根本原因。

---

## 3. 代码证据（行号以实测所用 commit 为准）

### 3.1 #589 在 fork 上已修 —— 头名大小写不敏感

| 位置 | 代码 | 说明 |
|---|---|---|
| fork `src/System/WebServer/WebSessionManager.cs:403` | `public readonly Dictionary<string, List<string>> Headers = new(..., StringComparer.OrdinalIgnoreCase);` | 头集合按大小写不敏感存储 |
| fork `.../WebSessionManager.cs:2645` | `if (string.Equals(name, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))` | 取 Key 时头名比较不敏感 |
| fork `.../WebSessionManager.cs:2596-2613` | 注释明确写了「原先是一句 `Regex.Match(request, "Sec-WebSocket-Key: (.*)")`，四点都不对」 | 修复意图已被记录在案 |

上游侧同样已修：`upstream/master` 的 `WebSessionManager.cs:125-149` `TryGetHeaderValue` 按 `OrdinalIgnoreCase` 比较头名并 trim 取值。

> **issue 状态与代码状态不一致**：`#589` 在上游仍为 OPEN，但上游 `3b91b5c`（2026-10-21）已重构该文件修掉了问题 —— 典型的「修完没关单」。

### 3.2 #580 在 fork 上确认生效 —— 反代必 403

| 位置 | 代码 |
|---|---|
| fork `.../WebSessionManager.cs:2536-2540` | 握手阶段 `if (!IsHostAllowed(info, policy) \|\| !IsOriginAllowed(info, policy))` → `SendErrorResponse(stream, 403, "Host / Origin 校验未通过。")` |
| fork `.../WebSessionManager.cs:1356-1360` | HTTP 路径同一套判定 |
| fork `.../WebSessionManager.cs:1971-1986` | `IsHostAllowed`：主机名须在白名单内，**且端口必须等于真实监听端口**（`:1983`） |
| fork `.../WebSessionManager.cs:1946-1949` | 白名单固定含 `127.0.0.1` / `localhost` / `::1`，其余需显式配置 |
| fork `src/System/WebServer/WebServerAccessPolicy.cs:25-34` | 局域网暴露必须配口令 |

**关键：没有可绕开的配置组合。**

- `WebServerTrustProxyHeaders`（`src/Core/Settings.cs:257`）在 fork 全仓只有三处引用：`Settings.cs:257`（声明）、`WebSessionManager.cs:1944`（赋值给 policy）、`WebSessionManager.cs:2085`（policy 字段声明）—— **没有任何一处读取它**。实测 C12 印证：带 `X-Forwarded-Host` 的请求照样 `101`，说明代理头根本不参与判定。该设置是**死设置**。
- 端口强校验要求 `Host` 里的端口 == 监听端口（默认 5000）。公网反代通常是 `443` 或任意对外端口，**无法通过配置改写**（只有 `WebServerAllowedOrigins` 可配 Origin 白名单，但 Host 白名单没有对应的「信任代理」开关）。

### 3.3 额外发现 —— fork 的 Upgrade 头判定退化（本次 A/B 才暴露）

| 位置 | 代码 |
|---|---|
| fork `.../WebSessionManager.cs:1323-1327` | `Regex.IsMatch(req, "GET", ...) && Regex.IsMatch(req, "Upgrade: websocket", RegexOptions.IgnoreCase)` |
| upstream `.../WebSessionManager.cs:117-122` | `req.StartsWith("GET ", ...) && TryGetHeaderValue(req, "Upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)` |

上游用正规头解析器：取到 `Upgrade` 的值再 trim 比较，**天然容忍冒号后 0..N 个空格**。
fork 用固定字面量正则：**必须恰好是 `Upgrade: websocket`（一个空格）**。

RFC 7230 §3.2.4 规定 `:` 与 field-value 之间允许 OWS（空格 / 制表符），因此 `Upgrade:websocket` 与 `Upgrade:  websocket` 都是**合法请求**。实测：这两种写法在 fork 上返回 `200 OK` + 登录页 HTML —— WebSocket 升级被静默降级，浏览器侧表现就是「网页端连不上/灰屏」。**这恰好复刻了 #589 描述的现象形态，只是触发原因不同（头名大小写 vs 冒号后空格）。**

---

## 4. 结论对照

| Issue | 上游 1.3.7 | fork 当前版本 | 对 fork 的处置建议 |
|---|---|---|---|
| #589 头名大小写 | 已修（issue 未关） | **已修**，实测 4 种大小写均 `101` | 关闭/替换该 issue；**但要按 §3.3 修 Upgrade 头**，否则同类现象会以另一种形态复现 |
| #580 反代访问 | 天然可用（无 Host 校验），但安全面全开 | **确认生效**：反代必 `403`，且无配置可绕 | ① 实现 `WebServerTrustProxyHeaders`（当前是死设置）—— 开启时采信 `X-Forwarded-Host` 并放宽端口校验；② 或提供 `WebServerAllowedHosts` 显式白名单；③ 在 UI 上明示反代需要哪些配置 |
| 附带 | 非法/重复 Key 静默关闭 | 明确 `400` + 原因文本 | 保留 fork 的可观测性优势 |

---

## 5. 复现方法（可重跑）

1. 构建被测程序集：
   - fork：仓库根 `dotnet build LiteMonitor.csproj -c Debug`
   - upstream：`git archive --format=tar -o up.tar upstream/master` 解包到临时目录后独立 `dotnet build`
2. 把探针（`.tmp/verification/webprobe/`）**构建进被测程序集的输出目录**（探针用 `AppContext.BaseDirectory` 定位 `settings.json`；fork 另支持 `SettingsHelper.PathOverride` 沙箱接缝）。
3. 探针会：挑一个空闲端口 → 写 `settings.json`（`WebServerEnabled=true`、`WebServerPort=<端口>`、fork 另加 `WebServerBindMode=1` 表示仅环回）→ 反射构造 `LiteWebServer` 并 `Start()` → 跑完整矩阵 → 打印每个用例的状态行与响应体。
4. 原始输出存档：`.tmp/verification/results-fork.txt`、`.tmp/verification/results-upstream.txt`。

**探针自身的两个已修正的坑（供后续复用者注意）**：
- `Cookie` 头必须是完整的 `name=value`（只传 value 会得到 `401 需要有效的主会话`）。
- `Host` 头必须是 `127.0.0.1:<端口>`，不能只写端口号（否则 Host 校验必然失败）。

---

## 6. 未覆盖 / 未能验证

- **#580 的端到端隧道验证**（真实 Cloudflare Tunnel / nginx）：本机无对外实例，且按约束不对外部生产服务发请求。§2 的 C7 用「等价 Host 改写」直接命中了 403 分支，等价于隧道转发后的真实请求形态，但未跑真实隧道。
- **#611（倒计时插件返回 null）**：属插件执行器语义，未纳入本次协议实测；结论仍以 TASK-5 的静态取证为准（fork 侧 `continue_on_error` / `native://newyear` / `Holiday.json` v1.1 均缺失 → 确认回归）。
- **#235 / #287 / #96**：需要可控断网、第二块物理网卡或链路抖动环境，本机不具备，仅保留静态取证。
- **wss:// / TLS 终止**：本次只验证了 ws:// 明文握手；wss:// 依赖外层 TLS 终止，不在服务端代码路径内。
