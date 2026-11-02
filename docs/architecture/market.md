# 插件市场与主题市场

本文描述 LiteMonitor++ 插件市场与主题市场的架构：两个市场共用的远端索引契约、下载源的信任边界与地址校验规则、内容完整性的判定口径、安装与卸载的落盘事务与崩溃恢复、路径安全约束、主题包形态与主题校验错误码、卸载守卫、市场页的两层现场渲染，以及外部插件仓库的目录与贡献规范。

适用范围是 `src/Plugins`（`PluginMarket`、`ThemeMarket`、`ThemeMarketValidator`、`PluginMarketInstallCore`、`PluginMarketUninstallCore`、`MarketFileIo`、`PluginMarketState`、`ThemeMarketState`）、`src/UI/Settings`（`PluginMarketPage`、`ThemePreviewService`、`MarketPageCache`）、`src/ThemeEditor`（`ThemePreviewControl`、`GpuThemePreviewControl`、`PreviewFrameSource`）、`src/Core/ThemeManager.cs` 的主题发现逻辑，以及 `src/Cli/Program.cs` 提供的校验命令。插件的声明式执行模型与沙箱配额、主题文件的字段语义与渲染参数取值域，均不在本文范围。

---

## 1. 总体结构

### 1.1 两个市场，一份索引

插件市场与主题市场是**同一份远端索引的两个视图**，不是一个市场的两种资产。

- 索引只有一处：`PluginMarket.DefaultIndexUrl` 指向社区插件仓库主分支上的 `index.json`。
- 索引内部分两段：顶层的 `plugins` 段与 `themes` 附加段。`total` 是插件条目数，`themes_total` 是主题条目数。
- 两类资产共用同一套下载源白名单、同一套摘要口径、同一套安装事务与崩溃恢复机制。
- 差异只体现在三处：落盘目录、落盘文件名规则、内容校验规则。

⇒ 主题市场不是插件市场的特例分支，而是同一套市场机制在「资产类型」维度上的一个取值。

### 1.2 组件与职责

| 组件 | 位置 | 职责 | 明确不负责 |
|---|---|---|---|
| `PluginMarket` | `src/Plugins/PluginMarket.cs` | 索引拉取、下载源白名单、地址校验、下载与摘要校验、插件安装入口、插件卸载入口、启动恢复入口 | 不做主题特有校验，不决定落盘文件名以外的策略 |
| `ThemeMarket` | `src/Plugins/ThemeMarket.cs` | 主题索引消费、主题载荷下载通道、主题安装入口、主题卸载守卫 | 不重复实现摘要与白名单逻辑，复用 `PluginMarket` |
| `ThemeMarketValidator` | `src/Plugins/ThemeMarketValidator.cs` | 主题内容校验，产出稳定错误码 | 不联网、不写文件、不依赖 Settings 初始化 |
| `PluginMarketInstallCore` | `src/Plugins/PluginMarket.cs` | 写前日志、暂存、原子替换、安装记录更新、启动对账 | 不理解资产类型语义 |
| `PluginMarketUninstallCore` | `src/Plugins/PluginMarketUninstallCore.cs` | 卸载归属判定、卸载事务、卸载回滚 | 不决定「当前使用中的主题要先切走」这类策略 |
| `MarketFileIo` | `src/Plugins/MarketFileIo.cs` | 共享读、原子替换、有界重试 | 不做路径合法性与内容判定 |
| `PluginMarketState` / `ThemeMarketState` | `src/Plugins/` | 安装记录的读写（插件挂 `Settings.MarketPlugins`，主题挂 `Settings.Themes`） | 不做安装判定 |
| `ThemePreviewService` | `src/UI/Settings/ThemePreviewService.cs` | 卡片层快照的磁盘缓存与离屏渲染调度 | 不持有活跃预览 |
| `PluginMarketPage` | `src/UI/Settings/PluginMarketPage.cs` | 市场页的列表、卡片、选中层预览、安装与卸载动作 | 不做校验与落盘 |
| `ThemeManager` | `src/Core/ThemeManager.cs` | 主题目录枚举与主题发现 | 不参与市场流程 |

### 1.3 目录与落盘位置

| 资产 | 安装目录 | 落盘文件名 | 发现方式 |
|---|---|---|---|
| 插件 | `AppContext.BaseDirectory/resources/plugins` | `{id}.json` | `PluginManager.LoadPlugins` 非递归扫描 `*.json` |
| 主题 | `AppContext.BaseDirectory/resources/themes` | `{id}.json` | `ThemeManager.GetAvailableThemes` 枚举 `*.json`，文件名去扩展名即主题名 |

两个目录都是**平铺**结构。主题安装目录与 `ThemeManager.ThemeDir` 是同一目录 ⇒ 内置主题、用户手放主题、市场安装主题共享同一个命名空间，不存在独立的市场目录。

---

## 2. 索引契约

### 2.1 索引顶层结构

`MarketIndex` 的 JSON 字段：

| 字段 | 类型 | 语义 |
|---|---|---|
| `schema` | int | 索引格式版本 |
| `updated_at` | string | 生成时间（ISO 8601 UTC） |
| `repo` | string | 仓库标识 |
| `total` | int | 插件条目数 |
| `plugins` | array | 插件条目 |
| `themes_total` | int | 主题条目数 |
| `themes` | array | 主题条目（附加段） |

