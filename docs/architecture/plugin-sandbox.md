# 插件沙箱与网络访问控制

本文描述 LiteMonitor++ 插件系统当前的沙箱边界与网络访问控制模型。适用范围覆盖 `src/Plugins` 下的插件契约模型、声明式执行器、内置 native 能力、地址策略与三类授权器，以及 `src/UI/Settings/PluginPage.cs` 中与插件授权相关的交互入口。不涉及主题市场的校验规则、插件市场的界面交互与插件文件格式的发布流程。

理解本文的前提只有一条：**LiteMonitor++ 的插件是纯声明式的 JSON 配方，不是可执行代码**。沙箱不是内存隔离，而是能力限制——所有约束都作用在"插件能请求什么、能消耗多少"上，而不作用在"插件代码跑在哪个进程、哪个堆上"上。

---

## 1. 插件形态与沙箱语义

### 1.1 插件是什么

插件是 `resources/plugins/` 目录下的 JSON 文件（扩展名 `*.json`），每个文件对应一个 `PluginTemplate`（`src/Plugins/PluginModels.cs`），描述"请求哪个 URL、把响应中的哪个字段映射到哪个显示项"。

插件不携带任何可执行体：

- 插件不包含第三方程序集，运行时也不加载任何第三方程序集。
- 插件不包含脚本、表达式或任何形式的可执行表达式。
- 插件不声明本地资源访问能力：文件读写、进程启动、注册表、剪贴板都不在插件能力面内。
- 插件唯一的对外副作用能力是发起 HTTP(S) 请求。

由此得到一条贯穿全文的等式：**安装一个插件，等价于授予该插件发起网络请求的能力**。真正的信任边界在网络层，而不在代码层。

`PluginProcessor`（`src/Plugins/PluginProcessor.cs`）把口令、令牌与 Cookie 排除在任何日志之外，并且不执行任何外部进程或命令。

### 1.2 沙箱的含义：能力限制而非内存隔离

沙箱由四类机制共同构成：

| 机制 | 作用 |
|---|---|
| 能力枚举封闭 | 变换（transform）只能取固定枚举值；native 处理器只能取编译期实现的若干 handler。不存在动态代码求值、反射调用或表达式引擎。 |
| 目标白名单 | 插件可访问的主机由模板的声明主机集合与用户授权记录共同决定。 |
| 资源配额 | 响应体、步骤数、目标数、执行时长、并发度都有硬上限，且原语层保证不可绕过。 |
| 结构化拒绝 | 每次拒绝都带稳定的机器可读原因码，便于界面呈现与日志归因。 |

明确不构成沙箱的部分：沙箱不限制插件自身声明的内容；沙箱不校验插件来源的真实性（手动放入的插件没有签名校验）；沙箱不提供进程级或内存级的隔离；沙箱不阻止插件发出"合法但恶意"的高频请求（这由配额与重试退避覆盖）。

### 1.3 插件来源与下载约束

| 来源 | 完整性校验 | 信任级别 |
|---|---|---|
| 内置插件（随程序分发的 `resources/plugins/*.json`） | 随程序分发 | 高 |
| 插件市场安装 | 下载内容按原始字节校验 SHA-256；下载源限定为固定 host 与路径前缀 | 中 |
| 用户手动放入 `resources/plugins/` | 无 | 用户自担 |

市场侧的下载约束（`src/Plugins/PluginMarket.cs`）：

- 索引地址为 `PluginMarket.DefaultIndexUrl`，schema 版本由 `SupportedIndexSchema` 约束。
- 允许下载源由**精确 host**（`DownloadHost`）与**仓库路径前缀**（`RepoMainBasePath` + 资产前缀 `plugins/`）共同限定。判定基于 Uri 解析后的 host 精确匹配与路径前缀比对，而不是整串前缀比较，因此路径穿越与"前缀后接其它域名/路径"都无法绕过。
- 校验项包括：绝对 URI、HTTPS、无 userinfo、默认端口、host 精确命中白名单、路径无编码分隔符、无 `..`、无空段、路径前缀命中白名单。
- 索引条目必须提供可用的 SHA-256（`IsUsableSha256`），否则以 `MarketErrorReason.MissingHash` 拒绝安装；安装时按原始字节校验，不匹配以 `MarketErrorReason.HashMismatch` 失败。
- 下载侧配额：`MaxDownloadBytes` = 8 MiB、`MaxDownloadRedirects` = 5、`DefaultDownloadTimeoutMs` = 30 000、`MaxDownloadUrlLength` = 2048。

