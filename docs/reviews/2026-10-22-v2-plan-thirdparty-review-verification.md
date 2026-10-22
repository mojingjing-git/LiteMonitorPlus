# V2.0 计划第三方评审：断言核对与复审

日期：2026-10-22。核对结论：**该评审的 20 项事实断言中 19 项成立、1 项需限定条件；1 处引用行号有误。未发现虚构或夸大。** 其结论与建议应整体采纳，但需补三处它未覆盖的事实。

审查对象：用户提交的 V2.0 计划第三方评审文本（本轮附件）。

核对边界：本次在 `G:\TOOL\LiteMonitor-fork` 实际读取源码、运行两个测试工程、比对 Git 分支、查询远端 GitHub 状态。**未修改任何产品源码、未提交、未启动 GUI、未提权。**

---

## 一、总体判断

这份评审比我上一轮的评审**在两处更深入**，并且**纠正了我的一处表述错误**：

1. **更深入**：它发现了 `UpdateSignatureTests` 的"提前 return 仍记为 Passed"假绿、`PluginExecutor` 在途合并的 `GetOrAdd` 并发工厂与双份 `TryRemove` 竞态、`NOTICE` 依赖版本与 csproj 不一致、以及"校验后按路径重开文件"的 TOCTOU 交接边界——这四点我上一轮均未覆盖。
2. **纠正我**：我上一轮写"7 条跳过全是依赖真实插件索引仓库"。实际是 **6 条本地插件仓库副本门控（`LITEMONITOR_PLUGIN_REPO`）+ 1 条联网 E2E（`LITEMONITOR_E2E=1`）**，其 LM-007 的分类更准确，且给出了正确处方（把 6 条接入受版本控制的固定夹具）。

它与我上一轮评审**独立收敛出同一批核心结论**：测试资产在本地而非旧仓库、解决方案级 `dotnet test` 静默空跑、分支基线必须冻结 SHA、Release 产物被运行中实例锁死、发布元数据未签名。两份评审互不引用却吻合，说明这些是真实缺陷而非某人的偏好。

**建议：整体采纳其 P1 六项与 P2 各项，并在其基础上补入三处它遗漏的事实（见 §四）。**

---

## 二、逐条核对结果

### 2.1 已核实成立的断言（19 项）

