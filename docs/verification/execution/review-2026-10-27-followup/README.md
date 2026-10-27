# 修复包与四项功能独立审查证据

日期：2026-10-27（Asia/Shanghai）。基线 HEAD：`4de25d38add7f8b2342303805fdf0b654c57fa55`，审查对象包含未提交实现。

报告：[execution-implementation-review](../../../reviews/2026-10-27-execution-implementation-review.md)。结论：暂不通过，10 P1 / 10 P2；已声明的功能未完成边界另列，不全部算作新缺陷。

| 文件 | 内容 |
|---|---|
| [source-manifest.json](source-manifest.json) | 本轮结束时改动/新增的生产、测试、资源、CI 文件 SHA-256 |
| [validation-summary.json](validation-summary.json) | 两组 TRX 汇总：123+97=220，通过 220、失败 0、跳过 0；本轮没有全量复验 |
| [observations.json](observations.json) | 独立纯行为探针：BOM、短时/长期趋势、告警、场景库/安全、诊断脱敏/合并和节能上限 |
| [gpu-observations.json](gpu-observations.json) | 真实 168 DPI、三次热更新出帧/设备复用、最小化停帧和恢复代次失败 |
| [invalid-preview-loop.json](invalid-preview-loop.json) | 真实工作室无效主题，68ms 内五次无输入刷新；第五次由探针关闭中止 |
| [web-apply-observations.json](web-apply-observations.json) | 模块门面返回 Applied=true，而实际服务因缺口令拒绝启动；未对外监听 |
| [Probe.cs.txt](Probe.cs.txt) / [Probe.csproj.txt](Probe.csproj.txt) | 可复现探针源码和工程原文；.txt 防止主工程误编译 |

## 复现

探针工程放在仓库 `.tmp/review-20261027-followup/Probe/`，从两个 `.txt` 恢复对应 `.cs` / `.csproj`。先确认没有其他会话的硬件测试或 GPU 压力测试，再串行运行：

```powershell
dotnet build .tmp/review-20261027-followup/Probe/Probe.csproj -c Release `
  -p:BaseOutputPath=bin/review-20261027-followup/ -p:NuGetAudit=false

$reviewProbeExe = '.tmp/review-20261027-followup/Probe/bin/review-20261027-followup/Release/net8.0-windows/Probe.exe'
& $reviewProbeExe
& $reviewProbeExe invalid-ui
& $reviewProbeExe gpu
& $reviewProbeExe web-result
```

带 BaseOutputPath 的产物直接启动上面已知路径，避免 `dotnet run` 按默认输出目录查找可执行文件。`gpu` 模式读取仓库首个 `*_GPU.json`，使用独立临时启动守卫，不修改生产 GPU 标记；`invalid-ui` 只在本进程内启用预览，不保存真实用户设置。

定向测试使用同一 BaseOutputPath。完整筛选参数保留在报告对应的临时日志及 TRX 测试条目中：

- `.tmp/review-20261027-followup/results/followup-targeted.trx`
- `.tmp/review-20261027-followup/results/followup-theme-web.trx`
- `.tmp/review-20261027-followup-tests.log`
- `.tmp/review-20261027-followup/theme-web-tests.log`

## 证据边界

- 合成路径 `G:\Users\Alice\private-theme.json` 和文档示例 IP `192.0.2.1` 仅用于隐私边界测试，没有读取该文件，也没有网络访问。
- R08/F02 与 R10/F03 的剩余缺陷依据当前完整调用链/测试体审查；不伪造模块故障注入或 200 次实际 GPU 资源验收。
- 单次 175% DPI 的有限实验不代替 100%–200%、RDP、休眠、AC/DC、50 次恢复的实机矩阵。
- 纯观察探针是复现材料，不冒充正式回归测试；验收修复时应转成能断言正确行为的测试。
- 业务源码、用户配置和现有实施记录未改；没有提交或发布。
