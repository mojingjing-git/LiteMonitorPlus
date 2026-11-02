# 已知缺口与限制

本文描述 LiteMonitor++ 当前在架构层面与产品层面客观存在的限制。每一条限制由四个部分构成：

- **现象** —— 当前可被代码证实或被脚本实测到的事实；
- **涉及模块** —— 承担该行为责任的程序集、目录与类型；
- **当前影响** —— 该限制在功能、诊断或门禁上造成的后果；
- **当前可用的规避方式** —— 已经存在、当前即可采用的做法。

适用范围是主程序与硬件采样子进程的架构边界、插件与市场的信任边界、渲染路线与资源治理、主题与插件的格式契约、持续集成门禁，以及硬件读数的计量口径。界面控件的绘制细节、单个传感器的采集算法、打包与发布流程不属于本文范围。

文中出现的数值（命名空间命中数、字节预算、用例条数等）都是可被对应脚本或命令重新实测的量，判定口径与实测方式随条目一并给出。本文只描述当前成立的事实。

---

## 1. 架构侧已知限制

### 1.1 市场索引缺少客户端可验证的发布者签名

**现象**

插件市场与主题市场的远端索引条目只携带内容摘要与大小，没有签名字段。`MarketPluginEntry` 与 `MarketThemeEntry` 暴露 `sha256` 与 `size`；`MarketIndex` 暴露 `schema`、`updated_at`、`repo`、`total`、`plugins`、`themes_total`、`themes`。索引与其载荷来自同一个信任源，客户端侧没有任何可验证的发布者身份材料：没有签名、没有证书、没有独立于索引源的信誉根。索引条目上的 `verified` 是仓库侧的人工实机验证记号，不是密码学属性。

**涉及模块**

`src/Plugins/PluginMarket.cs`（`MarketIndex`、`MarketPluginEntry`、`PluginMarket.VerifySha256`、`PluginMarket.IsUsableSha256`、`PluginMarket.IsTargetAvailable`）、`src/Plugins/ThemeMarket.cs`、`src/Plugins/ThemeMarketValidator.cs`、`src/Plugins/PluginMarketState.cs`、`src/Plugins/PluginConstants.cs`。

**当前影响**

客户端能证明的命题只有「下载到的原始字节与索引条目声明的 `sha256` 相同」。索引源被替换时，摘要与载荷可以同时被替换，客户端无法察觉。安装路径强制要求条目带可用摘要（缺失即以 `MarketErrorReason.MissingHash` 拒绝，摘要不符即以 `MarketErrorReason.HashMismatch` 拒绝），这只保证内容完整性，不提供身份保证。

**当前可用的规避方式**

- 索引只从 `PluginMarket.DefaultIndexUrl` 取；条目 URL 由固定仓库、固定分支、合法 id 与固定文件名构造，不接受条目给出的任意下载地址 ⇒ 能突破摘要的路径只剩「索引源本身被替换」。
- 需要更高置信度的插件时使用内置插件（`PluginConstants.BuiltinPluginIds`）或用户手动放置的本地文件。手动放置的文件不经市场安装路径的摘要门禁，这是一处显式的取舍：`PluginMarket.IsTargetAvailable` 仍会阻止市场安装覆盖这类文件。
- 主题包在发布侧用 `LiteMonitor.Cli` 的 `theme-validate` 命令（规则来源 `src/Plugins/ThemeMarketValidator.cs`）做形状与语义校验。

### 1.2 插件地址预检与实际连接之间存在时间差

**现象**

`PluginAddressPreflight.EnsureAddressPolicyAsync` 在 HTTP 请求发出前解析目标主机名，把解析出的**整组**候选地址按 `PluginAddressPolicy.Classify` 分类后整组判定：

