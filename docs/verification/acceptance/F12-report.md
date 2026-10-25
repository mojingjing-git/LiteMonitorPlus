# F12 验收记录 —— 字体生命周期与 DPI 测量安全（#574 的已知结构风险）

> 依据：`docs/plans/dispatch/P-F12.md` + 拆解计划 §4 P-F12 + 母计划 §F12。
> 原则：只记录**真实执行过**的命令与**实际观察到的**结果；未执行的场景逐条列入"未覆盖场景"。
> ★ "代码层验收通过" ≠ "可关闭 #574"：#574 的**原始崩溃**仍需日志与触发链对应（归 P-F15 取证）。

## 1. 标识

- 工作包：P-F12
- 分支：`fix/F12-font-measure-safety`
- 基线 SHA（开工时 HEAD）：`d52c0ab`
- 提交 SHA 列表：
  | SHA | 说明 |
  |---|---|
  | `8ee42f7` | `test(F12): 字体测量安全回归用例（红：布局仍走旧的 GetHeight(Graphics) 路径）` |
  | `d37e98a` | `fix(F12): 字体度量与 Graphics 解耦 + 横排布局统一构造点（受控布局阶段测量并缓存）` |
  | `b831d9c` | `fix(F12): 已释放主题字体只判一次，避免高频布局测量反复撞异常` |
  | （本提交） | `docs(F12): 验收记录（字体生命周期与 DPI 测量安全）` —— SHA 以 `git log` 为准（自引用，不写死在本文） |
- 完成日期：2026-10-25
- 执行者：worker 会话 `mvs_3e5693d1939041bb9607c15fa801dc84`（P-F12 执行智能体）

## 2. 覆盖与结论

- 覆盖 issue：**#574 的已知结构风险**（`Parameter is not valid.`）。**不覆盖** #574 原崩溃本身。
- 结论：**☑ 代码层验收通过**
- 允许关闭的 issue 与条件：**无**。本包**不**允许关闭 #574 —— 关闭条件是"拿到 `LiteMonitor++_Error.log` 且触发链与本包修复路径对应"（母计划 §4 #574 行 / §7）。
- 允许关闭的**结构风险**：fork 在绘制/布局路径重新引入的 `Font.GetHeight(Graphics)` 及其连带问题（见 §2.1）。

### 2.1 本包关闭的结构风险（逐条）

| # | 风险（修复前） | 证据 | 修复后 |
|---|---|---|---|
| R1 | 布局路径把 `Graphics` 交给 `Font.GetHeight(Graphics)`；Graphics 已释放时抛 `ArgumentException` | `src/UI/HorizontalLayout.cs` 构造函数（基线 57 行） | 全部改为 `Font.GetHeight(float dpi)`（Graphics 无关）。**全程序集 IL 扫描零匹配** |
| R2 | 主题字体**已被释放**时读度量抛 `ArgumentException("Parameter is not valid.")` —— **这正是 #574 的异常消息** | 实测（§3 用例 ③） | 度量前置"字体可用性"判定；不可用 ⇒ 推迟测量 + 记诊断 + 保留最小布局，异常不再冒到 UI 线程 |
| R3 | `HorizontalLayout` 构造点**有两处**且参数不同（legacy 侧不传 `Settings`、不传测量控件），两处都用 `??=` ⇒ 行高/列间距随"GPU 路线 vs legacy 路线"漂移 | 基线 `UIController.cs:754` 与 `:1906` | 收敛为唯一构造点 `UIController.CreateHorizontalLayout` |
| R4 | legacy 路径按 **96 DPI 基线**（`Font.Height`）算行高，而主题字体是 Point 单位、GDI+/GDI 按**设备 DPI**放大 ⇒ 150%/200% 下行高偏小 ⇒ **文字被裁切** | 实测：Point 字体行高随 DPI 线性（1×/1.5×/2×），`Font.Height` 恒为 96 基线 | 行高统一按**设备 DPI**测量（`Control.DeviceDpi`，PerMonitorV2） |
| R5 | `Control.CreateGraphics()` 在句柄不存在时**强制创建 HWND**（关闭过程不得新建 HWND） | 基线 `HorizontalLayout` 构造函数 | 度量前先判 `IsHandleCreated`；句柄不存在 ⇒ **推迟测量**（不建 HWND、不碰已释放对象） |

