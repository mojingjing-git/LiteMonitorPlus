# Web 主会话、短期票据与访问边界

本文描述 LiteMonitor++ 网页服务的会话模型、凭据传递载体、访问边界判定与失败语义。它回答四个问题：**谁被允许连上来**（Host / Origin / 外部 authority 白名单）、**连上来之后拿什么证明自己**（主会话 Cookie、短期票据、CSRF 令牌）、**边界收紧后既有连接如何处置**（策略代次）、**各条失败路径回什么状态码**。

适用范围是 `src/System/WebServer`（`LiteWebServer`、`WebSessionManager`、`WebServerAccessPolicy`、`WebPageContent`）、`src/Core/Settings.cs` 中网页服务相关配置项，以及 `src/Core/Actions/AppActions.cs` 的网页服务设置应用流程。硬件采集与需求联动、界面渲染路线、插件市场与主题工作室不在本文范围。

---

## 1. 目标与设计前提

### 1.1 目标

- **对外暴露必须带凭据。** 对局域网暴露、对反向代理暴露，两种情形都必须配置访问口令；无有效口令 ⇒ 拒绝监听，绝不"先跑起来再说"。
- **凭据不得进入 URL。** 访问口令只出现在登录请求的请求体里；会话凭据只出现在 Cookie 头里；CSRF 令牌只出现在请求头里。
- **撤销必须彻底。** 注销、改密、口令被清空、访问边界收紧、服务停止，任何一条都必须让既有的主会话、票据与已建立的 WebSocket 连接同时失效。
- **判定口径唯一。** HTTP 路由与 WebSocket 握手共用同一份请求解析结果、同一份访问边界快照、同一套会话有效性判定。

### 1.2 三类访问面

| 访问面 | 监听 | 主机名来源 | 口令要求 |
|---|---|---|---|
| 本机 | 仅 `127.0.0.1`（`WebServerBindMode.Loopback`） | `Host` 头，须是 `127.0.0.1` / `localhost` / `::1` | 不强制，但会话机制照常生效 |
| 局域网 | 全部网卡（`WebServerBindMode.Lan`） | `Host` 头，额外允许 `WebServerLanAddresses` 中配置的地址 | **必须**有有效口令 |
| 外部（反向代理） | 由反向代理终止 TLS 并转发到本机监听端口 | 可信反向代理给出的 `X-Forwarded-Host`，否则 `Host` 头 | **必须**有有效口令，且主机名须命中 `WebServerAllowedHosts` |

外部暴露在实现上与局域网共用同一套凭据口径：反向代理即使从环回地址连进来，也按对外暴露处理，不继承"环回即可信"的信任。

### 1.3 组件与职责

| 组件 | 位置 | 职责 | 明确不负责 |
|---|---|---|---|
| `LiteWebServer` | `src/System/WebServer/LiteWebServer.cs` | 监听套接字、在途连接上限、冷启动守卫、快照序列化、需求同步 | 不做会话与边界判定 |
| `WebSessionManager` | `src/System/WebServer/WebSessionManager.cs` | 请求解析、会话与票据生命周期、Host/Origin 判定、HTTP 路由、WebSocket 握手与广播 | 不解释配置语义，只消费访问边界快照 |
| `WebServerAccessPolicy` | `src/System/WebServer/WebServerAccessPolicy.cs` | 访问边界快照与指纹、authority 解析与匹配、可信反向代理判定、冷启动守卫判定 | 不持有任何会话状态 |
| `WebPageContent` | `src/System/WebServer/WebPageContent.cs` | 内嵌前端：登录、票据、注销、快照轮询、WebSocket 订阅、DOM 渲染 | 不做安全判定 |
| `Settings` | `src/Core/Settings.cs` | 网页服务配置项与口令哈希 / 盐 | 不做运行时判定 |
| `AppActions.ApplyWebServerWithResult` | `src/Core/Actions/AppActions.cs` | 运行中应用网页服务设置；决定停止 / 重启 / 拒绝 | 不实现判定细节，只调用 `WebServerAccessPolicy` |

---

## 2. 凭据分层与传递载体

### 2.1 三层模型

| 层 | 持有者 | 形态 | 有效期 | 用途 |
|---|---|---|---|---|
| 主会话 | 浏览器（`lmsid` Cookie，HttpOnly） | 32 字节 CSPRNG → base64url（无填充） | 绝对 30 分钟 / 空闲 2 分钟，取先到者 | HTTP API 的身份凭据；短期票据的申请凭据 |
| 短期票据 | 调用方（内存） | 32 字节 CSPRNG → base64url（无填充） | 30 秒 | **仅**用于一次 WebSocket 握手 |
| CSRF 令牌 | 前端内存变量 | `HMAC-SHA256(key = 会话 ID, msg = "liteMonitor-csrf-v1")` → base64url | 随主会话 | 改变状态的端点的二次确认 |

