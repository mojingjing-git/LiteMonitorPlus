# 近期改动独立审查

日期：2026-10-23。结论：**REQUEST_CHANGES；存在启动阻断、运行时回退和安全边界缺陷，不建议按当前状态发布。**

## 1. 审查范围与证据口径

- 基线：`0f1177e0d005c2f5b6b68a81dfc29de9722ac263`。
- 被审版本：`3b81b596ecd6c6459d1b3266ee06b458167a3659`。
- 范围：两者之间 62 个提交、103 个变更文件；审查开始时工作区干净。
- 主审负责 GPU 生产接线、旧主题兼容与结果交叉核对；5 个并行审查任务分别检查网页认证、插件执行、更新器/市场/凭据、GPU 资源清理、CI/探针。
- 本轮为静态审查：读取差异、完整相关实现、调用者和测试；未修改实现，未构建、运行测试、启动 GUI、开启捕获或网络服务。本文为唯一新增文件。
- 下文“确认”指代码调用链或状态推导成立，不冒充实机复现。部署权限、显示器行为和本地攻击者能力仍有各自前提。
- 历史 CI 的 1396 条测试与 PASS_WITH_NOTES 不是本轮执行结果。原台账已经把 T06–T08、G01–G12 和若干实机项标为未完成，本报告不把这些声明本身当成虚假通过。

## 2. 发现总览

按独立修复点登记 **25 项：5 项 P1、20 项 P2**。其中 R05 是明确的不兼容设计缺少升级迁移方案；其他项为实现错误或未闭环的运行路径。P1 应先修，P2 的具体触发面应在修复后分别验收。

| 编号 | 级别 | 模块 | 问题 |
| --- | --- | --- | --- |
| R01 | P1 | 启动 | 新样式查询在窗口助手初始化前执行，普通启动空引用 |
| R02 | P1 | GPU | 运行故障后的自动回退仍未进入完整 HWND 恢复流程 |
| R03 | P1 | 网页 | LAN 服务运行中清除哈希口令，会留下无口令监听 |
| R04 | P1 | 市场 | 恢复日志路径未校验，可删除或覆盖插件目录外文件 |
| R05 | P1 | 更新器 | 拒绝旧调用协议，却没有旧客户端升级桥接 |
| R06 | P2 | 主题 | 切到原生旧主题仍套用中性回退颜色 |
| R07 | P2 | 字体 | 派生主题漏复制运行期字体字段 |
| R08 | P2 | 旧 M2 | HWND 重建后未按配置恢复旧实时玻璃 |
| R09 | P2 | WDA | 重配置取得捕获策略所有权时绕过 pending 检查 |
| R10 | P2 | WDA | 重试耗尽被误当成允许重新取得策略所有权 |
| R11 | P2 | 网页 | 修改监听范围不重绑，实际地址展示读取草稿 |
| R12 | P2 | 网页 | 已连接 WS 不随会话注销、过期而关闭 |
| R13 | P2 | 配置 | 口令迁移忽略保存失败，报告迁移成功 |
| R14 | P2 | 插件 | 合并请求没有包含白名单策略，复用绕过调用者限制 |
| R15 | P2 | 插件 | IPv6 主机截断发生地址碰撞，重定向可越过目标限制 |
| R16 | P2 | 插件 | 带查询参数的无路径 URL 被误解析为动态或错误主机 |
| R17 | P2 | 插件 | 手动重定向把 HEAD 改成 GET |
| R18 | P2 | 原生插件 | 请求按实例 ID 合并，重叠执行的取消相互影响 |
| R19 | P2 | 市场 | 启动恢复晚于插件加载，磁盘回滚但内存仍运行新版本 |
| R20 | P2 | 市场 | 下载正文缺少总超时，安装可无限等待 |
| R21 | P2 | 凭据 | 加密主配置后，备份仍保留旧插件密钥明文 |
| R22 | P2 | 探针 | 持续送帧跨 UI 线程，后台任务失败未纳入结果 |
| R23 | P2 | 探针 | MainForm 隔离门在 OnShown 启动服务之前检查 |
| R24 | P2 | 探针 | 未创建哈希守卫时仍输出源码未漂移 |
| R25 | P2 | 发布 | Release 未接入新增依赖声明核对门禁 |

