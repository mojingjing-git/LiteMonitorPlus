# 插件仓库结构改造与 PR 协作流程 Implementation Plan

> **状态**：待实施（本文件只是计划，尚未改动任何仓库或源码）
> **编写时间**：2026-10-20
> **适用仓库**：`https://github.com/mojingjing-git/awesome-litemonitor-plugins`（本地路径 `G:\TOOL\awesome-litemonitor-plugins`）
> **关联计划**：`docs/superpowers/plans/2026-10-20-plugin-market-hardening.md`（客户端侧计划）—— 本计划**取代**其 Task 1 与 §3.2，详见 §1.4
> **文件性质**：独立新文件。编写本计划的过程**没有改动任何既有 plan 的任何一行，也没有改动任何既有源码行**。

---

## 0. 摘要

| 项目 | 结论 |
|---|---|
| **触发原因** | fork 的立项理由之一是「上游缺少插件市场」；插件市场要在**未来开放社区 PR**，因此仓库不能只是一堆平铺的 JSON |
| **核心诉求** | 每个插件在仓库里有**一个页面**，页面同时包含**插件本体**与**介绍**；软件侧的市场能给每个插件**附上一句简介** |
| **用户原始设想** | 仓库页面含本体 + 介绍；软件侧读取"介绍里的 JSON 文件"取简介 |
| **评估结论** | **目标成立，实现路径需调整**：把"页面的生成"交给 CI，而不是让贡献者手写页面、更不能让软件去爬 HTML 页面 |
| **已确认决策** | **A** = 纯仓库形态（不做 GitHub Pages）／**不动 id**（只要求目录名对齐）／**不加 `meta.json`** |
| **对客户端的影响** | **几乎为零** —— 客户端只消费 `index.json` 的 `download_url` 与 `id`，仓库内部布局怎么改都与它无关 |
| **改动量** | 仓库：13 个插件目录重组 + 13 篇 README + 1 个脚本 + 1 个 workflow + 2 个文档；客户端：**仅 `MarketPluginEntry` 增加 7 个字段** + 市场页消费它们（见 §3.3），其余客户端代码不动 |
| **对 PR 友好度** | 最小提交 = **1 个文件**（`plugin.json`）+ **1 个文件**（`README.md`），**无需写 HTML，无需写第二份元数据** |

---

## 1. 背景与目标

### 1.1 原始设想与评估

用户提出：

> 在仓库给每个插件新建一个页面，页面里包含两部分：① 插件本体 ② 介绍；软件侧的市场可以读取介绍里的 JSON 文件，给插件附上一个简短的简介。

**目标成立**，"每插件一页 + 软件侧有简介"是正确的方向。但"软件侧读页面里的 JSON"这一步有明确问题：

| 问题 | 说明 |
|---|---|
| **HTML 不是好的机器读载体** | 要从页面提取 JSON，必须约定注入方式（`<script type="application/json">` / `<meta>` / data-* 属性），属隐式契约 —— 页面模板一改就断，且无任何校验能在断之前发现 |
| **N+1 请求** | 13 个插件 = 13 次 HTTP 往返，还带每页 HTML 体积。而一个 `index.json` 一次就够。桌面监控程序启动拉索引，不该付这个代价 |
| **"介绍"与"页面"是两种东西** | 介绍是**数据**（要能进市场列表），页面是**渲染**（给人看）。绑在一起等于逼机器解析人类界面 |

**本计划的解法（核心倒置）**：

> 不让贡献者手写页面，也不让软件去爬页面 —— **让 CI 生成聚合索引，让软件读 CI 产出的 `index.json`；插件页由 GitHub 原生渲染目录下的 `README.md`**。

### 1.2 已确认的三项决策

| 编号 | 决策 | 落地方式 |
|---|---|---|
| **D1** | **方案 A：纯仓库形态** | 插件页 = GitHub 仓库的目录页 `.../tree/main/plugins/<id>`。**不启用 GitHub Pages**，不需要 CI 渲染 HTML，不需要写站点模板 |
| **D2** | **不动 id** | 现有 13 个插件的 `id` **一个都不改**。只要求**目录名 == id**，从而消除文件名与 id 不符的问题 |
| **D3** | **不加 `meta.json`** | 插件的机器可读信息**全部放在 `plugin.json` 里**（`meta` 段已有 name/description/author/version + `_en` 变体）。不新增第二个元数据文件，避免两处维护不一致 |

> **D2 的重要含义**：id 是落盘文件名、是市场主键、会进用户 settings —— 一旦有人装了就不能改。现在只有 13 个插件、尚未开放 PR，此刻是统一的最佳时机；但用户选择**保留现状 id**，本计划尊重该决策，并在 §3.4 明确记录"id 一经发布即冻结"的规则。

### 1.3 目标

1. **每个插件在仓库里有一个页面**，页面同时呈现「插件本体」与「介绍」——由 GitHub 原生渲染，零维护成本。
2. **软件侧市场能给每个插件附简介**，且**只发 1 次 HTTP 请求**。
3. **PR 友好**：贡献者提交新插件的成本 = 放 2 个文件；不合规的 PR 在 CI 阶段就变红。
4. **可扩展**：未来若需要站点展示，可在此结构上叠加 GitHub Pages，而不必重构仓库。

### 1.4 与既有计划的关系

| 文件 | 关系 |
|---|---|
| `docs/superpowers/plans/2026-10-20-plugin-market-hardening.md`（客户端侧计划） | **本计划取代其 Task 1（仓库侧）与 §3.2（index.json schema）**。其余 Task 2-8（语言 key、安全加固、源锁死、i18n、测试、文档）**继续有效**，但其中已按本计划同步修正（逐项清单见 Task 6.1） |
| `docs/superpowers/plans/2026-10-20-gpu-*.md` / `igpu-load-fix.md` | 与本计划无关，未改动 |

**为什么仓库布局大改而客户端几乎不动**：

客户端消费的字段全部来自 `index.json`（`id` / `download_url` / `sha256` / `name` / `description` / `name_en` / `description_en` / `source` / `page_url` / `readme_url` / `verified` …），**仓库内部是"平铺 JSON"还是"每插件一个目录"，客户端完全无感**：

| | 旧布局 | 新布局 |
|---|---|---|
| 仓库里 | `plugins/PublicIP.json` | `plugins/public_ip_info/plugin.json` |
| `download_url` | `.../main/plugins/PublicIP.json` | `.../main/plugins/public_ip_info/plugin.json` |
| 客户端本地落盘 | `resources/plugins/{id}.json` | `resources/plugins/{id}.json`（**不变**） |
| 客户端代码改动 | — | **仅 `MarketPluginEntry` 加 7 个字段 + 市场页消费**（见 §3.3） |

> 客户端落盘仍用 `{id}.json`（旧计划 §3.2 的 L1 机制），与仓库的目录式布局**解耦**。客户端只做 `Directory.GetFiles(dir, "*.json")` 非递归扫描，不会误读仓库式的子目录。

---

## 2. 现状盘点（实测数据，2026-10-20）

### 2.1 仓库现状

| 项 | 状态 |
|---|---|
| 仓库 | `mojingjing-git/awesome-litemonitor-plugins`，public，创建于 2026-10-19，默认分支 `main` |
| `plugins/` | 13 个插件 JSON，**平铺** |
| `index.json` | ❌ **不存在**（HTTP 404）—— 这是市场不可用的唯一直接原因 |
| `.github/workflows/` | ❌ 不存在 |
| `README.md` | ❌ 不存在 |
| GitHub Pages | ❌ **未启用**（`has_pages: false`）—— D1 下不需要启用 |
| 目录 / 话题 | 已设置 `awesome-list` `hardware-monitor` `litemonitor` `plugin` `windows` |

### 2.2 十三个插件的实测盘点

| 原文件名 | id | 需改目录 | `meta.name_en` | `meta.description_en` | `outputs.label_en` | version | author |
|---|---|---|---|---|---|---|---|
| BluetoothBattery.json | `bluetooth_battery` | **是** | ✓ | ✓ | ✗ | 1.4.1 | WorkBuddy |
| DeepSeekBalance.json | `deepseek_balance` | **是** | ✓ | ✓ | ✗ | 1.0 | shamough1792 |
| DirectPublicIP.json | `DirectPublicIP` | 是 | ✓ | ✓ | ✓ | 1.0 | *(空)* |
| FanSpeed.json | `fan_speed` | **是** | ✓ | ✓ | ✓ | 1.0 | LiteMonitor |
| Gold.json | `gold_price` | **是** | ✓ | ✓ | ✓ | 1.0 | JimmyK1m |
| LiteMonitor_Watercooler_API.json | `watercooler_manager_local_api_full` | **是** | ✓ | ✓ | ✓ | 1.3 | OpenAI |
| MiMoToken.json | `MiMoToken` | 是 | ✓ | ✓ | ✓ | 1.0.0 | wolfjkd |
| MiniMaxPlan.json | `minimax_plan` | **是** | ✓ | ✓ | ✗ | 1.0 | User |
| ProxyPublicIP.json | `proxy_public_ip` | **是** | ✓ | ✓ | ✓ | 1.0 | *(空)* |
| PublicIP.json | `public_ip_info` | **是** | ✓ | ✓ | ✓ | 1.0 | LiteMonitor |
| TokenTracker.json | `TokenTracker` | 是 | ✓ | ✓ | ✗ | 1.0.0 | Codex |
| WirelessBattery.json | `WirelessBattery` | 是 | ✓ | ✓ | ✗ | 1.0 | Antigravity |
| XinzhiWeather.json | `XinzhiWeather` | 是 | ✓ | ✓ | ✓ | 1.0 | DT9025A |