**主会话 ID 与票据 ID 都不得由口令派生、不得可预测**：二者都取自 `RandomNumberGenerator.GetBytes(32)`。口令只以**指纹**形式参与比对（`Settings.GetWebPasswordFingerprint`），指纹本身不参与 ID 生成。

CSRF 令牌的密钥就是会话 ID：拿到会话 ID 才能算出令牌，而会话 ID 存放在 HttpOnly Cookie 中、前端脚本读不到，因此令牌必须由登录响应一并下发。服务端与前端使用同一算法（`WebSessionManager.ComputeCsrfToken`）。

### 2.2 未配置口令时的会话口径

未配置有效口令（`Settings.HasWebPasswordConfigured()` 为 false）时，访问边界只剩 Host 与 Origin。此时：

- `POST /api/auth/login` 以 `{"password":""}` 提交必须成功，签发主会话与 CSRF 令牌；
- 票据、注销、快照流程与配置了口令时**完全一致**，包括 CSRF 校验；
- HTTP API 在任何模式下都**必须**持有主会话，不存在"无口令 ⇒ 免会话"的第二套鉴权口径。

### 2.3 凭据载体

| 凭据 | 载体 | 备注 |
|---|---|---|
| 访问口令明文 | `POST /api/auth/login` 的请求体 `{"password":"…"}` | 不进 URL、不进 `localStorage` / `sessionStorage`、不进日志 |
| 主会话 ID | `Cookie` 请求头，Cookie 名 `lmsid` | `HttpOnly; SameSite=Strict; Path=/; Max-Age=1800` |
| CSRF 令牌 | 请求头 `X-LiteMonitor-CSRF` | 登录响应体 `{"csrf":"…"}` 下发，前端只存在内存变量里 |
| 短期票据 | 查询串参数 `ticket` | 只出现在 WebSocket 握手 URL 上 |

Cookie 的 `Secure` 属性按**已验证的有效协议**决定：有效 authority 是外部的 `https` ⇒ 加 `Secure`；本机明文 HTTP ⇒ 不加（加了浏览器就不回传，功能直接坏掉）。清除 Cookie 的 `Set-Cookie` 必须与签发时的属性集一致（含 `Path=/` 与 `Secure`），否则浏览器可能不删除旧 Cookie。

### 2.4 查询串里的凭据一律不被接受

**查询串中出现口令参数 `pwd` ⇒ 一律 401，不做兼容、不做转换。** 这条规则在两处独立执行：

- HTTP 路由的第一步，早于有效 authority 的解析判定；
- WebSocket 握手中，在 Host / Origin 校验之后判定。

查询串里唯一被接受的凭据是短期票据 `ticket`：它有 30 秒 TTL、一次性、且只能用于 WebSocket 握手。浏览器 WebSocket API 无法设置自定义请求头，这是票据进入 URL 的直接原因；短 TTL + 一次性 + 仅限 WS 三者叠加，使它即使出现在 URL、Referer 或转发节点日志里，也几乎不可能被复用来建立第二条连接。

### 2.5 歧义一律拒绝，不猜

以下情况一律拒绝，不取"第一个"也不取"最后一个"：

- 重复的 `lmsid` Cookie ⇒ 无有效会话（401）；
- 重复的查询串参数（含重复的 `ticket`）⇒ 拒绝；
- 重复的请求头（`Host`、`Cookie`、`Content-Length`、`X-Forwarded-Proto`、`X-Forwarded-Host`、`Sec-WebSocket-Key`、`Sec-WebSocket-Version`）⇒ 拒绝；
- 请求体里重复的 `password` 字段 ⇒ 400。

单值头必须唯一取值（`HttpRequestInfo.GetSingleHeader`）；列表头（`Connection`、`Upgrade`）允许多行，按 RFC 7230 合并令牌后精确比较。

---

## 3. 主会话

### 3.1 会话记录

`WebSessionRecord` 携带：会话 ID、创建时刻、最后使用时刻、口令指纹、**策略代次**、撤销标记。

### 3.2 有效期

