# GPU 与 metrics 修复独立代码复审

日期：2026-10-24（Asia/Shanghai）。快照核对时间：2026-10-24 01:16:52 +08:00。

**结论：PASS（限定 A/D 组已冻结代码与新增测试的静态复审）。未发现有具体生产反例支持的未修复 P1/P2。** 此结论不表示整个修复批次或运行验收已经通过；编译、Debug/Release 测试及集成验收仍由主控在所有组冻结后统一执行。

## 范围与依据

- 工作树：`C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork`。
- 基线：`9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f`。
- 已读计划 `docs/superpowers/plans/2026-10-23-independent-review-remediation-plan.md` §4、§5、§9、§10、§11；已读两份计划复审的最新有效结论：R1 第 144～164 行的 v2 定向 PASS，R2 第 121～140 行的 v3 定向 PASS。
- 主控先后通知 A、D 组 ready；最终结论覆盖下表六份生产文件和两份完整新增测试。初读时 D 测试尚在进行中，本报告的最终结论以两组 ready 后复读并核对哈希的冻结快照为准。
- 只读上述实现、测试及其基线/指定上游差异；未复审 B/C 组、CI 或 T09，也未编辑生产代码和测试。
- 未执行 `dotnet build`、`dotnet test`、`dotnet run`，未加载产品程序集、启动硬件采集或运行新增用例。限定六份生产文件的 `git diff --check` 返回 0。

下述行号均为本快照的一基源码行号。表中的测试是已核对的断言及调用路径，不是本复审者实跑通过的结果。

## F01：同一 HWND 历史、真实新代次及生产回退

| 核对项 | 实现及测试锚点 | 静态结论 |
| --- | --- | --- |
| 同代次历史只能前进 | `src/UI/RenderingTransitionCoordinator.cs:566`、`:577`；`tests/LiteMonitor.Tests/ReviewGpuRecoveryRegressionTests.cs:20` | 显式按 Unbound / Bound / PresentAttempted / Presented 排序，低状态与相同状态不覆写历史；不依赖枚举数值。 |
| 真相源已 Advance 后，新通知仍能 reset | `RenderingTransitionCoordinator.cs:437`、`:442`、`:448`；新测试 `:122`、`:134`、`:137`、`:139` | 比较已记账的 `_state.HandleGeneration`，而非已推进的 getter；接受较新的真实代次后将历史置 Unbound。另拒绝落后于真相源的中间代次（测试 `:179`）。 |
| 旧/重复通知不推进阶段、不重启宿主 | `src/UI/UIController.cs:346`、`:349`；新测试 `:153`、`:168`、`:170` | 协调器返回 false 后 UI 立即退出；测试在真实 Form HWND 已创建且期望 GPU 稳定的状态下断言同一状态对象、历史不变和宿主未创建。 |
| 新通知继续现有受控流程 | `RenderingTransitionCoordinator.cs:454`、`:460`；`UIController.cs:355`、`:361`、`:1615`、`:1629`、`:1634`、`:1729` | 非受控通知接受代次后仍不启动 GPU；受控通知保留 Restoring 阶段，因此不会在重建中抢先创建宿主，调用方仍继续 CompleteLegacyTransition 并发布稳定 legacy。没有把有效新通知一并截断。 |
| 直接传 42 的兼容接缝 | `RenderingTransitionCoordinator.cs:441`；新测试 `:200`、`:207` | 允许直接传较新代次；测试明确断言注入源仍为 1，已记账代次为 42。该用例与真正 Advance 后 reset 的用例分开，未用它冒充真实推进验证。 |
| 启动前失败仍保留旧窗口历史 | `UIController.cs:560`、`:577`、`:584`；新测试 `:52`、`:65`、`:72`、`:76` | 测试直接调用生产 ScheduleGpuFailureFallback，以新宿主 Unbound 报告验证旧窗口的 Bound / PresentAttempted / Presented 不丢；旧窗口也为 Unbound 时不强制重建。 |
| 回退执行仍接统一重建判据 | `UIController.cs:594`、`:633`、`:653`、`:1577`、`:1608`、`:1749`；`RenderingTransitionCoordinator.cs:593` | 入队后由 UI 安全点处理，进入既有 RunGpuToLegacyTransition；手动切换与故障回退共用 RequiresControlledRecreate。成功启动/失败启动的另两个记录点 `UIController.cs:1832`、`:1858` 也统一经过单调记录。 |
| 关闭、隐藏、有限恢复及重建失败 | 新测试 `:84`、`:86`、`:94`、`:217`、`:248`；`UIController.cs:565`、`:566`、`:582`、`:599` | 测试断言隐藏窗口不创建 HWND、运行故障不提前入队、关闭后状态不变、重复回调不清除重建失败阶段/原因。真实 GPU 设备恢复及画面没有在本次执行。 |

具体原缺陷反例的静态复核：同一窗口先 Presented，再由替换宿主报告 Unbound。基线 RecordBindingState 直接覆写，RequiresControlledRecreate 随即为 false；当前生产调度入口仍调用同一协调器，但低状态被拒绝，因此重建判据保留为 true。新增测试第 47～91 行直接锁住此入口，并同时保留无历史冷启动失败的反例边界。

