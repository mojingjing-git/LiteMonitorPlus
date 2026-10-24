# LiteMonitor 全项目回归与上游继承问题独立审查

审查日期：2026-10-23（Asia/Shanghai）  
审查版本：`9238bb9` / `master`  
结论：**需要补修。确认 8 项产品缺陷（1 P1／7 P2），其中 2 项为最近修复新增回归、1 项为近期修复未覆盖的残留、5 项为历史／继承问题。**

提供的报告中，Debug 全量测试计数可以独立复现；但“未发现修复引入的新缺陷”的结论需要修正。下面 F01 是 R02 修复新增的呈现历史覆盖问题；F02 是 `74a09674`（LM-074）新增的状态机缺陷，现有单轮恢复测试没有覆盖第二轮恢复再次失败的情形。F05 也说明 R15／LM-094 的 IPv6 修复还存在表示规范化的缺口。

## 范围与证据

- 最近修改：`3b81b59..9238bb9`，72 个提交、60 个文件；其中 24 个生产代码文件位于 `src/` 和 `LiteMonitor.Updater/`。检查其差异及关键调用链，重点包括启动、GPU 故障回退与 WDA 清理、网页会话与重绑、插件执行与重定向、安装恢复、凭据备份和更新器。
- 扩展检查：硬件取值／缓存、采样调度、网络／磁盘、历史记录、自启动、主题和插件热加载等核心路径；结合 Git 历史区分上游、fork 来源和最近修改。
- 已同步远端。原始上游 `Diorser/LiteMonitor` 为 `3b91b5c`，fork 来源 `mojingjing-git/LiteMonitor` 为 `949835f`；本分支与两者的共同祖先均为 `169e90a`。历史存在分叉，使用源码和具体补丁核对，未把提交号不在祖先链中直接当成修复缺失。
- 在 `.review-scratch/review-20261023-independent/source/` 中从 `9238bb9` 导出干净代码，隔离现有未跟踪文件后进行 Debug／Release 构建和完整测试；补跑生产 MainForm 构造检查和测试计数门禁。
- 编写独立探针，调用实际程序集中的生产方法。八项产品发现均得到确定输出，并由独立断言脚本逐项核对。注入的手动计时器、假硬件和恢复委托只用于固定边界条件；没有安装／卸载驱动，也没有向外部服务发送探针请求。
- 本轮只增加审查报告和本地证据，没有修改业务实现或提交代码。

| 编号 | 严重度 | 问题 | 来源 |
| --- | --- | --- | --- |
| F01 | P1 | 新 GPU 宿主启动失败覆盖 HWND 呈现历史，回退跳过必要重建 | 最近新增回归，R02 |
| F02 | P2 | 第二次明确恢复成功后，旧失败记录仍永久阻塞 WDA 所有权 | 最近新增回归，LM-074 |
| F03 | P2 | 插件更新／卸载没有同步替换／退役现有调度任务 | 上游调度逻辑与 fork 市场热加载组合形成的历史缺陷 |
| F04 | P2 | 安装记录保存失败，市场仍宣告提交成功并删除恢复材料 | fork 历史问题，非最近新增 |
| F05 | P2 | 非压缩 IPv6 声明与实际请求主机表示不一致，合法请求被拒绝 | 最近修复的残留，非新增放行漏洞 |
| F06 | P2 | 物理磁盘缓存命中提前返回，盘符使用率不能正常读取 | 原始上游继承问题 |
| F07 | P2 | 股票 `us.NDX` 被清洗成 `us`，查询错误代码 | 原始上游继承问题，上游已有修复 |
| F08 | P2 | 网页忽略监控项的自定义单位，与桌面显示不一致 | 继承行为残留，上游已有修复 |

## F01：新宿主失败会清掉同一窗口已有的 GPU 呈现历史

定位：`src/UI/UIController.cs:1855`、`:573`；被调用的直接赋值位于 `src/UI/RenderingTransitionCoordinator.cs:551`。

`StartGpuPresentation()` 的新失败分支及 `ScheduleGpuFailureFallback()` 都把当前宿主的 `BindingState` 直接写回协调器。然而重建宿主不等于重建 HWND：旧宿主已经在该 HWND 上成功呈现过，新宿主仍可能在启动闸门／设备创建等阶段失败，状态保留初始 `Unbound`。这个 `Unbound` 会覆盖旧窗口的 `Bound`／`Presented` 历史。