| # | 评审断言 | 核对结果 | 依据 |
|---|---|---|---|
| 1 | 主测试 990 项 = 983 通过 / 0 失败 / 7 跳过 | **成立**，与我的独立测量完全一致 | `dotnet test tests/LiteMonitor.Tests -c Debug` 连续两次：983/0/7 |
| 2 | 更新器独立测试 13 项全部通过，不在 990 内 | **成立** | 我实测 `LiteMonitor.Updater.Tests`：通过 13 / 失败 0 / **跳过 0** / 总计 13，240 ms |
| 3 | 解决方案测试入口退出码 0、不执行测试 | **成立** | `dotnet test LiteMonitor.sln` → EXITCODE=0，零测试输出。`LiteMonitor.sln` 仅 2 个 Project 条目 |
| 4 | 主测试 64 个非生成源码文件，仍被 `.gitignore` 排除 | **成立** | `tests/LiteMonitor.Tests` 实测 64 个 `.cs`；`.gitignore:18` 为 `/tests/` |
| 5 | 打包、签名脚本本地存在但 master 未跟踪 | **成立** | `scripts/` 实有 `pack-release.ps1`、`sign-release.ps1` 等 12 个文件；`.gitignore:19` 为 `/scripts/`；`git ls-files scripts` 为空 |
| 6 | 远端无 Actions 运行记录、无 Release | **成立** | `gh run list` / `gh release list` 均 EXIT=0 且输出为空；仓库 `public`、`default_branch=master` |
| 7 | 484（端点比较）与 522（共同祖先比较）数字差异 | **成立，数字精确** | 两点 `git diff --name-only master branch` = **484**；三点 `master...branch` = **522**；`merge-base` = `169e90a2ccaa68ca29d0b549d10c6c59634c1b49` |
| 8 | `codex/full-fork-remediation` 无登记 worktree | **成立** | `git worktree list` 仅有 `.worktrees/codex-integrate-performance-sampling`(97f793d) 与 `.worktrees/codex-performance-sampling`(ec62fe4)，remediation 分支无 worktree |
| 9 | `UpdateSignatureTests.cs:274` 找不到私钥即 `return`，仍记 Passed | **成立** | `UpdateSignatureTests.cs:273-274` `if (keyPath == null) return;` —— 但**须限定**，见 §三 |
| 10 | `PluginMarket` 缺摘要仍可安装 | **成立** | `PluginMarket.cs:155` `if (string.IsNullOrEmpty(expected)) return true;` |
| 11 | 安装"先写文件、后写记录"，非同一事务 | **成立** | `PluginMarket.cs:321` 写文件 → `:324-332` 才 `PluginMarketState.Upsert` |
| 12 | `GetOrAdd` 工厂可能并发执行多次 | **成立** | `PluginExecutor.cs:324` `_inflightRequests.GetOrAdd(cacheKey, _ => FetchRawAsync(...))` |
| 13 | 旧任务完成可能误删新条目 | **成立，且比其描述更严重** | `PluginExecutor.cs:346`（catch）与 `:351`（finally）**各做一次无条件 `TryRemove`**；finally 在**成功路径也执行**。旧任务收尾可能删掉同键的新条目 |
| 14 | 插件响应未释放 | **成立** | `PluginExecutor.cs:509` `var response = await client.SendAsync(...)`，无 `using` |
| 15 | 更新元数据的请求、响应、JsonDocument 均未释放 | **成立** | `UpdateChecker.cs:371` request、`:381` resp、`:388` `JsonDocument.Parse` 三处均无 `using` |
| 16 | 插件正则处理无匹配期限 | **成立** | `PluginProcessor.cs:130` `Regex.Replace(val, t.Pattern, replacement)`，`:132` `catch { return val; }` 吞掉超时/回溯异常 |
| 17 | 镜像测速用 `ResponseHeadersRead` 后正文读取无独立期限 | **成立** | `UpdateChecker.cs:373` `SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)`，正文在 `:386` 另行读取 |
| 18 | `NOTICE` 写 9.0.10、项目引用 10.0.3 | **成立** | `NOTICE:40` `System.Text.Json 9.0.10`；`LiteMonitor.csproj:52` `Version="10.0.3"` |
| 19 | 主项目复制预构建更新器 | **成立** | `LiteMonitor.csproj:87-90` `Content Include="resources\LiteMonitor++.Updater.exe"` |
| 20 | 5 秒请求头期限 + 64 连接上限 | **成立**（行号引用有误，见下） | 5 秒在 `WebSessionManager.cs:353` `DefaultHeaderReadTimeout` / `:362`；64 上限在 `LiteWebServer.cs:66` `MaxConcurrentConnections = 64` |
| 21 | `GpuVramTests` 在无该型号显卡时直接 return 显示 Passed | **成立** | `GpuVramTests.cs:56` `if (!actual.HasValue) return;`，且 `:51-52` 是写死 "RTX 3080/3080 Ti" 的本机实测值 |
| 22 | 主程序校验摘要后，更新器按路径重新读取 ZIP | **成立** | `UpdateDialog.cs:369` `ComputeFileSha256(_context.SavePath)`；`LiteMonitor.Updater/Program.cs:48` `string zipFile = args[0];` 事后重开 |
| 23 | 6 条本地契约测试 + 1 条联网 E2E | **成立，且纠正我上一轮的表述** | `PluginRepoFactAttribute.cs:21` 门控 `LITEMONITOR_PLUGIN_REPO`（本地目录）；`E2EFactAttribute.cs:19` 门控 `LITEMONITOR_E2E=1`，跳过理由明写"联网 E2E，会真实访问 GitHub" |

### 2.2 需修正的引用（1 处）

**5 秒请求头期限的行号引用错误。** 评审写 `LiteWebServer.cs#L47`，实际该行是 `ListenAddress` 接缝的注释（"null（默认）= 生产行为：IPAddress.IPv6Any…"）。真正的期限定义在 `WebSessionManager.cs:353`：

```csharp
private static readonly TimeSpan DefaultHeaderReadTimeout = TimeSpan.FromSeconds(5);
```

并且该处的注释质量很高（明确指出 `NetworkStream.ReadTimeout` 对 `ReadAsync` 无效、必须用取消令牌对抗 slowloris）。**不影响其结论，但引用应更正**——否则实施者会跑错文件。

---

## 三、必须限定条件的一条：签名测试的"假绿"

评审第 3 条称"本次未复制私钥，该测试也随 983 项一起通过"。**该描述对他们的隔离副本成立，对本机工作副本不成立。**

