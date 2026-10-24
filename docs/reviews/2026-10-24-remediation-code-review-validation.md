# T09 与验证门禁独立代码复审

日期：2026-10-24（Asia/Shanghai）。

**结论：PASS。指定六个文件中未发现需要阻断实施的 P1／P2 问题，未发现本次改动引入的新回归。**

工作树：`C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork`。基线：`9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f`。独立复审只覆盖主控负责的 T09 和持续门禁；其他实施组仍在编辑，本报告不评价其修复结果。

已读取计划 v3 的 [§12](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/docs/superpowers/plans/2026-10-23-independent-review-remediation-plan.md:161)、[§13](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/docs/superpowers/plans/2026-10-23-independent-review-remediation-plan.md:168)，以及 R2 报告中替代历史 REQUEST_CHANGES 的[最新 PASS](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/docs/reviews/2026-10-23-remediation-plan-review-r2.md:125)。主控通知文件冻结后，再次核对六文件 SHA-256，与本轮审阅及探针使用的版本完全一致。

**阻塞发现：无。**

**代码核对**

- **Dispose 接收者关联成立。** [HttpLifecycleTests.cs:464](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/HttpLifecycleTests.cs:464) 限定实际 call／callvirt、非静态 MethodInfo、void Dispose()，仅跳过 nop／constrained 前缀后检查紧邻的接收者加载。字段从对应 FieldInfo 取类型，局部变量从该条加载指令绑定的 LocalVariableInfo 取类型，不再回溯附近无关字段。静态同名 Dispose 不会被承认为实例释放。
- **槽号和地址加载实现正确。** [IlInspector.cs:215](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/IlInspector.cs:215) 分别解码短槽号和无符号 16 位长槽号；[233 行](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/IlInspector.cs:233) 关联 ldloc.0～.3、ldloc.s／ldloc、ldloca.s／ldloca。参数加载和存储指令不会误绑定局部变量。原操作数宽度、方法／字段解析及 Scan 接口保持兼容。
- **正反例覆盖对应合同。** [HttpLifecycleTests.cs:483](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/HttpLifecycleTests.cs:483) 包括字段释放、局部释放、目标未释放、仅释放其他对象、field／local 混合和静态同名反例；[500 行](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/HttpLifecycleTests.cs:500) 覆盖值类型局部地址；[506 行](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/HttpLifecycleTests.cs:506) 显式发射 0／1／2／3／4／256 槽位的 IL。夹具只反汇编，不执行故意遗漏释放的代码。原请求、响应、JsonDocument 三项生产 IL 断言及请求／响应运行时释放测试均保留。UpdateChecker 相对基线无修改。
- **两流水线确实执行两个配置。** CI 的 [Debug](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/ci.yml:196)／[Release](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/ci.yml:230)，以及发布的 [Debug](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/release.yml:172)／[Release](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/release.yml:205)，均逐一运行三个必需工程，使用相应 -c 参数，并在每个调用后保存 LASTEXITCODE、非零即 throw。三个工程与解决方案中的三个测试工程一致。
- **TRX 隔离和门禁接线成立。** 结果路径为 ci-artifacts/test-results/{Debug|Release}/{程序集}/results.trx。两工作流先清理该结果树，再分别传入配置根目录：CI [Debug](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/ci.yml:211)／[Release](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/ci.yml:245)，发布 [Debug](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/release.yml:187)／[Release](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/release.yml:220)。两配置均传入完整计数、必需程序集、跳过上限和市场契约参数，均处理门禁非零退出码。
- **仅指定 E2E 可跳过。** [assert-test-counts.ps1:400](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/.github/workflows/assert-test-counts.ps1:400) 同时要求跳过数量与 NotExecuted 明细一致；程序集名忽略大小写，测试名按完整字符串精确匹配现有 MarketE2ETests 方法。原跳过上限、失败数、市场契约至少六项且不跳过的检查未放宽。
- **新增门禁测试使用真实脚本和真实步骤文本。** [ReviewReleaseValidationGateTests.cs:110](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/ReviewReleaseValidationGateTests.cs:110) 构造的八类输入具备相应计数与身份差异；[31 行](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/ReviewReleaseValidationGateTests.cs:31) 独立验证 Release 失败；[50 行](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/tests/LiteMonitor.Tests/ReviewReleaseValidationGateTests.cs:50) 检查两工作流的配置、结果目录、参数和退出码处理，并以函数替身执行实际 Release 步骤，不递归启动构建。

