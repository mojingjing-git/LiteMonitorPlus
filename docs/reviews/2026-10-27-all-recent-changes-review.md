# 2026-10-27 最近全部改动独立审查

## 结论与范围

**尚不建议将本批标为完整验收通过。确认 4 项 P1、7 项 P2；编译通过不能替代 CI 脚本和真实交互路径验收。**

- 比较基线：上次独立验收记录的 `42b6b61ffa347261a69077418c25aac663061fe4`。
- 本次 HEAD：`4de25d38add7f8b2342303805fdf0b654c57fa55`。
- 覆盖基线至 HEAD 的 118 个变更文件，以及审查开始时的 9 个文档删除、未跟踪的 `resources/plugins/vps_traffic.json`。涵盖可空引用治理、CI、主题工作室整改、三款 GPU 主题、测试和验收材料。
- 业务代码未修改；本次只新增审查报告、证据和被忽略的临时探针。未提交、撤回或清理其他人的改动。
- 文件摘要见 [source-manifest.json](../verification/theme-studio/review-2026-10-27/source-manifest.json)。`null` 表示工作区内已删除。

## 问题总表

| 编号 | 优先级 | 问题 | 证据性质 |
|---|---|---|---|
| F01 | P1 | 新 CI 门禁漏续行符，构建参数变成独立命令 | PowerShell AST 与无副作用执行复现 |
| F02 | P1 | 应用结果仍以请求的 Skin 充当实际主题，异常回滚也未恢复运行时 | 生产调用链静态确认 |
| F11 | P1 | GPU 热更新只推进预览代次，下一真实帧因代次不匹配失败 | 真实 GPU 与 175% DPI 路径复现 |
| F03 | P1 | GPU 资源门禁仍允许完全没有 GPU 会话的执行通过 | 测试代码与相同真实切换路径探针 |
| F04 | P2 | 主效果自动降级时未套用 fallbackStyle | 实际窗体路径复现 |
| F05 | P2 | GPU 不可用时，横向画布退成竖向布局 | 实际窗体路径、截图复现 |
| F06 | P2 | 任务栏预览未消费当前设置，展示默认颜色和双行布局 | 实际窗体路径复现 |
| F07 | P2 | 最小化窗口后仍持续 GPU 出帧 | 真实 GPU 帧计数复现 |
| F08 | P2 | GPU 首次创建与热更新使用不同 DPI 布局口径 | 真实 175% DPI 路径复现 |
| F09 | P2 | 任务栏形态导出仍静默输出竖向主面板 | UI 至导出服务调用链确认 |
| F10 | P2 | 新 VPS 插件的真实令牌头没有进入凭据去向校验 | 纯本地策略调用复现，无网络请求 |

## F01 — P1：修正 CI 参数的续行

