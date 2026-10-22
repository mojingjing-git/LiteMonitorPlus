# LiteMonitor++ V3.0 分步分 Agent 修复计划

日期：2026-10-22。版本：V3.0。

**基线提交：** `9ffb4219cad9b468ec748c547039da22ab9e83be`（已核实等于本地 `master` 等于 `origin/master`）。
**上游仓库：** `mojingjing-git/LiteMonitorPlus`（public，默认分支 master）。
**性质：** 缺陷修复、安全加固、测试恢复、发布治理。**不是**新功能开发。

---

## 0. 本计划的来源与取舍

V3.0 是三次评审的收敛结果，不是全新撰写：

| 来源 | 贡献 |
|---|---|
| V1.0 原计划 | 任务域划分、优先级框架、执行原则 |
| 本机首轮评审（A1–A6、B1–B8） | 仓库现状核实：测试套件已在本地而非旧仓库；`.sln` 静默空跑；Release 锁死；验签已实现；分支基线未交代 |
| 第三方 V2.0 评审（P1 六项、P2 十项） | 签名假绿、在途合并竞态、NOTICE 版本不符、TOCTOU 交接、跳过用例分类、状态机矛盾 |
| 第三方评审的复核结论 | 19/20 事实断言成立；1 处引用行号更正；3 处补充（在途租约模型、`ResetNetworkClients` 联动、合并键组成修正） |

**取舍原则：** 已被实测证伪的前提一律丢弃（如"从旧仓库恢复测试"）；已被证实的断言全部纳入并给出可执行验收；两轮评审重复发现的缺陷提高置信度并优先排期。

---

## 1. 强制执行规则

### 1.1 证据规则

1. 不以 README、代码注释、既有测试记录或 Agent 自述代替实际验证。
2. 每条结论必须附：可复现命令 + 实际输出 + 文件行号。
3. 无法在当前环境验证的，一律记 `NOT_VERIFIED` 或 `BLOCKED_ENV`，**禁止**表述为通过。
4. "未发现缺陷"必须附**测试范围**，不得表述为无限范围的无缺陷证明。
5. **变异/还原后必须确认构建产物确实重新编译（2026-10-23 补充门禁踩坑后新增）**：用 `Copy-Item` 还原文件会**保留原时间戳**；若还原后的源文件 mtime **早于**已产出的 `bin/**` 程序集，MSBuild 增量判定"无需重编译"⇒ 测试实际跑的是**上一次变异的二进制**。核对：比对源文件 mtime 与程序集 mtime；不确定时用 `--no-incremental` 强制重建。
   > **真实发生过**：M3 补充门禁的 9 次变异后，首轮全量回归出现 2 条失败，其签名与**最后一次变异（M9）完全一致** —— 正是这个"签名对得上"才暴露了陈旧二进制问题。还原校验（SHA-256 逐次比对）**只能证明源文件字节一致，不能证明构建产物来自该源文件**。

### 1.2 假绿禁令（每个任务都要自检）

| # | 禁令 | 判定方式 |
|---|---|---|
| G1 | 测试用例不得以"提前 return"冒充通过 | 扫描 `tests/`：早退分支必须改用 `Skip` 特性（仓库已有 `PluginRepoFactAttribute` / `E2EFactAttribute` 范例） |
| G2 | 关键断言缺前置条件时必须失败而非跳过 | 缺私钥、缺设备、缺夹具时按契约 fail 或显式 skip，**不得**静默 pass |
| G3 | `dotnet test` 发现 0 个测试时必须非零退出 | CI 断言输出含测试计数；0 发现即 FAIL |
| G4 | 只检查测试数量不足以判定通过 | 必须同时核对"哪些用例真的执行了" |

### 1.3 变更纪律

- 每个任务一个独立提交，提交信息含缺陷 ID。
- 不做与缺陷无关的格式化、依赖升级或架构重写。
- 修复必须保留原有配置兼容性；破坏性默认变更必须配迁移路径。
- 不因为修复失败而删测试、降断言强度或扩大异常吞噬范围。

### 1.4 环境阻断登记

以下为已知环境事实，**开工前必须处理或显式登记**，不得当作编译失败：

| ID | 事实 | 影响 | 处置 |
|---|---|---|---|
| ENV-001 | `LiteMonitor++.exe`（PID 24936）运行中，锁定 `bin/Release/net8.0-windows/LiteMonitor++.exe` | `dotnet build -c Release` 报 MSB3021/MSB3027，重试 10 次后失败 | 开工前停止该实例，或改用独立发布目录 |
| ENV-002 | `secrets/update-signing-key.pkcs8` 在本地存在（被 `.gitignore` 排除） | 干净检出缺钥，签名一致性用例会早退 | 正式签名任务用真实私钥；PR 用测试密钥 |
| ENV-003 | `LITEMONITOR_PLUGIN_REPO` 未设置 | 6 条市场契约测试被跳过 | 建固定夹具后纳入版本控制 |
| ENV-004 | `LITEMONITOR_E2E` 未设置 | 1 条联网 E2E 被跳过 | 单独运行，不进 PR 门禁 |

---

## 2. 统一状态机（取代原计划的自相矛盾枚举）

原计划前文允许的状态集合不含后文使用的 `VERIFIED_FIXED` / `CONFIRMED_NO_DEFECT`，且"所有 P1 关闭"未排除 `DEFERRED` / `NOT_VERIFIED`。V3.0 改为**三条正交轴**，不得混用：

**轴一 · 缺陷结论（DefectVerdict）**

| 值 | 含义 |
|---|---|
| `CONFIRMED` | 已复现，根因明确到源码行 |
| `PARTIAL` | 现象成立但根因未完全定位 |
| `NOT_A_DEFECT` | 经核实不成立（须附核实范围） |
| `DEFERRED` | 成立但本轮不做（须写明理由与重新评估触发条件） |
| `SUPERSEDED` | 被其他缺陷覆盖 |

**轴二 · 验证状态（VerificationState）**

| 值 | 含义 |
|---|---|
| `VERIFIED_FIXED` | 已修复，且回归测试实际执行通过 |
| `PARTIAL_FIXED` | 部分修复，剩余部分有明确阻断项 |
| `NOT_VERIFIED` | 无法在本环境验证 |
| `BLOCKED_ENV` | 环境/权限/硬件缺失阻断 |
| `REGRESSION_FOUND` | 修复引入回归 |

**轴三 · 发布裁决（ReleaseGate）**

| 值 | 含义 |
|---|---|
| `RELEASE` | 满足发布准入 |
| `NO_RELEASE` | 存在未关闭的阻断项 |
| `CONDITIONAL` | 允许继续开发，但**不满足**发布准入 |

> 规则：`DEFERRED` / `NOT_VERIFIED` / `BLOCKED_ENV` **一律不计入**"已关闭"。M0 允许在存在 `CONDITIONAL` 项时继续开发，但发布准入（§9）要求所有 P0/P1 均为 `VERIFIED_FIXED`。

---

## 3. Agent 名册与分工规则

### 3.1 名册

| Agent | 角色 | 适用 |
|---|---|---|
| `mavis` | 编排 / 决策 / 契约冻结 / 对外交付 | 需要判断的跨模块决策 |
| `explore` | 只读勘察 | 映射调用图、分支差异、权限需求；**不得改文件** |
| `worker` | 有界实现 | 按既定契约改代码并自测 |
| `verifier` | 独立验收 | 对已完成交付物出 PASS/FAIL/PARTIAL 裁决；**不得改产品源码** |
| `mule` | 机械批量 | 批量入库、格式生成、清单产出等边界清晰的杂活 |

### 3.2 并行规则

1. **文件所有权不重叠**是并行的唯一前提。同一文件由两个 worker 同时改 → 强制串行。
2. 每个任务一次 `task` 派发，briefing 必须**自包含**（子 Agent 无父会话上下文）：目标、已核实事实、精确范围、文件所有权、约束、交付物、验收标准、输出格式。
3. 每个实现任务之后必须有**独立 verifier 复核**，不得由实现者自审代替。
4. 派发后不得由父 Agent 同时亲自做同一范围的工作。
5. 同一子 Agent 的后续追问用 `task_append` 续接，不新开。

### 3.3 Briefing 必填模板

```
【目标】要消除的具体现象（引用缺陷 ID + 文件行号）
【已核实事实】父会话已验证的结论（含命令与实际输出），子 Agent 不必重查
【范围】允许改的文件（逐个列出）/ 明确不许碰的文件
【契约】必须遵守的接口约定（签名、状态值、错误语义）
【约束】禁止事项（无关重构、放宽断言、吞异常、绕过 G1–G4）
【交付物】代码 + 测试 + 变更说明
【验收】可机器判定的命令与期望输出
【输出格式】Result / Changes / Validation / Assumptions / Blockers
```

---

## 4. 缺陷台账

优先级：**P0** 可验证的严重安全漏洞或数据破坏；**P1** 确定性功能缺陷、重要安全边界、发布阻断；**P2** 稳定性与工程质量；**P3** 长期架构。

| ID | 标题 | 优先级 | 状态 | 归属步骤 |
|---|---|---|---|---|
| BASE-002 | 分支基线建立在过时状态；484/522 口径未标注 | P1 | `CONFIRMED` | S0.1 |
| BASE-003 | `tests/` `scripts/` `docs/` 未纳入版本控制；`.sln` 无测试工程 → `dotnet test` 静默空跑 | P1 | `CONFIRMED` | S0.2 |
| BASE-004 | 最小 CI 建立过晚，流水线第 3 步恒绿不测 | P1 | `CONFIRMED` | S0.3 |
| BASE-005 | Release 构建被运行中实例锁定 | P1 | `CONFIRMED` | S0.0 |
| LM-001 | 插件调度依赖未文档化副作用重武装计时器，且无实例代次守卫（原"永久停摆"根因经实测证伪，见 §4.1） | P2 | `CONFIRMED`（根因已更正） | S1.1 |
| LM-002 | 请求身份缺方法/Header/代理；`GetHashCode()` 作等价依据；`GetOrAdd` 工厂并发；`TryRemove` 双份无条件删除 | P1 | `CONFIRMED` | S1.2 |
| LM-003a | 插件响应未释放、响应体无上限 | P2 | `CONFIRMED` | S1.2 |
| LM-003b | 更新元数据 request/resp/JsonDocument 未释放；`ResponseHeadersRead` 后正文无独立期限（第三方编号 LM-018） | P2 | `CONFIRMED` | S1.3 |
| LM-003c | 镜像测速正文读取无独立取消期限（第三方编号 LM-019） | P2 | `CONFIRMED` | S1.3 |
| SEC-001 | 默认监听全接口（`IPv6Any`/`Any`）；`Access-Control-Allow-Origin: *`；无 Origin/Host 校验 | P1 | `CONFIRMED` | S2.2 |
| SEC-002 | 凭据持久化于 `localStorage` 与查询串；默认环回属破坏性变更，缺配置迁移 | P1 | `CONFIRMED` | S2.2 |
| LM-004 | 动态分组名/监控名进入 `innerHTML` 与属性/选择器拼接 | P1 | `CONFIRMED` | S2.1 |
| LM-005/006 | 主会话协议未定义：传递、过期、注销、改密撤销、HTTP 快照认证、CSRF | P1 | `CONFIRMED` | S2.0 + S2.2 |
| LM-007 | 市场下载 URL 仅前缀匹配；重定向后不复核下一跳；无字节上限 | P2 | `CONFIRMED` | S3.1 |
| LM-008 | 缺失 SHA 仍放行；先写文件后写记录，非同一事务 | P2 | `CONFIRMED` | S3.1 |
| LM-009 | 插件正则无匹配期限；步骤数/目标展开数/并发数无边界 | P2 | `CONFIRMED` | S3.2 |
| LM-010~014 | GPU/采样/资源恢复的验证标准缺失；用例在无设备时冒充通过 | P2 | `PARTIAL` | S6.2 |
| LM-015 | 正式签名未就绪；签名一致性用例在缺钥环境冒充通过 | P1 | `CONFIRMED` | S5.1 + S5.2 |
| LM-016 | 验签后按路径重开 ZIP → 高权限更新器接手，缺"字节保持不变"约束 | P1 | `CONFIRMED` | S4.2 |
| LM-017 | 发布顺序缺"冻结→签名→独立验证→上传核验→公开→客户端可见" | P1 | `CONFIRMED` | S5.1 |
| LM-020 | 自动 GC/工作集裁剪策略未经实测，可能只降低显示值而增加开销 | P2 | `PARTIAL` | S6.2 |
| LM-021 | 主程序 `requireAdministrator`，插件/Web/更新器同处高权限令牌 | P3 | `CONFIRMED` | S7（独立） |
| LM-023 | 凭据加密与旧备份明文冲突；DPAPI 作用域未定义 | P2 | `CONFIRMED` | S2.2 |
| LM-024 | `NOTICE` 写 System.Text.Json 9.0.10，项目实际引用 10.0.3；产物清单未按实际解析生成 | P1 | `CONFIRMED` | S5.3 |
| LM-030 | LM-009 的 6 个上限在 `cb460b5` 上仍未接入端到端执行路径（5 个在生产完全不生效） | P2 | `CONFIRMED` | S3.3 |
| LM-031 | `ExtractJsonValue` 宽泛 `catch { return STATUS_UNKNOWN; }` | P2 | `CONFIRMED` | 待清理 |
| LM-032 | `PluginMarketPolicyTests.cs:420` 用例名与断言不符（只断言终态不直接断言顺序） | P2 | `CONFIRMED` | 待清理 |
| LM-033 | 门禁基线描述陈旧（任务书下限与仓库 CI 自述不一致） | P3 | `CONFIRMED` | S3 |
| LM-034 | 被拒绝的 WebSocket 握手不关闭连接（**实为套接字泄漏**，危害描述见 §4.5.4） | P2 | `CONFIRMED` | S4.2b |
| LM-035 | `Handshake` 的 `catch { return false; }` 宽泛吞异常 | P2 | `CONFIRMED` | 待清理 |
| LM-036 | `dfe0fff` 无必要地删除一条断言（违反契约 §5.2「不得放宽断言」形式要求） | P2 | `CONFIRMED` | S2.4 |
| LM-037 | Host 缺失的拒绝存在三层冗余 ⇒ 该具体行号无独立回归护栏 | P3 | `CONFIRMED` | 信息项，不阻断 |
| LM-038 | 包内更新器的「内嵌构建提交」指向错误提交，使 LM-024 产物来源护栏**永久失效** | P2 | `CONFIRMED` | S4.2c |
| LM-039 | `native://` 步骤绕过沙箱的三道闸门（并发/字节上限/凭据目标/截止时刻） | P1 | `CONFIRMED` | ✅ 已修复（`d23e6fc`：原生取数接入同一套闸门） |
| LM-040 | 「目标展开数按实例累计」未被实现强制（比较入参而非累计值），类文档与实现不符 | P2 | `CONFIRMED` | ✅ 已修复（`d588440`：改用 `Interlocked.Add` 返回值判定累计值） |
| LM-041 | 端到端正则用例不锁定 500 ms 数值 | P2 | `CONFIRMED` | ✅ 已修复（`d588440`） |
| LM-042 | `端到端_目标数恰好等于上限` 的请求数断言过弱（实测 61 vs 64） | P2 | `CONFIRMED` | ✅ 已修复（`d588440`） |
| LM-043 | `Assert.NotEqual(TargetLimit, failure?.Reason)` 在 `failure == null` 时恒真（空转断言） | P3 | `CONFIRMED` | ✅ 已修复（`d588440`） |
| LM-044 | 生产入口的 30 s 契约值未被测试钉住 | P2 | `CONFIRMED` | ✅ 已修复（`d588440` + 主审集成 `6310dbf`） |
| GPU-001 | WDA 恢复失败后 GPU 呈现**永久不可重启**（进程生命周期内不可用） | P1 | `CONFIRMED` | GPU T03/T04（B 期只冻合同，不写生产代码） |
| GPU-002 | `PresentationSession._originalAffinity` 跨 HWND 代次不可复用 | P2 | `CONFIRMED` | GPU T02/T04 |
| GPU-003 | 鼠标穿透关闭后 `WS_EX_LAYERED` 永久残留，与 WDA 互斥 | P2 | `CONFIRMED` | GPU T02/T03（**待 T01/T09 实测确认**） |
| GPU-004 | `IsWindow` 无法区分「旧 HWND 已销毁」与「句柄值被复用」 | P2 | `CONFIRMED` | GPU T04 |
| GPU-005 | `GpuStartupGuard` 的 pending 语义比计划 §4.4 描述更弱 | P2 | `CONFIRMED` | GPU T02（合同澄清） |
| ENV-003 | `LITEMONITOR_PLUGIN_REPO` 未设置 ⇒ 6 条市场契约测试被跳过 | P1 | `CONFIRMED`（已闭环，`a8fb2fc`） | S4.1 |
| ENV-004 | `LITEMONITOR_E2E` 未设置 ⇒ 1 条联网 E2E 被跳过 | P2 | `CONFIRMED`（`BLOCKED_ENV`，**不计入通过**） | S4.1（联网 E2E 单独运行，不进 PR 门禁） |
| LM-045 | `HttpClient` 默认跟随重定向 ⇒ 已声明主机 302 到未声明主机不被拦截，凭据（`X-Api-Key`/`Cookie`）外泄且响应数据进入插件流水线 | **P1** | `CONFIRMED`（**独立 verifier 已实证**） | ✅ **已修复**（`938dc4b`）：HTTP 步骤路径与 **`native://` 路径两侧**都关闭自动重定向、改为逐跳跟随并逐跳复核目标主机；不通过时**该跳请求根本不发出**。见 §4.5.9 LM-053 |
| LM-046 | 生产代码内的可变全局测试接缝 `CryptoNative.SourceUrlOverrides`（`internal static`，生产路径不设置） | P3 | `CONFIRMED`（**接受并登记**） | 接受 |
| LM-047 | 字节上限在原语层未被契约天花板夹住（`ReadResponseBodyAsync` 不与 `MaxResponseBytes` 比较） | P2 | `CONFIRMED` | ✅ 已修复（`46f3f9d`） |
| LM-048 | 允许目标口径分裂：主机判据用 `allowedHosts`，带凭据时改用 `declaredHosts` | P2 | `CONFIRMED`（当前无功能影响） | 登记待清理 |
| LM-049 | `native://` URL 产出伪主机并污染声明集合 | P2 | `CONFIRMED`（无害） | 登记待清理 |
| LM-050 | `native://citycode` 的远程回退闸门（并发槽/字节上限/令牌）无回归钉住 | P3 | `CONFIRMED` | 登记待清理（需先加测试接缝） |
| LM-051 | `CryptoNative.NormalizeSymbol` 未对 symbol 做 `EscapeDataString`（**既有缺陷，非本批引入**） | P2 | `CONFIRMED`（主机固定，不构成沙箱逃逸） | 登记待清理 |
| LM-052 | `CityCodeResolver._isLoading` 是无锁 `static bool`（**既有缺陷，非本批引入**） | P3 | `CONFIRMED`（无安全影响） | 登记待清理 |
| LM-053 | `native://` 路径同样可被 302 绕过"允许目标"：`CryptoNative` 客户端未关自动重定向，且 `allowedHosts` 未贯穿到发请求层 | P2 | `CONFIRMED`（**代码实测**，§4.5.9） | ✅ 已修复（`938dc4b`，随 LM-045 一并落地） |
| LM-054 | 市场索引拉取仍允许自动跳转（`PluginMarket._http` 沿用原行为） | P3 | `CONFIRMED`（信息项，§4.5.9） | 登记待评估（本批不修，风险有界） |
| LM-055 | `CityCodeResolver` 的远程回退 handler 未关自动重定向（目标是代码内建常量） | P3 | `CONFIRMED`（信息项，§4.5.9） | 登记待评估（本批不修） |
| LM-056 | 发布流水线的用例发现数阈值与 CI **不一致**（`release.yml` 写着 1000，`ci.yml` 写着 1281），且未传 `-MaxSkipped` / `-MustExecuteTestClass` | P2 | `CONFIRMED`（**主审实测**，§4.5.9） | ✅ 已修复（`b1a8b42` 同步阈值 + 补齐跳过数上限与"必须真正执行的测试类"；阈值随后按**被推送分支实测总数**再校正为 **1349**，见 §8「阈值纪律」） |
| LM-057 | 逐跳主机比对**忽略端口** ⇒ 同主机不同端口的重定向被放行（声明主机 `api.example.com` 的插件，凭据可被 302 到 `api.example.com:8443`） | P2 | `CONFIRMED`（**独立 verifier 独立实测**，§4.5.10） | 登记待评估（与 .NET 自身重定向语义一致、**既有行为非本批引入**；但 LM-045 把该口径继承到了逐跳复核上 ⇒ 属真实口径缺口。本批不修，见 §4.5.10 F1） |
| LM-058 | IPv6 字面量主机在两处被**不同地**截断（`PluginProcessor.AddDeclaredHost` 取**第一个**冒号 ⇒ `"["`；`PluginExecutor.StripPort` 取**最后一个**冒号 ⇒ `"[:"`）⇒ IPv6 目标恒不可用 | P3 | `CONFIRMED`（**独立 verifier 独立实测**，§4.5.10） | 登记待清理（**偏严方向 / fail-closed**，不漏放行；影响 IPv6 回环与双栈插件） |
| LM-059 | 既有 public 记录 `RenderingRouteRequest` 的**主位置签名**被追加成员（`bool WindowIsLayered = false`）⇒ 记录元数 5→6、相等性与 `ToString()` 语义改变（**二进制不兼容**） | P3 | `CONFIRMED`（**独立 verifier 实测**，§4.5.10） | 登记知悉（构造侧**源码兼容**；全仓仅 1 处生产调用点且用具名参数，无解构/相等比较消费点） |

**当前无 P0。** 若任一执行步骤新发现 P0，立即挂起其他任务优先处理。

**编号规则：** `LM-` 缺陷 / `SEC-` 安全 / `BASE-` 基线 / `ENV-` 环境事实 / `GPU-` GPU 切换专项。第三方评审使用的 `LM-018`、`LM-019` 与 `LM-003b`、`LM-003c` 指同一处代码，已合并并保留别名以便交叉引用；`LM-005/006` 为同一协议缺陷的两个侧面。

> **编号占用现状（2026-10-23 第四次核对）**：已占用 `LM-001`–`LM-021`、`LM-023`、`LM-024`、`LM-030`–`LM-059`。`LM-022` 与 `LM-025`–`LM-029` 为**从未使用的空号**（经 `Select-String` 全文检索 + `git log --grep` 双向确认），保持空置不复用。**新增缺陷从 `LM-060` 起顺延，不得复用已关闭编号**（`LM-045`–`LM-052` 于上一轮占用，`LM-053`–`LM-056` 于本轮占用，`LM-057`–`LM-059` 由本轮独立 verifier 复核产出）。 GPU 专项独立编号 `GPU-001`–`GPU-005`（`GPU-0xx` 此前未占用）。

### 4.1 根因更正记录 · LM-001（原"永久停摆"前提已证伪）

> **状态**：本节由 M1 独立复核触发，经父 Agent **独立实测复核**后写入。所有断言附可复现命令与实际输出。

#### 4.1.1 被证伪的断言

原始 V1.0 计划、本仓库首轮评审、第三方 V2.0 评审**三方重复了同一句根因**：

> "不可见路径永不进入 try/finally → 计时器永久停摆。"

**该断言不成立。** 基线代码（`git show 9ffb4219:src/Plugins/PluginManager.cs:371-375`）：

```csharp
if (!IsPluginVisible(inst.Id))
{
    newTimer.Interval = 2000; // Check again in 2s
    return;
}
```

不可见分支**在提前 `return` 之前已经给 `Interval` 赋了值**。`System.Timers.Timer` 在 `AutoReset = false` 时，一旦 `Interval` 被赋值即重新武装——**该行为未被 .NET 文档明文保证**，但在本机 .NET 8.0.426 SDK 上确实成立。

#### 4.1.2 实测证据（可复现）

PowerShell 7 + `Add-Type` 内联编译纯 C# 探针，三个变体各跑 3 秒：

| 变体 | 形状 | 3 秒内触发次数 |
|---|---|---|
| A | `AutoReset=false`，处理器内赋值 `Interval` | **6**（@50/551/1051/1551/2051/2551 ms） |
| B | 基线 `async` 处理器 + 提前 `return` 形状 | **6**（@51/550/1050/1560/2060/2560 ms） |
| C | 不赋 `Interval`、不 `Start()`（`AutoReset=false` 纯单发） | **1**（@49 ms） |

探针源码（可原样重放）：

```csharp
// 变体 A
var t = new System.Timers.Timer(50);
t.AutoReset = false;
t.Elapsed += (s, e) => {
    Interlocked.Increment(ref count);
    ((System.Timers.Timer)s).Interval = 500;   // 关键：处理器内赋值即重武装
};
t.Start(); Thread.Sleep(3000);
```

**结论：基线在不可见状态下仍按 2 秒周期持续轮询，并未停摆。** 变体 C 证明"单发不重武装"是 `AutoReset=false` 的默认语义——基线之所以没停，靠的是变体 A 这个**未被文档化的副作用**，而不是任何显式 `Start()`。