`plugins` 与 `themes` 缺省时按空表处理 ⇒ 索引可以只含其中一段。

### 2.2 schema 版本策略

- 客户端支持的索引格式版本为常量 `PluginMarket.SupportedIndexSchema = 1`。
- 拉取索引后若 `schema != 1`，`PluginMarket.FetchIndexAsync` 抛出 `PluginMarketException`，原因为 `SchemaMismatch`，**不进入任何降级读取路径**。
- 主题以**加法附加段**（`themes` / `themes_total`）引入，`schema` 保持为 `1`。

⇒ 索引顶层 schema 版本号**不得提升**。提升会让只支持 `schema = 1` 的客户端整体拒收该索引，连本可读的插件段一起失效。新增能力必须落在既有顶层段内部的附加字段上。

### 2.3 插件条目字段

`MarketPluginEntry` 与索引 `plugins[]` 一一对应：

| 字段 | 语义 |
|---|---|
| `id` | 逻辑主键，必须与仓库目录名一致，同时是客户端落盘文件名的主键 |
| `original_id` / `renamed` | 改名留痕；未改名时 `original_id` 为空串、`renamed` 为 `false` |
| `name` / `name_en` | 中文名 / 英文名 |
| `description` / `description_en` | 中文简介 / 英文简介 |
| `author` | 作者 |
| `version` | 插件自身版本号 |
| `file` | 仓库内文件名，恒为 `plugin.json` |
| `download_url` | 载荷下载地址 |
| `readme_url` / `page_url` / `source` | 介绍页 / 目录页 / 源码链接 |
| `sha256` | 载荷原始字节的 SHA-256（小写十六进制） |
| `size` | 载荷字节数 |
| `status` | `ok` / `needs-service` / `needs-code` |
| `requires` | 额外准备要求数组 |
| `note` | 自由备注 |
| `verified` | 是否经实机验证 |

### 2.4 主题条目字段

`MarketThemeEntry` 与索引 `themes[]` 一一对应：

| 字段 | 语义 |
|---|---|
| `id` | 主题 id，同时是落盘文件名主键与仓库目录名 |
| `name` / `name_en` / `description` / `description_en` / `author` / `version` | 展示与发布元数据 |
| `file` | 仓库内文件名，恒为 `theme.json` |
| `download_url` / `page_url` | 载荷下载地址 / 目录页 |
| `sha256` | 载荷原始字节的 SHA-256 |
| `size` | 载荷字节数 |
| `route` | 渲染路线声明，`gpu` / `legacy`，缺省 `legacy` |
| `material` | 材质声明，`liquid_glass` / `cyber_punk` / `legacy`，缺省 `legacy` |
| `status` | 缺省 `ok` |
| `verified` | 是否经实机验证 |

`route` 与 `material` 不是自由文本：它们必须与载荷内 `rendering.backend` / `rendering.material` 互验一致，不一致即拒绝安装。

### 2.5 插件载荷清单格式

插件载荷是单文件 JSON，结构由 `PluginTemplate` 定义：

| 段 | 字段 |
|---|---|
| 顶层 | `id`、`meta`、`inputs`、`execution`、`outputs`、`dynamic_host`、`display` |
| `meta` | `name`、`name_en`、`version`、`author`、`description`、`description_en`、`url` |
| `inputs[]` | `key`、`label`、`label_en`、`type`、`default`、`placeholder`、`options`、`scope` |
| `execution` | `type`、`method`、`interval`、`min_interval`、`url`、`body`、`headers`、`extract`、`process`、`steps` |
| `outputs[]` | `key`、`label`、`label_en`、`short_label`、`short_label_en`、`format_val`、`color`、`unit` |
| `display` | `label`、`short_label` |

`short_label` 取单个空格是「隐藏标签」的约定，在英文标签下同样成立。插件载荷必须含非空 `id`，且该 `id` 与索引声明一致（插件安装判定顺序见 §6.2）。

### 2.6 拉取语义

- 索引拉取与载荷下载是两条独立通道：索引使用 `PluginMarket` 的长生命周期 `HttpClient`，超时 30 秒，允许自动跳转。
- 载荷下载**禁止自动跳转**（`HttpClientHandler.AllowAutoRedirect = false`），跳转由 `PluginMarket.DownloadBytesAsync` 逐跳处理。
- 索引拉取失败时，市场页清空当前索引快照并在状态行给出失败原因与重试入口；列表区区分「加载中」「无条目」「有数据」三种状态，三者互不冒充。

### 2.7 失败原因枚举

市场操作失败统一以 `MarketErrorReason` 表达，`PluginMarketException` 携带该原因与技术细节，界面据此挑选本地化文案：

