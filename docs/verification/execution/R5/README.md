# R5 证据报告（F03 GPU 门禁与测试线程收口）

- 日期：2026-10-27（Asia/Shanghai）
- 计划：`docs/plans/2026-10-27-repair-and-product-execution-plan.md`（v2.1）
- 范围：真实 GPU 目标切换门禁、RunSta 线程回收、全量回归、测试线程异常收口。
- 结论：**F03 自动化门禁通过；R10 真实 GPU 出帧门禁已在 RTX 3080 物理会话通过；真实 DPI/会话/供电场景仍留 G1。**

## 1. 改动

| 落点 | 处理 |
|---|---|
| `ThemeStudioGpuSessionAndFallbackTests.cs` | RunSta 线程 `Join` 改为检查返回值；失败即抛，不再静默放过 |
| `ThemeStudioResourceTests.cs` | 200 次真实 GPU 目标切换移入子 testhost（`LITEMONITOR_C03_CHILD=1`）；子进程直接 `vstest` 当前输出目录程序集，sentinel 拒绝零匹配；先断言 GPU 激活/首帧，再断言 200 次期间帧推进且设备不重建；父测试 9 分钟超时后 `Kill(entireProcessTree:true)`；父/子各 1/1 通过 |
| `BatchB1WebSessionCredentialTests.cs` | 同上 |
| `CapturePolicyCleanupGateTests.cs` | 同上 |
| `PluginExecutorSandboxTests.cs` | 修正测试 schema 路径，改为读取实际 `dynamic_host` 示例，避免依赖未跟踪的 `vps_traffic.json` |

## 2. 验证

```powershell
dotnet build LiteMonitor.sln -c Release --no-incremental --nologo `
  -p:TreatWarningsAsErrors=true `
  -p:WarningsNotAsErrors=$legacy `
  -p:NuGetAudit=false
# exit 0；44 warnings，0 errors

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj `
  -c Release --no-build --nologo
# 通过 2759，跳过 8，失败 0，总计 2767；7m59s（r5-full-tests-recheck.log）
# 这是 2026-10-27 复核前基线；复核后的 C01–C07 修复尚未重跑全量
# 跳过项含 R10（默认不跑真实 GPU）；R10 已在物理会话单独通过，见下
```

真实 GPU 资源场景（RTX 3080 级机器，物理会话）：

```text
场景4_真实GPU主题真实目标切换200次_主效果与兼容外观交替_资源增量不累积
父测试：通过（3m12s，含子 testhost）；子 testhost：通过（2m24s）
子进程 iter=200  ms=144613
handles=363->565/562
gdi=0->75/39
user=2->14/14
privateMB=36.79->63.91/63.89
managedMB=10.67->12.57/12.11
说明：子进程由父测试启动并在 9 分钟超时后 Kill 整个进程树；GPU/D3D 线程不滞留在父 testhost。
```

R10 真实出帧门禁（必须单独显式运行；未运行时 xUnit 记为 Skipped）：

```powershell
$env:LITEMONITOR_GPU_TESTS='1'
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj `
  -c Release --no-build --nologo `
  --filter "FullyQualifiedName~R10GpuFrameGate"
# 通过 1，跳过 0，失败 0（r5-gpu-frame-gate.log）
```

断言内容：真实 GPU 预览必须观察到首帧；目标下拉切到兼容外观再切回主效果后必须继续出新帧；热更新不得重建设备。场景 4 只测资源稳定性，不能替代本门禁。

同组资源场景：S1 50 次开关 29s、S2 1000 次编辑 1s、S3 200 次主题切换 387ms，均通过。

## 3. 证据文件

- `r5-solution-build.log`
- `r5-full-tests.log`（首次运行，后被语言资源副本问题中断）
- `r5-full-tests-final.log`（P1 修复前的全量通过）
- `r5-full-tests-final3.log`（P1 修复后的全量通过）
- `r5-full-tests-p2.log`（P2 修复后的全量基线，2751/8/0）
- `r5-full-tests-recheck.log`（N01–N03 修复后的全量通过，2759/8/0）
- `r5-solution-build-p2.log`（P2 修复后的 CI 门禁 2 构建）
- `r5-p2-tests.log`（R13/R17/R18/R19/R20 定向 33/33）
- `r5-p1-alert-tests.log`（R07/R15/R16 告警定向 21/21）
- `r5-c03-child.log`（C03 父/子进程隔离 2/2，3m12s；GPU 激活/首帧/帧推进断言）
- `r5-p1-p2-recheck.log`（C01/C02/C04/C05/C06 复核定向 23/23）
- `r5-final-build.log`
- `r5-gpu-200-switch.log`
- `r5-gpu-frame-gate.log`（R10 真实首帧/持续出帧门禁）
- `r5-runsta-tests.log`
- `r5-plugin-host-test.log`

## 4. 未覆盖

- 真实 96–192 DPI、远程会话、AC/DC 供电切换、休眠恢复仍需 G1 实机矩阵。
- 200 次切换的 private MB 有增长但在测试阈值内；是否属于可接受的工作集增长，需在 G1 与 D0-6 机器上复测确认。
