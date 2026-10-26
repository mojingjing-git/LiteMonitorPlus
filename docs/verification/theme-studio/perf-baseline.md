# 主题工作室 资源与性能基线（TE08 前半 / Lane C / M1 参考值）

## 0. 基本信息

| 项 | 值 |
|---|---|
| 文档日期 | 2026-10-26 |
| 交付线 | Lane C（并行线，分支 `lane/te08-perf`，工作树 `G:/TOOL/LiteMonitor-fork/.worktrees/lane-c`） |
| 计划依据 | `docs/plans/2026-10-26-theme-studio-parallel-plan.md` §1「Lane C」/§3 门禁 G6 /§4 风险登记 |
| 基线提交 | **master@832d357**（计划指定的 M1 基线） |
| 实际测量提交 | **8dfe0d4**（`chore(LaneI前置): catalog partial、路径级原子写入入口、GPU 快照 internal 暴露`）= 本工作树起点 |
| 与基线提交的差异 | `git diff --stat 832d357..8dfe0d4` = 4 个文件：并行计划文档（新增）、`ThemeFileService.WriteThemeJsonToPath`（新增方法，**未被本基线场景调用**）、`ThemePropertyCatalog` 改 `partial`（1 行）、`ThemePreviewService` 快照方法改 `internal`（1 行）。三者都不落在「开关工作室／编辑事务／主题打开」路径上，**对本文数值的影响可忽略**。 |
| 数值性质 | **M1 参考值，不是验收值**。用途：为 TE08 后续批次提供对比基准；Lane I 集成完成后按门禁 **G6** 重跑本套测试并追加第二组数据。 |
| 采集脚本 | `tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs`（本次新增，类名 `ThemeStudioResourceTests`） |

### 0.1 基准机器

| 项 | 值 |
|---|---|
| CPU | AMD Ryzen 7 5800X 8-Core Processor（8 物理核 / 16 逻辑核） |
| 内存 | 68,637,044,736 B ≈ 63.9 GiB |
| 操作系统 | Microsoft Windows 11 专业版，10.0.26300（build 26300），64 位 |
| 显示适配器 | GameViewer Virtual Display Adapter（**虚拟显示**，无物理 GPU） |
| 屏幕 | DISPLAY1 2194×1234（主），DISPLAY2 1152×2048；虚拟屏合计 3346×2048 |
| 屏幕 DPI | **96**（工作室窗体 `DeviceDpi` 实测 = 96；`AutoScaleMode.None`，几何走 `Dip()` 换算） |
| .NET SDK | 8.0.426；目标框架 `net8.0-windows`，`UseWindowsForms=true` |
| 显卡路径 | 本基线场景全部使用**经典主题**（无 `rendering` 段）⇒ 预览走 GDI 同源路线，**不启动 GPU 会话**；GPU 预览在本基线中**未覆盖**。 |

---

## 1. 场景定义

三个场景全部在**同一个 testhost 进程**内执行（`dotnet test` 的 `LiteMonitor.Tests.dll` 宿主），主题目录由 `ThemeDocumentStore.SetThemeDirForTests` 指向**临时目录**并预置一个经典主题「经典蓝」（`version 3`，含 `layout.rowHeight`/`padding`、`font.family`/`value`、`color.background`/`textTitle` 与一个未知扩展键 `x-unknown`）；恢复草稿目录同样指向临时目录（`SetRecoveryDirForTests`），**不污染 `%LOCALAPPDATA%`**。

每个场景分**冷启动**与**稳态**两段测量：

| 场景 | 冷启动段 | 稳态段 | 说明 |
|---|---|---|---|
| **S1 开关工作室** | 3 轮 | 50 轮 | STA 线程 + `ThemeStudioForm`：`Show()` → `Application.DoEvents()` → `OpenThemeForTesting("经典蓝")`（走完整打开流程：会话 + 检查器 + 同源预览）→ `RefreshPreviewForTesting()` → `Close()` → `DoEvents()`。冷启动段用于把「一次性初始化」从「每轮增量」里剥离。 |
| **S2 编辑事务** | 20 次 | 1000 次 | `ThemeDocumentSession.ApplyEdit` 交替修改 `color.background`（两个替代色 `#3C3C3C`/`#4A4A4A` 来回切）与 `layout.rowHeight`（30~36）。**合并键每 10 次切换一次**（`laneC-group-N`）⇒ 产生 100 个可撤销事务（`UndoCount == 100` 已断言）。 |
| **S3 主题打开／切换** | 10 次 | 200 次 | `ThemeDocumentSession.Open("经典蓝")` 后立即丢弃（独立 `[MethodImpl(NoInlining)]` 方法，确保局部变量真正离开作用域）。 |

