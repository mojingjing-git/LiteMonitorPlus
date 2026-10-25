# TASK-5 核实报告：Web、插件与数据刷新

| 项 | 值 |
|---|---|
| 任务单 | TASK-5 Web、插件与数据刷新 |
| 检查日期 | 2026-10-25 |
| 上游仓库 / ref | `Diorser/LiteMonitor` @ `upstream/master` = **3b91b5c**（"fix: 完成 1.3.7 发版审查并修复监控与网络边界问题", 2026-10-21） |
| fork 分支 / commit | `LiteMonitor-fork` @ `master` = **5e756e7**（"feat(theme-market): M2+M3 主题索引解析/安装事务/状态与 D7 卸载守卫"） |
| 上游→fork 差异规模 | `src/` 下 172 个文件，+52692 / −2984 |
| 取证方式 | 只读 git（`show` / `grep` / `diff` / `ls-tree` / `log`）+ `gh issue view` + 源码抽取式协议验证程序 |
| 禁止事项遵守 | 未执行 checkout/switch/stash/reset/clean/restore/commit/branch；未修改 `src/**`；未触碰工作树既有未提交改动 |

---

## 1. 汇总表

| Issue | 当前是否 open | 上游结论 | fork 结论 | 证据位置 | 复现/验证结果 | 下一步 |
|---|---|---|---|---|---|---|
| **#589** WS 握手 `Sec-WebSocket-Key` 区分大小写 | ✅ OPEN（2026-09-25 更新） | **已解决（代码已修，单未关闭）** — 正则已换成大小写不敏感的解析头 | **已解决（就头名大小写而言），且比上游更严**；但残留 `Upgrade` 行正则脆弱点 | 上游 `WebSessionManager.cs:125-149,400`；fork `WebSessionManager.cs:2561,2616-2678` | 验证程序 CASE A/C/D/E 全通过（4 种大小写 + 重复键 + 伪造头 + 非法值） | 上游关闭该单；fork 修 `IsWebSocketUpgrade` 正则（见 §6） |
| **#580** Cloudflare Tunnel 代理访问异常 | ✅ OPEN（无评论，仅截图） | **部分确认（原根因已修；缺端到端环境）** | **确认存在，且比上游更严重** —— 新增 Host/Origin 闸门默认 403 | 上游 `WebSessionManager.cs:117-122`（无 Host 闸门）；fork `:1937-2008,2536-2539` | 验证程序 CASE B（2 处 FAIL，fork 侧）、CASE F（fork 反代 Host 被拒） | fork 需提供反代 Host/Origin 放行方案（见 §6） |
| **#611** 倒计时插件 API 返回 null → 只显示 ❌ | ✅ OPEN（2026-10-25 更新，2 人复现） | **已解决** —— 有 `continue_on_error` + `native://newyear` 本地兜底 | **确认（回归）** —— 兜底步骤、`continue_on_error`、`native://newyear` 处理器全部缺失 | 上游 `resources/plugins/Holiday.json` v1.1、`PluginExecutor.cs:260-283,506-509`、`Native/NewYearNative.cs`；fork `Holiday.json` v1.0、`PluginModels.cs` 无 `continue_on_error`、`PluginExecutor.cs:736-748` | 静态链路推导 + 提取语义核对（`ExtractJsonValue` 两边一致返回 `❓`→触发抛出） | fork 回补 `continue_on_error` 语义与 `native://newyear` 兜底（见 §6） |
| **#96** 网络检测一段时间后数据卡住 & 网卡选择 | ✅ OPEN（2 条评论） | **部分确认** —— 设置界面已有网卡选择；右键菜单未做；"卡住"未定位到修复提交 | **部分确认** —— 同样保留 `lastValidMap` 冻结行为；IP 刷新比上游更稳 | 上游 `NetworkManager.cs:216-318`、`UI/Settings/SystemHardwarPage.cs:116,170-171`、`Core/Settings.cs:32-33`；fork `NetworkManager.cs:385-485` | **未复现**（缺真实网卡切换环境），已给出复现步骤与判据 | fork/上游补"存活 NIC 长时间零流量则重选"；右键菜单网卡选择（未实现） |
| **#287** 切换 Wi-Fi 后局域网 IP 未更新 | ✅ OPEN（维护者 2026-02-12 回"新版已修复"） | **已解决** —— 存在成组 `[Fix #287]` 标记 | **已解决，且强于上游** —— 额外让 UI 立即刷新 | 上游 `NetworkManager.cs:35,45-51,88-105,190-196`；fork `NetworkManager.cs:48-52,85-90,256-273,359-361` + `InfoService.cs:71-75` | 静态代码取证（两侧标记齐备）；**未做真实 Wi-Fi 切换实测** | 上游可补订阅 `NetworkChange.NetworkAvailabilityChanged`；fork 可补同项 |
| **#235** 启动不可达，恢复后插件数据不刷新 | ✅ OPEN（维护者 2026-01-29 回"新版本已修复"） | **已解决** —— 4036cee 引入线性退避重试 | **已解决（等价实现）** | 上游 `PluginManager.cs:341-417`（369-402 退避、377-382 重置客户端）；fork `PluginManager.cs:485-537`（505-514、518-526） | 静态代码取证：两侧 `finally` 均重启定时器，成功后 `InjectValue` 覆盖 ❌ | 关闭该单（"布局问题"子项仍属信息不足） |

---

## 2. #589 — WebSocket 握手 `Sec-WebSocket-Key` 区分大小写

### 2.1 当前状态
`OPEN`，创建于 2026-09-25T23:32:18Z，最后更新 2026-09-25T23:39:39Z，仅 1 条评论（提交者自述由 DSH 协助整理）。

### 2.2 问题摘要
issue 报告 `src/System/WebServer/WebSessionManager.cs` L361 用 `Regex.Match(request, "Sec-WebSocket-Key: (.*)")` 匹配头名，正则默认区分大小写。经 Cloudflare Tunnel / nginx 转发后头名被规范化（`Sec-Websocket-Key` 或全小写），匹配失败 → `Handshake` 直接 `return false`，服务端一个字节都不回 → 客户端超时断开 → 反代 502 → 网页版永久灰屏。

### 2.3 上游代码证据

上游 `3b91b5c` **已经修掉了 issue 描述的根因**。提交 `3b91b5c`（2026-10-21）对该文件做了 59 增 22 删：

- `src/System/WebServer/WebSessionManager.cs:5` —— 删除 `using System.Text.RegularExpressions;`
- `src/System/WebServer/WebSessionManager.cs:117-122` —— `IsWebSocketUpgrade` 由正则改为**解析头**：
  ```csharp
  return req.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
         TryGetHeaderValue(req, "Upgrade", out var upgrade) &&
         upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase);
  ```
- `src/System/WebServer/WebSessionManager.cs:124-149` —— 新增 `TryGetHeaderValue`：逐行扫描**独立头字段**，头名比较用 `StringComparison.OrdinalIgnoreCase`（L139），值做 `Trim(' ', '\t')`（L143），**重复字段直接拒绝**（L142）。
- `src/System/WebServer/WebSessionManager.cs:400-401` —— `Handshake` 改用 `TryGetHeaderValue(request, "Sec-WebSocket-Key", out var key)`，并额外校验 Base64 解码后恰为 16 字节。

即 issue 正文描述的 `Regex.Match(..., "Sec-WebSocket-Key: (.*)")` 在**当前上游已不存在**。issue 提交于 2026-09-25，修复提交于 2026-10-21，晚 26 天——这解释了"为什么 issue 至今仍 OPEN 却已不成立"。

### 2.4 fork 对照

fork `5e756e7` 的 `src/System/WebServer/WebSessionManager.cs` 已从上游的 423 行扩到 2680 行，握手路径**重写**：

- `:2616-2662` `TryGetWebSocketKey` —— 只扫"请求行 + 头行"，遇空行即止；头名比较 `string.Equals(name, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)`（L2645）；**重复键拒绝**（L2647）；值形态校验（L2650）。
- `:2672-2678` `IsWebSocketKeyShapeValid` —— `Convert.TryFromBase64String` 且解码后恰 16 字节。
- `:2561-2565` —— 握手时调用上述方法，失败返回 **400**（上游是静默 `return false`）。

**fork 在"Sec-WebSocket-Key 头名大小写"这一点上与上游等价且更严格**（额外具备重复键拒绝 + RFC 6455 形态校验）。

**但 fork 残留一个新的脆弱点**（见 §2.5 CASE B）：`:1323-1327` 的 `IsWebSocketUpgrade` 仍用正则：
```csharp
return Regex.IsMatch(req, "GET", RegexOptions.IgnoreCase) &&
       Regex.IsMatch(req, "Upgrade: websocket", RegexOptions.IgnoreCase);
```
它要求请求原文里**字面出现** `Upgrade: websocket`（恰好一个空格）。而 RFC 7230 规定 `field-name ":" OWS field-value OWS`，**冒号后的空白是可选的**，`Upgrade:websocket` 与 `Upgrade:  websocket` 都是合法请求。上游的解析头写法对两者都接受，fork 的正则对两者都拒绝。

