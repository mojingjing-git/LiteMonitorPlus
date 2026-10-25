# 派发单 P-E08a —— 产物定位与证据模板（E08 基础部分）

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| W1（三路并行之一） | HEAD | `pkg/E08-artifacts`（独立工作树） | 完善计划 §12 前两条（产物定位、证据模板）；G5 缺口收口 |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-E08a 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读完善计划 §12 与审查报告 G5 节（`docs/reviews/2026-10-26-post-plan-full-review.md`）。开工建独立工作树 `git worktree add .worktrees/e08-art -b pkg/E08-artifacts <HEAD>`；环境变量 `NUGET_PACKAGES=G:\TOOL\LiteMonitor-fork\.nuget-packages`、`DOTNET_CLI_HOME=G:\TOOL\LiteMonitor-fork\.dotnet-cli-home`。

## 任务

1. **Host 产物定位改造**（G5 缺口）：`tests/LiteMonitor.Tests/I2HostBinaries.Locate` 支持显式产物路径（环境变量 `LITEMONITOR_HOST_EXE` 优先，其次默认构建目录）；目录缺失或 DLL 摘要与构建清单不符时**直接失败**，不在 Debug/Release/其他工作树之间静默挑旧产物。补自定义输出目录（`BaseOutputPath=bin/review-xxx/`）复现 G5 场景的测试。
2. **证据记录模板**：新增 `docs/verification/templates/evidence-template.md`——字段：源 SHA、dirty 补丁摘要（`git status --porcelain` + diff hash）、配置、各测试程序集 DLL 摘要、主程序/Host/Contracts 摘要、依赖锁定情况、执行命令、TRX 发现/执行/通过/失败/跳过及逐项原因、机器环境。附一个生成这些字段的 PowerShell 小工具 `scripts/collect-evidence.ps1`（只收集，不上传/不改 CI）。
3. **测试类纳入建议清单**：列出建议进 `MUST_EXECUTE_TEST_CLASS` 的关键测试类（回归包/配置保存真实入口/快照语义/进程故障链），只写建议不改 CI 阈值（阈值随实际发现数由集成人统一上调）。
4. 行尾空白与噪音清理范围盘点：列出 LiteUI 等文件的行尾空白清单（只列不改，改动随相关批次走）。

## 边界

- 零产品源码改动（`src/**` 不碰）；只动 `tests/LiteMonitor.Tests/I2HostBinaries*`、`scripts/collect-evidence.ps1`（新）、模板（新）、本报告。
- 与 P-E00 并行：不碰其测试文件；与 P-E05 零交集。

## 完成产物

1. 分支提交：定位改造 + G5 场景测试 + 模板与工具。
2. `docs/verification/acceptance/E08a-report.md`：改造说明、G5 复现/修复证据、模板使用说明、建议纳入清单、遗留（CI 接线归后续批次）。
