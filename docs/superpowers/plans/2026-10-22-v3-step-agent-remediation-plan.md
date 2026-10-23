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
| GPU-001 | WDA 恢复失败后 GPU 呈现**永久不可重启**（进程生命周期内不可用） | P1 | ✅ **已闭环**（独立复核 `PASS_WITH_NOTES`） | GPU T03/T04：`4ade856` + `5a1eeb7` + `48de1a8`（**实机端到端属 T07/T09 ⇒ `NOT_VERIFIED`**） |
| GPU-002 | `PresentationSession._originalAffinity` 跨 HWND 代次不可复用 | P2 | ✅ **已闭环**（独立复核 `PASS_WITH_NOTES`） | GPU T02/T04：`4ade856`（R1–R6） |
| GPU-003 | 鼠标穿透关闭后 `WS_EX_LAYERED` 永久残留，与 WDA 互斥 | P2 | 🟡 **已实现，待实机** | GPU T02/T03：`5a1eeb7`（只读 layered 探针）+ T01 实测 `RECREATE_HANDLE_SAFE`（**合同 §14 第 3 项仍需实机 ⇒ `NOT_VERIFIED`**） |
| GPU-004 | `IsWindow` 无法区分「旧 HWND 已销毁」与「句柄值被复用」 | P2 | ✅ **已闭环**（独立复核 `PASS_WITH_NOTES`） | GPU T04：`4ade856`（代次判据先于 `IsWindow`）+ `5a1eeb7`（删除第二个计数器）+ `48de1a8`（护栏盲区修复） |
| GPU-005 | `GpuStartupGuard` 的 pending 语义比计划 §4.4 描述更弱 | P2 | ✅ **已闭环**（独立复核 `PASS_WITH_NOTES`） | GPU T02 + owner A 接线：`03a925a`（`AbandonPreservingPending`）+ `4ade856`（`ReleaseStartupAttempt` 衔接） |
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
| LM-060 | `FpsStateTests.需求抖动不会击穿退避窗口` 是**时序敏感**用例（内部用真实时间 `WaitUntil(..., StateWaitMs)` 等待）⇒ 在满负载（`--no-incremental` 全量构建 + 全量测试并行）下**偶发超时变红** | P3 | `CONFIRMED`（**GPU T03 变异自证过程中实测到 1 次**，§4.4 GPU T03 行；**LM-090 第三轮独立复核再次独立再现**：6 次全量测试中 1 次红，发生在紧跟 `--no-incremental` 全量重建（负载最高）那轮，随后立即复跑 + 3 轮 flake-hunt **共 5 次全绿** ⇒ 非确定性） | 登记待清理：**与本批改动无关**（不触碰 `FpsCounter` / FPS 退避任何代码；同一提交其余 9 轮"还原后全量"全绿、最终独立复跑全量亦全绿）⇒ 属**既有 flaky 用例**。修它需把真实时间等待改为**假时钟 / 注入式等待** ⇒ 属他人文件（`tests/LiteMonitor.Tests/FpsStateTests.cs`），本批不动。★ 第三轮复核补充的嫌疑名单：`HttpLifecycleTests.cs:165/244/299`（`sw.Elapsed < 10s` 断言）、`FpsStateTests.cs:217/362/750/928`；**那一次失败的用例名未被捕获**（复核方脚本只留汇总行），⇒ 复现时需先补捕获用例名 |
| LM-061 | **实现者护栏 `EnumerateMethods` 不枚举实例构造函数** ⇒ D-G 唯一性护栏在**构造函数路径上不承重**（`GetMethods` 不返回实例 `.ctor`，`TypeInitializer` 只是静态 `.cctor`） | P2 | `CONFIRMED`（**GPU 独立复核用变异 M7 实测**，§4.5.12） | ✅ **已修复**（`48de1a8`）：`EnumerateMethods` 增枚举 `GetConstructors(flags)`；新增 2 条钉住用例；**主审变异自证**：宿主构造函数里 `new WindowHandleGeneration()` ⇒ 红 ⇒ 字节还原 SHA-256 一致 ⇒ 绿 47/47。**注**：当前生产代码本身**没有**第二实例 ⇒ 本条是**护栏可靠性**问题 |
| LM-062 | 既有 public 方法 `NotifyGpuHandleChanged()` 的**无参重载被删除且未保留**（`src/UI/UIController.cs:325`） | P3 | `CONFIRMED`（**GPU 独立复核实测**，§4.5.12） | 登记知悉（**主审裁决 D-I**，§4.5.12）：内部 WinForms 类、非对外库 API、全仓仅 1 处调用点且已同步更新；补一个转发到 `(false, gen)` 的重载会**改变原语义**（原方法尝试拉起 GPU，新签名下 `controlled=false` 明确禁止）⇒ **不修**，保留复核方建议备查 |
| LM-063 | `finally` 内 `_captureCleanup.Add(...)` 排在 `_gpuPresentation = null;` **之前** ⇒ 理论上登记异常会跳过置 null | P3 | `CONFIRMED`（**GPU 独立复核指出**，§4.5.12；当前 `Add` 无抛出路径 ⇒ **实际不可达**） | ✅ **已修复**（`48de1a8`）：改为**嵌套 `try`/`finally`**，置 null 位于**最内层** `finally` 体内；新增可执行 IL 护栏；**主审变异自证**：恢复单层 `finally` ⇒ 红 ⇒ 字节还原 SHA-256 一致（还原后 SHA 与修复前原值完全相同）⇒ 绿 47/47 |
| LM-064 | ledger `Pump` 预填 `Attempts=2` 并覆盖 `LastErrorCode` ⇒ ledger 与 policy 两份实现存在语义漂移窗口 | P3 | `CONFIRMED`（**GPU 独立复核观察**，§4.5.12；生产移交路径恒为 2 ⇒ **实际不触发**） | 登记，不修（属**诊断保真观察**，抽取显式常量属**美化**而非修复 ⇒ 禁止无关重构） |
| LM-065 | **R01 · 启动阻断**：`MainForm` 构造期 `_winHelper` 的赋值**晚于** `new UIController(...)`，而 `UIController..ctor → ApplyTheme → ApplyGpuRenderingRoute → MainForm.IsWindowLayered()` 会解引用它 ⇒ **主程序启动即抛 `NullReferenceException`**（与主题/GPU 开关/安全模式**全部无关**） | **P1** | `CONFIRMED`（**主审独立复核 + 实机复现**：探针 `mainform` 场景产出 `.artifacts/r01-repro/mainform-construction-error.log`，堆栈 `MainForm.IsWindowLayered → UIController.ApplyGpuRenderingRoute → ApplyTheme → UIController..ctor → MainForm..ctor`） | ✅ **已修复**（`a157c1b`）：`_winHelper` 赋值前移到 `new UIController` 之前（`MainFormWinHelper` 构造只存窗体引用，无依赖）。**新增 4 条 IL 护栏**（顺序 / 读前必赋值 / 必须转发给助手 / 必须实时读句柄）。**主审变异自证**（`.artifacts/r01-mutate.ps1`）：变异 ⇒ 红 1/4（偏移 304 vs 248）⇒ 字节还原 SHA-256 一致 ⇒ 绿 4/4 ⇒ `RESULT=PASS`；**启动检查负向自证**（`.artifacts/r01-startup-mutate.ps1`）：变异 ⇒ 退出码 1 + `NullReferenceException@IsWindowLayered` ⇒ 字节还原 ⇒ 退出码 0 ⇒ `RESULT=PASS`。**并新增生产主窗体启动检查场景**（探针 `--scenario=startup`）并接入 `ci.yml` |
| LM-066 | **R02 · GPU 故障回退**：运行故障/启动失败路径只做 `StopGpuPresentation` + `Invalidate`（`UIController.cs:1575`、`GpuPresentationHostController.OnRenderFailure`），**不进入** `RunGpuToLegacyTransition` ⇒ 与"手动关闭 GPU"的受控 HWND 重建路径**不对称** | **P1** | `CONFIRMED`（**主审逐行复核**：唯一调用点 `UIController.cs:1206` 在 `ApplyGpuRenderingRoute` 内，故障路径完全绕开） | ✅ **已修复**（`9e3ba32`）：新增纯 CLR `src/Rendering/Presentation/GpuFailureFallbackScheduler.cs`（阶段/报告/禁止上下文/重入保护/先到先得合并）。**宿主只上报、不做窗口操作**（新增 `PresentationFailed` 事件，宿主不认识 `UIController` ⇒ 结构上不可能在 GPU 帧栈内自启动转换）；UI 侧 `ScheduleGpuFailureFallback` 只「记录根因 + 记录本代次呈现历史 + 入队」，由 **UI 安全点** `PumpGpuFailureFallback`（Tick，在 GPU 重启兜底**之前**）执行，调用**与手动路径同一个** `RunGpuToLegacyTransition`（合同 §4 步 2-9）。**失败原因保留**：首次失败原因只写一次，后续 affinity 恢复失败等清理诊断**不得顶替**；协调器 `RecordPresentationRootCause` 先到先得；恢复预算耗尽**不再顶替**已记录根因（另存诊断通道）。**重建判据上移**到协调器 `RequiresControlledRecreate()` ⇒ 两条路径共用同一判据（对称性成为结构事实）。删除了启动失败分支的 `Invalidate` 与"只标记 Stable"。**新增 30 条用例**（23 方法 + 7 Theory）。**worker 变异自证**：M1 退回只 `Invalidate` / M2 转换移出 UI 安全点 / M3 失败原因被吞 / M4 恢复耗尽顶替根因 ⇒ **4 条全部在最终代码上重跑**（worker 自查首轮变异后又改过代码 ⇒ 主动全部重验），逐条 SHA-256 == `D1CC7038…` / `136C73C4…` / `89A66CDA…` / `E3D64B6B…` / `D7A32AF6…` 字节级还原 |
| LM-067 | **R03 · 网页口令策略**：运行中清除口令只清哈希+盐+明文，而 `ApplyWebServer`（`AppActions.cs:255`）**只比较明文**口令 ⇒ 判定"无需重启" ⇒ LAN 监听继续；而 `HandleLoginAsync`（`WebSessionManager.cs:1283`）读**单例** `Settings.Load()` 且 `:1290` `HasWebPasswordConfigured()` 为假 ⇒ 直接签发 **L0 无口令主会话** | **P1** | `CONFIRMED`（**主审逐行复核**：单例语义 `Settings.cs:311-314`；`Settings.cs:485`） | ✅ **已修复**（`3494fbc`）：新增纯函数 `WebServerAccessPolicy.IsLanAccessForbidden(cfg, runtimeLanExposed)` —— **冷启动与运行中变更共用同一条判定**（结构上保证运行中绕不过冷启动约束）；`ApplyWebServer` 改用**有效凭据指纹**（`Settings.GetWebPasswordFingerprint()`）与 `LiteWebServer.CurrentCredentialFingerprint` 比较（明文在迁移后恒为空串，拿明文比永远发现不了变更）；「局域网暴露 + 无有效凭据」⇒ **停止服务**并返回确定结果 `RejectedLanWithoutPassword`；停止/重启连带撤销全部会话/票据/WS（`WebSessionManager.Stop()`）。**新增 6 条用例**（含"端口上确实不再有人监听"的事实断言）。**worker 变异自证**：M1 改回只比明文（红 2）/ M2 LAN 无口令放行（红 4）/ M3 去 WS 撤销（红 1）/ M4 去会话撤销（红 1，既有 `N8` 命中）⇒ 逐条 SHA-256 一致后还原。**★ 主审更正了 worker 的前提**：设置页操作的是**草稿**配置（`SettingsForm.cs:221` `page.SetContext(_draftCfg, ...)`），变更在「应用」时才进入 live 单例 ⇒ 精确触发是「**清除口令 + 应用**」，而非"清除即生效" |
| LM-068 | **R04 · 插件恢复日志路径边界**：恢复日志的 `Target`/`Stage`/`Backup` 直接进 `Path.Combine`（`PluginMarket.cs:965-967`）⇒ 绝对路径会**顶掉**插件目录、`..` 会**越出**目录；随后 `SafeDelete`（`:978`/`:979`/`:1001`）与 `File.Move(..., overwrite: true)`（`:997`）⇒ 任意文件删除/覆盖 | **P1** | `CONFIRMED`（**主审逐行复核**；触发前提为**可篡改本地事务日志**，非远程可利用） | ✅ **已修复**（`c835a59`）：**被执行的路径永远来自重建值，日志字段只做比对** —— 按【插件 id + 事务 id】重建三个**直接子文件名**（`<id>.json` / `<id>.json.<txid>.stage` / `<id>.json.<txid>.bak`），三层校验任一不过即**整笔拒绝**：① 形状（不得含目录分隔符 / 绝对路径 / `.` / `..`）；② 一致性（三个字段必须与重建值**逐字节**相同；事务 id 必须 32 位十六进制）；③ 边界（重建值解析后父目录必须**就是** `pluginsDir`，且叶子不得是重解析点 —— 读不到属性时 fail-closed）。`Rollback` 走同一套校验 ⇒ 实际落盘路径在结构上限于插件目录的直接子文件。**坏日志隔离留证**：原始内容逐字节追加到 `PluginsDir/.market-install-txn.quarantine.log`（**不以 `.json` 结尾** ⇒ `CONFIG_EXT="*.json"` 不会扫到），被拒条目不再留在活动日志、其中路径一个都不执行。**新增 25 条用例**（含目录联接的重解析点检测，**无需管理员权限**；正常事务恢复后目录清单逐字节比对）。**worker 变异自证**：M1 去掉边界校验（红 19/25，且剩余 6 条恰为"正常事务行为不变"组）/ M2 拒绝改放行（红 15/25）/ M3 重建改回信任日志字段（红 1/25，精确命中不变式用例）⇒ 逐条 SHA-256 == `2cc67b99…` 字节级还原。**★ 残余风险如实登记**：重解析点检查与实际删除/移动之间存在 **TOCTOU 窗口**（同目录写权限的攻击者可在窗口内换目标）⇒ 本实现是**纵深防御层**，主防线是"路径只可能是重建出的直接子文件名" |
| LM-069 | **R05 · 旧客户端升级桥接缺失**：基线旧客户端用**单参数**启动更新器并 `Application.Exit()`，而新更新器（`LiteMonitor.Updater/Program.cs:128`）**有意拒绝**单参数（退出码 6）⇒ 旧客户端装上新更新器后**静默失败**、用户只剩旧版本且反复重试 | **P1** | `CONFIRMED`（**主审核对基线 `0f1177e` 的 `UpdateDialog` 实测**：单参数 + `Process.Start` 后立即 `Application.Exit()`） | ✅ **已修复**（`0363141`）：**失败可见化** —— 更新器单参数被拒时写出机器可读标记 `update_legacy_bridge.json`（含 `schema`/`code`/`reason`/`guidance`/`manualDownloadUrl`，**无任何私钥材料**），主程序启动时（**排在任何网络请求之前**）读取并弹窗「自动升级通道与当前版本不兼容，请手动下载新版本」，读完即**消费**（不重复弹）。**未撤销任何完整性校验**：单参数仍被拒（退出码仍为 6），默认**不联网**。另建**可注入**的 `ITrustedDigestSource`（`NullTrustedDigestSource` 默认永远取不到 ⇒ 行为不变；`SignedVersionMetadataDigestSource` = 方案 (B) 骨架，用**与旧客户端同一把**内嵌公钥验签 `version.json`）。**新增 26 条用例**（更新器 15 + 主程序 11）。**worker 变异自证**：M1 删标记写出（红 4）/ M2 摘要不匹配改放行（红 3）/ M3 来源跳过验签（红 1）/ M4 不消费标记（红 2）⇒ 逐条 SHA-256 一致后还原。**★ 方案 (A) 经论证不可实现**：更新器要校验的正是它自己所在的包 ⇒ 需构造 SHA-256 **不动点**（包内清单含自身摘要 ⇒ 摘要依赖清单每个字节）⇒ 代价 ~2^256 次求逆；编进更新器自身则是同一循环。**方案对比与推荐见 `docs/plans/2026-10-23-updater-legacy-bridge-design.md`**；(B) 何时开启**待主人拍板** |
| LM-070 | R06 · 原生旧主题被错误套用 GPU 回退配色 | P2 | `CONFIRMED`（**P2 批次 A 主审独立复核**：GPU 路线下换到**无 `rendering` 块**的原生旧主题 ⇒ `LegacyFallbackStyleResolver` 返回中性默认值（`:56-61`/`:135`）⇒ `BuildDerivedTheme`（`RenderingTransitionCoordinator.cs:813-831`）**无条件**覆盖 6 个色槽（`:819-826`）⇒ 旧主题自身配色**整组**丢失；后续换肤经 `RefreshLegacyFallbackTheme` **继续**重算覆盖，直到重回 GPU 或重启） | 批次 A 待修（`src/UI/UIController.cs` + `src/UI/RenderingTransitionCoordinator.cs` + `src/Rendering/LegacyFallbackStyleResolver.cs` + `RenderingTransitionCoordinatorTests.cs`）；★ **无测试覆盖**；★ 难点：须区分"原生旧主题"与"GPU 主题被迫回退"（后者**应当**用 `fallbackStyle`）⇒ 建议**加分支**而非改 `BuildDerivedTheme` 语义（否则既有回退样式用例会变红） |
| LM-071 | R07 · 派生主题只复制 public 属性，漏掉 `Theme` 的 `FontTitle/FontGroup/FontItem/FontValue` **字段** ⇒ 运行期字体/字号/高 DPI 缩放丢失 | P2 | `CONFIRMED`（**批次 A 主审独立复核**：`DeriveConfig`（`RenderingTransitionCoordinator.cs:836-850`）用 `GetProperties` 只取**公开实例属性** ⇒ 只拷**属性**；而 `ThemeManager.cs:370-374` 的 5 个运行期字体是**字段**（`public Font FontTitle = ...`）⇒ 派生主题字体恒为 `SystemFonts.CaptionFont`；`derived.Font`（`FontConfig` **属性**）被正确复制 ⇒ 只读配置的代码看不出异常） | 批次 A 待修（+ `src/Core/ThemeManager.cs` / `src/UI/UIRenderer.cs` / `RenderingTransitionCoordinatorTests.cs`）；★ **无测试覆盖**；★ 难点：**字体所有权** —— 派生主题若**共享**源主题字体对象，换肤时 `oldTheme.DisposeFonts()` 会释放当前主题字体（悬垂引用）；若**新建**则须定释放方且不能二次 `Scale`；`DeriveConfig` 在纯 CLR 类内拿不到 DPI×用户缩放系数 ⇒ 可能要改**内部**签名（非 public API） |
| LM-072 | R08 · 受控重建后未按配置恢复旧 M2 实时玻璃（`LiveGlassController` 只 `RefreshRequest`，服务为 null 即返回） | P2 | `CONFIRMED`（**批次 A 主审独立复核**：`OnHandleDestroyed` → `StopLiveGlass`（`UIController.cs:320`）→ `LiveGlassController.SetEnabled(false)`（`:190-205`）→ `StopService()`（`:267-284`）⇒ `_service = null`（`:272`）；重建后 `RefreshRequest`（`:351-354`）`svc is null` 即 return；而 `SetEnabled(true)` 全仓**只有**构造期（`UIController.cs:162`）与用户手动（`:297`）两处 ⇒ **无人按配置恢复** ⇒ 设置页复选框仍显示"开"而状态行显示"未启用"（UI 自相矛盾）） | 批次 A 待修（+ `src/UI/MainForm_Transparent.cs` / `LiveGlassLifecycleTests.cs`）；★ **无测试覆盖**（既有 4 条只覆盖租约交接/位置变化/几何暂停/请求身份）；★ 难点：`SetEnabled` 有 `Enabled==enabled` **早退** ⇒ "临时停机"必须**不改** `Enabled` 标志，否则恢复时被早退跳过；恢复须等 legacy 就绪后再启（避免与 GPU 实时捕获并行抓屏，违反单一捕获所有者约束）；新增用例须走 STA |
| LM-073 | R09 · `TryReconfigure` 取得 WDA 所有权时**绕过** pending 清理检查（只有 `Start` 查 pending） | P2 | `CONFIRMED`（**批次 A 主审独立复核**，并**扩大范围**）：`CapturePolicyCleanupPendingProbe` 全库**只在 `Start`（`GpuPresentationHostController.cs:282`）被检查**；`TryReconfigure`（`:513`）的 `SetCapturePolicy`（`:554`/`:566`）**无**检查 ⇒ 同一 HWND 同代次的 pending 清理责任被绕过，新会话把残留 `0x11` 当作"外部原值" ⇒ `_affinityChangedBySession` 保持 **false** ⇒ **不承担恢复责任**；★ **主审新增发现（报告未提）**：`Resume`（`:474`）与 `NotifyOutputChanged`（`:695`）也是取 WDA 入口，**同样无检查** ⇒ 修复须覆盖**三个**入口 | 批次 A 待修（+ `src/UI/UIController.cs` / `GpuPresentationTransitionTests.cs` / `RenderingTransitionCoordinatorTests.cs`）；★ **全库无任何 `TryReconfigure` 用例**；★ 难点：`TryReconfigure` 是 **public** ⇒ 加参数即改**公共签名**；更稳的是内部复用已注入的探针（不改签名）；拒绝时的错误码需裁决 |
| LM-074 | R10 · 恢复重试耗尽被记为 `Failed` 后 `HasPendingForGeneration` 返回 false ⇒ 同 HWND 可再取新 WDA 所有权，原策略却未恢复 | P2 | `CONFIRMED`（**批次 A 主审独立复核**：`Pump` 耗尽后 `TryMarkFailed("exhausted")`（`CapturePolicyCleanupLedger.cs:190`）⇒ `IsTerminal`（`RenderingTransitionState.cs:156-157`，**含 `Failed`**）⇒ `HasPendingForGeneration`（`:125-136`）的 `!entry.IsTerminal` 判据不再命中 ⇒ **同代次耗尽后无任何解除入口** ⇒ INV-6 闸门放行新所有者，但原策略（通常 WDA_NONE）**从未恢复** ⇒ `0x11` 残留至 HWND 销毁 ⇒ 窗口在录屏/截图里持续不可见） | 批次 A 待修（+ `src/Rendering/Configuration/RenderingTransitionState.cs` / `RenderingTransitionCoordinatorTests.cs`）；★ 既有测试只断言 `State==Failed` / `TerminalReason=="exhausted"`，**未断言**耗尽后 `HasPendingForGeneration` 的行为 ⇒ 该不变式**无覆盖**；★ 难点：改 `HasPendingForGeneration` 会放大 `Start` 的拦截面；**合同 §6.3"终态不可回退"与"同代次 Failed 必须阻止新所有者"存在张力 ⇒ 需主审裁决是否属契约修订** |
| LM-075 | R11 · 修改监听范围（Loopback/LAN）不重绑服务；界面"实际监听地址"读**草稿** `BindMode` 而非真实端点 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 B，提交 `7ef594c` + `f66cb0b`）：修法要点与证据见 **§4.12** |
| LM-076 | R12 · WS 连接只保存服务代次、**不绑定会话** ⇒ 注销/过期后已建立的 WS 继续收到数据 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 B，提交 `dc6488a`）：修法要点与证据见 **§4.12** |
| LM-077 | R13 · 口令迁移 `EnsureWebPasswordMigrated` 调 `Save` 后直接返回 true，而 `Save` 在禁止保存时**直接返回**、遇 IO 错误**吞异常** ⇒ 旧明文可能仍在磁盘 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 B，提交 `d8f3661`）：修法要点与证据见 **§4.12** |
| LM-078 | R14 · 请求合并键只有请求摘要，**不含允许目标/凭据策略** ⇒ 不同白名单的调用者可能复用他人的响应 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 B，提交 `dd50295`）：修法要点与证据见 **§4.12** |
| LM-079 | R15 · `StripPort`（`PluginExecutor.cs:847`）对 IPv6 字面量按**最后一个冒号**截断 ⇒ `[fd00::1]` 与 `[fd00::2]` 都化成 `[fd00:` ⇒ **地址碰撞** | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11；★ 与 LM-058 是**同一类缺陷的另一处**，LM-058 当时判为"偏严方向"，**本项方向相反 ⇒ fail-open，更严重**） | ✅ **已修复并集成**（P2 批次 B，提交 `703083f`）：修法要点与证据见 **§4.12** |
| LM-080 | R16 · `AddDeclaredHost`（`PluginProcessor.cs:421`）只按 `/` 截 authority，**不处理 `?`/`#`** ⇒ 无路径 URL 的查询串被当作主机名 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 C，提交 `628dfcd`）：修法要点与证据见 **§4.13** |
| LM-081 | R17 · 手动重定向按状态码**无条件**把方法设为 GET（`PluginExecutor.cs:1006`）⇒ HEAD 语义丢失 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 C，提交 `63c5e0a` + `cb33cfd`(R4) + `9d36f1a`(R5)）：修法要点与证据见 **§4.13** |
| LM-082 | R18 · 原生 Crypto 请求按**实例 ID** 而非**执行作用域**合并；旧任务未收尾即被新执行复用 ⇒ 跨代次取消相互影响 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 C，提交 `4de5db9`）：修法要点与证据见 **§4.13** |
| LM-083 | R19 · 插件事务恢复晚于首次加载与调度 ⇒ 磁盘回滚了但**内存仍运行新版本** | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 D，提交 `cc9ca91`）：修法要点与证据见 **§4.13** |
| LM-084 | R20 · 市场改用 `ResponseHeadersRead` 后，30 秒超时只约束到**响应头**；正文读取用调用方 `ct` ⇒ "头成功、正文停滞"可无限等待 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 D，提交 `d77c074`）：修法要点与证据见 **§4.13** |
| LM-085 | R21 · 旧配置首存时主文件被加密，但 `AtomicWrite` 把旧主文件**原样**复制成 `settings.json.bak` ⇒ 备份里仍有插件密钥**明文** | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 D，提交 `8a44f07`）：修法要点与证据见 **§4.13** |
| LM-086 | R22 · T01 探针 `PumpScene` 在 `Task.Run` 内调用需 UI 线程的 `UpdateScene` ⇒ 抛 `InvalidOperationException` 且任务未等待 ⇒ 持续送帧失败未被报告 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 E，提交 `c70fc33`）：修法要点与证据见 **§4.13** |
| LM-087 | R23 · 真实 MainForm 隔离门在 `OnShown` 启动服务**之前**检查 ⇒ 生产 `OnShown` 开启监听的副作用未被约束 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 E，提交 `61d37c7`）：修法要点与证据见 **§4.13** |
| LM-088 | R24 · 探针找不到源码时 `guard` 为 null，但 `unchanged` 初值为 true ⇒ 仍输出"源码未漂移" | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 E，提交 `79252c4`）：修法要点与证据见 **§4.13** |
| LM-089 | R25 · `release.yml` **没有**接入 `generate-dependency-manifest.ps1 -Mode Check` 门禁 ⇒ 依赖漏列时 CI 会红而**发布流水线仍会**构建/签名/上传草稿 | P2 | `CONFIRMED`（**主审逐条回源码复核：成立**，2026-10-23；证据见 §4.11） | ✅ **已修复并集成**（P2 批次 E，提交 `b5d80fe`）：修法要点与证据见 **§4.13** |
| **LM-090** | **启动检查 CI 门禁假绿**：探针 `GpuHostLifecycleProbe.csproj` 是 `<OutputType>WinExe</OutputType>`（GUI 子系统）⇒ PowerShell 的 `&` 对 GUI 程序**不等待**且**不更新** `$LASTEXITCODE`；而门禁前一步恰是 `dotnet build`（把 `$LASTEXITCODE` 置 **0**）⇒ `$code` 恒为 0 ⇒ `if ($code -ne 0)` **永不触发** ⇒ **任何主窗体构造异常都被判绿** | **P1** | `CONFIRMED`（**独立复核者 LM-065~LM-069 复核产出**；**主审独立复现**：见 §4.6.6） | ✅ **已修复**（`1a94378`）：两份工作流改用 `Start-Process -PassThru` + `WaitForExit(180000)` 取**真实**退出码（超时 ⇒ 判失败）。**主审负向自证**（直接执行**工作流里的那段脚本本身**，非副本）：基线退出码 0（耗时 3912 ms，真的在等）⇒ 变异（R01 空引用放回）退出码 **1 并打印真实 NRE 堆栈** ⇒ 字节还原 SHA-256 `3C84A169…` 逐位一致 ⇒ 退出码 0。**待独立复核**（第二轮已派发） |
| **LM-091** | CI 用例发现数阈值**低于**实测总数（`ci.yml` / `release.yml` 均 1396，实测 1487），且 `ci.yml` 注释里"阈值 == 当前实测总数（删用例会变红）"的声明**不成立**（删 91 条仍绿）；分解里的 `58` 已过时（Updater.Tests 现为 73） | P3 | `CONFIRMED`（**独立复核者在其基线 `9e3ba32` 上实测**；主审在 `176e78e` 已自行修正并被复核者确认吻合） | ✅ **已修复**（`176e78e`）：`ci.yml` 与 `release.yml` 两处 `MIN_TEST_TOTAL` 同步上调至 **1487**（= 被推送分支 `43538bb` 的实测总数），注释与分解同步更新；两处**逐字一致**（主审复核确认） |
| **LM-092** | 启动检查门禁的 `Start-Process -ArgumentList` **不给含空格的元素加引号** ⇒ `GITHUB_WORKSPACE` 含空格时输出目录被拆成多个 argv（探针报 `Unknown argument 'with'.`，退出码 **64**）⇒ 门禁**误报红**（fail-closed，非假绿）；且失败信息误称"MainForm 构造期抛异常" | P3 | `CONFIRMED`（**LM-090 第二轮独立复核产出**；**主审独立复现**：本机 PS 7.6.6 上实测 —— 数组直接给含空格路径 ⇒ 退出码 **64**；手工加引号 ⇒ 退出码 **0** 且产物落在正确路径） | ✅ **已修复**（`3cb9e00`）：`-ArgumentList @('--scenario=startup', ('"{0}"' -f $out))` 手工加引号（Windows 路径不含 `"` ⇒ 安全）；失败信息改为列出**退出码契约**（0=构造成功 / 1=构造期抛异常 / 2=环境不具备 / 64=参数错误）。**主审四组合自证**（执行工作流抽取出的真脚本）：健康×无空格 = **0** / 健康×含空格 = **0** / 变异×无空格 = **1** / 变异×含空格 = **1**；还原后 SHA-256 `3C84A169…` 逐位一致。★ GitHub 托管 runner（`D:\a\<repo>\<repo>`）无空格 ⇒ **默认不触发**；自托管 runner / 本地含空格路径会命中 |
| **LM-093** | **LM-074 返工带出的次生缺陷（假告警）**：`UIController.StopGpuPresentation` 的**双路登记**（事件回调 `OnGpuCapturePolicyCleanupRequired` + `LastCapturePolicyCleanup` 兜底）里，事件回调已把记录登记成功，兜底路径再 `Add` **同一实例** ⇒ 被 `CapturePolicyCleanupLedger.Add` 的 INV-4 检查拒绝 ⇒ LM-074 新增的留痕逻辑把这次**拒绝**一律记成 `gpu_capture_policy_cleanup_registration_rejected` ⇒ **每一次正常移交都误报**；且 `CleanupWarningCode` 是**单槽** ⇒ 误报会挤掉 `gpu_capture_policy_cleanup_superseded` 这类真正需要人看的告警 | P3 | `CONFIRMED`（**主审复核 LM-074 返工 diff 时发现**，源码级推演已闭合：`HandOffCapturePolicyCleanup` 置 `LastCapturePolicyCleanup` 后**不清空** ⇒ 兜底必然二次登记；`Add` 的 INV-4 判据 `existing.Generation == cleanup.Generation && !existing.IsTerminal` 对**同一实例**同样命中） | ✅ **已修复**（`50f8fb5`）：修在 ledger 层 —— `Add` 的判定顺序改为「**先**判同一实例是否已在册 ⇒ 幂等返回 `true`（不触发任何被拒诊断）→ 再判终态 → 最后才是 INV-4（**不同**条目撞同代次才算真被拒，才写 `RegistrationRejectedCode`）」。★ INV-4 的不变量是"同一代次至多一条 `Pending`" ⇒ 重复登记**同一条**不违反它；`Add` 的返回值语义改为"这条责任**已在册**"。★ 顺带修正 `UIController.StopGpuPresentation` XML 文档里"双路并存安全 —— `Add` 对同一代次幂等"的错误说法（`Add` 实际并不幂等，旧代码只是忽略了 `false`）。**批次 A 独立复核判定 `PASS`**（6 条自写用例 + M4 变异） |