**汇总**：

| 指标 | 数值 | 说明 |
|---|---|---|
| 文件名与 id 不符 | **8 / 13** | 平铺布局下，`PublicIP.json` 与内置 `PublicIP.json` **同名**，是旧计划缺陷 B 的直接来源 |
| `meta.name_en` 齐备 | **13 / 13** | 双语元数据完整，客户端改一行即可生效 |
| `meta.description_en` 齐备 | **13 / 13** | 同上 |
| `outputs.label_en` 齐备 | **8 / 13** | 缺 **5 个插件 / 共 12 个 output 字段** → 英文界面下这 5 个的监控项标签仍是中文 |
| id 撞内置名单 | **0 / 13** | ✓ 内置保护名单不会误伤现有插件 |
| `meta.author` 为空 | 2 个 | `DirectPublicIP` / `ProxyPublicIP` |
| `meta.author` 疑似 AI 生成 | 3 个 | `minimax_plan`="User"、`TokenTracker`="Codex"、`watercooler_manager_local_api_full`="OpenAI" |

**`outputs.label_en` 缺失明细（实测，共 5 个插件 / 12 个字段）**：

| 插件 | outputs 总数 | 缺 `label_en` 的 output key |
|---|---|---|
| `bluetooth_battery` | 7 | `dev1` `dev2` `dev3` `dev4` `dev5` `dev6`（6 个） |
| `deepseek_balance` | 2 | `bal_cny` `bal_usd`（2 个） |
| `minimax_plan` | 3 | `interval`（1 个） |
| `TokenTracker` | 5 | `main`（1 个） |
| `WirelessBattery` | 3 | `mouse` `headset`（2 个） |
| 其余 8 个 | — | 0 ✅ |

**双语的完整缺口（实测，比 `label_en` 大得多）**：

| 字段 | 非空总数 | 缺失数 | 涉及插件 |
|---|---|---|---|
| `outputs[].label_en` | — | 12 | 5 个 |
| **`outputs[].short_label_en`** | **45** | **31** | **10 个** |
| **`inputs[].label_en`** | **13** | **4** | `deepseek_balance` `MiMoToken` `minimax_plan` `XinzhiWeather` |
| `display` 段 | **0 / 13 个插件有** | — | 全部没有 |

| 插件 | 缺 `short_label_en` 的 output key | 缺 `label_en` 的 input key |
|---|---|---|
| `bluetooth_battery` | `dev1`~`dev6` `cnt`（7 个） | — |
| `deepseek_balance` | `bal_cny` `bal_usd` | `api_key` |
| `DirectPublicIP` | `direct_ip` `direct_addr` `direct_latency` | — |
| `fan_speed` | `cpu` `gpu` | — |
| `MiMoToken` | — | `cookies` |
| `minimax_plan` | `interval` `weekly` `reset` | `api_key` |
| `proxy_public_ip` | `proxy_ip` `proxy_addr` `proxy_latency` | — |
| `public_ip_info` | `direct_ip` `direct_addr` `direct_latency` `proxy_ip` `proxy_addr` `proxy_latency` | — |
| `TokenTracker` | `main` | — |
| `WirelessBattery` | `mouse` `headset` `combined` | — |
| `XinzhiWeather` | `temp` | `api_key` |

> **`short_label` 才是任务栏上真正显示给用户的那行字** —— 缺 31 个意味着英文界面下**任务栏简写几乎全是中文**。这比 `label_en` 的影响更大，必须一并补齐。
>
> **`display` 段 13 个插件全都没有** → 客户端**不要**给 `PluginDisplay` 加 `_en` 字段（纯死字段，YAGNI）。见客户端计划 Task 5.1。

### 2.3 内置插件 id 名单（客户端与仓库需保持一致）

```
Bilibili, Crypto, Exchange, GitHub, Hitokoto, Holiday,
ProxyLatency, PublicIP, Stock, UniversalAPI, Weather, WeiboHot
```

> 这份名单在**客户端 C#**（`PluginConstants.BuiltinPluginIds`）与**仓库 Python**（`gen_index.py` 的 `BUILTIN_IDS`）各存一份。跨仓库无法共享，只能靠**两处互相注释 + CI 拦截**。新增内置插件时两处必须同时改。

---

## 3. 方案设计

### 3.1 仓库目录结构（改造后）

```
awesome-litemonitor-plugins/
├── plugins/
│   ├── bluetooth_battery/
│   │   ├── plugin.json      ← ① 插件本体（唯一权威的机器可读配置，必需）
│   │   ├── README.md        ← ② 介绍（GitHub 自动渲染在目录页上，必需）
│   │   └── verified         ← ③ 审核标记（可选，空文件，存在即 verified: true）
│   ├── deepseek_balance/
│   │   ├── plugin.json
│   │   ├── README.md
│   │   └── verified
│   ├── DirectPublicIP/
│   │   ├── plugin.json
│   │   └── README.md
│   ├── ...（共 13 个）
│   └── XinzhiWeather/
│       ├── plugin.json
│       └── README.md
├── scripts/
│   └── gen_index.py         ← 校验 + 聚合（本地与 CI 共用）
├── .github/
│   ├── workflows/
│   │   └── gen-index.yml     ← PR 校验 + main 上发布索引
│   └── PULL_REQUEST_TEMPLATE.md   ← PR 模板
├── index.json               ← ★ CI 自动生成，禁止手改
├── PLUGINS.md               ← ★ CI 自动生成：插件清单（根 README 引用它）
├── README.md                ← 手写：仓库定位、规则、贡献流程
└── LICENSE
```

**为什么用"每插件一个目录"而不是继续平铺**：

| 收益 | 说明 |
|---|---|
| **文件名冲突根除** | 目录名 == id，`public_ip_info/plugin.json` 与 `PublicIP/plugin.json` 天然不冲突，**不再有"覆盖内置插件"的可能**（仓库侧彻底解决，客户端侧的 L1/L2/L3 防护仍作为纵深防御保留） |
| **介绍有位置放** | `README.md` 放在目录里 → GitHub **自动渲染在目录页顶部**，这正是 D1 方案 A 的落地方式 |
| **插件可自带资源** | 未来要加截图、图标、LICENSE，`plugins/<id>/` 里直接放，不用改仓库结构 |
| **PR 粒度天然** | 一个插件一个目录 = 一个 PR 一个插件，review 边界清晰 |

### 3.2 插件页的构成（方案 A 的实际形态）

插件页 URL：`https://github.com/mojingjing-git/awesome-litemonitor-plugins/tree/main/plugins/<id>`

GitHub 渲染该目录页时会呈现：

```
┌─────────────────────────────────────────────────────────┐
│  plugins / public_ip_info                     [编辑] [删除]│
├─────────────────────────────────────────────────────────┤
│  public_ip_info/README.md          ← GitHub 自动渲染     │
│  ──────────────────────────────────────────────────────  │
│  # 公网IP显示 (Public IP)                                  │
│  一句话简介……                                             │
│  ## 配置项                                                 │
│  ## 输出项                                                 │
│  ## 踩坑提示                                               │
│  ──────────────────────────────────────────────────────  │
│  public_ip_info/plugin.json  ← ① 插件本体（可点开看源码） │
└─────────────────────────────────────────────────────────┘
```

**两要素映射**：

| 用户要的 | 落在哪 |
|---|---|
| ① 插件本体 | `plugins/<id>/plugin.json`（目录页上的文件条目，可点开看全文） |
| ② 介绍 | `plugins/<id>/README.md`（目录页顶部自动渲染） |

> **GitHub 的 README 自动渲染行为**：目录下的 `README.md` 会被渲染在文件列表**上方**。这是 GitHub 的既定行为，无需任何配置或 CI。**这正是方案 A 零成本实现"每插件一页"的关键。**

**`README.md` 的必备小节**（CI 校验，见 §3.4）：