## F02：多轮失败责任链

实现锚点：`src/UI/CapturePolicyCleanupLedger.cs:223`、`:277`、`:300`、`:309`、`:322`、`:333`、`:334`、`:343`。

- 选择条件现在包含 `!entry.ReclaimedByExplicitRecovery`，先拒绝已有非终态责任，再创建携带同一 HWND、generation 和精确 OriginalAffinity 的新责任。
- 新责任经过真实 Add 登记成功后，才标记选中的旧责任被接管。原 Failed / exhausted 审计不被改写，其他未解决责任不被批量标记。
- 实例级登记钩子默认不设置；测试只在真实 Add 前建立冲突，未替换 Add 的不变量。成功/失败顺序及 finally 恢复钩子见新测试第 438～488 行。
- 生产显式恢复接线仍在 `UIController.cs:1441` 先于 StopGpuPresentation，并保留启动拒绝后的补充入口 `:1492`；拒绝码过滤仍走 ledger 的 `:359`。没有在 Tick/Pump 内增加自动 rearm。

具体原缺陷反例：A 耗尽后接管为 B，B 也耗尽，再恢复为 C。基线会再次选中已 Reclaimed 的 A；即使 C 成功，B 仍未接管而保持 unresolved 闸门。当前跳过 A，改为接管 B，C 成功后才解除闸门。

新增 `ReviewGpuRecoveryRegressionTests.cs:270`～`:365` 覆盖 A→B→C 及更长失败链，断言每轮恰好一条非终态责任、同实例登记幂等、恢复对象/精确原值不变、所有旧 Failed 审计保留、最终闸门解除且不能凭空再恢复。另有：

| 边界 | 测试一基行号 |
| --- | --- |
| 成功后再次产生失败，沿用该新责任的精确原值 | `:369` |
| 多个未解决责任分别接管，其他责任仍保持闸门 | `:398` |
| 登记成功后才接管；登记冲突保留旧责任 | `:438` |
| 空账本/未修改 affinity 不凭空恢复 | `:492` |
| 新代次不接管旧失败责任，不调用旧 HWND 探针 | `:513` |
| 接管后换代，在窗口探针之前终结旧责任 | `:536` |

## F06：逻辑盘 Used 与物理盘缓存/休眠

`src/System/HardwareServices/DiskManager.cs:221`～`:245` 将完整合法 `DISK.<ASCII盘符>.Used` 的 DriveInfo 路径置于物理缓存前。冷/热缓存均先读逻辑盘实际使用率；盘符大小写均可用。未就绪盘仍继续既有缓存/lastValid 回退；DriveInfo 构造失败返回 null，读取异常返回 0f。其他键不进入该分支。

本次未改 `ProcessUpdate`（`:86`）、后台间隔及睡眠判据（`:119`、`:123`、`:128`），逻辑盘读取也不调用 hw.Update 或更新物理盘扫描时间。

具体原缺陷反例：DISK.Read 建立物理缓存后，在十秒新鲜期内读取 DISK.C.Used，基线会先由 ReadDiskSensor 得到 null，再因缓存新鲜直接返回 null，完全跳过 DriveInfo。当前分支顺序避免了该返回。

新增 `ReviewMetricPresentationRegressionTests.cs:25` 使用未 Open 的 Computer、假物理盘和真实就绪盘，覆盖冷读、读写/温度后热读、9999ms 与 10000ms 缓存边界及重新选盘，按前后真实空闲空间采样加容差比较；`:91`～`:93` 断言没有物理 Update，也不污染 Used 的 lastValid。非法/非完整键和未就绪盘回退分别在 `:112`、`:123`。后台休眠的既有运行回归仍须主控执行，不能由“此方法未改”推称已跑过。

## F07：Stock 仅移植指定上游规则

已直接对照本地对象 `git show a71ce7584d892230d157c02047bf052fad6a738f -- resources/plugins/Stock.json`：本次差异与该提交的 Stock 差异一致，仅在原有清洗之前增加 `^us\\.` → `us`（`resources/plugins/Stock.json:46`～`:52`），以及文件末尾换行。没有移植其他上游文件或泛化为清除所有前缀/点号。

具体反例：原 `us.NDX` 经过既有 `\\.[A-Z]+$` 清洗会成为 `us`，错误构造 `q=us`；当前先归一化为 `usNDX`。

新增测试 `ReviewMetricPresentationRegressionTests.cs:138`～`:168` 加载实际随产物复制的 Stock 模板，经 PluginProcessor.ApplyTransforms/ResolveTemplate 核对 us.NDX/usNDX、us.AAPL、合法交易所后缀、沪深港代码、空值、问号及缺失 code 的 query fallback；未发股票请求，也未写用户模板。执行期能否找到复制资源仍须主控测试确认。

## F08：真实快照格式路径与 JSON 合同

