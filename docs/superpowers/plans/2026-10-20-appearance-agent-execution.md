# LiteMonitor++ 外观改造：Agent 执行计划（v4）

> 状态：待实施。本次只审查和细化计划，未修改业务代码，未构建、运行测试或实测新效果。
>
> 审查基线：2026-10-20，`97f793dccac6d5ab37120602ecab4f6238a855da`。`docs/APPEARANCE_PLAN.md` 当时已有未提交修改，本轮在其基础上修订。
>
> 设计背景：[APPEARANCE_PLAN.md](../../APPEARANCE_PLAN.md)。**本文件是实施入口**；技术路线、默认值、任务归属和验收以 v4 为准。旧稿附录中的工期、行数与历史测量不作为当前保证。

## 1. 目标与交付顺序

保留机能风和液态玻璃两个目标，把可确定的主题/绘制工作与实时背景的可行性分开验收。

| 里程碑 | 内容 | 完成条件 |
|---|---|---|
| M0：主题与证据基础 | schema、颜色、未知字段保真、可靠保存、预览复制、独立渲染探针 | 保存往返可复核，现有主题主面板画面不变；探针不需要硬件/驱动即可运行 |
| M1：机能风与完整配置入口 | 静态装饰、窗口轮廓、渐变背景、编辑器与预览、机能主题、Web 配色 | 横竖屏可用；真实 DPI/局部重绘/旧主题及性能验收通过 |
| M2：实时玻璃 | 已验证的系统材质或无自反馈的捕获后端、受控后台处理、玻璃主题、失败降级 | 先通过 A06 技术验证，再通过真实窗口、资源、性能与兼容矩阵 |

M2 的技术验证、假后端和资源协议可与 M0/M1 并行。M2 最终接线在 M1 验收后完成。A06 失败时继续交付 M0/M1，提交失败证据与未完成范围，不能把渐变降级称为已实现实时背后模糊。

范围：主悬浮窗横竖屏、主题编辑器及主色 Web 适配。任务栏维持现有透明背景与绘制方式；硬件详情/趋势/设置控件不整体换肤。复用现有组标题、GPU 键、指标顺序、阈值颜色与采样协调器。

## 2. 审查发现与修订

| 优先级 | 原计划的问题 | v4 的处理 |
|---|---|---|
| P1 | `DWMWCP_NONE=0` 被当作禁用圆角 | 使用 `DWMWCP_DONOTROUND=1`；0 是系统默认，2 是 ROUND；检查返回值和实际轮廓。见[微软枚举](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_window_corner_preference) |
| P1 | 以“必须引入 Windows App SDK”排除 Win11 系统材质 | 官方提供 `DwmSetWindowAttribute` 与 `DWMWA_SYSTEMBACKDROP_TYPE` 的 Win32 API；后者要求 Win11 22621+。API 存在不代表当前 layered/GDI 客户区能正确显示，放入 A06 实测。见[API](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmsetwindowattribute)、[材质枚举](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type) |
| P1 | 推荐每帧移开主窗抓屏，或用上一帧含自身图像回填 | 正式方案禁止周期性移动/隐藏/改 Opacity 抓屏；上一帧回填和强 tint 均不能证明得到被遮挡的真实背景，不能作为实时玻璃的正确性方案 |
| P1 | 断言捕获排除一定留下洞 | 文档区分 WDA_MONITOR 与 EXCLUDEFROMCAPTURE，但不保证某捕获后端的底层像素结果；必须按后端、系统版本验证。不得把录屏限制的安全保证描述当成背景像素依据。见[API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity) |
| P1 | 只交换 Bitmap 引用，没有定义何时释放 | 生产者拥有待发布帧；UI 线程接管、替换和释放当前帧，禁止后台释放 UI 正在绘制的对象 |
| P1 | 装饰缺少 overlay 挂载，外框使用裁剪区尺寸，轮廓更新存在初始化顺序问题 | 主集成者统一接入完整客户区 bounds、绘制层次和窗口事件；不能在 UIController 构造期间访问尚未创建的 `_winHelper` |
| P1 | 编辑器会丢 alpha；缺省整数会被写出；复制遗漏 LayoutScale | 明确整数缺键=0，新增整数写出时忽略默认值；颜色保留 alpha；统一副本和只缩放一次的合同 |
| P1 | `SafeWrite` 先删除目标再移动临时文件 | 独立修复为同目录临时文件+替换，失败保留原文件；不使用删除目标后重试的退路 |
| P1 | 既有探针固定旧主题、硬件未就绪时跳过渲染，不能验证新效果 | 增加无需采集实例的外观入口及固定夹具；生产 UI 最短 80ms，按 12.5Hz 而非 1Hz 或固定 10Hz 评估 |
| P2 | 未知字段遗漏 thresholds 层级；要求所有 null 消失；Font.Scale 验收不存在的属性 | 所有类型化节点保留扩展数据；原有未知 null 要保留；font.scale 本次只保留，不启用新字号语义 |
| P2 | Web 挂载点写成 LiteWebServer，且未定义 CSS 颜色格式 | 实际 HTML 在 `WebSessionManager` 组装；只注入解析后规范化 CSS 色值，不能直接注入主题文本或把 ARGB 当 CSS 八位色值 |
| P2 | 把未清 ParseColor 缓存认定为串色；顺带修字体 DPI | 字符串颜色缓存不因主题改变而变成错误颜色；统一清理是资源治理。Point 字体是否存在 DPI 问题需另行取证，本轮不顺手改变字号算法 |

