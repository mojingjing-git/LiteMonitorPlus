# A05 —— 装饰几何与资源缓存（实施结果）

> 计划：[2026-10-20-appearance-agent-execution.md](2026-10-20-appearance-agent-execution.md)（v4）§5 A05
> 模型合同（只读，未改动）：`src/Core/ThemeAppearanceOptions.cs`
> 实施人：A05 装饰线（独占写集 `src/UI/Decor/**`、`tests/AppearanceDecorProbe/**`、本文件）
> 完成时间：2026-10-20 17:50 (Asia/Shanghai)

---

## 1. 实际基线

| 项 | 值 |
|---|---|
| HEAD | `97f793dccac6d5ab37120602ecab4f6238a855da`（`merge: performance sampling hardening`） |
| 分支 | `master` |
| SDK | `G:\TOOL\dotnet-sdk\dotnet.exe`（8.0.426）/ 系统 `dotnet` 8.0.3xx |
| 主工程全量重建 | **0 错误 / 232 警告 / 1.88 s**（232 个警告全部来自既有文件，`src/UI/Decor/**` **0 警告**） |
| 主程序集 SHA256 | `CAC12FEABF3A4DD838CCB02BEF54A4B8DD51E32342E234F411A0ED98DDD87C86`（`bin/Release/net8.0-windows/LiteMonitor++.dll`，1,661,952 字节） |
| 既有单元测试 | `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release` → **544 通过 / 0 失败**（退出码 0） |

> ⚠ 程序集哈希是**共享工作区**的产物：测量时其他 agent（Web / 背景线）仍有在途改动，
> 该哈希不能作为"只含 A05 改动"的纯净锚点。A05 的可复核锚点是下面的**文件清单 + 探针证据**。
> A00 记录的 242 警告与本次 232 警告的差异来自其他 agent 的在途修改，不在本线范围。

---

## 2. 修改文件清单（全部为本线新建，无既有文件被改动）

### 生产代码 `src/UI/Decor/**`（命名空间 `LiteMonitor.src.UI.Decor`）

| 文件 | 类型 | 职责 |
|---|---|---|
| `src/UI/Decor/DecorGeometry.cs` | `DecorGeometry`（静态） | 路径构造 + 尺寸夹紧：切角 / 圆角 / 全息轮廓、内描边"带"路径、局部坐标版本 |
| `src/UI/Decor/DecorCacheKey.cs` | `DecorCacheKey`（readonly struct） | 缓存键：主题代次、颜色、槽位尺寸、方向、DPI、用户缩放、噪点种子、变体参数 |
| `src/UI/Decor/DecorResourceCache.cs` | `DecorResourceCache` | LRU + 总量核算的缓存（128 条目 / 16 MiB），资源**所有者** |
| `src/UI/Decor/DecorResourceHost.cs` | `DecorResourceHost` | 缓存宿主：显式失效入口 + 开箱即用的取缓存方法 |
| `src/UI/Decor/DecorTextureFactory.cs` | `DecorTextureFactory`（静态） | 渐变条槽 / 噪点 / 扫描线 / 网格 / 条纹理位图工厂（调用方拥有） |
| `src/UI/Decor/DecorPainter.cs` | `DecorPainter`（静态） | 绘制原语：向内窗口框、内侧高光线、条槽渐变裁剪、纹理填充、平铺 |

### 自证工程 `tests/AppearanceDecorProbe/**`

| 文件 | 说明 |
|---|---|
| `tests/AppearanceDecorProbe/AppearanceDecorProbe.csproj` | 独立 console；`ProjectReference` 指向主工程；输出到本项目 `output/`；**未新增任何 NuGet 依赖** |
| `tests/AppearanceDecorProbe/Program.cs` | 入口、参数解析、证据 JSON 输出、退出码 |
| `tests/AppearanceDecorProbe/DecorProbeRunner.cs` | 7 个用例的实现 |
| `tests/AppearanceDecorProbe/ProbeReport.cs` | 用例结果与 JSON 汇总 |
| `tests/AppearanceDecorProbe/README.md` | 用法与验证范围说明 |

### 结果文档

| 文件 | 说明 |
|---|---|
| `docs/superpowers/plans/2026-10-20-appearance-a05-decor.md` | 本文件 |