| 小节 | 内容要求 | 门禁级别 |
|---|---|---|
| `# 标题` | 用插件的 `meta.name`（可附英文名） | 必需 |
| `## 功能说明` | 1-3 句说明这个插件监控什么、解决什么问题 | **错误** |
| `## 配置项` | `inputs` 的表格：参数名 / 说明 / 默认值 / 是否必填 | **条件错误**（`inputs` 为空时降为警告） |
| `## 输出项` | `outputs` 的表格：key / 显示标签 / 单位 | **错误** |
| `## 注意事项` | 踩坑提示：限流、API Key 申请、区域限制、已知问题等 | **错误** |
| `## 使用说明` | 装上后去哪启用、怎么填参数 | 警告（建议有） |

> **级别口径以 §3.4 为准**（那张表是校验规则的可执行来源）。`## 使用说明` 是建议项；`## 配置项` 在该插件 `inputs` 为空时是建议项。

> **为什么强制"踩坑提示"**：仓库 description 写的是"收录并实机验证第三方插件，附一键安装与**踩坑提示**"。这是本仓库相对官方仓库的核心价值 —— **每个插件的坑都省得下一个人踩一遍**。把它列为必备小节，是让这个价值不随插件增多而稀释。

### 3.3 `index.json` Schema（客户端与仓库的唯一契约）

**这是整个改造里最重要的一份约定**。客户端只读这个文件。

```jsonc
{
  "schema": 1,                                   // 索引格式版本
  "updated_at": "2026-10-20T05:00:00Z",          // 生成时间 (ISO8601 UTC)
  "repo": "mojingjing-git/awesome-litemonitor-plugins",
  "total": 13,
  "plugins": [
    {
      "id": "public_ip_info",                    // 逻辑主键；== 目录名；客户端据此决定落盘文件名
      "name": "公网IP显示",                       // 中文名
      "name_en": "Public IP",                    // 英文名
      "description": "公网IP插件，获取当前网络的公网IPv4地址",  // ★ 市场列表显示的简介
      "description_en": "Public IP plugin, gets the public IPv4 addresses of the current network",
      "author": "LiteMonitor",
      "version": "1.0",
      "file": "plugin.json",                     // 相对 plugins/<id>/ 的文件名（恒为 plugin.json）
      "download_url": "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/public_ip_info/plugin.json",
      "readme_url": "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/public_ip_info/README.md",
      "page_url": "https://github.com/mojingjing-git/awesome-litemonitor-plugins/tree/main/plugins/public_ip_info",
      "source": "https://github.com/mojingjing-git/awesome-litemonitor-plugins/blob/main/plugins/public_ip_info/plugin.json",  // ★ 源码链接，客户端 MarketPluginEntry.Source 读它（不可删）
      "sha256": "3f786850e387550fdab836ed7e6dc881de23001b…",  // plugin.json 内容的 SHA-256
      "size": 4148,                              // plugin.json 字节数
      "status": "ok",                            // ok | needs-service | needs-code —— 来自 meta.status，缺省 "ok"
      "requires": [],                            // 额外准备要求 —— 来自 meta.requires，缺省 []
      "note": "",                                // 自由备注 —— 来自 meta.note，缺省 ""（如"该插件需要自建 API 服务"）
      "verified": false,                         // ★ 是否经审核 Agent 实机验证（来自 plugins/<id>/verified 标记文件，见 §3.5）
      "original_id": "",                         // 若曾改名则记录原 id（本仓库从未改名，恒为空）
      "renamed": false                           // 若曾改名则为 true（本仓库从未改名，恒为 false）
    }
  ]
}
```

**与旧计划 §3.2 的差异**：

| 字段 | 旧计划 | 本计划 | 理由 |
|---|---|---|---|
| `file` | `id + ".json"` | `"plugin.json"` | 目录化后文件名恒定，`file` 变成"相对路径" |
| `id` 约束 | `^[A-Za-z0-9_-]{1,64}$` | 同 | 目录名 == id，必须合法（客户端改用 `\A..\z`，见旧计划 Task 3.2） |
| `source` | 存在 | **保留，值改为目录式路径** | 客户端市场页的「查看源码」链接读 `entry.Source`（`PluginMarketPage.cs:200-204`）。**删掉它 = 市场页一条链接都不剩** |
| 新增 | — | `readme_url` / `page_url` | 软件侧可据此做「查看介绍 / 查看源码」跳转 |
| 新增 | — | `verified` | 开放 PR 后的可信度标记（§3.5），**客户端需消费** |

**`status` / `requires` / `note` 三个字段的来源**：

这三个字段 13 个插件**都没有**（实测），因此索引里恒为默认值。`gen_index.py` 的取值规则必须写死：

```python
entry["status"]   = meta.get("status", "ok")     # 缺省 "ok"
entry["requires"] = meta.get("requires", [])     # 缺省空数组
entry["note"]     = meta.get("note", "")         # 缺省空串
```

> 也就是说：**这三个是 `meta` 段的可选扩展字段**，插件不写就是默认值。CI 不必强制它们存在（有则校验类型即可），但**取值的来源要写明**，否则后来人不知道该从哪读。

**`sha256` 的计算口径（必须与客户端一致）**：

- **仓库侧**：对 `plugin.json` 的**原始文件字节**计算 SHA-256。
- **客户端**：对 **HTTP 响应的原始字节**计算 SHA-256（**不是** `GetStringAsync` 拿到的字符串重新编码后的字节）。
- **为什么强调**：如果客户端用字符串再编码，文件带 BOM 或服务端 charset 非 UTF-8 时，字节不一致 → **误报"校验失败"**。当前 13 个文件都是无 BOM UTF-8，实践中不会触发，但**口径必须写死**，否则将来某个插件带 BOM 就会炸。
- 客户端实现要点：改用 `HttpClient.GetByteArrayAsync` 或 `GetAsync` + `ReadAsByteArrayAsync`，再 `Encoding.UTF8.GetString(bytes)` 得到内容、`SHA256.HashData(bytes)` 得到哈希。

**客户端兼容性**：

`MarketPluginEntry` 现有 13 个 JSON 字段 —— `id` / `original_id` / `renamed` / `name` / `description` / `author` / `version` / `file` / `download_url` / `status` / `requires` / `note` / `source` —— **本 schema 全部保留且名字一致**，逐字段对得上。

需要**客户端新增**的字段（旧计划 Task 3.1 / Task 5.1 一次性补齐，别分两次）：

| 字段 | 用途 | 旧计划对应任务 |
|---|---|---|
| `name_en` | 英文名，市场页标题 | Task 5.1 |
| `description_en` | 英文简介，市场页描述 | Task 5.1 |
| `sha256` | 下载完整性校验 | Task 3.2 |
| `size` | 下载体积展示（可选） | Task 3.1 |
| `verified` | 「未经验证」徽标（§3.5） | **新增，见 §3.5 与旧计划 Task 4.3** |
| `page_url` / `readme_url` | 「查看详情 / 查看介绍」跳转 | **新增，见 §3.5 与旧计划 Task 4.3** |

> ⚠️ **推翻了"旧计划的客户端 Task 2-8 一行都不用改"这个结论**。客户端**确实需要**在 `MarketPluginEntry` 上加 **7 个**字段（`name_en` / `description_en` / `sha256` / `size` / `verified` / `page_url` / `readme_url`），并在市场页消费它们。除这一处外，客户端其余 Task 2-8 **不受仓库布局变更影响**。

### 3.4 CI 三段式

一个 workflow，三个阶段，**按触发条件分流**：

```
┌────────────────────────────────────────────────────────────────────┐
│ 触发: PR 改 plugins/** 或 scripts/**                                │
│   → job: validate                                                   │
│       跑 gen_index.py --check                                       │
│       校验失败 → ❌ PR 变红，评论里列出具体问题                       │
│       校验通过 → ✅ PR 变绿，评论里列出各插件检查摘要                 │
│       ★ 不写任何文件，不需要 contents:write 权限                     │
└────────────────────────────────────────────────────────────────────┘
┌────────────────────────────────────────────────────────────────────┐
│ 触发: push 到 main                                                  │
│   → job: validate（同上）                                           │
│   → job: publish-index（依赖 validate 通过）                        │
│       跑 gen_index.py  →  生成 index.json + PLUGINS.md              │
│       git commit & push 到 main                                     │
│       ★ 需要 contents:write                                         │
└────────────────────────────────────────────────────────────────────┘
```

#### `gen_index.py` 的校验规则

**目录级**：

| 规则 | 级别 | 失败信息 |
|---|---|---|
| `plugins/` 下每个子目录名匹配 `^[A-Za-z0-9_-]{1,64}$` | **错误** | 目录名非法：`<dir>` |
| 目录名不在 `BUILTIN_IDS` 中 | **错误** | `<dir>` 与内置插件 id 冲突 |
| 目录内存在 `plugin.json` | **错误** | `<dir>` 缺少 plugin.json |
| 目录内存在 `README.md` | **错误** | `<dir>` 缺少 README.md（介绍是页面的必需部分） |
| 目录名全局唯一 | **错误** | 目录名重复：`<dir>` |

**插件级**（读 `plugin.json`）：