## 3. P1：优先修复

### R01：普通启动会空引用，与是否启用 GPU 无关

位置：[UIController.cs:1182](G:/TOOL/LiteMonitor-fork/src/UI/UIController.cs:1182)、[MainForm_Transparent.cs:89](G:/TOOL/LiteMonitor-fork/src/UI/MainForm_Transparent.cs:89)。

调用顺序是 MainForm 构造函数第 220 行 `new UIController(...)` → UIController 构造函数第 150 行 `ApplyTheme` → 第 1182 行求值 `MainForm.IsWindowLayered()`。但 `_winHelper` 的唯一赋值在 MainForm 第 227 行，尚未执行，第 89 行直接解引用 null。

所有主题都会先求值请求参数，再进入路由策略判断，所以旧主题、GPU 关闭、安全模式均不能绕过。异常还发生在协调器内部 catch 之前。这是当前源码的启动阻断，不是“只影响新主题”。

**修复：**调整初始化依赖，先提供可用的窗口属性查询，再创建 UIController；或将路由启动推迟到窗口基础设施就绪。不能简单把 null 当作“非 layered”永久缓存。补真正构造生产主窗体的隔离启动检查，覆盖旧主题与 GPU 关闭。

### R02：运行故障仍会在原 flip HWND 上尝试 GDI 回退

位置：[UIController.cs:1575](G:/TOOL/LiteMonitor-fork/src/UI/UIController.cs:1575)、[GpuPresentationHostController.cs:678](G:/TOOL/LiteMonitor-fork/src/Rendering/Presentation/GpuPresentationHostController.cs:678)。

GPU 已成功呈现后发生故障，宿主 `OnRenderFailure` 只记录错误、设置恢复标志、`StopCore()`。UI Tick 尝试有限重启；启动失败或恢复耗尽时，`StartGpuPresentation` 仅标记耗尽、释放宿主并 Invalidate，没有调用 `RunGpuToLegacyTransition`，也没有按本代次呈现历史恢复 HWND。

因此手动关闭 GPU 新增的受控重建不会覆盖故障回退。原 HWND 已经进入 flip 呈现，旧画面可能冻结，后续 GDI 绘制不能据此恢复。恢复分支也没有同步完整的有效路由/首帧状态。

**修复：**启动失败、首帧失败、运行故障恢复耗尽统一调度到 legacy 转换入口；在 UI 安全点执行，保留失败原因，按 HWND 历史决定重建。不得只补 Invalidate 或继续标记 Stable。

依据：[微软 DXGI 文档](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ne-dxgi-dxgi_swap_effect)描述了 flip 呈现后同一 HWND 的普通 GDI 限制。当前代码调用的是 `Present`，不要误写成调用 `Present1`。此项是原修复目标仍未覆盖的路径，不将其宣称为本轮实机复现。

### R03：清除口令后，运行中的 LAN 服务可接受无口令登录

位置：[MainPanelPage.cs:418](G:/TOOL/LiteMonitor-fork/src/UI/Settings/MainPanelPage.cs:418)、[AppActions.cs:255](G:/TOOL/LiteMonitor-fork/src/Core/Actions/AppActions.cs:255)、[WebSessionManager.cs:1290](G:/TOOL/LiteMonitor-fork/src/System/WebServer/WebSessionManager.cs:1290)。

前提：从已经保存为哈希的配置启动 LAN 服务，此时明文 `WebServerPassword` 与服务器保存的 `CurrentPassword` 都为空。设置页清除口令会清空哈希、盐和明文。应用时只比较端口及明文，判断无需重启，LAN 监听继续存在。

新请求按当前配置判断无需认证，登录直接创建 L0 会话；允许的 Host 可取得快照。LAN“必须有口令”的约束只在冷启动校验，没有覆盖运行中变更。

**修复：**按有效凭据指纹与运行时策略检测变更；应用 LAN 空口令配置时停止服务或明确拒绝应用。敏感策略变化须撤销会话、票据和已有 WS。补“哈希配置启动 → 清除 → 应用”的入口测试。

### R04：插件恢复日志可驱动目录外文件删除和覆盖