- 任一候选落入永远禁止的类别（云元数据 / 链路本地 / 未指定地址）⇒ 整组拒绝，原因码 `LM009_ADDRESS_POLICY_DENIED`；
- 任一候选落入本机或私网且该 origin 未获用户授权 ⇒ 整组拒绝，原因码 `LM009_LOCAL_NETWORK_NOT_ALLOWED`；
- 解析不出任何候选地址 ⇒ 拒绝，原因码 `LM009_TARGET_UNRESOLVABLE`。

IP 字面量目标直接分类，不经 DNS。走代理的主机名目标不做本地解析——解析发生在代理侧。每一跳重定向都会重新执行预检（`PluginExecutor` 与 `src/Plugins/Native/CryptoNative.cs` 均如此）。

**涉及模块**

`src/Plugins/PluginAddressPreflight.cs`、`src/Plugins/PluginAddressPolicy.cs`、`src/Plugins/PluginLocalNetworkAuthorizer.cs`、`src/Plugins/PluginExecutor.cs`、`src/Plugins/Native/CryptoNative.cs`。

**当前影响**

预检的解析结果与实际建立连接之间 DNS 记录可能发生变化 ⇒ 预检承担的是「连接前缓解」职责，地址分类不构成对 DNS 重绑定的完整防护。走代理的目标连本地判定都没有，地址判定完全落在代理侧。

**当前可用的规避方式**

- 本地网络访问依赖 `PluginLocalNetworkAuthorizer` 的显式用户授权，而不是依赖地址分类单独把关。
- 预检覆盖连接链上的每一跳，使重定向到本地地址的目标同样被拦下。
- 对代理路径的目标，地址类别判定不在本地完成，授权粒度落在具体 origin 上。

### 1.3 顶层主题 `version` 字段尚未形成校验与迁移合同

**现象**

主题文件顶层的 `version` 字段映射到 `Theme.Version`（缺省 `3`）。加载器只把它记录下来并透出到诊断日志与外观快照（`ThemeAppearanceOptions.ThemeVersion`），既不校验取值范围，也不按版本迁移字段语义。同层的 `rendering.version` 有硬约束（必须为 `1`，否则产生校验错误 `rendering.version must be 1.`），顶层 `version` 没有对应规则。情景档案走的是另一套版本机制：`ScenarioProfile.SchemaVersion` 与 `ScenarioFieldCatalog.CurrentSchemaVersion`，版本不受支持即以 `unsupported-schema` 拒绝。

**涉及模块**

`src/Core/ThemeManager.cs`（`Theme.Version`、`ThemeManager`）、`src/Core/ThemeAppearanceOptions.cs`、`src/Core/ThemeColorParser.cs`、`src/Plugins/ThemeMarketValidator.cs`、`src/Core/Scenarios/ScenarioFieldCatalog.cs`。

**当前影响**

不同主题文件里的顶层 `version` 取值可以不同，加载器一律按同一套字段语义解释。主题作者无法用该字段声明字段语义的变化，客户端也不会因为版本不符而拒绝加载 ⇒ 该字段目前只有记录价值。未知键进入 `ExtensionData` 扩展槽、缺省段不被写入，这两点是当前实际承担向前兼容的机制。

**当前可用的规避方式**

- 主题作者不依赖顶层 `version` 表达兼容性；向后兼容只能靠字段缺省语义保证。
- 需要严格形状与语义校验时走主题校验接口与 `LiteMonitor.Cli` 的 `theme-validate`，该路径的规则与市场校验器同源。

### 1.4 GPU 资源账本的字节计量覆盖不完整

**现象**

`GpuResourceLedger` 是 GPU 资源的唯一登记处，但账本里的字节数是调用方申报的口径值，不是从驱动读回的实际占用：

| 资源种类 | 申报口径 |
| --- | --- |
| `GpuResourceKind.Device` | 0 字节（`D3D11DeviceService.TryCreate`） |
| `GpuResourceKind.Shader` | 0 字节（`GpuSceneRenderer`） |
| `GpuResourceKind.Query` | 0 字节（`GpuSceneRenderer`） |
| 着色器资源视图 | 0 字节（`D3D11DeviceService.CreateShaderResourceView`） |
| `GpuResourceKind.Texture` | `width × height × 4`（`D3D11DeviceService.CreateTexture`），与实际 `Format` 的字节宽无关 |
| `GpuResourceKind.SwapChain` | `width × height × 4 × 2`（`GpuSceneRenderer`） |