| 枚举值 | 含义 |
|---|---|
| `Unknown` | 无更具体原因 |
| `NoDownloadUrl` | 条目没有下载地址 |
| `BadSource` | 下载地址不在允许的下载源范围内 |
| `InvalidId` | 资产 id 含非法字符 |
| `BuiltinConflict` | 与内置插件 id 冲突 |
| `TargetExists` | 目标文件已存在且不是本市场安装的 |
| `HashMismatch` | 载荷的 SHA-256 与索引声明不一致 |
| `IdMismatch` | 载荷内容与索引声明的 id 不一致 |
| `NotManaged` | 不是通过本市场安装的，无法卸载 |
| `RecordCorrupted` | 安装记录异常（文件名与 id 不匹配） |
| `SchemaMismatch` | 索引格式不兼容 |
| `BadContent` | 载荷不是合法 JSON 或缺少 id |
| `MissingHash` | 索引条目没有提供可用的 SHA-256 摘要 |
| `ThemeContentRejected` | 主题内容未通过主题特有校验 |

---

## 3. 信任边界

### 3.1 固定源

允许的下载源是**精确主机名 + 路径前缀**的组合：

| 项 | 值 |
|---|---|
| 主机 | `raw.githubusercontent.com`（精确匹配，不做后缀或包含匹配） |
| 插件路径前缀 | `/mojingjing-git/awesome-litemonitor-plugins/main/plugins/` |
| 主题路径前缀 | `/mojingjing-git/awesome-litemonitor-plugins/main/themes/` |

路径前缀由 `PluginMarket.SourceForAsset` 按资产类型从仓库基路径与资产前缀拼接得到，两类资产共用同一套判定逻辑。

### 3.2 地址校验链

`PluginMarket.TryResolveAllowedDownloadUri` 按固定顺序判定，任一步不通过即整体拒绝（结果原因为 `BadSource`）：

1. 地址非空；
2. 长度不超过 `MaxDownloadUrlLength`（2048）；
3. 整串不含反斜杠；
4. 可解析为绝对 URI；
5. scheme 为 `https`；
6. 不含 userinfo（不得出现 `user:password@` 形式）；
7. 端口为 443；
8. 原始路径通过 §3.3 的路径校验；
9. 主机名与白名单**精确相等**；
10. 原始路径以对应资产前缀开头。

校验对象是**地址串的原始写法**的路径部分（`PluginMarket.ExtractRawPath`），不是 `Uri` 规范化后的路径。`Uri` 会把 `/a/../b` 规范化成 `/b`，规范化后再判定就看不出穿越意图。

### 3.3 原始路径校验规则

`PluginMarket.TryValidateRawPath` 的规则：