位置：[PluginMarket.cs:965](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginMarket.cs:965)。

恢复日志反序列化后的 `Target/Stage/Backup` 直接传给 `Path.Combine`，随后执行 Delete/Move，没有目录边界、插件 ID、事务文件命名或重解析点检查。例如 Stage 为 `..\..\settings.json` 且文件存在时，会进入第 978 行删除该文件；其他状态会按日志字段覆盖目标。

这需要能够修改本地事务日志，不是单凭远程索引即可利用。但损坏或被篡改的恢复材料不应具备任意路径写删能力；程序提权运行且目录允许较低权限写入时，后果更严重，实际权限条件需部署检查。

**修复：**校验插件 ID 与事务 ID，并据此重建直接子文件名；拒绝绝对路径、目录分隔符、越界路径和不受信任重解析点。坏日志隔离留证，不执行其中任意路径。补恶意日志恢复测试。

### R05：新更新器协议缺少旧客户端升级桥接

位置：[Program.cs:128](G:/TOOL/LiteMonitor-fork/LiteMonitor.Updater/Program.cs:128)。

基线 UpdateDialog 会从新包预更新 updater，再仅传 ZIP 路径启动它。新 updater 明确拒绝单参数并返回错误码 6；旧主程序只看到进程启动成功，随后退出，不会等待更新器拒绝，也不会重启旧程序。再次尝试仍是同一路径。

拒绝无摘要调用是代码中明确的有意变更，本项不建议撤销完整性校验。缺少的是旧客户端到新协议的发布迁移方案，当前新增测试只证明“应该拒绝”，未证明已有用户能升级。

**修复：**设计保留完整性验证的兼容入口（例如由可信签名元数据独立验证包），或规划并验证实际可达的协议过渡发布。增加旧客户端 → 新完整发布包的交接验收；若只支持手动升级，须明确用户提示和发布限制。

## 4. P2：GPU 与旧主题兼容

### R06：原生旧主题被错误套用 GPU 回退配色

位置：[UIController.cs:1397](G:/TOOL/LiteMonitor-fork/src/UI/UIController.cs:1397)。

GPU 运行后选择 `LiquidGlass.json` 等普通旧主题，仍进入 CompleteLegacyTransition，无条件 Resolve 目标主题的 fallback。旧主题没有 rendering，resolver 返回中性默认值，覆盖背景、分组、正文和标题等色槽。之后 RefreshLegacyFallbackTheme 继续重算覆盖，直到重新进入 GPU 或重启。

**修复/验证：**区分“GPU 主题被迫回退”和“用户选中了原生 legacy 主题”；后者清空派生覆盖，使用主题本身。补 GPU → 两个不同旧主题的真实入口颜色对照。此问题仅改变进程内显示，没有写坏主题文件。

### R07：派生主题遗漏运行期字体，实际绘制使用系统默认字体

位置：[RenderingTransitionCoordinator.cs:748](G:/TOOL/LiteMonitor-fork/src/UI/RenderingTransitionCoordinator.cs:748)。

DeriveConfig 只复制 public 属性，但 Theme 的 FontTitle/FontGroup/FontItem/FontValue 是字段。新建派生 Theme 后这些字段保持 CaptionFont；没有重新 BuildFonts/Scale。UIRenderer 和 HorizontalRenderer 直接读取它们，导致字号、字体、粗体及高 DPI 缩放丢失，竖向布局还可能按原字体测量、按默认字体绘制。

**修复/验证：**采用明确的运行期主题派生接口，保留已构建字体及所有权，或重新创建等效字体并负责释放；避免再次缩放共享 Layout。测试应断言运行期 Font 字段和实际测量，而不仅是 FontConfig 属性相等。

### R08：受控重建永久停用配置仍开启的旧 M2

位置：[UIController.cs:1353](G:/TOOL/LiteMonitor-fork/src/UI/UIController.cs:1353)、[LiveGlassController.cs:307](G:/TOOL/LiteMonitor-fork/src/UI/LiveGlassController.cs:307)。