| 维度 | 时长 | 依据 |
|---|---|---|
| 绝对 TTL | 30 分钟（`WebSessionManager.SessionAbsoluteTtl`） | 与 Cookie 的 `Max-Age` 同值，限定被窃取凭据的最长可用时间 |
| 空闲 TTL | 2 分钟（`WebSessionManager.SessionIdleTtl`） | 网页版的正常节奏是"打开页面一直看着"，每秒一次的快照轮询持续刷新空闲计时；"登录完就走"的会话 2 分钟后消失 |

两者取先到者过期。所有 TTL 用**单调时钟刻度**判定（`TimeProvider` 可注入），墙上时间回拨不影响判定结果。

正在接收 WebSocket 推送的连接视为"会话正在被使用"：广播前的会话有效性检查会顺带刷新空闲计时，否则只看网页的客户端会在空闲 TTL 到点被踢下线，而它仍在正常收数据。

### 3.3 撤销路径

会话在下列任一情况下失效：

1. **显式注销**（`POST /api/auth/logout`）；
2. **绝对 TTL 到期**；
3. **空闲 TTL 到期**；
4. **口令指纹不匹配**（改密或口令被清空）⇒ 撤销**全部**会话与票据；
5. **策略代次不匹配**（访问边界已变）⇒ 该会话作废；
6. **服务停止 / 重启** ⇒ 内存态会话表清空。

会话失效**必须**连带断开绑定在它上面的 WebSocket 连接（`CloseSessionWebSockets`），且只断开这一个会话的连接，不影响其它会话。关闭套接字的动作一律在鉴权锁之外执行——关闭连接可能触发回调并重入回收路径。

### 3.4 会话与 WebSocket 连接的绑定

`WsClientBinding` 记录一条已鉴权 WS 连接的三件事：

| 字段 | 含义 | 失效后果 |
|---|---|---|
| `Generation` | 连接的运行代次（服务 Start / Stop 纪元） | 与广播循环代次不符 ⇒ 不作为收件人 |
| `SessionId` | 所属主会话 ID（握手时从票据带出） | 会话失效 ⇒ 连接被断开 |
| `PolicyGeneration` | 登记时的策略代次 | 边界收紧 ⇒ 连接被断开 |

**运行代次与主会话是两个正交的概念**：代次只回答"是不是这一轮服务周期建立的连接"，回答不了"它的会话还在不在"。两者都记录下来，才既能在 Stop → Start 之后拒绝旧广播，又能在会话过期时反查并断开对应连接。

---

## 4. 短期票据

### 4.1 生命周期

`WebTicketRecord` 携带：票据 ID、到期时刻、已使用标记、申请它的主会话 ID、申请时的口令指纹、申请时的策略代次。

- **申请**：`POST /api/auth/ticket`，需要有效主会话 + 有效的 `X-LiteMonitor-CSRF` 头；
- **消费**：WebSocket 握手中以查询串参数 `ticket` 提交，**查到即从表里移除**——同一张票据第二次使用必然查不到；
- **TTL**：30 秒（`WebSessionManager.TicketTtl`）。

### 4.2 拒绝条件

消费失败（401，提示票据无效、已过期或已被使用）的条件：

- 票据不存在或已被使用；
- 已过 30 秒 TTL；
- 口令指纹不匹配（期间改过口令）；
- 策略代次不匹配（期间访问边界变过）；
- 申请它的主会话已经失效（注销 / 过期 / 策略代次变化）。

### 4.3 使用范围

**票据只用于 WebSocket 握手，不得用于任何 HTTP API。** HTTP 路由根本不读取票据参数——用票据调用 `GET /api/snapshot` 会被当作"没有有效主会话"而 401。

---

## 5. 访问边界

### 5.1 边界快照与指纹

`WebServerAccessPolicy.BuildBoundary` 一次性构造当前边界快照 `WebAccessBoundary`：

| 字段 | 内容 | 来源 |
|---|---|---|
| `AllowedHosts` | `127.0.0.1` / `localhost` / `::1`；局域网模式追加 `WebServerLanAddresses` 中配置的地址 | 配置 |
| `AllowedOrigins` | 显式跨源白名单（精确字符串） | `WebServerAllowedOrigins` |
| `ExternalAuthorities` | 外部 authority 白名单 | `WebServerAllowedHosts` |
| `TrustedProxies` | 可信反向代理的 TCP 对端 IP | `WebServerTrustedProxies` |
| `TrustProxyHeaders` | 是否信任转发头 | `WebServerTrustProxyHeaders`，默认 false |
| `BindMode` / `ListenPort` | 监听范围与真实监听端口 | 配置 + 运行时 |
| `Fingerprint` | 上述内容的 SHA-256 指纹 | 计算得出 |