| 规则 | 级别 | 失败信息 |
|---|---|---|
| 是合法 JSON | **错误** | `<dir>` 的 plugin.json 不是合法 JSON |
| `id` 字段非空 | **错误** | `<dir>` 缺少 id |
| `id` == 目录名 | **错误** | `<dir>` 的 id 是 `<id>`，与目录名不一致 |
| `meta.name` 非空 | **错误** | `<dir>` 缺少 meta.name |
| `meta.description` 非空 | **错误** | `<dir>` 缺少 meta.description |
| `meta.author` 非空 | **错误** | `<dir>` 缺少 meta.author（空会被填 "未知作者"） |
| `meta.version` 非空 | **错误** | `<dir>` 缺少 meta.version |
| `meta.name_en` 非空 | **警告** | `<dir>` 缺少 meta.name_en（英文界面会回退中文） |
| `meta.description_en` 非空 | **警告** | 同上 |
| 每个 `outputs[].label_en` 非空 | **警告** | `<dir>` 的 output `<key>` 缺少 label_en |
| 每个**非空** `outputs[].short_label_en` 非空 | **警告** | 任务栏简写缺英文（影响比 label_en 更大 —— 实测 45 个非空 short_label 缺 31 个） |
| 每个 `inputs[].label_en` 非空 | **警告** | 设置页的输入项标签缺英文（实测 13 个 input 缺 4 个） |

> **警告不阻塞合并**：双语字段缺失不该卡住一个功能完好的插件，只是英文用户看到中文。但**错误一律阻塞**。
>
> **现存的缺口在本次改造中一并补齐**（既然要写 README，顺手补双语成本极低）：
>
> | 类别 | 缺失量 | 涉及插件 |
> |---|---|---|
> | `outputs[].label_en` | 12 个字段 | 5 个 |
> | `outputs[].short_label_en` | **31 个字段** | **10 个** |
> | `inputs[].label_en` | 4 个字段 | 4 个 |
>
> 明细见 §2.2。

**README 级**（介绍质量门禁）：

| 规则 | 级别 | 说明 |
|---|---|---|
| 含 `## 功能说明` | **错误** | 介绍必须有功能说明 |
| 含 `## 配置项` | **条件错误** | **仅当该插件 `inputs` 非空时**才是错误；`inputs` 为空时降为**警告**（实测有 5 个插件 `inputs` 为空：`DirectPublicIP` / `FanSpeed` / `ProxyPublicIP` / `public_ip_info` / `WirelessBattery` —— 强行要求它们写配置项表，等于逼贡献者写废话，与「PR 友好」目标相悖） |
| 含 `## 输出项` | **错误** | 同上（实测 13 个插件 `outputs` 均非空；若某插件确实无输出，则降为警告） |
| 含 `## 注意事项` | **错误** | 同上（踩坑提示是本仓库的核心价值） |
| 含 `## 使用说明` | **警告** | 建议有 |

> **条件化规则的实现**：`gen_index.py` 读插件的 `inputs` / `outputs` 数组长度，为 0 时把对应小节的级别从「错误」降为「警告」。这样门禁既不放过"介绍写得太敷衍"，也不为难本来就没参数的插件。

#### 输出产物

| 产物 | 生成方式 | 提交者 |
|---|---|---|
| `index.json` | 脚本生成 | CI |
| `PLUGINS.md` | 脚本生成（插件清单表格：名称 / 简介 / 作者 / 版本 / 链接） | CI |

> `PLUGINS.md` 自动生成，根 `README.md` 里链接它。这样**插件清单永远与实际目录同步**，不需要手工维护。

### 3.5 开放 PR 后的上架与可信度

**开放 PR 是有意保留的决策**（用户明确确认：「还是保留 PR 吧，到时候派 Agent 去审核并发布」）。

**合并即上架**：PR 合并到 `main` → CI 生成 `index.json` → 客户端下次拉索引就能看到新插件。

#### 3.5.1 审核流程（派 Agent 审核并发布）

```
贡献者提 PR
   │
   ├─ CI validate（自动）：格式校验，不合规 → PR 变红
   │
   ▼
审核 Agent 接手（用户派发）
   │
   ├─ 读取 PR diff：plugin.json 的字段完整性、URL 是否合理、extract 路径是否对得上
   ├─ 实机验证：装到 LiteMonitor++ 里跑一遍，确认数据能正常显示
   ├─ 检查安全：是否请求了可疑域名、是否索取不必要的凭据
   │
   ▼
审核通过 → Agent 创建 plugins/<id>/verified 标记文件 → 合并 PR
   │
   ▼
CI publish-index → index.json 带上 verified: true → 客户端可见「已验证」徽标
```

**为什么让 Agent 审核而不是人肉看 diff**：插件的核心风险是「看起来对、跑起来是 `[ERROR]`」或「悄悄把请求发到奇怪的地方」。这两类**只有实机跑一遍才能发现**，正好是 Agent 擅长的（读代码 + 跑程序 + 报结论）。

**Agent 的审核结论要落成仓库里可见的东西**，而不是只在对话里说 —— 这就是 `verified` 标记文件的意义。

#### 3.5.2 `verified` 标记文件（解决"禁止手改"与"必须手改"的矛盾）

**问题**：`index.json` 是 CI 产物，标记"禁止手改"；但 `verified` 又需要人工设置 —— 若手改 `index.json`，该提交**不匹配** workflow 的 `paths: ['plugins/**', 'scripts/**']` 过滤，CI 根本不会触发，所谓"CI 重跑后标记仍在"是假的。

**解法**：把人工标记移出 CI 产物。

```
plugins/
  public_ip_info/
    plugin.json      ← CI 读
    README.md        ← CI 读
    verified         ← ★ 人工维护：空标记文件，存在即 verified: true
```

- `gen_index.py` 生成索引时，**只读** `plugins/<id>/verified`：存在 → `verified: true`，不存在 → `false`。
- `gen_index.py` **永不写** `verified` 文件，也**永不覆盖**人工维护的标记。
- `index.json` 保持**纯 CI 产物、禁止手改**这一不变式。
- 审核 Agent 通过后，只需 `touch plugins/<id>/verified` 并提交，CI 重跑（`plugins/**` 变更会触发）时自动带上 `verified: true`。

> **顺带的好处**：`verified` 标记文件**随插件目录一起可见** —— 在 GitHub 上打开插件目录页，能直接看到"这个已被审核验证过"，不需要去翻索引。

#### 3.5.3 客户端展示

| `verified` | 含义 | 市场页展示 |
|---|---|---|
| `false`（默认） | 未经审核验证 | 显示「未经验证」徽标（黄色）+ 提示 |
| `true` | 审核 Agent 已实机跑过，数据正常 | 无徽标（视为可信） |

- **新插件一律 `false`** —— 这是正确的默认值。
- 客户端市场页对 `verified == false` 的插件显示提示：**「该插件由社区贡献，尚未经验证」**。
- 提示文案必须走 i18n（新增语言 key `Menu.PluginMarketUnverified` / `Menu.PluginMarketUnverifiedHint`），否则英文界面下混中文。

**这解决了"用户装了个坏插件"的信任问题**：用户能区分"已审核收录"与"社区投的"，出问题也知道找谁。

### 3.6 客户端契约边界（明确谁负责什么）

| 事项 | 仓库侧 | 客户端侧 |
|---|---|---|
| 插件 id 的**取值** | 决定（目录名） | 不关心，只当作不透明字符串 |
| 插件 id 的**合法性** | CI 校验 | 旧计划 L1 再次校验（纵深防御） |
| 插件的**机器可读配置** | `plugin.json` | `PluginTemplate` 解析（`PluginModels.cs`） |
| 插件的**人类可读介绍** | `README.md` | **不读**（软件侧只用 `index.json` 里的 `description`） |
| 插件的**本地落盘** | 不关心 | `resources/plugins/{id}.json`（旧计划 L1） |
| 插件的**下载** | 提供 `download_url` | 下载 + sha256 校验（旧计划 Task 3） |
| 插件的**介绍跳转** | 提供 `page_url` / `readme_url` | 市场页渲染成可点击链接 |
| 插件的**双语文案** | `plugin.json` 的 `_en` 字段 | 按界面语言选词（旧计划 Task 5） |

> **边界原则**：仓库负责"内容正确且格式合规"，客户端负责"安全地取用内容"。两边都做校验是**有意的纵深防御**，不是重复。

---

## 4. File Structure

### 4.1 仓库 —— 新建