**生产触发路径：**GPU 主题已运行 → 换用另一 GPU 配置，旧宿主无法原位重配置而被替换 → 新宿主在绑定前启动失败 → 失败分支写入 `Unbound` → 统一回退下一次执行 `RequiresControlledRecreate()` 得到 false → `CompleteLegacyTransition()` 在旧 HWND 上直接切回 GDI。

**复现：**探针给协调器登记已有 `Presented` 历史，使用未运行构造函数的 UIController 注入该协调器和一个普通未显示 Form，然后直接调用生产 `ScheduleGpuFailureFallback()`，传入新宿主绑定前启动失败的报告。没有触发硬件初始化、没有使用虚构的替代调度算法。实际输出：

```text
requiredBeforeFailure = true
bindingAfterFailure = Unbound
requiredAfterFailure = false
fallbackPending = true
```

**影响：**回退调度本身虽然已经统一，重建判据的输入却被降级，仍可能回到 R02 要修复的窗口冻结／黑屏路径。微软说明 flip 模型成功呈现后，与交换链关联的 HWND 的 GDI 绘制受到限制，销毁交换链也不会恢复这项能力；这也是项目已经采用按 HWND 历史重建的原因。参见 [Microsoft DXGI_SWAP_EFFECT 文档](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ne-dxgi-dxgi_swap_effect)。本轮实测的是生产状态路径中的历史丢失与错误重建判定，未进行真实 GPU 故障后的桌面像素验证。

**来源：**两处新增失败记录调用均由本轮 `9e3ba326` 引入。协调器直接赋值的实现虽然更早存在，但最近新增的 `Unbound` 失败报告写入路径使其能够抹掉旧宿主的窗口历史。

**修复建议：**在同一个 HWND 代次内，呈现历史只能从 Unbound 向 Bound／PresentAttempted／Presented 推进；仅在真实句柄代次变更时重置。新宿主的即时绑定状态应与窗口累积呈现历史分开记录。补“旧 HWND 已呈现、新宿主绑定前失败仍必须重建”的行为测试。

## F02：明确恢复再次失败后，后续成功仍无法解除闸门

定位：`src/UI/CapturePolicyCleanupLedger.cs:297`，`TryRearmForExplicitRecovery()`。

选择旧责任时只检查 `Failed && AffinityChangedBySession`，没有排除已经 `ReclaimedByExplicitRecovery` 的条目。第一次恢复将 A 标为已接管并创建 B；若 B 也耗尽预算，第二次恢复仍选中最早的 A，创建 C。C 成功后，B 仍然是未接管的失败责任，`GetBlockingReasonForGeneration()` 持续返回 `gpu_capture_policy_cleanup_unresolved`。

**复现：**原始责任 A 耗尽 → 第一次明确恢复 B 耗尽 → 第二次明确恢复 C 成功。实际输出：

```text
firstRearm = true
secondRearm = true
lastEntryState = Completed
remainingBlock = gpu_capture_policy_cleanup_unresolved
entries = [Failed/reclaimed, Failed/not-reclaimed, Completed]
```

**影响：**同一个 HWND 代次下，即使原始截图策略已经恢复，实时 GPU 背景仍然被拒绝；后续明确恢复重复选择 A，不能消除 B 的阻塞，通常要通过重建句柄或重启解脱。

**来源：**`git blame` 定位到 `74a09674`，属于本次 72 个提交中的新增回归。现有 `LM074_明确恢复成功后闸门解除...` 只验证 A 失败、B 成功，没有覆盖 A 和 B 连续失败。

**修复建议：**选择责任时排除 `ReclaimedByExplicitRecovery`，确保每次恢复接管当前尚未转交的失败责任；新增“两轮失败、第三轮成功后闸门解除”的回归用例，保留旧记录的失败审计信息。

## F03：模板热加载没有同步现有插件任务

定位：`src/Plugins/PluginManager.cs:253`、`:349`、`StartInstance()`；市场调用点为 `src/UI/Settings/PluginMarketPage.cs:369`、`:404`。

市场安装／更新和卸载后只调用 `LoadPlugins()`。运行中的计时器闭包仍持有启动时的 `inst`、`tmpl` 和刷新间隔：

