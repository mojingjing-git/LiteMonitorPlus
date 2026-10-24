# 修复计划独立复审 R2

日期：2026-10-23（Asia/Shanghai）  
结论：**REQUEST_CHANGES — R2 范围内 4 项 P2 计划阻塞，无新增 P1。**

审查基线：`9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f`。仓库根目录为 `G:\TOOL\LiteMonitor-fork`；下文代码锚点均相对此目录。

最终审查对象为 `docs/superpowers/plans/2026-10-23-independent-review-remediation-plan.md` 的 **v2**，SHA-256：`76985B6F4EFCFD4BFCF260BC6AD1A16E808D0E709B38190E3897305701CF6ED1`。先读上轮独立审查报告及 v1、核对对应代码与证据，收尾时仅定向核对 v2 的 §8～§13；上述四项阻塞在 v2 仍未处置。§6／§7 由 R1 复核，R2 不重复审查。

本次专门核对 F05～F08、T09、工作区分工和测试门禁，没有深入重复 R1 的 GPU 恢复或市场事务审查。仅新增本报告及 R2 独有临时目录中的小探针；没有修改计划、产品实现、现有测试或工作流，没有运行全量构建／测试，也没有派实施 Agent。

## 阻塞项及最小修订

### R2-P2-01：scope-id 必须在信息丢失前处理，并覆盖请求身份与缓存

**计划锚点：**v2 §8，第 134～139 行，尤其第 137 行“按 Uri 的合法性和既有支持边界处理”。

**代码证据：**`PluginProcessor.cs:1075`、`PluginExecutor.cs:846`／`:1214`、`Native/CryptoNative.cs:278`／`:616` 都先取 `Uri.Host`。此外，`PluginExecutor.cs:1593` 的 `CanonicalizeUrl()` 在 `:1601` 也使用 `uri.Host`；其结果进入 `RequestIdentity`，用于 `:445`～`:458` 的缓存和 `:509` 的在途请求归并。

R2 在 **.NET 8.0.32** 上读取既有基线 Release 产物，调用生产规范化方法，得到：

| 原地址 | `Uri.Host` | 生产 `CanonicalizeUrl()` |
| --- | --- | --- |
| `http://[fe80::1%12]/data` | `[fe80::1]` | `http://[fe80::1]/data` |
| `http://[fe80::1%13]/data` | `[fe80::1]` | `http://[fe80::1]/data` |

`DnsSafeHost` 此时仍分别保留 `%12`、`%13`；`%2512`、`%2513` 形式也被 `Host` 抹去。另一个纯调用反例中，无作用域声明 `[fe80::1]` 对带作用域的实际目标 `http://[fe80::1%13]/data` 被当前凭据检查放行。这些调用没有发网络请求，不据此宣称已发生凭据泄漏。

**阻塞原因：**计划原则上要求不合并不同作用域，但没有明确辅助过程的输入必须保留原始作用域，也没有覆盖会合并不同目标的请求身份。若只把三处 `StripPort` 换成接收 `uri.Host` 的共用函数，作用域信息已经不可恢复；即使放行比较修好，缓存／在途请求仍可能把不同接口上的目标当作同一请求。

**最小修订：**在 F05 中明确选择一种政策，并统一应用到声明、直接目标、逐跳重定向和 native fallback／redirect：

- 最小方案是明确不支持 scoped IPv6，并在原始 URI／authority 尚保留 scope-id 时拒绝；必须早于缓存命中与加入在途租约，不能只在真正发 HTTP 的方法里拒绝。仅检查 IPv6 authority，不误杀路径／查询串中的百分号编码。
- 如保留支持，则规范身份必须包含作用域，所有比较入口保留该信息，且 `CanonicalizeUrl()`／请求摘要区分不同作用域。不能从 `Uri.Host` 重新解析一个已丢失作用域的 IP 来判等。

**必须补入验收：**有／无 scope 不得默认相等；不同 scope 不得相等或共享缓存／在途响应；覆盖数字 scope、编码形式、普通 HTTP、带凭据目标、HTTP redirect、native fallback 和 native redirect。若统一拒绝，逐条断言在请求／缓存复用之前拒绝。现有 LM-079／094 的不同 IP 反例不能替代这些用例。

