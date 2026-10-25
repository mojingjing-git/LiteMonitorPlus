# 两份Plan交付后的完整变更审查

日期：2026-10-26。类型：代码审查及下一步建议；未修改产品源码。

> **修复记录（2026-10-26，commit `5ef523b`）**：R1～R9 已全部修复，探针转为 19 例自动回归
> （`ReviewFix20261026Tests` + I2 监督测试新增 R2/R9）。要点：R1 apply 按目标模式落状态+
> verify 读真实状态+HotSwap latch 显式 Off 放行（`ControllerTargetEnabled` 纯映射可测）；
> R2 会话全程收口（SpawnHost 入 try）+finally 确认终止子进程+`protocol-violation` 故障注入
> （崩溃循环暂停为 §6.2 正确有界行为，测试按此断言）；R3 `CatalogSnapshotCache` 线程池单飞
> （在途合并不增并发 LHM 访问）；R4 为 B4-1 引入的真实回归已修（非刷新分支应用插件色）；
> R7 口令入运行时黑名单+真实凭据变更由编排显式比对；R8 `LatencyWindow` 环形窗口(512)；
> R9 帧序号统一入口跟踪+批次缺口按 BatchId 另计（`PipeClientMetrics.SnapshotBatchGaps`）。
> R5 为源码级护栏（Helper 构造需 UIController，Helper 级状态迁移测试待注入缝，同
> WebSamplingDemandTests 先例）。验证：专项 19/19、全量 2433 执行/0 失败（发现 2440）、
> 棘轮 12/12。§4 阶段缺口（G1～G5）未在本包处理——按 §6 第二/三步另行推进。

## 1. 结论

本轮确认 **9项问题：3项P1、6项P2**。其中7项通过额外小型探针复现，2项通过完整调用链静态确认。没有确认到P0问题。

建议先修复策略切换、监督器异常收口和硬件目录查询阻塞这3项P1，再完成其余状态/缓存修复。现阶段可继续保留Host为显式选择的原型，暂不把I2当作完整进程隔离发布，也不以B3/B4的单元测试通过替代生产接线验收。

## 2. 范围与验证口径

- 起点：交付Plan A/B时的源码状态`1f652c1f0e402f124c4535fcf878f19c216c5e39`。
- 终点：`11239569704f69f0714ed1b0cf451f867b4e54e5`。
- 比较：`git diff 1f652c1 1123956`，**104个文件，新增23493行、删除1150行**。
- 文件分布：src 52、tests 15、scripts 3、.github 2、docs 24、tools 6、主项目/解决方案2。
- 纳入：交付Plan后合入主线的F08b/F14b/F06、自动隐藏修复、B0～B3、B4-1、I2，以及相关测试、脚本、工程配置和验收记录。合并进来的提交即使开发更早，也按主线新增效果审查。
- 排除：Plan交付前主线已经包含的F02/F03/F05/F07/F14a/F17等代码；不把它们的问题重新计入本次回归。
- 两份当前未提交文档`development-windows-firewall.md`和`F00-baseline.md`在交付Plan时已经是dirty，无法据现状判定哪些行是交付后新增，不计为本次代码回归；未改变本机防火墙设置。
- 新出现的`.cua-helper/`按本机探针材料查看，未运行其控制脚本。它不属于新增产品功能，应按本地材料管理。

变更审查结合净diff、核心调用链、测试与验收文档。大型性能JSON检查其用途、字段与结论对应关系；本次未重做真实驱动更新、USB设备拔插、锁屏/RDP和72小时长跑。

## 3. 确认的问题

### R1 · P1：控制器切换返回成功，实际仍保持开启

**位置**：[HardwareMonitor.cs](../../src/System/HardwareMonitor.cs):1024～1029；[ControllerProbePolicy.cs](../../src/System/HardwareServices/ControllerProbePolicy.cs):179～214；并关联`HardwareMonitor.cs:275～302`。

**触发**：控制器已开启时，调用新增的`ApplyControllerProbeMode(Off)`。

`ApplyMode`先执行`apply(target)`，成功后才更新`Mode`。但传入的回调是`_ => RefreshHardwareConfig()`，没有使用target；`RefreshHardwareConfig`又通过`ShouldEnableController`读旧策略。于是On→Off时仍算出应开启，直接返回；随后策略被标为Off，verify却为null，最终报成功。即使先解决旧模式读取，旧的true→false latch分支仍会忽略关闭请求。