## 3. 用例与命令（可从干净检出复现）

### 3.1 关键取证（本包新增用例实测，不是抄来的结论）

在 Windows 11 / .NET 8 上实测（用例 `取证_*`，见 §5）：

| 事实 | 实测值 |
|---|---|
| `Font.GetHeight(Graphics)` 在 Graphics 已释放时 | 抛 `ArgumentException`（消息含 "graphics"） |
| `Font.GetHeight(Graphics)` vs `Font.GetHeight(float dpi)`（同 DPI） | **逐位相等**（17.734373 = 17.734373） |
| `Font.Height` / `Font.GetHeight(float)` 在**字体已释放**时 | 抛 `ArgumentException("Parameter is not valid.")` ← **#574 的异常形态** |
| Point 字体行高：`GetHeight(96/144/192)` | 17.734373 / 26.601562 / 35.468746（= 1× / 1.5× / 2×） |
| `Font.Height` | 18（= ceil(96 DPI 行高)，**不含**设备 DPI） |
| Pixel 字体行高 `GetHeight(96/144/192)` | 均为 17.291016（像素字体不随 DPI 缩放） |

⇒ 结论：`Font.GetHeight(float dpi)` 是 `GetHeight(Graphics)` 的**同 DPI 等价替代**，且不依赖任何 GDI 句柄；
`Font.Height` **不是** DPI 等价物，用它排版在 150%/200% 下会裁切。

### 3.2 红 → 绿（两个提交的真实对照）

| 阶段 | 提交 | `FontMeasureSafetyTests` 结果 |
|---|---|---|
| 红 | `8ee42f7`（布局仍走旧的 `GetHeight(Graphics)` 路径） | **失败 11 条 / 通过 14 条 / 总计 25** |
| 绿 | `d37e98a`（受控布局阶段测量 + 统一构造点）+ `b831d9c`（已释放字体只判一次） | **失败 0 / 通过 25 / 总计 25** |

红的 11 条（红阶段运行输出里逐条列出的失败用例）：

1. `护栏_HorizontalLayout不得再调用GetHeight_Graphics重载`
2. `护栏_产品程序集里不得再出现GetHeight_Graphics调用`
3. `护栏_横排布局只有一个构造点_行高不再随代码路径漂移`
4. `护栏_横排布局构造点必须传实时配置与测量控件`
5. `句柄不存在时推迟测量并保留最小布局`（旧实现顺带创建了 HWND）
6. `主题字体已被释放时不抛异常且保留最小布局`（旧实现直接抛 `ArgumentException("Parameter is not valid.")`）
7. `行高按设备DPI计算_不小于字体在该DPI下的渲染高度(dpi: 144)`
8. `行高按设备DPI计算_不小于字体在该DPI下的渲染高度(dpi: 192)`
9. `行高在高DPI下严格大于96基线的FontHeight_旧的FontHeight路径会裁切(dpi: 144)`
10. `行高在高DPI下严格大于96基线的FontHeight_旧的FontHeight路径会裁切(dpi: 192)`
11. `行高随DPI单调不减`

### 3.3 命令与结果

| 命令 | 目的 | 结果（发现数/通过/失败/跳过） |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | 构建 | **0 错误 / 532 警告**（与基线 `d52c0ab` 的 532 警告**逐条一致**，本包未新增告警） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build` | 主测试 | 总计 **1809** / 通过 **1802** / 失败 **0** / 跳过 **7** |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj -c Debug --no-build` | GPU 非性能门禁 | 总计 **19** / 通过 **19** / 失败 **0** / 跳过 **0** |
| `dotnet test tests/LiteMonitor.Updater.Tests/LiteMonitor.Updater.Tests.csproj -c Debug --no-build` | 更新器 | 总计 **73** / 通过 **73** / 失败 **0** / 跳过 **0** |
| `dotnet test ... --filter 'FullyQualifiedName~FontMeasureSafetyTests'` | 本包新增用例 | 红：失败 11 / 通过 14 / 总计 25；绿：失败 0 / 通过 **25** / 总计 25 |

★ **CI 阈值（禁改，未动）**：`.github/workflows/ci.yml` 的 `MIN_TEST_TOTAL: 1876` / `MAX_SKIPPED_TESTS: 1` **一字未改**。
三个程序集发现数合计 = 1809 + 19 + 73 = **1901**（基线 1876 + 本包新增 25）。
⇒ **建议协调者在批次集成时把 `MIN_TEST_TOTAL` 统一上调到 ≥ 1901**（`release.yml` 逐字一致）。

