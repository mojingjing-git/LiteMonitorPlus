# 更新认证、配置持久化与已复现缺陷修复 — Implementation Plan

> 状态：待实施（复审后修订版）。本次只编写计划，不代表补丁、签名体系、探针或实机验证已经完成。
>
> 日期：2026-10-21 初审；2026-10-21 修订（两名独立复审者意见 + 逐条回源核对）。
>
> 编写基线：`master`，HEAD `e918558`（`docs(appearance): A16 验收文档`）。工作区未提交改动为 `UIController.cs`、`LiveGlassController.cs`、`resources/themes/LiquidGlass.json`、`tests/AppearanceThemeCheck/ThemeSpec.cs` 与 `docs/superpowers/plans/2026-10-20-performance-sampling-verification.md`，另有若干未跟踪文件。执行前重新检查分支、HEAD 与工作区，保留用户及其他任务的改动。
>
> 证据依据：2026-10-21 独立核实——15 项隔离探针、583 项现有测试、编译通过。核实推翻了原审查的 3 条推论，收窄了若干项的严重级。本计划只接收核实后仍然成立的项；本版另外撤回了初审计划中的 2 处错误标注（W5「无交集」、`update_success` 写入位置）并修正 4 处不可执行的验收条件，详见 §9。

## 0. 执行前置条件

三件事未清前不能全量开工，但**不必等待它们**才开始：

| 编号 | 阻塞内容 | 阻塞范围 | 可先做 |
|---|---|---|---|
| X1 | 更新源仓库返回 404（见 P-3） | W1.2 的真实链路验收 | W1.1、W1.3；W1.2 的代码与本地三篡改验证 |
| X2 | P-1、P-2 产品决策未定 | W1.1 的失败提示文案、W5 的默认暴露策略 | 缺陷修复本身；文案留 TODO 并在提交说明标注 |
| X3 | W5 唯一写入者未定（见 §3.3） | W5 全部 | — |

W1.1、W1.3、W6 可做项、W7 可做项与三项前置**均无关**，可立即开工。

## 1. 目标与范围

修复三类问题，按可独立验收的四个里程碑交付：

| 里程碑 | 工作包 | 允许作出的结论 |
|---|---|---|
| M1 更新可信 | W1 | 更新包来源可认证；证书校验恢复后失败可见而非静默绕过 |
| M2 配置不丢 | W2、W3 | 读取故障不再导致用户配置与备份被覆盖；设置页不静默改写未编辑项 |
| M3 运行期正确 | W4、W5、W6 | 硬件重载/关闭有明确生命周期；Web 认证与读取有边界；已复现的显示与调度错误修正 |
| M4 工程卫生 | W7 | 资源所有权、语言包、单实例、日志与仓库规范收口 |

**明确排除**（外观改造独占写集，本计划不写）：

`UIRenderer.cs`、`HorizontalRenderer.cs`、`UIController.cs`、`MainForm_Transparent.cs`、`MainFormWinHelper.cs`、`UIUtils.cs`、`ThemeManager.cs`，以及 A16 实时玻璃、`src/System/Backdrop/`、`src/UI/Decor/`、`src/ThemeEditor/`。

涉及上述文件的修复项（W6 的布局零高度与主题数值校验）标注为「排队」，由外观改造主集成者在收口时统一修改，或在其冻结写集后单独派发。本计划不为这些文件另设第二个写入者。

## 2. 证据分级

贯穿本计划的口径。原审查把「可疑代码形态」与「已复现的功能错误」混在同一严重级，这是本轮要避免的重复错误。

| 级别 | 定义 | 是否进入交付 |
|---|---|---|
| A | 已用探针或隔离进程复现具体行为 | 必须修复，验收以复现脚本回归为准 |
| B | 静态阅读可确定代码路径，行为无需运行即可判断 | 修复，验收以单测或契约断言为准 |
| C | 只有代码形态可疑，实际后果依赖运行时条件且未验证 | **不进入本计划交付**，列入 §8 观察项 |

缺陷清单与级别：