不依赖未文档化 API；删除原稿关于未文档化调用必然导致杀软误报的推断。老 Aero blur API 不能当作现代 Win10/11 通用模糊实现；微软说明自 Windows 8 起 `DwmEnableBlurBehindWindow` 不再产生该模糊效果，见[文档](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmenableblurbehindwindow)。

## 3. 实施前冻结的合同

### 3.1 JSON 与颜色

- 保留现有 JSON 结构、旧属性名/默认值及旧主题 Version；新成品主题用 Version 4。不能仅凭 Version 打开新特效。
- `Color.Border/Shadow/TextSecondary` 使用可空字符串，属性级 `WhenWritingNull`。只有特效显式开启才消费这些新增用途，不能因为 7 个旧主题已有这些键就自动增加描边/辉光。
- `Layout.FrameStyle/FrameWidth/FrameLength/Scanline/Glow` 保留 int、默认 0；**缺键与 0 等价**，属性级 `WhenWritingDefault`。补齐已有设计中无开关的 `Grid`（0–100）和 `BarStyle`（0=旧条、1=渐变、2=纹理），同样默认关闭。
- FrameStyle：0=旧路径、1=切角、2=圆角描边、3=静态全息边框。FrameWidth/FrameLength 为 96 DPI 逻辑尺寸；Glow/Scanline/Grid 是强度，不缩放。M1 不增加扫描移动、呼吸灯或其他动画。
- `Backdrop` 为可空配置，缺失/solid 走原背景；gradient 走静态渐变；glass 表示请求实时材质，实际后端不可用时回退 gradient→solid，并保留可诊断原因。仅仅存在 Backdrop 对象不代表启用抓屏。
- Backdrop 配置：Mode；Opacity 默认 0.75；Blur 默认 12；Tint、GradientStart、GradientEnd 可空；GradientAngle 默认 90°；Noise、EdgeGlow 默认 0；Fps 默认 2。渐变两端缺省分别取 Background 与 GroupBackground。可选对象缺失不自动保存一个默认实例。
- Opacity 定义为背景 tint 层的额外 alpha 系数，与 Tint 自身 alpha 相乘，只作用于背景，不乘文字和窗口整体透明度。Tint 缺省取 Background；Mode=solid 保留旧路径。Blur 只约束捕获滤波后端，系统材质若不支持自定义半径，编辑器显示系统控制，不伪造生效。
- 归一化只作用于运行时副本：FrameWidth 0–8、FrameLength 0–64、强度 0–100、Opacity 0–1、Blur 0–32、Fps 1–6；未知样式关闭该效果，未知背景模式退回 solid。路径几何还需夹紧到当前 bounds。不能把运行时夹紧值自动回写用户 JSON。
- `rgba(r,g,b,a)` 的 r/g/b 为 0–255 整数，**a 统一为 0–1**；`1` 表示不透明。字节 alpha 用已有 `#AARRGGBB`。使用 InvariantCulture，拒绝越界/NaN/Infinity，alpha×255 按 AwayFromZero 舍入。保留旧六/八位十六进制及颜色名行为；非法值维持透明兜底，新效果使用前可另取安全回退色。
- 扩展数据覆盖 Theme、LayoutConfig、FontConfig、ColorConfig、ThresholdConfig、ThresholdSet、BackdropConfig。保留未知对象、数组、布尔和 null；后续字段类型化后不能重复写出同名已知/扩展键。
- font.scale 只作为扩展数据保留，增加 1.5 的合成样本；不宣称存在 `Font.Scale` 属性，不改变字体的 Point/DPI 算法。