- 更新时，`_templates` 虽已替换，现有任务仍执行旧模板；即使随后应用设置触发 `Reload()`，它只比较实例配置的序列化结果，模板变化不改变该指纹，任务仍被跳过。
- 卸载时，`LoadPlugins()` 从设置中删除实例和监控项，却没有调用 `StopInstance()`。旧任务的代次、取消令牌和捕获的 `inst.Enabled` 仍有效。

**复现：**加载指向 `old.example.test` 的模板并启动 → 文件改成 `new.example.test` → `LoadPlugins()` 加 `Reload()` → 手动驱动原任务 → 删除模板并再次加载 → 再驱动任务。实际输出：

```text
loadedUrl = https://new.example.test/data
executedUrls = [old, old, old]
timersCreated = 1
scheduleCountAfterUninstall = 1
remainingInstances = 0
```

**影响：**更新显示成功后仍使用旧接口／解析规则；卸载后还有后台任务。在网页服务运行时，生产可见性判断直接返回 true，这些任务还会继续执行网络请求。无可见需求时也仍保留轮询任务。

**来源：**只按实例配置判定重启的逻辑来自上游 `ebfdaa92`，在 fork 来源分支中也存在；市场的动态更新／卸载触发了这条未同步的运行态路径。不是 LM-083 最近调整启动恢复顺序所引入。

**修复建议：**热加载后统一对账运行任务：移除的实例立即取消并退役代次；模板内容或版本变化时替换任务并清理相关缓存。任务指纹应包含模板，不能只包含实例设置。分别覆盖更新和卸载后的执行行为。

## F04：设置写盘失败仍被当成安装事务成功

定位：`src/Plugins/PluginMarketState.cs:65`；提交清理点为 `src/Plugins/PluginMarket.cs:988`、`:993`。

`PluginMarketState.Upsert()` 修改内存列表后调用吞掉失败的 `settings.Save()`，不向安装事务返回真实保存结果。`CommitInstall()` 因此继续设置 `recordTouched = true`，删除旧插件备份和待恢复日志。

**复现：**先成功保存一份配置，再用 `FileShare.None` 实际锁住探针目录中的 `settings.json`，调用生产 `CommitInstall()` 及生产 `PluginMarketState.Upsert()`，最后释放锁并强制重新加载设置：

```text
initialSave = true
returnedSuccess = true
presentInMemory = true
presentAfterReload = false
journalExists = false
primaryExists = true
pluginFileExists = true
```

**影响：**界面报告安装成功，但重启后文件还在、管理记录却不存在。市场会显示未安装，同名文件可能阻止重新安装，卸载也会因非托管而拒绝；更新时则可能出现文件版本与保存的管理记录不一致。日志和旧文件备份已清理，启动恢复无法闭环。

**来源：**记录保存入口来自 fork 的 `579f8ed3`，早于本轮修复。LM-077 已为其他调用方增加了 `TrySave()`，市场记录尚未接入。

**修复建议：**让记录持久化失败可被事务层感知，同时撤销已经修改的内存状态；只有记录确实落盘后才清理日志和备份。不能仅在 `Save()` 后抛异常而不处理内存变更，因为当前 `recordTouched` 在回调返回后才置真。

## F05：IPv6 声明缺少与请求侧相同的规范化

定位：`src/Plugins/PluginProcessor.cs:452`，声明集合构造；比较位置为 `PluginSandbox.HostMatches()`。

LM-094 正确保留了 IPv6 方括号内的冒号，但最终仍把声明中的原始字符串放入集合。实际请求侧使用 `Uri.Host`，会压缩零段、移除段内前导零，双方表示不同。

**复现：**模板静态声明并请求同一个完整地址：

```text
url = https://[2001:0db8:0000:0000:0000:0000:0000:0001]/data
declared = [2001:0db8:0000:0000:0000:0000:0000:0001]
actualHost = [2001:db8::1]
result = LM009_CREDENTIAL_TARGET_NOT_ALLOWED
```

**影响：**显式声明的合法 IPv6 请求携带 Authorization 等凭据头时仍被沙箱拒绝。该问题是错误拒绝，没有证据表明它放宽了允许目标或泄漏凭据。

**来源：**属于 R15／LM-094 未覆盖的残留，不把修复前本来就失败的场景算成新增回归。原报告关于 .NET 8 的 `Uri.Host` 包含方括号是正确的，但方括号一致不足以保证地址表示一致。