| 编号 | 缺陷 | 级别 |
|---|---|---|
| D1 | 7 处 TLS 证书校验无条件关闭 | B |
| D2 | 应用更新包无哈希/签名核验即解出并执行 | B |
| D3 | 更新器覆盖失败后继续、无条件报成功并删除恢复材料 | B |
| D4 | 配置读取故障静默降级为默认值，后续保存覆盖主文件与备份 | A |
| D5 | 监控页未同步「跟随任务栏」复选框即回写 false | A |
| D6 | 设备下拉重填时把缺席设备偏好写成空串 | A |
| D7 | 硬件延迟重载绕过采集门限，且未跟踪生命周期 | B |
| D8 | Web 口令未做 URL 解码 | A |
| D9 | `ReadTimeout` 对 `ReadAsync` 无效，无连接上限 | A |
| D10 | 全部可见项关闭导致面板高度 0 | A |
| D11 | 内存容量未知时把百分比显示为 GB | A |
| D12 | 主题字体数值为负时抛异常到 UI 线程 | A |
| D13 | 任务栏刷新间隔不随配置更新 | B |
| D14 | 繁中缺 3 个短标签键 | B |
| D15 | 单实例在长路径分支使用随机化哈希 | A |
| D16 | 设置窗体未释放字典中的非挂载页面 | A |
| D17 | 市场 E2E 未启用时提前返回却报告为通过 | A |

D14 由初审的 A 降为 B：依据是 `resources/lang/zh-tw.json:47-48` 与 `zh.json:48-50` 的键集静态比对，属代码可判定，未经过语言包加载探针复现渲染缺失。

## 3. 与其他并行计划的关系

初审只比对了外观改造一份计划，把 W1–W7 全部标为「无交集」，这是本版撤回的主要错误。事实上同仓库有**四份**计划声明了重叠写集，其中两份处于活跃状态。

### 3.1 冲突分档

**活跃写集**（有未提交改动，或刚提交、明确在推进）

| 来源 | 声明写集（与本计划相关的部分） |
|---|---|
| `2026-10-20-appearance-agent-execution.md:145-153` | `UIRenderer.cs`、`HorizontalRenderer.cs`、`UIController.cs`、`MainForm_Transparent.cs`、`MainFormWinHelper.cs`、`UIUtils.cs`（主集成者）；`ThemeManager.cs`（A01/A03）；**`WebSessionManager.cs`、`WebPageContent.cs`（A11）**；全部 csproj/sln/依赖/发布接线 |
| `2026-10-20-appearance-agent-execution.md:159` | 硬件计划并行时，`UIController`、`MainForm`、`WebSessionManager` 归**同一个总集成负责人** |
| `2026-10-20-performance-sampling-merge.md:171` | 单一 owner 接线：`HardwareMonitor.cs`、采集协调器、缓存与生命周期；`Settings.cs`；`SystemHardwarPage.cs` 与 9 个语言包；`src/System/WebServer/` |
| `2026-10-20-performance-sampling-verification.md`（工作区已有改动） | 性能采样的验收与接线 |

**草稿声明**（文档本身 `??` 未提交，计划状态为「待实施」，暂无实际写入者）

| 来源 | 声明写集（与本计划相关的部分） |
|---|---|
| `2026-10-20-hardware-stability-hardening.md:64-74` | `Settings.cs`、`SettingsHelper.cs`、`HardwareMonitor.cs`、`SystemHardwarPage.cs`、`SettingsForm.cs`、`TaskbarForm.cs`、`src/System/WebServer/`、`pack-release.ps1` |
| `2026-10-20-hardware-stability-parallel-dispatch.md:109-120` | 主 agent：`HardwareMonitor`、`HardwareValueProvider`、`DiskManager`、`HardwareScanner`、`SensorMap`、`GpuRegistry`、`HardwareErrorLog`、`Settings`、`SettingsHelper`、`SettingsForm`、`AppActions`、`UIController`、`TaskbarForm`、全部 csproj/sln；P13：`src/System/WebServer/` |

草稿声明不构成「已有人在写」，但只要该计划可能被启动，就不能按「无主文件」处理。判定规则：**活跃写集 = 禁止并行；草稿声明 = 需报备，且若其启动则让位。**

### 3.2 逐包判定