### 3.2 配置、缩放与绘制上下文

新增不可变的 `ThemeAppearanceOptions`/`AppearanceRenderContext`，名称由 A00 最终固定。包含归一化后的标量/Color、主题代次、实际 DPI、用户缩放、完整客户区矩形、背景帧/预览源。后台不持有整个 Theme、Font、Brush、Pen 或 Control。

**每个新尺寸只缩放一次。** 新 FrameWidth/Length/Blur 保留逻辑值，由 options 生成物理像素值；不再让 `LayoutConfig.Scale` 和绘图 helper 同时处理这些新字段。现有布局字段的缩放行为保持原样。禁止以全局 `UIUtils.ScaleFactor` 代替当前窗口 DPI。

保留现有公开 Render 签名，通过重载传入上下文。完整窗口 bounds 来自布局完成后的 ClientRectangle，不能用 VisibleClipBounds 的局部尺寸或 `(-5,-5)` 背景填充矩形定位外框。实际区域无有效宽高时跳过新几何绘制。

预览使用独立 Layout 副本，复制所有配置字段与扩展数据，明确源是否已缩放，更新 LayoutScale 后只缩放一次。可在模型提供局部 Clone，避免两份手写字段清单；不引入通用深拷贝框架。字体只借用，不释放原主题字体。预览不启动桌面捕获；玻璃使用固定样例图并注明是预览。

### 3.3 绘制层次与资源

顺序固定：底板/背景 → 窗口底纹 → 组块填充 → 组底纹/组框 → 原有标题/指标/条 → 向内的窗口边框/高光。扫描线和网格不覆盖数值文字；绘制入口用 Graphics.Save/Restore 隔离状态。

gradient/捕获后端每帧从确定底板或完整有效背景帧开始，不能反复在上一帧上叠半透明 tint 而累积变色。系统材质则遵循 A06 已验证的客户区呈现方式，不能用不透明底板把材质完全盖住。

横屏复用窗口底板、底纹和外框；其现有模型是列，M1 不为了“组框一致”改造横屏分组布局。条纹只作用于原来存在进度条的指标。阈值取色、格式化和标题继续复用已有逻辑，不借机启用旧 BarLow 或改变安全值颜色。

新增几何/纹理/渐变资源优先放在 Decor 目录；UIUtils 只做确有必要的兼容接线。共享缓存对象由所有者释放，借用者不能 Dispose 或修改 Transform；动态变换使用克隆/独占对象。

