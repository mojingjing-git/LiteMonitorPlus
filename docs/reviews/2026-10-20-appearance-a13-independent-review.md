# A13 外观改造独立验收

日期：2026-10-20。对象：提交 `954a634`，父提交 `5d5201f`。

## 结论

**主面板接线已经落地，构建、单元与原接线/轮廓探针通过，但本次验收不通过。** 局部失效探针与补充检查确认 7 项功能或合同缺陷，另有 1 项资源释放缺口。均按 P2 处理，未确认 P0/P1；不宜将 A13 标为所有接线要求已经收口。

这些问题可以在 A14 收口。它们涉及新效果实际绘制、编辑器预览和生命周期，建议先修复再扩展外观功能。实时玻璃属于后续阶段，本报告不把它尚未接入列为 A13 缺陷。

证据：[检查汇总](evidence/a13-954a634/suite-summary.json)、[独立复现结果](evidence/a13-954a634/independent-repros.json)、[复现程序与重跑方式](evidence/a13-954a634/README.md)。

## 对象与隔离

- 使用 `git archive 954a634` 导出固定源码，所有构建和复现均在 `.review-scratch/appearance-a13-audit-954a634/` 内完成。
- 导出压缩包 SHA256：`464060D4BCA2CDC0670D666F80AA30550CF76B04510ECD59E8E3B0EAD569652A`。
- 构建所得主 DLL SHA256：`032E3EA8A4F47753D8E57C1D5746D9023D6195A3D6B8B47EBAD47F064C8AEAD4`。
- 主工作区已有另一执行者的 A14 改动。本次未修改它们，也未修改产品实现。
- 两个并行只读审查分别检查窗口生命周期与渲染/缓存；以下发现均由主验收补充核对。除资源项外，均有直接运行证据。
- 本文行号均指固定提交 `954a634`。主工作区正在修改，后续行号可能变化。

## 已运行的检查

| 检查 | 结果 | 能证明的范围 |
|---|---|---|
| Release 解决方案构建 | 0 错误 | 固定源码可编译；有 254 条警告，包含 NuGet 漏洞源无法访问等信息 |
| 单元测试 | 577/577，通过，0 跳过 | 当前测试集未发现失败 |
| A13 接线探针 | 70/70，通过 | 旧主题新旧入口对比、新效果入口、运行时快照和基础轮廓接线 |
| 窗口轮廓探针 | 11/11，通过 | 离屏 HWND、DWM 回读、基础 Region 生命周期与资源计数 |
| 旧入口外观探针 full | 368 个夹具成功出图、无运行异常；44 个局部失效场景均存在差异 | 不含 A13 runtime；不能据其退出码 0 宣称局部重绘或新入口性能通过 |
| 独立补充复现 | 见下文和证据 JSON | 现有探针遗漏的边界条件 |

普通沙箱下 VSTest 与 testhost 的本机 TCP 通信被拒绝，日志为 `SocketException (10013)`。在隔离目录允许本机通信后测试通过；前两次中止不能计作产品测试失败。默认并行构建也出现无诊断失败，单进程 `-m:1` 构建成功。

## 发现

### R1 · P2：噪点缓存淘汰了刚创建的画刷，随后仍将其返回

位置：[BackdropNoiseCache.cs:73](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/System/Backdrop/BackdropNoiseCache.cs:73)，调用方为 [BackdropRenderer.cs:216](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/System/Backdrop/BackdropRenderer.cs:216)。

新 `Entry.LastUsed` 为 0，插入缓存后立即淘汰。已有四项被命中过时，新项反而最旧，因此第五项自己被释放，`GetBrush` 仍返回该画刷。

**实测**：依次使用噪点强度 `10,10,20,20,30,30,40,40,50`，前八次成功，第九次 `Graphics.FillRectangle` 抛出 `ArgumentException: Parameter is not valid.`。还通过 `AppearanceRuntime.OnThemeChanged → UIRenderer.Render → BackdropRenderer.Render` 复现，异常栈落在 `DrawNoise`，不是只调用缓存的人工构造问题。当前换肤路径没有清空该噪点缓存。

**修复方向**：插入前设置 `LastUsed = ++_clock`，确保新项不会被立即淘汰；补充超过容量的强度切换与实际渲染检查。另应落实背景缓存换肤、尺寸和 DPI 失效合同。

### R2 · P2：主题编辑器预览仍走旧入口，辉光和新进度条不生效

位置：[ThemePreviewControl.cs:218](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/ThemeEditor/ThemePreviewControl.cs:218)、[ThemePreviewControl.cs:229](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/ThemeEditor/ThemePreviewControl.cs:229)。