| **LM-094** | **`AddDeclaredHost` 的端口截断对 IPv6 字面量同样不成立**：`PluginProcessor.AddDeclaredHost`（`:431-432`）用 `authority.IndexOf(':')` 截端口 ⇒ 形如 `[fd00::1]` 的 IPv6 字面量被截成 **`"[fd00"`** ⇒ 声明主机集合里是一枚**畸形串** ⇒ 该 IPv6 主机永远无法被正确声明（带凭据请求被一律拒绝） | P3 | `CONFIRMED`（**主审复核 LM-079 时发现**：与 LM-079 同族但**不同处** —— LM-079 是三处 `StripPort` 按**最后一个冒号**截断，本项是 `AddDeclaredHost` 按**第一个冒号**截断；★ 方向**偏严**（不会误放行，只是 IPv6 声明不可用）） | ✅ **已修复并集成**（P2 批次 C，提交 `a2302b1`）：修法要点与证据见 **§4.13**（★ 与 LM-080 同函数不同语句，两项均已改且互不干扰；与 LM-079 修好的 `PluginSandbox.StripPort` **IPv6 语义一致**） |

| **LM-095** | **受控重建失败时实时玻璃停在"暂停"，且状态行的"自动恢复"承诺落空**：LM-072 把句柄销毁改成"临时停机"（保留 `Enabled`），但**按配置恢复的唯一调用点**是 `UIController.CompleteLegacyTransition` 末尾的 `_liveGlass?.RestoreIfEnabled()`；而受控重建**失败**时 `RunGpuToLegacyTransition` 在 `FailHandleRecreate()` 后直接 `return` ⇒ `CompleteLegacyTransition` 不执行 ⇒ `RestoreIfEnabled()` 永不调用 ⇒ 服务保持停止，而状态行仍显示"实时玻璃已暂停（窗口句柄重建中），**重建完成后自动恢复**。"—— 重建已失败，"自动恢复"永远不发生 | P2 | `CONFIRMED`（**批次 A 独立复核产出**，主审回源码复核成立：`src/UI/UIController.cs:1611-1618` 早退 + `:1727-1730` 唯一恢复调用点 + `src/UI/LiveGlassController.cs:151-154` 虚假承诺文案） | ✅ **已修复**（本轮）：新增 `LiveGlassController.HandleRecreateFailed(handleAlive)`（句柄仍在 ⇒ 按配置恢复；句柄已失效 ⇒ 保持停止并如实说明"重建失败，需手动重开"）；`UIController` 失败分支改为调用它并读 `MainForm.IsClosing`（关闭流程中不得拉起捕获，合同 §4 / U10）；`MainForm` 新增 `internal bool IsClosing`。**新增 4 条用例**（3 条行为 + 1 条 IL 接线护栏）；**主审变异自证** MA1/MA2/MA3 **3/3 全杀**且 SHA-256 逐位一致还原 |
| **LM-096** | **同代次"明确恢复"的重建次数无上限** ⇒ 单 HWND 累计 affinity 写入次数可**远超**"6 + 最多 6"：`TryRearmForExplicitRecovery` 每次都从同代次里挑**第一条** `Failed && AffinityChangedBySession` 的条目重建新责任，**不检查该条目是否已被 `ReclaimedByExplicitRecovery` 标记** ⇒ 同一条旧责任可被反复接管，每轮新建一条拥有独立 6 次预算的新责任 | P3 | `CONFIRMED`（**批次 A 独立复核产出**，主审回源码复核成立：`src/UI/CapturePolicyCleanupLedger.cs:277-299` 的挑选条件不含 `ReclaimedByExplicitRecovery`；⇒ **主审 §4.9 原记录的"6 + 最多 6"口径是错的**，已在 §4.9 更正） | ⏸️ **裁决：不改代码**（见 §4.10 主审裁决）。理由：① 重复写入的是**同一个** affinity 值，实质危害远低于"原策略永不恢复"；② 若加"每代次重建 ≤ 1"封顶，会把 R10 的实质危害（原策略始终没被写回 ⇒ 窗口在录屏/截图里持续不可见）从"有机会恢复"变成"必然不恢复" ⇒ **净风险更大**；③ 真正的缺陷是**台账口径**（已更正）。`_entries` 只增不减的缓慢增长作为**已知残留**记档 |
| **LM-097** | **`TryRearmForExplicitRecovery` 的 XML 文档与代码相反（埋雷）**：文档仍写"旧条目……终结为 `Completed("rearmed_by_explicit_recovery")`"，而代码**保持旧条目为 `Failed`/`exhausted`**、只打 `ReclaimedByExplicitRecovery` 标记 ⇒ 后来者按文档"对齐"代码会把"原策略**始终没有被写回**"这一事实抹掉 ⇒ 闸门误判"已恢复" ⇒ **R10 的危害原样复活** | P3 | `CONFIRMED`（**批次 A 独立复核产出**，主审回源码复核成立：`src/UI/CapturePolicyCleanupLedger.cs:263-265`（文档）vs `:331-334`（代码）；这正是主审 §4.9 第 1 条"不实记账"问题的**文档侧残留**） | ✅ **已修复**（本轮）：XML 文档改为与 `ReclaimedByExplicitRecovery` 说明一致（"旧条目**保持** `Failed`/`exhausted`（如实记账：原策略始终没被写回），只打接管标记；仍然终态、不再重试、不再触碰 HWND"），并**显式写明"不得改成 `Completed`"及其后果**。纯文档改动，行为不变（⇒ 本项**不产生**测试用例，以台账核对确认；这是它与 LM-095 的区别） |
| **LM-098** | **口令迁移成功后**内存里的明文仍保留** ⇒ 每次登录重复写盘，且「迁移早已完成」时的一次写盘失败会**永久拒绝登录**：`WebPasswordJsonConverter`（`Settings.cs:858`）只在**写盘时**把 `WebServerPassword` 写成 `""`，**不修改内存字段**（`Settings.cs:196-197`）⇒ 迁移成功后 `Settings.WebServerPassword` 仍非空 ⇒ 登录路径 `WebSessionManager.cs:1469` 的 `!string.IsNullOrEmpty(cfg.WebServerPassword)` 恒为真 ⇒ **每次登录都重跑一次迁移（重新落盘）**；且此时若配置目录不可写，`EnsureWebPasswordMigrated`（`Settings.cs:553`）如实返回 `false` ⇒ 登录被拒 500 —— 尽管**磁盘上早已没有明文** | P3 | `CONFIRMED`（**主审复核 LM-077 时发现**，回源码核实：`Settings.cs:196-197`（字段带 converter）+ `:858`（converter 写盘置空）+ `:553`（迁移据写盘结果判定）+ `SettingsHelper.cs:196-198`（`TrySave`）+ `WebSessionManager.cs:1469`（登录路径前置条件）） | ⏸️ **登记为已知残留，本轮不改**（理由见 §4.12.7）：① 重复写盘是**幂等**的（写出的内容与上次一致），对低频登录无实际影响；② 「迁移早已完成仍被拒」属于 **fail-closed 的过度收紧**，**不产生凭据泄露**；③ 正确修法（迁移成功后清空内存明文）会改变 `WebServerPassword` 的内存语义，影响面超出 LM-077 的修复边界 ⇒ 另立批次处理 |

**当前无 P0。** 若任一执行步骤新发现 P0，立即挂起其他任务优先处理。

**编号规则：** `LM-` 缺陷 / `SEC-` 安全 / `BASE-` 基线 / `ENV-` 环境事实 / `GPU-` GPU 切换专项。第三方评审使用的 `LM-018`、`LM-019` 与 `LM-003b`、`LM-003c` 指同一处代码，已合并并保留别名以便交叉引用；`LM-005/006` 为同一协议缺陷的两个侧面。

> **编号占用现状（2026-10-23 第十三次核对）**：已占用 `LM-001`–`LM-021`、`LM-023`、`LM-024`、`LM-030`–`LM-098`。`LM-022` 与 `LM-025`–`LM-029` 为**从未使用的空号**（经 `Select-String` 全文检索 + `git log --grep` 双向确认），保持空置不复用。**新增缺陷从 `LM-099` 起顺延，不得复用已关闭编号**（`LM-045`–`LM-052` 于上一轮占用，`LM-053`–`LM-056` 于本轮占用，`LM-057`–`LM-059` 由 LM-045 独立复核产出，`LM-060` 由 GPU T03 变异自证过程实测产出，`LM-061`–`LM-064` 由 GPU 独立复核产出，**`LM-065`–`LM-089` 由 2026-10-23 独立审查报告产出**，对应报告编号 R01–R25；**`LM-090`–`LM-091` 由 LM-065~LM-069 第一轮独立复核产出**，`LM-090` 由第二轮复核追加确认；**`LM-092` 由 LM-090 第二轮独立复核产出**；**`LM-093` 由主审复核 LM-074 返工 diff 时发现**；**`LM-094` 由主审复核 LM-079 时发现**；**`LM-095`–`LM-097` 由批次 A 独立复核产出**；**`LM-098` 由主审复核 LM-077 时发现**）。 GPU 专项独立编号 `GPU-001`–`GPU-005`（`GPU-0xx` 此前未占用）。

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
| **GPU T03** | `5a1eeb7` + `f4f3b2a` | **切换协调器 + 受控重建 + 判别位**（owner B）：新增 `src/UI/RenderingTransitionCoordinator.cs`（**状态与判据的唯一落点**：期望/有效路由、切换阶段、三重身份、HWND 呈现历史、清理状态、诊断字段、HandleCreated 判别位）与 `src/UI/CapturePolicyCleanupLedger.cs`（**未完成 affinity 清理责任的独立持有者**，合同 §6.2 INV-1~INV-6）；GPU→legacy 固定 **9 步**（`Decide` → 窗口状态快照 → 停呈现/转交清理 → 受控 HWND 重建 → 代次推进 → 布局/DPI 恢复 → 窗口状态恢复 → 一次有效重绘 → 发布稳定）；判别位规则 ①~④（受控 vs 构造期首次创建 / 取消归零 / 陈旧残留归零 / 5 秒超时兜底）；**清理责任永不通过宿主引用表达**（`StopGpuPresentation` **无条件**置 null）；`LiveGlassController` 的**第二个句柄代次计数器已删除**（⇒ 消除上一轮 verifier 报告的 F7）；回退样式经 `LegacyFallbackStyleBridge` 生成**派生主题副本**（不改 `theme.Color`、不改 `ThemeManager.Current`、不写盘）。44 条用例（worker）+ **主审补 1 条钉住裁决 D-H 的用例**（共 45 条，变异自证：删掉 accent 落点 ⇒ 红 ⇒ 字节级还原 SHA-256 一致 ⇒ 绿）。★ worker 自证 **10 个变异全被杀**、10/10 还原 SHA-256 逐字节一致（其中 9/10 轮"还原后全量"绿；M5 那轮出现 1 条**无关**的既有 flaky 用例 `FpsStateTests.需求抖动不会击穿退避窗口` 偶发红 ⇒ 已登记 **LM-060**，该用例属他人文件、本批未改）。★ **自证暴露并修复了一处真实护栏缺陷**：第一轮 **M9（恢复"保留引用"分支）未被杀死** —— 根因是 IL 护栏只识别**长格式**分支 opcode（`Br`/`Brfalse`/`Brtrue`/`Beq`/`Bne_Un`），而编译器对近距离跳转发射**短格式** `brtrue.s`/`brfalse.s`/`br.s` ⇒ 护栏漏判；修复为"**条件**分支（含短格式）不得跳过最后一次置 null"并**排除无条件 `br`**（"宿主本就为 null ⇒ 早退"路径，跳过置 null 属良性）后，M9 被杀死且还原后全量绿。★ 顺带修复了 `src/UI/UIController.cs` 注释里 1 个 **U+FFFD 乱码字符**（字节层 `EF BF BD EF BF BD` → `E5 A6 82`）。★ **主审集成巡检发现并纠正的缺口（阻断级）**：worker 首版**未订阅** T04 的 `CapturePolicyCleanupRequired` 移交事件、**从未**调用 `ledger.Add` ⇒ ledger 永远 `State=None` ⇒ 清理责任**彻底丢失**（旧 HWND 永远保持 WDA 排除，比 GPU-001 更糟）；且 `StopGpuPresentation` 仍是 T04 落地前的旧口径（"移交出口尚未接入 ⇒ 保留引用"）⇒ **死代码 + 掩盖缺口**。主审发回后补上「订阅 + `LastCapturePolicyCleanup` 兜底」双路（`Add` 同代次幂等）+ 统一工厂 `CreateGpuPresentationHost`（三个创建点订阅不可能漏）+ INV-6 探针 `CapturePolicyCleanupPendingProbe` |
| **GPU 独立复核归档 + LM-061/063 修复** | `48de1a8` + `dd26b4c` | **GPU 独立复核（§4.5.12）**：verifier **自写 42 条独立用例**（不跑实现者用例）+ **自设计 14 条变异**（11 条为 NEW、不在实现者类别内）⇒ **14/14 全部被杀**（`ShaMatch` 14/14、`RedExit=1` 14/14、还原后 `GreenExit=0 & StillRed=0` 14/14）；A 块用**全程序集 IL 扫描 + `StopGpuPresentation` 分支目标分析**证明唯一订阅点 / 事件登记 / 兜底登记 / 无条件置 null / 无第二创建点全部成立。**结论 `PASS_WITH_NOTES`**，产出 LM-061~LM-064 与**裁决 D-I**。★ **主审据此修 LM-061（护栏盲区）**：实现者护栏 `EnumerateMethods` 只枚举方法、**不枚举实例构造函数** ⇒ "生产代码不得新建第二个句柄代次实例"这道 D-G 护栏**在构造函数路径上不承重**（`GetMethods` 不返回实例 `.ctor`，`TypeInitializer` 只是静态 `.cctor`）⇒ 增 `GetConstructors(flags)` 枚举 + 新增 2 条钉住用例。★ **并修 LM-063（finally 顺序）**：`StopGpuPresentation` 的 `finally` 内 `_captureCleanup.Add(...)` 排在 `_gpuPresentation = null;` **之前** ⇒ 理论上登记异常会跳过置 null ⇒ 改为**嵌套 `try`/`finally`**，置 null 置于**最内层** `finally` 体内。**主审变异自证**（`lm061-063-mutate.ps1`）：变异 A（宿主构造函数里 `new WindowHandleGeneration()`）⇒ 红 1/47 ⇒ 字节还原 SHA-256 一致 ⇒ 绿 47/47；变异 B（置 null 移回外层 `finally`，恢复单层）⇒ 红 1/47 ⇒ 字节还原 SHA-256 一致（还原后 SHA = `CB3CC174…`，与**修复前原始值完全相同**，证明变异精确还原旧代码）⇒ 绿 47/47 ⇒ `RESULT=PASS`。LM-062（`NotifyGpuHandleChanged()` 无参重载被删）与 LM-064（ledger `Pump` 预填 `Attempts=2`）按**裁决 D-I** 登记不修 |

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

#### 4.5.12 GPU 部分独立复核结论归档（T03 / T04 / T05 + owner A 接线 + 裁决 D-H）

**复核方式（严格独立）**：复核方在**自己新建**的隔离 worktree（`.artifacts/wt-verify-gpu`，分支 `verify/gpu-t03-t05`，**未提交未推送**）里**自写 42 条用例**（40 个 `[Fact]` + 1 个 `[Theory]` 的 3 个 `InlineData`），**自设计 14 条变异**（其中 **11 条属 NEW 类别**，与实现者已做类别不重复，远超"≥6 条、≥3 条不重复"的要求），每条走「改坏 → `dotnet build --no-incremental` → 红 → **字节级还原 + SHA-256 校验** → 强制重编译 → 复跑绿」闭环。**A 块不使用 grep 判定**，而是用**全程序集 IL 扫描**（找 `add_CapturePolicyCleanupRequired` / `CapturePolicyCleanupLedger.Add` / `set_CapturePolicyCleanupPendingProbe`）+ `StopGpuPresentation` 的**分支目标分析**（覆盖 `InlineBrTarget` / `ShortInlineBrTarget` / `InlineSwitch`）⇒ 证明置 null **不被任何条件分支跳过**且位于 `finally` 内。

**结论：`PASS_WITH_NOTES`**（无阻断性缺陷；遗留 1 条中等护栏盲区 + 3 条低危）。

**★ 过程留痕（基线选错，由主审纠正后重做）**：复核方第一轮把基线选到**已被取代的中间提交 `aafe949`**（T03 worker 首版，与最终提交 `a498583` **同父兄弟**；缺 ledger 订阅、缺 resolver 接线、`StopGpuPresentation` 仍是旧口径）⇒ 其 A 块结论**全部作废**；由主审发现并纠正后改用主分支 HEAD `7283b91` **重做全部验证**（A–H、42 条用例、14 条变异、门禁、依赖清单**无一复用旧结论**）。旧基线资产隔离备份至 `.artifacts/verify-backup-gpu`，**不混入**新基线证据目录。