- 路径必须以 `/` 开头；
- 不得含编码分隔符 `%2f` / `%5c`（大小写不敏感）；
- 不得含空段；
- 每段解码一次后不得为 `.` 或 `..`；
- 解码后的段不得含 `/` 或 `\`；
- 解码一次后仍含 `%xx` 转义序列的，判定为二次编码并拒绝。

⇒ 百分号编码分隔符、空段、`.` / `..` 穿越段、二次编码四类绕过路径全部被拒绝。

### 3.4 重定向

`PluginMarket.DownloadBytesAsync` 自行处理重定向：

- 每跳都用同一套白名单规则**重新校验**目标地址；
- 跳数上限 `MaxDownloadRedirects = 5`；
- 响应缺少 `Location` 或下一跳跳出白名单，即以 `BadSource` 失败。

自动跳转被显式关闭，是因为 `HttpClient` 内建跳转会在校验完成前就把请求发出去。

### 3.5 体积与时限

| 约束 | 值 | 作用对象 |
|---|---|---|
| 载荷下载上限 | 8 MiB（`MaxDownloadBytes`） | 插件 |
| 载荷下载上限 | 256 KiB（`ThemeMarketValidator.MaxThemeBytes`） | 主题 |
| 下载期限 | 30 秒（`DefaultDownloadTimeoutMs`） | 两者 |
| URL 长度上限 | 2048 | 两者 |

下载期限覆盖「响应头 + 正文读取」整段。`HttpClient.Timeout` 只约束到响应头，正文读取不受它约束，因此正文读取另有一条独立期限令牌；调用方令牌与期限令牌合并时**取最早**，用户主动取消必须立刻中断，不得退化为「只能等超时」。

正文读取先看 `Content-Length`，再在流式读取过程中累计实际字节数；任一处超限即中止。

### 3.6 SHA-256 的作用边界

摘要校验的**准确含义**是：下载到的原始字节与索引条目声明的 `sha256` 一致。

- 计算对象是 HTTP 响应的**原始字节**，不是解码后重新编码的字节。仓库侧则对提交文件的原始字节计算，两侧口径必须一致。
- 索引条目**没有提供可用摘要**（缺失、长度不是 64、含非十六进制字符）时，安装一律被拒，原因为 `MissingHash`。没有摘要不等于「不需要校验」，更不等于「可以装」。
- 摘要**不构成发布者身份验证**。索引与载荷来自同一个信任源，摘要只证明「收到的内容与索引声明的那份内容相同」；当索引本身被替换时，摘要与内容可以同时被替换。当前市场没有签名链、没有证书、没有独立于索引源的信誉根。
- `verified` 是仓库侧的人工实机验证记号，不是密码学属性，也不随载荷变更自动继承到新的内容上。

⇒ 摘要保证内容完整性，身份可信度由仓库侧的审核流程承担，两者是不同的机制。

---

## 4. 安装事务

### 4.1 事务标识与写前日志

- 每次安装事务有一个 32 位十六进制事务 id（`Guid.NewGuid().ToString("N")`）。
- 写前日志文件名固定为 `.market-install-txn.log`，与安装目录同目录；它不以 `.json` 结尾，不会被插件加载器扫到。
- 坏日志隔离文件名固定为 `.market-install-txn.quarantine.log`，同样不以 `.json` 结尾。隔离内容是坏日志原文的**逐字节**保留，外加拒绝原因。
- 日志条目字段：`txId`、`pluginId`、`target`、`stage`、`backup`、`sha256`、`recoveryVersion`、`hadTarget`、`previousFileSha256`、`previousRecordSha256`、`operation`。
- `operation` 取 `install`（缺省）或 `uninstall`。卸载事务的 `sha256` 语义是**卸载前的旧记录摘要**，与安装事务的「新载荷摘要」不同。

### 4.2 安装落盘顺序

`PluginMarketInstallCore.CommitInstall` 的顺序是固定的：

1. **写前日志**：把这笔事务追加进 `.market-install-txn.log`；
2. **同目录暂存**：把载荷写入 `{目标名}.{事务 id}.stage`，与目标同目录同卷；
3. **备份旧内容**：目标已存在时复制为 `{目标名}.{事务 id}.bak`；
4. **原子替换**：`MarketFileIo.MoveAtomic` 把暂存文件移为正式目标；
5. **更新安装记录**：`MarketPluginRecord` 写入 Settings（插件写 `Settings.MarketPlugins`，主题写 `Settings.Themes`）；
6. **清理**：删除备份文件，移除日志条目。

任一步抛出异常即回滚：旧文件原样恢复、旧安装记录原样恢复、暂存文件清理。回滚本身失败时，保留新载荷、日志与备份，交由启动恢复判定。

进程被强制终止（`MarketInstallCrashException`）不触发回滚，保留写前日志等待恢复。

安装记录 `MarketPluginRecord` 字段：`Id`、`File`、`Version`、`Sha256`、`InstalledAt`（ISO 8601）。

### 4.3 崩溃恢复

恢复入口：`PluginMarket.RecoverInterruptedInstalls`、`PluginMarket.RecoverInterruptedInstallsIn(dir)`、`PluginMarket.RecoverInterruptedInstallsIn(dir, findRecord)`。最后一个参数把记录来源按资产类型切换（插件用 `PluginMarketState.FindPersistedForRecovery`，主题用 `ThemeMarketState.FindPersistedForRecovery`）。

恢复流程：

1. 读取写前日志；日志不存在即无事可做；日志读不出来时保持原样，不做任何判定；
2. 日志解析失败（结构不对）即判为坏日志：原文逐字节隔离留证，**一个路径都不执行**，日志文件删除；
3. 逐笔处理在途事务；
4. 每笔事务的路径**一律不当路径使用**：按【资产 id + 事务 id】重建目标名、暂存名、备份名，再做目录边界与一致性校验；
5. 校验不通过的整笔拒绝，原文进隔离文件，不执行也不重试；
6. 校验通过的按「记录权威」对账：读到的安装记录与事务前态或事务后态一致才收尾，否则保留在日志中下次再试；
7. 卸载事务走独立分支，且**必须先于安装分支分流**。卸载事务的提交点是「安装记录被持久化删除」：记录仍在 ⇒ 事务未提交，载荷必须恢复存在（目标缺失时把备份原子移回）；记录已删 ⇒ 事务已提交，完成卸载并清理残留；
8. 单笔失败不影响其它笔；剩余在途事务写回日志，无剩余时删除日志文件。

恢复失败**不阻断启动**。每目录默认只做一次恢复；按目录显式入口以强制方式当场执行，保证「先恢复、后加载」。

加载侧顺序由 `PluginManager.LoadPlugins` 保证：它在扫描插件目录前先执行该目录的恢复。主题目录的兜底恢复由 `ThemeMarket` 的静态构造承担。

### 4.4 卸载事务

`PluginMarketUninstallCore.Uninstall` 的顺序：

1. **归属判定**（§7.1）；
2. **写前日志**：写入 `operation = "uninstall"` 的事务条目；
3. **备份**：目标存在时复制为 `{目标名}.{事务 id}.bak`；
4. **删除载荷**；
5. **删除安装记录**（提交点）；
6. **清理**：删除备份，移除日志条目。

提交点之后的清理失败不回滚，残留只是可回收的垃圾，由启动恢复幂等收尾。提交点之前的失败按回滚处理：备份原子移回目标，日志条目删除。

### 4.5 原子替换与文件占用

`MarketFileIo.MoveAtomic` 以带覆盖的 `File.Move` 为基础，在 `UnauthorizedAccessException` 与非「源不存在」类 `IOException` 上做**有界**重试：最多 4 次尝试，总预算 300 毫秒，退避 25 毫秒起步按指数增长。预算耗尽仍失败即抛 `MarketFileReplaceBlockedException`，调用方据此判定「目标被占用」，既有回滚路径照常清理，不把「重试耗尽」吞成成功。

所有读目标的入口统一使用 `FileShare.ReadWrite | FileShare.Delete` 的共享读：读者不挡住写侧改名挪走。但 Windows 的 replace-existing 要求目标没有任何打开的句柄，共享读救不了这一种，只有等待占用解除后的重试能救。

---

## 5. 路径安全

### 5.1 直接子路径解析

`PluginMarketInstallCore.TryResolveDirectChildPath` 只接受**叶子文件名**：

- 拒绝空名；
- 拒绝 `.` 与 `..`；
- 拒绝含目录分隔符或绝对路径的形式；
- 解析后的父目录必须与根目录相等，否则判为「路径越出插件目录」；
- 目标不得是重解析点。

`PluginMarketUninstallCore.TryGetDirectChildFilePath` 采用同口径的目录边界校验。

### 5.2 重解析点判定

`PluginMarketInstallCore.IsUntrustedReparsePoint` 按 fail-closed 判定：

- 文件或目录不存在 ⇒ 不是重解析点；
- 属性可读且带 `FileAttributes.ReparsePoint` ⇒ 不可信；
- **属性读不到 ⇒ 判定为不可信**。

### 5.3 落盘文件名只由 id 决定

落盘文件名由 `PluginMarket.GetInstallFileName(id)` 产生，形如 `{id}.json`，**不采信索引里的 `file` 字段**。id 的合法字符集是 `[A-Za-z0-9_-]{1,64}`，用 `\A..\z` 锚定（不用 `^..$`，因为 `$` 允许在末尾换行符前的位置匹配成功）。字符集不含路径分隔符与点号 ⇒ 路径穿越在结构上不可能发生。

主题落盘文件名同样是 `{id}.json`，与主题身份一致。

### 5.4 事务路径的一致性校验

恢复时重建的三个文件名必须与事务条目中记录的 `target` / `stage` / `backup` **逐字节相等**，事务 id 必须为 32 位十六进制，`recoveryVersion` 只能是 0 或 1。任一项不符即整笔拒绝。日志字段长得像路径（含分隔符、绝对路径、相对目录标记）时直接判定为逃逸形态并拒绝。

---

## 6. 主题市场

### 6.1 主题包形态

主题包是**纯 JSON 单文件包**，不压缩：

- 载荷就是仓库里的 `theme.json` 原始字节，单文件，无归档格式、无附加资源、无远程引用；
- 安装目标为 `resources/themes/{id}.json`；
- 主题身份**以文件名认定**：`ThemeManager.GetAvailableThemes` 枚举 `*.json`，文件名去扩展名即主题 id，与 `Settings.Skin`、市场安装记录、卸载流程使用同一个标识；
- 完整性由**摘要与大小**钉住：索引 `sha256` 校验载荷原始字节，索引 `size` 与实际字节数必须相等，安装记录再存一份 `sha256` 用于后续更新与覆盖判定。

### 6.2 主题安装顺序

`ThemeMarket.InstallAsync` 的判定顺序：

1. 主题 id 合法（否则 `InvalidId`）；
2. 对账主题目录里未完成的安装事务；
3. 目标可用性检查（否则 `TargetExists`，见 §7.1）；
4. 载荷下载通道 `ThemeMarket.DownloadThemePayloadAsync`：下载地址存在（否则 `NoDownloadUrl`）→ 白名单解析（否则 `BadSource`）→ 摘要强制（否则 `MissingHash`）→ 下载（256 KiB 上限）→ 摘要比对（否则 `HashMismatch`）→ 主题内容校验（否则 `ThemeContentRejected`）；
5. 事务落盘（复用 §4.2 的安装事务核心）。

插件安装的判定顺序同理：下载地址存在 → 白名单 → id 合法且不撞内置插件 → 对账未完成事务 → 目标可用性 → 摘要强制 → 下载 → 摘要比对 → 内容必须是合法 JSON 且含非空 id（否则 `BadContent`）→ 载荷 id 与索引 id 一致（否则 `IdMismatch`）→ 事务落盘。

### 6.3 主题校验规则与错误码

`ThemeMarketValidator` 按固定顺序判定，前一条不通过即返回，错误码稳定：

| 序号 | 检查 | 失败错误码 |
|---|---|---|
| 1 | 文件大小 ≥ 1 字节 | `theme_empty` |
| 2 | 文件大小 ≤ 262144 字节 | `theme_too_large` |
| 3 | 声明大小（若提供）等于实际字节数 | `theme_size_mismatch` |
| 4 | 严格 UTF-8 解码成功（接受前导 BOM） | `theme_utf8_invalid` |
| 5 | JSON 解析成功（无注释、无尾随逗号、深度 ≤ 64） | `theme_json_invalid` |
| 6 | 根节点为对象 | `theme_root_not_object` |
| 7 | 属性名与字符串值都不含 `http://` / `https://` | `theme_remote_reference` |
| 8 | 已知颜色字段全部可解析 | `theme_color_invalid` |
| 9 | 索引声明路线为 `gpu` 或 `legacy` | `theme_route_invalid` |
| 10 | 索引声明材质为 `liquid_glass` / `cyber_punk` / `legacy` | `theme_material_invalid` |
| 11 | `rendering` 段为对象或缺省 | `theme_rendering_invalid` |
| 12 | `rendering.backend` 存在且为字符串 | `theme_rendering_backend_missing` |
| 13 | `rendering.backend` 与索引路线一致 | `theme_route_mismatch` |
| 14 | `rendering.material` 与索引材质一致 | `theme_material_mismatch` |
| 15 | `rendering` 段通过渲染配置校验 | `theme_rendering_invalid` |
| 16 | 文件可读 | `theme_read_failed` |