缓存键覆盖主题代次、颜色、稳定槽位尺寸、方向、DPI/用户缩放、噪点种子等；**渐变按完整条槽缓存，再裁剪到当前填充值**，不能每个百分比建一条缓存。初始上限：128 个绘图缓存条目、纹理/静态装饰位图总计 16MiB；满额按可复核策略释放。尺寸/主题/DPI 变化有明确失效入口，每次 Paint 都绘制有效缓存，不能用跳过绘制来节流。

### 3.4 窗口与后台帧

MainForm 拥有窗口外观生命周期，在 helper 已创建、最终尺寸已确定后应用主题轮廓；覆盖初始化、同尺寸换主题、Resize、DPI/横竖变化、HandleCreated/Destroyed 及退出。只保留一处 Resize 绑定。切角使用与画框相同几何，Win11 DONOTROUND=1；回到圆角恢复 ROUND=2 与正确 Region。定义 Region/路径的所有权与释放，不重写置顶、穿透和淡入的既有时序。

系统材质后端在 UI 线程应用/恢复窗口属性，由 DWM 呈现，不创建后台抓屏计时器。以下帧所有权与捕获预算只适用于需要位图的后端，不能为复用接口又重复抓屏。

实时帧推荐采用以下所有权方案，不能仅写一个 Interlocked.Exchange 就结束：

1. 单个后台任务捕获/处理自己的位图，最多一个在途请求和一个待发布结果；尺寸/主题/窗口代次失效的结果丢弃。
2. 通过可接管的 pending 槽及 UI Post 发布。后台可释放尚未被 UI 接管的替代帧，不能释放 active 帧。
3. UI 线程取走 pending，替换 active，并在 Paint 不再使用旧帧时释放旧 active；Paint 不 await，后台不修改 active。
4. 隐藏/最小化/进入非 glass 模式/关闭时停止工作；显示恢复时请求一帧。不能积压计时器回调或为了背景重复触发硬件采样。
5. 取消、窗口销毁、迟到回调和 Dispose 都检查代次；最终释放 pending/active/未发布位图及 DC/GDI 句柄，不在 UI 上无限等待后台退出。

捕获上限 6fps、默认 2fps，处理未结束则合并需求；大尺寸下允许降低分辨率/频率并记录生效状态。初始捕获面积预算 2,000,000 物理像素，帧缓冲预算 32MiB；超限退化，不能持续按全屏分配。

## 4. 玻璃效果的技术门槛（A06）

先在 `tests/AppearanceBackdropProbe/` 实现最小验证，沿用真实 MainForm 的 FormBorderStyle、GDI 文本、Opacity、穿透与 Region 条件；不安装驱动、不改用户设置、不上传桌面图像。新项目配置由主集成者接入。

记录 OS 构建、捕获后端、权限级别、窗口扩展样式、物理尺寸/DPI，以及是否处于 RDP/HDR 等特殊条件；不能从提权或某次成功返回推定所有桌面可捕获。

按顺序验证：

1. Win11 22621+ 的文档化 DWM 系统材质。分别记录 API 结果、窗口样式与实际截图/短录制；必须看到背后内容响应，不能凭成功返回值或 Mica 壁纸色判断为实时模糊。
2. 如果不兼容，验证一个能排除自身且得到正确背景的捕获后端。使用移动棋盘/文字做背景，连续观察 30s；不能出现自己的标题/数字轮廓、递归变暗/拖影或窗口抖动。
3. 可测试 EXCLUDEFROMCAPTURE，但不能先验保证它对 BitBlt/其他后端的结果；低于 Win10 2004 的行为也不同。记录该设置对用户录屏的影响，不能把屏蔽应用录屏作为无提示的永久附作用。
4. 单纯缩小再放大只是缩放近似，不是带半径合同的模糊。若采用捕获路线，明确 Blur 的逻辑像素半径、降采样映射和实际滤波；可用受预算约束的可分离 box/Gaussian 近似。用阶跃边缘/棋盘比较 Blur=0/12/24，结果应随半径变化。见[插值文档](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/how-to-use-interpolation-mode-to-control-image-quality-during-scaling)，不要把 SmoothingMode 当作模糊算法。
5. 通过可用的 Win10/Win11、负坐标显示器、跨屏 DPI、RDP/锁屏恢复和动态高面板测试，产出后端支持矩阵；缺少条件记未验证。