指纹**刻意不含**访问口令（口令变更走口令指纹通道），也**刻意不含**跨源白名单（修改跨源例外不影响"谁能连上来"）。

### 5.2 Host 校验

Host 校验在解析出有效 authority 之后执行：

- 主机名命中 `AllowedHosts` ⇒ 本机 / 局域网访问。要求请求里的显式端口与真实监听端口一致，否则 403；
- 主机名未命中 ⇒ 走外部 authority 匹配，未命中则 403。

Host 头缺失、重复、格式非法一律 403（重复头明确报"Host 头重复：必须唯一。"）。主机名归一化统一走 `WebServerAccessPolicy.NormalizeHost`（小写、去 IPv6 方括号、去首尾空白），白名单条目与请求里的主机名必须走同一套归一化，否则大小写不同的写法会算出两条白名单。IPv6 主机名必须写成 `[::1]` 形式；不带方括号的多个冒号一律解析失败。端口解析是严格的：空、非数字、越界都失败，不"猜"一个端口出来。

### 5.3 Origin 校验

| `Origin` 头 | 结果 |
|---|---|
| 缺失（非浏览器客户端） | 允许；Host 校验仍然生效 |
| `null` | 拒绝 403 |
| 命中显式跨源白名单 | 允许 |
| 与**有效 authority** 逐项相等（协议 + 主机 + 端口） | 允许（同源） |
| 主机在 `AllowedHosts` 内、端口与监听端口一致，且该请求不是外部 authority | 允许 |
| 其它 | 拒绝 403 |

同源判定使用**有效 authority**而不是原始 `Host` 头：反向代理保留 `Host` 时，浏览器的 Origin 是 `https://tunnel.example.com`，而有效 authority 正是它 ⇒ 判定为同源。

### 5.4 外部 authority 白名单

条目语法：`[scheme://]host[:port]`，精确匹配，**无通配符**。

| 写法 | 含义 |
|---|---|
| `tunnel.example.com` | 主机名；端口按协议取默认（https ⇒ 443 / http ⇒ 80） |
| `tunnel.example.com:8443` | 主机名 + 精确端口 |
| `https://tunnel.example.com` | 固定协议（有效协议 = https，端口默认 443） |
| `https://tunnel.example.com:8443` | 固定协议 + 精确端口 |
| `[2001:db8::1]:443` | IPv6 必须带方括号 |

非法条目**静默丢弃**：一条写错的地址不该让整份配置失效，但它也绝不会因此被当成"允许"。丢弃后白名单为空 ⇒ 按"未配置外部暴露"处理，不平白把服务锁死。

固定协议的写法解决"外部是 HTTPS、服务端只看到明文 HTTP"的问题：白名单是用户对"我的对外地址"的权威声明，因此由它决定有效协议。

### 5.5 CORS

- 默认**不返回任何 CORS 响应头**，响应头中不得出现 `Access-Control-Allow-Origin: *`；
- 只有请求的 `Origin` 命中显式跨源白名单时，才回显**具体的那个源**，并附 `Access-Control-Allow-Credentials: true` 与 `Vary: Origin`；
- `OPTIONS` 预检走**同一套** Host / Origin 校验（校验在路由分发前完成），预检响应固定带 `Access-Control-Allow-Methods: GET, POST, OPTIONS` 与 `Access-Control-Allow-Headers: Content-Type, X-LiteMonitor-CSRF`。

### 5.6 监听范围与口令前置条件

判定集中在 `WebServerAccessPolicy`，共有三个执行点，**共用同一套凭据口径**：

| 执行点 | 场景 | 行为 |
|---|---|---|
| `LiteWebServer.Start` | 冷启动 | 局域网暴露或配置了外部 authority 且无有效凭据 ⇒ 拒绝启动并给出明确错误 |
| `AppActions.ApplyWebServerWithResult` | 运行中变更 | 同样的条件 ⇒ 停止服务（不以无口令方式继续监听） |
| 每请求判定 | 命中外部 authority | 外部 authority 且无有效凭据 ⇒ 403 |

局域网暴露的判定同时看配置与运行时事实：配置写着"仅本机"但服务实际还绑在非回环地址上时，凭据一旦被清掉也必须停。

---

## 6. 反向代理与外部地址判定

### 6.1 可信反向代理判定

对端可信 ⇔ **同时**满足：

- `WebServerTrustProxyHeaders` 为 true（默认 false，是"信任任意来源转发头"的降级开关）；
- 请求的**实际 TCP 对端 IP**命中 `WebServerTrustedProxies`。