全部通过时错误码为 `ok`。

关键口径：

- `rendering` 段缺省或为 `null` 时，按纯 legacy 主题解释，此时索引声明的路线与材质必须都是 `legacy`，否则报 `theme_route_mismatch`。
- 第 15 条复用 `ThemeRenderingConfig.Validate()` 的全规则集，不另立弱口径。
- 颜色检查只在主程序侧参与；命令行与市场校验器共用的共享编译配置不执行该检查，其余规则完全一致。
- `rendering.fallbackStyle` 的非法取值只产出诊断码 `gpu_fallback_style_invalid`，不参与「整份主题是否可用」的判定。

### 6.4 校验命令

`src/Cli/Program.cs` 提供一条命令：

```
theme-validate --path=<file> [--route=gpu|legacy] [--material=liquid_glass|cyber_punk|legacy] [--declared-size=<bytes>]
```

- 标准输出恰好一个 JSON 对象，字段为 `ok`、`command`、`path`、`route`、`material`、`error_code`、`message`；标准错误一行人类可读摘要。
- 退出码：`0` 校验通过、`1` 校验失败、`2` 环境不具备、`64` 参数错误、`70` 未处理异常。
- 命令行与客户端**复用同一份 `ThemeMarketValidator`**（共享编译），保证两侧判据逐条一致。

