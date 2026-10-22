# GPU 非性能 runner：独立只读侧审

结论：**NEEDS-REVISION**。存在以下 7 项阻断，不能据此 runner 声明完整非性能验收通过。

审阅时间：2026-10-22 04:08:55–04:12:46 +08:00，Asia/Shanghai。下列两个文件在开始与结束读取时哈希一致；行号对应此次版本，后续并发修改须另行对照。

| 对象 | SHA256 | 最后修改时间（+08:00） |
| --- | --- | --- |
| `scripts/run-gpu-nonperformance.ps1`（1290 行） | `7E2946F8A46A0C154480864ADB12CAB868FE7B636487735A4E7AEB3B4C6A60A0` | 2026-10-22 04:08:42 |
| `docs/reviews/evidence/gpu-themes/nonperformance-20261022/qa/nonperformance-manifest.json` | `AA2EAE27044637FBDBFF104210218041ACA7399610147ADBAADFD0627F1C28A7` | 2026-10-22 03:43:09 |

仅阅读脚本、JSON 及验证其调用合同所需的相关源码，未执行被审 runner（包括 PlanOnly）、测试、构建或 GUI；唯一写入为本报告。manifest 当前 `candidate.status=pending` 的拒绝执行是正确保护，以下描述冻结后仍会遇到的实际缺陷，不把当前 pending 当作缺陷。

## 阻断问题

### R1 · P1：QA manifest 完整文件哈希形成自引用，无法按正常流程冻结

位置：runner 第 229、895–905 行；manifest 第 13–22 行。

`manifestValidation.manifestSha256` 是包含 `candidate.qaManifestSha256` 本身的完整 JSON 文件 SHA256，运行又要求该字段等于这个文件哈希。先计算再填入字段会改变文件内容及哈希；反复回填不能完成正常冻结。当前没有排除字段、规范化计算或外置冻结收据的约定。

**必要修复：**选择可完成的身份算法，例如对移除/固定为空的该字段做规范化哈希，或由外部冻结收据保存完整 manifest 原始哈希；冻结端与校验端使用同一算法。保持实际候选、清单内容变更会失效的约束。

### R2 · P1：源码/构建输入指纹漏掉真实输入，且运行中不复核漂移

位置：runner 第 71–119、836、903–905、1233 行；manifest 第 68–92 行。

`buildInputHash` 只是与同一个 `sourceSnapshot.sha256` 比较，没有覆盖全部真实构建输入：

- 清单不含 `app.manifest`、`Properties/Resources.Designer.cs`、`Properties/Resources.resx`、图标等工程显式输入，以及实际生效的构建配置/导入文件。改变 UAC manifest 或资源仍可保持此输入指纹不变。
- 自制 glob 将 `**` 替为 `.*`，但保留其后的分隔符。因此 `LiteMonitor.Updater/**/*.cs` 不匹配该目录直接下的 `Program.cs`、`UpdateFileTransaction.cs`；`**/*.csproj` 同样漏掉 updater 根项目。这里不能按通常“零层或多层目录”的 glob 语义理解。
- 仅启动前扫描一次；构建、缓存复用、运行结束均不再验证源码。summary 的 `currentSourceManifestHash` 仍是启动前值。候选目录若被改动，会把变化后的构建/结果归到旧指纹。

**必要修复：**完整列出实际构建/运行输入并正确处理零层目录匹配；使用已经验证、不会被并发写入的候选副本，或在构建/运行边界重新核对输入，漂移立即阻断。不要将同一个不完整源码哈希改名为 build-input 校验后当成独立证明。

### R3 · P1：二进制及人工证据身份校验可为空，EXE 哈希也未覆盖实际业务 DLL

位置：runner 第 402–479、949–963、988–989、1067–1071、1171–1186 行；manifest `candidate.artifactSha256`。

候选允许 `artifactSha256={}`，缺失条目时 `$expectedHash=null`，两个执行分支均跳过预期哈希校验。对 dotnet-exe 即使填入映射，也只检查 apphost `.exe`；.NET 实际逻辑在相邻 probe DLL、`LiteMonitor++.dll` 及其资源中。修改这些文件不必改变 EXE 哈希。缓存记录也不在实际启动前重新验证。

人工分支只比较 `candidateId` 和源码哈希，随后接受 `status=PASS`。没有要求或校验所运行 EXE/DLL、最终 ZIP SHA256、人工截图/诊断附件清单及其哈希；结果中也不保存人工收据本身的哈希。即使收据对应同源码的另一份二进制或包，仍可能被接受为当前成品证据。

**必要修复：**冻结并强制校验实际加载的产物集合，至少覆盖 probe/main DLL、相关 shader/主题及所需运行文件，启动前验证其仍符合冻结记录；不能只校验 apphost。人工/成品收据明确绑定同一候选的二进制、ZIP 和实际证据文件哈希，缺项或不一致阻断并保留收据身份。源码测试证据与成品运行证据可分层，不要求每个单测都使用成品 EXE。

### R4 · P1：默认 static 入口仍会执行被暂停的性能配对与预算

位置：manifest 第 326–350 行；runner 第 201、1093–1100、1250 行。

`appearance-decor-renderer` 位于默认 static 组，只传 `--out`。当前 `tests/AppearanceDecorRendererProbe/Program.cs:48` 无条件 `RunAll()`，`DecorRendererProbeRunner.cs:48` 继续调用 `RunFrameCost()`；第 841 行起包含预热、多轮旧/新路径配对及每帧成本预算。它不是单纯状态/资源归零检查。