**放行条件：** 原生/捕获方案满足图像正确性、现有窗口行为和 §7 性能预算，且没有周期性移窗/隐藏/改透明度。不满足就不启用该后端；保留 gradient/solid 及具体失败记录。不同 Windows 版本可以选择不同后端或降级，不承诺跨版本像素一致。

## 5. 工作包、依赖与验收

共 **18 包（A00–A17）**。表中依赖是接线前的最小依赖，独立草稿/用例可提前准备；“通过”均指后续实施时运行并留证。

| ID | 任务 | 前置 | 具体交付与验收 |
|---|---|---|---|
| A00 | 主集成者固定基线与合同 | 无 | 记录实际 HEAD/工作区、旧主题哈希、文件 owner、接口/项目路径；保留其他任务改动。对当前源码重新确认，不沿用历史“100%适用”结论 |
| A01 | schema、颜色与扩展数据 | A00 | ThemeManager 中新增字段、局部 Layout Clone、rgba 解析及配置往返测试；旧字段行为不变，合成嵌套未知值/null 保留，新默认键不凭空出现 |
| A02 | 文件保存可靠性 | A00 | ThemeFileService 的同目录临时写入与安全替换，注入写/替换失败；已有目标原文仍可读，未成功不得返回成功；临时目录测试 |
| A03 | 外观 options 与预览副本合同 | A01 | 新增纯归一化/颜色快照/物理尺寸解析，未知值与极端尺寸测试；重复解析不修改原主题，缩放无累计效应 |
| A04 | 独立外观探针与截图夹具 | A00 | 新增明确标注的 `--appearance-only` 入口，不创建 HardwareMonitor/Open 硬件；覆盖旧/新主题、横竖、1/4卡、真实DPI元数据与用户缩放、变化的进度。旧探针采集语义不改 |
| A05 | 装饰几何与资源缓存 | A03 | 新增路径、内描边、纹理/渐变缓存，覆盖 1×1/窄窗/极端半径；持续变化进度 10,000 次后缓存/原生内存稳定 |
| A06 | 玻璃技术验证与路线裁决 | A00 | 按 §4 留本地证据、支持矩阵、实际样式、CPU/句柄数据；明确通过/失败/未验证。失败不阻塞 M1 |
| A07 | 机能装饰与进度条 | A05 | 新 DecorRenderer/BarRenderer，静态扫描线/网格/组框，关闭直接走旧逻辑；连续 Paint 100 次不消失，不改变文字/阈值/组名 |
| A08 | 静态背景与降级 | A03 | 新 BackdropRenderer：solid/gradient、tint/noise/edge；颜色与角度缺省确定；未就绪 glass 明确回退，预览使用固定源，不启动捕获 |
| A09 | 窗口轮廓策略 | A05 | 新窗口外观组件，消费统一几何；定义初始化/句柄/Region 所有权。现有文件的事件接线交 A13，不能另改一份 MainForm |
| A10 | 编辑器及预览接入 | A03 | AppearanceFields、ThemeEditorForm/ThemePreviewControl；alpha/取消/恢复缺省、A→B换主题只改B、初始化不写回；可用假绘制提前做，完整预览在 A13 后验收 |
| A11 | Web 配色适配 | A03 | 新 WebThemeCss 与 WebSessionManager/WebPageContent 的唯一挂载；规范 CSS 颜色、刷新后跟随主题；HTML 请求不触发采样/FPS需求，鉴权/连接代次行为不变 |
| A12 | 机能主题与视觉样本 | A01、A03 | CyberPunk_Mech.json、参数说明及亮/暗/复杂背景夹具；横竖可读、多卡不溢出。最终截图由 A14 的真实渲染产生，不能拿设计示意图当实现证据 |
| A13 | 主面板 M1 集成 | A04、A07、A08、A09 | 同一 owner 接两个 Renderer、UIController、MainForm/helper及必要UIUtils；完整bounds、所有绘制层、缓存清理、窗口轮廓、Web颜色快照发布；每项入口唯一 |
| A14 | M0/M1 综合验收与独立审查 | A02、A10、A11、A12、A13 | 按 §7 完成旧主题对比、保存往返、窗口/编辑器/Web、性能和干净发布；汇总准确源码/程序集及限制 |
| A15 | 实时背景服务及后端 | A03、A06、A08 | 仅 A06 放行后端；可提前用假后端完成 §3.4 的帧所有权/取消/代次/降级测试，真实后端的正确性和预算须有证据 |
| A16 | 主面板 M2 接线与玻璃主题 | A14、A15 | 主集成者接启动/可见性/模式/尺寸/退出；主题 owner 产出 LiquidGlass.json；首帧有底色，无额外捕获/重复服务，配置和实际降级状态一致 |
| A17 | M2 故障、性能及发布验收 | A16 | 高面板/跨屏/隐藏恢复/快速换肤/释放压力，实际进程成本及支持矩阵；无自反馈、孤立任务、句柄/内存持续增长；未通过的后端不发布 |