| 路径 | 用途 |
|---|---|
| `scripts/gen_index.py` | 校验 + 聚合；支持 `--check`（只校验不写文件）与 `--fix-readme`（生成 README 初稿） |
| `scripts/init_readme.py` | 从 `plugin.json` 生成 `README.md` 初稿模板（可选，见 Task 1.3） |
| `.github/workflows/gen-index.yml` | PR 校验 + main 发布索引 |
| `.github/PULL_REQUEST_TEMPLATE.md` | PR 模板：勾选清单（已填 id / 已在本地测试 / 已写介绍 / 作者署名） |
| `PLUGINS.md` | **CI 生成**：插件清单 |
| `README.md` | 手写：仓库定位、规则、贡献流程、PR 注意事项 |
| `LICENSE` | 建议补 MIT（与主项目一致） |
| `plugins/<id>/README.md` × 13 | 每个插件的介绍 |
| `plugins/<id>/verified` | **审核标记**（空文件）：审核 Agent 实机验证通过后创建，存在即 `verified: true`（§3.5.2） |
| `.gitignore` | **新建**：忽略 `validation-report.md`（脚本的本地产物，避免开发者 `git add -A` 时误提交） |

### 4.2 仓库 —— 移动 / 修改

| 原路径 | 新路径 | 操作 |
|---|---|---|
| `plugins/BluetoothBattery.json` | `plugins/bluetooth_battery/plugin.json` | 移动 |
| `plugins/DeepSeekBalance.json` | `plugins/deepseek_balance/plugin.json` | 移动 |
| `plugins/DirectPublicIP.json` | `plugins/DirectPublicIP/plugin.json` | 移动 |
| `plugins/FanSpeed.json` | `plugins/fan_speed/plugin.json` | 移动 |
| `plugins/Gold.json` | `plugins/gold_price/plugin.json` | 移动 |
| `plugins/LiteMonitor_Watercooler_API.json` | `plugins/watercooler_manager_local_api_full/plugin.json` | 移动 |
| `plugins/MiMoToken.json` | `plugins/MiMoToken/plugin.json` | 移动 |
| `plugins/MiniMaxPlan.json` | `plugins/minimax_plan/plugin.json` | 移动 |
| `plugins/ProxyPublicIP.json` | `plugins/proxy_public_ip/plugin.json` | 移动 |
| `plugins/PublicIP.json` | `plugins/public_ip_info/plugin.json` | 移动 |
| `plugins/TokenTracker.json` | `plugins/TokenTracker/plugin.json` | 移动 |
| `plugins/WirelessBattery.json` | `plugins/WirelessBattery/plugin.json` | 移动 |
| `plugins/XinzhiWeather.json` | `plugins/XinzhiWeather/plugin.json` | 移动 |
| `plugins/*/plugin.json` | 同路径 | 补 4 处 `outputs[].label_en`、2 处 `meta.author` |
| `plugins/*/plugin.json` | 同路径 | 视情况补 `meta.note` / `meta.requires` / `status` |

### 4.3 客户端 —— **无改动**

本计划**不修改客户端的既有逻辑**，只要求客户端 `MarketPluginEntry` **新增 7 个字段**并消费它们（详见 §3.3 与 Task 6.2）。旧计划 `2026-10-20-plugin-market-hardening.md` 的 Task 2-8 照常执行。

> **§4.3 表格的旧版描述已作废**：本计划初稿曾写"旧计划 §3.2 写的是 `file` 必须等于 `id + ".json"`，Task 6 会去改那一行" —— **该处已由作者在编写本计划时同步改完**（旧计划 §3.2 约束表现为"`file` 恒为 `plugin.json`"）。**不要再改一遍**，逐项清单见 Task 6.1。

---

## 5. Task 分解

> 每个 Task 结束时仓库状态必须自洽（`gen_index.py --check` 通过）。

### Task 1：仓库目录重组

#### 1.1 Clone 与建分支

```powershell
git clone https://github.com/mojingjing-git/awesome-litemonitor-plugins.git G:\TOOL\awesome-litemonitor-plugins
cd G:\TOOL\awesome-litemonitor-plugins
git checkout -b refactor/dir-layout
```

> 目录与 `LiteMonitor-fork` 平级，**独立 git 仓库**，不污染主仓库状态。

#### 1.2 脚本化重组（避免手工漏项）

写一次性脚本，按 `plugin.json` 里的 `id` 建目录并移动：

```python
import json, pathlib, shutil
root = pathlib.Path("plugins")
for f in sorted(root.glob("*.json")):
    data = json.loads(f.read_text(encoding="utf-8"))
    d = root / data["id"]
    d.mkdir(exist_ok=True)
    shutil.move(str(f), str(d / "plugin.json"))
    print(f"{f.name} -> {d.relative_to(root.parent)}/plugin.json")
```

**预期输出（8 个需要移动到新目录，5 个只是改目录名）**：

```
BluetoothBattery.json           -> plugins/bluetooth_battery/plugin.json
DeepSeekBalance.json            -> plugins/deepseek_balance/plugin.json
DirectPublicIP.json             -> plugins/DirectPublicIP/plugin.json
FanSpeed.json                   -> plugins/fan_speed/plugin.json
Gold.json                       -> plugins/gold_price/plugin.json
LiteMonitor_Watercooler_API.json-> plugins/watercooler_manager_local_api_full/plugin.json
MiMoToken.json                  -> plugins/MiMoToken/plugin.json
MiniMaxPlan.json                -> plugins/minimax_plan/plugin.json
ProxyPublicIP.json              -> plugins/proxy_public_ip/plugin.json
PublicIP.json                   -> plugins/public_ip_info/plugin.json
TokenTracker.json               -> plugins/TokenTracker/plugin.json
WirelessBattery.json            -> plugins/WirelessBattery/plugin.json
XinzhiWeather.json              -> plugins/XinzhiWeather/plugin.json
```

#### 1.3 生成 README.md 初稿

写 `scripts/init_readme.py`，从 `plugin.json` 抽取信息生成初稿模板：

```markdown
# {meta.name}（{meta.name_en}）

> {meta.description}

## 功能说明

<!-- TODO: 用 1-3 句说明这个插件监控什么、解决什么问题 -->

## 配置项

| 参数 | 说明 | 默认值 | 必填 |
|---|---|---|---|
| `{input.key}` | {input.label} | `{input.default}` | {是/否} |

## 输出项

| key | 显示标签 | 单位 |
|---|---|---|
| `{output.key}` | {output.label} | {output.unit} |

## 使用说明

1. 在 LiteMonitor++ 的「设置 → 插件市场」中搜索并安装本插件
2. 在「设置 → 插件管理」中启用，并填写上方配置项

## 注意事项

<!-- TODO: 踩坑提示 —— 限流 / API Key 申请 / 区域限制 / 已知问题 -->
```

> **初稿是骨架，不是成品**。每篇 README 的「功能说明」与「注意事项」必须**人工撰写** —— 这两节是仓库的核心价值，脚本生成不了。`init_readme.py` 只负责消除重复劳动。

#### 1.4 补齐字段缺口

| 插件 | 补什么 |
|---|---|
| `bluetooth_battery` | `outputs[].label_en` × 6（`dev1`~`dev6`）+ `outputs[].short_label_en` × 7 |
| `deepseek_balance` | `outputs[].label_en` × 2（`bal_cny` `bal_usd`）+ `short_label_en` × 2 + `inputs[].label_en`（`api_key`） |
| `minimax_plan` | `outputs[].label_en` × 1（`interval`）+ `short_label_en` × 3（`interval` `weekly` `reset`）+ `inputs[].label_en`（`api_key`） |
| `TokenTracker` | `outputs[].label_en` × 1（`main`）+ `short_label_en` × 1（`main`） |
| `WirelessBattery` | `outputs[].label_en` × 2（`mouse` `headset`）+ `short_label_en` × 3（`mouse` `headset` `combined`） |
| `DirectPublicIP` | `outputs[].short_label_en` × 3 |
| `fan_speed` | `outputs[].short_label_en` × 2（`cpu` `gpu`） |
| `MiMoToken` | `inputs[].label_en`（`cookies`） |
| `proxy_public_ip` | `outputs[].short_label_en` × 3 |
| `public_ip_info` | `outputs[].short_label_en` × 6 |
| `XinzhiWeather` | `outputs[].short_label_en` × 1（`temp`）+ `inputs[].label_en`（`api_key`） |
| `DirectPublicIP` | `meta.author`（当前为空） |
| `ProxyPublicIP` | `meta.author`（当前为空） |
| `minimax_plan` | `meta.author`："User" → 真实作者 |
| `TokenTracker` | `meta.author`："Codex" → 真实作者 |
| `watercooler_manager_local_api_full` | `meta.author`："OpenAI" → 真实作者 |

> **author 字段的意义**：开放 PR 后，用户看到的是**谁写的这个插件**。`User` / `Codex` / `OpenAI` 这类占位符会让用户无法判断该不该信任。**必须填真实作者**（GitHub 用户名最佳）。

#### 1.5 验证

- [ ] `plugins/` 下是 13 个**目录**，每个含 `plugin.json` + `README.md`
- [ ] 目录名 == `plugin.json` 的 `id`（13/13）
- [ ] 13 篇 README 均含必备小节（`## 配置项` 对 `inputs` 为空的 5 个插件降为警告）
- [ ] 5 个插件 / 12 个缺失的 `label_en` 已补齐
- [ ] 5 个异常的 `author` 已修正
- [ ] 仓库根**不再有**平铺的 `*.json`