`UpdateSignatureTests.FindSigningKey()`（`:311-320`）自测试输出目录向上最多 8 级查找 `secrets/update-signing-key.pkcs8`。本机：

- 测试输出目录 = `tests/LiteMonitor.Tests/bin/Debug/net8.0-windows/`
- 向上 5 级 = `G:/TOOL/LiteMonitor-fork`，其中 **`secrets/update-signing-key.pkcs8` 确实存在**
- 因此该用例在**本机**找到了私钥，`Assert.True(TryVerifyUpdateSignature(...))` **真实执行并通过**

结论应分两档表述：

- **本地基线不是假绿**——我实测的 983 项通过中，这一条执行了真实断言；
- **全新检出与 CI 一定是假绿**——私钥被 `.gitignore:26`（`/secrets/`）排除，干净环境走 `return` 分支，xUnit 记为 Passed 而非 Skipped。

因此这条缺陷的准确表述不是"测试没验到签名"，而是**"在缺失私钥的环境中，该用例以 Passed 冒充通过，使干净检出无法暴露密钥与内置公钥不匹配"**。评审给的处方（PR CI 用测试密钥；正式发布任务强制验证真实私钥；缺钥必须失败）**正确且应当采纳**，但不要据此否定已有 983 项基线中这条的效力。

顺带一条它未提及的事实：`scripts/sign-release.ps1` 内有同口径自检（`UpdateSignatureTests.cs:266-267` 的注释亦确认），所以"密钥与内置公钥错配"这一失败模式在发布脚本层已有第二道防线，缺的是**让这条防线在 CI 里必然触发**。

---

## 四、它未覆盖、但需并入 V2.0 的三处事实

### 4.1 在途合并键的现状被它描述得更严重，且它给出的修法不完整

评审第 10 条已抓到 `GetOrAdd` 与 `TryRemove` 的问题，但它的修法表述是"定义统一请求身份……增加……旧任务完成不误删新条目的测试"，**只提了测试，没有给出足以让这些测试通过的结构性改法**。实测确认的真实现状是三重叠加：

```
PluginExecutor.cs:324   GetOrAdd(cacheKey, _ => FetchRawAsync(...))   ← 工厂可并发执行多次 → 并发重复下载
PluginExecutor.cs:346   catch 内 TryRemove(cacheKey, out _)             ← 与 finally 重复
PluginExecutor.cs:351   finally 内无条件 TryRemove(cacheKey, out _)     ← 成功路径也删 → 合并窗口只在"请求进行中"
```

仅把 `finally` 的 `TryRemove` 改为"仅当条目仍指向自己这个 Task 才删"（比较后再删）**并不足以**解决 `GetOrAdd` 工厂并发问题——需要 `GetOrAdd` 返回的 Task 与条目身份可比对（典型做法是包装成带自增计数的租约对象，或改用 `Lazy<Task<string>>`）。建议 V2.0 把这条写成一个带结构的改造任务，而不是一条"补测试"的任务。

### 4.2 失败重置网络客户端与在途合并的联动，两份评审都未点出

`PluginManager.cs:391-396`：连续失败达 3 次时调用

```csharp
_executor.ResetNetworkClients();
_executor.ClearCache(inst.Id);
```

`ResetNetworkClients()` 会替换 `HttpClient` 实例（`PluginExecutor.cs:78-83` 更换并释放旧客户端）。这与 4.1 的在途合并是**同一个共享资源**：重置发生时，正在被其他实例等待的在途请求可能被打断；而 `ClearCache(inst.Id)` 只清本实例缓存，其他实例的在途条目仍在。V2.0 同时改 LM-002（合并键/生命周期）与 LM-001（计时器重试）时，必须显式写明这两处的交互，否则会出现"修了合并、坏了重试"或反之。

### 4.3 LM-002 的现状描述有一处需要修正，避免实施者改错方向

评审写"当前键使用实例、步骤及 URL／正文的 `GetHashCode()`"——正确。但它随后要求"避免以易碰撞的字符串哈希作为等价依据"，而 V2.0 若照此执行，需注意：**`inst.Id` 与 `step.Id` 已经在键内**（`PluginExecutor.cs:285` `cacheKey = $"{inst.Id}{keySuffix}_{step.Id}_{contentHash}"`）。真正缺失的是 **HTTP 方法、影响响应的 Header、代理配置**这三项，以及用 `GetHashCode()` 这一**进程内加盐、每次运行可能不同**的 32 位哈希做等价判定。把"实例/步骤身份"当成待补项会导致重复编码或改错对象。