所有包交付：实际基线/上游提交、修改文件、API/接线说明、测试命令与退出码、证据路径、未验证项。共享文档和主验收记录由主集成者汇总，其他 agent 写独立结果文件。

## 6. 并行分工与文件归属

建议 **1 名主集成者 + 3–5 名工作 agent**，按就绪队列补位，实际数量受工具上限约束。新文件不等于零冲突：两个 Renderer、UIController、ThemeManager 和 WebSessionManager 尤其需要唯一 owner。

| 工作线 | 独占写集 | 交接规则 |
|---|---|---|
| 主集成者 A00/A13/A16 | `UIRenderer.cs`、`HorizontalRenderer.cs`、`UIController.cs`、`MainForm_Transparent.cs`、`MainFormWinHelper.cs`、必要 `UIUtils.cs`；所有 csproj/sln/依赖/发布接线、总验收记录 | 共享入口顺序修改；不把同一个文件分别派给背景和装饰 agent |
| 模型 A01/A03 | `ThemeManager.cs`、新增 options/复制/快照类、相关独立测试 | A01 冻结字段/Clone 接口后，其他工作线消费；不同时修改编辑器 |
| 保存 A02 | `ThemeFileService.cs`、保存失败/往返测试 | 接口保持兼容；schema 之外的文件可靠性可立即独立推进 |
| 装饰 A05/A07/A09 | `src/UI/Decor/`、新增窗口外观组件、相关测试 | 现有窗口 helper/UIUtils 的接线需求交主集成者；本线不直接改 Renderer |
| 背景 A06/A08/A15 | `src/System/Backdrop/`、`tests/AppearanceBackdropProbe/` 源码及后端测试 | 项目文件由主集成者接入；A06 不通过也可交付 A08 的静态背景 |
| 编辑器 A10 | `ThemeEditorForm.cs`、`ThemePreviewControl.cs`、`AppearanceFields.cs`、必要语言 key、相关测试 | 每次 LoadSelectedTheme 重新绑定或用当前对象访问器；不预设需要改 resx，不复制主窗口捕获服务 |
| Web A11 | `WebSessionManager.cs`、`WebPageContent.cs`、新增 CSS 构造器及测试 | 不改采集协调器/普通指标快照；需要主题快照的发布交 A13 |
| 视觉主题 A12/A16 | 两个新主题 JSON、参数说明和固定视觉样本 | 可复用已完成模型工作的 agent；不批量改 10 个旧主题 |
| 验证 A04/A14/A17 | PerformanceProbe 源码/README、独立夹具/结果文件 | 自有输出目录；不复制或偷偷替换生产渲染实现；最终独立审查由空闲 agent 接替 |