生产宿主的预算是 1 GiB 字节与 256 个资源。

**涉及模块**

`src/Rendering/Gpu/Device/GpuResourceLedger.cs`（`GpuResourceLedger`、`GpuResourceLease`、`GpuResourceSnapshot`、`GpuResourceKind`）、`src/Rendering/Gpu/Device/D3D11DeviceService.cs`、`src/Rendering/Presentation/GpuSceneRenderer.cs`、`src/Rendering/Presentation/GpuPresentationHostController.cs`。

**当前影响**

`GpuResourceSnapshot` 的 `ActiveBytes`、`PeakBytes` 与 `BytesByKind` 与真实显存占用不是同一口径 ⇒ 1 GiB 字节预算不能被当作真实显存占用的上界判据，按种类分布的字节数也不能用于定位显存压力来自哪一类资源。

**当前可用的规避方式**

- 资源压力优先看 `ActiveResourceCount` 与 `PeakResourceCount`——资源条数预算（256）的口径与字节无关。
- 真实显存占用看硬件层的 `GPU.VRAM.Used` / `GPU.VRAM.Total` / `GPU.VRAM.Load`，它们与账本字节数互不替代。
- 判定预算是否被突破时同时读 `RejectedReservations` 计数。

### 1.5 GPU 资源超预算时的行为取决于下层拒绝路径

**现象**

账本拒绝时给出两个原因码：`gpu_resource_byte_budget_exceeded`（字节预算超限）与 `gpu_resource_count_budget_exceeded`（资源条数超限）。只有走 `TryReserve` 的调用方能原样拿到这两个码；走 `Reserve` 的调用方收到的是 `InvalidOperationException`（其 `Message` 即原因码），上层按各自的失败路径归一：

| 拒绝发生位置 | 对外错误码 |
| --- | --- |
| 设备创建（`D3D11DeviceService.TryCreate`） | `gpu_device_initialization_failed` |
| 渲染器构造（`GpuSceneRenderer.TryCreate`） | `gpu_scene_renderer_initialization_failed` |
| 呈现宿主启动（`GpuPresentationHostController.Start`） | `gpu_presentation_start_failed` |

**涉及模块**

`src/Rendering/Gpu/Device/GpuResourceLedger.cs`、`src/Rendering/Gpu/Device/D3D11DeviceService.cs`、`src/Rendering/Presentation/GpuSceneRenderer.cs`、`src/Rendering/Presentation/GpuPresentationHostController.cs`。

**当前影响**

超预算与真实的设备创建失败、渲染器初始化失败在错误面上不可区分 ⇒ 只看错误码无法判断失败是否由预算引起。`gpu_resource_byte_budget_exceeded` 与 `gpu_resource_count_budget_exceeded` 登记在渲染切换文档的错误码表中，但生产路径当前不直接产出它们。

**当前可用的规避方式**

- 诊断时以 `GpuResourceSnapshot.RejectedReservations` 与 `BytesByKind` 交叉判断：拒绝计数上升即说明账本确实拒绝了预留。
- 呈现宿主的失败原因码与账本拒绝原因码分开读，两者不构成同一层语义。

### 1.6 分层窗口相关注释与实际行为不一致

**现象**

分层窗口（`WS_EX_LAYERED`）在渲染路线上的地位，当前行为与三处注释不一致：

