# A13 独立验收证据

对应报告：`../../2026-10-20-appearance-a13-independent-review.md`。固定源码为 `954a634`，不能将工作区正在修改的 A14 代码混入此次结果。

## 文件

- `suite-summary.json`：固定源码/产物哈希、构建、单元、接线、轮廓和旧入口 full 探针汇总。旧入口 44 个局部失效场景有差异，退出码 0 不代表视觉全部通过。
- `independent-repros.json`：预览选项、缓存、裁剪、局部底色、关闭态分配、斜纹、尺寸变化的原始结果。
- `noise-pipeline.json`：通过主渲染入口触发已释放画刷的异常栈。
- `texture-image-ownership.json`：源图和 `TextureBrush.Image` 返回对象的独立释放行为。
- `wiring-evidence.json`：70 个接线用例均通过。
- `window-shape-evidence.json`：11 个窗口轮廓用例均通过；操作系统为 `10.0.26300.0`。
- `repro-program.cs.txt`、`repro-project.csproj.txt`：独立复现程序，保存为文本以免被主项目的默认 C# 文件收集规则纳入构建。
- PNG：预览/主入口效果、裁剪越界和纹理瓦片的原始图像。

## 本机重跑

本次隔离目录：`G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/`。其中 `target-src` 为固定源码导出，`repros` 为独立复现项目，`logs` 与 `evidence` 为完整产物。

在 `target-src` 执行：

```powershell
dotnet build LiteMonitor.sln -c Release -m:1 --no-incremental -p:UseSharedCompilation=false
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --no-restore -m:1
dotnet build tests/AppearanceWiringProbe/AppearanceWiringProbe.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet tests/AppearanceWiringProbe/output/AppearanceWiringProbe.dll --out ../evidence/wiring-recheck
dotnet build tests/AppearanceWindowShapeProbe/AppearanceWindowShapeProbe.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet tests/AppearanceWindowShapeProbe/output/AppearanceWindowShapeProbe.dll --out ../evidence/window-recheck
dotnet build tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll --appearance-only --appearance-set full --appearance-draws 20 --appearance-invalidation all --out ../evidence/appearance-legacy-recheck
```

在 `repros` 执行：

```powershell
dotnet build Repros.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet bin/Release/net8.0-windows/Repros.dll ../evidence/repros-recheck
```

最后一个命令默认运行全部补充场景，也可在输出目录参数后指定单项名称：`preview`、`noise_eviction`、`bar_clip`、`partial_background`、`off_allocations`、`texture_geometry`、`resize_shape`、`texture_image_ownership`、`noise_pipeline`。

在新机器复现时，先把 `954a634` 导出到 `target-src`，再在同级 `repros` 中将两个 `.txt` 分别还原为 `Program.cs` 和 `Repros.csproj`。固定资源与接线探针夹具均来自该提交，不读取用户配置。测试需要本机测试进程通信；复现程序不打开硬件监控、不抓屏，窗口场景仅创建不显示的离屏 Form。

关闭态分配数字为同一线程、相同场景的新旧入口差额，不能直接解释成整机 CPU 百分比。长时间内存增长和真实系统人工观感仍需另外验收。