启动：A00 后先做 A01、A02、A04、A06；模型合同就绪后放行装饰、编辑器、Web、主题与静态背景；主 agent 继续项目接线及 A13。保存/模型任务完成后释放名额承接下一包，避免为了固定角色保持空闲 agent。

代码 agent 使用独立 worktree/输出，按确认的上游提交更新；同机的 CPU 测量、录屏/屏幕捕获实验和正式打包串行执行，其他人继续纯代码/假后端工作。现有 xUnit 进程内全局状态保持串行配置。

若硬件稳定性计划也在实施，两个计划共用的 UIController、MainForm 与 WebSessionManager 仍归同一个总集成负责人，不能各设一个主 agent 同时修改。外观模块按当前已合并的采样接口接入，不携带旧文件整份覆盖，也不引入第二个采样时钟。

## 7. 验收细则

### 7.1 配置与编辑器

- 逐一对 10 个旧主题副本执行 Load→Save→Load，检查已知语义与各层未知 JSON 值；未知 null 不删除，新可空已知属性不凭空新增 null。
- 只打开/预览/取消不修改文件字节；编辑后保存按 JSON 语义比较，不要求原空白/键顺序/PascalCase 与 camelCase 的字节一致。
- 新数值从关闭→开启→关闭后不遗留默认键；新颜色取消不变、alpha=0/部分/1可保留并正确绘制。
- 注入临时文件写入/替换失败，原文件完整；测试不覆盖内置主题、不写用户当前配置目录。
- 预览反复切换/缩放不改变原布局、不释放主窗口字体；用反射/字段覆盖测试防止未来新增 Layout 属性又被复制遗漏。

### 7.2 画面与窗口

- 同机、同字体、固定指标/文本/时钟，在旧/新代码之间比较全部旧主题的横竖截图；关闭新能力时像素应一致。真实桌面合成截图单列，不与可控离屏图混作一个指标。
- 装饰开启时覆盖完整/局部失效：例如 `Rectangle(37,43,20,20)` 与连续 100 次重绘；合成结果与一次完整绘制一致。
- 1/4卡组、长组名/自定义标签、零项、窄窗、横屏自适应宽度；不改变 GPU 名称、指标键或排序。
- 真实 96/120/144/192 DPI 与用户缩放分别记录；至少覆盖 100%→200%→100% 跨屏往返、窗口尺寸不变的圆角→切角→圆角、句柄重建和 100 次换肤。
- 新成品主题以窗口 Opacity=1 在亮/暗/复杂背景下验证文字可读，普通文字目标对比度至少 4.5:1；保留用户现有 Opacity，但不能据此承诺任意用户透明度都达标。扫描线/辉光不能遮盖告警文字。

### 7.3 Web

M1 采用原计划 B：主色随主题，**浏览器刷新页面后生效**；不扩充实时指标协议、不增加颜色轮询。浏览器不复现桌面截屏、DWM 或噪点动画。

UI 发布不可变颜色快照；HTML 构造只消费该快照。将成功解析的 Color 输出为规范 `rgba(r,g,b,a)` 或六位 RGB，采用 invariant 小数；主题的 `#AARRGGBB` 不直接当 CSS 的 `#RRGGBBAA`。未知/非法色值用明确回退，不能原文插入 `<style>`；用 `</style>` 等输入测试不会成为注入内容。保持已有仅取 HTML/图标不产生硬件需求的规则。

### 7.4 性能与资源

以下是实施用的**初始验收预算，尚未实测**。先留基线，达不到时减少效果/降级或给出数据和原因，不能自行放宽后仍写“通过”。同机至少三轮配对，报告 CPU 时间、墙钟、分配、真实尺寸/DPI、主题、组数、频率、程序集哈希。