> **未触碰**（其他 agent / 主集成者写集）：`src/Core/**`、`src/UI/UIRenderer.cs`、`src/UI/HorizontalRenderer.cs`、
> `src/UI/TaskbarRenderer.cs`、`src/UI/MainForm_Transparent.cs`、`src/UI/Helpers/**`、`src/UI/Controls/**`、
> `src/UI/Decor` 之外的所有 UI 目录、`LiteMonitor.csproj`、`tests/LiteMonitor.Tests/**`、
> `tests/LiteMonitor.PerformanceProbe/**`、`tests/AppearanceFixtures/**`、`tests/AppearanceBackdropProbe/**`。
> **本线不接线到主面板**（v4 §5 A13：每项入口唯一，由主集成者统一接 UIRenderer / HorizontalRenderer / MainForm）。
> 未执行任何 `git add` / `commit` / `stash`，未修改 `.gitignore`。

---

## 3. 公共 API 与接线说明

### 3.1 冻结合同遵守情况

| 合同 | 落实方式 |
|---|---|
| ① 只消费 `ThemeAppearanceOptions`，用 `FrameWidthPx` / `FrameLengthPx`，**不再乘缩放** | 所有取缓存入口第一个参数都是 `ThemeAppearanceOptions`，只读取 `ThemeGeneration` / `DeviceDpi` / `UserScale` / `FrameWidthPx` / `FrameLengthPx` / `BorderColor`；本线**没有任何**缩放运算，也不引用 `UIUtils.ScaleFactor` |
| ② 消费 `BorderColor` / `ShadowColor` 前先判 `FrameEnabled` / `GlowEnabled` | `DecorPainter.FillInnerFrame` 内部再判一次 `frameStyle == FrameStyleNone` / `color.A <= 0`；调用方仍应先判 `options.FrameEnabled`（探针里就是这么接线的） |
| ③ 完整窗口 bounds 用 `options.ClientBounds` | 几何/绘制 API 的 `bounds` 参数即完整客户区矩形；本线**不使用** `VisibleClipBounds`，也不使用 `(-5,-5)` 背景填充矩形 |
| ④ `BackgroundFrame` / `PreviewSource` 是借用引用 | 本线**完全不消费**这两个字段，options 不释放、本线也不释放 |
| ⑤ 归一化只作用于运行时副本 | 本线只读 options，不写回 Theme / JSON |
| ⑥ 每个新尺寸只缩放一次 | 见 ①；`DecorGeometry` 的输入一律是**物理像素** |
| ⑦ 绘制入口用 `Save()` / `Restore()` 隔离状态 | `DecorPainter` 每个绘制方法都 `Graphics.Save()` / `Restore()`；不使用 `SmoothingMode` 冒充模糊（`SmoothingMode` 只用于抗锯齿） |

### 3.2 A05 对外能力一览

```csharp
namespace LiteMonitor.src.UI.Decor;

public static class DecorGeometry          // 几何：路径构造 + 夹紧（返回的路径由调用方 Dispose）
public readonly struct DecorCacheKey       // 缓存键（值语义）
public sealed class DecorResourceCache     // 缓存本体：资源所有者
public sealed class DecorResourceHost      // 缓存宿主：失效入口 + 取缓存便捷方法
public static class DecorTextureFactory    // 位图工厂（返回的位图由调用方 Dispose）
public static class DecorPainter           // 绘制原语
```

### 3.3 A07（机能装饰与进度条）怎么调用

典型接线（**A13 负责真正接到 UIRenderer**）：

```csharp
// ① 持有者创建（生命周期与窗口一致）
private readonly DecorResourceHost _decor = new DecorResourceHost();

// ② 每次 Paint 前同步上下文（主题代次 / DPI / 用户缩放变化时自动失效）
_decor.UpdateContext(options);

// ③ 窗口底纹（扫描线 / 网格）—— 必须早于文字，不能盖住数值
if (options.ScanlineEnabled)
{
    TextureBrush? scan = _decor.GetOrAddTiledBrush(
        options, DecorCacheKey.KindScanlineTile,
        Color.FromArgb(24, options.BorderColor), 8, 4);      // 瓦片宽高 = 图案参数
    DecorPainter.FillTiled(g, scan!, options.ClientBounds);
}

// ④ 进度条：渐变按"完整条槽"缓存，再按当前填充值裁剪
if (options.BarStyleEnabled)
{
    int slotW = barRect.Width, slotH = barRect.Height;      // 稳定槽位尺寸
    Bitmap? slot = _decor.GetOrAddGradientSlot(
        options, startColor, endColor, 0.0, slotW, slotH);
    DecorPainter.FillGradientSlot(g, slot!, barRect, progress01);
}

// ⑤ 向内的窗口边框（层次最末）
if (options.FrameEnabled)
{
    GraphicsPath band = _decor.GetOrAddBandPath(
        options, options.ClientBounds.Width, options.ClientBounds.Height,
        options.FrameStyle, options.FrameLengthPx, radiusPx, 1, options.FrameWidthPx);
    DecorPainter.FillInnerFrame(g, options.ClientBounds, options.FrameStyle,
        options.FrameWidthPx, options.FrameLengthPx, radiusPx, options.BorderColor, band);
}
```