**独立、不构建的输入探针**

以下是直接调用当前 PowerShell 门禁的实测结果，使用与新增测试等价的最小合成 TRX、三个必需程序集、MinTotal=3、MaxSkipped=1、市场契约最少六项。它们不是产品测试运行结果。

| 输入 | 退出码 | 实际判据 |
| --- | ---: | --- |
| 零跳过 | 0 | 六条市场契约均通过 |
| 仅指定 E2E 跳过 | 0 | 六条市场契约均通过 |
| 仅一个非 E2E 跳过 | 1 | 未获允许的跳过身份 |
| E2E 加一个非 E2E 跳过 | 1 | 超过上限，并拒绝非白名单身份 |
| 市场契约一项跳过 | 1 | 白名单与市场必执行检查均拒绝 |
| 市场契约全部缺失 | 1 | 市场类没有结果 |
| 计数显示跳过，但无明细 | 1 | 跳过计数与明细不符 |
| E2E 同名结果出现在 Updater 程序集 | 1 | 程序集不符合白名单 |
| 一个失败用例 | 1 | 失败用例数非零 |

另外单独构造 Debug 通过、Release 失败，并把三份 Debug TRX 的修改时间设到未来一分钟：分别调用两配置根目录，退出码为 **Debug=0、Release=1**，Release 失败未被更新的 Debug 报告覆盖。

为确认反例确实命中新约束，将基线 9238bb9 的门禁只写入隔离临时目录，使用完全相同的非 E2E／无跳过明细／错误程序集输入重放：基线退出码均为 **0**，当前脚本均为 **1**。因此这些反例不会仅因夹具缺少程序集、市场类或数量不足而偶然判红。

还提取两工作流的真实 Debug／Release 测试步骤，以 dotnet 函数替身执行，共 **16 组**：两工作流 × 两配置 × 全成功或第 1／2／3 个程序集失败。全成功均调用三个工程并以 0 结束；指定程序集模拟返回 7 时，均在该次调用立即中止，PowerShell 退出码为 1，诊断包含“退出码 7”，后续程序集不再被调用。没有启动真实 dotnet 进程。

指定已跟踪文件的 git diff --check 通过；当前门禁文件和四个提取步骤的 PowerShell 语法解析均无错误。

**验证边界与交付**

本轮没有运行 dotnet build／test／run，没有编译 IL 夹具，没有运行产品、MainForm startup 或联网 E2E；没有修改生产代码、测试或工作流，没有提交。上述 IL 结论来自代码复审；新增 C# 测试在 Debug／Release 中的实际通过情况，仍由全员 ready 并冻结后的主控串行验证提供。

按主控最新说明，MIN_TEST_TOTAL 保持 1632，待最终实际发现数确定后同步提高两份工作流；本阶段不以猜测数量替代实测，也不把待计数视为协议缺陷。本 PASS 仅对应以下版本的指定改动，不代替整批集成验收。

仓库内唯一写入文件为本报告。探针文件均位于独立目录 `C:\Users\wrz20\AppData\Local\Temp\LiteMonitor-validation-review-20261024-1792775319542`。自动审批审查拒绝了该临时目录的递归清理，返回原因仅为“blocked by policy”；目录保留为证据，未改用其他删除方式绕过限制。

| 受审文件 | SHA-256 |
| --- | --- |
| tests/LiteMonitor.Tests/HttpLifecycleTests.cs | 99E6B643561DD5E4FC968F4327D964FB976B19F014CBD4F07706A795EF7A2741 |
| tests/LiteMonitor.Tests/IlInspector.cs | 94604FB58A604E84C9A0B20B4E007662DB4F81C6C0E02638488E25F2B998443F |
| tests/LiteMonitor.Tests/ReviewReleaseValidationGateTests.cs | 82E586A2CF320A2CA04F409E413C40F21F5C808D1CC9F61D2C14499783E7F62E |
| .github/workflows/assert-test-counts.ps1 | C3C1AE1B3EE0334322820E3FB2A72F62F52360CCFBA41396CDAF155BF6EA8B8A |
| .github/workflows/ci.yml | 70D4BFC34D74B1FAF8F6FA355FED88998CCD6A3A7A797D8F6A7874BD36D370D9 |
| .github/workflows/release.yml | 08F2680C48807B854D62CCBEB97864EE9796F2035C230BF42A225737F2438347 |