### 2.5 复现条件与验证步骤

**验证手段（协议级，已执行）**：把上游与 fork 的解析函数**逐字抽取**进一个独立 C# 验证程序（`.tmp/verification/Harness.csproj` + `Program.cs`，`dotnet run`，只编译 `Program.cs` 避免把产品树 glob 进来），喂入构造的握手报文。

```
== CASE A: Sec-WebSocket-Key header-name casing (#589) ==
  [PASS] upstream extract Sec-WebSocket-Key (canonical)          actual=True
  [PASS] fork     extract Sec-WebSocket-Key (canonical)          actual=True
  [PASS] upstream extract Sec-Websocket-Key (cloudflared)        actual=True
  [PASS] fork     extract Sec-Websocket-Key (cloudflared)        actual=True
  [PASS] upstream extract sec-websocket-key (all lower)          actual=True
  [PASS] fork     extract sec-websocket-key (all lower)          actual=True
  [PASS] upstream extract SEC-WEBSOCKET-KEY (all upper)          actual=True
  [PASS] fork     extract SEC-WEBSOCKET-KEY (all upper)          actual=True
== CASE B: Upgrade header spacing (RFC 7230 OWS optional) ==
  [PASS] upstream IsWebSocketUpgrade "Upgrade: websocket"        actual=True
  [PASS] fork     IsWebSocketUpgrade "Upgrade: websocket"        actual=True
  [PASS] upstream IsWebSocketUpgrade "Upgrade:websocket"         actual=True
  [FAIL] fork     IsWebSocketUpgrade "Upgrade:websocket"         actual=False   <-- fork 脆弱点
  [PASS] upstream IsWebSocketUpgrade "Upgrade:  websocket"       actual=True
  [FAIL] fork     IsWebSocketUpgrade "Upgrade:  websocket"       actual=False   <-- fork 脆弱点
  [PASS] upstream IsWebSocketUpgrade "upgrade: WebSocket"        actual=True
  [PASS] fork     IsWebSocketUpgrade "upgrade: WebSocket"        actual=True
== CASE C: duplicate Sec-WebSocket-Key (request smuggling shape) ==
  [PASS] upstream rejects duplicate key    [PASS] fork rejects duplicate key
== CASE D: Sec-WebSocket-Key value echoed inside another header ==
  [PASS] upstream rejects (no real header) [PASS] fork rejects (no real header)
== CASE E: malformed Sec-WebSocket-Key value (RFC 6455 shape) ==
  [PASS] upstream rejects bad key shape    [PASS] fork rejects bad key shape
TOTAL: pass=30 fail=2      （2 个 FAIL 即 fork 侧 CASE B 偏差，属"预期检出"）
```

**端到端复现办法（需真实进程，缺环境）**：
```powershell
# 对运行中的 LiteMonitor（默认 5000）逐个改头名大小写
$key = [Convert]::ToBase64String((1..16 | ForEach-Object { [byte]$_ }))
'HTTP/1.1','Host: 127.0.0.1:5000','Upgrade: websocket','Connection: Upgrade',"Sec-Websocket-Key: $key",'','' | %{ "$_`r`n" } | curl.exe --http1.1 -s -i -N --data-binary @- http://127.0.0.1:5000/
# 期望：101 Switching Protocols + Sec-WebSocket-Accept
```
或用 `npx wscat -c ws://127.0.0.1:5000` + 手工构造头。

**"恢复成功"判据**：
- `Sec-WebSocket-Key` 用任意大小写（`Sec-WebSocket-Key` / `Sec-Websocket-Key` / `sec-websocket-key` / `SEC-WEBSOCKET-KEY`）→ 响应首行必须是 `HTTP/1.1 101 Switching Protocols`，且 `Sec-WebSocket-Accept` 等于 `base64(SHA1(key + 258EAFA5-E914-47DA-95CA-C5AB0DC85B11))`。
- fork 额外要求：`Upgrade` 行必须写成 `Upgrade: websocket`（恰好一个空格），否则会被降级成普通 HTTP 请求，返回首页 HTML 而不是 101。
- 反代场景还须满足 fork 的 Host/Origin 白名单（见 §3.5）。

### 2.6 结论
- **上游：已解决（代码已修，issue 未关闭）**。issue 描述的正则根因在 `3b91b5c` 中已不存在；协议级验证 4/4 大小写变体全部正确提取。
- **fork：已解决（就 `Sec-WebSocket-Key` 头名大小写而言）**，且比上游更严；但 `IsWebSocketUpgrade` 的 `Upgrade` 行正则是一处**独立的、上游已消除而 fork 仍存在**的协议脆弱点。

### 2.7 风险
- fork 的 `Upgrade` 行正则一旦遇到会规范化空白（而非仅大小写）的代理/网关，握手会被静默降级为 HTTP —— 表现与 #589 原始症状（灰屏）高度相似，但服务端返回的是首页而非 101，排查难度更大。
- 上游 `Handshake` 失败时**静默 `return false`**（不发任何字节），客户端只能等超时；fork 返回明确的 400/403，**可诊断性更好**。

### 2.8 建议下一步
1. 上游：关闭 #589（补一句"已在 1.3.7 修复"），或至少在 issue 内说明修复 commit。
2. fork：把 `:1323-1327` 的正则换成与上游同源的"解析头 + 值 `Trim` + `OrdinalIgnoreCase`"判定。

---

## 3. #580 — Cloudflare Tunnel 代理访问 Web 异常

### 3.1 当前状态
`OPEN`，创建于 2026-09-19T16:48:58Z，**无任何评论**，正文只有一句话 + 一张截图（`https://github.com/user-attachments/assets/f45ba59a-...`）。截图内容未取回，故"显示异常"的具体形态（灰屏 / 数值错乱 / 布局错位）属**信息不足**。

### 3.2 问题摘要
用 cloudflared 把本地 `http://<IP>:5000` 代理出去后页面能打开，但"显示有问题"。与 #589 是同一场景族（反代 → WS 握手失败 → 灰屏），时间上 #589（09-25）晚于 #580（09-19）。

### 3.3 上游代码证据

上游**没有任何 Host/Origin 闸门**，`WebSessionManager.cs` 全文只有：
- `:117-122` `IsWebSocketUpgrade`（解析头）
- `:151-189` `HandleHttpRequest` —— 路径路由：`/api/snapshot` → JSON 快照；`/favicon.ico` → 404；**其余一律返回首页 HTML**（L177-187）。这条"默认兜底"对反代保留的路径前缀是宽容的。
- `:191-212` `CheckAuth` —— 仅在配置了 `WebServerPassword` 时生效，解析查询串 `pwd`。
- `:214-220` `SendHttpResponse` —— 固定带 `Access-Control-Allow-Origin: *`，对反代/跨源友好。
- `:389-421` `Handshake` —— 已在 `3b91b5c` 改为大小写不敏感。

**wss:// 支持**：`src/System/WebServer/WebPageContent.cs:349-351`
```javascript
const protocol = location.protocol === 'https:' ? 'wss://' : 'ws://';
ws = new WebSocket(protocol + location.host + qs);
```
即 Cloudflare Tunnel 的 `https://` 页面会**自动**用 `wss://` 回连，TLS 由隧道终止，服务端仍是明文 TCP —— 协议上成立。

**上游残余缺口（非 #580 根因）**：
- `Sec-WebSocket-Version` 从未被校验（上游 `WebSessionManager.cs` 全文无此字符串）。
- `Connection` 头从未被校验（RFC 6455 要求 `Connection: Upgrade`）。
- `HandleHttpRequest` 的路径取自 `lines[0].Split(' ')[1]`（L156），若反代改写路径前缀（如 `/litemonitor/`），`/api/snapshot` 匹配会失效（但首页兜底仍返回 HTML）。

### 3.4 fork 对照 —— **这是 fork 相对上游最关键的偏离**

fork 在 `src/System/WebServer/WebSessionManager.cs` 新增了一整套**访问边界策略**：

| fork 位置 | 内容 |
|---|---|
| `:1937-1969` | `BuildAccessPolicy()`：`AllowedHosts` 只放 `127.0.0.1` / `localhost` / `::1`，**再加上 LAN 模式下用户填写的 `WebServerLanAddresses`**（默认空） |
| `:1972-1986` | `IsHostAllowed()`：取 `Host` 头 → 拆分主机/端口 → 归一化 → 必须命中 `AllowedHosts`；**端口还必须等于真实监听端口**（L1983） |
| `:1989-2008` | `IsOriginAllowed()`：有 `Origin` 时必须精确命中 `WebServerAllowedOrigins`，或主机命中 `AllowedHosts` 且端口等于监听端口（`:2006`） |
| `:1355-1359` | HTTP 路径：**先**做 Host/Origin 校验 → 不过则 `SendErrorResponse(403)` |
| `:2535-2539` | WS 握手：**同样**先做 Host/Origin 校验 → 不过则 `403` |
| `:2045-2054` / `:2011-2042` | `NormalizeHost` / `SplitHostPort` |
| `WebServerAccessPolicy.cs:25-34` | LAN 暴露且无有效口令 ⇒ **拒绝启动监听** |

