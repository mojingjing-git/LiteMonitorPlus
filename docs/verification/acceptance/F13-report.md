# F13 验收记录 —— 插件步骤失败兜底与倒计时恢复

> 工作包 P-F13 的验收记录。只记录真实执行过的命令与观察到的结果；未执行的场景逐条列入"未覆盖场景"。

## 1. 标识

- 工作包：P-F13（插件步骤失败兜底与倒计时恢复）
- 分支：`fix/F13-plugin-fallback`
- 提交 SHA 列表（`git log --oneline d52c0ab..fix/F13-plugin-fallback`）：
  ```
  6fa5c33 fix(F13): 执行器接入步骤级失败兜底并回补 Holiday HTTPS 主源与元旦兜底
  4c142c8 test(F13): 新增步骤级兜底与元旦倒计时恢复回归用例（红）
  ```
- 基线 SHA（开工时 HEAD）：`d52c0ab`（master）
- 完成日期：2026-10-25
- 执行者：工作包 P-F13 执行智能体（分支 `fix/F13-plugin-fallback`，worktree `.worktrees/f13`）

## 2. 覆盖与结论

- 覆盖 issue：**#611**（「放假倒计时」插件 API 返回 null 后只显示错误标记 ❌）；回归保护 **#235**（启动时节点不可达、恢复后插件数据不刷新）。
- 结论：**☒ 代码层验收通过**　（☐ 部分通过　☐ 阻塞）
- 允许关闭的 issue 与条件：
  - **#611 可关闭（代码层）** —— 步骤级 `continue_on_error` 语义已恢复，`native://newyear` 本地兜底处理器已回补，内置 `Holiday.json` 已升级到 v1.1（https 主源 + 兜底步骤）。
    关闭条件（母计划 §7）：**代码层验收通过 + 协调者批次集成后重跑三个测试项目全量**。
    ⚠️ 本包**未做实机联网验证**（`timor.tech` 主源的真实可达性与返回格式未在本机实测，见 §6），
    因此"接口恢复后自动回切"只由**本地回环服务器 + 注入时钟**的自动化用例证明。
  - **#235 不由本包关闭**：本包只做**回归保护**（既有退避 / 发布租约 / 卸载后不回写用例全部保持绿），
    其修复归属 P-F03（网络值时效、失败恢复与自动选卡）。

## 3. 用例与命令（可从干净检出复现）

> **命令说明（协调者复核后更正）**：派发单里的 `dotnet build LiteMonitor.sln` **本仓可用** —— `LiteMonitor.sln` 在 master 与本 worktree 均存在（`Get-ChildItem -Filter '*.sln'` 可查到）。本包仍按逐工程构建/测试执行，覆盖范围与 `.sln` 等价（主工程 + 三个测试工程）。

| 命令 | 目的 | 结果（发现数/通过/失败） |
|---|---|---|
| `dotnet build LiteMonitor.csproj -c Debug --nologo` | 主工程构建 | 0 错误（2.37 s） |
| `dotnet build LiteMonitor.csproj -c Release --nologo` | 主工程构建 | 0 错误（2.81 s） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug` | 主测试全量 | **1796 通过 / 0 失败 / 7 跳过 / 共 1803**（改动前基线为 1784，见 §7.2） |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/… -c Debug` | GPU 非性能测试 | **19 通过 / 0 失败 / 0 跳过** |
| `dotnet test tests/LiteMonitor.Updater.Tests/… -c Debug` | 更新器测试 | **73 通过 / 0 失败 / 0 跳过** |
| `dotnet test … --filter "FullyQualifiedName~PluginExecutorSandboxTests"`（**红**，`4c142c8` 状态） | 失败→通过证明 | **7 失败 / 32 通过 / 39 总计** ← 红 |
| 同上（**绿**，`6fa5c33` 状态） | 失败→通过证明 | **0 失败 / 39 通过 / 39 总计** ← 绿 |

### 3.1 红→绿明细

红态（提交 `4c142c8`：模型字段 / `NewYearNative` / 可注入时钟 / 测试已就位，**执行器兜底语义尚未接入**）失败 7 条：

```
兜底_主源返回null_发布元旦倒计时而非错误标记
兜底_主源非法JSON_发布元旦倒计时
兜底_主源HTTP失败_发布元旦倒计时
兜底_主源单请求超时_发布元旦倒计时
兜底_主源字段提取失败_半成品不污染变量且不误触发skip_if_set
兜底_主源恢复后自动回切到真实节假日数据
内置Holiday模板_主源HTTPS且带步骤级兜底与本地兜底步骤
```