runner 只检查 JSON 声明的 `allowPerformance=false`，并在 summary 无条件写 `performanceExecuted=false`，不会排除这条实际调用。修复前面的执行阻断后，默认模式会违反暂停指令且生成错误声明。

**必要修复：**先提供并使用该 probe 的明确非性能组选择，保留其像素/裁剪/有界缓存等功能用例，逐用例列出延期项；对可运行入口实施实际模式校验，不以配置布尔值和硬编码摘要替代。无需恢复任何性能实验。

### R5 · P1：RequireAll 使用“任一映射被选中”，可漏掉必要实机入口；整组 N/A 也可判绿

位置：runner 第 1193–1208、1210–1223、1255–1278 行；manifest requirements 与人工状态列表。

完整验收检查只在一个 use case 的所有 entry 都未选中时阻断，没有要求该用例的必要 entry 全部取得有效结果。例如：

- `wda-and-editor-zero-capture` 映射 live glass、backdrop diagnostic、editor glass；仅选择 editor glass 也满足该用例“已选择”。
- `automatic-recovery-boundary` 映射状态测试、真实宿主和设备合同；遗漏整个 offscreen-gpu 组仍可能满足。
- 所选组中存在其他 entry 的结果不等于上述缺失 entry 有结果，`selectedUseCases` 也不检查断言覆盖。

此外，`manual-display-matrix`、`manual-peripheral` 等允许整份收据 `NOT-APPLICABLE`；overall 只统计其数量，没有按支持条件和必要子项阻断。这样可能把当前已有多屏或必要外围业务整体豁免并返回 PASS。可选 HDR 不适用与当前多屏未验不能共用整组 N/A。

**必要修复：**明确每个用例的 AND 必需入口与可替代入口，RequireAll 按每一必要断言/entry 的有效结果计算；必要的多屏、live、设备与业务项不能因另一入口被选择而关闭。N/A 仅作用于清楚限定的可选环境子项，记录原因，不能豁免整组必要功能。

### R6 · P2：清单与读取/探针输出合同不一致，多个必要入口必然失败

位置：runner 第 15、603、675、766、1074、1109 行；manifest 各静态与 production entry。

存在数个直接可从源码确认的合同错误：

1. `Set-StrictMode -Version Latest` 下直接访问不存在的属性会抛错。多数 dotnet-exe entry 没有 `copyArtifacts`，但启动前无条件执行 `@($entry.copyArtifacts)`；theme-check 没有 `evidence`，大多数其他 entry 没有 `stdoutRegex`，证据函数也直接读取。它们并非通过空数组安全跳过。
2. `Get-EntryCommands` 第 766 行先无条件访问 `Entry.project`，之后才判断 kind。manual entry 没有 project；选择 manual/release 时，在计划对象构造阶段就会异常，尚未进入人工证据分支。
3. decor、decor-renderer 清单断言 `total>=1`，当前两份 Program.cs 实际输出 `cases` 和 `allPassed`，没有 total。
4. 三个 production 固定入口断言根路径 `performanceMode`，实际在 `visibilityAudit.performanceMode`（`ProductionRenderProbe/Program.cs:698,771,842–845`），因此有效的正式输出也会被拒绝。

**必要修复：**对可选字段统一 schema 默认值/存在性读取，manual 分支不读构建字段；按当前探针真实 JSON 路径和 case 数/通过状态绑定断言。不是通过删除必要证据要求来让 runner 变绿。

### R7 · P1：live 捕获清单调用仍硬编码置顶并激活的旧试验台

位置：manifest `backdrop-capture`（第 829 行起）；`tests/AppearanceBackdropProbe/Scenarios/CaptureScenario.cs:79`、`Probe/Rig.cs:42–58`、`Probe/ProbeForm.cs:62`、`Probe/SceneForm.cs:154`。

该 entry 选择旧 `--scenario capture`，构造 Rig 后，Scene/Probe 均设置 `TopMost=true`，依次 `Show()` 并调用 `BringToTopMost`；没有与 manifest 对应的不激活选择。`-AllowGui -AllowLive` 授权的是短时 GUI/live 功能验收，不包含违背已冻结的非置顶、不抢焦点约束。

外部窗口审计是在子进程启动后运行，最多只能事后报告违规，不能阻止已经发生的置顶/激活；因此即使最后判 FAIL，也已经干扰用户桌面。

**必要修复：**此入口在符合窗口策略前不得自动运行；改用明确非置顶、不激活且可取消的功能模式，或保持对应必要项阻塞并提供人工步骤。不能仅取消 windowAuditRequired 或忽略审计失败。

## 已有保护及本次不重复的问题

- TRX 分支有零总数/零 executed、失败、SKIP/notExecuted 及无 passed 的检查；执行分支明确阻断超时和非零退出，当前没有把这些正常错误路径判为 PASS。上述判断未通过实际执行验证。
- 用户指出的 PID 基线误杀和 stdout 超时边界正在修，本报告不重复列项。本次读取已见 `OwnedProbeStarts` 的 PID+StartTime 匹配，以及异步双流/有界流等待；这里只记录观察，不据此宣称该并发修复最终验收通过。
- 当前 manifest 尚处 pending，未填最终新样式/集成结果本身不作为这次侧审的新缺陷；冻结和 RequireAll 之前，仍须由集成 owner 补齐该候选实际功能与最终 ZIP 的证据。
- 其他 worktree、实现文件、runner、manifest 均未由本审查者改动。报告只针对上述时间和哈希版本；修订后可逐项定向复审，不需要重跑整套测试或重开性能任务。