**实际探针**（只构造未Open的Computer，不访问真实硬件）：

```text
controller-off: applied=True mode=Off enabled=True state=Enabled
```

**影响**：新增API的结果与实际设备访问状态不一致。当前设置页尚无完整控制器模式入口，但不能据此把热切换判为通过；后续接入该安全开关时会继续触碰用户以为已关闭的设备。

**建议**：在采样协调边界内按目标策略执行切换，显式Off必须释放/移除控制器资源；Auto模式的旧latch行为与显式关闭分开。验证真实`IsControllerEnabled`和资源释放后再提交策略状态。

**缺失测试**：当前策略测试主要对独立Policy和假工厂，未经过真实`HardwareMonitor.ApplyControllerProbeMode`接线。补On→Off→On、失败回滚与重入测试。

### R2 · P1：已握手Host发送非法帧后，监督循环退出且遗留活子进程

**位置**：[HardwareHostSupervisor.cs](../../src/System/Pipe/HardwareHostSupervisor.cs):135、229～254、263～275。

**触发**：Host成功握手后，发送一个非法长度帧；也适用于读取循环抛出的其他未收口异常。

`ReadLoopAsync`重抛`IpcProtocolException`，`RunSingleSessionAsync`随后`await readTask`直接抛出。控制流跳过`TerminateHost`、`SessionClosed`和退避结果返回；外层`RunAsync`也没有将异常转成失败会话。finally只Dispose进程对象/连接，不结束仍在运行的进程。

**实际探针**：真实子进程完成合法PID/令牌握手后写入长度-1，保持存活；等待监督器处理后得到：

```text
invalid-frame: state=Ready spawnCount=1 childStillAlive=True
```

探针结束时由Job Object回收该子进程，未留孤儿。

**影响**：恰在隔离系统应处理的故障路径上，自动恢复失效，健康状态误报Ready。主程序不退出时，Job Object也不会替它立即清理子进程。

**建议**：所有会话失败都转成统一失败结果；在会话finally保证已启动子进程终止且确认退出，再允许新会话。`SpawnHost`也应纳入异常收口，不能在try之外使缺文件/启动失败直接结束监督任务。发生异常时通知客户端、更新状态并执行有界退避。

**缺失测试**：现有非法帧用例验证编解码或握手端点，未验证“握手后协议异常→监督器→子进程回收→恢复”的整条链。

### R3 · P1：名为异步的目录查询同步等待采样锁，阻塞主UI

**位置**：[HardwareClientAdapter.cs](../../src/System/HardwareServices/HardwareClientAdapter.cs):59～74；[HardwareInfoForm.cs](../../src/UI/HardwareInfoForm.cs):101～113。

**触发**：默认进程内模式打开硬件传感器窗口，同时后台采样持有硬件锁。

`GetCatalogAsync`不是异步实现：它在返回Task之前同步取得`SyncLock`、遍历整棵设备树，最后`Task.FromResult`。调用方的await不能让前面这些工作离开UI线程。传感器窗口构造时调用一次，随后WinForms Timer每秒重复调用。

**实际探针**：让另一线程持有同一锁350ms，测量调用至取得Task的时间：

```text
catalog-async-call-return-latency-ms=351
```

**影响**：正常采样耗时直接变成UI卡顿；若原生采样卡住，传感器窗口的定时刷新可以把整条UI消息循环一起阻塞。它是消费方迁移后新增的高频同步锁路径。

**建议**：优先由采样线程生成并发布不可变目录快照，UI查询缓存。如果阶段性改为后台构建，必须有在途合并、关闭取消与明确等待边界，且不能因此增加并发LHM访问。

**缺失测试**：需要测调用返回延迟及UI消息循环响应，而不是只await最终目录并校验内容。

### R4 · P2：数值缓存命中时丢失插件颜色更新

**位置**：[MetricItemFormatter.cs](../../src/Core/MetricItemFormatter.cs):85～92。

旧实现先更新插件颜色，再决定是否重算数值文本。新纯函数在`!refresh`分支只更新`HasCustomUnit`，忽略本次`pluginColorOverride`。

**实际探针**：连续两次以数值42格式化同一numeric DASH项，颜色由0变2：

```text
plugin-color: expected=2 actual=0
```

