# P1/P2 关闭复核证据

日期：2026-10-27（Asia/Shanghai）。报告：[P1/P2 关闭复核](../../../reviews/2026-10-27-p1-p2-closure-review.md)。

| 材料 | 内容 |
|---|---|
| [validation-summary.json](validation-summary.json) | 当前构建定向 105 通过/1 跳过/0 失败；显式真实 GPU 门禁另跑 1 通过/0 跳过/0 失败 |
| [source-manifest.json](source-manifest.json) | 本轮当前实现/测试/资源/CI 的 SHA-256，不以 HEAD 代替未提交快照 |
| [observations.json](observations.json) | 生产快照告警、规则语义变化、脱敏反例与已修复 BOM/统计/场景/事件/帧率原始观察 |
| [gpu-observations.json](gpu-observations.json) | 175% DPI：热更、最小化停帧、恢复出帧和单设备复用 |
| [invalid-preview-loop.json](invalid-preview-loop.json) | 原无效主题循环已停止，1 秒只一次通知 |
| [web-apply-observations.json](web-apply-observations.json) | 服务实际拒绝时模块明确失败 |
| [diagnostic-ui.json](diagnostic-ui.json) | 详情长度保留，但摘下页面后 Visible/Timer 仍为 true，后台继续读取 |
| [fallback-ui.json](fallback-ui.json) | 首次失败后实际 GDI 背景仍是原色，未用 fallbackStyle |
| [apply-fault.txt](apply-fault.txt) | 同名主题 v1→v2 在加载后故障，来源已变、Current 已变却返回 Failed |
| [gpu-gate.txt](gpu-gate.txt) | 本轮显式运行的 R10 实际出帧测试结果 |
| [Probe.cs.txt](Probe.cs.txt) / [Probe.csproj.txt](Probe.csproj.txt) | 独立探针完整源码，.txt 避免主工程递归编译 |

临时工程在 `.tmp/review-20261027-closure/Probe/`。恢复 `.cs`/`.csproj` 原名后，从仓库根目录构建并串行运行：

```powershell
dotnet build .tmp/review-20261027-closure/Probe/Probe.csproj -c Release `
  -p:BaseOutputPath=bin/review-20261027-closure/ -p:NuGetAudit=false

$closureProbeExe = '.tmp/review-20261027-closure/Probe/bin/review-20261027-closure/Release/net8.0-windows/Probe.exe'
& $closureProbeExe closure
& $closureProbeExe apply-fault
& $closureProbeExe diagnostic-ui
& $closureProbeExe fallback-ui
& $closureProbeExe gpu
& $closureProbeExe invalid-ui
& $closureProbeExe web-result
```

`apply-fault` 使用未初始化 UIController 的缺失窗体依赖，在真实 ThemeManager.Load 后固定触发异常，不构造硬件、不改生产源码；只证明 catch 的身份/修订判定，不能代替生产 GUI 故障矩阵。所创建的主题文件在探针自己的输出目录中，探针执行后删除。

路径/IP 均为合成材料。隐私测试只导出本地临时 ZIP，没有上传；Web 探针在监听前被安全策略拒绝。GPU 使用独立临时启动守卫；R10 单元测试按其既定流程运行并恢复进程内开关。本轮没有修改用户配置或关闭其他会话进程。

原始 TRX、构建和定向日志在 `.tmp/review-20261027-closure/`。没有重跑全量或 200 次 GPU 压力测试，不将实施方既有日志重记为本轮通过。C03 的超时回收缺口来自测试代码审查，没有人为制造不可停止线程。
