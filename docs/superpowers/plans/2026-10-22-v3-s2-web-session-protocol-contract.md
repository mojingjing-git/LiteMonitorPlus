# S2.0 契约：Web 主会话、短期票据与访问边界

日期：2026-10-22。状态：**已冻结**。适用步骤：S2.1（LM-004 DOM 注入）、S2.2（SEC-001 / SEC-002 / LM-005 / LM-006 / LM-023）。

本文件是实现前必须遵守的契约，由编排方冻结。两个实施 worker 不得各自发明语义；若证明某条不可实现，**先停下报告**，不得自行改契约后继续。

---

## 0. 现状（已核实，行号对应 `9ffb4219`）

### 0.1 鉴权模型：没有会话，只有"每请求带明文口令"

```csharp
// src/System/WebServer/WebSessionManager.cs
:841  private bool CheckAuth(string requestHeader)
:843      var pwd = Settings.Load().WebServerPassword;
:844      if (string.IsNullOrEmpty(pwd)) return true;      // ★ 空密码 = 完全不鉴权
:850      var match = PwdQueryRegex.Match(firstLine);      // 只看请求行第一行
:858      if (match.NextMatch().Success) return false;     // 拒绝重复 pwd 参数
:863      supplied = Uri.UnescapeDataString(match.Groups[1].Value);
:875      return FixedTimeEqualsUtf8(supplied, pwd);
```

- 口令来自 `Settings.WebServerPassword`（`src/Core/Settings.cs:170`，默认**空串**），是**明文**。
- 口令放在**请求行查询串**里（`?pwd=` 或 `&pwd=`）。
- **不存在**会话、票据、注销、过期、CSRF 这些概念。
- 空密码 ⇒ `CheckAuth` 直接返回 true ⇒ 完全开放。

### 0.2 前端把密码长期存进 localStorage

```javascript
// src/System/WebServer/WebPageContent.cs
:303  let pwd = localStorage.getItem('ws_pwd') || '';
:335  await fetch('/api/snapshot?pwd=' + encodeURIComponent(newPwd));
:351  localStorage.setItem('ws_pwd', pwd);
:362  const qs = pwd ? '?pwd=' + encodeURIComponent(pwd) : '';
:363  ws = new WebSocket(protocol + location.host + qs);
:393  fetch('/api/snapshot?pwd=' + encodeURIComponent(pwd))
```

### 0.3 监听与跨源

```csharp
// src/System/WebServer/LiteWebServer.cs
:52   internal IPAddress? ListenAddress { get; set; }      // 测试接缝；null = 生产
:190  listener = new TcpListener(IPAddress.IPv6Any, port); // ★ 全接口
:197  listener = new TcpListener(IPAddress.Any, port);     // ★ 回退也是全接口
:66   internal int MaxConcurrentConnections { get; set; } = 64;

// src/System/WebServer/WebSessionManager.cs
:894  "Access-Control-Allow-Origin: *\r\n"               // ★ 所有响应都带
:353  DefaultHeaderReadTimeout = TimeSpan.FromSeconds(5)  // 请求头期限
:372  private const int StreamWriteTimeoutMs = 5000;      // 写超时
```

### 0.4 已有可复用的好设计（**必须保留**）

- `CheckAuth` 的 **URL 解码后再比较**（`Uri.UnescapeDataString`，避免 `+` 语义错配）与 **恒时比较**（`FixedTimeEqualsUtf8`）。
- **重复参数一律拒绝**（`:858`），不猜取第一个还是最后一个。
- 请求头读取期限用**取消令牌**而非 `NetworkStream.ReadTimeout`（`:343-362` 注释已说明后者对 `ReadAsync` 无效）。
- 写超时 5 秒，防御"客户端建连不读"（`:372`）。
- 64 连接上限作为**背压**（`:66`）。
- `WebDemandState` 用可注入 `TimeProvider`（`:53`），便于确定性测试——新会话/票据的 TTL 测试应沿用该模式。

### 0.5 HTTP 与 WebSocket 的鉴权已统一

`:747`（HTTP 分支）与 `:1235`（WS 握手）都调用 `CheckAuth`。**这项已满足，本轮只需回归保护，不得重写。**

---

## 1. 冻结的契约

### W1 · 三层认证模型

| 层 | 适用 | 凭据形态 | 载体 |
|---|---|---|---|
| L0 无密码 | `WebServerPassword` 为空 | 无 | —— |
| L1 主会话 | HTTP API | 不透明随机会话 ID | Cookie |
| L2 一次性票据 | WebSocket 握手 | 不透明随机票据 | 查询串（**仅此一次**） |

