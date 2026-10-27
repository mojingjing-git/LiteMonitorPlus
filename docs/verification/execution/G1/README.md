# G1 修复切片门禁报告

- 日期：2026-10-27（Asia/Shanghai）
- 计划：`docs/plans/2026-10-27-repair-and-product-execution-plan.md`（v2.1）
- 范围：R1–R5、F01–F11、INF-06/INF-07、全量回归、真实 GPU/DPI/会话/供电证据。
- 结论：**自动化门禁通过；G1 不签字，Slice 1 不发布。** 真实 DPI/会话/供电矩阵、R3 模块级故障注入、R4 真实代理/DNS 仍未覆盖。

## 1. 自动化门禁

```powershell
dotnet build LiteMonitor.sln -c Release --no-incremental --nologo `
  -p:TreatWarningsAsErrors=true `
  -p:WarningsNotAsErrors=$legacy `
  -p:NuGetAudit=false
# exit 0；44 warnings，0 errors（docs/verification/execution/R5/r5-solution-build-final.log）

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj `
  -c Release --no-build --nologo
# 2660 通过、7 跳过、0 失败、总计 2667；7m02s
# docs/verification/execution/R5/r5-full-tests-final.log
```

> 2026-10-27 追加 R3 的 Settings 写失败与 Partial 呈现用例后，R3 定向 10/10 通过。随后全量复跑在 testhost 中超过 10 分钟无进度，已取消（`R5/r5-full-tests-final2.log` 仅保留启动头，无汇总）；因此本次**不主张 2661 全绿**。已通过的全量基线仍是加测试前的 2660/7/0，新增 1 个测试后的全量结果待重新执行。

## 2. 逐项状态

| 项 | 状态 | 证据 / 说明 |
|---|---|---|
| F01 CI 续行 | 关闭 | `ci.yml` 门禁 2 通过；无独立 `-p:` 命令 |
| F02 分阶段加载/应用结果 | 核心关闭，残余 | R3 报告；Settings 写失败、Partial UI 已覆盖；模块级故障注入、GPU 首帧、恢复旧主题再失败未覆盖 |
| F03 GPU 门禁/RunSta | 自动化关闭 | R5 报告；200 次真实切换通过；真实 DPI/会话/供电留 G1 |
| F04 主效果自动降级 | 自动化关闭 | R2 报告；派生 fallback 主题；像素/截图待 G1 |
| F05 横向 GPU 回退 | 自动化关闭 | R2 报告；横向 GDI 回退路径 |
| F06 任务栏设置快照 | 自动化关闭 | R2 报告；`ApplySettings` 接线 |
| F07 最小化暂停/恢复 | 自动化关闭 | R1 报告；50 次隐藏/恢复待 G1 |
| F08 DPI 预览 | 自动化关闭 | R1 报告；96–192 DPI 实机待 G1 |
| F09 任务栏导出 formFactor | 自动化关闭 | R2 报告；独立 `PreviewFormFactor`；投稿默认 orientation 不变 |
| F10 凭据与动态主机授权 | 核心关闭，残余 | R4 报告；真实代理/DNS、默认存储注入未覆盖 |
| F11 GPU 热更新代次 | 自动化关闭 | R1 报告；30 次热更新待 G1 |

## 3. 未关闭项（发布阻断）

1. **真实 DPI/会话/供电矩阵**：96–192 DPI、远程会话、AC/DC 切换、休眠恢复未测。
2. **R3 模块级故障注入**：菜单/任务栏可控失败、GPU 首帧 Pending/Unknown、恢复旧主题再次失败。
3. **R4 真实代理/DNS**：企业代理、DNS 重绑定、真实 VPS 模板未验证；`resources/plugins/vps_traffic.json` 仍为本地未跟踪模板。
4. **G0 独立复核签字**：工程完成，但尚无独立复核记录。

## 4. 放行结论

- Slice 1 代码与自动化证据已齐，可进入 G1 实机矩阵；**不得**以“全量测试绿”替代真实 DPI/GPU/供电签收。
- 上述 1–3 项关闭并由集成/验收签字后，才可将 G1 标为通过。