### R2-P2-02：先剥 userinfo／端口再验证，会把非法 authority 洗成合法声明

**计划锚点：**v2 §8，第 135 行的处理顺序，与第 136／139 行要求“非法 userinfo／无效 authority 拒绝”存在冲突。

**代码证据：**`PluginProcessor.cs:444`～`:455` 先取最后一个 `@` 之后的字符串，再剥端口后加入声明集；native 的 `PluginExecutor.cs:809`～`:818` 使用类似步骤，并仍按第一个冒号截断 IPv6。

小探针调用真实声明和凭据方法确认：

| 静态模板 URL | 完整 `Uri.TryCreate` | 当前声明结果 | 对合法 `api.example.test` 目标的凭据检查 |
| --- | --- | --- | --- |
| `https://user@@api.example.test/data` | false | `api.example.test` | 放行 |
| `https://api.example.test:notaport/data` | false | `api.example.test` | 放行 |

合法 `https://user:pass@api.example.test:8443/data` 则能正常解析。问题不是要求禁止所有 userinfo，而是先删除语法证据后无法判断原 authority 是否合法。native 内嵌的完整 IPv6 fallback 当前还会得到残片 `[2001`，证明其声明提取入口必须纳入共用实现。

**最小修订：**将第 135 行改为“先划定静态 authority，拒绝动态 authority，再验证完整 authority 的语法和消费边界；验证通过后提取并规范主机，按既有合同忽略合法端口和合法 userinfo”。辅助过程要区分完整 authority 和已解析的主机输入，不能先截断再验证。native 内嵌 URL 必须保留 `&` 等外层参数边界。

**必须补入验收：**双 `@`、非法端口、非法／不完整方括号不得贡献有效声明，也不得使动态目标获得凭据资格；合法 userinfo／端口继续支持。保留 LM-080 的无路径 query／fragment 和动态 authority 反例，并增加“静态主机、动态路径／查询参数”仍可声明的正例。不能靠对完整含模板 URL 一次 `Uri.TryCreate` 而误拒绝本来合法的静态主机模板。

### R2-P2-03：Release 回归尚未成为 CI／发布门禁

**计划锚点：**v2 §12，第 163～165 行要求分别验证 Debug／Release；§13 第 172～176 行安排本地双配置回归和两份工作流的计数阈值同步。

**代码证据：**`.github/workflows/ci.yml:197` 与 `release.yml:170` 都只执行 `dotnet test ... -c Debug`。Release 分别在 `ci.yml:222`／`release.yml:194` 只做 build。`BatchEReleaseWorkflowGateTests.cs:83` 只证明门禁参数一致，不能证明实际执行了 Release 测试。

**阻塞原因：**提高 `MIN_TEST_TOTAL` 不会执行 Release 的 `MoveNext` 断言。本次已知的 T09 就是 Debug 通过、Release 失败；按当前工作流，后续同类配置差异仍会在 CI 和发布准入时漏检。一次本地双配置通过不足以形成这项回归的持续门禁。

**最小修订：**主控在两份工作流中同步加入 Release 测试及相应计数校验，明确每个配置都校验三个必需程序集、市场契约至少 6 条且不跳过、失败 0。各配置／程序集使用互不覆盖的 TRX；分别把 Debug 根目录和 Release 根目录交给计数脚本。脚本 `assert-test-counts.ps1:282`～`:288` 按程序集选择最新 TRX，不能把两个配置混在一个根目录后只验一次。

**验收：**保留配置敏感的 T09 回归；检查两条流水线都真正执行 Release 测试、处理非零退出码，并分别对两个配置调用门禁。给 Release 引入失败时，Debug 的通过结果不得覆盖它。无需在计划复审阶段新增全量验证。

### R2-P2-04：跳过数量门禁不能保证“只有联网 E2E 可跳过”

**计划锚点：**目标第 17 行与 v2 §13 第 173／176 行；目前只要求保持 `MAX_SKIPPED_TESTS=1` 和市场类参数。

**代码证据：**`.github/workflows/assert-test-counts.ps1:395`～`:418` 仅检查跳过总数与指定必执行类；其余跳过项只打印，不校验允许身份。