**L0 只允许本机模式。** 局域网模式下 `WebServerPassword` 为空 ⇒ **拒绝启动**并给出明确错误提示（不得"先跑起来再说"）。

### W2 · 主会话

- **会话 ID**：32 字节 CSPRNG → base64url（无填充）。**不得**由口令派生、不得可预测。
- **服务端存储**：内存会话表 `{ id → 会话记录 }`。会话记录至少含：创建时刻、最后使用时刻、口令指纹、撤销标记。
- **传输**：`Set-Cookie: lmsid=<id>; HttpOnly; SameSite=Strict; Path=/; Max-Age=<绝对TTL>`。
  - `HttpOnly`：**必须**（前端 JS 读不到）。
  - `SameSite=Strict`：**必须**（这是第一道 CSRF 防线）。
  - `Secure`：**仅在 TLS 下**添加；本机 HTTP 模式下不得添加（否则浏览器不回传，功能直接坏掉）。
- **TTL**：绝对 **30 分钟** + 空闲 **2 分钟**。两者取先到者过期。空闲 TTL 参照既有 HTTP 需求租期（2 秒）的思路，但会话需覆盖人工查看场景，故取 2 分钟并**明确写出**。
- **注销**：显式端点 + 服务重启即全部失效（内存态，天然如此）。
- **改密**：口令变化 ⇒ **全部**会话与票据立即撤销。
- **Cookie 解析**：必须拒绝重复的 `lmsid` 参数（与既有 `pwd` 的重复参数拒绝同一原则）。

### W3 · 短期票据

- **申请**：已持有主会话的请求 → `POST /api/auth/ticket` → 返回票据。
- **形态**：32 字节 CSPRNG → base64url。
- **TTL**：**30 秒**。
- **一次性**：握手成功后立即作废；**过期或已用即拒**。
- **用途**：仅用于 WebSocket 握手的 `?ticket=`。**不得**用于任何 HTTP API。
- **不得**写入任何日志；服务端访问日志必须对其**脱敏**（与口令同级）。

> 浏览器 WebSocket API 无法设置自定义 Authorization 头，这是采用票据的直接原因。票据短 TTL + 一次性 + 仅限 WS 三者叠加，使其即使出现在 URL 中也不可长期复用。

### W4 · CSRF 防护

- 第一道：`SameSite=Strict`（跨站请求不带 Cookie）。
- 第二道：所有**改变状态**的端点（申请票据、注销、改密）必须校验 `X-LiteMonitor-CSRF` 头，其值 = 由会话 ID 派生的令牌（HMAC，密钥为会话 ID 本身，服务端按同一算法重算比对）。
- 第三道：`Origin` 校验（见 W5）。
- **只读的 `/api/snapshot` 不要求 CSRF 头**（它无副作用）。

### W5 · Host 与 Origin 校验

- **Host 头**必须属于允许集合：
  - 本机模式：`127.0.0.1[:port]`、`localhost[:port]`、`[::1][:port]`；
  - 局域网模式：本机集合 + 用户配置的局域网地址。
  - 不匹配 ⇒ **拒绝**（防 DNS Rebinding：恶意站点把受害者的 DNS 指向本机，用 `Host: attacker.com` 访问）。
- **Origin**：
  - **缺失**（非浏览器客户端，如 curl / 脚本）⇒ **允许**，但 Host 校验必须通过；
  - 值为 `null` ⇒ **拒绝**；
  - 不在允许集合 ⇒ **拒绝**；
  - 允许集合：本机模式的 `http://127.0.0.1[:port]`、`http://localhost[:port]`（与 Host 集合一致）；局域网模式额外含配置的局域网源。
- **可信代理头**：**默认不信任** `X-Forwarded-For` / `X-Real-IP` / `Forwarded`。只有在用户显式配置了可信代理地址时才采信，且必须在文档与代码注释中写明该配置是"信任任意来源的转发头"的降级开关。

### W6 · CORS

- **移除** `Access-Control-Allow-Origin: *`。
- 默认**不返回任何 CORS 响应头**（同源访问不需要）。
- 若用户显式配置了允许跨源的列表：只回显**匹配的**具体源（不得 `*`），且带凭据时必须同时给 `Access-Control-Allow-Credentials: true` 且不得用 `*`。
- **预检（OPTIONS）**必须走同一套 Host/Origin 校验。

### W7 · 新增端点的请求体规则