预览继续使用 `DrawPreviewTexture` 假扫描线/网格、透明背景变体和三参数 `UIRenderer.Render`；没有传入 `AppearanceRuntime`。这正是 lead-review 的 E-1/E-3 要求 A13 收口的交接项。

**实测**：使用同一份 `DarkNeo_Modern` 场景，明确设置可见的边框/辉光颜色，分别只切换一个选项：

| 变化 | 预览变化像素 | 主渲染入口变化像素 |
|---|---:|---:|
| Glow：0 → 100 | 0 | 4,256 |
| BarStyle：0 → 1 | 0 | 9,390 |

**修复方向**：预览使用同一套正式渲染器和资源生命周期，继续采用固定样例图；移除重复的假底纹与背景变通，补充预览与主入口的效果一致性检查。

### R3 · P2：两个新进度条样式会替换调用方裁剪

位置：[BarRenderer.cs:243](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/Decor/BarRenderer.cs:243)，同文件第 278 行亦然。

`g.SetClip(shape)` 默认替换已有裁剪。即使最后恢复 Graphics 状态，也无法撤销已经画到原裁剪之外的像素。

**实测**：条槽 `(20,40,180,8)`、50% 填充、调用方裁剪 `(37,43,20,20)`，样式 1 和 2 均改写了裁剪区外 **602 个像素**。半透明纹理还可能因局部重画反复叠加。

**修复方向**：使用 `CombineMode.Intersect`，保留外层裁剪；两种样式分别断言区外像素不变，并检查多次局部重绘后与完整绘制一致。

### R4 · P2：窗口变高后，Region 沿用旧高度夹紧后的切角长度

位置：[WindowAppearanceController.cs:321](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/Helpers/WindowAppearanceController.cs:321)、[WindowAppearanceController.cs:356](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/Helpers/WindowAppearanceController.cs:356)。Resize 由 [MainForm_Transparent.cs:230](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/MainForm_Transparent.cs:230) 经 helper 调用 `Reapply`。

控制器缓存了旧快照中已经按旧窗口高度夹紧的 `FrameLengthPx`。Resize 更新 bounds，但没有重新取当前快照；绘制层随后重新计算快照，二者出现不同几何。

**实测**：`frameLength=64`、1× 缩放，窗口从 `240×80` 变为 `240×200`，`Reapply` 后切角长度仍为 **40**，新快照为 **64**。点 `(50,5)` 在旧 Region 内、在正确的新 Region 外。可由增加监控项或横竖布局变化触发。

**修复方向**：最终客户区尺寸确定后，同步快照并调用 `Apply`；保持唯一 Resize 订阅。补充先缩小再增大、同尺寸换肤及布局切换检查。

### R5 · P2：solid 底板仍按局部裁剪尺寸从窗口原点填充

位置：[UIRenderer.cs:73](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/UIRenderer.cs:73)、[HorizontalRenderer.cs:33](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/HorizontalRenderer.cs:33)，后者第 51 行亦然。

这是旧实现的已知问题在 A13 中延续。填充使用窗口原点和 `VisibleClipBounds` 的尺寸，失效区偏离原点时漏补底色；新入口也没有修复。v4 和 lead-review 的 B-2/H-3 已明确要求底板锚定完整客户区。

**实测**：客户区 `240×160`、背景 `#123456`、裁剪 `(37,43,20,20)`，横竖两种新入口绘制后，失效区内点 `(40,45)` 仍是预填的洋红色 `FFFF00FF`，预期为 `FF123456`。

旧入口 full 探针也复核了这一继承问题：`itemDriven` 为 0/44 一致，`fixedRect` 为 42/44；先补底色后，两类场景均为 44/44。该探针的退出码 0 只表示正常完成，不能代表视觉断言全通过。

**修复方向**：在新入口按完整 `options.ClientBounds` 补底，由 Graphics 裁剪限制写入；保留旧入口兼容要求。M1 当前整窗失效可降低暴露频率，但不能把该路径算作已满足合同。

### R6 · P2：效果全关时，竖屏每帧仍新增 200 字节分配

位置：[UIRenderer.cs:79](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/UIRenderer.cs:79)、同文件第 122 行。

只要有 runtime，调用方就构造 `BuildGroupRegions` 列表，之后下层才检查纹理是否关闭。旧主题也会新增 List 和后备数组。

**实测**：7 分组、1×、新效果全关，预热 200 轮后进行三组、每组 500 帧的同线程分配测量：旧入口 **3,112 B/帧**，新入口 **3,312 B/帧**，三组均增加 **200 B/帧**。这是关闭态零增量分配合同失败，不足以推导成显著 CPU 回退。