---

### Task 2：`gen_index.py` 校验 + 聚合

#### 2.1 结构

```python
#!/usr/bin/env python3
"""插件索引生成器 —— 校验 plugins/ 下的插件并生成 index.json 与 PLUGINS.md

用法:
    python3 scripts/gen_index.py            # 校验 + 生成并写入 index.json / PLUGINS.md
    python3 scripts/gen_index.py --check    # 只校验，不写文件（CI 的 PR 阶段用）
"""
```

#### 2.2 必须实现的函数

| 函数 | 职责 |
|---|---|
| `scan_plugins(root)` | 遍历 `plugins/*/`，返回 `(dir_name, plugin_dict, readme_text)` 列表 |
| `validate_all(plugins)` | 执行 §3.4 全部规则，返回 `(errors, warnings)` |
| `build_index(plugins)` | 生成 `MarketIndex` 字典（含 `download_url` / `readme_url` / `page_url` / `sha256`） |
| `write_plugins_md(index)` | 生成 `PLUGINS.md` 清单表格 |
| `main()` | 解析 `--check`，编排上述流程，打印人类可读的报告 |

#### 2.3 URL 拼接

```python
REPO   = "mojingjing-git/awesome-litemonitor-plugins"
BRANCH = "main"
RAW    = f"https://raw.githubusercontent.com/{REPO}/{BRANCH}"
PAGE   = f"https://github.com/{REPO}/tree/{BRANCH}"

download_url = f"{RAW}/plugins/{plugin_id}/plugin.json"
readme_url   = f"{RAW}/plugins/{plugin_id}/README.md"
page_url     = f"{PAGE}/plugins/{plugin_id}"
source       = f"https://github.com/{REPO}/blob/{BRANCH}/plugins/{plugin_id}/plugin.json"  # ★ 客户端 MarketPluginEntry.Source 读它
```

> **`source` 不可省略**：客户端市场页的「查看源码」链接读 `entry.Source`（`PluginMarketPage.cs:200-204`）。索引里少了它，市场页上一条链接都不会剩。

> **分支名要跟着仓库实际默认分支走**。当前是 `main`；若将来改分支，只需改这一个常量。

#### 2.4 `verified` 的处理

`verified` **只读标记文件，不从 `index.json` 回读**：

```python
# plugins/<id>/verified 是人工维护的空标记文件，存在即 verified: true
verified = (plugin_dir / "verified").exists()
entry["verified"] = verified
```

> **为什么不用"从现有 index.json 回读"那种写法**：那种写法要求人工去手改 `index.json`，而 `index.json` 又是 CI 产物 —— 循环论证，且手改的提交不匹配 workflow 的 `paths` 过滤，CI 压根不会触发，标记会在下一次 `plugins/**` 变更时被重新生成覆盖。改用标记文件后：
> - `index.json` 保持**纯 CI 产物**这一不变式（禁止手改仍然成立）
> - 人工标记是**独立的、可在 GitHub 目录页直接看到**的文件
> - 审核 Agent 通过后 `touch plugins/<id>/verified` 即可，CI 重跑自动带上 `verified: true`
>
> 详见 §3.5.2。

#### 2.5 输出报告

校验结果**同时**写到 stdout 和一个报告文件（供 CI 上传成 artifact）：

```python
def write_report(path: str, text: str) -> None:
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
```

**命令行接口**：

| 参数 | 行为 |
|---|---|
| （无） | 校验 + 生成 `index.json` / `PLUGINS.md` + 写 `validation-report.md` |
| `--check` | **只校验**，不生成也不覆盖 `index.json` / `PLUGINS.md`；仍写 `validation-report.md` |
| `--report <path>` | 指定报告文件路径（默认 `validation-report.md`） |

> **为什么报告要落文件**：PR 上只有红色 ✗ 没有"哪个插件违反哪条规则"，等于让贡献者自己猜。报告作为 artifact 上传，PR 作者能直接看到问题清单。

**★ 报告写入必须包在 `try/finally` 里**：

```python
def main():
    report = []
    try:
        errors, warnings = validate_all(scan_plugins(ROOT))
        report.append(render_report(errors, warnings))
        if errors:
            write_report(args.report, "\n".join(report))
            sys.exit(1)
        # 生成 index.json / PLUGINS.md ...
        write_report(args.report, "\n".join(report))
    except Exception as e:
        # 脚本自身崩溃时也必须留下报告，否则 artifact 步骤会以
        # "找不到文件" 失败，而不是干净地报出"脚本崩了"
        write_report(args.report, f"## 脚本异常\n\n```\n{traceback.format_exc()}\n```")
        raise
```

> **为什么**：workflow 的 artifact 步骤用了 `if-no-files-found: error`。若脚本在写报告前抛异常，artifact 步骤会以"找不到文件"失败 —— 掩盖了真正的错误信息。

校验失败时，输出要**能直接指导修复**：

```
✗ 校验失败：3 个错误，2 个警告

错误：
  [plugins/foo] plugin.json 不是合法 JSON: Expecting value at line 1
  [plugins/bar] 目录名 'bar' 与 plugin.json 的 id 'baz' 不一致
  [plugins/qux] 缺少 README.md（介绍是插件页的必需部分）

警告：
  [plugins/qux] meta.name_en 为空，英文界面将回退中文
  [plugins/qux] output 'temp' 缺少 label_en

提示：运行 python3 scripts/init_readme.py 可生成 README.md 初稿
```

#### 2.6 验证

- [ ] `--check` 模式下**不写** `index.json` / `PLUGINS.md`（但**会**写 `validation-report.md`）
- [ ] `--check` 模式下 `validation-report.md` 确实被生成（否则 CI artifact 恒为空）
- [ ] 故意造一个 id 与目录名不一致的插件 → 报错并指明具体插件
- [ ] 故意造一个撞内置 id 的插件 → 报错
- [ ] 故意删掉一个 README.md → 报错
- [ ] 故意改坏 JSON → 报错
- [ ] 故意给一个 `inputs` 为空的插件删掉 `## 配置项` 小节 → **只警告不报错**（条件化规则生效）
- [ ] `index.json` 里的 `download_url` 能 200 下载，且下载内容 sha256 与索引一致
- [ ] `index.json` 里的 `source` / `page_url` / `readme_url` 均可访问
- [ ] 在某插件目录下 `touch verified` → 重新生成后该条 `verified: true`；删除该文件 → 变回 `false`

---

### Task 3：GitHub Actions Workflow

#### 3.1 `gen-index.yml`

```yaml
name: Plugin Index

on:
  pull_request:
    paths: ['plugins/**', 'scripts/**']
  push:
    branches: [main]
    paths: ['plugins/**', 'scripts/**']
  workflow_dispatch:

permissions:
  contents: read

# 同一分支的 workflow 串行执行：两次快速 push 时，第二个排队等第一个完成，
# 避免两个 job 同时 git push 导致 non-fast-forward。
# group 带 github.ref → PR 与 main 互不阻塞。
concurrency:
  group: plugin-index-${{ github.ref }}
  cancel-in-progress: false

jobs:
  validate:
    name: 校验插件
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-python@v5
        with:
          python-version: '3.x'
      - name: 校验 plugins/
        run: python3 scripts/gen_index.py --check --report validation-report.md
      - name: 上传校验报告
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: plugin-validation-report
          path: validation-report.md
          if-no-files-found: error

  publish-index:
    name: 发布索引
    runs-on: ubuntu-latest
    needs: validate
    if: github.event_name != 'pull_request'
    permissions:
      contents: write
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-python@v5
        with:
          python-version: '3.x'
      - name: 生成 index.json 与 PLUGINS.md
        run: python3 scripts/gen_index.py
      - name: 提交产物
        run: |
          git config user.name  "github-actions[bot]"
          git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
          git add index.json PLUGINS.md
          if git diff --staged --quiet; then
            echo "索引无变化，跳过提交"
          else
            git commit -m "chore(index): 自动重新生成插件索引"
            git pull --rebase origin main
            git push
          fi
```

#### 3.2 关键实现注意