1. `src/UI/UIController.cs` 的 `ApplyGpuRenderingRoute` 注释写「分层窗口上不得申请 WDA 排除 ⇒ 有效路由降级为 `GpuFixedInput`」；而 `RenderingRoutePolicy.Decide` 对分层只追加诊断条目 `diagnostic_window_is_layered`，不改变路线，仍返回 `RenderingRoute.GpuLiveBackground`；`GpuPresentationHostController` 也已取消宿主侧的分层二次拦截。
2. `src/UI/Helpers/MainFormWinHelper.cs` 的 `IsWindowLayered` 把「期望状态」写成 `Opacity < 1 || ClickThrough`；而 `MainForm_Transparent.CreateParams` 只在 `ClickThrough` 为真时 OR 入 `WS_EX_LAYERED`，`MainFormWinHelper.SetClickThrough(false)` 只清 `WS_EX_TRANSPARENT` 而不清 `WS_EX_LAYERED` ⇒ 关闭穿透后该位可能一直残留到下一次句柄重建。
3. `src/UI/Settings/RenderingRouteStatusBuilder.cs` 把 `RenderingRouteStatusKind.LayeredDiagnostic` 描述为「分层窗口（透明度<100%）」，而该事实来自 `WS_EX_LAYERED` 位，与窗口透明度不是同一个量。

**涉及模块**

`src/Rendering/Configuration/RenderingRoutePolicy.cs`（`RenderingRoutePolicy.Decide`、`RenderingRouteDecision`）、`src/Rendering/Presentation/GpuPresentationHostController.cs`、`src/UI/UIController.cs`、`src/UI/Helpers/MainFormWinHelper.cs`、`src/UI/MainForm_Transparent.cs`、`src/UI/Settings/RenderingRouteStatusBuilder.cs`、`src/UI/Settings/RenderingRouteStatusView.cs`。

**当前影响**

按注释理解会得到两个错误结论：以为分层窗口会阻断 GPU 实时背景；以为该状态等价于「透明度低于 100%」。真实行为是：分层只是诊断事实，不参与路线判定，也不产生 `FallbackCode`。

**当前可用的规避方式**

- 路线判定只以 `RenderingRoutePolicy.Decide` 的输出为准（`Route`、`FallbackCode`、`Errors`）；`Errors` 中的 `diagnostic_window_is_layered` 是诊断条目而不是回退原因。
- 设置页状态行以 `RenderingRouteStatusBuilder.Build` 的输出为准。
- `IsWindowLayered` 是只读方法，不得据它修改 ex-style；鼠标穿透的开关由 `MainFormWinHelper.SetClickThrough` 负责。

### 1.7 探针工程未纳入持续集成编译

**现象**

`tests/` 下的探针工程（`AppearanceThemeCheck`、`AppearanceStaticBackdropProbe`、`AppearanceM2Probe`、`AppearanceWiringProbe`、`AppearanceDecorProbe`、`AppearanceDecorRendererProbe`、`AppearanceWindowShapeProbe`、`AppearanceSettingsProbe`、`AppearanceResourceProbe`、`AppearanceBackdropProbe`、`WebProbe` 等）不在 `LiteMonitor.sln` 内；CI 工作流只构建 `LiteMonitor.sln` 与 `tools/GpuHostLifecycleProbe`。探针的统一入口是 `scripts/run-probes.ps1`，它串行构建并运行全部外观探针（默认组）并附带单元测试。

**涉及模块**

`tests/*Probe*`、`scripts/run-probes.ps1`、`.github/workflows/ci.yml`。

**当前影响**

探针工程的编译错误不会让 CI 变红 ⇒ 探针侧的防回退能力只在有人手动运行探针时成立。探针与主工程之间的漂移（源码哈希比对、`AppearanceM2Probe` 的合同门槛）同样只由本地运行覆盖。

**当前可用的规避方式**

- 改动探针工程、探针依赖的主工程代码或探针夹具后本地执行 `scripts/run-probes.ps1`；需要实机抓屏时加 `-IncludeBackdrop`，需要 Web 协议矩阵时用 `-Groups web`。
- CI 侧对主工程与三个测试程序集（`LiteMonitor.Tests`、`LiteMonitor.Updater.Tests`、`LiteMonitor.GpuNonperformance.Tests`）的覆盖不受此影响。