**A–H 逐条结论（复核方实测）**：

| 块 | 结论 | 关键实测证据 |
|---|---|---|
| **A** GPU-001 清理责任闭环 | ✅ 成立 | 唯一订阅点 `src/UI/UIController.cs:478`（在唯一工厂 `CreateGpuPresentationHost` `:463-482` 内）；事件侧登记 `:489`→`:491`；兜底侧登记 `:518`→`:521`；**无条件置 null** `:525`（IL 分支目标分析确认无 `br`/`switch` 可跳过）；全程序集 `newobj GpuPresentationHostController` **仅 1 处** ⇒ 不存在漏订阅的第二创建点 |
| **B** 句柄代次唯一性（D-G） | ✅ 成立，**但实现者护栏有盲区**（LM-061） | 唯一实例 `WindowHandleGeneration.cs:29`；`SharedWindowHandleGeneration` 全仓零命中；`LiveGlassController` 只剩 `_sizeGeneration`；递增唯一点 `MainForm_Transparent.cs:332`（`HandleCreated` 第一行） |
| **C** 受控重建判别位 ①~④ | ✅ 成立 | 规则③ 陈旧兜底 `RenderingTransitionCoordinator.cs:215-220`；规则④ 5 秒超时 `:29` / `:283` |
| **D** 三重丢弃判据（U05） | ✅ 成立 | `RenderingTransitionCoordinator.cs:553-556` 三判据缺一不可 |
| **E** 诊断隔离 | ✅ 成立 | `gpu_fallback_style_invalid` 只进 diagnostics、不进 `Validate()` 错误列表；错误/警告/失败三路互不覆盖 |
| **F** 回退样式桥 + **D-H 落点** | ✅ 成立，裁决独立成立 | `RenderingTransitionCoordinator.cs:746` `colors.TextTitle = style.Accent;`；派生副本**不修改源主题**、不写 `ThemeManager.Current`、不落盘。**裁决 D-H 的独立佐证**：另发现 `src/Rendering/Materials/ThemeForegroundStyleFactory.cs:46` 已把 `theme.Color.BarLow` 映射为 `ForegroundTone.Accent`、而 `BarLow` 同时是 `BarSafe`（`:55`）⇒ **双重语义**，进一步佐证**不能**占用 `Bar*` 作 accent 槽 |
| **G** owner A 的 pending 处置 | ✅ 成立 | `GpuStartupGuard.cs:287-294` 只 `_attempts.Remove`、**不碰** `_pendingPath`、**不复位** `_attemptWritten`；`:355` `Interlocked.Exchange` 幂等；`GpuPresentationHostController.cs:893` `ReleaseStartupAttempt(stopClean)` → `:908` → `:924-929` |
| **H** 常量 / 退避表 / 唯一决策函数 | ✅ 成立 | 插件沙箱 6 常量**逐字未改**（`PluginProcessor.cs:447/450/453/456/459/462`）；退避 **6 次 + 250/500/1000/2000 ms** 两份实现一致（`CapturePolicyCleanupLedger.cs:33/:36-42` 与 `CapturePolicyCleanupPolicy.cs:51`）；`Decide` 仍是唯一决策函数；CI 与 release 阈值**均为 1396**（@`7a2ea69` 实测，两文件逐字一致） |

**变异汇总**：`ShaMatch=True` **14/14**；`BuildRed=0 & RedExit=1` **14/14**；`BuildGreen=0 & GreenExit=0 & StillRed=0` **14/14**。

**复核方报告的 4 条新缺陷与主审处置**：

| ID | 级别 | 摘要 | 主审处置 |
|---|---|---|---|
| **LM-061** | 🟡 中 | **实现者护栏 `EnumerateMethods` 不枚举实例构造函数** ⇒ "生产代码不得新建第二个句柄代次实例"这道 D-G 护栏在**构造函数路径上不承重**（复核方用变异 M7 实测：构造函数里 `new WindowHandleGeneration()` ⇒ **仅复核方自己的护栏变红**，实现者护栏保持绿）。根因：`GetMethods(DeclaredOnly)` **不返回**实例 `.ctor`，`TypeInitializer` 只是**静态** `.cctor`。**当前生产代码本身没有第二实例** ⇒ 是**护栏可靠性**问题，不是现有代码缺陷 | ✅ **已修**（`48de1a8`）：`EnumerateMethods` 增枚举 `type.GetConstructors(flags)`（只返回实例构造函数 ⇒ 静态类返回空，不与 `.cctor` 重复）；**并新增两条钉住用例**：`护栏_方法枚举必须覆盖实例构造函数`（直接钉住前提本身）与 `护栏_停止呈现必须把释放宿主引用放在最内层finally里`。**主审变异自证**：变异 A（宿主构造函数里 `new WindowHandleGeneration()`）⇒ **红 1 条**（实现者护栏现已承重）⇒ 字节还原 SHA-256 一致 ⇒ 绿 47/47 |
| **LM-062** | 🔵 低 | **既有 public 方法 `NotifyGpuHandleChanged()` 的无参重载被删除且未保留**（`src/UI/UIController.cs:325`）⇒ 与"既有 public 签名不得删除"口径不符 | 📌 **登记知悉，不修**（**主审裁决 D-I**）：`UIController` 是 WinForms 应用的**内部**类，其 `public` 只是 C# 默认可见性，**不是对外发布的库 API**；全仓生产调用点**仅 1 处**（`MainForm_Transparent.cs:347`）且已同步更新；**补一个转发到 `(false, gen)` 的无参重载反而有害** —— 原无参语义是"尝试拉起 GPU"，而 `controlled=false` 的新语义明确**禁止**拉起 ⇒ 为"兼容编译"引入一个**语义不同**的方法比删掉更危险。**保留复核方的建议备查**，将来若该类升格为对外 API 再补兼容重载 |
| **LM-063** | 🔵 低 | **`finally` 内 `_captureCleanup.Add(...)` 排在 `_gpuPresentation = null;` 之前** ⇒ 理论上 `Add` 抛异常会跳过置 null，与"停止呈现后宿主引用必须无条件为 null"的合同意图存在**理论缝隙**（当前 `Add` 无抛出路径 ⇒ **实际不可达**） | ✅ **已修**（`48de1a8`）：改为**嵌套 `try`/`finally`**，使"无条件释放宿主引用"位于**最内层** `finally` 体内 ⇒ 任何登记异常都**不可能**旁路它。**新增可执行护栏** `护栏_停止呈现必须把释放宿主引用放在最内层finally里`（判据：IL 里 `endfinally` ≥ 2 次，且最后一次 `stfld _gpuPresentation` 之后**仍有** `endfinally`）。**主审变异自证**：变异 B（把置 null 移回外层 `finally`，恢复单层）⇒ **红 1 条** ⇒ 字节还原 SHA-256 一致（还原后 SHA 与修复前原始值 `CB3CC174…` **完全相同**，证明变异精确还原了旧代码）⇒ 绿 47/47 |
| **LM-064** | 🔵 低 | **ledger `Pump` 预填 `Attempts=2` 并覆盖 `LastErrorCode`**（`CapturePolicyCleanupLedger.cs` 的 `while (cleanup.Attempts < 2)` 段）⇒ 生产移交路径恒为 `Attempts == 2` ⇒ **实际不触发**；但使 ledger 与 policy 两份实现的语义存在潜在漂移窗口 | 📌 **登记，不修**：属**诊断保真观察**，非功能缺陷；两份实现的"宿主停止时的首次 + 内联重试 = 2 次"口径本就来自合同 §6.4，抽取显式常量属**美化**而非修复 ⇒ 本批不做（禁止无关重构） |

**`NOT_VERIFIED` / `BLOCKED_ENV`（复核方如实标注，不计入通过）**：
- `BLOCKED_ENV`：① GPU 实机行为（真实 DXGI 呈现 / 真实 HWND 重建 / 真实 WDA 排除与恢复 / 异 DPI 显示器）⇒ 需真实 GPU + 双屏 + 管理员；② 计划 §7.2 的 **G01–G12** 实际窗口与画面矩阵。
- `NOT_VERIFIED`：① `WS_EX_LAYERED` 窗口上 `SetWindowDisplayAffinity` 是否**确实**失败（合同 §14 第 3 项）；② 重建后 `Form.Opacity` 分层 alpha 是否自动恢复（第 1 项）；③ `MakeWindowAssociation` 残留是否清除（第 2 项）；④ 清理预算 6 次 / 退避 250→2000 ms 是否覆盖**真实**窗口存活时间（第 5 项，本轮只验了退避表数值与重试逻辑）；⑤ "新代次独立取得所有权"在实机上无 WDA 副作用（第 8 项）；⑥ 继承 T01 报告的 **N1–N10**。
- **已验证且明确不列入未验**：合同 §14 第 10 项（`WindowIsLayered` 落在 `Decide` 的位置是否影响既有单测期望）—— 既有单测全绿，且变异 M8/M13/M14 仍能精准打红相关用例 ⇒ 该落点确实被覆盖。

---

## 4.6 独立审查报告的登记与处置（2026-10-23）

### 4.6.1 登记口径

2026-10-23 收到一份独立审查报告（`docs/reviews/2026-10-23-recent-changes-independent-review.md`，范围 `0f1177e → 3b81b59`，62 提交 / 103 文件），登记 **25 项：5 项 P1、20 项 P2**（报告编号 R01–R25）。

**主审的独立复核口径**（不采信报告结论，逐条回源码核对）：

| 范围 | 主审动作 | 台账状态 |
|---|---|---|
| **R01–R05（5 项 P1）** | **逐行回源码核对调用链与状态推导**；R01 另做**实机复现**（探针真实构造生产 `MainForm`）⇒ **5/5 全部成立** | `CONFIRMED` |
| **R06–R25（20 项 P2）** | ~~**未逐条复核**（本轮只做 P1 闭环）~~ → ✅ **已逐条复核**（2026-10-23，批次 A 5 项 + 批次 B~E 15 项，见 §4.8 与 §4.11） | `CONFIRMED`（**15/15 全部成立**；批次 A 的 LM-070~LM-074 已修复，批次 B~E 待修） |

> ★ **口径纪律**：报告是**外部输入**，不是证据。P1 的"成立"由主审**自己**读源码 + 自己的实机复现支撑；P2 只登记为"报告提出、待复核"，**不计入**任何"已修复"或"已确认"统计。

### 4.6.2 R01（LM-065）：主程序在审查所指的分支上**根本起不来**

**这是本轮最重要的发现，且它推翻了一个此前已经写进台账的结论。**

- **实证（修复前）**：`tools/GpuHostLifecycleProbe` 的 `mainform` 场景**真的构造生产 `MainForm`**，产出 `.artifacts/r01-repro/mainform-construction-error.log`：

  ```
  System.NullReferenceException: Object reference not set to an instance of an object.
     at LiteMonitor.MainForm.IsWindowLayered()
     at LiteMonitor.UIController.ApplyGpuRenderingRoute(Theme theme)
     at LiteMonitor.UIController.ApplyTheme(String name, Boolean retainData)
     at LiteMonitor.UIController..ctor(Settings cfg, Form form)
     at LiteMonitor.MainForm..ctor()
  ```

- **静态链路**：`MainForm..ctor` → `new UIController(_cfg, this)`（`_winHelper` 尚未赋值）→ `UIController..ctor` → `ApplyTheme(_cfg.Skin)` → `ApplyGpuRenderingRoute(t)`（**无条件**）→ 求值 `WindowIsLayered: MainForm.IsWindowLayered()` → `MainForm_Transparent.cs:89` `_winHelper.IsWindowLayered()` ⇒ **null 解引用**。
  与**主题、GPU 开关、安全模式全部无关**（该调用无条件求值，且发生在协调器自身的 catch 之前）。
- **引入点**：GPU T03 集成（`5a1eeb7`）新增了 `MainForm.IsWindowLayered()` 这条读路径；T01 的实机探针跑在 T03 **之前**，因此**从未**用真实启动验证过它。

**修复（`a157c1b`）**：把 `_winHelper = new MainFormWinHelper(this);` 前移到 `new UIController(...)` **之前**（`MainFormWinHelper` 构造只保存窗体引用，不触碰 HWND、不依赖 `_ui` ⇒ 提前赋值无副作用；`IsWindowLayered()` 本身在句柄未创建时安全返回 `false`）。

**两道自证**（脚本在 `.artifacts/`，未入库）：

| 自证 | 脚本 | 结果 |
|---|---|---|
| **IL 护栏能杀变异** | `r01-mutate.ps1` | 变异（把赋值还原到 `new UIController` 之后）⇒ **红 1/4**（`初始化顺序错误：_winHelper 的赋值在 IL 偏移 304，而 new UIController(...) 在偏移 248`）⇒ 字节还原 SHA-256 一致（`3C84A169…`）⇒ **绿 4/4** ⇒ `RESULT=PASS` |
| **启动检查能杀变异** | `r01-startup-mutate.ps1` | 变异 ⇒ **退出码 1** + `startup-error.log` 命中 `NullReferenceException@IsWindowLayered` ⇒ 字节还原 SHA-256 一致 ⇒ **退出码 0** ⇒ `RESULT=PASS` |

**新增护栏**（`tests/LiteMonitor.Tests/MainFormStartupOrderTests.cs`，4 条）：① 窗口助手赋值必须早于 UIController 构造；② 构造函数里每次读 `_winHelper` 前必须已赋值；③ 分层窗口查询**必须转发**给助手（禁止"直接返回 false"式假修 —— 那会静默破坏 GPU-003 的真实读数）；④ 助手的分层查询必须**实时**读句柄（禁止把 null 当"非 layered"永久缓存）。

### 4.6.3 新增 CI 门禁：**生产主窗体启动检查**

R01 一路穿过了全部 1396 条用例与**连续 8 次绿色 CI** ⇒ 证明"单测全绿"**不能**推出"主程序能启动"。

⇒ `ci.yml` 新增步骤「**生产主窗体启动检查（R01：真实构造 MainForm）**」：构建 `tools/GpuHostLifecycleProbe` → 运行 `--scenario=startup` → **退出码非 0 即流水线红**。该场景刻意**不跑消息循环**（不触发 `OnShown`）⇒ 网页服务不会开始监听；产物 `ci-artifacts/startup-check/**` 一并上传。

> ★ **本节的结论已被 §4.6.6 更正**：自 `ada4338` 起，这条门禁在 CI 上一直是**假绿**（WinExe 下 `&` 不更新 `$LASTEXITCODE`）⇒ "已在 runner 实测通过"当时**不成立**；已由 `1a94378` 改用真实退出码并重新取证。**教训**：新加的门禁除了"它能绿"，还必须证明"**它能红**"。

### 4.6.4 ★ 对此前结论的更正（留痕）

§8 与 §11 曾把 `0f1177e → 3b81b59` 记为"**本批收敛 + CI 全绿 + 独立复核通过**"。**该结论只对"组件层"成立，对"可运行"不成立**：主线程序在该区间**构造即崩**。

⇒ **教训（写进台账，避免再犯）**：**组件级单测与 IL 护栏无法覆盖"构造顺序/对象可用性"这一类缺陷**。凡是需要"对象在某个时刻已经可用"的契约，都必须补**真实启动**或**真实构造**的检查，不能用"调用存在"类护栏代替。

### 4.6.5 R05（LM-069）：发布策略 —— **主人已拍板：(C) 发布 + (B) 骨架默认关闭**

> **★ 主人拍板（2026-10-23，问卷 `ask_ae7194170252762a59d9246e`）**：**"现在按 (C) 发布 + (B) 骨架默认关闭（推荐）"** ⇒ 落到 **方案 (C)**：当前对外行为 = 旧客户端得到**明确的人工指引**（机器可读标记 + 新客户端启动提示"自动升级通道不兼容，请手动下载"），**不是**静默失败；**方案 (B) 的骨架保留但默认关闭**，等 **S5.1/S5.2 真实签名**获授权后再开。
>
> **该拍板与当前代码状态逐条吻合（主审已回源码核对，非推断）**：
> - **(B) 是"结构性关闭"，不是靠开关**：生产入口 `Program.cs:106` `Main(args) => RunFromArgs(args, AppContext.BaseDirectory)` ⇒ `options = null` ⇒ `Program.cs:163` `options?.TrustedDigestSource` 为 null ⇒ `TryGetSourcedDigest` 返回 null ⇒ 单参数调用仍落 `RejectMissingDigest`（**退出码 6，行为不变**）。`SignedVersionMetadataDigestSource` 在**生产路径上从未被构造**，只有测试通过 `UpdaterRunOptions` 注入。
> - **结构上不可能"悄悄联网"**：更新器工程（`LiteMonitor.Updater/*.cs`）**零** `System.Net` / `HttpClient` / `WebRequest` / `TcpClient` / `Socket` 引用 ⇒ 即便有人日后装配了 (B)，也没有任何抓取实现可用（`SignedVersionMetadataDigestSource` 的抓取委托必须由调用方注入）。
> - **未撤销任何完整性校验**：即便装配了来源，拿到的摘要**照样**要过 `RunUpdate` 里的比对（`Program.cs:150`）；来源取不到 / 抛异常 / 验签失败 **一律 null**（fail-closed，`TrustedDigestSource.cs`）。
> - **开启 (B) 时的代价**：只需给生产路径注入一个抓取 `version.json` 的委托 —— **一行开关，不需重写**（但仍受"真实签名"前置约束）。
>
> ★ 与 §11.3 的"R05 方案 (B) 何时开启"行对应：**已关闭，等 S5.1/S5.2 真实签名授权**。

主审核对基线 `0f1177e` 实测查明：旧客户端**本来就有 ECDSA 验签**（内嵌公钥 SPKI 指纹 `25E6659D…`），且 `UpdateContext.ExpectedSha256` **已经带着**来自**已验签** `version.json` 的摘要 —— **只是没有把它传给更新器**。因此存在**不削弱完整性**的桥接路径。三个候选方案（自引用循环论证、完整性影响、发布流程影响）见 `docs/plans/2026-10-23-updater-legacy-bridge-design.md`（由本轮 worker 产出）。

| 方案 | 信任锚 | 与旧客户端信任级别对比 | 前提 |
|---|---|---|---|
| (A) 过渡发布 + 钉住摘要 | 摘要钉在更新器二进制里 | **等于** | **自引用循环**（更新器在包内，钉住自己所在包的摘要不可行） |
| (B) 更新器自行验签 `version.json` | 内嵌 ECDSA 公钥 | **等于**（同一把钥匙） | 发布真的签名 ⇒ 与 **S5.1/S5.2 真实签名**绑定（当前**未授权**） |
| (C) 仅支持手动升级 | 无 | **降低**（旧客户端失去自动升级） | 无（可立即执行） |

**主审建议（已被主人采纳，见上方拍板）**：先落地"**失败可见**"部分（更新器写机器可读标记 + 新客户端启动时明确提示"自动升级通道不兼容，请手动下载"），该部分**与策略选择无关**且立即改善用户体验；桥接本体待 (A)/(B)/(C) 拍板后再开。**⇒ 拍板结果：按 (C) 发布 + (B) 骨架默认关闭。**

### 4.6.6 ★ LM-090：CI「生产主窗体启动检查」门禁是**假绿**（本轮自己踩的坑，留痕）

**发现者**：LM-065~LM-069 的**独立复核者**（不是主审自审）。

**根因**：探针工程 `tools/GpuHostLifecycleProbe/GpuHostLifecycleProbe.csproj:3` 是 `<OutputType>WinExe</OutputType>`（**GUI 子系统**）。PowerShell 用 `&` 调用 **GUI 程序**时**不等待**、**不更新** `$LASTEXITCODE`；而门禁步骤的前一行恰是 `dotnet build`（把 `$LASTEXITCODE` 置为 **0**）⇒ `$code = $LASTEXITCODE` 读到的**永远是 0** ⇒ `if ($code -ne 0)` **永不触发** ⇒ 门禁对**任何**主窗体构造异常都判绿。

**主审独立复现**（不采信复核者自述，脚本 `.artifacts/lm090-repro/`，**未入库**）：

1. **机制复现**（未变异）：`& $probe --scenario=startup $out` **174 ms 就返回**；`$LASTEXITCODE` 仍为 `0`（继承自 `dotnet build`）；**判定那一刻 `startup-ok.json` 与 `startup-error.log` 都不存在** ⇒ 门禁在探针**还没跑完**时就下了结论；探针的输出（`[ThemeManager] Loaded theme…`）是在**门禁判定之后**才打印的。真实退出码（`Start-Process` + `WaitForExit`）= 0。
2. **决定性证伪**（把 R01 的空引用放回去）：旧写法 **23 ms 返回 / `$LASTEXITCODE` = 0 / 判 PASS（假绿）**，而**真实退出码 = 1**，且 `startup-error.log` 里就是 `NullReferenceException @ MainForm.IsWindowLayered → ApplyGpuRenderingRoute → ApplyTheme → UIController..ctor → MainForm..ctor`。⇒ **假绿判定 = True**。
3. **修复后的负向自证**（`1a94378`）：**直接执行工作流里的那段脚本本身**（从 `ci.yml` / `release.yml` **抽取** `run:` 体，不抄副本）：
   - 两份工作流脚本体**逐字一致**（各 24 行）；
   - 基线退出码 **0**（耗时 **3912 ms** —— 真的在等，旧写法只要 23 ms）；
   - 变异（R01 空引用放回）退出码 **1**，脚本打印真实 NRE 堆栈 ⇒ 门禁**正确报红**；
   - 字节级还原，SHA-256 = `3C84A169…4038` 与基线**逐位一致** ⇒ 退出码 **0**。

**★ 对既往结论的更正（第二次留痕）**：§4.6.3 与 §8 曾把"生产主窗体启动检查已在 GitHub runner 实测通过（退出码 0）"当作 R01 回归防护**已生效**的证据。**该结论不成立** —— 自 `ada4338` 起，那条门禁在 CI 上一直是**假绿**：CI 日志里那句"生产主窗体启动检查退出码: 0"是**继承自 `dotnet build` 的 0**，不是探针的真实退出码。⇒ R01 的**修复本身**没问题（本地实机退出码 0、复核者独立实测退出码 0 均成立），但**CI 侧的门禁证据当时是无效的**。现已用 `1a94378` 换成真实退出码，并重新取得 CI 侧有效证据（§8）。

**★ 教训（写进台账）**：**"流水线绿"不等于"门禁有效"**。凡是用 `$LASTEXITCODE` 判定的地方，都必须确认被调用程序是**控制台**程序；GUI 子系统 exe 必须用 `Start-Process -PassThru` + `WaitForExit` 取退出码。凡是"新加的门禁"，除了"它能绿"，还必须证明"**它能红**"—— 变异自证是门禁本身的准入条件，不是可选检查。★ 另一条：**跨进程传参**要自己处理引号，不要假设 `Start-Process` 会帮你做（LM-092）。

**★ CI 侧重新取证**（修复后）：`39879199466` @`410192a` = **success**（18 步全 success）；「生产主窗体启动检查」步骤耗时 **21 秒**（00:40:18 → 00:40:39）—— 旧写法在本地只需 **23 ms** 就返回 ⇒ 这次是真的在等探针进程结束；日志里「生产主窗体启动检查退出码: 0」现在来自 `$proc.ExitCode`，是**真实**退出码。

**★ 第二轮独立复核（针对 `1a94378` 的修复）—— `PASS_WITH_NOTES`**：复核者**自选另一个变异锚点**（`src/UI/Helpers/MainFormWinHelper.cs` 构造函数抛异常，与主审锚点不同）⇒ 新门禁**红**（退出码 1，打印真实异常栈）/ 旧 `&` 写法**绿**（退出码 0，作假绿对照组）/ 字节还原 SHA `DC6DA7BD…` 逐位一致 / 还原后绿。**时序证据**（不依赖源码变异）：`&` 返回 **13–16 ms** 而探针真实耗时 **1001 ms** ⇒ 未等待；判定瞬间 `$LASTEXITCODE` = **0（陈旧）**、`startup-ok.json` **不存在**、探针进程**仍在跑**（晚 **1055 ms** 才结束）。**全量 `$LASTEXITCODE` 扫描**：两份工作流 + 被 CI 调用的 `scripts/*.ps1` 共 20 个读取点，生产者**全部是控制台程序**（dotnet/pwsh/git）；`&` 直接调 `.exe` 的行数 = **0** ⇒ **无第二处同类假绿**。**超时分支**：探针挂死 ⇒ 退出码 **1**、用时 **182430 ms** ⇒ 未误判为绿；`$probe` 指向不存在路径 ⇒ 退出码 **1**。**副作用**：探针基目录零增删改、TCP 监听数 67→67、`webServerRunning:false`。

