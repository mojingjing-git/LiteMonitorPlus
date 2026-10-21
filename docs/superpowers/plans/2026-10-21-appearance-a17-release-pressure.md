# A17 —— 释放压力（句柄 / GDI / USER / 内存）实测清单

> 日期：2026-10-21
> 状态：**工具已就绪，场景清单已定，实测待执行**（需要真实交互的 GUI 操作）
> 上游：A16（主面板 M2 接线与玻璃主题）✅
> 权威判据来源：`docs/superpowers/plans/2026-10-20-appearance-agent-execution.md` §5 工作包表 A17

---

## 1. A17 对「释放压力」的要求

> A17 | M2 故障、性能及发布验收 | A16 | 高面板/跨屏/隐藏恢复/快速换肤/释放压力，实际进程成本及支持矩阵；
> **无自反馈、孤立任务、句柄/内存持续增长**；未通过的后端不发布

拆成可判定的三条：

| # | 判据 | 含义 |
|---|---|---|
| R1 | **句柄/内存持续增长** | 反复操作后，句柄/GDI/USER 计数**不随操作次数线性增长** |
| R2 | **无自反馈** | 实时玻璃的画面里不能出现自己的窗口（抓屏包含自己 ⇒ 正反馈发散） |
| R3 | **无孤立任务** | 后台没有残留的线程 / 定时器 / 未退出的捕获循环 |

本文件只覆盖 **R1**；R2 / R3 分别在 §6 列出各自的验证归属。

---

## 2. 测量工具：`tests/AppearanceResourceProbe`

A17 此前**没有任何可用的测量手段** —— 「句柄/内存持续增长」这条判据只能靠人工看任务管理器。
本轮补了一个采样器。

### 2.1 它做什么

给定一个**正在运行**的 LiteMonitor 进程，按固定间隔采样：

| 指标 | 来源 |
|---|---|
| 句柄数 | `Process.HandleCount` |
| GDI 对象数 | `GetGuiResources(hProcess, GR_GDIOBJECTS)` |
| USER 对象数 | `GetGuiResources(hProcess, GR_USEROBJECTS)` |
| 私有内存 / 工作集 | `Process.PrivateMemorySize64` / `WorkingSet64` |

输出：时间序列（CSV + JSON）+ **后半段斜率**（个/分钟）+ **判定结论与退出码**。

> ★ 斜率只看**后半段**，避开启动/预热瞬态 ——
> 这与 A15 探针「CPU 按处理器时间、p95 按经过时间」的口径是同一个思路：**只看稳定态**。
>
> ★ 斜率用**最小二乘直线拟合**，不是两端点差分（x 取相对 `ElapsedMs`，避免数值稳定性问题）——
> 单次 GC 或一次 UI 拖动在两端点差分里能被放大成 ±几十，30 分钟长时场景尤易误判。
> 算法与判据的修订过程见 §9。

### 2.2 用法

```powershell
# 采样一个正在运行的 LiteMonitor（60 秒，间隔 1 秒）
tests/AppearanceResourceProbe/output/AppearanceResourceProbe.exe `
    --name LiteMonitor --label "空闲基线" --duration-sec 60 `
    --out docs/reviews/evidence/a17-release/idle

# 或按 pid
tests/AppearanceResourceProbe/output/AppearanceResourceProbe.exe --pid 12345 --label "换肤 x30"
```

| 参数 | 说明 |
|---|---|
| `--pid` / `--name` | 目标进程（`--name` 取内存占用最大的那个实例，避免选到更新器） |
| `--label` | 场景标签，写进证据 |
| `--interval-ms` | 采样间隔，默认 1000 |
| `--duration-sec` | 采样时长，默认 60 |
| `--out` | 证据目录，默认 `<可执行文件目录>\evidence` |
| `--idle-baseline` | 标记本轮为「空闲基线」：额外报告绝对增量 Δ，供人工确认 |

证据：`resource-samples.csv`（时间序列）+ `resource-samples.json`（含斜率与判定）。

