# 主题工作室 N00～N10 独立验收

- 日期：2026-10-26。
- 结论：**不通过，暂不进入后续优化实施计划的细化。**
- 候选：HEAD 42b6b61ffa347261a69077418c25aac663061fe4 加当前未提交的工作树实现。该 HEAD 本身不包含本批修改；实际文件身份见[源码摘要](../verification/theme-studio/independent-review/source-manifest.json)。
- 范围：用户粘贴的 N00～N10 完成声明、相关生产接线、新增测试、N08 矩阵与 N09 资源报告。未修改产品源码。
- 依据：[下一阶段计划](../plans/2026-10-26-theme-studio-next-stage-plan.md)、[实施记录](../design/theme-studio/implementation-log.md)。
- 直接证据：[独立探针观测](../verification/theme-studio/independent-review/observations.json)。探针源码与本机运行产物留在工作区 .tmp/acceptance-theme-studio-20261026/。
- 测试与产物记录：[测试摘要](../verification/theme-studio/independent-review/test-summary.json)。所审 15 个关键源码／测试文件在复验期间摘要保持不变。

## 1. 结论与范围边界

本批确实增加了任务栏样式上下文、GPU 参数更新、回退解析提取、导出菜单等实现；但存在可复现的画布与编辑目标不同步、持久化失败未识别、导出内容错误，以及仍未接入的用户交互。

“专项全绿”不足以证明 N00～N10 全部完成。部分新增测试只检查状态文案、直接调用辅助方法或预先编排的模拟结果，没有覆盖本次失效的真实路径。N08 的必需实机项仍有缺失，N09 也未提供要求的 GPU 路径资源验收。

以下分开列出代码问题与验收证据缺口。验证不足不直接等于存在泄漏，未验证也不等于已经通过。

## 2. 问题总表

| 编号 | 优先级 | 问题 | 证据 |
|---|---|---|---|
| R1 | P1 | 切换编辑目标只更新检查器与状态文字，画布仍显示旧目标 | 实际下拉事件及内部预览状态复现 |
| R2 | P1 | Settings 保存失败不会抛出，网关仍进入运行时应用阶段 | 禁止保存的隔离探针＋真实 Save 语义 |
| R3 | P1 | 实际生效路线由全局 GPU 开关推测，未读取实际主题与过渡结果 | 网关源码，经典主题／GPU 降级均可被误报 |
| R4 | P1 | 兼容导出使用原主题配色，未应用 fallbackStyle | 导出标 Fallback，像素仍为主配置颜色 |
| R5 | P2 | 横向 GPU 导出忽略方向，任务栏导出也未保持所选形态 | 本机 GPU 实测返回 240×420，而请求为 400×120 |
| R6 | P1 | GPU 拒绝非法新主题后，宿主继续显示旧帧并报 GPU 活跃 | 父控件真实更新流程与独立 GPU 会话复现 |
| R7 | P2 | 点选定位未接入鼠标命中流程，部分角色还指向不存在的字段 | 全局调用检索＋GPU 背景角色实际定位失败 |
| R8 | P2 | 工作区未接明暗策略，暗色设置下仍固定浅色 | 暗色色板与窗体实际颜色对照 |
| R9 | P2 | 隐藏 GPU 预览仍持续渲染 | 隐藏约 250 ms 后仍增加 6 个 presented frames |
| R10 | P2 | 热更新 targetFps 未更新调度器 | 请求 2 FPS 后调度器仍为初始 30 FPS |

## 3. R1：目标已切换，画布没有切换

落点：[ThemeStudioForm.OnTargetChanged](../../src/ThemeEditor/ThemeStudioForm.cs:1147)。

真实下拉事件只执行检查器 Bind 和 UpdateRouteStatus，没有 RefreshPreviewNow 或 SchedulePreviewRefresh。测试钩子 SelectTargetForTesting 同样未更新画布。

探针通过实际 ToolStripComboBox 的 SelectedIndex 从主效果切至兼容外观，观察到：

- 检查器目标：GpuFallback。
- 状态区：兼容外观、常规绘制、生产同源。
- 画布内部目标：仍是 GpuMain。
- 画布背景：仍为 #112233，期望的 fallbackStyle.background 为 #AABBCC。

这会让用户在编辑兼容配色时观察主效果，直到另一次属性修改或形态切换碰巧触发刷新。直接破坏本次重做要解决的“双管线不明确”问题。