**影响范围**：numeric DASH/复用该纯格式接口的路径。当前主界面的常规DASH使用TextValue分支，该分支没有本问题；不将影响扩大为所有插件颜色都失效。

**建议**：颜色状态独立于文本缓存更新；数值不变时仍应用新的覆盖状态。补“两帧同数值、只改颜色”和清除覆盖的测试；现有测试每次从初始state开始，无法发现这个跨帧问题。

### R5 · P2：恢复窗口位置后保留旧泊出区域，自动隐藏状态卡死

**位置**：[MainFormBizHelper.cs](../../src/UI/Helpers/MainFormBizHelper.cs):490～509，关联293～304。

新增自动隐藏实现以`_parkedArea.HasValue`决定是否走已泊出分支，但`RestorePos`仍只重置旧字段`_isHidden`和`_dock`，没有清空新增的`_parkedArea`、`_parkedScreenDevice`。

**实际探针**：设置合法泊出状态后调用真实`RestorePos`：

```text
restore-position: hidden=False parkedAreaRemains=True
```

下一次检查仍进入已泊出分支，dock却是None，纯决策返回None；这会使显示拓扑恢复或位置恢复之后的自动隐藏无法重新正常运行。

**建议**：集中一个完整的泊出状态退出方法，在RestorePos、显示拓扑恢复、看门狗夹取、禁用和手动恢复路径复用；不能只维护两个旧字段。补“泊出→RestorePos→重新泊出”的真实Helper级状态迁移测试。

### R6 · P2：同级Z序只看紧邻窗口，漏掉隐藏窗口后面的可见遮挡

**位置**：[TaskbarWinHelper.cs](../../src/UI/Helpers/TaskbarWinHelper.cs):622～627。

`IsCoveredByVisibleSibling`只取一次`GW_HWNDPREV`。如果顺序是“可见遮挡层→隐藏窗口→面板”，直接前驱不可见，就返回false，尽管更前方存在覆盖面板的可见同级窗口。

**实际Win32探针**：创建屏幕外、不激活的测试父窗口和三个同级子窗口，固定上述层级、矩形相同：

```text
sibling-z-order: invisiblePredecessor=True coverVisible=True detectedCovered=False
```

所有测试窗口随后销毁；未操纵真实Explorer任务栏。

**建议**：有界遍历同级前驱，跳过隐藏窗口，确认可见窗口及必要的几何关系后再恢复。补三窗口/多窗口测试，不能只测两个相邻可见窗口。

另外，当前仅排定3次后续Z序检查；对于更晚出现的合成层，应补低频持续巡检场景验证。该时间维度本次未做真实驱动更新实验，不与上面的已复现问题混计。

### R7 · P2：差异快照丢弃内存口令，空保存被识别成Web配置变化

**位置**：[Settings.cs](../../src/Core/Settings.cs):386～389；[SettingsChanger.cs](../../src/Core/Actions/SettingsChanger.cs):207～224。

`CloneForDiff`使用持久化JSON往返，而内存态`WebServerPassword`被JsonIgnore。克隆前存在内存口令时，克隆后该字段为空；ComputeDiff仍把它当普通可写属性比较。

**实际探针**：对一份未修改的设置做CloneForDiff，再与原对象比较：

```text
clone-diff-with-password: count=1 fields=WebServerPassword
```

**影响**：保存路径进入Web应用分支，破坏“空差异零扰动”的判断。既有Web应用函数会再检查实际状态，因此本次证据不支持宣称每次都重启监听或断开连接。

**建议**：使用专用差异快照显式保存可比较状态；口令以明确的凭据变更标记/指纹或只驻留内存的快照比较，不能直接把持久化序列化器当作完整对象克隆。补有口令与只有哈希两种状态的空保存测试。

### R8 · P2：Host客户端的延迟统计无界积累

**位置**：[PipeHardwareClient.cs](../../src/System/Pipe/PipeHardwareClient.cs):66～68、140～144、371～387。

两个List初始capacity=4096只是初始容量；每个快照都追加，未淘汰旧样本。即使未配置指标落盘路径，累计也仍发生。运行越久占用越大，调用ReadMetrics时还会复制/排序全部历史。

**证据级别**：完整写入/读取路径静态确认，未做数日长跑；不提供未经实测的泄漏速度估算。