---

## 2. 执行模型

### 2.1 模板结构

`PluginTemplate` 的顶层字段（`src/Plugins/PluginModels.cs`）：

| 字段 | 类型 | 含义 |
|---|---|---|
| `id` | string | 插件标识，全局唯一 |
| `meta` | `PluginMeta` | 名称（中英）、版本、作者、描述、主页 |
| `inputs` | `PluginInput[]` | 用户输入项 |
| `execution` | `PluginExecution` | 执行定义 |
| `outputs` | `PluginOutput[]` | 输出项（显示层契约） |
| `dynamic_host` | bool | 目标主机由用户输入决定 |
| `display` | `PluginDisplay` | 标签与任务栏简写文案 |

`PluginMeta` 字段：`name` / `name_en` / `version` / `author` / `description` / `description_en` / `url`。

`PluginInput` 字段：`key` / `label` / `label_en` / `type` / `default` / `placeholder` / `options` / `scope`。

- `type` 的取值集合是 `text`、`password`、`select` 等；缺省为 `text`。
- `scope` 取 `global`（缺省，整个插件实例共享一份，例如 API Key）或 `target`（每个 target 各配一份，例如股票代码）。

`PluginOutput` 字段：`key` / `label` / `label_en` / `short_label` / `short_label_en` / `format_val` / `color` / `unit`。

`PluginDisplay` 字段：`label` / `short_label`。

### 2.2 执行类型

`PluginExecution.type` 的取值：

| 值 | 语义 | 使用的字段 |
|---|---|---|
| `api_json` | 单请求，响应按 JSON 解析 | `url` / `method` / `body` / `headers` / `extract` / `process` |
| `api_text` | 单请求，响应按纯文本处理 | 同上 |
| `chain` | 步骤链 | `steps` |

`PluginExecution` 字段全集：`type` / `method` / `interval` / `min_interval` / `url` / `body` / `headers` / `extract` / `process` / `steps`。

刷新节奏由 `interval`（秒）与 `min_interval` 共同决定。`min_interval` 为 0 表示不额外限制。实际生效间隔取 max(实例自定义间隔或模板 `interval`, max(`PluginConstants.DEFAULT_INTERVAL`, 模板 `min_interval`))，由 `PluginManager.GetMinimumInterval` 与 `PluginManager.GetEffectiveInterval` 计算。

### 2.3 步骤字段契约

`PluginExecutionStep`：

| 字段 | 缺省 | 含义 |
|---|---|---|
| `id` | — | 步骤标识，进入缓存键与请求路由维度 |
| `url` | — | 目标 URL，支持 `{{变量}}` 插值；也可为 `native://` 目标 |
| `method` | `GET` | 请求方法 |
| `body` | 空 | 请求体，支持 `{{变量}}` 插值 |
| `headers` | 空 | 请求头字典 |
| `response_encoding` | `utf-8` | 响应编码，取 `utf-8` 或 `gbk` |
| `response_format` | `json` | 响应格式，取 `json`、`jsonp` 或 `text` |
| `extract` | 空 | 字段提取规则，键为写入 context 的变量名 |
| `process` | 空 | 变换规则数组 |
| `cache_minutes` | 0 | 缓存分钟数；0 表示不缓存，-1 表示永久缓存 |
| `skip_if_set` | 空 | 跳过条件：context 中该键存在且非空时跳过本步 |
| `continue_on_error` | `false` | 步骤失败时是否继续执行链中其余步骤 |
| `proxy` | 空 | `host:port` 形式的 proxy 端点 |

`method` 在出网点只支持 GET / POST / HEAD，其它取值回落为 GET ⇒ 插件可发出的方法面被限制在这三种之内。

### 2.4 变量流

步骤之间通过 context（键值字典）传递变量。`{{name}}` 形式的占位符在 URL、body、proxy、headers 中解析（`PluginProcessor.ResolveTemplate`）。`extract` 规则从响应中取出字段写入 context，`process` 是变换数组，逐条作用于 context。模板插值同样带 500 ms 的正则时限。