> **场景 3 的替代说明**：重建计划原场景是「**切换编辑目标** 200 次」。该路径（`c800d31 feat(TE03 GPU): 主效果/兼容外观属性视图与编辑目标切换`）**尚未进入本工作树**（本工作树 `src/ThemeEditor/` 内无编辑目标切换 API），按计划 §1 Lane C「若集成时尚无该路径则先测主题切换并注明替代」执行。**Lane I 集成后须按 G6 追加「切换编辑目标 200 次」的第二组数据。**

---

## 2. 采样方法

### 2.1 指标与来源

| 指标 | 来源 |
|---|---|
| GDI 对象数 | `GetGuiResources(hProcess, GR_GDIOBJECTS=0)` — **本测试文件自带一份 `private extern` P/Invoke**（`user32.dll`），写法参考 `tests/AppearanceResourceProbe/Program.cs:55`；**不引用探针工程**（该工程是 Exe 且不在 sln，`dotnet test --filter` 无法匹配）。 |
| USER 对象数 | `GetGuiResources(hProcess, GR_USEROBJECTS=1)` — 同一份 extern。 |
| 句柄数 | `Process.GetCurrentProcess().HandleCount`（进程句柄只打开一次并常驻，避免采样本身开关句柄造成噪声）。 |
| 私存 | `Process.GetCurrentProcess().PrivateMemorySize64`。 |
| 托管堆 | `GC.GetTotalMemory(false)`。 |

### 2.2 采样时点与口径

每个测量段采三次：

| 时点 | 说明 |
|---|---|
| `before` | 段开始前执行 `GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();`（下称 **Settle**）后采样 —— 归一化基线，让有终结器的对象（如 `System.Drawing.Font`）先释放。 |
| `afterRaw` | 场景结束**立即**采样（不做 GC）—— 暴露「尚未终结」的瞬时占用。 |
| `afterSettled` | 再做一次 **Settle** 后采样 —— **断言口径**，代表稳态真实占用。 |

场景内部另采一条**等间隔时间序列**（S1 每 10 轮、S2 每 100 次、S3 每 20 次），用最小二乘算「每次迭代增量」，用于判定是否线性累积。

### 2.3 断言策略

**不设绝对阈值失败线**。只断言三件事：

1. **场景完成**：迭代次数达标（`S1=50` / `S2=1000` / `S3=200`），`S2` 另断言 `UndoCount == 100`、`Revision - 1 == 1000`。
2. **差值为有限值**：10 个差值（raw/settled × 句柄/GDI/USER/私存/托管）全部 `double.IsFinite`。
3. **宽松上限**（GDI / USER / 句柄的 settled 增量）：

| 上限 | 值 | 最大实测 settled 增量 | 余量 | 依据 |
|---|---|---|---|---|
| `MaxGdiDelta` | **200** | 43（S1 冷启动） | ≥ 4.6× | M1 实测 + 余量 |
| `MaxUserDelta` | **200** | 7（S1 冷启动） | ≥ 28× | M1 实测 + 余量 |
| `MaxHandleDelta` | **400** | 110（S1 冷启动） | ≥ 3.6× | M1 实测 + 余量（工作室开关本身有一次性初始化成本） |

私存与托管堆**只记录不判定**（见 §5 结论：二者的变化由 CLR 堆提交行为解释，不是资源泄漏）。稳定性优先：宁可放宽，也不引入偶发失败。

---

## 3. 实测数字

采集命令（第二次运行 = 本文主表；第一次运行 = 稳定性复核，见 §3.2）：

```
cd G:/TOOL/LiteMonitor-fork/.worktrees/lane-c
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj --filter "FullyQualifiedName~ThemeStudioResourceTests" --logger "console;verbosity=detailed"
```

### 3.1 主表（第二次运行，2026-10-26）