`LiveGlassEnabled=true` 时从 GPU 回到旧 M2 主题，重建触发 OnHandleDestroyed → StopLiveGlass → SetEnabled(false)，清空服务。HandleCreated 只 RefreshRequest，服务为 null 时立即返回；完成切换也没有按配置恢复。设置仍显示开启，但捕获只能手动重开。

**修复/验证：**将临时停机与用户关闭分开，目标 legacy 就绪后按配置恢复服务，遵守单一捕获所有者。覆盖停止、重建、显示和恢复出帧的完整生命周期。

### R09：TryReconfigure 取得 WDA 时绕过未完成清理检查

位置：[GpuPresentationHostController.cs:522](G:/TOOL/LiteMonitor-fork/src/Rendering/Presentation/GpuPresentationHostController.cs:522)。

旧实时会话恢复失败并移交 ledger 后，同一 HWND 可先启动 fixed 宿主。清理尚未完成时开启 live，TryReconfigure 直接 SetCapturePolicy；只有 Start 查询 pending。新会话会把遗留 0x11 当成外部原值，随后旧 ledger 恢复原策略，可能撤掉正在使用的排除并产生自捕获。

**修复/验证：**全部取得 WDA 的入口共用所有权检查，包括重配置与恢复；验证 pending → fixed → live 的状态组合，而非只检查 Start。

### R10：恢复耗尽后仍允许在同一 HWND 取得新 WDA 所有权

位置：[CapturePolicyCleanupLedger.cs:129](G:/TOOL/LiteMonitor-fork/src/UI/CapturePolicyCleanupLedger.cs:129)。

重试耗尽将记录设为 Failed，IsTerminal 为 true，HasPendingForGeneration 随即返回 false。但原策略并未恢复。新 live 会话读到残留 0x11，将其当作原始外部状态，不承担恢复；旧记录又不再重试，之后关闭 live 仍可能保持截图排除，直到 HWND 销毁。

**修复/验证：**区分“停止重试”与“安全恢复”；同代次 Failed 必须阻止新所有者，并经明确恢复或受控重建解除。新增耗尽后重启和关闭的用例。

## 5. P2：网页协议与凭据迁移

### R11：监听范围变更不生效，实际地址还显示草稿值

位置：[AppActions.cs:255](G:/TOOL/LiteMonitor-fork/src/Core/Actions/AppActions.cs:255)、[MainPanelPage.cs:451](G:/TOOL/LiteMonitor-fork/src/UI/Settings/MainPanelPage.cs:451)。

只修改 Loopback/LAN 不触发服务器重启。Loopback → LAN 仍无法远程连接；LAN → Loopback 仍监听所有网卡。界面却用草稿 BindMode 拼“实际地址”。Host 白名单不能代替网络绑定，远端客户端可以自行填写 Host。

**修复/验证：**运行时保存并比较监听策略，范围变化时重新绑定；地址显示读取真实端点。补两个方向的设置应用和连接测试。

### R12：WS 建立后脱离会话，注销与过期不撤销已有访问

位置：[WebSessionManager.cs:910](G:/TOOL/LiteMonitor-fork/src/System/WebServer/WebSessionManager.cs:910)、[WebSessionManager.cs:2231](G:/TOOL/LiteMonitor-fork/src/System/WebServer/WebSessionManager.cs:2231)。

RevokeSession 只删除会话记录；WS 连接仅保存服务代次，广播不复核所属会话。先建立 WS 再通过另一 HTTP 连接注销，原连接继续收到数据；绝对期限同样不能终止它。持续观看也不维护 LastUsedTicks，超过空闲期限后断线重取票据会失败。

**修复/验证：**记录 WS → session 关联；注销、凭据变更和期限到期时关闭对应连接、回收采样需求；明确长连接活动计时策略。覆盖“建立 WS 后”的注销与过期，不仅检查 HTTP 请求。

### R13：口令迁移把吞异常的 Save 当作成功

位置：[Settings.cs:546](G:/TOOL/LiteMonitor-fork/src/Core/Settings.cs:546)、[SettingsHelper.cs:181](G:/TOOL/LiteMonitor-fork/src/Core/SettingsHelper.cs:181)。

EnsureWebPasswordMigrated 调用 Save 后返回 true；Save 在禁止保存时直接返回，遇到 IO 错误则吞异常。旧明文配置可能仍在磁盘，登录也不会得到设计中的迁移失败响应。