R2 将上一轮 Debug 的三份实际 TRX **复制到自己的临时目录**，仅交换市场 E2E 与资源释放测试的 outcome，保持全部计数和市场契约结果不变：

| 门禁输入 | 总计／通过／失败／跳过 | 市场契约 | 脚本退出码 |
| --- | --- | --- | --- |
| 原始 TRX 副本，仅 E2E 跳过 | 1632／1631／0／1 | 6 条通过 | 0 |
| 合成反例，唯一跳过项为 `HttpLifecycleTests.更新元数据取数路径释放了请求响应与Json文档` | 1632／1631／0／1 | 6 条通过 | **0** |

这是对计数脚本的反例输入验证，不是实际运行测试，也不是声称资源释放测试已在本轮跳过。

**最小修订：**将允许跳过的用例身份校验写入主控拥有的验收脚本或共用门禁，并让 CI／发布同步调用；任意非指定市场 E2E 用例跳过都失败，即使跳过数只有 1。明确把涉及的门禁脚本列入主控修改范围。保留原计数上限和市场契约要求。

**验收：**仅指定 E2E 跳过与零跳过均可通过；仅一个非 E2E 跳过必须失败；超额跳过、市场契约跳过或缺失仍失败。现有 `WebDomSanitizationTests.cs:22`～`:28` 还有依赖 Node 的条件跳过，执行前应确保运行时可用，不能放宽跳过合同来绕过环境缺失。

## F06～F08 与 T09 的结论

| 范围 | 复审结论 | 生产／现有测试锚点及实施验收重点 |
| --- | --- | --- |
| F06 磁盘缓存 | **修复设计可保留，无额外计划阻塞。** | `DiskManager.cs:217`～`:266` 确认物理盘新鲜缓存先返回，盘符逻辑后执行；`:355`～`:371` 只处理物理传感器键。前置合法逻辑盘分支或限制缓存键，两种方案均可。`DiskUpdateScheduleTests` 主要覆盖 `ProcessUpdate()` 的节奏／首选盘，并没有覆盖 `GetBestValue()` 的本次缺陷；新增行为回归是必要的。用现有假时钟控制 10 秒缓存边界，假物理设备加本机就绪逻辑盘验证冷／热／刷新缓存，保持未就绪和异常返回合同，不能用真实等待代替假时钟。 |
| F07 Stock | **上游补丁适用，计划可保留。** | 已用本地 `git show a71ce7584d892230d157c02047bf052fad6a738f` 核对，Stock 补丁是在后缀清洗前增加 `^us\\.` → `us`。当前 `Stock.json:57` 的后缀删除会把 `us.NDX` 变为 `us`，`:64` 不会再 fallback。真实随包模板 + `PluginProcessor.ApplyTransforms()`／`ResolveTemplate()` 可以不联网完成测试。固定 `us.NDX`、`usNDX`、空／未知搜索码 fallback，以及沪深港和原有合法后缀即可；无须合并整个上游或改变用户插件。 |
| F08 网页单位 | **修复方向和生产调用范围正确，计划可保留。** | `LiteWebServer.cs:569` 没传 `UnitPanel`，`:579`／`:590` 直接取默认单位；`WebSnapshotValues.ResolveValue():717` 没接用户格式。两处既有公开重载在 `:662`／`:691`，应都保持原调用兼容。`MetricUtilsMemoryUnitFormatTests.cs:22` 只拼 formatter，`WebSamplingDemandTests.cs:1353` 起主要检查 FPS 状态；都不能代替真实快照接线回归。测试应证明生产快照调用新格式入口，并分别断言 JSON 的 `v`、`u`、`pct`、`sts`，覆盖 `%`／null／空串／`{u}`／未知容量／提交内存／按卡显存；FPS 无需求仍不读值，DASH 插件文本和 `.Unit` 不被改写。 |
| T09 Release IL | **误报判断成立，接收者验证设计可保留；需同时解决上述 Release 门禁阻塞。** | 源码 `UpdateChecker.cs:447`／`:454`／`:481` 确有三个 using。只读扫描既有 Release 产物确认 `JsonDocument` 为 local 4；IL `1184 ldloc.s`、`1186 brfalse.s`、`1188 ldloc.s`、`1190 callvirt Dispose`，共 6 处 Dispose。当前 `HttpLifecycleTests.cs:444`～`:462` 只向前找字段；`IlInspector.cs:205`～`:211` 还把 local 操作数直接跳过，反汇编结果也未保留局部变量类型表。由主控扩展测试辅助数据即可，无须改生产 UpdateChecker。 |