**建议**：固定大小环形窗口或流式直方图；指标关闭时避免无用统计分配。用超过窗口容量的测试验证计数有界，保留独立总样本数。

### R9 · P2：快照缺口统计把正常心跳和应答误计为丢包

**位置**：[PipeHardwareClient.cs](../../src/System/Pipe/PipeHardwareClient.cs):119～123；[HostPipeSession.cs](../../src/HardwareHost/HostPipeSession.cs):263～281。

Host为心跳、应答和快照共用一个方向序号；客户端却只在HandleSnapshot中更新`_lastSequence`。例如快照10、心跳11、快照12完整收到，仍会计为1个SequenceGap。

**证据级别**：按两端真实序号分配及消费路径静态确认；无需网络丢包才能触发。

**影响**：原型A/B结果中SequenceGaps不能直接解释为实际丢失的采样批次，影响接收链路验收。

**建议**：帧序号在统一接收入口跟踪；采样批次缺口另按BatchId及会话统计。测试快照之间夹心跳/应答时缺口为0，并单独验证快照槽位合并带来的批次缺口。

## 4. 阶段边界与接线缺口

以下是需要收口的阶段成果，不重复计入上述9项回归。

### G1：F14b目前交付的是明暗主题引擎，用户路径尚未完成

源码中没有生产调用`SettingsThemeManager.Initialize/SyncFromSettings/BeginPreview/CommitPreview`，也没有`ThemeChanged`订阅或对`SettingsThemeApplier.Apply`的生产接线；设置页未提供模式选择，系统配色消息也未接入该管理器。52条测试主要证明调色板、重映射与独立状态机。

应补启动时读取已保存模式、模式选择、预览/保存/取消、窗体关闭取消、系统主题变化与UI线程调度，以及语言资源。验收状态应标记“引擎完成、用户功能待接线”，不能据此关闭#437暗色子项。

### G2：I2是有效的通信/监督原型，尚未达到完整隔离

`UIController`仍无条件创建`HardwareMonitor`；Host模式只把请求入口改为Pipe客户端，面板、告警、历史等仍有`_mon.Get`直读。I2验收报告已明确披露面板读数仍来自进程内路径，因此这里不把保留双采集当作违反其已批准原型范围的新缺陷。

相应限制：Host进程存活、收到快照，不等于用户所见读数来自Host；现有额外CPU也混合了双份采集成本。I3需替换真正的数据消费链，不能将原型叠加成本作为最终隔离架构开销。

### G3：B1/B2建立了契约边界，快照语义仍是占位

`HardwareClientAdapter.GetLatestSnapshot`每次读都增加BatchId并使用当前时间，SampleAge固定0、ConfigRevision固定-1；SubmitDemand只记账。源码已注释这些占位和未迁移的直读路径。

在UI/Web/历史正式改用快照前，必须改成“真实采样完成时发布、读取不改变代次/时效”的模型，补上设备身份、需求汇总和配置revision。这是下一步接线的前置，不能只把`_mon.Get`逐个替换成即时现算快照。

### G4：B3和架构棘轮应按实际职责评估

生产保存入口使用的是`ApplySettingsSelectively`安全子集，`SettingsApplyDispatcher`与`SamplingConfigApplier`仍未接入保存链；验收报告已说明，不能把这部分理解为模块服务全面接线。

架构脚本A-1～A-4主要统计using语句。AppActions/WebActions改为全限定`HardwareGateway`引用后，using数量减少，但Core的运行依赖并未因此消失。12/12是当前棘轮通过，不是Core已无反向依赖。下一阶段应采用程序集/类型引用检查，或至少覆盖全限定引用。

### G5：Host测试的产物定位影响自定义输出目录验证

`I2HostBinaries.Locate`固定查找`src/HardwareHost/bin/{Debug|Release}`，不跟随本次`BaseOutputPath=bin/review-20261026/`。第一次全量测试确实会使用已有默认目录Host；抽查DLL摘要不同，不能把它自动等同于新构建产物。

本次已单独重建默认目录Host，确认其DLL SHA-256与独立输出目录版本一致，再重跑I2测试33项通过。后续应允许测试显式指定本轮Host产物路径，并记录摘要，避免旧产物使隔离验证假绿。

## 5. 本次执行的验证