定位：[ci.yml:434](../../.github/workflows/ci.yml#L434)。

`-p:WarningsNotAsErrors=$legacy` 末尾没有反引号，下一行的 `-p:NuGetAudit=false` 因此是第二条命令。PowerShell AST 实际解析出两个语句；把 `dotnet` 替换为不构建的本地函数后执行，第二条语句仍报找不到 `-p:` 命令。

结果是新增的 `nullable-gate` 门禁 2 会失败，即使解决方案没有新警告。本机将参数写在同一条命令的构建通过，不能证明仓库中的 YAML 脚本能通过。

**修复建议**：补续行符或改用参数数组。直接提取 YAML 的原始 `run` 内容验证，避免验证命令和提交命令各维护一份。

## F02 — P1：应用结果必须反映真正的运行状态

定位：[ThemeStudioLauncher.cs:91](../../src/ThemeEditor/ThemeStudioLauncher.cs#L91)、[ThemeStudioLauncher.cs:104](../../src/ThemeEditor/ThemeStudioLauncher.cs#L104)。关联：[ThemeManager.Load](../../src/Core/ThemeManager.cs#L531)、[AppActions.ApplyThemeAndLayout](../../src/Core/Actions/AppActions.cs#L219)。

R2 的持久化失败已修好，但 R3 的“实际生效主题”仍没有取得运行时证据：

1. `Success=true` 和 `EffectiveTheme=_cfg.Skin` 只证明请求的名字还在配置中。主题编辑后未继续修改、磁盘文件被外部删除，再点应用时，`ThemeManager.Load` 会捕获缺文件/解析错误并把 `Current` 换成默认主题，不会向网关抛错；网关仍报告请求主题已应用。
2. 异常分支仅恢复 `Skin` 并再次保存，直接宣称 `EffectiveTheme=previousSkin`。但编排先调用 `ui.ApplyTheme`，再重建菜单和任务栏；如果后续操作失败，新主题已经进入 `ThemeManager.Current`，配置回滚并不等于运行时回滚。

`ResolveEffectiveRoute` 又重新读取请求文件，没有读取刚生效的主题对象，无法消除上述不一致。这里确认的是生产代码路径；本次没有借助伪造的运行时宿主声称已完成完整应用故障实测。

**修复建议**：由加载/应用编排返回实际主题身份、加载是否降级和实际路线。部分成功要如实报告；若承诺回滚，必须执行并验证运行时回滚。增加“文件失踪/坏 JSON”“主题切换后任务栏或菜单阶段失败”两类回归。

## F11 — P1：GPU 热更新之后，下一帧就因代次不匹配失败

定位：[GpuThemePreviewControl.cs:226](../../src/ThemeEditor/GpuThemePreviewControl.cs#L226)。关联：[GpuSceneRenderer.Render:555](../../src/Rendering/Presentation/GpuSceneRenderer.cs#L555)。

预览热更新调用 `renderer.UpdateConfiguration` 后，自行把 `_generations.Theme` 加一；该渲染器方法只更新材质、字体和运行参数，没有更新渲染器持有的 `_generations`。下一次 `Render` 检查 `CanAccept` 时必然拒绝新代次，`OnTick` 捕获错误后执行 `Fail` 并释放渲染器。

以实际显示器 DPI=168（175%）、合法 cyber_punk 主题实测，**不修改主题，原样调用第二次 `SetTheme`**：

```text
首次：IsActive=true
热更新同步返回：IsActive=true，Error=null，CurrentGeneration=2
渲染器仍持有：Theme=1 / Window=1 / Size=1 / Topology=1 / Device=1
泵送约 120ms 后：IsActive=false
Error="The scene generation does not match the renderer session."
DeviceCreationCount=1，SessionUpdateCount=1
```

这解释了为什么读取热更新返回值或立即检查字段会通过，而用户真正编辑/切换目标后的下一帧会退出 GPU。代次缺口与 DPI 数值无关，175% 是本次实测环境；DPI 布局口径本身另见 F08。

**修复建议**：让预览、场景、渲染器和相关资源在一次提交中接受同一代次；提供明确的会话代次更新合同。不要通过删除 `CanAccept` 校验掩盖，也不能假设同尺寸 `Resize` 会同步（它会提前返回）。回归必须等待热更新后至少一帧成功呈现，并确认没有销毁重建设备。

## F03 — P1：资源门禁必须确认 GPU 真的启动并出帧

定位：[ThemeStudioResourceTests.cs:374](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs#L374)、[ThemeStudioResourceTests.cs:385](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs#L385)。

S4 改成 GPU 主题和真实下拉是正确的，但仍未设置 `GpuThemeEffectsEnabled=true`，也未断言 `IsGpuPreviewActive`、设备创建数、已呈现帧或 GPU 资源账本。该开关默认是 `false`，生产预览在开关关闭时根本不创建 GPU 子控件。

独立探针用 GPU 主题、真实窗体和同样的 200 次 `SelectedIndex` 切换，记录：

```text
iterations=200
effectsEnabled=false
activeCount=0
deviceCreations=0
```

这不是宣称探针执行了测试类本身，而是证明该测试依赖的相同交互路径可完全不经过 GPU；现有断言没有检查这个前提。即使开关开启，启动失败后降级也同样不会被识别为未覆盖。

**修复建议**：用隔离配置显式开启 GPU，确认成功呈现首帧，逐次核对实际目标和会话状态；无 GPU 条件应明确记为未验证/受限，不能签发 GPU 资源稳定性通过。斜率检查之外补充 GPU ledger 和关闭后的归还检查。

另外，`RunSta` 的两分钟 `Join` 返回值未检查（`:446`）。超时后可能继续采样、恢复全局测试目录，而原线程还在运行。应在超时处明确失败并确保不遗留后台 UI 操作。

## F04 — P2：自动降级也必须应用兼容外观

定位：[ThemePreviewControl.cs:154](../../src/ThemeEditor/ThemePreviewControl.cs#L154)、[ThemePreviewControl.cs:214](../../src/ThemeEditor/ThemePreviewControl.cs#L214)。

目前只有显式选择 `GpuFallback` 才构造派生主题。用户停在主效果目标，但关闭 GPU、进入安全模式或 GPU 初始化失败时，只隐藏 GPU 子控件，GDI 绘制继续使用主效果的原始 `Color`。

实测：主效果背景 `#112233`、兼容背景 `#AABBCC`、GPU 关闭。状态栏显示“GPU 不可用 → 兼容外观”，实际 `RenderedTheme.Color.Background` 却仍是 `#112233`。因此显式兼容预览、自动降级预览和导出图仍可能三者不一致。

**修复建议**：编辑目标和实际绘制路线分别建模；实际落到 GPU 主题的 legacy 路线时，统一通过回退派生逻辑构造绘制主题。

## F05 — P2：GPU 横向预览失败后必须保持横向形态

定位：[ThemeStudioForm.cs:1113](../../src/ThemeEditor/ThemeStudioForm.cs#L1113)、[ThemePreviewControl.OnPaint:571](../../src/ThemeEditor/ThemePreviewControl.cs#L571)。

工作室按“主题声明 GPU + 编辑目标为主效果”选择 `_preview` 的 GPU 横向能力，没有按 GPU 是否可用决定 GDI 兜底。该控件的 GDI 绘制始终使用 `UILayout`，没有按 `_shape` 选择横向布局。

关闭 GPU、选横向后，状态显示“横向 · 常规绘制（生产同源）”，探针却记录 `_horizontalPreview` 不存在、实际布局为 `UILayout`。截图显示竖向 CPU/GPU/MEM 分组，与横向声明不符。

**修复建议**：GPU 不可用或运行中失败时转到同源横向 GDI 渲染器，保留当前形态；测试必须覆盖“正常 GPU → 故障 → 横向兼容”以及初始不可用。

证据：[automatic-fallback-horizontal.png](../verification/theme-studio/review-2026-10-27/automatic-fallback-horizontal.png)。

## F06 — P2：把当前设置传入任务栏预览

定位：[ThemeStudioForm.cs:1142](../../src/ThemeEditor/ThemeStudioForm.cs#L1142)、[ThemePreviewViews.cs:368](../../src/ThemeEditor/ThemePreviewViews.cs#L368)。

`TaskbarPreviewControl.ApplySettings` 和独立样式上下文已经实现，但工作室只调用 `SetTheme`；构造时传入的 `_appSettings` 没有接到该预览。用户看到的是 `new Settings()` 的默认值。

探针向工作室传入“自定义颜色开启、标签色 `#123456`、单行”，实际预览设置仍为“自定义颜色关闭、标签色 `#141414`、双行”。这影响颜色、字体、间距和行模式，不只是状态文案。

**修复建议**：在创建和刷新任务栏预览时，传入当前设置的隔离快照，并同步实际明暗状态。保留本批已经修好的生产静态样式隔离。

## F07 — P2：最小化不等于 Visible=false

定位：[GpuThemePreviewControl.cs:476](../../src/ThemeEditor/GpuThemePreviewControl.cs#L476)。

新可见性判断确实修复了 `Visible=false` 时继续出帧的问题；但最小化的 WinForms 窗口和子控件通常仍返回 `Visible=true`。

真实 GPU 探针把宿主设为 `Minimized`，记录父子均 `Visible=true`、GPU 活跃；约 500ms 内呈现帧数从 8 增至 21，即后台继续出了 13 帧。

**修复建议**：把宿主 `WindowState` 纳入呈现条件，最小化暂停调度，恢复时重置调度时间基准。用真实最小化/恢复事件和帧计数回归，不只调用 `Hide()`。

## F08 — P2：GPU 热更新必须保留 DPI 预览副本

定位：[ThemePreviewControl.cs:235](../../src/ThemeEditor/ThemePreviewControl.cs#L235)、[ThemePreviewControl.cs:252](../../src/ThemeEditor/ThemePreviewControl.cs#L252)。

首次创建 GPU 子控件传入 `GetPreviewTheme(GetDpiScale())`，它对 `Layout` 创建 DPI 缩放副本；后续复用会话却把未缩放的 `_rawTheme` 直接传给 `UpdateTheme`。GPU 侧随后使用该主题重算 `UILayout` 和 `LayoutScale / dpiScale`。

真实 DPI=168（175%）下，原样刷新同一主题：首次 `LayoutScale=1.75 / RowHeight=70`，热更新后变为 `LayoutScale=1 / RowHeight=40`。因此即便修好 F11 的代次错误，预览仍会因为一次编辑或刷新改变尺寸。当前 DPI 回归只检查工作室分栏宽度和资料库行高，测不到这条路径。

**修复建议**：首次创建和热更新都使用同一种按当前 DPI 生成的预览快照，并在真实 DPI 变化后刷新 GPU 场景。加入“非 96 DPI，原样刷新不改变几何”的回归。

## F09 — P2：任务栏导出应明确支持或明确拒绝

定位：[ThemeStudioForm.cs:982](../../src/ThemeEditor/ThemeStudioForm.cs#L982)。

导出方向只区分 `Horizontal` 和其他；`Taskbar` 落入 `Vertical`。当用户正在任务栏画布点击“导出效果图”，得到的仍是竖向主面板，成功提示没有披露形态变化。横向主面板导出尺寸已修复，但上次 R5 的任务栏部分仍在。

**修复建议**：增加独立任务栏导出路线并传入隔离样式快照；如果本批不支持，禁用该形态的导出入口并说明原因，不能静默替换成另一种产物。

## F10 — P2：新 VPS 插件不能绕过凭据目标检查

定位：[vps_traffic.json:50](../../resources/plugins/vps_traffic.json#L50)、[vps_traffic.json:53](../../resources/plugins/vps_traffic.json#L53)。此文件在审查开始时未跟踪。

模板使用动态主机 `https://{{host}}/api` 和全局令牌 `X-API-Token: {{token}}`。动态主机不构成插件字面量声明，`GetDeclaredHosts` 返回空集合；而 `X-API-Token` 不在现有 `CredentialHeaderNames` 中，故该真实秘密被当作普通请求头。

纯本地验证：以虚构令牌调用实际 `EnsureCredentialTargetAllowed`，向未声明的 `https://unlisted.example/api` 仍被允许；没有向该地址发送请求，也没有读取真实令牌。

**修复建议**：把该令牌头纳入真实凭据分类，并同时解决用户自建主机的显式授权/声明合同。仅增加头名会让当前动态主机模板全部被拒，不能靠换一个未识别头名维持可用。补充跨目标实例、重定向和未授权主机的凭据去向回归。

## 已确认的改善与交付边界

- 原验收探针重新运行：目标下拉能刷新画布；阻止持久化时停在 `SettingsPersisted` 并恢复内存 Skin；显式回退导出底色正确；GPU 横向导出为 400×120；非法 GPU 更新有错误且旧帧不再被当作有效预览；隐藏后出帧为 0；调度器 FPS 字段从 30 更新到 2；焦点落在 TextBox；暗色工作区进入既有色板。以上即时观测不能证明热更新后持续成功出帧，F11 是本次补做跨帧观察发现的缺陷。
- 任务栏预览的样式上下文隔离是实质改善，未发现该改动继续写入生产静态样式。
- 可空治理的大部分变更是声明、局部快照和显式守卫；保留了单位 `null` 与空字符串、数值项 `TextValue=null` 等关键语义。本次未确认其生产行为回归。新增 CI 脚本本身的问题见 F01。
- 三款 GPU 主题的结构/验证/往返测试已纳入本次专项；本次没有完成逐主题视觉效果和全部硬件矩阵签收。
- 鼠标命中接线仍不存在，但当前整改计划已经明确把 C-1b 移至 Phase F，本报告不将已披露的范围调整再计为新缺陷；它也不能继续被描述为已交付的画布点选。
- N08 的未测 DPI/辅助功能行降级是正确的。M-01/M-02 中“点选坐标逆变换精确/无偏移”的描述仍应移除或另附真实命中证据，因为 Phase F 尚未实现。
- N09 的冷启动约 80/60/70/110 句柄分项仍缺少各类型快照或跟踪文件。稳定平台只能支持“未观察到持续增长”，不能证明这些具体归属。
- 工作区删除的文档造成至少 5 处现存 Markdown 链接失效，涉及架构拆分、硬件加固和 dispatch 索引。迁移/删除文档时同步更新引用；本次未恢复这些文件。

## 独立验证记录

1. Debug 验收探针构建：0 警告、0 错误；真实 GPU 初始化和快照成功。
2. 主题工作室（不含资源）、旗舰主题、插件、WebSession 和格式化相关专项：**448 总计，447 通过、1 跳过、0 失败**。该数字不是全量结果。
3. 修正为一条命令的 Release 解决方案警告门禁：退出码 0，44 条既有测试警告、0 错误；没有以此代替 F01 的原始脚本验证。
4. 主工程全量：**2633 总计，2626 通过、7 跳过、0 失败**，约 7 分钟。跳过为 6 项需要插件仓库夹具的 MarketIndexContract 和 1 项联网 MarketE2E；本次未设置对应环境条件，不能冒充完整市场门禁通过。
5. 更新器：**73/73 通过**；GPU 非性能工程：**19/19 通过**。三个程序集合计 **2725 项，2718 通过、7 跳过、0 失败**。专项测试与全量有重叠，不相加。
6. 实际应用/路线的 F02 是静态控制流结论；F08/F11 有真实 175% DPI 控件与 GPU 观测。真实混合 DPI、多显示器拔插和完整应用故障矩阵没有据此冒充已测。

证据目录：[review-2026-10-27](../verification/theme-studio/review-2026-10-27/)。原始探针结果、扩展结果、175% DPI 跨帧结果、截图、探针源码文本、测试摘要和文件摘要已写入仓库目录；构建产物、TRX 及运行日志保留在被忽略的 `.tmp/review-20261027/`。

## 下一步建议

优先修 F11，让 GPU 热更新后确实能继续出帧，同时修 F01，让提交的门禁本身可执行；然后收口 F02 的实际应用结果与 F03 的 GPU 前提验证。主题预览把“编辑目标、实际路线、形态、DPI、当前设置”作为一份一致的输入处理，集中修 F04–F09，避免各分支继续单独补文案。VPS 插件在发布前完成 F10 的授权合同与凭据去向测试。

本轮不宜直接进入前后对比、质量检查和批量样式等优化实施；应先完成以上回归修复，再将意向计划中的 O02/O03 细化为下一批。