### 2.5 动态主机

`dynamic_host` 是模板的**声明**：目标主机由用户输入给出（如 `https://{{host}}/api`）。声明了 `dynamic_host` 的模板在需要凭据时必须走"用户对具体 origin 授权"的流程。

该字段表达"模板设计允许动态主机"；运行时的强制点在授权器与目标白名单，而不在模板字段本身。动态主机的 origin 由 `DynamicHostAuthorizer` 管理，授权记录按完整 origin 存储，规范化形式为 `scheme://host[:port]`，IDN 小写、IPv6 加方括号、默认端口省略；端口、IDN 形式与大小写均精确匹配。

`DynamicHostAuthorizer` 的授权**只接受 HTTPS**，本机回环地址的 http 除外。

---

## 3. 变换能力：封闭枚举

`PluginTransform` 的 `function` 字段是封闭枚举，取值只有五种：

| 值 | 语义 |
|---|---|
| `regex_replace` | 正则替换。`pattern` 是正则，`to` 是替换串；`to` 中含 `{{` 时先做模板插值 |
| `regex_match` | 正则捕获。`pattern` 是正则，`to` 可为数字表示捕获组号（缺省为 1） |
| `map` | 值映射。`map` 是字典；未命中时返回原值 |
| `resolve_template` | 对目标变量做模板插值 |
| `threshold_switch` | 阈值映射。`value_map` 给出阈值到输出的对应关系 |

`PluginTransform` 的字段：`var`（写入的目标变量）/ `source`（源变量，缺省取目标变量本身）/ `function` / `pattern` / `to` / `map` / `value_map`。

源变量在 context 中不存在时，该条变换被跳过。变换循环受单次执行总时长约束，可被总时长到期打断。

明确不具备的能力：没有动态代码求值（无 eval、无脚本引擎）、没有反射调用、没有表达式引擎、没有动态编译。`PluginProcessor` 的类注释把这一点写为插件契约的组成部分。

正则受时限约束：所有正则匹配与替换都带 500 ms 超时（`PluginSandboxLimits.RegexMatchTimeout`），超时以 `PluginSandboxReasons.RegexTimeout` 失败。pattern 缓存有容量上限（`MaxGuardedRegexCacheEntries` = 64），避免插件用无限多 pattern 撑爆缓存。非法 pattern 导致的解析异常不被吞掉，直接向上传播为失败。

---

## 4. native 能力：编译期实现的处理器

步骤的 `url` 可以是 `native://<handler>?k=v&...` 形式，参数中可内嵌绝对 URL 作为回退目标。`native://` 目标与 HTTP 步骤共用同一套沙箱闸门：允许目标集合取自插件声明的主机集合；参数里内嵌的绝对 URL 在任何请求发出前完成校验，不通过时一个请求都不会发出。

内置处理器只有三个，全部是编译期实现：

| 处理器 | 作用 | 触网行为 |
|---|---|---|
| `native://citycode` | 由 `province` / `city` / `district` 解析城市编码（`CityCodeResolver.ResolveAsync`） | 优先使用随包 CityCode.json；本地缺失时回退到固定的远程资源 `raw.githubusercontent.com/mojingjing-git/LiteMonitorPlus` 仓库中的 `resources/assets/CityCode.json`（完整地址常量由 `CityCodeResolver` 持有）。回退请求占进程级并发槽位、响应体受 2 MiB 约束、绑定执行作用域的取消令牌。 |
| `native://crypto` | 由 `symbol` 取行情（`CryptoNative.FetchLeasedAsync`） | 远程回退目标必须落在插件声明的主机集合内，否则以 `CredentialTargetNotAllowed` 失败且请求不发出。 |
| `native://newyear` | 计算距元旦的天数（`NewYearNative.GetCountdownJson`） | 纯本地计算：不触网、不占并发槽位、不产生响应体。`name` 恒为"元旦"，`source` 恒为 `local`；不推测调休；时钟可注入 `TimeProvider`。 |

`native://` 的 URL 形态是 `native://handler?k=v&fallback=https://host/path`。内嵌的绝对 URL 同样是插件写死的目标，因此同样受目标白名单约束。

**未知 handler 一律拒绝**：`uri.Host` 不匹配上述三者时，执行器抛出异常并中止该步骤，不存在"回落到通用 HTTP 抓取"的路径。