绿态（提交 `6fa5c33`）全部通过。**每条用例都必须能证伪**：把 `ExecuteStepWithFallbackAsync`
的兜底分支去掉（或把 `continue_on_error` 的默认值改成 true / 把 `IsRecoverableStepFailure`
的返回恒为 false），对应用例立即变红。

### 3.2 跳过说明（既有、与本包无关）

7 条跳过全部是**环境门禁**用例（`PluginRepoFactAttribute` / `E2EFactAttribute` / Node 缺失），
分别为 `MarketE2ETests.端到端_拉索引_安装_校验_卸载` 与 `MarketIndexContractTests` 的 6 条 ——
本包未改动插件市场子系统，跳过数与基线一致。

## 4. 环境

- OS / 版本：Windows 11（10.0.26300 x64），PowerShell 7（7.6.6），.NET SDK 8.0.426
- DPI / 显示器拓扑：不涉及（本包无 UI 改动）
- GPU / 驱动：不涉及（本包不触碰硬件采样链）
- 关键依赖版本：LibreHardwareMonitorLib 0.9.6（传递依赖）、System.Text.Json 10.0.3
- 备注：同机 8 个执行智能体并行构建，`dotnet build/test` 明显变慢属正常现象；本次全部耐心等待完成。

## 5. 新增测试清单

文件：`tests/LiteMonitor.Tests/PluginExecutorSandboxTests.cs`（**扩展既有文件，未破坏既有 20 条用例**）。
**新增 19 条**（统计口径见 §7）。

| # | 用例名 | 验证了什么 |
|---|---|---|
| 1 | `兜底_主源返回null_发布元旦倒计时而非错误标记` | #611 的真实症状：接口返回 `null` ⇒ 发布"距离元旦 N 天"，**不是** `[ERROR]` |
| 2 | `兜底_主源正常_不触发兜底且发布真实节假日数据` | 主源成功 ⇒ 兜底步骤被 `skip_if_set` 跳过（标签仍是主源的节假日名） |
| 3 | `兜底_主源非法JSON_发布元旦倒计时` | 非法 JSON 属业务可恢复失败 ⇒ 兜底生效 |
| 4 | `兜底_主源HTTP失败_发布元旦倒计时` | 非 2xx 状态码属业务可恢复失败 ⇒ 兜底生效 |
| 5 | `兜底_主源单请求超时_发布元旦倒计时` | **单请求超时可恢复**（与"总预算耗尽"区分）⇒ 兜底生效 |
| 6 | `兜底_主源字段提取失败_半成品不污染变量且不误触发skip_if_set` | 失败步骤已提取成功的 `rest=5` **不得**合并回链上下文、**不得**让兜底步骤被误跳过 |
| 7 | `兜底_备用源也失败_发布错误状态而非正常值` | 主源与备用源都失败 ⇒ 发布 `[ERROR]`，**不得**当成正常值 |
| 8 | `兜底_缺省continue_on_error为false_步骤失败仍中断整条链` | 默认语义不变：第二步不得执行（请求数 = 1） |
| 9 | `兜底_外层取消不被吞掉_备用步骤不得执行` | 外层取消**不可**被兜底吞掉 ⇒ 备用步骤不执行 |
| 10 | `兜底_总时长耗尽不被吞掉_仍抛LM009_DEADLINE_EXCEEDED` | 总预算耗尽仍以带原因码的沙箱异常中断 |
| 11 | `兜底_响应体超沙箱天花板不被吞掉_仍抛LM009_RESPONSE_TOO_LARGE` | 沙箱违规（响应体）不可被吞 |
| 12 | `兜底_步骤数超限不被吞掉_仍抛LM009_STEP_LIMIT` | 沙箱违规（步数）不可被吞；兜底步骤与主源**同预算、同步数上限** |
| 13 | `兜底_主源恢复后自动回切到真实节假日数据` | 主源恢复 ⇒ 下一次执行自动回到真实数据，兜底值不残留 |
| 14 | `元旦倒计时_跨年十二三十一日为一天` | 跨年：12-31 ⇒ 1 天，目标日期次年 1-1 |
| 15 | `元旦倒计时_元旦当天为零天` | 元旦当天 ⇒ 0 天（不跳到下一年） |
| 16 | `元旦倒计时_闰年跨过二月二十九日多算一天` | 闰年：2027-02-28 ⇒ 307 天；2028-02-28 ⇒ 308 天 |
| 17 | `元旦倒计时_按本地日期计算_时区跨日结果不同` | 时区：同一瞬间 UTC ⇒ 2 天、UTC+14 ⇒ 1 天（按**本地日期**计算） |
| 18 | `元旦倒计时_不冒充法定节假日_名称恒为元旦且标注本地来源` | 本地兜底**不冒充**法定节假日 / 调休数据：`name` 恒为"元旦"、`source` 恒为 `"local"` |
| 19 | `内置Holiday模板_主源HTTPS且带步骤级兜底与本地兜底步骤` | 随包发布的模板契约：https 主源 + `continue_on_error` + `native://newyear` 兜底步骤 |

