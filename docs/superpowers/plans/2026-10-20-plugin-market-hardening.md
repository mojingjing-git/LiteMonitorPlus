# 插件市场加固与仓库绑定 Implementation Plan

> **状态**：待实施（本文件只是计划，尚未改动任何源码）
> **编写时间**：2026-10-20
> **适用分支**：`feat/gpu-group-titles`（当前 HEAD = `5957aa0`）
> **文件性质**：独立新文件。编写本计划的过程**没有改动任何既有 plan 的任何一行，也没有改动任何既有源码行**。
> **关联仓库**：`https://github.com/mojingjing-git/awesome-litemonitor-plugins`（客户端侧硬编码唯一源）
> **仓库侧计划**：`docs/superpowers/plans/2026-10-20-plugin-repo-structure.md` —— **本计划的 Task 1 与 §3.2 已被该计划取代**（仓库改为「每插件一目录」布局）。其余 Task 2-8 继续有效；**客户端代码不因仓库布局变更而改动**。详见 §1.4。

---

## 0. 摘要

| 项目 | 结论 |
|---|---|
| **触发原因** | 本 fork 的立项理由之一是「上游缺少多显卡支持与插件市场」；插件市场的**客户端骨架已存在**，但**仓库侧与安全性部分基本是空的** |
| **用户诉求** | 插件市场**强绑定**用户自建的 `awesome-litemonitor-plugins` 仓库，插件**装在软件根目录**（程序目录），**不写 AppData** |
| **当前可用性** | ❌ **完全不可用** —— 索引文件 404，页面必然显示「加载失败」；即便拉到索引，界面文案也全是 key 名 |
| **最主要的技术风险** | 市场插件与内置插件**同目录平铺**，索引里的文件名可覆盖内置插件；卸载时无所有权判定，会把内置插件**永久删掉**（内置插件随安装包分发，删后只能重装程序恢复） |
| **改动量** | 客户端：改 8 个文件 + 新增 2 个文件 + 新增 1 个测试文件；仓库：新增 1 个脚本 + 1 个 workflow + 1 个 README，修改 1 个插件文件名 |
| **破坏性变更** | 插件源地址输入框被移除（不再支持自定义源）；已安装的旧版市场插件（若曾存在）需要重装 |
| **是否需要上游配合** | 不需要。全部在本 fork 与用户自建仓库内闭环。 |

---

## 1. 背景与目标

### 1.1 用户诉求（已确认的三项决策）

本计划基于以下三项**已拍板**的决策：

| 编号 | 决策 | 说明 |
|---|---|---|
| **D1** | **源完全锁死** | 删除插件源地址输入框，硬编码唯一源 `https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/index.json`，用户不可更改 |
| **D2** | **插件装在软件根目录** | 市场插件落到程序目录 `resources/plugins/`，**不使用** `%AppData%`。用户明确要求「不要在 AppData 里边拉屎」 |
| **D3** | **客户端补 i18n** | 仓库插件里的 `name_en` / `description_en` / `label_en` 字段**保留**，由客户端解析并按当前界面语言选词 |

**D2 的直接后果**：市场插件与内置插件仍然同目录平铺，覆盖/误删风险**不会因为换目录而消失**，必须靠「落盘文件名 + 不覆盖未记录文件 + 保护名单 + 所有权记录」四层防护解决。这是本计划 §2 的缺陷 B / C。

### 1.2 目标

1. 插件市场**开箱可用**：仓库 CI 自动产出索引，客户端能拉取、列出、安装、卸载、更新。
2. **绝对安全**：市场插件**不能覆盖、不能删除**任何内置插件；下载内容必须通过哈希与 id 双重校验。
   > **实现方式已修正**（原表述"靠 12 个硬编码 id 常量"不成立）：真正兜底的是 **L2 规则** —— 目标文件已存在且不在市场安装记录内 → 拒绝安装。内置名单降为第二道防线，名单漏改不再造成覆盖。详见 §3.3。
3. **可维护**：新增/更新插件只改仓库文件，不改客户端代码。
4. **多语言可用**：英文界面下市场插件显示英文文案。

### 1.3 现状盘点

**客户端已就绪的部分**（本计划保留并加固，不推倒重来）：

| 文件 | 现有能力 |
|---|---|
| `src/Plugins/PluginMarket.cs` | 拉索引 / 装 / 卸 / 文件名安全检查 |
| `src/UI/Settings/PluginMarketPage.cs` | 源输入框 + 刷新按钮 + 插件列表 + 装卸按钮 |
| `src/Plugins/PluginManager.cs` | 模板加载、实例生命周期、失败退避重试 |
| `src/Plugins/PluginExecutor.cs` | HTTP 取数、链式步骤、变量提取、格式转换、标签注入 |
| `src/Plugins/PluginModels.cs` | 插件 JSON 的数据模型 |

> **行数会随改动漂移**，本计划一律用「文件:行」定位，不写绝对行数。

**仓库已就绪的部分**：

| 项 | 状态 |
|---|---|
| `plugins/` 下 13 个插件 JSON | ✅ 存在 |
| `index.json` | ❌ **不存在**（HTTP 404） |
| 生成索引的 workflow / 脚本 | ❌ 不存在 |
| `README.md` | ❌ 不存在 |

**已核实的插件引擎能力边界**（重要，决定了安全声明怎么写）：

| 能力 | 是否支持 | 证据 |
|---|---|---|
| HTTP GET / POST 取数 | ✅ | `PluginExecutor.ExecuteStepAsync` |
| `native://` 内建处理器 | ✅ 仅 `citycode` / `crypto` 两个 | `PluginExecutor.ExecuteNativeAsync` + `src/Plugins/Native/` |
| JSON 变量提取 | ✅ | `PluginExecutionStep.Extract` |
| 正则替换 / 映射 / 模板解析 / 阈值切换 | ✅ | `PluginProcessor` 的 switch：`regex_replace` / `regex_match` / `map` / `resolve_template` / `threshold_switch` |
| **执行任意脚本 / 动态编译 / 加载程序集** | ❌ **完全不支持** | 全 `src/Plugins/` 目录 grep `Process.Start` / `Assembly.Load` / `CSharpCodeProvider` **零命中** |

> 结论：插件系统是**纯声明式 JSON**，**不具备执行第三方代码的能力**。仓库插件里用到的 `threshold_switch` 与 `resolve_template` 都在支持列表内。

### 1.4 与仓库侧计划的关系（2026-10-20 补充）

仓库侧的结构改造（平铺 JSON → 每插件一目录 + 引入 `README.md` 介绍页 + PR 协作流程）由独立计划负责：

> **`docs/superpowers/plans/2026-10-20-plugin-repo-structure.md`**

**该计划取代本计划的以下内容**：

| 本计划位置 | 处置 |
|---|---|
| §3.2 的 `index.json` schema | **以仓库侧计划 §3.3 为准**（`file` 恒为 `"plugin.json"`，新增 `readme_url` / `page_url` / `verified` 等字段） |
| §3.2 的 CI 约束表 | **以仓库侧计划 §3.4 为准**（约束对象从"文件名"改为"目录名"） |
| §4.3 的仓库文件结构表 | **以仓库侧计划 §4 为准** |
| §5 的 Task 1（仓库侧全部） | **由仓库侧计划的 Task 1-6 取代** |
| §6 验证清单的仓库侧部分 | **以仓库侧计划 §6 为准** |

**本计划继续有效、不受影响的**：Task 2（语言 key）、Task 3（安全加固）、Task 4（源锁死 + 更新检测）、Task 5（i18n）、Task 6（单元测试）、Task 7（真机验证）、Task 8（文档与安全声明）。

**为什么仓库大改而客户端几乎不动**：客户端消费的字段**全部来自 `index.json`**（`id` / `download_url` / `sha256` / `name` / `description` / `name_en` / `description_en` / `source` / `page_url` / `readme_url` / `verified`），仓库内部布局对它**完全无感**。客户端本地落盘仍是 `resources/plugins/{id}.json`（§3.3 的 L1 机制），与仓库的目录式布局**解耦**。

> **客户端需要配合的唯一一处**：`MarketPluginEntry` 新增 7 个字段并消费它们（仓库侧计划 §3.3 / Task 6.2）。除此之外，客户端逻辑零改动。

---

## 2. 缺陷分析（逐条核实，证据附后）

### 缺陷 A：索引文件不存在 → 市场页永远加载失败【P0】

`PluginMarket.DefaultIndexUrl` 指向：

```
https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/index.json
```

实测该 URL 返回 **404 Not Found**。仓库根目录只有 `plugins/` 一个目录，**没有** `index.json`，**没有** `.github/workflows`。

客户端 `FetchIndexAsync` 抛异常 → `PluginMarketPage.LoadIndexAsync` catch → `_statusText = "加载失败 " + ex.Message`，列表区显示「暂无插件」。

**这是市场不可用的唯一直接原因。**

### 缺陷 B：落盘文件名来自索引 → 可覆盖内置插件【P0】

`PluginMarket.InstallAsync`（`PluginMarket.cs:134-136`）：

```csharp
Directory.CreateDirectory(PluginsDir);
string path = Path.Combine(PluginsDir, entry.File);
await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
```

`entry.File` 完全来自索引。`PluginsDir` = `程序目录/resources/plugins/`，与内置插件同目录。

仓库现有插件里就有 `plugins/PublicIP.json`（id = `public_ip_info`），而内置 `resources/plugins/PublicIP.json`（id = `PublicIP`）**同名**。装这个插件会**直接覆盖内置的公网 IP 插件文件**。

`MarketPluginEntry` 里已有 `OriginalId` / `Renamed` 两个字段（`PluginMarket.cs:20-21`），说明**设计意图**就是"重命名避让"，但安装代码里**没有实现任何改名逻辑** —— 字段目前只用于在界面上显示一行提示。

### 缺陷 C：卸载无所有权判定 → 内置插件被永久删除【P0】

`PluginMarket.Uninstall`（`PluginMarket.cs:140-147`）：

