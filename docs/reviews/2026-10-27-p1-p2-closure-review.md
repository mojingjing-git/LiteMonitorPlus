# P1/P2 关闭复核

日期：2026-10-27（Asia/Shanghai）。对象：当前未提交工作区及 R5/p1-p2-closure.md，基线 HEAD 仍为 `4de25d38add7f8b2342303805fdf0b654c57fa55`。

**结论：不能认可“P1 全部关闭”。R07、R08 仍有可复现的 P1；R10 的真实出帧部分通过，但原超时线程回收缺口仍在。** R18 的脱敏和 R19 的实际导航生命周期也未完整闭合；另发现规则语义更新和首次 GPU 失败回退两个 P2。并未否定已正确完成的修复。

## 本轮验证

| 检查 | 结果 |
|---|---|
| 当前源码重新构建后的定向测试 | **105 通过 / 1 跳过 / 0 失败**；跳过的是默认关闭的真实 GPU 门禁 |
| 显式 `LITEMONITOR_GPU_TESTS=1` 单跑 R10 | **1 通过 / 0 跳过 / 0 失败** |
| 独立 GPU 探针，DPI=168 | 三次热更出帧 13→17→22；最小化保持 22；恢复 27，Active=true，Error=null，设备创建次数=1 |
| 原无效 GPU 主题刷新循环探针 | 1001ms 仅一次失败通知，窗口未被探针强制关闭，循环已消失 |
| 原 Web 拒绝路径 | 模块结果改为 Applied=false、Retryable=true，与实际拒绝一致 |
| 原 BOM、历史聚合、场景恢复/安全/高版本、事件身份、低帧率节能探针 | 对应修复通过，原始观察已归档 |
| 新边界探针 | 复现下述 C01/C02/C04/C05/C06/C07；C03 为测试体与线程所有权检查 |

独立构建输出为 `bin/review-20261027-closure/`；没有与其他硬件/testhost 会话并跑。本轮没有重跑全量和 200 次压力测试。交付日志中的 2751/8/0 与 44 warnings/0 errors 已查阅，属于实施方结果，不冒充本轮重新执行。

证据：[closure-review](../verification/execution/review-2026-10-27-closure/README.md)。只新增审查材料和 `.tmp` 探针，没有修改实现、实施方关闭矩阵、用户配置，也未提交或发布。

## 仍需处理的发现

### C01 [P1] R07：快照入口仍未正确处理源时间与身份缺失

