# 修复验收证据

对应[验收报告](../../2026-10-21-remediation-validation.md)，代码及二进制验收提交 `a88e359`。

- `verification-summary.json`：基线、五个实现/产物提交、测试计数、跳过项和验证边界。
- `package-verification.json`：ZIP 大小、条目数、必要条目检查、更新器自检和两份更新器的摘要。
- `appearance-summary.txt`：八组默认外观探针的实际执行汇总。

在 Windows 和 .NET 8 SDK 环境中，按顺序复跑：

```powershell
dotnet build LiteMonitor.sln -c Release
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --no-restore
dotnet test tests/LiteMonitor.Updater.Tests/LiteMonitor.Updater.Tests.csproj -c Release --no-build --no-restore
pwsh -NoProfile -File scripts/run-probes.ps1 -SkipUnitTests -OutDir .review-scratch/remediation/appearance
pwsh -NoProfile -File scripts/pack-release.ps1 -Version 1.0.0
```

打包会刷新受版本管理的 `resources/LiteMonitor++.Updater.exe`；ZIP 留在被忽略的 `dist/`。需要隔离构建产物时，将输出放在仓库根的 `.review-scratch/`，避免在测试工程内嵌套其他项目的生成源码。

本机最终原始测试结果为 `.review-scratch/remediation/main-remediation-accepted.trx` 和 `.review-scratch/remediation/updater-remediation-final.trx`。全部新增行为回归使用临时文件、合成帧、可控时序或任务调用替身，不执行真实系统任务迁移。