- 读请求体**之前**必须检查 `Content-Length`；超过上限（**4 KB**）⇒ 直接拒绝，不读。
- 没有 `Content-Length`（chunked）⇒ 拒绝（不允许流式上传）。
- 读取期限**复用**既有的 5 秒请求头期限（不是新值）。
- **重复字段** ⇒ 拒绝。
- `Content-Type` 必须是 `application/json`；`charset` 必须是 UTF-8。其他编码 ⇒ 拒绝并说明。
- 解析失败 ⇒ 400，且**不得**把异常细节原样回给客户端。

### W8 · 既有防护必须保留并扩展到新端点

- 5 秒请求头期限（`WebSessionManager.cs:353`）—— 新增端点同样适用。
- 64 连接上限（`LiteWebServer.cs:66`）—— 保持。
- 写超时 5 秒（`WebSessionManager.cs:372`）—— 保持。
- **处理顺序**：必须**先读完请求头、完成鉴权，再决定是否读取请求体**。未鉴权的请求**不得**让服务器去读 body（避免被用作带宽/内存消耗放大器）。

### W9 · 凭据存储与迁移

必须区分两类凭据：

| 类别 | 例子 | 要求 |
|---|---|---|
| **只需验证** | Web 访问密码 | 落盘应存**哈希 + 盐**（PBKDF2 或等价），不存明文 |
| **必须恢复原文** | 插件 API Key | 需要原文才能发请求 ⇒ 必须加密存储（DPAPI） |

- **Web 访问密码**：新增 `WebServerPasswordHash` + `WebServerPasswordSalt`。加载时若哈希为空而明文非空 ⇒ **惰性迁移**：首次需要验证时计算哈希并落盘，随后**清空落盘的明文字段**。
  - **存量兼容**：老 `settings.json` 只有明文 ⇒ 首次启动自动迁移，用户无感；**迁移失败不得静默变成"空密码"**。
  - 迁移后**明文不得**再写回 `settings.json`。
- **插件密钥**：DPAPI **`CurrentUser`** 作用域（用户隔离）。**不得**用 `LocalMachine`（任何本机用户都能解）。
  - 跨用户 / 迁移到其他机器 / 解密失败 ⇒ **明确报错**，**禁止**静默变成空值、禁止因此扩大监听范围、禁止删除原凭据。
- **旧备份**：`settings.json.bak` 同样会含明文。迁移时必须**一并处理备份**（重新生成或删除），并在报告中说明策略。
- **禁止**：为了"兼容"而保留明文口令在 `settings.json` 里。

### W10 · 前端配套（属 S2.1 与 S2.2 的交界）

- 升级/登出流程必须**主动删除**旧的 `localStorage['ws_pwd']`（避免历史口令长期残留）。
- 前端**不再**把口令放进任何 URL。
- 前端在拿到主会话后，WS 握手前先 `POST /api/auth/ticket` 取一次性票据。
- **密码修改必须撤销既有会话、票据与 WebSocket 连接**（服务端执行；前端收到响应后应断开并重新登录）。
- 口令**不得**写入 `localStorage`、`sessionStorage`、Cookie（可读的）或任何日志。

### W11 · TLS 与明文传输的明确条款

- 局域网模式下的 HTTP 与 WebSocket 都是**明文传输**。
- 因此：**要么**由用户自行部署反向代理提供 TLS，**要么**在设置界面明确展示"当前为明文传输，局域网内可被嗅探"的告警。**不得**只写"受保护端点"这类含糊表述。
- 前端**不得**在非 TLS 连接下声称连接是安全的。
- 设置界面必须**显示实际监听地址与端口**。

### W12 · 配置兼容与迁移（存量用户）

- 现状默认：`WebServerEnabled = false`（`Settings.cs:168`）、`WebServerPort = 5000`（`:169`）、`WebServerPassword = ""`（`:170`）。**"Web 默认关闭"的行为保持不变。**
- **存量配置**：`WebServerEnabled = true` 且口令为空 ⇒ 切到"默认仅环回"后远程访问会失效。必须定义并实现明确处置，**不得静默变更监听范围**：
  - 建议方案：新增 `WebServerBindMode`（枚举：`Loopback` / `Lan`）。
    - **新安装**默认 `Loopback`。
    - **存量配置**（`WebServerEnabled=true` 且此前版本没有该字段）默认保持**旧行为**（等价 `Lan`），并由 UI 提示用户收敛到环回。
    - `Lan` 模式下口令为空 ⇒ **拒绝启动**并给出明确提示。
  - 若实现方选择不同方案，须在报告中写明并说明存量用户的影响，**不得**让存量用户"升级后服务莫名其妙连不上"且没有解释。