### 2.3 两个必须知道的限制

1. **权限**：LiteMonitor 主工程是 `requireAdministrator`。目标进程以管理员身份运行时，
   采样器**也必须以管理员身份运行**，否则 `OpenProcess` 拿不到进程句柄，
   读不到 GDI/USER 计数（探针会明确报「OpenProcess 失败」并提示提权，不会静默给出假数据）。
2. ★ 本工程**故意不引用主工程**（只用 BCL + 三个 Win32 API）⇒
   编译不碰主工程的 `obj/` ⇒ **可以与其它会话的 build/test 并发执行**而不争抢；
   跑起来也不加载 LiteMonitor 的任何类型，不碰硬件、不抓屏。

### 2.4 判读阈值（统一门槛）

| 指标 | 门槛 | 含义 |
|---|---|---|
| 句柄 后半段最小二乘斜率 | **≤ 5 个/分钟** | 超过即判「句柄持续增长」 |
| GDI 后半段最小二乘斜率 | **≤ 5 个/分钟** | 超过即判「GDI 持续增长」 |
| USER 后半段最小二乘斜率 | **≤ 5 个/分钟** | 超过即判「USER 持续增长」 |
| 私存 单场景增量（末 − 首） | **≤ 30 MB** | 超过即判「内存持续增长」 |
| 后半段样本数 | **≥ 4 个** | 少于 ⇒ **不可判定**（退出码 3） |
| 后半段时间跨度 | **≥ 5 秒** | 少于 ⇒ **不可判定**（退出码 3） |

**只有一套硬门槛**：句柄 / GDI / USER 一律用「后半段最小二乘斜率 ≤ 5 个/分钟」。

> ★ 「空闲基线」**用同一个门槛**，另外把绝对增量 Δ 单独报告出来、供人工确认
> （采样时加 `--idle-baseline`，工具会明确提示这一点）。
>
> ★ 早先这里写的「空闲基线斜率**必须为 0**」已废弃 ——
> 同一个 PID 的 USER 对象数本身就有几个的噪声（实测 381 vs 378），
> 硬要求 0 会把正常抖动判成失败。详见 §9.4。

**退出码语义**：

| 码 | 含义 |
|---|---|
| `0` | 全部门槛通过 |
| `1` | 参数/环境错误 |
| `2` | 采样过程中出错 |
| `3` | **有门槛未通过**或**样本不足无法判定** |

> ★ `3` 是刻意设计的：**「测不出来」不等于「没问题」**。
> 样本不足或读数不可信时，工具只报「⚠ 无法判定」，**不会**给出 ✓ 通过结论。


---

## 2.5 外观探针的防回退入口（`scripts/run-probes.ps1`）

本文件 §3 的场景要人操作 GUI 才能跑，但**代码层面的防回退门槛**可以自动化 ——
那些探针（主题一致性 / M2 合同 / 接线 / 装饰 / 窗口轮廓 / W3 设置页回写）全是机器判定。

问题是：外观探针一律**不进 sln**，`dotnet build` / `dotnet test` 平时不会碰它们
⇒ 门槛只在有人想起来手动跑时成立。`scripts/run-probes.ps1` 就是补这个缺口：

```powershell
scripts/run-probes.ps1                                   # 默认：8 个探针 + 单元测试
scripts/run-probes.ps1 -SkipUnitTests                    # 只跑探针
scripts/run-probes.ps1 -IncludeBackdrop -OutDir D:\ev   # 连实机捕获一起跑，证据写别处
```

- **串行**构建 + 串行运行（并发会争抢主工程 `obj/`）
- 收录清单与命名由外观线定；`AppearanceBackdropProbe` 需要实机抓屏，默认跳过
- 失败时打印失败输出并以 exit 1 结束 ⇒ 可直接挂到任何本地 CI / 提交前钩子