### 1.8 架构分层断言有五项处于不满足状态，且未接入持续集成强制档

**现象**

`scripts/architecture-assert.ps1` 采用棘轮判定：实测值与 `scripts/architecture-baseline.json` 记录的基线比对，实测值不超过基线即通过，只拦截「变差」、允许「改善」。当前有五项实测值超出基线：

| 断言 | 检查内容 | 基线 | 实测 |
| --- | --- | --- | --- |
| C-1 | 全仓不得存在 `LiteMonitor.src.` 前缀的命名空间 | 148 | 170 |
| C-3 | `src/` 下文件的声明命名空间必须与所在目录一致 | 173 | 197 |
| C-4 | `LiteMonitor.src.*` 命名空间下不得暴露 public 类型 | 353 | 405 |
| A-2 | `src/Core` 不得 `using` SystemServices 与 WebServer 命名空间 | 5 | 6 |
| A-4 | `src/Core` 不得 `using` Rendering 命名空间 | 1 | 2 |

其余七项（C-2、A-1、A-3、A-5、A-6、A-7、A-8）满足。断言脚本默认模式在任一断言失败时以退出码 1 结束，因此当前无法直接接入 CI 强制档；脚本只在本地手动执行。

**涉及模块**

`scripts/architecture-assert.ps1`、`scripts/architecture-baseline.json`、`src/Core/`、`src/` 下的命名空间声明。

**当前影响**

`src/Core` 的跨层反向依赖仍然存在（Core → SystemServices / Plugins / Rendering / UI / WebServer / WinForms），`LiteMonitor.src.*` 前缀命名空间与干净命名空间并存，`src/Core` 仍是「名义核心层、实际混合层」。这两类问题都不会被 CI 拦住。

**当前可用的规避方式**

- 本地执行 `pwsh -NoProfile -File scripts/architecture-assert.ps1` 查看断言结果；`-WarnOnly` 只告警不阻断，`-UpdateBaseline` 以当前实测值刷新基线。
- 棘轮方向上，新增跨层 `using` 会让命中数上升并触发断言 ⇒ 改动 `src/Core` 的引用时以「命中数不上升」为约束。
- 断言的完整口径与分层现状见 `docs/architecture/layering.md`。

### 1.9 Host 模式本地硬件源重复（已关闭）

**原问题**

Host 模式曾在主进程保留一个 `HardwareMonitor`，同时 Host 子进程运行 LHM CPU/GPU。主进程展示、历史、告警和部分 Gateway 数据可能来自本地树，而其他调用来自 Host 快照，形成同机重复采集和批次不一致。

**当前状态**

- `UIController` 的硬件源已收口到 `ApplicationHardwareRuntime`。Host 分支只创建 `PipeHardwareClient`，不创建本地 `HardwareMonitor`。
- `HardwareGateway.GetSampleValue`、`HasFpsData`、`NetworkIP`、`DetectedGpuCount` 和 Web 展示均从 client 快照取值；本地 monitor 仅保留默认模式的维护能力命令。
- 每个 UI Tick、Web 响应和诊断读取分别固定自己的快照批次。UI 读取失败时保留旧画面，但不会重放旧批次到历史或告警。
- 设置页不再直接调用 `HardwareMonitor.ListAll*`。Host 目录中没有候选的来源被禁用，磁盘探测、控制器探测、IP 流量排除与主进程驱动检查显示为不可用。
- Host `SessionEpoch` 变化会清空 GPU 注册表，新目录成功加载后再重建，避免旧会话卡名污染新会话。

**仍存在的产品边界**

首批 Host 只提供 CPU/GPU 指标。内存、磁盘、网络、主板、风扇、FPS 等扩展传感器尚未迁移，缺失时必须显示不可用，禁止由主进程本地补数。验证覆盖与后续扩展项见 `docs/development/hardware-data-source-unification-plan.md`。
### 1.10 跳过用例门禁的上限与允许清单

