# 修复包与四项功能实施审查

日期：2026-10-27（Asia/Shanghai）。结论：**本轮暂不通过发布验收**。

审查对象是 `4de25d38add7f8b2342303805fdf0b654c57fa55` 之后的当前工作区，包括未跟踪的新源码和测试；不是只审 HEAD。对照修复计划、四项产品计划和 execution-plan v2.1。原有九份文档删除不作为本轮新增缺陷，未恢复或提交任何文件。

发现 **10 项 P1、10 项 P2**。其中 F02、F03 为上一轮没有真正闭合的问题；GPU 最小化恢复是 F07 验收路径新发现的同类代次缺陷。其余按下文标注区分新回归、实现缺陷和计划接线缺口。测试通过不能替代真实出帧、迁移前后数据一致性与完整用户路径。

## 验证与证据

| 验证 | 本轮结果 |
|---|---|
| 新功能/合同/修复定向测试 | 123 通过 / 0 失败 / 0 跳过 |
| 主题工作室、渲染合同、捕获清理、Web、插件定向测试 | 97 通过 / 0 失败 / 0 跳过 |
| 上述合计 | **220 通过 / 0 失败 / 0 跳过**；不包含人工探针失败场景 |
| 主项目及独立探针 Release 构建 | 成功；最终探针构建 0 警告 / 0 错误 |
| 真实 GPU，PerMonitorV2，DPI=168 | 连续 3 次热更新正常出帧，设备创建次数始终为 1；最小化停帧；恢复失败 |
| 无效主题真实工作室 | 无用户输入，68ms 内连续 5 次失败刷新回调；探针主动关闭窗口中止循环 |
| 纯行为探针 | 复现 BOM、历史迁移、场景恢复/版本/交互入口、告警计时/重置/恢复、多卡规则、诊断合并/脱敏、节能上限问题 |

本轮没有重跑全量约七分钟的硬件测试或 200 次 GPU 资源压力测试，也没有把历史全量结果计为当前快照通过。运行前检查了测试进程，没有并行运行硬件测试；结束后未遗留探针/testhost。未读取或发送真实凭据，Web 拒绝路径探针没有建立对外连接。

证据目录：[review-2026-10-27-followup](../verification/execution/review-2026-10-27-followup/README.md)。包含源文件哈希、可复现探针源码、原始观察、测试摘要。临时工程、TRX 和完整构建日志在 `.tmp/review-20261027-followup/`，业务代码零修改。

## P1

### R01：GPU 预览最小化后恢复，仍会发生代次拒帧