可信列表按**精确 IP**比较，不接受 CIDR、不接受转发链。IPv4 与 IPv6 分开比较，不做 IPv4-mapped 归一——归一化会把 `::ffff:127.0.0.1` 变成 `127.0.0.1`，那等于凭空多授了信任。非 IP 字面量的配置项被丢弃，绝不当成通配。

### 6.2 转发头处理

**对端不可信 ⇒ 转发头完全不参与判定**：连"它是不是歧义"都不看——不可信对端的头既不能帮忙也不能捣乱。

对端可信时才读取 `X-Forwarded-Proto` 与 `X-Forwarded-Host`，且两者必须**唯一**：

| 情形 | 结果 |
|---|---|
| 头出现多次 | 400（转发头必须唯一） |
| 值里含逗号（多值） | 400（转发头必须唯一） |
| `X-Forwarded-Proto` 不是 `http` / `https` | 400 |
| `X-Forwarded-Host` 主机名或端口格式非法 | 400 |

`X-Forwarded-For` 与 `X-Real-IP` **不被读取**：对外地址判定只认 `X-Forwarded-Proto` 与 `X-Forwarded-Host`，对端身份只认 TCP 对端 IP。

### 6.3 有效 authority 解析链

判定链顺序固定，错误优先级固定：

1. 对端是否可信（不可信 ⇒ 转发头完全不参与）；
2. 可信时才读转发头，并校验唯一性（歧义 ⇒ 400）；
3. 取 authority 文本：可信且带 `X-Forwarded-Host` ⇒ 用它；否则用 `Host` 头（缺失 / 重复 / 端口非法 ⇒ 403）；
4. 严格解析 authority（主机名 + 可选端口）；
5. 命中 `AllowedHosts` ⇒ 本机 / 局域网，显式端口必须与真实监听端口一致（不一致 ⇒ 403）；
6. 未命中 ⇒ 必须命中外部 authority 白名单（未命中 ⇒ 403）。

**转发头歧义（400）先于 Host / Origin 不通过（403）**：歧义的转发头意味着"这次请求的对外身份不可确定"，继续用猜测的 authority 往下判定会把歧义传递下去。

### 6.4 端口口径

- 外部 authority 的端口**绝不**与内部监听端口比较（外部 443 ≠ 内部 5000）；
- 请求带了显式端口 ⇒ 必须与白名单条目的端口**精确一致**（条目没写端口时只接受该协议的默认端口）；
- 请求没带端口 ⇒ 条目写了端口就用它（它就是这条外部 authority 的对外端口），没写则取协议默认端口。

---

## 7. 策略代次与既有连接的处置

### 7.1 策略代次

边界指纹变化 ⇒ 策略代次 +1（`WebSessionManager.EnsurePolicyGeneration`）。指纹覆盖所有会改变访问边界的配置项：监听范围、信任转发头开关、监听端口、主机名集合、外部 authority、可信反向代理列表。

主会话、短期票据、已鉴权 WS 连接都记住自己诞生时的策略代次。

### 7.2 触发条件

| 变更 | 指纹变化 | 影响 |
|---|---|---|
| 收紧 / 放宽主机名白名单 | 是 | 代次变化，既有会话与连接失效 |
| 增删外部 authority 条目 | 是 | 同上 |
| 增删可信反向代理 / 关闭信任转发头 | 是 | 同上 |
| 修改跨源白名单 | 否 | 只影响 CORS，不影响"谁能连上来" |
| 修改访问口令 | 否（走口令指纹通道） | 全部会话与票据撤销 |
| 修改监听范围 / 端口 | 是 | 代次变化 + 服务重启 |

### 7.3 处置

**白名单收紧后，既有连接不得绕过新策略。** 三条处置路径共同保证这一点：

| 路径 | 触发点 | 处置 |
|---|---|---|
| **代次比对** | 取用会话、消费票据 | 代次不匹配 ⇒ 会话作废、票据作废 |
| **广播期反查** | 广播循环枚举收件人 | 连接登记时的 `PolicyGeneration` 与当前代次不符 ⇒ 立即断开，该连接此后收不到任何监控数据 |
| **服务重启** | `ApplyWebServerWithResult` 检测到边界指纹变化 | `Stop` → `Start`：撤销全部会话、票据与 WS 连接 |

**断开语义**：策略变更或凭据失效时，既有连接的处置是**立即断开**，而不是"允许它发完当前帧"或"降级为只读"。已建立的 WebSocket 连接必须停止接收监控数据——否则注销与边界收紧只挡住了已建立的 HTTP 请求，挡不住已在推送的数据流。

