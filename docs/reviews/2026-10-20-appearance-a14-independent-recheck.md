# A14 独立复核与 M2 推进建议

复核日期：2026-10-20。固定提交：`9c16ad3`；实现提交：`2491ff0`。

## 结论

**原 8 项 P2 的主要修复成立，自动化复验通过；“A14 全部收口”仍需附带条件。** 本轮新增确认 2 项 P2：性能报告将经过时间标为 CPU 时间、固定预览图的来源状态误报为实时帧。另有缓存创建异常时的资源清理加固项，尚未实测触发。

建议允许 A15 的基础开发与 A14 实机验证并行。两项新 P2 应在 A16 正式接线前处理；实机清单和 M2 实际 CPU/延迟预算需在发布前验收。

沿用已经明确的产品决定：**M1 已知超预算归档，画面保持不变，不实现自动降级。** 本报告没有变更该决定。这个例外不自动扩展为 M2 捕获服务的预算豁免。

证据：[汇总](evidence/a14-9c16ad3/suite-summary.json)、[原反例复验](evidence/a14-9c16ad3/independent-repros.json)、[窗口完整接线复验](evidence/a14-9c16ad3/resize_shape_integrated.json)、[预览来源反例](evidence/a14-9c16ad3/preview_source_status.json)、[重跑说明](evidence/a14-9c16ad3/README.md)。

## 一、本轮发现

### N1 · P2：配对经过时间不能作为 CPU 预算及整机 CPU 增量的实测值

位置：[AppearanceProbe.cs:445](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a14-audit-9c16ad3/target-src/tests/LiteMonitor.PerformanceProbe/AppearanceProbe.cs:445)、同文件第 501 行；结论使用处为 [A14 acceptance:282](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a14-audit-9c16ad3/target-src/docs/superpowers/plans/2026-10-20-appearance-a14-acceptance.md:282)、第 451 行。

`MeasurePaired` 的前后差值完全来自 `Stopwatch.GetTimestamp`，输出为 `extraMsPerDraw`；没有配对测量新、旧入口的处理器时间。微软对 [Stopwatch](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.stopwatch?view=net-8.0) 的定义是经过时间计量，等待、抢占等同样计入，不能因为绘制通常消耗 CPU 就直接替代 CPU 时间。

普通夹具的 `cpuMsPerDraw` 确实读取 `Process.TotalProcessorTime`，但该测量调用的是旧入口；它不能补出配对的新入口 CPU 差额。因此报告中的“额外 CPU −0.20～+0.52ms”“额外 CPU +10.45ms/帧”“整机增加 0.85 个百分点”目前没有对应的配对 CPU 测量支持。

**独立抽测**：同一固定程序集、三轮配对、每轮 40 帧：

| 场景 | 尺寸 | 额外经过时间 | 额外托管分配 |
|---|---|---:|---:|
| DarkFlat_Classic，1 卡，96 DPI，1× | 240×820 | −0.0999 ms/帧 | 0 B/帧 |
| CyberPunk_Mech，4 卡，96 DPI，2× | 480×2660 | +10.7887 ms/帧 | 17,712 B/帧 |

该结果支持此前绘制耗时的量级，**不补齐 CPU 预算的证据**。关闭态零增量分配可以独立认定通过。

**建议**：保留逐帧交替经过时间以观察延迟；增加足够长批次的进程或线程 CPU 时间差，交替批次顺序、至少三轮，记录误差和每轮结果。CPU 预算按实际处理器时间判断；p95 延迟继续按经过时间判断。CPU 数据补齐前，将现有表头与整机 CPU 推算明确标注为经过时间/假设估算。

### N2 · P2：编辑器固定样本图被报告为 LiveFrame

位置：[ThemePreviewControl.cs:85](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a14-audit-9c16ad3/target-src/src/ThemeEditor/ThemePreviewControl.cs:85)、[BackdropRenderer.cs:115](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a14-audit-9c16ad3/target-src/src/System/Backdrop/BackdropRenderer.cs:115)。

编辑器将 `PreviewFrameSource` 作为普通 `IBackdropFrameSource` 注入运行时。运行时没有设置 `options.PreviewSource`；背景层于是把可用结果标成 `isPreview:false`，最终来源为 `LiveFrame`。

**独立复现**：用固定 Bitmap 构造 `PreviewFrameSource`，通过正式 `ThemePreviewControl.RenderPreview(..., runtime)` 绘制 glass；`runtime.Backdrop.LastStatus.Source` 为 **LiveFrame**。同一张图直接传入 `ThemeAppearanceOptions.PreviewSource` 时为 **PreviewFrame**。两次均没有注册真实捕获后端。

画面仍是固定预览，并未偷跑抓屏；错误在来源诊断。`BackdropStatus` 已明确承诺区分真实帧与预览源，这个状态需要在 M2 前纠正，以免固定样本被计入实时玻璃验收。