位置：[GpuThemePreviewControl.cs:368](../../src/ThemeEditor/GpuThemePreviewControl.cs#L368)、[GpuSceneRenderer.cs:697](../../src/Rendering/Presentation/GpuSceneRenderer.cs#L697)。关联 F07，并非 F11 热更新修复失效。

`ResizeRenderer` 先递增 Size 代次，但渲染器遇到同尺寸会直接返回、不同步代次。最小化恢复产生同尺寸布局事件后，场景与渲染会话不再一致。真实 GPU 探针连续热更新帧数 12→17→22，最小化仍为 22；恢复后 `IsActive=false`，错误为 `The scene generation does not match the renderer session.`，呈现计数读回 0 是渲染器被销毁，不是成功恢复。设备创建计数仍为 1。

建议：明确几何变化和代次变化的协议；同尺寸事件不制造单边代次，或允许渲染器安全同步。保留拒帧保护，补最小化/还原、同尺寸布局、跨屏 DPI 后持续出帧测试。

### R02：GPU 不可用通知触发自身重试，形成无输入刷新循环

位置：[ThemeStudioForm.cs:1214](../../src/ThemeEditor/ThemeStudioForm.cs#L1214)、[ThemePreviewControl.cs:230](../../src/ThemeEditor/ThemePreviewControl.cs#L230)。本批新增回归。

`UseGdiFallbackTheme → NotifyGpuUnavailable → OnGpuPreviewUnavailable → RefreshPreviewNow → SetTheme → UpdateTheme失败 → UseGdiFallbackTheme` 构成闭环。每次 `SetTheme` 又从 raw theme 开始，因此失败状态无法稳定停在回退路线。真实工作室加载无效 GPU material 后，未操作也在 68ms 内产生 5 次回调；探针在第五次主动关闭，避免继续占用消息循环。合法主题但 GPU 初始化持续失败也有同样路径。

建议：失败通知只切换现有画布的实际路线；以文档修订/会话代次去重。重新尝试应由参数变化或显式重试触发。补无输入消息循环稳定性测试，并保证失败首次回退就使用 fallbackStyle。

### R03：新主题读取方式破坏 UTF-8 BOM 兼容性

位置：[ThemeManager.cs:562](../../src/Core/ThemeManager.cs#L562)。本批新增回归。

旧 `File.ReadAllText` 会识别 BOM；新 `Encoding.UTF8.GetString(File.ReadAllBytes(...))` 保留开头 U+FEFF，随后字符串 JSON 解析失败。探针同一文件旧读取成功，新 `Inspect.Success=false`，报 `'0xEF' is an invalid start of a value`。生产 `Load` 随后会静默套默认主题，工作室预检则拒绝保存应用；UTF-16 BOM 旧文件也受此次解码变更影响。

建议：摘要按原始字节计算，文本解码继续使用识别 BOM 的读取器；补 BOM/无 BOM 与既有编码兼容测试。

### R04：历史 v2 只保存平均值，重启/迁移后峰值、最低值和计数丢失

位置：[HardwareHistoryLogger.cs:312](../../src/Core/HardwareHistoryLogger.cs#L312)、[HardwareHistoryLogger.cs:120](../../src/Core/HardwareHistoryLogger.cs#L120)、[TrendHistoryV2Store.cs:184](../../src/Core/Trends/TrendHistoryV2Store.cs#L184)。本批持久化回归。

v1 以及内存桶拥有 Avg/Max/Min/Count；v2 点只有单个 Value，导入和正常写入都只取 Avg，读取再重建为 Max=Min=Avg、Count=1。探针旧桶 `Avg=65, Min=30, Max=99, Count=6` 迁移后仅剩 `Value=65`。当前趋势表和 CSV 仍展示最高/最低/样本数，因此同一历史在重启后显示不同，后续 v2 数据没有原始峰值可供恢复。

建议：在停止旧格式写入前保存完整分钟聚合和来源信息；读取保持统计语义，补“写入→重启读取→图表/导出”一致性测试。已写出的均值不能被宣称可逆还原峰值。

### R05：场景库备份恢复只对第一次读取有效

位置：[ScenarioProfileStore.cs:36](../../src/Core/Scenarios/ScenarioProfileStore.cs#L36)、[ScenarioProfileStore.cs:48](../../src/Core/Scenarios/ScenarioProfileStore.cs#L48)。新增存储缺陷。

损坏主库被移走后，本次返回备份，但没有恢复主文件。下一次 `LoadAll` 因主文件不存在直接返回空集合，完全不再检查备份。探针连续读取结果 `[A] → []`。场景列表刷新、Get、Save 内部都会再次读取，随后新增保存可能从空库开始，丢掉恢复出的场景。

建议：从备份原子恢复有效主库，或在主库缺失时仍提供可靠备份读取；读写应使用同一恢复状态。补连续读、Get、恢复后编辑/新增和重启测试。

### R06：场景应用绕过“至少保留一个可交互入口”的保护

位置：[ScenarioApplyCoordinator.cs:79](../../src/Core/Scenarios/ScenarioApplyCoordinator.cs#L79)、[SettingsForm.cs:338](../../src/UI/SettingsForm.cs#L338)、[AppActions.cs:247](../../src/Core/Actions/AppActions.cs#L247)。新增用户路径缺陷。

一般设置保存会检测主面板/任务栏的隐藏或鼠标穿透，以及隐藏托盘的组合；场景应用直接保存候选并调用模块，没有同一保护。排除 HideTrayIcon 不能保证安全，因为当前配置可以已经隐藏托盘。探针：当前两窗口可交互且托盘隐藏；场景让两窗口都穿透，`Preflight.CanApply=true`，投影后 `noInteractiveEntry=true`。`ApplyVisibility` 的旧保护只处理两个窗口都隐藏，不处理穿透，关闭设置窗后用户失去通常的操作入口。

建议：把可交互入口检查提升到所有配置写入共用的预检，在保存前阻止或明确修正危险组合；修正结果必须反映到预览和最终保存值。

### R07：告警生产接线伪造新批次和新采样时间

位置：[AlertRuntime.cs:35](../../src/Core/Alerts/AlertRuntime.cs#L35)、[UIController.cs:2171](../../src/UI/UIController.cs#L2171)。T06 的核心接线缺陷。

运行时读取 `_mon.Get` 的 nullable 数值，每次 UI 调用自增 BatchId，并把 now 和固定 `DeviceId="default"` 填入 AlertSample。没有使用已发布批次的 SessionEpoch、StableDeviceId、质量或源采样时刻。旧值重复读取可满足持续超限；设备替换/采集会话重启也不会切断事件。探针仅让同一缓存数值在 t0/t6 被读取，就产生 `Triggered/Notify`；引擎无法知道这两次读取是否来自同一源样本。

建议：让告警消费 HardwareSnapshot/MetricSample，并按真实源采样身份去重；区分不可用、陈旧和真实恢复。告警需求应独立登记，不依赖局部窗口可见性。补冻结批次、部分传感器未刷新、设备替换/Host 重启测试。

### R08：主题运行时提交后抛错，仍只回滚 Skin 并宣称旧主题有效

位置：[AppActions.cs:320](../../src/Core/Actions/AppActions.cs#L320)、[ThemeStudioLauncher.cs:109](../../src/ThemeEditor/ThemeStudioLauncher.cs#L109)。**原 F02 未闭合**，依据完整控制流，未以真实硬件注入复现。

`UIController.ApplyTheme` 先更新 ThemeManager.Current、布局/外观并释放旧字体，末尾还会更新窗口、路由和调用 AppearanceChanged。这些后段步骤抛错时，AppActions 已经把异常转成“全部失败”的结果，Launcher 的 Failed 分支便回滚 Skin、忽略恢复保存结果并填写 `EffectiveTheme=previousSkin`，不会执行后面的 catch/Partial 判定，也没有恢复运行时。给菜单/任务栏分阶段并没有封住“ThemeRuntime 内部部分提交”的路径。

建议：在真正提交点形成应用事实，捕获后检查实际加载身份/修订；已提交就报告 Partial，或显式恢复并确认恢复结果。补后段故障和恢复写盘再次失败测试；不以旧 Skin 推断实际画面。

### R09：设置结果门面将被拒绝或启动失败的 Web 服务报告为成功

位置：[AppActions.cs:129](../../src/Core/Actions/AppActions.cs#L129)。INF-06 新增结果接线缺陷。

`ApplySettingsSelectivelyWithResults` 调用 void `ApplyWebServer`，只以“不抛异常”判断 Ok；而真实拒绝和启动失败通过 `ApplyWebServerWithResult` 返回，原本就不抛。探针局域网无口令场景，返回模块 `Applied=true`，同时 `server.IsRunning=false`，原生结果明确 `RejectedLanWithoutPassword=true`。端口占用同理，用户看不到本次失败。

建议：直接消费已有 WebServerApplyResult，按目标启停状态、拒绝码和实际运行状态构造 ModuleApplyResult；补安全拒绝、端口占用、重启失败和正常关闭。

### R10：GPU 资源门禁依旧不要求实际 GPU 成功出帧

位置：[ThemeStudioResourceTests.cs:374](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs#L374)、[ThemeStudioResourceTests.cs:446](../../tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs#L446)。**原 F03 未闭合**。

场景 4 仍只开 GPU 主题、切目标下拉、检查资源，没有启用/恢复 GpuThemeEffectsEnabled，也没有断言 GPU 激活、设备创建或每个修订实际出帧。本批修改仅检查 Join 并把上限延长到十分钟。因此“200 次切换通过”仍不能证明 GPU 路径被测到，R5 文档的“自动化门禁通过”结论应撤回。另，超时后前台 STA 线程继续运行；抛异常不是收回该线程，正对应记录中的 testhost 长时间无进展风险。

建议：有 GPU 环境必须验证首帧和更新后持续出帧；不可用环境明确未验证。长压力测试用独立进程/外部超时回收，不能留下仍持有全局状态的线程。

## P2

### R11：短时趋势不是秒桶，也没有保留缺失空隙或源采样去重

位置：[ShortTermTrendBuffer.cs:98](../../src/Core/Trends/ShortTermTrendBuffer.cs#L98)、[ShortTermTrendBuffer.cs:116](../../src/Core/Trends/ShortTermTrendBuffer.cs#L116)。T01/T02 实现不足。

仅按全局 BatchId 去重，每个非空值都用 observedAtUtc 入队；null 直接丢弃，没有秒桶聚合、峰值包络或源样本去重。探针两批实际指向同一采样时刻（TimestampMonoMs-SampleAgeMs 相同）仍写出两个点，第三批 Missing 没留下空隙。3600 个点在 250ms 或 2s 采样下也不等于一小时。

建议：以真实源采样时间去重，按时间裁剪和聚合一秒桶，显式保留质量/缺口与峰值；设备退场释放指标槽位。当前 HardwareTrendForm 也未消费 TrendBuffer，不能声称秒级用户路径已经完成。

### R12：长期趋势生产写入丢掉设备身份和质量

位置：[HardwareHistoryLogger.cs:312](../../src/Core/HardwareHistoryLogger.cs#L312)、[HardwareHistoryLogger.cs:123](../../src/Core/HardwareHistoryLogger.cs#L123)。与 R04 的聚合统计丢失是不同的数据维度。

全部新记录被写成 `StableDeviceId=null, Quality=Good, SampleAgeMs=0`；读回按 MetricKey 覆盖，忽略设备身份。旧 GPU.0 历史和当前 GPU.0 数据进入相同系列，换卡/优先卡变化后会串设备。仅在存储类型加字段，生产值固定为空，并没有实现稳定身份和时效语义。

建议：长期聚合同样消费真实快照；旧历史明确标记 legacy/identity-unknown，不归给当前设备。查询按设备和来源分段，缺失不能补 Good。

### R13：未知高版本场景库被当作损坏库移走，随后允许覆盖新库

位置：[ScenarioProfileStore.cs:192](../../src/Core/Scenarios/ScenarioProfileStore.cs#L192)、[ScenarioProfileStore.cs:42](../../src/Core/Scenarios/ScenarioProfileStore.cs#L42)。新增前向兼容缺陷。

TryRead 将未知 schema 一律返回 false，LoadAll 随后隔离主库。Save 对“库中存在高版本”的检查已经看不到原对象，于是可从空库继续写入。探针 schema=99 被读成空集合，紧接着 Save 新场景成功，活动库只剩 replacement。这不符合“未知版本只读、不覆盖”的设计。

建议：区分格式损坏和版本不支持；后者保留原文件、显示受限只读状态并拒绝写入。损坏恢复按 R05 独立处理。

### R14：规则列表引用变化会清空全部告警状态与冷却

位置：[AlertRuntime.cs:28](../../src/Core/Alerts/AlertRuntime.cs#L28)。新增状态生命周期缺陷。

ReferenceEquals 被当作规则版本；设置 DeepClone/Merge 会生成内容相同的新 List，任意无关设置保存都可能重建 evaluator。探针 t6 已触发通知，仅换成内容相同的规则集合，t14 再次触发通知，绕过三分钟冷却，并丢掉原 IncidentId。

建议：以 RuleId/定义修订比较，只重置语义改变的规则，保留未变规则的事件和通知冷却；列表分配不应成为事件边界。

### R15：Recovering 遇到采样缺口，未经确认就变成 Normal

位置：[AlertEvaluator.cs:65](../../src/Core/Alerts/AlertEvaluator.cs#L65)、[AlertEvaluator.cs:81](../../src/Core/Alerts/AlertEvaluator.cs#L81)。新增状态机缺陷。

gap 分支只保护 Active，不保护 Recovering；恢复等待期间发生休眠/断流，便走 Normal 分支。探针活动告警→Recovering→93 秒缺口，结果为 `State=Normal`、无恢复事件，且旧 IncidentId 仍残留。缺口被当作了恢复，违反质量与恢复分离的合同。

建议：所有尚未关闭 Incident 的状态统一进入 InputUnavailable，恢复有效输入后重走恢复持续时间；补 Recovering 下的缺口、无值和会话变化。

### R16：增加监控 GPU 数量后，不会为新卡创建温度告警规则

位置：[AlertRuleMigration.cs:19](../../src/Core/Alerts/AlertRuleMigration.cs#L19)、[SystemHardwarPage.cs:671](../../src/UI/Settings/SystemHardwarPage.cs#L671)。替换旧逐卡告警的行为回归。

规则仅首次迁移时按当时 MonitoredGpuCount 生成，后续 GPU 数量设置没有调用规则同步；再次启动也因 AlertRulesVersion=1 跳过。探针先迁移一张卡、再改两张，仍只有 GPU.0.Temp。旧告警每轮遍历当前监控数量，所以这是实际丢掉的能力。

建议：将“旧配置格式迁移”和“内置逐设备规则维护”分开，数量/稳定设备集合变化时增量同步，保留用户编辑和原事件状态。

### R17：告警诊断身份被过滤，多个设备的独立事件又被合并

位置：[UIController.cs:2208](../../src/UI/UIController.cs#L2208)、[DiagnosticRedaction.cs:13](../../src/Diagnostics/DiagnosticRedaction.cs#L13)、[DiagnosticEventStore.cs:107](../../src/Diagnostics/DiagnosticEventStore.cs#L107)。新增事件合同接线缺陷。

生产写入 ruleId/deviceId/state/notification/incidentId，但 context 白名单没有这些键，最终仅保留 reason。合并又只看 Module+ReasonCode，忽略规则、设备、事件和 SessionId。探针 CPU/GPU 两次独立告警变成一条 RepeatCount=2 的匿名告警，无法查出哪张卡、是否静音、是否同一事件。

建议：统一字段合同，设备使用会话/导出级伪名；合并键包含事件与来源身份。补同一时刻多设备触发及不同会话的测试。

### R18：诊断包脱敏依赖关键字黑名单，仍能导出绝对路径与 IP

位置：[DiagnosticRedaction.cs:75](../../src/Diagnostics/DiagnosticRedaction.cs#L75)、[DiagnosticPackageService.cs:154](../../src/Diagnostics/DiagnosticPackageService.cs#L154)。新增隐私边界缺陷。

允许任意 `module.*` 值，IsSafeValue 只拦 C 盘/UNC、若干秘密关键字；G 盘、其他盘符、IP、无关键字的凭据值不会被过滤。用合成异常 `failed: Could not open G:\Users\Alice\private-theme.json (host 192.0.2.1)` 构造模块事实后，导出 ZIP 成功且原文完整保留。部分模块身份/ReasonCode 也未经严格投影。测试只覆盖 C 盘和含 secret/token 的字符串，不能证明默认不含路径/IP。

这是本地导出边界测试，未使用真实隐私，也未上传文件。当前 ApplyResultDiagnosticProvider 尚未接生产组合根，不能把该探针夸大为已经发生的用户信息外泄。

建议：仅序列化枚举、数值、受控原因码等有类型白名单字段；原始 FailureReason 不进导出。需要身份时做本包伪名，预览展示最终实际投影，而非只给文件名/计数。

### R19：诊断页每秒刷新会清空正在阅读的详情和选择

位置：[DiagnosticPage.cs:169](../../src/UI/Settings/DiagnosticPage.cs#L169)。新增交互缺陷，依据明确控件生命周期。

每次 RefreshView 先清空 details，再清空并新建全部 ListViewItem，没有按 Module 恢复选中项。用户选中详情后下一秒就被抹掉，无法持续阅读或复制。同一计时器在页面从设置容器摘下后也不会停止，会继续构造快照和重建不可见列表，直到整窗销毁。

建议：按稳定键增量更新并保留选择/滚动/详情；页面离开时停刷新，再显示时恢复。补选中后跨多轮更新和隐藏页无刷新测试。

### R20：节能显示策略可能把低帧率主题提速

位置：[PowerPolicy.cs:38](../../src/Rendering/Power/PowerPolicy.cs#L38)。纯策略缺陷；生产电源事件接线尚未完成，不宣称当前用户已可触发节能开关。

Eco/Auto 电池直接返回固定 10/15，而不是不超过 normalFps 的上限。探针 normalFps=5 时反而变成 10/15。独立 ResolveAnimationFps 又给出 20/30，与本轮计划 10/15 动画上限不一致。

建议：所有节能上限均取 Math.Min(用户目标, 策略上限)，统一显示与动画的合同；补用户目标 1/2/5/10/15/30/60，禁止省电时反向增加工作量。

## 上一轮 F01–F11 的闭合判断

| 原项 | 本轮判断 |
|---|---|
| F01 CI 续行 | 修复正确，diff 已补反引号；本轮没有重新执行完整 CI 工作流 |
| F02 实际应用结果 | **未闭合，R08**；预检和菜单/任务栏分阶段是进步，仍有内部部分提交路径 |
| F03 GPU 资源门禁 | **未闭合，R10**；等待上限不是实际出帧断言 |
| F04 fallback 配色 | 禁用/显式回退定向测试通过；初始化/运行失败与 R02 仍需闭合 |
| F05 横向 fallback | 同形态分流已实现；失败回调的 R02 阻止稳定验收 |
| F06 任务栏设置 | 当前设置快照已传入；定向测试通过 |
| F07 最小化 | 停帧实测通过；**恢复失败，R01** |
| F08 DPI 一致 | 初建/热更均使用 DPI 预览副本；168 DPI 下热更持续出帧；完整 DPI 矩阵未执行 |
| F09 任务栏导出 | 独立 formFactor 和 TaskbarRenderer 路线已实现，定向测试通过 |
| F10 凭据头/动态授权 | X-API-Token 分类、实例授权、origin 匹配已接线，定向通过；真实代理/DNS、用户撤权入口仍未完整验收 |
| F11 热更新代次 | 真实 GPU 连续三次热更正常，复用同一设备；R01 是另外的 Size 代次路径 |

## 计划进度边界，不混算为本轮新增缺陷

- 诊断：生产组合根没有 ApplyResultDiagnosticProvider；IPC 未仪表化、插件仅计数，这些界面如实标 Unknown/Warning，但不能算完整模块健康/恢复中心。事件详情与真正的导出内容预览仍需补齐。
- 趋势：短时缓冲虽被 UI Tick 写入，趋势窗仍只用 HardwareHistoryLogger 的分钟数据，尚未形成计划要求的秒级用户路径；T07 长时/资源协议未执行。
- 场景：请求/实际生效场景 Id 和修订没有完整持久化；启动、托盘、设备/屏幕稳定绑定及 S06 仍不完整。当前 Profile 名称/修订不能替代真实生效事实。
- 节能：UpdatePowerContext 尚无生产电源事件调用者，缺设置入口、非 GPU/任务栏调度、P04–P07 采样频率/IPC/消费者协同与实机性能证据。接口和测试完成不等于用户可用功能完成。
- G1 的 100%–200% DPI、RDP、休眠、AC/DC 矩阵仍缺证据；本轮只对当前 175% 环境作有限实证，不代签整张矩阵。
- 新页面有大量硬编码英语和固定像素布局，未达到此前工作室级的明暗/DPI/本地化完成度；应在相应页面正式验收时一并补足。

## 建议下一步

1. 先修 R01/R02/R03/R05/R06：实际交互、恢复、兼容和配置安全；同时纠正 R5“GPU 门禁通过”的记录。
2. 在扩大分发前完成 R04/R07/R08/R09/R10：历史写入保真、真实采样告警、真实应用结果和有效资源门禁。
3. 按模块处理 R11–R20，把本报告探针转成行为测试；优先覆盖跨帧、跨读取、跨重启、跨规则修订的状态转换，避免只检查即时 bool 和源码字符串。
4. 再完成四项功能的生产组合根和用户入口，最后跑实机矩阵与全量回归。当前适合继续受控修复，不适合直接签署三个发布切片完成。

本次仅审查与归档，没有替用户修改实现、清理工作区、提交或发布。
