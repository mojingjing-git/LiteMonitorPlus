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

输出：时间序列（CSV + JSON）+ **后半段斜率**（个/分钟）。

> ★ 斜率只看**后半段**，避开启动/预热瞬态 ——
> 这与 A15 探针「CPU 按处理器时间、p95 按经过时间」的口径是同一个思路：**只看稳定态**。

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

证据：`resource-samples.csv`（时间序列）+ `resource-samples.json`（含斜率与判定）。

### 2.3 两个必须知道的限制

1. **权限**：LiteMonitor 主工程是 `requireAdministrator`。目标进程以管理员身份运行时，
   采样器**也必须以管理员身份运行**，否则 `OpenProcess` 拿不到进程句柄，
   读不到 GDI/USER 计数（探针会明确报「OpenProcess 失败」并提示提权，不会静默给出假数据）。
2. ★ 本工程**故意不引用主工程**（只用 BCL + 三个 Win32 API）⇒
   编译不碰主工程的 `obj/` ⇒ **可以与其它会话的 build/test 并发执行**而不争抢；
   跑起来也不加载 LiteMonitor 的任何类型，不碰硬件、不抓屏。

### 2.4 判读阈值

| 指标 | 阈值 | 含义 |
|---|---|---|
| 句柄后半段斜率 | **≤ 5 个/分钟** | 超过即判「句柄持续增长」 |
| GDI 后半段斜率 | **≤ 5 个/分钟** | 超过即判「GDI 持续增长」 |
| USER 后半段斜率 | **≤ 5 个/分钟** | 超过即判「USER 持续增长」 |
| 单场景私存增量 | **≤ 30 MB** | 超过即判「内存持续增长」 |
| **空闲基线**斜率 | **必须为 0** | 空闲时任何持续增长都是 bug（后台线程/定时器在漏） |

> 空闲基线是**最灵敏**的一项：什么都不做时如果计数还在涨，不用再分场景排查，直接判不通过。

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
  工具已就绪且自测通过（对 `explorer.exe` 采样，句柄 4885 / GDI 238 / USER 378，斜率 0）。
- S5（30 分钟长时）尤其需要一段不被打断的独占时间。
- 采样器本身**未**验证「目标进程以管理员身份运行」这一路径（自测目标是普通权限的 `explorer.exe`）。
  实测时若报 `OpenProcess 失败`，用管理员身份重跑采样器即可。
- §4.2 的候选点 C1~C6 **均未**经实测确认是否真的增长；静态审计不足以判定。