**直接后果**：Cloudflare Tunnel 转发过来的请求头是 `Host: app.example.com`（公网域名）+ `Origin: https://app.example.com`。这两个值**都不在** fork 的默认白名单里 → **HTTP 与 WS 双双 403**。用户即使把公网 Origin 填进 `WebServerAllowedOrigins`（`src/Core/Settings.cs:248`），`Host` 仍然过不了（验证程序 CASE F 第 4 项）。

**另外发现一处死设置**：`src/Core/Settings.cs:257` 定义了 `WebServerTrustProxyHeaders`（默认 `false`，注释写明"是否信任 `X-Forwarded-For` / `X-Real-IP` / `Forwarded`"），在 `WebSessionManager.cs:1944` 被赋给 `policy.TrustProxyHeaders`，字段声明在 `:2085` —— 但**全文件没有任何一处读取它**。全仓 grep `TrustProxyHeaders` 只有 `:1944`（写）与 `:2085`（声明）两处命中。该开关**当前完全无效**。

**再一个 fork 独有的硬约束**：`WebServerAccessPolicy.cs:25-34` —— LAN 模式且无口令 ⇒ 拒绝监听。#580 场景的用户恰恰是"局域网暴露 + 反代出去"，在 fork 上必须先配口令（fork 走一次性票据协议，`WebSessionManager.cs:2549-2558`）才能起服务。

### 3.5 复现条件与验证步骤

**验证手段（已执行）**：把 fork 的 `ParseRequest` / `IsHostAllowed` / `IsOriginAllowed` / `NormalizeHost` / `SplitHostPort` / `WebServerAccessPolicy.IsLanAccessForbidden` 逐字抽取进验证程序，构造反代请求：

```
== CASE F: reverse-proxy Host / Origin (#580, #589 through Cloudflare Tunnel) ==
  [PASS] fork Host 192.168.1.50:5000 allowed      expected=False actual=False (默认 LanAddresses 为空)
  [PASS] fork Host 127.0.0.1:5000 allowed         expected=True  actual=True
  [PASS] fork Host app.example.com allowed        expected=False actual=False  -> 403
  [PASS] fork Origin allowed (显式配置 origin)    expected=True  actual=True
  [PASS] fork Host still blocked                  expected=False actual=False  -> 403 即使 Origin 已放行
  [PASS] upstream has no Host/Origin gate         expected=False actual=False  -> 反代请求被接受
  [PASS] fork Host port mismatch rejected         expected=False actual=False  -> 403 (端口 != ListenPort)
== CASE G: LAN bind mode requires a password (fork-only hardening) ==
  [PASS] fork Lan mode + no password -> forbidden   actual=True
  [PASS] fork Lan mode + password    -> allowed      actual=False
  [PASS] fork Loopback + no password -> allowed      actual=False
```

**端到端复现办法（需真实进程 + 隧道，缺环境）**：
```powershell
# 1) 本机直连基线（fork）
curl.exe -i http://127.0.0.1:5000/            # 期望 200
# 2) 用隧道公网域名访问（把 Host 换成公网名）
curl.exe -i -H "Host: app.example.com" http://127.0.0.1:5000/
# fork 期望：403 + body 含 "Host / Origin 校验未通过。"
# 上游期望：200（首页 HTML）
```
隧道侧：`cloudflared tunnel run --url http://127.0.0.1:5000`，浏览器打开公网地址，观察 WebSocket 面板。

**"恢复成功"判据**：
- 上游：浏览器 WS 面板显示 `101 Switching Protocols`，状态码 101，页面实时数据在动。
- fork：反代访问必须能拿到 **200**（首页）而不是 403；并且在 fork 的设置里，`WebServerLanAddresses` 需填入公网主机名（或等价的放行手段），`WebServerAllowedOrigins` 需填 `https://<公网域名>`，且监听端口需与 Host 头端口一致 —— **目前 fork 没有可用的配置组合能同时满足这两条**，因为 `AllowedHosts` 走的是 `WebServerLanAddresses`，而 `IsHostAllowed` 的端口校验（L1983）仍要求 `Host` 端口 == 监听端口（5000），公网通常为 443。

### 3.6 结论
- **上游：部分确认**。#589 描述的根因（头名大小写）已修，`wss://` 也支持；但**未在真实 Cloudflare Tunnel 环境下端到端复现**，且 issue 正文只有截图、无复现步骤，"显示异常"的具体形态无法从文本判定。→ 结论受限于**缺少 Cloudflare Tunnel 环境 + 缺少截图内容**。
- **fork：确认存在（且比上游更严重）**。fork 新增的 Host/Origin 白名单闸门默认拒绝一切反代来源（403），并且 `WebServerTrustProxyHeaders` 是死设置，无法用来解决。该问题**不是**从上游继承的，而是 fork 自身引入的。

### 3.7 风险
- fork 的 403 是"快速失败"，比上游的"灰屏"更易诊断；但对已按上游文档配置反代的用户是**功能性破坏**。
- fork 端口强校验（L1983 / L2006）意味着**任何**走标准 443 的反代在 fork 上都不可能通过 —— 除非去掉端口校验。
- fork `WebServerTrustProxyHeaders` 死设置会给用户"我已经开了信任代理头"的错觉，实际不生效。

### 3.8 建议下一步
1. fork：让 `WebServerTrustProxyHeaders` 真正生效 —— 当该开关为 `true` 时，用 `X-Forwarded-Host` / `Forwarded` 的 host 参与 `AllowedHosts` 判定，并**放宽端口校验**（反代端口与本地监听端口本就不同）。
2. fork：给 `AllowedHosts` 增加"任意主机名"模式（显式开关），供反代场景使用；或至少让 `WebServerLanAddresses` 支持主机名（当前 `NormalizeHost` 只做小写/去括号，行为上支持主机名，但 UI 文案与校验口径未对齐）。
3. 上游：请 issue 提交者补充文字复现步骤与浏览器 Network 面板截图，以便区分"握手失败"与"数据解析失败"。

---

## 4. #611 — 「放假倒计时」插件 API 返回 null 后只显示 ❌

### 4.1 当前状态
`OPEN`，创建于 2026-10-19T17:26:00Z，最后更新 2026-10-25T02:32:10Z，2 条评论（`mn-sl` 2026-10-25："我也是" + 截图）。

### 4.2 问题摘要
节假日接口自 10 月 11 日起不再返回数据，任务栏只剩 ❌。报告者直接请求："**能不能出个距离元旦还有多少天接一下**"——这正是上游后来实现的方案。

### 4.3 上游代码证据 —— **上游已实现，且实现了 issue 要的东西**

1. **`resources/plugins/Holiday.json`**（上游版本 `"version": "1.1"`，作者 `LiteMonitor`）：
   ```json
   "description": "显示距离下一个法定节假日还有多少天（含调休计算）；接口无数据或不可用时显示元旦倒计时"
   "steps": [
     { "id": "fetch_holiday", "url": "https://timor.tech/api/holiday/next",
       "method": "GET", "response_format": "json", "continue_on_error": true,
       "extract": { "name": "holiday.name", "date": "holiday.date", "rest": "holiday.rest" } },
     { "id": "fallback_newyear", "url": "native://newyear", "skip_if_set": "rest",
       "extract": { "name": "name", "date": "date", "rest": "rest" } }
   ]
   ```
2. **`src/Plugins/PluginModels.cs:152-157`** —— 模型支持 `skip_if_set` 与 `continue_on_error`。
3. **`src/Plugins/PluginExecutor.cs:260-283`** —— `ExecuteStepAsync` 实现 `continue_on_error`：
   ```csharp
   if (!step.ContinueOnError) { await ExecuteStepCoreAsync(...); return; }
   // 可回退的步骤只在成功后合并变量，避免部分结果或错误标记阻止备用步骤。
   var stepContext = new Dictionary<string, string>(context);
   try { ... await ExecuteStepCoreAsync(inst, step, stepContext, ...);
         foreach (var kv in stepContext) context[kv.Key] = kv.Value; }
   catch (Exception ex) { token.ThrowIfCancellationRequested(); Debug.WriteLine(...); }
   ```
   **关键**：失败时 `stepContext` **不并入** `context`，因此 `rest` 不会被污染成 `❓`，备用步骤得以执行。
4. **`src/Plugins/PluginExecutor.cs:285-295`** —— `skip_if_set` 判定：`context["rest"]` 非空则跳过本步。
5. **`src/Plugins/PluginExecutor.cs:506-509`** —— `native://newyear` 处理器：
   ```csharp
   else if (uri.Host.Equals("newyear", StringComparison.OrdinalIgnoreCase))
       return NewYearNative.GetCountdownJson(DateTime.Today);
   ```
6. **`src/Plugins/Native/NewYearNative.cs:10-25`** —— 纯本地计算，不依赖网络：
   ```csharp
   // 在节假日接口不可用时，按本机日期计算元旦倒计时，不推测调休安排。
   var newYear = new DateTime(today.Year, 1, 1);
   if (newYear < today) newYear = newYear.AddYears(1);
   return JsonSerializer.Serialize(new { name = "元旦", date = ..., rest = (newYear - today).Days });
   ```
