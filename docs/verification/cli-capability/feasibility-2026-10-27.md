# CLI 能力可行性验证报告（Agent 复用探测点与工具）

- 日期：2026-10-27
- 范围：为 LiteMonitor++ 增加独立 CLI 通道，让 Agent 复用应用内的探测点与纯逻辑工具
- 结论：**可行性大**。基础设施齐备，工作量集中在"包装成命令"与"L3 变更通道设计"两处
- 状态：已完成工程形态 / 编译 / 运行时 / 架构棘轮 / 解决方案 五项实证；L3 通道为设计结论（未实施）

---

## 1. 仓库既有的三种 CLI 化先例

| 模式 | 代表工程 | 做法 | 适用面 |
|---|---|---|---|
| A · 工程引用 | `tools/GpuHostLifecycleProbe` | `WinExe` + `net8.0-windows` + `ProjectReference ..\..\LiteMonitor.csproj`；借 `InternalsVisibleTo` 访问 internal | 需要真实窗体 / GPU / 硬件 |
| B · 反射加载 | `tests/WebProbe` | `Exe`，**零工程引用**，运行时 `Assembly.LoadFrom` 产品程序集；只替换 `SettingsHelper.PathOverride` 与端口 | 只吃 public API |
| C · 源码共享 | `tools/ThemeMarketValidator` | `Exe` + `net8.0` + `<Compile Include="..\..\src\…">` + `DefineConstants=THEME_MARKET_CLI` | 纯逻辑，客户端与 CLI **同一份源码** |

模式 C 的 csproj 注释确立了本次方案的基调：

> 与客户端共用同一份源码；**禁止在仓库脚本里复制校验规则**。

---

## 2. 已实施的验证工程

```
src/Cli/LiteMonitor.Cli.csproj      OutputType=Exe, net8.0-windows, Nullable=enable
src/Cli/Program.cs                  手写 --flag=value 解析；JSON→stdout；摘要→stderr
```

采用**模式 C**，共享 `src/Plugins/ThemeMarketValidator.cs` 与 `src/Rendering/Configuration/ThemeRenderingConfig.cs`。

### 2.1 命名空间与目录（架构棘轮的决定性约束）

`scripts/architecture-assert.ps1` 的 `Get-ExpectedNamespace`（第 205 行）规则为
`src/<a>/<b>/…` ⇒ `LiteMonitor.<a>.<b>…`（`System` 特例改 `SystemServices`，白名单优先）。

⇒ 目录必须命名为 **`src/Cli`**，声明 **`namespace LiteMonitor.Cli`**，才能让 C-3（目录↔命名空间一致性）判定通过。
⇒ 严禁声明 `namespace LiteMonitor.src.*`（会抬高 C-1 命中数与 C-4 public 类型数）。

### 2.2 主工程必须排除 CLI 源码

`LiteMonitor.csproj` 的 `<Compile Remove>` 原本只排除了 `src/Contracts`、`src/HardwareHost`、`LiteMonitor.Updater`、`LiteMonitor.Wpf`、`tests`、`tools`、`docs` 等。SDK 默认通配 `**/*.cs` 会把 `src/Cli/**` glob 进主工程 ⇒ **CS0017 多入口点**。

已补：

```xml
<Compile Remove="src\Cli\**" />        <!-- 有自己的 Main，必须排除 -->
<EmbeddedResource Remove="src\Cli\**" />
```

---

## 3. 实证结果

### 3.1 编译（Release，`TreatWarningsAsErrors=true`）

```
LiteMonitor.Cli -> src\Cli\bin\Release\net8.0-windows\LiteMonitor.Cli.dll
已成功生成。0 个警告，0 个错误
```

**首次编译失败并暴露了一个真问题**：`ThemeRenderingConfig.Validate()` 内部用 `#if THEME_MARKET_CLI` 切掉 `LegacyFallbackStyleResolver`（后者拖进 `System.Drawing` + `LiteMonitor.src.Core`）。缺少 `DefineConstants` 时报 `CS0103: 名称"LegacyFallbackStyleResolver" 不存在`。

⇒ **CLI 工程必须定义 `THEME_MARKET_CLI`**（已写入 csproj 并注释原因）。

