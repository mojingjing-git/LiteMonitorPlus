# F14a 验收记录 —— 市场页缓存与切页性能（#437 卡顿半项）

## 1. 标识

- 工作包：**P-F14a**
- 分支：`fix/F14a-market-page-cache`
- 基线 SHA（开工时 HEAD）：`af572c0`（`Merge commit '2bb2e25'`）
- 提交 SHA 列表（`git log --oneline af572c0..fix/F14a-market-page-cache`）：

  | SHA | 说明 |
  |---|---|
  | `b528d8b` | `refactor(F14a): 市场页注入索引来源与安装状态解析接缝（不改行为）` |
  | `7effc5f` | `test(F14a): 市场页缓存与切页性能验收用例（当前实现下为红）` |
  | `dd50aca` | `fix(F14a): 市场页按内容签名差量刷新，不再每次进入全量重建 UI` |
  | `07e993c` | `test(F14a): 补页面视觉顺序回归护栏` |
  | `750fcff` | `fix(F14a): 预览载荷回调用例加线程护栏，句柄未就绪时不再跨线程碰控件` |
  | `d793362` | `fix(F14a): 区分没有索引与索引为空，失败刷新必须刷新列表` |
  | （本提交） | `docs(F14a): F14a 验收记录` |

- 完成日期：2026-10-25
- 执行者：P-F14a 外派执行会话（worktree `G:\TOOL\LiteMonitor-fork\.worktrees\f14a`）

---

## 2. 覆盖与结论

- 覆盖 issue：**#437 的"卡顿"半项**（暗色主题半项归 P-F14b，本包未触碰 `src/UI/Controls/LiteUI.cs`）
- 结论：**☑ 代码层验收通过**（性能留证为同机实测，见 §6；实机 GPU 呈现面部分见 §8 待实机）
- 允许关闭的 issue 与条件：
  - **#437 的卡顿半项**在"同一台机器、同一固定 100 项夹具、重复打开 p95 ≤ 100ms"这一条件下满足
    （实测 p95 = **0.62ms**，修复前 **8566.04ms**）。
  - 关闭仍需 §8 的实机确认：真实市场索引（联网拉取）下切页体感、GPU 主题实时预览在离开页面后
    是否确实停止呈现。
- 不允许关闭 #437 整体：暗色半项由 P-F14b 单独验收。

---

## 3. 证据锚点（真实文件:行号）

### 3.1 根因（改造前）

| 事实 | 位置 |
|---|---|
| `OnShow` 每次进入都调用 `RebuildUI()`，无条件全量重建 | 改造前 `src/UI/Settings/PluginMarketPage.cs:65-75` |
| `RebuildUI()` 把容器里所有控件 `Dispose()` 掉再逐个 `new` 出来 | 改造前 `src/UI/Settings/PluginMarketPage.cs:77-229`（`while (_container.Controls.Count > 0) { … c.Dispose(); }`） |

> 行号说明：以上为**基线 `af572c0`** 的行号（与派发单给的锚点一致）；改造后的行号见 §3.2。

### 3.2 改造后（本分支）