7. **`src/Plugins/PluginProcessor.cs:21-68`** —— `ExtractJsonValue` 遇到路径不可达时 `return PluginConstants.STATUS_UNKNOWN`（即 `❓`，`PluginConstants.cs:26`），**不抛异常**。
8. **`src/Plugins/PluginExecutor.cs:387-400`** —— 但在提取之后有一道校验：若任一提取值等于 `❓` 或 `❌`，**抛异常**（`throw new Exception($"Step {step.Id} failed: ...")`）。

**完整失败链（上游）**：
```
接口返回 null / 404 / 超时
  → ParseAndExtract 得到 name/date/rest = "❓"            (PluginProcessor.cs:58)
  → 关键值校验命中 ❓ → throw                              (PluginExecutor.cs:396)
  → continue_on_error 捕获并吞掉；stepContext 不合并，context 里没有 "rest"
  → 步骤2 skip_if_set="rest" 不成立 → 执行
  → native://newyear → NewYearNative 本地算元旦           (PluginExecutor.cs:508)
  → 输出 "距离元旦 N 天"
```
**上游不会显示 ❌，而是优雅降级为元旦倒计时** —— 恰好满足 issue 的诉求。

### 4.4 fork 对照 —— **确认回归**

fork 的 `resources/plugins/Holiday.json` 被**降级**回 v1.0（`"author": "LiteMonitor++"`），与上游 v1.1 的 diff：

```diff
-    "version": "1.1",
-    "description": "...接口无数据或不可用时显示元旦倒计时"
+    "version": "1.0",
+    "description": "显示距离下一个法定节假日还有多少天（含调休计算）"
@@
-        "url": "https://timor.tech/api/holiday/next",
+        "url": "http://timor.tech/api/holiday/next",
         "method": "GET",
         "response_format": "json",
-        "continue_on_error": true,
         "extract": { "name": "holiday.name", "date": "holiday.date", "rest": "holiday.rest" }
-      },
-      {
-        "id": "fallback_newyear",
-        "url": "native://newyear",
-        "skip_if_set": "rest",
-        "extract": { "name": "name", "date": "date", "rest": "rest" }
       }
@@
-      "short_label": "🏖️",
+      "short_label": "放假",
```

四处退化：
1. **兜底步骤 `fallback_newyear` 被删除**；
2. **`continue_on_error: true` 被删除**；
3. **`https://` 降级为 `http://`**（明文传输，且该域名若有 HTTPS-only / 反代策略会直接失败）；
4. `src/Plugins/Native/NewYearNative.cs` **整文件从 fork 删除**（`git diff --stat` 显示 `NewYearNative.cs | 26 -`，0 增）。

而且**不是只改了模板，是能力也没了**：
- `src/Plugins/PluginModels.cs:164-165` —— fork 的 `PluginExecutionStep` **只有 `skip_if_set`，没有 `continue_on_error` 字段**（全仓 grep `continue_on_error|ContinueOnError` 在 `master:src/Plugins/` 下**零命中**）。即使日后把模板改回去，JSON 里的 `continue_on_error` 也会被忽略。
- `src/Plugins/PluginExecutor.cs:736-748` —— fork 的 `ExecuteNativeAsync` 只注册了 `citycode`（L736）与 `crypto`（L743）两个 handler，**没有 `newyear`**。

**完整失败链（fork）**：
```
接口返回 null
  → 提取得到 name/date/rest = "❓"
  → 关键值校验命中 ❓ → throw                              (fork PluginExecutor.cs:617)
  → 无 continue_on_error，异常直接冒泡出整个 chain
  → PublishExecutionErrorAsync → values.Add(new(raw, STATUS_ERROR))  (fork PluginExecutor.cs:1358)
  → InfoService.InjectValue(key, "❌")                      (fork PluginExecutor.cs:1371)
  → 任务栏显示 ❌
```
**与 issue 报告的症状完全吻合。**

### 4.5 复现条件与验证步骤

**失败注入办法（不改产品代码）**：
1. 用 hosts / DNS 覆盖把 `timor.tech` 指向 `127.0.0.1`，在本机起一个返回 `null` 的 mock：
   ```powershell
   # mock: 返回 HTTP 200 + body "null"
   $listener = [System.Net.HttpListener]::new("http://127.0.0.1:8099/")
   $listener.Start()
   while ($listener.IsListening) {
     $ctx = $listener.GetContext()
     $body = [Text.Encoding]::UTF8.GetBytes("null")
     $ctx.Response.StatusCode = 200
     $ctx.Response.ContentType = "application/json"
     $ctx.Response.OutputStream.Write($body,0,$body.Length)
     $ctx.Response.Close()
   }
   ```
   然后把 `resources/plugins/Holiday.json` 的 URL 临时指向 mock（**仅本地实验副本，勿提交**）。
2. 或直接让请求失败：断网 / 把 `timor.tech` 解析到黑洞 → 上游走 `continue_on_error` + `native://newyear`；fork 走 `❌`。

**"恢复成功"判据**：
- **上游**：任务栏显示 `距离元旦 N 天`（`🏖️`），**不出现 ❌**；`Debug` 输出中可见 `Step fetch_holiday fallback: ...`。
- **fork**：任务栏显示 ❌（`PluginConstants.STATUS_ERROR`）；`Debug` 输出 `Plugin exec error (Holiday): Step fetch_holiday failed: Extracted invalid value for 'rest'. Value: '❓'`。
- 两者都必须满足：接口恢复后，下一次执行应自动显示真实节假日倒计时（fork 的重试退避见 §6 #235，间隔上限 60s）。

### 4.6 结论
- **上游：已解决**。issue 诉求（"出个距离元旦还有多少天接一下"）已被 `Holiday.json` v1.1 + `native://newyear` + `NewYearNative.cs` 完整实现。
- **fork：确认（回归）**。fork 把上游的降级路径整体删除（模板降级 v1.0 + `continue_on_error` 能力缺失 + `native://newyear` 处理器缺失 + `NewYearNative.cs` 删除），导致接口一失败就直接打 ❌ 错误标记。

### 4.7 风险
- fork 的 `http://timor.tech/...` 走明文；`HttpClient` 默认跟随重定向，若目标站点强制 HTTPS 会 301→https 尚可，但若返回 403/阻断明文访问则插件彻底不可用。
- fork 删掉 `continue_on_error` **是能力级移除**，影响的不止 Holiday：任何想用"主源失败 → 备用源"模式的插件模板在 fork 上都无法工作。
- fork 的 `short_label` 从 `🏖️` 改成 `放假`，在窄任务栏上会挤占更多宽度（次要）。

### 4.8 建议下一步
1. fork：回补上游 `Holiday.json` v1.1（https + `continue_on_error` + `fallback_newyear` 步骤）。
2. fork：在 `PluginModels.cs` 恢复 `continue_on_error` 字段并在 `PluginExecutor.cs` 恢复对应的"仅成功才合并变量"语义。
3. fork：恢复 `src/Plugins/Native/NewYearNative.cs` 与 `native://newyear` handler（并接入 fork 的沙箱闸门：fork 的 `ExecuteNativeAsync` 有 `declaredHosts` / `maxResponseBytes` / `nativeToken` 参数，新 handler 需沿用同一约束）。
4. fork：把 `PluginConstants.cs` 里的插件名清单与实际内置插件集核对（`PluginConstants.cs:63` 已列 `Holiday`）。

---

## 5. #96 — 网络检测开启一段时间后数据卡住 & 网卡选择

### 5.1 当前状态
`OPEN`，创建于 2025-12-27（正文），2 条评论：
- `Diorser` 2025-12-27："设置窗口里可以切换网卡，切换网卡或者重启软件看看是否正常？"
- `zhucaidan` 2025-12-28："**重启软件正常了**。建议在右键软件后增加一个网卡选择菜单，谢谢！"

### 5.2 问题摘要
两个子项：
- **(a) 卡住**：开启网络检测一段时间后，上传/下载数据不再变化（截图显示的应是速率归零或数值冻结）。
- **(b) 网卡选择**：希望在**右键菜单**里能选择要监控的网卡。

### 5.3 上游代码证据

**(b) 网卡选择**：
- `src/Core/Settings.cs:32-33` —— `PreferredNetwork` / `LastAutoNetwork` 两个持久化字段。
- `src/UI/Settings/SystemHardwarPage.cs:116` —— `FillSync(_cbNet, taskNets.Result, Config.PreferredNetwork)`，**设置页有网卡下拉框**。
- `src/UI/Settings/SystemHardwarPage.cs:170-171` —— 绑定 `Config.PreferredNetwork`（选"自动"则写 `""`）。
- `src/System/HardwareServices/NetworkManager.cs:64-66` —— `isTarget` 判定同时认 `LastAutoNetwork` 与 `PreferredNetwork`。
- `src/System/HardwareServices/NetworkManager.cs:240-251` —— 启动时用 `LastAutoNetwork` 直接命中上次网卡，避免全盘扫描。