**现象**

CI 的跳过用例数门禁由 `MAX_SKIPPED_TESTS = 2` 与 `.github/workflows/assert-test-counts.ps1` 共同实现，包含三层判定：

1. **数量上限**：三个测试程序集的跳过数合计超过上限即失败。
2. **身份核对**：允许被跳过的用例有且只有两条——`LiteMonitor.Tests.MarketE2ETests.端到端_拉索引_安装_校验_卸载` 与 `LiteMonitor.Tests.R10GpuFrameGateTests.R10_真实GPU_首帧与目标切换后持续出帧_且不重建设备`；其余任何被跳过的用例都产生「未获允许的跳过用例」。
3. **硬门禁类**：`MUST_EXECUTE_TEST_CLASS` 清单里的测试类「必须被发现、必须不少于下限、必须一条都没被跳过」。

门控变量与跳过来源：

| 被跳过的用例 | 门控变量 | 条数 |
| --- | --- | --- |
| `MarketIndexContractTests` 的插件市场契约用例 | `LITEMONITOR_PLUGIN_REPO` 需指向插件仓库副本 | 6 |
| `MarketE2ETests` 的端到端用例 | `LITEMONITOR_E2E=1` | 1 |
| `R10GpuFrameGateTests` 的真实 GPU 出帧门禁 | `LITEMONITOR_GPU_TESTS=1` | 1 |

**涉及模块**

`.github/workflows/ci.yml`、`.github/workflows/release.yml`、`.github/workflows/assert-test-counts.ps1`、`tests/LiteMonitor.Tests/GpuFactAttribute.cs`、`tests/LiteMonitor.Tests/PluginRepoFactAttribute.cs`、`tests/LiteMonitor.Tests/E2EFactAttribute.cs`。

**当前影响**

CI 工作流设置了 `LITEMONITOR_PLUGIN_REPO`（指向 `tests/fixtures/plugin-repo`），6 条插件市场契约用例因此真正执行而不被跳过；未设置 `LITEMONITOR_GPU_TESTS`，GPU 出帧门禁被记为 Skipped 并落在允许清单内。⇒ CI 上的跳过数为 2，恰好等于上限。

在**未设置** `LITEMONITOR_PLUGIN_REPO` 的环境里跑同一套用例，跳过数为 8 > 2 ⇒ 数量上限门禁不满足；此时 `MarketIndexContractTests` 属硬门禁类，其 6 条用例被跳过会额外触发「契约用例必须真正执行」的判定。

**当前可用的规避方式**

- 设置 `LITEMONITOR_PLUGIN_REPO` 指向仓库内的插件仓库副本（`tests/fixtures/plugin-repo`），6 条插件市场契约用例即真正执行而不是被跳过；该类已在 `MUST_EXECUTE_TEST_CLASS` 清单内。
- 需要真实 GPU 出帧门禁时设置 `LITEMONITOR_GPU_TESTS=1`（要求机器具备可用图形设备）；需要联网端到端时设置 `LITEMONITOR_E2E=1`。
- 修改跳过上限时，`ci.yml` 与 `release.yml` 的 `MAX_SKIPPED_TESTS` 必须**逐字一致**，否则两个工作流的可满足状态会分叉。

---

## 2. 产品侧已知限制

### 2.1 多显卡数量与显存口径边界

**现象**

- 可监控的显卡数量上限是 `GpuKey.MaxGpuCount = 8`；设置页的可选值取 `min(MaxGpuCount, 检测到的显卡数)`，且不小于 1。
- 显卡顺序由槽位绑定决定（`DeviceSlotResolver`）。已绑定的设备不在场时，槽位保持绑定并显示离线，不改绑到别的卡；空槽位按 `HardwareRules.GetHwPriority` 的优先级补齐，且只对新槽位固化绑定。
- 显存有两条通道：厂商通道（NVAPI / ADL，多卡实现下可能被全局化）与 D3D 通道（按适配器统计）。真实容量从 Windows 注册表的 `HardwareInformation.qwMemorySize` 读取。串卡判定要求两条证据同时成立：厂商通道报的容量与本卡真实容量对不上，且该容量命中系统里另一块适配器的容量。
- 注册表记录的是驱动可见容量，不保证等于物理容量；同名多块显卡的注册表容量不一致时，真实容量视为不可判定，择源退化为默认通道。