修复建议：使用同一操作切换目标、更新预览请求、取得实际路线并刷新状态；新增测试触发真实下拉事件，验证实际主题／画面变化，不能只断言文案。

## 4. R2：持久化失败未被识别

落点：[ThemeStudioLauncher.ApplySavedTheme](../../src/ThemeEditor/ThemeStudioLauncher.cs:37)、[SettingsHelper.Save](../../src/Core/SettingsHelper.cs:464)。

网关在 try/catch 中调用 _cfg.Save；真实扩展方法 Save 仅调用 TrySave 并丢弃 bool，TrySave 对禁止保存、序列化与 I/O 失败返回 false。网关的 catch 捕捉不到这些失败。

隔离探针令 Settings.GlobalBlockSave=true：磁盘 Skin 仍为 DarkFlat_Classic，内存 Skin 已变成 AuditGpu，网关仍越过 Settings 阶段进入 ThemeApplied。探针故意不提供运行时宿主，随后产生的空宿主错误只用于证明阶段已经错误推进，不将它列为产品空引用缺陷。

修复建议：消费真实持久化结果；失败立即返回 SettingsPersisted 失败，不启动运行时应用。明确内存 Skin 的回滚／未持久化状态。用实际设置写入故障覆盖网关，不能只让 MockThemeApplyGateway 返回预设失败。

## 5. R3：生效结果仍是意图推测

落点：[ThemeStudioLauncher 的 route 生成](../../src/ThemeEditor/ThemeStudioLauncher.cs:60)。

当前只依据 GpuThemeEffectsEnabled 与 SafeMode 返回“GPU 场景生效”或“常规绘制生效”，并把请求的 themeName 当 EffectiveTheme。

静态即可确认：

- GPU 开关开启时，经典主题同样会被标成 GPU 生效。
- GPU 初始化失败后正常降级而未抛异常，仍可被标成 GPU 生效。
- 过渡尚未达到实际生效条件时，调用返回不等于有效路线已经落定。
- 异常分支使用已经被改写的 _cfg.Skin，不能说明失败后真正仍生效的主题。

修复建议：从真实主题应用／过渡状态取得有效主题、有效路线、诊断与修订；必要时返回处理中。保存、持久化、运行时成功分开判断。补经典主题、GPU 降级和旧主题继续生效的真实网关用例。

## 6. R4：兼容导出未应用回退配色

落点：[ThemeExportService 的 Fallback 分支](../../src/ThemeEditor/ThemeExportService.cs:330)、[RenderClassicImage](../../src/ThemeEditor/ThemeExportService.cs:589)。

兼容分支把原始 session.Draft 传入 RenderClassicImage，后者直接 CreatePreviewTheme，没有调用生产的回退派生逻辑。

独立导出结果：Success=true、Route=Fallback，但背景采样为 FF112233，等于原始 color.background；fallbackStyle.background 配置为 #AABBCC。

原图见[兼容导出实测](../verification/theme-studio/independent-review/fallback-export.png)。

修复建议：画布、显式兼容导出、GPU 不可用时的兼容导出，以及适用的市场回退缩略图共用实际派生主题规则；保持字体所有权及告警色语义。断言派生内容／图像，不只断言 IsFallback 标记。

## 7. R5：导出形态与选择不一致

落点：[ExportEffectImage](../../src/ThemeEditor/ThemeExportService.cs:289)、[GPU 导出固定 Bounds](../../src/ThemeEditor/ThemeExportService.cs:691)、[工作室导出形态映射](../../src/ThemeEditor/ThemeStudioForm.cs:808)。

GPU 分支未将 orientation 传入 TryRenderGpuImage，后者固定建立竖向输入和 240×420 宿主。真实 GPU 请求 Horizontal 返回成功，图片仍为 240×420；期望为接口声明的 400×120。

即便常规绘制分支把位图宽高改成横向，仍调用竖向 UILayout／RenderPreview，未消费已交付的横向布局。任务栏在 UI 中也被映射成 Vertical，不告知用户形态变化。

原图见[横向请求的 GPU 输出](../verification/theme-studio/independent-review/gpu-horizontal-export.png)。

修复建议：导出消费与画布相同的目标／形态／输入请求。分别覆盖经典横向、GPU 横向和任务栏；不支持的组合明确禁用或报错，不能悄悄换成竖向。

## 8. R6：非法更新后旧 GPU 画面被当成新主题

落点：[GpuThemePreviewControl.UpdateTheme](../../src/ThemeEditor/GpuThemePreviewControl.cs:130)、[ThemePreviewControl 忽略更新结果](../../src/ThemeEditor/ThemePreviewControl.cs:237)。