| 工作包 | 目标文件 | 活跃冲突 | 草稿冲突 | 判定 |
|---|---|---|---|---|
| W1.1 | 7 处 TLS 文件 | 无 | 无 | **可立即开工** |
| W1.2 | `UpdateChecker.cs`、`version.json` | 无 | 无 | **可立即开工**（真实链路验收待 X1） |
| W1.3 | `LiteMonitor.Updater/Program.cs`、`UpdateChecker.cs`、`src/UI/Helpers/MainFormBizHelper.cs` | 无 | 无 | **可立即开工** |
| W1 发布签名步骤 | `scripts/pack-release.ps1` | 外观主集成者声明「发布接线」 | hardening:74 | 交付为独立签名脚本 + 接入说明，由该 owner 接入，本计划不改此文件 |
| W2 | `SettingsHelper.cs`、`Settings.cs` | `Settings.cs`（性能线） | 两份 | **拆分**：`SettingsHelper.cs` 若性能线未动可先做；`Settings.cs` 排队 |
| W3 | `MonitorPage.cs`、`SettingsUIBuilder.cs`、`SettingsChanger.cs` | 无 | 无 | **可立即开工** |
| W3 | `SystemHardwarPage.cs` | 性能线 | 两份 | **排队** |
| W4 | `HardwareMonitor.cs`、`HardwareSamplingCoordinator.cs`、`SensorMap.cs`、`FanMapper.cs` | `HardwareMonitor.cs` + 采集协调器（性能线） | 两份 | **排队** |
| W5 | `LiteWebServer.cs`、`WebSessionManager.cs`、`WebPageContent.cs` | A11 两个 + 性能线 `src/System/WebServer/` | P13 + hardening | **整体排队**，见 §3.3 |
| W6 可做项 | `MetricUtils.cs` | 无 | 无 | **可立即开工** |
| W6 | `TaskbarForm.cs` | 无 | 两份 | 排队 |
| W6 排队项 | `UIController.cs`、`ThemeManager.cs` | 外观独占 | — | 交外观主集成者 |
| W6 待裁决 | `HorizontalLayout.cs` | 不在外观声明写集内 | 无 | **当前无 owner**，见 P-4 |
| W7 可做项 | `TrafficHistoryForm.cs`、`MenuManager.cs`、`src/System/Program.cs`、`.gitignore`、`MarketE2ETests.cs` | 无 | 无 | **可立即开工** |
| W7 | `SettingsForm.cs` | 无 | 两份 | 排队 |
| W7 | 语言包 `resources/lang/*.json` | 性能线（9 个语言包） | 两份 | 排队；若只追加 3 个缺失键，可向 owner 报备后单独提交 |

注意 `src/System/Program.cs`（主程序入口，D15）与 `LiteMonitor.Updater/Program.cs`（更新器，D3）是两个不同文件，前者无冲突，后者的引用必须写全路径。

### 3.3 W5 的处置

D8（口令未 URL 解码，`WebSessionManager.cs:722-734`）与 D9 的读取期限（同文件 `:512`/`:631`）落在 `WebSessionManager.cs`，被 A11 与性能线双重声明；D9 的连接上限落在 `LiteWebServer.cs:262-285`，属 `src/System/WebServer/`，被 P13 与性能线声明。

初审把 W5 标为「无交集，可立即开始」，与上述三处原文直接矛盾，本版撤回。

处置：W5 整体交给 `src/System/WebServer/` 的既有 owner 执行，本计划只提供缺陷描述与验收用例。若该 owner 近期不收口，则由本计划申请独占 `WebSessionManager.cs` + `LiteWebServer.cs` 的写权，并要求 A11 与 P13 在获得确认前不写这两个文件。**不得**由两个写入者各改一份再依赖自动合并。

D8/D9 为 A 级已复现缺陷，排队不得变成无限期搁置：若 X3 在 M1 完成时仍未解决，D8 升级为跨计划升级事项，由用户指定写入者。

## 4. 实施前定位

| 文件 | 类型/成员 | 关注点 |
|---|---|---|
| `src/System/UpdateChecker.cs` | `UpdateChecker.http`、`PreUpdateUpdater` | 静态 HttpClient 的 `SslOptions`；启动更新器前的覆盖与自检 |
| `src/UI/UpdateDialog.cs` | 下载方法内的局部 handler | 下载 `update.zip` 的客户端；启动更新器的提权分支 |
| `LiteMonitor.Updater/Program.cs` | `Program.Main`、`TryCopyFile`、`RestartMain` | 备份、替换、失败回滚、清理时机、成功标志 |
| `src/UI/Helpers/MainFormBizHelper.cs` | `CheckUpdateSuccess` | 更新结论的用户可见提示 |
| `src/Core/SettingsHelper.cs` | `TryLoadFile`、`Load`、`AtomicWrite` | 三种失败语义区分；`GlobalBlockSave` 已存在可复用 |
| `src/UI/Settings/MonitorPage.cs` | `Save`、`OnShow`/`SetContext` | 复选框同步时机 |
| `src/UI/Settings/SystemHardwarPage.cs` | `FillSync`/`FillGpuSync` | 重填期间的选中事件 |
| `src/UI/Settings/SettingsUIBuilder.cs` | `AddCombo`/`set` 回调 | 抑制回写的接缝 |
| `src/System/HardwareMonitor.cs` | 延迟重载入口、`Dispose`、`ReloadComputerSafe` | 任务跟踪、`_disposed` 早退、映射重建的同步范围 |
| `src/System/WebServer/WebSessionManager.cs` | 请求行解析、口令比较、响应头 | 解码、恒时比较、读取期限 |
| `src/System/WebServer/LiteWebServer.cs` | 监听端点、连接接受循环 | 绑定策略、并发上限 |

