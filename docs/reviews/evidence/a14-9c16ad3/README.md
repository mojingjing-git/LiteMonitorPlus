# A14 独立复核证据

对应报告：`../../2026-10-20-appearance-a14-independent-recheck.md`。对象固定为 `9c16ad3`，产品实现为 `2491ff0`。

## 文件与解释

- `suite-summary.json`：构建、577 项单元测试、全部组件/接线探针，以及两夹具配对耗时结果。
- `independent-repros.json`：用 A13 相同输入复跑的旧反例。原始裸 `resize_shape` 没有提供新契约要求的快照提供者，故仍回退为 40；生产接线结果见下项。
- `resize_shape_integrated.json`：安装与主窗口相同的 SnapshotProvider 合同，尺寸变大后切角为 64。
- `preview_source_status.json`：固定样本被误标为 LiveFrame 的新反例。
- `texture_factory_failure.json`：锁定位图试验没有触发构造失败，正常创建/释放计数配平；不能将其描述为已复现异常泄漏。
- `wiring-evidence.json`、`decor-evidence.json`、`decor-renderer-evidence.json`、`window-shape-evidence.json`、`a08-static-backdrop-evidence.json`：固定版本的原始探针结果。
- `repro-program.cs.txt`、`repro-project.csproj.txt`、`repro-legacy-fixture.cs.txt`：独立程序。后者是固定 A13 的夹具源码，保证旧反例输入未被悄悄替换。

完整 TRX、构建和运行日志、图片与配对结果保存在 `.review-scratch/appearance-a14-audit-9c16ad3/`。

## 重跑补充反例

将 `9c16ad3` 导出至 `target-src`，在同级 `repros` 中把上述三个源码文本分别还原为 `Program.cs`、`Repros.csproj`、`LegacyFixture.cs`。从 `repros` 执行：

```powershell
dotnet build Repros.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet bin/Release/net8.0-windows/Repros.dll ../evidence/recheck
```

可在输出路径后追加单项名称：`preview`、`noise_eviction`、`bar_clip`、`partial_background`、`off_allocations`、`texture_geometry`、`noise_pipeline`、`resize_shape_integrated`、`preview_source_status`。

配对耗时抽测从 `target-src` 执行：

```powershell
dotnet build tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll --appearance-only --appearance-set full --appearance-filter DarkFlat_Classic__vertical__gpu1__dpi96__scale1.0,CyberPunk_Mech__vertical__gpu4__dpi96__scale2.0 --appearance-draws 40 --appearance-paired --appearance-paired-rounds 3 --appearance-invalidation none --out ../evidence/paired-recheck
```

必须确认夹具数为 2；这个版本不支持 `--appearance-entry` 或 `paired` 夹具集。`extraMsPerDraw` 来自 Stopwatch，解释为经过时间。该抽测没有替代原报告所有夹具，也不证明 CPU 增量预算通过。