> 副作用（须写进 CLI 文档）：`#if THEME_MARKET_CLI` 分支令 `diagnostics = Array.Empty<string>()`，
> 因此 **CLI 不产出 `gpu_fallback_style_invalid` 诊断项**——`IsValid` 判定不受影响，但诊断集合比客户端少一项。

### 3.2 运行时行为与退出码契约

| 场景 | stdout JSON | exit |
|---|---|---|
| 合法 GPU 主题 | `{"ok":true,…,"error_code":"ok"}` | 0 |
| 合法 legacy 主题 + legacy 路线 | `{"ok":true,…}` | 0 |
| 文件不存在 | `{"ok":false,"error_code":"file_not_found"}` | 2 |
| 主题含 `http://` 远程引用 | `{"ok":false,"error_code":"theme_remote_reference"}` | 1 |
| route/material 互验不符 | `{"ok":false,"error_code":"theme_route_mismatch"}` | 1 |
| 缺 `--path` | `{"ok":false,"command":"usage"}` | 64 |
| 未知命令 | `{"ok":false,"command":"usage"}` | 64 |

退出码与仓库既有约定同族：探针 `0=pass / 1=fail / 2=环境不具备 / 64=参数错 / 70=未处理异常`；
Updater `0/2/3/5/6/7`。**每次调用 stdout 恰好一个 JSON 对象**，可被 Agent 直接解析。

### 3.3 架构棘轮：零影响（决定性）

新增工程后重跑 `scripts/architecture-assert.ps1`：

| 断言 | 验证前 | 验证后 | 结论 |
|---|---|---|---|
| 扫描文件数 | 797 | 798 | +1（`src/Cli/Program.cs`） |
| C-1 `namespace LiteMonitor.src.` 命中数 | 166 | **166** | 不变 |
| C-2 裸 `namespace LiteMonitor` | 30 | **30** | 不变 |
| C-3 目录↔命名空间不一致 | 191 | **191** | 不变 |
| C-4 `LiteMonitor.src.*` 下 public 类型数 | 401 | **401** | 不变 |
| A-1 … A-8 | 1/5/3/1/1/1/11/3 | **完全一致** | 8/8 PASS |

原因：棘轮统计的是**源文件文本**而非程序集——共享进来的文件路径未变，声明的命名空间未变，故计数不变。

> C-1/C-3/C-4 当前 FAIL 是**既有**问题（新增 WIP 子系统 Alerts/Trends/Scenarios/Diagnostics 用了 `LiteMonitor.src.*`），与 CLI 无关。

### 3.4 解决方案不被破坏

`dotnet build LiteMonitor.sln -c Release` ⇒ **0 警告 0 错误**（主工程未出现 CS0017）。
`dotnet sln add src\Cli\LiteMonitor.Cli.csproj` 后重建仍为 **0 警告 0 错误**。

### 3.5 CI 接入影响

- `.github/workflows/assert-test-counts.ps1` 的 `ExpectedAssembly` 语义是**下界断言**（第 122 行注释："必须显式声明『必须出现』的测试程序集"）⇒ **新增第四个测试程序集不会让 CI 变红**。
- 但反过来说：新增测试程序集**不会被 CI 自动执行**，需改两个 workflow（踩 LM-056 的手工同步坑）。
- ⇒ **推荐：CLI 的单元测试直接放进现有 `tests/LiteMonitor.Tests`** ⇒ 零 workflow 改动、`MIN_TEST_TOTAL` 只增不减、棘轮方向正确。

---

## 4. L3（变更层）通道设计结论

L3 命令（改主题、装插件、改自启、写设置）**不能**由 CLI 进程直接改文件，原因有两处实证：

### 4.1 主程序不监视配置文件

全仓无 `FileSystemWatcher`（grep 仅命中 3 处，全在 `PipeServerEndpoint.cs`）。主程序只在自身 `ApplySettings` 流程里读配置 ⇒ CLI 改 `settings.json` 不会热生效，且与运行中实例争抢写入（`Settings._ioLock` 是**进程内**锁）。

### 4.2 Web 通道拿不到明文口令