位置：[AlertRuntime.cs:55](../../src/Core/Alerts/AlertRuntime.cs#L55)、[AlertRuntime.cs:58](../../src/Core/Alerts/AlertRuntime.cs#L58)。生产接线确已改为 HardwareSnapshot；原来完全伪造批次的问题得到改善，但持续时间仍不可信。

第一条路径：`observedAt = now - SampleAgeMs` 没有使用 TimestampMonoMs，也没有源采样身份去重。两批 `TimestampMonoMs=1000/2000`、Age=0/1000 指向同一个源时刻；若第二批在 UI 的 t6 被消费，就得到 observedAt=t5，触发持续五秒告警。探针中该源实际没有产生第二份采样，仍返回 Triggered/Notify。消费者排队时间被当成了越限时间。

第二条路径更直接：生产 PublishFailure 会生成 `StableDeviceId=null` 的 Missing 样本。新入口把它归入 `deviceId="default"`，而原设备 cpu-A 的 Pending/Active 状态没有收到缺失。探针 t0 cpu-A=90 → t1 Missing/null-id → t6 cpu-A=90，Missing 只产生 default 的 InputUnavailable；恢复后 cpu-A 立刻触发持续五秒告警，把失败区间算进了持续时间。

建议：用同一会话的发布单调时间和实际源时间建立时间映射，按每个指标的源采样身份去重；读取同一缓存不能刷新时效。缺失时应使原绑定流进入不可用状态，不能另建 default 状态而保活原状态。补非零 TriggerDuration 的缓存/排队、PublishFailure/null-id 与重新出现测试。现有 R07 测试多用零持续时间，未验证这两条路径。

### C02 [P1] R08：以显示名判断提交并恢复，仍会错报同名修订失败

位置：[AppActions.cs:333](../../src/Core/Actions/AppActions.cs#L333)、[AppActions.cs:352](../../src/Core/Actions/AppActions.cs#L352)、[AppActions.cs:359](../../src/Core/Actions/AppActions.cs#L359)。

`committed` 仅比较新旧 Theme.Name；同一主题文件修改参数、两个文件同名展示时，运行时对象与内容已变化，Name 仍相同，因此返回 Failed，而不是 Partial，也不会尝试恢复。Launcher 的 Failed 分支依旧只回滚 Skin 并声称旧主题有效。

独立受控故障探针先加载显示名相同的 v1，再把文件改成 v2；在 ThemeManager.Load 后、几何步骤前注入异常。观察为 `currentChanged=true, sourceChanged=true, loadSucceeded=true, currentVersion=2`，结果却是 `Failed/theme-runtime-failed`。探针使用未初始化窗体依赖触发该固定异常点，不构造硬件；它验证的是提交判定分支，**不冒充完整生产 GUI 故障恢复矩阵**。真实 ApplyTheme 在加载之后仍有窗口、字体、外观和事件步骤可能失败。

另一个身份错误是恢复时 `ui.ApplyTheme(previousTheme)` 中 previousTheme 来自显示名，不是旧文件标识。例如仓库 `CyberPunk_GPU.json` 的 name 是 `Cyberpunk GPU`；恢复会尝试不存在的 `Cyberpunk GPU.json`。仅比较恢复后的 Name 也没有确认 LastLoad.Success 和旧修订。

建议：在进入操作前保留实际来源文件身份、摘要/修订及必要的旧配置快照；明确运行时提交点，以来源身份和修订确认恢复。不要用显示名承担唯一标识，不要从同一已被覆盖的文件推断能恢复旧版本。

### C03 [P1] R10：新增真实出帧门禁有效，长压力线程仍没有回收边界

位置：[ThemeStudioResourceTests.cs:439](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs#L439)、[R10GpuFrameGateTests.cs:131](../../tests/LiteMonitor.Tests/R10GpuFrameGateTests.cs#L131)。

本轮已亲自运行新增的真实 GPU 门禁；首帧、三次目标切换后的新帧和设备复用断言有效。这个进步可以确认。

但原 R10 还包括压力测试超时后的线程存活问题。200 次资源测试仍创建前台 STA，Join 十分钟后只 throw，不停止线程；进程会被仍运行的线程保活。新 R10 把自己的 STA 改为 background，只能改善进程退出，不会回收超时后仍在执行的 GPU 操作，也不能阻止它与后续测试争用全局设置/主题目录。200 次资源测试自身也仍可能在 GPU 开关关闭时测到纯回退资源；三个周期的独立出帧门禁不等价于验证该压力场景实际走过 GPU。

本轮没有故意卡死线程；这项依据当前完整测试体审查，不能用顺利执行的一次 1/1 来证明超时清理。

建议：长压力任务放到可终止的独立进程，外部超时确认子进程退出后才恢复全局状态；压力场景在受支持 GPU 环境应证明实际设备/出帧。关闭矩阵应将“出帧通过”与“压力隔离完成”分开记录。

### C04 [P2] R14 修复引入：规则真正变化后也沿用旧事件计时

位置：[AlertRuntime.cs:75](../../src/Core/Alerts/AlertRuntime.cs#L75)。

生产入口的 ApplyRuleSetChange 现在只更新列表引用，完全不比较规则内容或清理对应状态。它修好了“等价新列表清空冷却”的原 R14，但注释中所说“语义改变才重置”没有实现。

探针规则阈值 80、持续五秒，t0=90 进入 Pending；t6 把同一 RuleId 阈值改到 100，再给第一份 110 样本，立即 Triggered/Notify。新阈值只在这一份样本上满足，却继承了旧阈值下的六秒。设置页重建迁移规则、禁用再启用也会触达同类状态残留。

建议：按 RuleId 和定义内容/修订计算语义变化；仅重置变化规则的 Pending/恢复时长，同时明确定义冷却和已激活事件的保留策略。等价集合应保留状态，内容变化不能无条件保留。

### C05 [P2] R18：IPv6 和其他路径仍可进入诊断包

位置：[DiagnosticRedaction.cs:14](../../src/Diagnostics/DiagnosticRedaction.cs#L14)、[DiagnosticRedaction.cs:20](../../src/Diagnostics/DiagnosticRedaction.cs#L20)、[DiagnosticPackageService.cs:154](../../src/Diagnostics/DiagnosticPackageService.cs#L154)。

新增 Regex 能挡住此前的部分样例，但 IpAddress 只有 IPv4；Windows 路径要求前方是有限分隔符；Unix 根目录也只枚举少数名称。探针 `IsSafeValue` 对 `2001:db8::1234`、`failed:G:\Users\Alice\private.json`、`/srv/private/alice/data.json` 全部返回 true。前两者放入 `module.*` 后，真实导出 ZIP 仍原样包含。

因此关闭矩阵的“IPv4/IPv6 均覆盖”与代码不符。模块任意字符串与原始设备身份仍没有转为类型化投影/本包伪名。所有值均为合成材料，没有用户信息被读取或上传。

建议：落实原 R18 的受控字段与类型投影，原始错误文本不默认导出；不要靠不断扩展关键字/路径黑名单证明隐私边界。若保留 IP 检测，应覆盖真实 IPv6 及不同上下文，但这只能是补充。

### C06 [P2] R19：真实导航是摘下页面，并不会触发隐藏事件

位置：[DiagnosticPage.cs:129](../../src/UI/Settings/DiagnosticPage.cs#L129)、[SettingsForm.cs:270](../../src/UI/SettingsForm.cs#L270)。

详情选择现在能跨刷新保持，探针 TextLength 120→120，原交互缺陷已修好。但 SettingsForm 切页使用 Controls.Clear，把旧页面 Parent 设为空，没有调用 Hide。脱离容器的 UserControl.Visible 仍可能为 true，OnVisibleChanged 无法作为离页通知。

真实 WinForms 探针按相同顺序摘下页面，结果 `Parent=null, Visible=true, Timer.Enabled=true`，1200ms 内 Provider 读取次数 3→5。因此页面不在用户眼前时仍持续构造快照和重建列表，原 R19 的资源部分未闭合。

建议：导航显式通知离页/激活，或同时根据 Parent、容器激活状态管理订阅；先停止旧页，再摘下，进入时恢复。补实际 Controls.Clear 导航测试，不能只测 page.Hide。

### C07 [P2] 首次 GPU 初始化失败，竖向 GDI 仍用主效果原始配色

位置：[ThemePreviewControl.cs:295](../../src/ThemeEditor/ThemePreviewControl.cs#L295)、[ThemePreviewControl.cs:603](../../src/ThemeEditor/ThemePreviewControl.cs#L603)。这是前次 F04 尚未覆盖的初始化/运行失败路径，不否定 R02 的闭环修复。

SetTheme 在开关开启时先选 raw theme；首次构造 GPU 控件失败后，OnPaint 只隐藏子控件并通知宿主，不调用 UseGdiFallbackTheme。R02 将宿主行为改为仅切画布后，后续也不会靠重新 SetTheme 偶然补上派生配色。

探针主色 `#112233`、fallbackStyle 背景 `#AABBCC`，只调用一次 SetTheme 并处理消息；GPU 不可用后，真实 RenderedTheme 背景仍为 `#112233`。无效材质是可稳定构造的首次失败输入；同一分支也处理设备初始化失败。

建议：控件内部在首次/运行期失败时一次性切换到同一修订的 fallback 派生主题，再只通知形态/状态变化；不重新启动 GPU，也不恢复失败刷新循环。

## 原编号复核矩阵

“通过”只代表本轮对应修复和已执行的复现路径，不代签完整 G1。

| 编号 | 本轮判断 | 依据 |
|---|---|---|
| R01 | 通过 | 真实最小化/恢复继续出帧，复用一个设备 |
| R02 | 通过 | 原无输入刷新循环消失；首次失败配色另列 C07 |
| R03 | 通过 | UTF-8 BOM、UTF-16 BOM 均解析成功，原始字节摘要一致 |
| R04 | 通过 | v1 Avg=65/Max=99/Min=30/Count=6 导入 v2 后完整保留；读写代码相符 |
| R05 | 通过 | 损坏主库后连续读均为同一备份集合，不再第二次变空 |
| R06 | 通过 | 原穿透/隐藏托盘组合被 interactive-entry 阻塞 |
| R07 | **不关闭** | C01，缺失身份与源时间仍错误 |
| R08 | **不关闭** | C02，同名修订提交判定与恢复身份仍错误 |
| R09 | 通过 | 原服务安全拒绝返回失败模块 |
| R10 | **部分通过，不能整体关闭** | 真 GPU 出帧通过；C03 压力隔离/超时回收未完成 |
| R11 | 继续开放 | 本轮未将秒桶、缺口和源时间去重算作已实现 |
| R12 | 继续开放 | 长期身份与质量仍缺失 |
| R13 | 通过 | 高版本原文件保留、IsReadOnly=true、保存被拒绝 |
| R14 | 原问题通过，新增 C04 | 等价规则集合不再重置生产 evaluator；真实定义变化也不重置，须另修 |
| R15 | 通过原 gap 路径 | Active/Recovering 的 gap 保护已补；不代替完整会话矩阵 |
| R16 | 通过本轮增量规则范围 | Load 和数量回调均接 SyncGpuRules；保留用户规则的测试通过 |
| R17 | 通过 | 两个规则/设备事件保持两条，身份字段未再全被过滤 |
| R18 | **不关闭** | C05，实际 ZIP 仍泄露合成 IPv6/路径 |
| R19 | **部分通过，不能整体关闭** | 详情保留通过；C06 实际摘页后仍轮询 |
| R20 | 通过 | normalFps=5 时显示/动画均保持 5，不被节能模式提速 |

本轮可确认关闭 13 个原编号；5 个申请关闭项需要保留或修正状态（R07/R08/R10/R18/R19）。R11/R12 是此前明确开放的两项，另有 C04/C07 两个需要处理的行为问题。

## 下一步建议

先修 C01/C02 的生产正确性，再补 C03 的测试进程边界。并行处理 C04–C07，将本次合成探针变成断言正确行为的正式测试；测试重点是“第一份样本”“同名新修订”“页面真实离开”“首次失败”，避免只验证修复后的某一中间 bool。

G1、场景/低功耗生产接线及 S06/P04–P07 继续保持未完成。实施方对此的保留是正确的，无需因为本次复核另起产品范围。