native 的在途去重按执行作用域标识（`PluginSandboxScope.ScopeId`）归并 ⇒ 不同执行作用域之间不共享 native 在途请求；无执行作用域时使用一次性标识。

---

## 5. 沙箱配额

配额定义在 `PluginSandboxLimits`（`src/Plugins/PluginProcessor.cs`），是契约常量，取值不得为 0、无限制或留空。

| 配额 | 值 | 计数口径 | 超限行为 |
|---|---|---|---|
| 响应体上限 `MaxResponseBytes` | 2 MiB（2 097 152 字节） | 单次响应 | 抛 `ResponseTooLarge`；不截断、不静默丢数据 |
| 正则匹配超时 `MaxRegexMatchTimeoutMs` | 500 ms | 单次匹配或替换 | 抛 `RegexTimeout` |
| 步骤数上限 `MaxStepsPerExecution` | 16 | 按目标计：每个 target 拥有独立步骤预算，跨 target 不累加 | 抛 `StepLimit` |
| 每步目标数上限 `MaxTargetsPerStep` | 64 | 按实例计：一次执行内所有 target 的目标展开数累计，判定依据是累加后的总数 | 抛 `TargetLimit` |
| 单次执行时限 `MaxExecutionDurationMs` | 30 000 ms | 按实例计：一次执行共用一个截止时刻，跨 target 累计 | 抛 `DeadlineExceeded` |
| 并发请求上限 `MaxConcurrentRequests` | 8 | 按进程计：进程级 `SemaphoreSlim(8, 8)` | 排队超时后抛 `ConcurrencyLimit` |
| 并发等待超时 `ConcurrencyAcquireTimeoutMs` | 1 000 ms | 获取并发槽位的排队上限 | 等不到槽位即明确失败，不无限排队 |

三种计数口径互不重叠，各有且只有一个判定点：`PluginSandboxScope.EnsureStepAllowed`（目标级作用域）、`PluginSandboxScope.EnsureTargetsAllowed`（实例级作用域）、`PluginSandboxScope.EnsureNotExpired`（两级作用域共用同一截止时刻）。

补充约束：

- 实例级响应体上限 `PluginExecutor.DefaultMaxResponseBytes` = 8 MiB，落在沙箱之下。实际生效值 = min(实例级上限, 沙箱硬顶 2 MiB)。`PluginSandbox.ReadResponseBodyAsync` 在原语层再次夹取 ⇒ 2 MiB 是原语层硬保证，调用方传入更大的上限也无法突破。
- 并发槽位按整条重定向链占用一次：链开始前获取，链结束后释放。
- 目标数上限在发起任何请求前判定（`EnsureTargetsAllowed`），保证超限时一个请求都不会发出。
- 单请求超时由 `HttpClient.Timeout`（10 s）与正文读取期限共同约束，两者都覆盖整条重定向链。

作用域模型（`PluginSandboxScope`）：实例级作用域持有取消令牌、截止时刻与目标展开计数；`BeginTarget()` 开启目标级作用域，与实例级共享截止时刻、取消令牌与目标展开数累计，只有步骤计数是目标独立的。所有作用域共享同一 `ScopeId`（Guid N 格式），用于按执行作用域归并在途请求。

---

## 6. 结构化拒绝原因码

所有沙箱拒绝都带稳定的机器可读原因码（`PluginSandboxReasons`，`src/Plugins/PluginProcessor.cs`），通过 `PluginSandboxException.Reason` 暴露。