代次与指纹的双通道各司其职：口令指纹负责"凭据变了"（全部撤销），策略代次负责"边界变了"（按诞生时的边界有效性判定）。

---

## 8. 请求身份与凭据判定

### 8.1 身份要素

一次请求的身份由三个要素共同决定：

| 要素 | 来源 | 用途 |
|---|---|---|
| TCP 对端 IP | 连接本身，不可伪造 | 可信反向代理判定 |
| 有效 authority | `X-Forwarded-Host`（仅可信对端）或 `Host` | 外部地址判定、同源判定、Cookie `Secure` 判定 |
| `Origin` | 浏览器 | 跨源判定、CORS 回显 |

### 8.2 身份参与转发判定

有效 authority 的协议与主机参与对外地址判定：外部 authority 条目固定了协议 ⇒ 有效协议由条目决定（"我的对外地址是 https"是用户的声明），请求侧协议不同也不影响匹配；条目没固定协议 ⇒ 有效协议取请求侧协议。

有效 authority 是外部的 `https` ⇒ 登录响应与注销响应都带 `Secure`；否则不带。

### 8.3 身份参与凭据判定

- **同源判定用有效 authority**，不是原始 `Host` 头。反向代理保留 `Host` 时，浏览器的 Origin（`https://tunnel.example.com`）与有效 authority 相等 ⇒ 判定为同源，凭据照常使用。
- **跨站导航与跨源跳转**：`lmsid` Cookie 带 `SameSite=Strict`，跨站上下文发出的请求不携带该 Cookie ⇒ 跨源跳转到另一个 authority 时客户端拿不到主会话，服务端按"没有有效主会话"处理（401），不得因为"请求到达了同一个进程"而放行。
- **票据不跨来源使用**：票据在 30 秒内被消费一次即作废；跨源跳转把票据带到另一个 authority 时，对端不认这张票据。
- **可信反向代理身份不构成凭据豁免**：可信只影响"是否采信转发头"，不意味着对端的其它声明获得豁免。`Origin` 的判定口径与对端是否可信无关。

### 8.4 请求头的唯一性是身份判定的一部分

同一身份要素出现两种解释（重复头、重复参数）时必须拒绝，而不是选一个解释继续：

- 重复 `Host` ⇒ 403（Host 头重复：必须唯一）；
- 重复 `X-Forwarded-Host` / `X-Forwarded-Proto` ⇒ 400；
- 重复 `lmsid` Cookie ⇒ 401；
- 重复 `ticket` 查询参数 ⇒ 401；
- 升级请求里重复的 `Content-Length`、出现 `Transfer-Encoding` ⇒ 400（长度头不得有两种解释）。

---

## 9. 失败语义

### 9.1 状态码与含义

| 状态码 | 触发条件 | 响应体 |
|---|---|---|
| 400 | 请求行不合法 / 头字段形态非法（折行、冒号前空白、非法字段名）/ 转发头歧义 / 非法或缺失的 `Content-Length` / 存在 `Transfer-Encoding` / 请求体超过 4 KB / `Content-Type` 不是 `application/json` / 请求体为空 / 请求体长度不一致 / 重复的 `password` 字段 / 升级请求缺 `Upgrade` 头 / `Connection` 头缺 `upgrade` 令牌 / `Upgrade` 协议令牌不是 `websocket` / `Sec-WebSocket-Key` 缺失、重复或形态非法 / `Sec-WebSocket-Version` 重复或不是 13 | 短文本说明 |
| 401 | 口令错误 / 无有效主会话 / 主会话无效或已过期 / 票据无效、已过期或已被使用 / 查询串携带口令参数 | 短文本说明 |
| 403 | Host / Origin 校验未通过 / 外部地址不在白名单 / Host 头缺失、重复或非法 / Host 端口与监听端口不一致 / CSRF 校验失败 / 外部访问未开放（无有效凭据） | 短文本说明 |
| 404 | `GET /favicon.ico` | 空 |
| 405 | 方法不被接受（`POST` 端点收到非 `POST`、`GET /api/snapshot` 收到非 `GET`、升级请求方法不是 `GET`） | 短文本说明 |
| 408 | 读取请求体超时 | 短文本说明 |
| 431 | 请求头块超过 8 KB 上限或未以空行正常结束 | 短文本说明 |
| 500 | 口令迁移失败（无法写入新配置） | 短文本说明 |

错误响应只包含短文本说明，**不得**包含口令、Cookie、票据原文，也不得把解析异常细节原样回给客户端。

### 9.2 错误的固定优先级

一次请求可能同时命中多个失败条件，判定顺序是固定的：