要点：

* **渐变按整条槽缓存**：`GetOrAddGradientSlot` 建的是"满值槽位"的位图，
  `FillGradientSlot` 只裁剪"取哪一段"，**不会为每个百分比建缓存**（探针实测：3 万次进度变化，渐变缓存条目恒为 1）。
* **路径是局部坐标**：`GetOrAddBandPath` 的路径以"客户区缩进后的区域"左上角为原点，
  绘制时由 `FillInnerFrame` 平移到位；因此**窗口移动不会让缓存失效**。
* **扫描线 / 网格只能画在文字之前**（`DecorPainter` 文档已写明层次合同）。
* 借用规则：返回的 `Bitmap` / `TextureBrush` / `GraphicsPath` **不要 Dispose**（缓存拥有它们），
  也**不要改 `TextureBrush.Transform`**；要变换请用 `DecorPainter.CloneTextureBrush` 克隆独占件。

### 3.4 A09（窗口轮廓）怎么调用

```csharp
// Region 需要绝对坐标 → 用 BuildFramePath（不是局部坐标版本）
using GraphicsPath regionPath = DecorGeometry.BuildFramePath(
    options.ClientBounds, options.FrameStyle, options.FrameLengthPx, radiusPx, insetPx);
Region region = new Region(regionPath);   // Region / Path 的所有权与释放由 A09 定义
```

* `BuildFramePath` 产出的是**绝对坐标**路径（可直接喂给 `Region`）。
* 若要与 A07 的绘制几何完全一致，用**同样的** `options.FrameLengthPx` / `radiusPx` / `insetPx`；
  A05 已保证"绘制用带路径"与"轮廓用轮廓路径"出自同一套夹紧规则。
* `BuildChamferPath` / `BuildRoundedPath` 提供单独构造入口。

### 3.5 A13（主集成）怎么接线

| 场景 | 调用 |
|---|---|
| 窗口创建 | `new DecorResourceHost()`（默认 128 条目 / 16 MiB） |
| 每次 Paint | `_decor.UpdateContext(options)`（主题代次 / DPI / 用户缩放变化会自动失效） |
| 窗口尺寸变化 | `_decor.OnSizeChanged()`（全量失效，槽位尺寸不再复用） |
| 换肤 | `_decor.OnThemeChanged(options.ThemeGeneration)`（或由 `UpdateContext` 自动处理） |
| 窗口销毁 | `_decor.Dispose()`（释放全部位图 / 画刷 / 路径） |
| 需要精确控制 | 直接用 `DecorResourceCache`：`InvalidateTheme` / `InvalidateStale` / `InvalidateAll` / `Trim` |

**不变量**：`UpdateContext` / `OnSizeChanged` / `OnThemeChanged` / `Dispose` 是**仅有的**四个生命周期入口，
不需要"猜测"何时该清缓存；缓存键本身也覆盖主题代次、颜色、槽位尺寸、方向、DPI、用户缩放、种子，
所以即使漏调某个失效入口，也只会退化成"多占一点内存"，不会画错。

### 3.6 缓存键覆盖（v4 §3.3 逐条对照）

| 要求 | 键字段 |
|---|---|
| 主题代次 | `ThemeGeneration` |
| 颜色 | `PrimaryArgb` / `SecondaryArgb` |
| 稳定槽位尺寸 | `WidthPx` / `HeightPx` |
| 方向 | `Direction`（水平 / 垂直 / 径向） |
| DPI / 用户缩放 | `DeviceDpi` / `UserScaleKey`（千分比量化） |
| 噪点种子 | `Seed` |
| 类别相关附加参数 | `Variant`（渐变角度量化、路径样式 / 边长 / 半径 / 缩进 / 带宽打包、噪点强度） |