> 这条是安全会话复核时指出的**真门槛问题**：
> 「探针不在 sln、无 CI、scripts 下也无统一入口 ⇒ 平时 build/test 不会跑它，
> 防回退只在有人想起时成立。」
> 工具齐了不等于门槛成立，**门槛需要一个入口**。

---

## 3. 实测场景清单

每个场景：**先启动采样器 → 在另一个窗口做操作 → 等采样结束 → 读斜率**。
建议每个场景跑 2 遍（首遍可能含 JIT/预热瞬态）。

| # | 场景 | 操作 | 靶子（预期能暴露的泄漏点） | 时长 |
|---|---|---|---|---|
| **S0** | **空闲基线** | 什么都不做 | 后台线程/定时器/捕获循环的自增长 | 120 s |
| **S1** | 设置页反复开关 | 打开设置页 → 切 3 个标签页 → 关闭，重复 **30 次** | 未挂载页面、Pen 泄漏、非模态入口不释放（见 §4） | 180 s |
| **S2** | 实时玻璃换肤 | 开启实时玻璃 → 连续切换主题 **30 次** | 显示亲和性设置/恢复、模糊缓冲、装饰资源缓存换肤淘汰 | 180 s |
| **S3** | 主面板隐藏/恢复 | 最小化 ↔ 还原，重复 **30 次** | 捕获句柄、亲和性恢复、`SetSuspended` 路径 | 120 s |
| **S4** | 主面板改尺寸 | 连续拖拽改变面板宽高 **100 次** | 尺寸代次 → 缓冲重分配、位图回收 | 120 s |
| **S5** | 长时运行 | 什么都不做，挂 30 分钟 | 慢性泄漏（定时器/事件订阅/日志缓冲） | 1800 s |

> ★ S1 要覆盖**两种入口**：托盘菜单「设置」（`Show()` 非模态）与任务栏双击（也是 `Show()`）。
> 模态入口在 `MenuManager` / `MenuMonitorHelper` 里走 `using { ShowDialog(); }`。
> 两者走的释放路径不同，**必须分别测**。

---

## 4. 已知泄漏点

### 4.1 已确认并已修（安全会话，`src/UI/SettingsForm.cs`）

| # | 泄漏点 | 修法 | 状态 |
|---|---|---|---|
| L1 | （**已排除**）曾怀疑非模态入口不触发 `Dispose` | 见 §7：对方以可复现探针证明 5 个入口全部覆盖 | **不成立，无需修** |
| L2 | `_pages` 里 6 个从未挂载的页面不被释放 | `DisposeCachedPages()` 显式释放 | 已修 |
| L3 | `pnlBottom.Paint` 每次重绘 `new Pen` 从不释放 | `using var pen` | 已修 |
| L4 | **设置页字体每次 `new Font`、从不释放** | `UIFonts` 改为进程内缓存 | 已修（见 §4.3） |

> L1 的核查过程与**被推翻的推断**见 §7 —— 那是一条「看起来很对、但没验证就下了结论」的推断，
> 留档是为了不让后人再推一遍同一个错。

### 4.2 候选点（静态审计所见，**必须用实测判定**）

静态审计只能指出「这里没有 `using`」，不能证明它在增长 —— GDI+ 对象是否真的占资源、
是否被别处释放，都要靠 §2 的采样器测。