#### 4.1.3 更正后的根因与修复定位

| | 原表述 | 更正后 |
|---|---|---|
| 根因 | 不可见分支提前 `return`，绕过 `try/finally`，计时器永久停摆 | 调度存活**依赖未文档化的 `Interval` 赋值重武装副作用**；且无实例代次守卫，停/启/重启竞态下旧回调可能重新调度或写回 |
| 用户可见影响 | 插件不可见后不再刷新（确定） | 插件不可见后**仍会刷新**（不成立）；真实风险是竞态与隐式依赖 |
| 修复定位 | 恢复停摆的轮询 | **消除对未文档化副作用的依赖**（显式统一收尾）+ **引入实例代次守卫** |
| 优先级 | P1（"必须在 M1 内关闭"） | **P2** —— 修复仍是净改进（显式重武装 + 代次守卫 + 不再依赖偶然副作用），但不存在"插件停止刷新"这一确定性 P1 后果 |

S1.1 的**验收条款不变**（显式收尾 + 代次守卫仍是必须项），仅"已核实事实"与回归用例的**观测点**需要更正——见 §5 S1.1 与 §4.1.4 F1。

#### 4.1.4 M1 独立复核发现台账（F1–F8）

M1 门禁由独立 `verifier` 判定 **PASS**，附 8 项发现（3 中 / 4 低 / 1 信息）。M1 复核报告见 §4.1.5。

| 编号 | 严重度 | 结论 | 处置 |
|---|---|---|---|
| **F1** | 🟠 中 | LM-001"永久停摆"前提不成立；`T2b` 因此**恒真**（守的是偶然副作用） | 根因更正见 §4.1；`T2b` 改为断言 `finally` 重武装路径本身（S1.1 回归用例 2 已重写） |
| **F2** | 🟠 中 | `H1` 观测的是**内容流**释放，而 `using var stream` 已独立保证 → 无法归因到响应消息；变异"只删 `using var response`"时 **0 条失败** | 改为跟踪 `HttpResponseMessage.Dispose` 自身 |
| **F3** | 🟠 中 | `R1` **结构性盲区**：`ExecuteStepAsync` 在首个 `await` 前同步执行 → N 个等待者在 `GetOrAdd` 点实际**串行**，无法检验契约 §1.3 的"工厂并发" | 用屏障让 N 个等待者**真正同时**抵达 `GetOrAdd` |
| **F4** | 🟡 低 | `X1` 用例名（"…后清空在途字典"）与断言（"**没有**被清空"）相反 | 更名为 `…后在途字典未清空` |
| **F5** | 🟡 低 | 两处**用户可见变化未在提交信息标注**：① 更新元数据最坏耗时 ~3s→~6s（headers 3s + body 3s + total 8s）；② 镜像测速单次读→循环读满 32KB，换算速度与镜像排序可能改变 | 判定为 S1.3 强制约束的必然结果、**不应回退**；补入交付记录（§4.1.6） |
| **F6** | 🟡 低 | `PluginManager.cs:293` 在 `_scheduleLock` **锁外**读 `_timers`（其余 10 处均在锁内）——基线本就无锁，非本次引入，但本次建立了加锁纪律却漏掉这一处 | **已由父 Agent 修复**（改为锁内 `exists` 变量），随 M1 收尾一并提交 |
| **F7** | 🟡 低 | `PluginRequestIdentityTests.cs:166` 注释载荷串少一位（`1:` 实为 `1:1`）；常量本身正确 | 注释更正 |
| **F8** | 🔵 信息 | `ClearCache` 前缀由 `StartsWith(instanceId)` 收紧为 `StartsWith(instanceId+"\|")`，实例"A"不再误清"AB" | C4 要求的**正确化**，保留 |

#### 4.1.5 判别力补强（F1/F2/F3/F4/F7）

F1–F4、F7 属**测试判别力缺口**，不影响产品实现正确性（verifier 以 17 次变异测试确认三个缺陷根因均被消除、零回归）。补强由独立 worker 执行，产出要求：

- **不得**改产品代码来迁就用例；
- **不得**用放宽断言、吞异常、提前 `return` 冒充通过；
- 补强后必须能给出**反向验证**：把对应缺陷放回 → 新用例必须失败；移除修复 → 新用例必须失败。

#### 4.1.6 用户可见变化登记（F5）

以下为 S1.3 引入的**行为变化**，须写入发布说明：

1. **更新元数据取数预算**：基线单一 3s CTS → 现 headers 3s + body 3s + total 8s（`http` 客户端自身 6s 超时不变）。最坏耗时 **~3s → ~6s**。
2. **镜像测速口径**：单次 `ReadAsync` → 循环读满 32KB（`UpdateChecker.cs` 的 `ReadUpToAsync`）。**换算速度与镜像排序可能改变**；测速预算由"客户端 6s 管头、正文无界"变为"头 5s + 正文无进展 5s"。
3. **取消不再计入连续失败**（`PluginManager.cs`）：基线会计入。仅拆卸路径可达，计数随即被 `StopInstance` 清除 → **无用户可见影响**。

---

### 4.2 M2 门禁复核结论归档（候选 `4be1a8c`，2026-10-22）

**结论：`CONDITIONAL`，不予放行。** 实现质量高——契约 W1–W14 绝大部分条款经源码与实测确认满足，全量回归 **1125/0/7/1132 + 13/0/0/13 + 19/0/0/19** 达标，7 条跳过经核实**均非 M2 矩阵条目**（`MarketIndexContractTests`×6 属 ENV-003、`MarketE2ETests`×1 属 ENV-004），不触发跳过型 `CONDITIONAL`。但**变异测试**证明两处契约硬约束的**回归护栏缺失** ⇒ 按契约 §3「任一 `NOT_VERIFIED` ⇒ M2 `CONDITIONAL`」，本门禁不放行。

> **要点：F-01 / F-02 不是实现缺陷。** 生产代码经复核**正确**；缺的是能抓住回归的测试。

#### 4.2.1 变异证据（verifier 实测，逐次 SHA-256 字节级还原）

| ID | 变异 | 目标 | 实测 | 判定 |
|---|---|---|---|---|
| M1a | 删掉 CORS 白名单回显 `WebSessionManager.cs:1995` | N7 | exit=0，**1/1 通过** | ❌ 无判别力 |
| M1b | 回显改回 `Access-Control-Allow-Origin: *` | N7 | exit=0，**1/1 通过** | ❌ 无判别力 |
| M5 | 恢复服务端接受 `?pwd=` | 全类 27 条 | exit=0，**27/27 通过** | ❌ 无护栏 |
| M2 | 票据不烧毁 | A4 | exit=1，失败 1 | ✅ 有效 |
| M3 | CSRF 恒真 | F2 | exit=1，失败 1 | ✅ 有效 |
| M4 | Origin 缺失也拒绝 | N5 | exit=1，失败 1 | ✅ 有效 |
| M6 | 空闲 TTL 置 0 | A8 | exit=1，失败 1 | ✅ 有效 |
| M7 | `?pwd=` 插回 `WebPageContent.cs` | DOM 全类 | exit=1，12 中 2 条红 | ✅ 有效 |
| P0–P2 | verifier 自写 CORS 探针 | 探针 | 基线绿 / 删回显红 / 改 `*` 红 | ✅ 探针自身有判别力 |

#### 4.2.2 根因与解除条件

- **F-01（🟠 中）｜W6 白名单回显路径零覆盖。** N7 请求**不带 `Origin`**（`WebSessionProtocolTests.cs:286-287`），`AppendCorsHeaders` 在 `WebSessionManager.cs:1988-1990` 提前 `return` ⇒ **CORS 代码在 N7 场景下根本没执行**。P0–P2 证明该路径真实存在且真实可被检测 ⇒ 属「回归网抓不住」而非「行为不存在」。
  **解除条件 C1**：补服务端用例——配置 `WebServerAllowedOrigins` 后，`Origin` 命中 ⇒ 回显**具体源** + `Access-Control-Allow-Credentials: true`，且响应**不含** `Access-Control-Allow-Origin: *`；未命中 ⇒ 响应**不含任何** `Access-Control-Allow-Origin` 头。
- **F-02（🟠 中）｜服务端 `?pwd=` 移除零覆盖。** 全仓**无任何**用例向服务端发送 `?pwd=`；`HasLegacyPasswordQuery`（`WebSessionManager.cs:1516-1523`）的 401 拒绝分支是**零覆盖**代码。DOM 侧护栏只锁前端字符串，不锁服务端接受路径。
  **解除条件 C2**：补服务端用例——`GET /api/snapshot?pwd=<明文>` 与 `GET /ws?pwd=<明文>` ⇒ **401** 且**不签发任何会话/票据**。
- **C3（建议，非阻断）**：补 W2 绝对 TTL 30 min（`:871-875` 判定分支无用例）、W5 `Host` 缺失 ⇒ 403（`:1801`）、W10 改密后**已建立的 WebSocket 被断开**（`RevokeAllAuthState` `:916-932`）。

⇒ **C1/C2/C3 归属 S2.4**（只补测试，**不动生产代码**）；解除后须**重跑 M2 门禁**方可放行。

#### 4.2.3 其余发现项

| ID | 级别 | 摘要 | 处置 |
|---|---|---|---|
| F-03 | 🟡 低 | 会话绝对 TTL 30 min 判定分支无用例 | 并入 C3 |
| F-04 | 🟡 低 | `WebServerTrustProxyHeaders` 被读入 `WebAccessPolicy` 但**全仓从未被使用**（转发头根本不读）；配置项是**误导性控件** | 登记；"默认不信任"成立（失效即安全），"显式配置后采信"未实现 |
| F-05 | 🟡 低 | `Host` 缺失 ⇒ 403 无用例 | 并入 C3 |
| F-06 | 🟡 低 | 改密后已建立的 WebSocket 断连无用例 | 并入 C3 |
| F-07 | 🟡 低 | `SendPreflightResponse`（`:1967-1975`）**无条件**返回 `Access-Control-Allow-Methods`/`Headers`，偏离 W6「默认不返回任何 CORS 头」字面 | 登记；因缺 `Allow-Origin` 仍被浏览器拦截，**不可利用**，属字面偏离 |
| F-08 | 🟡 低 | `Settings.cs:1104` DPAPI 保护失败时**明文回写** | 登记；与 W9「必须加密存储」冲突，需后续单独修复 |
| F-10 | 🔵 信息 | `WebPasswordJsonConverter.Read`（`Settings.cs:842`）字段非字符串时返回 `""` ⇒ 视为 L0 无口令 | 登记；仅配置被外部改坏时触发，Lan 模式仍拒绝启动 ⇒ 失效即安全 |
| F-11 | 🔵 信息 | 实现者自报「隔离输出目录下会误报找不到 `LiteWebServer.cs`」 | **实测未复现**：全量 0 失败，6 条源码扫描用例均执行通过 ⇒ 不构成验收风险 |

#### 4.2.4 F-09：提交粒度偏离 —— **登记为已接受的偏离**

契约 §3.4 要求「每个缺陷 ID 一次提交」；实际 S2.2 将 5 个缺陷 ID（SEC-001/SEC-002/LM-005/LM-006/LM-023）合并为 1 次提交 `4be1a8c`（提交信息含全部 5 个 ID，可追溯性满足）。实现者理由「拆分会产生不可编译的中间提交」**部分成立但不充分**（会话/票据与 CSRF 同文件同结构，理论上可按缺陷切 hunk 提交）。

**独立评估：形式偏离，不构成阻断**，理由：(a) 计划 §1.3 的口径是「每个**任务**一个独立提交」，S2.2 = 1 提交，符合；(b) `4be1a8c` 已成为共享分支祖先且其后有他人提交，重写历史风险大于收益；(c) 缺陷 ID 在提交信息中完整可追溯。**不返工**；后续步骤一律按「一任务一提交」执行。

#### 4.2.5 M2 的 `NOT_VERIFIED` 清单

`W6 白名单回显`（交付网）｜`W13/W13.5 服务端不再接受 ?pwd=`（交付网）｜`W2 绝对 TTL 分支`｜`W5 Host 缺失`｜`W5 转发头可信代理开关`｜`W10 改密后 WS 断连`｜`W9 DPAPI 保护失败分支`｜`W11 UI 明文告警/实际监听地址`（自动化）｜`真实浏览器跨源读取`（`BLOCKED_ENV`，无浏览器驱动）｜`端到端真实浏览器登录/WS 握手`（`BLOCKED_ENV`）。

#### 4.2.6 M2 重核结论（2026-10-23，候选 `dfe0fff`）——**M2 放行（PASS）**

S2.4 补齐护栏后由**独立 verifier 重核**（不依赖实现者自述，自行重做全部变异）：**10 条变异中 9 条被捕获**，全量回归退出码 0、跳过数未增加（仍为 M2 门禁时的同 7 条），两个提交**零生产代码改动**（`git show --stat` 无 `src/**` 条目）。

| 变体 | 破坏点 | 实际失败用例 | exit | 捕获 |
|---|---|---|---|---|
| V1 | 删 `:1995` 白名单回显 | `C1_白名单内的Origin回显具体源且绝不带通配符` | 1 | ✅ |
| V2 | `:1995` 改回 `Access-Control-Allow-Origin: *` | 同上 | 1 | ✅ |
| V1b | 删 `:1993` 白名单成员判定 | `C1_白名单外的Origin不返回任何CORS头` | 1 | ✅ |
| V3 | `HasLegacyPasswordQuery` 恒 false（HTTP+WS 同时恢复） | C2 ×2 | 1 | ✅ |
| **V3a** | **仅** HTTP `:1175` 失效 | **仅** `C2_...HTTP...` | 1 | ✅ **分支级独立护栏** |
| **V3b** | **仅** WS `:2372` 失效 | **仅** `C2_...WebSocket...` | 1 | ✅ **分支级独立护栏** |
| V4 | `:871` 绝对 TTL 判定失效 | `C3_会话超过绝对期限后失效` | 1 | ✅ |
| V5 | `:1801`（Host 缺失⇒拒）单独失效 | **无（35/35 全绿）** | 0 | ❌ 见 LM-037 |
| V5b | `:1801`+`:1805`+`:1806` 同时失效 | `C3_缺少Host头时返回403` | 1 | ✅ 行为级护栏 |
| V6 | `:927` `client.Close()` 删除 | `C3_改密后已建立的WebSocket被断开` | 1 | ✅ |

**判别力陷阱已消除**：S2.4 首轮 M5' 只红一条 —— WS 那条最初没带票据，"因票据无效而被拒"同样能让它绿。最终版（`dfe0fff`）明确登录取**有效会话**并申请**两张有效票据**（`Assert.False(string.IsNullOrEmpty(ticket1/ticket2))`），V3b **精确命中**该用例 ⇒ 护栏落在"旧路径被移除"本身。

**LM-034 未削弱 S2.4 护栏**：在 HEAD `6d993d7` 上 `WebSessionProtocolTests` **35/35 全绿**且 V3b 仍被捕获（"被拒握手主动关闭"未削弱护栏）。

**契约条款重新判定**：W6 白名单回显 → **VERIFIED_FIXED**；W13/W13.5 服务端不再接受 `?pwd=` → **VERIFIED_FIXED**；W2 绝对 TTL → **VERIFIED_FIXED**；W5 Host 缺失 → **VERIFIED_FIXED（行为层）**；W10 改密后 WS 断连 → **VERIFIED_FIXED**。W5 转发头开关（F-04）、W9 DPAPI（F-08）、W11 自动化、真实浏览器跨源读取、端到端真实浏览器登录 ⇒ **维持 NOT_VERIFIED / BLOCKED_ENV，不计入通过**。

**口径纠正（归档以实测为准）**：S2.4 自述的全量数字（1181 / 58）**并非在其提交点 `dfe0fff` 上测得** —— verifier 在 `dfe0fff` 实测为 **1161 / 13 / 19**（差异来自 `dfe0fff` 之后并入的 S4.1b / S3.3 / S4.2 / LM-034）。最终发布提交点仍须重跑一次全量回归。

**M2 放行条件**：① LM-036 恢复被删断言或登记为已接受偏离；② LM-037 登记为信息项；③ 两条 `BLOCKED_ENV` 不得计入通过；④ 最终发布提交点重跑全量。

#### 4.2.7 重核后新增的发现项

| ID | 级别 | 摘要 | 处置 |
|---|---|---|---|
| **LM-036** | 🟡 低 | `dfe0fff` **无必要地删除**了一条断言（`WebSessionProtocolTests.cs` WS 用例中的 `Assert.DoesNotContain("ticket", resp, …)`）。verifier 的 V7 验证：把该行加回后 35/35 仍全绿 ⇒ **删除不必要**。虽**不削弱判别力**（V3b 仍能捕获），但违反契约 §5.2「不得放宽断言」的**形式要求** | **已修复**（恢复该行，复跑 35/35 通过、退出码 0） |
| **LM-037** | 🔵 信息 | Host 缺失的拒绝存在**三层冗余**（`:1801` 缺 Host⇒拒 / `:1805` 空主机名⇒拒 / `:1806` 不在允许集⇒拒）⇒ 单改 `:1801` 用例仍绿，须三处同时失效才变红。契约条款**已有行为级护栏**（V5b），但该具体行号无独立回归护栏；构造"空主机名但命中允许集"的请求在当前架构下不可达 | 登记为信息项，**不阻断** |

#### 4.2.8 M2 放行条件 #4 的执行：最终发布提交点全量回归（2026-10-23，候选 `366bf9c`）

M2 放行条件 ④ 要求"最终发布提交点重跑全量"。编排方在提交点 `366bf9c` 上按 **CI 口径**重跑：`LITEMONITOR_PLUGIN_REPO` 指向仓库内夹具 `tests/fixtures/plugin-repo`（⇒ 6 条市场契约用例**真正执行**而非被跳过）、产出 TRX 后由 `.github/workflows/assert-test-counts.ps1` 做硬断言、另跑依赖清单核对。

| 程序集 | 总计 | 通过 | 失败 | 跳过 |
|---|---|---|---|---|
| `LiteMonitor.Tests` | 1193 | 1192 | 0 | 1 |
| `LiteMonitor.Updater.Tests` | 58 | 58 | 0 | 0 |
| `LiteMonitor.GpuNonperformance.Tests` | 19 | 19 | 0 | 0 |
| **合计** | **1270** | **1269** | **0** | **1** |

- `dotnet test LiteMonitor.sln` 退出码 **0**；`assert-test-counts.ps1` 退出码 **0**（合计 1270 ≥ 阈值 1150；`MarketIndexContractTests` 发现 6 条 / 通过 6 条 / 被跳过 **0** 条）。
- 唯一跳过项：`LiteMonitor.Tests.MarketE2ETests.端到端_拉索引_安装_校验_卸载`（原因：需 `LITEMONITOR_E2E=1` 才运行，会真实访问 GitHub）—— 即 CI 口径允许的**那 1 条**（`MAX_SKIPPED_TESTS: 1`），非新增跳过。
- `scripts/generate-dependency-manifest.ps1 -Mode Check` 退出码 **0**：`SUMMARY MISSING=0 MISMATCH=0 EXTRA=0`、`SUMMARY-TRANSITIVE MISSING=0 MISMATCH=0 EXTRA=0`。
- **M2 放行条件 ④ 满足** ⇒ M2 的 ①②③④ 四项条件全部达成，**M2 正式放行**。

复现命令（本地等价 CI 口径）：

```powershell
cd 'G:\TOOL\LiteMonitor-fork'
$env:LITEMONITOR_PLUGIN_REPO = "$PWD\tests\fixtures\plugin-repo"
dotnet test LiteMonitor.sln -c Debug --nologo --logger "trx;LogFileName=results.trx" -v minimal
pwsh -NoProfile -File '.github/workflows/assert-test-counts.ps1' -TrxRoot tests -MinTotal 1150 `
  -ExpectedAssembly 'LiteMonitor.Tests, LiteMonitor.Updater.Tests, LiteMonitor.GpuNonperformance.Tests' `
  -MaxSkipped 1 -MustExecuteTestClass MarketIndexContractTests -MustExecuteTestClassMinCount 6
pwsh -NoProfile -File 'scripts/generate-dependency-manifest.ps1' -Mode Check
```

---

### 4.3 M3 门禁复核结论归档（候选 `cb460b5`，2026-10-22）

**结论：`PASS_WITH_NOTES`，建议放行（附三项强制条件，均已满足）。**

S3.1 / S3.2 / cb460b5 在各自所有权范围内的行为均被独立复现且**具备判别力**：15 处破坏点、6 轮变异**全部被捕获**（含 1 处挂死）。全量回归 **1153/1146/7/0 + 13/13/0/0 + 19/19/0/0 = 1185**，退出码 **0**（两次独立复跑）。

#### 4.3.1 变异证据（节选）

| 变体 | 破坏点 | 结果 |
|---|---|---|
| M1-a | 去掉 8 MiB 下载上限 | 2 条 FAIL ✅ |
| M1-b | 安装路径去掉强制摘要 | 1 条 FAIL ✅ |
| M1-c | `AllowAutoRedirect` false→true | 1 条 FAIL ✅ |
| M2-d | Host 精确匹配→`Contains` | 1 条 FAIL ✅ |
| M2-e | 移除逐跳重定向重校验 | 3 条 FAIL ✅ |
| M3-f | `matchTimeout` 500 ms→10 s | 1 条 FAIL（"灾难性回溯耗时 9987 ms"）✅ |
| M3-g~k | 去掉 6 个沙箱上限 | 8 条 FAIL ✅ |
| M4 | 恢复宽泛 `catch{return val;}` | 4 条 FAIL ✅ |
| **M5** | **彻底删除 `matchTimeout` 参数** | **挂死 120 077 ms 未完成**（HEAD 同用例约 500 ms 通过）✅ |
| M6 | 记录写入提前到 `File.Move` 之前 | 1 条 FAIL + 探针 FAIL ✅ |

M5 另做独立佐证：`^(a+)+$`（40×a + `!`）无 timeout 时 **180 s 未完成**；带 500 ms timeout 时 **494 ms** 抛 `RegexMatchTimeoutException` ⇒ `matchTimeout` 参数确被真实传入并生效。

#### 4.3.2 发现项

| ID | 级别 | 摘要 | 处置 |
|---|---|---|---|
| **LM-030** | 🟠 中 | **LM-009 的 6 个上限在 `cb460b5` 上仍未接入端到端执行路径**：`PluginSandbox`/`PluginSandboxScope` 在 `src/` 下**仅出现在 `PluginProcessor.cs` 自身**，`PluginExecutor.cs` 命中数 **0** ⇒ 6 个上限中 **5 个**（响应体 2 MiB、步骤 16、目标 64、总时长 30 s、并发 8）在生产**完全不生效**（执行器仍用自带 8 MiB 传输上限 `PluginExecutor.cs:537`）；仅"正则超时"经 `ApplyTransforms` 间接生效 | **指派 S3.3**（`PluginExecutor.cs` 所有权）。**闭环前禁止在任何里程碑结论中记为"LM-009 已闭环"**。注：`PluginProcessor.cs:66-72` 的"覆盖边界"自述虽诚实，但**文档 ≠ 闭环** |
| LM-031 | 🟡 低 | `PluginProcessor.cs:135-138` `ExtractJsonValue` 仍 `catch { return STATUS_UNKNOWN; }` 宽泛吞异常。**属既有行为**（基线 `4be1a8c` 即存在），非 S3.2 引入 ⇒ 不判本轮失败 | 登记为后续清理项，**不阻断** M3。不建议在声明式插件场景下盲目改成传播异常 —— "坏字段降级"与"整体失败"是设计取舍，需单独评估 |
| LM-032 | 🟡 低·测试质量 | `PluginMarketPolicyTests.cs:420` 用例名 `安装_成功_先落盘再写记录且内容与摘要一致` 只断言**终态**（文件存在/内容/记录字段），**不直接断言顺序**；真正抓住顺序回退的是 `启动恢复_替换已发生但记录未更新_回滚到旧内容`（M6 已证明） | 登记为后续清理项（重命名或补顺序断言）。**不阻断** |
| LM-033 | 🔵 流程 | 门禁基线描述陈旧：任务书下限写 1152(1145/7)，仓库 CI 自述为 **1185(1153+13+19)** | 已按 §8 更新基线数字 |

#### 4.3.3 M3 放行条件（3 项，均已满足）

1. ✅ LM-030 登记为独立缺陷并指派 S3.3，验收口径改为"`PluginExecutor.cs` 端到端路径实测触达 6 个上限"。
2. ✅ LM-031 / LM-032 登记为后续清理项（低 severity，不阻断）。
3. ✅ M3 表述显式声明 6 条契约 + 1 条联网 E2E 为 `NOT_VERIFIED`，不计入通过数。

---

#### 4.3.4 S3.3 + S3.4 联合专项复核（2026-10-23，候选 `bc7b479`）—— **VERDICT: PASS**

由**独立 verifier** 在隔离 worktree（`git worktree add --detach .artifacts/wt-lm030 bc7b479`）中复核，**未触碰共享主工作区**。verifier 自做 **9 个变异**（6 个上限 + 2 个口径分离 + 1 个目标上限差一），**每个都让对应用例变红**。