| 原因码 | 含义 | 触发条件 |
|---|---|---|
| `LM009_RESPONSE_TOO_LARGE` | 响应体超限 | 声明长度或实际读取量超过生效上限（不超过 2 MiB） |
| `LM009_REGEX_TIMEOUT` | 正则匹配超时 | 单次匹配或替换超过 500 ms |
| `LM009_STEP_LIMIT` | 步骤数超限 | 单个 target 内的步骤数超过 16 |
| `LM009_TARGET_LIMIT` | 目标数超限 | 单次执行内累计的目标展开数超过 64 |
| `LM009_DEADLINE_EXCEEDED` | 单次执行超时 | 一次执行的总时长超过 30 000 ms |
| `LM009_CONCURRENCY_LIMIT` | 并发超限 | 1 000 ms 内未取得进程级并发槽位 |
| `LM009_CREDENTIAL_TARGET_NOT_ALLOWED` | 凭据目标未获授权 | 带凭据头的请求指向不在声明主机集合或用户授权集合内的目标 |
| `LM009_REDIRECT_TARGET_NOT_ALLOWED` | 重定向目标未获允许 | 重定向落点不在允许集合内、缺失 Location、scheme 非 http/https、或发生 https→http 降级 |
| `LM009_REDIRECT_LIMIT` | 重定向跳数超限 | 重定向跳数达到 5（`PluginExecutor.MaxRedirectHops`） |
| `LM009_REQUEST_TARGET_NOT_ALLOWED` | 请求目标未获允许 | 不带凭据的请求目标落在声明主机集合与用户授权集合之外 |
| `LM009_ADDRESS_POLICY_DENIED` | 地址策略拒绝 | 目标解析出的候选地址中存在永远禁止的类别 |
| `LM009_LOCAL_NETWORK_NOT_ALLOWED` | 本地网络未获授权 | 目标解析出的候选地址中存在本机或私网地址，且用户未授权该 origin |
| `LM009_TARGET_UNRESOLVABLE` | 目标不可解析 | 主机名解析不出任何候选地址 |
| `LM009_PLUGIN_PROXY_NOT_AUTHORIZED` | proxy 端点未获授权 | 步骤指定的 proxy 端点未获得用户确认 |

`PluginSandboxException` 另提供带 inner exception 的构造，用于包裹底层异常而不丢失原因码。`PluginResponseTooLargeException` 继承自 `PluginSandboxException`，原因码统一为 `ResponseTooLarge`。

---

## 7. 网络访问的七道闸门

一个插件步骤从"声明"到"发出请求"必须依次通过七道闸门。任一闸门不通过时，该请求根本不发出。

### 7.1 闸门一：声明主机集合

插件可访问的主机由声明主机集合决定，集合来自 `PluginProcessor.GetDeclaredHosts(tmpl, instanceId)`：

- 只认步骤 URL 与执行根 URL 中的**字面量主机**，经 `PluginHostPolicy.TryGetDeclaredHost` 解析。
- **主机名来自模板变量（如 `{{target_url}}`）不构成声明** ⇒ 动态目标不会因为出现在模板里就获得访问权。
- 集合并入 `DynamicHostAuthorizer` 中该实例的用户授权 origin。

### 7.2 闸门二：URL 与 authority 合法性

`PluginHostPolicy` 先校验原始 authority 字符串，再读取 `Uri.Host`（`Uri.Host` 会丢掉 IPv6 zone id）。规则：

- scheme 必须通过 `Uri.CheckSchemeName`；请求类目标只接受 http 与 https。
- authority 不得含空白、控制字符以及 `/ ? # \ { }`。
- `@` 至多出现一次；userinfo 只允许 `%XX` 转义与 `-._~!$&'()*+,;=:`
- `[v6]` 形式要求内嵌是合法 IPv6，且拒绝原始形式与百分号编码形式的 zone id（含 `%` 直接拒绝），输出小写方括号形式。
- 非括号形式禁止 `[`、`]`、`%`，禁止多冒号；host 不得为空、不得含 `*`；`Uri.CheckHostName` 不得返回 Unknown。
- 端口必须纯数字且不超过 65535。

非法时 `PluginHostPolicy.RequireHttpHost` 抛出 `PluginSandboxException`，原因码默认为 `CredentialTargetNotAllowed`。

### 7.3 闸门三：凭据目标白名单

凭据头名单（大小写不敏感，`PluginProcessor.CredentialHeaderNames`）：

`authorization`、`proxy-authorization`、`cookie`、`set-cookie`、`x-api-key`、`x-api-token`、`api-key`、`apikey`、`x-auth-token`、`x-access-token`、`x-token`、`x-secret`、`x-client-secret`、`x-hub-signature`

规则：

- 只有带凭据头的请求才要求落在**声明主机集合**内；不满足时以 `CredentialTargetNotAllowed` 失败，凭据不被发送。
- 不带凭据的目标同样必须落在声明主机集合与用户授权集合的并集内，否则以 `RequestTargetNotAllowed` 失败。
- 凭据校验先于请求目标校验 ⇒ 原因码合同稳定。
- 授权记录只保存主机名与原因码，凭据值绝不写入任何日志。