**修复建议：**声明和请求侧共用相同的主机规范化过程，使用 URI／IP 解析后的规范表示比较；新增完整／压缩 IPv6、前导零及大小写等价形式的断言。

## F06：物理磁盘缓存遮蔽逻辑盘使用率读取

定位：`src/System/HardwareServices/DiskManager.cs:229`，盘符分支在 `:238`。

`GetBestValue()` 先读取缓存物理磁盘，只要处在扫描新鲜期就直接返回。`ReadDiskSensor()` 只识别 `DISK.Read`、`DISK.Write`、`DISK.Temp`，不能读取 `DISK.C.Used`；因此盘符请求可能在真正的 `DriveInfo` 分支前得到 null。

**复现：**用假物理磁盘挂入 `Computer`，实际调用生产 `DiskManager`；同一个管理器在建立物理磁盘缓存前后读取本机逻辑盘使用率：

```text
key = DISK.C.Used
before = 有效的实际使用率
after = null
```

**影响：**同时启用磁盘温度／读写和盘符使用率时，自动选择物理磁盘后的缓存窗口会让逻辑盘使用率显示缺失，网页快照也可能不包含该项。是否触发受首次读取顺序影响。

**来源：**缓存提前返回和盘符分支的先后关系已存在于共同祖先 `169e90a`；`git blame` 分别追到上游 `8489df42`／`53d036c7`。本分支后来的单调时钟改造保留了原顺序。

**修复建议：**把 `DISK.<盘符>.Used` 的处理放到物理磁盘缓存之前，或者严格限制缓存提前返回所覆盖的键。

## F07：纳斯达克代码被后缀清洗规则截掉

定位：`resources/plugins/Stock.json:57`，后续请求模板为 `:64`。

搜索步骤对结果代码应用 `\.[A-Z]+$` 删除规则。输入 `us.NDX` 时会删除 `.NDX`，留下非空的 `us`，所以后续 `{{code ?? query}}` 不会回落到用户输入。

**复现：**加载本仓库实际 Stock 模板，以搜索返回值 `code=us.NDX`、输入 `query=NDX` 驱动实际转换和模板解析：

```text
processedCode = us
resolvedFetchUrl = http://qt.gtimg.cn/q=us
```

**影响：**这类美股指数查询发送错误的证券代码，不能正常取得行情。