| 变异 | 破坏点 | 变红用例 | 端到端判定 |
|---|---|---|---|
| M1 | 去 `Math.Min(实例 8 MiB, 沙箱 2 MiB)` | `端到端_响应体超沙箱天花板` | ✅ 真端到端 |
| M2 | 注释 `scope.EnsureStepAllowed()` | 3 条 | ✅（2 条端到端 + 1 条步骤级） |
| M3 | 注释 `scope.EnsureTargetsAllowed(...)` | `端到端_目标数超限` | ✅ 真端到端 |
| M4 | 等待者不监听 `scope.Token` | `端到端_总时长超限` | ✅ 实例级生产路径 |
| M5 | 注释 `AcquireRequestSlotAsync` | `端到端_并发超限` | ✅ 步骤级生产路径 |
| M6 | `RegexMatchTimeout` → 10 s | 仅 2 条**单元级**变红 | ⚠️ 端到端只锁"可达性 + 原因码"，**未锁 500 ms 数值** ⇒ **LM-041** |
| M7 | 步骤计数改回实例级累加 | 4 条（含 3 条端到端） | ✅ 步骤/总时长两口径**确实分离** |
| M8 | 目标级作用域自带截止时刻 | 2 条（含端到端） | ✅ 同上 |
| M9 | 目标上限差一（`>` → `>=`） | 被改写的那条断言**变红** | ✅ 护栏强度**不降反升** |

**§1.3 既有断言改动专项审查（`端到端_目标数恰好等于上限_目标上限不触发`）**：

- **判断：正当，护栏强度不降反升。** 旧断言 = "以别的原因码失败（且非 TargetLimit）"；新断言 = "**根本不许失败**"。旧断言 `Assert.Equal(StepLimit, ex.Reason)` 在新语义下**必然失败**（64 目标 × 1 步不再触发 StepLimit 且不再抛异常）⇒ 同一场景**无法既"不改断言"又"让新语义成立"**；改场景的替代覆盖已由 `端到端_多目标_单目标链超16步_仍按LM009_STEP_LIMIT失败` 完整提供。
- **硬证据**：M9（目标上限差一）与 M7（口径回退）**都让这条被改写的断言变红** ⇒ 它**仍具判别力**。

**口径判定**：步骤数（按目标）✅ / 总时长（按实例共用截止时刻）✅ / 并发（按进程）✅ / **目标展开数（按实例累计）⚠️ 口径 ≠ 实现** ⇒ **LM-040**。逐字段审计 `PluginSandboxScope`（`:551-561`）确认**未发现数据竞争**（`_stepCount` 每目标各持一份并 `Interlocked.Increment`；`_cts` 共享且 CTS 线程安全；`Max*` 只读）。

**内置插件实测复核**：12 个内置插件中**仅 9 个有 `inputs`**，这 9 个的 **18 个 input 声明 100% 为 `"scope": "target"`**（`Holiday`/`PublicIP`/`WeiboHot` 的 `inputs` 为空数组）。每目标步数：Stock=2、Weather=3、其余 10 个=1 ⇒ **全部 ≤ 16**。实测 12/12 均为"成功"（Stock 9 目标、Weather 6 目标，与裁决理由逐字吻合）；M7 实测证实"旧语义下同一探针全部会失败"。

**残留风险（已记录，不阻断）**：单实例一次执行最大步骤数 16 → 64×16 = **1024**；每次响应 ≤ 2 MiB，而 `ParseAndExtract` 在 `response_format="text"` 时把**整段原始响应**写入 context（`PluginExecutor.cs:856-862`，context 为每目标一份）⇒ 单实例可保留 ≈ **2 GiB** 原始文本（需攻击者服务器在 30 s 内投递 ~68 MB/s；**上界可达，非必然**）。30 s 只约束时长、8 槽只约束并发、2 MiB 只约束单响应 —— **三者都不约束累计内存**。