## 5. 工作包

### W1 — 更新可信（M1，最高优先）

#### W1.1 恢复全部证书校验

7 处无条件放行，已逐处回源确认，行号如下：

```
src/System/UpdateChecker.cs:33
src/UI/UpdateDialog.cs:244
src/Core/NetworkSpeedTester.cs:24
src/Plugins/PluginExecutor.cs:58
src/Plugins/PluginExecutor.cs:443
src/Plugins/Native/CryptoNative.cs:50
src/Plugins/Native/CityCodeResolver.cs:172
```

删除承载回调的 `SslOptions` 赋值即可——已确认这些 `SslOptions` 实例除挂回调外无其它设置。全仓检索确认**不存在** `ServicePointManager.ServerCertificateValidationCallback` 一类的全局旁路，因此删除这 7 处即为完整修复，无需再找第 8 处。

需要显式处理的取舍：原注释称关闭校验是为了「解决用户证书报错问题」。恢复后，处于企业 TLS 拦截、自签代理或证书过期环境下的用户会看到失败。要求是**失败必须可见**——返回明确的「无法验证更新源」提示并停止，不得静默降级、不得提供跳过选项。该判断写入代码注释，避免后续被当作「又出证书问题了」而改回。具体文案待 P-1 定。

验收（初审此处不可执行，已替换）：

初审要求「单测断言 7 个 handler 的 `RemoteCertificateValidationCallback` 为 null」。不可执行：`UpdateDialog.cs:240-246` 与 `CityCodeResolver.cs:162-174` 的 handler 是方法内的局部委托，不挂载在任何可达字段或属性上，反射取不到。改为两条：

1. **静态断言**（可执行，覆盖全部 7 处，含递归子目录）：

   ```powershell
   Get-ChildItem src -Recurse -Filter *.cs |
     Select-String -Pattern 'RemoteCertificateValidationCallback|ServerCertificateCustomValidationCallback'
   ```

   期望无输出。若未来确有合法的自定义校验需求，必须在该处加 `// TLS-POLICY-EXCEPTION: <理由>` 并登记入本计划，否则视为回归。

2. **行为验证**：本地起一个自签证书 HTTPS 端点，用 `UpdateChecker` 的取版本路径请求，断言抛出 `AuthenticationException` 且提示文案可读。

#### W1.2 更新包来源认证

采用签名 `version.json` 方案，而非「把摘要和 ZIP 放在同一个响应里」——后者在同一可替换通道内不构成认证。

- `version.json` 增加 `sha256`（针对 release ZIP）与 `signature`（对规范化后的 payload 签名）。
- 公钥编译进主程序；私钥只存在于发布流程，不进仓库。
- 校验顺序：验签 → 取 `sha256` → 下载 → 比对摘要 → 才允许解压或执行任何内容。
- 验签或摘要失败：中止更新，保留现有版本，提示来源不可信。

算法用 BCL 内的 ECDSA P-256 或 RSA-2048+SHA256，不引入新依赖。签名载荷建议为 `version\nsha256\nreleaseDate`（UTF-8、`\n` 分隔），**不签** `changelog`——它含中文与换行，规范化规则容易在发布脚本与客户端之间漂移。公钥以 SPKI DER 单行 base64 常量存放，配 `ECDsa.Create()` + `ImportSubjectPublicKeyInfo`。私钥走 `-KeyPath` + DPAPI 保护的 PFX，`.gitignore` 增加 `*.pfx`。`UpdateChecker.cs:216-220` 用 `JsonDocument` 按名取键，因此新增字段不会破坏旧客户端反序列化。

`scripts/pack-release.ps1:191` 已计算 ZIP SHA256 但不回写；签名步骤接在其后，签名失败即终止发布。按 §3.2，该脚本由既有 owner 接入，本计划交付独立签名脚本与接入说明。

验收：篡改 ZIP 一个字节、篡改 `version.json` 的 `sha256`、用错误私钥重签，三种情形均被拒绝，并有独立探针记录证据。**真实链路（从线上源成功更新一次）需先解决 X1。**