| 场景 | 段 | 迭代 | 耗时 | 句柄 before→raw/settled（Δsettled） | GDI before→raw/settled（Δsettled） | USER before→raw/settled（Δsettled） | 私存 MB before→raw/settled（Δsettled） | 托管堆 MB before→raw/settled（Δsettled） |
|---|---|---|---|---|---|---|---|---|
| S1 开关工作室 | 冷启动 | 3 | 4.5 s | 380 → 491 / 490（**+110**） | 3 → 171 / 46（**+43**） | 5 → 11 / 12（**+7**） | 45.54 → 49.02 / 43.70（−1.84） | 10.72 → 13.85 / 10.92（+0.20） |
| S1 开关工作室 | 稳态 | 50 | 62.0 s | 490 → 489 / 483（**−7**） | 46 → 376 / 46（**0**） | 12 → 11 / 11（**−1**） | 43.70 → 67.61 / 61.85（**+18.15**） | 10.91 → 22.70 / 11.03（+0.12） |
| S2 编辑事务 | 冷启动 | 20 | 0.18 s | 353 → 376 / 376（**+23**） | 0 → 3 / 3（**+3**） | 2 → 5 / 5（**+3**） | 36.82 → 39.88 / 36.02（−0.81） | 9.89 → 11.07 / 10.45（+0.56） |
| S2 编辑事务 | 稳态 | 1000 | 1.04 s | 376 → 379 / 379（**+3**） | 3 → 3 / 3（**0**） | 5 → 5 / 5（**0**） | 33.29 → 43.66 / 45.79（**+12.50**） | 10.45 → 21.81 / 11.20（+0.74） |
| S3 主题打开 | 冷启动 | 10 | 0.13 s | 483 → 483 / 483（**0**） | 46 → 46 / 46（**0**） | 11 → 11 / 11（**0**） | 57.98 → 57.98 / 54.17（−3.81） | 11.00 → 11.63 / 11.01（+0.01） |
| S3 主题打开 | 稳态 | 200 | 0.27 s | 483 → 483 / 483（**0**） | 46 → 46 / 46（**0**） | 11 → 11 / 11（**0**） | 50.81 → 50.82 / 48.66（−2.16） | 11.00 → 13.09 / 11.00（+0.00） |

### 3.2 稳定性复核（第一次运行，同一命令）

| 场景 | 段 | 迭代 | 耗时 | Δsettled 句柄 | Δsettled GDI | Δsettled USER | Δsettled 私存 MB | Δsettled 托管 MB |
|---|---|---|---|---|---|---|---|---|
| S1 | 冷启动 | 3 | 5.9 s | +110 | +43 | +7 | +0.61 | +0.09 |
| S1 | 稳态 | 50 | 106.3 s | −2 | 0 | −1 | +16.57 | +0.12 |
| S2 | 冷启动 | 20 | 0.34 s | +23 | +3 | +3 | −0.89 | +0.57 |
| S2 | 稳态 | 1000 | 7.2 s | +3 | 0 | 0 | +9.76 | +0.76 |
| S3 | 冷启动 | 10 | 0.14 s | 0 | 0 | 0 | −3.45 | +0.01 |
| S3 | 稳态 | 200 | 0.38 s | 0 | 0 | 0 | −1.48 | +0.00 |

**两次运行的句柄 / GDI / USER 增量完全一致或相差 ≤ 5**（冷启动段逐字一致：+110 / +43 / +7 与 +23 / +3 / +3），说明这三项计数在本机上是**确定性**的。第三次运行复核（改动注释后重跑）再次得到 **+110 / +43 / +7**（S1 冷启动）与 **+23 / +3 / +3**（S2 冷启动）、稳态段 **句柄 −2 / GDI 0 / USER −1**（S1）与 **+1 / 0 / 0**（S2）、**全 0**（S3）——**三次一致**。差异项：私存（+18.15 / +16.57 / +15.42 MB；+12.50 / +9.76 / +12.45 MB）与 S1 稳态耗时（62.0 s / 106.3 s / 65.5 s）——前者由 GC 提交时机决定，后者由机器负载/GC 节奏决定，**不参与断言**。

### 3.3 时间序列（稳态段，用于判定线性累积）

第二次运行，格式 `轮次:句柄/GDI/USER/私存MB/托管MB`：

```
S1 稳态（每 10 轮一点）
  0 : h490 g46  u12 p43.70 m10.91
  10: h498 g458 u15 p48.99 m18.70
  20: h495 g868 u15 p60.21 m25.99
  30: h498 g417 u15 p61.18 m23.30
  40: h498 g827 u15 p61.93 m30.63
  50: h493 g376 u14 p67.68 m22.69
S2 稳态（每 100 次一点）
  0   : h376 g3 u5 p33.29 m10.45
  100 : h376 g3 u5 p33.41 m11.78
  200 : h376 g3 u5 p34.13 m12.82
  300 : h376 g3 u5 p34.30 m13.86
  400 : h379 g3 u5 p35.81 m15.58
  500 : h379 g3 u5 p37.01 m16.61
  600 : h379 g3 u5 p38.75 m17.67
  700 : h379 g3 u5 p40.16 m18.71
  800 : h379 g3 u5 p41.23 m19.74
  900 : h379 g3 u5 p42.56 m20.77
  1000: h379 g3 u5 p43.66 m21.81
S3 稳态（每 20 次一点）
  0 … 200: h483 g46 u11 p50.81→50.82 m11.00→13.09   （全程恒定）
```