## 6. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| `timor.tech` 主源的真实可达性与**返回格式**（本包按上游契约假定 `holiday.name/date/rest`） | 联网访问 `https://timor.tech/api/holiday/next`，核对字段名是否仍与模板一致 | 用户 / 协调者 |
| 主源**恢复**后的**实机**自动回切（界面从"元旦 N 天"回到真实节假日） | 断网 → 恢复网络 → 等待插件下一轮（`interval` = 14400 s ≈ 4 h，或手动刷新）观察任务栏 | 用户 |
| `native://newyear` 在**非 UTC+8 时区**机器上的显示 | 改系统时区后启动应用，观察元旦倒计时 | 用户 |
| 任务栏上"🏖️ 5 天"与"距离国庆 5 天"的**显示效果**（窄任务栏截断、emoji 字形） | 实机观察 | 用户 |
| 多语言界面下"元旦 / 放假倒计时"的**本地化**（当前内置模板的 label/unit 为中文，与其它内置插件一致，未做 `_en` 变体） | 切换界面语言观察 | 用户 |
| 插件市场内**第三方插件**是否也需要 `continue_on_error`（能力已恢复，但市场侧无模板改动） | 市场插件作者自行验证 | 用户 |

## 7. 偏离与遗留

### 7.1 与派发单的偏离

1. **构建命令**：本包按逐工程构建执行（覆盖与 `.sln` 等价；`LiteMonitor.sln` 实际存在，详见 §3 协调者复核说明）。
2. **短标签**：内置 `Holiday.json` 的 `short_label` 由 fork 原值 `"放假"` 改回上游 v1.1 的 `"🏖️"`。
   理由：取证报告 §4.4 把该项列为 v1.0 降级点之一；emoji 与界面语言无关、且不挤占窄任务栏宽度。
   若协调者认为该 UI 变更超出本包范围，回退该行即可，不影响兜底语义。
3. **模板 `author` 保持 `"LiteMonitor++"`**（未抄上游的 `"LiteMonitor"`）：保持 fork 身份标识，不回退 fork 既有约定。
4. **未改 `resources/lang/*.json`**：本包没有新增界面文案 key —— 新增的"元旦"是**插件数据内容**
   （由 `native://newyear` 产出，随模板的 `{{name}}` 进入 label），不是界面 chrome；
   且内置插件模板整体为中文（与其它 11 个内置插件一致），本包不单独为一个内置插件引入 `_en` 变体。

### 7.2 新增发现数（供协调者上调 `MIN_TEST_TOTAL`）

- `tests/LiteMonitor.Tests`：**+19** 条新用例（`git diff --numstat` 确认：**新增 19 个 `[Fact]`、删除 0 个**）。
- `LiteMonitor.GpuNonperformance.Tests`：0 条；`LiteMonitor.Updater.Tests`：0 条。

三个测试工程的**发现数合计**（与 `.github/workflows/ci.yml` 的 `assert-test-counts.ps1 -MinTotal`
口径一致 —— 它统计三个工程的 TRX 结果总数）：

| 工程 | 改动前 | 改动后 |
|---|---|---|
| LiteMonitor.Tests | 1784 | **1803** |
| LiteMonitor.Updater.Tests | 73 | 73 |
| LiteMonitor.GpuNonperformance.Tests | 19 | 19 |
| **合计** | **1876**（= 当前 `MIN_TEST_TOTAL`，说明基线口径吻合） | **1895** |

⇒ **建议协调者把 `ci.yml` 与 `release.yml` 的 `MIN_TEST_TOTAL` 统一上调到 `1895`（或更高）**，
两个文件必须**逐字一致**；`MAX_SKIPPED_TESTS: 1` **不得放宽**。

⚠️ **CI 阈值未动**（`.github/workflows/ci.yml` / `release.yml` 一字未改），按硬约束由协调者在批次集成时统一上调。


### 7.3 实现方式说明（供复核）

**① 步骤级 `continue_on_error`（`src/Plugins/PluginModels.cs`）**

```csharp
[JsonPropertyName("continue_on_error")]
public bool ContinueOnError { get; set; } = false;   // 默认 false
```

**② 兜底语义（`src/Plugins/PluginExecutor.cs` 新增 `ExecuteStepWithFallbackAsync`）**