凭据目标授权由 `DynamicHostAuthorizer` 承担，只接受 HTTPS（本机回环地址的 http 除外）。

### 7.4 闸门四：地址分类

所有目标地址都经 `PluginAddressPolicy.Classify` 分类。分类是集中判定的唯一事实来源 ⇒ 调用方不得用字符串前缀或 `IPAddress.IsLoopback` 之类的零散判断替代。分类只反映地址本身，地址类别是否可达由预检与授权器判定。完整分类见第 8 节。

### 7.5 闸门五：地址预检

`PluginAddressPreflight.EnsureAddressPolicyAsync(target, proxy, instanceId, token)` 在实际连接前完成解析与判定：

- 主机名解析出的**整组**候选地址一起判定。任一候选落入永远禁止的类别 ⇒ 整组拒绝（`AddressPolicyDenied`）；任一候选落在本机或私网且该 origin 未获用户授权 ⇒ 整组拒绝（`LocalNetworkNotAllowed`）。不允许"存在公网候选就放行"的部分判定。
- IP 字面量目标直接分类，即使走 proxy 也执行。
- 走 proxy 的主机名目标不做本地 DNS 解析（解析发生在 proxy 侧，本地结果既不代表终点，也可能误伤企业 proxy 场景）。
- 解析不出候选地址 ⇒ `TargetUnresolvable`，不静默放行。

### 7.6 闸门六：逐跳重定向复核

默认 `HttpClient` 关闭自动重定向（`AllowAutoRedirect` = false）⇒ 重定向由 `PluginExecutor.FetchRawAsync` 逐跳处理、逐跳复核。自动跳转会让请求在来不及复核目标主机时到达下一跳，凭据头与 Cookie 会随跳转外泄。

每一跳都必须满足：

1. `EnsureHopTargetAllowed` 通过：带凭据时每一跳都必须在**声明主机集合**内（失败原因码 `CredentialTargetNotAllowed`）；不带凭据时每一跳都必须在允许集合内，该集合为声明主机集合与原始直连目标主机的并集（失败原因码 `RedirectTargetNotAllowed`）。不通过时该跳请求根本不发出。
2. 重新执行 `PluginAddressPreflight.EnsureAddressPolicyAsync` ⇒ 地址分类与授权检查每跳重做。
3. `EnsureRedirectSchemeAllowed` 通过：目标必须仍是 http 或 https；**不允许 https→http 降级**，因为凭据与正文会从加密信道掉到明文信道（`RedirectTargetNotAllowed`）。
4. 跳数不超过 `MaxRedirectHops` = 5（超限为 `RedirectLimit`）；缺失 Location 为 `RedirectTargetNotAllowed`；相对 Location 以当前 URI 解析，缺基准 URI 为 `RedirectTargetNotAllowed`。

协议相对 Location（`//host/path`）与绝对 Location 都按 `RedirectTargetNotAllowed` 校验。

### 7.7 闸门七：资源配额

请求进入出网点后仍受第 5 节配额约束：响应体不超过 2 MiB、单次执行总时长不超过 30 s、进程并发不超过 8、步骤数与目标数上限在发出请求前判定。并发槽位按整条重定向链占用一次。

### 7.8 proxy 端点授权

步骤声明 `proxy` 时，该端点必须已获用户确认（`PluginProxyAuthorizer`，按实例与端点字符串记录）。**端点字符串发生变化即视为新端点**，需要重新确认。系统 proxy 不经过该授权器。未授权时以 `PluginProxyNotAuthorized` 失败。

### 7.9 本地网络授权与凭据授权是两套独立记录

`PluginLocalNetworkAuthorizer` 的 origin 规范化**允许 http 与 https**（本地服务常见明文私网地址），且只放行**不带凭据**的本地访问；`DynamicHostAuthorizer` 的凭据目标授权只接受 HTTPS。两者刻意分离，避免把凭据发往明文 HTTP。

`PluginLocalNetworkAuthorizer.RevokeAll(instanceId)` 在磁盘记录未能删除时同步恢复内存记录，防止该实例在下一次启动中重新继承该授权。

---

## 8. 地址分类