#### W1.3 更新器失败回滚（含自举缺陷）

现状（逐行确认）：

- `LiteMonitor.Updater/Program.cs:75-98` 遍历 ZIP 内容逐个覆盖，`TryCopyFile` 重试 10 次后返回 false，失败仅 `LogError`（`:94-97`）**继续下一个文件**。
- `:108-109` 无论成败都删除 `_update_tmp` 与 `update.zip`，失败现场不保留。
- `:114` → `RestartMain`（`:231-253`）：`:236` **无条件**创建 `update_success`，即使所有文件都覆盖失败。
- `:244-252` `Process.Start` 失败被 `catch { }` 吞掉，不留证据。
- 主程序侧 `src/UI/Helpers/MainFormBizHelper.cs:365-375`：只要见到 `update_success` 就提示「软件已成功更新到最新版本！」，`:370` 随即删除标志。

所以现在的「成功」等于「更新器跑到了 `RestartMain` 这一步」，而不是「文件被正确替换」。（初审把 `update_success` 的写入位置记为 `UpdateChecker.cs:233-239`，实际在 `LiteMonitor.Updater/Program.cs:236`；`UpdateChecker.cs:233` 是取版本失败返回 null。）

**自举缺陷**（初审未处理）：`UpdateChecker.PreUpdateUpdater`（`:339-400`）在启动更新器之前，从 ZIP 中直接 `ExtractToFile(targetPath, true)`（`:393`）覆盖 `resources/Updater.exe`，**无备份、无自检**。若这份新更新器本身损坏（截断、缺依赖、构建错误），主程序已把它写到磁盘并随即启动，损坏的更新器不会执行任何回滚代码。因此「把回滚写在更新器里」覆盖不到「更新器自身损坏」——而初审的验收条件（只读文件、磁盘满）恰好只覆盖更新器能正常运行的情形。

修订后的流程：

**a) 更新器主流程**

1. 解压到 `_update_tmp_<时间戳>`，不再复用固定 `_update_tmp`，避免与残留目录混淆。
2. 生成替换清单：逐文件记录 `相对路径 → 备份路径 → 原文件是否存在 → 原文件长度`，备份写入 `_update_backup_<时间戳>/`。
3. 先写 `update_in_progress` 标记（含备份目录路径、目标版本、时间戳），再开始替换。
4. 逐文件：备份 → 替换 → 校验（至少长度一致；`.exe`/`.dll` 追加 SHA-256 与 ZIP 内条目比对）。任一失败进入回滚。
5. 回滚：按清单**逆序**从备份恢复，恢复数量与失败原因写入 `update_error.log`。
6. 只有全部文件成功且自检通过，才创建 `update_success`；回滚后**不创建**。
7. 成功时清理备份、tmp、zip 与 `update_in_progress`；失败时保留备份与 zip，只清 tmp。
8. **无论成败都调用 `RestartMain`**。这是对「`Program.cs:114` 是唯一重启出口」的处理：失败时不拉起会留下「程序消失、无人负责」的状态，拉起已回滚的旧版本是更小的伤害。失败由「不写 `update_success` + 写 `update_error.log`」表达，而不是由「不重启」表达。
9. `RestartMain` 的 `Process.Start` 失败必须写 `update_error.log`，不再静默（当前 `:252` 为 `catch { }`）。

**b) 主程序侧**

1. `PreUpdateUpdater` 覆盖 `Updater.exe` 前先备份为 `Updater.exe.<时间戳>.bak`。
2. 覆盖后以 `--selfcheck` 启动它，要求 3 秒内以退出码 0 结束并打印版本号；否则恢复 `.bak`、删除新文件、返回 null，`UpdateDialog` 不退出主程序，提示「更新器自检失败，已取消更新」。**这条是消除自举缺陷的关键**：把「更新器可用」变成更新前的**前置断言**，而不是更新后的期望。
3. `CheckUpdateSuccess` 改为三态：见 `update_success` → 提示成功并删除；见 `update_error.log` → 提示「上次更新失败，已回滚到 vX」并附原因，然后删除该日志（避免每次启动重复提示）；两者皆无 → 不提示。
4. 启动时若存在 `update_in_progress` 但既无 `update_success` 也无 `update_error.log`（更新器被强制结束），提示「上次更新未完成」，保留备份目录供人工恢复。

**c) 仍未消除的残留风险（如实记录）**

