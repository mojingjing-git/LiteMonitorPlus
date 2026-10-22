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

**当前无 P0。** 若任一执行步骤新发现 P0，立即挂起其他任务优先处理。

**编号规则：** `LM-` 缺陷 / `SEC-` 安全 / `BASE-` 基线 / `ENV-` 环境事实。第三方评审使用的 `LM-018`、`LM-019` 与 `LM-003b`、`LM-003c` 指同一处代码，已合并并保留别名以便交叉引用；`LM-005/006` 为同一协议缺陷的两个侧面。**新增缺陷从 `LM-030` 起顺延，不得复用已关闭编号。**

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

| 缺陷 | 步骤 | 优先级 | 门禁阶段 |
|---|---|---|---|
| BASE-002 | S0.1 | P1 | M0 |
| BASE-003 | S0.2 | P1 | M0 |
| BASE-004 | S0.3 | P1 | M0 |
| BASE-005 | S0.0 | P1 | M0 |
| LM-001 | S1.1 | **P2**（原 P1，根因更正见 §4.1） | M1 |
| LM-002 | S1.2 | **P1** | M1 |
| LM-003a | S1.2 | P2 | M1 |
| LM-003b / LM-018 | S1.3 | P2 | M1 |
| LM-003c / LM-019 | S1.3 | P2 | M1 || SEC-001 | S2.2 | **P1** | M2 |
| SEC-002 | S2.2 | **P1** | M2 |
| LM-004 | S2.1 | **P1** | M2 |
| LM-005/006 | S2.0 + S2.2 | **P1** | M2 |
| LM-023 | S2.2 | P2 | M2 |
| LM-007 | S3.1 | P2 | M3 |
| LM-008 | S3.1 | P2 | M3 |
| LM-009 | S3.2 | P2 | M3 |
| LM-010~014 | S6.2 | P2 | M6 |
| LM-015 | S5.1 + S5.2 | **P1** | M5 |
| LM-016 | S4.2 | **P1** | M4 |
| LM-017 | S5.1 | **P1** | M5 |
| LM-020 | S6.2 | P2 | M6 |
| LM-021 | S7 | P3 | M7（可延期） |
| LM-024 | S5.3 | **P1** | M5 |

---

## 8. 已知基线（用于判断"未下降"）

| 指标 | 数值 | 复现命令 | 核实状态 |
|---|---|---|---|
| 主测试工程 | 通过 983 / 失败 0 / 跳过 7 / 共 990 | `dotnet test tests/LiteMonitor.Tests -c Debug` | 已实测，连续两次一致 |
| 更新器测试工程 | 通过 13 / 失败 0 / 跳过 0 / 共 13 | `dotnet test tests/LiteMonitor.Updater.Tests -c Debug` | 已实测 |
| 主测试源码文件 | 64 个 `.cs` | `Get-ChildItem tests/LiteMonitor.Tests -Filter *.cs` | 已实测 |
| 解决方案测试发现 | **0（静默空跑，退出码 0）** | `dotnet test LiteMonitor.sln` | 已实测——S0.2 目标 |
| 两点 / 三点分支差异 | 484 / 522，共同祖先 `169e90a2…` | `git diff --name-only master branch` / `master...branch` | 已实测 |
| Release 构建 | **失败**（ENV-001 锁定） | `dotnet build LiteMonitor.sln -c Release` | 已实测 |
| 远端 Actions / Release | 均无 | `gh run list` / `gh release list` | 已实测 |

**基线纪律：** 任何步骤完成后，上述"通过 983 / 通过 13"两项**不得下降**。下降即为回归，必须在同一步骤内修复，不得记为"已知问题"后放行。

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

1. **S0.0**：确认是否停止运行中的 `LiteMonitor++.exe`，或改用独立发布目录（ENV-001 阻断 Release 构建）。
2. **S0.2**（mule）与 **S0.1**（explore）可同时派发。
3. S0.2 完成后派发 **S0.3**（worker，建立最小 CI）。
4. M0 门禁通过后，进入 **S1.0**（mavis 冻结在途租约契约），再并行派发 S1.1 / S1.2 / S1.3。

**注意：** 本计划是待执行文档，尚未开始实施。任何"PASS"都必须附带 §10 的完整字段与真实命令输出。