- 迁移必须是**一次性、可回退**的：不得删除用户原有配置项。

### W13 · 禁止事项

- 禁止保留 `Access-Control-Allow-Origin: *`。
- 禁止把口令写进任何 URL、任何长期存储、任何日志。
- 禁止用"测试方便"为由放宽 Host/Origin 校验。
- 禁止把会话/票据做成可预测（时间戳、自增、用户名派生）。
- 禁止在 `CheckAuth` 之外新增第二套鉴权口径（HTTP 与 WS 必须一致）。
- 禁止把 DOM 修复写成"用正则过滤危险字符"——必须按文本节点处理（W14）。

### W13.5 · 端点与报文形态（**冻结，前端与后端据此并行实现**）

> 本节存在的唯一目的：让 **S2.1（前端，`WebPageContent.cs`）** 与 **S2.2（后端，`WebSessionManager.cs` 等）** 能**并行**开发而无需互相等待。端点路径、请求体形状、响应头名**全部在此固定，不得各自另立**。

| 方法与路径 | 请求 | 成功响应 | 失败响应 |
|---|---|---|---|
| `POST /api/auth/login` | `Content-Type: application/json`，体 `{"password":"<明文口令>"}` | `200`，`Set-Cookie: lmsid=<id>; HttpOnly; SameSite=Strict; Path=/; Max-Age=1800` | `401`（口令错误），`400`（体不合规），`403`（Host/Origin 不通过） |
| `POST /api/auth/ticket` | 需要 `lmsid` Cookie + 头 `X-LiteMonitor-CSRF: <令牌>` | `200`，体 `{"ticket":"<base64url>"}` | `401`（无有效会话），`403`（CSRF 不符），`400` |
| `POST /api/auth/logout` | 需要 `lmsid` Cookie + 头 `X-LiteMonitor-CSRF: <令牌>` | `204`，并下发 `Set-Cookie: lmsid=; Max-Age=0` 清除 Cookie | `401`，`403` |
| `GET /api/snapshot` | 需要 `lmsid` Cookie（**不再接受 `?pwd=`**） | `200` + 快照 JSON（既有格式不变） | `401`（无有效会话），`403`（Host/Origin） |
| `GET /ws?ticket=<票据>` | WebSocket 升级 | `101` | `401`（票据无效/过期/已用），`403`（Host/Origin） |

- **CSRF 令牌的派生**：HMAC-SHA256，密钥 = 会话 ID，消息 = 固定串 `"liteMonitor-csrf-v1"`，输出 base64url。服务端与前端用同一算法各自计算（前端需要能算——见下）。
  - 前端**读不到** `HttpOnly` 的 Cookie，因此 CSRF 令牌**必须**由登录响应一并下发：`POST /api/auth/login` 的 `200` 响应体改为 `{"csrf":"<令牌>"}`，前端保存在**内存变量**中（**不得**写 localStorage/sessionStorage）。
- **旧 `?pwd=` 路径**：`GET /api/snapshot?pwd=...` 与 `GET /ws?pwd=...` **一律移除**，不再被接受（W13 禁止口令进 URL）。前端与后端在同一次发布中切换，不留兼容期。
- **`GET /api/snapshot` 的响应体格式与既有实现保持一致**，本轮**不改**快照数据结构。

### W14 · DOM 修复（S2.1）与本契约的交界
- S2.1 负责 DOM 文本化；S2.2 负责会话与边界。
- **交界点**：前端从"URL 带口令"改为"Cookie + 票据"后，`WebPageContent.cs` 中**两处**都要改（`:335/:362/:393` 的取数与 WS 连接，以及 `:303/:351` 的 localStorage）。这两个文件不同，但**前端协议**由本契约统一。实施顺序：**S2.2 先定服务端协议，S2.1 后接前端**，或两者按契约并行但**前端请求形态必须与本契约一致**。

---

## 2. 验收矩阵

### 2.1 访问边界（SEC-001）

| # | 断言 |
|---|---|
| N1 | 本机模式下，服务**不**监听非环回地址（用 `Get-NetTCPConnection` 或 `netstat` 核对实际监听地址） |
| N2 | 局域网模式下，配置了非空口令才能启动；口令为空 ⇒ **启动失败并有明确错误** |
| N3 | `Host` 头不匹配允许集合 ⇒ 请求被拒（返回 403） |
| N4 | `Origin: null` ⇒ 被拒 |
| N5 | `Origin` 缺失（无 Origin 头）⇒ 允许，但 Host 校验仍生效 |
| N6 | 跨源页面（不同 Origin）⇒ **读不到**监控数据 |
| N7 | 响应中**不再**出现 `Access-Control-Allow-Origin: *` |
| N8 | `Stop`/`Start` 之后旧会话失效、新会话可用，无重复广播 |