| 场景 | 初始预算/不变量 |
|---|---|
| 新效果关闭 | 新模块不创建位图/计时器/捕获；热路径不增加托管分配；每帧额外 CPU ≤ `max(基线×5%, 0.1ms)` |
| 静态机能效果 | 普通档额外 CPU ≤1ms/帧；4卡+两倍物理尺寸档 ≤2ms/帧；不新增动画计时器；以实际最低80ms（12.5Hz）报告投影 |
| 实时背景 | 默认2fps、上限6fps；后台额外 CPU 初始预算≤40ms/秒，单次处理 p95≤30ms；叠加绘制另按静态预算计。报告 `CPUms / 1000 / 逻辑核数 ×100%`，不能把墙钟当CPU |
| 暂停/失败 | 主窗隐藏/最小化/非glass时无持续捕获；失败回退后不按每次Paint无限重试；显示恢复只合并请求一帧 |
| 资源 | 10,000次进度更新、100次换肤/尺寸变化后，新缓存/句柄/原生内存进入有界平台；释放后新增资源回到基线波动范围，按§3缓存与帧预算检查 |
| 功能隔离 | UI重绘、背景帧更新、HTML/预览不触发新硬件采样，不扩大Web/FPS需求，不恢复旧的高频更新路径 |

历史 4.56ms 等数值来自旧基线，只作参考；两倍竖屏物理尺寸为 480×1576，不能写成宽度不变的 240×1576。既有 `Scale(1f,scale)` 是用户缩放模拟，不能替代真实高DPI窗口测试。离屏渲染不包含 DWM/窗口呈现，最终必须补实窗进程 CPU 对比；若同期已接入采集 helper，统计完整进程树。

### 7.5 执行时命令

以下命令由接手 agent 在对应实施包运行并记录退出码，本次文档审查未运行：

```powershell
dotnet build LiteMonitor.sln -c Release
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build
dotnet build tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj -c Release
```

A04 **新增并实现** `--appearance-only` 后，才可执行以下新入口；它不是当前已有功能：

```powershell
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll --appearance-only --out tests/LiteMonitor.PerformanceProbe/output/appearance-baseline
```

先在实际基线提交保存外观结果，再基于相同夹具复测改动。更新构建/引用后重新 build 才能用 `--no-build`。A06 的试验项目须有独立 README，明确哪些是截图观察、哪些是自动断言及安全退出方式。

## 8. 回滚、完成定义与派发模板

按“模型/颜色、可靠保存、探针、几何资源、装饰、静态背景、编辑器、Web、M1接线、实时服务、M2接线”组织可审查改动；提交遵循仓库实际规则，不因旧稿一句约定而执行 git add -A 或顺带提交用户文件。

新增 JSON 能力在旧程序上可能被忽略或保存丢失；回滚先停后台捕获、释放资源并恢复旧绘制/窗口轮廓。保留用户主题文件，不把重写旧主题或删除设置作为回滚步骤。

最终交付包含代码/测试、两个成品主题（M2未完成时明确只有机能主题）、前后截图、真实性能与资源数据、Windows/后端支持矩阵、失败降级及未验证范围。没有 A06/实机证据时不能声称实时玻璃跨 Win10/11 完成。

可直接给执行 agent：

> 请按 `docs/superpowers/plans/2026-10-20-appearance-agent-execution.md` 实施 LiteMonitor++ 外观改造，并将 `docs/APPEARANCE_PLAN.md` 作为设计背景。先做 A00，记录当前实际 HEAD/工作区并冻结合同；按表格依赖和独占写集分派，主 agent 统一接入共享文件。完成 M0、M1；M2 的真实后端必须先通过 A06，无条件继续所有不受该结果影响的工作。保留现有采样限频、Web需求、GPU多卡和用户改动；按计划运行对应测试及隔离实窗验证，提交可复核证据，不把历史测量、假后端或降级效果当成实时玻璃已完成。