**方法论陷阱（已记入 §1.1 第 5 条）**：9 次变异后首轮全量回归出现 2 条失败，签名与 M9 完全一致 ⇒ 根因是 `Copy-Item` 还原**保留旧时间戳**导致 MSBuild 增量判定未重编译，回归跑的是 **M9 的陈旧二进制**。修正时间戳并 `--no-incremental` 强制重建后全量回归 exit 0（`LiteMonitor.Tests` 1189/1182/**0** 失败/7 跳过、Updater 58/58、Gpu 19/19），跳过清单与基线一致（未增加），契约数值未被改动（`git diff b8444a1 bc7b479 -- src/Plugins/PluginProcessor.cs` 未触及任何数值常量）。

**放行结论**：**LM-030 闭环成立**（其缺陷描述"6 个上限中 5 个完全不生效"已被证伪并修复）。**但"LM-009 已闭环"的表述必须限定为"经 `PluginExecutor` HTTP 步骤路径的 6 个上限"** —— 不得宣称"插件一切网络出口受沙箱约束"，因为 **LM-039**（`native://` 出口）尚未修复，且它**直接违反 S3.2 的明文验收条款**（"总并发数"须有效、"API Key 只发往用户知晓并允许的目标"）⇒ **LM-039 必须修复，不得仅登记**。

---

#### 4.3.5 S3.5 + S3.6 + 主审集成的独立复核（2026-10-23，锚定 `711ec61`）—— **VERDICT: PASS_WITH_NOTES**

由**独立 verifier** 在隔离 worktree（`git worktree add --detach .artifacts/wt-verify 711ec61`）中复核，**未触碰共享主工作区**，且**不采信任何实现者自述**。

**纪律执行**：每轮变异 = 改坏 → `dotnet build --no-incremental` → `dotnet test --no-build` → `[System.IO.File]::WriteAllBytes` 字节级还原 → `Get-FileHash` **逐文件 SHA-256 比对** → 强制重编译 → 复跑 **53/53 全绿（exit 0）**。收尾实证：worktree `git status --porcelain` 空、4 个变异文件 SHA-256 与基线逐字节一致；主工作区 `git status` 空；`master` 仍为 `9ffb4219`。

**10 轮自设计变异，全部变红（无一条护栏失灵）**：

| # | 破坏点（verifier 自设计，非照抄实现者） | 变红用例 | 捕获 |
|---|---|---|---|
| M1 | `CryptoNative.GetStringAsync` 删掉 `AcquireRequestSlotAsync` | `原生步骤_并发闸门生效_…LM009_CONCURRENCY_LIMIT失败` | ✅ |
| M2 | 原生正文读取改传 `int.MaxValue` | `原生步骤_响应体超沙箱天花板_按LM009_RESPONSE_TOO_LARGE失败` | ✅ |
| M3 | `nativeToken` 改为 `CancellationToken.None`（不绑 `scope.Token`） | `原生步骤_总时长到期_被及时中止且原因码为LM009_DEADLINE_EXCEEDED` | ✅ |
| M4 | 删执行器侧 native 预检 + 传给 `CryptoNative` 的 `allowedHosts` 置 `null` | `原生步骤_fallback主机来自用户输入且未声明_…` 与 `原生步骤_带凭据且回退主机未在模板级声明_…`（均"No exception was thrown"） | ✅ |
| M5 | `EnsureTargetsAllowed` 改回比较入参 | `目标展开数_按实例计_跨目标累计超上限即失败` | ✅ |
| M6 | `MaxRegexMatchTimeoutMs` 500 → 10 000 | **端到端正则用例由绿转红**（旧版漏判的那条）+ 2 条单元级 | ✅ |
| M7 | 目标上限差一（`>` → `>=`） | 3 条 | ✅ |
| M8a | `CreateScope` 硬编码 5 分钟 | `端到端_生产入口实际使用的总时长_等于契约值30秒` | ✅ **正是 S3.6 未能实测的变体** |
| M8b | `DefaultExecutionDuration` → 5 分钟 | 同上 + 2 条 | ✅ |
| M8c | 生产入口**绕过** `CreateScope` 直接 `new` | 同一用例（`CapturedScope` 为 null） | ✅ **证明缝隙无假绿** |

**其余核查项**：契约 6 常量逐行核对**未改**（`PluginProcessor.cs:447/450/453/456/459/462`）；CI 口径回归 **1280 / 1279 通过 / 0 失败 / 1 跳过**，`assert-test-counts.ps1 -MinTotal 1280` **恰等于**实测总数 ⇒ 删任何一条用例即跌破阈值变红；`PluginSandboxScope` 全类**唯一** static 成员是 `DefaultExecutionDuration`（不可变），进程级登记表已彻底消失；三个提交在 `src/` 新增的 static **字段**只有 `CryptoNative.SourceUrlOverrides`（`internal static`），全仓赋值点**仅在测试文件** ⇒ 生产路径确实不会设置它（LM-046 描述属实）；内置 `Crypto.json` 的官方源落在允许集内 ⇒ **未被误杀**。

**LM-045 独立实证为真实且定级准确（P1）**：verifier 自建本地双服务器复现（声明主机 `127.0.0.1` → 302 → 未声明主机 `127.0.0.2`），实测：未声明主机**确实收到产品发出的请求**；请求头带 **`X-Api-Key=SECRET-APIKEY`**，且 **`Cookie` 被原样带过去**（`Authorization` 被 .NET 跨主机剥离，但前两者不会，而二者均被产品自身 `IsCredentialHeaderName` 判为凭据头）⇒ 沙箱"凭据只发往声明主机"的承诺**确实被破坏**；且未声明主机的**响应数据进入插件流水线并被用于构造后续请求**。

> **对闭环口径的二次更正**：§4.5.4 的 LM-039 行原写"闭环范围扩展到 HTTP 步骤路径 **+ native:// 路径**" —— 经复核，**该表述仍不可用**：LM-045 证明 HTTP 步骤路径在**重定向场景下**同样会把凭据发往未声明主机 ⇒ 正确口径是「**直连目标**已受约束，**重定向后的目标不受约束**」。§7 的 LM-009 行已同步更正。

---

### 4.4 本轮并行步骤产出台账

| 步骤 | 提交 | 关键结论 |
|---|---|---|
| S0.3 | `d84b40f` | 建立最小 CI 并强制校验测试发现数（BASE-004）。配套 `assert-test-counts.ps1` 逐程序集核对总计/通过/失败/跳过，"0 发现"或缺程序集即红 |
| S1.0 | `e1a7978` | 冻结 S1.0 在途租约模型与请求身份契约（C1–C7） |
| S1.1 | `ac652f8` + `afb0869` + `6609543` | LM-001 插件计时器收尾 + 实例代次守卫；`afb0869` 修 M1-F6（`_timers` 锁外读 → 锁内 `exists`）；`6609543` 补强 F1/F2/F3/F4/F7 判别力。**根因已更正**（见 §4.1）：提交信息中的"永久停摆"前提已被证伪，以 §4.1 为准 |
| S1.2 | `d437283` | LM-002 租约模型 + LM-003a 请求身份纳入方法/凭据/代理与 SHA-256（21 用例） |
| S1.3 | `2bbb4cd` | LM-003b/c 更新器三段期限 + 资源释放 + 测速无进展期限（15 用例） |
| S2.0 | `5521d1c` + `1beff7f` + `0f1177e` | 冻结 S2.0 主会话/票据与访问边界契约（W1–W14、W13.5 端点冻结）；补遗签发 L0 登录语义 / 既有测试迁移义务 / A10↔W2 |
| S2.1 | `0484177` | LM-004 DOM 文本化 + 前端协议迁移（12 用例） |
| S2.2 | `4be1a8c` | SEC-001/SEC-002/LM-005/LM-006/LM-023：默认环回监听、CORS `*` 移除、主会话+票据+CSRF、PBKDF2/DPAPI（27 新用例）。**M2 首轮门禁在此提交上判定 CONDITIONAL**（见 §4.2） |
| S2.4 | `e893faf` + `dfe0fff` | 补 CORS 白名单回显与旧 `?pwd=` 拒绝的 8 条回归护栏；`dfe0fff` 强化 WS 断言（有效票据 + 有效会话）。**M2 重核放行的依据**（见 §4.2.6） |
| S3.1 | `14542f3` | LM-007/LM-008 市场 URL 精确校验 + 逐跳重定向校验 + 8 MiB + 强制摘要 + 事务化安装（53 用例 / 11 变异全捕获） |
| S3.2 | `b8444a1` | LM-009 插件沙箱原语：6 上限 + 7 原因码 + 异常语义 + `PluginSandboxScope`/`PluginSandbox`（20 用例 / 7 变异全捕获）。**冻结契约，数值不得改** |
| S3.3 | `ebe1c38` | LM-009 沙箱接入 `PluginExecutor` 端到端路径（15 用例 / 7 变异全捕获）⇒ LM-030 闭环 |
| S3.4 | `bc7b479` | 步骤数上限改为**按目标计**（`PluginSandboxScope.BeginTarget()` 子作用域，8 新用例 / 3 变异全捕获）。语义变更裁决见 §4.5.3 |
| S3.2b | `cb460b5` | LM-008 本地化：9 语言包文案 + `LanguagePackTests` 守卫（228 键 / 14 用例） |
| S4.1 | `a8fb2fc` | 插件市场契约夹具入库 + CI 接线（ENV-003）。**6 条契约用例从"永远跳过"变为"真正执行"**（跳过 7→1，只剩联网 E2E）。CI 门禁**收紧**：`MAX_SKIPPED_TESTS: 1` + `MUST_EXECUTE_TEST_CLASS: MarketIndexContractTests(≥6)`，并做负向验证（置空 env var ⇒ 退出码 1、3 条 FAIL）。**关键发现**：原 `assert-test-counts.ps1` 只统计并打印跳过数、**没有任何上限** —— 这正是 ENV-003 能长期存在的原因 |
| S4.1b | `6c89a8b` | 5 类越界反例补自动化覆盖（S4.1 只留台账；台账不跑就只是文档） |
| S4.2 | `1a904d5` | LM-016 验签→更新器**字节绑定**（独立验证 + `FileShare.Read` 字节钉住，58 用例）+ 重建 `resources/LiteMonitor++.Updater.exe`；ZIP 展开边界加固 |
| S5.3 | `217102d` | NOTICE 版本修正 + 补 3 个漏列依赖 + 依赖清单护栏脚本。**方法论突破**：产物内嵌 `ProductVersion = 1.0.0+<git commit SHA>`，据此可判定"包内更新器是否由本次源码构建"（.NET 发布产物**非字节确定**，**不可**用重新 publish 比 SHA-256） |
| S5.3b | `e452097` | 补全**传递依赖**披露并扩展护栏（NOTICE 须覆盖实际随包分发的传递依赖）⇒ LM-024 闭环 |
| LM-034 | `6d993d7` | 被拒 WebSocket 握手补 `else { client.Close(); }`（4 用例）；危害描述经实测更正为"套接字泄漏"（见 §4.5.4） |
| CI 护栏 | `600a18c` | 把依赖清单核对接入 CI（LM-024 护栏：NOTICE 与实际解析依赖不一致即红） |
| 归档 | `56d573e` / `b0066da` / `d30f314` / `4aa4915` / `366bf9c` | 分别为：LM-001 根因更正 + M1 复核 F1–F8 归档 / M2 首轮 CONDITIONAL 归档 / M3 门禁 PASS_WITH_NOTES 归档 / LM-034 危害描述更正 / LM-036 断言恢复 + M2 重核放行归档 |
| **S3.5** | `d23e6fc` | **LM-039**：`native://` 步骤接入沙箱闸门 —— 原生取数复用**同一套既有原语**（`AcquireRequestSlotAsync` 进程级槽位 / `ReadResponseBodyAsync` 2 MiB / `scope.Token` 30 s 截止 / `EnsureCredentialTargetAllowed` 凭据目标），并新增**允许目标集合**（模板声明主机 ∪ native URL 内嵌字面量主机）—— 校验发生在**任何请求发出之前**（`fallback=<用户填的任意 URL>` 必拒，`LM009_CREDENTIAL_TARGET_NOT_ALLOWED`）。8 条新用例 / 5 变异全捕获 |
| **S3.6** | `d588440` | **LM-040~044**：目标展开数改判**累计值**（`Interlocked.Add` 返回值，跨 target 累计 > 64 即失败）；端到端正则用例钉住 **500 ms**；目标数用例 URL 改目标专属并收紧为**恰好 64 个请求**（实测旧 URL 因在途合并只得 61）；删除 2 处 `failure == null` 时恒真的空转断言；生产默认时长收敛到唯一出处 `DefaultExecutionDuration`。2 条新增 / 3 条期望反转 |
| **主审集成** | `6310dbf` | **LM-044 缝隙收敛**：把 S3.6 放进 `PluginSandboxScope` 的**进程级静态登记表**换成 `PluginExecutor.CreateScope` **实例级虚方法缝隙**（生产入口唯一创建点），移除只服务于测试的全局可变状态；**并补上 S3.6 自述未能实测的变体**（`CreateScope` 硬编码 5 分钟 / 生产入口绕开 `CreateScope`）—— 3 变异全捕获 |
| **GPU T01** | `2c8915b` | 实测判定 **`RECREATE_HANDLE_SAFE`**：`Control.RecreateHandle()` 可安全使用 ⇒ 走计划 §4.1 主线，**不启用** §9 备用路线。4 组独立运行（S1 正式 renderer / S2 MainForm 形态×2 / **真实 MainForm**）结论一致：句柄事件 `HandleDestroyed → HandleCreated` 有序、代次恰好 +1；窗口状态（可见性/置顶/扩展样式/矩形/Region/分层 alpha/前台句柄）逐项相同；**桌面像素回读**（`CopyFromScreen`，非 `DrawToBitmap`）证明重建后 GDI 动态帧恢复（1024/1024，相邻帧颜色不同），对照组停在冻结 GPU 帧（0/1024，三帧逐位等于 GPU 帧色）。真实 MainForm 分支**隔离门通过**（托盘不可见、网页端口未监听、用户/仓库目录零增减），**非** `NOT_VERIFIED` |
| **owner A 物化** | `e874e2a` + `28c6805` | T02 合同序 1/2/3 物化（合同 §12.4 的前置，**不能外包**，由主审直接做）：§3.2 全部纯类型（`src/Rendering/Configuration/RenderingTransitionState.cs`）+ §7.2 句柄代次唯一真相源（`WindowHandleGeneration.cs`）+ §3.1 `WindowIsLayered` 路由分支（`RenderingRoutePolicy.cs`）；13 条用例 / **4 变异全捕获**。**裁决 D-G**（`28c6805`）：唯一实例由**类型自身**持有（`WindowHandleGeneration.Instance`）—— 集成巡检实测发现 owner B（`src/UI/`）与 owner C（`src/Rendering/Presentation/`）**各自**建了一份 holder ⇒ 进程内两个单例 ⇒ 合同 §7.4 的跨代次判据**恰好在它要防的场景里失效**；下移到 owner A 的低层目录后，依赖方向始终自上而下 |
| **LM-045** | `198a9d4` | **P1 闭环**：HTTP 步骤路径与 **`native://` 路径两侧**都关闭自动重定向、改为**逐跳**跟随并**逐跳复核目标主机**；带凭据的每一跳须落在**声明主机集合**内（`LM009_CREDENTIAL_TARGET_NOT_ALLOWED`），不带凭据的须落在（声明集合 ∪ {直连目标主机}）内（`LM009_REDIRECT_TARGET_NOT_ALLOWED`）；不通过 ⇒ **该跳请求根本不发出** ⇒ 凭据不外泄、未声明主机的响应也不进入流水线。跳数上限 5 / 缺 `Location` / 相对 `Location` / 非 http(s) / **https→http 降级**均被拒。22 条新用例 / **7 变异全捕获**。**范围由主审集成巡检扩大**：原生侧存在同构缺口 ⇒ 新登记 **LM-053**（随本提交一并修掉） |
| **GPU T04** | `4ade856` | **GPU-001 根因修复**（owner C）：清理责任**转交**（宿主恢复失败 ⇒ 产出 `WindowCapturePolicyCleanup` 交给订阅者并**释放自己**，`_presentation = null`）⇒ 三条重启路径重新可达；`PresentationSession` 补齐 R1–R6（代次单调校验 / 恢复失败不改句柄 / 新句柄重读原值 / 代次可观测 / 失败码可区分）；§6.4 重试预算 **6 次** + 250/500/1000/2000 ms 退避；§7.4 代次判据**先于** `IsWindow`（旧代次 ⇒ 禁止任何 Win32 调用）。14 条用例 / **5 变异全捕获** |
| **owner A 接线** | `03a925a` | **GPU-005 守卫侧终结路径**（主审接线）：`IGpuStartupAttemptAbandon` **下移**到 `src/Rendering/Configuration/`（主审集成准则：接口若需下层实现必须放低层目录，否则形成 `Configuration ↔ Presentation` 命名空间循环依赖）；`GpuStartupGuard` 新增 `AbandonAttemptPreservingPending`（**只注销尝试，刻意不删 pending 标记、不复位 `_attemptWritten`** ⇒ 下次进程进入 conservative、等待用户显式重试）；`GpuStartupAttempt` 实现该接口（幂等）。5 条用例 / **3 变异全捕获**（MB3 曾一度假绿 —— 判别点被 `Dispose()` 抢先置位，已把判别点移到 `Dispose()` **之前**）。**该提交同时关闭 T04 报告里标为 `NOT_VERIFIED` 的「停止不干净 ⇒ pending 保留」在 guard 侧的空缺**（宿主侧仍需真实 GPU，属 T07/T09） |
| **GPU T05** | `07a1b03` | **legacy 回退样式**（owner D）：加法式兄弟块 `rendering.fallbackStyle`（`ThemeFallbackStyle`，6 个纯 CLR `string?` 字段 + 自定义 JSON 转换器），`rendering.fallback` 类型与 legacy/legacy 校验**一字未改**；非法值走**非致命**诊断 `gpu_fallback_style_invalid`（不进 `Validate()` 错误列表）；编辑器克隆/恢复路径与 `ThemeManager.PruneNode` 保留路径均保住该块；`LegacyFallbackStyleResolver` 为**纯函数**（不改 Skin、不改 `ThemeManager.Current`、自动回退不写盘）；三份 GPU 主题各补一个回退样式块。13 条用例 / **4 变异全捕获**（含"撤掉 `Validate` 扩展"与"编辑器克隆丢块"）。**跨 owner 接线点由 T03 消费**（见下一行） |
| **GPU T03** | `5a1eeb7` + `f4f3b2a` | **切换协调器 + 受控重建 + 判别位**（owner B）：新增 `src/UI/RenderingTransitionCoordinator.cs`（**状态与判据的唯一落点**：期望/有效路由、切换阶段、三重身份、HWND 呈现历史、清理状态、诊断字段、HandleCreated 判别位）与 `src/UI/CapturePolicyCleanupLedger.cs`（**未完成 affinity 清理责任的独立持有者**，合同 §6.2 INV-1~INV-6）；GPU→legacy 固定 **9 步**（`Decide` → 窗口状态快照 → 停呈现/转交清理 → 受控 HWND 重建 → 代次推进 → 布局/DPI 恢复 → 窗口状态恢复 → 一次有效重绘 → 发布稳定）；判别位规则 ①~④（受控 vs 构造期首次创建 / 取消归零 / 陈旧残留归零 / 5 秒超时兜底）；**清理责任永不通过宿主引用表达**（`StopGpuPresentation` **无条件**置 null）；`LiveGlassController` 的**第二个句柄代次计数器已删除**（⇒ 消除上一轮 verifier 报告的 F7）；回退样式经 `LegacyFallbackStyleBridge` 生成**派生主题副本**（不改 `theme.Color`、不改 `ThemeManager.Current`、不写盘）。44 条用例 + **主审补 1 条钉住裁决 D-H 的用例**（变异自证：删掉 accent 落点 ⇒ 红 ⇒ 字节级还原 SHA-256 一致 ⇒ 绿）。★ **主审集成巡检发现并纠正的缺口（阻断级）**：worker 首版**未订阅** T04 的 `CapturePolicyCleanupRequired` 移交事件、**从未**调用 `ledger.Add` ⇒ ledger 永远 `State=None` ⇒ 清理责任**彻底丢失**（旧 HWND 永远保持 WDA 排除，比 GPU-001 更糟）；且 `StopGpuPresentation` 仍是 T04 落地前的旧口径（"移交出口尚未接入 ⇒ 保留引用"）⇒ **死代码 + 掩盖缺口**。主审发回后补上「订阅 + `LastCapturePolicyCleanup` 兜底」双路（`Add` 同代次幂等）+ 统一工厂 `CreateGpuPresentationHost`（三个创建点订阅不可能漏）+ INV-6 探针 `CapturePolicyCleanupPendingProbe` |

**S5.3 实测的随包分发传递依赖**（均 MIT）：`Vortice.DXGI 3.8.3`、`Vortice.DirectX 3.8.3`、`Vortice.Mathematics 2.1.0`、`SharpGen.Runtime 2.4.2-beta`、`SharpGen.Runtime.COM 2.4.2-beta`、`Microsoft.Diagnostics.FastSerialization`。

> ⚠️ **本段已被 §4.5.1 的 S5.3b 实测推翻，保留仅作留痕**：实际随包分发的传递依赖是 **21 个**（不是 6 个），且 `Microsoft.Diagnostics.FastSerialization` **不是独立 NuGet 包**（是 TraceEvent 包内程序集），真正随包分发的 TraceEvent 传递依赖是 `Microsoft.Diagnostics.NETCore.Client 0.2.661903`。以 §4.5.1 与 `NOTICE` 现文本为准。

**CI 接线待办**：把 `scripts/generate-dependency-manifest.ps1 -Mode Check` 接入 CI（退出码 1 即失败）。`.github/workflows/**` 归 S4.1 所有且已提交；待 S5.3b 落地后由编排方接入。

**环境事实（供后续引用）**：受保护的 `LiteMonitor++.exe`（PID 24936）在本轮**自行退出**；应用事件日志**无崩溃记录** ⇒ 属正常退出而非崩溃。S4.1 与 M3 verifier **两个独立 agent** 均自述从未终止该进程，且全程会话记录中**无任何**针对 `LiteMonitor++` 的 `Stop-Process`/`taskkill` 命令。

---

### 4.5 裁决更正与偏离登记

本节登记两类必须留痕的事：**编排方（mavis）给出的事实或裁决被实测推翻**，以及 **worker 对指令的有意偏离**。两者都不得静默生效。

#### 4.5.1 编排方 brief 的事实错误（以实测为准，已更正）

S5.3b（`e452097`）在实测中推翻了编排方在 S5.3 简报里给出的三处数据：

| 编排方原述 | 实测结论 | 影响 |
|---|---|---|
| "`Microsoft.Diagnostics.FastSerialization`（TraceEvent 附带）是传递依赖" | **它不是独立 NuGet 包** —— NuGet 缓存中不存在 `microsoft.diagnostics.fastserialization`，它是 TraceEvent 4.0.0 包内的程序集（`lib/netstandard2.0/Microsoft.Diagnostics.FastSerialization.dll`，`AssemblyVersion 4.0.0.0`）。故不单列为传递依赖，改为在 NOTICE 传递依赖章节开头以说明段披露（连同 `Dia2Lib.dll`、`TraceReloggerLib.dll`、`amd64/KernelTraceControl.dll`、`amd64/msdia140.dll`） | NOTICE 章节结构变更 |
| "随包分发的传递依赖是 6 个" | 实测是 **21 个**（另 23 个不随包分发）。编排方漏列了 LHM 的配套库（BlackSharp.Core / DiskInfoToolkit / HidSharp / Mono.Posix.NETStandard / RAMSPDToolkit-NDD）与多个 `System.*` 平台库 | NOTICE 补全 21 条 |
| TraceEvent 的传递依赖是 FastSerialization | 真正随包分发的是 **`Microsoft.Diagnostics.NETCore.Client 0.2.661903`** | 清单条目更名 |

**更重要的更正：S5.3 的"随包分发"粗判据是错的。** 原判据（"包内是否有 `lib/<tfm>/*.dll`"）会**多算 5 个包** —— NuGet/MSBuild 会把"与 .NET 共享框架同名且框架版本 ≥ 包内版本"的程序集判为**框架提供**，该文件**不**进发布目录。多算的 5 个是 `Microsoft.Win32.Registry 5.0.0`、`System.Security.AccessControl 5.0.0`、`System.Security.Principal.Windows 5.0.0`、`System.Reflection.TypeExtensions 4.7.0`、`System.Runtime.CompilerServices.Unsafe 6.1.2`。

S5.3b 改用的判据（还原图 `runtime`/`runtimeTargets`/`native` → 排除 `_._` → 只接受 win-x64/win RID → 剔除被 targeting pack 以不低于版本提供的程序集）经**三方交叉验证**：

| 真值来源 | 结果 |
|---|---|
| **A** 实际产物：`dotnet publish -c Release -r win-x64 --self-contained false` 的输出目录 + `LiteMonitor++.deps.json` 的 `targets[".NETCoreApp,Version=v8.0/win-x64"]` 归属 | 26 个包（5 直接 + 21 传递） |
| **B** 脚本判据 `Get-DistributionMap` | 26 个包 |
| **C** MSBuild 自身：`dotnet msbuild -t:ResolveReferences -getItem:ReferenceCopyLocalPaths`（含框架冲突消解） | 26 个包 |

**A ≡ B ≡ C**；且 RID 无关还原与 RID 专项还原两条路径结果一致。

#### 4.5.2 worker 对指令的有意偏离（均已批准）

| 步骤 | 偏离 | 裁决 |
|---|---|---|
| S3.3 | 未使用 `scope.RunStepAsync` 取数，改为在作用域内组合 `AcquireRequestSlotAsync` + 限字节读取 | **批准**。`RunStepAsync` 内部会再调一次 `EnsureStepAllowed()`，会把冻结契约的 16 步上限**重复计数**成 8 步；且它只抛 `PluginSandboxException`，会破坏既有 `PluginRequestIdentityTests.cs:649-652` 的**精确类型**断言（该测试不归 S3.3 所有）。四项语义（并发闸门 / 字节上限 / 总时长取消 / 原因码）与原裁决一致，且既有 LM-003a 行为不破 |
| S4.2 | 重建 `resources/LiteMonitor++.Updater.exe` 时用 `--self-contained false` 而非编排方指定的 `true` | **批准**。仓库发布流水线 `scripts/pack-release.ps1:125` 用的就是 `--self-contained false`（原受跟踪产物 178 738 字节亦为框架依赖单文件）；`true` 会产出 **67 536 024 字节（64 MB）** 的 blob 且与流水线产物形态不一致。自包含变体已一并产出于 `.artifacts/upd-lm016/publish-sc/`（SHA-256 `F45C5E72…`），如需改用可替换后重提交 |

#### 4.5.3 编排方裁决的变更（S3.4）

S3.3 在其交付报告 §5 **显式标出**编排方裁决 3（步骤计数在实例内累计）的未预见后果：单实例总步骤 = 目标数 × 每目标步骤数 > 16 即 `LM009_STEP_LIMIT` 失败 ⇒ **Stock 配 9 只股票、Weather 配 6 个城市就会失败**（常见用法）。

**裁决变更（2026-10-23）：步骤数上限改为「每目标 ≤ 16 步」。** 理由：

1. **安全意图不变** —— LM-009 的步骤数上限针对"单条链无限长"，无论累计口径如何，**单链长度都被限死**；
2. **跨目标放大已有三道独立闸门** —— 目标展开数 ≤ 64（实例内累计）、单次执行总时长 ≤ 30 000 ms（跨 target 累计）、并发 ≤ 8（进程级），**均不变**；
3. **不引入新的攻击面** —— 目标数来自**用户配置**（`PluginInstanceConfig.Targets`），恶意插件**无法自行增加目标数**；
4. **消除真实功能退化** —— LM-009 的目标是安全加固，**不应限制用户配置的目标数量**。

落地归属 **S3.4**；四种计数口径的最终定义（步骤数按目标 / 目标数按实例 / 总时长按实例 / 并发按进程）须在 S3.4 完成后写入 `PluginSandboxScope` 的类文档。

> ⚠️ **本节论证的覆盖边界（2026-10-23 补充门禁更正）**：上文第 2 条"跨目标放大已有三道独立闸门"**只覆盖 `PluginExecutor` 的 HTTP 步骤路径**。`native://` 步骤（`src/Plugins/PluginExecutor.cs:459-462`）走 `CityCodeResolver` / `CryptoNative` 自带的 HttpClient，**不占并发槽、不受 2 MiB 响应上限、不做凭据目标校验、也不绑定 `scope.Token`（不受 30 s 截止时刻中止）** ⇒ 本论证**不覆盖 native:// 出口**。已登记为 **LM-039**。因此"LM-009 已闭环"的结论口径必须限定为"**经 `PluginExecutor` HTTP 步骤路径的 6 个上限**"，**不得**表述为"插件一切网络出口均受沙箱约束"。

#### 4.5.4 本轮新发现的缺陷

| ID | 级别 | 摘要 | 归属 |
|---|---|---|---|
| **LM-034** | 🟡 低~中 | **被拒绝的 WebSocket 握手不关闭连接** —— `WebSessionManager` 的 `client.Close()` 只在 HTTP 分支，WS 握手被拒后连接**悬挂**到服务停止（不发 FIN）。**危害经 S4.2b 实测更正**：是**服务端套接字/句柄泄漏**（每次被拒握手留下 1 条停在 `ESTABLISHED` 的连接，服务端 `TcpClient` 无人 Close），**不是**"占满 64 连接额度致服务不可用" —— `HandleClientAsync` 返回后 finally 已 `Interlocked.Decrement(_activeConnections)`，计数器不会被耗尽（实测 10 次被拒握手后仍能正常完成 101 成功握手）。附带：`SendHttpResponse` 已写 `Connection: close` 却不发 FIN，本身也是协议违约 | **S4.2b** ✅ 已修复（`6d993d7`：在 `if (Handshake(...))` 补 `else { client.Close(); }`，**先写回 401/403/400 响应再关闭**；握手成功路径不受影响） |
| **LM-035** | 🟡 低~中 | `Handshake` 的 `catch { return false; }`（`WebSessionManager.cs:2415`）**宽泛吞异常** —— 写响应失败时根因不可诊断，调用方只能看到"握手失败"。与 LM-031（`ExtractJsonValue` 宽泛 catch）同类 | 登记待清理（本轮未修，避免战线扩张） |
| **LM-037** | 🔵 信息 | Host 缺失的拒绝存在**三层冗余**（`:1801`/`:1805`/`:1806`）⇒ `:1801` 无独立行级护栏（行为级护栏已由 V5b 证明） | 登记为信息项，见 §4.2.7 |
| LM-032 | 🟡 低 | `PluginMarketPolicyTests.cs:420` 的用例名只断言终态、不直接断言顺序（见 §4.3.2） | 登记待清理 |
| **LM-038** | 🟡 低 | **包内更新器的"内嵌构建提交"指向错误提交，使 LM-024 的产物来源护栏永久失效** —— `resources/LiteMonitor++.Updater.exe` 是 S4.2 在 HEAD 仍为 `ebe1c38` 时构建的（改源码 → 构建 → 再提交），产物 `ProductVersion = 1.0.0+ebe1c38…` **并不包含** S4.2 对 `LiteMonitor.Updater/Program.cs` 与 `UpdateFileTransaction.cs` 的改动。后果：`scripts/generate-dependency-manifest.ps1` 的"包内更新器是否由本次源码构建"检查**永久停在 `NOT_VERIFIED`**（`git diff ebe1c38 -- LiteMonitor.Updater/` 非空；差异文件写入时间早于产物写盘时间 ⇒ 无法证明一致）—— 即这道护栏**形同失效**：今后改 `LiteMonitor.Updater/` 而不重建，它**不会变红** | ✅ 已修复（按流水线口径 `dotnet publish -c Release -r win-x64 --self-contained false` 在提交点 `366bf9c` 重建并刷新仓库副本；内嵌 SHA 变为 `366bf9c3d53ffc7f16c5c97d6360c4c4293e301c`，`git diff` 为空，判定由 `NOT_VERIFIED` 翻转为**"与本次源码构建一致"**） |
| **LM-039** | 🟠 中 | **`native://` 步骤绕过沙箱的三道闸门** —— `ExecuteNativeAsync`（`src/Plugins/PluginExecutor.cs:459-462`）直接调用 `CityCodeResolver` / `CryptoNative` **自带的 HttpClient**：**不占并发槽**、**不受 2 MiB 响应上限**、**不做凭据目标校验**、**不绑定 `scope.Token`**（不受 30 s 截止时刻中止）。且 `native://crypto?...&fallback=<任意 URL>` 会取回**用户提供的任意 URL**（`src/Plugins/Native/CryptoNative.cs:126-141`，8 s 超时）。实测：64 个 target 各跑一步 ⇒ 观察到 **64 个并发**请求直连回环，而 `PluginSandbox.AvailableRequestSlots` 仍为 8 ⇒ **突破"并发 ≤ 8（进程级）"**。**这直接违反 S3.2 的明文验收条款**"总并发数"须有效与"**API Key 只发往用户知晓并允许的目标**" ⇒ **不是范围外发现，必须修** | ✅ **已修复**（S3.5，`d23e6fc`）：原生取数接入同一套闸门（并发槽位 / 2 MiB / 30 s 截止 / 凭据目标 / 允许目标），8 条新用例、5 变异全捕获。**口径更新**：LM-009 的闭环范围由"HTTP 步骤路径"扩展到"HTTP 步骤路径 **+ native:// 路径**"，但**仍不覆盖"插件自带任意 HTTP 客户端"**（见 §4.5.7 LM-045） |
| **LM-040** | 🟡 低~中 | "目标展开数按实例累计"**未被实现强制** —— `PluginSandboxScope.EnsureTargetsAllowed`（`src/Plugins/PluginProcessor.cs:694-701`）累加 `_targetCount` 后比较的是**入参 `targetCount`** 而非累计值（类文档 `:534-536` 声称"累计 ≤ 64"）。单元测试 `PluginSandboxLimitsTests` 目标展开数用例（`:272-290`）**自证**累计到 70 > 64 **不抛异常**（`:282-283`）。当前 `PluginExecutor` 只调用一次 ⇒ 实际行为正确，属**未来调用方风险 + 文档不符** | ✅ **已修复**（S3.6，`d588440`）：改用 `Interlocked.Add` 的返回值判定累计值（无读—比较竞态），并同步反转了那条"自证累计 70 不抛异常"的单元测试期望 |
| **LM-041** | 🟡 低 | 端到端正则用例**不锁定 500 ms 数值** —— `tests/LiteMonitor.Tests/PluginExecutorSandboxTests.cs:508-531` 只锁"可达性 + 原因码"，数值 500 ms 仅由 `PluginSandboxLimitsTests.cs:151-153` 守住。变异（M6：`PluginProcessor.cs:468` → 10 s）时**端到端用例仍通过** | ✅ **已修复**（S3.6，`d588440`）：端到端用例断言内层 `RegexMatchTimeoutException.MatchTimeout == 500 ms`（字面量）+ 宽松耗时上界 < 5 s |
| **LM-042** | 🟡 低 | `端到端_目标数恰好等于上限` 的请求数断言**过弱** —— `PluginExecutorSandboxTests.cs:296` 的 URL 不含目标变量、`:309` 断言 `RequestCount > 0`。实测：64 目标 + 同一 URL ⇒ 服务端只收到 **61** 个请求（在途去重合并，`PluginExecutor.cs:432-435`）；URL 改目标专属后恰为 64 ⇒ 现断言无法证明"64 个目标都真的跑了各自的一步"。**S3.3 既有，非 S3.4 引入** | ✅ **已修复**（S3.6，`d588440`）：URL 改目标专属（`?n={{n}}`）+ 收紧为 `Assert.Equal(MaxTargetsPerStep, server.RequestCount)` |
| **LM-043** | 🔵 信息 | `Assert.NotEqual(TargetLimit, failure?.Reason)` 在 `failure == null` 时**恒真**（空转断言），`PluginExecutorSandboxTests.cs:304` | ✅ **已修复**（S3.6，`d588440`）：删除该行（同模式另一处一并删除）；承重的 `Assert.Null(failure)` / `Assert.True(ok)` 保留 |
| **LM-044** | 🔵 信息 | 生产入口的 **30 s 契约值未被测试钉住** —— 3 条总时长用例（`:322`/`:362`/`:543`）注入测试自建作用域并走私有方法；若把 `ExecuteInstanceAsync` 的默认时长改成 5 分钟，这 3 条仍全绿 | ✅ **已修复**（S3.6 `d588440` + 主审集成 `6310dbf`）：S3.6 用进程级登记表钉住共享默认；主审把它换成实例级 `CreateScope` 缝隙，**并补上 S3.6 自述未能实测的两个变体**（3 变异全捕获，详见 §4.5.7） |
| **LM-045** | 🔴 高 | **`HttpClient` 默认跟随重定向 ⇒ "允许目标"校验可被 302 绕过** —— S3.5 把 `native://` 的用户可控 URL 收进"允许目标"集合（校验发生在请求发出之前），但底层 `HttpClient` 默认 `AllowAutoRedirect = true` ⇒ **已声明主机 302 跳转到未声明主机不会被拦截**。**HTTP 步骤路径同样存在**（`PluginExecutor` 的 HttpClient 未关闭自动重定向）⇒ S2.2 的"API Key 只发往用户知晓并允许的目标"在**重定向场景下不成立**。LM-007 的逐跳重定向校验只覆盖**市场下载**，不覆盖插件取数 | ✅ **已修复**（`938dc4b`）：**HTTP 步骤路径**与 **`native://` 路径两侧**都关闭自动重定向（`PluginExecutor` 的默认 handler 与代理 handler、`CryptoNative` 的 handler），改为**逐跳**跟随并**逐跳复核目标主机**：带凭据的请求每一跳必须落在**声明主机集合**内（原因码 `LM009_CREDENTIAL_TARGET_NOT_ALLOWED`），不带凭据的每一跳必须落在（声明集合 ∪ {直连目标主机}）内（原因码 `LM009_REDIRECT_TARGET_NOT_ALLOWED`）；不通过时**该跳请求根本不发出** ⇒ 凭据不外泄、未声明主机的响应也不进入流水线。跳数上限 5、缺 `Location`、相对 `Location` 解析、非 http(s) 协议、**https→http 降级**均被拒。22 条新用例 / 7 变异全捕获。**口径再次更正**：本行原文"HTTP 步骤路径同样存在"只覆盖了一半 —— 主审集成巡检**实测**发现 `native://` 侧同样是缺口（见 **LM-053**）⇒ 现两侧都闭环，"插件网络出口不再能绕过允许目标"这句话**现在**才成立 |
| **LM-046** | 🔵 信息 | **生产代码内的可变全局测试接缝** —— `CryptoNative.SourceUrlOverrides`（`internal static Dictionary<string,string>`）供回归用例把官方币价源指向本地回环。生产路径**永不设置**（默认 null），测试用 `IDisposable` 助手保存/复位 ⇒ 无泄漏、无并行冲突（测试工程已关闭并行）。但它**位于生产程序集内且可写**，一旦将来有生产代码误赋值，官方取数源会被静默改向 | **接受并登记**：不属可利用缺陷，但须在 T08 文案与后续审计中显式知情。**可选加固**（未做，避免战线扩张）：改为只读属性 + `internal` setter 方法，或注入式 URL 提供者。**独立 verifier 已复核**：全仓赋值点仅在测试文件 ⇒ 生产路径确实不会设置它 |
| **LM-047** | 🟡 低 | **字节上限在原语层未被契约天花板夹住** —— `PluginSandbox.ReadResponseBodyAsync`（`PluginProcessor.cs:886`）取 `limit = maxBytes > 0 ? maxBytes : MaxResponseBytes`，**未与 `MaxResponseBytes`（2 MiB）比较** ⇒ 调用方传入更大的数字即可突破沙箱字节上限。**当前生产调用方传的都是 `EffectiveMaxResponseBytes`（= min(实例级 8 MiB, 沙箱 2 MiB)），故尚无实际绕过**（verifier 的 M2 变异即证明生产实际为 2 MiB）—— 但"2 MiB"此前只是**调用方约定**，不是原语层保证 | ✅ **已修复**（`46f3f9d`）：`limit = maxBytes > 0 ? Math.Min(maxBytes, MaxResponseBytes) : MaxResponseBytes` —— 契约值从"约定"变成"强制"，对现有生产行为**零变化**；新增护栏用例 `原语层字节上限_调用方传入更大上限也不能突破契约天花板`，并以变异自证（去掉夹取 ⇒ 该用例变红 ⇒ 还原 SHA-256 一致 ⇒ 复跑 26/26 全绿） |
| **LM-048** | 🟡 低 | **允许目标口径分裂** —— `PluginExecutor.cs:810-821`：主机判据用 `allowedHosts`（声明集 ∪ native URL 内嵌字面量主机），而**带凭据时改用 `declaredHosts`（严格声明集）** ⇒ 同一主机"不带凭据可用、带凭据被拒"。实测内置插件的凭据步骤数为 **0**，**当前无功能影响** | 登记待清理：统一为一套口径，或显式文档化该差异。**本批不改**（改动会影响凭据可达性，属契约变更） |
| **LM-049** | 🟡 低 | **`native://` URL 产出伪主机并污染声明集合** —— `PluginProcessor.cs:413-436` 的 `AddDeclaredHost` 对 `native://crypto?symbol=BTC&fallback=http:` 这类 URL 会切出伪主机塞进集合。当前匹配不上任何真实主机 ⇒ **无害**，但污染集合 | 登记待清理：对 `native:` scheme 单独解析，不产出伪主机。**本批不改**（该函数是安全判据核心，改动需连带回归） |
| **LM-050** | 🔵 信息 | **`native://citycode` 的远程回退闸门无回归钉住** —— 本地 `resources/CityCode.json` 存在时走离线路径，远程 DB URL 是 `const` 不可替换 ⇒ 现有 8 条 native 用例只覆盖 crypto 与本地库，**远程回退路径的并发槽 / 字节上限 / 令牌中止均无用例** | 登记待清理：需先给 `JSON_DB_URL` 加测试接缝（与 `SourceUrlOverrides` 同类），再补一条强制远程回退的用例。**本批不做**（引入新接缝需独立评估） |
| **LM-051** | 🟡 低 | **`CryptoNative.NormalizeSymbol` 未对 symbol 做 `EscapeDataString`** ⇒ 可向官方源 URL 注入查询串。**主机仍固定**（`api.binance.com` 等），**不构成沙箱逃逸**；属**既有缺陷，非本批引入** | 登记待清理（既有项）。修复需评估各官方源对 symbol 字符集的容忍度 |
| **LM-052** | 🔵 信息 | **`CityCodeResolver._isLoading` 是无锁 `static bool`** ⇒ 并发加载时可能重复加载或漏加载。**非本批引入**，无安全影响 | 登记待清理（既有项）：改 `Interlocked` 标记或 `SemaphoreSlim` |
| **LM-053** | 🟠 中 | **`native://` 路径同样可被 302 绕过"允许目标"** —— `CryptoNative.CreateClient()`（`src/Plugins/Native/CryptoNative.cs:40-57`）**未设** `AllowAutoRedirect = false`；`GetStringAsync()`（`:319-339`）用 `GetClient().GetAsync(...)` ⇒ 一次 302 就被静默跟到下一跳。而 `EnsureFallbackTargetAllowed`（`:144`）**只校验首个 URL 的主机**，且 `allowedHosts` **根本没有贯穿**到 `FetchFreshAsync`（`:186`）/ `GetStringAsync` ⇒ fallback 目标 302 到未声明主机时，未声明主机的响应会被当作行情 JSON 返回并**进入插件流水线**。**这是 LM-045 的 native 侧另一半** ⇒ 只修 HTTP 步骤路径**不足以关闭 LM-045** | ✅ **已修复**（`938dc4b`，随 LM-045 一并落地）：native 侧同样关闭自动重定向 + 逐跳复核；`allowedHosts` 贯穿 `FetchAsync` → `FetchFreshAsync` → `GetStringAsync`；官方源（代码内建目标）允许集合 = 内建官方源主机 ∪ 该次请求初始主机（`SourceUrlOverrides` 测试接缝指向回环时仍可用），fallback（插件/用户可控）允许集合 = native 允许目标集合 ∪ 回退目标主机；沙箱异常**不被降级**成"这个源不可用"/"回退源冷却" |
| **LM-054** | 🔵 信息 | **市场索引拉取仍允许自动跳转** —— `src/Plugins/PluginMarket.cs:167-171` 的 `_http` 明确注释"索引拉取用（沿用原行为：允许自动跳转）" | 登记待评估（本批不修）：索引 URL 是**用户配置的固定目标**；其内容驱动的下载仍受 LM-007 的逐跳白名单 + 路径前缀 + sha256 三重校验，风险有界 |
| **LM-055** | 🔵 信息 | **`CityCodeResolver` 的远程回退 handler 未关自动重定向** —— `src/Plugins/Native/CityCodeResolver.cs:196-206` | 登记待评估（本批不修）：其目标 `JSON_DB_URL` 是**代码内建常量**，插件与用户都改不动，与 LM-053 的"用户可控目标"不同级 |
| **LM-056** | 🟡 低~中 | **发布流水线的防假绿阈值与 CI 不一致** —— `.github/workflows/release.yml:45-46` 的注释写着"与 ci.yml 完全一致的防假绿阈值，改动必须两处同步"，但**实际值是 `MIN_TEST_TOTAL: 1000`**，而 `ci.yml` 同期是 **1281** ⇒ **删掉 281 条用例时 CI 会红、发布流水线却会绿**；且 `release.yml` 的 `$assertArgs` **既没传 `-MaxSkipped` 也没传 `-MustExecuteTestClass`**（只依赖脚本默认值 1），比 CI 少两道门 | ✅ **已修复**：阈值两处同步，并补上 `MAX_SKIPPED_TESTS: 1` + `MUST_EXECUTE_TEST_CLASS: MarketIndexContractTests`（≥6 条）及其两个参数。门禁**只收紧不放宽**。★ **二次更正**：阈值随后按「**被推送分支实测总数**」再校正为 **1349**（`b1a8b42` 只同步到 1316，而该分支实测仅 **1295** ⇒ 门禁**正确**报红，见 §8「阈值纪律」；最终 `61465ba` 实测 1349 ⇒ CI `39836128969` = **success**） |
| **LM-057** | 🟡 低~中 | **逐跳主机比对忽略端口** —— `PluginExecutor.HostInAllowedSet` / `StripPort`（`src/Plugins/PluginExecutor.cs:831-850`）与 `PluginProcessor.HostMatches`（`:1044-1056`）在比对主机时**剥掉端口** ⇒ 同主机**不同端口**的重定向被放行：声明主机 `api.example.com` 的插件，凭据可被 302 到 `api.example.com:8443`（同一台机器上的另一个服务）。**既有行为非本批引入**（`.NET` 自身重定向语义即只比主机不比端口；`PluginProcessor.HostMatches` 早就忽略端口），但 LM-045 把该口径**继承**到了逐跳复核上 ⇒ 属**真实口径缺口** | 登记待评估，**本批不修**：修它属于**收紧**，会改变既有取数可达性 ⇒ 须单独评估兼容性并同步更新契约。**独立复核方实测复现**（同主机另一端口的服务器确实收到 1 个请求） |
| **LM-058** | 🔵 低 | **IPv6 字面量主机在两处被不同地截断** —— `PluginProcessor.AddDeclaredHost`（`:431`）取**第一个**冒号 ⇒ `http://[::1]:8080/x` 切出 `"["`；`PluginExecutor.StripPort`（`:845`）取**最后一个**冒号 ⇒ `"[::1]"` → `"[:"` ⇒ 声明集合与比对值**恒不匹配** ⇒ IPv6 目标恒不可用 | 登记待清理：**偏严方向 / fail-closed**（不漏放行），但 IPv6 回环与双栈插件会莫名失败。修它需**两处口径对齐**，属独立小改动 |
| **LM-059** | 🔵 低 | **既有 public 记录 `RenderingRouteRequest` 的主位置签名被追加成员** —— `src/Rendering/Configuration/RenderingRoutePolicy.cs:18-24` 追加 `bool WindowIsLayered = false` ⇒ 记录元数 5→6、记录相等性与 `ToString()` 语义改变（**二进制不兼容**）。与 §4.5.9 裁决 D-F 第 3 条"尾部可选参数 ⇒ 源码兼容"的意图一致，但**二进制**层面不是 | 登记知悉：全仓仅 **1 处**生产调用点（`src/UI/UIController.cs:941`，用具名参数），`git grep` 确认**无**任何解构 / 记录相等比较消费点 ⇒ 实际风险极低。**不 rebase、不改签名**（改动会波及合同 §3.1 的物化结果） |

#### 4.5.5 LM-016 的不变式与残余窗口（S4.2）

> **高权限更新器所使用的每一个字节，都必须是主程序已经验证过的那批字节。**

由两条机制共同保证：**①更新器独立验证**（期望摘要随参数交接，更新器在**任何解压/标记/临时目录之前**用**它自己打开的**文件流重算 SHA-256 并比对；缺失/畸形/不匹配一律拒绝）+ **②字节钉住**（主程序与更新器均以 `FileShare.Read` 打开更新包，摘要与解压**同一句柄**完成）。

- **改动前**：主程序按路径算摘要 → 只把路径交给更新器 → 更新器按路径重开并解压，**无任何字节约束**，窗口横跨"主程序退出等待 + 1 s 缓冲 + 解压"全过程。
- **改动后**：主程序自校验起即持有 deny-write/delete 句柄；更新器自开句柄、独立重算、同一句柄解压。
- **残余窗口**：主程序释放句柄 → 更新器打开文件之间的毫秒级缝隙。该缝隙内若被替换，结果是**更新被拒绝**（退出码 6、不解压），**不再是"未验证字节被安装"**。
- **方案 B（受保护暂存区）未实现**：同用户低权限进程与主程序**共享同一 SID**（提权不换身份），`%LOCALAPPDATA%`/`%TEMP%`/安装目录对本用户非提权进程同样可写 —— 换目录**不能**隔离该威胁模型；内容绑定严格强于路径绑定，且不引入 ACL 加固与残留清理等新失败模式。
- **NOT_VERIFIED**：安装目录 `resources/` 的 ACL 未实测 ⇒ **不主张**此前存在可利用提权漏洞，也**不主张**已"彻底消除提权风险"。本次消除的是"校验与使用之间无字节约束"这一缺口本身。

#### 4.5.6 提交信息勘误：`ac652f8` 的根因表述 —— **登记更正，不 rebase**

`ac652f8 fix(plugins): 修复不可见分支导致调度永久停摆并引入实例代次 (LM-001)` 的**提交信息**把根因写成"不可见分支导致调度**永久停摆**"。该前提**已被实测证伪**（见 §4.1.1），更正后的根因是"依赖未文档化副作用 + 缺实例代次守卫"，**代码修复本身不需要返工**。

| 项 | 内容 |
|---|---|
| **勘误内容** | 以 **§4.1（根因更正记录）** 与提交 `56d573e docs(plan): 更正 LM-001 根因为"依赖未文档化副作用"并归档 M1 复核 F1-F8` 为**权威口径**；`ac652f8` 的提交信息中的"永久停摆"字样**作废** |
| **为什么不 rebase 改提交信息** | `ac652f8` 是本批 26 个提交中的第 5 个，rebase 重写其信息会连带重写**其后 21 个提交的哈希**，使 §4.2/§4.3/§4.4 及本节台账中**全部哈希引用失效**，并需对已推送到 origin 且已跑过 5 次绿 CI 的 `fix/audit-remediation-v3` 做 **force-push**。代价远大于收益，且 Git 历史本就不是唯一事实来源 —— 本节即为其勘误入口 |
| **可检索性** | 任何审计者 `git show ac652f8` 后，应能通过本节与 §4.1 找到更正后的根因。§4.4 台账 S1.1 行亦已标注"**根因已更正**（见 §4.1）" |
| **结论** | **不 rebase**，以台账勘误方式闭环。该项**不阻断** M2/M3 门禁与发布准入 |

#### 4.5.7 主审裁决 D-E：LM-044 的测试缝隙改为**实例级**（取代 S3.6 的进程级登记表）

S3.6 为钉住"生产入口实际使用的总时长"，在 `PluginSandboxScope` 里放了一个**进程级静态登记表**（`_createdInstanceScopes` + `FindCreatedInstanceScope`），供测试按外部取消令牌反查生产入口创建的作用域。该方案经**主审逐行复核后否决**，理由三条：

| # | 问题 | 证据 |
|---|---|---|
| 1 | 生产代码里保留**只服务于测试的全局可变状态**，最多滞留 32 个**已释放**作用域（含各自的 `CancellationTokenSource`），永不主动回收 | `RecordInstanceScopeCreation` 只做入队 + FIFO 裁剪，**无 Dispose 钩子** |
| 2 | 它**钉不住生产入口本身**：只能观测「共享默认」与「默认构造」两处；"在 `PluginExecutor.cs` 里硬编码 5 分钟"这一变体**未被覆盖** | S3.6 交付报告第 5 节第 1 条自述："该文件属 LM-039 agent 所有权，我不得写入" ⇒ **未实测** |
| 3 | 按取消令牌反查在令牌为 `CancellationToken.None` 时会命中**多个历史作用域**（返回最后一个匹配项） | `FindCreatedInstanceScope` 的实现对每个匹配项覆盖 `found`，最终返回**最后**一个 |

**裁决 D-E**：改为**实例级**缝隙 —— `PluginExecutor` 新增 `protected virtual PluginSandboxScope CreateScope(token)`（生产入口**唯一**的沙箱作用域创建点），`ExecuteInstanceAsync` 与 `ExecuteStepAsync` 两处创建改走它；回归测试用 `RecordingPluginExecutor` 覆写 `CreateScope` 并**调用基类实现** ⇒ 观测到的就是生产路径真正使用的那份作用域；**移除** `PluginSandboxScope` 的进程级登记表（字段 / 常量 / 查询方法 / 登记调用）。

**裁决理由**：实例级缝隙与生产语义**同源**（覆写时调用基类 ⇒ 基类就是生产实现），因而能覆盖"生产入口硬编码时长"与"生产入口绕开 `CreateScope`"两类变体；而进程级登记表**与生产语义异源**（观测的是共享默认值，不是入口实际用的那一个），天然覆盖不到入口级漂移。代价是生产类多一个 `protected virtual` 成员 —— 与被否决方案相比，**没有全局状态、没有滞留、没有歧义匹配**，净收益为正。

**裁决 D-E 的判别力自证**（`6310dbf`，每轮改坏 → 变红 → 字节级还原 → 复跑全绿）：

| 变异 | 变红用例 | 判定 |
|---|---|---|
| V1 `CreateScope` 硬编码 5 分钟 | `端到端_生产入口实际使用的总时长_等于契约值30秒`（Expected 30000 / Actual 300000） | ✅ **这正是 S3.6 未能实测的变体** |
| V2 生产入口绕开 `CreateScope` 直接 `new` | 同一用例（`CapturedScope` 为 null ⇒ `Assert.NotNull` 失败） | ✅ |
| V3 共享默认时长漂移（30 s → 5 min） | 3 条（含新护栏） | ✅ |

还原后 3 个文件 SHA-256 与基线**逐一致**，聚焦用例 **45/45 全绿**；契约 6 数值（2 MiB / 500 ms / 16 步 / 64 目标 / 30 s / 8 并发）**一个未改**。

#### 4.5.8 S3.5 / S3.6 的未验证项汇总（**不计入通过**）

| 来源 | 事项 | 处置 |
|---|---|---|
| S3.5 | **重定向未复核**：`HttpClient` 默认跟随重定向，已声明主机 302 到未声明主机不被拦截（HTTP 步骤路径同样如此） | **登记为 LM-045（P1 未关闭）**；本批**不擅自**改 `AllowAutoRedirect`（会改变既有取数行为） |
| S3.5 | 内建官方源（bybit/gateio/binance/okx）与 citycode DB URL 是**代码常量**，不受"允许目标"集合约束（但受槽位 / 字节 / 令牌约束） | 如实记录：用户与插件**改不动**这些主机 |
| S3.5 | native 字节超限抛 `PluginSandboxException`，HTTP 路径抛其子类 `PluginResponseTooLargeException` —— **原因码一致**，异常类型不同 | 记录；统一异常继承属契约变更，本批不做 |
| S3.5 | `CryptoNative` 在途去重改为**按作用域键**（不同实例不再共享同一在途请求） | 记录为一处**行为收窄**，已由既有功能回归用例覆盖 |
| S3.5 | 测试接缝 `CryptoNative.SourceUrlOverrides`（`internal static`）位于生产程序集内且可写 | **登记为 LM-046（接受并登记）** |
| S3.5 | 未复现 verifier 的"64 target ⇒ 64 并发"原场景 | 如实记录：改用 9 并发闸门实测 + 服务端最大并发观测 |
| S3.6 | "在 `PluginExecutor.cs` 里硬编码 5 分钟"变体当时**未实测**（文件归属他方） | **已由裁决 D-E 补测**（V1/V2 均变红） |
| S3.6 | LM-041 的 5 s 耗时上界只是粗护栏（精确钉死靠 `MatchTimeout` 字面量断言） | 记录：机器极端高负载下可能变松，但**不会误绿** |

#### 4.5.9 主审裁决 D-F / D-G：GPU C 期 owner A 物化，与"两份句柄代次 holder"的结构性纠正

**背景**：GPU 切换合同（T02）§12.4 规定 C 期物化顺序 —— 序 6（T04 宿主清理）必须等序 1/2 落地，序 7（T03 UI 接线）必须等序 2/6 落地。序 1/2/3 属 **owner A**（主审/集成），**不能外包**，由主审直接物化。

**裁决 D-F（物化补注）** —— 三处：

1. `WindowCapturePolicyCleanup`（§3.2 草案）把 `Attempts` / `State` / `TerminalReason` 等写成 `private set` 却**没有任何迁移方法** ⇒ 纯 record 之外的状态机无法被 ledger 推进。**裁决**：保留合同原文的字段名与类型，**补齐**迁移方法 `TryRecordFailedAttempt` / `TryMarkCompleted` / `TryMarkFailed` / `TryMarkSupersededByNewGeneration`，并加一个 `WarningCode` 字段承载合同 §7.4 要求的 `gpu_capture_policy_cleanup_superseded`。**四个方法在已终结时一律返回 `false` 且不改动任何字段**（终态不可回退，INV-5 幂等）。
2. `WindowHandleGeneration`（§7.2 草案）用 `++Current` 实现 `Advance()`。**裁决**：改用 `Interlocked.Increment` / `Interlocked.Read`，**公开签名与语义逐字不变**（唯一递增点、唯一驱动者）—— 读点分散在 live 捕获链与 GPU 清理链两条线程上。
3. `RenderingRouteRequest.WindowIsLayered`（§3.1）按合同落为**尾部可选参数、默认 `false`** ⇒ 既有 4 处具名参数构造无需改动即可编译，`Decide` 输出与改造前**逐位相同**。

**裁决 D-G（结构性纠正）** —— **两份 holder 会让 §7.4 的判据恰好在它要防的场景里失效**：

集成巡检实测发现，owner C 在 `src/Rendering/Presentation/SharedWindowHandleGeneration.cs` 与 owner B 在 `src/UI/SharedWindowHandleGeneration.cs` **各自**建了一份 holder，各自 `new WindowHandleGeneration()` ⇒ **进程内出现两个不同的单例** ⇒ GPU 清理链与 live 捕获链读到**不同的代次** ⇒ 合同 §7.4 的"代次先于 `IsWindow`"判据失效（两边各以为对方是旧代次）。

**裁决**：唯一实例由**类型自身**持有 —— `WindowHandleGeneration.Instance`，落在 owner A 的 `src/Rendering/Configuration/WindowHandleGeneration.cs`。
- 该目录位于 UI 层与 Rendering 层**之下**，两侧都只读消费 ⇒ **不产生层次倒置**；
- 类型与实例**同文件** ⇒ **结构上不可能**再出现第二份 holder；
- owner B 与 owner C **禁止**另行 `new WindowHandleGeneration()`（测试如需隔离实例仍可自行 `new`，生产路径不得）。

**判定标准（集成时逐条查）**：全仓（排除测试文件）`new WindowHandleGeneration()` 出现次数 = **0**；`SharedWindowHandleGeneration` 类型名在全仓**不存在**。合同 §13 自检"是否新建了第二个句柄代次计数器？否"仍然成立。

**本轮新发现的缺陷**（§4 / §4.5.4 / §7 已补行；**新增缺陷从 LM-057 起顺延**）：

| ID | 级别 | 摘要 | 归属 |
|---|---|---|---|
| **LM-053** | 🟠 中 | **`native://` 路径同样可被 302 绕过"允许目标"** —— `CryptoNative.CreateClient()`（`src/Plugins/Native/CryptoNative.cs:40-57`）**未设** `AllowAutoRedirect = false`；`GetStringAsync()`（`:319-339`）用 `GetClient().GetAsync(...)` ⇒ 一次 302 就被静默跟到下一跳。而 `EnsureFallbackTargetAllowed`（`:144`）**只校验首个 URL 的主机**，且 `allowedHosts` **根本没有贯穿**到 `FetchFreshAsync`（`:186`）/ `GetStringAsync` ⇒ fallback 目标 302 到未声明主机时，未声明主机的响应会被当作行情 JSON 返回并**进入插件流水线**。**这是 LM-045 的 native 侧另一半** ⇒ 只修 HTTP 步骤路径**不足以关闭 LM-045** | ✅ **已修**（随 LM-045 一并落地，`938dc4b`）：native 侧同样关闭自动重定向 + 逐跳复核；`allowedHosts` 贯穿 `FetchAsync` → `FetchFreshAsync` → `GetStringAsync`；官方源（代码内建目标）允许集合 = 内建官方源主机 ∪ 该次请求初始主机（`SourceUrlOverrides` 测试接缝指向回环时仍可用），fallback（插件/用户可控）允许集合 = native 允许目标集合 ∪ 回退目标主机 |
| **LM-054** | 🔵 信息 | **市场索引拉取仍允许自动跳转** —— `src/Plugins/PluginMarket.cs:167-171` 的 `_http` 明确注释"索引拉取用（沿用原行为：允许自动跳转）" | **登记为信息项，本批不修**：索引 URL 是**用户配置的固定目标**；其内容驱动的下载仍受 LM-007 的逐跳白名单 + 路径前缀 + sha256 三重校验，风险有界 |
| **LM-055** | 🔵 信息 | **`CityCodeResolver` 的远程回退 handler 未关自动重定向** —— `src/Plugins/Native/CityCodeResolver.cs:196-206` | **登记为信息项，本批不修**：其目标 `JSON_DB_URL` 是**代码内建常量**，插件与用户都改不动，与 LM-053 的"用户可控目标"不同级 |
| **LM-056** | 🟡 低~中 | **发布流水线的防假绿阈值与 CI 不一致** —— `.github/workflows/release.yml:45-46` 注释声称"与 ci.yml 完全一致"，实际 `MIN_TEST_TOTAL: 1000` 而 `ci.yml` 是 1281 ⇒ **删掉 281 条用例时 CI 会红、发布流水线却会绿**；且 `release.yml` 未传 `-MaxSkipped` / `-MustExecuteTestClass` | ✅ **已修**：阈值同步到 1316（与 `ci.yml` 逐字一致）+ 补齐跳过数上限与"必须真正执行的测试类"两道门。门禁**只收紧不放宽** |

#### 4.5.10 独立复核结论归档（LM-045 闭环 + owner A 物化 + 裁决 D-G）

**复核方式（严格独立）**：复核方在**自己的** worktree（`.artifacts/wt-verify2`，钉在 `33eab52`）与对照 worktree（`.artifacts/wt-verify2-base`，detached `23a6c5c`）里**自写** 47 条用例（LM-045 34 + owner A 12 + 决策快照 1），**不跑实现者的任何用例**，**自设计** 12 条变异（其中 9 条完全不在实现者已做的类别内，含任务点名的"主机比对忽略端口"与"允许集合误改为直连主机优先"），每条变异走「改坏 → `dotnet build --no-incremental` → 红 → **按字节还原 + SHA-256 校验** → `--no-incremental` 强制重编译 → 复跑绿」闭环。

**结论：`PASS_WITH_NOTES`**（闭环成立，遗留 3 项低危口径问题 + 2 项 `NOT_VERIFIED`）。

**A 块 · LM-045 闭环（实证成立）**：未声明主机在带凭据重定向下**收到 0 个请求**、**从未见到秘密**、其响应正文**未进入插件流水线**；无凭据时跳到未声明主机同样 0 请求（原因码 `LM009_REDIRECT_TARGET_NOT_ALLOWED`）；带凭据时原因码为 `LM009_CREDENTIAL_TARGET_NOT_ALLOWED`。**对照项全部放行**：同主机换路径、带凭据跳到**另一个已声明主机**、native fallback 同主机 302/307。**绕过面自找实测**：相对 `Location` 放行（同主机，正确）、**协议相对 `//host/…` 被拒**、**缺 `Location` 被拒**、跳数超限实测恰好 6 个请求后以 `LM009_REDIRECT_LIMIT` 失败、`file://` 目标被拒、**经代理路径同样拦住**（未声明主机 0 请求）、声明集合为空时凭据请求被拒、**等价 IP 写法（十进制/八进制/十六进制）无法绕过**。

**既有行为未被破坏（实测）**：301/302/303 ⇒ 方法变 GET 且丢正文；307/308 ⇒ 保留方法与正文；2 MiB 字节上限 ⇒ `LM009_RESPONSE_TOO_LARGE`；30 s 总时长**覆盖整条重定向链** ⇒ `LM009_DEADLINE_EXCEEDED`；并发槽位服务端观测峰值 **≤ 8**。

**契约 6 常量逐行核对**（`src/Plugins/PluginProcessor.cs:447-462`）：`MaxResponseBytes=2 MiB` / `MaxRegexMatchTimeoutMs=500` / `MaxStepsPerExecution=16` / `MaxTargetsPerStep=64` / `MaxExecutionDurationMs=30000` / `MaxConcurrentRequests=8` —— **一个都没改**；`git diff` 在该文件的**唯一**改动是 `PluginSandboxReasons` 新增的 2 个常量。

**公开面核对**：`git diff` 的 `^-` 侧**无任何** public/protected 行被删除或改签名；新增 public 成员**只有 2 个**（两个原因码常量）。

**B/C 块 · owner A 与裁决 D-G**：改造前后决策矩阵 **192 行 / 23116 字节逐位相同**（两份 dump 的 SHA-256 同为 `5AB7CED0…862F2BD`）；layered 分支给出 `wda_capture_policy_unsupported_on_layered_window` 且**不抢**三个既有原因码的优先级；清理状态机两种终态下 4 个迁移方法**全部返回 false 且逐字段无变化**；句柄代次单调、`Instance` 为同一对象。**D-G 的结构性保证在复核时点 `33eab52` 与当时 HEAD `7c48d57` 均成立**：全仓 `new WindowHandleGeneration()` **仅 1 处**（`Instance` 初始化器）、`SharedWindowHandleGeneration` 全仓不存在。

**D 块 · `Exchange.json`**：旧端点 `api.frankfurter.app/latest` 实测 **301 跨主机** ⇒ 在新的逐跳主机校验下**必然被拒** ⇒ 迁移**必要**；新端点 `api.frankfurter.dev/v1/latest` 实测 **200** 且 JSON 含 `rates` 与 `date`（插件 `extract` 依赖成立）；**不带 `/v1` 实测 404** ⇒ `/v1` 必需。其余 12 个内置端点逐条联网复测：**无一**会被新策略误伤（`timor.tech` 是同主机 301 + http→https **升级** ⇒ 按策略放行）。

**本轮新发现的缺陷（§4 / §7 已补行；新增缺陷从 LM-060 起顺延）**：

| ID | 级别 | 摘要 | 归属 |
|---|---|---|---|
| **LM-057** | 🟡 低~中 | **逐跳主机比对忽略端口** —— `PluginExecutor.HostInAllowedSet` / `PluginProcessor.HostMatches` 比对时剥掉端口 ⇒ 同主机**不同端口**的重定向被放行：声明主机 `api.example.com` 的插件，凭据可被 302 到 `api.example.com:8443`（同一台机器上的另一个服务） | **登记待评估，本批不修**：与 .NET 自身重定向语义一致、且 `PluginProcessor.HostMatches` **忽略端口是既有行为非本批引入**；但 LM-045 把该口径**继承**到了逐跳复核上 ⇒ 属真实口径缺口。修它属于**收紧**，须单独评估兼容性 |
| **LM-058** | 🔵 低 | **IPv6 字面量主机在两处被不同地截断** —— `PluginProcessor.AddDeclaredHost`（`:431`）取**第一个**冒号 ⇒ `http://[::1]:8080/x` 得 `"["`；`PluginExecutor.StripPort`（`:845`）取**最后一个**冒号 ⇒ `"[::1]"` → `"[:"` ⇒ 声明集合与比对值恒不匹配 | **登记待清理**：**偏严方向 / fail-closed**（不漏放行），但 IPv6 回环与双栈插件会莫名失败 |
| **LM-059** | 🔵 低 | **既有 public 记录 `RenderingRouteRequest` 的主位置签名被追加成员** —— `RenderingRoutePolicy.cs:18-24` 追加 `bool WindowIsLayered = false` ⇒ 记录元数 5→6、相等性与 `ToString()` 语义改变（**二进制不兼容**） | **登记知悉**：构造侧**源码兼容**（尾部可选参数）；全仓仅 1 处生产调用点且用具名参数，无解构/相等比较消费点 |

**复核方的一条"预期外"发现（已被本轮消除）**：复核方在其复核时点报告「仓内仍存在**第二个**句柄代次计数器」（`src/UI/LiveGlassController.cs:87` 的 `private long _handleGeneration = 1;`）。该计数器**在复核前即存在**（`git grep 23a6c5c` 已证实），属 backdrop/live-capture 链。**GPU T03 已将其删除** —— `LiveGlassController` 改为**只读消费** `WindowHandleGeneration.Instance.Current`，代次递增点唯一化到 `MainForm_Transparent` 的 `HandleCreated` 处理器的**第一行** ⇒ 该项在 T03 集成后**自然消失**，无需单独立项。

**`NOT_VERIFIED`（2 项，诚实标注）**：
1. **https→http 降级的端到端拒绝** —— 生产客户端**刻意不装证书校验回调**，自签 TLS 服务器必然被 TLS 校验拒绝 ⇒ 无法构造受信任的 HTTPS 源。已用**白盒**核对 `EnsureRedirectSchemeAllowed`（降级抛 `REDIRECT_TARGET_NOT_ALLOWED`、http→https 升级放行、`file://` 抛）。**该分支的端到端行为未实测。**
2. **真实公网插件端到端** —— 只做了**端点级**联网复测（状态码/Location/JSON 形状）与**本地双服务器**的策略级实测；未把真实公网端点灌进 `PluginExecutor` 跑完整链。D 块结论是由"端点实测"与"本地跨主机拒绝实测"**合成**推出的。

**复核方自述的口径偏差（如实留痕）**：任务给定基线「1316 总数 / 1315 通过」与其实测「**1317 / 1316**」差 1 —— 查证 `1316` 是 **CI 阈值**（`e91c0cc`），不是该提交的**实际发现数**；不影响验收标准（失败 0、跳过 1）。

#### 4.5.11 主审裁决 D-H：legacy 回退样式的 `Accent` 落点

**背景**：GPU T05 的 `LegacyFallbackStyle` 输出 6 项（`Background` / `PanelBackground` / `Border` / `Text` / `MutedText` / `Accent`），而 legacy 渲染的 `ColorConfig`（`src/Core/ThemeManager.cs:254-281`）**没有 accent 色槽**：

| resolver 输出 | `ColorConfig` 落点 | 状态 |
|---|---|---|
| `Background` | `Background`（`:254`） | 直接对应 |
| `PanelBackground` | `GroupBackground`（`:269`） | 直接对应（分组底色） |
| `Border` | `Border`（`:273`，可空） | 直接对应 |
| `Text` | `TextPrimary`（`:259`） | 直接对应（正文） |
| `MutedText` | `TextSecondary`（`:281`，可空） | 直接对应（次要文字） |
| `Accent` | **无对应槽** | ★ 本裁决 |

**候选与排除（逐条给理由，不留"看着行"式裁决）**：

- `ValueSafe` / `ValueWarn` / `ValueCrit`（`:260-262`）—— **排除**：它们是**告警语义色**（绿 / 黄 / 红）。合同 §4.6 明文要求"保留告警语义和进度值" ⇒ 占用即改变告警语义。
- `BarLow` / `BarMid` / `BarHigh`（`:264-267`）—— **排除**：进度条分段色，与"强调"无关，且占用会改变进度条观感。
- `TextGroup`（`:258`）—— **排除**：分组标题色，其层级语义接近 `MutedText`（次级文字）；若与 `MutedText` 同源覆盖，两个层级会塌陷成同一色。
- `TextTitle`（`:256`）—— **采用**：标题 / 强调前景色，是 legacy 渲染里承担"突出"角色的槽位；正文可读性对比度由 `TextPrimary` 承担 ⇒ 占用它**不影响**合同 §9.5 针对正文与次要文字的对比度口径。

**裁决 D-H**：`Accent` → **`ColorConfig.TextTitle`**。

**落实要求（交给集成方 / T03 收尾）**：owner B 的派生主题副本（`LegacyFallbackStyleBridge.BuildDerivedTheme`）必须把 `Accent` 落到 `TextTitle`，且**不得**触碰 `ValueSafe/ValueWarn/ValueCrit` 与 `Bar*`；该落地必须由**至少一条会变红的用例**钉住（把落点改回"不消费 accent"必须红）。

**本裁决的独立佐证**：GPU 部分的独立复核任务已同时收到本问题（要求给出带理由的独立判断）；若其判断与本裁决不一致，按"实现者/编排方意见一律以实测为准"处理，并在此处留痕更正。

---

## 5. 执行步骤

### M0 · 基线与可复现

> 目标：让"全新检出"成为可验证的起点。这一步不通过，后续所有验收都不成立。

#### S0.0 · 释放构建输出占用 `[mavis]`

- **动作**：确认并停止占用 `bin/Release/net8.0-windows/LiteMonitor++.exe` 的进程（ENV-001）；若用户不愿停止，改为全部 Release 操作走独立发布目录 `.artifacts/publish`。
- **交付物**：一份记录（进程处置方式 + 生效范围）。
- **验收**：`dotnet build LiteMonitor.sln -c Release` 退出码 0。
- **门禁**：M0 后续步骤依赖本项。

#### S0.1 · 分支基线清单 `[explore]`（只读）

- **目标**：为 BASE-002 产出可裁决的分支差异事实。
- **范围**：只读 Git。**不得**切分支、不得合并、不得改工作区。
- **动作**：
  1. 固定两端 SHA：`master` 与 `codex/full-fork-remediation`。
  2. 分别产出：共同祖先（`git merge-base`）、分支独有提交清单（`git log --oneline master..branch`）、端点差异（两点 `git diff --name-only master branch`）、共同祖先到分支的差异（三点 `master...branch`）。
  3. 标出 remediation 分支中**与 V3.0 目标文件重叠**的提交（V3.0 目标文件清单见 §6 所有权表），判断是否已有成果可直接采用。
  4. 产出 `codex/performance-sampling` 与 `codex/integrate-performance-sampling` 两个 worktree 的在途状态与是否与本轮冲突。
- **交付物**：分支差异报告（结论 + 证据表），**不含**实施决策。
- **验收**：报告中每个数字附产生它的原命令。
- **禁止**：不得替父 Agent 决定"选哪个分支"。

#### S0.2 · 可复现范围恢复 `[mule]`

- **目标**：消除 BASE-003。
- **文件所有权**：`.gitignore`、`LiteMonitor.sln`、`tests/**`、`scripts/**`、`docs/**`（**仅本步骤独占**，后续步骤不再改这四类目录的结构）。
- **动作**：
  1. 改 `.gitignore`：移除 `/tests/`、`/scripts/`、`/docs/` 三条；**保留** `secrets/`、`*.pkcs8`、`*.pfx`、`*.log`、`settings.json`、`bin/`、`obj/`、`/dist/` 等忽略项。
  2. 把 `tests/LiteMonitor.Tests`、`tests/LiteMonitor.Updater.Tests`、`tests/LiteMonitor.GpuNonperformance.Tests` 加入 `LiteMonitor.sln`。
  3. `git add` 上述目录与 `.gitignore`、`LiteMonitor.sln`。
  4. **禁止**从旧仓库 `fork-source/master` 复制测试文件——本地套件是旧仓库的超集（旧 13 个 `.cs`，本地 64 个），复制会造成覆盖率倒退。
  5. 批量操作前先在单文件验证 `git check-ignore` 行为，再扩展。
- **交付物**：一次提交，提交信息含 `BASE-003`。
- **验收**：
  - `git ls-files tests scripts docs | Measure-Object` 数量 > 0；
  - `git ls-files scripts` 至少含 `sign-release.ps1`、`pack-release.ps1`；
  - `dotnet test LiteMonitor.sln -c Debug` 输出中出现**两个**测试程序集的计数（990 与 13），且退出码 0。
- **报告格式**：按 mule 的四段式（干了哪些 / 改了什么 / 跳过的 / 需主人看的）。

#### S0.3 · 最小 CI `[worker]`

- **目标**：消除 BASE-004，落实禁令 G3。
- **文件所有权**：`.github/workflows/**`（**独占**）。
- **前置**：S0.2 完成（CI 必须能发现测试工程）。
- **动作**：
  1. 新建最小流水线：Restore → Release Build → **Unit Test（逐工程核对发现数与执行数）** → 打包 → SHA-256 → 签署元数据 → 发布到测试渠道。
  2. CI 的测试步骤**不得**只跑 `dotnet test LiteMonitor.sln` 就判绿：必须解析输出中的测试计数，0 发现即失败（禁令 G3）。
  3. 引入 `--logger trx` 并把结果作为构件上传。
  4. 设置 `LITEMONITOR_PLUGIN_REPO` 指向仓库内固定夹具（夹具由 S4.1 提供；若本步骤先于 S4.1，则先挂 `continue-on-error` 并在 S4.1 后改为阻断）。
  5. 私钥不进仓库；PR 流水线用**测试密钥**，正式发布用**真实私钥**（由 S5.1 接入）。
- **交付物**：`.github/workflows/ci.yml` + 一次提交（`BASE-004`）。
- **验收**：本地用 `act` 或等价方式或直接读 YAML + 实跑等价命令，确认"0 发现即失败"这条真的成立。

> **M0 门禁：** S0.0/S0.1/S0.2/S0.3 全部 `VERIFIED_FIXED`（S0.1 产出事实即可），且全新检出可复现 §9 的基线数字。任一未达成 → M0 `CONDITIONAL`，**不得进入 M1**。

---

### M1 · 插件调度与请求身份

> 前置：**S1.0 契约冻结**（并行组开始前必须完成）。

#### S1.0 · 冻结 LM-002 租约模型契约 `[mavis]`

- **为什么必须先冻结**：LM-001（`PluginManager.cs`）、LM-002（`PluginExecutor.cs`）、LM-003a（同文件）以及 `PluginManager.cs:391-396` 的 `ResetNetworkClients()` 联动，四者共享"在途请求"这一语义。不先定契约，三个 worker 会各自发明一套。
- **产出契约文档**（落 `docs/superpowers/plans/`，不写产品代码）：
  1. **请求身份定义**：由 `实例ID + 步骤ID + HTTP方法 + 规范化URL + 请求体 + 影响响应的Header + 代理配置 + 响应编码` 组成。**明确 `inst.Id`/`step.Id` 已在现行键内**（`PluginExecutor.cs:285`），不重复编码。
  2. **等价依据**：改用稳定摘要（如 SHA-256 截断），**不使用** `GetHashCode()`（进程内加盐、32 位、每次运行可能不同）。
  3. **在途租约模型**：条目须可与自身 Task 比对身份（推荐包装为带等待者计数的租约对象或 `Lazy<Task<string>>`），使"旧任务完成不误删新条目"可实现——`ConcurrentDictionary.GetOrAdd` 的工厂可能并发执行多次，仅改 `TryRemove` 的条件**不足以**解决重复下载。
  4. **取消语义**：单个等待者取消只移除自己的等待，不影响共享请求与其他等待者。
  5. **`ResetNetworkClients()` 联动**：`PluginManager.cs:391-396` 在连续失败第 3 次时替换并释放 `HttpClient`（`PluginExecutor.cs:78-83`），会打断被其他实例等待的在途请求。契约须写明：重置时的在途请求处置（等待完成 / 显式失败 / 迁移到新客户端），以及 `ClearCache(inst.Id)` 是否只清本实例。
  6. **缓存与在途是否同一键**：明确"缓存身份"与"请求身份"是否需要分离（当前共用 `cacheKey`）。
- **门禁**：契约未冻结，不得派发 S1.1/S1.2/S1.3。

#### S1.1 · LM-001 插件计时器 `[worker-A]`

- **文件所有权**：`src/Plugins/PluginManager.cs`、`tests/LiteMonitor.Tests/PluginSchedulingTests.cs`（新建，**独占**）。
- **已核实事实**（不必重查；**已由 §4.1 实测更正**）：`PluginManager.cs:363-364` 设 `AutoReset = false`；`:371-375` 不可见分支在 `try` 之前 `return`，**但在 `return` 之前已执行 `newTimer.Interval = 2000`**；重启计时器的 `finally` 位于 `:417-423`。
  > ⚠️ **根因更正（见 §4.1）**：不可见路径虽然不进 `try/finally`，**但基线并未停摆**——`System.Timers.Timer` 在处理器内被赋值 `Interval` 即重新武装（实测：3 秒内触发 6 次）。基线存活依赖的是**未被文档化的副作用**，而非显式 `Start()`。
  > **因此本步骤的定位是**：**消除对未文档化副作用的依赖 + 引入实例代次守卫**，而不是"恢复停摆的轮询"。优先级由 P1 更正为 **P2**。
- **约束**：
  - 最小修复，不替换整个 Timer 框架；
  - 可见/不可见/失败/取消/停止路径统一收尾；
  - `StopInstance()`/`Stop()` 后不得重启；已释放计时器不得被异步回调访问；
  - 实例重启不得产生两个并行调度循环；
  - **必须引入实例代次或等价保护**：验收"Stop 返回后旧任务不重新调度、不写回结果，Restart 后最多一个有效调度"。仅把 `return` 移进 `try` **不满足**验收；
  - **重武装必须显式**（不得依赖"给 `Interval` 赋值会自动重武装"这一未文档化副作用）；不可见分支亦须走统一收尾路径；
  - 保持现有刷新间隔与退避的用户可见语义；
  - 与 S1.0 契约的第 5 条对齐（不得破坏 `ResetNetworkClients` 的重试语义）。
- **回归测试（必须全部执行，不得早退）**：
  1. 启动即可见 → 能执行；
  2. 启动即不可见 → 变可见后恢复执行。
     > ⚠️ **观测点更正（F1）**：不得以"计时器仍在跳"作为判据——基线同样满足该条件，断言恒真、无法证伪缺陷。必须改为断言**统一收尾路径被走到**（如：`finally`/`RearmSchedule` 被调用、或计时器由显式 `Start()` 重武装），并做反向验证（放回缺陷 → 该用例必须失败）。
  3. 可见→不可见→可见 → 轮询持续（同样受上述观测点约束）；
  4. 执行期间被禁用 → 不重复启动；
  5. 失败进入退避 → 成功恢复周期；
  6. 快速停止/启动/重启 → 无并发重复执行；
  7. 取消 → 无未观察异步异常；
  8. Stop 返回后旧任务不写回结果。
- **验收**：`dotnet test tests/LiteMonitor.Tests -c Debug --filter PluginScheduling` 全通过且**用例计数与断言匹配**（非 0 发现），且每条用例可给出**反向验证**（缺陷放回 → 失败）。
- **交付物**：代码 + 测试 + 提交 SHA。

#### S1.2 · LM-002 + LM-003a `[worker-B]`

- **文件所有权**：`src/Plugins/PluginExecutor.cs`、`tests/LiteMonitor.Tests/PluginRequestIdentityTests.cs`（新建，**独占**）。
- **前置**：S1.0 契约冻结。
- **已核实事实**：`PluginExecutor.cs:284` `contentHash = (url + "|" + body).GetHashCode().ToString("X")`；`:285` 键已含 `inst.Id`/`step.Id`；`:324` `_inflightRequests.GetOrAdd(cacheKey, _ => FetchRawAsync(...))`（工厂可并发执行多次）；`:346` catch 与 `:351` finally **各做一次无条件 `TryRemove`**，finally 在成功路径也执行；`:509` `var response = await client.SendAsync(...)` 无 `using`。
- **约束**：按 S1.0 契约实现租约模型；响应必须释放；响应体读取须有字节上限；**不得**以放宽断言或吞异常来"通过"；`ResetNetworkClients()` 的在途处置须与契约一致。
- **回归测试**：同请求并发只发一次（高并发计数断言）；不同方法/Header/代理/凭据互相隔离；单等待者取消不影响其他等待者；旧任务完成不误删新条目；响应体超限被拒且连接归还。
- **验收**：`dotnet test ... --filter PluginRequestIdentity` 全通过。
- **风险**：本步骤改动共享 HTTP 层，**必须**在完成后跑全量 `dotnet test tests/LiteMonitor.Tests -c Debug`（983+13 基线不得下降）。

#### S1.3 · LM-003b + LM-003c `[worker-C]`

- **文件所有权**：`src/System/UpdateChecker.cs`、`src/Core/NetworkSpeedTester.cs`、`tests/LiteMonitor.Tests/HttpLifecycleTests.cs`（新建，**独占**）。
- **已核实事实**：`UpdateChecker.cs:371` `request` 无 `using`；`:381` `resp` 无 `using`；`:388` `JsonDocument.Parse` 无 `using`；`:373` 用 `ResponseHeadersRead`，正文在 `:386` 另行读取而客户端超时不覆盖该阶段；`NetworkSpeedTester.cs:121` 与 `:243` 同样用 `ResponseHeadersRead`，正文读取无独立取消期限。**对照**：`UpdateChecker.cs:470`、`UpdateDialog.cs:179/272`、`NetworkSpeedTester.cs:121` 的 `resp` 本身已有 `using`——问题在请求对象、JSON 文档与正文期限，不在响应。
- **约束**：
  - 分别规定**连接期限 / 正文读取期限 / 总任务期限**三者，不得用单一超时糊弄；
  - `ResponseHeadersRead` 下必须自建正文读取期限与字节上限；
  - **禁止**改动 `UpdateChecker` 现有的验签实现与失败即拒绝路径（`:395-410`）——那是既有已验证行为，回归基准是 `tests/LiteMonitor.Tests/UpdateSignatureTests.cs`；
  - 不得关闭 TLS 校验。
- **验收**：新增用例通过 + 全量基线不下降 + `UpdateSignatureTests` 仍全通过。

> **M1 门禁：** S1.1/S1.2/S1.3 全部 `VERIFIED_FIXED`，全量测试无回归，且由 `verifier` 独立复核（不得由实现者自审代替）。LM-001 已由独立复核判 **PASS**（附 F1–F8，见 §4.1.4）；F1–F4/F7 的**判别力补强**为 M1 闭环的最后一项，补强完成后须复跑全量回归。LM-001 优先级已由 P1 更正为 **P2**（§4.1.3），但 S1.1 的"显式收尾 + 代次守卫"验收条款**不变**。

---

### M2 · Web 安全验收单元

> 监听、会话、票据、DOM 与配置迁移是**同一个安全验收单元**：任一项单独交付都不构成可发布状态。
> 前置：**S2.0 协议冻结**。

#### S2.0 · 冻结主会话与票据协议 `[mavis]`

- **产出协议文档**，须写明：
  1. **主会话**：如何传递（Cookie 或等价）、属性（`HttpOnly` / `SameSite` / HTTPS 下的 `Secure`）、CSRF 防护方式；
  2. **生命周期**：签发、过期（绝对 TTL 与空闲 TTL）、注销、改密后如何撤销既有会话/票据/WebSocket；
  3. **HTTP 快照如何认证**（当前走 URL 上的 `?pwd=`，`WebSessionManager.cs:380` 的 `PwdQueryRegex` 同时接受 `?pwd=` 与 `&pwd=` 两形态，收敛为单一口径）；
  4. **票据**：申请方式、TTL、是否一次性、绑定与失效规则；浏览器 WebSocket 无法自定义 Authorization 头这一限制如何绕过；
  5. **请求体解析**：新增登录/票据申请端点后，请求体大小上限、读取期限、重复字段、不支持编码的拒绝行为；
  6. **保留既有防护并扩展到新端点**：5 秒请求头期限（`WebSessionManager.cs:353`）、64 连接上限（`LiteWebServer.cs:66`）、写超时 5 秒（`:372`）；
  7. **Origin / Host 校验覆盖本机模式**，明确"缺失 Origin"、"Origin 为 `null`"、可信代理头（`X-Forwarded-For`）的处理方式；
  8. **升级页主动删除旧 `ws_pwd`**；密码修改必须撤销既有会话、票据与 WebSocket；
  9. **TLS 部署要求**写成明确条款，不得只写"受保护端点"。
- **门禁**：未冻结不得派发 S2.1/S2.2。

#### S2.1 · LM-004 DOM 注入 `[worker-A]`

- **文件所有权**：`src/System/WebServer/WebPageContent.cs`、`tests/LiteMonitor.Tests/WebDomSanitizationTests.cs`（新建，**独占**）。
- **已核实事实**：`WebPageContent.cs:483` 插值 `${grp.name}`；`:518`、`:547`、`:581` 插值 `${item.n}`；动态分组标识还参与 HTML 属性与选择器拼接。**本次未运行攻击载荷，不得声称已复现脚本执行。**
- **约束**：
  - 文本一律用 `textContent` 或 `createElement`；动态 ID / 选择器单独处理（受限字符集），不与文本路径混用；
  - **不依赖前端过滤作为唯一防御**；
  - **不改变当前网页布局与监控项排序**（回归网：`tests/LiteMonitor.Tests/WebThemeCssTests.cs`）。
- **测试载体（三选一，写死）**：结构级断言（生成的 JS 中不得出现对用户数据的 `innerHTML` 插值）／JSDOM／Playwright。**不得**只写"人工检查"。
- **覆盖范围**：普通卡片、大小卡片、DASH、首次创建与持续刷新，**HTTP 与 WebSocket 两条数据通道**；断言"脚本未执行"且"DOM 结构未改变"，并断言正常刷新仍工作。

#### S2.2 · SEC-001 + SEC-002 + LM-005/006 + LM-023 `[worker-B]`

- **文件所有权**：`src/System/WebServer/LiteWebServer.cs`、`src/System/WebServer/WebSessionManager.cs`、`src/Core/Settings.cs`、相关设置 UI（`src/UI/Settings/**`）、`tests/LiteMonitor.Tests/WebSessionProtocolTests.cs`（新建，**独占**）。
- **已核实事实**：默认绑定 `IPv6Any`（`LiteWebServer.cs:190`）/ 回退 `IPAddress.Any`（`:197`）；`WebSessionManager.cs:894` 对**所有**响应返回 `Access-Control-Allow-Origin: *`；全文件无 Origin / Host 校验；`Settings.cs:168` `WebServerEnabled` 默认 `false`（现有默认关闭行为保持不变）、`:170` `WebServerPassword` 默认空串；`localStorage['ws_pwd']`（`WebPageContent.cs:303/351`）与 `?pwd=`（`:335/362/393`）。
- **约束**：
  - 默认仅监听环回（`127.0.0.1`，必要时 `::1`）；局域网模式需显式开启且必须配置非空密码；
  - **存量配置兼容**：`WebServerEnabled=true` 且密码为空的既有配置，必须有明确处置（拒绝启动并提示／强制环回／引导设置），**不得静默变更监听范围**；
  - Host 校验（防 DNS Rebinding）与 Origin 白名单覆盖本机模式；**不使用**宽泛 CORS；
  - HTTP 与 WebSocket 权限判断一致（**现状已统一**：`WebSessionManager.cs:747` 与 `:1235` 均调 `CheckAuth`，本项只需回归保护，不得重写）；
  - 凭据按 S2.0 协议改为主会话 + 短期票据；**不得**把密码换到另一个可被脚本读取的长期存储位置；
  - 区分"Web 密码验证材料"与"必须恢复原文的插件密钥"；明确 DPAPI 作用域（`CurrentUser` 与 `LocalMachine` 的用户隔离能力不同）与旧备份的明文处理策略；**禁止**解密失败后静默变成空密码、扩大监听范围或删除原凭据。
- **回归测试**：本机模式不对局域网暴露；未设密码不能对外监听；错误密码拿不到快照也建不了有效 WS；跨来源网页不能读监控数据；HTTP 与 WS 权限一致；Stop/Start 不留旧会话或重复广播；旧配置升级后行为符合迁移约定；跨用户/迁移到其他机器/解密失败三态各自可判定。
- **验收**：新增用例通过 + `WebSamplingDemandTests`/`WebDemandTests`/`WebAuthHardeningTests`/`WebThemeCssTests` 全部仍通过。

> **M2 门禁：** S2.1 与 S2.2 同属一个验收单元，**必须一起**通过独立 verifier 复核。两者任一 `NOT_VERIFIED` → M2 `CONDITIONAL`，且**不得**进入发布准备。

---

### M3 · 插件市场与执行边界

#### S3.1 · LM-007 + LM-008 `[worker-A]`

- **文件所有权**：`src/Plugins/PluginMarket.cs`、`tests/LiteMonitor.Tests/PluginMarketPolicyTests.cs`（新建，**独占**）。
- **已核实事实**：`:145` 仅 `url.StartsWith(prefix, OrdinalIgnoreCase)`；`:128-131` 静态 `HttpClient` 默认跟随重定向且不复核最终目标；`:286` `GetByteArrayAsync` 无字节上限；`:155` `if (string.IsNullOrEmpty(expected)) return true;`（缺摘要放行）；`:321` 写文件 → `:324-332` 才 `PluginMarketState.Upsert`（非同一事务）。
- **约束**：
  - 用 `Uri.TryCreate` 解析，强制 HTTPS，**精确**匹配允许 Host（不靠前缀）；
  - **每一次跟随重定向之前**校验下一目标，而非下载结束后才看最终网址；限制跳转次数、端口、路径规范化（拒绝编码分隔符与路径穿越）、拒绝 userinfo；
  - 实际读取字节数上限；
  - 市场安装路径**强制**摘要（"没提供摘要"不得解释为"不需要校验"）；
  - 文件与安装记录使用**同一事务标识**：同卷暂存 → 原子替换 → 落盘成功后才更新记录；并发生成加锁；启动时恢复；
  - **区分两种失败**："抛异常后的回滚"与"进程被杀后的恢复"，两者各有验收；
  - 安装失败不得破坏原有插件；用户自装插件不得被市场覆盖或误卸载。
- **回归测试**：路径规范化、编码分隔符、跨域跳转、畸形 URL、缺摘要、事务中断恢复。

#### S3.2 · LM-009 插件执行边界 `[worker-B]`

- **文件所有权**：`src/Plugins/PluginProcessor.cs`、`tests/LiteMonitor.Tests/PluginSandboxLimitsTests.cs`（新建，**独占**）。
- **已核实事实**：`PluginProcessor.cs:130` `Regex.Replace(val, t.Pattern, replacement)` 无匹配期限，`:132` `catch { return val; }` 把超时/回溯异常一并吞掉。
- **约束**：为**响应字节数、正则匹配时限、步骤数、目标展开数量、单次执行总时长、总并发数**规定**具体数值**与**失败行为**（数值不得留空）；灾难性回溯与超大正文下进程仍可响应取消；API Key 只发往用户知晓并允许的目标；禁止把用户输入或网络响应写进高权限命令执行入口；**保持声明式插件，不新增任意脚本执行**。
- **声明式插件仍有网络访问能力**：须在文档中说明可信插件来源与本地网络访问策略。

> **M3 门禁：** S3.1、S3.2 可并行（文件不重叠），但须一起复跑全量测试。

---

### M4 · 市场夹具与发布链路

#### S4.1 · 市场契约夹具入库 `[mule]`

- **目标**：消除 ENV-003，让 6 条本地契约测试在 CI 中真正执行。
- **文件所有权**：`tests/fixtures/plugin-repo/**`（新建，**独占**）。
- **已核实事实**：6 条由 `PluginRepoFactAttribute` 门控（环境变量 `LITEMONITOR_PLUGIN_REPO`，`PluginRepoFactAttribute.cs:21`），指向**本地**含 `index.json` 的插件仓库副本；另 1 条由 `E2EFactAttribute` 门控（`LITEMONITOR_E2E=1`，`:19`，理由明写"联网 E2E，会真实访问 GitHub"）。二者**不可混称**。
- **动作**：构造最小但**完整**的插件仓库夹具（`index.json` + 各插件目录 `plugin.json` + 故意含一处下载 URL 前缀越界的反例），纳入版本控制；记录夹具的期望判定（哪些条目应通过、哪些应被拒）。
- **验收**：设 `LITEMONITOR_PLUGIN_REPO` 指向夹具后，6 条契约测试**全部执行且通过**（不是跳过）；CI 配置中已指向该路径。
- **注意**：**联网 E2E 单独运行，不进 PR 门禁**（ENV-004）。

#### S4.2 · LM-016 验签→更新器交接 `[worker]`

- **文件所有权**：`LiteMonitor.Updater/Program.cs`、`LiteMonitor.Updater/UpdateFileTransaction.cs`、`src/UI/UpdateDialog.cs`、`tests/LiteMonitor.Updater.Tests/**`（**独占**）。
- **已核实事实**：`UpdateDialog.cs:369` `ComputeFileSha256(_context.SavePath)` 校验摘要；`LiteMonitor.Updater/Program.cs:48` `string zipFile = args[0];` 更新器**事后按路径重新读取**该文件。**本次未验证安装目录权限，不能认定已存在可利用提权漏洞。**
- **约束**：
  - 增加"**已验证字节必须保持不变直至使用**"的验收要求；可选方案：受保护暂存区／受控文件句柄／更新器独立验证；
  - 测试：校验后替换、并发更新、异常 ZIP 展开量、目标目录越界、错误更新器被拒；
  - 更新失败可恢复时恢复原版本；无法完全恢复时保留完整诊断与恢复材料；**绝不**把部分更新误标记为成功；失败后不得误删仍需恢复的备份；不得误删用户数据、主题或配置。
- **验收**：`dotnet test tests/LiteMonitor.Updater.Tests -c Debug`（现有 13 项基线不得下降）+ 新增故障注入用例通过。

> **M4 门禁：** S4.1 与 S4.2 可并行（文件不重叠）。

---

### M5 · 签名与发布

#### S5.1 · LM-015 + LM-017 真实签名与发布顺序 `[worker]`

- **文件所有权**：`scripts/sign-release.ps1`、`scripts/pack-release.ps1`、`resources/version.json`、新增发布流程脚本（**独占**）。
- **已核实事实**：`resources/version.json` 的 `sha256` 与 `signature` **均为空字符串**；客户端验签**已实现**（`UpdateChecker.cs:115-160` ECDSA P-256，失败即拒，`:395-410` 验不过换源而非放行），配套 `UpdateSignatureTests.cs`。`scripts/sign-release.ps1` 存在于本地但未纳入版本控制（由 S0.2 负责入库）。
- **约束**：
  - **不得改动 `UpdateChecker` 现有验签实现**——回归基准是 `UpdateSignatureTests.cs`；
  - 发布顺序固定为：**最终产物冻结 → 签名 → 独立验证 → 上传并核验远端资产 → 公开 Release → 客户端可查询的元数据**；当前元数据直接来自 master，**不得**先公告尚不可下载的版本；
  - 私钥不进 Git 历史、日志或构建产物；
  - 镜像必须做**相同**签名与内容一致性验证。
- **验收**：对真实 Release ZIP 计算 SHA-256 并签名 → 产出非空 `version.json` → 用仓库内公钥独立验签通过 → 空摘要/错误签名/错配摘要三种情形必然被拒（扩展 `UpdateSignatureTests.cs`）。

#### S5.2 · 签名一致性用例去假绿 `[worker]`

- **目标**：消除 LM-015 的假绿面。
- **文件所有权**：`tests/LiteMonitor.Tests/UpdateSignatureTests.cs`（**独占**）。
- **已核实事实**：`UpdateSignatureTests.cs:273-274` `if (keyPath == null) return;` —— 缺私钥时**静默返回并被记为 Passed**。`FindSigningKey()`（`:311-320`）自输出目录上溯最多 8 级查找 `secrets/update-signing-key.pkcs8`。
- **重要限定（不得误用）**：本机 `secrets/update-signing-key.pkcs8` **存在**，上溯 5 级即命中，因此**本机既有 983 项基线中该用例真实执行了断言**——假绿发生在**干净检出与 CI**，不是本地基线。
- **约束**：
  - 改用与 `PluginRepoFactAttribute` / `E2EFactAttribute` 同款的**显式 Skip 特性**（缺私钥 → 记 Skipped 而非 Passed），或按契约直接失败；**二者择一，但必须让"没验到"在报告里可见**；
  - PR CI 用**测试密钥**验证算法与脚本；正式发布任务**单独强制**验证真实私钥、内置公钥与最终产物的一致性；缺钥或不匹配**必须失败**；
  - `scripts/sign-release.ps1` 内已有同口径自检（`UpdateSignatureTests.cs:266-267` 注释确认）——缺的是让它在 CI 中必然触发。

#### S5.3 · LM-024 依赖与产物清单 `[mule]`

- **文件所有权**：`NOTICE`、新增依赖清单生成脚本（**独占**）。
- **已核实事实**：`NOTICE:40` 写 `System.Text.Json 9.0.10`；`LiteMonitor.csproj:52` 实际引用 `Version="10.0.3"`；`LiteMonitor.csproj:87-90` 以 `Content` 复制**预构建**的 `resources\LiteMonitor++.Updater.exe`。因此"解决方案编译成功"**不能**证明包内更新器对应本次源码。
- **动作**：按**实际解析依赖**与**最终包内容**生成清单；核对许可证、运行时要求、驱动/FPS 资源与更新器版本；修正 NOTICE 版本；把"包内更新器是否由本次源码构建"列为显式检查项。
- **验收**：清单与 `dotnet list package --include-transitive` 及最终 ZIP 实际内容一致；NOTICE 不再出现与实际引用不符的版本。

> **M5 门禁：** S5.1/S5.2/S5.3 可并行（文件不重叠）。**正式签名未就绪 → 发布准入直接 `NO_RELEASE`。**

---

### M6 · GPU 与硬件验证（验证型阶段）

> 本阶段以**产出判定结论**为交付物，不以"改了多少"为交付物。

#### S6.1 · 调用图与生产路径清单 `[explore]`（只读）

- **目标**：区分当前生产路径、兼容路径、实验代码与已废弃实现，为后续判断提供依据。
- **范围**：`src/System/Backdrop/**`、`src/Rendering/Presentation/**`、`src/System/HardwareServices/**` 只读。
- **交付物**：调用图 + 每条路径的"是否被生产调用"判定 + 证据（调用点行号）。
- **约束**：**不得**凭文件名推断"已废弃"；删除决策另立任务，且不得在修复过程中顺手删除。

#### S6.2 · 设备失败与性能验证 `[verifier]`

- **交付物**：逐行判定结论（每行 PASS / FAIL / `NOT_VERIFIED` / `BLOCKED_ENV` + 证据）。
- **验证矩阵**：GPU 初始化失败、设备丢失、切换 GPU/GDI+、窗口最小化/恢复、拖入不同 DPI 显示器、主题反复切换、WDA 设置失败、待机/唤醒、窗口关闭。
- **性能指标**：进程 CPU、工作集与 Private Bytes、GPU 显存、GDI/USER Objects、捕获耗时、GPU 渲染耗时、有效帧率、捕获失败次数、设备重建次数。**区分一次性缓存增长与持续无界增长**，不以"任务管理器内存更低"为唯一标准。
- **已知方法学缺陷必须一并处理**：`tests/LiteMonitor.Tests/GpuVramTests.cs:56` `if (!actual.HasValue) return;` 在无该型号显卡时**静默返回并记 Passed**，且 `:51-52` 的期望值是本机实测的 RTX 3080/3080 Ti 容量。正确改法是**改为本机自洽断言 + 机型差异显式标记跳过**，**不得**删除该用例（那会丢掉一条真实有效的容量回归）。
- **另需实测 LM-020**：自动 GC / 工作集裁剪对 CPU 开销、GC 暂停、缺页行为、UI 响应延迟、真实内存压力的影响。若只降低显示值而增加开销，应考虑取消自动执行或改为用户显式操作。
- **结论规则**：确认无缺陷也是合格交付物，但**必须附测试范围**（禁止 G1–G4 的"无限范围无缺陷证明"）。

---

### M7 · 权限架构（独立，可延期）

> **不得与前面的修复混在同一提交。**

#### S7.1 · 权限需求调查 `[explore]`（只读）

- 逐项确认实际所需权限：读普通性能计数器、读 LHM 传感器、用 PawnIO、ETW/FPS 采集、改自启计划任务、装驱动、更新程序文件。
- 交付物：权限需求矩阵（操作 × 所需权限 × 可否降级）。

#### S7.2 · 架构方案 `[mavis]`

- 评估拆分：普通权限 UI 与网络进程 / 最小功能的高权限硬件辅助服务 / 受限 IPC 通道 / 独立驱动安装及提权升级。

#### S7.3 · 实施 `[worker]`（**默认延期**）

- 条件：S7.1/S7.2 完成且用户批准。**默认不进入本轮**。
- 关键约束：**当前 Web、插件与更新器涉及的高权限边界不能一并延期**——这三处在 M2/M3/M5 已按降权设计，但**权限架构本身**可延期。

---

### M8 · 终验

#### S8.1 · 独立终审 `[verifier]`

- **交付物**：`PASS` / `FAIL` / `PARTIAL` + 按缺陷 ID 的状态矩阵 + 残留风险清单。
- **必须核对**：所有 P0/P1 为 `VERIFIED_FIXED`；无未解决 P0；全量测试通过且用例真实执行；Release 构建通过；核心 GPU 路径有真实证据或已明确标 `NOT_VERIFIED`；安全验收无未处理高危路径；新装与升级装均验证；发布包与受测源码一致；无法验证项列入残留风险。
- **禁止**：不得以"Agent 复审通过""曾经实机通过"作为通过依据。

---

## 6. 文件所有权总表（并行可行性判定）

| 步骤 | Agent | 独占文件 |
|---|---|---|
| S0.0 | mavis | （无代码；仅环境处置记录） |
| S0.1 | explore | 无（只读） |
| S0.2 | mule | `.gitignore`、`LiteMonitor.sln`、`tests/**`、`scripts/**`、`docs/**` |
| S0.3 | worker | `.github/workflows/**` |
| S1.0 | mavis | `docs/superpowers/plans/**`（契约文档） |
| S1.1 | worker-A | `src/Plugins/PluginManager.cs`、`tests/.../PluginSchedulingTests.cs` |
| S1.2 | worker-B | `src/Plugins/PluginExecutor.cs`、`tests/.../PluginRequestIdentityTests.cs` |
| S1.3 | worker-C | `src/System/UpdateChecker.cs`、`src/Core/NetworkSpeedTester.cs`、`tests/.../HttpLifecycleTests.cs` |
| S2.0 | mavis | `docs/superpowers/plans/**`（协议文档） |
| S2.1 | worker-A | `src/System/WebServer/WebPageContent.cs`、`tests/.../WebDomSanitizationTests.cs` |
| S2.2 | worker-B | `src/System/WebServer/LiteWebServer.cs`、`src/System/WebServer/WebSessionManager.cs`、`src/Core/Settings.cs`、`src/UI/Settings/**`、`tests/.../WebSessionProtocolTests.cs` |
| S3.1 | worker-A | `src/Plugins/PluginMarket.cs`、`tests/.../PluginMarketPolicyTests.cs` |
| S3.2 | worker-B | `src/Plugins/PluginProcessor.cs`、`tests/.../PluginSandboxLimitsTests.cs` |
| S4.1 | mule | `tests/fixtures/plugin-repo/**` |
| S4.2 | worker | `LiteMonitor.Updater/**`、`src/UI/UpdateDialog.cs`、`tests/LiteMonitor.Updater.Tests/**` |
| S5.1 | worker | `scripts/sign-release.ps1`、`scripts/pack-release.ps1`、`resources/version.json`、发布流程脚本 |
| S5.2 | worker | `tests/LiteMonitor.Tests/UpdateSignatureTests.cs` |
| S5.3 | mule | `NOTICE`、依赖清单生成脚本 |
| S6.1 | explore | 无（只读） |
| S6.2 | verifier | 仅临时验证产物（须显式指定临时目录） |
| S7.x | explore / mavis / worker | 另议 |

**可并行组：** `{S0.1, S0.2}` → `{S0.3}`；`{S1.1, S1.2, S1.3}`（S1.0 后）；`{S2.1, S2.2}`（S2.0 后）；`{S3.1, S3.2}`；`{S4.1, S4.2}`；`{S5.1, S5.2, S5.3}`。

**强制串行：** S1.0 → {S1.1,S1.2,S1.3}；S2.0 → {S2.1,S2.2}；S0.2 → S0.3。

---

## 7. 缺陷 → 步骤 追踪矩阵

| 缺陷 | 步骤 | 优先级 | 门禁阶段 | 状态 | 依据 |
|---|---|---|---|---|---|
| BASE-002 | S0.1 | P1 | M0 | ✅ 已闭环 | `0366268`；484/522 口径已在 §8 标注 |
| BASE-003 | S0.2 | P1 | M0 | ✅ 已闭环 | `0366268`（`.gitignore` 修正 + tests/scripts/docs 入库 + `.sln` 加 3 个测试工程）⇒ 解决「测试发现 0」 |
| BASE-004 | S0.3 | P1 | M0 | ✅ 已闭环 | `d84b40f`（CI + `assert-test-counts.ps1`） |
| BASE-005 | S0.0 | P1 | M0 | ✅ 已闭环 | ENV-001：实例自行退出，Release 构建在 CI 内成功（§8） |
| LM-001 | S1.1 | **P2**（原 P1，根因更正见 §4.1） | M1 | ✅ 已闭环 | `ac652f8` + `afb0869` + `6609543`；根因已更正（§4.1），提交信息勘误见 §4.5.6 |
| LM-002 | S1.2 | **P1** | M1 | ✅ 已闭环 | `d437283`（租约模型 + SHA-256 请求身份，21 用例） |
| LM-003a | S1.2 | P2 | M1 | ✅ 已闭环 | `d437283`（响应释放 + 8 MiB 上限） |
| LM-003b / LM-018 | S1.3 | P2 | M1 | ✅ 已闭环 | `2bbb4cd`（三段期限 + 资源释放） |
| LM-003c / LM-019 | S1.3 | P2 | M1 | ✅ 已闭环 | `2bbb4cd`（测速无进展期限） |
| SEC-001 | S2.2 | **P1** | M2 | ✅ 已闭环 | `4be1a8c`；M2 重核 PASS（§4.2.6） |
| SEC-002 | S2.2 | **P1** | M2 | ✅ 已闭环 | `4be1a8c`；M2 重核 PASS（§4.2.6） |
| LM-004 | S2.1 | **P1** | M2 | ✅ 已闭环 | `0484177`（DOM 文本化 + 前端协议迁移，12 用例） |
| LM-005/006 | S2.0 + S2.2 | **P1** | M2 | ✅ 已闭环 | `4be1a8c` + `e893faf`/`dfe0fff`（C1/C2/C3 护栏）；M2 **放行 PASS** |
| LM-023 | S2.2 | P2 | M2 | ✅ 已闭环 | `4be1a8c`（PBKDF2/DPAPI）；残留 F-08（DPAPI 失败明文回写）登记为独立信息项 |
| LM-007 | S3.1 | P2 | M3 | ✅ 已闭环 | `14542f3`（URL 精确校验 + 逐跳重定向 + 8 MiB，53 用例 / 11 变异全捕获） |
| LM-008 | S3.1 | P2 | M3 | ✅ 已闭环 | `14542f3`（强制摘要 + 事务化安装）+ `cb460b5`（本地化） |
| LM-009 | S3.2 | P2 | M3 | 🟢 **已闭环**（2026-10-23 三次更正后） | `ebe1c38`/`bc7b479` + 联合专项复核 **PASS**（§4.3.4）+ 独立复核 **PASS_WITH_NOTES**（§4.3.5）+ `938dc4b`。**口径（2026-10-23 三次更正）**：① **直连目标**的 6 个上限已在「HTTP 步骤路径 **+ `native://` 路径**」闭环；② **重定向后的目标**原先不受约束（LM-045，P1）⇒ **现已受约束**（`938dc4b`：两侧都禁自动重定向 + 逐跳复核目标主机，不通过时该跳请求根本不发出）⇒ 「插件一切网络出口受沙箱约束」**现在**成立，但**仅限**插件可控的这两条出口 —— 市场索引拉取（LM-054）与 citycode 常量 URL（LM-055）的目标是**用户配置 / 代码内建常量**，不受"允许目标"约束（见 §4.5.9） |
| LM-010~014 | S6.2 | P2 | M6 | ⏳ 未开始 | GPU/采样/资源恢复实机验收 **G01–G12 全部未跑**（G08 所需异 DPI 环境本机不存在） |
| LM-015 | S5.1 + S5.2 | **P1** | M5 | ⏸ **延后** | 用户已定本批只做 S5.3 ⇒ 正式签名未就绪 ⇒ **阻断发布准入条件 4** |
| LM-016 | S4.2 | **P1** | M4 | ✅ 已闭环 | `1a904d5`（字节绑定 + `FileShare.Read`，58 用例）+ `e1bd587`（重建包内更新器） |
| LM-017 | S5.1 | **P1** | M5 | ⏸ **延后** | 发布顺序未执行 ⇒ **阻断发布准入条件 8** |
| LM-020 | S6.2 | P2 | M6 | ⏳ 未开始 | GC/工作集裁剪策略未经实测 |
| LM-021 | S7 | P3 | M7（可延期） | ⏸ 延后 | 权限架构独立阶段，默认延期 |
| LM-024 | S5.3 | **P1** | M5 | ✅ 已闭环 | `217102d` + `e452097`（21 个传递依赖）+ `600a18c`（CI 门禁）+ `e1bd587`（产物来源判定由 `NOT_VERIFIED` 翻转为「与本次源码构建一致」） |
| LM-030 | S3.3 | P2 | M3 | ✅ 已闭环 | `ebe1c38`；§4.3.4 判定 **LM-030 闭环成立** |
| LM-031 | 待清理 | P2 | — | 📌 登记待清理 | §4.3.2；宽泛 catch，属**基线既有**行为，不判本轮失败 |
| LM-032 | 待清理 | P2 | — | 📌 登记待清理 | §4.3.2；用例名与断言不符 |
| LM-033 | S3 | P3 | — | ✅ 已闭环 | §8 基线数字已按实测更新 |
| LM-034 | S4.2b | P2 | M4 | ✅ 已闭环 | `6d993d7`；危害描述经实测更正为「套接字泄漏」（§4.5.4） |
| LM-035 | 待清理 | P2 | — | 📌 登记待清理 | `Handshake` 宽泛 catch，与 LM-031 同类 |
| LM-036 | S2.4 | P2 | M2 | ✅ 已闭环 | `366bf9c`（恢复被删断言，复跑 35/35 通过）⇒ M2 放行条件 ① 满足 |
| LM-037 | 信息项 | P3 | — | 📌 信息项 | §4.2.7；行为级护栏已存在（V5b），**不阻断** |
| LM-038 | S4.2c | P2 | M4 | ✅ 已闭环 | `e1bd587`（按流水线口径在 `366bf9c` 重建并刷新仓库副本，内嵌 SHA 对齐） |
| LM-039 | **S3.5** | **P1** | M3 | ✅ 已闭环 | `d23e6fc`：原生取数接入同一套闸门（并发槽位 / 2 MiB / 30 s 截止 / 凭据目标 / 允许目标）；8 条新用例 / 5 变异全捕获。**口径更新**：LM-009 闭环范围由"HTTP 步骤路径"扩展到"HTTP 步骤路径 **+ native:// 路径**" |
| LM-040 | **S3.6** | P2 | M3 | ✅ 已闭环 | `d588440`：改判 `Interlocked.Add` 的累计返回值；单元测试期望已同步反转 |
| LM-041 | **S3.6** | P2 | M3 | ✅ 已闭环 | `d588440`：端到端断言 `MatchTimeout == 500 ms`（字面量）+ 宽松耗时上界 < 5 s |
| LM-042 | **S3.6** | P2 | M3 | ✅ 已闭环 | `d588440`：URL 改目标专属 + 收紧为「恰好 64 个请求」 |
| LM-043 | **S3.6** | P3 | M3 | ✅ 已闭环 | `d588440`：删除 2 处空转断言，承重断言保留 |
| LM-044 | **S3.6** + 主审集成 | P2 | M3 | ✅ 已闭环 | `d588440`（共享默认）+ `6310dbf`（实例级 `CreateScope` 缝隙，**补上 S3.6 未能实测的变体**） |
| GPU-001 | GPU T03/T04 | **P1** | GPU B→C | 🟡 **已实现，待独立复核** | **根因修复已落地**：`4ade856`（T04）宿主恢复失败 ⇒ 产出 `WindowCapturePolicyCleanup` 交给订阅者并**释放自己**；`5a1eeb7`（T03）`StopGpuPresentation` **无条件**置 null ⇒ 三条重启路径重新可达；清理责任由 `CapturePolicyCleanupLedger` **独立持有**（合同 §6.2 INV-1）。★ 主审巡检曾发现 worker 首版**未订阅**移交事件、`ledger.Add` 从未被调用 ⇒ 清理责任会彻底丢失 ⇒ 已纠正。**实机端到端（真实 GPU 走非干净停止分支）仍属 T07/T09 ⇒ `NOT_VERIFIED`** |
| GPU-002 | GPU T02/T04 | P2 | GPU B→C | 🟡 **已实现，待独立复核** | `4ade856`（T04）正式采用 `PresentationSession.RecreateWindow`，补齐 R1–R6（代次单调校验 / 恢复失败不改句柄 / 新句柄重读原值 / 代次可观测 / 失败码可区分） |
| GPU-003 | GPU T02/T03 | P2 | GPU B→C | 🟡 **已实现，待实测** | T01 实测判定 **`RECREATE_HANDLE_SAFE`**（句柄事件 `HandleDestroyed → HandleCreated` 有序、代次恰好 +1、桌面像素回读证明重建后 GDI 动态帧恢复）；`5a1eeb7`（T03）新增**只读** `MainFormWinHelper.IsWindowLayered()`（只读 ex-style，**绝不**修改 ⇒ 合同 §1.3 明确否决的"顺手清 `WS_EX_LAYERED`"未发生），真实值喂给 `RenderingRouteRequest.WindowIsLayered` ⇒ 期望路由由 `RenderingRoutePolicy.Decide` 判定。**真实 layered 窗口下的二次校验仍需实机（T09）⇒ `NOT_VERIFIED`** |
| GPU-004 | GPU T04 | P2 | GPU B→C | 🟡 **已实现，待独立复核** | `4ade856`（T04）代次判据**先于** `IsWindow`（旧代次 ⇒ 禁止任何 Win32 调用）；代次唯一真相源 = `WindowHandleGeneration.Instance`（裁决 D-G）；`5a1eeb7`（T03）**删除了** `LiveGlassController` 的第二个计数器（⇒ 上一轮 verifier 的 F7 已消除） |
| GPU-005 | GPU T02 + owner A 接线 | P2 | GPU B→C | 🟡 **已实现，待独立复核** | pending 仅在「首帧成功」或「受控放弃且停止流程干净」时清除：`03a925a`（owner A 接线）让 `GpuStartupAttempt` 实现 `IGpuStartupAttemptAbandon`（`AbandonPreservingPending`：只注销尝试、**不删** pending 标记、**不复位** `_attemptWritten`）；`4ade856`（T04）的 `ReleaseStartupAttempt(stopClean)` 通过运行时探测衔接该接口。**宿主侧端到端（停止不干净 ⇒ pending 保留）需真实 GPU ⇒ T07/T09** |
| LM-045 | **本批已修** | **P1** | M3→M4 | 🟢 **已闭环（独立复核 `PASS_WITH_NOTES`）** | `938dc4b`：HTTP 步骤路径与 `native://` 路径**两侧**都关闭自动重定向、改为逐跳跟随并**逐跳复核目标主机**；带凭据的每一跳须落在**声明主机集合**内，不带凭据的每一跳须落在（声明集合 ∪ {直连目标主机}）内；不通过 ⇒ **该跳请求根本不发出**。跳数上限 5 / 缺 `Location` / 相对 `Location` / 非 http(s) / **https→http 降级**均被拒。22 条新用例 / 7 变异全捕获。**独立复核已完成（§4.5.10）**：复核方**自写** 47 条独立用例（不跑实现者用例）+ **自设计** 12 条变异（其中 9 条完全不在实现者已做类别内）**12/12 全部被捕获**；实测"未声明主机 **0 个请求** / **从未见到秘密** / 响应体**不入流水线**"；契约 **6 常量一个未改**；公开面**无既有 public 签名被删改** |
| LM-046 | 接受并登记 | P3 | — | 📌 信息项 | `CryptoNative.SourceUrlOverrides`（`internal static` 可变全局测试接缝）：生产路径永不设置，测试用 `IDisposable` 复位 ⇒ 无泄漏、无并行冲突。但位于生产程序集内且可写 ⇒ **须在后续审计中显式知情**。独立 verifier 已复核：全仓赋值点仅在测试文件 |
| LM-047 | 已修 | P2 | M3 | ✅ 已闭环 | `46f3f9d`：原语层 `Math.Min(maxBytes, MaxResponseBytes)` 夹取，契约天花板从"约定"变成"强制"；新增护栏用例并以变异自证（去掉夹取 ⇒ 变红 ⇒ 字节级还原 ⇒ 26/26 全绿） |
| LM-048 | 登记待清理 | P2 | — | 📌 信息项 | 允许目标口径分裂（主机判据 `allowedHosts` vs 凭据判据 `declaredHosts`）；内置插件凭据步骤数 = 0 ⇒ 当前无功能影响 |
| LM-049 | 登记待清理 | P2 | — | 📌 信息项 | `native://` URL 产出伪主机污染声明集合；当前匹配不上真实主机 ⇒ 无害 |
| LM-050 | 登记待清理 | P3 | — | 📌 信息项 | `native://citycode` 远程回退闸门无用例；需先给 `JSON_DB_URL` 加测试接缝 |
| LM-051 | 登记待清理 | P2 | — | 📌 信息项 | `CryptoNative.NormalizeSymbol` 未 `EscapeDataString`（**既有缺陷**，主机固定 ⇒ 不构成沙箱逃逸） |
| LM-052 | 登记待清理 | P3 | — | 📌 信息项 | `CityCodeResolver._isLoading` 无锁 `static bool`（**既有缺陷**，无安全影响） |
| LM-053 | 随 LM-045 一并修 | P2 | M4 | 🟢 **已闭环（独立复核 `PASS_WITH_NOTES`）** | `native://` 路径同样可被 302 绕过"允许目标"：`CryptoNative` 客户端未关自动重定向 + `allowedHosts` 未贯穿到发请求层 ⇒ 只修 HTTP 步骤路径**不足以**关闭 LM-045。`938dc4b` 已两侧闭环（见 §4.5.9）；**独立复核实测** native fallback 302→未声明主机 = **0 个请求** + 原因码 `LM009_REDIRECT_TARGET_NOT_ALLOWED`（**非**静默返回），且 native 307 同主机重定向**必须**被跟随（对照项） |
| LM-054 | 登记待评估 | P3 | — | 📌 信息项 | 市场索引拉取仍允许自动跳转（`PluginMarket._http`）：索引 URL 是**用户配置的固定目标**，其内容驱动的下载仍受 LM-007 的逐跳白名单 + 路径前缀 + sha256 三重校验 ⇒ 风险有界，本批不修 |
| LM-055 | 登记待评估 | P3 | — | 📌 信息项 | `CityCodeResolver` 远程回退 handler 未关自动重定向：目标是**代码内建常量** `JSON_DB_URL`，插件与用户都改不动 ⇒ 与 LM-053 的"用户可控目标"不同级，本批不修 |
| LM-056 | 本批已修 | P2 | M4 | ✅ **已闭环** | 发布流水线的防假绿阈值与 CI **不一致**（`release.yml` 写着 1000、`ci.yml` 写着 1281，注释却声称"完全一致"）⇒ 删掉 281 条用例时 CI 会红、**发布流水线却会绿**；且 `release.yml` 少传 `-MaxSkipped` / `-MustExecuteTestClass` 两道门。已同步并补齐两道门；阈值随后按**被推送分支实测总数**校正为 **1349**（CI `39836128969` = success） |
| LM-057 | 独立复核产出 | P2 | M4 | 📌 登记待评估 | 逐跳主机比对**忽略端口** ⇒ 同主机不同端口的重定向被放行（声明主机 `api.example.com` 的插件，凭据可被 302 到 `api.example.com:8443`）。**既有行为非本批引入**（`.NET` 自身语义即只比主机不比端口），但 LM-045 把该口径继承到了逐跳复核上 ⇒ 真实口径缺口。修它属**收紧**，须单独评估兼容性（§4.5.10） |
| LM-058 | 独立复核产出 | P3 | M4 | 📌 登记待清理 | IPv6 字面量主机在两处被**不同地**截断（`AddDeclaredHost` 取第一个冒号 ⇒ `"["`；`StripPort` 取最后一个冒号 ⇒ `"[:"`）⇒ IPv6 目标恒不可用。**偏严 / fail-closed**，不漏放行（§4.5.10） |
| LM-059 | 独立复核产出 | P3 | — | 📌 登记知悉 | 既有 public 记录 `RenderingRouteRequest` 的**主位置签名**被追加成员 ⇒ 二进制不兼容；构造侧源码兼容，全仓仅 1 处生产调用点（§4.5.10） |
| ENV-003 | S4.1 | P1 | M4 | ✅ 已闭环 | `a8fb2fc`：跳过 7→1；CI 门禁收紧（`MAX_SKIPPED_TESTS: 1` + `MUST_EXECUTE_TEST_CLASS`） |
| ENV-004 | S4.1 | P2 | M4 | ⏳ `BLOCKED_ENV` | 联网 E2E 单独运行，**不进 PR 门禁**；**不计入通过**（§9） |

> **发布准入现状（§9 对当前状态的判定）：`NO_RELEASE`。** 条件 4 不满足（LM-015/LM-017 延后 ⇒ 正式签名未就绪）、条件 7 不满足（LM-015/LM-017 仍为未关闭 P1）、条件 8 不满足（发布顺序未执行）。**合并到 master 与发布是两件事**：合并（快进、零冲突）可进行，发布不可。

---

## 8. 已知基线（用于判断"未下降"）

| 指标 | 数值 | 复现命令 | 核实状态 |
|---|---|---|---|
| 主测试工程（**初始基线**） | 通过 983 / 失败 0 / 跳过 7 / 共 990 | `dotnet test tests/LiteMonitor.Tests -c Debug` | 已实测，连续两次一致 |
| 主测试工程（**CI 口径实测** @`f4f3b2a`） | 通过 **1316** / 失败 0 / 跳过 **1** / 共 **1317** | 设 `LITEMONITOR_PLUGIN_REPO=tests/fixtures/plugin-repo` 后 `dotnet test LiteMonitor.sln -c Debug` | 本轮实测（owner A 物化 + 裁决 D-G + LM-045 + GPU T04 + GPU T05 + owner A 接线落地后）；跳过 1 = 唯一剩余联网 E2E（ENV-004） |
| 主测试工程（**本地口径实测** @`f4f3b2a`） | 通过 **1310** / 失败 0 / 跳过 **7** / 共 **1317** | **不设** `LITEMONITOR_PLUGIN_REPO` 的同一条命令 | 本轮实测；与 CI 口径的**用例总数相同**（三工程合计同为 **1349**），差值恰为 6 条市场契约用例（ENV-003，`MarketIndexContractTests`） |
| 更新器测试工程 | 通过 **58** / 失败 0 / 跳过 0 / 共 **58** | `dotnet test tests/LiteMonitor.Updater.Tests -c Debug` | 已实测（S4.2 扩展后由 13 增至 58） |
| GPU 非性能测试工程 | 通过 **19** / 失败 0 / 跳过 0 / 共 **19** | `dotnet test tests/LiteMonitor.GpuNonperformance.Tests -c Debug` | 已实测 |
| **三工程合计（当前）** | **1394**（1317 + 58 + 19） | `dotnet test LiteMonitor.sln -c Debug -v minimal` | CI 口径实测（`LITEMONITOR_PLUGIN_REPO` 指向夹具），退出码 **0**；门禁脚本实测「发现 1349 条用例（通过 1348 / 失败 0 / 跳过 1）」、退出码 **0**。CI 门禁 `MIN_TEST_TOTAL` 已同步收紧 1281 → **1349**（= **被推送分支** `61465ba` 的实测总数，**不是**集成分支的数字） |
| 主测试源码文件 | 顶层 **74** 个 / 含子目录 **80** 个 `.cs` | `Get-ChildItem tests/LiteMonitor.Tests -Filter *.cs -Recurse` | 已实测（原台账「64 个」已过时） |
| 更新器 / GPU 测试源码文件 | 更新器 顶层 3 / 递归 9；GPU 顶层 0 / 递归 6 | 同上 | 已实测 |
| 解决方案测试发现 | **1394**（CI 口径） | `dotnet test LiteMonitor.sln` | **不再是 0** —— BASE-003 已闭环（`0366268` 三个测试工程入 `.sln`）；`assert-test-counts.ps1` 把「发现 0」变红 |
| 两点 / 三点分支差异 | 484 / 522，共同祖先 `169e90a2…` | `git diff --name-only master branch` / `master...branch` | 已实测（BASE-002 起点；**本轮未重测**） |
| Release 构建 | **成功** | `dotnet build LiteMonitor.sln -c Release` | 6 次 CI 全部 success（CI 内含 `dotnet build LiteMonitor.sln -c Release`，`.github/workflows/ci.yml:159`）。本地曾被运行中的 `LiteMonitor++.exe` 锁住输出目录（ENV-001，**非代码缺陷**，实例已自行退出） |
| 远端 GitHub Actions | 本轮 **1 次 failure → 修正后连续 2 次 success** | `gh run list --repo mojingjing-git/LiteMonitorPlus --limit 8` | `39844039100` @`9e384e0` = **success**（最新，含 GPU T03）；`39836128969` @`61465ba` = **success**；`39834352321` @`b1a8b42` = **failure**（根因：阈值按**集成分支**实测 1316 设置，而被推送的 `b1a8b42` 实测仅 **1295** ⇒ 门禁**正确**报红，**不是**门禁缺陷）；`39829176306` @`23a6c5c` = success |
| 远端 Release | **仍为 0**（本批不发布） | `gh release list` | 已实测；发布准入见 §9 |
| 本地 HEAD vs 远端 `origin/fix/audit-remediation-v3` | 本轮推送后**齐平**（`9e384e0`） | `git log --oneline origin/fix/audit-remediation-v3..HEAD` | 已实测；`master` 本地与远端恒为 `23a6c5c`（**本批未合并**；合并须等本批收敛 + CI 绿 + 独立复核通过） |

**基线纪律：** 任何步骤完成后，上述**当前实测值**（CI 口径：主工程 共 **1317**（通过 **1316** / 跳过 **1**），更新器 通过 **58**，GPU 非性能 通过 **19**，三工程合计 **1394**）**不得下降**；主工程**总数只增不减**。下降即为回归，必须在同一步骤内修复，不得记为"已知问题"后放行。CI 侧的对应门禁为 `MIN_TEST_TOTAL: 1394`、`MAX_SKIPPED_TESTS: 1`、`MUST_EXECUTE_TEST_CLASS: MarketIndexContractTests (≥6)`（`.github/workflows/ci.yml`）—— 跳过数**有上限**、契约类**不许被跳过**，两者均为 S4.1 引入的收紧。

> **本行的重测义务（本轮已履行）：** 上表数字测于**被推送分支 `f4f3b2a`**（owner A 物化 + 裁决 D-G + LM-045 + GPU T04 + GPU T05 + owner A 接线 + GPU T03 + 裁决 D-H 落地）。`MIN_TEST_TOTAL` 已同步上调至 **1394**，**等于**该分支的实测总数。★ **阈值纪律**（本轮实测教训）：阈值**必须**取「**被推送分支**」的实测总数，**不得**取集成分支的数字，也**不得**为求绿下调阈值 —— `39834352321` @`b1a8b42` 就是把集成分支的 1316 写进了只含 `b1a8b42` 的分支（该分支实测仅 **1295**）⇒ 门禁**正确**报红。
>
> **本地口径 vs CI 口径的差异（已解释，非回归）：** 不设 `LITEMONITOR_PLUGIN_REPO` 时，主工程为 1317 / 通过 1310 / 跳过 **7**；设了（指向 `tests/fixtures/plugin-repo`）后跳过降为 **1**，通过升为 **1316** ⇒ 差值恰为 **6** 条市场契约用例（ENV-003），与 §4.2.8 完全吻合。**两口径的用例总数相同（三工程合计同为 1394）**，只有环境门控造成的跳过数不同。

---

## 9. 发布准入

以下任一成立 → `NO_RELEASE`：

1. 全新检出无法复现构建与测试基线；
2. 关键测试未实际执行（用例数为 0，或存在早退冒充通过）；
3. Web 安全验收缺失（监听范围、Origin/Host、CORS、会话撤销、DOM 注入任一未 `VERIFIED_FIXED`）；
4. 正式签名未就绪，或签名一致性未在真实私钥下验证；
5. 最终包与受测源码不一致（含包内更新器非本次构建）；
6. 升级流程可能破坏用户数据或无法可靠恢复；
7. 存在未关闭的 P0 或 P1；
8. 发布顺序未满足"冻结→签名→独立验证→上传核验→公开→客户端可见"。

以下情形**允许继续开发但不允许发布**（`CONDITIONAL`）：仅剩 P2/P3 未关闭、M6 的部分场景 `NOT_VERIFIED`、M7 权限架构延期。

> `DEFERRED` / `NOT_VERIFIED` / `BLOCKED_ENV` **不计入**"已关闭"，也不满足发布准入。

---

## 10. 交付报告模板（每个步骤一份）

| 字段 | 要求 |
|---|---|
| 步骤 ID | 对应本计划步骤号 |
| 缺陷 ID | 本步骤覆盖的缺陷编号 |
| Agent | 实际执行者 |
| 缺陷结论 | `CONFIRMED` / `PARTIAL` / `NOT_A_DEFECT` / `DEFERRED` / `SUPERSEDED` |
| 验证状态 | `VERIFIED_FIXED` / `PARTIAL_FIXED` / `NOT_VERIFIED` / `BLOCKED_ENV` / `REGRESSION_FOUND` |
| 修改文件 | 完整相对路径 + 关键行号 |
| 根因 | 基于源码与复现证据 |
| 修改内容 | 具体到关键函数与行为变化 |
| 测试 | 用例名、命令、真实输出（**须含执行用例数**） |
| 回归风险 | 可能影响的旧功能 |
| 提交 | Commit SHA |
| 遗留问题 | 尚未消除或无法测试的情况 |

最终报告另需包含：**缺陷状态矩阵**（§7 加状态列）、**发布裁决**（§9）、**残留风险清单**。

---

## 11. 立即可执行的下一步

> **状态（2026-10-23）：本计划已进入实施阶段，不是待执行文档。** M0 / M1 / M2 / M3 四个门禁已全部归档（M1 PASS、M2 **PASS 并放行**、M3 `PASS_WITH_NOTES`），S0–S5.3b 各步骤均已产出提交并登记于 §4.4。以下为**当前**的收尾动作。

### 11.1 本批已完成的并行任务（2026-10-23）

| 编号 | 内容 | 隔离方式 | 产出物 | 结论 |
|---|---|---|---|---|
| **S3.5** | 修 **LM-039**（`native://` 绕过并发槽 / 2 MiB 上限 / 凭据目标校验 / 30 s 截止时刻）—— **P1，必须修**，直接违反 S3.2 明文验收条款 | 隔离 worktree `.artifacts/wt-lm039` | `d23e6fc`：`src/Plugins/PluginExecutor.cs`、`src/Plugins/Native/CryptoNative.cs`、`src/Plugins/Native/CityCodeResolver.cs`、新建 `tests/LiteMonitor.Tests/PluginNativeSandboxTests.cs` | ✅ 已闭环（8 条新用例 / 5 变异全捕获） |
| **S3.6** | 修 **LM-040~LM-044**（目标数累计未强制 / 500 ms 未钉住 / 请求数断言过弱 / 空转断言 / 30 s 生产入口未钉住） | 隔离 worktree `.artifacts/wt-lm040` | `d588440`：`src/Plugins/PluginProcessor.cs`、两个测试文件 | ✅ 已闭环（2 条新增 / 3 条期望反转） |
| **主审集成** | 裁决 **D-E**：把 S3.6 的进程级登记表换成实例级 `CreateScope` 缝隙，并补测 S3.6 未能实测的变体 | 隔离 worktree `.artifacts/wt-integ` | `6310dbf` | ✅ 3 变异全捕获 |
| **GPU T01** | HWND 重建 A/B 对照（仅停止 vs 重建）+ GDI 桌面像素观察 ⇒ 判定 `Control.RecreateHandle()` 是否可安全使用 | 主工作区（`tools/GpuHostLifecycleProbe/`） | `2c8915b`：探针工程 + `docs/superpowers/plans/2026-10-23-gpu-transition-t01-report.md` | ✅ **`RECREATE_HANDLE_SAFE`**（4 组独立运行一致） |
| **LM-045** | 修 **LM-045**（P1，重定向绕过"允许目标"导致凭据外泄与响应注入）+ **LM-053**（native 侧同构缺口，主审巡检扩围） | 隔离 worktree `.artifacts/wt-lm045` | `198a9d4`：`src/Plugins/PluginExecutor.cs`、`src/Plugins/Native/CryptoNative.cs`、`src/Plugins/PluginProcessor.cs`、新建 `tests/LiteMonitor.Tests/PluginRedirectSandboxTests.cs` + 测试服务器 | ✅ 已闭环（22 条新用例 / 7 变异全捕获；**独立复核 PASS_WITH_NOTES**） |
| **owner A 物化 + 裁决 D-G** | 物化 T02 合同序 1/2/3；纠正"两份句柄代次 holder" | 主审直接做（合同 §12.4：不能外包） | `e874e2a` + `28c6805` | ✅ 13 条用例 / 4 变异全捕获；**D-G**：唯一实例由类型自身持有 |
| **GPU T04** | 清理责任**转交** + `PresentationSession` R1–R6 + §6.4 退避 | 隔离 worktree `.artifacts/wt-gpu-t04` | `4ade856` | ✅ 14 条用例 / 5 变异全捕获（**GPU-001 根因**） |
| **GPU T05** | legacy 回退样式块 + 纯函数 resolver + 编辑器保留路径 | 隔离 worktree `.artifacts/wt-gpu-t05` | `07a1b03` | ✅ 13 条用例 / 4 变异全捕获 |
| **owner A 接线** | `IGpuStartupAttemptAbandon` 下移 + 保留 pending 的终结路径（GPU-005 / 裁决 D6） | 集成 worktree `.artifacts/wt-integ` | `03a925a` | ✅ 5 条用例 / 3 变异全捕获 |
| **GPU T03** | 切换协调器 + 受控重建 + HandleCreated 判别位（owner B） | 隔离 worktree `.artifacts/wt-gpu-t03` | `5a1eeb7` | ✅ 44 条用例；★ 主审巡检发现并纠正**移交事件未订阅**的阻断级缺口 |
| **主审裁决 D-H** | 回退样式 `Accent` 落点 = `ColorConfig.TextTitle` | 主审直接做 | `f4f3b2a` | ✅ 1 条钉住用例 + 变异自证（红 → SHA-256 一致 → 绿） |
| **CI 门禁** | 阈值与被推送分支实测总数对齐 | 主审直接做 | `b1a8b42` → `fe57544` | ✅ 1316 → **1349** → **1394**；`39834352321` 的一次红灯是**门禁正确报红**（阈值误用集成分支数字），非门禁缺陷 |

### 11.2 收尾动作序列（按序执行）

1. ✅ **合入 S3.5 / S3.6 / 主审集成**：由编排方在主工作区 `cherry-pick`（**不 rebase、不改写其历史**）；文件所有权不重叠，均干净合入。
2. ✅ **重跑全量回归（CI 口径）**：`LITEMONITOR_PLUGIN_REPO=$PWD\tests/fixtures/plugin-repo` + `dotnet test LiteMonitor.sln -c Debug` + `assert-test-counts.ps1` + `generate-dependency-manifest.ps1 -Mode Check` ⇒ **三道门禁退出码必须全 0**，且主工程**总数只增不减**。实测：**1280 / 1279 通过 / 0 失败 / 1 跳过**。
3. ✅ **上调 `MIN_TEST_TOTAL`**：`.github/workflows/ci.yml` 由 `1150` → **`1280`**（**等于**当前实测总数；门禁只收紧不放宽）。
4. ✅ **回写 §8 已知基线**与 **§7 追踪矩阵**（LM-039~044 转 `✅ 已闭环`），并在 §4.4 台账补上 S3.5 / S3.6 / 主审集成 / GPU T01 四行。
5. ✅ **独立 verifier 复核**（S3.5 + S3.6 + 主审集成的合并结果）—— **不得由实现者自审**；复核在隔离 worktree 内自做变异。**VERDICT: PASS_WITH_NOTES**（10 轮自设计变异全部变红；契约 6 常量未改；CI 阈值恰等于实测总数；LM-045 被独立实证为真实 P1；发现 LM-047~LM-052 共 6 项）。归档见 §4.3.5。
5b. ✅ **LM-047 修复**（`46f3f9d`）：字节上限在原语层夹到契约天花板（2 MiB 从"约定"变"强制"），新增护栏用例并以变异自证。
6. ✅ **推送** `fix/audit-remediation-v3` 到 origin（已授权）⇒ 本轮最新 `cf1c8ee`；CI `39844039100` @`9e384e0` = **success**（`39836128969` @`61465ba` 亦 success）。★ **阈值纪律**：阈值**必须**等于「**被推送分支**」的实测总数，**不得**取集成分支的数字，也**不得**为求绿下调 —— `39834352321` @`b1a8b42` 即是把集成分支的 1316 写进了只含 `b1a8b42` 的分支（该分支实测仅 **1295**）⇒ 门禁**正确**报红。
7. ⏳ **master 合并评估**：**本批尚未合并到 master**（`master` 仍为 `23a6c5c`）。合并的前置条件是「本批收敛 + CI 全绿 + 独立复核通过」—— 前两项已达成，**第三项（GPU 部分独立复核）进行中** ⇒ 合并**暂缓**，等复核结论落地再评估。
8. ✅ **GPU T01 结论落地**：判定 **`RECREATE_HANDLE_SAFE`** ⇒ 走 GPU 计划 §4.1 主线，**不启用** §9 备用路线；判别位重建分支**保留**，`RecreateWindow` **采用**。回填 GPU 计划 §12.7。
9. ✅ **GPU C 期（T03–T08）启动条件**已齐备并**已启动**：T01 路线结论落地 **且** `src/Rendering/**` 哈希**全程未变**（T01 已实测）**且** M2/M3 门禁确认 ⇒ T03 / T04 / T05 已落地；**T06–T08 与实机验收（G01–G12）仍未完成**。
10. ✅ **LM-045（重定向绕过允许目标，P1）已修并经独立复核闭环**：`198a9d4`（HTTP 步骤路径 + `native://` 路径**两侧**都禁自动重定向 + 逐跳复核目标主机）；**范围由主审集成巡检扩大**（native 侧同构缺口 ⇒ 新登记 LM-053，随本提交一并修掉）。独立复核 **PASS_WITH_NOTES**（§4.5.10）。
11. ⏳ **GPU 部分独立复核**（T03 / T04 / T05 + owner A 接线 + 裁决 D-H）：进行中。★ **过程留痕**：复核方第一轮把基线选到**已被取代的中间提交** `aafe949`（与最终提交 `a498583` 同父兄弟，缺 ledger 订阅与 resolver 接线）⇒ 由主审纠正后改用主分支 HEAD 重做；旧基线上的结论**作废**，详见复核报告。

### 11.3 需要人工到场 / 外部确认的事项（**不可由 agent 代替**）

| 事项 | 原因 | 状态 |
|---|---|---|
| **M6 GPU 与硬件实机验收 G01–G12** | G05 / G09 需**管理员**；**G08（不同 DPI 屏幕）环境本机不存在**（两屏均 96 dpi） | 全部未跑；G08 记 `BLOCKED` + 人工步骤，**不得用夹具冒充** |
| **真实浏览器跨源读取 / 端到端登录与 WS 握手** | 无浏览器驱动 | 2 条 `BLOCKED_ENV`，**不计入通过** |
| **法律三项复核** | `amd64/msdia140.dll`（VS EULA 再分发条款）、`Mono.Posix.NETStandard 1.0.0`（nuspec 无 SPDX 表达式）、`HidSharp 2.6.4`（`type="file"`，Apache-2.0 由正文识别） | 已如实登记进 NOTICE，**未替主人下结论** |
| **S5.1 / S5.2 真实签名** | 用户已定延后 | 需明确授权后才启动；未启动 ⇒ 发布准入 `NO_RELEASE` |
| **S7 权限架构（LM-021）** | 计划默认延期 | 待授权 |

**注意：** 任何"PASS"都必须附带 §10 的完整字段与真实命令输出。`DEFERRED` / `NOT_VERIFIED` / `BLOCKED_ENV` **一律不计入**"已关闭"。