---

## 7. 卸载守卫

### 7.1 归属判定

卸载只允许作用于「由本市场安装且记录在案」的资产。`PluginMarketUninstallCore.TryCreatePlan` 在**任何磁盘动作发生前**完成全部判定，任一项不通过即拒绝并给出原因：

| 判定 | 失败原因 |
|---|---|
| id 合法 | `InvalidId` |
| 不撞内置插件名单 | `BuiltinConflict` |
| 存在本市场的安装记录 | `NotManaged` |
| 记录 id 与条目 id 一致 | `RecordCorrupted` |
| 记录的文件名与目标文件名一致 | `RecordCorrupted` |
| 载荷存在时，内容身份与记录一致 | `RecordCorrupted` |

内容身份判据按资产类型区分：插件是载荷 JSON 的 `id` 字段与记录 id 相同；主题是文件可解析为 JSON 对象（主题 JSON 顶层没有 id 字段，其完整性由记录摘要比对兜底）。

主题没有内置常量表：「内置」等价于「目录里已存在但没有本市场记录的文件」，由目标可用性判定覆盖，`builtinIds` 传空。

### 7.2 目标可用性

`PluginMarket.IsTargetAvailable` 决定目标文件是否允许被市场写入：

- 文件不存在 ⇒ 可写；
- 文件存在但**没有**本市场安装记录（内置主题、用户手放文件、其它来源残留）⇒ 拒绝，原因 `TargetExists`；
- 文件存在、有安装记录、记录指向该文件、且内容身份一致 ⇒ 允许覆盖（更新流程）；
- 记录里的摘要可用时，还要重算磁盘文件摘要；与记录不一致说明用户安装后改过这个文件 ⇒ 拒绝覆盖，提示先卸载。

⇒ 市场不得静默覆盖用户改动过的文件，也不得覆盖不是自己装的文件。

### 7.3 使用中的主题必须先切走

卸载一个**当前正在使用**的主题（`Settings.Skin` 与主题 id 相同）时：

- 必须先完成主题切换，切换成功才执行卸载；
- 切换手段由调用方注入（`ThemeMarket.TryUninstall` 的 `switchToDefaultTheme` 参数），市场层自己不碰运行时；
- 调用方没有提供切换手段 ⇒ 卸载失败；
- 切换过程中抛出异常 ⇒ 文件不删，安装记录保留。

市场页注入的切换动作把 `Settings.Skin` 指向出厂默认主题 `RefinedDark`，经 `AppActions.ApplyThemeAndLayoutWithResult` 应用，并要求运行时主题模块的应用结果为成功，否则视为切换失败。

卸载与切换的顺序保证：记录保持与文件删除必须同进同退——切换成功但卸载失败时，原文件与安装记录保留，当前画面可以保持备用主题，界面如实提示失败，不得报告整项成功。

---

## 8. 市场页与两层现场渲染

### 8.1 页面与类别

- 设置侧栏的「插件市场」与「主题市场」两个入口指向**同一个** `PluginMarketPage` 实例，进入时通过 `RequestCategory` 声明类别（`MarketCategory.Plugins` / `MarketCategory.Themes`）。
- 页内也可切换类别，切换会同步侧栏状态。
- 页面内容签名 `MarketContentSignature` 由八个维度构成：资产类别、当前类别那一段的索引摘要、安装状态摘要、选中项、语言、DPI、设置配色、请求代次。签名相同则不重建列表控件，签名变化只做行级更新。

### 8.2 卡片层

主题列表的每一行带一张卡片快照，由 `ThemePreviewService` 提供：