| # | 候选点 | 位置 | 说明 |
|---|---|---|---|
| C1 | 空 `GraphicsPath` 不进缓存、也没人释放 | `src/UI/Decor/DecorResourceHost.cs` → `GetOrAddBandPath` 的「空间不足」分支 | 按 A05 的借用规则「返回的路径不要 Dispose（缓存拥有它）」，但这条**没进缓存** ⇒ 每次调用留一个托管对象。空路径不占 GDI 句柄，只影响托管堆 |
| C2 | `TextureBrush` 由 `DecorResourceCache` 托管 | `src/UI/Decor/DecorPainter.cs` → `CreateTextureBrush` / `CreateTileBrush` | 换肤时缓存淘汰是否 Dispose 旧画刷与位图 —— 需实测 |
| C3 | 静态 `Brush` / `Pen` 字段 | `src/UI/Controls/LiteTreeView.cs` | 进程级**常驻**（不增长），退出时不释放 ⇒ 属正常，不判失败 |
| C4 | 窗口轮廓的 `GraphicsPath` / `Region` | `src/UI/Helpers/WindowAppearanceController.cs` | A09 已定死所有权：路径构造完 Region 后**立即**释放，Region 交 `Control`（`Control` 会释放自己持有的旧 Region）⇒ 设计上无泄漏，实测确认 |
| C5 | 实时玻璃的显示亲和性 / 捕获 DC / 位图 / 模糊中间缓冲 | `src/System/Backdrop/**` | 每次换肤（S2）、改尺寸（S4）、隐藏恢复（S3）都走一遍重新分配 ⇒ **S2/S3/S4 的主要靶子** |
| C6 | 诊断日志缓冲 | `src/UI/LiveGlassController.cs` → `LiveGlassLog` | 只在状态变化时写，但长时间运行若不轮转会持续增长 ⇒ S5 观察私存 |

### 4.3 L4：设置页字体从未释放（已修）

`Control.Font` 是**借用**关系 —— `Control.Dispose()` 只把属性置空，**不会**释放 Font 实例。
所以 `new Font(...)` 赋给控件的每个字体，都是一个只靠终结器回收的 GDI 资源。

**泄漏面比预估大得多**：`src/UI/Controls/LiteUI.cs` 里的 `UIFonts.Regular(size)` / `UIFonts.Bold(size)`
**每次调用都 `new Font`**，而它被大量调用：

| 调用点 | 每处新建 |
|---|---|
| `src/UI/Controls/MonitorControls.cs` → 监控项控件 | 3 个（8F ×2、9F Bold ×2）**每个监控项** |
| `src/UI/Controls/LiteUI.cs` → `LiteSettingsGroup` 标题 | 1 个**每个设置组** |
| `src/UI/Controls/LiteUI.cs` → `LiteThresholdRow` 标题与箭头 | 2 个**每个阈值行** |
| `src/UI/Settings/PluginMarketPage.cs` | 4 个**每次进页面**（`RebuildUI` 每次重建） |
| `src/UI/Settings/MonitorPage.cs` | 4 个 |
| `src/UI/Settings/PluginPage.cs`、`MainPanelPage.cs`、`SettingsBase.cs` | 各 1 个 |

⇒ 打开一次设置页、监控项多一点，就能新建**几十个**字体对象；反复开关设置页就持续累积。

**最严重的一处**（复核时扩查 `LiteUI.cs` 全量才发现）：

```
src/UI/Controls/LiteUI.cs → LiteNavBtn.OnPaint
    Font = new Font(Font, FontStyle.Bold);   // ★ 每次重绘都 new 一个
```

⇒ **鼠标划过导航按钮就漏一个 GDI 字体对象** —— 这比设置页的泄漏严重得多，因为它随鼠标移动
**持续发生**，而不只是「每次开页一次」。

**修法**（不改成在页面 Dispose 里 Dispose 字体 —— 控件可能正在用它绘制，提前释放会画出错字）：

把 `UIFonts` 本身改成**进程内缓存**（与 `MenuManager.GetBoldFont` 同一思路）：

```csharp
private static readonly ConcurrentDictionary<(float Size, FontStyle Style), Font> _cache = ...;
public static Font Regular(float size) => Get(size, FontStyle.Regular);
public static Font Bold(float size) => Get(size, FontStyle.Bold);
```

⇒ 进程内**每种字号/样式只存在一个实例**，不再随操作次数增长。
`LiteUI.cs` 的 19 处全部改走缓存（含新增 `Underline` / `BoldUnderline` 变体），
`LiteNavBtn.OnPaint` 改为复用两个固定实例。