**建议**：通过运行时正确传递预览源，或为帧提供者明确增加来源元数据；增加针对编辑器实际 runtime 的 `Source == PreviewFrame` 检查，保留“预览捕获次数为 0”的断言。

### 补充加固项：纹理创建失败后的接管资源清理

位置：[DecorResourceCache.cs:243](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a14-audit-9c16ad3/target-src/src/UI/Decor/DecorResourceCache.cs:243)。

正常创建、淘汰和 Dispose 的源图/画刷释放路径已修复，记账也不再取得 `TextureBrush.Image`。不过，源位图从工厂转移所有权后，到 `new TextureBrush(source)` 及入表完成之前没有异常清理；构造或入表抛异常时，源图/画刷可能脱离缓存管理。旧项还在新画刷创建成功前就被淘汰。

建议在入表成功前用 `try/finally` 管理待接管资源，成功后提交所有权和计数，并加入可注入的构造失败检查。本次尝试以锁定位图触发构造失败，实际构造成功，最终计数 1/1 配平；**没有实测复现该异常路径，不将其算作已发生的泄漏或崩溃**。

## 二、独立复验结果

全部在 `git archive 9c16ad3` 导出的隔离目录运行，未修改产品代码。

| 项目 | 本轮实测 |
|---|---|
| Release 解决方案构建 | 0 错误，254 警告 |
| 单元测试 | 577/577，0 跳过 |
| 接线探针 | 73/73；含 60 组旧主题新旧入口逐像素相同 |
| 主题检查 | 335/335 |
| 静态背景探针 | 21/21 |
| 装饰几何/资源探针 | 9/9 |
| 装饰渲染探针 | 11/11 |
| 窗口轮廓探针 | **15/15**，包含 A14 新增尺寸/换肤/布局/订阅检查 |
| 配对耗时抽测 | 2/2 夹具完成，三轮×40 帧；不作为 CPU 预算通过证据 |

单元测试沿用已查明的环境处理：允许 VSTest/testhost 本机通信，在隔离目录保存 TRX。所有探针均不打开硬件监控或捕获桌面；窗口探针使用不显示的 Form。

### 原 8 项处置的复核

| 原编号 | 本轮结果 |
|---|---|
| R1 噪点缓存 | 第五种强度可用；完整渲染路径不再抛异常；失效合同已有接线与探针 |
| R2 预览正式渲染 | Glow 变化 4,256 像素、BarStyle 变化 9,390 像素，预览与主入口变化量相同；N2 的来源误报另列 |
| R3 进度条裁剪 | 两种样式改写外层裁剪区外的像素均从 602 降为 **0** |
| R4 切角同步 | 按生产 `SnapshotProvider` 接线后，240×80→240×200 的切角从 40 更新为 **64**，与新快照相同 |
| R5 局部底色/边缘覆盖 | 横竖新入口均将哨兵色补为正确的 `FF123456`；73 项接线包含边缘及局部覆盖检查 |
| R6 关闭态分配 | 同一旧反例三轮×500 帧，每帧旧/新入口均为 3,112 B，增量 **0 B** |
| R7 纹理斜线 | 16×16 瓦片非透明像素从 7 增为 160，内部从 0 增为 **86** |
| R8 位图所有权 | 正常生命周期源码与资源探针通过；构造失败清理作为补充加固项保留 |

**R4 的口径说明**：原独立程序只调用裸 `Reapply`，没有提供当前快照。A14 的生产修复是新增并接入 `SnapshotProvider`；缺少提供者时仍按设计回退旧快照，因此旧程序该子项仍显示 40。不能据此判定生产修复失败，也不能删掉反例掩盖结果。另行按主窗接线运行的 `resize_shape_integrated.json` 为 64，源码确认主窗确实安装该提供者。

**底板根因说明**：局部漏底与边缘漏色都属于覆盖范围问题，但具体成因不同：前者是使用偏移裁剪的尺寸却从窗口原点填充；后者是精确边界在抗锯齿下没有完全覆盖边缘像素。A14 同时采用完整客户区锚定和向外扩展绘制，两类修复都有针对性。

## 三、A14 尚待完成的验收

8 项实机清单仍需真实窗口操作：跨屏 DPI 往返、句柄重建后的 DWM 外观、100 次换肤后的实际内存平台、亮背景文字与边缘观感等。本轮离屏探针不能替代这些项目，不能将状态写成“全部实机通过”。已知的预览字体跨 DPI 限制也没有因 R2 修复而自动消失。

建议归档状态写成：**M1 功能修复及自动化通过；2 项复核问题待处理；实机验证待验；静态超预算按既定决定接受。**

## 四、M2 可以怎样推进

### 1. 现在可以启动 A15，A16 正式接线保留检查点