- **磁盘缓存**：目录为 `%LocalAppData%\LiteMonitor++\theme-previews`，缓存键为**主题 id + 摘要前 12 位**，尺寸 360×200，总量上限 50 MB，超限时按写入时间从旧到新淘汰。摘要变化时键自动变化 ⇒ 版本更新会让旧快照失效。
- **命中缓存**同步回调；未命中则入队，由串行泵处理：下载在后台线程，渲染回到 UI 线程；同一个主题 id 不重复排队。
- **GPU 主题**走离屏渲染：隐藏宿主窗体 + `GpuThemePreviewControl.TryCaptureOnce`，同步渲一帧并从 GPU 帧缓冲回读 PNG。离屏宿主使用一次性临时目录的启动守卫，不污染生产的持久状态，释放即删除该临时目录。
- **legacy 主题**走 `ThemePreviewControl.RenderPreview`，用正式渲染管线画到内存位图。
- **GPU 不可用**（显卡不支持、远程会话、启动守卫限流）⇒ 回退为 legacy 示意帧，并对 GPU 主题叠加「需要 DirectX 11」的标注。

### 8.3 选中层

- 任一时刻市场页只保留**唯一活跃**的实时预览实例（`ThemePreviewControl`）。选中主题时先释放旧实例再建立新的。
- 载荷在后台线程下载，完成后回到 UI 线程写入预览。
- **代次守卫**：写入前必须同时满足「页面代次未变」与「预览实例仍是当前实例」，否则丢弃该回调并计数。离开页面、切换类别即释放活跃预览，释放后到达的回调不得复活预览。
- 卡片层与选中层互不干扰：预览宿主与列表数据解耦，列表重建不影响正在进行的选中层预览。

### 8.4 预览诚实性

预览**一律使用程序化背景与演示数据，绝不抓取真实桌面**：

- legacy 预览由 `ThemePreviewControl` 的预览专用帧来源提供固定样例图，该帧来源不含任何抓屏调用；玻璃路线叠加「预览样例图（非实时桌面捕获）」提示。
- GPU 预览由 `GpuThemePreviewControl` 承担，它不构造任何抓屏源，渲染输入固定，场景通知标记为「GPU PREVIEW / FIXED INPUT / CAPTURE 0」。
- 演示分组由 `ThemePreviewControl.CreateMockGroups` 生成固定数值。
- GPU 预览失败时，`ThemePreviewControl` 改用 `LegacyFallbackStyleResolver.BuildDerivedTheme` 派生的兼容主题渲染，并把预览控件的说明文本标注为 GPU 预览不可用。

⇒ 预览呈现的是主题在固定输入下的观感，**不承诺**真实桌面折射效果；界面上的提示文案与这一口径保持一致。

### 8.5 安装状态

市场页对每个条目给出四种状态之一：

| 状态 | 判定 |
|---|---|
| `NotInstalled` | 没有安装记录 |
| `NeedReinstall` | 记录在，但目标文件不存在 |
| `Updatable` | 已安装，且索引里的 `sha256` 与记录里的不同 |
| `Installed` | 已安装且是当前版本 |

「未安装」是有效状态，与「拉取失败」「记录缺失」是不同的语义，不得互相冒充。

---

## 9. 外部插件仓库规范

### 9.1 仓库位置

插件与主题的远端内容位于**独立仓库** `mojingjing-git/awesome-litemonitor-plugins`（其下载前缀与 §3.1 的白名单前缀一致）。该仓库**不在本仓库内**，它的目录结构、校验脚本与工作流属于本仓库之外的独立资产；本仓库只通过 `index.json` 消费其产物。

### 9.2 目录约定

```
awesome-litemonitor-plugins/
  plugins/
    <插件 id>/
      plugin.json     插件本体（机器可读配置）
      README.md       插件介绍（目录页由 GitHub 原生渲染）
      verified        可选空标记文件，存在即表示已实机验证
  themes/
    <主题 id>/
      theme.json      主题本体（严格 UTF-8，≤ 256 KiB，禁止远程引用）
      README.md       主题介绍
      verified        可选空标记文件
  scripts/
    gen_index.py          插件校验与索引生成
    gen_theme_index.py    主题校验与索引生成
    init_readme.py        从 plugin.json 生成 README 初稿
  .github/
    PULL_REQUEST_TEMPLATE.md
    workflows/
      gen-index.yml       校验与索引发布
  index.json        索引（生成产物）
  PLUGINS.md        插件清单（生成产物）
  THEMES.md         主题清单（生成产物）
  README.md         仓库定位、规则与贡献流程（手写）
  LICENSE
```

目录名即 id：插件目录名必须与 `plugin.json` 的 `id` 完全一致，且匹配 `^[A-Za-z0-9_-]{1,64}$`；主题目录名匹配 `^[a-z0-9][a-z0-9-]{1,39}$`。插件目录名不得与内置插件 id 冲突；内置名单在客户端 `PluginConstants.BuiltinPluginIds` 与仓库生成脚本 `BUILTIN_IDS` 各存一份，新增内置插件时两处必须同步。

### 9.3 索引生成