> ★ **S1 要断言的是「不变量」，不是一个具体数字。**
> 写死「字体实例恒为 N 个」是错的：字体键是全进程共享的，
> 光 `SettingsForm` 自己就会用到 6 个（Regular 8/9/10 + Bold 9/10 + Underline 9），
> 加上主窗口的 8.5 / Bold 7 / Bold 8，全进程去重后共 **9 个键**
> （`Regular 8 / 8.5 / 9 / 10`、`Bold 7 / 8 / 9 / 10`、`Underline 9`）。
> 这个数**还会变** —— 下次谁加一个字号，写死的断言就莫名其妙地红，然后有人会去「修」成旧数字。
>
> ⇒ **正确的断言**：`UIFonts.CachedCount` 在「反复开关设置页 30 次」前后**不变**。
> 这才是要测的不变量：不管缓存里有多少个键，**开关页面不能让它增长**。


安全性已核：所有调用点都只是 `Font = UIFonts.xxx(...)` 赋值，**没有任何地方修改返回的 Font 对象属性**；
且 `Control.Dispose` 不会释放借用的 Font ⇒ 共享实例不会被误释放。

> ★ 这条是「静态审计 → 实测」之间的一个例子：光看 `new Font(...)` 那一处（8 处）
> 只能看出 8 个；把 `UIFonts` 的调用链一起看，才知道实际是**几十个**；
> 再把 `LiteUI.cs` 全量扫一遍，才发现 `LiteNavBtn` 那个**随鼠标移动持续泄漏**的点。

---

## 5. 实测记录表（待填）

| 场景 | 轮次 | 句柄起→末 | 句柄斜率 | GDI 斜率 | USER 斜率 | 私存 Δ | 判定 |
|---|---|---|---|---|---|---|---|
| S0 空闲基线 | 1 | | | | | | 待测 |
| S0 空闲基线 | 2 | | | | | | 待测 |
| S1 设置页（模态入口） | 1 | | | | | | 待测 |
| S1 设置页（非模态入口） | 1 | | | | | | 待测 |
| S2 换肤 ×30 | 1 | | | | | | 待测 |
| S3 隐藏恢复 ×30 | 1 | | | | | | 待测 |
| S4 改尺寸 ×100 | 1 | | | | | | 待测 |
| S5 长时 30 分钟 | 1 | | | | | | 待测 |

---

## 6. R2 / R3 的验证归属

| 判据 | 验证方式 | 归属 |
|---|---|---|
| R2 无自反馈 | 实时玻璃开启时，玻璃里**不能**看到面板自身。录屏或截图后比对 —— 与 A14 清单的「录屏下瞬时排除是否闪动」同批做 | A14 清单第 5~7 项 / A17 实测 |
| R3 无孤立任务 | 关闭实时玻璃后，进程内应无残留捕获线程；可看任务管理器的线程数是否回落 | A17 实测（与 S3 合并） |
| 未通过的后端不发布 | 由 A17 汇总判定，不在本文档 | A17 汇总 |

---

## 7. 一条被推翻的推断（2026-10-21，留档）

外观线在核验安全会话的 `src/UI/SettingsForm.cs` 释放改动时，**推断**非模态入口不会触发 `Dispose`，
并据此建议加 `FormClosed` 钩子。该推断**被对方以可复现探针推翻** —— 记录在此，
以免后人再推一遍同一个错。

### 7.1 被推翻的推断

当时的推理是：`Form.Close()` 对非模态窗体走 `PostMessage(WM_CLOSE)` → `WmClose` → 只触发
`FormClosing` / `FormClosed`，**不**调 `Dispose()`；没有 `using`、窗体也没被字段持有 ⇒
`Dispose(bool)` 永远不被调用 ⇒ 未挂载页面照旧泄漏。

据此还追加了第二条推断：`Control.Dispose()` **不会**把控件从父容器的 `Controls` 里摘掉
⇒ `InitPages()` 里需要先 `_pnlContent?.Controls.Clear()`。

### 7.2 实测结论（安全会话，2026-10-21）