| 注意 | 说明 |
|---|---|
| **`permissions` 分 job 声明** | 顶层用 `contents: read`，只有 `publish-index` job 提升到 `contents: write`。PR 阶段**不申请写权限**，安全性更好 |
| **`if: github.event_name != 'pull_request'`** | PR 阶段只跑 `validate`，不写索引。`workflow_dispatch` 时条件也成立 → 手动触发可重跑发布 |
| **`needs: validate`** | 校验不通过就不发布索引 |
| **`paths` 过滤** | 改 README 也会触发（README 在 `plugins/**` 下）—— 这是**对的**，README 变了要重新生成清单。同时 `index.json` / `PLUGINS.md` 在仓库根，**不匹配** `paths` → CI 自提交**不会自触发**，无无限循环 |
| **`concurrency` 组** | 同一分支的 workflow 串行，避免两次快速 push 时两个 job 同时 `git push` 导致 non-fast-forward。`cancel-in-progress: false` 保证排队而非互相取消 |
| **`git pull --rebase`** | 提交前 rebase 一次，兜住排队期间 main 被别人推了的情况 |
| **`verified` 标记** | 由 `gen_index.py` **只读** `plugins/<id>/verified` 标记文件实现（§2.4 / §3.5.2），workflow 无需特殊处理，也**不需要**把 `index.json` 加进 `paths` |
| **报告可见性** | 脚本用 `--report validation-report.md` 把报告**写到文件**（不是只打 stdout），再作为 artifact 上传。没有这一步，artifact 恒为空，PR 上只有一个红叉没有原因 |

#### 3.3 验证

- [ ] 推一个 PR → 只有 `validate` job 运行，PR 上出现 ✅/❌ 结论
- [ ] PR 里放一个不合规插件 → PR 变红，且 **artifact 里有报告**（含具体违规插件与规则）
- [ ] 合并到 main → `publish-index` 运行，`index.json` 与 `PLUGINS.md` 被自动提交
- [ ] 在 `plugins/<id>/` 下 `touch verified` 并提交 → CI 重跑后该条 `verified: true`
- [ ] 删除 `verified` 标记文件并提交 → CI 重跑后该条回到 `verified: false`
- [ ] 连续两次快速 push 到 main → 两次 workflow 串行执行，**都成功**（无 non-fast-forward 报错）

---

### Task 4：仓库根 `README.md` 与 `LICENSE`

#### 4.1 `README.md` 结构

```markdown
# Awesome LiteMonitor Plugins

> 社区维护的 LiteMonitor++ 插件索引与生态资源清单（非官方）。

## 这是什么

LiteMonitor++ 的插件仓库。每个插件一个目录，目录下的 `README.md` 就是插件介绍页，
`plugin.json` 是插件本体。软件内的「插件市场」从这里拉取索引，一键安装。

## 插件清单

见 [PLUGINS.md](./PLUGINS.md)（由 CI 自动生成，请勿手改）

## 目录结构

plugins/
  <插件 id>/
    plugin.json    插件本体（机器可读）
    README.md      插件介绍（人可读，渲染在目录页上）

## 贡献一个插件

1. Fork 本仓库
2. 在 `plugins/<你的插件 id>/` 下放入 `plugin.json` 与 `README.md`
3. 目录名必须与 `plugin.json` 里的 `id` 完全一致
4. 本地跑 `python3 scripts/gen_index.py --check` 确认通过
5. 提 PR，CI 会自动校验

### 命名规则

- `id`：只允许 `A-Za-z0-9-_`，**一经合并即不可更改**（它是用户的安装主键）
- `id` 不能与内置插件重名
- 目录名必须 == `id`

### plugin.json 必填字段

meta.name / meta.description / meta.author / meta.version / id

### README 必填小节

功能说明 / 输出项 / 注意事项（**必填**）

配置项（**本插件有 inputs 时必填**；无 inputs 的插件可省略）

使用说明（建议有）

## 插件安全说明

插件是**纯声明式 JSON 配置**，只声明「去哪取数、怎么提取、怎么显示」，
**不具备执行第三方脚本或代码的能力**。但插件会按配置发起网络请求，
可能把你的 IP 或填写的 API Key 发送给对应服务 —— 请只安装可信来源的插件。

## 免责声明

本仓库与 LiteMonitor++ 原作者 Diorser 无关联，插件由社区贡献，质量参差不齐，
请自行判断是否信任。
```

#### 4.2 `LICENSE`

建议补 MIT（与主项目一致），并在 README 里说明插件本身可能采用不同许可，由各插件作者声明。

#### 4.3 验证

- [ ] 根 README 渲染正常，链接可达
- [ ] `PLUGINS.md` 链接可达且内容与实际目录一致

---

### Task 5：PR 模板与贡献流程

#### 5.1 `.github/PULL_REQUEST_TEMPLATE.md`

```markdown
## 提交插件 PR

- [ ] 目录名与 `plugin.json` 的 `id` 完全一致
- [ ] `id` 符合 `^[A-Za-z0-9_-]{1,64}$`，且不与内置插件重名
- [ ] `meta.name` / `meta.description` / `meta.author` / `meta.version` 均已填写
- [ ] `meta.name_en` / `meta.description_en` 已填写（英文用户会看到）
- [ ] 每个 `outputs[]` 都填了 `label_en`
- [ ] 每个**非空** `short_label` 都填了 `short_label_en`（任务栏那行字）
- [ ] 每个 `inputs[]` 都填了 `label_en`
- [ ] `README.md` 含「功能说明 / 输出项 / 注意事项」三个必填小节
- [ ] 若本插件有 `inputs`，README 含「配置项」小节
- [ ] 已在本地安装测试过，数据能正常显示
- [ ] `meta.author` 填的是**真实作者**，不是 `User` / `Codex` 之类占位符

## 变更说明

<!-- 这个 PR 做了什么 -->
```

#### 5.2 `meta.author` 的约定

明确写入 README 与 PR 模板：**author 填 GitHub 用户名**。理由：开放 PR 后，用户看到的是"谁写的"，需要能追溯到人。

#### 5.3 审核环节（派 Agent）

**PR 不会自动合并** —— CI 只做格式校验，**内容审核是独立的人工/Agent 环节**：

| 环节 | 谁做 | 产出 |
|---|---|---|
| 格式校验 | CI（`validate` job） | PR 变绿/变红 + 报告 artifact |
| **内容审核** | **审核 Agent（用户派发）** | 审核结论 + `plugins/<id>/verified` 标记文件 |
| 合并与发布 | maintainer 合并 PR → CI `publish-index` | `index.json` 更新，插件上架 |

**审核 Agent 的检查清单**（写进仓库 README 的「审核标准」一节）：

- [ ] `plugin.json` 字段完整、`id` 与目录名一致
- [ ] 插件请求的域名合理（无可疑/广告/数据回传域名）
- [ ] 不索取不必要的凭据（只要求该插件确实需要的）
- [ ] **实机安装运行**，确认监控项有正常数值（不是 `[ERROR]` / `[EMPTY]`）
- [ ] README 的「注意事项」写清了已知的限制与坑
- [ ] `meta.author` 是真实作者

审核通过后，Agent 提交 `plugins/<id>/verified` 空文件，PR 合并后 CI 自动把它写进索引。

#### 5.3 验证

- [ ] 提一个测试 PR，模板自动出现
- [ ] 模板中的勾选项与 `gen_index.py` 的校验规则一一对应

---

### Task 6：与客户端计划对接

#### 6.1 修正旧计划的不一致【✅ 已完成】

`docs/superpowers/plans/2026-10-20-plugin-market-hardening.md` 的以下不一致**已在编写本计划时同步修正完毕，无需再动**：

| 位置 | 修正内容 | 状态 |
|---|---|---|
| 旧计划顶部 | 加了指向本计划的指针 | ✅ 已改 |
| 旧计划 §1.4 | 新增「与仓库侧计划的关系」小节 | ✅ 已改 |
| 旧计划 §3.2 schema | `file` 改为 `"plugin.json"`；`download_url` 改为目录式路径；`original_id`/`renamed` 改为本仓库的真实值（空/false）；补 `readme_url`/`page_url`/`verified`；补回 `source` | ✅ 已改 |
| 旧计划 §3.2 约束表 | `file` 恒为 `"plugin.json"`；目录名 == id；id 正则改 `\A..\z` | ✅ 已改 |
| 旧计划 §3.1 架构图 | 仓库侧改为「每插件一目录 + verified 标记」 | ✅ 已改 |
| 旧计划 §3.3 | 三层防护 → **四层**（新增 L2「不覆盖未记录文件」）；卸载流程加落盘名校验；补旧版残留迁移 | ✅ 已改 |
| 旧计划 §3.4 | 接入点从 7 个拆到 13 个；订正 `DynamicLabel` 不持久化的错误说法 | ✅ 已改 |
| 旧计划 §4.2 | `MarketPluginEntry` 补 `Verified`/`PageUrl`/`ReadmeUrl`；`PluginManager` 加 id 去重 | ✅ 已改 |
| 旧计划 Task 3 | 补 L2 规则、`download_url` 前缀白名单、`\A..\z`、启动自检 | ✅ 已改 |
| 旧计划 Task 4 | 补「未经验证」徽标、双链接、「需重装」状态、`schema` 版本校验 | ✅ 已改 |
| 旧计划 Task 5 | `MarketPluginEntry` 一次性补 7 个字段；接入点改为 13 个 | ✅ 已改 |
| 旧计划 §6 / §9 / 附录 B | 验证清单与风险表同步 | ✅ 已改 |