| 项目 | 实际结果 | 解释 |
|---|---|---|
| Debug解决方案构建 | 0错误，555条warning | 使用独立输出目录；不把warning计为零 |
| Debug现有全量测试 | 2513 total，2506 passed，0 failed，7未执行 | 主测试2421/2414；GPU测试19/19；Updater73/73 |
| 当前Host专项复跑 | 33/33通过 | 先重建并核对默认目录Host摘要，解决固定路径测试使用旧产物的问题 |
| 架构棘轮 | 12/12通过 | 只表示当前脚本指标未超过基线，边界见G4 |
| 额外回归探针 | 7个预期/实际差异复现 | R1～R7；探针在临时目录，不加入产品编译 |
| diff空白检查 | 未通过 | LiteUI多处行尾空白及一份基线md尾部空行；属于清理项 |
| Release/完整CI市场门禁 | 本次未执行 | 不声称候选发布门禁全部通过 |

7项未执行包括6项外部插件仓库契约和1项市场E2E；本机未提供`LITEMONITOR_PLUGIN_REPO`等完整CI条件。CI的跳过上限为1，故“本地测试退出码0”不能直接写成“完整发布门禁通过”。TRX采用total/executed/passed及逐用例outcome解释，避免把notExecuted计数器为0误当成没有跳过。

本次命令与输出保存在`.tmp/review-20261026/`：

- `build-debug.log`：独立目录构建。
- `test-debug.log`、`test-results/*.trx`：全量测试。
- `build-current-host.log`、`test-i2-current-host.log`、`i2-current-host-results/*.trx`：新Host重建和专项复跑。
- `architecture.log`：棘轮检查。
- `probe/Program.cs`、`probe/ReviewProbe.csproj`、`probe-result.log`：复现源码和最终结果。

探针输出：

```text
plugin-color: expected=2 actual=0
controller-off: applied=True mode=Off enabled=True state=Enabled
clone-diff-with-password: count=1 fields=WebServerPassword
catalog-async-call-return-latency-ms=351
restore-position: hidden=False parkedAreaRemains=True
sibling-z-order: invisiblePredecessor=True coverVisible=True detectedCovered=False
invalid-frame: state=Ready spawnCount=1 childStillAlive=True
```

探针的Controller/Catalog对象未调用Computer.Open；Z序使用屏幕外测试窗口，结束后销毁；非法帧使用专用假Host子进程，并由Job清理。现有I2测试另有LHM自测，其结果不代表完成所有目标硬件验收。

## 6. 下一步建议

### 第一步：完成一个范围受控的回归修复包

按R1→R2→R3优先处理安全开关、故障恢复与UI阻塞，再修R4～R9。每项先把本次探针整理为针对真实接线的自动回归，修复后验证预期；不只修改测试期望。

重点新增的测试应是：Policy→HardwareMonitor的真实回调、合法握手后的协议故障、UI调用目录接口的返回延迟、跨帧颜色变化、完整泊出状态退出、多窗口同级Z序、含凭据的空设置保存，以及指标统计的有界性/正确序号口径。

### 第二步：把已有成果接成用户可验收的闭环

1. 完成G1的明暗主题用户路径，使用真实SettingsForm验证保存/取消/系统切换。
2. 将G3的采样完成快照、设备目录缓存、配置revision与需求语义做实。
3. 把UI/Web/历史逐个切换到同一快照；每迁移一类消费者，比较读数、质量、采样次数和设备绑定。
4. 修正G4/G5的验收工具与状态文档，用“实际接线完成”区分“类和测试已经存在”。

### 第三步：再推进完整隔离与深层解耦

- I3迁移正式采集器并移除Host模式下的主进程LHM采集，再做两进程合计性能A/B；继续推进I4生命周期与I5打包/升级。
- B4-2净化MetricUtils和B5工程拆分沿稳定快照边界推进，不在数据来源仍分叉时同时扩大改动面。
- 原有F系列待实机项继续保留，尤其USB/DAPLink、真实显示变化和驱动场景；不能借架构重构的测试数增长关闭它们。

## 7. 建议的交付判据

第一批补丁应做到：本报告R1～R9逐项有结果，补齐缺失接线的真实界面验收；Debug/Release及完整CI条件门禁通过；Host仍明确标为原型模式，或者确实完成主进程采集迁出后再声明完整隔离。新回归先收口，再考虑扩大重构范围。