用 PowerShell 7（STA）加载 WinForms 复现**真实拓扑**（主窗体跑 `Application.Run` 消息循环，
设置窗体是次级非模态窗体 `.Show()`，Timer 里 `.Close()`）：

```
Close 前:                IsDisposed=False
Close 后（同一 Tick 内）: IsDisposed=True
Close 后 200ms:           IsDisposed=True  IsHandleCreated=False
仍在 Application.OpenForms: False
```

另一次探针里 `FormClosed` 与 `Disposed` 两个事件都真实触发（事件回调里读到
`IsDisposed=False` 是正常的 —— 它在 Dispose 完成后才翻转）。

**正确的机制**：`Form.Close()` 走 `PostMessage(WM_CLOSE)` 这半句没错，但 `WmClose` 在
**非模态分支**的最后就是调 `Dispose()`；只有**模态分支**才只隐藏不定时销毁，
留给 `ShowDialog` 返回后的 `using` 管。

**子控件 Dispose 的行为**：`Panel` 里放一个 `UserControl`，`CreateControl` 后
`parent.Controls.Count = 1`；调用 `uc.Dispose()` 后 `Count = 0`、`Contains = false` ——
`Control.Dispose()` **会**顺手把子控件从父容器摘掉。
`BufferedPanel`（`src/UI/Controls/LiteUI.cs`）没有重写 `Dispose`（只重写了 `CreateParams`
与 `WM_ERASEBKGND` 拦截），语义与 `Control` 一致。

### 7.3 结论

- 全仓库 5 个打开设置窗体的入口**全部**会走到 `Dispose(bool)`：
  三处 `using { ShowDialog(); }`（`MenuManager` 设置项、`MenuMonitorHelper` ×2）
  由 `using` 调 `Dispose`；两处 `new SettingsForm(...).Show()`
  （`MainFormBizHelper.OpenSettings`、`TaskbarBizHelper` 双击）由 `Close()` 自己 Dispose。
- ⇒ **不需要** `FormClosed` 钩子；加了也只是靠幂等养着的冗余代码。
- ⇒ **不需要** `_pnlContent?.Controls.Clear()`；不存在「已释放却仍挂在 `_pnlContent` 里」的悬挂页。
- L1 不成立。安全会话本轮**只有注释变更**，无功能改动；其 `DisposeCachedPages()` 的释放范围
  本来就覆盖全部 5 个入口。

> ★ 留档要点：上面两条推断都是**读源码推出来的、没验证就下了结论**的。
> 对方用**可复现的探针**推翻它们，比任何推理都可靠。
> A17 的释放压力判据本身就是「不靠推理、靠实测」—— 这条经验同样适用于本次核验本身。

---

## 8. 未验证项

- **本文档的所有场景都尚未实测** —— 需要真实交互的 GUI 操作（打开/关闭设置页、换肤、拖拽尺寸）。
  工具已就绪且**判据逻辑**已用四条实跑场景自测通过（见 §9.5）。
- S5（30 分钟长时）尤其需要一段不被打断的独占时间。
- 采样器本身**未**验证「目标进程以管理员身份运行」这一路径（自测目标是普通权限的 `explorer.exe`）。
  实测时若报 `OpenProcess 失败`，用管理员身份重跑采样器即可。
- §4.2 的候选点 C1~C6 **均未**经实测确认是否真的增长；静态审计不足以判定。

---

## 9. 一轮复核指出的判据缺口与处置（2026-10-21，留档）

安全会话复核 `378d7ef` + `8069eec` 时指出：**3 个判据缺口 + 1 个标准冲突**。
四项全部属实 —— 都是我方实现疏漏，不是意见分歧。已全部处置。

### 9.1 小样本假通过（缺口）

原实现：`SlopePerMinute` 在 `samples.Count < 4` 时 `return 0`，
而汇总末行**无条件**打「✓ 都在阈值内」。

实测复现：`--duration-sec 2` 时 USER 377 → 378，斜率报 `0.00`，**仍然输出 ✓**。