`Variant` 是补齐项：渐变**角度**与路径**带宽**如果不在键里，
会出现"换了角度还是旧渐变""换了带宽还是旧外框"这类错误，因此一并纳入。

### 3.7 容量与淘汰策略（可复核）

* 条目上限 `DecorResourceCache.DefaultMaxEntries = 128`。
* 位图/静态装饰位图总字节上限 `DecorResourceCache.DefaultMaxBitmapBytes = 16 MiB`。
* **淘汰规则 = LRU + 总量核算**：插入前先按"最久未使用"淘汰，直到条目数 < 上限且
  `总字节 + 新条目字节 ≤ 字节上限`。
* **已文档化的例外**：单个条目本身超过整个字节上限时，无法既缓存又守恒；
  此时允许它单独存在（总量暂时超限），由于它是最久未使用项，**下一次插入必然先被淘汰**。
* 字节核算：位图按 `宽 × 高 × 4` 精确计；`TextureBrush` 按其引用位图计；
  缓存负责连同画刷引用的位图一起释放。
* 每次 Paint 都**绘制有效缓存**，没有"跳过绘制"式节流；命中只影响资源查找，不影响是否绘制。

---

## 4. 测试命令与退出码

```powershell
# ① 主工程全量重建
dotnet build LiteMonitor.csproj -c Release -t:Rebuild
#   → 0 错误 / 232 警告（src/UI/Decor 0 警告），退出码 0

# ② 既有单元测试（回归）
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release
#   → 544 通过 / 0 失败，退出码 0

# ③ 自证工程构建
dotnet build tests/AppearanceDecorProbe/AppearanceDecorProbe.csproj -c Release
#   → 0 错误 / 0 警告，退出码 0

# ④ 自证程序运行
dotnet tests/AppearanceDecorProbe/output/AppearanceDecorProbe.dll --out tests/AppearanceDecorProbe/output/evidence
#   → 7/7 用例通过，退出码 0
```

**证据路径**：`tests/AppearanceDecorProbe/output/evidence/decor-evidence.json`

### 4.1 边界用例覆盖（探针实测）

| 用例 | 输入 | 结果 |
|---|---|---|
| 1×1 客户区 | 1×1，FrameWidth=8 / FrameLength=64 | 不抛异常；`FrameWidthPx=0`、`FrameLengthPx=0`（options 夹紧），不绘制 |
| 零宽 / 零高 / 零尺寸 | 0×120、240×0、0×0 | 不抛异常；`HasValidBounds=false`，跳过绘制 |
| 窄窗 160px | 160×40 | 正常绘制，路径不越界 |
| 极端半径 | 240×400，FrameWidth=8 / FrameLength=64 | 正常绘制，路径不越界 |
| 切角超过半宽 | 60×40 窗口用 FrameLength=64 | 几何层二次夹紧到 **19**（`min(宽,高)/2` 再收 1 像素留直线段），不越界 |
| 2×2 极小窗 | 2×2 | 切角夹到 0（不切角），描边宽夹到 1，正常绘制 |
| 全息样式 1×1 | 1×1，FrameStyle=3 | 不抛异常，不绘制 |
| **不得画到客户区外** | 240×120 客户区 + 24px 哨兵边距，三种样式 | 客户区外被改动像素 = **0 / 0 / 0** |

> 夹紧链：options 把 `FrameLengthPx` 夹到**高**的一半（60×40 窗口 → 20），
> 几何层再按 `min(宽,高)/2` 夹一次（→ 19）。**两层夹紧都保留**，几何层不会因为 options 的口径而越界。

---

## 5. 实测数字

### 5.1 10,000 次持续变化的进度值（硬要求）

探针跑了 **3 个 10,000 次区块**（共 30,000 次，进度值伪随机覆盖 0–1），
证明"进入有界平台"而不是"还在爬坡"：

| 区块 | 截至迭代 | 缓存条目 | 缓存字节 | 累计创建资源 | 区块内工作集增量 | 区块内托管内存增量 |
|---|---|---|---|---|---|---|
| 1 | 10,000 | **4** | **9,792** | **4** | +14,196,736 | +846,256 |
| 2 | 20,000 | **4** | **9,792** | **4** | +6,483,968 | **+1,152** |
| 3 | 30,000 | **4** | **9,792** | **4** | **+204,800** | **+1,088** |