- A15 的后端、滤波和假后端状态机不依赖人工观感结果，可以与 A14 实机验证并行。
- A16 正式接入前完成 N1/N2、冻结帧所有权/来源/代次协议；主窗口事件仍由同一集成者负责。
- 实机 M1 固定使用 `9c16ad3` 或后续明确修订的基线；M2 独立分支推进，避免移动中的开发版本混入人工验收。
- A17 才是合并后故障、性能、录屏和支持矩阵的验收阶段，不以组件假后端通过替代真实验证。

### 2. 适合并行的工作包

先由集成者冻结捕获请求、帧来源、代次、所有权和测量字段，再派发以下独占写集：

| 工作包 | 交付 | 依赖/边界 |
|---|---|---|
| A15 服务与帧交接 | 单后台任务、pending/active 所有权、取消/释放、代次淘汰、可诊断状态 | 不修改 MainForm/Renderer；后台只持有不可变请求 |
| A15 捕获后端 | A06 放行路线、瞬时排除作用域、异常/取消恢复、真实 bounds/负坐标支持 | 与服务共享已冻结协议；不自行绑定另一套窗口事件 |
| A15 图像处理 | 降采样、物理模糊半径、像素/缓冲预算、有界临时图 | 可单独用固定图验证；不创建捕获循环 |
| A15 故障与性能探针 | 假后端、过期帧、快速换肤/尺寸变化、捕获失败/取消、CPU 与延迟分开计量 | 独立探针和证据目录；不替换生产实现 |
| A16 LiquidGlass 主题 | 默认关闭前提下的完整主题配置及固定预览样本 | 与实现并行；不把样本当真实桌面 |
| A16 集成 | 单一服务创建点、显示/隐藏/模式/DPI/句柄/退出接线、开关和首次提示 | 等服务/后端/处理包通过后，由一个 owner 串行修改共享入口 |

项目文件、共享接口与最终集成由同一个 owner 汇总。A17 的最终验证在合并后串行执行，避免并行构建、采集和压力任务干扰性能结果。

### 3. A15 必须冻结的合同

1. 后台只发布 pending；UI 接管 active，在没有 Paint 使用旧帧时替换并释放。后台不得释放 UI 正在绘制的 Bitmap。
2. 请求带客户区物理 bounds、DPI/用户缩放、主题与窗口句柄代次。变更后拒绝旧结果；pending 最多一份，避免堆积。
3. 隐藏、最小化、非 glass、关闭时无持续捕获；恢复合并为一帧。`Paint` 和 `TryGetFrame` 只读当前帧，不启动抓屏。
4. 取消、捕获/滤波失败、句柄失效都恢复原显示亲和性，释放待发布图和原生资源；不阻塞 UI 无限等后台退出。
5. 保留 glass→gradient→solid 的实际状态和原因；失败后有受控重试，不在每次 Paint 无限重试。
6. 默认关闭。**显式开启与首次提示在 A16 首次接入真实捕获时同时提供**，A17 验证其行为，不能等 A17 才补提示。编辑器固定预览不启动服务。

### 4. M2 预算与真实验证

- 后台 CPU ≤40ms/秒，以处理器时间计算；单次处理 p95 ≤30ms，以经过时间计算。并列记录等待 UI/排除窗口时长、捕获、滤波、发布和 UI 绘制成本。
- 默认 2fps、上限 6fps；采集像素和包括临时/pending/active 在内的缓冲预算按 v4 执行。A06 的 `captureScale=0.25` 可作为候选起点，需按实际最大面板尺寸重新测量。
- M1 的画面优先例外保留；M2 新服务仍须验证预算、实际生效状态及暂停/失败成本。
- A06 的证据只支持已测系统/后端组合。`WDA_EXCLUDEFROMCAPTURE` 从 Windows 10 2004 起支持，旧系统行为不同，并依赖 DWM 合成；官方 API 说明本身不证明每个捕获后端都能获得窗口后的背景。[Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
- 瞬时排除提示应表述实际方案：捕获瞬间可能让面板在录屏/截图中短暂缺失或闪动；不能据先前观察占空比保证所有录屏软件都无感。Win10、RDP、锁屏恢复、HDR 和真实 OBS/游戏栏录屏按支持矩阵保留“通过/失败/未验证”。

## 五、证据来源

- 固定导出压缩包 SHA256：`AC406C61C4DC27CAE5705D5C2CFB771057AC1ABA8CFAB542319EF74E511F7C37`。
- 本轮主 DLL SHA256：`F8C64F4E3A243302067793BBBD5A00FE1DA602E5A5EE136D696D31264B4D900C`。
- 完整产物：`.review-scratch/appearance-a14-audit-9c16ad3/`。固定源码没有 `.git`；来源以导出提交和哈希为准，不采用向上搜索到的仓库 HEAD 冒充源码状态。
- 本轮没有重跑原报告全部 42 个性能夹具；读取了原始配对结果并对两个代表夹具重新测量。一次使用不支持参数而筛选为零项的探索运行已明确排除，不列入通过证据。