⇒ 处置：斜率改为返回 `double?`（`null` = **不可判定**）；
样本不足时输出「⚠ 样本不足，无法判定」、**不打 ✓**、退出码 `3`。

### 9.2 私存判据只写了没实现（缺口）

原实现：清单里写着「单场景私存 Δ ≤ 30MB」「空闲基线必须为 0」，
但 `PrivateBytes` **只被打印和导出**，判定逻辑只覆盖句柄 / GDI / USER。

⇒ 处置：补上私存判定（单场景增量 = 末样本 − 首样本，门槛 30MB）。

### 9.3 斜率算法是两端点差分（缺口）

原实现：`斜率 = (末值 − 首值) / 跨度`。
单次 GC 或一次 UI 拖动就能把两端点差分拉成 ±几十，30 分钟长时场景尤易误判。

⇒ 处置：改为**最小二乘直线拟合**，x 取相对 `ElapsedMs`（避免绝对时间戳的数值稳定性问题）。

### 9.4 两套门槛打架（标准冲突）

清单里同时存在「后段斜率 ≤ 5 个/分钟」和「空闲基线必须为 0」两套门槛，
而工具只实现了前者 —— 清单说的和工具做的对不上。

**裁定：写死一套统一门槛。**

- 句柄 / GDI / USER：后半段最小二乘斜率 **≤ 5 个/分钟**；
- 私存：单场景增量 **≤ 30MB**；
- 空闲基线：**用同一个门槛**，绝对增量 Δ 单独报告、供人工确认。

> ★ 「必须为 0」被推翻的理由：**同一个 PID 的 USER 对象数本身就有几个的噪声**
> （实测 381 vs 378）。硬要求 0 会把正常抖动判成失败 ——
> 那是把门槛调得**更严**，不是更准。
>
> ★ 这条与「可读性问题的修法是改配色、不是放宽判据」不冲突：
> 统一门槛没有**放宽**任何一个数值（仍是 5 个/分钟、30MB），
> 只是把「空闲基线必须为 0」这个**写死且不可实现**的判据，换成了
> 「同一门槛 + Δ 单独报告」。

### 9.5 额外补的一条：进程中途退出 ⇒ 假通过

复核时顺带发现、我方补上：目标进程中途退出后，句柄 / GDI / USER 会掉到 `0`，
在旧实现里看起来像「不增长」⇒ **假通过**。

⇒ 处置：采样时标记读数失败；证据里有失败样本 ⇒ **不可判定**（退出码 3）。

### 9.6 四条实跑自测（2026-10-21）

| # | 场景 | 期望 | 实测 |
|---|---|---|---|
| 1 | `--duration-sec 2`（样本不足） | 不可判定，exit 3，无 ✓ | exit 3，「⚠ 句柄 / GDI 对象 / USER 对象 —— 样本不足，无法判定」，无 ✓ |
| 2 | `--duration-sec 12 --interval-ms 500`（空闲基线） | 通过，exit 0 | exit 0，斜率全 0.00，输出 ✓ + Δ 人工确认提示 |
| 3 | 目标进程 6 秒后退出（采样 12 秒） | 不可判定，exit 3 | exit 3，「⚠ 有样本读不到目标进程 —— 读数不可信」 |
| 4 | 目标进程持续开句柄（每 100ms 一个，采样 30 秒） | 未通过，exit 3 | exit 3，「✗ 句柄 —— 后半段斜率 549.00 个/分钟 > 门槛 5.00」 |

> 第 4 条顺带验证了最小二乘的有效性：同一份数据，两端点差分会给出约 2022 个/分钟
> （被启动瞬态放大），最小二乘给出 549 个/分钟（≈ 实际 600 个/分钟的量级）。

> ★ 另一条留档在 §7：外观线曾**读源码推理**后断言非模态入口不触发 `Dispose`，
> 被安全会话以可复现探针推翻。§7 与本节是同一条经验的两面 ——
> **判据与结论都要靠实测**，不能靠推理。