⇒ **设置界面的网卡选择上游已有**（维护者回复属实）；但 **issue 要求的"右键菜单网卡选择"上游未实现**（`src/Core/Actions/WebActions.cs` 等右键菜单里没有网卡项）。

**(a) 卡住** —— 上游相关机制：
- `src/System/HardwareServices/NetworkManager.cs:216-299` `GetBestValue`：运行时缓存 `_cachedNetHw`；**存活检查**（L222-226）
  ```csharp
  if (!computer.Hardware.Contains(_cachedNetHw)) { _cachedNetHw = null; }
  ```
- `src/System/HardwareServices/NetworkManager.cs:301-318` `ReadNetworkSensor`：传感器值为 NaN 时**回落到 `lastValidMap` 里的上一次有效值**（L316-317）。
- `src/System/HardwareServices/NetworkManager.cs:62-83` `ProcessUpdate`：只有 `isTarget` 的硬件会被 `hw.Update()`；非目标硬件在启动 3 秒后、或在慢扫描 tick 才更新。
- `src/System/HardwareServices/NetworkManager.cs:429-475` `MatchNativeNetworkAdapter`：`state.NativeAdapter != null` 就直接返回（L431），匹配有 10 秒节流（L432）。
- `src/System/HardwareServices/NetworkManager.cs:45-51` / `:88-105` —— 网络变更时清 `_staticIPCache` 并重置 `NativeAdapter`（见 §6 #287）。

**关键推论（区分「停止采集」与「有值但未刷新」）**：上游的存活检查只看"该硬件**是否还存在于** `computer.Hardware` 列表中"。一块**仍在枚举但已不再产生流量**的网卡（例如切到另一块网卡后旧网卡静默）**不会**被丢弃；而 `ReadNetworkSensor` 在 NaN 时会返回 `lastValidMap` 里的旧值 —— 于是界面**冻结在上一次的数字**而不是归零。这与 issue 截图"数据卡住"以及报告者"重启软件正常了"（重启会清空 `lastValidMap` 与 `_cachedNetHw`）高度吻合。

**但**：上游**没有**找到针对该"卡住"现象的专门修复提交（`git log upstream/master -- src/System/HardwareServices/NetworkManager.cs` 中 `#287`/`#206` 标记针对的是 IP 与 APIPA，不是冻结）。故上游该子项判定为**未复现 / 未定位**。

### 5.4 fork 对照

fork `src/System/HardwareServices/NetworkManager.cs` 净 +583 行。核心机制与上游同构：
- `fork :48-52` `_staticIPCache` / `_shouldResetAdapters`；`:85-90` `NetworkAddressChanged` 处理器；`:93` `ClearCache()`；`:256-273` 变更后重置 `NativeAdapter`（fork 用 `state.HasMatchAttempt = false` 代替上游的 `LastMatchAttempt = DateTime.MinValue`）；`:278` 30s 缓存窗（`IsIPCheckFresh()`，L247-251，**改用单调时钟**）；`:359-361` 跳过 APIPA。
- `fork :385-466` `GetBestValue` / `:470-485` `ReadNetworkSensor` —— **同样保留了 `lastValidMap` 回落**（`:466`、`:485`），因此**同样存在"冻结在旧值"的形态**。
- fork 额外新增 `src/System/InfoService/InfoService.cs:71-75`：网络变更时强制 UI 立即刷新（`_currentInterval = INTERVAL_FAST; _lastUpdateTick = 0;`）。

**结论性差异**：fork 在 **IP 刷新**上比上游强（见 §6 #287），但在 **"冻结在旧值"** 这件事上**与上游一样没有修** —— `lastValidMap` 回落逻辑两侧一致。

### 5.5 复现条件与验证步骤

**复现条件（卡住）**：
1. 设置里选 `PreferredNetwork` = 某块物理网卡（如 Wi-Fi）。
2. 启动，让网速有流量（`lastValidMap["NET.Up"]` / `["NET.Down"]` 被写入）。
3. 拔掉该网卡 / 禁用该网卡，但**不要**退出程序（网卡仍在 `computer.Hardware` 列表中）。
4. 观察任务栏 `NET.Up` / `NET.Down` —— 预期**冻结在上一次的数值**而不是归零或显示 `❓`。

**区分「停止采集」与「有值未刷新」的判据**：
- 若 `ProcessUpdate` 不再对该硬件调 `hw.Update()`（`NetworkManager.cs:70-74` 的 `isTarget` 为 false）⇒ 属于**停止采集**。
- 若 `hw.Update()` 仍在跑、传感器值确为 0/NaN，但界面显示旧值 ⇒ 属于**有值但未刷新显示**，根因在 `ReadNetworkSensor` 的 `lastValidMap` 回落（L316-317）。
- 调试手段：在 `Debug` 输出里同时打 `hw.Update()` 是否执行、`target?.Value` 原值、`lastValidMap[key]` 回落值，三者对比即可定性。

**复现条件（网卡选择 / 右键菜单）**：右键托盘图标 → 观察菜单项列表（上游与 fork 均无"选择网卡"项）。

**"恢复成功"判据**：拔网卡后 NET.Up/NET.Down 归零（或显示 `❓`）而非停在旧值；`PreferredNetwork` 可在右键菜单中切换且立即生效。