- `continue_on_error = false` ⇒ 路径与既有实现**逐字一致**（异常照旧上抛）。
- `continue_on_error = true` ⇒
  1. 用**临时 context**（`new Dictionary<string, string>(context)`）执行该步骤，
     **只有成功**才 `foreach (var kv in stepContext) context[kv.Key] = kv.Value;` 合并回链上下文
     ⇒ 失败步骤留下的 ❓/❌ 不会污染链上下文，也不会让后续步骤的 `skip_if_set` 误判"已有值"。
  2. 只有**业务可恢复失败**被吞掉并继续下一步，判据见 `IsRecoverableStepFailure`：
     - 可恢复：`HttpRequestException`（网络故障 / 非 2xx）、`JsonException`（非法 JSON）、
       `PluginStepDataException`（字段提取到 ❓/❌）、`PluginClientDisposedException`（客户端被重置）。
     - **不可恢复**（一律 rethrow）：`PluginSandboxException` 的**任何**原因码
       （并发 / 响应体 / 步数 / 目标数 / 总截止 / 凭据目标 / 重定向）、外层取消、总时长耗尽，
       以及**任何未列入上表的异常类型**（未知错误不得被降级成"看起来正常的值"）。
  3. **单请求超时 vs 总预算耗尽**：取消异常按**令牌状态**分类 ——
     外层 `token.IsCancellationRequested` ⇒ rethrow；`scope.Token.IsCancellationRequested`（总截止）⇒ rethrow
     （内核已转成 `PluginSandboxException(DeadlineExceeded)`，此处再判一次防止竞态降级）；
     两者皆未取消 ⇒ 只可能来自**单请求期限**（`HttpClient.Timeout` / 正文读取期限）⇒ 可恢复。
- 兜底步骤与主源**共用同一沙箱作用域**：同一总时长截止、同一步数上限、同一发布租约。

**③ 本地兜底路径（`src/Plugins/Native/NewYearNative.cs` + `native://newyear`）**

- 纯本地计算（不触网 ⇒ 不占并发槽位、不产生响应体），但仍走 `native://` 的统一入口与沙箱闸门
  （`PluginExecutor.ExecuteNativeAsync`），不绕过 native 白名单与资源限制。
- 时钟可注入：`PluginExecutor.Clock`（`internal`，生产 `TimeProvider.System`，测试注入固定时钟）
  ⇒ 跨年 / 元旦当天 / 闰年 / 时区都能被**确定性**测试覆盖（禁用 `Thread.Sleep`）。
- **不冒充法定节假日数据**：`name` 恒为"元旦"、`source` 恒为 `"local"`；
  只算元旦这一天的日期差，**不推测调休安排**；模板的 `description` 明写"接口无数据或不可用时显示元旦倒计时
  （仅按本机日期计算，不含调休安排）"。

**④ 兼容既有配置与插件身份（迁移）**

- 内置模板 `id` 仍是 `Holiday`、输出 key 仍是 `Holiday.next` ⇒ 用户 `settings.json` 里的
  `PluginInstances`（`TemplateId = "Holiday"`）、实例 id、排序与别名**全部不受影响**，
  **不存在把用户配置静默绑定到另一插件**的情况。
- `PluginInstanceConfig` 没有版本字段（模板 `meta.version` 只是元数据）⇒ 版本 1.0 → 1.1 不触发实例重建。
- 内置插件位于 `AppContext.BaseDirectory/resources/plugins`，随程序升级整体替换 ⇒ 升级即生效，无需用户操作。

### 7.4 发现的相邻问题（不属于本包，建议归属）

1. **构建命令采用逐工程方式**（协调者复核更正：`LiteMonitor.sln` **存在**于 master 与本 worktree，命令本身可用；本包逐工程构建覆盖等价）：
   建议协调者要么补一个解决方案文件，要么把文档里的命令改成逐工程。
2. **`resources/plugins/*.json` 全部没有 `_en` 变体**：`PluginMeta` / `PluginOutput` 支持
   `name_en` / `label_en` / `short_label_en`，但 12 个内置插件**一个都没用** ⇒ 英文界面下内置插件的
   label/unit 仍是中文。建议单独排一个"内置插件本地化"包（不在本包范围）。
3. **`resources/plugins/Holiday.json` 的 `interval` 是 14400 s（4 小时）**：
   倒计时类插件一天只刷新两次尚可，但接口故障时的**恢复可见性**依赖它 —— 建议协调者评估是否降到 3600 s
   （本包**未改**：改刷新频率属于产品策略，且会改变公网请求量）。
4. **插件开发文档标题仍是 `v1.0`**（`PLUGIN_DEV_GUIDE.md` 首行 `# LiteMonitor++ 插件开发完全指南 (v1.0)`），
   本包只补了 4.3.1 节，未动标题版本号 —— 建议协调者统一处理。
