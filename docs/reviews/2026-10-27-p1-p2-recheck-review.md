# C01–C07 修复再复核

日期：2026-10-27（Asia/Shanghai）。审查对象为当前未提交工作区，HEAD 仍为 `4de25d38add7f8b2342303805fdf0b654c57fa55`。

**结论：R07/C01、R08/C02、C04、C06、C07 的上轮复现路径通过。C05 的原隐私反例通过，但修复引入数字字段丢失；R10/C03 的进程隔离已加入，真实 GPU 压力和当前产物身份仍未得到门禁保证。当前不签署 P1 全部关闭。**

本轮保留 **1 项 P1、2 项 P2**，见 N01–N03。R11/R12 与 G1 未完成项沿用原状态，不扩展功能范围。

## 本轮执行结果

| 验证 | 结果 |
|---|---|
| 独立输出目录重新构建后的较宽定向 | **73 通过 / 1 失败 / 0 跳过，共 74** |
| 失败用例 | `DiagnosticProviderContractTests.Hub_RecordsEventWithSession`；revision=3 被过滤，读取该字段抛 KeyNotFoundException |
| 显式真实 GPU 出帧门禁 | **1 通过 / 0 失败 / 0 跳过** |
| 独立 GPU 探针，DPI=168 | 最小化 23→23，恢复到 28，Active=true，Error=null；三次热更后设备创建数仍为 1 |
| 原无效主题循环 | 1001ms 一次通知，没有重新进入失败刷新闭环 |
| C01/C02/C04 独立原复现 | 同源批次不延长超限；身份丢失中断原设备；规则阈值变化重新计时；同名新修订故障返回 Partial |
| C05 原隐私反例 | IPv6、冒号前缀 Windows 路径、Unix /srv 路径不再进入 ZIP |
| C06 真实摘页 | 详情 120→120 字符；Parent=null 后 Timer=false；即时摘页读取次数=4，等待 1200ms 后仍为 4 |
| C07 实际截图/像素 | 回退背景 #AABBCC；截图中 56,714 个目标背景像素，原主色 #112233 为 0；已目视检查 |

当前主工程和探针 Release 构建成功，最终探针构建 0 warnings/0 errors。全量 2751/8/0 属实施方明确标为修复前的基线，未冒充本轮通过。本轮没有重跑全量或 200 次压力任务；既有定向已失败，先修明确回归更合适。测试/探针串行占用 GPU，未发现残留 testhost/Probe。

证据：[recheck](../verification/execution/review-2026-10-27-recheck/README.md)。本轮未修改业务代码、用户设置、原关闭矩阵或计划，未提交或发布。

## 仍需处理

### N01 [P1] 子进程中的 200 次“GPU”压力测试仍可能全程没有 GPU