### 5.6 结论
- **上游：部分确认**。子项 (b)「设置窗口可切换网卡」**已实现**；子项 (b')「右键菜单网卡选择」**未实现**；子项 (a)「数据卡住」**未复现** —— 未找到对应修复提交，但静态代码给出了明确的冻结机制假设（`lastValidMap` 回落 + 存活检查只看枚举列表），可解释"重启即恢复"。
- **fork：部分确认**。同构的 `lastValidMap` 回落仍在（`fork NetworkManager.cs:466,485`），冻结形态未消除；但 IP 刷新路径 fork 更稳（单调时钟 + InfoService 即时刷新）。右键菜单网卡选择同样未实现。

### 5.7 风险
- 「冻结在旧值」比「归零」更有误导性：用户会以为网卡还在跑流量。
- `lastValidMap` 是跨 key 的共享回落表，若某块网卡被"选错"，回落值可能来自另一块网卡的历史。

### 5.8 建议下一步
1. fork/上游：在存活检查里加"该 NIC 连续 N 个慢扫描周期零流量 ⇒ 丢弃 `_cachedNetHw` 并重新选优"，或让 `ReadNetworkSensor` 在回落时同时校验该值的新鲜度。
2. 两边都未做：右键菜单网卡选择（#96 明确诉求），可在托盘菜单里加一个网卡的二级菜单。

---

## 6. #287 — 切换 Wi-Fi 后局域网 IP 未更新

### 6.1 当前状态
`OPEN`，创建于 2026-02-08，2 条评论：
- `Diorser` 2026-02-08："设置里有设置首选网卡吗？一般情况下 只要跑出网速 就会自动识别新的网络"
- `Diorser` 2026-02-12："**新版已经修复**"

### 6.2 问题摘要
切换 Wi-Fi 后网段与设备 IP 全变，但软件内显示的内网 IP 仍是旧的，导致**网页版打不开**（因为服务端对外公布的还是旧 IP）。

### 6.3 上游代码证据

上游存在成组的 `[Fix #287]` 标记，覆盖了这条链路：

| 上游位置 | 内容 |
|---|---|
| `src/System/HardwareServices/NetworkManager.cs:35` | `private volatile bool _shouldResetAdapters = false; // [Fix #287] 网络变更标记` |
| `:45-51` | 构造函数内订阅 `NetworkChange.NetworkAddressChanged += (s,e) => { _staticIPCache = null; _shouldResetAdapters = true; };` |
| `:88-105` | `GetCurrentIP()` 开头消费 `_shouldResetAdapters`：把每个 `NetworkState` 的 `NativeAdapter` 置 null、`LastMatchAttempt` 复位 ⇒ **强制 `MatchNativeNetworkAdapter` 重新枚举 `NetworkInterface.GetAllNetworkInterfaces()` 找新实例** |
| `:109` | 30 秒 IP 缓存窗（`!string.IsNullOrEmpty(_staticIPCache) && (DateTime.Now - _lastIPCheckTime).TotalSeconds < 30`）——缓存被事件清空后该窗口自动失效，立即强制重查 |
| `:190-192` | 跳过 APIPA（`169.254.x.x`），注释：*"这些地址通常是 DHCP 失败时的临时地址，会导致网页版服务绑定错误"* |
| `:194-196` | 优先返回 `192.168.x.x` |
| `:177-211` | `GetIPv4FromAdapter` 在异常时返回 null，向上冒泡为"重新扫描" |

⇒ **上游已修复**，与维护者 2026-02-12 的回复一致（issue 创建于 02-08，修复落在其后 4 天内的版本）。

### 6.4 fork 对照 —— **fork 强于上游**

fork `src/System/HardwareServices/NetworkManager.cs` 保留了同一套 `[Fix #287]` 机制：
- `:48` `_staticIPCache`；`:52` `_shouldResetAdapters`；`:85-90` `NetworkAddressChanged` 订阅；`:256-273` 消费标志并重置 `NativeAdapter` / `HasMatchAttempt`；`:359-361` 跳过 APIPA。

并额外补了两点上游没有的：
1. **`src/System/InfoService/InfoService.cs:71-75`**：
   ```csharp
   // [Fix #287] 监听网络变更，立即触发IP刷新
   NetworkChange.NetworkAddressChanged += (s, e) => { _currentInterval = INTERVAL_FAST; _lastUpdateTick = 0; };
   ```
   上游的 `InfoService` **没有**网络变更监听 —— 上游只是把 IP 缓存清空，而**界面上的 IP 文本**要等 `InfoService` 自己的刷新周期才变。fork 直接强制下一次 `Update` 立即执行 ⇒ **"软件内识别的内网 ip 仍为原来的 ip" 这一显示层症状被真正消除**（上游在显示层可能仍有滞后）。
2. **单调时钟的缓存窗**：fork `:240-251` 用 `_time.GetElapsedTime(...)` 判 30s 窗口，注释明确写 *"墙上时间回拨不会让缓存窗口永远不过期"*；上游 `:109` 用墙钟 `DateTime.Now - _lastIPCheckTime`，系统时间回拨会让旧 IP 被无限期缓存。

### 6.5 复现条件与验证步骤

**复现条件**：
1. 连 Wi-Fi A，记下任务栏 / 网页版显示的内网 IP（应是 `192.168.x.x`）。
2. 切到 Wi-Fi B（**同一个物理网卡**，DHCP 换网段）。
3. 观察软件内 IP 是否更新。

**"恢复成功"判据**：
- `NetworkManager.GetCurrentIP()` 返回**新**网段的 `192.168.x.x`（不是 `169.254.x.x`、不是旧值）。
- `src/System/HardwareServices/NetworkManager.cs:437` 的 `MatchNativeNetworkAdapter` 拿到的是**新的** `NetworkInterface` 实例（不是切换前的旧对象）。
- fork：界面上的 IP **立即**变为新值（`InfoService` 强制刷新），无需等待常规刷新周期。
- 网页版：用新 IP + 端口能打开（服务端对外公布的 IP 已同步）。

**未实测原因**：本环境无可切换的第二块 Wi-Fi 链路；已用静态代码取证 + 维护者"新版已修复"的回复交叉印证。

### 6.6 结论
- **上游：已解决**（维护者已确认，代码有完整 `[Fix #287]` 标记链）。残留两点：只订阅 `NetworkAddressChanged`（未订阅 `NetworkAvailabilityChanged`）；`InfoService` 显示层刷新滞后。
- **fork：已解决，且强于上游** —— 同样具备 `[Fix #287]` 链路，并额外用 `InfoService.cs:71-75` + 单调时钟缓存窗补齐了显示层与时钟回拨两个缺口。

### 6.7 风险
- `NetworkChange.NetworkAddressChanged` 是**静态事件**，上下游两处订阅（`NetworkManager` 构造、`InfoService` 构造）**均未退订**（上游 1 处、fork 2 处）—— 程序生命周期内单实例通常无害，但反复重建实例会累积订阅。
- 某些驱动在"同网卡换 SSID"时可能不触发 `NetworkAddressChanged` ⇒ 依赖 30s 窗口自然过期，最长滞后 30 秒。
- fork 的 `_staticIPCache` 仍是 `static`（`:48`），跨实例共享。

### 6.8 建议下一步
1. 上游/fork 都可补 `NetworkChange.NetworkAvailabilityChanged` 订阅，覆盖"链路 down→up 但地址未变"的场景。
2. 上游可把 `InfoService` 的 IP 刷新改为按 `NetworkAddressChanged` 立即触发（对齐 fork）。
3. 上游可把 30s 窗口改为单调时钟（对齐 fork）。

---

## 7. #235 — 启动时网络节点不可达，恢复连接后插件数据不刷新

### 7.1 当前状态
`OPEN`，创建于 2025-12-xx（正文），3 条评论：
- `Diorser` 2026-01-29 17:23："**新版本已经修复**"
- `chenbi001` 2026-02-04 02:02：转而报告"鼠标光标拖动卡顿"，并称"只要把 litemonitor 退出就立刻恢复正常"
- `Diorser` 2026-02-04 07:46：问"有开启网页版吗？"
- `chenbi001` 2026-02-04 07:48："没有开启"

⇒ 后半段是**另一个问题**（鼠标卡顿），与本 issue 的插件刷新无关，且**未解决**。

### 7.2 问题摘要
程序启动时网络未连通，天气插件与科学节点延迟插件持续显示错误；**即使后续网络恢复，也不再刷新**。另提到"科学节点有概率能刷新，但布局会有问题"。

### 7.3 上游代码证据

上游 `src/Plugins/PluginManager.cs` 的定时器骨架（`341-417`）：

```csharp
newTimer.Elapsed += async (s, e) => {
    if (cts.IsCancellationRequested) return;
    if (!IsPluginVisible(inst.Id)) { newTimer.Interval = 2000; return; }   // 不可见 → 不执行
    try {
        bool success = await _executor.ExecuteInstanceAsync(inst, tmpl, cts.Token);
        if (!success) {
            int fails = _consecutiveFailures.AddOrUpdate(inst.Id, 1, (k,v) => v+1);
            int backoff = Math.Min(fails * PluginConstants.RETRY_INTERVAL_MS, 60000);   // 5s,10s,...,60s
            if (fails >= 3 && fails % 3 == 0) {                                 // L377-382
                _executor.ResetNetworkClients();                               // 重置 HTTP 客户端
                _executor.ClearCache(inst.Id);                                 // 清步骤缓存
            }
            newTimer.Interval = backoff;
        } else {
            _consecutiveFailures.TryRemove(inst.Id, out _);                    // 成功 → 计数清零
            newTimer.Interval = interval * 1000;                                // 恢复常规间隔
        }
    } catch (Exception ex) { /* 异常路径同样退避 */ }
    finally {
        if (_timers.ContainsKey(inst.Id) && inst.Enabled && !cts.IsCancellationRequested)
            newTimer.Start();                                                 // L403-409 无论如何重启定时器
    }
};
```

- `src/Plugins/PluginConstants.cs:37` —— `RETRY_INTERVAL_MS = 5000`。
- `src/Plugins/PluginExecutor.cs:599-637` `ProcessOutputs`：`val != currentVal` 才 `InjectValue`（L634-637）⇒ 恢复后写入真实值会**覆盖掉先前注入的 ❌**。
- `src/Plugins/PluginExecutor.cs:708` —— 失败时 `InfoService.Instance.InjectValue(injectKey, PluginConstants.STATUS_ERROR)`。

**恢复链路**：网络恢复 → 下一个 tick 执行成功 → `success == true` → 失败计数清零、间隔恢复常规值 → `ProcessOutputs` 检测到值变化 → `InjectValue(真实值)` → 界面刷新。**采集任务没有停，失败只是把执行间隔拉长（最长 60s）。**

**关键时间线吻合**：上游提交 `4036cee`（2026-01-29，"改进插件失败重试逻辑，采用线性退避策略（5s, 10s... 最大60s）"）与维护者 2026-01-29 17:23 的"新版本已经修复"是**同一天** ⇒ 修复提交就是本 issue 的落地。

### 7.4 fork 对照 —— **等价实现**

fork `src/Plugins/PluginManager.cs:485-537`：
```csharp
if (success) { _consecutiveFailures.TryRemove(runtime.Instance.Id, out _); timer.Interval = runtime.IntervalSeconds * 1000; }
else {
    int failures = _consecutiveFailures.AddOrUpdate(runtime.Instance.Id, 1, (_, n) => n + 1);
    timer.Interval = Math.Min(failures * PluginConstants.RETRY_INTERVAL_MS, 60000);   // L505-506
    if (failures >= 3 && failures % 3 == 0) { _executor.ResetNetworkClients(); _executor.ClearCache(runtime.Instance.Id); }  // L507-514
}
catch (Exception ex) { ... timer.Interval = Math.Min(failures * RETRY_INTERVAL_MS, 60000); }   // L518-526
finally { runtime.Lease.TryCommit(() => { if (runtime.Source.Enabled && ...) timer.Start(); }); }  // L527-534
```
退避公式、失败阈值、重置条件、异常路径、`finally` 重启——**与上游逐条对应**。fork 额外用 lease / `TryCommit` 包住了定时器回调，避免旧代次回调污染新状态。

⇒ **fork 与上游在 #235 的核心机制上等价，双方都已修复。**

### 7.5 复现条件与验证步骤

**失败注入**：
1. 断网（或把代理指向一个不可达地址；`resources/plugins/ProxyLatency.json` 的 `proxy_server` 默认 `127.0.0.1:7890`，把它指向 `127.0.0.1:1` 即可让"科学节点延迟"稳定失败）。
2. 启动 LiteMonitor，观察插件项显示 ❌。
3. 恢复网络 / 恢复代理。

**"恢复成功"判据**：
- 插件项从 ❌ 变回真实数值（天气温度 / 延迟毫秒数）。
- 退避节奏：失败第 1 次 5s、第 2 次 10s …… 上限 60s ⇒ **最坏需等 60s**才可能成功一次，恢复延迟是预期行为，不是 bug。
- `Debug` 输出应出现 `Plugin {inst.Id} failed (N times). Retry in {backoff}ms.`，以及失败 3/6/9… 次时的 `Resetting network clients and clearing cache.`。
- 界面刷新：upstream `ProcessOutputs` L634-637 的"值变化才注入"保证 ❌ 被真实值覆盖。

**布局问题（"科学节点有概率能刷新，但布局会有问题"）**：
⇒ **未能验证**。未在代码中找到与插件项**布局**相关的专门修复；`InfoService.InjectValue` 只改数据，布局由 `src/UI/UIController.cs` / `UIRenderer` 负责。该子项在两侧都属**信息不足**。

### 7.6 结论
- **上游：已解决**（核心诉求"恢复连接后插件数据不刷新"已由 4036cee 的线性退避重试覆盖，代码可验证）。
- **fork：已解决（等价实现）**。
- 两个附带子项均不成立/信息不足：① 鼠标卡顿（与网页版无关，用户明确"没有开启网页版"，两侧均无对应代码线索，**未解决**）；② 布局问题（**信息不足**）。

### 7.7 风险
- 退避上限 60s ⇒ 网络恢复后最长 1 分钟才回填，用户容易误判为"还没恢复"。
- `IsPluginVisible` 闸门：插件不可见时不执行（仅 2s 轮询可见性），恢复后若插件处于隐藏状态则不会立即刷新。
- `PluginManager` 的 `NetworkChange` / 定时器退订路径未逐一核对（本次未展开）。

### 7.8 建议下一步
1. 上游：关闭 #235，并单独开单跟踪"鼠标拖动卡顿"（需 `Diorser` 复现）。
2. 上游/fork：考虑在网络恢复（`NetworkAvailabilityChanged`）时**立即**把插件间隔从退避值拉回常规值，而不是等 60s 自然到期 —— fork 已订阅 `NetworkAddressChanged`，可复用。

---

## 8. fork 的 WebServer / 声明式插件执行器 vs 上游 代码映射表

| 关注点 | 上游实现（文件:行号） | fork 实现（文件:行号） | 差异 | 是否已修复相关 issue |
|---|---|---|---|---|
| WS 升级判定 | `src/System/WebServer/WebSessionManager.cs:117-122`（解析头 + `OrdinalIgnoreCase`） | `src/System/WebServer/WebSessionManager.cs:1323-1327`（**正则** `Regex.IsMatch(req,"Upgrade: websocket",IgnoreCase)`） | **fork 退回正则**，要求原文恰好出现 `Upgrade: websocket`（单空格）；RFC 7230 允许 `Upgrade:websocket` / 多空格 | 上游 ✅；fork ⚠️（#589 根因已修，但新脆弱点） |
| `Sec-WebSocket-Key` 提取 | `WebSessionManager.cs:125-149` `TryGetHeaderValue` + `:400-401` | `WebSessionManager.cs:2616-2662` `TryGetWebSocketKey` + `:2672-2678` `IsWebSocketKeyShapeValid` + `:2561` | **fork 更严**：重复键拒绝、Base64/16 字节形态校验、失败回 400（上游静默失败） | 两侧均 ✅ 已修 #589 根因 |
| Host / Origin 白名单 | **无** | `WebSessionManager.cs:1937-1969` `BuildAccessPolicy`、`:1972-1986` `IsHostAllowed`、`:1989-2008` `IsOriginAllowed`；HTTP `:1355-1359`、WS `:2535-2539` 均返回 **403** | **fork 新增**。默认只放行回环 + `WebServerLanAddresses`（默认空）；端口还须等于监听端口 | ❌ **fork 使 #580 恶化** |
| 反代头信任 | 无此概念 | `src/Core/Settings.cs:257` `WebServerTrustProxyHeaders`；`WebSessionManager.cs:1944` 赋值、`:2085` 字段声明 | **死设置**：全仓无任何读取点，开关不生效 | ❌ 无效 |
| 监听范围 | `LiteWebServer.cs:40-82`（始终 `IPv6Any`→回退 `IPAddress.Any`，无模式区分） | `LiteWebServer.cs:238-302`（Loopback / Lan 分支，L302 写入 `ListenPort`）+ `WebServerAccessPolicy.cs:25-34` | **fork 新增**绑定模式与"Lan 必须有口令"硬约束 | fork 独有约束，与 #580 场景冲突 |
| 认证协议 | `WebSessionManager.cs:191-212`（查询串 `?pwd=`，`Regex.Match` 已被 3b91b5c 换掉） | `WebSessionManager.cs:2543-2547`（`?pwd=` 已移除 → 401）、`:2549-2558`（**一次性票据** `?ticket=`，30s TTL、仅限 WS、绑定主会话） | 协议不兼容；fork 增加会话/票据体系 | — |
| CORS | `WebSessionManager.cs:216` 固定 `Access-Control-Allow-Origin: *` | `WebSessionManager.cs:2092-2155` 默认**不返回**任何 CORS 头；仅回显显式白名单源 + `Vary: Origin` | fork 收紧（默认同源） | — |
| 错误可见性 | `WebSessionManager.cs:389-421` 握手失败**静默** `return false`（不回字节） | `WebSessionManager.cs:2538/2545/2556/2563` 分别回 **403 / 401 / 401 / 400** | fork 可诊断性更好 | — |
| 插件回退语义 | `src/Plugins/PluginExecutor.cs:260-283`（`continue_on_error` + "仅成功才合并变量"）、`PluginModels.cs:152-157` | `src/Plugins/PluginExecutor.cs:454-456`（仅 `skip_if_set`）、`PluginModels.cs:164-165`（**无 `continue_on_error` 字段**） | **fork 能力级移除** `continue_on_error` | ❌ **fork 回归 #611** |
| native 处理器 | `PluginExecutor.cs:506-509` `native://newyear` + `src/Plugins/Native/NewYearNative.cs:10-25` | `PluginExecutor.cs:736-748`（仅 `citycode` / `crypto`）；`NewYearNative.cs` **整文件删除** | **fork 删除**本地兜底 | ❌ **fork 回归 #611** |
| 插件模板 | `resources/plugins/Holiday.json` v1.1（https + `continue_on_error` + `fallback_newyear`） | `resources/plugins/Holiday.json` v1.0（**http**、无兜底、无 `continue_on_error`） | fork 模板降级 | ❌ **fork 回归 #611** |
| 插件失败重试 | `src/Plugins/PluginManager.cs:369-402`（5s→60s 线性退避；`fails>=3 && fails%3==0` 重置客户端与缓存）；`:403-409` `finally` 重启定时器 | `src/Plugins/PluginManager.cs:505-514`、`:518-526`、`:527-534`（lease/`TryCommit` 包装） | **等价** | 两侧均 ✅ 已修 #235 |
| 声明式插件执行器整体 | `PluginExecutor.cs`（旧式步骤执行，`ParseAndExtract` + `ProcessOutputs`） | `PluginExecutor.cs` 重写至 1597 行 + **新增** `PluginHostPolicy.cs`（152 行，host/authority 解析与 SSRF 闸门）、`PluginMonitorSyncService.cs`、`PluginRuntimeSnapshot.cs`、`PluginPublicationLease.cs`；`PluginProcessor.cs` +845 行 | fork 全面重写并加沙箱化（`declaredHosts` / `maxResponseBytes` / `nativeToken` / 执行总时长上限） | fork 的沙箱闸门**不阻断** timor.tech（host 来自模板声明），但**会阻断**未在模板声明的备用源 |
| IP / 网卡刷新 | `NetworkManager.cs:35,45-51,88-105,109,190-196`（墙钟 30s 窗口） | `NetworkManager.cs:48-52,85-90,256-273,278,359-361`（**单调时钟** 30s 窗口）+ **`InfoService.cs:71-75` 网络变更即刷新 UI** | **fork 更强**：补齐显示层刷新与时钟回拨两个缺口 | 两侧均 ✅ 已修 #287；fork 更稳 |
| 网卡选择 | `UI/Settings/SystemHardwarPage.cs:116,170-171` + `Core/Settings.cs:32-33` | `src/UI/Settings/MainPanelPage.cs`（web 侧）；网络侧同构 | 均有设置界面下拉；**双方均无右键菜单入口** | ❌ #96 子项 (b') 未实现 |

---

## 9. 共性发现

1. **上游在 `3b91b5c`（2026-10-21）做了一轮"协议边界 + 网络边界"审查**，一次性覆盖了 #589 的头名大小写、重复头拒绝、Base64 形态校验、Base64 形态、请求头读取 5s 截止、以及 APIPA IP 过滤。这些修复**没有同步关闭对应 issue**，导致 6 个单里有 3 个（#589、#235、#287）在代码层面已解决却仍是 OPEN —— issue 状态与代码状态脱节。
2. **fork 不是上游的"超集"，而是一次有取舍的重写**。三处能力**净减少**：
   - `continue_on_error` 语义被移除（`PluginModels.cs` 无该字段）→ 直接导致 #611 回归；
   - `native://newyear` + `NewYearNative.cs` 被删除 → 同上；
   - `IsWebSocketUpgrade` 从"解析头"退回"正则" → 在 #589 上游已消除的脆弱点重新出现。
3. **fork 的 WebServer 引入了上游没有的访问边界模型（Host/Origin 白名单 + 绑定模式 + 票据认证 + 一次性票据会话）**。这套模型对"防 DNS Rebinding / 防未授权暴露"是正确方向，但**默认配置与反向代理场景不兼容**，且配套的 `WebServerTrustProxyHeaders` 开关**从未接线**，用户无法通过配置自救。
4. **两侧共享的未修问题**：
   - `NetworkManager` 的 `lastValidMap` 回落（上游 `:316-317` / fork `:466,485`）会让"枚举中但已静默"的网卡**冻结在旧值**（#96 的"卡住"最可能的机制）。
   - 右键菜单网卡选择（#96 明确诉求）**两边都没做**。
   - `NetworkChange` 只订阅 `NetworkAddressChanged`，未订阅 `NetworkAvailabilityChanged`；且订阅从不退订。
   - `Sec-WebSocket-Version` / `Connection: Upgrade` 在两侧都**未被校验**（仅 fork 校验了 `Sec-WebSocket-Key`）。
5. **"停止采集" vs "有值未刷新" 的区分口径**（#96 关键）：
   - **停止采集**：`NetworkManager.ProcessUpdate` 的 `isTarget` 为 false ⇒ `hw.Update()` 不再被调用（上游 `:70-74`、fork 同构）。
   - **有值但未刷新显示**：采集在跑、传感器值为 0/NaN，但 `ReadNetworkSensor` 回落到 `lastValidMap` 的旧值 ⇒ 界面冻结在旧数字。
   - 二者的外部观感相同（数字不动），但修复位置完全不同。
6. **上游 `Handshake` 失败时静默不回字节**（`:389-421`），fork 返回明确的 400/403/401。**fork 在可诊断性上优于上游** —— 这是 fork 在 WebServer 方向少数明确的正向差异。

---

## 10. 未能验证及原因

| 项 | 未验证内容 | 原因 | 补做建议 |
|---|---|---|---|
| #580 端到端 | 真实 Cloudflare Tunnel / nginx 下的完整链路 | 本环境无隧道、无可对外的实例；且按约束不对外部生产服务发请求 | 在隔离网络里跑 `cloudflared tunnel --url http://127.0.0.1:5000`，用 `curl -H "Host: <公网名>"` 与浏览器 Network 面板取证 |
| #580 issue 截图 | "显示异常"的具体形态（灰屏/数值错/布局错） | 截图在 GitHub user-attachments 上，需登录态，未取回 | 由提交者补充文字复现步骤 |
| #96 "卡住" | 真实网卡切换后 NET.Up/NET.Down 的冻结复现 | 本机无第二块可切换的物理网卡 | 按 §5.5 的四步法在有双网卡/可禁用的机器上复现，并对比 `hw.Update()` 是否执行、`target.Value` 原值、`lastValidMap` 回落值 |
| #287 端到端 | 真实 Wi-Fi 切换后的 IP 更新时间线 | 同上，无可切换链路 | 用 `netsh wlan` 切 SSID + 同一网卡的另一网段，配合 Debug 日志看 `MatchNativeNetworkAdapter` 是否换了新 `NetworkInterface` 实例 |
| #235 端到端 | 断网 → 恢复后插件刷新时延与退避序列 | 需要可控代理/断网环境 + 插件运行时长 | 用 `ProxyLatency` 的 `proxy_server` 指向不可达端口注入失败，统计退避序列 |
| #611 端到端 | mock 返回 `null` 后的真实显示 | 需要运行完整产品 + 改 hosts；本次未构建产品 | 见 §4.5 的 mock 脚本（需在本地实验副本上改 URL，勿提交） |
| fork #611 归因 | `http://timor.tech` 是否真会失败 | 未对该外部 API 发请求（约束禁止） | 在允许联网的隔离环境里 `curl.exe -i http://timor.tech/api/holiday/next` 与 https 版对比 |
| fork 沙箱闸门 | `PluginHostPolicy` / 声明式执行器对**其它**内置插件模板的全量影响 | 本任务单范围限于 6 个 issue，未做全插件回归 | 另开一轮：逐个内置模板在 fork 上做一次沙箱放行核对 |
| 产品二进制 | 未能运行真实 LiteMonitor 做协议级取证 | 仓库无构建产物；全量构建超出本任务单"不跑长时间构建"的约束 | 验证程序采用**源码逐字抽取**替代（已在 §2.5 说明）；如需端到端请单独安排一次构建 |

---

## 11. 建议优先级

| 优先级 | 事项 | 归属 | 依据 |
|---|---|---|---|
| **P0** | 回补 `continue_on_error` 语义（`PluginModels.cs` 字段 + `PluginExecutor.cs` "仅成功才合并变量"实现） | fork | #611 有 2 人复现，fork 现状直接输出 ❌；这是**能力级缺失**，影响所有需要"主源失败→备用源"的模板 |
| **P0** | 回补 `native://newyear` handler 与 `src/Plugins/Native/NewYearNative.cs`（并接入 fork 沙箱闸门） | fork | #611 的用户诉求原文 |
| **P0** | `resources/plugins/Holiday.json` 回补 v1.1（https + `continue_on_error` + `fallback_newyear`） | fork | 同上；注意 https，当前 fork 是明文 http |
| **P1** | `IsWebSocketUpgrade` 从正则改为解析头 + 值 `Trim` + `OrdinalIgnoreCase` | fork | §2.5 CASE B 实测 2 处 FAIL；上游已有正确实现可直接对齐 |
| **P1** | 让 `WebServerTrustProxyHeaders` 真正接线（信任 `X-Forwarded-Host` / `Forwarded` 时放宽 Host 与端口判定） | fork | 当前是死设置，用户无法自救；#580 在 fork 上必然 403 |
| **P1** | 关闭 #589 / #235 / #287（代码已修复），并在 issue 内标注修复 commit | 上游 | issue 状态与代码状态脱节，会持续污染统计 |
| **P2** | `NetworkManager` 存活检查增加"连续零流量 ⇒ 重选 NIC"，或让 `lastValidMap` 回落带新鲜度校验 | 上游 + fork | #96 "卡住"的最可能机制；两侧同构未修 |
| **P2** | 托盘右键菜单加网卡选择二级菜单 | 上游 + fork | #96 明确诉求，双方均未实现 |
| **P2** | 补 `NetworkChange.NetworkAvailabilityChanged` 订阅；退订不再使用的静态事件 | 上游 + fork | 网络 down→up 但地址未变的场景；订阅泄漏 |
| **P2** | 上游补 `InfoService` 的网络变更即时刷新 + 单调时钟 IP 缓存窗 | 上游 | 对齐 fork 已有实现（`InfoService.cs:71-75`、`NetworkManager.cs:240-251`） |
| **P3** | 校验 `Sec-WebSocket-Version`（13）与 `Connection: Upgrade`；校验握手失败时回一条可诊断的 400 而非静默断开 | 上游（fork 已有 400） | RFC 6455 合规性 |
| **P3** | fork 全量内置插件模板在沙箱闸门下的放行核对 | fork | 本次仅核对了 Holiday / Weather / ProxyLatency 三个模板 |

---

## 12. 附录：验证程序说明

- 位置：`.tmp/verification/Harness.csproj` + `.tmp/verification/Program.cs`（**任务结束后已删除**）。
- 性质：把上游与 fork 的解析函数**逐字抽取**进独立 C# 程序（`dotnet run`，net8.0，`EnableDefaultCompileItems=false` 以避免把产品树 glob 进来）。**未启动任何产品二进制**，因此结论限定为"算法级"验证，不等同于端到端复现。
- 抽取的函数与来源：
  - 上游：`WebSessionManager.cs:117-122`（`IsWebSocketUpgrade`）、`:125-149`（`TryGetHeaderValue`）、`:400-401`（key 形态校验）。
  - fork：`WebSessionManager.cs:1323-1327`（`IsWebSocketUpgrade`）、`:2616-2662`（`TryGetWebSocketKey`）、`:2672-2678`（`IsWebSocketKeyShapeValid`）、`:1972-2008`（`IsHostAllowed` / `IsOriginAllowed`）、`:2011-2054`（`SplitHostPort` / `NormalizeHost`）、`WebServerAccessPolicy.cs:25-34`（`IsLanAccessForbidden`）。
- 结果：**32 条断言，30 条符合预期，2 条 FAIL 均为 fork 侧 CASE B 的偏差检出**（`Upgrade:websocket` / `Upgrade:  websocket` 被 fork 的正则拒绝）。
- 未改动的文件：全程只读；`src/**`、设置与用户数据均未触碰；工作树既有未提交改动（`src/Plugins/ThemeMarket.cs`、`src/ThemeEditor/GpuThemePreviewControl.cs`、`src/UI/Settings/ThemePreviewService.cs`、`docs/superpowers/plans/2026-10-25-theme-marketplace-plan.md` 等）未被触碰。