1. **转发头歧义（400）** —— 对外身份不可确定，先于一切；
2. **Host / Origin 不通过（403）**；
3. **外部访问无有效凭据（403）**；
4. **端点自身的鉴权与 CSRF（401 / 403）**；
5. **方法与报文形态（405 / 400）**。

WebSocket 路径上，Host / Origin 与"外部访问必须有凭据"两项先于票据校验：连"你是谁"都还没确定，就不该先评估"你的票据是否有效"。

HTTP 路径上，查询串口令参数（`?pwd=`）的判定排在最前，早于有效 authority 解析。

### 9.3 超时语义

| 场景 | 期限 | 处置 |
|---|---|---|
| 读取请求头 | 5 秒（`WebSessionManager.HeaderReadTimeout`） | 到期直接断开连接（对抗慢速连接与半开连接） |
| 读取请求体 | 复用 5 秒请求头期限 | 到期 408 |
| 同步写（响应 / 握手） | 5 秒（`WebSessionManager.StreamWriteTimeoutMs`） | 到期抛异常，连接关闭 |
| 共享采样等待 | 2 秒（`WebSessionManager.SamplingWaitTimeout`） | 超时后**降级**：照常序列化缓存值并返回，不挂死请求线程 |
| HTTP 快照需求租期 | 2 秒 | 未在窗口内被续期 ⇒ 采集层进入空闲 |

请求头读取期限用取消令牌施加，而不是 `NetworkStream.ReadTimeout`——后者只作用于同步 `Read`，异步读路径不观察它。期限的作用范围只到"读完请求头"为止；握手成功转入长连接后不受它约束，因为长连接的寿命由客户端决定。

在途连接数超过上限（`LiteWebServer.MaxConcurrentConnections` = 64）时直接断开连接。此时尚未读取请求，无法区分 HTTP 与 WebSocket，因此不回 503。该上限提供的是背压，不是防暴力破解。

### 9.4 升级请求的判定优先级

`WebSessionManager.ClassifyUpgrade` 按固定优先级逐条判定：

1. 有无升级意图（`Upgrade` 头存在，或 `Connection` 里带 `upgrade` 令牌）——没有 ⇒ 走普通 HTTP 路由；
2. 头块是否完整（8 KB 上限截断 ⇒ 431，不进握手）；
3. 头字段形态是否合法（折行 / 冒号前空白 / 非法字段名 ⇒ 400）；
4. 请求行（方法必须是 `GET` 否则 405；版本必须是 `HTTP/1.1` 否则 400；目标必须是绝对路径否则 400）；
5. `Upgrade` / `Connection` 是否齐备且协议令牌精确等于 `websocket`（否则 400）；
6. 歧义的长度头（`Transfer-Encoding` 存在或重复的 `Content-Length` ⇒ 400）。

判定为"确实是升级请求但不合法"时必须回明确的 4xx，并关闭连接：回 200 登录页会让 WebSocket 客户端把"收到 200"理解成"连上了"。

---

## 10. 端点契约

| 方法与路径 | 凭据 | 成功 | 失败 |
|---|---|---|---|
| 其它路径（默认路由，返回登录页 / 首页） | 无（仍需通过 Host / Origin 校验） | 200 + HTML | 400 / 403 |
| `GET /favicon.ico` | 无 | 404 + 空体 | — |
| `POST /api/auth/login` | 请求体 `{"password":"…"}` | 200 + `Set-Cookie: lmsid=…; HttpOnly; SameSite=Strict; Path=/; Max-Age=1800` + 体 `{"csrf":"…"}` | 400 / 401 / 403 / 405 / 500 |
| `POST /api/auth/ticket` | `lmsid` Cookie + `X-LiteMonitor-CSRF` 头 | 200 + 体 `{"ticket":"…"}` | 401 / 403 / 405 |
| `POST /api/auth/logout` | `lmsid` Cookie + `X-LiteMonitor-CSRF` 头 | 204 + 清除 Cookie 的 `Set-Cookie` | 401 / 403 / 405 |
| `GET /api/snapshot` | `lmsid` Cookie | 200 + 快照 JSON | 401 / 405 |
| `GET /ws?ticket=…` | 短期票据 | 101 Switching Protocols | 400 / 401 / 403 / 405 / 431 |

端点契约补充：