**涉及模块**

`src/Core/GpuKey.cs`、`src/System/HardwareServices/SensorMap.cs`（`SelectMonitoredGpus`）、`src/System/HardwareServices/GpuVramCapacity.cs`、`src/System/HardwareServices/GpuVramSource.cs`、`src/System/HardwareServices/DeviceSlotResolver.cs`、`src/System/HardwareServices/HardwareRules.cs`、`src/UI/Settings/SystemHardwarPage.cs`。

**当前影响**

- 超过 8 张显卡的机器只能监控其中 8 张。
- 显存分母在部分驱动下不可判定：注册表读不到时判定退化为「厂商通道可信」；同名多卡容量不一致时分母退化为默认通道；厂商通道被全局化时多卡可能显示同一份显存数据。
- 核显在注册表里记的是 UMA 窗口大小，与它实际使用的共享显存上限本就不相等 ⇒ 核显一律不参与串卡判定，核显显存恒取自身通道。

**当前可用的规避方式**

- 多卡场景优先信任 D3D 通道（`GPU.VRAM.D3D.Used` / `GPU.VRAM.D3D.Total`），并以注册表容量作分母。
- 设置项里把 `MonitoredGpuCount` 设为实际要看的卡数；实际卡数少于配置数时主程序会夹紧配置并重建监控项（`GpuItemSync.Ensure`）。

### 2.2 核显利用率口径限制

**现象**

`GPU.Load` 取自驱动上报的 3D 引擎利用率传感器（`SensorMatcher.MatchGpu` 匹配名称含 `core` 或 `d3d 3d` 的负载传感器）。核显与独显走同一套匹配规则，但两者的负载含义不同：核显的利用率同时受显示输出与合成器影响。核显的 `GPU.VRAM.*` 描述的是共享显存占用，其分母是共享显存上限而不是显存颗粒容量。Intel 核显的负载与显存传感器在 LibreHardwareMonitor 中是懒激活的——硬件对象构造时只创建传感器实例，只在 `Update()` 里才注册进传感器集合——因此采集侧必须补一次预热更新（`HardwareMonitor.WarmUpGpuSensors` → `GpuWarmup.WarmUpIfNeeded`），否则 `GPU.Load` 键缺失。

**涉及模块**

`src/System/HardwareServices/SensorMatcher.cs`、`src/System/HardwareServices/GpuWarmup.cs`、`src/System/HardwareMonitor.cs`、`src/System/HardwareServices/HardwareValueProvider.cs`、`src/System/HardwareServices/HardwareRules.cs`。

**当前影响**

- 跨卡横向比较 `GPU.Load` 没有意义：核显与独显不是同一口径。
- 核显的显存占用率与独显的专用显存占用率不是同一口径，二者不可直接比较。
- 预热未发生时核显负载键缺失，显示为缺失而不是 0。

**当前可用的规避方式**

- 核显与独显的利用率分开解读，不做横向比较。
- 核显显存按「共享显存占用」解读，分母是共享显存上限。
- 核显负载键的存在依赖采集侧的预热路径，不以「显示 0」判断核显空闲。

### 2.3 整机功耗天然低估

**现象**

`SYS.Power` 的求和口径固定为 `CPU.Power + Σ(GPU.{index}.Power, index = 0 .. Settings.MonitoredGpuCount - 1)`。`CPU.Power` 取处理器封装功耗口径，`GPU.{index}.Power` 取该卡的板级 / 封装功耗。求和范围只含 CPU 封装功耗与被监控显卡的板级功耗，**不含**主板 VRM 转换损耗、内存与存储功耗、风扇与水泵功耗、主板其它负载、电源本身的转换损耗。产品没有电源直读（PSU AC 输入功率）数据源。