#### 6.2 客户端需要配合的改动（**已不是零改动**）

客户端**确实需要**在 `MarketPluginEntry` 上新增 7 个字段（见 §3.3 表）并消费它们。除此之外，客户端**没有任何一处**依赖仓库的内部布局：

| 客户端行为 | 依赖仓库什么 | 布局变更影响 |
|---|---|---|
| `FetchIndexAsync` 拉索引 | `index.json` 的 URL | 无 |
| `InstallAsync` 下载 | `entry.download_url` | 无（URL 由索引给出） |
| 落盘文件名 | `entry.id`（客户端自己拼 `{id}.json`） | 无 |
| sha256 校验 | `entry.sha256` | 无 |
| 市场页简介 | `entry.name` / `entry.description` | 无 |
| i18n 选词 | `entry.name_en` / `entry.description_en` | 无 |
| **新增**：未验证徽标 | `entry.verified` | **需客户端消费** |
| **新增**：介绍页/源码链接 | `entry.page_url` / `entry.readme_url` / `entry.source` | **需客户端消费** |

#### 6.3 验证

- [ ] 旧计划 Task 2-8 的代码改动**除 `MarketPluginEntry` 的 7 个新字段外**，不因本计划而变
- [ ] 仓库改造完成后，客户端拉到的索引能正常渲染
- [ ] 市场页的「查看源码」链接可点（`entry.Source` 有值）
- [ ] `verified: false` 的插件在市场页显示「未经验证」徽标

---

## 6. 验证清单（仓库侧）

**结构**：

- [ ] `plugins/` 下是 13 个目录，每个含 `plugin.json` + `README.md`
- [ ] 目录名 == `plugin.json` 的 `id`（13/13）
- [ ] 仓库根无平铺 `*.json`
- [ ] `python3 scripts/gen_index.py --check` 通过

**索引**：

- [ ] `https://raw.githubusercontent.com/.../main/index.json` 返回 200 + 合法 JSON
- [ ] `plugins` 数量 == 13
- [ ] 每条含 `id` / `name` / `description` / `version` / `author` / `download_url` / `sha256` / `page_url` / `source` / `verified`
- [ ] 每条的 `download_url` 可下载，且下载内容 sha256 与索引一致
- [ ] 每条的 `source` / `page_url` / `readme_url` 均可访问
- [ ] 每条的 `page_url` 打开后，**页面顶部是 README 渲染结果，下方是 plugin.json 文件条目**（= 用户要的"本体 + 介绍"两合一）

**质量**：

- [ ] 13 篇 README 均含必备小节（`## 配置项` 对 `inputs` 为空的 5 个插件降为警告）
- [ ] 5 个插件 / 共 12 个缺失的 `outputs[].label_en` 已补齐（见 §2.2 明细）
- [ ] 5 处异常的 `meta.author` 已修正
- [ ] `PLUGINS.md` 与实际目录一致

**PR 流程**：

- [ ] 提测试 PR → 只有 validate job 运行
- [ ] 不合规 PR → 变红，且 **artifact 报告里有具体违规插件与规则**
- [ ] 合并 → 自动提交 `index.json` 与 `PLUGINS.md`
- [ ] 审核 Agent 通过后 `touch plugins/<id>/verified` → CI 重跑后该条 `verified: true`
- [ ] 删除 `verified` 标记 → CI 重跑后回到 `verified: false`
- [ ] 连续两次快速 push → 串行执行，两次都成功

**客户端联动**：

- [ ] 客户端能拉到新索引并列出 13 个插件
- [ ] 安装一个插件成功，且**内置 12 个插件完好无损**
- [ ] 市场页的「查看源码」链接可点（`entry.Source` 有值）

---

## 7. 回滚方案

| 变更 | 回滚方式 |
|---|---|
| 目录重组（13 个移动） | `git revert` 该 commit；或 `git checkout <commit> -- plugins/` |
| 补 `label_en` / `author` | 独立 commit，可单独回滚 |
| `gen_index.py` + workflow | 删文件即可，客户端不受影响（index.json 缺失时市场页显示"加载失败"，但程序其他功能正常） |
| `index.json` / `PLUGINS.md` | CI 产物，删掉后下次 push 会重新生成 |
| 旧计划 §3.2 的 schema 与约束表修正 | 独立 commit（**已在客户端仓库完成**） |
| 仓库根 README / LICENSE | 纯文档，无风险 |

> **关键性质**：本计划的**主体产出都在独立仓库**里。客户端侧只有 `MarketPluginEntry` 的 7 个新字段这一处依赖 —— 最坏情况下把仓库 revert 回现状，客户端依然能编译运行，只是市场拉不到索引（= 改造前的状态）。

---

## 8. 附录 A：本计划**没有**做的事（YAGNI）

| 项 | 理由 |
|---|---|
| GitHub Pages 静态站 | D1 选纯仓库形态；未来想要可在现有结构上叠加，不必重构 |
| CI 渲染每插件 HTML 页 | GitHub 原生渲染 README 即可，零成本零维护 |
| 独立的 `meta.json` | D3 决定不加；机器可读信息全在 `plugin.json` 的 `meta` 段 |
| 改 id 为统一 snake_case | D2 决定不动；id 一经发布即冻结 |
| 插件星级 / 下载量统计 | 需要服务端，超出目标 |
| 插件签名 / GPG | sha256 + 单一可信源足够；开放 PR 后再评估 |
| 插件依赖声明 | 当前插件均独立，无依赖需求 |
| 多语言 README | 软件侧不读 README；`_en` 字段已在 `plugin.json` 里覆盖机器可读部分 |
| 软件侧直接拉 `readme_url` 渲染 | 渐进加载：市场列表用 `description`，详情跳 `page_url` 即可，不必自己渲染 Markdown |

---

## 9. 附录 B：风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| 13 篇 README 的人工撰写工作量大 | 拖延上线 | `init_readme.py` 生成骨架，只写「功能说明」与「注意事项」两节正文 |
| 开放 PR 后有低质量插件混入 | 用户体验受损 | CI 校验挡住格式问题；**审核 Agent 实机验证**后打 `verified` 标记；市场页对未验证插件显示「未经验证」徽标 |
| 有人提 PR 用 id 抢占内置插件名 | 内置插件被覆盖 | CI 校验撞内置名单（仓库侧）+ 客户端 L2 保护名单（客户端侧），双保险 |
| `id` 被贡献者反复请求修改 | 已装用户的配置错乱 | PR 模板明确"id 一经合并不可改"；客户端以 id 为落盘文件名，改 id 等于换插件 |
| CI 自动提交 `index.json` 与人工改动冲突 | 提交失败 | `git add` 指定文件 + `git diff --staged --quiet` 判断；冲突时 CI 变红需人工介入 |
| `verified` 标记被 CI 覆盖 | 审核成果丢失 | 标记是**独立的标记文件** `plugins/<id>/verified`，`gen_index.py` 只读不写（§2.4 / §3.5.2）—— CI 永远不会动它 |
| `plugins/**` 的 paths 过滤导致 README 改动不触发 | 索引过期 | README 在 `plugins/**` 下，**会**触发；已核对 |
| 两次快速 push 导致 CI 提交冲突 | 索引发布失败 | `concurrency` 组串行 + 提交前 `git pull --rebase`（§3.2） |
| 客户端拉到的索引字段与 `MarketPluginEntry` 不匹配 | 反序列化丢字段 | 已逐字段核对（§3.3）；客户端需新增 **7** 个字段（§3.3 表） |
| 客户端漏加 7 个新字段就发布 | 市场页缺「未经验证」徽标与介绍页链接 | Task 6.2 已列为客户端必做项；字段缺失只会静默降级，不会崩 |
| PR 积压，审核 Agent 无人接手 | 贡献者等待过久 | 审核是独立环节，`verified` 标记让审核成果显式可见（§3.5.1） |

---

## 10. 附录 C：相关文件速查

| 关注点 | 位置 |
|---|---|
| 仓库根 | `G:\TOOL\awesome-litemonitor-plugins` |
| 插件目录 | `plugins/<id>/` |
| 索引生成脚本 | `scripts/gen_index.py` |
| CI workflow | `.github/workflows/gen-index.yml` |
| 插件页 URL 形态 | `https://github.com/mojingjing-git/awesome-litemonitor-plugins/tree/main/plugins/<id>` |
| 索引 URL | `https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/index.json` |
| 客户端索引 URL 定义 | `src/Plugins/PluginMarket.cs:66-67`（客户端仓库） |
| 客户端内置 id 名单 | `src/Plugins/PluginConstants.cs`（旧计划 Task 3.1 新增） |
| 客户端本地落盘规则 | `src/Plugins/PluginMarket.cs`（旧计划 Task 3.2，落盘 `{id}.json`） |
| 旧计划（客户端侧） | `docs/superpowers/plans/2026-10-20-plugin-market-hardening.md` |