**来源：**问题规则来自上游 `b3fcc261`。上游已经在 [a71ce75](https://github.com/Diorser/LiteMonitor/commit/a71ce7584d892230d157c02047bf052fad6a738f) 中增加先处理 `^us\.` 的规则；本分支的 Stock 模板没有该补丁。这里验证的是本地确定的转换结果，没有把外部行情 API 的当前返回值当作本轮实测。

**修复建议：**带入针对美股前缀的规范化，并使用 `us.NDX` 等具体输入固定转换结果，保留其他市场后缀处理的行为。

## F08：网页数值和单位未使用监控项自定义格式

定位：`src/System/WebServer/LiteWebServer.cs:579`；数值格式化入口为 `:717`。

网页快照调用 `WebSnapshotValues.ResolveValue()` 时没有传递 `item.UnitPanel`；单位也直接使用 `GetUnitStr()`，没有再经过 `GetDisplayUnit()`。桌面 `MetricItem` 已传递该格式，因此网页与桌面走出不同结果。

**复现：**全局容量模式、物理内存 32 GB、使用率 50%，该监控项的 `UnitPanel="%"`。实际调用生产桌面格式化方法和网页数值辅助方法，再按快照调用点取得网页单位：

```text
configuredUnit = %
panel = 50.0%
webValue = 16.0
webUnit = GB
```

**影响：**用户在设置中为该项选择百分比后，网页仍显示容量；隐藏单位和其他单位自定义也未被同一路径遵循。网页自身的容量数值与 GB 单位匹配，本项不是“网页把 16 GB 错标为 16%”。

**来源：**上游 [a71ce75](https://github.com/Diorser/LiteMonitor/commit/a71ce7584d892230d157c02047bf052fad6a738f) 同时修改了桌面和网页调用点。本分支已具备桌面／格式化辅助函数一侧的修复，网页改造后的路径仍缺这一半。

**修复建议：**网页数值格式化携带项级单位格式，最终单位经过相同覆盖规则；给实际快照构造路径补上自定义 `%`、空单位和默认单位的断言。

## 实际验证结果

环境：Windows，.NET SDK `8.0.426`。市场契约测试使用仓库本地夹具；联网 E2E 未开启。

| 验证 | 结果 |
| --- | --- |
| Debug 全解决方案构建及测试 | 总计 1632，1631 通过、0 失败、1 跳过 |
| Release 全解决方案构建及测试 | 总计 1632，1630 通过、1 失败、1 跳过 |
| 生产 MainForm 构造检查 | 退出码 0，构造成功，网页服务未监听；没有运行 OnShown／消息循环 |
| 测试计数门禁 | 通过；阈值 1632，MarketIndexContractTests 6 项均执行并通过 |
| 独立产品问题探针 | 8 项均复现，额外断言脚本逐项确认输出 |
| Release 失败项 IL 复核 | 确认测试误报，见下文 |

唯一的 Release 失败为 `HttpLifecycleTests.更新元数据取数路径释放了请求响应与Json文档`。它通过 Dispose 调用前的 `ldfld` 判断释放对象，假设所有 using 对象都会被提升到异步状态机字段。Release 编译器把最后一次 await 后创建的 `JsonDocument` 留在局部变量中：实测局部变量 4 是 `JsonDocument`，IL `1184..1190` 包含局部变量载入、空检查和 Dispose，方法共存在 6 处 Dispose 调用。测试只识别到其他 5 个状态机字段，因此错误判成资源泄漏。

建议修正该测试对局部变量释放的识别，或改用能直接观察资源生命周期的断言。本项是测试维护问题，未计入上述八项产品缺陷。Debug 的历史 CI 绿色不能用于声称 Release 配置的测试也全部通过。

## 与既有报告的关系及边界

- R01 的构造顺序修复及本轮实际启动构造检查通过；本轮未重现原启动空引用。
- R02 的统一故障回退入口已经存在，但不能据此判定重建一定正确，F01 是其新增回归。R03 的 LAN 运行态清口令守卫、R04 的安装恢复路径重建、R05 的默认关闭摘要来源和不兼容提示机制，在已检查路径上没有发现其他同等级问题。没有把自动化测试通过推论成真实 GPU 设备丢失／多显示器长时间运行已经通过。
- 原报告关于 IPv6 方括号的判断正确，但缺少地址规范化等价场景；F05 补充的是这部分。
- 原报告列出的旧备份一次性清理缺口、LAN 改为仅本机同时清口令的保守停服行为，以及旧客户端升级需手动下载的发布说明要求，仍应保留，不重复计为本轮新发现。
- 没有执行真实联网市场 E2E、真实版本升级／签名发布、驱动安装／卸载、GPU 设备故障注入、多屏不同 DPI 或长时间压力测试；本报告不为这些项目给出通过结论。
- 没有为了验证发现而改动生产实现；本轮没有执行修复后的回归测试。

## 证据与复跑

审查证据目录：`docs/reviews/evidence/full-project-20261023-9238bb9/`（仓库既有规则将证据目录忽略，文件保留在本机）。

- `probe-results.json`：八项产品缺陷及 Release IL 复核的原始结构化输出。
- `probe-verification.log`：逐项断言通过的记录。此处通过表示“缺陷得到复现”，不表示产品实现正确。
- `test-summary.json`：按配置和测试程序集提取的 TRX 计数。
- `gate-counts.log`：测试门禁完整输出。
- `startup-ok.json`：本轮生产 MainForm 构造检查输出。

探针源码：`.review-scratch/review-20261023-independent/probes/Program.cs`；工程文件在同目录，引用隔离副本中的主程序，并复用仓库的 IL 读取辅助类。复跑入口在 `.review-scratch/review-20261023-independent/`：

```powershell
dotnet run --project probes/ReviewProbes.csproj -c Release
pwsh -NoProfile -File verify-probe-results.ps1
```

验证脚本对八项已知缺陷的具体输出作断言，修复实现后对应断言应不再成立。独立探针不属于主线测试集合，没有把它们加进 1632 的计数。

修复次序建议：先处理 F01，再处理 F02 和 F03／F04 的恢复与运行任务一致性，再处理 F05 的合法请求误拒绝；F06～F08 可按功能模块独立修复。无需因为 F07／F08 而直接合并整个上游分支，应分别带入已确认适用的补丁及行为测试。