### 2.2 会话与票据（LM-005 / LM-006）

| # | 断言 |
|---|---|
| A1 | 正确口令可换取主会话；响应含 `Set-Cookie` 且带 `HttpOnly` 与 `SameSite=Strict` |
| A2 | 错误口令 ⇒ 401，且**不**签发任何 Cookie |
| A3 | 票据 TTL 过期后使用 ⇒ 被拒 |
| A4 | 票据**用过即废**（同一票据第二次使用 ⇒ 被拒） |
| A5 | 票据只能用于 WS 握手；用于 HTTP API ⇒ 被拒 |
| A6 | 改密后，既有主会话与票据**全部失效** |
| A7 | 显式注销后，既有 Cookie 立即失效 |
| A8 | 会话空闲超过空闲 TTL ⇒ 失效 |
| A9 | HTTP 与 WS 的鉴权判断一致（同一凭据在两条通道上的结果相同） |
| A10 | 重复 `lmsid` Cookie ⇒ 被拒 |

### 2.3 CSRF 与请求体（W4 / W7）

| # | 断言 |
|---|---|
| F1 | 改变状态的端点缺少 `X-LiteMonitor-CSRF` ⇒ 被拒 |
| F2 | CSRF 头值错误 ⇒ 被拒 |
| F3 | 请求体超过 4 KB ⇒ 被拒且**不**读完整个 body |
| F4 | 重复 JSON 字段 ⇒ 被拒 |
| F5 | 非 `application/json` 的 `Content-Type` ⇒ 被拒 |
| F6 | 未鉴权请求**不会**触发请求体读取 |

### 2.4 凭据存储与迁移（W9 / W12）

| # | 断言 |
|---|---|
| P1 | 迁移后 `settings.json` 中**不再有**明文口令（只有哈希 + 盐） |
| P2 | 迁移后 `settings.json.bak` 中**不再有**明文口令 |
| P3 | 解密失败（模拟换机器/换用户）⇒ **明确报错**，且监听范围**未**因此扩大、原凭据**未**被删除 |
| P4 | 存量配置（`WebServerEnabled=true` 且无新字段）升级后行为符合 W12 的约定，且有可回退路径 |
| P5 | 迁移前后，正确的旧口令**仍然可用** |

### 2.5 DOM（LM-004，S2.1）

| # | 断言 |
|---|---|
| D1 | 分组名/监控名中的 `<`、`"`、`'`、`&`、`<script>`、`onerror=` 等载荷**只能作为文本显示** |
| D2 | 载荷**不能**改变 DOM 结构（节点数、层级不变） |
| D3 | 载荷**不能**触发脚本执行 |
| D4 | 动态分组标识参与 HTML 属性与 CSS 选择器时，非法字符**不能**逃逸出属性/选择器上下文 |
| D5 | 普通卡片、大小卡片、DASH、首次创建、持续刷新**五种场景**都覆盖 |
| D6 | HTTP 与 WebSocket **两条数据通道**都覆盖 |
| D7 | 正常数据的**布局与排序不变**（回归网：`tests/LiteMonitor.Tests/WebThemeCssTests.cs`） |

---

## 3. 交付要求

S2.1 与 S2.2 **必须一起通过**才构成 M2 门禁通过（任一 `NOT_VERIFIED` ⇒ M2 为 `CONDITIONAL`，不得进入发布准备）。

每个步骤交付时必须给出：

1. **契约符合性说明**：逐条对照 W1–W14，说明落在哪个文件哪一行；未满足项给理由。
2. **验收矩阵覆盖表**：§2 每个编号 → 测试方法名 → 该测试**实际执行**的证据（用例计数与结果）。空跑或跳过的条目必须显式标注，**不得计入通过**。
3. **全量回归**：`dotnet test LiteMonitor.sln -c Debug` 的逐程序集计数，**不得低于 1038 / 13 / 19**（当前基线）。
4. **提交**：每个缺陷 ID 一次提交，提交信息含缺陷 ID。

---

## 4. 冻结记录

- 本契约由编排方冻结。S2.1 / S2.2 实施者不得单方面变更语义。
- W12 的存量兼容方案允许实施方在**满足约束**的前提下自选，但必须写明并说明对存量用户的影响；W1–W11、W13、W14 为硬约束。
- 若实施中证明某条不可实现，**先停下报告**，由编排方裁决。
