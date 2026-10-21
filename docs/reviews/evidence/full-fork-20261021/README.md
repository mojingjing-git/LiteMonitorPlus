# 全量分叉审查证据

对应 [审查报告](../../2026-10-21-full-fork-review.md)，目标提交为 `d6946dddbe7b831da139ba96bcd3a3e1bc191c13`。

- `verification-summary.json`：范围、测试计数和检查结果。
- `commits.txt` / `changed-files.txt`：从最初克隆点 `169e90a` 起的提交及文件清单。
- `runtime-repros-results.json`：Web 停止竞态、插件卸载越界、玻璃连续重绘的生产程序集复现结果。
- `runtime-repros-program.cs.txt` / `runtime-repros-project.csproj.txt`：复现源码及工程；代码仅操作隔离输出目录和本机回环，不显示窗口、不捕获桌面。
- `updater-fault-results.json`：更新器部分覆盖失败后的恢复结果。
- `updater-fault-harness.cs.txt` / `updater-fault-project.csproj.txt`：直接链接生产更新器源码的故障注入工程。文件写入注入可控 I/O 故障；进程枚举、启动和等待使用无副作用的替身，因此不会结束或启动任何用户程序。
- `nuget-audit.json`：NuGet 查询的原始 JSON。

复现工程以 `.txt` 归档，避免进入主工程的默认 C# 编译集合。复跑时，在仓库根目录下分别恢复为以下结构（工程里的相对引用按此结构编写）：

```text
.review-scratch/full-review/
  runtime-repros/
    RuntimeRepros.csproj
    Program.cs
  updater-fault/
    UpdaterFault.csproj
    Harness.cs
```

然后运行：

```powershell
dotnet run --project .review-scratch/full-review/runtime-repros/RuntimeRepros.csproj -c Release
dotnet run --project .review-scratch/full-review/updater-fault/UpdaterFault.csproj -c Release
```

更新器复现会保留隔离目录内的 `review-update.zip` 和备份；再次运行前，换用新的隔离输出目录或仅移除上次复现生成的这个 ZIP。禁止把替身换成真实系统进程调用后直接运行更新器入口。

完整单元测试原始 TRX 留在本机 `.review-scratch/full-review/final-d6946dd.trx`，默认外观探针的详细产物位于 `.review-scratch/full-review/appearance-final-d6946dd/`。这些临时目录已被 Git 忽略。汇总计数取自本次实际执行，详细结果可按下列命令重新生成：

```powershell
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-restore
pwsh -NoProfile -File scripts/run-probes.ps1 -SkipUnitTests -OutDir .review-scratch/full-review/appearance
```