- `GET /api/snapshot` 是只读端点，**不要求** CSRF 头；
- `POST /api/auth/ticket` **不读**请求体；
- `POST /api/auth/logout` 的清除 Cookie 必须带 `Path=/` 且与签发时的属性集一致（含 `Secure`）；
- 请求体规则：必须有 `Content-Length`（存在 `Transfer-Encoding` ⇒ 拒绝，不允许流式上传）；长度上限 4 KB，超过即拒绝且**不读完**；`Content-Type` 必须是 `application/json`（`charset` 只能是 UTF-8）；读取期限复用 5 秒请求头期限；
- 处理顺序：**先读完请求头 → 校验 Host / Origin → 完成鉴权 → 才决定要不要读请求体**。未鉴权的请求绝不触发请求体读取（否则可被当作带宽 / 内存消耗放大器）。

### 10.1 前端流程

前端（`WebPageContent`）的固定路径：**登录取主会话与 CSRF 令牌 → 申请票据 → 带票据建立 WebSocket**。CSRF 令牌只存在内存变量里；注销时主动清除，并清理浏览器本地存储中可能残留的口令键（`ws_pwd`）。检测到 401 / 403 时清空内存中的 CSRF 令牌并回到登录态，不静默失败；无口令模式下退避后重试登录，避免与服务端互相死循环。

---

## 11. 生命周期与资源约束

| 约束 | 值 | 位置 |
|---|---|---|
| 主会话绝对 TTL | 30 分钟 | `WebSessionManager.SessionAbsoluteTtl` |
| 主会话空闲 TTL | 2 分钟 | `WebSessionManager.SessionIdleTtl` |
| 短期票据 TTL | 30 秒 | `WebSessionManager.TicketTtl` |
| HTTP 快照需求租期 | 2 秒 | `WebDemandState.DefaultHttpLease` |
| 请求头读取期限 | 5 秒 | `WebSessionManager.HeaderReadTimeout` |
| 写超时 | 5 秒 | `WebSessionManager.StreamWriteTimeoutMs` |
| 请求体上限 | 4 KB | `WebSessionManager.MaxRequestBodyBytes` |
| 请求头缓冲上限 | 8 KB | `WebSessionManager.HandleClientAsync` |
| 在途连接上限 | 64 | `LiteWebServer.MaxConcurrentConnections` |
| 共享采样等待上限 | 2 秒 | `WebSessionManager.SamplingWaitTimeout` |
| WebSocket 广播频率 | 每秒 1 帧；无客户端时每 2 秒一轮 | `WebSessionManager.BroadcastLoop` |

需求与采集的联动：已鉴权 WS 连接数大于 0，或存在未过期的 HTTP 快照租期 ⇒ 存在合法的 Web 数据需求；否则采集层回到空闲。需求状态变化通过 `WebDemandChanged` 通知，订阅者必须重新读取 `HasWebDataDemand` 这一唯一真值来源。

服务停止时：会话表与票据表清空、已鉴权 WS 连接全部关闭、需求归零并通知采集层。

---

## 12. 已知限制

以下均为当前客观存在的限制：

- **没有内建 TLS。** 局域网与本机的 HTTP / WebSocket 都是明文传输。对外加密必须由用户自建反向代理提供。
- **白名单是精确匹配，没有通配符。** 主机名、外部 authority、跨源 origin 都不支持通配或模式匹配，子域较多时只能逐条列出。
- **可信反向代理只支持精确 IP。** 不支持 CIDR、不支持转发链；多层转发时只能登记最外层那一跳的实际对端 IP。
- **会话与票据全部在进程内存。** 服务重启即全部失效；多实例部署或水平扩展不成立（实例之间也无法共享会话）。
- **票据出现在 URL 上。** 30 秒 TTL + 一次性 + 仅限 WS 是缓解，不是消除——它仍可能出现在浏览器历史、Referer 与转发节点日志中。
- **没有登录失败限速或锁定。** 反复尝试口令不会被限流或锁定；在途连接上限 64 只提供背压。
- **未配置口令时，访问边界只剩 Host 与 Origin。** 任何能连到监听地址的客户端都可以走"空口令登录"取得主会话。
- **Host 头校验依赖客户端如实填写。** 它防的是 DNS Rebinding（恶意站点把受害者的 DNS 指向本机并以 `Host: attacker.com` 访问），不阻止同一台机器上的其它本机进程访问环回监听端口。
- **跨源白名单是含协议的精确字符串匹配。** `http://` 与 `https://` 是两条不同条目，配置时必须写全。
- **`Sec-WebSocket-Version` 缺失不拒绝。** 为兼容不发送该头的客户端，代价是版本协商能力有限。
- **WebSocket 是广播式扇出。** 每秒一帧向所有收件人发送同一份载荷，不做按客户端的差分或背压分片；客户端数量多时单帧内存开销线性增长。