**修复/验证：**提供可报告写入结果的持久化入口，成功落盘后才报告迁移完成。覆盖配置不可写、文件占用和 GlobalBlockSave，不改变通用 Save 的所有调用者语义。

## 6. P2：插件沙箱与 HTTP 行为

### R14：请求合并键遗漏允许目标策略

位置：[PluginExecutor.cs:490](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginExecutor.cs:490)。

inflightKey 只有请求摘要，声明主机检查只在首个请求工厂执行。相同 A URL、相同请求头的两个调用者，甲允许 A/B、乙只允许 A；甲先请求且 A 跳 B 时，乙可直接复用 B 的响应，不执行自身的拒绝策略。

**修复/验证：**把规范化后的允许目标与凭据策略纳入合并身份，复用前也应完成调用者校验。将并发合并测试与不同白名单重定向反例组合。

### R15：IPv6 StripPort 产生碰撞，台账“均为 fail-closed”不完整

位置：[PluginExecutor.cs:847](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginExecutor.cs:847)。

Uri.Host 不含端口，StripPort 却按最后一个冒号删除尾部。`[fd00::1]` 与 `[fd00::2]` 都被化成 `[fd00:`。无凭据路径会把初始 URL 的完整 Host 纳入 hopAllowedHosts，因此不同 IPv6 地址之间重定向能被误判为同一允许主机。

**修复/验证：**主机与端口分别使用 URI 语义解析，IPv6 字面量完整规范化。补不同 IPv6 地址的逐跳拒绝。原台账 LM-058 的凭据声明解析失败路径确实偏严，但不能据此推导所有 IPv6 路径均不会漏放行。

### R16：无路径 URL 的查询串被当作 authority

位置：[PluginProcessor.cs:421](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginProcessor.cs:421)。

AddDeclaredHost 只按 `/` 截 authority，没有处理 `?`、`#`。例如 `https://api.example?symbol={{symbol}}` 会因查询占位符被判为动态主机；固定查询串也会进入主机名。合法的 Authorization/API Key 插件请求因此被拒绝。

**修复/验证：**按 URI authority 边界识别静态主机，占位符只检查主机部分。覆盖无路径 URL、查询、片段及 IPv6，不使用字符串冒号截断。

### R17：HEAD 经手动重定向后变成 GET

位置：[PluginExecutor.cs:1006](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginExecutor.cs:1006)。

301/302/303 路径按状态码无条件将方法设为 GET，丢掉 HEAD 语义。探测请求变为下载正文，可能触发超限或超时，目标只接受 HEAD 时也会失败；后续 HEAD 空正文返回分支失效。

**修复/验证：**重写同时考虑状态码与当前方法，保留 HEAD；补 HEAD 与 POST/GET 的逐状态码对照。

### R18：原生 Crypto 请求按实例而非执行合并，取消可相互影响

位置：[CryptoNative.cs:124](G:/TOOL/LiteMonitor-fork/src/Plugins/Native/CryptoNative.cs:124)、[PluginExecutor.cs:479](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginExecutor.cs:479)。

所谓执行作用域键实际传入实例 ID。生产 RestartInstance/Reload 停旧后即启新，StopInstance 只 Cancel/Dispose，没有等待执行任务收尾；新计时器首次延迟为 50ms。如果同交易对没有新鲜缓存，且旧 Crypto 条目到新执行进入时尚未从静态合并表移除，新执行会复用绑定旧令牌的任务，收到旧代次的取消异常并被记为失败。

这是有生产入口的跨代次竞态，不是正常周期调度必然并发，也不声称每次重启都会触发或已证明长期无法取消。调用顺序见 [PluginManager.cs:402](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginManager.cs:402)、[PluginManager.cs:435](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginManager.cs:435)。现有调度测试先等待旧执行结束再触发新计时器，没有覆盖该窗口。

**修复/验证：**使用每次执行唯一的作用域标识，或实现独立等待者取消与共享请求生命周期。补旧任务退出与新任务启动重叠场景，不只测单次执行超时。

## 7. P2：市场安装与密钥保存