新主题校验失败时 UpdateTheme 直接返回 false，不改变旧 renderer 或设置 Error；父控件忽略 bool，继续把旧子控件设为可见并置顶。

独立探针先启动合法 AuditGpu，再经父控件选择 glow=101 的 InvalidAudit：

- 父控件 selectedTheme=InvalidAudit。
- GPU 子控件 renderedTheme=AuditGpu。
- IsGpuPreviewActive=true，GpuPreviewError=null。

因此用户可在加载另一个不合法的 GPU 文档后继续看到旧主题成功帧，状态仍显示 GPU 活跃。此问题不是“渲染器拒绝非法参数”本身，而是宿主把拒绝结果当作更新成功。

修复建议：验证失败需返回可见诊断及未提交状态，宿主不得把旧帧声明为新修订的有效预览。补合法→非法→修复后的实际会话用例，并检查主题／配置代次。

## 9. R7：点选定位仍只有辅助方法

落点：[SelectPropertyByRole](../../src/ThemeEditor/ThemeStudioForm.cs:1100)、[ResolvePropertyPath](../../src/ThemeEditor/ThemePropertyCatalog.cs:327)。

当前 SelectPropertyByRole 只有测试调用，未发现画布 MouseDown／Click、布局命中、坐标逆变换或场景对象到角色的接线。N05 测试直接传入角色，验证的是辅助函数，不是用户点选。

GPU 主效果背景还被映射至 rendering.parameters.panel_color，当前检查器无该字段；实测定位返回 false。现有映射也没有按材质／样式判断固定前景、共享字段或 Settings 驱动项。

FocusProperty 当前将焦点落到行内 Label，并不保证对应编辑控件可直接输入；本次观察实际 ActiveControl 为 Label。

修复建议：接真实布局／场景命中和坐标变换，按属性能力表定位有效编辑器，并提供键盘等价路径。测试应从画布输入触发，验证对象、属性与焦点，不直接调用角色辅助函数代替整个功能。

## 10. R8：暗色工作区尚未接线

落点：[ThemeStudioForm 固定配色](../../src/ThemeEditor/ThemeStudioForm.cs:28)、[构造与配色](../../src/ThemeEditor/ThemeStudioForm.cs:107)、PropertyInspector。

探针设置并初始化现有明暗引擎为 Dark，CurrentPalette.IsDark=true；工作室背景仍为 FFFAFAF8，检查器仍为 FFFFFFFF。当前实现没有消费该色板或订阅主题变化。

修复建议：按现有设置策略给工作区控件换色，排除预览与色样；覆盖打开时为暗色、已打开窗口切换、系统跟随和高对比度。N08 报告中“跟随生产策略”的表述不能视为实际接线证据。

## 11. R9：隐藏仍在运行 GPU 渲染

落点：[GpuThemePreviewControl.OnTick](../../src/ThemeEditor/GpuThemePreviewControl.cs:403)。

定时器仅在句柄销毁、Fail、Dispose 时停止；OnTick 未检查 Visible、父窗最小化或当前预览目标。父控件切兼容／经典主要通过 Visible=false 保留 GPU 子控件，设备得以复用，但调度并没有暂停。

本机合法 GPU 会话隐藏约 250 ms，presented frames 从 6 增至 12，即隐藏期间仍绘制 6 帧。

修复建议：保留可复用资源与暂停帧调度分开处理；当前目标不使用、隐藏和最小化时停帧，恢复后重新调度。用实际帧数或 GPU 调用计数验收。

## 12. R10：targetFps 热更新未更新帧调度器

落点：[GpuThemePreviewControl.UpdateTheme](../../src/ThemeEditor/GpuThemePreviewControl.cs:130)与 _scheduler。

更新时重新解析 _runtimeSettings 并调用 renderer.UpdateConfiguration，但 _scheduler 仍保持构造时 FPS。现有 FrameScheduler 已提供 UpdateTargetFramesPerSecond，预览没有调用。

本机探针将 targetFps 改为 2，更新后的调度器仍为 30。

修复建议：应用运动／FPS 参数时同步更新帧调度状态；测试从同一活动会话修改 FPS，检查实际调度或帧数，不能只读配置字段。

## 13. 验收证据缺口

### G1：N08 的实机覆盖未满足最终放行条件