* 缓存条目数：**全程恒为 4**（第 1 次、第 10 次、第 100 次、第 1000 次、第 10000/20000/30000 次完全相同），
  **不随进度变化增长**。
* 累计创建资源数：**4**（30,000 次绘制只建过 4 个资源），
  缓存命中 120,000 次 / 未命中 4 次，**淘汰 0 次**。
* 缓存位图字节：全程恒为 **9,792** 字节。
* 托管内存（强制 GC 后）：116,392 → 963,728，**净增 847,336 字节（0.81 MiB）**；
  逐区块增量 846,256 → 1,152 → 1,088，**已完全平台化**。
* 进程工作集：37,003,264 → 60,149,760，净增 23,146,496 字节；
  逐区块增量 **14.2 MB → 6.5 MB → 0.2 MB**，**收敛到平台**。
  （前两个区块的爬升是 .NET GC 提交新段 + GDI+ 初始化的一次性开销，
  与缓存无关：缓存自始至终只持有 4 个资源、9,792 字节位图。）

### 5.2 缓存上限

| 场景 | 结果 |
|---|---|
| 插入 300 个不同尺寸的条槽 | 条目 **128**（=上限），位图 1,717,760 字节，淘汰 172 次 |
| 插入 64 个 1024×1024 条槽（每个 4 MiB） | 条目 **4**，位图 **16,777,216 字节（正好 16 MiB 上限）**，淘汰 60 次 |

### 5.3 绘制耗时（离屏，240×120 客户区，含扫描线 + 网格 + 渐变条 + 向内边框）

| 指标 | 值 |
|---|---|
| 平均 | **0.1366 ms / 帧**（首个区块 0.0986 ms） |
| P95 | **0.2268 ms / 帧** |
| 最大 | 1.7771 ms / 帧（首次 JIT 预热） |

对照 v4 §7.4「静态机能效果：普通档额外 CPU ≤ 1 ms/帧」→ **在预算内**。
（离屏渲染不含 DWM / 窗口呈现，最终仍需 A13/A14 用实窗复测。）

### 5.4 关闭新能力时

| 指标 | 值 |
|---|---|
| 缓存条目 | **0** |
| 累计创建资源 | **0**（**未创建任何位图**） |
| 缓存位图字节 | **0** |
| 画布 | 完全透明，**0 个像素被绘制** |
| 计时器 | 本线**没有**任何计时器 |

### 5.5 渐变方向约定（取证）

| 角度 | 实测 | 约定 |
|---|---|---|
| 0° | 左 = `A128 R255 G0 B0`（偏红=起始色），右 = `A255 R4 G0 B251`（偏蓝=结束色） | **0° = 左 → 右** |
| 90° | 上 = `A128 R255 G0 B0`（偏红），下 = `A255 R32 G0 B223`（偏蓝） | **90° = 上 → 下** |

即：**角度按屏幕坐标顺时针为正**（0°=左→右，90°=上→下，180°=右→左，270°=下→上）。
该约定已写进 `DecorTextureFactory` 的 XML 注释，避免下游误解。

---

## 6. 实施过程中发现并修正的问题（值得下游知悉）

1. **不能用画笔描边做"向内的边框"**。GDI+ 的画笔沿路径**居中**，
   `PenAlignment.Inset` 对 `DrawPath` 不生效，描边必然有一半落到客户区外
   （实测客户区外被改动 1,830–3,432 像素）。
   → 改为**填充"内描边带"**（外轮廓 + 内轮廓两圈，`FillMode.Alternate` 让内轮廓成孔），
   带严格落在路径内。
2. **抗锯齿在整数边界仍有 1 像素溢出**（实测右/下边界各溢出 126–326 像素）。
   → 外轮廓默认**向内缩 1 像素**（`FillInnerFrame` 的 `outerInsetPx` 默认 1），
   溢出量落在客户区**之内**，实测客户区外 **0 像素**被改动。
3. **圆角路径原来缺 4 条直线边**（只有 4 段 `AddArc`），路径断开导致填充结果不可预期。
   → 补齐 4 条 `AddLine`。
