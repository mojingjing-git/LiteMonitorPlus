# 派发单 P-B0b —— B0 收尾：行为 fixture 采集 + 性能基线

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| B0（首批） | master `98c0799` 之后 | `pkg/B0-fixture-perf` | Plan B §6.1/§6.3、Plan A §8 性能口径 |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-B0b 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读 `docs/plans/dispatch/README.md` 外派总则，再通读 `docs/plans/2026-10-26-architecture-decoupling-plan.md` 的 §6（兼容与验收）与 `docs/plans/2026-10-26-B0-I0-baseline-inventory.md` 的 §7（你的任务来源）。
> 开工前先建独立工作树：`git worktree add .worktrees/b0-tail -b pkg/B0-fixture-perf master`，全程在该工作树内工作（主工作树有并行会话在改 `src/`，**你不得改 `src/` 的任何文件**）。

## 证据锚点

- 行为 fixture 的意义：B1/B2 将迁移采集消费方、B4 将拆分格式规则；改造前后跑同一套"输入→输出"用例必须逐字一致（Plan B §6.1 行为保持矩阵第一行）。
- 待抄题的 API（全部在 `src/Core/MetricUtils.cs`）：`GetValueStr(key, value, compact, userFormat)`(:106)、`GetUnitStr(key, value, context)`(:206)、`FormatDataSize/FormatDataSizeParts`(:503/:524)、`GetState(key, value)`(:535，阈值)、`GetDisplayUnit`(:423)；以及 `src/Core/MetricItem.cs` 的 `TextValue/CachedValueText` 组装路径（:77/:89 起）。
- 性能测量复用既有设施：`tests/LiteMonitor.PerformanceProbe`（MicroBenchmarks/PairedCpuMeasurement/CountingHardware）与 `scripts/run-probes.ps1`（串行入口，注意其"并发跑会争抢主工程 obj/"的告警）。启动产品用 `scripts/start-current.ps1`。
- 测试环境：`$env:NUGET_PACKAGES='G:\TOOL\LiteMonitor-fork\.nuget-packages'`（工作树内路径换成 `.worktrees/b0-tail` 下对应值按 README 总则处理）。

## 要点

1. **fixture 是抄题，不是出题**：所有用例必须对当前代码实跑取实际输出后固化；发现疑似 bug 一律写进报告，不得"顺手修正"期望值。覆盖 Plan B §6.1 第一行的四类：正常/紧凑格式、阈值边界（`GetState` 的临界值两侧）、用户单位/自定义格式（`userFormat`）、多语言（若 `LanguageManager` 可在测试内切换则覆盖，不能则记录限制并固定单一语言）。
2. 数值域覆盖：真实 0、缺失（null）、极大（1e12）、极小、负值（温度/电压可能出现）、千位进制边界（1023/1024/1023999/1048576 对 `FormatDataSize`）。
3. fixture 落在 `tests/LiteMonitor.Tests/`（如 `MetricFormatBehaviorFixtureTests.cs`），命名含 `Fixture`，与普通单测区分；断言逐字比较（含空串与空格）。
4. 性能基线脚本 `scripts/perf-baseline.ps1`：① 启动时间（启动进程到 `WaitForInputIdle`）；② 60 秒采样窗口的 CPU 时间与私有字节（`Get-Process`，1 秒粒度）；③ 可自动化的探针项（PerformanceProbe 里现成的微基准）；④ 输出 `docs/verification/baseline/perf-baseline-<yyyymmdd-HHmm>.md` + 原始 json，含机器/GPU/OS/版本/HEAD 记录。脚本可重复执行，供 B6 前后对比。
5. 性能基线需真实运行 GUI 程序：在本机实跑一次并把结果文件入库；若某项（如 UI 刷新延迟、目录查询延迟）无法无侵入测量，明确列入"无法自动采集"清单，不许造数。
6. **禁改**：`src/` 零改动；不动 `scripts/architecture-assert.ps1` 与 `architecture-baseline.json`；不动 CI 阈值（新增测试数写进报告，由协调者上调 `MIN_TEST_TOTAL`）。

## 完成产物

1. 分支 `pkg/B0-fixture-perf`：fixture 测试文件（建议 ≥40 用例，四类×数值域成矩阵）；当前代码全绿。
2. `scripts/perf-baseline.ps1` + 一份实测基线记录（md+json）。
3. `docs/verification/acceptance/B0b-report.md`：用例矩阵、疑似 bug 清单、性能数字、无法自动采集项、新增测试数（供上调门禁）。
4. 合入顺序：独立，不依赖 P-B1B2；建议在 B2 迁移动工前合入（fixture 即迁移的回归网）。