---

## 4. 结论：是否线性累积

| 指标 | S1 开关工作室（50 轮） | S2 编辑事务（1000 次） | S3 主题打开（200 次） | 判定 |
|---|---|---|---|---|
| 句柄 | 冷启动 +110 后**平台化**（稳态 −7，序列 490→493 无趋势） | 冷启动 +23 后**平台化**（稳态 +3） | **全程 0** | **不累积**（只有一次性初始化台阶） |
| GDI 对象 | 冷启动 +43 后**平台化**（稳态 0） | 冷启动 +3 后**平台化**（稳态 0） | **全程 0** | **不累积** |
| USER 对象 | 冷启动 +7 后**平台化**（稳态 −1） | 冷启动 +3 后**平台化**（稳态 0） | **全程 0** | **不累积** |
| 托管堆（GC 后） | 稳态 +0.12 MB（噪声） | 稳态 +0.74 MB（噪声量级） | +0.00 MB | **不累积** |
| 私存（Private Bytes） | **线性增长**：序列 43.70 → 67.68 MB，≈ **+0.48 MB/轮** | **线性增长**：33.29 → 43.66 MB，≈ **+10.6 KB/次** | **持平**（+0.01 MB / 200 次） | **S1、S2 线性增长；S3 不增长** |

**总体判定**：句柄 / GDI / USER / 托管存活集**均无线性累积**，三个场景的资源生命周期是干净的。唯一单调增长的量是**进程私存**，且它与托管存活集脱钩（GC 之后托管堆回到基线、私存不回落），这是 **CLR 已提交堆页不归还 OS** 的典型形态，不是对象泄漏；但对「长时间反复开关工作室」的使用形态仍是需要跟踪的量。

---

## 5. 异常现象与观察

1. **句柄「随次数增长」只在冷启动出现，不是每轮泄漏。**
   第一次开发运行（未加预热段）曾观察到：50 轮结束时 settled 句柄 **+109**、GDI **+43**、USER **+6**，但时间序列显示增量集中在**头 10 轮**（句柄 376→491）后即进入平台（20/30/40/50 轮：490/495/495/490）。加预热段后冷启动段把这笔一次性成本单独测出（**+110 句柄 / +43 GDI / +7 USER**），稳态段随即回到 0 附近。**结论：一次性初始化成本，不是每轮泄漏。**
   这 +110 句柄具体由谁持有**未定位**（可能来自 WinForms 视觉样式／字体缓存／`AppearanceRuntime` 等静态缓存）。`ThemeStudioForm.cs` 属 Lane I 白名单文件，**Lane C 不做修改**；如需定位请由 Lane I 接手（可按 §7 的重跑命令在同一工作树复现）。

2. **S1 稳态的 raw GDI 计数在 376~868 之间剧烈摆动，settled 后回到 46（基线）。**
   原因是工作室每轮都会新建 `System.Drawing.Font`（例：`ThemeStudioForm.DrawLibraryItem` 内每次绘制都 `new Font("Segoe UI", 7.5F)`），这些 Font 的原生句柄要靠**终结器**释放，GC 未触发时就会堆在 GDI 计数里。**这不是泄漏**（settled 增量 = 0），但它意味着：在没有 GC 的高频操作窗口内，GDI 对象数会短时冲到数百。建议在 TE08 后续批次评估「绘制路径不新建 Font」的收益。

3. **私存线性增长的两条来源**（均为分配抖动，非存活对象）：
   - S1：≈ +0.48 MB/轮 —— 窗体创建/销毁的原生分配与堆碎片；
   - S2：≈ +10.6 KB/次编辑 —— `ApplyEdit` 每次提交事务会克隆一份配置快照（`AppearanceThemeConfig.CloneConfig`，反射深拷贝）+ 覆盖层节点，1000 次编辑产生约 2000 份克隆。托管存活集在 GC 后仅 +0.74 MB，说明克隆**是垃圾而非常驻**；私存不回落是 CLR 堆页保留。