4. **切角边长正好等于短边一半时会产生重合顶点**（路径自交，奇偶填充翻转）。
   → 夹紧规则改为 `min(宽,高)/2`，"正好等于一半"时再收 1 像素保证留有直线段
   （60×40 窗口 → 19）。
5. **缓存键漏了渐变角度与路径带宽** → 补 `Variant` 字段（long），
   否则"换角度还是旧渐变 / 换带宽还是旧外框"。
6. **局部坐标路径的平移曾经漏做**（只在命中缓存的分支里平移），
   导致未命中时整条外框画在画布原点 → 客户区外 1,866 像素被改动。已统一在两个分支平移。

---

## 7. 未验证项与已知限制

### 未验证（需 A13 / A14 在真实窗口补做）

1. **真实 DPI / 跨屏 / 用户缩放**下的像素表现：本线只做离屏渲染与**物理像素**几何，
   未在 120/144/192 DPI 的真实窗口里验证。
2. **实窗 CPU 对比**（§7.4 要求）：离屏 0.1366 ms/帧只是下界，
   不含 DWM / 窗口呈现 / 桌面合成；需 A13 接线后做实窗进程 CPU 对比。
3. **真实窗口轮廓（Region）**：A05 只提供几何，A09 的 Region 所有权与
   `DWMWCP_DONOTROUND` 等窗口属性策略不在本线。
4. **横向布局（横屏）复用同一套外框**：A05 的几何与横竖无关，
   但"横屏复用窗口底板/底纹/外框"的具体接线由 A13 完成。
5. **100 次换肤 / 100 次尺寸变化后的资源回落**：探针验证了失效入口本身
   （换肤/DPI/用户缩放/尺寸四类失效后条目归零、字节归零），
   但没有跑"连续 100 次换肤后新增资源回到基线波动范围"这一 §7.4 场景。
6. **扫描线/网格"不覆盖数值文字"**：本线只提供原语并写明层次合同，
   真正的层次正确性由 A07 的 DecorRenderer 与 A13 的接线保证。

### 已知限制

1. **离屏渲染不含 DWM / 窗口呈现**：见上。
2. **缓存条目上限与字节上限是 v4 §3.3 的初始值**，不是实测调优结果；
   若 A07/A09 实际需要的资源种类多于 128，需要主集成者统一决定是否上调。
3. **单条目超预算的例外**：单个位图大于 16 MiB 时无法既缓存又守恒，
   此时允许它单独存在并在下一次插入时被优先淘汰（已文档化）。
4. **`Variant` 的路径参数打包上限 1023**：切角边长 / 半径 / 缩进 / 带宽超过 1023 物理像素
   会被夹紧（实际业务取值远小于该值）。
5. **本线不接线到主面板**：所有装饰目前**不会**出现在真实界面上，
   接线由 A13 统一完成（A07 / A09 的消费方式见 §3.3 / §3.4）。
6. **工作集数字含 .NET GC 段提交的一次性开销**：探针已用三区块收敛证明它不是泄漏，
   但绝对值依赖机器与运行时版本，跨机比较需重新取数。
7. **探针本身没有单元测试工程**：它是独立 console 自证程序（按任务要求），
   未加入 `tests/LiteMonitor.Tests`（该目录属于其他 agent 写集）。

---

## 8. 交接清单（给主集成者 / A07 / A09）

* [x] 装饰几何（切角 / 圆角 / 全息 / 内描边带 / 局部与绝对坐标两套路径）
* [x] 纹理与渐变缓存（渐变按完整条槽缓存后裁剪；扫描线 / 网格 / 噪点 / 条纹理瓦片）
* [x] 路径缓存（局部坐标，窗口移动不失效）
* [x] 显式失效入口（主题代次 / DPI / 用户缩放 / 尺寸 / 全量）
* [x] 容量策略（128 条目 / 16 MiB，LRU + 总量核算）
* [x] 边界防御（1×1 / 零宽高 / 窄窗 / 极端半径 / 切角超半宽 / 1 像素抗锯齿溢出）
* [x] 自证工程与 JSON 证据
* [ ] **A13 接线到 UIRenderer / HorizontalRenderer / MainForm（本线不接）**
* [ ] **A07 消费装饰原语并实现 DecorRenderer / BarRenderer**
* [ ] **A09 消费几何实现窗口轮廓 / Region**