若更新器与主程序 exe 同时损坏且都无法启动，则没有自动恢复路径——自动恢复需要一个永不参与更新的独立启动器，超出本次范围。b.2 的自检把风险窗口从「更新器运行前不可知」缩小到「自检通过但随后损坏」，但不能归零。**本计划不得声称已完全解决自举问题。**

验收（四条均须实际运行，不接受代码阅读代替）：

- 注入只读目标文件：更新器回滚，旧版本完整，`update_success` 不存在，`update_error.log` 有原因，程序仍被拉起。
- 注入磁盘满（小容量 VHD 或配额）：同上。
- 把 ZIP 中的 `Updater.exe` 换成截断文件：主程序自检失败，不覆盖、不退出。
- 手工制造「`update_in_progress` 存在但无结论」：下次启动提示未完成。

### W2 — 配置故障降级（M2）

`TryLoadFile` 区分三种结果：文件不存在、内容无法解析、临时 I/O 故障（被独占锁、权限、磁盘错误）。

- 前两种：不存在按新装处理；无法解析则把坏文件改名为 `.corrupt-<时间戳>` 保留取证，再用备份。
- 第三种：**不得**当作新装。加载返回默认实例的同时置 `GlobalBlockSave`，阻止本次会话任何自动落盘；界面提示「配置暂时无法读取，本次修改不会保存」。
- `AtomicWrite` 调整顺序：新内容写入临时文件并回读校验通过后，才更新备份；不再先用当前主文件覆盖备份。

验收（沿用已复现的两个场景，写成回归用例）：

1. 主文件坏、备份有效 → 从备份恢复；保存后备份不得变成坏文件。
2. 主备同时被独占锁 → 加载为默认；解锁后保存**不得**覆盖原主文件；第二次保存也不得把备份变成默认值。

按 §3.2，`Settings.cs` 部分排队；`SettingsHelper.cs` 若性能线未动可先做，开工前确认。

### W3 — 设置页意外回写（M2）

- 「跟随任务栏」：`MonitorPage.cs:530-535` 在页面加载时无条件从 `Config` 同步复选框，`Save` 只提交用户实际改动过的值。`MonitorPage.cs:86-88` 的 `CheckedChanged` 即时写 `Config` 需一并改为在抑制作用域内生效。保留现有「监控页未加载时 `Save` 早退」的行为。
- 设备偏好：`SystemHardwarPage.cs:91-108` 重填下拉期间抑制选中事件；当前偏好不在候选列表时保留原值并显示为「自动（原值不可用）」的只读提示，不写空串。
- `SettingsUIBuilder.cs:114`/`:125` 提供抑制回写的显式作用域，替代各页各自加标志位。

验收：复现「原值 true → 进监控页 → 保存 → 变 false」与「U 盘未插 → 打开设置页 → 保存 → PreferredDisk 变空」两条路径，改为修复后不成立；并验证点「取消」在任何情况下都不提交修改。`SystemHardwarPage.cs` 部分按 §3.2 排队。

### W4 — 硬件重载与关闭生命周期（M3）

- 延迟重载改为被跟踪的 `Task`：`Dispose` 时取消并等待，或在超时后放弃并不再触碰硬件。
- 重载入口与取得硬件锁后各加一次 `_disposed` 早退。
- 映射重建与硬件访问统一同步范围：把 `SensorMap.Rebuild` 移入与 `hw.Update()` 相同的锁域，或明确其只在采集线程执行。
- 修正与实现相反的注释（`HardwareMonitor.cs` 中关于「全程持有 `_lock`」的声明）。

验收：B 级项，不要求硬件掉卡复现。以「生命周期状态机单测 + 并发路径的锁序断言」为证据，并在文档中明确「窄窗口竞态未在真实掉卡下复现」这一限制。

**本包整体排队**：`HardwareMonitor.cs` 与采集协调器已被性能线声明为单一 owner 写集。

### W5 — Web 认证与资源边界（M3）

- 口令比较前先解析查询参数并 URL 解码；比较改用 `CryptographicOperations.FixedTimeEquals`。
- `ReadAsync` 改为带 `CancellationTokenSource` 的总读取期限，不再依赖 `ReadTimeout`（对异步读无效）。
- 增加并发连接上限（`SemaphoreSlim`，超限直接关闭），并统计当前连接数供状态展示。
- 明确默认暴露策略（见 P-2）。

验收：含空格、中文、`&`、`%` 的口令可正常登录；把 `sleep` 客户端接入后，连接在总期限到达时被关闭；并发超限时新连接被拒绝且已有连接不受影响。

**本包整体排队，写入者待定**，见 §3.3 与 P-4。