**★ 复核者又挑出主审修复里的 1 项新缺陷（LM-092，P3）**：`Start-Process -ArgumentList` **不给含空格元素加引号** ⇒ `GITHUB_WORKSPACE` 含空格时输出路径被拆成多个 argv ⇒ 探针报 `Unknown argument`、退出码 **64** ⇒ 门禁**误报红**（fail-closed，**不是**假绿）。GitHub 托管 runner（`D:\a\<repo>\<repo>`）无空格 ⇒ **默认不触发**；自托管 runner / 本地含空格路径会命中。主审已复现并修（`3cb9e00`：手工加引号 + 失败信息列出退出码契约），四组合自证：健康×无空格 = 0 / 健康×含空格 = 0 / 变异×无空格 = 1 / 变异×含空格 = 1；还原后 SHA-256 `3C84A169…` 逐位一致。

**★ 第三轮独立复核（针对 `3cb9e00` 的 LM-092 修复）—— `PASS_WITH_NOTES`**：复核者**沿用自己的抽取器**（从 YAML 抽真脚本执行，不采信主审自证）。① 含空格工作区 ⇒ 退出码 **0**，且**截断路径精确判定为 False**（`<ws>\space` / `<ws>\dir` 均不存在）、`probe-args-error.log` 数量 **0**（第二轮的老毛病消失）。② 两份工作流抽取脚本 SHA-256 同为 `D5387A67…D9F8E`，与 YAML `run:` 块各 **28 行逐字相同**。③ 负向仍成立：变异×含空格×ci / 变异×无空格×ci / 变异×含空格×release **全红（1）**，`startup-error.log` 两种路径都落在**完整路径**；字节还原 SHA `DC6DA7BD…` 逐位一致；还原后含空格/无空格 = 0 / 0。④ **引号处理无新风险**：`--scenario=startup` **未被**引号包裹（静态 False / 动态探针记录 `scenario = startup`）；实参不含 `"`、不以 `\` 结尾（`Join-Path` 自带归一化）；反斜杠未被吃掉/双重转义；补充测 `ws & dir` / `ws (dir)` / `ws,dir;dir` ⇒ 退出码 **0/0/0**。⑤ 强制探针返回 64 ⇒ 脚本退出码 1，信息「…退出码 64…**64=参数错误**」与源码契约（`RealMainFormScenario.cs:178-179`、`Program.cs:109`、`Program.cs:124`）一致。

**★ 第三轮附带观察（归并到 LM-060，不另占编号）**：6 次本地全量测试中 **1 次红**（LiteMonitor.Tests 1438 通过 / 1 失败 / 1 跳过），发生在紧跟 `--no-incremental` 全量重建（负载最高）那轮；随后立即复跑 + 3 轮 flake-hunt **共 5 次全绿** ⇒ 非确定性，且与本轮被审对象（只改 2 个 YAML）无关 ⇒ **既有 flaky**（见 LM-060）。★ **过程留痕**：该次失败的**用例名未被捕获**（复核方驱动脚本只留汇总行，把详情丢了）⇒ 复现时需先补捕获用例名。另：复核方自建的 3 个目录 junction 因可恢复删除工具按安全策略拒绝处理**链接型路径**而**保留未删**（均在**未跟踪**的 `.artifacts/` 内，不入库、不影响主工作区）—— **没有绕过安全策略**。

---

### 4.7 P2（LM-070~LM-089）分批计划 —— **主人已拍板：等 master 合并后按模块分批**

> **主人拍板（2026-10-23，问卷 `ask_ae7194170252762a59d9246e`）**：**"先合并本批，再按模块分批做（推荐）"** ⇒ 前置三项已全部达成（独立复核三轮 `PASS_WITH_NOTES` / CI `39882927682` 全绿 / `master` 合并为 `bbdb1db`）⇒ **P2 批次自此解封**。

**20 项已全部登记**（§4 缺陷表 LM-070~LM-089）。★ **状态推进（2026-10-23）**：LM-070~LM-074 经批次 A 主审复核**成立**并已修复（§4.8 / §4.9）；LM-075~LM-089 经主审**逐条回源码复核，15/15 全部成立**（§4.11），**仍未计入任何"已修复"统计**（修复完成前一律不计入）。按模块分 5 批：

| 批次 | 模块 | 项 | 备注（取自审查报告的触发条件要点） |
|---|---|---|---|
| **A** | **GPU / 外观** | LM-070~LM-074（5 项） | 旧主题被套 GPU 回退配色；派生主题漏 `FontTitle/FontGroup`；受控重建后不恢复 M2 实时玻璃；`TryReconfigure` 绕过 pending 清理检查；恢复重试耗尽被记 `Failed` 后 `HasPendingForGeneration` 返回 false |
| **B** | **网页 / 会话** | LM-075~LM-079（5 项） | 改监听范围不重绑；WS 不绑定会话（注销后仍推送）；口令迁移后 `Save` 即返回 true；请求合并键不含允许目标/凭据策略（白名单旁路）；`StripPort` 对 IPv6 字面量截断（地址碰撞，与 LM-058 同类） |
| **C** | **插件沙箱** | LM-080~LM-082（3 项） | `AddDeclaredHost` 只按 `/` 截断；手动重定向按状态码无条件改 GET；原生 Crypto 请求按**实例 ID** 而非执行作用域合并 |
| **D** | **安装 / 市场 / 配置** | LM-083~LM-085（3 项） | 事务恢复晚于首次加载（内存仍跑新版本）；市场正文读取**无超时**（30 s 只约束到响应头）；`AtomicWrite` 把加密前的旧主文件**原样**复制成备份 |
| **E** | **探针 / 发布流水线** | LM-086~LM-089（4 项） | 探针 `PumpScene` 在 `Task.Run` 里调 UI 线程方法；真实 MainForm 隔离门在 `OnShown` 启动服务**之前**检查；探针找不到源码时 `guard` 为 null 却仍输出"未漂移"；`release.yml` **未接入**依赖清单门禁 |

**每一批的执行纪律（与前批一致，不打折）**：

1. **逐条先复核确认成立** —— 报告是**外部输入，不是证据**；每项必须由主审**自己回源码逐行核对**，并给出可复现命令 + 实际输出 + 文件行号。**不成立**的项按 `REPORTED → 否决` 登记并说明理由，**不为了凑数去修**。
2. 成立项才修；每项修复都带**变异自证**（红 → **SHA-256 逐位一致**还原 → 绿）。
3. **实现者自审不算数** ⇒ 每批派**独立 verifier**（自写用例 + 自设计变异 + 必查"不得"项）。
4. 阈值必须等于**被推送分支**的实测总数（`ci.yml` 与 `release.yml` **逐字一致**），只收紧不放宽。
5. 契约 6 常量**逐字不得改动**。
6. 实机行为（GPU 画面解冻、受控重建时序、真实浏览器端到端、真实签名升级）**一律如实标 `NOT_VERIFIED` / `BLOCKED_ENV`，不计入通过**。

★ **批次顺序建议 A → B → C → D → E**：A/B/C 是**运行时行为**（风险最高、收益最直接），D/E 是**安装与流水线**（与发布准入直接相关）。批次 E 的 LM-089（`release.yml` 缺依赖清单门禁）虽然只是门禁缺失，但**先修 A~D** 可以避免在流水线半成品上叠加更多改动。

★ **批次 A 的文件所有权结论（2026-10-23，主审实测）**：LM-070~LM-074 的预估触及文件**大量重叠** —— `src/UI/UIController.cs` 被 **4 项**（070/072/073/074）触及，`tests/LiteMonitor.Tests/RenderingTransitionCoordinatorTests.cs` 被 **3 项**（070/071/073/074）触及 ⇒ **批次 A 必须串行派工**（一个 worker 独占整批），**不能**按项并行。批次 B 的 LM-075~LM-079 触及 `src/System/WebServer/*` 与 `src/Core/Actions/AppActions.cs`（LM-075/076 同文件）⇒ 同理串行或按文件切分。

### 4.8 P2 批次 A（LM-070~LM-074）复核结论 —— **5 项全部成立**（主审独立复核）

**复核方式**：只读证据采集（独立于主审的会话，**不采信其结论**）先行采集 5 项的源码证据；**主审随后自己逐条回源码复核**，逐项核对触发前提、代码位置、调用链闭合性、后果与测试覆盖。

**报告指针核验**：R06~R10 的 5 个行号在审查报告归档时（`822de57`）**全部精确命中**（用 `git show 822de57:<file>` 逐条比对）；当前漂移仅因 R02 修复（`9e3ba32`）加行，**缺陷位置本身未过时** ⇒ 无"报告不成立"的项。

| 编号 | 主审复核 | 关键不变式（修复后必须钉住） |
|---|---|---|
| LM-070 | ✅ 成立 | 换到**原生旧主题**（无 `rendering` 块）时，**不得**被套上 GPU 回退的中性默认配色；同时 **GPU 主题被迫回退**时**应当**用 `fallbackStyle` ⇒ 两者必须**区分**，不能一刀切 |
| LM-071 | ✅ 成立 | 派生主题的 5 个**运行期字体字段**（`FontTitle/FontGroup/FontItem/FontValue/FontTaskbar`）必须与源主题一致；且**不得**引入悬垂字体引用（`DisposeFonts` 与共享对象的交互） |
| LM-072 | ✅ 成立 | 受控重建后，若 `_cfg.LiveGlassEnabled == true` 则**必须**按配置恢复实时玻璃；"临时停机"**不得**改变 `Enabled` 标志（否则恢复被早退跳过）；设置页复选框与状态行**不得**自相矛盾 |
| LM-073 | ✅ 成立（**范围扩大**） | 取 WDA 所有权的**三个**入口（`TryReconfigure` / `Resume` / `NotifyOutputChanged`）**都必须**受 pending 清理闸门约束，与 `Start` 保持一致 |
| LM-074 | ✅ 成立 | 同代次清理责任**耗尽**后，仍**不得**允许新的 WDA 所有者进入（INV-6 闸门）；★ 该不变式与合同 §6.3"终态不可回退"存在张力 ⇒ **须先裁决是否属契约修订**再动手 |

★ **共同结论**：5 项**全部成立**、**全部无针对性测试覆盖** ⇒ 修复时每项至少补 **1 条会变红的钉住用例**。LM-070~LM-073 属**确定性代码缺陷**（静态可闭合）；LM-073/LM-074 的**最终观感**（自捕获、截图不可见）依赖真实 HWND + 真实 GPU/WDA 时序 ⇒ 修复后仍按纪律 6 标 `NOT_VERIFIED`。★ **主审新增发现（报告未提）**：LM-073 的 `Resume` 与 `NotifyOutputChanged` 也是取 WDA 入口且同样无 pending 检查 ⇒ 修复范围**不得**只覆盖 `TryReconfigure`。

### 4.8.1 主审裁决 —— P2 批次 A（LM-070~LM-074）修复口径（2026-10-23）

**裁决性质**：本节是**主审对修复口径的最终裁决**。worker 的实现必须逐条对齐；与审查报告原文冲突时**以本节为准**，并把差异写进交付说明。

#### (1) LM-070（R06）：原生旧主题**不得**被套上 GPU 回退配色

- **判别式**：目标主题**本身**是不是 GPU 主题 ⇒ `theme.Rendering?.IsGpuRequested == true`（`src/Rendering/Configuration/ThemeRenderingConfig.cs:58-59`）。
- **规则**：
  - GPU 主题**被迫回退**到 legacy（`safe_mode` / `gpu_effects_opt_in_required` / 运行时 GPU 故障）⇒ **应当**套用 `rendering.fallbackStyle`（未声明时走中性可读默认值，合同 §9.5）。
  - **原生 legacy 主题**（`theme.Rendering == null`，或 `IsGpuRequested == false`）⇒ **不得**套用任何回退配色 ⇒ 直接使用主题本身。
- **落点（两处，缺一不可）**：
  1. `src/UI/UIController.cs:1534-1545`（`CompleteLegacyTransition`）：目标主题不是 GPU 主题 ⇒ `_legacyFallbackTheme = null`（清空派生覆盖）。
  2. `src/UI/UIController.cs:1432-1439`（`RefreshLegacyFallbackTheme`）：重载进来的主题不是 GPU 主题 ⇒ **清空** `_legacyFallbackTheme` 并返回 —— R06 说的"覆盖一直挂到重新进入 GPU 或重启"就修在这里。
- **禁止**：不得改动 `LegacyFallbackStyleResolver` 与 `LegacyFallbackStyleBridge.BuildDerivedTheme` 的既有语义（既有回退样式用例必须仍绿）—— 分支只加在**调用点**。
- **钉住用例**：① GPU 主题被迫回退 ⇒ 派生主题的 6 个色槽 == `fallbackStyle`；② 原生旧主题 ⇒ `CurrentRenderTheme` **就是** `ThemeManager.Current`（同一实例）、其色槽**未被**中性默认值覆盖；③ 原生旧主题 ⇒ 6 个色槽与源主题逐个相等。

#### (2) LM-071（R07）：派生主题必须带上**运行期字体**

- **【事实】**：`Theme` 的 5 个运行期字体是**字段**（`src/Core/ThemeManager.cs:370-374`），而 `DeriveConfig`（`src/UI/RenderingTransitionCoordinator.cs:836-850`）只复制 **public 属性** ⇒ 派生主题的 5 个字体全部退回 `SystemFonts.CaptionFont` ⇒ `UIRenderer` / `HorizontalLayout` 用默认字体绘制。
- **裁决：共享引用，不新建**。
  1. `BuildDerivedTheme` 必须**显式**把源主题的 5 个运行期字体**字段**复制给派生主题（**同一 Font 实例**）。
  2. **释放责任仍在源主题**（`ThemeManager.Current`）；派生主题**不得**调用 `DisposeFonts()`。
  3. `src/UI/UIController.cs:191-194`（`oldTheme.DisposeFonts()`）**必须**推迟到 `RefreshLegacyFallbackTheme(t)`（`:254`）**之后** —— 否则旧主题的字体被释放，而派生主题仍引用它们（悬垂引用）。
- **禁止**：派生主题**不得**自己 `BuildFonts()` / `Scale()`（会二次缩放；且纯 CLR 的协调器拿不到 dpi×user 系数）。
- **钉住用例**：① 派生主题的 5 个字体字段与源主题 `Assert.Same`（同一实例）；② 换肤（旧主题 `DisposeFonts()`）之后，派生主题引用的字体**未被释放**（可用 `Font.IsSystemFont` 之外的存活断言或引用计数式断言）；③ 派生主题**不得**释放源主题的字体。

#### (3) LM-072（R08）：受控重建后必须**按配置**恢复实时玻璃

- **【事实】**：`MainForm_Transparent.OnHandleDestroyed`（`:498-508`）调 `StopLiveGlass()` ⇒ `UIController.StopLiveGlass`（`:320`）⇒ `LiveGlassController.SetEnabled(false)`（`:190-205`）⇒ **`Enabled` 标志被清成 false**；`HandleCreated`（`:355`）⇒ `OnHandleChanged`（`:307-311`）⇒ `RefreshRequest`（`:351-354`）在 `_service == null` 时**直接返回** ⇒ 服务永不重启；即便补一次 `SetEnabled(true)`，也会被 `:193` 的 `Enabled == enabled` 早退挡掉。
- **裁决：临时停机 ≠ 用户关闭**。
  1. **句柄销毁路径不得清 `Enabled` 标志** —— 只停服务，保留 `Enabled`（配置仍为开）。⚠️ `OnFormClosed`（`:605`）是**真关闭**，必须与句柄销毁**区分开**（真关闭才允许整停）。
  2. `LiveGlassController` 必须能在"配置为开但服务已停"时**按配置重启**（`SetEnabled(true)` 在 `Enabled` 已为 true 且 `_service == null` 时**不得**早退跳过重启）。
  3. **恢复时机**：目标 legacy **就绪之后**（GPU→legacy 转换步 6–9 完成时）按 `_cfg.LiveGlassEnabled` 恢复；**不得**在句柄重建过程中恢复（那时句柄尚未就绪）。
  4. **单一捕获所有者**：GPU 宿主必须在 live glass 恢复**之前**已停止（转换步 3 已 `StopGpuPresentation`）⇒ 不得出现两个捕获所有者。
  5. **状态一致性**：`LiveGlassStatusText` 必须反映真实状态（服务停了就要说停了），设置页复选框与状态行不得自相矛盾。
- **钉住用例**：① 临时停机后 `Enabled` 仍为 true；② 配置为开且服务已停 ⇒ `SetEnabled(true)` **确实重启**服务（不是早退）；③ 转换完成后按配置恢复；④ 用户显式关闭 ⇒ `Enabled == false` 且**不**被自动拉起。

#### (4) LM-073（R09）：**全部** WDA 取得入口共用同一个闸门

- **【事实】**：WDA 取得入口共 **4 个** —— `GpuPresentationHostController.Start`（`:281-288`，**唯一**有 pending 闸门）、`TryReconfigure`（`:554` 释放 / `:566` 取得）、`Resume`（`:474`）、`NotifyOutputChanged`（`:695`）。R09 的可复现场景：旧 live 会话恢复失败并移交 ledger（Pending）⇒ 同 HWND 可先启动 **fixed 宿主**（`Start` 的闸门只在 `policy == WdaExcludeFromCapture` 时生效 ⇒ fixed 放行）⇒ 之后开启 live 走 `TryReconfigure` ⇒ **无闸门** ⇒ 施加 0x11 ⇒ 新会话把遗留 0x11 当"外部原值" ⇒ 随后 ledger 恢复原策略 ⇒ **撤掉正在使用的排除** ⇒ 自捕获。
- **裁决**：
  1. 所有"**要施加** `WDA_EXCLUDEFROMCAPTURE`"的入口在施加前查同一个闸门：`Start`、`TryReconfigure` 的 live 分支、`Resume`、`NotifyOutputChanged`。
  2. `TryReconfigure` 的 `SetCapturePolicy(None)` 分支（`:554`）是**释放** WDA ⇒ **不受**闸门约束。
  3. 命中闸门 ⇒ 拒绝 + 明确错误码。
  4. **不得**改 `TryReconfigure` 的 **public 签名**（加参数即破坏 API）⇒ 闸门复用**已注入**的 `CapturePolicyCleanupPendingProbe`。
- **钉住用例**：pending → fixed → live 的状态组合下，live 取得必须被拒绝（三个入口各 1 条）。

#### (5) LM-074（R10）：同代次"未达成清理目标" ⇒ 阻止新所有者；**契约不修订**

- **★ 契约张力裁决：不修订合同。** 三条理由：
  1. 合同 §6.3「终态不可回退」约束的是**那一条清理责任自身**（不再重试、不再触碰 HWND），**不是**"闸门必须放行"。
  2. 合同 §6.4「耗尽后不阻塞新代次（INV-2）」说的是**宿主生命周期**（`StopGpuPresentation()` 之后 `_gpuPresentation == null` 无条件成立）与**不同代次**（新 HWND ⇒ 新代次不受阻）⇒ **不豁免同一代次叠加新所有者**。
  3. 支配条款是计划 §4.5 :202（合同 §6.5 `:424` 原文引用）："**同一 HWND 上清理未完成时禁止叠加新的 affinity 所有者**"。"清理未完成" = **未达成清理目标**；`Failed(exhausted)` 正是"永远达不成" ⇒ 同代次**仍被占用**。
  ⇒ ⇒ 张力在"**闸门语义**"，不在"**状态机语义**" ⇒ 合同**一字不改**，改的是闸门判据。
- **修复口径**：
  1. **判据**：`CapturePolicyCleanupLedger` 增加"该代次是否仍被占用"的查询 ⇒ 同代次存在 `Pending` 条目，**或** `Failed` 且 `AffinityChangedBySession == true` 的条目 ⇒ 仍被占用。（`Failed` 且 `AffinityChangedBySession == false` ⇒ 没改过 affinity ⇒ 无残留 ⇒ 不占用。）`HasPendingForGeneration` 的既有语义**不得**改写（既有调用点与用例依赖它）⇒ **新增**方法并列。
  2. **入口**：LM-073 的 4 个入口统一改用新判据；命中 ⇒ 拒绝 + **可区分**的错误码（`Pending` ⇒ 既有 `gpu_capture_policy_cleanup_pending`；`Failed` ⇒ 新增 `gpu_capture_policy_cleanup_unresolved`）。
  3. **解除路径（必须存在且被测）**：
     - **受控重建 / 代次推进** ⇒ 自动解除（新代次不受阻；残留随旧 HWND 一起消失）。
     - **明确恢复** ⇒ ledger 新增"对同代次 `Failed`（且 `AffinityChangedBySession == true`）条目**重建一条新的清理责任**"的入口：新责任**沿用精确原值 `OriginalAffinity` 与同一 `WindowHandle`**、`Attempts` 归零；**旧条目不再重试、不再触碰 HWND**（终结为 `Completed("rearmed_by_explicit_recovery")`）⇒ **不违反 §6.3**；新责任走 §6.4 的退避重试预算 ⇒ 成功后**真正恢复原策略** ⇒ 闸门自然解除。
  4. **触发**：明确恢复**只能由显式的用户动作触发**；**禁止**在 `Tick` 里自动触发（合同 §6.4 禁止每帧重试）。推荐落点：`UIController` 收到 `gpu_capture_policy_cleanup_unresolved` 拒绝码时安排一次恢复（该拒绝码只可能来自用户显式发起的 WDA 取得）。
  5. **精确原值不丢失**：任何路径都**不得**把残留 0x11 当成"外部原值"（这正是 R10 的危害本体）；同代次被放行时仍必须按合同 §7.3 R3 **重读**原值。
- **钉住用例**：① 同代次耗尽 ⇒ 新的 WDA 取得被拒绝且错误码可区分；② 不同代次 ⇒ 不受阻；③ 明确恢复成功后 ⇒ 闸门解除且**原策略真的被写回**（精确原值，不是残留值）；④ 旧 `Failed` 条目在明确恢复后**不再重试**（§6.3）。

---

### 4.9 P2 批次 A（LM-070~LM-074 + LM-093）修复与集成记录（2026-10-23）

**派工结构**：批次 A 的预估触及文件**大量重叠**（`src/UI/UIController.cs` 被 **4 项**触及）⇒ **串行派工**，共 3 个 worker、8 个提交，全部集成到 `fix/p2-batch-a`（批次基线 `6eac90c` ⇒ 批次头 `50f8fb5`，+3124 / -45 行，14 个文件）。

| 缺陷 | 报告 | 裁决要点（§4.8.1） | 实现落点 | 提交（主分支） | 钉住用例 | 变异自证 |
|---|---|---|---|---|---|---|
| LM-070 | R06 | 判别式 `theme.Rendering?.IsGpuRequested`；**原生 legacy 主题不得套回退配色**，GPU 主题被迫回退**应当**套 `fallbackStyle` | `LegacyFallbackStyleBridge.IsGpuTheme`（新增）；`UIController.RefreshLegacyFallbackTheme` / `CompleteLegacyTransition` 两处分支 | `ff4e026` | 5 | 2 |
| LM-071 | R07 | 派生主题**共享**源主题 5 个运行期字体**同一实例**；释放责任仍在源主题；旧主题字体释放**推迟**到派生主题重算之后 | `BuildDerivedTheme` 显式复制 5 个字体**字段**；`ApplyTheme` 的 `DisposeFonts()` 移到 `RefreshLegacyFallbackTheme(t)` 之后 | `79844af` | 5 | 2 |
| LM-072 | R08 | **临时停机 ≠ 用户关闭**：`Enabled` 标志保留；`SetEnabled(true)` 不得被早退挡掉；目标 legacy 就绪后按配置恢复；单一捕获所有者；状态行反映真实状态 | `LiveGlassController.StopForHandleRecreate` / `RestoreIfEnabled`（新增）、`SetEnabled` 早退修正、`StatusText` 三态；`MainForm_Transparent.OnHandleDestroyed` 改调临时停机；`UIController.CompleteLegacyTransition` 收尾恢复 | `151dd32` | 8 | 3 |
| LM-073 | R09 | **四个** WDA 施加入口（`Start` / `TryReconfigure` live 分支 / `Resume` / `NotifyOutputChanged`）**共用同一个闸门**；释放分支不受约束；**不得**改 `TryReconfigure` 的 public 签名 | `GpuPresentationHostController.TryBlockCapturePolicyOwnership`（新增私有闸门）+ 四个入口接入 | `bb05bc0` | 5 | 4 |
| LM-074 | R10 | 同代次「**未达成清理目标**」（`Pending` ∨ `Failed`∧`AffinityChangedBySession`）⇒ **占用**闸门；错误码可区分；解除路径 = 受控重建（代次推进）**或**明确恢复（新建携带**精确原值**的责任，旧责任如实保持 `Failed`+`exhausted` 并打「已被接管」标记） | `CapturePolicyCleanupLedger.GetBlockingReasonForGeneration` / `HasUnresolvedForGeneration` / `TryRearmForExplicitRecovery` / `TryScheduleExplicitRecoveryForRejectedCode`（均新增）；`WindowCapturePolicyCleanup.ReclaimedByExplicitRecovery`（新增标记位）；宿主新增 `CapturePolicyCleanupBlockingProbe`（不改既有探针类型） | `74a0967` + `5e980ff` + `214a47e` | 12 | 8 |
| **LM-093** | — | **主审复核 LM-074 返工 diff 时发现**：双路登记（事件回调 + `LastCapturePolicyCleanup` 兜底）把**同一实例**二次登记 ⇒ 被 INV-4 拒绝 ⇒ 新增的留痕逻辑把**每一次正常移交**都误记成 `gpu_capture_policy_cleanup_registration_rejected`，并挤掉 `CleanupWarningCode` 单槽里更有意义的 `gpu_capture_policy_cleanup_superseded`。裁决：修在 ledger 层 —— `Add` 的判定顺序改为「**先**判同一实例是否已在册 ⇒ 幂等返回 `true`（不触发任何被拒诊断）→ 再判终态 → 最后才是 INV-4（**不同**条目撞同代次才算真被拒）」 | `CapturePolicyCleanupLedger.Add` 判定顺序 + `UIController.StopGpuPresentation` XML 文档更正 | `50f8fb5` | 4 | 2 |

**★ 合同处置**：LM-074 触及的合同张力**按 §4.8.1 (5) 裁决为「不修订合同」** —— 合同 §6.3「终态不可回退」约束的是**那一条清理责任自身**（不再重试、不再触碰 HWND），不是「闸门必须放行」；§6.4「耗尽后不阻塞新代次（INV-2）」说的是**宿主生命周期**与**不同代次**；支配条款是计划 §4.5 :202（合同 §6.5 `:424` 引用）"同一 HWND 上清理未完成时禁止叠加新的 affinity 所有者" ⇒ 改**闸门判据**，不改**状态机语义**。**合同文档 `docs/superpowers/plans/2026-10-23-gpu-transition-t02-contract.md` 一字未改**（`git diff 6eac90c..HEAD -- <合同>` 为空）。

**★ 主审复核实际拦下的问题（4 项 + 1 项新登记）** —— 均为**读 diff 读出来的**，非 worker 自查：
1. `Failed → Completed` 是**不实记账**（"清理成功"与"原策略始终没被写回"矛盾）⇒ 改为 `ReclaimedByExplicitRecovery` 标记位，`State` / `TerminalReason` 一字不动。
2. `NotifyOutputChanged` 的早退顺序被前移 ⇒ 宿主未运行时从非 owner 线程调用会**抛 `InvalidOperationException`**（改变既有行为）⇒ 已复原（闸门改用 IL 结构护栏钉住）。
3. 「明确恢复」与宿主停止移交的**先后顺序**必须固定 ⇒ 顺序反了，移交先占坑、恢复被拒 ⇒ 写回的是**误采残留 0x11 的值** ⇒ R10 的危害原样复活。
4. 明确恢复的触发点漏了 `Start` 分支 ⇒ 最常见的「切 legacy 再切回 GPU」流程（`canReconfigure == false` ⇒ 走不到 `TryReconfigure`）根本不会安排恢复 ⇒ 解除路径在主流程里几乎不可达 ⇒ 已补回。
5. **LM-093**（新登记，占用编号 `LM-093`，下一个空号 `LM-094`）：正常移交上的假「登记被拒」告警。

**★ 已知取舍（主审接受并记档，不是遗漏）**：
- **闸门前移**（`TryReconfigure` / `Resume`）：闸门回答的是"能否取得 WDA 所有权"，是所有 WDA 施加路径的共同前置条件；拒绝原因不得被通用的 `gpu_presentation_not_reconfigurable` 掩盖。
- **「明确恢复」由事件驱动的 WDA 取得尝试触发**（用户切换主题 / 开关 live / **DPI 变化**等），**严禁** `Tick` / ledger `Pump` 里每帧触发（合同 §6.4「禁止每帧重试」）。★ "DPI 变化也会触发一次恢复"是**已知取舍**。
- **同一 HWND 的累计恢复写入次数可能超过 6 次**：每条责任**独立预算** + 事件驱动 ⇒ 已知取舍。★ **口径更正（2026-10-23，独立复核后）**：本条原先写作"（6 + 最多 6）"，**该括注是错的** —— 同代次的**重建次数本身没有上限**（同一条旧责任可被反复接管，每轮新建一条拥有独立 6 次预算的新责任），累计写入次数只受"用户事件频率"约束 ⇒ 详见 **LM-096**。

**★ `NOT_VERIFIED` / `BLOCKED_ENV`（不计入通过）**：
- `NotifyOutputChanged` 入口的**运行时行为**（仅 IL 结构护栏钉住闸门位置）—— 无真实 D3D11，`GpuSceneRenderer` 是 sealed 且无法伪造。
- 真实 GPU / 桌面会话下的端到端行为（真实 flip-model 画面解冻、受控重建实机时序、首帧失败实机时序、运行故障→恢复耗尽的完整链路、WDA 恢复失败交互、DPI/跨屏/隐藏窗口故障回退）。
- `UIController` 完整触发链路（`ApplyGpuRenderingRoute` → 恢复安排 → ledger 重建 → `Tick` 的 `Pump` 写回）在真实窗体 + GPU 宿主下的行为（结构有 IL 护栏、两端各有纯 CLR 用例，但**串起来**无自动化断言）。
- DPI 变化触发一次恢复的实际链路。
- 真实浏览器跨源读取、端到端登录 / WS 握手 —— `BLOCKED_ENV`。

**★ 不得项核查（主审亲自跑）**：本批**未删除任何 public 成员**（`git diff 6eac90c..HEAD -- src/` 中无 `-` 开头的 public 行）；契约 6 常量逐字未改（`PluginSandboxLimits.MaxStepsPerExecution=16` / `MaxTargetsPerStep=64` / `MaxExecutionDurationMs=30000` / `MaxConcurrentRequests=8` / `MaxResponseBytes=2*1024*1024` / `MaxRegexMatchTimeoutMs=500`）；`docs/superpowers/plans/**` 与合同文档零改动；未删除测试、未降低断言；全部改动文件 **CRLF、无 BOM**。

**★ 测试与阈值**：批次基线 **1487**（1395 + 73 + 19）⇒ 批次头 **1526**（1433 + 73 + 19），净增 **+39**（LM-070 5 + LM-071 5 + LM-072 8 + LM-073 5 + LM-074 12 + LM-093 4）。主审**亲自**在集成后的主分支上跑全量：`dotnet build --no-incremental` **0 错误** + `dotnet test --no-build` ⇒ **1525 通过 / 0 失败 / 1 跳过（合计 1526）**，与 worker 报告逐位一致。两个工作流的 `MIN_TEST_TOTAL` 同步上调至 **1526**（= 被推送分支的实测总数），两处**逐字一致**；`MAX_SKIPPED_TESTS` 与其余 4 个门禁参数未动。

**★ CI 取证**：`39895885534` @`aa66629` = **success**（作业「构建 / 测试 / 打包」）。门禁「强制校验测试发现数」的实际输出：**发现 1526 条用例（通过 1525 / 失败 0 / 跳过 1），阈值 1526** ⇒ 阈值**等于**被推送分支的实测总数（若阈值被调低或用例被删，本行会变红）。跳过数上限 `MAX_SKIPPED_TESTS=1` 实际跳过 **1** 条（`MarketE2ETests.端到端_拉索引_安装_校验_卸载`，联网 E2E，门控 `LITEMONITOR_E2E=1`，CI 故意不设）；必须真正执行的 `MarketIndexContractTests` **发现 6 条 / 通过 6 条 / 跳过 0 条** ✓。

### 4.10 P2 批次 A（LM-070~LM-074 + LM-093）独立复核 —— **`PASS_WITH_NOTES`**（2026-10-23）

> **复核者**：独立 **verifier**，在隔离 worktree（`.artifacts/wt-verify-batchA`，分支 `verify/batch-a`，
> 基线 `50f8fb5`）中复核，**未触碰共享主工作区**。
> **纪律**：结论**只**由复核者**自写的 26 条用例** + **自选的 9 个变异锚点**支撑；
> 实现者的 7 个测试文件仅作语义参考，**未采信**。
> **证据**：`.artifacts/wt-verify-batchA/.artifacts/verify-batchA/evidence-checks.md`
> + `logs/M1.txt`~`logs/M9.txt` + `mutations/` + `backup/M1..M9/`（字节备份）。

**总判定：`PASS_WITH_NOTES`** —— 6 项修复在确定性层面**全部成立**（基线实测与期望逐位一致、
9 次自设计变异全部"改坏即红 / 还原即绿"且 SHA-256 逐位一致、6 类不得项逐条核查通过）；
附带 **3 条新发现（LM-095 / LM-096 / LM-097）**，均为低～中风险，**不否定本批修复**。

| 缺陷 | 复核结论 | 支撑 |
|---|---|---|
| LM-070 | **PASS** | 3 条自写用例 + M1/M2 变异；两处落点均以 `IsGpuTheme` 为前置，`BuildDerivedTheme` 全仓仅此两个调用点 |
| LM-071 | **PASS** | 2 条自写用例（含生产顺序 Load→BuildFonts→Scale→派生）+ M2 变异；`DisposeFonts()` 全仓只对旧主题调用 |
| LM-072 | **PASS_WITH_NOTES** | 4 条自写用例 + M6/M7 变异；**新发现 LM-095** |
| LM-073 | **PASS** | 3 条自写用例（四入口同一闸门 / 释放分支不受约束 / public 签名未变）+ M9 变异；另核实生产侧不存在第 5 个 WDA 施加入口 |
| LM-074 | **PASS_WITH_NOTES** | 7 条自写用例 + M5/M8 变异；**新发现 LM-096 / LM-097** |
| LM-093 | **PASS** | 6 条自写用例（ledger 层 5 + 协调器层 1）+ M4 变异；双路移交确认是**同一实例** |

**变异表（9 个锚点，无一与实现者用过的一致；3 个落在实现者**从未触碰**的文件）**：
还原一律用**字节写回**（`Copy` 备份 → 覆盖），**未**使用 `git checkout/restore/stash/reset`；
每次还原后同时核对 SHA-256 与 `git hash-object` blob。

| # | 锚点 | 改坏内容 | 改坏后 | 还原后 | SHA-256 前=后 |
|---|---|---|---|---|---|
| M1 | `ThemeRenderingConfig.cs` `IsGpuRequested` ★实现者未触碰 | `backend=legacy` 也判为 GPU 主题 | **2 红** / 1548 绿 / 1551，exit=1 | 0 红 / 1550 绿，exit=0 | `90B1F008…C0B8` = 同 ✔ |
| M2 | `ThemeManager.cs` `Theme.Scale()` ★实现者未触碰 | `DisposeFonts()` 挪到创建新字体**之后** | **1 红** / 1549 绿，exit=1 | 0 红 / 1550 绿，exit=0 | `B3809BF6…A79D6` = 同 ✔ |
| M3 | `PresentationSession.cs` `RestoreOriginalAffinityCore` ★实现者未触碰 | 写回 `WdaNone` 而非精确原值 | **2 红** / 1548 绿，exit=1 | 0 红 / 1550 绿，exit=0 | `3549DE6C…D6A4B` = 同 ✔ |
| M4 | `CapturePolicyCleanupLedger.cs` `Add()` | 去掉「终态不可回退」的拒绝 | **1 红** / 1549 绿，exit=1 | 0 红 / 1550 绿，exit=0 | `DCC1BD20…D0F3ED` = 同 ✔ |
| M5 | `CapturePolicyCleanupLedger.cs` `TryRearmForExplicitRecovery` | 重建责任不再标记 `RebuiltByExplicitRecovery` | **2 红** / 1548 绿，exit=1 | 0 红 / 1550 绿，exit=0 | 同上 ✔ |
| M6 | `LiveGlassController.cs` `RestoreIfEnabled()` | 去掉「用户显式关闭过 ⇒ 绝不自动拉起」守卫 | **2 红** / 1548 绿，exit=1 | 0 红 / 1550 绿，exit=0 | `897B8A9C…C604F` = 同 ✔ |
| M7 | `MainForm_Transparent.cs` `OnFormClosed` | 真关闭不再整停实时玻璃 | **2 红** / 1548 绿，exit=1 | 0 红 / 1550 绿，exit=0 | `FD43B79A…9E83` = 同 ✔ |
| M8 | `GpuPresentationHostController.cs` `TryBlockCapturePolicyOwnership` | 丢弃原因探针结果（`unresolved` 退化为 `pending`） | **2 红** / 1548 绿，exit=1 | 0 红 / 1550 绿，exit=0 | `79F3B610…E8601` = 同 ✔ |
| M9 | 同上 | **统一闸门恒返回 false**（四入口同时失效） | **7 红** / 1543 绿，exit=1 | 0 红 / 1550 绿，exit=0 | 同上 ✔ |

⇒ **9 / 9 全杀**；每次变异的"打红的是哪条**复核者自己的**用例"见 `logs/M*.txt` 的 `failed_names`。
基线实测 **1526 总 / 1525 通过 / 0 失败 / 1 跳过**（与期望逐位一致）；加复核者 26 条后终态
**1552 总 / 1551 通过 / 0 失败 / 1 跳过**。

**不得项（复核者逐条实跑）**：① 契约 6 常量 `src/Plugins/PluginProcessor.cs` 的 blob 在
`6eac90c` 与 `50f8fb5` **逐字节相同**；② 7 个改动 src 文件逐行比对 `public/internal/protected`
成员签名，**删除数 = 0**（`TryReconfigure` 8 参签名未变）；③ 6 个测试文件为新增、
`LiveGlassLifecycleTests` 7→12 条**删除 0 条**、`src` diff 中 `catch` 增删 = **0**、
无 `Assert.True(true)` / `Skip=` 等弱化模式；④ 合同文档与计划台账的 blob **完全相同**（零改动）；
⑤ 14 个改动文件逐字节统计 **BOM=False / 裸 LF=0**；⑥ 9 次变异改坏态 `dotnet test` 退出码**全为 1**
（"有失败 ⇒ 非零退出"成立），CI 另有独立门禁 `assert-test-counts.ps1`。

**★ 复核者对 3 条已知取舍的独立判断（主审采纳并据此裁决）**：

| 取舍 | 复核者判断 | 主审裁决 |
|---|---|---|
| ① 闸门前移（`TryReconfigure` / `Resume`） | **不违反**合同 §6.4 —— 闸门是**拒绝**而非重试；闸门条件与"施加条件"逐字对齐，不存在"只挡一半"。唯一瑕疵：无会话时也报 pending/unresolved 而非 `not_reconfigurable`（诊断略失真，行为不变） | ✅ **维持**（诊断略失真记为已知残留） |
| ② 明确恢复由**事件驱动**的 WDA 取得尝试触发（含 DPI 路径） | **不违反** —— 逐条追了调用链：`ApplyGpuRenderingRoute` **全仓只有一个调用点**（`UIController.ApplyTheme`），`ApplyTheme` 的调用者是构造函数 / `OnDpiChanged` / `AppActions` / 主题编辑器 ⇒ **没有任何 Tick/Pump 每帧路径**；重建动作只做 ledger 记账、**不触碰 HWND**，真正写回由 `ledger.Pump` 的退避完成 | ✅ **维持** |
| ③ 同一 HWND 累计恢复写入次数可能超过 6 次 | **字面不违反、实质有缺口** —— §6.4 的"总上限 6 次"是**每条责任**的预算；但复核者实测推导**重建次数本身无上限**，主审 §4.9 记录的"6 + 最多 6"**低估** ⇒ 不同意"最多 6 次"这一表述 | ⏸️ **登记为 LM-096 并更正 §4.9 口径**；**裁决不改代码**（理由见 §4 表 LM-096 行） |

**★ 主审对本轮三项新发现的处置**：

1. **LM-095（P2）→ 已修复**（本轮）。新增 `LiveGlassController.HandleRecreateFailed(bool handleAlive)`；
   `UIController` 的重建失败分支改为调用它（并读新增的 `MainForm.IsClosing`，
   关闭流程中**不得**把捕获服务拉回来 —— 合同 §4 全局约束 / U10）；
   状态行在重建失败后**如实**说明"重建失败，需手动重开"，**不再承诺**"重建完成后自动恢复"。
   **新增 4 条用例**（3 条行为 + 1 条 IL 接线护栏）；**主审变异自证 MA1/MA2/MA3 3/3 全杀**
   （MA1 删掉收尾调用 ⇒ IL 护栏红；MA2 让 `handleAlive` 分支不恢复 ⇒ 行为用例红；
   MA3 去掉"重建失败"文案 ⇒ 行为用例红），**每次还原后 SHA-256 与变异前逐位一致**。
2. **LM-096（P3）→ 登记但不改代码**（理由见 §4 表）。主审已把 §4.9 里**自己写错的**
   "（6 + 最多 6）"口径更正为准确表述。
3. **LM-097（P3）→ 已修复**（纯文档）。XML 文档改为与 `ReclaimedByExplicitRecovery` 一致，
   并显式写明"**不得**改成 `Completed`"及其后果（R10 危害原样复活）。
   ⇒ 纯文档改动**行为不变**，因此**不产生**测试用例（与 LM-095 的区别）；以台账核对确认。

**★ 复核者的 `NOT_VERIFIED` / `BLOCKED_ENV`（不计入通过）**：真实 GPU/D3D11 下的端到端行为、
`NotifyOutputChanged` 入口的运行时行为（仅 IL 护栏）、`UIController` 完整触发链路的串接、
DPI 变化路径、`PresentationSession.RecreateWindow` 的合同 §7.3 R1~R6（生产侧**无任何调用点**）、
真实浏览器跨源与端到端登录/WS 握手、插件市场 E2E（套件中仍是唯一 1 条跳过）。
`BLOCKED_ENV`：无（所有必需检查均在本地完成）。

### 4.11 P2 批次 B~E（LM-075~LM-089）主审逐条复核 —— **15 / 15 全部成立**（2026-10-23）

> **复核口径**：审查报告是**外部输入，不是证据**。本节每一条都由主审**自己回源码逐行核对**并给出文件行号；
> 其中 **2 条**另做**本机实测**（探针源码与实际输出留存于 `.artifacts/`，可复现）。
> ★ **本节只判定"缺陷是否成立"，不含修复方案、不含修复完成统计** ——
> 修复完成前，LM-075~LM-089 **一律不计入任何"已修复"统计**。
> ★ 复核底稿：`.artifacts/batch-b-review-draft.md` / `batch-c-review-draft.md` /
> `batch-d-review-draft.md` / `batch-e-review-draft.md`（四份，主审亲笔）。

**结论：15 / 15 全部成立，无一项被否决。**

| 批次 | 缺陷 | 成立的关键证据（文件:行） | 危害方向 |
|---|---|---|---|
| B | LM-075 | `AppActions.cs:316` 重启判据**只看端口与凭据、不看 `BindMode`**；`MainPanelPage.cs:459-469` 用**配置草稿**算 scope，而真实端点服务端**本来就记着**（`LiteWebServer.cs:282`） | **安全向**：LAN→仅本机 时 socket 仍绑 `0.0.0.0`，界面却显示 `127.0.0.1`（暴露状态与显示相反） |
| B | LM-076 | `WebSessionManager.cs:405` `_wsClients` 的值**只有服务代次**；`:909-921` `RevokeSession` 只删 `_sessions`；`:887`/`:892`/`:958` 三条过期路径同样只删记录 | **数据泄露**：注销/过期后已建立的 WS **继续收监控数据** |
| B | LM-077 | `SettingsHelper.cs:181` 禁存**直接 return** + `:191-194` **吞异常**（`Save` 返回 `void`）；`Settings.cs:549-553` 无条件 `return true` | **凭据泄露**：旧明文留盘；"迁移失败 ⇒ 拒绝登录"的承诺落空 |
| B | LM-078 | `PluginExecutor.cs:451` 在途键**只有摘要**；`:889-892` 凭据目标校验在 valueFactory **内部**；`:1587-1600` 命中已有租约时**不调用自己的 factory** | **白名单旁路**（fail-open）：白名单更严的调用方拿到本应被拒的响应 |
| B | LM-079 | 三处 `StripPort` 一律 `LastIndexOf(':')`（`PluginExecutor.cs:845` / `PluginProcessor.cs:1051` / `CryptoNative.cs:185`）⇒ `[fd00::1]` 与 `[fd00::2]` 都化成 `[fd00:` | **白名单旁路**（fail-open）：声明了 `[fd00::1]` 却能把凭据发往 `[fd00::2]` |
| C | LM-080 | `PluginProcessor.cs:422` 只按 `/` 截 authority，**不处理 `?` / `#`** | 偏严（声明被污染成垃圾串 ⇒ 该主机的带凭据请求被拒） |
| C | LM-081 | `PluginExecutor.cs:1006-1010` + `:1073-1076` 判据**只看状态码** ⇒ HEAD 也被改成 GET | 语义丢失 + **与注释自称的".NET 原生重定向一致"直接矛盾**（见下方实测） |
| C | LM-082 | `CryptoNative.cs:124-134` 键 = `cacheKey + "\|" + 实例ID`；`:133` 共享任务绑定**创建者**的 `token` | 跨代次取消相互影响 + **与 HTTP 路径口径不一致**（违反 S1.0 契约 C3.4） |
| D | LM-083 | `PluginMarket.cs:181-188` 恢复挂在**静态构造**上；`PluginManager.cs` 全文**零** `PluginMarket` 引用；`MainForm_Transparent.cs:213-218` 顺序是「先 `LoadPlugins`+`Start`，后 `PluginSelfCheck.RunOnce`（此时才第一次碰 `PluginMarket` ⇒ 才恢复）」 | 磁盘回滚了**内存仍运行新版本**（回滚承诺在进程内不成立） |
| D | LM-084 | `PluginMarket.cs:607-608` 用 `ResponseHeadersRead`；`:691-698` 正文读取**只用调用方 `ct`**；`PluginMarketPage.cs:366` 生产调用**不传令牌** | **无限等待**（实测）+ 市场页 `_busy` 永久卡死 |
| D | LM-085 | `SettingsHelper.cs:326-330` 把**旧主文件原样**复制成备份；`Settings.cs:792-798` 插件凭据在主文件里是 **DPAPI 密文**；`SanitizeBackupFile` **只删 `WebServerPassword`** | **凭据明文留存磁盘**（fail-open）：备份比主文件泄露面更大 |
| E | LM-086 | `RecreateHandleScenario.cs:394` `_ = Task.Run(...)` **丢弃任务**；`:403-410` 只捕获 `OperationCanceledException`/`ObjectDisposedException`；`GpuPresentationHostController.cs:422-425` + `:1134-1141` `EnsureOwnerThread` 抛 `InvalidOperationException` | **假绿**：持续送帧一次都没成功，实验仍可退出 0 |
| E | LM-087 | `RealMainFormScenario.cs:66-67` 隔离门在 `Application.Run` **之前**求值；`MainForm_Transparent.cs:560-569` 生产 `OnShown` 才启动 WebServer | 探针的"隔离"承诺**未被约束**（可能真的开局域网监听） |
| E | LM-088 | `RecreateHandleScenario.cs:715-720` `_guard is null` ⇒ `unchanged` 保持初值 `true` ⇒ `:750`/`:808` 输出 `sourceHashChanged=false` | **假绿**：输出"源码未漂移"，实际**根本没做比对** |
| E | LM-089 | `ci.yml:165-170` **有**依赖清单门禁；`release.yml` 全文检索 `dependency-manifest` / `generate-dependency` / `Mode Check` **零命中** | 发布准入与 CI 门禁**不一致**（CI 红、发布流水线照跑） |

**★ 两条本机实测证据**（探针源码与原始输出：`.artifacts/lm081-probe/`、``.artifacts/lm084-probe/`，
输出另存 `.artifacts/lm081-probe-evidence.txt`、`.artifacts/lm084-probe-evidence.txt`）：

1. **LM-081 · .NET 原生在 301/302/303 上保留 HEAD**（.NET SDK 8.0.426，裸 `TcpListener` 自写 HTTP/1.1 响应）：
   ```
   301 HEAD -> observed=[HEAD /301 | HEAD /final] finalMethod=HEAD
   301 GET  -> observed=[GET /301 | GET /final]       finalMethod=GET
   301 POST -> observed=[POST /301 | GET /final]      finalMethod=GET
   302 HEAD -> observed=[HEAD /302 | HEAD /final]     finalMethod=HEAD
   303 HEAD -> observed=[HEAD /303 | HEAD /final]     finalMethod=HEAD
   ```
   ⇒ 本仓 `RewritesMethodOnRedirect`（`PluginExecutor.cs:1073-1076`）**多改写了 HEAD**，
   与它自己注释里"与 .NET 原生重定向一致"的说法**矛盾**。
2. **LM-084 · `ResponseHeadersRead` 下 `HttpClient.Timeout` 不覆盖响应正文**（客户端 `Timeout = 5 s`）：
   ```
   [调用方令牌=None] 响应头已到达，用时 49 ms（Content-Length=1024）
   [调用方令牌=None] 20 秒后仍未返回 ⇒ 正文读取不受 HttpClient.Timeout 约束，确认无限等待
   [调用方令牌=12s] 异常 TaskCanceledException，总用时 12008 ms
   ```
   ⇒ **只有调用方令牌能中断正文读取**；而生产调用链**恰好不传令牌** ⇒ 无限等待成立。

**★ 复核中新登记的缺陷**：`LM-094`（P3）—— `PluginProcessor.AddDeclaredHost`（`:431-432`）用
`IndexOf(':')` 截端口 ⇒ IPv6 字面量 `[fd00::1]` 被截成 `"[fd00"` ⇒ 声明主机集合里是一枚畸形串。
与 LM-079 **同族但不同处**（LM-079 是三处 `StripPort` 按**最后一个**冒号截断；本项是 `AddDeclaredHost`
按**第一个**冒号截断），方向**偏严**。⇒ **LM-079 的修复不得顺带改这里**（与批次 C 的 LM-080 同一个函数，避免跨批次冲突）。

**★ 复核中确认的、必须写进修复方案的硬约束**（否则修复会被既有护栏判红）：

1. **LM-078 不得靠改在途键或摘要来修** —— S1.0 已冻结契约 C1/C2/C4 + `PluginRequestIdentityTests.cs:214`
   （`Assert.Equal(IdentityDigest(...), key1)`）要求**在途键逐字等于摘要**；
   `I6`/`R1` 要求同一身份**只发一条** HTTP。⇒ 正解只能是**每个调用方在加入租约前用自己的策略各校验一次**
   （与 C3.4"由等待者侧负责"的既有哲学一致）。
2. **LM-076 的四条路径都要覆盖**：注销（`RevokeSession`）、绝对 TTL、空闲 TTL、`PruneSessionsLocked` ——
   只补注销那一处**不够**（会话过期同样泄露）；且 `_wsClients` 必须**能反查到属于哪个会话**。
3. **LM-077 的 `Save` 是扩展方法且多处调用** ⇒ 改签名必须逐个调用点核对语义，
   不得把"原来静默失败"的调用点悄悄变成"现在抛异常"；`GlobalBlockSave` 与 IO 失败**都要**如实反映为失败。
4. **LM-075 不得动冷启动守卫**（"局域网 + 无有效凭据 ⇒ 停止服务"是**好的**）；
   "实际监听地址"必须以 `LiteWebServer.CurrentListenEndpoint` 为**唯一事实来源**。
5. **LM-083 不得靠"多调一次恢复"敷衍** —— 必须在任何加载/调度**之前**；**LM-084** 新增的期限令牌必须与
   调用方令牌**取最早**（不能把"用户主动取消"降级成"只能等超时"）；**LM-085 不得用"删掉备份"替代**。

**★ 批次 B 派工状态（2026-10-23）**：批次 B 拆为两个**文件所有权不重叠**的子批次并行推进：

| 子批次 | 缺陷 | 分支 | worktree | 任务书 |
|---|---|---|---|---|
| B-1 | LM-075 / LM-076 / LM-077（网页 / 会话 / 凭据） | `task/b2-1-web-session` | `.artifacts/wt-b1` | `.artifacts/brief-batch-b1.md` |
| B-2 | LM-078 / LM-079（插件沙箱策略） | `task/b2-2-plugin-policy` | `.artifacts/wt-b2` | `.artifacts/brief-batch-b2.md` |

⇒ 两个子批次的生产文件集合**互不相交**（B-1：`AppActions.cs` / `MainPanelPage.cs` / `LiteWebServer.cs` /
`WebSessionManager.cs` / `Settings.cs` / `SettingsHelper.cs`；B-2：`PluginExecutor.cs` / `PluginProcessor.cs` /
`CryptoNative.cs`），且与批次 A 的文件集合**同样不相交** ⇒ 可安全并行。
批次 C~E（LM-080~LM-089，12 项）**已复核成立、待派工**。

---

### 4.12 P2 批次 B（LM-075~LM-079）修复与集成记录（2026-10-23）

**批次 B 全部 5 项（LM-075 / LM-076 / LM-077 / LM-078 / LM-079）已修复、已集成到 `fix/p2-batch-a`、已由主审独立复核通过。**

#### 4.12.1 提交序列（保持线性历史，cherry-pick 自两个并行 worktree）

| 提交 | 缺陷 | 来源 | 要点 |
|---|---|---|---|
| `dd50295` | LM-078 | B-2 | 调用侧前移凭据目标校验，修在途租约的白名单旁路 |
| `703083f` | LM-079 | B-2 | `StripPort` 正确处理 IPv6 字面量，修地址碰撞 |
| `7ef594c` | LM-075 | B-1 | 修改监听范围必须重绑服务；界面「实际监听地址」改读真实端点 |
| `dc6488a` | LM-076 | B-1 | WebSocket 连接绑定主会话；注销/过期后已建立的 WS 立即断开 |
| `f66cb0b` | LM-075 | B-1 | 复核返工 R1/R2 —— `DescribeListenAddress` 收为 `internal`；IPv6 地址加方括号 |
| `d8f3661` | LM-077 | B-1 | 口令迁移必须如实报告写盘结果（失败即拒绝登录） |

两个子批次的生产文件集合**互不相交**，与批次 A 的文件集合**同样不相交** ⇒ 并行安全；集成采用 `cherry-pick` 保持线性历史。

#### 4.12.2 修法要点（逐项，均经主审回源码核实）

- **LM-075**：`LiteWebServer` 新增 `internal WebServerBindMode? CurrentBindMode`（`LiteWebServer.cs:112`），在 `Start` 里**按真正用来建监听套接字的那份判定**记录（`:246` 的 `lanMode` ⇒ `:299`），`Stop` 时置 `null`（`:360`）⇒ 与 `CurrentListenEndpoint` **同源**；`AppActions.ApplyWebServerWithResult` 的重绑判据纳入监听范围变化（`AppActions.cs:293` 的 `bindModeChanged`，归一化口径与 `Start` **完全一致**：只有 `Lan` 算局域网，其余含 `Unset` 按仅本机）。界面侧 `MainPanelPage.BuildListenAddressText` 降为**薄壳转发**，`AppActions.DescribeListenAddress`（`:383`）成为「实际监听地址」的**唯一纯逻辑** —— 地址与端口**都**来自真实端点。★ 冷启动守卫（`AppActions.cs:305`）**未动**。
- **LM-076**：`_wsClients` 的值由「运行代次（`int`）」改为 `WsClientBinding`（**运行代次 + 所属主会话 ID**）；票据消费（`TryConsumeTicket(..., out sessionId)`）与握手（`Handshake(..., out sessionId)`）把会话 ID 带出来；新增 `CloseSessionWebSockets(sessionId)` 反查并断开该会话的全部 WS；**四条失效路径全部接入**（`RevokeSession` / 绝对 TTL / 空闲 TTL / `PruneSessionsLocked`）；广播循环逐收件人校验会话存活（`TryTouchSessionForWebSocket`），会话没了即断开。
- **LM-077**：新增 `internal static bool TrySave(this Settings)`（`SettingsHelper.cs:196`）—— 禁止保存 ⇒ `false`（`:198`）、任何 IO/序列化异常 ⇒ `false`、成功 ⇒ `true`；**既有 `public static void Save(this Settings)` 的签名与语义逐位不变**（静默失败、不抛异常）⇒ 20+ 处调用点没有被悄悄变成「现在会抛异常」；`Settings.EnsureWebPasswordMigrated` 改用**真实写盘结果**判定（`Settings.cs:553`），备份清理同样如实反映（`:561`）。
- **LM-078**：**调用侧前移**凭据目标校验 —— 每个调用方在加入租约前用自己的 `declaredHosts` 各校验一次。★ **在途键与摘要一字未改**（S1.0 契约 C1/C2/C4 + `PluginRequestIdentityTests.cs:214` 要求在途键逐字等于摘要；`I6`/`R1` 要求同一身份只发一条 HTTP）⇒ 这是唯一正解。
- **LM-079**：**只改三处** `StripPort`（`PluginExecutor.cs:870` / `PluginProcessor.cs:1060` / `CryptoNative.cs:194`），三处**逐字同构**；**未**顺带改 `PluginProcessor.AddDeclaredHost` 的端口截断（那是新登记的 **LM-094**，归批次 C，避免跨批次冲突）。

#### 4.12.3 新增用例：17 条（`BatchB1WebSessionCredentialTests.cs` 11 条 + `BatchB2PluginHostPolicyTests.cs` 6 条）

- LM-075（4 条）：只改监听范围也必须重绑（反证：重启 ⇒ 旧会话 401）/ 实际监听地址必须反映真实端点而不是配置草稿 / 没有真实端点时不得凭空显示监听地址 / IPv6 显示文本必须带方括号且端口正确。
- LM-076（4 条）：注销后已建立的 WS 不再收到数据（**先断言真的收到过一帧**再注销）/ 空闲 TTL 到期 / 绝对 TTL 到期 / 清理过期会话。
- LM-077（3 条）：保存被禁止 ⇒ 返回 `false` **且磁盘上旧明文仍在** / 写盘失败（独占锁制造 IO 故障）⇒ 返回 `false` 且旧明文仍在 / 迁移成功 ⇒ 返回 `true` 且**主文件与备份都没有旧明文**。
- LM-078/LM-079（6 条）：见 B-2 交付记录。

#### 4.12.4 主审独立复核（本轮，5 个自设计变异全杀）

主审**未采信**实现者自述，自行回源码核实并自设计变异；**锚点与实现者用过的不重合**：

| 变异 | 锚点 | 期望 | 实测 | 还原 SHA-256 |
|---|---|---|---|---|
| MB-A | `LiteWebServer.cs:299` 的 `CurrentBindMode` 赋值改为恒 `Loopback`（事实源断链） | 红 | **红 2 / 绿 9** | 逐位一致 ✅ |
| MB-B | `WebSessionManager.CloseSessionWebSockets` 去掉 `RemoveClient(client)`（需求计数泄漏） | 红 | **红 1 / 绿 10** | 逐位一致 ✅ |
| MB-C | `SettingsHelper.TrySave` 的 `GlobalBlockSave` 分支改为 `return true` | 红 | **红 1 / 绿 10** | 逐位一致 ✅ |
| MB-D | `SettingsHelper.TrySave` 的 `catch` 块改为 `return true`（IO 失败被当成成功） | 红 | **红 1 / 绿 10** | 逐位一致 ✅ |
| MB-E | `MainPanelPage.BuildListenAddressText` 的端点改为**配置草稿**冒充事实 | 红 | **红 1 / 绿 10** | 逐位一致 ✅ |

⇒ **5 / 5 全杀**，每次还原后 SHA-256 与变异前**逐位一致**。★ MB-B 的还原哈希 `B3D79B540995ADA9B24B39995A43E00CC9B5652E5296A0970222E54CBA5DAF09` 与实现者自报的 LM-076 还原哈希**完全相同** ⇒ 交叉印证其字节级还原无误。证据：`.artifacts/b1-mutation/`（`MB-A.txt` ~ `MB-E.txt`）。

**主审额外核实的两点**（不在实现者自述范围内）：

1. **锁序无死锁**：逐处核对 `WebSessionManager` 的 `_clientsGate` / `_authGate` 取用 —— 广播循环（`:2386` 的 `foreach`）**不持有** `_clientsGate`；四条失效路径的 `CloseSessionWebSockets` 一律**在释放 `_authGate` 之后**调用；`RemoveClient` 是同线程重入 ⇒ **不存在 `clientsGate → authGate` 与 `authGate → clientsGate` 的嵌套**。
2. **票据 → 会话 → WS 的绑定链完整**：`CreateTicket` 写入 `SessionId`（`WebSessionManager.cs:1095`）⇒ `TryConsumeTicket` 出参带出（`:1148`）⇒ `Handshake` 出参带出（`:2568`）⇒ `AddClientWithSession` 登记（`:1226`）⇒ `CloseSessionWebSockets` 反查（`:1039`）。

#### 4.12.5 回归与阈值

- 主审亲跑 CI 口径（**构建与测试串行**）：`dotnet build --no-incremental` **0 错误**；`dotnet test --no-build` ⇒ **1547 总 / 1546 通过 / 0 失败 / 1 跳过**（分工程：`LiteMonitor.Tests` 1455/1454/0/1、`LiteMonitor.Updater.Tests` 73/73/0/0、`LiteMonitor.GpuNonperformance.Tests` 19/19/0/0）。
- 阈值：`MIN_TEST_TOTAL` **1530 → 1547**（= 被推送分支实测总数 1455+73+19）；`ci.yml` 与 `release.yml` **逐字一致**；另外 4 个门禁参数（`EXPECTED_TEST_ASSEMBLIES` / `MAX_SKIPPED_TESTS` / `MUST_EXECUTE_TEST_CLASS` / `MUST_EXECUTE_TEST_CLASS_MIN_COUNT`）**逐字一致**；只收紧不放宽；两个文件 **CRLF、无 BOM**。
- ★ 口径说明：1526 → 1530 是 **LM-095** 的 +4；1530 → 1547 是**批次 B 的 +17**（LM-075 4 + LM-076 4 + LM-077 3 + LM-078 6；**LM-079 三处 `StripPort` 同构改动不加用例**）。
- ★ **CI 取证**（★ 2026-10-23 补记：本条此前**遗漏**，主审收尾核查时发现并回填）：run **`39905818210`** @ `cb4450a` = **success**（作业「构建 / 测试 / 打包」）。门禁「强制校验测试发现数」在真实 runner 上的实际输出：**发现 1547 条用例（通过 1546 / 失败 0 / 跳过 1），阈值 1547** ⇒ 阈值**等于**被推送分支的实测总数。日志存档：`.artifacts/ci-run-39905818210.log`。

#### 4.12.6 残留与未验证项（**不计入通过**）

1. **LM-075 界面渲染整体** —— 用例在 STA 线程构造真实 `MainPanelPage` 并调用真实 `BuildListenAddressText`（非纯逻辑替身），但**未**覆盖 `OnShow()` 建卡后的实际控件渲染与 `RegisterRefresh` 的刷新时机 ⇒ `NOT_VERIFIED`。
2. **LM-076 广播侧逐收件人加锁** —— 每轮广播对每个收件人取一次 `_authGate`。正确性优先（避免 TOCTOU），但收件人多时有额外锁开销 ⇒ 记为性能观察项，**未**改动。
3. **LM-076「广播刷新会话空闲计时」这一副作用** —— 「正在收数据的连接即视为会话在使用」，避免只看 WS 的网页在空闲 TTL（2 分钟）到点被踢线（否则属回归）。★ 该副作用**目前没有单独的用例钉住** ⇒ 记为**覆盖缺口**（非缺陷）。
4. **LM-076 空闲 TTL 与「清理过期会话」两条路径判据相同**（都判 `now - LastUsedTicks`）⇒ 生产环境由哪条路径关闭取决于竞态；用例 ④ 用「快照提供器抛异常」把广播侧存活判定排除，使清理路径成为唯一触发者 —— 属**隔离手段**，非生产形态。
5. **LM-076 绝对 TTL 用例**依赖 100 秒步进 + HTTP 快照请求刷新「最后使用时刻」（共 17 步）⇒ 步进节奏若被外部时钟干预存在理论抖动风险。
6. **LM-077 IO 故障注入**依赖 Windows 上对被独占锁文件执行 `File.Move(overwrite)` 必然抛 sharing violation（实测成立）⇒ 该前提在别的文件系统语义下不成立。
7. **LM-076「重定向逐跳复核仍只对租约创建者生效」**（B-2 worker 如实标注为 `NOT_VERIFIED`）—— **未**在本轮解决。
8. **LM-075 用户可见行为变化登记**：局域网模式下真实绑定的是 `IPAddress.IPv6Any`（DualMode，同时接受 IPv4 连接）⇒ 界面显示由旧版的 `0.0.0.0:8080` 变为 **`[::]:8080`**；服务未运行时显示由 `127.0.0.1:{端口}` 变为 **`-:{端口}`**（没有真实端点 ⇒ 不用配置草稿冒充事实）。两者都是**修复的预期结果**，非回归。

#### 4.12.7 本轮新登记的缺陷

**LM-098**（P3）—— 口令迁移成功后**内存里的明文仍保留** ⇒ 每次登录重复写盘，且「迁移早已完成」时的一次写盘失败会**永久拒绝登录**。详见 §4 缺陷表。**本轮不改**：① 重复写盘是**幂等**的（写出的内容与上次一致），对低频登录无实际影响；② 「迁移早已完成仍被拒」属于 **fail-closed 的过度收紧**，不产生凭据泄露；③ 正确修法（迁移成功后清空内存明文）会改变 `WebServerPassword` 的内存语义，影响面超出 LM-077 的修复边界 ⇒ 另立批次处理。

---
### 4.13 P2 批次 C / D / E（LM-080~LM-089 + LM-094）修复与集成记录（2026-10-23）

**P2 全部 20 项（LM-070~LM-089）至此全部修复并集成。批次 C/D/E 共 12 项（LM-080~LM-089 + LM-094），由三个 worker 在三个**文件所有权互不相交**的 worktree 里并行施工，主审逐项回源码复核 + 自设计独立变异。**

#### 4.13.1 三批的文件所有权（并行前提，主审实测）

| 批次 | 缺陷 | 生产文件 | 新增用例 |
|---|---|---|---|
| **C** | LM-080 / LM-081 / LM-082 / LM-094 | `PluginProcessor.cs` / `PluginExecutor.cs` / `CryptoNative.cs` | 58 条 |
| **D** | LM-083 / LM-084 / LM-085 | `Settings.cs` / `SettingsHelper.cs` / `PluginMarket.cs` / `PluginManager.cs` / `MainForm_Transparent.cs` | 12 条 |

★ 批次 D 另有第 4 个提交（`52d0c4a`）：**只动测试** —— 新用例改为**不重建共享配置单例**（见 §4.13.5 末段）。
| **E** | LM-086 / LM-087 / LM-088 / LM-089 | `tools/GpuHostLifecycleProbe/**` / `LiteMonitor.Tests.csproj` / 探针 `AssemblyInfo.cs` / `release.yml` | 15 条 |

⇒ 三批之间**零文件重叠** ⇒ 并行安全；集成按 **C → E → D** 顺序 `cherry-pick`（保持线性历史）。

#### 4.13.2 修法要点（主审逐项回源码核实）

- **LM-080**：`AddDeclaredHost` 取 authority 时在 `'/'`、`'?'`、`'#'` **三者中最先出现处**结束 ⇒ 无路径 URL 的查询串/片段不再被当成主机名。
- **LM-094**：端口截断改走新增私有助手 `StripAuthorityPort` —— **方括号内的冒号不是端口分隔符**（`[fd00::1]` / `[fd00::1]:8080` 的主机都是 `[fd00::1]`），非方括号的多冒号是裸 IPv6 整体保留，单冒号 `host:port` 行为**逐字不变**。与 LM-079 修好的 `PluginSandbox.StripPort` **IPv6 语义一致**。
- **LM-081**：`RewritesMethodOnRedirect` 判据改为**逐状态码**对齐 .NET 原生（见 4.13.4）。
- **LM-082**：`PluginSandboxScope` 新增 `ScopeId`（**每次执行一个 GUID**，目标级作用域继承 owner 的）⇒ 原生在途去重改为**按执行作用域**归并；`CryptoNative` 的共享任务改绑**自己的**中止令牌启动，等待者各自用**自己的**取消令牌决定是否继续等，**所有等待者都放弃**时才中止共享任务（及时释放进程级并发槽位）。
- **LM-083**：`PluginManager.LoadPlugins` **开头**先 `RecoverMarketTransactions(directoryPath)` ⇒ **结构性兜底**，任何调用方都绕不过；`MainForm` 构造函数里再显式排一次"先恢复、后 `LoadPlugins`/`Start`"（顺序一眼可见）。恢复失败**不阻断**加载，但**绝不跳过**恢复。
- **LM-084**：`DownloadBytesAsync` 新增 `internal DefaultDownloadTimeoutMs = 30_000`（覆盖**响应头 + 正文**整段下载）；期限令牌与调用方令牌用 **linked CTS 合并 ⇒ 取最早**；`catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadlineCts.IsCancellationRequested)` ⇒ **只有期限先到**才报超时，**用户主动取消保留取消语义**（不被降级成"只能等超时"）。
- **LM-085**：`AtomicWrite` 不再 `File.Copy` 旧主文件，改走 `WriteSanitizedBackup`（读回旧主文件 → `Settings.SanitizeSettingsJsonForBackup` 脱敏/加密 → 写备份）；脱敏规则与 `PluginSecretDictionaryConverter.WriteDict` **逐条一致**（secret key 且非已保护且非空 ⇒ DPAPI 加密），**DPAPI 失败 ⇒ 从备份里去掉该值，绝不写明文**；脱敏失败 ⇒ **不写备份**（绝不退回"原样复制"）。`WebServerPassword` 脱敏成空串。
- **LM-086**：探针"持续送帧"改用 `System.Windows.Forms.Timer`（**必然**在 UI 线程触发），成功/失败**如实计数**；送帧失败 ⇒ 退出码 **5**；证据文件的 `exitCode` 与**进程真实退出码**一致。
- **LM-087**：隔离门抽成纯函数 `IsolationGate.EvaluateBeforeRun / EvaluateAfterRun`；判据从"此刻有没有在监听"升级为"**配置会不会让它开** + 运行后复查"；门在 `Application.Run` **之前**求值并拒绝（消息循环不启动 ⇒ 生产 `OnShown` 永不执行）；读不到配置时**保守按"会启动"处理**；失败 ⇒ 退出码 **4**。
- **LM-088**：新增 `sourceHashVerified` 与 `sourceHashStatus`；**没做比对**时 `sourceHashChanged` 必须是 **`null`**（"不知道"）而不是 `false`（"没漂移"），并给出退出码 **6**（`NOT_VERIFIED` 必须非零）。
- **LM-089**：`release.yml` 接入与 `ci.yml` **逐字同构**的依赖清单门禁步骤（位置也相同：启动检查之后、跑测试之前）；**未改门禁判据，未改 `ci.yml`**。

#### 4.13.3 主审独立变异自证（**13 个锚点，无一与实现者重合**）

| 变异 | 锚点 | 缺陷 | 实测 | 还原 SHA-256 |
|---|---|---|---|---|
| MC-A | `AddDeclaredHost` 内 authority 截断结果赋值（完全不截断） | LM-080 | **红 4/6** | 逐位一致 ✅ |
| MC-B | `StripAuthorityPort` **函数体内部**（去掉方括号处理） | LM-094 | **红 1/6** | 逐位一致 ✅ |
| MC-C | `ScopeId` 的**生成处**（退化成常量） | LM-082 | **红 1/4** | 逐位一致 ✅ |
| MC-D | 重定向循环的**调用点**（不再调用方法降级判据） | LM-081 | **红 3/42** | 逐位一致 ✅ |
| ME-A | `WriteEvidence` 里抬退出码的判定 | LM-086 | **红 1/4** | 逐位一致 ✅ |
| ME-B | `EvaluateAfterRun` 的运行后复查 | LM-087 | **红 1/5** | 逐位一致 ✅ |
| ME-C | "未验证 ⇒ 退出码 6"的判定 | LM-088 | **红 2/3** | 逐位一致 ✅ |
| ME-D | `release.yml` 门禁的**步骤名**（内容不动） | LM-089 | **红 2/3** | 逐位一致 ✅ |
| MD-A~MD-D | 见 4.13.5 | LM-083/084/085 | 见 4.13.5 | 逐位一致 ✅ |

⇒ **全部被杀，每次还原后 SHA-256 与变异前逐位一致**。★ MC-D / ME-D 的还原哈希与实现者自报的还原哈希**完全相同** ⇒ 交叉印证实现者的字节级还原无误。证据：`.artifacts/c-mutation/`、`.artifacts/e-mutation/`、`.artifacts/d-mutation/`。

#### 4.13.4 ★ 主审推翻实现者的两处（LM-081）

**① 判据与 .NET 原生不一致（R4 返工）**

实现者把"PUT/DELETE 在重定向后如何处理"标为 `NOT_VERIFIED`，并按"非 GET/HEAD 即降级为 GET"实现。主审**补跑扩展探针**（`.artifacts/lm081-probe-ext/`，本机 .NET SDK 8.0.426，裸 `TcpListener` 自写 HTTP/1.1，只监听 127.0.0.1:18100 回环，不触外网），实测：

```
301 PUT    -> observed=[PUT /301 | PUT /final]        finalMethod=PUT      ← 保持 PUT！
301 DELETE -> observed=[DELETE /301 | DELETE /final] finalMethod=DELETE   ← 保持 DELETE！
302 PUT    -> finalMethod=PUT
302 DELETE -> finalMethod=DELETE
303 PUT    -> observed=[PUT /303 | GET /final]        finalMethod=GET      ← 只有 303 才降级
303 DELETE -> observed=[DELETE /303 | GET /final]     finalMethod=GET
307 / 308  => 全部方法保持原方法与正文
```

⇒ **.NET 原生是逐状态码不同**：**301/302 只有 POST 被降级为 GET**（PUT/DELETE/HEAD 等保持原方法与正文）；**303 才把非 GET/HEAD 一律降级**；**307/308 全部保持**。
⇒ 实现者的判据 `(301||302||303) && method != GET && method != HEAD` 在 **301/302 + PUT/DELETE** 上与 .NET 不一致 ⇒ 仍是"自称与 .NET 一致而实际不一致"的**同一类残留**。
⇒ 已返工为逐状态码口径：301/302 仅 POST 降级；303 非 GET/HEAD 降级；307/308 一律保持；注释写入逐状态码实测口径与两份证据文件名。

**② 顺带扩大插件可发 HTTP 方法白名单（R5 返工，回退）**

实现者为让用例能端到端断言"服务端收到 PUT"，新增 `ResolveHttpMethod`（白名单 GET/POST/HEAD/**PUT/DELETE/PATCH/OPTIONS**）。主审回源码核实修复前真实行为（`cb4450a:src/Plugins/PluginExecutor.cs`）：

```csharp
HttpMethod method = HttpMethod.Get;
if (methodStr?.ToUpper() == "POST") method = HttpMethod.Post;
else if (methodStr?.ToUpper() == "HEAD") method = HttpMethod.Head;
// 其余一律 GET
```

⇒ **修复前本仓只发得出 GET / POST / HEAD** ⇒ 生产路径上**根本不存在 PUT/DELETE 请求** ⇒ "301/302 上 PUT 保持原方法"在生产中**不可达**。
⇒ 扩大后第三方插件可对**已声明主机**发 DELETE/PUT/PATCH/OPTIONS —— 这是**新的、更具破坏性的能力**，属需**单独评估与授权**的安全变更，**搭车进一个 P2 缺陷修复不可接受**。
⇒ 已**完整回退**（`ResolveHttpMethod` 删除、方法解析恢复原样、全仓 `HttpMethod.Put/Delete/Patch/Options` **0 命中**）；PUT/DELETE 的断言改为**谓词级**（反射直接调用生产用的同一个私有判据），并**如实标注"生产不可达，防御性对齐"**；HEAD/GET/POST 三条**生产可达**的端到端断言**一条未减**。

★ **这两个问题实现者自审发现不了** —— 其用例集恰好只覆盖 HEAD/GET/POST。这正是"实现者自审不算数、必须主审独立复核 + 本机实测兜底"的价值。

#### 4.13.5 批次 D 的主审独立变异

| 变异 | 锚点（**与实现者不重合**） | 缺陷 | 实测 | 还原 SHA-256 |
|---|---|---|---|---|
| MD-A | `PluginManager.LoadPlugins` 里的 `RecoverMarketTransactions(directoryPath)` **调用点**（摘掉结构性兜底） | LM-083 | **红 2/5** | 逐位一致 ✅ |
| MD-B | `PluginMarket` 里 **linked CTS 的创建表达式**（期限令牌不再并入） | LM-084 | **红 1/3** | 逐位一致 ✅ |
| MD-C | `Settings.SanitizeSettingsJsonForBackup` 里的**凭据加密调用**（不再加密） | LM-085 | **红 2/4** | 逐位一致 ✅ |
| MD-D | `SettingsHelper.WriteSanitizedBackup` 里的**脱敏函数调用点**（退回原样复制旧主文件） | LM-085 | **红 3/4** | 逐位一致 ✅ |

⇒ **4/4 全杀**，每次还原后 SHA-256 与变异前**逐位一致**。证据：`.artifacts/d-mutation/`。

★ **另记一条主审观察（测试污染类）**：批次 D 的新用例初版用 `Settings.Load(forceReload: true)` 读取配置 —— 这会**重建共享配置单例**，
把「监听范围」的解析结果**冻结**在当时的文件状态上（文件里若是 `WebServerEnabled=true` 且**没有**监听范围字段 ⇒ 解析成**局域网**模式），
从而让**同批次里的网页服务用例**起不来，甚至可能让服务真的开成局域网监听。
实现者在收尾自查中自行发现并改为**不碰单例**的 `SettingsHelper.Load(forceReload: true)`。
★ 口径登记：本仓「`Settings` 单例 + `cfgfile` 集合」是既有雷区（`SettingsRecoveryTests` / `WebSessionProtocolTests` 均受其约束），
**新增测试文件若要读写配置，必须避免重建共享单例**。

#### 4.13.6 回归与阈值

- 主审亲跑 CI 口径（**构建与测试串行**）：`dotnet build --no-incremental` **0 错误**；
  `dotnet test --no-build` ⇒ **1632 总 / 1631 通过 / 0 失败 / 1 跳过**
  （分工程：`LiteMonitor.Tests` 1540/1539/0/1、`LiteMonitor.Updater.Tests` 73/73/0/0、`LiteMonitor.GpuNonperformance.Tests` 19/19/0/0）。
- 增量核对：1547（批次 B 终态）+ **58**（批次 C：LM-080 6 + LM-094 6 + LM-081 42 + LM-082 4）
  + **12**（批次 D：LM-083 5 + LM-084 3 + LM-085 4）+ **15**（批次 E：LM-086 4 + LM-087 5 + LM-088 3 + LM-089 3）
  = **1632**，与实测**逐位吻合**，无既有用例回归。
- 阈值：`MIN_TEST_TOTAL` **1547 → 1632**（= 被推送分支实测总数 1540+73+19）；`ci.yml` 与 `release.yml` **逐字一致**；
  另外 4 个门禁参数（`EXPECTED_TEST_ASSEMBLIES` / `MAX_SKIPPED_TESTS` / `MUST_EXECUTE_TEST_CLASS` /
  `MUST_EXECUTE_TEST_CLASS_MIN_COUNT`）**逐字一致**；只收紧不放宽；两个文件 **CRLF、无 BOM**。
- 集成顺序 **C → E → D**，全部 `cherry-pick` 保持**线性历史**（批次 C 6 提交 + 批次 E 4 提交 + 批次 D 4 提交 = 14 提交）。
- ★ **CI 取证**：run **`39914647089`** @ `8796c3f` = **success**（作业「构建 / 测试 / 打包」）。
  门禁「强制校验测试发现数」在**真实 GitHub Actions runner** 上的实际输出：
  **发现 1632 条用例（通过 1631 / 失败 0 / 跳过 1），阈值 1632** ⇒ 阈值**等于**被推送分支的实测总数
  （若阈值被调低或用例被删，该行会变红）。跳过数上限 `MAX_SKIPPED_TESTS=1` 实际跳过 **1** 条
  （`MarketE2ETests.端到端_拉索引_安装_校验_卸载`，联网 E2E，门控 `LITEMONITOR_E2E=1`，CI 故意不设）；
  必须真正执行的 `MarketIndexContractTests` **发现 6 条 / 通过 6 条 / 跳过 0 条** ✓。
  日志存档：`.artifacts/ci-run-39914647089.log`。
- ★ **推送前门禁自证**（不依赖 runner）：主审本机**直接执行 `ci.yml` 里那段脚本本身**，
  参数按 YAML 去引号语义**机械解析**（不手抄）后按 `ci.yml` 的调用形状传入 ⇒
  输出与真实 runner **逐字一致**（`发现 1632 条用例（通过 1631 / 失败 0 / 跳过 1），阈值 1632`），退出码 **0**。
  证据：`.artifacts/pre-push-gate.log`；回归证据：`.artifacts/pre-push-regression.log`。
  ★ 附带一次**真实假失败**：自证脚本首版把 YAML 的单引号当**字面量**传参 ⇒ 门禁以
  「缺少测试程序集 ''LiteMonitor.Tests'」判红 ⇒ 反证该门禁对参数**敏感**（假绿禁令确实有效），
  修正解析语义后转绿 —— **非门禁缺陷**，如实记录。
- ★ **同一 run 上的探针运行时证据**：探针工程因批次 E 的 `ProjectReference` 被
  `dotnet build LiteMonitor.sln` **间接构建**（日志中 3 次出现 `GpuHostLifecycleProbe -> …dll`，
  分别来自 restore、`sln` 构建与启动检查步骤的单独构建）；
  「生产主窗体启动检查（R01）」在真实 runner 上**退出码 0** ⇒ 生产 `MainForm` 在 runner 上**构造成功**。
  ★ 这同时给 §4.13.7 第 10 条提供**正面证据**：只要那条 `ProjectReference` 还在，探针就随 `sln` 构建
  ⇒ 该结构风险当前**未实际发生**（引用一旦移除风险即回来，故仍登记为已知结构风险）。

#### 4.13.7 残留与未验证项（**不计入通过**）

1. **LM-081 的 .NET 对照由主审两份探针提供**（`.artifacts/lm081-probe/` 与 `.artifacts/lm081-probe-ext/`），
   实现者**未亲自重跑**；且 PUT/DELETE 判据**生产不可达**（`FetchRawAsync` 只发 GET/POST/HEAD）⇒ 属**防御性对齐**，
   生产可达的 HEAD/GET/POST 另有端到端证据。
2. **LM-081 带正文的 PUT/DELETE 在 301/302 上"正文保留并重发"**未覆盖 —— 本仓**只有 POST 步骤附带正文**（既有行为未改），
   且 PUT/DELETE 生产不可达 ⇒ 该维度在生产中不存在。
3. **LM-094 无 `[::1]` 回环服务器的端到端** —— 仅覆盖"声明集合 + 凭据目标校验"层面。
4. **LM-082 未覆盖 `PluginManager` 真实重启（代次推进）路径** —— 用例以"同一实例的两个执行作用域"模拟跨代次。
5. **`PluginProcessor.StripAuthorityPort` 与 `PluginSandbox.StripPort` 在退化输入（`"host:"`、`":8080"`）上语义不同**
   （前者按冒号截断、后者保留原串）⇒ **不影响任何真实 URI**（`Uri.Host` 不带尾冒号），但**未写专门用例锁定**。
6. **LM-086 未在无 GPU 环境验证"泵成功但 GPU 不可用"的组合** —— 已用真实 `s1` 场景端到端跑通
   （`scenePump={"sent":5,"failed":0,"required":true,"ok":true}`、真实退出码 0）。
7. **LM-087 `mainform` 场景端到端未跑** —— 该场景会构造生产 `MainForm` 并写用户 `settings.json`（真实副作用）；
   门逻辑与求值顺序由 5 条用例覆盖（含 **IL 顺序断言**）。
8. **LM-088 未构造"源码真的漂移"的运行** —— 需要写 `src/Rendering/**`（本批禁改）；已用真实 `s1` 验证正常路径
   `sourceHashStatus = VERIFIED_UNCHANGED`。
9. **LM-089 未在真实 GitHub Actions runner 上执行该步骤**、未本地执行 `generate-dependency-manifest.ps1 -Mode Check`
   ⇒ `BLOCKED_ENV`（本机无 runner）。
   ★ 澄清：run `39914647089` 走的是 **`ci.yml`**，**不覆盖** `release.yml` 的步骤（发布工作流只在打 tag 时触发）；
   本轮 CI 全绿**不构成** LM-089 的运行时证据，该项**仍为** `BLOCKED_ENV`。
10. **探针工程不在 `LiteMonitor.sln` 中** —— 现在它只因**被测试工程引用**才被 `dotnet build LiteMonitor.sln` 间接构建；
    若将来移除该引用，探针将不再随 CI 构建 ⇒ 登记为**已知结构风险**（非本批引入）。
11. **★ 登记 NOTE：新增 public 成员** —— `PluginSandboxScope.ScopeId`（只读，外部不可改写）与
    `PluginManager.RecoverMarketTransactions`（`public`）是本批新增的 public 成员。
    `ScopeId` 与该类型既有成员（`Token`/`DeadlineUtc`/`MaxDuration`）同为 public 且**只读**，不构成
    "可被外部随意改写的 public 开关" ⇒ **本轮不要求收窄**；若后续要求，可一并收为 `internal`。
12. **★ 登记 NOTE：新增测试文件污染共享配置单例的雷区** —— 见 §4.13.5 末段；
    本仓「`Settings` 单例 + `cfgfile` 集合」是既有约束，**新增测试若要读写配置，必须避免重建共享单例**
    （用 `SettingsHelper.Load(forceReload: true)` 而不是 `Settings.Load(forceReload: true)`）。
13. **★ 登记 NOTE：独立复核者自写的验证用例未整合进主线** —— 主审在收尾核查中发现，
    14 个独立复核产出（`VerifierLm065~069Tests.cs`、`IndependentBatchAAppearanceVerificationTests.cs`、
    `IndependentBatchACapturePolicyVerificationTests.cs`、`VerifierGpuClosureTests.cs`、`VerifierIl.cs`、
    `VerifierP1UpdaterAuditTests.cs`、`VerifyLm045Independent*` 等）**只存在于各自的 worktree**，
    主线 `fix/p2-batch-a` 内**没有**这些文件 ⇒ 这层独立复核护栏**未沉淀为持续回归**。
    ★ 已做**无损保全**：把这些未跟踪文件连同 LM-045 的 12 份变异规格、48 份变异日志、12 份备份，
    用**显式路径**（非 `git add -A`）提交进各自 worktree 的临时分支
    （`verify/batch-a` `3000ad1`、`verify/gpu-t03-t05` `b559a9c`、`verify/p1-batch-audit` `ba5267a`、
    `verify/lm045-ownerA` `0843d06` 等）⇒ 内容已固化进 git 历史，不会因清理目录而丢失。
    ★ **整合进主线本轮不做**：这批用例**未经独立复核**（可能含复核者刻意留下的断言），
    贸然整合会引入红 ⇒ 建议另立小批次，单独评估后再整合。

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
| GPU-001 | GPU T03/T04 | **P1** | GPU B→C | 🟢 **已闭环（独立复核 PASS_WITH_NOTES）** | **根因修复已落地**：`4ade856`（T04）宿主恢复失败 ⇒ 产出 `WindowCapturePolicyCleanup` 交给订阅者并**释放自己**；`5a1eeb7`（T03）`StopGpuPresentation` **无条件**置 null ⇒ 三条重启路径重新可达；清理责任由 `CapturePolicyCleanupLedger` **独立持有**（合同 §6.2 INV-1）。★ 主审巡检曾发现 worker 首版**未订阅**移交事件、`ledger.Add` 从未被调用 ⇒ 清理责任会彻底丢失 ⇒ 已纠正。**实机端到端（真实 GPU 走非干净停止分支）仍属 T07/T09 ⇒ `NOT_VERIFIED`**。**独立复核（§4.5.12）**：以全程序集 IL 扫描 + `StopGpuPresentation` 分支目标分析实测「唯一订阅点 / 事件登记 / 兜底登记 / 无条件置 null / 无第二创建点」全部成立；14 条变异 14/14 被杀（含拆移交出口、`Add` 变 no-op、移除无条件置空） |
| GPU-002 | GPU T02/T04 | P2 | GPU B→C | 🟢 **已闭环（独立复核 PASS_WITH_NOTES）** | `4ade856`（T04）正式采用 `PresentationSession.RecreateWindow`，补齐 R1–R6（代次单调校验 / 恢复失败不改句柄 / 新句柄重读原值 / 代次可观测 / 失败码可区分） |
| GPU-003 | GPU T02/T03 | P2 | GPU B→C | 🟡 **已实现，待实测（独立复核未发现新问题）** | T01 实测判定 **`RECREATE_HANDLE_SAFE`**（句柄事件 `HandleDestroyed → HandleCreated` 有序、代次恰好 +1、桌面像素回读证明重建后 GDI 动态帧恢复）；`5a1eeb7`（T03）新增**只读** `MainFormWinHelper.IsWindowLayered()`（只读 ex-style，**绝不**修改 ⇒ 合同 §1.3 明确否决的"顺手清 `WS_EX_LAYERED`"未发生），真实值喂给 `RenderingRouteRequest.WindowIsLayered` ⇒ 期望路由由 `RenderingRoutePolicy.Decide` 判定。**真实 layered 窗口下的二次校验仍需实机（T09）⇒ `NOT_VERIFIED`** |
| GPU-004 | GPU T04 | P2 | GPU B→C | 🟢 **已闭环（独立复核 PASS_WITH_NOTES）** | `4ade856`（T04）代次判据**先于** `IsWindow`（旧代次 ⇒ 禁止任何 Win32 调用）；代次唯一真相源 = `WindowHandleGeneration.Instance`（裁决 D-G）；`5a1eeb7`（T03）**删除了** `LiveGlassController` 的第二个计数器（⇒ 上一轮 verifier 的 F7 已消除） |
| GPU-005 | GPU T02 + owner A 接线 | P2 | GPU B→C | 🟢 **已闭环（独立复核 PASS_WITH_NOTES）** | pending 仅在「首帧成功」或「受控放弃且停止流程干净」时清除：`03a925a`（owner A 接线）让 `GpuStartupAttempt` 实现 `IGpuStartupAttemptAbandon`（`AbandonPreservingPending`：只注销尝试、**不删** pending 标记、**不复位** `_attemptWritten`）；`4ade856`（T04）的 `ReleaseStartupAttempt(stopClean)` 通过运行时探测衔接该接口。**宿主侧端到端（停止不干净 ⇒ pending 保留）需真实 GPU ⇒ T07/T09** |
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
| LM-060 | GPU T03 自证中实测 | P3 | — | 📌 登记待清理 | `FpsStateTests.需求抖动不会击穿退避窗口` 用真实时间等待 ⇒ 满负载下偶发超时变红。**既有 flaky，非本批引入**（不触碰 FPS 退避任何代码）；本批不动他人文件 |
| LM-061 | GPU 独立复核产出 | P2 | M3 | ✅ **已修复**（48de1a8） | 实现者护栏 EnumerateMethods 不枚举实例构造函数 ⇒ D-G 唯一性护栏在构造函数路径上不承重。已补 GetConstructors 枚举 + 2 条钉住用例 + 主审变异自证（红 → SHA 一致 → 绿）。**当前生产代码无第二实例**，本条属护栏可靠性 |
| LM-062 | GPU 独立复核产出 | P3 | — | 📌 登记知悉（裁决 D-I） | 既有 public NotifyGpuHandleChanged() 无参重载被删除。内部 WinForms 类、非对外库 API、全仓仅 1 处调用点；补兼容重载会**改变原语义** ⇒ 不修 |
| LM-063 | GPU 独立复核产出 | P3 | M3 | ✅ **已修复**（48de1a8） | finally 内登记排在置 null 之前 ⇒ 理论缝隙。已改为嵌套 try/finally（置 null 在最内层）+ 新增可执行 IL 护栏 + 变异自证 |
| LM-064 | GPU 独立复核产出 | P3 | — | 📌 登记，不修 | ledger Pump 预填 Attempts=2 并覆盖 LastErrorCode ⇒ 诊断保真观察（生产路径恒为 2，实际不触发） |
| ENV-003 | S4.1 | P1 | M4 | ✅ 已闭环 | `a8fb2fc`：跳过 7→1；CI 门禁收紧（`MAX_SKIPPED_TESTS: 1` + `MUST_EXECUTE_TEST_CLASS`） |
| ENV-004 | S4.1 | P2 | M4 | ⏳ `BLOCKED_ENV` | 联网 E2E 单独运行，**不进 PR 门禁**；**不计入通过**（§9） |

> **发布准入现状（§9 对当前状态的判定）：`NO_RELEASE`。** 条件 4 不满足（LM-015/LM-017 延后 ⇒ 正式签名未就绪）、条件 7 不满足（LM-015/LM-017 仍为未关闭 P1）、条件 8 不满足（发布顺序未执行）。**合并到 master 与发布是两件事**：合并（快进、零冲突）可进行，发布不可。

---

## 8. 已知基线（用于判断"未下降"）

| 指标 | 数值 | 复现命令 | 核实状态 |
|---|---|---|---|
| 主测试工程（**初始基线**） | 通过 983 / 失败 0 / 跳过 7 / 共 990 | `dotnet test tests/LiteMonitor.Tests -c Debug` | 已实测，连续两次一致 |
| 主测试工程（**CI 口径实测** @`43538bb`） | 通过 **1394** / 失败 0 / 跳过 **1** / 共 **1395** | 设 `LITEMONITOR_PLUGIN_REPO=tests/fixtures/plugin-repo` 后 `dotnet test LiteMonitor.sln -c Debug` | 本轮实测（**审查批 R01~R05 全部修复并集成后**：R01 +4 / R03 +6 / R05 +11 / R04 +25 / R02 +30）；跳过 1 = 唯一剩余联网 E2E（ENV-004）。**CI 侧同口径实测一致**：`39876338644` @`43538bb` 门禁输出「合计 **1487** / 通过 **1486** / 失败 **0** / 跳过 **1**」 |
| 主测试工程（**本地口径实测** @`43538bb`） | 通过 **1388** / 失败 0 / 跳过 **7** / 共 **1395** | **不设** `LITEMONITOR_PLUGIN_REPO` 的同一条命令 | 本轮实测（`$env:LITEMONITOR_PLUGIN_REPO=''`）；与 CI 口径的**用例总数相同**（三工程合计同为 **1487**），差值恰为 6 条市场契约用例（ENV-003，`MarketIndexContractTests`） |
| 更新器测试工程 | 通过 **73** / 失败 0 / 跳过 0 / 共 **73** | `dotnet test tests/LiteMonitor.Updater.Tests -c Debug` | 已实测（由 58 增至 73 —— R05 更新器桥接新增 15 条） |
| GPU 非性能测试工程 | 通过 **19** / 失败 0 / 跳过 0 / 共 **19** | `dotnet test tests/LiteMonitor.GpuNonperformance.Tests -c Debug` | 已实测 |
| **三工程合计（当前）** | **1487**（1395 + 73 + 19） | `dotnet test LiteMonitor.sln -c Debug -v minimal` | CI 口径实测（`LITEMONITOR_PLUGIN_REPO` 指向夹具），退出码 **0**；门禁脚本实测「合计 **1487** / 通过 **1486** / 失败 **0** / 跳过 **1**」、退出码 **0**（`GATE_EXIT=0`）。CI 门禁 `MIN_TEST_TOTAL` 已同步收紧 1316 → 1349 → 1394 → 1396 → **1487**（= **被推送分支** `43538bb` 的实测总数，**不是**集成分支的数字）；`release.yml` 同名阈值**逐字一致** |
| 主测试源码文件 | 顶层 **87** 个 / 含子目录 **93** 个 `.cs` | `Get-ChildItem tests/LiteMonitor.Tests -Filter *.cs -Recurse` | **本轮重新实测 @`43538bb`**（原台账记「顶层 82 / 递归 88」已过时 ⇒ 本批新增 5 个测试文件：`MainFormStartupOrderTests` / `WebPasswordPolicyTests` / `PluginMarketRecoveryBoundaryTests` / `GpuFailureFallbackTests` / `UpdaterLegacyBridgeTests`） |
| 更新器 / GPU 测试源码文件 | 更新器 顶层 4 / 递归 10；GPU 顶层 0 / 递归 6 | 同上 | 已实测 |
| 解决方案测试发现 | **1487**（CI 口径） | `dotnet test LiteMonitor.sln` | **不再是 0** —— BASE-003 已闭环（`0366268` 三个测试工程入 `.sln`）；`assert-test-counts.ps1` 把「发现 0」变红 |
| 两点 / 三点分支差异 | 484 / 522，共同祖先 `169e90a2…` | `git diff --name-only master branch` / `master...branch` | 已实测（BASE-002 起点；**本轮未重测**） |
| Release 构建 | **成功** | `dotnet build LiteMonitor.sln -c Release` | 6 次 CI 全部 success（CI 内含 `dotnet build LiteMonitor.sln -c Release`，`.github/workflows/ci.yml:159`）。本地曾被运行中的 `LiteMonitor++.exe` 锁住输出目录（ENV-001，**非代码缺陷**，实例已自行退出） |
| 远端 GitHub Actions | 本轮 **1 次 failure → 修正后连续 11 次 success**（快照截至 `8626345`） | `gh run list --repo mojingjing-git/LiteMonitorPlus --limit 8` | `39880855065` @`8626345` = **success**（**LM-092 修复 + 台账**，18 步全 success；启动检查退出码 **0**（真实退出码）、门禁合计 **1487** / 通过 **1486** / 失败 **0** / 跳过 **1**、阈值 **1487**）；`39879978619` @`e375f51` = success；`39879199466` @`410192a` = success（LM-090 修复后首次取证：启动检查步骤耗时 **21 秒** ⇒ 真的在等）；`39877889929` @`086ae62` = success；`39877330711` @`176e78e` = success（阈值校正）；`39876338644` @`43538bb` = success（R02 集成）；`39875058697` @`794a9ab` = success（R04 集成，1457 条）；`39874691878` @`acdcd3f` = cancelled（被后续 push 取消，正常）；`39872880204` @`2625f0a` = success；`39872199423` @`a157c1b` = success；`39857370637` @`be3dffd` = success；`39856338946` @`7a2ea69` = success（发现 1396 条）；`39847068945` @`427b35d` = success；`39845677635` @`7283b91` = success；`39844871915` @`cf1c8ee` = success；`39844039100` @`9e384e0` = success；`39836128969` @`61465ba` = success；`39834352321` @`b1a8b42` = **failure**（根因：阈值按**集成分支**实测 1316 设置，而被推送的 `b1a8b42` 实测仅 **1295** ⇒ 门禁**正确**报红，**不是**门禁缺陷） |
| 远端 Release | **仍为 0**（本批不发布） | `gh release list` | 已实测；发布准入见 §9 |
| 本地 HEAD vs 远端 | **P2 批次 A~E 已合并到 master**（`28c7ce8`）；分支 `fix/p2-batch-a` 与 `origin/` 齐平于 `f362190` | `git log --oneline origin/fix/p2-batch-a..HEAD` / `git rev-parse master origin/master` | 已实测（2026-10-23）：`master` 本地与远端同为 `28c7ce8afdafe487e158d346fe8ade552cd264bb`（由 `674e68c` 前进）；`fix/p2-batch-a` 与 `origin/fix/p2-batch-a` 同为 `f362190`（**保留为历史分支**）。★ **合并前后内容一致性已证**：`git diff fix/p2-batch-a master` **为空** ⇒ master 的树与已验证的分支树**逐字节一致**；与 CI 全绿那次 `39914647089` @`8796c3f` 的差异**只有台账 Markdown**（无任何代码差异）。★ 上一批（LM-065~LM-092）的合并记录 `bbdb1db` 与分支 `fix/audit-remediation-v3`（`4140c4e`）**保持不变**（历史批次留档） |

**基线纪律：** 任何步骤完成后，上述**当前实测值**（CI 口径：主工程 共 **1395**（通过 **1394** / 跳过 **1**），更新器 通过 **73**，GPU 非性能 通过 **19**，三工程合计 **1487**）**不得下降**；主工程**总数只增不减**。下降即为回归，必须在同一步骤内修复，不得记为"已知问题"后放行。CI 侧的对应门禁为 `MIN_TEST_TOTAL: 1487`、`MAX_SKIPPED_TESTS: 1`、`MUST_EXECUTE_TEST_CLASS: MarketIndexContractTests (≥6)`（`.github/workflows/ci.yml`，`release.yml` 同名阈值**逐字一致**）—— 跳过数**有上限**、契约类**不许被跳过**，两者均为 S4.1 引入的收紧。

> **本行的重测义务（本轮已履行）：** 上表数字测于**被推送分支 `43538bb`**（**审查批 R01~R05 全部修复并集成后**：R01 +4 / R03 +6 / R05 +11 / R04 +25 / R02 +30）。`MIN_TEST_TOTAL` 已同步上调至 **1487**（`176e78e`），**等于**该分支的实测总数，且 `ci.yml` 与 `release.yml` 两处**逐字一致**。★ **阈值纪律**（本轮实测教训）：阈值**必须**取「**被推送分支**」的实测总数，**不得**取集成分支的数字，也**不得**为求绿下调阈值 —— `39834352321` @`b1a8b42` 就是把集成分支的 1316 写进了只含 `b1a8b42` 的分支（该分支实测仅 **1295**）⇒ 门禁**正确**报红。
>
> **本地口径 vs CI 口径的差异（已解释，非回归）：** 不设 `LITEMONITOR_PLUGIN_REPO` 时，主工程为 1395 / 通过 1388 / 跳过 **7**；设了（指向 `tests/fixtures/plugin-repo`）后跳过降为 **1**，通过升为 **1394** ⇒ 差值恰为 **6** 条市场契约用例（ENV-003），与 §4.2.8 完全吻合。**两口径的用例总数相同（三工程合计同为 1487）**，只有环境门控造成的跳过数不同。

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
| **GPU 独立复核归档** | GPU T03/T04/T05 + owner A 接线 + 裁决 D-H 的**独立复核**（实现者自审不算数） | 隔离 worktree `.artifacts/wt-verify-gpu` | `dd26b4c`（台账归档）；复核证据留在复核方 worktree（`.artifacts/verify-evidence/`，**不入库**） | ✅ **`PASS_WITH_NOTES`**：自写 42 条独立用例 + 自设计 14 条变异（11 条 NEW）⇒ **14/14 全被杀**（`ShaMatch` 14/14、`RedExit=1` 14/14、还原后 `GreenExit=0 & StillRed=0` 14/14）。产出 LM-061~064 + **裁决 D-I** |
| **LM-061 / LM-063 修复** | 修复核产出的护栏盲区与 `finally` 顺序缝隙 | 主审直接做 | `48de1a8`：`tests/LiteMonitor.Tests/RenderingTransitionCoordinatorTests.cs`、`src/UI/UIController.cs` | ✅ 2 条钉住用例 + **主审变异自证**（变异 A/B 各红 1/47 ⇒ 字节还原 SHA-256 一致 ⇒ 绿 47/47 ⇒ `RESULT=PASS`）。LM-062 / LM-064 按**裁决 D-I** 登记不修 |
| **CI 门禁** | 阈值与被推送分支实测总数对齐 | 主审直接做 | `b1a8b42` → `578ba2e` → `fe57544` → `7a2ea69` | ✅ 1316 → **1349** → **1394** → **1396**；`39834352321` 的一次红灯是**门禁正确报红**（阈值误用集成分支数字），非门禁缺陷 |
| **审查批 R01–R05（LM-065~LM-069）** | 独立审查报告的 **5 项 P1** 逐条复核 → 修复 → 集成 | 4 个隔离 worker worktree（`wt-r02`/`wt-r03`/`wt-r04`/`wt-r05`）+ 主审集成 | `a157c1b`(R01) / `3494fbc`(R03) / `0363141`(R05) / `c835a59`(R04) / `9e3ba32`(R02) | ✅ 5 项全部修复并集成；本地回归 **1487 / 1486 通过 / 0 失败 / 1 跳过**；**20 项 P2（LM-070~LM-089）如实标 `REPORTED（待复核）`**，未冒充已确认 |
| **审查批独立复核（LM-065~LM-069）** | 独立 verifier 复核 5 项 P1（**实现者自审不算数**） | 隔离 worktree `.artifacts/wt-verify-p1`（分支 `verify/p1-batch-audit`，基线 `9e3ba32`） | 复核证据留在复核方 worktree（`.artifacts/verify-p1/`，**不入库**） | ✅ **`PASS_WITH_NOTES`**：自写 **60 条**独立用例（含**自研 IL 解码器**，不复用实现者的 `IlInspector`）+ 自设计 **6 条**变异 ⇒ **6/6 全被杀**（红→**SHA-256 逐位一致**还原→绿）；LM-068 与修复前**逐字节对拍**（两份输出 SHA-256 相同）；5 项 P1 全部 **FIXED**；**新发现 LM-090（P1，假绿门禁）+ LM-091（P3，阈值）** |
| **LM-090 修复** | 修独立复核者报出的**假绿 CI 门禁** | 主审直接做（**主审先独立复现**再动手） | `1a94378` | ✅ 改用 `Start-Process -PassThru` + `WaitForExit` 取真实退出码（两份工作流逐字同构）；**主审负向自证**：基线 0（耗时 3912 ms）⇒ 变异 **1（红，打印真实 NRE）** ⇒ 字节还原 SHA `3C84A169…` 逐位一致 ⇒ 0。**第二轮独立复核 `PASS_WITH_NOTES`**（自选另一锚点复现红 / 旧写法作假绿对照 / 时序证据 13 ms vs 1001 ms / 20 个 `$LASTEXITCODE` 读取点全为控制台程序 / 超时分支 182 s ⇒ 红）→ 又产出 **LM-092（P3）** |
| **LM-092 修复** | 修第二轮复核报出的**含空格路径被拆 argv**（误报红） | 主审直接做 | `3cb9e00` | ✅ `-ArgumentList` 手工加引号 + 失败信息列出退出码契约；**主审四组合自证**（健康×无空格=0 / 健康×含空格=0 / 变异×无空格=1 / 变异×含空格=1），还原 SHA `3C84A169…` 逐位一致。**第三轮独立复核 `PASS_WITH_NOTES`**：含空格工作区退出 0 且**截断路径精确判定为 False**、`probe-args-error.log` 数量 0；两份工作流抽取脚本 SHA 相同（`D5387A67…`）且与 YAML `run:` 块逐字相同（各 28 行）；负向四种组合全红；元字符路径 `&`/`(`/`;` 全过 ⇒ 引号处理无新风险；退出码 64 的信息与源码契约一致 |

### 11.2 收尾动作序列（按序执行）

1. ✅ **合入 S3.5 / S3.6 / 主审集成**：由编排方在主工作区 `cherry-pick`（**不 rebase、不改写其历史**）；文件所有权不重叠，均干净合入。
2. ✅ **重跑全量回归（CI 口径）**：`LITEMONITOR_PLUGIN_REPO=$PWD\tests/fixtures/plugin-repo` + `dotnet test LiteMonitor.sln -c Debug` + `assert-test-counts.ps1` + `generate-dependency-manifest.ps1 -Mode Check` ⇒ **三道门禁退出码必须全 0**，且主工程**总数只增不减**。实测：**1280 / 1279 通过 / 0 失败 / 1 跳过**。
3. ✅ **上调 `MIN_TEST_TOTAL`**：`.github/workflows/ci.yml` 由 `1150` → **`1280`**（**等于**当前实测总数；门禁只收紧不放宽）。
4. ✅ **回写 §8 已知基线**与 **§7 追踪矩阵**（LM-039~044 转 `✅ 已闭环`），并在 §4.4 台账补上 S3.5 / S3.6 / 主审集成 / GPU T01 四行。
5. ✅ **独立 verifier 复核**（S3.5 + S3.6 + 主审集成的合并结果）—— **不得由实现者自审**；复核在隔离 worktree 内自做变异。**VERDICT: PASS_WITH_NOTES**（10 轮自设计变异全部变红；契约 6 常量未改；CI 阈值恰等于实测总数；LM-045 被独立实证为真实 P1；发现 LM-047~LM-052 共 6 项）。归档见 §4.3.5。
5b. ✅ **LM-047 修复**（`46f3f9d`）：字节上限在原语层夹到契约天花板（2 MiB 从"约定"变"强制"），新增护栏用例并以变异自证。
6. ✅ **推送** `fix/audit-remediation-v3` 到 origin（已授权）⇒ 本轮最新 `7a2ea69`；CI `39856338946` @`7a2ea69` = **success**（17 步全 success，含"强制校验测试发现数"门禁：发现 **1396** 条 / 通过 **1395** / 失败 **0** / 跳过 **1**）。★ **阈值纪律**：阈值**必须**等于「**被推送分支**」的实测总数，**不得**取集成分支的数字，也**不得**为求绿下调 —— `39834352321` @`b1a8b42` 即是把集成分支的 1316 写进了只含 `b1a8b42` 的分支（该分支实测仅 **1295**）⇒ 门禁**正确**报红。
7. ⏳ **master 合并评估**：**本批尚未合并到 master**（`master` 仍为 `23a6c5c`）。合并的前置条件「本批收敛 + CI 全绿 + 独立复核通过」**三项现已全部达成**（收敛 = 本轮 `7a2ea69` 全量回归 **0 失败**；CI 全绿 = `39856338946`；独立复核 = 两轮 **`PASS_WITH_NOTES`**）⇒ **合并可提交用户评估**，但在获得**新的明确授权**前**不得**执行。
8. ✅ **GPU T01 结论落地**：判定 **`RECREATE_HANDLE_SAFE`** ⇒ 走 GPU 计划 §4.1 主线，**不启用** §9 备用路线；判别位重建分支**保留**，`RecreateWindow` **采用**。回填 GPU 计划 §12.7。
9. ✅ **GPU C 期（T03–T08）启动条件**已齐备并**已启动**：T01 路线结论落地 **且** `src/Rendering/**` 哈希**全程未变**（T01 已实测）**且** M2/M3 门禁确认 ⇒ T03 / T04 / T05 已落地；**T06–T08 与实机验收（G01–G12）仍未完成**。
10. ✅ **LM-045（重定向绕过允许目标，P1）已修并经独立复核闭环**：`198a9d4`（HTTP 步骤路径 + `native://` 路径**两侧**都禁自动重定向 + 逐跳复核目标主机）；**范围由主审集成巡检扩大**（native 侧同构缺口 ⇒ 新登记 LM-053，随本提交一并修掉）。独立复核 **PASS_WITH_NOTES**（§4.5.10）。
11. ✅ **GPU 部分独立复核**（T03 / T04 / T05 + owner A 接线 + 裁决 D-H）：**`PASS_WITH_NOTES`**（§4.5.12）。★ **过程留痕**：复核方第一轮把基线选到**已被取代的中间提交** `aafe949`（与最终提交 `a498583` 同父兄弟，缺 ledger 订阅与 resolver 接线）⇒ 由主审纠正后改用主分支 HEAD 重做；旧基线上的结论**作废**，详见复核报告。复核产出的 **LM-061（护栏盲区）与 LM-063（`finally` 顺序）已由主审修复并做变异自证**（`48de1a8`）；**LM-062 / LM-064 按裁决 D-I 登记不修**。

12. ✅ **审查报告独立复核**（2026-10-23 报告，R01–R25 / LM-065–LM-089）：**主审逐条回源码复核** ⇒ **5 项 P1 全部成立**（R01 另有**实机复现**：`.artifacts/r01-repro/mainform-construction-error.log`，堆栈 `MainForm.IsWindowLayered → ApplyGpuRenderingRoute → ApplyTheme → UIController..ctor → MainForm..ctor` ⇒ 该分支主程序**根本起不来**，而非"只影响新主题"）；**20 项 P2 全部登记为 `REPORTED（待复核）`**，未冒充已确认。
13. ✅ **5 项 P1 修复并集成**（4 个隔离 worker worktree + 主审逐条读 diff + 行尾/BOM 核验后集成）：`a157c1b`(R01) / `3494fbc`(R03) / `0363141`(R05) / `c835a59`(R04) / `9e3ba32`(R02)。每项均带**变异自证**（红 → **SHA-256 逐位一致**还原 → 绿）。
14. ✅ **本批全量回归（双口径）**：CI 口径（`LITEMONITOR_PLUGIN_REPO` 指向夹具）**1487 / 通过 1486 / 失败 0 / 跳过 1**；本地口径（不设该变量）**1487 / 通过 1380 / 失败 0 / 跳过 7**（差值恰为 6 条市场契约用例，ENV-003）。CI 侧同口径一致：`39876338644` @`43538bb` 门禁输出「合计 1487 / 通过 1486 / 失败 0 / 跳过 1」。
15. ✅ **阈值校正**（`176e78e`）：`ci.yml` 与 `release.yml` 的 `MIN_TEST_TOTAL` 由 `1396` → **`1487`**（= 被推送分支 `43538bb` 的实测总数），两处**逐字一致**；`39877330711` @`176e78e` = **success**（18 步全 success）。★ 独立复核者在其基线上独立测得同样的 **1487**（1395+73+19），**完全吻合**。
16. ✅ **5 项 P1 独立复核**（实现者自审不算数）：**`PASS_WITH_NOTES`** —— 自写 **60 条**独立用例（**自研 IL 解码器**，判据与实现者不同）+ 自设计 **6 条**变异 **6/6 全被杀** + LM-068 与修复前**逐字节对拍**（两份输出 SHA-256 相同）；"不得项"逐条核查通过（契约 6 常量逐字未改 / 未删 public 成员 / 未删测试或降断言 / 探针零副作用）。★ **新发现 LM-090（P1）与 LM-091（P3）**。
17. ✅ **LM-090 修复**（`1a94378`）：CI 启动检查门禁**假绿**（WinExe 下 `&` 不更新 `$LASTEXITCODE`）⇒ 改用 `Start-Process -PassThru` + `WaitForExit` 取真实退出码，两份工作流**逐字同构**；**主审负向自证**：基线 0（耗时 3912 ms）⇒ 变异 **1（红）** ⇒ 字节还原 SHA `3C84A169…` 逐位一致 ⇒ 0。★ §4.6.6 同时**更正**了"启动检查已在 runner 实测通过"这条**基于假绿证据**的既往结论。
19. ✅ **主人已拍板两项策略**（2026-10-23）：**R05 按 (C) 发布 + (B) 骨架默认关闭**（桥接本体等 S5.1/S5.2 真实签名授权后再开；当前 (B) 为结构性关闭，见 §4.6.5）；**20 项 P2（LM-070~LM-089）先不动**，等本批合并到 master 后再**按模块分批**做（20 项已全部登记，不会丢）。
20. ✅ **master 合并（本批）—— 已获明确授权并执行**（2026-10-23）：主人问卷（`ask_ae7194170252762a59d9246e`）选择「**合并到 master（推荐）**」。前置条件「本批收敛 + CI 全绿 + 独立复核通过」三项**全部达成**：收敛 = `a2d2899` 全量回归 **0 失败**（CI 口径 1487/1486/0/1）；CI 全绿 = `39882927682` @`a2d2899` = **success**（18 步全 success）；独立复核 = **三轮** `PASS_WITH_NOTES`（第一轮 5 项 P1 / 第二轮 LM-090 修复 / 第三轮 LM-092 修复）。动作：`git checkout master` → `git merge --no-ff fix/audit-remediation-v3` ⇒ **`master` = `bbdb1db`** → 推送 `origin/master`（`23a6c5c → bbdb1db`）；随后 `674e68c` 回写本条合并记录，master 的 CI `39883864043` @`674e68c` = **success**（18 步全 success，合计 1487 / 通过 1486 / 失败 0 / 跳过 1，启动检查退出码 0）。★ **合并前后内容一致性已证**：`git diff fix/audit-remediation-v3 master` **为空**（master 树 == 已验证的分支树，逐字节一致）；与 CI 全绿那次 `8626345` 的差异**只有台账 Markdown**，无任何代码差异。★ **无对外发布副作用**：`release.yml` 是**纯 `workflow_dispatch` 手动触发**（"仓库里没有任何自动触发发布的入口 —— 合并到 master 不会自动签名、不会自动发版"）⇒ 合并只触发 CI，不触发签名/上传。★ `fix/audit-remediation-v3` **冻结为历史分支**（`4140c4e`）；后续 P2 批次从 master 另起分支 **`fix/p2-batch-a`**（`8ca9ade`，含 §4.7 分批计划）。
21. ✅ **master 合并（P2 批次 A~E）—— 已获明确授权并执行**（2026-10-23）：主人指示「**合并吧，合并后我派一个整体的审查**」。前置条件**全部达成**：① P2 **20 项（LM-070~LM-089）+ LM-094 共 21 项全部修复并集成**（A→B→C→D→E 五批）；② 主审独立复核 **17 个锚点全部被杀**（每次还原后 SHA-256 逐位一致；MC-D / ME-D 的还原哈希与实现者自报哈希**完全相同** ⇒ 交叉印证字节级还原无误）；③ CI 口径全量回归 **1632 条 0 失败**（主工程 1540/1539/0/1 + 更新器 73 + GPU 19）；④ 门禁阈值 **1632**（`ci.yml` 与 `release.yml` 逐字一致，其余 4 个门禁参数同样逐字一致，只收紧不放宽）；⑤ CI 全绿 = `39914647089` @`8796c3f` = **success** 且分支头 `39915637187` @`f362190` = **success**，门禁实测「**发现 1632 条用例（通过 1631 / 失败 0 / 跳过 1），阈值 1632**」。动作：`git switch master` → `git merge --no-ff fix/p2-batch-a -F` ⇒ **`master` = `28c7ce8`**（双父 `674e68c` + `f362190`）→ 推送 `origin/master`（`674e68c → 28c7ce8`）；随后回写本条合并记录。★ **合并前后内容一致性已证**：`git diff fix/p2-batch-a master` **为空**（master 树 == 已验证分支树，逐字节一致）。★ **无对外发布副作用**：`release.yml` 是**纯 `workflow_dispatch` 手动触发**（"仓库里没有任何自动触发发布的入口 —— 合并到 master 不会自动签名、不会自动发版"）⇒ 合并只触发 CI，不触发签名/上传。★ `fix/p2-batch-a` **保留为历史分支**（`f362190`）。★ 主人下一步动作：**派一个整体的审查**（合并后）。

### 11.3 需要人工到场 / 外部确认的事项（**不可由 agent 代替**）

| 事项 | 原因 | 状态 |
|---|---|---|
| **M6 GPU 与硬件实机验收 G01–G12** | G05 / G09 需**管理员**；**G08（不同 DPI 屏幕）环境本机不存在**（两屏均 96 dpi） | 全部未跑；G08 记 `BLOCKED` + 人工步骤，**不得用夹具冒充** |
| **真实浏览器跨源读取 / 端到端登录与 WS 握手** | 无浏览器驱动 | 2 条 `BLOCKED_ENV`，**不计入通过** |
| **法律三项复核** | `amd64/msdia140.dll`（VS EULA 再分发条款）、`Mono.Posix.NETStandard 1.0.0`（nuspec 无 SPDX 表达式）、`HidSharp 2.6.4`（`type="file"`，Apache-2.0 由正文识别） | 已如实登记进 NOTICE，**未替主人下结论** |
| **S5.1 / S5.2 真实签名** | 用户已定延后 | 需明确授权后才启动；未启动 ⇒ 发布准入 `NO_RELEASE` |
| **S7 权限架构（LM-021）** | 计划默认延期 | 待授权 |
| **R05 方案 (B)（更新器自验签）的开启时机** | 需**真实签名发布**才有意义；且会改变旧客户端升级通道的行为 | ✅ **主人已拍板（2026-10-23）**：**按 (C) 发布 + (B) 骨架默认关闭** —— 桥接本体等 **S5.1/S5.2 真实签名**获授权后再开（届时只需注入一个抓取委托，**一行开关、不需重写**）。当前 (B) 为**结构性关闭**（生产路径 `options = null`；更新器工程**零网络 API 引用**）。详见 §4.6.5 |
| **20 项 P2（LM-070~LM-089）的批次范围** | 涉及 GPU / 网页 / 插件 / 安装 / 验证 五个模块，工作量与风险差异大 | ✅ **主人已拍板（2026-10-23）并已执行完毕**：**按模块分五批**（A GPU/外观 → B 网页/插件策略 → C 插件沙箱 → D 安装/配置 → E 探针/发布门禁）；**20 项 + LM-094 共 21 项全部修复并集成**（`master` = `28c7ce8`）。每批均走「逐条先复核确认成立 → 修 → 独立复核 → 阈值校正 → CI 绿」，主审独立变异 **17/17 全杀**，CI 口径 **1632 条 0 失败**。详见 §4.8 ~ §4.13 |
| **master 合并授权（P2 批次 A~E）** | 合并是不可逆的对外动作 | ✅ **已获明确授权并执行**（2026-10-23）：主人指示「**合并吧，合并后我派一个整体的审查**」⇒ `master` 由 `674e68c` 前进到 **`28c7ce8`**（`--no-ff` 合并 `fix/p2-batch-a`，双父 `674e68c` + `f362190`），已推 `origin/master`；合并前后内容一致性已证（`git diff` 为空）；**无对外发布副作用**（`release.yml` 纯 `workflow_dispatch` 手动触发 ⇒ 只触发 CI）。若需回退：可在 master 上做一个 **revert 提交**（不建议 force-push） |

**注意：** 任何"PASS"都必须附带 §10 的完整字段与真实命令输出。`DEFERRED` / `NOT_VERIFIED` / `BLOCKED_ENV` **一律不计入**"已关闭"。