`src/Core/Settings.cs:1114` 注释："无论调用方怎么设置 `WebServerPassword`，序列化出来的永远是 `""`"。
落盘只有 `WebServerPasswordHash` + `WebServerPasswordSalt`（哈希不可逆）。
⇒ CLI **无法从配置恢复明文口令**；走 `/api/cli/*` 需每次由调用方提供明文口令（进程列表泄露）或新增一份 DPAPI 加密凭据。

### 4.3 结论：L3 走命名管道，而非 Web

`src/System/Pipe/PipeServerEndpoint.cs:142-160` 已有现成范式：

```csharp
var security = new PipeSecurity();
security.AddAccessRule(new PipeAccessRule(
    WindowsIdentity.GetCurrent().User …,
    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
    AccessControlType.Allow));
NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, …, pipeSecurity: security);
```

ACL 显式限**当前用户 SID** ⇒ 天然就是"本机同用户 Agent"的信任模型：**不需要口令、不需要暴露端口、不需要 CSRF**。

需补的三件事：

1. **一条新的控制面管道**（不可复用 Host 管道：`LiteMonitor.HardwareHost.{Guid:N}` 名随机且协议专用），可完整复用 `IpcFrameReader` / `IpcFrameWriter` / `IpcEnvelope` 协议层。
2. **端点发现**：主程序启动后把管道名写入 `%LocalAppData%\LiteMonitor++\cli-endpoint.json`（该文件同样需限当前用户 ACL）。
3. **命令执行面**：主程序侧接收命令 → 调 `AppActions` / `Settings` → 回结果，**复用既有的 `ModuleApplyResult` / `ApplyOperationResult` 诚实失败语义**（Applied / Partial / Pending / Failed / Cancelled / Unknown），禁止造第三套失败契约。

---

## 5. 尚未验证 / 需决策项

| 项 | 状态 |
|---|---|
| CLI 是否随产品发布 | **未决**。`scripts/pack-release.ps1` 目前只发布主程序 + 运行时资源 + LICENSE/NOTICE，CLI 不在包内 |
| L2 观测层（连运行实例读快照） | **未实施**。可复用现有 `/api/snapshot`（`WebSessionManager.cs:1639`），它只"请求共享采样"、不自己 `hw.Update()`，无新增采集器风险 |
| CLI 产物形态 | 验证工程为 `net8.0-windows` 依赖框架；L1 纯逻辑本可降到 `net8.0` 跨平台（`ThemeMarketValidator` 即如此），待按最终命令集再定 |
| L3 的管道协议扩展 | 未实施 |

---

## 6. 落地建议（下一步）

1. 把 `src/Cli/` 扩展为正式 CLI 工程：按 L1 命令清单逐条包装，**每条命令只用纯函数**，禁止引入 WinForms。
2. 命令实现优先取自：路由裁决、阈值判定、格式化、主题/插件校验、URL 校验、场景预检、趋势覆盖率、告警规则校验、采样需求聚合。
3. CLI 单元测试放进 `tests/LiteMonitor.Tests`（零 workflow 改动）。
4. L2 先接 `/api/snapshot`；L3 走命名管道控制面，复用 `ModuleApplyResult` 语义。
5. 顺带把新 WIP 子系统的命名空间从 `LiteMonitor.src.*` 迁到 `LiteMonitor.*`（B5 本就计划做），让 C-1/C-3/C-4 棘轮归零。
6. 把 CLI 接进 CI 当门禁（模式参考 `GpuHostLifecycleProbe --scenario=startup` 的退出码契约用法），避免又造一个"在 sln 外、无自动回归"的探针。

---

## 附：本次验证产生的仓库改动

| 文件 | 改动 |
|---|---|
| `src/Cli/LiteMonitor.Cli.csproj` | 新增 |
| `src/Cli/Program.cs` | 新增 |
| `LiteMonitor.csproj` | 新增 `<Compile Remove="src\Cli\**" />` 与 `<EmbeddedResource Remove="src\Cli\**" />` |
| `LiteMonitor.sln` | `dotnet sln add src\Cli\LiteMonitor.Cli.csproj` |
| `docs/verification/cli-capability/feasibility-2026-10-27.md` | 本文件 |