### W6 — 已复现的功能错误（M3）

可直接做：

- `MetricUtils.cs:396-397`：容量未知时的单位回退统一为百分比，不再出现「45.0 GB」。
- 电池负值显示口径（`MetricUtils.cs` 内相关分支）：先确定产品期望（绝对值还是满条），再同步注释与实现；当前注释与代码相反，以产品结论为准。

排队：

- `TaskbarForm.cs:98`/`:108`：任务栏 `Interval` 在 `ReloadLayout` 内随配置同步——草稿写集有两份，需报备。
- `HorizontalLayout.cs:55` + `UIController.cs:270`：全部可见项关闭时的最小高度与空态提示。初审归入外观独占，但 `HorizontalLayout.cs` **不在** `appearance-agent-execution.md:145-153` 的声明写集内，属于「当前无 owner」，需裁决（P-4）。
- `ThemeManager.cs:373`/`:390`：主题字体数值加载时夹紧到合法区间，失败回退完整主题而非保留坏值。属外观独占。

验收：可做项用单测；排队项在外观收口后并入其验收。

### W7 — 工程卫生（M4）

可直接做：

- `src/System/Program.cs:46-50`：单实例名改用稳定摘要（如 SHA-256 前 16 位十六进制）。`string.GetHashCode()` 在 .NET Core 按进程随机化，长路径下两个进程会算出不同的 Mutex 名，双实例可共存。
- `TrafficHistoryForm.cs` 的两处画刷改用 `using` 或静态复用——属资源所有权收口，不按「必达 GDI 句柄上限」立论（该推论已被探针否定）。
- `MenuManager.cs`：菜单项字体复用，注意该项成立范围是「构造菜单时」而非每帧。
- `.gitignore` 增加 `LiteMonitor-old-config-*.zip`、`update.zip`、`*.pfx`。
- `MarketE2ETests.cs:23-30`：未启用时不再以 Passed 呈现。

`MarketE2ETests` 的具体做法（初审此处不可执行，已替换）：

初审要求「改为显式 Skip」。但 `tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj` 用 xunit **2.9.2**，而 `Assert.Skip` 是 xunit v3 的 API——已用反射确认 `Xunit.Assert` 中不存在任何 `Skip*` 方法。

可行做法是自定义特性，在构造时按环境变量设置 `Skip`：

```csharp
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LITEMONITOR_E2E") != "1")
            Skip = "需要 LITEMONITOR_E2E=1 才运行（联网 E2E）";
    }
}
```

依据：xunit 2.9.2 的 `Xunit.FactAttribute.Skip` 为 `public virtual string { get; set; }`（反射确认 `canWrite=True`、`virtual=True`），在构造函数内赋值即可实现动态跳过，`dotnet test` 会报 Skipped。

兜底：若实测该做法在当前 runner 下未生效，则退化为让用例在未启用时 **Fail 并给出明确原因**。两条路径都不允许退回 `return`——「未运行却报 Passed」是本项要消除的行为，换一种谎报方式不算修复。

排队：

- `SettingsForm.cs:23`/`:189`：释放字典中的**全部**页面，而不只是当前挂载页。草稿写集有两份。

验收：`Get-ChildItem src -Recurse -Filter *.cs | Select-String 'RemoteCertificateValidationCallback'` 之外，另需——语言包键集与 `zh.json` 完全一致（D14，`zh-tw.json:47-48` 补 `Short.DASH.HOST`/`Time`/`IP`）；三进程并发启动同一长路径实例只允许一个存活；E2E 在未启用时报 Skipped 或 Fail，不得报 Passed。

## 6. 需要决策的四项

实施前必须有明确结论，否则实现者会自行选择。

**P-1 证书校验恢复后的兼容策略。** 建议：失败即提示且不提供跳过。替代方案是提供「更新源不可信」的手动下载入口（跳转到 GitHub Release 页面，由浏览器完成证书处理），但这会让不验签的手动包绕过认证，需同时说明风险。

**P-2 Web 服务默认暴露范围。** 建议：默认绑定回环，开启局域网访问时强制生成随机口令并在界面显示一次。替代方案是保持现绑定但把「留空即无密码」改为不可保存。

**P-3 更新源仓库名。** `src/System/UpdateChecker.cs` 的取版本与下载地址指向 `mojingjing-git/LiteMonitorPlus`。2026-10-21 实测：