### R19：事务恢复晚于首次加载，回滚只修磁盘

位置：[PluginMarket.cs:186](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginMarket.cs:186)、[MainForm_Transparent.cs:213](G:/TOOL/LiteMonitor-fork/src/UI/MainForm_Transparent.cs:213)。

启动先 LoadPlugins、Start，后在 PluginSelfCheck 首次使用 PluginMarket 时恢复。若上次在替换插件文件后、写安装记录前崩溃，本次已把新模板载入并开始调度，随后磁盘才回滚旧文件，没有同步撤销内存新模板。

**修复/验证：**首次扫描与调度前完成恢复，避免静态构造时机承担关键顺序。新增崩溃恢复后的模板版本与运行输出断言，而不只比较磁盘文件。

### R20：市场流式下载缺少覆盖正文的超时

位置：[PluginMarket.cs:607](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginMarket.cs:607)、[PluginMarket.cs:698](G:/TOOL/LiteMonitor-fork/src/Plugins/PluginMarket.cs:698)。

改用 ResponseHeadersRead 后，HttpClient 的 30 秒超时只约束到响应头完成；后续读正文使用调用方 ct，而市场页面安装未传可取消令牌。返回响应头后不再发送正文的服务器可让安装和页面 busy 一直等待，字节上限不能约束“始终不返回下一字节”。

**修复/验证：**建立覆盖重定向、响应头、正文和解析全过程的总截止时间，并接通用户取消。补“头成功、正文停滞”。超时语义参见[微软 HttpCompletionOption 文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption)。

### R21：新主配置加密，但旧插件明文进入备份

位置：[Settings.cs:1088](G:/TOOL/LiteMonitor-fork/src/Core/Settings.cs:1088)、[SettingsHelper.cs:328](G:/TOOL/LiteMonitor-fork/src/Core/SettingsHelper.cs:328)。

首次保存旧配置时，转换器加密新主文件里的 api_key/token；AtomicWrite 又把旧主文件原样复制为 settings.json.bak。备份清理只移除 WebServerPassword，不转换插件参数。成功保存后，插件密钥仍可从备份读取。

**修复/验证：**迁移主文件和备份中的全局/目标凭据，保持备份可恢复，不以简单删除有效备份替代。测试从旧明文配置起步，检查保存后的主文件及备份均无原始密钥。

## 8. P2：验证工具和发布门禁

### R22：T01 持续送帧任务跨线程失败，实验未报告

位置：[RecreateHandleScenario.cs:394](G:/TOOL/LiteMonitor-fork/tools/GpuHostLifecycleProbe/RecreateHandleScenario.cs:394)。

PumpScene 在 Task.Run 内调用要求 UI 创建线程的宿主 UpdateScene，触发 InvalidOperationException；catch 未覆盖且任务未等待。初始 UI 线程主动送的一帧仍可能成功，主实验仍可退出 0，但不能据此证明后台持续场景输入成功。

**修复/验证：**改为 UI 线程定时送帧，记录真实送帧数量和失败，并在实验结束等待/终止任务。此项不自动推翻独立成立的初始 GPU 帧与后续 GDI 桌面像素 A/B 观察。

### R23：真实 MainForm 隔离只检查构造完成时，不约束 OnShown

位置：[RealMainFormScenario.cs:50](G:/TOOL/LiteMonitor-fork/tools/GpuHostLifecycleProbe/RealMainFormScenario.cs:50)。

隐藏托盘且 WebServerEnabled=true 时，构造后服务尚未启动，隔离门通过；Application.Run 触发生产 OnShown 后会开启监听。最后的 TCP 快照又是在 WriteEvidence 之后追加，没有写入该份结果。

**修复/验证：**显示前验证/拒绝有副作用的配置，显示后及结束前复查，最后一次观察完成后再写证据。当前 HEAD 会先受 R01 阻断，本项针对探针隔离设计；不声称本轮已实际启动了服务。

### R24：无哈希守卫时仍声明未漂移

位置：[RecreateHandleScenario.cs:715](G:/TOOL/LiteMonitor-fork/tools/GpuHostLifecycleProbe/RecreateHandleScenario.cs:715)、[CandidateHashGuard.cs:37](G:/TOOL/LiteMonitor-fork/tools/GpuHostLifecycleProbe/CandidateHashGuard.cs:37)。

