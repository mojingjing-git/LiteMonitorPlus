# GPU 非性能交付独立质量审查

日期：2026-10-22。结论：**NEEDS-REVISION，当前候选尚不能认定非性能完成。**

## 范围和方法

- 审查工作区中的 GPU 渲染、宿主接线和非性能 runner 修订，以及现存验收证据。HEAD 为 `694a8c5`；没有新提交，不能把整个工作区的历史改动都归因于本次接手者。
- 主审与两位独立 reviewer 分别审阅生产生命周期、缓存和 runner。主审核对了下列发现的当前源码。
- 本轮未修改产品实现，未运行构建、测试、GUI 或性能任务。以下代码缺陷是静态确认的执行路径，不声称已经实机重现。
- 硬件、电压、内存、网络等并行改动不属于本轮外观交付审查范围。

## 实质改进

1. `MainForm.OnShown` 只在宿主缺席时调用 `EnsureGpuPresentationStarted`，合理覆盖构造期重建句柄、事件尚未订阅导致的漏启动，保留已有会话。
2. runner 的规范化 manifest 哈希、glob 零层匹配、可选属性读取、必需入口 AND 判断和必要 N/A 阻断确实改进了。功能组与性能组已有明确分流。
3. live 专用背景窗补了 `WS_EX_NOACTIVATE`；现存窗口审计有不置顶、不持有前台的结果。旧 backdrop 入口不再默认由 runner 自动启动。
4. 保留失败尝试及其后修复记录，优于只呈现最后一份 PASS。现存 live 诊断确实记录 `backgroundInput.Mode=2`、`capture_frame_available` 和最终账本归零。
5. 本轮读取到的发布 ZIP SHA256 为 `061BB666CAFF7FA8B3C9F24F7E581603C4124EE2600100DF15D8A9C0FE190A82`，与发布收据一致。这只证明当前 ZIP 的字节身份，不代替完整功能验收。

## 产品缺陷

### F01 — P1：前景缓存签名截断，后段指标可停留在旧数值

位置：`src/Rendering/Gpu/Foreground/Direct2DForegroundPainter.cs:280`、`:293`、`:182`；调用方 `src/Rendering/Presentation/GpuSceneRenderer.cs:505`。

`BuildSignature` 固定分配 `Commands.Length * 32 + 16` 个字符，但单条命令包含种类、完整 ID、文本、颜色状态、四个坐标和进度，通常显著长于 32 字符。空间不足直接 `return`，当前及后续命令全部被忽略。

命令数及已写前缀不变、仅被忽略的后段数值/状态/进度变化时，新旧签名相同，`Paint` 跳过整幅前景重绘。生产正常循环的 `force` 为 false，因此没有兜底；前部指标再次变化时才会连带更新。对监控软件属于数据展示正确性问题，不是可延期的性能优化。

修复：完整比较绘制状态，或对全部命令、全部影响绘制的字段计算完整签名，禁止静默截断。回归应单独修改最后一个指标的数值、状态和进度，同时固定前部指标。

### F02 — P2：隐藏时改变尺寸，恢复后 GPU 仍可能使用旧大小

位置：`src/Rendering/Presentation/GpuPresentationHostController.cs:511`、`:313`；`src/UI/UIController.cs:333`、`:344`。

隐藏使宿主 suspended；期间尺寸通知因 `!IsActive` 被丢弃。恢复只 Resume 和 UpdateGpuScene，没有补同步 renderer/交换链尺寸。若场景重建又修改客户区高度，重入保护还会丢弃该次通知。结果是场景新尺寸与旧纹理/交换链不一致，可能出现拉伸和错位。

修复：记录待处理尺寸，在恢复后和布局最终确定后同步一次，再提交场景。覆盖隐藏期间改宽度、缩放及布局后重新显示。

### F03 — P2：在已隐藏窗口上启用实时 GPU，会保持活动捕获

位置：`src/UI/UIController.cs:987`；`src/Rendering/Presentation/GpuPresentationHostController.cs:250`。

托盘设置可在主窗口已隐藏时开启 GPU/live；Start 不检查当前可见状态，无条件启动捕获、清除 suspended 并启动 timer。设置应用结束再次 Hide 不会产生新的 VisibleChanged，因此不会补 Suspend。它违反隐藏时暂停捕获的生命周期合同；本条关注会话状态，不提出性能预算要求。

修复：启动后同步实际可见/最小化状态，保证隐藏的新会话进入暂停，再由显示事件恢复。

### F04 — P2：启动失败分支仍丢弃恢复 WDA 失败的宿主

位置：`src/UI/UIController.cs:1017`；对照同文件 `:353` 和 `src/Rendering/Presentation/GpuPresentationHostController.cs:666`。

成功设置排除后，若捕获启动失败，且撤销排除及清理重试均失败，宿主会保留 presentation 以便后续恢复。但 StartGpuPresentation 的失败分支 Dispose 后无条件清空引用，绕过 StopGpuPresentation 已有的所有权保留判断。窗口可能已回退 GDI 却继续被截图排除，并失去恢复入口。

修复：统一走保留未恢复所有权的停止路径；对“捕获失败 + 撤销排除失败 + 稍后恢复成功”做顺序验证。

## 验收和版本绑定缺陷

### F05 — P2：最新主窗/编辑器/live 验收没有覆盖新浮动玻璃主题

位置：`docs/reviews/evidence/gpu-themes/nonperformance-20261022/qa/nonperformance-manifest.freeze-verification.json:703`、`:767`、`:890`；同文件 `:543`。