[N08 矩阵](../verification/theme-studio/N08-matrix.md)将 125%～200% 多项标为 PASS，但证据说明主要是布局换算／代码逻辑；混合 DPI、4K、250%／300%、物理拔屏仍标 NOT-VERIFIED。表内也未列真正的暗色组合。

源码使用 AutoScaleMode.None，几何在 BuildUi 时计算，未见 DPI 改变后的整体重布局接线；分栏仍固定宽度，检查器也保留固定像素布局。不能据此证明跨屏、极端缩放、系统文本放大和紧凑布局已通过。

需要恢复区分“代码逻辑检查”“实际事件／实机验证”和“尚未验证”；在补实现与必测证据前，不满足原计划的 N08／N10 最终条件。

### G2：N09 没有证明 GPU 稳态与真实预览目标切换

[ThemeStudioResourceTests 场景 4](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs:352)打开 ClassicThemeName，然后调用测试钩子绑定 GpuMain／GpuFallback 检查器。该主题没有 GPU rendering，钩子又未刷新画布，实际不是 GPU 主效果与回退渲染的往返。

资源断言只检查 GDI≤200、USER≤200、句柄≤400 的净增上限，不验证序列斜率。一次 200 次切换的 +200 句柄在这种断言下通过，不能推出“无逐周期泄漏”；本审查同样不据该净增就直接判定存在泄漏。

需要补真实 GPU 主题、确认会话实际活跃／帧数、真实目标事件与画布更新、GPU ledger、隐藏和关闭后的资源所有权，以及多段稳态测量。

### G3：冷启动细粒度归因与全量结果需要可追溯依据

[N09 报告](../verification/theme-studio/N09-perf-report.md)中约 80／60／70／110 个句柄的归因未附对应句柄类型快照、跟踪输出或可复算差分；总计数稳定不能证明每一类所有者。

“2686+”不是本次完整测试结果，必须给出实际执行、通过、失败、跳过数以及绑定的产物摘要。主题专项并不能替代 G12 要求的串行全量。

## 14. 本次独立验证

### 已完成

- 主工程、依赖与测试项目成功构建到审查专用输出目录。
- 非资源 ThemeStudio 专项：31 执行、31 通过、0 失败、0 跳过。
- 隔离探针使用沙箱配置／主题目录，不更改用户生产配置；真实 GPU 初始化成功。
- 动态确认：R1、R2 的阶段错误、R4、R5 的 GPU 横向尺寸、R6、R8、R9、R10。
- R3 为真实网关静态逻辑确认；R7 为调用链核对与角色定位观测，未把缺少命中流程误报成完整 UI 测试已通过。

### 全量运行的实际边界

1. 首次使用审查隔离输出布局，I2HostBinaries 清单定位器不支持该布局，出现 11 个 Host 定位失败；随后 testhost 中止。TRX 已记录 1322 执行／1311 通过／11 失败，但该结果不是完成的全量，也不把这些布局失败归因于本批产品。进程还输出 FontMetrics 的 Parameter is not valid，未完成归因，不能当作已经确认的新回归。
2. 第二次改用工程支持的 BaseOutputPath 和同轮 Host 路径。运行中发现另一个会话的默认输出 testhost 与审查 testhost 同时存在；为遵守串行硬件测试约束，停止了本审查会话，未终止其他会话进程。此次未产生完成的全量 TRX。
3. 因此本轮没有宣称全量通过，也没有把上述运行环境问题加入 R1～R10。资源四场景的历史通过数仍归实施方报告，本次未重新签发 G6。

本轮可采纳的是已完成的 31 项非资源专项、独立沙箱与真实 GPU 探针、源码调用链核对；它们已经足以复现阻断问题，结论不依赖未完成的全量运行。

## 15. 建议的修复与复验顺序

1. 修 R1、R2、R3：先让目标、保存、实际生效三个关键状态可信。
2. 修 R4～R6：导出消费正确派生主题／视图，GPU 更新失败不保留误导性成功状态。
3. 补 R7／R8 及 N08 实机接线，修 R9／R10 的调度生命周期。
4. 把本次失效场景加入正式回归，重新执行真实菜单／下拉／画布路径，避免继续用辅助函数和 Mock 返回值替代。
5. 重新记录 N08／N09／G12；必需未验证项明确处理后，再重新判断 N10 及后续优化细化的前置条件。

用户要求“验收没问题再细化优化计划”。当前条件不成立，因此本轮保留既有[优化意向计划](../plans/2026-10-26-theme-studio-optimization-intent-plan.md)，不把它升级成已放行的实施计划。