T09 的修复必须把具体 local 槽号／字段与对应的实例 `Dispose` 接收者关联。保留方法体局部变量元数据，正确解码 `ldloc.0`～`.3`、`ldloc.s`／`ldloc`，以及实际需要的 `ldloca*`；保留原字段识别与既有 IL 扫描接口。不能把“附近出现同类型变量”或“方法中出现任意名为 Dispose 的调用”当作证明。

T09 的反例验收应明确包括：目标 local 存在且确实被使用但未释放、仅其他类型对象被释放、字段和 local 混在同一方法时不误配、无目标释放；正例包括字段释放和 local 释放。反例在 Release 下不能因未使用而被优化掉，可通过实际使用／KeepAlive 固定。Debug／Release 的实际生产检查，以及既有请求／响应运行时释放测试都要保留。此部分是对 v2 第 164～165 行的具体落实，不另列产品阻塞。

## 分工可行性与可选改进

- **互斥文件分工总体可执行。** B 独占 PluginProcessor／PluginExecutor／CryptoNative 和新增主机辅助文件，覆盖 F05 所需入口；D 修改 Stock JSON、DiskManager、LiteWebServer，与 B 不冲突。主控拥有 HttpLifecycleTests／IlInspector，新增测试文件会被现有 SDK 工程自动纳入，不必让实施 Agent 争改项目文件。
- **建议澄清构建前的全员写入冻结。** §2 禁止并行构建是必要条件，但按 v2 §13 第 171 行“某组 ready 就统一构建”时，其他组可能仍在编辑同一个主工程的其他文件。最简单是全部 ready 后统一 build，再串行过滤测试；或每次构建前取得所有写入者的短暂冻结。最终全量必须针对同一份冻结版本。此为协作约定补充，无须扩大实施范围。
- **保留现有反射测试的入口兼容。** `BatchB2PluginHostPolicyTests.cs:339`／`:349` 反射调用 `HostInAllowedSet`／`IsHostAllowed`。可以保留薄包装调用共用实现；若确需改测试，记录旧接缝被替换的理由，不删除不同 IPv6／凭据／逐跳拒绝的行为断言。
- **增加用例身份对账会比仅看总数更强。** 数量阈值不能证明原有 1632 个用例逐个仍存在。可保存基线 TRX 的用例身份并与最终发现集合比较，特别保护 T09 和安全反例；这是可选增强，不要求为了本次复审运行全量测试。
- **设置与环境隔离具备现成基础。** `xunit.runner.json` 已关闭集合并行；`SettingsHelper.cs:14`～`:16` 将配置定位在各测试输出目录。新增涉及单例的用例仍需按计划使用 `cfgfile`／try-finally，恢复实例、GlobalBlockSave、插件目录、native 来源钩子和检测总量，不写用户运行目录。R2 没有重新审计 F04 的锁序／回滚算法。
- 计划对依赖 NOTICE 与预构建 updater 来源判定作了区分，符合脚本实际行为：`generate-dependency-manifest.ps1:946` 起用提交内容比较来源，`:1633` 的成功结论是 NOTICE 依赖匹配。不要把两者混作同一证据；无须为本批修复刷新预构建二进制。

## 本轮证据与验证边界