**工作区状态**：A14 未提交改动已增加纹理开关判断及复用区域列表。本次对固定 A13 的缺陷成立；该 A14 修正尚未在本报告中单独构建、复验，不应重复派工。

### R7 · P2：纹理进度条的斜线只擦过瓦片角点

位置：[DecorTextureFactory.cs:195](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/Decor/DecorTextureFactory.cs:195)。

两条直线分别位于 `x+y=0` 与 `x+y=2×size`，没有贯穿正方形瓦片内部；常见条高和位置下，条内部只显示半透明平底。

**实测**：`16×16` 瓦片仅 **7 个非透明像素**，去掉两像素边缘后的内部区域为 **0 个**，没有预期的斜纹。

**修复方向**：画出穿过瓦片内部且平铺连续的对角线；检查多个条高和 Y 偏移，增加纹理形态断言。

### R8 · P2：纹理源图与记账时取得的图像没有确定的释放路径

位置：[DecorResourceHost.cs:315](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/Decor/DecorResourceHost.cs:315)、[DecorRenderer.cs:443](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/Decor/DecorRenderer.cs:443)、[DecorResourceCache.cs:401](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a13-audit-954a634/target-src/src/UI/Decor/DecorResourceCache.cs:401)。

前两处创建 `Bitmap tile` 后仅返回 `new TextureBrush(tile)`，未释放源图；记账时取得 `tb.Image` 也未释放。缓存淘汰时释放另一次取得的 `tb.Image`，无法补上此前源图和临时图的释放。

**已核实的 API 行为**：两次 `TextureBrush.Image` 返回不同对象；释放原始 Bitmap 和第一次返回的 Image 后，第二张图及画刷仍可用，说明不能把它们当作同一份借用引用。结合源码，可确认释放路径缺口。

**修复方向**：工厂用 `using` 管理源 Bitmap；按明确尺寸记账，或及时释放取出的 Image。缓存计数归零不能替代资源所有权检查。本次没有长期内存增长测量，**不将此表述为已证实的永久泄漏**。

## 为什么原接线与组件检查没有发现这些问题

- 接线探针每次在新整张位图上绘制，没有测试调用方的偏移裁剪。
- 运行时复用检查只测试 `Update`，没有测实际 `Render` 的关闭态分配。
- `AppearanceProbe.cs:619/621` 仍调用旧重载；它的旧主题画廊与耗时不能代表 A13 runtime 接入后的效果和预算。
- 噪点组件原用例重复同一强度，没有触发第五种强度的淘汰。
- Region 原用例覆盖形状切换与尺寸边界，未覆盖旧快照先被小高度夹紧、随后增高的同步过程。
- 样式变化只断言“与旧条不同”不足以证明斜纹正确；资源记账归零也不足以证明临时原生图像已释放。

## 建议收口顺序与并行边界

| 工作包 | 处理项 | 写入范围/并行约束 |
|---|---|---|
| 噪点缓存 | R1 | BackdropNoiseCache 和对应组件探针，可独立推进 |
| 编辑器预览 | R2 | ThemePreviewControl，可独立推进；复用现有正式入口 |
| 进度条与纹理 | R3、R7 | BarRenderer、DecorTextureFactory，安排同一执行者 |
| 窗口轮廓 | R4 | MainForm/helper/controller，保持唯一接线 owner |
| 主渲染底板与关闭态分配 | R5、R6 | 两个 Renderer、AppearanceRuntime，交给当前 A14 owner，避免并发修改相同文件 |
| 资源所有权 | R8 | DecorResourceHost/Cache/Renderer，可与上述包并行；与纹理包约定工厂返回值由调用方持有 |
| 最终复验 | 全部 | 各包合并后串行构建，重跑单元/组件/接线及本报告反例 |

每个修复必须补上本次对应反例。修复合并后再以同尺寸、同入口、同参数的前后配对测量验证性能预算；不要用本次旧入口画廊的耗时代替。

## 验收边界

本次未完成真实 Win10/Win11 可见窗口的人工观感、跨屏 DPI 往返、RDP、高对比度、置顶/穿透/淡入联动验收，也没有验证 M2 实时捕获。11 项轮廓探针包含能力模拟，不能替代两种真实系统。CPU 1ms/2ms 增量预算也不在本次已通过结论内。

完整构建日志、TRX、探针报告、独立复现源代码和 PNG 位于 `.review-scratch/appearance-a13-audit-954a634/`；精选原始证据保存于本文同目录的 `evidence/a13-954a634/`，便于交给后续执行者。