- `https://api.github.com/repos/mojingjing-git/LiteMonitorPlus` → `404 Not Found`
- `https://api.github.com/users/mojingjing-git` → `200`（`public_repos: 9`，用户存在）
- `git remote -v` → `origin = https://github.com/mojingjing-git/LiteMonitor.git`，`upstream = Diorser/LiteMonitor.git`
- `README.md` 引用的却是 `mojingjing-git/LiteMonitorPlus`

需要确认：该仓库是**改名**（应为 `LiteMonitor`）、**尚未公开**，还是**计划中的新仓库名**。这直接决定 W1.2 能否做端到端验收——待签名的 `version.json` 目前取不到。确认前，W1 只能交付「代码与签名工具就绪 + 本地三篡改拒绝」，**不得**声明更新链路已被验证。若结论是「应统一到 `LiteMonitor`」，则 `UpdateChecker.cs` 的地址常量与 README 需在同一次提交中一起改。

**P-4 两个无主文件。** `HorizontalLayout.cs`（D10 的尺寸写入点）与 W5 的三个 Web 文件目前没有可执行的唯一写入者。请指定：由既有 owner 收口，还是允许本计划独占。未指定前，D10 与 D8/D9 只能保持排队。

## 7. 验收与证据要求

- 每个工作包提交时附：改动文件清单、对应缺陷编号、验收命令与输出。A 级项必须附复现→修复的对照证据；B 级项附单测或断言。
- 静态断言（如 §5 W1.1 的 grep）必须附实际输出，**结果为空也要附**，不能只写「已检查」。
- 不得用「已阅读代码」代替运行证据；不得把 C 级观察项写成已修复；不得把「未运行」表述为「通过」。
- 涉及驱动的验证不安装/卸载驱动，不刺激外置设备。
- 全部工作完成后跑一次完整测试，并在结论中区分「实际执行」与「提前返回仍记为通过」的用例。

## 8. 本轮不做（C 级观察项）

以下项在核实后仅确认代码形态可疑、后果依赖运行时条件，本轮不修：

- 口令比较的远程计时侧信道（改为恒时比较属 W5 内顺手加固，不作为独立验收项）。
- CORS `*` 与缺失 CSP——加固建议，单独不构成可利用漏洞。
- 插件正则无超时、插件 SSRF——需先定义插件信任模型与网络权限约束。
- 市场索引与内容摘要同源、缺摘要放行——需先决定兼容策略与是否引入发布者签名。
- 混合 DPI 下用桌面 DC 测量——需实机多屏验证视觉后果。
- 菜单/页面字体的长期增长曲线——需压力测试给出数值。
- 驱动包 ZIP 路径穿越——上游 `:279` 已有固定摘要校验，属防御纵深缺口。
- 全接口监听在 IPv6 下的远程可达性——需网络环境实测。

## 9. 修订记录（对初审版本）

| 项 | 初审写法 | 修订后 | 依据 |
|---|---|---|---|
| W5 写集 | 「无交集，可立即开始」 | 整体排队，写入者待定（§3.3） | `appearance-agent-execution.md:151`、`:159`；`hardware-stability-parallel-dispatch.md:120`；`performance-sampling-merge.md:171` |
| 并行计划比对 | 只比对外观改造一份 | 四份，分「活跃」与「草稿」两档（§3.1） | 两份 hardware 计划、`performance-sampling-merge.md` 均在仓库内 |
| `update_success` 位置 | 记为 `UpdateChecker.cs:233-239` | `LiteMonitor.Updater/Program.cs:236` | 实际读取；`UpdateChecker.cs:233` 是失败返回 null |
| W1.3 回滚 | 未处理更新器自身损坏 | 新增 `PreUpdateUpdater` 备份 + `--selfcheck` 前置断言，并说明残留风险 | `UpdateChecker.cs:339-400`、`:393` |
| W1.3 重启 | 未处理「失败即停」与唯一重启出口冲突 | 明确失败也拉起旧版本，用成功标志与错误日志表达失败 | `LiteMonitor.Updater/Program.cs:114` |
| W1.1 验收 | 反射断言 7 个 handler 为 null | grep 静态断言 + 自签端点行为验证 | handler 为方法内局部委托，反射不可达 |
| W7 跳过 | 「改为显式 Skip」 | 自定义 `E2EFactAttribute` 动态设 `Skip`，兜底为 Fail | xunit 2.9.2 无 `Assert.Skip`；`FactAttribute.Skip` 为 virtual |
| D14 级别 | A | B | 键集静态比对，未经加载探针复现 |
| 基线 | 含 `Settings.cs` 等未提交改动 | HEAD `e918558`，工作区实况 | `git status --porcelain` |
