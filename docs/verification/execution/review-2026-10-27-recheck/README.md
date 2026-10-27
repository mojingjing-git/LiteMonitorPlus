# C01–C07 再复核证据

日期：2026-10-27（Asia/Shanghai）。报告：[修复再复核](../../../reviews/2026-10-27-p1-p2-recheck-review.md)。

| 文件 | 内容 |
|---|---|
| [validation-summary.json](validation-summary.json) | 本轮较宽定向 73/1/0（通过/失败/跳过）；独立真 GPU 首帧门禁 1/0/0 |
| [source-manifest.json](source-manifest.json) | 当前未提交实现、测试、资源与 CI 的 SHA-256 |
| [targeted-tests.txt](targeted-tests.txt) | 完整定向日志与 revision 字段缺失的既有测试失败 |
| [observations.json](observations.json) | C01/C04 原样例不再触发；Missing 转到原设备并恢复为 Pending；C05 原隐私字符串从 ZIP 消失 |
| [apply-fault.txt](apply-fault.txt) | 同名新修订固定加载后异常，返回 Partial/theme-runtime-partial |
| [diagnostic-ui.json](diagnostic-ui.json) | 真实摘页停止 Timer；等待前后读取次数相同，详情跨刷新保留 |
| [fallback-ui.json](fallback-ui.json) / [fallback-preview.png](fallback-preview.png) | 首次失败后实际派生背景，像素计数和目视检查 |
| [numeric-diagnostic.json](numeric-diagnostic.json) | 0/1/2/512/1200/42.0 被误判敏感，事件丢失 revision/count/duration_ms |
| [child-precondition.json](child-precondition.json) | GPU_TESTS=1、合法设置 GPU effects=false 下，同一场景前置操作仍创建 0 个 GPU 设备、呈现 0 帧；仅六次切换 |
| [parent-assembly-path.txt](parent-assembly-path.txt) / [child-assembly-path.txt](child-assembly-path.txt) | 当前独立输出与子命令默认输出的 TargetPath 不一致 |
| [no-match.txt](no-match.txt) / [no-match-result.json](no-match-result.json) | 零匹配测试时 dotnet test 仍返回 exit 0 |
| [gpu-observations.json](gpu-observations.json) / [gpu-gate.txt](gpu-gate.txt) | 当前 175% DPI 下恢复出帧、设备复用与真实首帧门禁 |
| [invalid-preview-loop.json](invalid-preview-loop.json) | 原失败通知闭环没有复发 |
| [Probe.cs.txt](Probe.cs.txt) / [Probe.csproj.txt](Probe.csproj.txt) | 独立探针源码，.txt 防误编译 |

临时项目在 `.tmp/review-20261027-recheck/Probe/`。将源码恢复相同层级的 .cs/.csproj，构建后从仓库根目录串行执行：

```powershell
dotnet build .tmp/review-20261027-recheck/Probe/Probe.csproj -c Release `
  -p:BaseOutputPath=bin/review-20261027-recheck/ -p:NuGetAudit=false

$recheckProbeExe = '.tmp/review-20261027-recheck/Probe/bin/review-20261027-recheck/Release/net8.0-windows/Probe.exe'
& $recheckProbeExe closure
& $recheckProbeExe apply-fault
& $recheckProbeExe diagnostic-ui
& $recheckProbeExe fallback-ui
& $recheckProbeExe numeric-diagnostic
& $recheckProbeExe child-precondition
& $recheckProbeExe gpu
& $recheckProbeExe invalid-ui
```

`child-precondition` 只在自身进程内设置有效的 GPU 关闭配置，不写用户设置，复用真实主题/下拉路径但只跑六次；不是 200 次压力通过的声明。`apply-fault` 仍使用受控的未初始化窗体依赖，在真实加载后触发固定异常，只核对提交判定分支，不代签 G1 模块故障矩阵。

本轮没有执行默认目录的 200 次子测试，避免把潜在旧产物当当前结果；也没有故意制造九分钟卡死。零匹配测试探针是只读验证。隐私样例仍全部为合成数据，仅导出本地临时包，无上传或真实凭据。

完整 TRX 在 `.tmp/review-20261027-recheck/results/`。构建及 GPU 使用串行时段，结束后没有残留 Probe/testhost。全量未执行；不将实施方既有基线算作当前通过。
