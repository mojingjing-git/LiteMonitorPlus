# 33245bc 独立复核证据

对应报告：`../../2026-10-21-appearance-a15-independent-review.md`。

## 关键文件

- `lifecycle-repros.json`：四项生命周期缺陷的第一次运行结果。
- `lifecycle-repeat-summary.json`：三次结果一致，包括双 worker、旧帧发布、失效图借用、暂停后发布及恢复后旧故障文字。
- `lifecycle-program.cs.txt`、`lifecycle-project.csproj.txt`：独立复现程序，只使用受控假后端，不捕获屏幕、不创建可见窗口。
- `cpu-data-recalculation.json`：已提交 5 轮 CPU 数据的复算；原始文件路径见 `suite-summary.json`。
- `cli-validation.json`：未知参数、不存在主题及合法 5 轮短测的退出码。
- `ApplicationConfiguration.g.cs.txt`：独立编译时实际生成的 PMv2 调用，保存为文本避免被主工程收集进编译。
- `m2-source-gate-evidence.json`、`m2-runtime-gate-evidence.json`、`wiring-evidence.json`：原探针均通过，不能覆盖新反例。
- `livecheck-script-parse.json`：仅 AST 语法结果，不表示实际脚本或 GUI 已运行。

完整产物位于 `.review-scratch/appearance-a15-audit-33245bc/`。单元测试 TRX 为该目录 `evidence/unit/a15-unit.trx`。

## 重跑生命周期反例

将 `33245bc` 导出为 `target-src`；在同级 `lifecycle-repros` 中，将两个源码文本还原为 `Program.cs`、`LifecycleRepros.csproj`：

```powershell
dotnet build LifecycleRepros.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet bin/Release/net8.0-windows/LifecycleRepros.dll ../evidence/lifecycle-recheck
```

程序用闸门精确控制停止/重启和暂停时序；红色像素只标识旧后端调用的合成帧。它输出观察值，退出码 0 表示程序完成，**不表示生命周期断言通过**。当前版本的错误表现是：并发数 2、旧红帧发布、旧线程写 Stopped 而新线程仍活着；无效租约仍被借出；暂停后仍接管帧；Running 状态仍显示故障文字。

CPU CLI 短测是每段只画 1 次，检查参数传播及噪声内返回 null；不能拿它替代长批次测量。审查没有执行 `run-livecheck.ps1` 的构建/备份/启动流程，也没有执行任何电源操作。