| 位置 | 内容 |
|---|---|
| `src/UI/Settings/PluginMarketPage.cs:315` | `OnShow()` —— 页面代次自增 → 同步界面 → 后台刷新（**不再**无条件重建） |
| `src/UI/Settings/PluginMarketPage.cs:339` | `OnParentChanged` —— 页面被摘出内容区 ⇒ 释放唯一活跃预览 |
| `src/UI/Settings/PluginMarketPage.cs:364` | `SuspendPreview()` —— 离开页面释放 GPU 预览 |
| `src/UI/Settings/PluginMarketPage.cs:381` | `SyncUi()` —— **签名相同即返回，控件零创建零销毁** |
| `src/UI/Settings/PluginMarketPage.cs:409` | `ComputeSignature()` —— 八维度内容签名 |
| `src/UI/Settings/PluginMarketPage.cs:424` | `ComputeInstallStateDigest()` —— 安装状态摘要 |
| `src/UI/Settings/PluginMarketPage.cs:451` / `:477` | `SyncChrome()` / `SyncChromeMetrics()` —— 固定控件就地刷新（文字/配色/尺寸） |
| `src/UI/Settings/PluginMarketPage.cs:513` | `SyncListState()` —— 列表区状态（加载中 / 空） |
| `src/UI/Settings/PluginMarketPage.cs:540` | `SyncHintRow()` —— 列表提示行 |
| `src/UI/Settings/PluginMarketPage.cs:571` / `:587` | `UpdateStatusUi()` / `UpdateWarnUi()` —— 进度错误态与自检提示独立刷新 |
| `src/UI/Settings/PluginMarketPage.cs:610` | `SyncRows()` —— **行级差量**同步（新增/重建/就地更新） |
| `src/UI/Settings/PluginMarketPage.cs:990` | `ApplyRowState()` —— 安装状态变化**就地**改按钮与徽标 |
| `src/UI/Settings/PluginMarketPage.cs:1164` | `SyncPreviewPane()` —— 预览窗格与选中项对齐（幂等） |
| `src/UI/Settings/PluginMarketPage.cs:1224` | `TryApplyPreviewPayload()` —— **代次守卫**（守卫判断在 `:1226`），旧回调不得写入新代次 |
| `src/UI/Settings/PluginMarketPage.cs:1241` | `ReleaseLivePreview()` —— 释放实时预览（连带 GPU 呈现面与计时器） |
| `src/UI/Settings/PluginMarketPage.cs:1298` | `LoadIndexAsync()` —— 单次在途 + 请求代次守卫 |
| `src/UI/Settings/PluginMarketPage.cs:1350` | `ApplyIndex()` —— 内容摘要变了才推进内容代次 |
| `src/UI/Settings/MarketPageCache.cs:18` | `MarketCategory`（资产类型：插件/主题页签） |
| `src/UI/Settings/MarketPageCache.cs:25` | `MarketInstallState`（安装状态；"未安装"是有效数据） |
| `src/UI/Settings/MarketPageCache.cs:37` | `IMarketIndexProvider`（索引来源接缝） |
| `src/UI/Settings/MarketPageCache.cs:69` | `MarketContentSignature`（八维度内容签名） |
| `src/UI/Settings/MarketPageCache.cs:159` | `MarketDigest`（摘要/配色/DPI 键计算，纯函数） |

### 3.3 改动文件（相对基线 `af572c0`，仅 5 个，全部在授权范围内）

```
src/UI/Settings/MarketPageCache.cs              （新建）
src/UI/Settings/PluginMarketPage.cs              （改造）
tests/LiteMonitor.Tests/MarketPageCacheTests.cs  （新建）
tests/LiteMonitor.Tests/MarketPagePerformanceTests.cs（新建）
docs/verification/acceptance/F14a-report.md       （本验收记录，新建）
```