★ **既有 flaky 观察**：自验期间 `ReviewMarketPersistenceRegressionTests` 偶发失败 2 次
（两次各命中该类的不同用例；同一套二进制单独重跑 3 次为"失败 1 / 通过 2"⇒ 非确定性）。
该类**不引用**本包改动的任何类型（`HorizontalLayout` / `UIController` / `FontMetrics` / `Theme` grep 零匹配），
且其源码注释已自陈"单跑全绿、全量间歇挂（评审同机三次复现三条不同用例）"⇒ **与本包无关**。
最终一轮全量跑该类**全绿**（1802/1802）。详见 §7.2 第 4 条。

## 4. 环境

- OS / 版本：Windows 11 专业版，10.0.26300（.NET SDK 8.0.426）
- DPI / 显示器拓扑：主显示 3840×2160；本机测试进程实测 `Graphics.FromHwnd(IntPtr.Zero).DpiX = 96`；
  应用 DPI 感知等级 = **PerMonitorV2**（`LiteMonitor.csproj` 的 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` + `app.manifest` 的 `dpiAwareness` 段）。
  ★ 100/150/200% 三档行高由**注入 DPI** 的用例覆盖（不依赖实机缩放）。
- GPU / 驱动：NVIDIA RTX 3080 + RTX 3080 Ti（驱动 32.0.16.1088）＋若干虚拟显示适配器（本包不涉及 GPU 路径）
- 关键依赖版本：本包不涉及 LHM / DiskInfoToolkit

## 5. 新增测试清单

文件：`tests/LiteMonitor.Tests/FontMeasureSafetyTests.cs`（新增，21 个测试方法 / **25 个发现用例**）

| # | 用例名 | 验证了什么 |
|---|---|---|
| 1 | `取证_GetHeight用Graphics与用DPI数值在同一DPI下完全等价` | 证明 `GetHeight(float dpi)` 是 `GetHeight(Graphics)` 的**逐位等价**替代（不是"近似替代"） |
| 2 | `取证_已释放的Graphics传给GetHeight会抛ArgumentException` | 复现 R1 的异常形态（Graphics 已释放 ⇒ ArgumentException） |
| 3 | `取证_已释放的字体读度量抛ArgumentException且消息为ParameterIsNotValid` | **确认 #574 的异常消息来自"已释放字体"**（不是已释放 Graphics） |
| 4 | `取证_Point单位字体的行高随DPI线性放大而FontHeight恒为96基线` | 证明 `Font.Height` **不是** DPI 等价物 ⇒ 旧路径在 150%/200% 下必然裁切 |
| 5 | `护栏_HorizontalLayout不得再调用GetHeight_Graphics重载` | **IL 护栏**：`HorizontalLayout` 内零 `Font.GetHeight(Graphics)` 调用 |
| 6 | `护栏_产品程序集里不得再出现GetHeight_Graphics调用` | **IL 护栏**：扫描 `LiteMonitor*` 命名空间全部类型/方法，产品程序集零匹配 |
| 7 | `护栏_横排布局只有一个构造点_行高不再随代码路径漂移` | **IL 护栏**：`UIController` 内 `newobj HorizontalLayout` 有且仅有 1 处，且在 `CreateHorizontalLayout` |
| 8 | `护栏_横排布局构造点必须传实时配置与测量控件` | **IL 护栏**：构造点必须读 `_cfg`（实时配置，非 `Settings.Load()`）、`_form`（设备 DPI）、`_fontMetrics`（共享缓存） |
| 9-11 | `行高按设备DPI计算_不小于字体在该DPI下的渲染高度(dpi: 96/144/192)` | **100/150/200% 无行高裁切**：行高 ≥ 该 DPI 下字体渲染高度；且 `DpiScale == dpi/96` |
| 12-13 | `行高在高DPI下严格大于96基线的FontHeight_旧的FontHeight路径会裁切(dpi: 144/192)` | 证明旧 `Font.Height` 路径确实会裁切（回归护栏） |
| 14 | `行高随DPI单调不减` | 行高随 DPI 单调（96 < 144 < 192） |
| 15 | `句柄不存在时推迟测量并保留最小布局` | 句柄不存在 ⇒ 推迟测量（`RowHeight == 0`）、**不新建 HWND**、`MinPanelHeight ≥ 24`、空态仍返回最小高度 |
| 16 | `控件已释放时DPI来源报告不可用而不抛异常` | 关闭/重建句柄时 DPI 来源报"不可用"而非抛异常 |
| 17 | `没有测量控件时用基线DPI做确定性测量` | 无显示上下文时用 96 DPI 基线做确定性测量（与旧行为等价，无回退） |
| 18 | `主题字体已被释放时不抛异常且保留最小布局` | **R2 护栏**：已释放字体 ⇒ 推迟测量 + 记诊断 + 保留最小布局，**不抛** `ArgumentException` |
| 19 | `高频换肤时已释放字体的度量失败不会无限重复` | 50 次换肤循环下诊断条数有界（≤ 20）⇒ 高频路径不刷屏、不反复抛（实现上：度量负缓存 + 已释放字体只判一次 + 诊断条数上限） |
| 20 | `同一字体同一DPI命中缓存_换DPI或换主题代次则重新测量` | 缓存键含 DPI 与**主题代次**；换 DPI / 换代次 ⇒ 重新测量 |
| 21 | `不同字号或不同字体族落在不同的缓存键上` | 缓存键含字体族与字号 |
| 22 | `主题代次推进后同一字体的度量按新代次重算` | 换肤后按新字体重算（不会沿用上一代行高） |
| 23 | `缓存键不含字体实例_因此不会把已释放字体留在缓存里` | 缓存命中路径**完全不碰字体对象**；未测过的已释放字体 ⇒ 度量失败并记诊断 |
| 24 | `空状态返回最小高度而不是零` | **空状态最小高度保留**（`≥ 24`，且 `Build` 返回它而非 0） |
| 25 | `有列时仍能算出列宽且高度为行高的整数倍` | 布局仍能产出列宽/列高（回归） |

★ 统计：**新增发现用例 25 条**（供协调者上调 `MIN_TEST_TOTAL`：1876 → **1901**）。

## 6. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| 真实 150% / 200% 缩放显示器上横排面板的行高与视觉裁切 | 改 Windows 缩放（150%/200%）→ 开横排模式 → 目视文字是否被裁切、面板高度是否随 DPI 变化 | 用户 / 实机 |
| 跨屏拖动（1080p 100% ⇄ 4K 200%）时行高是否随 per-monitor DPI 重算且不跳变 | 双屏不同缩放，拖动窗口跨屏 | 用户 / 实机 |
| 高频切主题（主题市场切换 10 次以上）后行高是否稳定、是否出现 `Parameter is not valid.` | 主题市场来回切 + 观察日志 | 用户 / 实机 |
| 关闭 / 重建 HWND（GPU↔legacy 路线切换、句柄重建）过程中是否仍访问已释放 Graphics/Font | 反复切 GPU 路线 + 观察 `LiteMonitor++_Error.log` | 用户 / 实机 |
| **#574 原崩溃**的触发链与日志对应 | 收集用户侧 `LiteMonitor++_Error.log`（含堆栈）与主题/DPI 切换时序 | P-F15（取证） |
| 任务栏模式（`LayoutMode.Taskbar`）的字体度量 | 本包未改任务栏渲染路径（任务栏行高由 Placement 决定） | 用户 / 实机 |
| Release 配置下的构建与测试 | 本包只在 Debug 下自验 | 协调者（批次集成时统一跑 Debug+Release） |

## 7. 偏离与遗留

### 7.1 与派发单的偏离

- **无越界改动**：只改了派发单"改动文件"清单内的 `src/UI/HorizontalLayout.cs`、`src/UI/UIController.cs`，
  并新建 `src/UI/FontMetricsCache.cs`、`tests/LiteMonitor.Tests/FontMeasureSafetyTests.cs`、`docs/verification/acceptance/F12-report.md`。
- **共享热点**：`src/UI/UIController.cs` 只做了本包明确授权的**字体测量路径**最小编辑
  （新增 `_fontMetrics` 字段、`ApplyTheme` 里推进主题代次、两个构造点收敛为一个 `CreateHorizontalLayout`），**未触碰**非字体测量区域。
- **未改** `src/Core/ThemeManager.cs` / `src/Core/UIUtils.cs`：字体资源的**持有与释放责任**经排查无需改动
  （`Theme.DisposeFonts()` 的唯一调用点是 `Theme.Scale()` 与 `UIController.ApplyTheme`，后者在释放旧主题字体**之前**
  已把 `_hxLayout` 置空并推进主题代次）；`UIUtils.GetFont` 的共享字体缓存由 `ClearBrushCache()` 统一释放，
  现有调用点（`LiteTooltipForm`）都是**绘制时现取**，不持悬空引用。
- **禁改项**：`ci.yml` / `release.yml` 的 `MIN_TEST_TOTAL: 1876` / `MAX_SKIPPED_TESTS: 1` **一字未改**；
  `resources/lang/*.json` **未改**（本包无新增用户可见文案，不需要新增 key）。
- **提交顺序说明（红→绿）**：本包按"先写会失败的用例再改实现"执行，但新增用例引用了**新引入的度量基础设施**
  （`FontMetricsCache` / 注入式 DPI 来源），无法在旧实现上独立编译。因此采用**两提交**形式：
  `8ee42f7`（用例 + 度量基础设施 + **旧的**测量逻辑 ⇒ 11 条用例红）→ `d37e98a`（换成受控测量 + 统一构造点 ⇒ 25 条全绿）。
  §3.2 给出了两侧的真实执行输出。

### 7.2 发现的相邻问题（**不属于本包**，建议归属）

1. **`UIUtils.GetFont` 的共享字体缓存**（`src/Core/UIUtils.cs:120`）在 `ClearBrushCache()` 后会让**此前取到的引用**变成悬空对象。
   当前调用点都安全，但该模式很脆：任何"把 `GetFont` 结果存进长生命周期字段"的改动都会静默降级
   （`TextRenderer.DrawText` 用已释放字体**不抛异常**，只是不生效）。建议在 `UIUtils` 里加"取用即校验 disposed"的护栏。
   → 建议归属：**P-F16 / 架构债**（`src/Core/UIUtils.cs` 不在本包清单内，未自行修改）。
2. **`TaskbarRenderer._cachedFont` 是进程级 static**，`ReloadStyle()` 会 Dispose 并重建。
   同一 UI 线程内安全，但若将来从非 UI 线程调用 `ReloadStyle`，任务栏绘制会拿到已释放字体。→ 建议归属：窗口线（F04/F05/F06）。
3. **CI 的 `MAX_SKIPPED_TESTS: 1` 与本地实况不一致**：本机 `LiteMonitor.Tests` 跳过 7 条
   （6 条插件市场契约受 `LITEMONITOR_PLUGIN_REPO` 门控 + 1 条联网 E2E），与 `ci.yml` 注释里"接入夹具前基线是 7 条跳过"一致，
   **本包新增用例 0 跳过**，不改变该数字。→ 提示协调者：CI 侧这 6 条契约需夹具才为 1 条跳过。
4. **既有用例 `ReviewMarketPersistenceRegressionTests` 偶发失败（flaky，既有问题）**：
   自验期间观察到 2 次失败（两次各命中该类的不同用例：`Recovery_LockedJournalRemainsPending`、
   `ExtendedJournalMetadata_IsValidatedBeforeRecovery`）。单独重跑该测试类 3 次为"失败 1 / 通过 2" ⇒ 非确定性。
   该类**不引用** `HorizontalLayout` / `UIController` / `FontMetrics` / `Theme`（grep 零匹配），
   且其源码注释已自陈"单跑全绿、全量间歇挂（评审同机三次复现三条不同用例）" ⇒ 与本包无关。
   → 建议归属：市场线（P-F13 / P-F14a）。

### 7.3 明确不做的事

- **不**把 `#574` 判为已关闭：原崩溃仍需日志与触发链对应（P-F15）。
- **不**用应用层 catch 冒充原生边界修复：catch 只做两件事 —— 记**有界诊断**（`FontMetricsCache.RecordDiagnostic`，
  上限 20 条）、推迟测量并保留最小布局；真正的修复是**结构性的**（不再把 Graphics 交给字体度量、不再度量已释放字体）。
- **不**新增高频硬件轮询 / 无限重试：度量缓存命中即零成本；失败有负缓存 + 有界诊断。
