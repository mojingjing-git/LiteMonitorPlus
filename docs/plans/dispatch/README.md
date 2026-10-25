# 外派执行总则（dispatch README）

配套：[母计划](../2026-10-25-verified-bugs-remediation-plan.md) ｜ [子智能体拆解计划](../2026-10-25-verified-bugs-remediation-subagent-plan.md)（§4 为各包完整规格）｜ [F00 基线](../../verification/acceptance/F00-baseline.md)

**用法**：`docs/plans/dispatch/` 下每个 `P-F*.md` 是一个自足的外派单，交给一个执行智能体（或人）。一次只派一包；执行者必须先读本 README，再读其派发单与拆解计划 §4 对应节。

## 1. 硬约束（违反即返工）

1. **分支**：从 `master` 切 `fix/F0x-slug`（研究包 `research/`、工具包 `tools/` 前缀）。不推送远端、不动 master。提交格式 `fix(F0x): …` / `test(F0x): …`。
2. **改动范围**：只改派发单「改动文件」清单内文件 + 新建文件。清单外的必要改动写入验收记录"偏离"节，不自行修改。
3. **禁改 CI**：`.github/workflows/ci.yml`、`release.yml` 一字不动（阈值 1876/1 只由协调者在批次集成时统一上调）。
4. **共享热点**（`src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、`src/UI/UIController.cs`、`src/System/HardwareMonitor.cs`）：仅限派发单明确授权的最小编辑；新增设置一律"字段 + Draft-Commit 保存"。特例：P-F03 与 P-F08a 在 `HardwareMonitor.cs` 各限 ≤30 行且局限于自己的区域；集成时先并 F03 再 rebase F08a。
5. **代码约束七条**（全文见拆解计划 §1.2）：不回退 fork 已有能力；修复含迁移；0 是有效数据（缺失/失败/过期各有状态）；恢复有界（去抖/退避/单次在途）；UI 只投递恢复请求；时钟一律 `TimeProvider`/单调可注入；应用 catch 不冒充原生边界。
6. **语言资源** `resources/lang/*.json`：只新增 key 不改语义，8 语种同步（缺失语种回退英文并注明）。
7. **回归红线**（母计划 §6.2 六条）：任一包不得破坏；相关既有测试必须保持绿。

## 2. 测试与验证

```bash
dotnet build LiteMonitor.sln -c Debug --nologo
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build
dotnet test tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj -c Debug --no-build
dotnet test tests/LiteMonitor.Updater.Tests/LiteMonitor.Updater.Tests.csproj -c Debug --no-build
# Web 端到端（P-F01/P-F02 及 Web 触碰包）：
pwsh scripts/run-probes.ps1 -Groups web -SkipUnitTests
```

- 先写会失败的用例再修实现（红→绿）；优先真实调用产品路径，禁止复制算法自证。
- 测试禁用 `Thread.Sleep` 真实等待；探针只用沙箱配置，不碰用户 settings.json。
- 无目标硬件的场景在验收记录标"待实机"；mock 结果不得用于关闭硬件特有问题。

## 3. 派发顺序

| 批次 | 包（可并行） | 前置门槛 |
|---|---|---|
| W1 | P-F01、P-F03、P-F04、P-F08a、P-F09a、P-F12、P-F13、P-F15 | F00（已完成） |
| W2 | P-F02、P-F05、P-F07、P-F14a | F02←F01 合并；F05←F04 合并；F07/F14a 仅需 F00 |
| W3 | P-F06、P-F08b、P-F14b | F06←F05；F08b←F07+F09a；F14b 仅需 F00 |
| W4 | P-F09b→P-F10 | F09b←F09a 决策且在 F08b 后；F10←F07（与 F09b 协调串行） |
| W5 | P-F11 | ←F03+F07+F10 全部合并 |

硬件实现链 **F07→F08b→F09b→F10→F11 严格串行**（共享 `HardwareMonitor.cs`/`SensorMap.cs`/`DiskManager.cs`/`Settings.cs`）。发布切批：R1=F01+F02+F03（+独立验收的 F12/F13），F02 完成即可出，不等硬件链。

## 4. 集成协议（协调者/用户执行）

1. 每包合并前核验：验收记录 `docs/verification/acceptance/F0x-report.md` 存在且"代码层验收"通过；分支仅含授权文件改动。
2. 合并序：批内先 Web/窗口/UI，`HardwareMonitor.cs` 冲突由协调者裁决（F03 先并、F08a rebase）。
3. 每合一批：Debug+Release 构建 + 三个测试项目全量 + `run-probes.ps1` 相应组；按新增用例数统一上调两个 workflow 的 `MIN_TEST_TOTAL`（逐字一致），`MAX_SKIPPED_TESTS` 不得放宽。
4. 更新母计划 §7 状态为：代码完成/待实机/已验证/已缓解/仍需调查。
5. 回滚：每任务独立提交可单独 revert；回滚前检查后续迁移依赖（母计划 §6.3）。

## 5. 开工基线事实（详见 F00-baseline.md）

- HEAD `98c9237`；32/33 任务目标文件与验证基线逐字节一致，唯 `ThemePreviewService.cs` 漂移（P-F14a 重定位行号）。
- 执行机：Windows 11 + **双 NVIDIA（RTX 3080 / 3080 Ti）**——P-F09b NVML 场景可部分本机实测，P-F07 可用本机双卡辅助验证；Win10 任务栏与 B580/Intel 项待实机。
- 依赖：LHM 0.9.6 + DiskInfoToolkit 1.1.2（传递）。
- WebProbe 红灯锚点已落盘：C5/C6=200（WEB-OWS）、C7=403（#580）、C12=101（转发头被忽略）。