`PluginAddressClass`（`src/Plugins/PluginAddressPolicy.cs`）的取值：`Unspecified` | `Loopback` | `LinkLocal` | `CloudMetadata` | `Private` | `Public`。

### 8.1 永远禁止的类别

`PluginAddressPolicy.IsForbiddenAlways` 覆盖三类，无论声明还是授权如何一律拒绝：

| 类别 | 覆盖地址 |
|---|---|
| `Unspecified` | 0.0.0.0/8、`::` |
| `LinkLocal` | 169.254/16（169.254.169.254 除外）、fe80::/10 |
| `CloudMetadata` | 169.254.169.254、fd00:ec2::254 |

云元数据端点（AWS / GCP / Azure IMDS）单列一类 ⇒ 即使用户授予了本地网络权限也不放行。

### 8.2 需用户单独授权的类别

`PluginAddressPolicy.IsLocalScope` 覆盖 `Loopback`（127/8、`::1`）与 `Private`，需要用户对具体 origin 授予本地网络权限（`PluginLocalNetworkAuthorizer`）。

`Private` 的覆盖范围：10/8、172.16/12、192.168/16、100.64/10 CGNAT、198.18/15、192.0.0.0/24、192.0.2/24 TEST-NET-1、198.51.100/24 TEST-NET-2、203.0.113/24 TEST-NET-3、224/4 组播、240/4 保留、广播、ff00::/8 组播、fc00::/7 ULA、fec0::/10、fe00::/16，以及首字节不小于 0xf0 的全部 IPv6 地址。未知地址族按最保守的 `Private` 处理。

### 8.3 IPv4-in-IPv6 拆解

`PluginAddressPolicy.TryUnwrapEmbeddedIPv4` 在分类前解包 v6 外壳内嵌 v4 的三种形态，解包后按内嵌 IPv4 分类 ⇒ v6 形态无法绕过 v4 私网判定。

| 形态 | 前缀 | 内嵌 IPv4 位置 |
|---|---|---|
| v4-mapped | `::ffff:0:0/96` | 第 13–16 字节 |
| NAT64 | `64:ff9b::/96` | 第 13–16 字节 |
| 6to4 | `2002::/16` | 第 3–6 字节 |

---

## 9. 生命周期与失败回退

### 9.1 实例与模板对账

插件实例（`PluginInstanceConfig`）与模板集合的对应关系由 `PluginManager` 维护：

- `LoadPlugins` 在 `lock(_lifecycleLock)` 内完成加载，随后在 `lock(_scheduleLock)` 内发布**完整**模板集 ⇒ 绝不发布部分加载的集合。
- 对账动作：模板集合中不存在的实例被退役（清理运行态、清 KeyCache、删除监控项、从 settings 中删除）；模板新增时创建实例（id 冲突时用 8 位 Guid 前缀），并按 input 默认值填充 `InputValues`。
- `ReconcileLocked` 的判定依据是 `PluginRuntimeSnapshot.Fingerprint(source, template)`：实例的 Id / TemplateId / Enabled / CustomInterval / InputValues / Targets 加上整个模板的**规范化 JSON**（对象键按序数排序、数组顺序保留）取 SHA-256。指纹未变时不重建运行态；指纹变化时重新准备运行态、退役被替换的运行态、同步监控项。
- 指纹刻意绕开 settings 的密文转换器（随机化加密不是语义指纹），否则同一语义会得到不同指纹。

### 9.2 锁顺序

`PluginManager` 的锁顺序是固定的：

```
lifecycle(_lifecycleLock) → publication(_scheduleLock) → executor HTTP/cache → InfoService 与 MonitorItems
```

- 调度 tick 只获取 publication gate。
- 任何路径都不得从 tick 或发布提交反向获取 lifecycle 锁。
- 取消、释放、计时器工厂、设置保存与外部观察者一律在两个管理器锁都释放之后运行。

### 9.3 步骤级失败继续

`continue_on_error` 缺省为 false ⇒ 步骤失败即中断整条链。置为 true 时：

- 只有**业务可恢复失败**被吞掉并进入下一步：请求失败、无有效数据、字段提取失败、单请求超时。
- 外层取消、单次执行总时长耗尽，以及一切沙箱违规（并发、响应体、步数、目标数、凭据目标、重定向）**一律不可吞**。
- 可回退步骤在独立的 context 副本上执行，只有成功才合并回链上下文 ⇒ 失败不会留下半成品变量误触发链中其余步骤的 `skip_if_set`。