探针从仓库外隔离目录运行时，向上找不到源码，guard 为 null。但 unchanged 初始为 true，结果仍可写 sourceHashChanged=false 和安全结论。没有测量与测量后未变化被混为一谈。

**修复/验证：**显式传入候选源码位置/清单；无法测量时输出 NOT_VERIFIED，不能给已验证未漂移的布尔值。独立报告技术 A/B 与候选身份约束是否完成。

### R25：Release 绕过新增依赖声明检查

位置：[release.yml:86](G:/TOOL/LiteMonitor-fork/.github/workflows/release.yml:86)、[ci.yml:121](G:/TOOL/LiteMonitor-fork/.github/workflows/ci.yml:121)。

普通 CI 已调用 generate-dependency-manifest -Mode Check；人工 Release 没有同一步骤，也不依赖该 CI 成功。依赖变更遗漏 NOTICE 时，CI 可红，而 Release 仍构建、签名并上传草稿资产。

**修复/验证：**在 Release 还原构建后执行同一门禁，确保退出码传播；用依赖清单故意不一致的候选验证失败。当前流程只上传草稿，本项不描述为已经自动公开发布。

## 9. 对本批质量的评价

### 已有增强

- 将 WDA 清理责任从已释放宿主转交独立 ledger，方向正确，订阅与 finally 兜底已实际接上。
- 更新器增加预期摘要与读取字节绑定，插件 HTTP 关闭自动重定向并逐跳检查，均缩小了原有风险面。
- Web 会话/票据、默认环回监听、配置哈希与插件凭据保护都有实质实现。
- 测试、变异测试、测试发现数量检查和候选证据比基线丰富；部分环境未验证项有明确登记。

这些是静态实现层面的积极判断，不等于本轮完成了动态验收。

### 主要短板

1. **组件测试强，生产组合测试弱。**大量 IL 护栏可以证明“有调用某方法”，无法证明构造顺序可用、目标主题未变色、字体对象正确或故障后屏幕能恢复。
2. **冷启动与运行时变更没有形成对称矩阵。**LAN 冷启动拒绝空口令，但运行中清除口令绕过；GPU 手动退出有 HWND 重建，故障回退没有。
3. **安全状态由多个不一致的身份承担。**请求摘要不含白名单、WDA 重试结束不等于恢复成功、WS 连接不绑定会话，均会使单点校验被旁路。
4. **迁移与恢复场景不完整。**旧 updater 协议、备份密钥、插件加载前恢复都需要真实的升级/崩溃链路测试。

因此，本批可评价为“有实质增强，但尚不能把局部 PASS 外推为可发布”。R01 也表明最后一次真实主程序启动不能被纯单测总数替代。

## 10. 建议修复顺序与后续验收

1. **先修 R01 并取得当前候选的隔离启动证据。**旧主题、GPU 关闭、安全模式、GPU 开启都要覆盖；不将其混成一次 GPU 演示。
2. **并行修 P1 的三个独立方向：**GPU 故障回退 R02；网页动态策略 R03/R11；市场日志边界 R04。R05 由更新协议 owner 提交可审阅的迁移方案。
3. **GPU owner 一次补齐 R06–R10。**用用户入口验 GPU→原生旧主题、GPU→同主题 legacy、故障→legacy、legacy→GPU、旧 M2 恢复，实际画面、字体、affinity 和资源同时检查。
4. **网页/插件/市场 owner 按模块修 R12–R21。**重点组合不同白名单、并发、注销、超时及旧配置，而不是继续只增加正常路径用例。
5. **先修探针，再补实机，最后对齐发布门禁。**R22–R25 修完后重新绑定候选版本与结果；保留原记录，不把旧 T01 或旧 CI 直接当最终 HEAD 的通过证据。
6. 独立复核修复，逐项记录 FIXED / NOT_VERIFIED / BLOCKED。未取得当前候选的必要实机证据时，只能说“实现修复、实机待验”。

本报告不执行上述修复、不重新安排原性能跑批，也不替用户合并或发布。