位置：[ThemeStudioResourceTests.cs:391](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs#L391)、[ThemeStudioResourceTests.cs:407](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs#L407)。关联 R10/C03。

子 testhost 的 RunScenario4Body 仍只加载 GPU 主题、切换目标和检查资源；没有设置/恢复 GpuThemeEffectsEnabled，没有断言 GPU 激活、设备创建或实际呈现帧。`LITEMONITOR_GPU_TESTS=1` 只是 GpuFact 的运行门槛，当前压力测试是普通 Fact，测试体没有读取这个变量。进程隔离不会使 GPU 路线自动开启。

独立探针沿用该测试的设置、主题和下拉操作，将有效用户配置 GpuThemeEffectsEnabled=false 保留在进程内，GPU_TESTS 环境变量设为 1，缩短为六次切换：`DeviceCreationCount=0, PresentedFrameCount=0, IsGpuPreviewActive=false`。这是前置条件反例，不声称重新执行了 200 次。完整测试体也没有能拒绝该情形的出帧断言。

新的独立首帧门禁确实通过，但它只有三次切换，不能证明另一个进程里的 200 次资源测量实际创建或使用 GPU。当前 C03 不能按“真实 GPU 压力验收完成”关闭。

建议：在子进程实际设置并恢复 GPU 配置；先确认首帧，再在主效果周期验证帧推进，并记录设备/呈现计数与资源测量来自同一进程。无 GPU 环境明确记为未验证，不把纯 GDI 回退资源数据作为 GPU 基线。

### N02 [P2] 子进程固定跑默认 Release 输出，可能验证旧程序集甚至零测试成功

位置：[ThemeStudioResourceTests.cs:456](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs#L456)。

父进程启动 `dotnet test <csproj> -c Release --no-build --filter C03=Child`，没有传递当前测试程序集路径、Configuration 或 BaseOutputPath。使用本仓库支持的独立输出目录时，子进程会转去读取默认 bin/Release：

```text
父：tests/LiteMonitor.Tests/bin/review-20261027-recheck/Release/net8.0-windows/LiteMonitor.Tests.dll
子：tests/LiteMonitor.Tests/bin/Release/net8.0-windows/LiteMonitor.Tests.dll
```

这两个 TargetPath 由本轮 MSBuild 查询得到。默认产物可能是旧版本或根本不存在；Debug 父测试也被硬编码转为 Release。父进程只检查退出码，没有验证子用例的实际执行数和结果。

本轮另做无匹配筛选探针：`C03=ReviewDeliberateNoMatch` 实际执行零测试，dotnet test 仍返回 exit 0。因此旧默认程序集尚无 C03 trait 时，父测试可以把零执行误判为成功。这个反例没有修改默认产物，也没有制造卡死子进程。

建议：启动当前已加载测试程序集的确切路径，保留相应依赖/运行环境；为子运行生成唯一结果文件，确认目标用例实际执行且通过，不能仅看进程 exit 0。超时关闭后也应确认进程树退出；本轮没有故意等待九分钟来制造超时。

### N03 [P2] IP 检测把普通数字识别为 IPv4，删除合法诊断事实

位置：[DiagnosticRedaction.cs:112](../../src/Diagnostics/DiagnosticRedaction.cs#L112)、[DiagnosticRedaction.cs:114](../../src/Diagnostics/DiagnosticRedaction.cs#L114)。C05 修复引入的行为回归。

`IPAddress.TryParse` 兼容 IPv4 的单整数和缩写写法。对任意字符串 token 直接调用，会把 `0`、`1`、`2`、`512`、`1200`、`42.0` 都当作地址。于是修订号、样本数、持续毫秒等允许字段被过滤，诊断事件和导出包丢掉本来需要保留的事实。

独立探针记录 `{revision:2, count:3, duration_ms:1200, status:Applied}`，实际 Context 只剩 status。已有的 `Hub_RecordsEventWithSession` 测试也因 revision=3 消失而失败，说明不是仅对人工样例的推测。此次 73/1 的较宽定向抓到了实施方 23/23 没覆盖的回归。

建议：按字段类型投影，允许字段的合法数值不要当原始自由文本做 IP 猜测；若继续检测字符串，候选至少要符合约定的 IPv4/IPv6 外形，不能把单个十进制数交给宽松地址解析。补数值保留与 IPv4/IPv6 拦截的正反测试，然后重跑全部诊断相关测试。

## 关闭建议

| 原项 | 本轮判断 |
|---|---|
| R07/C01 | 上轮两条反例通过：缓存源时间不再随 UI 延迟前移；Missing/null-id 中断已知设备 Pending。可关闭该发现 |
| R08/C02 | 同名新修订在加载后故障，现为 Partial/theme-runtime-partial；恢复使用原 RequestedName 与源摘要核对。可关闭上轮身份判定发现；完整 GUI 模块故障矩阵仍归 G1 |
| R10/C03 | 子进程结构已有，但 N01/N02 使真实 GPU 与当前版本的压力证据不可信；继续开放 |
| C04 | 规则签名变化会 ResetRule；原阈值变化继承计时反例不再触发。可关闭 |
| C05 | 原隐私反例通过；新增 N03 数值丢失使本批不能整体验收通过 |
| C06 | 真实 Controls.Clear 摘页停止计时器，等待后无继续读取。可关闭 |
| C07 | 首次失败立即使用 fallback 派生颜色；实际位图/像素与目视检查通过。可关闭 |

R01/R02 在本轮 GPU/无效主题探针中继续通过；没有因为新问题把已经证实的修复重新打开。R11/R12、G1 的 DPI/会话/供电、模块故障注入和代理/DNS、场景/低功耗生产接线仍维持此前边界。

## 下一步

先修 N01–N03，再按当前产物重跑诊断定向和真正使用 GPU 的子进程压力任务。两条都通过后再执行全量回归并更新关闭矩阵；当前不建议用修复前的全量结果签字。