可恢复性由 `PluginExecutor.IsRecoverableStepFailure` 判定：`PluginSandboxException` 不可恢复；`PluginClientDisposedException`、`PluginStepDataException`、`HttpRequestException`、`JsonException` 可恢复；**任何未列入的异常类型一律不可恢复** ⇒ 不得降级成"看起来正常的备用值"。

提取出的值为 `PluginConstants.STATUS_UNKNOWN` 或 `PluginConstants.STATUS_ERROR` 时抛 `PluginStepDataException` ⇒ 该值绝不会被当成正常值发布。

### 9.4 运行态失败状态与重试间隔

- 实例执行的结果按"任一 target 成功即视为成功"判定；全部失败则判定为失败。多 target 之间并发执行，并对第 2 个及以后的 target 做递增错峰。
- 失败时连续失败计数加一，重试间隔 = min(连续失败数 × `PluginConstants.RETRY_INTERVAL_MS`(5 000 ms), 60 000 ms)。
- 成功时清零连续失败计数，间隔回到 intervalSeconds × 1000。
- 连续失败数不小于 3 且为 3 的倍数时，触发 `PluginExecutor.ResetNetworkClients()` 与 `PluginExecutor.ClearCache(instanceId)`。重置是**全局**动作而失败计数是按实例的，因此重置由 60 秒的全局冷却窗口（`NetworkResetCooldown`）节流，窗口内的重复调用是幂等空操作；重置不清空在途字典、不主动取消在途请求。
- 实例不可见时，间隔取 `InvisibleRetryIntervalMs`（缺省 2 000 ms）。
- 失败态在显示层以 `PluginConstants.STATUS_ERROR`（`[ERROR]`）呈现，其余状态串为 `[UNKNOWN]`、`[EMPTY]` 与 `...`。

### 9.5 内置插件标识的第二道防线

`PluginConstants.BuiltinPluginIds` 是一份内置插件标识名单（大小写不敏感）：Bilibili、Crypto、Exchange、GitHub、Hitokoto、Holiday、ProxyLatency、PublicIP、Stock、UniversalAPI、Weather、WeiboHot。

这份名单只是**第二道防线**。真正兜底的是 `PluginMarket.IsTargetAvailable` 的规则：目标文件存在且不在市场安装记录内 ⇒ 拒绝安装，提示目标文件由内置插件或非本市场安装的文件占用。该规则不依赖任何名单 ⇒ 名单与实际文件状态不一致时，市场侧仍然安全。

`PluginSelfCheck.RunOnce()` 扫描插件目录，把"文件名 stem 与内容 id 不一致、且不在市场安装记录内"的疑似残留文件列入 `SuspiciousFiles`，供市场页展示。

---

## 10. 已知限制

- **地址预检与实际连接之间存在时间差**。`PluginAddressPreflight` 的解析结果与实际建立连接之间 DNS 记录可能发生变化，因此预检是"连接前缓解"，**不构成完整的 DNS 重绑定防护**。`src/Plugins/PluginAddressPreflight.cs` 的类注释对此有明确自述，对外表述不得超出"连接前缓解"这一范围。
- **共享在途请求的取消语义**。共享的 HTTP 请求以不携带取消令牌的方式启动 ⇒ 取消一个等待者不会取消共享任务；每个等待者由自身执行作用域的令牌约束总时长，等待者超时按 `DeadlineExceeded` 失败。
- **共享在途请求的策略校验前移到调用侧**。每个等待者在加入租约前各自履责目标与凭据策略 ⇒ 白名单更严的调用方不会复用更宽松调用方的响应；但允许目标与凭据策略不得进入在途键或请求摘要。
- **动态主机不构成声明**。模板变量拼出的目标主机不进入声明主机集合，其访问权完全依赖用户对具体 origin 的授权记录。
- **手动放置的插件没有完整性校验**。用户直接放入 `resources/plugins/` 的 JSON 不会被校验来源与内容完整性。
- **地址分类偏保守**。TEST-NET、CGNAT、站点本地等保留地址段一律按 `Private` 处理，未知地址族同样按 `Private` 处理 ⇒ 一部分实际可用的公网地址落在授权之外。