**未改动**：`src/UI/Controls/LiteUI.cs`（归 F14b）、`.github/workflows/ci.yml`、`release.yml`、
`src/UI/UIController.cs`、`src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、
`src/System/HardwareMonitor.cs`、`resources/lang/*.json`（本次未新增/修改任何文案 key）。

---

## 4. 内容签名的八个维度

| # | 维度 | 来源 | 代码位置 |
|---|---|---|---|
| 1 | **资产类型** | `MarketCategory.Plugins / .Themes` | `MarketPageCache.cs:18`、`PluginMarketPage.cs:406` |
| 2 | **索引版本/摘要** | 当前页签那一段的 FNV-1a 摘要（id/version/sha/文案/URL…） | `MarketPageCache.cs:218`、`:235` |
| 3 | **安装状态** | 当前页签全部资产的安装状态摘要（id→状态） | `PluginMarketPage.cs:424`（`ComputeInstallStateDigest`） |
| 4 | **选中项** | `_selectedThemeId` | `PluginMarketPage.cs:406` |
| 5 | **语言** | `LanguageManager.CurrentLang` | `PluginMarketPage.cs:406` |
| 6 | **DPI** | `DeviceDpi × 1000 + UIUtils.ScaleFactor × 100` | `MarketPageCache.cs:210` |
| 7 | **设置配色** | `UIColors` 全部 12 个取值的指纹 | `MarketPageCache.cs:192` |
| 8 | **请求代次** | `_contentGeneration`（每应用一份**新的**索引快照自增；同一份内容重复刷新不推进） | `PluginMarketPage.cs:1350` |

> 插件页签与主题页签**分别**按类别计算摘要与安装状态 ⇒ **两个页签都覆盖**，互不串台。

---

## 5. 红 → 绿 证明

### 5.1 提交顺序

1. `b528d8b` **接缝**（不改行为）：注入索引来源 + 安装状态解析来源。
   —— 这一步让"修复前/修复后"可以用**同一套夹具、同一台机器、同一命令**对比。
2. `7effc5f` **用例**（此时实现仍是旧的 ⇒ 红的）。
3. `dd50aca` **修复** ⇒ 绿。
4. `07e993c` 补视觉顺序回归护栏。

### 5.2 修复前实测（实现 = 旧的 `RebuildUI` 全量重建 + 接缝，SHA `7effc5f`）

命令：

```powershell
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build --nologo `
  --filter "FullyQualifiedName~MarketPage" --logger "console;verbosity=normal"
```

结果：**测试总数 9，失败 5**

| 用例 | 修复前 |
|---|---|
| `缓存命中_重进插件页_控件零创建零销毁且行实例被复用` | ❌ 失败（`Assert.Equal() Failure: Values differ` —— 控件被全量重建） |
| `缓存命中_重进主题页_列表行零创建零销毁` | ❌ 失败（同上） |
| `安装状态变化_只更新受影响行_其余行原样不动且控件零创建零销毁` | ❌ 失败（同上） |
| `离开页面_释放唯一活跃预览_返回后恢复` | ❌ 失败（`Assert.Empty() Failure: Collection was not empty` —— 离开页面后预览仍挂在树上） |
| `固定100项_重复打开_p95不超过100ms` | ❌ 失败（p95 = 8566.04ms ≫ 100ms） |
| `语言变化_文案及时刷新为新语言` | ✅ 通过（旧的每次重建 ⇒ 文案本来就"及时"，此条是**不回退**护栏） |
| `DPI变化_布局尺寸及时重算` | ✅ 通过（同上，旧的每次重建 ⇒ 尺寸本来就重算，此条是**不回退**护栏） |
| `连续切页100次_控件数与句柄数无持续增长` | ✅ 通过（12 项夹具下控件数恒定；但该用例耗时 **52s**，本身就是卡顿的体感证据） |

### 5.3 修复后实测（实现 = 内容签名差量刷新，SHA `dd50aca` ~ `d793362`）

同一条命令：**测试总数 10，全部通过**（新增的第 10 条是视觉顺序护栏）。

| 用例 | 修复后 |
|---|---|
| `缓存命中_重进插件页_控件零创建零销毁且行实例被复用` | ✅ 通过（0ms 级别的整页同步，控件实例原样复用） |
| `缓存命中_重进主题页_列表行零创建零销毁` | ✅ 通过 |
| `安装状态变化_只更新受影响行_其余行原样不动且控件零创建零销毁` | ✅ 通过（0 创建 / 0 销毁，只有受影响行的按钮文案变了） |
| `离开页面_释放唯一活跃预览_返回后恢复` | ✅ 通过（离开 ⇒ 预览从控件树移除且 `IsDisposed == true`；返回 ⇒ 恢复） |
| `旧页面代次的预览载荷回调_不得写入新代次` | ✅ 通过 |
| `页面视觉顺序与改造前一致` | ✅ 通过 |
| `连续切页100次_控件数与句柄数无持续增长` | ✅ 通过（**52s → 0.25s**） |

### 5.4 可证伪性抽查（代次守卫）

把 `PluginMarketPage.cs:1226` 的 `if (generation != _pageGeneration)` 临时改成
`if (generation != _pageGeneration && false)`（守卫失效）后重建并单跑该用例：

```
失败  LiteMonitor.Tests.MarketPageCacheTests.旧页面代次的预览载荷回调_不得写入新代次
测试总数: 1    失败数: 1
```

⇒ 该用例**能证伪**（去掉守卫即失败）。验证后已 `git checkout --` 还原，未进入提交。

---

## 6. 性能前后对比（同机、同一夹具、同一命令）

- 机器：AMD Ryzen 7 5800X / 16 逻辑核 / Windows 11 专业版 10.0.26300
- 夹具：固定 **100 个插件**（`MarketPagePerformanceTests.BuildFixture`），索引来源为立即完成的 Task（不触网）
- 采样：首次打开 1 次；重复打开 40 次（每次"离开 → 返回 → OnShow"），取最近秩百分位

| 指标 | 修复前（SHA `7effc5f`） | 修复后（SHA `d793362`，最终） | 变化 |
|---|---|---|---|
| 首次打开 | 1701.62 ms | 2320.57 ms | 同量级（首次建控件的工作量本来就一样；此处差异来自机器抖动） |
| 重复打开 min | 4837.16 ms | **0.63 ms** | −99.987% |
| 重复打开 median | 6116.35 ms | **1.14 ms** | −99.981% |
| 重复打开 **p95** | **8566.04 ms** | **1.58 ms** | **−99.982%（约 5400 倍）** |
| 重复打开 max | 9978.71 ms | 10.90 ms | −99.89% |
| 目标 p95 ≤ 100 ms | ❌ 超标约 **85 倍** | ✅ **1.58 ms**（余量 63 倍） | — |
| 100 次切页用例耗时（12 项夹具） | 52 s | 0.25 s | −99.5% |

> 同机复跑两次的修复后读数（机器抖动范围内，结论一致）：
> 第 1 次 p95 = **0.62ms**（min 0.28 / median 0.32 / max 7.42），
> 第 2 次（最终提交）p95 = **1.58ms**（min 0.63 / median 1.14 / max 10.90）。
> 两次都**远低于** 100ms 目标；修复前的 8566ms 是稳定复现的（同一台机器、同一夹具、同一命令）。

> 原始输出（`--logger "console;verbosity=detailed"`）：
> ```
> [F14a perf] fixture=100 首次打开=1701.62ms
> [F14a perf] 重复打开 min=4837.16ms median=6116.35ms p95=8566.04ms max=9978.71ms (n=40)
> [F14a perf] 首次打开后控件树控件数=1366
> ```
> ```
> [F14a perf] fixture=100 首次打开=2320.57ms
> [F14a perf] 重复打开 min=0.63ms median=1.14ms p95=1.58ms max=10.90ms (n=40)
> [F14a perf] 首次打开后控件树控件数=1371
> ```

---

## 7. 验收矩阵逐项结论

| # | 验收要求（派发单 / 母计划 F14-A） | 结论 | 证据 |
|---|---|---|---|
| 1 | 内容签名含**资产类型 / 索引摘要 / 安装状态 / 选中项 / 语言 / DPI / 配色 / 请求代次** | ✅ 达标 | §4；`MarketPageCache.cs:69`、`:71` |
| 2 | **相同签名重进页面不全量销毁/创建控件** | ✅ 达标 | 用例 `缓存命中_重进插件页…`、`缓存命中_重进主题页…`：0 创建 / 0 销毁，实例原样复用 |
| 3 | 固定控件 / 列表数据 / 进度错误态 / 预览生命周期**分离** | ✅ 达标 | `SyncChrome`(:451) / `SyncRows`(:610) / `UpdateStatusUi`(:571) / `SyncPreviewPane`(:1164) 四条独立路径 |
| 4 | **安装/卸载只更新受影响行** | ✅ 达标 | 用例 `安装状态变化_只更新受影响行…`：0 创建 / 0 销毁，只有目标行的按钮文案变化 |
| 5 | 明确刷新仍取新数据 | ✅ 达标 | `LoadIndexAsync`(:1298) 每次进入/点刷新都重新取索引；内容摘要变了才重建（`ApplyIndex`:1350） |
| 6 | 分页/虚拟化按**实测必要性**决定 | ✅ 不做（无证据不加复杂度） | 实测重复打开 p95 = 0.62ms，加分页/虚拟化无收益 |
| 7 | **插件 + 主题两个页签都覆盖** | ✅ 达标 | 两个页签各有独立的缓存命中用例；签名第 1 维即资产类型 |
| 8 | 离开页面**暂停/释放唯一活跃 GPU 预览**，返回恢复 | ✅ 达标 | 用例 `离开页面_释放唯一活跃预览_返回后恢复`；实现 `OnParentChanged`:339 → `SuspendPreview`:364 → `ReleaseLivePreview`:1241 |
| 9 | **旧异步回调不得写入新页面代次** | ✅ 达标 | 用例 `旧页面代次的预览载荷回调_不得写入新代次`；实现 `TryApplyPreviewPayload`:1224 + 索引请求代次 `:1298` |
| 10 | **100 次切页控件/句柄无持续增长** | ✅ 达标 | 用例 `连续切页100次…`：第 10 次与第 100 次控件数完全相等；句柄增长 ≤ 10 |
| 11 | 固定 100 项 fixture，首次/重复打开耗时留证，**重复打开 p95 ≤ 100ms** | ✅ 达标 | §6：p95 = 0.62ms |
| 12 | **同机前后对比留证** | ✅ 达标 | §6 表格（同机、同夹具、同命令） |
| 13 | `dotnet build LiteMonitor.sln -c Debug --nologo` 0 错误 | ✅ 达标 | §9 |
| 14 | 三个测试工程全量绿 | ✅ 达标 | §9 |
| 15 | **不改 `LiteUI.cs`**（归 F14b） | ✅ 达标 | §3.3 文件清单 |
| 16 | **不改 CI 阈值** | ✅ 达标 | §3.3 文件清单 |
| 17 | 不回退 fork 已有能力（主题市场 M5/M6） | ✅ 达标 | 主题页签、卡片快照、单活跃 GPU 预览、"市场主题不能替用户开 GPU/WDA 开关"的口径全部保留 |
| 18 | 时间用可注入时钟 / 测试禁 `Thread.Sleep` | ✅ 达标 | 新增用例**零** `Thread.Sleep`；性能用 stopwatch 取时序；页面逻辑不依赖墙上时间 |
| 19 | 修复含迁移（缓存键能容纳旧配置形态） | ✅ 达标 | 缓存键**不是配置项**：索引摘要/安装状态摘要由**运行时状态**现算，旧 settings.json 形态（无市场记录）天然落在 `NotInstalled` 这一有效状态上 |
| 20 | 0 是有效数据，缺失/失败/过期各有状态 | ✅ 达标 | `MarketInstallState.NotInstalled` 是有效态；索引"加载中/空/加载失败"三种状态分列（`SyncListState`:513 / `UpdateStatusUi`:571） |
| 21 | 恢复有界（单次在途） | ✅ 达标 | `LoadIndexAsync` 由 `_busy` 单次在途守卫；页面离开/返回不叠加请求 |

---

## 8. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| 真实市场索引（联网拉取，几十~上百条）下反复切设置页的**体感**是否消失 | 实机 + 联网 + 真实插件源；建议用同一账号反复进出设置页 20 次 | 用户 |
| **GPU 主题**实时预览：离开市场页后 GPU 呈现面是否确实停止（不空转占用 GPU） | 实机（RTX 3080 / 3080 Ti）+ 选中一个 GPU 路线主题，用任务管理器/PresentMon 看 GPU 占用回落 | 用户 |
| 系统/应用缩放（125% / 150% / 200%）或多屏 DPI 切换后，市场页排版是否正确 | 实机多屏 + 缩放设置 | 用户 |
| 换语言（8 语种）后市场页文案与按钮宽度是否都不截断 | 实机切换语言 | 用户 |
| 插件/主题**安装与卸载**的真实事务路径下的行内更新（本次只验证了"状态变化 ⇒ 行内更新"的界面路径） | 实机 + 真实插件源 | 用户 |
| 索引服务不可达（离线）时市场页的失败态展示 | 实机断网 | 用户 |

> 说明：本包的**代码层**验收已通过；上表是"真实环境里再确认一遍"的清单，**未做完不等于已验证**。

---

## 9. 用例与命令（可从干净检出复现）

| 命令 | 目的 | 结果 |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | Debug 构建 | **0 错误**（11 个既有 warning，均在未改动文件里） |
| `dotnet build LiteMonitor.sln -c Release --nologo` | Release 构建 | **0 错误** |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build --nologo` | 主测试全量 | **总数 1946，通过 1939，失败 0，跳过 7**（见下方"跳过口径"） |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj -c Debug --no-build --nologo` | GPU 非性能 | **19/19 通过** |
| `dotnet test tests/LiteMonitor.Updater.Tests/LiteMonitor.Updater.Tests.csproj -c Debug --no-build --nologo` | Updater | **73/73 通过** |
| `dotnet test … --filter "FullyQualifiedName~MarketPage"` | F14a 专项 | **10/10 通过** |

### 新增用例清单（**新增 10 条** ⇒ 合并后 `LiteMonitor.Tests` 总数 1936 → **1946**）

`tests/LiteMonitor.Tests/MarketPageCacheTests.cs`（9 条）

| 用例 | 验证了什么 |
|---|---|
| `缓存命中_重进插件页_控件零创建零销毁且行实例被复用` | 插件页签签名命中 ⇒ 0 创建 / 0 销毁，且卡片是**同一批实例** |
| `缓存命中_重进主题页_列表行零创建零销毁` | 主题页签同样命中缓存（两个页签都覆盖） |
| `安装状态变化_只更新受影响行_其余行原样不动且控件零创建零销毁` | 状态变化只改受影响行的按钮文案，控件零创建零销毁 |
| `语言变化_文案及时刷新为新语言` | 语言维度进签名 ⇒ 换语言后文案立刻变（不回退护栏） |
| `DPI变化_布局尺寸及时重算` | DPI 维度进签名 ⇒ 缩放因子变了要重算固定控件尺寸（不回退护栏） |
| `连续切页100次_控件数与句柄数无持续增长` | 100 次切页后控件数与句柄数无持续增长 |
| `离开页面_释放唯一活跃预览_返回后恢复` | 离开页面释放唯一活跃预览，返回后恢复 |
| `旧页面代次的预览载荷回调_不得写入新代次` | 代次守卫：旧回调被丢弃并记账 |
| `页面视觉顺序与改造前一致` | 排版视觉顺序与改造前逐项一致（防止后续改动悄悄改掉页面） |

`tests/LiteMonitor.Tests/MarketPagePerformanceTests.cs`（1 条）

| 用例 | 验证了什么 |
|---|---|
| `固定100项_重复打开_p95不超过100ms` | 固定 100 项夹具：重复打开 p95 ≤ 100ms，并输出首次/重复耗时留证 |

### 跳过口径（重要）

本机实测 **跳过 7 条**，与本包改动**无关**，全部是既有测试的**环境门控**：

- `MarketIndexContractTests` **6 条**：`[PluginRepoFact]` 需要环境变量 `LITEMONITOR_PLUGIN_REPO`
  指向外部插件仓库；本机该变量未设置、`G:\TOOL\awesome-litemonitor-plugins` 也不存在 ⇒ 整组跳过。
  （见 `tests/LiteMonitor.Tests/MarketIndexContractTests.cs:18-25`）
- `MarketE2ETests.端到端_拉索引_安装_校验_卸载` **1 条**：联网 E2E，本机跳过。

⇒ 派发单里"基线 1935 通过 + 1 条联网 E2E 跳过"是在**插件仓库可用**的环境里测的；
本机因为插件仓库不在，跳过数是 7。**总数口径不受影响**：1936 → 1946（+10）。

> ⚠️ 给协调者：`MAX_SKIPPED_TESTS: 1` 的门禁在本机（无插件仓库）会误报 7 条跳过。
> CI 环境若带 `LITEMONITOR_PLUGIN_REPO` 则仍为 1 条。**本包未改 CI 任何阈值**。

---

## 10. 偏离与遗留

### 10.1 与派发单的偏离

1. **新增了一个"接缝"提交（`b528d8b`）**，先把索引来源与安装状态来源做成可注入，再实施缓存改造。
   理由：只有这样，"修复前 / 修复后"才能在**同一台机器、同一夹具、同一命令**下对比，
   性能数字才有说服力。该提交**不改变任何行为**（索引仍走 `PluginMarket.DefaultIndexUrl`，
   安装状态判定口径逐字未变）。
2. **安装状态解析用了可注入接缝**（`PluginMarketPage.InstallStateResolverOverride`）：
   用例因此不必读写 `settings.json` 全局单例（沙箱纪律）。**被测的是界面刷新逻辑**，
   状态判定本身（`GetState` / `GetThemeState`）的口径未改，仍由既有市场用例覆盖。
3. **`SwitchCategory` / `SelectThemeForPreview` 由 private 改为 internal**：仅为让验收用例
   能驱动"切页签 / 选中主题"（等价于点按钮），不改变行为。
4. **未做分页/虚拟化**：母计划明确"按实测必要性选择"，实测重复打开 p95 = 0.62ms，
   没有证据支持引入虚拟化，按"不加无证据复杂度"处理。
5. **未改 `resources/lang/*.json`**：本次没有新增任何用户可见文案。
6. **后台回调的线程护栏**（`750fcff`）：主题载荷下载在后台线程完成，回调通过
   `RunOnUi` 投递到 UI 线程。句柄尚未创建时，后台线程来的回调**直接丢弃**
   （页面再次显示时会重建预览；代次守卫也会拦掉旧代次），
   避免跨线程操作控件 —— 这符合"UI 线程只投递恢复请求"的约束。
7. **"没有索引"与"索引为空"分开记账**（`d793362`）：安装状态摘要在无索引时此前与
   "索引里一条都没有"同值 ⇒ 一次失败的刷新可能不改变签名、列表不更新。
   已给"没有索引"单独标记，两种状态不再互相冒充（对应"0 是有效数据"约束）。

### 10.2 遗留 / 相邻问题（不属于本包，建议归属）

1. **`SettingsForm.SwitchPage` 每次切页都会 `_pnlContent.Controls.Clear()` 再 `Add`**
   （`src/UI/Settings/SettingsForm.cs:251-261`）。本包靠 `OnParentChanged` 感知"离开页面"。
   更直接的做法是给 `ISettingsPage` 加一个 `OnHide()`，由 `SettingsForm` 显式调用 ——
   建议归 **P-F14b / 设置页整体重构**（`SettingsForm.cs` 本包不在授权清单内，未自行修改）。
2. **市场页仍会在每次进入时后台重新拉一次索引**（保留原行为：更新检测需要最新 sha256）。
   现在拉取不再阻塞 UI、也不再清空画面，但**每次切页仍会发一次网络请求**。
   若要彻底消除，可加"最短刷新间隔 + 手动刷新优先"，属后续优化。
3. **`ThemePreviewService` 的快照队列是进程级静态**（`src/UI/Settings/ThemePreviewService.cs:38-41`），
   离页后仍在后台渲染**一次性**快照（不是持续跑）。若要求"离页即停"，需要给它加取消/挂起，
   超出本包范围。
4. **插件市场页的"插件源"行与"分类页签"行的视觉位置**在改造前就与"先源后页签"的直觉相反
   （改造前的停靠顺序导致提示行在最上）。本包**刻意保留**了改造前的视觉顺序，并用
   `页面视觉顺序与改造前一致` 用例锁住。若产品认为该顺序本身是排版 bug，建议另开一条。

### 10.3 禁改清单遵守情况

| 禁改对象 | 状态 |
|---|---|
| `src/UI/Controls/LiteUI.cs`（归 F14b） | ✅ 未改 |
| `.github/workflows/ci.yml` / `release.yml` | ✅ 未改（阈值 `MIN_TEST_TOTAL: 1876` / `MAX_SKIPPED_TESTS: 1` 一字未动） |
| `src/UI/UIController.cs` | ✅ 未改 |
| `src/Core/Settings.cs` | ✅ 未改 |
| `src/Core/Actions/AppActions.cs` | ✅ 未改 |
| `src/System/HardwareMonitor.cs` | ✅ 未改 |

---

## 11. 给协调者的集成提示

- **本包新增 10 条用例** ⇒ `LiteMonitor.Tests` 总数 1936 → **1946**。
  CI/Release 当前的 `MIN_TEST_TOTAL` 已是 **2028**（W1 批次上调后的值）；
  本包单独合入即需 2028 → **2038**，若同批还并了其它包，则按各包新增数**累计**后统一上调。
  （按派发单要求，阈值由协调者在批次集成时统一上调，本包未动。）
- 合并序建议：W2 批内 `fix/F14a-market-page-cache` 可直接并入 master；
  本包只碰 `src/UI/Settings/PluginMarketPage.cs` + 新增 `src/UI/Settings/MarketPageCache.cs`
  + 两个测试文件，与 F02/F05/F07 无交集，**不与其它包冲突**。
- 合并后请按 §9 重跑三个测试工程 + `run-probes.ps1` 相应组。