R2 独有目录：`G:\TOOL\LiteMonitor-fork\.review-scratch\r2-plan-review-20261023-9238bb9\`。

- `Program.cs`、`BoundaryProbe.csproj`、`boundary-probe.log`：仅构建小型探针，读取上一轮隔离 Release 产物，调用声明／凭据／native 声明／URL 身份纯方法，并读取 Release 方法 IL；未执行 UpdateChecker 的取数方法。网络请求 0，Settings 调用 0，产品文件写入 0。
- `probe-test-gate.ps1`、`gate/input-notes.txt`、`gate/result.json`、`gate/baseline.log`、`gate/non-e2e-skip.log`：只在副本上交换测试结果，重放现有门禁；两次退出码均为 0，后者是需修正的门禁反例。
- 本轮没有运行 `dotnet test LiteMonitor.sln`，没有运行产品过滤测试、MainForm startup、真实 IPv6 网络请求、外部行情或市场 E2E；没有用上一轮缺陷探针的成功替代修复验收。
- 取证结束时 HEAD 仍为 `9238bb9`，已跟踪文件差异为空；原有 `%SystemDrive%/`、`undefined/` 未被处理。主控更新 v2 后，R2 仅核对本范围内的 §8～§13，没有新增探针、全量验证或项目复查；最终结论已更新为针对 v2。

主控须在启动实施前处置 R2-P2-01～04，并把修订版本交 R2 定向复核。F06～F08 和 T09 不需要推倒重写；R1 的 F01～F04 结论由其独立报告承接。

## v3 定向复核追加结论：PASS

复核日期：2026-10-23（Asia/Shanghai）。受审计划 v3 SHA-256：`E6059B06E62C362FEEE69235FC63B5E84FD51F8813B27DBF8BA7B2197A7C3372`。

**最终有效结论：PASS。R2-P2-01～04 的计划阻塞全部关闭，R2 范围内没有剩余 P1／P2 计划阻塞。** 上文针对 v2 的 REQUEST_CHANGES 保留为历史记录，由本次 v3 结论替代。

本次仅定向读取 §8、§13，以及 §2／§3 的主控范围与协作约定，使用前轮已读代码和证据检查修订是否覆盖原阻塞；没有重新审项目，没有复核 R1 负责的 §6／§7，没有新增探针、运行构建或测试。

| 原阻塞 | v3 修订锚点 | 定向复核结论 |
| --- | --- | --- |
| R2-P2-01：scope-id 丢失及请求归并 | §8 第 137／139 行 | **已关闭。** 明确统一拒绝 scoped IPv6，在原始 URI／authority 的 scope 信息丢失前识别，覆盖直接／凭据／HTTP redirect／native fallback／redirect，覆盖 CanonicalizeUrl／RequestIdentity 及缓存／在途租约前的拒绝，要求数字／编码形式及不同作用域反例均未发请求、未复用。普通 IPv6 和路径／查询中的百分号仍保留。 |
| R2-P2-02：authority 验证顺序 | §8 第 135／139 行 | **已关闭。** 完整静态 authority 先验证语法和消费边界，通过后才提取主机；区分 authority 与已解析主机，保留静态 host 的动态路径／查询和 native 外层参数边界。非法双 `@`、端口、方括号及合法 userinfo／端口均有验收要求。 |
| R2-P2-03：Release 持续门禁 | §13 第 172／176 行 | **已关闭。** 两工作流真正执行 Debug／Release，分别处理失败退出码、按配置／程序集分离 TRX、分别调用完整计数门禁，并增加 Release 失败不会被 Debug 通过覆盖的合成反例和工作流接线检查。 |
| R2-P2-04：跳过项身份 | §13 第 173 行 | **已关闭。** 由共用 `assert-test-counts.ps1` 强制既有 Market E2E 的唯一允许身份，CI／release 同步使用；零跳过／仅指定 E2E 的正例、单个非 E2E／超额／市场契约跳过或缺失的反例均已列入。Node 环境要求没有通过放宽跳过合同解决。 |

主控范围也已补齐：§3 第 43 行明确拥有 `HttpLifecycleTests.cs`、必要的 `IlInspector.cs`、新增 `ReviewReleaseValidationGateTests.cs`、两份工作流及共用门禁脚本，与 B／D 的实现和新增测试文件互斥。§3 第 43 行及 §13 第 171 行规定所有写入者冻结后才统一构建、串行测试，修复后重新冻结，已消除分组 ready 时仍可能并发编辑主工程的歧义。

本次没有改变前轮对 F06～F08 和 T09 主体方案的认可。T09 实施验收仍须遵守原报告的接收者关联和正反例要求，不能以任意 Dispose 或同类型 local 的存在判绿。

此 PASS 只表示计划可按用户已授权流程进入实施，不表示产品修复、门禁修改或 Debug／Release 验收已经完成；实施后的验证与独立代码复审仍按计划执行。