---

## 五、采纳建议

| 评审条目 | 采纳方式 |
|---|---|
| P1 第 1 条（分支基线） | **采纳，并补**：`merge-base = 169e90a2…`（= 上游原始克隆点），三点 522 与两点 484 必须分别标注口径；建议 V2.0 固定"以 master 端点为发布基线，remediation 分支按提交选择性移植" |
| P1 第 2 条（可复现范围） | **采纳**：M0 同时纳入 `LiteMonitor.Tests` + `LiteMonitor.Updater.Tests`（13 项）+ `scripts/` + 最小 CI；验收必须来自全新检出 |
| P1 第 3 条（签名假绿） | **采纳，限定表述**（见 §三）：本机基线非假绿，干净检出与 CI 是；缺钥必须失败 |
| P1 第 4 条（主会话协议） | **采纳**：票据之外必须先定义主会话生命周期与撤销；保留 5 秒 / 64 上限并扩展到新端点 |
| P1 第 5 条（DOM 与认证同单元验收） | **采纳**：`WebThemeCssTests.cs` 为现成回归网；浏览器测试需同时覆盖 HTTP 与 WebSocket 两条数据通道 |
| P1 第 6 条（验签→高权限更新器交接） | **采纳**：补充"已验证字节必须保持不变直至使用"；发布顺序须为"冻结→签名→独立验证→上传核验→公开→客户端可见" |
| P2 第 7–16 条 | 全部采纳；第 8、9、10、11 条已由我独立复核成立 |
| 第 15 条（状态枚举自相矛盾） | **采纳**：统一状态机，把"缺陷结论 / 验证状态 / 发布裁决"三者分开 |
| 第 16 条（NOTICE 依赖版本） | **采纳**：以实际解析依赖与最终包内容生成清单 |

### 仍需在 V2.0 中明确的三件事（本轮新增）

1. **§4.1 的结构性改法**——在途合并的租约模型，否则 LM-002 的测试无从写起；
2. **§4.2 的重置联动**——`ResetNetworkClients()` 与在途合并的交互边界；
3. **§4.3 的键组成修正**——实例/步骤身份已在键内，缺的是方法/Header/代理。

### 一处不应照搬的地方

评审第 14 条主张"主测试全绿不能等同于真机恢复通过"，并以 `GpuVramTests.cs:56` 为例——这一点正确。但需注意：该文件 `:51-52` 的期望值是**本机实测的 RTX 3080 / 3080 Ti 容量**，且 `:56` 的跳过分支让用例在其他机型上显示 Passed。这属于"测试设计把本机事实写死为断言"的问题，改法应是**改为本机自洽断言 + 机型差异标记为显式跳过**，而不是删除该用例。否则会丢掉一条真实有效的容量回归。

---

## 六、核对方法与未验证边界

| 检查 | 方式 | 结果 |
|---|---|---|
| 主测试基线 | `dotnet test tests/LiteMonitor.Tests -c Debug` | 983 通过 / 0 失败 / 7 跳过 / 990 总计（连续两次一致） |
| 更新器测试 | `dotnet test tests/LiteMonitor.Updater.Tests -c Debug` | 13 通过 / 0 失败 / 0 跳过 |
| 解决方案入口 | `dotnet test LiteMonitor.sln` | EXITCODE=0，零测试输出 |
| 分支差异 | `git diff --name-only master branch` / `master...branch` / `merge-base` | 484 / 522 / `169e90a2…` |
| worktree | `git worktree list` | 3 个，remediation 分支无 worktree |
| 远端状态 | `gh run list` / `gh release list` / `gh api repos/...` | 均空；仓库 public，master，`pushed_at=2026-10-22T10:11:24Z` |
| 私钥可达性 | `FindSigningKey()` 路径推演 + `secrets/` 实查 | 本机 5 级命中，断言真实执行 |

**未验证边界：**Release 构建在**本机原目录**仍被运行中的 `LiteMonitor++.exe`(PID 24936) 锁定（评审在隔离副本中构建成功，不能推广到原目录）；真实 GPU 故障恢复、正式私钥与最终产物的一致性、完整安装升级过程、WS 票据与 CSRF 行为，均未验证。

**产物：**`git status` 为空，本次未改动任何受版本控制的文件。