```csharp
string path = Path.Combine(PluginsDir, entry.File);
if (!File.Exists(path)) return false;
File.Delete(path);
```

配合缺陷 B，用户装完 `PublicIP.json`（覆盖了内置的）再点卸载 → **内置插件文件被删除**。

内置插件是**随安装包分发**的（`LiteMonitor.csproj` 的 `Content Include="resources\plugins\*.json"` + `CopyToOutputDirectory=Always`），被删后：
- 重新解压新版本安装包可恢复；
- 但**已安装用户的当前目录里就永久没有了**，且 `PluginManager.LoadPlugins` 会连带清掉对应的 `PluginInstanceConfig` 与 `MonitorItems`（`PluginManager.cs:71-87`）—— 用户配置的监控项一并消失。

### 缺陷 D：`PluginMarket*` 语言 key 从来就不存在 → 界面显示 key 名【P0】

`PluginMarketPage.cs` 引用了 **17 个** `LanguageManager.T("Menu.PluginMarket*")` key（另有 `Menu.PluginMarket` 用在 `SettingsForm.cs:151`，全项目共 18 个），例如：

```csharp
var btnRefresh = new LiteButton(LanguageManager.T("Menu.PluginMarketRefresh"), true);
```

实测 9 个语言包（`zh` / `zh-tw` / `en` / `ja` / `ko` / `ru` / `de` / `es` / `fr`）中，**`PluginMarket` 相关的 key 数量全部为 0**。`git diff resources/lang/*.json` 中也**没有**删除记录 —— 说明这组 key **从来就没被添加过**（该页面是未发布的新功能，没人用过）。

`LanguageManager.GetOriginal`（`LanguageManager.cs:71-79`）在 key 找不到时回退到**第一个 `.` 之后**的字符串（`key.IndexOf('.')`，取的是第一个点），因此界面实际显示：

| 代码里的 key | 界面实际显示 |
|---|---|
| `Menu.PluginMarketRefresh` | `PluginMarketRefresh` |
| `Menu.PluginMarketInstall` | `PluginMarketInstall` |
| `Menu.PluginMarketUninstall` | `PluginMarketUninstall` |
| `Menu.PluginMarketLoading` | `PluginMarketLoading` |
| `Menu.PluginMarketLoaded` | `PluginMarketLoaded` |
| `Menu.PluginMarketFailed` | `PluginMarketFailed` |
| `Menu.PluginMarketInstalling` | `PluginMarketInstalling` |
| `Menu.PluginMarketInstallOk` | `PluginMarketInstallOk` |
| `Menu.PluginMarketGoEnable` | `PluginMarketGoEnable` |
| `Menu.PluginMarketRequires` | `PluginMarketRequires` |
| `Menu.PluginMarketRenamed` | `PluginMarketRenamed` |
| `Menu.PluginMarketSourceLink` | `PluginMarketSourceLink` |
| `Menu.PluginMarketInstalled` | `PluginMarketInstalled` |
| `Menu.PluginMarketEmpty` | `PluginMarketEmpty` |
| `Menu.PluginMarketHint` | `PluginMarketHint` |

**修复成本极低（加 key），但不做的话市场页面在所有语言下都是"开发者看"的界面。**

### 缺陷 E：`_en` 双语字段是死字段【P1】

仓库 13 个插件均写了 `name_en` / `description_en` / `label_en`。但：

- `PluginModels.cs` 的 `PluginMeta` 只有 `name` / `version` / `author` / `description` / `url`；
- `PluginOutput` 只有 `key` / `label` / `short_label` / `format_val` / `color` / `unit`；
- 全 `src/` 目录 grep `name_en|label_en|_en\b` **零命中**。

`System.Text.Json` 默认**静默忽略**未声明的属性 → 这些字段被丢弃，英文用户看到的仍是中文。

### 缺陷 F：没有版本与更新机制【P1】

`PluginMarket.IsInstalled`（`PluginMarket.cs:98-102`）**只判断文件是否存在**：

```csharp
return File.Exists(Path.Combine(PluginsDir, entry.File));
```

后果：
- 仓库里插件更新了（`version` 变了 / 内容变了），客户端**永远收不到新版**；
- 界面上「已安装」就是「已安装」，没有「可更新」的概念；
- 用户只能手动删文件重装。

### 缺陷 G：下载内容缺少完整性校验【P1】

`InstallAsync`（`PluginMarket.cs:115-136`）下载后只做了两件事：
1. 校验是合法 JSON；
2. 校验有 `id` 字段且非空。

**没有**校验：
- 下载内容的 `id` 是否等于索引声明的 `id`（不一致会导致界面显示的插件与实际加载的插件对不上）；
- 内容哈希是否与索引声明的一致（中间人 / 仓库被误改都无从发现）。

---

## 3. 方案设计

### 3.1 总体架构

```
┌──────────────────────────────────────────────────────────────┐
│  仓库 awesome-litemonitor-plugins (用户自建, 唯一源)          │
│                                                              │
│   plugins/<id>/plugin.json  ──(人工编辑/提 PR)──▶ 插件本体     │
│   plugins/<id>/README.md    ──(人工编辑/提 PR)──▶ 介绍          │
│   plugins/<id>/verified     ──(审核 Agent 打标)──▶ 审核标记    │
│         │                                    │                │
│         │ push main                          ▼                │
│         ▼                          GitHub Actions             │
│   scripts/gen_index.py  ◀──────────────────────┘             │
│         │  校验 + 计算 sha256 + 生成索引                        │
│         ▼                                                     │
│   index.json  ──(自动提交)──▶  raw.githubusercontent.com        │
└──────────────────────────────────────────────────────────────┘
                              │
                              │  ① 拉取索引
                              ▼
┌──────────────────────────────────────────────────────────────┐
│  客户端 LiteMonitor++                                         │
│                                                              │
│   PluginMarketPage  ◀── D1：源锁死，删掉输入框                  │
│        │                                                     │
│        ▼                                                     │
│   PluginMarket  ── ② 校验 sha256 + id 一致性 + 源前缀白名单     │
│        │             ③ 落盘文件名 = {id}.json（D2：程序目录）    │
│        │             ④ L2：目标已存在且非市场装 → 拒绝          │
│        │             ⑤ L3：命中内置保护名单 → 拒绝              │
│        ▼                                                     │
│   程序目录/resources/plugins/                                 │
│        ├── Bilibili.json … WeiboHot.json   ← 内置（12 个）     │
│        └── {市场插件 id}.json               ← 市场（受管）      │
│                                                              │
│   settings.json  ── MarketPlugins[] 记录市场安装的             │
│                              id / file / version / sha256       │
│                              → 所有权判定 + 更新检测            │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 `index.json` Schema（客户端与仓库的唯一契约）

**这是整个计划里最重要的一份约定**，客户端与 CI 都必须严格遵守。

```jsonc
{
  "schema": 1,                                   // 索引格式版本，客户端按此做兼容判断
  "updated_at": "2026-10-20T05:00:00Z",          // 索引生成时间 (ISO8601 UTC)
  "repo": "mojingjing-git/awesome-litemonitor-plugins",
  "total": 13,
  "plugins": [
    {
      "id": "public_ip_info",                    // 插件唯一 id（也决定落盘文件名）
      "original_id": "",                          // 原 id（本仓库从未改名，恒为空）
      "renamed": false,                           // 是否发生过改名（恒为 false）
      "name": "公网IP显示",                       // 中文名
      "name_en": "Public IP",                    // 英文名（D3：客户端会用到）
      "description": "……",                       // 中文描述
      "description_en": "……",                    // 英文描述（D3）
      "author": "LiteMonitor",
      "version": "1.0",
      "file": "plugin.json",                     // 相对 plugins/<id>/ 的路径（见仓库侧计划 §3.3）
      "download_url": "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/public_ip_info/plugin.json",
      "readme_url": "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/public_ip_info/README.md",
      "page_url": "https://github.com/mojingjing-git/awesome-litemonitor-plugins/tree/main/plugins/public_ip_info",
      "sha256": "3f786850e387550fdab836ed7e6dc881de23001b…",  // 插件文件内容的 SHA-256（小写十六进制）
      "size": 4148,                              // 字节数
      "status": "ok",                            // ok | needs-service | needs-code
      "requires": [],                            // 额外准备要求（数组）
      "note": "",                                // 自由备注
      "verified": false,                         // 是否经审核 Agent 实机验证（仓库侧计划 §3.5）
      "source": "https://github.com/mojingjing-git/awesome-litemonitor-plugins/blob/main/plugins/public_ip_info/plugin.json"
    }
  ]
}
```

> **`source` 不可省略**：市场页的「查看源码」链接读 `entry.Source`（`PluginMarketPage.cs:200-204`）。索引里少了它，市场页上一条链接都不会剩。

**约束（CI 强制校验，违反则让 CI 失败）**：

| 约束 | 理由 |
|---|---|
| `file` 恒为 `"plugin.json"` | 仓库改为「每插件一目录」布局，`file` 变成相对路径（见仓库侧计划 §3.3） |
| `id` 必须匹配 `\A[A-Za-z0-9_-]{1,64}\z` | 落盘文件名安全性；也防止 id 撞内置名单（**注意用 `\A..\z` 不用 `^..$`** —— .NET 的 `$` 会匹配串尾换行之前，`"abc\n"` 能通过 `^..$`，详见 §3.2） |
| `id` 不得落在内置保护名单内 | 防止与内置插件 id 冲突（两个模板同 id 会导致 `FirstOrDefault` 取值不确定） |
| `id` 不得与索引内其他条目重复 | 索引自身一致性 |
| `sha256` 必须等于文件实际内容的 SHA-256 | 客户端据此校验下载完整性 |
| 插件 JSON 必须能被解析且含非空 `id` | 基本可用性 |
| 插件**目录名**必须等于其 `id` | 仓库侧目录名规范，避免 `PublicIP.json` / `public_ip_info.json` 这类名实不符（见仓库侧计划 §3.4） |

### 3.3 安装位置与所有权模型（D2 的落地）

**安装位置**：保持 `AppContext.BaseDirectory/resources/plugins/`，即程序目录内的 `resources/plugins/`。

**"软件根目录"的落位说明**：`resources/plugins/` 位于程序安装目录之下，与内置插件、主题、语言包同级。用户在设置页看到的所有资源都在程序目录下，插件也在这里，**软件目录本身自包含**（整包拷贝即可迁移，含已装插件）。

**四层防护**（L2 是核心 —— 见下方说明）：

| 层 | 机制 | 防住什么 |
|---|---|---|
| **L1 文件名由 id 决定** | 落盘路径 = `PluginsDir/{id}.json`，**不信任**索引的 `file` 字段 | 索引被投毒时无法写到目录外；也无法覆盖任意其他文件 |
| **L2 不覆盖未记录文件** | 安装时若目标文件**已存在**且**不在 `MarketPlugins` 记录内** → **拒绝安装** | **内置插件（含未来新增的）**、用户手动放置的文件、旧版市场残留 —— **一条规则全覆盖** |
| **L3 内置保护名单** | `PluginConstants.BuiltinPluginIds` 常量列出 12 个内置 id；命中即拒绝并给出友好提示 | 市场插件抢占内置 id（在 L2 之前先拦一步，报错信息更准） |
| **L4 所有权 + 落盘名校验** | 卸载时校验 `record.File == GetInstallFileName(record.Id)` **且** id 不在内置名单 | 防止 `settings.json` 被篡改后删掉任意文件；内置插件永不被误删 |

> **为什么 L2 不能省**（这是本计划最重要的一处修正）：L3 的内置名单是**硬编码常量**，将来客户端新增内置插件时若忘了同步名单，L3 就失效了 —— 而 L2 **不依赖任何名单**：「目标文件已存在且不是本市场装的」就拒绝。这条规则同时覆盖三类文件：
>
> | 场景 | L2 的判定 | 结果 |
> |---|---|---|
> | 全部 12 个内置插件 | 文件存在 + 不在市场记录里 | ✅ 拒绝（**且未来新增的内置插件自动受保护**） |
> | 用户手动放进 `resources/plugins/` 的插件 | 同上 | ✅ 拒绝（不覆盖用户的文件） |
> | 旧版市场插件残留（如 `PublicIP.json`） | 同上 | ✅ 拒绝并提示「检测到旧版残留，请删除该文件后重试」 |
>
> L3 从"唯一防线"降为"第二道防线 + 更友好的提示文案"。**名单漏改不再造成覆盖。**

**卸载流程（D2 下删除内置插件的完整防护）**：

```
用户点「卸载」
  → 在 MarketPlugins 记录中查找该 id
  → 找不到 → 拒绝，提示「该插件非本市场安装，无法卸载」
  → 找到 → 校验 record.File == GetInstallFileName(record.Id)
           （不匹配 → 拒绝，提示「安装记录异常」，防 settings.json 被篡改）
  → 校验 id 不在 BuiltinPluginIds 内
           （命中 → 拒绝，内置插件永不可卸载）
  → 删除 PluginsDir/{id}.json
  → 从记录中移除该条 → 保存 settings
  → PluginManager.LoadPlugins 重载（失效实例与监控项自动清理）