这些入口全部选择旧 `LiquidGlass_GPU`，没有选择新增 `LiquidGlass_Toolbar_GPU`。后者才启用本轮 floatingToolbar 材质、固定背景和主题前景映射。新主题出现在打包目录、合同测试通过，均不能代替其真实主窗/预览/live 验证。此前独立材质夹具和主审临时运行也不能自动算作最新冻结候选的结果。

此外 unit-theme-editor filter 未纳入 `GpuThemeEditorPreflightRegressionTests`，其 4 个新增回归不在宣称的 75 项结果中；当前测试目录也未发现针对新 SaveThemeAs/ThemeEditHistory 的直接调用覆盖。

修复：添加新主题固定、编辑器、live 和布局条目，检查实际样式/输入模式/捕获数；补入新编辑器回归及新增交互的定向覆盖。人工清单目前也预置旧玻璃主题。

### F06 — P2：汇总把三个候选的结果列在最终候选下

位置：`docs/reviews/2026-10-22-gpu-nonperformance-runner-revision.md:116`。

对应原始 summary：

| 组 | 候选 | 源快照前缀 |
| --- | --- | --- |
| static/contracts | `frozen-20261021T210747Z` | `C356332B` |
| gui-fixed/offscreen-gpu | `frozen-20261021T215413Z` | `C64BB8F9` |
| live/release | `frozen-20261021T220711Z` | `7290F91F` |

§5.3 在最后一个候选名下直接列出各组全通过，掩盖前两组来自旧快照的事实。各次运行自身的 `partial=true` 是诚实的，但不能拼成最终候选的完整验收。

修复：逐行标注实际版本；修复完成后冻结同一候选，在其上完成必要组及人工项目，再生成最终汇总。

### F07 — P2：产物集合哈希漏掉实际加载的 shader 和主题

位置：`scripts/run-gpu-nonperformance.ps1:633`。

默认只包含输出目录根层的 EXE/DLL；manifest 未覆盖 artifactFileGlobs。输出的 `.cso`、主题 JSON、deps/runtimeconfig 不参与集合哈希。尤其 shader 是 PreserveNewest 复制，较新的陈旧输出可在 Rebuild 后继续存在。EXE/DLL 一致仍可能运行不同材质。

修复：绑定实际加载的程序、依赖描述、预编译 shader、主题等运行资源，并验证输出与冻结输入的对应关系。

### F08 — P2：构建缓存命中后使用旧哈希记录

位置：`scripts/run-gpu-nonperformance.ps1:681`、`:1575`。

同工程后续入口直接返回旧 buildRecord，比较的还是旧哈希，随后却运行磁盘当前路径。如果并发构建替换了输出 DLL，源码扫描又排除 bin，后续入口可以运行新文件却记录旧产物身份。

修复：缓存用于免重建，但每次启动前重新核对实际产物集合；使用隔离且不可变的候选输出更稳妥。

### F09 — P2：人工收据未绑定对应入口，也绕过可机器核验的证据规则

位置：`scripts/run-gpu-nonperformance.ps1:175`、`:216`、`:1421`。

校验仅要求收据里的产物哈希属于任意冻结入口，不要求对应当前入口；附件只检查存在和哈希，requiresManualStep 分支随后直接采纳收据状态，不执行该入口的 evidence/jsonChecks/windowAuditRequired。填错入口的合法产物和附件，也能被当作当前入口 PASS。问题是不能识别配错证据，不据此断言现存收据造假。

修复：绑定 entryId 和该入口产物；对可机器检查的附件继续执行合同。release 明确绑定 ZIP、运行文件清单和实际启动模式。

### F10 — P2：冻结输入仍漏掉明确参与编译的文件

位置：`docs/reviews/evidence/gpu-themes/nonperformance-20261022/qa/nonperformance-manifest.freeze-verification.json:123`。

两套输入集合均漏掉 `Properties/Resources.Designer.cs`，还未覆盖被引用的图标等资源。仅修改这些文件时，输入指纹不变。二进制检查有额外保护，但不能声称完整构建输入已冻结。

修复：按工程实际编译/嵌入/复制输入生成清单，纳入源生成结果的输入资源、应用图标及所需构建配置。

## 其他限制和收口判断

- 报告自行承认 5 项 manual、正式候选冻结和最终绑定待完成，不能描述为“全部非性能工作已做完”。
- `qa/manual/MANUAL-STEPS.md:12` 仍说每条 N/A 都不阻断，与已修正的 runner 必需项逻辑冲突，应更正文案。
- 发布 smoke-report 对缺主题/缺 shader 变体只记进程存活，未证明 GPU 开启后的可读回退；它不能代替计划 N11 的完整启动矩阵。当前资料也不足以单凭该收据确认真实交互 UAC 链路完成。
- README 把 Windows Graphics Capture 和 Desktop Duplication 混为同一实现；实际当前捕获源是 Desktop Duplication。多屏支持声明也应与仍待完成的实际跨屏/DPI 验收分开。
- 圆角 67.5 取整为 68、8-bit alpha 为 64/255、GPU 组标题使用 Font.Group 这三处断言修订有明确实现语义依据，不应仅因“改了测试”就判定有问题。Font.Title 不生效的范围仍应向编辑器用户说明。
- 性能延期决定保持；本报告不要求优化帧率、CPU/GPU 预算或开启长测。

总体：**接手工作有真实工程价值，尤其修复了验收脚本不能运行和 GPU 宿主漏启动的问题；但功能正确性及证据绑定仍有缺口，尚未达到最终交付标准。** 优先处理 F01–F04，再补齐新主题覆盖、运行资源绑定和同候选验收，最后完成必要人工事项及独立复核。