生产 `src/System/WebServer/LiteWebServer.cs:537` 直接调用新增 `WriteSnapshotItemJson`（`:550`），传入真实 item 和取值提供器；该方法由 `:594` 将 UnitPanel 传到带格式的 ResolveValue，`:752` 再传给既有 MetricUtils.GetValueStr。`:604`～`:605` 与占位分支 `:616`～`:617` 的单位均走 GetDisplayUnit。没有修改 MetricUtils 的既有规则。

`pct` 与 `sts` 仍在 `:608`、`:611` 使用 rawValue 计算，未使用显示字符串。DASH 分支 `:568`～`:583` 保留插件文本、.Unit 和 .Color；FPS 在 `:737`～`:745` 先判断需求及可信数据，再于 `:748` 读值。两个公开 ResolveValue 入口（`:685`、`:714`）仍以默认格式转发。

| 合同/反例 | 新增测试一基行号 | 核对内容 |
| --- | --- | --- |
| 32GB、50%、容量模式、自定义 % | `ReviewMetricPresentationRegressionTests.cs:172`、`:180`、`:188` | 实际序列化行断言 v=50.0、u=%、pct=50、sts=Warn，能区分基线默认 16.0/GB 的错配。 |
| null / 空字符串 / {u} / {u}% / 未知 RAM 容量 | `:173`～`:188` | 分别断言 16.0/GB、16.0/空单位、占位单位展开，以及未知容量回退 50.0/%。 |
| 全局百分比模式与提交内存 | `:196`、`:211` | 真实快照行；提交内存经生产 Publisher 提供负载，断言 48.0 / 64.0 GB 与自定义 75.0%。 |
| 按卡 VRAM 容量、未知容量 | `:225`～`:241` | 8GB/24GB 卡分别显示 4.00/12.0 GB；未知卡回退 50.0/%；空单位与 % 不改变 raw 进度/状态。 |
| FPS 无需求/首帧未到/可信 0 | `:249`、`:260` | 未启用的值提供器抛异常以证明不可读取；可信 0 仅读一次，保留既有 pct=5 的规则；缺失硬件行跳过。 |
| DASH 文本、.Unit、.Color | `:280`～`:292` | UnitPanel 不改写插件文本或 USD 单位；空文本为 --；不访问硬件。 |
| 公开入口兼容与真实生产接线 | `:296`、`:322`、`:332`、`:348` | 两个公开重载维持默认值及 FPS 守卫；行为测试直接调用生产行写入方法并解析 v/u/pct/sts，另由 IL 断言 live snapshot 确实调用它。 |

测试 finally 恢复 Settings 单例、RAM/VRAM 容量、各卡字典、CommittedMemory 发布值与 culture（新测试 `:361`～`:394`）；磁盘测试恢复静态 DriveInfo 缓存（`:403`～`:418`）。没有 Load(forceReload)/Save 或用户配置目录写入。

## 冻结快照 SHA-256

| 文件 | SHA-256 |
| --- | --- |
| src/UI/RenderingTransitionCoordinator.cs | 21AF7C456819B0BB5A064827D773665F837FA3814E3181953F205F7A4D7D1CFB |
| src/UI/CapturePolicyCleanupLedger.cs | 69E5309F7B92F15EFE5559740ADD3C3524CE5B90F3C79FD5CFF55C750BDC67DE |
| src/UI/UIController.cs | F01022111C34891FE30CDEF19579B8EC70F79DD43BC93C5822FD943E288BB93F |
| tests/LiteMonitor.Tests/ReviewGpuRecoveryRegressionTests.cs | 9E332BC4DD05521C4793D1A9E4CE43E17B1D3962093E8B7799A438861421B95B |
| src/System/HardwareServices/DiskManager.cs | 3AA6E8F89D1837AFE0411C2431B47EF379AE8BE9A98E8F6AB4F1F788BCD0ECCE |
| resources/plugins/Stock.json | 0AD6AD3B6EFCDC20E6555AC6F7399251F3BFBFC9250C3926DB62A53A0846CBB9 |
| src/System/WebServer/LiteWebServer.cs | 901F3DBABA8364605240323B98B656C2F441DE0C5E032399D3E27E27197A1397 |
| tests/LiteMonitor.Tests/ReviewMetricPresentationRegressionTests.cs | 74DD3620945DA1A9B999011BB62C6516D0017672D86F89AA690B62916462D0B4 |

## 验证边界

本报告仅支持上述冻结文件的代码复审 PASS，未给出任何新测试通过计数。主控提到其另行调用了生产 WriteSnapshotItemJson 的正向探针；本复审未执行或独立读取其运行证据，不将该口头状态计为自己的实跑证据。

待主控统一完成：新增两套回归及相邻 GPU/cleanup、DiskUpdateSchedule、WebSnapshot/内存格式套件的 Debug/Release 执行，完整解决方案和主窗体构造检查。真实 GPU 设备故障、像素回读、窗口重建的实际画面与实机磁盘休眠未在本次验证；B/C 组及整批集成不在此 PASS 范围。