4. **S3（主题打开 200 次）完全无增长**：句柄/GDI/USER 增量恒为 0，私存 ±2 MB 噪声。
   `ThemeDocumentSession.Open` 会为每个草稿调用 `Theme.BuildFonts()` 建 4 个 `System.Drawing.Font`，但 GDI+ 字体不计入 `GetGuiResources` 的 GDI 对象数，且 `Font` 由终结器回收 ⇒ 三项计数均不受影响。**注意**：该结论只对「经典主题、无 GPU」成立。

5. **GPU 路径未覆盖**：三个场景都用经典主题（无 `rendering` 段），`ThemePreviewControl.UpdateGpuPreview` 因 `_theme.Rendering is null` 直接短路，**不启动 GPU 会话**。GPU 预览的资源画像（`GpuThemePreviewControl` 会话/纹理/守卫）**不在本基线内**，TE08 后半批次需单独测。

6. **非本线引入的全量回归失败（记录，不修改）**：`dotnet test`（全量，2511 项）中 `I2HostSupervisionTests` 失败（`进程被杀_监督器确认退出后重启并恢复新会话`，`Assert.Equal()` Expected 1 / Actual 2）。**已验证为既有问题、与本线无关**：把 `ThemeStudioResourceTests.cs` 临时移出编译后单独重跑该类，仍失败（2 项失败，失败点在两次运行间漂移：另一次为 `FailFast与握手超时_三败进窗口_暂停自动重启`，Expected 0 / Actual 2）⇒ 属**既有的 flaky / 环境相关**失败（进程监督时序），按门禁 G5「全量回归失败在主线修复，不在并行线互相打补丁」处理。其余 2503 通过、7 跳过。

---

## 6. 断言与余量汇总

| 断言 | 实现 | 上限 | 最大实测 | 余量 |
|---|---|---|---|---|
| 场景完成 | `Assert.Equal(迭代数, completed)` + `S2: UndoCount==100 / Revision-1==1000` | — | 全部达标 | — |
| 差值有限 | 10 个差值 `double.IsFinite` | — | 全部有限 | — |
| GDI settled 增量 | `Assert.True(Δ <= MaxGdiDelta)` | 200 | 43 | ≥ 4.6× |
| USER settled 增量 | `Assert.True(Δ <= MaxUserDelta)` | 200 | 7 | ≥ 28× |
| 句柄 settled 增量 | `Assert.True(Δ <= MaxHandleDelta)` | 400 | 110 | ≥ 3.6× |
| 私存 / 托管堆 | **不判定**（只记录） | — | — | — |

上限取「M1 实测 + 余量」的一次性记录，**不是验收线**；任何一项被突破都应先复核噪声来源，再决定是否调整。

---

## 7. 重跑命令

```bash
cd G:/TOOL/LiteMonitor-fork/.worktrees/lane-c

# 构建
dotnet build LiteMonitor.csproj

# 验收命令（Lane C 门禁）
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj --filter "FullyQualifiedName~ThemeStudioResourceTests"

# 带完整数值输出（本文 §3 的原始输出来自此命令）
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj --filter "FullyQualifiedName~ThemeStudioResourceTests" --logger "console;verbosity=detailed"
```

数值行以 `[LaneC-PERF]` 前缀输出，便于 grep：

```bash
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj --filter "FullyQualifiedName~ThemeStudioResourceTests" --logger "console;verbosity=detailed" | grep "LaneC-PERF"
```

> 备注：首次构建较慢；并行线同时跑 `dotnet build/test` 会触发 `MSB3027 testhost 锁定`，遇到时等待 30 秒重试一次（门禁 G5／风险 6），**不改产品代码绕过**。

---

## 8. 后续（门禁 G6）

1. Lane I 集成「**切换编辑目标**」后（`c800d31` 已进 master，本工作树尚未包含），在集成后的代码上**重跑本套测试**并把第二组数据追加到本文 §3（表格下方新增「M2/集成后」列），对比句柄 / GDI / USER / 私存的增量差异。
2. TE08 后半批次补 GPU 预览路线（`rendering.backend=gpu`）的资源画像 —— 本基线未覆盖（§5.5）。
3. 若要把 §5.1 的 +110 句柄定位到具体持有者，需在 `ThemeStudioForm` / `PropertyInspector` / `ThemePreviewControl` 上做句柄归因；该文件属 Lane I 白名单，由 Lane I 收口（G4）。