- `index.json` 是**生成产物**，禁止手工编辑；`PLUGINS.md` 与 `THEMES.md` 同理。
- 插件生成器校验目录名合法性、与内置名单冲突、目录重复、`plugin.json` 存在、`README.md` 存在、`id` 与目录名一致、`meta` 的 `name` / `description` / `author` / `version` 非空；双语字段缺失、`outputs[].label_en`、`outputs[].short_label_en`（原始值非空时）、`inputs[].label_en` 缺失产出**警告**；README 缺少 `## 功能说明`、`## 输出项`、`## 注意事项` 产出**错误**；`## 配置项` 在插件有 `inputs` 时是错误、无 `inputs` 时是警告；`## 使用说明` 缺失是警告。
- 主题生成器校验目录名合法与唯一、`theme.json` 与 `README.md` 存在、严格 UTF-8、合法 JSON、顶层为对象、不含远程 `http(s)` 引用、体积 ≤ 256 KiB、`rendering.backend` 与 `rendering.material` 取值合法。
- 主题载荷里有两个互不相同的版本字段：顶层 `version` 是**主题格式版本**（整数），`market.version` 是**发布版本**（字符串）。索引的 `version` 取 `market.version`；两者不得互相猜测。
- 索引条目的 URL 由固定仓库、固定分支、合法 id 与固定文件名**构造**，不采用投稿者给出的任意下载地址。`sha256` 与 `size` 由生成器对最终载荷字节计算。
- `schema` 保持为 1（§2.2）；`total` 是插件条目数，`themes_total` 是主题条目数，两个计数互不覆盖。主题生成器只写顶层 `themes` 与 `themes_total` 附加段，插件段原样保留。
- 主题生成器的更新时间取自相关目录与脚本的最近一次提交时间 ⇒ 同一提交重复生成得到同一索引；插件生成器使用生成时刻的 UTC 时间。

### 9.4 `verified` 标记

- `verified` 是**人工维护的空标记文件**：文件存在即表示该资产经过实机验证。
- 生成器**只读**该文件，**永不写、永不覆盖**。这使 `index.json` 保持纯生成产物，同时允许人工标记。
- 语义是「已在记录的环境下实机跑通」，**不承诺**所有硬件兼容。载荷发生变更后，旧验证不再对应当前内容。
- 客户端对 `verified` 为假的条目显示「未经验证」徽标与说明文案。

### 9.5 贡献流程

插件：

1. 在 `plugins/<插件 id>/` 下放入 `plugin.json` 与 `README.md`；
2. 目录名与 `plugin.json` 的 `id` 一致；
3. 本地运行生成脚本的校验模式确认通过；
4. 提交拉取请求；
5. CI 校验（只读权限，不写文件），不合规则请求变红并给出报告；
6. 内容审核：核对字段完整性、`id` 与目录名一致性、请求域名是否合理、是否索取不必要的凭据、实机安装运行确认监控项有正常数值、README 的「注意事项」写清限制、作者署名真实；
7. 审核通过后创建 `verified` 标记文件；
8. 合并后 CI 生成并提交索引，客户端下次拉取索引即可看到新条目。

主题的流程同构，投稿物是 `themes/<主题 id>/theme.json` 与 `README.md`，展示元数据写在载荷的 `market` 段（`name`、`name_en`、`description`、`description_en`、`author`、`version`），`rendering.backend` / `rendering.material` 必须与实际内容一致。

### 9.6 工作流与权限

`.github/workflows/gen-index.yml` 分两个 job：

- **校验**：在拉取请求与主干推送上触发，运行插件与主题两个生成脚本的校验模式，上传校验报告。工作流权限为 `contents: read`，不写任何文件。
- **发布索引**：只在非拉取请求事件上触发，依赖校验 job 通过，权限为 `contents: write`；生成 `index.json`、`PLUGINS.md`、`THEMES.md` 并提交。

同一主干上的工作流串行执行，避免两次快速推送导致提交冲突。

---

## 10. 已知限制

- **摘要不提供发布者身份保证**。SHA-256 只保证「收到的字节与索引声明的字节相同」。索引源被替换时，摘要与载荷可以同时被替换；当前市场没有签名链或独立信誉根。
- **`verified` 不是密码学属性**。它是仓库侧的人工记号，含义是「已在记录环境下实机验证」，且不对应「所有硬件均兼容」。
- **预览无法呈现真实桌面折射**。所有预览一律使用程序化背景与演示数据，这是刻意的诚实性约束，不是渲染缺陷。
- **GPU 不可用时卡片退化为示意帧**。GPU 主题的卡片会退化为 legacy 示意帧并叠加「需要 DirectX 11」标注；选中层则改用兼容外观派生主题渲染。
- **市场无离线安装能力**。索引不持久化到本地，卡片快照缓存只是渲染产物缓存，不构成离线索引，也不支持离线安装。
- **主题 id 的字符集两侧不一致**。客户端接受 `[A-Za-z0-9_-]{1,64}`，仓库主题目录名要求 `[a-z0-9][a-z0-9-]{1,39}`；客户端是更宽的一侧。
- **主题内容禁止远程引用**。载荷的属性名与字符串值都不得出现 `http://` 或 `https://`，因此主题不能引用外部图片或字体资源。
- **插件内置名单双份维护**。内置插件 id 名单在客户端 C# 与仓库生成脚本各存一份，跨仓库无法共享，两处必须同步修改。
- **主题市场没有内置主题常量表**。内置主题的保护完全依赖「目标文件已存在且无本市场安装记录」这一判定。