```

> **注意 L4 的两道校验为什么必要**：`record.File` 来自 `settings.json`（位于程序目录，`SettingsHelper.cs:14`），若无条件 `Path.Combine(PluginsDir, record.File)` 后 `File.Delete`，一个被篡改的 `File: "../../LiteMonitor++.exe"` 就能删掉程序目录内的任意文件。**文件名必须由 id 唯一推导，不能接受外部写入的任意路径。**

**旧版市场插件的迁移（P3）**：

旧版客户端用 `entry.File` 落盘，所以仓库里那个 `plugins/PublicIP.json`（id=`public_ip_info`）会**落成 `PublicIP.json`** —— 与内置文件**同名**，即**旧版已经覆盖了内置插件**，且卸载时删的就是内置插件本身（不存在"两份"，也无法"按记录清理"）。

后果链：`LoadPlugins` 看不到 id=`PublicIP` 的模板 → `PluginManager.cs:71-87` 清掉用户的 `PublicIP` 实例与 `MonitorItems` 并**保存 settings** → 用户的公网 IP 监控项配置**永久丢失**。

**本计划的处理**：

1. **L2 规则**天然拦住重装：升级后装 `public_ip_info` 时，目标 `public_ip_info.json` 不存在 → 正常安装；而旧的 `PublicIP.json` 仍在且内容 id=`public_ip_info` → 它**不在市场记录里**（旧版没有记录）→ 若再有人用旧索引装 `public_ip_info`，L2 会因 `public_ip_info.json` 已存在而拒绝。**但旧的 `PublicIP.json` 覆盖问题不会被自动修复** —— 需要用户手动删除它或重装程序。
2. **加一条启动自检**（Task 3.5）：扫描 `resources/plugins/*.json`，若某文件的 `id` **不等于**其文件名 stem **且**该文件不在市场记录里 → 记日志并提示用户「检测到疑似覆盖内置插件的文件 `Xxx.json`，请删除该文件或重装程序」。
3. **修正文档**：附录 B 对旧版残留的描述改为"旧版落盘名 = `entry.File` = 内置同名文件，**不是**两份"（见 §9 风险表）。

**写权限处理（D2 的已知代价）**：装在程序目录 → 若装在 `C:\Program Files\` 下，普通权限写入会失败。现有代码已 catch `UnauthorizedAccessException`（`PluginMarketPage.cs:282-286`），本计划把提示文案升级为**明确引导以管理员身份运行**（程序本身因需安装 PawnIO 驱动就已建议管理员运行，见 `使用说明(必读).txt:18-19`）。

### 3.4 i18n 选词规则（D3 的落地）

**选词原则**：

| 当前界面语言 | 选词 |
|---|---|
| `zh`（简体中文） | 中文原字段 |
| `zh-tw`（繁体） | 中文原字段（仓库暂未提供繁体，保留中文） |
| `en` / `ja` / `ko` / `ru` / `de` / `es` / `fr` | `_en` 字段；`_en` 为空时**回退中文** |

**实现**：新增 `src/Plugins/PluginI18n.cs`，提供静态方法：

```csharp
public static class PluginI18n
{
    /// <summary>当前是否应优先使用英文文案</summary>
    public static bool PreferEnglish =>
        !LanguageManager.CurrentLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>按当前界面语言在中文/英文之间选词；英文缺失时回退中文</summary>
    public static string Pick(string zh, string en)
        => PreferEnglish && !string.IsNullOrEmpty(en) ? en : zh;
}
```

**接入点清单**（全部已在代码中定位到行）：

| # | 文件 | 行 | 现状 | 改为 |
|---|---|---|---|---|
| 1 | `PluginMonitorSyncService.cs` | 82 | `output.Label` | `PluginI18n.Pick(output.Label, output.LabelEn)` |
| 2 | `PluginMonitorSyncService.cs` | 83 | `labelPattern` 兜底用 `tmpl.Meta.Name` | `PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn) + " " + output.Key` |
| 3 | `PluginMonitorSyncService.cs` | 86 | `output.ShortLabel` | `PluginI18n.Pick(output.ShortLabel, output.ShortLabelEn)` |
| 4 | `PluginMonitorSyncService.cs` | 91, 130 | `safeLabel` 兜底用 `tmpl.Meta.Name` | 同 #2 |
| 5 | `PluginMonitorSyncService.cs` | 215-230 | `TryGetSmartLabel` 的 labelPattern 与兜底 | labelPattern 用 #1 / #3；兜底用 #2（**注意 :215 是 `output.ShortLabel`，要用 #3 的 `Pick(output.ShortLabel, output.ShortLabelEn)`，不是 #1**） |
| 6 | `PluginExecutor.cs` | 645-648 | `ProcessOutputs` 的 labelPattern / shortLabel | 同 #1 / #3 |
| 7 | `PluginExecutor.cs` | 695-698, 729 | `HandleExecutionError` 的 labelPattern / fallback | 同 #1 / #3（**别漏**，否则报错时标签回退中文，与正常时不一致） |
| 8 | `PluginPage.cs` | 204 | 标题 `tmpl.Meta.Name` | `PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn)` |
| 9 | `PluginPage.cs` | 223-225 | 描述 `tmpl.Meta.Description` | `PluginI18n.Pick(tmpl.Meta.Description, tmpl.Meta.DescriptionEn)` |
| 10 | `PluginPage.cs` | 244 | 开关标签 `tmpl.Meta.Name` | 同 #8 |
| 11 | `PluginMarketPage.cs` | 154 | 标题 `entry.Name` | `PluginI18n.Pick(entry.Name, entry.NameEn)` |
| 12 | `PluginMarketPage.cs` | 170 | 描述 `entry.Description` | `PluginI18n.Pick(entry.Description, entry.DescriptionEn)` |
| 13 | `PluginMarket.cs` | 34-45 | `StatusLabel` **硬编码中文**「开箱即用 / 需配套服务 / 需改代码」 | **锁定走 i18n 方案**：改为 `LanguageManager.T("Menu.PluginMarketStatusOk" / "StatusService" / "StatusCode")`，**不新增 `status_en` 字段**（否则 `MarketPluginEntry` 就变成 8 个字段，与 §3.3 / Task 5.1 的 7 字段清单互斥） |

> **为什么 6/7 两处都必须改**（这一点容易搞错）：`PluginExecutor` 注入的是**运行时**的 `PROP.Label.*`（`InfoService`），`PluginMonitorSyncService` 写的是 `MonitorItemConfig.DynamicLabel`（`[JsonIgnore]`，**不持久化**）。两者是同一标签的**两个独立数据源**，且被不同 UI 各自读取：
>
> | 读取方 | 取值路径 | 是否读运行时值 |
> |---|---|---|
> | `MetricLabelResolver.ResolveLabel` | `PROP.Label.<key>` → 退回 `config.DynamicLabel` | ✅ 读 |
> | `MenuMonitorHelper.cs:91`（托盘菜单） | 走 resolver | ✅ 读 |
> | `MenuMonitorHelper.cs:123`（面板子菜单） | 直接读 `conf.DisplayLabel`（= `DynamicLabel`） | ❌ **绕开运行时值** |
>
> 只改一侧 → **托盘菜单是英文、面板子菜单是中文**这种混显。> 顺带纠正一个流传的注释：`MetricLabelResolver.cs:63` 的「由 SyncService 定期同步并**持久化保存**的快照」是**错的** —— `DynamicLabel` 标了 `[JsonIgnore]`（`Settings.cs:289`），根本不写进 `settings.json`。

---

## 4. File Structure

### 4.1 客户端 —— 新建文件

| 文件 | 用途 |
|---|---|
| `src/Plugins/PluginI18n.cs` | 语言选词 helper（§3.4）。**需 `using LiteMonitor.src.Core;`**（`LanguageManager` 在该命名空间） |
| `src/Plugins/PluginMarketState.cs` | 市场安装记录模型 + 读写（挂在 `Settings.MarketPlugins`）。`Settings` 在命名空间 `LiteMonitor`，是 `LiteMonitor.src.Plugins` 的外层命名空间，**无需额外 using** |

### 4.2 客户端 —— 修改文件

| 文件 | 改动 |
|---|---|
| `src/Plugins/PluginMarket.cs` | 目录常量拆分、内置保护名单、落盘名由 id 决定、sha256 校验、id 一致性校验、记录读写、索引解析、**`download_url` 前缀白名单**、**`schema` 版本校验**、**删除 `IsInstalled` 与 `IsSafeFileName`（见 Task 4.2）** |
| `src/Plugins/PluginModels.cs` | `PluginMeta` / `PluginOutput` / `PluginInput` / `PluginDisplay` 增加 `_en` 字段；`MarketPluginEntry` 增加 `NameEn` / `DescriptionEn` / `Sha256` / `Size` / **`Verified` / `PageUrl` / `ReadmeUrl`** |
| `src/Plugins/PluginConstants.cs` | 增加 `BuiltinPluginIds` 名单、`MarketPluginsFileSuffix` 等常量 |
| `src/Plugins/PluginMonitorSyncService.cs` | 接入 i18n 选词（§3.4 接入点 1-5） |
| `src/Plugins/PluginExecutor.cs` | 接入 i18n 选词（§3.4 接入点 6-7） |
| `src/UI/Settings/PluginMarketPage.cs` | 删源输入框（D1）、更新检测 UI、**「未经验证」徽标**、i18n 文案、权限提示 |
| `src/UI/Settings/PluginPage.cs` | 接入 i18n 选词（§3.4 接入点 8-10） |
| `src/Core/Settings.cs` | 新增 `MarketPlugins` 列表属性 |
| `src/Plugins/PluginManager.cs` | **`LoadPlugins` 增加 id 去重**（同 id 模板只留第一个 + 记日志，见 §2 缺陷 F 补充） |
| `resources/lang/*.json`（9 个） | 补齐 `Menu.PluginMarket*` 全套 key（缺陷 D），**含 `PluginMarketUnverified` / `PluginMarketUnverifiedHint` / `PluginMarketStatusOk` / `PluginMarketStatusService` / `PluginMarketStatusCode`** |

### 4.3 仓库 —— 新建 / 修改

| 路径 | 动作 |
|---|---|
| **全部仓库侧改造** | **已移交仓库侧计划** `2026-10-20-plugin-repo-structure.md` §4 —— 目录重组、`gen_index.py`、workflow、`README.md`、PR 模板、13 篇插件介绍 |

### 4.4 测试

| 文件 | 覆盖 |
|---|---|
| `tests/LiteMonitor.Tests/PluginMarketTests.cs` | **新建**：落盘名生成、内置保护、sha256 校验、id 一致性、记录读写、卸载所有权 |
| `tests/LiteMonitor.Tests/PluginI18nTests.cs` | **新建**：选词规则与回退 |

---

## 5. Task 分解

> 每个 Task 结束时都必须**能编译通过**（`dotnet build -c Release`），Task 6 之后必须**测试全绿**。

### Task 1：仓库侧 —— 规范化 + CI 生成索引【已移交仓库侧计划】

> **本节已被 `docs/superpowers/plans/2026-10-20-plugin-repo-structure.md` 取代。**
> 仓库改为「每插件一目录 + README 介绍页」布局，改造步骤、CI 设计、PR 流程、
> `index.json` schema 全部以仓库侧计划为准。**客户端侧无任何改动。**
>
> 下方原文保留作为历史记录，**请勿按其执行**。

**为什么先做**：缺陷 A（索引 404）是市场不可用的唯一直接原因，且**不依赖客户端任何改动**，可以立刻解除。

#### 1.1 Clone 仓库

```powershell
git clone https://github.com/mojingjing-git/awesome-litemonitor-plugins.git G:\TOOL\awesome-litemonitor-plugins
```

> 目录放在 `G:\TOOL\` 下与 `LiteMonitor-fork` 平级。**不属于** LiteMonitor-fork 仓库，不污染主仓库的 git 状态。

#### 1.2 规范化插件文件名

对 `plugins/` 下每个文件，读取其 `id`，若 `文件名 != id + ".json"` 则重命名。

已知至少 1 个需要改名：

| 原文件名 | id | 新文件名 |
|---|---|---|
| `PublicIP.json` | `public_ip_info` | `public_ip_info.json` |

脚本化执行（避免漏项）：

```python
# 一次性脚本，或直接纳入 gen_index.py 的 --fix 模式
import json, pathlib
for p in pathlib.Path("plugins").glob("*.json"):
    data = json.loads(p.read_text(encoding="utf-8"))
    want = f'{data["id"]}.json'
    if p.name != want:
        p.rename(p.with_name(want))
        print(f"renamed -> {want}")
```

#### 1.3 编写 `scripts/gen_index.py`

要求：

- 扫描 `plugins/*.json`；
- 逐个校验 §3.2 的 7 条约束，**任一不满足则整体失败并打印具体原因**（CI 会因此变红，阻止不合规的索引被发布）；
- 计算每个文件的 `sha256` 与 `size`；
- 从插件 JSON 里读出 `meta.name` / `meta.name_en` / `meta.description` / `meta.description_en` / `meta.version` / `meta.author`；
- 生成 `index.json`，`file` 字段写 `id + ".json"`；
- `updated_at` 用 UTC ISO8601；
- 输出到仓库根 `index.json`；
- 支持 `--check` 模式（只校验不写文件），供 CI 与本地预检使用。

**内置保护名单必须同步进脚本**（与客户端 `PluginConstants.BuiltinPluginIds` 保持一致）：

```python
BUILTIN_IDS = {
    "Bilibili", "Crypto", "Exchange", "GitHub", "Hitokoto", "Holiday",
    "ProxyLatency", "PublicIP", "Stock", "UniversalAPI", "Weather", "WeiboHot",
}
```

> ⚠️ **单一事实来源问题**：这份名单在客户端（C#）与仓库（Python）各存一份，有漂移风险。缓解：CI 脚本从**客户端仓库的常量**拉取？跨仓库做不到。折中方案 —— 两处都写，并在两份文件里互相写上"另一处的同款常量在此维护"的注释；后续若新增内置插件，**两处必须同时改**。本计划接受这份重复（12 项，变更频率极低）。

#### 1.4 编写 `.github/workflows/gen-index.yml`

```yaml
name: Generate Plugin Index

on:
  push:
    branches: [main]
    paths: ['plugins/**', 'scripts/**']
  workflow_dispatch:

permissions:
  contents: write

jobs:
  index:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Validate plugins and regenerate index.json
        run: python3 scripts/gen_index.py
      - name: Commit updated index.json
        run: |
          git config user.name  "github-actions[bot]"
          git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
          git add index.json
          if git diff --staged --quiet; then
            echo "index.json unchanged, nothing to commit"
          else
            git commit -m "chore(index): 自动重新生成插件索引"
            git push
          fi
```

#### 1.5 编写仓库 `README.md`

至少包含：仓库定位（社区插件索引，非官方）、插件收录方式（提 PR 到 `plugins/`）、**命名规范（文件名必须等于 `id + ".json"`）**、索引生成机制、免责声明。

#### 1.6 验证

```powershell
# 本地预检
cd G:\TOOL\awesome-litemonitor-plugins
python scripts/gen_index.py --check     # 应通过
python scripts/gen_index.py             # 生成 index.json
git add -A; git commit -m "feat: 规范化插件文件名并新增索引生成脚本"
git push
```

推送后 CI 自动运行并提交 `index.json`，随后验证：

```
https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/index.json
```

应返回 **200 + 合法 JSON**（不再是 404）。

**验收标准**：
- [ ] `index.json` 可通过 HTTP 200 获取
- [ ] `index.json` 中 `plugins` 数量 = 13
- [ ] 每条都有 `id` / `name` / `version` / `file` / `download_url` / `sha256`
- [ ] 每条的 `file` 等于 `id + ".json"`
- [ ] 仓库内无 `PublicIP.json` 残留
- [ ] 故意放一个 id 撞内置的插件 → CI **应当失败**（验证校验有效）

---

### Task 2：客户端 —— 补齐 `PluginMarket*` 语言 key【修缺陷 D】

#### 2.1 确定 key 清单

在 `resources/lang/zh.json` 的 `Menu` 节点下新增以下 **35 个** key（清单是**权威** —— 以本表为准，不要按别处的数字核）：

| key | 中文 | 英文 |
|---|---|---|
| `PluginMarket` | 插件市场 | Plugin Market |
| `PluginMarketSource` | 插件源 | Source |
| `PluginMarketRefresh` | 刷新 | Refresh |
| `PluginMarketLoading` | 正在拉取插件索引… | Loading plugin index… |
| `PluginMarketLoaded` | 已加载插件 | Loaded plugins |
| `PluginMarketEmpty` | 索引里没有插件 | No plugins in the index |
| `PluginMarketFailed` | 插件市场加载失败 | Failed to load plugin market |
| `PluginMarketInstall` | 安装 | Install |
| `PluginMarketUninstall` | 卸载 | Uninstall |
| `PluginMarketUpdate` | 更新 | Update |
| `PluginMarketInstalling` | 正在安装 | Installing |
| `PluginMarketInstallOk` | 安装完成 | Installed |
| `PluginMarketUninstallOk` | 已卸载 | Uninstalled |
| `PluginMarketUpdateOk` | 已更新到最新版本 | Updated to the latest version |
| `PluginMarketGoEnable` | 去「插件管理」页启用 | Enable it on the Plugins page |
| `PluginMarketRequires` | 需要额外准备 | Requires |
| `PluginMarketRenamed` | 原插件已改名 | Renamed from |
| `PluginMarketSourceLink` | 查看源码 | View source |
| `PluginMarketInstalled` | 已安装 | Installed |
| `PluginMarketUpdatable` | 可更新 | Update available |
| `PluginMarketHint` | 插件为纯声明式 JSON 配置，不含可执行代码 | Plugins are declarative JSON only — no executable code |
| `PluginMarketUnverified` | 未经验证 | Unverified |
| `PluginMarketUnverifiedHint` | 该插件由社区贡献，尚未经验证 | Community-contributed plugin, not yet verified |
| `PluginMarketBuiltinProtected` | 该 id 与内置插件冲突，安装被拒绝 | This id conflicts with a built-in plugin; install rejected |
| `PluginMarketHashMismatch` | 下载内容校验失败，已中止安装 | Download integrity check failed; install aborted |
| `PluginMarketIdMismatch` | 下载内容与索引声明的 id 不一致，已中止安装 | Downloaded id does not match the index; install aborted |
| `PluginMarketNotManaged` | 该插件不是通过本市场安装的，无法卸载 | Not installed via the market; cannot uninstall |
| `PluginMarketTargetExists` | 目标文件已存在且非本市场安装，已拒绝覆盖 | Target file exists and was not installed via the market; refusing to overwrite |
| `PluginMarketBadSource` | 下载地址不在允许的插件源范围内 | Download URL is outside the allowed plugin source |
| `PluginMarketSchemaMismatch` | 插件索引格式不兼容 | Plugin index format is incompatible |
| `PluginMarketNeedAdmin` | 写入程序目录失败，请以管理员身份重新运行 | Cannot write to the program directory; please run as administrator |
| `PluginMarketStatusOk` | 开箱即用 | Ready to use |
| `PluginMarketStatusService` | 需配套服务 | Requires a companion service |
| `PluginMarketStatusCode` | 需改代码 | Requires code changes |
| `PluginMarketReinstall` | 需重装 | Reinstall required |

> 精确 key 名以实现时的 `LanguageManager.T(...)` 调用点为准 —— **先改代码确定 key 名，再补语言包**，避免两边对不上。

#### 2.2 同步到 9 个语言包

`zh` / `zh-tw` / `en` / `ja` / `ko` / `ru` / `de` / `es` / `fr` 全部补齐**同样的 key 集合**（缺一个就回退成 key 名，等于没国际化）。

翻译由 `en` 打底，其余语言按语义翻译。繁体用 `zh` 的译文做简繁转换（`繁` 字形差异：`軟體`/`软件`、`設置`/`设置` 等）。

#### 2.3 验证

- [ ] 9 个语言包 key 集合完全一致（写个临时脚本比对，key 数与名称逐一对应）
- [ ] 切到英文界面，市场页面所有文案为英文而非 key 名

---

### Task 3：客户端 —— 市场安全加固【修缺陷 B / C / G】

#### 3.1 `PluginConstants` 增加内置保护名单

```csharp
/// <summary>
/// 内置插件 id 保护名单：市场插件的 id 撞上其中任意一项都会被拒绝安装。
/// 维护提示：新增内置插件时，必须同步更新
///   - 本文件（客户端）
///   - 仓库 scripts/gen_index.py 的 BUILTIN_IDS
/// 重要提示：这份名单只是【第二道防线】。真正兜底的是 §3.2 的 L2 规则
/// （目标文件已存在且不在市场记录内 → 拒绝安装），它不依赖任何名单。
/// </summary>
public static readonly HashSet<string> BuiltinPluginIds = new(StringComparer.OrdinalIgnoreCase)
{
    "Bilibili", "Crypto", "Exchange", "GitHub", "Hitokoto", "Holiday",
    "ProxyLatency", "PublicIP", "Stock", "UniversalAPI", "Weather", "WeiboHot"
};
```

#### 3.2 `PluginMarket` 目录与落盘逻辑

> **★ 先补 using**：`PluginMarket.cs` 现有的 using 块（`System` / `Collections.Generic` / `IO` / `Linq` / `Net.Http` / `Text` / `Text.Json` / `Text.Json.Serialization` / `Threading` / `Threading.Tasks`）里**没有** `System.Text.RegularExpressions`，而下面要用 `Regex.IsMatch`。**照抄代码前必须先加**：
>
> ```csharp
> using System.Text.RegularExpressions;
> ```

```csharp
/// <summary>插件目录（程序目录内，与内置插件、主题、语言包同级）</summary>
public static string PluginsDir => Path.Combine(AppContext.BaseDirectory, "resources", "plugins");

/// <summary>落盘文件名：只由插件 id 决定，不信任索引给出的 file 字段</summary>
public static string GetInstallFileName(string pluginId)
    => pluginId + ".json";

/// <summary>目标路径是否安全（id 合法 + 不撞内置）</summary>
public static bool CanInstall(string pluginId, out string reason)
{
    if (string.IsNullOrWhiteSpace(pluginId)) { reason = "id 为空"; return false; }

    // ★ 用 \A..\z 而不是 ^..$：
    // .NET 的 `$`（非 Multiline）会匹配「串尾或串尾换行之前」，
    // 所以 "abc\n" 能通过 ^..$ 校验。实测：
    //     [regex]::IsMatch("abc`n", '^[A-Za-z0-9_-]{1,64}$')  ->  True  ← 漏过
    // 而 Path.GetInvalidFileNameChars() 含 LF/CR，落盘时会抛 ArgumentException。
    if (!Regex.IsMatch(pluginId, @"\A[A-Za-z0-9_-]{1,64}\z")) { reason = "id 含非法字符"; return false; }

    if (PluginConstants.BuiltinPluginIds.Contains(pluginId)) { reason = "与内置插件 id 冲突"; return false; }
    reason = "";
    return true;
}

/// <summary>
/// L2 核心规则：目标文件已存在且【不是本市场装的】→ 拒绝安装。
/// 这一条不依赖内置名单，同时覆盖：内置插件（含未来新增的）、用户手放的文件、旧版市场残留。
/// </summary>
public static bool IsTargetAvailable(string pluginId, out string reason)
{
    string path = Path.Combine(PluginsDir, GetInstallFileName(pluginId));
    if (!File.Exists(path)) { reason = ""; return true; }

    // 文件在，且确认是本市场装的这个 id → 允许（这是"更新"）
    var record = PluginMarketState.Find(pluginId);
    if (record != null && FileContentIdMatches(path, pluginId))
    {
        reason = "";
        return true;
    }

    reason = "目标文件已存在且非本市场安装（可能是内置插件或旧版残留），请先移除该文件";
    return false;
}

/// <summary>读取插件文件的 id 字段；读不出来返回 null</summary>
private static string FileContentIdMatches(string path, string expectedId)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("id", out var idEl))
        {
            string id = idEl.GetString() ?? "";
            return string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase);
        }
    }
    catch { }
    return false;
}
```

> **★ 为什么豁免条件要加「文件内容的 id == 记录 id」这一层**：
>
> 可复现的绕过路径 —— ①市场插件 `foo` 已装并有记录；②后续客户端新增内置插件 `foo`（内置 id 与市场 id 撞车），安装包的 `CopyToOutputDirectory=Always`（`LiteMonitor.csproj:44-47`）**覆盖**了 `resources/plugins/foo.json`；③用户点"更新"→ 若豁免条件只看"记录里有没有"，L2 放行 → **市场插件反手覆盖内置插件**。
>
> 加上"文件内容 id 必须仍等于记录 id"这一判定后，②之后文件内容 id 仍是 `foo`（内置同名也可能是 `foo`）… 注意：**内置插件若恰好同名同 id，内容 id 也相等**。因此这一判定还必须配合 §3.1 的 **L3 内置名单**做最终拦截 —— L3 命中即拒绝，**无论记录是否存在**。L2 + L3 合起来才闭合。
>
> 成本：一次文件读取，可忽略。

**`download_url` 前缀白名单（S6）**：

```csharp
/// <summary>允许的下载源前缀 —— 索引被投毒时不得让客户端去任意站点取内容</summary>
private static readonly string[] AllowedDownloadPrefixes =
{
    "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/"
};

if (!AllowedDownloadPrefixes.Any(p => entry.DownloadUrl.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    throw new InvalidDataException("下载地址不在允许的插件源范围内");
```

> **为什么需要**：sha256 也在同一份索引里，索引被改时二者可以一起被改。**前缀白名单把"能取内容的地方"限制在自家仓库**，这样即使索引被投毒，客户端也只会去自家 raw 域名取东西。

`InstallAsync` 改造后的完整流程：

```
1. 校验 entry 非空、download_url 非空
2. ★ 新增：download_url 必须命中允许的源前缀白名单
3. CanInstall(entry.Id) → 不通过则抛 InvalidDataException（区分"撞内置"与"id 非法"）
4. ★ 新增：IsTargetAvailable(entry.Id) → 目标已存在且非市场安装 → 拒绝
5. 下载内容（超时 30s）
6. 解析 JSON → 必须是合法 JSON、含非空 id
7. ★ 新增：解析出的 id 必须 == entry.Id，否则抛 InvalidDataException
8. ★ 新增：若 entry.Sha256 非空 → 对**HTTP 响应的原始字节**计算 SHA-256 并比对，不一致则抛 InvalidDataException
9. Directory.CreateDirectory(PluginsDir)
10. 落盘路径 = PluginsDir / GetInstallFileName(entry.Id)   ← ★ 不再用 entry.File
11. 写文件（UTF-8 无 BOM，与内置插件一致）
12. ★ 新增：写入 settings.MarketPlugins 记录（id / file / version / sha256 / installedAt）
```

**★ 第 8 步的字节口径（必须与仓库侧一致）**：

仓库侧算的是 `plugin.json` 的**原始文件字节**的 SHA-256。客户端**必须对 HTTP 响应的原始字节算**，不能先取字符串再重新编码：

```csharp
// ★ 不要用 GetStringAsync —— 字符串重新编码后的字节可能与原始字节不同
//   （BOM、非 UTF-8 charset），会导致误报"校验失败"
byte[] raw = await client.GetByteArrayAsync(entry.DownloadUrl, ct);
string content = Encoding.UTF8.GetString(raw);

string actualHash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
if (!string.IsNullOrEmpty(entry.Sha256) &&
    !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("下载内容校验失败（SHA-256 不匹配）");
```

> 当前 13 个插件都是无 BOM UTF-8，实践中不会触发，但**口径必须写死** —— 将来某个插件带 BOM 就会炸，而且报错信息会指向"网络问题"这种完全错误的方向。

**关于第 10 步**：即便索引里的 `file` 写的是 `../evil.json`，落盘路径也只由 `id` 决定，而 `id` 的字符集 `A-Za-z0-9_-` **不含路径分隔符与点号** → **路径穿越在结构上不可能发生**。第 4 步则保证**不会覆盖已存在的文件**（内置插件 / 用户手放的文件 / 旧版残留）。

#### 3.3 `Uninstall` 加所有权判定 + 落盘名校验

```csharp
/// <summary>卸载市场插件。只能卸载"由本市场安装并记录在案"的插件。</summary>
public static bool Uninstall(MarketPluginEntry entry, out string error)
{
    error = "";
    if (entry == null || string.IsNullOrEmpty(entry.Id)) { error = "缺少 id"; return false; }

    // 1) 只有记录在案的市场插件才允许卸载
    var record = PluginMarketState.Find(entry.Id);
    if (record == null) { error = "该插件非本市场安装，无法卸载"; return false; }

    // 2) ★ 落盘文件名必须由 id 唯一推导，不接受 settings.json 里写入的任意路径。
    //    否则一个被篡改的 File: "../../LiteMonitor++.exe" 就能删掉程序目录内的任意文件。
    string expected = GetInstallFileName(record.Id);
    if (!string.Equals(record.File, expected, StringComparison.OrdinalIgnoreCase))
    { error = "安装记录异常（文件名与 id 不匹配）"; return false; }

    // 3) ★ 内置插件永不可卸载
    if (PluginConstants.BuiltinPluginIds.Contains(record.Id))
    { error = "内置插件不可卸载"; return false; }

    string path = Path.Combine(PluginsDir, expected);
    if (File.Exists(path)) File.Delete(path);

    PluginMarketState.Remove(entry.Id);
    return true;
}
```

#### 3.4 新增 `PluginMarketState`（安装记录）

```csharp
public class MarketPluginRecord
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";        // 落盘文件名（= id + ".json"）
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";       // 安装时的内容哈希，用于更新检测
    public string InstalledAt { get; set; } = "";  // ISO8601
}
```

挂在 `Settings` 上：

```csharp
// ====== 插件市场 ======
/// <summary>市场安装的插件记录：所有权判定 + 更新检测的唯一依据</summary>
public List<MarketPluginRecord> MarketPlugins { get; set; } = new List<MarketPluginRecord>();
```

`PluginMarketState` 提供 `Find(id)` / `Upsert(record)` / `Remove(id)` 三个方法，内部走 `Settings.Load()` + `Save()`。

> **旧版兼容**：已有用户的 `settings.json` 里没有 `MarketPlugins` 字段 → `System.Text.Json` 反序列化为空列表 → 行为正常（只是旧的市场插件无法通过市场卸载，用户可手动删文件）。需在 `Settings.Load` 里补一个 `null` 兜底（与 `WebServerPassword` 的既有写法一致）。

#### 3.5 启动自检 + `LoadPlugins` 去重

**(a) `PluginManager.LoadPlugins` 增加 id 去重**（修 S8/S9）：

```csharp
// ★ 排序后再遍历，保证去重的 tie-break 是确定性的
//   （否则留下的那个取决于 Directory.GetFiles 的枚举顺序 —— 正是我们要消除的不确定性）
var files = Directory.GetFiles(directoryPath, PluginConstants.CONFIG_EXT)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

foreach (var file in files)
{
    // ... 解析 tmpl 后，_templates.Add(tmpl) 之前：
    if (_templates.Any(t => t.Id == tmpl.Id))
    {
        System.Diagnostics.Debug.WriteLine($"[PluginManager] 跳过重复 id 的插件: {tmpl.Id} ({file})");
        continue;
    }
    _templates.Add(tmpl);
}
```

> **为什么需要**：内置插件换了 id 时，旧文件不会被 `CopyToOutputDirectory=Always` 删除 → 新旧两个模板同时存在 → `FirstOrDefault` 取到哪个取决于 `Directory.GetFiles` 的枚举顺序，行为不确定。用户手动放的插件也可能撞 id。
>
> **★ 为什么还要排序**：只加去重判断的话，"留哪个"仍然取决于枚举顺序 —— 等于把不确定性问题从"取模板"挪到了"跳过谁"。**排序后取字典序第一个**，行为才真正确定。

**(b) 启动自检：发现疑似覆盖内置插件的残留文件**：

**调用点**：`Program.cs` 启动流程里、`PluginManager.Instance.LoadPlugins(...)` **之后**调用一次（自检依赖 `MarketPlugins` 记录，而记录在 settings 里，启动时已加载）。结果写入一个静态属性供市场页读取：

```csharp
// 新增：src/Plugins/PluginMarketState.cs 或独立文件
public static class PluginSelfCheck
{
    /// <summary>启动自检发现的疑似残留文件（文件名 ≠ 内容 id，且不在市场记录里）</summary>
    public static List<string> SuspiciousFiles { get; } = new();

    public static void RunOnce()
    {
        SuspiciousFiles.Clear();
        if (!Directory.Exists(PluginMarket.PluginsDir)) return;

        foreach (var f in Directory.GetFiles(PluginMarket.PluginsDir, "*.json"))
        {
            string stem = Path.GetFileNameWithoutExtension(f);
            if (PluginMarketState.Find(stem) != null) continue;      // 市场装的，正常
            string fileId = TryReadPluginId(f);
            if (fileId != null && !string.Equals(fileId, stem, StringComparison.OrdinalIgnoreCase))
            {
                SuspiciousFiles.Add(f);
            }
        }
    }
}
```

**市场页的呈现**：`PluginMarketPage` 打开时，若 `PluginSelfCheck.SuspiciousFiles.Count > 0`，在页面顶部显示一条醒目提示：

> ⚠️ 检测到 N 个疑似覆盖内置插件的文件（`PublicIP.json` 等）。这些文件可能来自旧版市场插件，已导致对应的内置插件被覆盖。请删除这些文件后重新安装本程序。

> 这个自检能发现**旧版 `PublicIP.json`（内含 id=`public_ip_info`）覆盖了内置 `PublicIP.json`** 的情况 —— 文件名与内容 id 不一致，且不在市场记录里。提示用户删除该文件或重装程序。
>
> **★ 补充判定**：自检除了"文件名 stem ≠ 内容 id"，还应覆盖"内容 id 与某内置插件 id 相同但文件名不同"的情况（内置 id 撞车场景）。

#### 3.5 验证

- [ ] 安装 id 撞内置的插件 → 被拒绝，提示明确
- [ ] 安装 id 含 `../` 的插件 → 被拒绝
- [ ] 安装 id 为 `"abc\n"`（尾部换行）的插件 → 被拒绝（`\A..\z` 生效，未抛 `ArgumentException`）
- [ ] 索引里 `file` 写成 `../evil.json` → 实际落盘仍是 `resources/plugins/{id}.json`
- [ ] 索引里 `download_url` 指向 `https://evil.example.com/x.json` → 被拒绝（源前缀白名单）
- [ ] 篡改索引的 `sha256` → 安装被中止
- [ ] 篡改下载内容的 `id` → 安装被中止
- [ ] 安装一个市场插件后卸载 → 文件被删，记录被清
- [ ] **卸载一个内置插件（手动构造 `Id=PublicIP` 的记录）→ 被拒绝，内置 `PublicIP.json` 完好**
- [ ] **手动构造 `Id=x, File="../../LiteMonitor++.exe"` 的记录 → 卸载被拒绝，程序文件完好**
- [ ] 安装时目标文件已存在且非市场安装（如把 `PublicIP.json` 改名成 `my_plugin.json`）→ 被拒绝
- [ ] `LoadPlugins` 遇到同 id 的两个模板 → 只加载第一个，日志有记录

---

### Task 4：客户端 —— 源锁死 + 更新检测【修缺陷 F，落实 D1】

#### 4.1 删除源输入框（D1）

`PluginMarketPage.cs` 中删除：

- `_srcInput` 字段；
- `LiteUnderlineInput` 的创建与 `rightBox` 布局；
- `LiteActionRow` 的"插件源"行（**或保留为一行只读文字**，显示固定源地址 + 一个"打开仓库"链接 —— 推荐后者，让用户知道源是什么，但不提供修改入口）。

`LoadIndexAsync` 改为直接使用 `PluginMarket.DefaultIndexUrl`，不再从输入框读 URL。

**同时**：`PluginMarketPage` 的 `OnShow` 里 `_index == null` 才自动拉一次 —— 保留，但增加**每次进入页面自动刷新**（因为现在有更新检测，需要拿到最新的 sha256）。建议改为进入页面即拉取（带 `_busy` 防抖）。

#### 4.2 更新检测 + 安装状态判定 + 删除失效的 `IsInstalled`

**★ 必须处理 `PluginMarket.IsInstalled`（`PluginMarket.cs:98-102`）**：

```csharp
// 现状：按 entry.File 判存在
public static bool IsInstalled(MarketPluginEntry entry)
{
    if (entry == null || string.IsNullOrEmpty(entry.File)) return false;
    return File.Exists(Path.Combine(PluginsDir, entry.File));
}
```

新布局下 `entry.File` 恒为 `"plugin.json"` → `File.Exists(PluginsDir/"plugin.json")` **恒为 false** → 调用点 `PluginMarketPage.cs:153` 的 `bool installed = PluginMarket.IsInstalled(entry);` 永远得到 `false` → **按钮永远显示「安装」，卸载按钮永不出现**。

**处置**：**删除 `IsInstalled`**，把判定改到市场页，用「记录 + 文件」二元判定：

```csharp
// PluginMarketPage.cs:153 附近
var record = PluginMarketState.Find(entry.Id);
bool fileExists = File.Exists(Path.Combine(PluginMarket.PluginsDir, PluginMarket.GetInstallFileName(entry.Id)));
bool installed = record != null && fileExists;
```

> **同理，`PluginMarket.IsSafeFileName`（`PluginMarket.cs:152-158`）也失去意义** —— 它的调用点 `PluginMarketPage.cs:258` 校验的是 `entry.File`，而落盘名已由 id 推导（`GetInstallFileName`），`id` 本身有白名单校验。**一并删除**，否则留下一段永远通过的检查（误导后来人以为还有保护）。

**新的状态机**：

```
对索引中的每一条：
  record   = MarketPlugins.Find(entry.Id)
  fileExists = File.Exists(PluginsDir / GetInstallFileName(entry.Id))

  if (record == null)               → 未安装 → 按钮「安装」
  else if (!fileExists)             → ★ 记录在但文件没了（用户手动删了）→ 状态「需重装」→ 按钮「重新安装」
  else if (record.Sha256 != entry.Sha256 && entry.Sha256 != ""
                                    → 可更新 → 按钮「更新」（显示新版本号）
  else                              → 已安装 → 按钮「卸载」
```

> **★ 为什么加「需重装」这一档**：判定从"文件存在"改成"记录存在"之后，用户手动删了插件文件，市场页仍会显示「已安装」→ 点卸载时文件已经不在 → 用户困惑。`fileExists` 兜底让状态与磁盘实际一致。

**更新流程** = 安装流程 + 保留原记录中的 `installedAt`、更新 `version` / `sha256`。

**边界**：`entry.Sha256` 为空（老索引）时不做更新检测，退化为"已安装"。

#### 4.3 界面调整

- 状态徽标区增加「可更新」标记（用 `UIColors.TextWarn` 黄色，与 `UIColors.TextSub` 的"已安装"区分）。
- **★ 增加「未经验证」徽标**：`entry.Verified == false` 时，在插件名旁显示黄色徽标 + 提示「该插件由社区贡献，尚未经验证」（`Menu.PluginMarketUnverified` / `Menu.PluginMarketUnverifiedHint`）。这是仓库侧「派 Agent 审核」机制在客户端的落点（仓库侧计划 §3.5）。
- **★ 链接改为双入口**：`entry.Source`（源码）与 `entry.PageUrl`（插件介绍页，含 README 渲染）各一个链接。`Source` 字段**必须保留**（`PluginMarketPage.cs:200-204` 读它）。
- 标题行 `{name} v{version}` 保持。
- 描述下方增加一行小字：`插件为纯声明式 JSON 配置，不含可执行代码`（`Menu.PluginMarketHint`）—— 这是**面向用户的安全承诺**，与实际能力一致。
- 权限失败提示改为引导管理员运行（`Menu.PluginMarketNeedAdmin`）。

#### 4.4 索引 schema 版本校验（S7）

`PluginMarket.FetchIndexAsync` 里补：

```csharp
if (index.Schema != 1)
    throw new InvalidDataException($"插件索引格式不兼容（schema={index.Schema}，本程序支持 1）");
```

> `MarketIndex.Schema` 字段**早就存在**（`PluginMarket.cs:53`）但从来没人用过。§7 回滚表承诺了"不认识的 schema 视为空索引并提示" —— 这里把它真正实现。

#### 4.5 验证

- [ ] 界面无源地址输入框
- [ ] 修改 `PluginMarket.DefaultIndexUrl` 为无效地址 → 页面显示「加载失败」且**没有**修改源的入口
- [ ] 仓库里改一个插件的 version 与内容 → 重新拉索引后，该条显示「可更新」
- [ ] 点「更新」→ 本地文件内容变化，记录的 sha256 更新，界面变回「已安装」
- [ ] 手动删掉已装插件的文件 → 市场页显示「需重装」而非「已安装」
- [ ] `verified: false` 的插件显示「未经验证」徽标；`true` 的不显示
- [ ] 市场页的「查看源码」链接可点（`entry.Source` 有值）
- [ ] 索引的 `schema` 改成 2 → 页面显示「加载失败」并提示格式不兼容

---

### Task 5：客户端 —— 插件 `_en` i18n 支持【修缺陷 E，落实 D3】

#### 5.1 模型加字段

`PluginModels.cs`：

```csharp
public class PluginMeta
{
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    [JsonPropertyName("name_en")] public string NameEn { get; set; } = "";        // ★ 新增
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("author")]  public string Author { get; set; } = "";
    [JsonPropertyName("description")]    public string Description { get; set; } = "";
    [JsonPropertyName("description_en")] public string DescriptionEn { get; set; } = "";  // ★ 新增
    [JsonPropertyName("url")]     public string Url { get; set; } = "";
}

public class PluginOutput
{
    [JsonPropertyName("key")]         public string Key { get; set; } = "";
    [JsonPropertyName("label")]        public string Label { get; set; } = "";
    [JsonPropertyName("label_en")]     public string LabelEn { get; set; } = "";     // ★ 新增
    [JsonPropertyName("short_label")]  public string ShortLabel { get; set; } = "";
    [JsonPropertyName("short_label_en")] public string ShortLabelEn { get; set; } = ""; // ★ 新增
    // ... 其余不变
}
```

`PluginInput` 加 `label_en` → `LabelEn`。

> **★ 不给 `PluginDisplay` 加 `_en` 字段**：实测仓库 13 个插件**全都没有 `display` 段**，加了对所有插件都是死字段（YAGNI）。仓库侧计划 §2.2 有完整统计。
>
> `PluginDisplay` 现有的 `label` / `short_label` 也一样没人用 —— 整个 `display` 段可以先不动。

`MarketPluginEntry` 加 7 个字段（**一次性补齐，别分两次改**，字段名与仓库侧计划的 `index.json` schema 严格对应）：

```csharp
[JsonPropertyName("name_en")]        public string NameEn { get; set; } = "";
[JsonPropertyName("description_en")] public string DescriptionEn { get; set; } = "";
[JsonPropertyName("sha256")]         public string Sha256 { get; set; } = "";
[JsonPropertyName("size")]           public int Size { get; set; } = 0;
[JsonPropertyName("verified")]       public bool Verified { get; set; } = false;   // ★ 审核标记
[JsonPropertyName("page_url")]       public string PageUrl { get; set; } = "";    // ★ 插件介绍页
[JsonPropertyName("readme_url")]     public string ReadmeUrl { get; set; } = "";  // ★ README 原文
```

> `source` 字段**已存在**（`PluginMarket.cs:31`），市场页的「查看源码」链接读它 —— **不要删、不要改名**。仓库侧计划的 schema 已把它保留并改为目录式路径。

#### 5.2 新增 `PluginI18n`

见 §3.4 的代码。

#### 5.3 接入 13 个接入点

严格按 §3.4 的表格逐点改。**三个最容易漏的**：

1. `PluginExecutor.HandleExecutionError`（接入点 7）—— 漏了会导致插件报错时标签回退成中文，与正常时不一致；
2. `PluginMonitorSyncService.cs:83`（接入点 2）—— `labelPattern` 的 `tmpl.Meta.Name` 兜底，与 91/130 行的 `safeLabel` 兜底是**三处**不是一处；
3. `PluginMarket.StatusLabel`（接入点 13）—— 硬编码中文「开箱即用 / 需配套服务 / 需改代码」，不改的话英文界面下市场页仍混中文。

#### 5.4 验证

- [ ] 英文界面下，插件管理页与市场页显示英文插件名
- [ ] 英文界面下，监控项标签为英文（`PluginExecutor` 注入的 `PROP.Label.*`）
- [ ] 英文界面下，**托盘菜单与面板子菜单的标签语言一致**（两者读不同的数据源，只改一侧会混显）
- [ ] 中文界面下行为与改动前**完全一致**（回归）
- [ ] 某个插件没有 `_en` 字段 → 回退中文，不显示空白

---

### Task 6：单元测试

新建 `tests/LiteMonitor.Tests/PluginMarketTests.cs`：

| 用例 | 断言 |
|---|---|
| `GetInstallFileName_返回id加json后缀` | `"my_plugin"` → `"my_plugin.json"` |
| `GetInstallFileName_不信任索引file字段` | 索引 `file="../evil.json"` 时落盘名仍为 `"{id}.json"` |
| `CanInstall_拒绝非法id` | `"../x"` / `"a b"` / `""` → false |
| `CanInstall_拒绝撞内置的id` | `"PublicIP"` → false |
| `CanInstall_接受合法id` | `"public_ip_info"` → true |
| `Install_下载内容id不一致则中止` | 索引 id=A，下载内容 id=B → 抛异常且**未落盘** |
| `Install_sha256不匹配则中止` | → 抛异常且**未落盘** |
| `Uninstall_非市场安装的插件被拒绝` | 记录中无此 id → false 且内置文件仍存在 |
| `Uninstall_市场安装的插件被删除且记录清理` | → 文件消失，记录移除 |
| `MarketPlugins_空列表兼容旧settings` | 反序列化无该字段的配置 → 空列表，不抛异常 |

新建 `tests/LiteMonitor.Tests/PluginI18nTests.cs`：

| 用例 | 断言 |
|---|---|
| `Pick_中文界面选中文` | `CurrentLang="zh"` → 返回 zh |
| `Pick_英文界面选英文` | `CurrentLang="en"` → 返回 en |
| `Pick_英文缺失回退中文` | en 为空 → 返回 zh |
| `Pick_繁体界面选中文` | `CurrentLang="zh-tw"` → 返回 zh |
| `Pick_日韩等界面选英文` | `ja` / `ko` / `ru` → 返回 en |

> `LanguageManager.CurrentLang` 是 `private set`，测试中需要可控。方案：给 `LanguageManager` 加一个 `internal` 测试钩子，或在 `PluginI18n` 里把"是否英文"的判断抽成可传入 langCode 的重载：
> `public static bool ShouldPreferEnglish(string langCode)`，测试直接调这个重载。**推荐后者** —— 不污染生产 API。

---

### Task 7：真机验证

```powershell
cd G:\TOOL\LiteMonitor-fork
dotnet build -c Release
dotnet test  -c Release
```

真机（管理员身份）运行，逐条走查：

| # | 场景 | 期望 |
|---|---|---|
| 1 | 打开「插件市场」 | 列出 13 个插件，文案是正常中文（不是 key 名） |
| 2 | 无源地址输入框 | 界面上不存在可编辑的源地址 |
| 3 | 安装 `public_ip_info` | 成功；`resources/plugins/public_ip_info.json` 出现；**内置 `PublicIP.json` 完好** |
| 4 | 在「插件管理」页启用 | 监控项出现，标签正确 |
| 5 | 卸载它 | 文件消失，记录清理；内置插件仍在 |
| 6 | 仓库改一个插件后重新进市场 | 该条显示「可更新」 |
| 7 | 点「更新」 | 文件更新，状态变「已安装」 |
| 8 | 切英文界面 | 市场页与插件页均为英文 |
| 9 | 删掉 `resources/plugins/PublicIP.json` 后重启 | 程序正常启动（内置插件缺失不崩溃） |
| 10 | 断网后打开市场 | 显示「加载失败」+ 错误信息，不崩溃 |

---

### Task 8：文档与安全声明修正

**重要发现**：`使用说明(必读).txt` 第 66-68 行写着：

> * 插件系统支持从远程源下载并在本地执行第三方脚本，安装插件即等同于执行该脚本作者提供的代码，请只安装可信来源的插件。

**这与实际能力不符**。经全 `src/Plugins/` 目录核实，执行器只做 HTTP 请求 + 两个内建 `native://` 处理器 + JSON 提取转换，**没有任何脚本执行、动态编译或程序集加载能力**。

对「强绑定自家仓库」的市场而言，这段话是**反向的** —— 它把一个纯声明式的插件机制描述成了可执行任意代码的高危机制，会让用户不敢装插件。

**修正为**：

> * 插件为**纯声明式 JSON 配置**（声明去哪取数、怎么提取、怎么显示），**不具备执行第三方脚本或代码的能力**。
> * 但插件会按配置发起网络请求，可能把你的 IP / 填写的 API Key 发送给对应服务 —— 请只安装可信来源的插件。

同步修正 `README.md` / `README.en.md` 中对应的描述段落（若有）。

**同时更新**：

| 文件 | 改动 |
|---|---|
| `使用说明(必读).txt` | 安全声明改写；补充「插件市场」小节（源地址、市场插件存放位置、卸载说明） |
| `README.md` / `README.en.md` | 补充插件市场章节 |
| `resources/plugins/PLUGIN_DEV_GUIDE.md` | 补充 `_en` 双语字段说明 + 命名规范（文件名 = `id + ".json"`） |

---

## 6. 验证清单（合并版）

**仓库侧**（`G:\TOOL\awesome-litemonitor-plugins`）—— **详见仓库侧计划 §6**：

- [ ] `https://raw.githubusercontent.com/.../main/index.json` 返回 200 + 合法 JSON
- [ ] `plugins` 数量 = 13，每条含 `id`/`name`/`version`/`file`/`download_url`/`sha256`
- [ ] 每条 `file` == `"plugin.json"`，且 `download_url` 可下载且 sha256 一致
- [ ] `plugins/` 下为 13 个目录，目录名 == 插件 id
- [ ] 故意提交一个 id 撞内置的插件 → CI 失败

**客户端侧**（`G:\TOOL\LiteMonitor-fork`）：

- [ ] `dotnet build -c Release` 无错误无警告
- [ ] `dotnet test -c Release` 全绿
- [ ] 市场页无源输入框、列出 13 个插件、文案正常
- [ ] 安装不覆盖内置插件；卸载不删除内置插件
- [ ] sha256 / id 一致性校验生效
- [ ] 更新检测可用
- [ ] 中英文界面切换表现正确

**文档侧**：

- [ ] 安全声明已改写为「纯声明式 JSON，无代码执行能力」
- [ ] README / 使用说明 / 插件开发指南 已更新

---

## 7. 回滚方案

| 变更 | 回滚方式 |
|---|---|
| 仓库 CI / 脚本 | 仓库侧独立，直接 `git revert`；客户端不依赖它也能启动 |
| `index.json` 格式 | `schema` 字段做版本判断；不认识的 schema 视为空索引并提示 |
| 客户端安全加固 | 改动集中在 `PluginMarket.cs` / `PluginMarketState.cs`，可用 `git revert` 单个 commit 回退 |
| `MarketPlugins` 设置项 | 新增字段，旧版读不到会忽略；回退客户端后残留字段无害 |
| i18n `_en` 支持 | 新增字段 + 选词；回退后插件仍显示中文（行为等同改动前） |
| 语言包补 key | 纯新增 key，删掉即回到当前行为（显示 key 名） |
| 删除源输入框 | 若用户强烈需要自定义源，可恢复输入框并把默认值改回 `DefaultIndexUrl`（**但这会违背 D1，需用户重新拍板**） |

---

## 8. 附录 A：本计划**没有**做的事（YAGNI）

| 项 | 理由 |
|---|---|
| 插件签名 / GPG 校验 | 源已强绑定到自家仓库，sha256 足够；签名体系成本高收益低 |
| 插件市场的多源 / 第三方源 | 违背 D1 |
| 插件市场的服务端（API） | GitHub raw + Actions 已足够；引入服务端等于引入运维负担 |
| 插件依赖管理 / 依赖解析 | 当前插件均为独立 JSON，无依赖声明需求 |
| 插件评分 / 评论 / 收藏 | 需要服务端与账号体系，超出当前目标 |
| 插件自动更新开关 | 当前策略是"手动点更新"，够用 |
| 把市场插件装到 AppData | 违背 D2 |
| 运行时检测内置插件（目录扫描） | 同目录无法区分内置与市场；改用常量名单 |
| 繁体 / 日韩等语言的插件文案 | 仓库只提供 `_en`，其余语言回退中文（可后续补 `_ja` / `_ko` 等，schema 已预留扩展空间） |

---

## 9. 附录 B：风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| 装在程序目录，遇 `C:\Program Files\` 无写权限 | 安装失败 | 捕获 `UnauthorizedAccessException` 并明确提示以管理员运行；程序本身已需管理员（PawnIO 驱动） |
| 内置 id 名单在 C# 与 Python 两处重复 | 新增内置插件时漏改其中一处 | **名单只是第二道防线**；真正兜底的是 L2「不覆盖未记录文件」规则（§3.3），名单漏改不致造成覆盖。两处文件互相注释标注同步要求 |
| 落盘文件名改为 `{id}.json`，与旧版已安装的市场插件文件名不同 | 旧版落盘名是 `entry.File`（= `PublicIP.json`），**与内置文件同名 → 旧版已覆盖内置插件**；且旧版没有安装记录，无法"按记录清理" | 旧版市场从未正式发布（缺陷 D 说明无人用过），实际影响极低；L2 规则会拒绝在已有文件上安装；加启动自检发现「文件名 ≠ 内容 id」的残留文件并提示用户删除或重装（§3.3 / §3.5） |
| 索引格式变更导致老客户端解析失败 | 老客户端市场页空白 | `schema` 字段 + 客户端 `FetchIndexAsync` 校验未知 schema 并报错（Task 4.4） |
| 仓库 CI 生成 `index.json` 需要 `contents: write` 权限 | 私有 fork 或受限仓库可能失败 | 仓库为 public，Actions 默认可用；若被限制，改用本地脚本手动提交 |
| i18n 改动遗漏某处接入点 | 中英混显 | §3.4 已逐点列出行号；Task 5 逐点勾选 |

---

## 10. 附录 C：相关文件速查

| 关注点 | 文件:行 |
|---|---|
| 索引 URL 定义 | `src/Plugins/PluginMarket.cs:66-67` |
| 插件目录定义 | `src/Plugins/PluginMarket.cs:70` |
| 安装落盘逻辑 | `src/Plugins/PluginMarket.cs:134-136` |
| 卸载逻辑 | `src/Plugins/PluginMarket.cs:140-147` |
| 文件名安全检查 | `src/Plugins/PluginMarket.cs:152-158` |
| 语言 key 缺失回退逻辑 | `src/Core/LanguageManager.cs:71-79` |
| 插件加载与失效实例清理 | `src/Plugins/PluginManager.cs:39-120` |
| 标签注入（运行时） | `src/Plugins/PluginExecutor.cs:645-648` |
| 标签注入（错误路径） | `src/Plugins/PluginExecutor.cs:695-698`, `729` |
| 标签同步（写入 `DynamicLabel`，**不持久化**） | `src/Plugins/PluginMonitorSyncService.cs:82-91`, `130` |
| 标签反查 | `src/Plugins/PluginMonitorSyncService.cs:215-230` |
| 内置插件打包 | `LiteMonitor.csproj:44-47` |
| 市场页面 | `src/UI/Settings/PluginMarketPage.cs` |
| 插件管理页 | `src/UI/Settings/PluginPage.cs:204-244` |
| **仓库侧计划** | `docs/superpowers/plans/2026-10-20-plugin-repo-structure.md`（仓库结构 / CI / PR 流程 / index.json schema） |