**涉及模块**

`src/System/HardwareServices/HardwareValueProvider.cs`（`SYS.Power` 分支）、`src/System/HardwareServices/ComponentProcessor.cs`、`src/Core/Settings.cs`（`RecordedMaxSysPower`）、`src/Core/SettingsHelper.cs`。

**当前影响**

读数系统性低于插座处的实测功率 ⇒ `SYS.Power` 只能作为「主要发热部件功耗之和」的参考，不得当作整机功率。另外求和范围受 `Settings.MonitoredGpuCount` 约束：未纳入监控的显卡功耗不计入；至少一个分量有效即给出总值，因此「只有 CPU 分量」的总值是一个偏小的真实值而不是缺失。量程上 `SYS.Power` 的 sanity ceiling 是 2000 W，与 `CPU.Power` 的 600 W、`GPU.Power` 的 1200 W 熔断是三套不同口径。

**当前可用的规避方式**

- 把 `SYS.Power` 当作主要发热部件的功耗和。
- 把 `MonitoredGpuCount` 设为实际在用的卡数，否则求和范围偏小。
- 需要真实整机功率时使用电源直读设备，本产品不提供该数据源。

### 2.4 AMD APU 核显与封装功耗重复计算风险

**现象**

AMD APU 的核显与封装在同一硅片上，封装功耗已经包含核显部分。若该平台的核显同时以 `GPU.{index}.Power` 的形式被采集到，同一份能量会在 `SYS.Power` 的求和里出现两次。当前实现不做 APU 核显去重——传感器层没有可靠判据能稳定区分「核显功耗」与「独立显卡功耗」。

**涉及模块**

`src/System/HardwareServices/HardwareValueProvider.cs`（`SYS.Power` 分支）、`src/System/HardwareServices/HardwareRules.cs`、`src/System/HardwareServices/SensorMatcher.cs`。

**当前影响**

在 AMD APU 机器上 `SYS.Power` 可能偏高，偏高幅度与核显负载相关。该偏差方向与整机功耗的系统性低估相反，两者在同一台机器上可能同时存在。

**当前可用的规避方式**

- 把 APU 机器上的 `SYS.Power` 视为上界而非实际值。
- 需要该平台的准确口径时以 `CPU.Power`（封装功耗）为准，忽略核显的 `GPU.{index}.Power` 分量。

---

## 3. 相关架构文档

本文各条目周边的现行设计说明分布在以下文档中：

| 文档 | 覆盖内容 |
| --- | --- |
| [overview.md](overview.md) | 程序集与进程拓扑、分层依赖图、跨进程数据流 |
| [layering.md](layering.md) | 每一层的职责边界、依赖方向约束、架构断言的完整口径与分层现状 |
| [market.md](market.md) | 插件市场与主题市场的信任边界、索引校验、安装事务 |
| [plugin-sandbox.md](plugin-sandbox.md) | 声明式插件的执行模型、网络闸门、地址分类 |
| [rendering.md](rendering.md) | 渲染路线判定、GPU 渲染栈、资源账本 |
| [rendering-transition.md](rendering-transition.md) | 路线切换合同、错误码表、清理责任归属 |
| [process-isolation.md](process-isolation.md) | 硬件采样子进程的隔离目标、管道协议、故障监督 |
| [../specs/theme-system.md](../specs/theme-system.md) | 主题文件 schema、校验规则、CLI 校验工具 |
| [../specs/derived-metrics.md](../specs/derived-metrics.md) | 派生指标定义、整机总功耗求和口径与量程 |
| [../specs/hardware-sampling.md](../specs/hardware-sampling.md) | 采样节拍、显存择源判定、核显与独显的口径区分 |
