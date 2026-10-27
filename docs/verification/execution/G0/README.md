# G0 基线报告

- **日期**：2026-10-27（Asia/Shanghai）
- **计划版本**：`docs/plans/2026-10-27-repair-and-product-execution-plan.md`（执行基线 v2.1）
- **Owner**：集成（兼岗）
- **结论**：**INF-00 基线冻结、INF-01 CI 修复、C1–C3 合同、INF-05 审计、D0 决策均已完成**；INF-06/INF-07 代码与回归完成，G0 待集成/独立复核签字与 INF-10 协作规则签字。

## 1. 代码基线

| 项目 | 值 |
|---|---|
| HEAD | `4de25d38add7f8b2342303805fdf0b654c57fa55` |
| 分支 | `master` |
| 工作区 | 有未提交变更：`ci.yml` 修改、9 个 docs 删除、6 份新计划、2 份审查报告、G0 证据目录、`resources/plugins/vps_traffic.json` 未跟踪 |
| 说明 | 计划/审查文档单独提交，不混入代码提交；`vps_traffic.json` 归属待 D0-7 决策 |

## 2. 构建门禁

命令（与 `.github/workflows/ci.yml` 的 Gate 2 等价）：

```powershell
$legacy = 'CS0168%3BCS0649%3BCS8632%3BxUnit2031%3BxUnit1030%3BxUnit1031%3BxUnit2013%3BxUnit1026%3BxUnit2000%3BxUnit2012%3BxUnit2017%3BxUnit2020'
dotnet build LiteMonitor.sln -c Release --no-incremental --nologo -p:TreatWarningsAsErrors=true -p:WarningsNotAsErrors=$legacy -p:NuGetAudit=false
```

结果：**exit 0**；44 个警告，0 个错误。完整输出见 `build-gate.log`，退出码见 `build-gate-exit.txt`。

## 3. 测试基线

引用 `docs/verification/theme-studio/review-2026-10-27/test-summary.json`（同一 HEAD）：

| 程序集 | total | passed | skipped | failed |
|---|---:|---:|---:|---:|
| main | 2633 | 2626 | 7 | 0 |
| updater | 73 | 73 | 0 | 0 |
| GPU non-performance | 19 | 19 | 0 | 0 |
| **合计** | **2725** | **2718** | **7** | **0** |

targeted 448/447/1 与 main 有重叠，不计入合计。测试摘要同时记录 Release 构建 exit 0 / 44 warnings / 0 errors。

## 4. 未验证与剩余限制

- 真实 GPU 跨帧、真实 DPI 175%、AC/DC/锁屏/多屏与实机长跑未在本次基线执行；这些属于 G1/G2/G4 证据，不能用当前结果代替。
- D0-6 已确认本机存在 NVIDIA RTX 3080 / 3080 Ti（驱动 32.0.16.1088）；仍须在物理显示会话执行，并禁用虚拟显示适配器/RDP。
- `resources/plugins/vps_traffic.json` 经 D0-7 决策按用户本地模板处理，本轮不提交；F10 回归使用独立测试夹具。
- 本报告只证明基线可构建，不证明 11 项缺陷已修复。

## 5. G0 任务状态

| 任务 | 状态 | 证据/备注 |
|---|---|---|
| INF-00 基线冻结 | 完成 | 本报告、`git-*.txt`、`build-gate.log` |
| INF-01 CI 修复 | 完成 | `.github/workflows/ci.yml` 续行修复；本地 Gate 2 exit 0 |
| INF-02 C1 合同 | 完成 | `src/Core/Actions/Services/ApplyOperationResult.cs`；`ModuleApplyResult` 增加 revision/retryable；5 个合同测试 |
| INF-03 C2 合同 | 完成 | `src/Diagnostics/DiagnosticEvent.cs`、`DiagnosticRedaction.cs`、`DiagnosticEventStore.cs`；4 个合同测试 |
| INF-04 C3 合同 | 完成 | `ConsumerSamplingDemand` + `SamplingChannels`；`DemandRegistry` 保留通道位/最大间隔；4 个合同测试 |
| INF-05 E03/E04 审计 | 完成 | `INF-05-e03-e04-gap-matrix.md`：E03 保存失败/模块结果未接线；E04 MainForm 兼组合根、Web 单例未 Dispose |
| INF-06 E03 必需接线 | 完成（代码/回归） | `SettingsForm.ApplySettings` 失败不关闭、不合并；`ApplySettingsSelectivelyWithResults` 汇总模块结果；9 语言 key；53/53 相关测试 |
| INF-07 E04 生命周期收口 | 完成（代码/回归） | `INF-07-lifecycle-report.md`：保存/停止/释放逐项隔离；`LiteWebServer.Dispose` 清实例；Web 回归 76/76；50 次窗口开关烟测留 G1 |
| D0 决策 | 完成 | `D0-decisions.md`：D0-1～D0-10 已记录；D0-6 真实 GPU 已确认，D0-7 `vps_traffic.json` 不入库 |
| INF-10 协作规则 | 完成（待签字） | `INF-10-collaboration-rules.md`、`.github/PULL_REQUEST_TEMPLATE.md`；分支/单 writer/证据/评审规则已落地 |

## 6. 证据清单

- `git-head.txt`、`git-branch.txt`、`git-status.txt`：代码基线。
- `dotnet-version.txt`：构建环境。
- `build-gate.log`、`build-gate-exit.txt`：Gate 2 构建证据。
- `contract-tests.log`、`contract-tests-exit.txt`：C1/C2/C3 合同测试证据（13/13 通过，exit 0）。
- `INF-05-e03-e04-gap-matrix.md`：E03/E04 模块应用与生命周期缺口矩阵。
- `INF-07-lifecycle-report.md`、`inf07-lifecycle-build.log`、`inf07-lifecycle-tests.log`：INF-07 退出顺序与 Web 生命周期回归。
- `D0-decisions.md`：D0-1～D0-10 决策、D0-6 GPU 证据、D0-7 模板归属。
- `INF-10-collaboration-rules.md`、`.github/PULL_REQUEST_TEMPLATE.md`：协作与 PR 规则。
- `README.md`：本报告。

## 7. 签收

| 角色 | 姓名 | 日期 | 结论 |
|---|---|---|---|
| 集成 |  |  | 待签 |
| 独立复核 |  |  | 待签 |
