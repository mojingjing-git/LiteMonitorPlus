# A07 —— 机能装饰与进度条（实施结果）

> 计划：[2026-10-20-appearance-agent-execution.md](2026-10-20-appearance-agent-execution.md)（v4）§5 A07
> 模型合同（只读，未改动）：`src/Core/ThemeAppearanceOptions.cs`
> 上游依赖（只读复用）：`src/UI/Decor/DecorGeometry.cs`、`DecorCacheKey.cs`、`DecorResourceCache.cs`、`DecorResourceHost.cs`、`DecorTextureFactory.cs`、`DecorPainter.cs`（A05）
> 实施人：A07 装饰线（独占写集 `src/UI/Decor/DecorRenderer.cs`、`src/UI/Decor/BarRenderer.cs`、`tests/AppearanceDecorRendererProbe/**`、本文件）
> 完成时间：2026-10-20（Asia/Shanghai）

---

## 1. 实际基线

| 项 | 值 |
|---|---|
| HEAD | `97f793dccac6d5ab37120602ecab4f6238a855da`（`merge: performance sampling hardening`），本线**未提交**任何 git 操作 |
| 分支 | `master` |
| SDK | `dotnet 8.0.426`（`C:\Program Files\dotnet`） |
| 主工程构建 | `dotnet build LiteMonitor.csproj -c Release` → **0 错误**（退出码 0） |
| 主程序集 SHA256 | `4141458ADCED2AD491B6227AF1B18EF1A62038893B5FBBCEF873B29787C5794E`（`bin/Release/net8.0-windows/LiteMonitor++.dll`，170,700 字节） |
| 既有单元测试 | `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release` → **577 通过 / 0 失败**（退出码 0） |
| 自证工程构建 | `dotnet build tests/AppearanceDecorRendererProbe/AppearanceDecorRendererProbe.csproj -c Release` → **0 错误 / 0 警告**（退出码 0） |
| 自证程序运行 | **9 / 9 用例通过**（退出码 0） |

> ⚠ 程序集哈希是**共享工作区**的产物：同期其他 agent（编辑器线 / Web 线）仍有在途改动，
> 该哈希不能作为"只含 A07 改动"的纯净锚点。A07 的可复核锚点是下面的**文件清单 + 探针证据**。

### 1.1 期间发生的并行构建冲突（已解决，未越界修改他人文件）

本线开发期间，`src/ThemeEditor/AppearanceFields.cs`（**A10 编辑器线的写集**）一度处于"编译不过"的中间态
（`ToolTipText` / `AppearanceFieldsComboItemAccessor` / `KeySecondary` 未定义），导致 `dotnet build` 失败。

按任务书的构建纪律，本线**没有改动该文件，也没有改动任何 csproj**，而是用**仓库外**的 MSBuild 覆盖做本地验证：

```powershell
dotnet build <目标工程> -c Release /p:CustomAfterMicrosoftCommonTargets="C:\Users\wrz20\AppData\Local\Temp\a07-exclude-broken.targets"
```

该覆盖文件只把 `src/ThemeEditor/**` 排除出编译并补一个只含 `ThemeEditorForm` 类型的替身，**不修改仓库内任何文件**。
对方 agent 完成后（`AppearanceFields.cs` / `ThemeEditorForm.cs` 恢复可编译），本线已用**标准命令**重新完整构建与复跑，
上表所有数字均来自**官方构建**（退出码 0）。

---

## 2. 修改文件清单（全部为本线新建，未改动任何既有文件）

### 生产代码 `src/UI/Decor/**`（命名空间 `LiteMonitor.src.UI.Decor`）

| 文件 | 类型 | 职责 |
|---|---|---|
| `src/UI/Decor/DecorRenderer.cs` | `DecorRenderer` + `DecorGroupRegion` | 窗口底纹（扫描线 / 网格）、组底纹 + 组框、向内的窗口边框 + 辉光高光 |
| `src/UI/Decor/BarRenderer.cs` | `BarRenderer` | 旧条（完全转调既有 `UIUtils.DrawBar`）/ 渐变条 / 纹理条 |

### 自证工程 `tests/AppearanceDecorRendererProbe/**`

| 文件 | 说明 |
|---|---|
| `tests/AppearanceDecorRendererProbe/AppearanceDecorRendererProbe.csproj` | 独立 console；`ProjectReference` 指向主工程；输出到本项目 `output/`；**未新增任何 NuGet 依赖** |
| `tests/AppearanceDecorRendererProbe/Program.cs` | 入口、参数解析、证据 JSON、退出码 |
| `tests/AppearanceDecorRendererProbe/DecorRendererProbeRunner.cs` | 9 个用例的实现 |
| `tests/AppearanceDecorRendererProbe/ProbeFrameRenderer.cs` | **模拟 A13 接线**的帧渲染器（旧路径 / 重排层次的新路径 / 横屏路径） |
| `tests/AppearanceDecorRendererProbe/ProbeFixture.cs` | 主题加载、指标与布局夹具、像素工具（哈希 / 比对 / 越界统计） |
| `tests/AppearanceDecorRendererProbe/ProbeReport.cs` | 用例结果类型 |
| `tests/AppearanceDecorRendererProbe/README.md` | 用法与验证范围说明 |

### 结果文档

| 文件 | 说明 |
|---|---|
| `docs/superpowers/plans/2026-10-20-appearance-a07-decor-renderer.md` | 本文件 |

> **未触碰**（其他 agent / 主集成者写集）：`src/UI/Decor/` 下 A05 已有的 6 个文件、`src/Core/**`、`src/ThemeEditor/**`、
> `src/System/**`、`src/UI/UIRenderer.cs`、`src/UI/HorizontalRenderer.cs`、`src/UI/MainForm_Transparent.cs`、`src/UI/Helpers/**`、
> `src/UI/Controls/**`、`LiteMonitor.csproj`、`tests/LiteMonitor.Tests/**`、`tests/LiteMonitor.PerformanceProbe/**`、
> `tests/AppearanceDecorProbe/**`、`tests/AppearanceFixtures/**`、`resources/themes/**`。
> **本线不接线到主面板**（v4 §5 A13：每项入口唯一，由主集成者统一接 `UIRenderer` / `HorizontalRenderer` / `MainForm`）。
> 未执行任何 `git add` / `commit` / `stash`，未修改 `.gitignore`，未读写 `LiteMonitor-old-config-20261020.zip`。
> 源码行尾：**CRLF**（已逐文件核验）。

---

## 3. 公共 API 与接线说明

### 3.1 冻结合同遵守情况

| 合同 | 落实方式 |
|---|---|
| ① 只消费 `ThemeAppearanceOptions`，用 `FrameWidthPx` / `FrameLengthPx`（物理像素），**不再乘缩放** | 所有入口第一个参数都是 `ThemeAppearanceOptions`；本线**没有任何**缩放运算，也不引用 `UIUtils.ScaleFactor`。本线自己引入的唯一尺寸是"图案间距"，由 `options.EffectiveScale` **换算一次**（4→扫描线间距、16→网格间距），且不与 `FrameWidthPx/FrameLengthPx` 发生二次缩放 |
| ② 消费 `BorderColor` / `ShadowColor` 前先判 `FrameEnabled` / `GlowEnabled` | `DecorRenderer.ResolvePatternBase` 只在 `options.FrameEnabled` 时取 `BorderColor`；`ResolveGlowColor` 只在 `options.GlowEnabled` 且有外框时取 `ShadowColor`。10 个旧主题里 7 个带 `border`/`shadow` 键，实测**一个都没有**冒出描边/辉光 |
| ③ 完整窗口 bounds 用 `options.ClientBounds` | 全部绘制入口只用 `options.ClientBounds`；**不使用** `g.VisibleClipBounds`，也不使用旧的 `(-5,-5)` 背景填充矩形定位外框 |
| ④ `BackgroundFrame` / `PreviewSource` 是借用引用 | 本线**完全不消费**这两个字段，既不释放也不修改 |
| ⑤ 归一化只作用于运行时副本 | 本线只读 options，**绝不回写** Theme / JSON |
| ⑥ 绘制入口用 `Graphics.Save()` / `Restore()` 隔离状态 | `DecorRenderer` / `BarRenderer` 的每个绘制入口都用 `g.Save()` / `g.Restore()`；A05 的 `DecorPainter` 同样如此 |
| ⑦ 渐变角度约定 | 用 A05 的 `DecorTextureFactory`（0° = 左→右，90° = 上→下），**不混用** A08 的 `BackdropGradient` |

### 3.2 A07 对外能力一览

```csharp
namespace LiteMonitor.src.UI.Decor;

public readonly struct DecorGroupRegion      // 组块矩形 + 组圆角（物理像素）
{
    public Rectangle Bounds { get; }
    public int RadiusPx { get; }
    public DecorGroupRegion(Rectangle bounds, int radiusPx);
}

public sealed class DecorRenderer : IDisposable
{
    public DecorRenderer();                                  // 自建资源宿主
    public DecorRenderer(DecorResourceHost host);            // 借用宿主
    public DecorResourceHost Host { get; }

    // 层次 1：窗口底纹（必须在任何文字之前）
    public bool RenderWindowTexture(Graphics g, ThemeAppearanceOptions options,
                                    IReadOnlyList<DecorGroupRegion>? groups = null);

    // 层次 2：组底纹 + 组框（必须在组内文字之前）
    public bool RenderGroupDecor(Graphics g, ThemeAppearanceOptions options, DecorGroupRegion group);

    // 层次 4：向内的窗口边框 + 辉光高光（层次最末）
    public bool RenderWindowFrame(Graphics g, ThemeAppearanceOptions options, int cornerRadiusPx);

    // 失效入口（与 A05 的宿主一一对应）
    public int UpdateContext(ThemeAppearanceOptions options);
    public int OnSizeChanged();
    public int OnThemeChanged(int themeGeneration);
    public void Dispose();
}

public sealed class BarRenderer : IDisposable
{
    public BarRenderer();
    public BarRenderer(DecorResourceHost host);
    public DecorResourceHost Host { get; }

    // 主入口：指标条（style=0 完全走旧逻辑）
    public bool RenderBar(Graphics g, ThemeAppearanceOptions options, MetricItem item, Theme theme);

    // 低层入口：只画"填充部分"，条底由调用方自己画
    public bool RenderBarSlot(Graphics g, ThemeAppearanceOptions options,
                              Rectangle barRect, double percent, Color barColor);

    public int UpdateContext(ThemeAppearanceOptions options);
    public int OnSizeChanged();
    public int OnThemeChanged(int themeGeneration);
    public void Dispose();
}
```

### 3.3 A13 怎么接线（推荐顺序）

```csharp
// ① 持有者创建（生命周期与窗口一致；两个渲染器**共用同一个宿主**）
private readonly DecorResourceHost _decorHost = new DecorResourceHost();
private readonly DecorRenderer _decor = new DecorRenderer(_decorHost);
private readonly BarRenderer _bars = new BarRenderer(_decorHost);

// ② 每次 Paint 前同步上下文（主题代次 / DPI / 用户缩放变化会自动失效）
_decorHost.UpdateContext(options);

// ③ 组块区域（竖屏才需要；横屏是列模型，不调用）
var groupRegions = groups
    .Select(gr => new DecorGroupRegion(gr.Bounds, theme.Layout.GroupRadius))
    .ToList();

// ④ 层次 1：窗口底纹（扫描线 / 网格）—— 必须早于文字
_decor.RenderWindowTexture(g, options, groupRegions);

// ⑤ 层次 2：组块填充（**既有代码**）+ 组底纹 / 组框
foreach (var gr in groups)
{
    UIUtils.FillRoundRect(g, gr.Bounds, theme.Layout.GroupRadius,
                          ThemeManager.ParseColor(theme.Color.GroupBackground));   // 既有
    _decor.RenderGroupDecor(g, options, new DecorGroupRegion(gr.Bounds, theme.Layout.GroupRadius));
}

// ⑥ 层次 3：标题 / 指标（**既有代码**）
//    层次 4：条 —— 旧条逐像素不变，新条走新样式
_bars.RenderBar(g, options, item, theme);       // style=0 时内部转调 UIUtils.DrawBar

// ⑦ 层次 5：向内的窗口边框 / 辉光高光（层次最末）
_decor.RenderWindowFrame(g, options, theme.Layout.CornerRadius);
```

要点：

* **层次顺序不可调换**：`窗口底纹 → 组块填充 → 组底纹/组框 → 标题/指标/条 → 向内的窗口边框/高光`。
  探针用像素采样证明：扫描线/网格**不会改变文字实心笔画像素**。
* **`groups` 一定要传**：传入后窗口底纹会自动跳过组块覆盖的区域（省掉一次整屏 alpha 混合）；
  不传则铺满整个客户区（画面相同，成本更高）。
* **`cornerRadiusPx` 传主题的 `layout.cornerRadius`**：它是**既有布局字段**，已由 `LayoutConfig.Scale` 缩放过，
  A07 **不再乘任何缩放**。
* **借用规则**：`Host.Cache` 里的位图 / 画刷 / 路径由缓存拥有，**不要 Dispose、不要改 `TextureBrush.Transform`**。
* **横屏**：`RenderWindowTexture` / `RenderWindowFrame` 照用（复用底板 / 底纹 / 外框），
  **不调用** `RenderGroupDecor`（横屏现有模型是列，M1 不改造横屏分组布局），
  **不调用** `RenderBar`（横屏本来就没有进度条）。
* **生命周期**：`UpdateContext` / `OnSizeChanged` / `OnThemeChanged` / `Dispose` 是仅有的四个入口；
  缓存键本身覆盖主题代次、颜色、槽位尺寸、方向、DPI、用户缩放、种子与变体参数，漏调只会多占内存，不会画错。

### 3.4 条样式语义

| `BarStyle` | 行为 |
|---|---|
| `0` 旧条 | **完全转调既有 `UIUtils.DrawBar`**：取整规则、颜色来源、圆角胶囊形状全部不变，探针实测与改造前**逐像素一致**（哈希相同、差异像素 0） |
| `1` 渐变条 | 渐变按**完整条槽**缓存（A05 的 `GetOrAddGradientSlot`），绘制时按填充比例裁剪（`FillGradientSlot`），**不会为每个百分比建缓存**；填充宽度与旧条**同一套取整规则**（截断 + 最小 1 像素） |
| `2` 纹理条 | 45° 斜纹瓦片（A05 的 `GetOrAddTiledBrush` + `FillTextureSlot`）+ 同色半透明底 |

* **阈值取色不变**：条色仍来自既有 `UIUtils.GetStateColor(state, theme, false)`（含旧 `BarHigh` / `BarMid`），
  新样式只在这**同一个颜色内部**做明度渐变（+30% 提亮）或叠斜纹，**不引入第二种阈值颜色，也不改安全值颜色**。
* **不凭空加条**：`item.BarRect` 为空即返回，不画任何东西。

---

## 4. 测试命令与退出码

```powershell
# ① 主工程构建
dotnet build LiteMonitor.csproj -c Release
#   → 0 错误，退出码 0

# ② 既有单元测试（回归）
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release
#   → 577 通过 / 0 失败，退出码 0

# ③ 自证工程构建
dotnet build tests/AppearanceDecorRendererProbe/AppearanceDecorRendererProbe.csproj -c Release
#   → 0 错误 / 0 警告，退出码 0

# ④ 自证程序运行
dotnet tests/AppearanceDecorRendererProbe/output/AppearanceDecorRendererProbe.dll --out tests/AppearanceDecorRendererProbe/output/evidence
#   → 9/9 用例通过，退出码 0
```

**证据路径**：`tests/AppearanceDecorRendererProbe/output/evidence/decor-renderer-evidence.json`

---

## 5. 实测数字

### 5.1 九个用例的结果

| # | 用例 | 结果 | 关键数字 |
|---|---|---|---|
| 1 | `closed_state_zero_side_effects` | PASS | 缓存条目 **0**、创建资源 **0**、位图字节 **0**、画布可见像素 **0**、计时器 **0** |
| 2 | `idempotent_100_paints` | PASS | 100 次绘制哈希**完全一致**（铺底板 / 不清理两种变体哈希相同：`0be35669bc22b867…`） |
| 3 | `layer_order_text_not_covered` | PASS | 文字**实心笔画**像素 2,984 个，被底纹改变 **0** 个；底纹可见像素 15,177 个 |
| 4 | `bounds_edge_cases` | PASS | 8 个边界场景：客户区外被改动像素 **全部 = 0**，无异常 |
| 5 | `legacy_themes_pixel_identical` | PASS | 10 个旧主题：新旧路径哈希**逐一相同**，差异像素 **0** |
| 6 | `horizontal_layout_reuse` | PASS | 横屏条数 **0**；新路径与「旧横屏 + 底纹 + 外框」参考路径哈希**相同** |
| 7 | `bar_styles_clipping` | PASS | 旧条哈希相同、差异 **0**；新条 0%/50%/100% 填充边界与预期**逐一吻合** |
| 8 | `progress_cache_platform` | PASS | 10,000 次变化进度：缓存条目恒 **3**、累计创建资源 **3**、淘汰 **0**、命中 9,997 |
| 9 | `frame_cost_budget` | PASS（含 WARN，见 5.4） | 见下 |

### 5.2 关闭态零副作用（用例 1）

| 指标 | 值 |
|---|---|
| 缓存条目 | **0** |
| 累计创建资源 | **0**（未创建任何位图） |
| 缓存位图字节 | **0** |
| 画布被绘像素 | **0**（完全透明） |
| 计时器 | **0**（本线没有任何计时器） |

### 5.3 幂等（用例 2）

* 变体 A（每帧重新铺底板，生产语义）：100 次哈希**全部相同**。
* 变体 B（**不清理画布**，直接叠加 100 次，更强）：100 次哈希**全部相同**
  → 证明**不累积变色、不淡出、不残留**。
* 两个变体的最终哈希相同：`0be35669bc22b867b12974d8d217ecbca7d3c0e3301a48663d184a138ce4fea3`。

### 5.4 每帧成本（用例 9，对照 v4 §7.4）

测量口径：**同一帧、只差"开不开装饰层"，逐帧配对**（配对抵消机器负载漂移），不画文字
（文字成本与装饰无关，且 GDI 文本几十毫秒会淹没增量）；3 轮取中位数。

| 档位 | 客户区 | 组 × 指标 | 装饰层中位数 | 最差一轮 | §7.4 预算 | 结论 |
|---|---|---|---|---|---|---|
| 普通档（扫描线+网格+外框+辉光全开） | 240×788 | 6 × 2 | **0.69 ms/帧** | 0.71 ms | ≤1 ms | **在预算内** |
| 4 卡 + 两倍物理尺寸（**关辉光**） | 480×1576 | 6 × 5 | **1.97 / 2.10–2.12 ms/帧**（三次运行） | 2.35 ms | ≤2 ms | **压线**：一次在内、两次超 0.10–0.12 ms（见下） |
| 4 卡 + 两倍物理尺寸（全开） | 480×1576 | 6 × 5 | **2.32 ms/帧**（复跑 2.40–2.49 ms） | 2.77 ms | ≤2 ms | 超 0.32–0.49 ms（见下） |

层次分解（4 卡 + 两倍物理尺寸档，全开）：

| 层 | 耗时 |
|---|---|
| 窗口底纹（边距条带） | 0.65 ms |
| 组底纹 + 组框（6 组） | 1.60 ms |
| 窗口边框 + 辉光 | 0.60 ms |

> 同机同期有其他 agent 在并行 `dotnet build`，实测值随负载在 **2.1–2.9 ms** 之间波动；
> 探针跑 3 轮取中位数，证据里同时记录了最差一轮（上表的"最差一轮"列）。
>
> **复跑一致性（2026-10-20 交付前复跑，主工程与探针均重新构建后执行）**：
> 共复跑 2 次，9/9 仍全部 PASS、退出码 0，但 480×1576 两档的中位数比首跑偏高
> （关辉光 1.97 → **2.10–2.12 ms**，全开 2.32 → **2.40–2.49 ms**）。
> 说明**关辉光档正好压在 2 ms 预算线上**（三次运行分别落在线的两侧），
> 不能写成"稳在预算内"；下表与"已知限制"按此口径表述。普通档复跑仍远在 1 ms 预算内（无 `[WARN]`）。

**结论与原因（据实上报，不自行放宽预算）**：

* 480×1576 下"**整屏一遍 alpha 混合的底纹**"实测就是 **~1.0–1.2 ms**
  （离屏微基准：单遍平铺填充 1.04 ms，两遍 2.03 ms）——这是**任何全屏底纹方案的地板**，
  与实现无关。叠加组框 / 外框 / 辉光三条 AA 路径填充（各 ~0.2–0.6 ms）后落在 **2.1–2.9 ms**。
* 已做的优化（实测有效）：
  1. **扫描线 + 网格合成一张瓦片**（`DecorRenderer.GetPatternBrush`）：两种图案同时开时只铺一遍，
     省掉一整遍全屏 alpha 混合（单是这一项在 4 卡档就值 ~1.0 ms）；
  2. **窗口底纹按"客户区减组块"切条带**（纯托管运算，零 GDI Region 句柄），不再重复铺组块区域；
  3. **组底纹用 `FillPath` 直接被组块圆角路径裁剪**，不再每组每帧新建 Region；
  4. **渐变条按完整条槽缓存**，不为每个百分比建缓存。
* **处置建议**：A12 的机能主题**关闭辉光**可把该档从 2.3–2.5 ms 压到 **2.0 ms 预算线附近**
  （实测 1.97–2.12 ms，仍有约 ±0.15 ms 的并行负载抖动）；若坚持开启辉光，则该档超预算 ~0.3–0.5 ms
  （按 12.5 Hz 折算，占 80 ms 帧预算的 ~0.4–0.6%）。若 A12/A13 要求该档**硬性**留在 2 ms 内，
  需要主集成者决定是否接受"底纹改为按需刷新（低频重铺）"这类超出 A07 写集的方案。
* 探针对普通档做**硬判定**（≤1 ms，不达标即失败）；4 卡 + 两倍物理尺寸档**只报数**：
  JSON 里写明 `withinBudget`、`overageMs` 与 `budgetNote`，控制台同时打印 `[WARN]`。

### 5.5 10,000 次持续变化的进度值（用例 8）

| 迭代 | 缓存条目 | 缓存字节 | 累计创建资源 | 命中 | 未命中 | 淘汰 |
|---|---|---|---|---|---|---|
| 2,000 | **3** | 17,280 | **3** | 1,997 | 3 | **0** |
| 4,000 | **3** | 17,280 | **3** | 3,997 | 3 | **0** |
| 6,000 | **3** | 17,280 | **3** | 5,997 | 3 | **0** |
| 8,000 | **3** | 17,280 | **3** | 7,997 | 3 | **0** |
| 10,000 | **3** | 17,280 | **3** | 9,997 | 3 | **0** |

* 缓存条目**全程恒为 3**（1 个条槽渐变位图 + 1 个斜纹瓦片画刷 + 1 条带路径），**不随进度变化增长**；
  淘汰 **0** 次。这与 A05 的结论一致（30,000 次变化后条目恒为 4、淘汰 0）。

### 5.6 边界用例（用例 4）

| 场景 | 客户区 | 客户区外被改动像素 | 异常 |
|---|---|---|---|
| 1×1 | 1×1 | **0** | 无 |
| 零宽 | 0×120 | **0** | 无 |
| 零高 | 240×0 | **0** | 无 |
| 零宽零高 | 0×0 | **0** | 无 |
| 窄窗 160px | 160×40 | **0** | 无 |
| 极端描边 | 240×400（FrameWidth=8 / FrameLength=64） | **0** | 无 |
| 切角超半宽 | 60×40（FrameLength=64） | **0** | 无 |
| 2×2 | 2×2 | **0** | 无 |

> 客户区故意带 24px 偏移（不是画布原点），用来验证"一律用 `options.ClientBounds`"。

---

## 6. 实施过程中发现并修正的问题（值得下游知悉）

1. **抗锯齿的路径填充会溢出 1 像素**。组底纹用 `FillPath` 走组块圆角路径时，AA 边缘会在路径外侧渗出约 1 像素；
   1×1 客户区时正好落到**客户区外 1 个像素**。
   → 绘制前把绘制范围夹到**组块矩形**（组块矩形必然在客户区之内），"客户区外 0 像素被改动"始终成立。
   （A05 的窗口外框用的是"外轮廓内缩 1 像素"，思路一致。）
2. **两遍全屏 alpha 混合直接超预算**。扫描线与网格分开铺 = 整屏铺两遍（4 卡档 2.03 ms，仅底纹就超 2 ms）。
   → 合成一张瓦片（`DecorRenderer.BuildCombinedPatternTile`，两种间距取最小公倍数保证平铺无缝），只铺一遍。
3. **`Region.Exclude` / `new Region(path)` 很贵**（要栅格化成掩码，还占 GDI 句柄）。
   → 窗口底纹改成"按 Y 切条带、条带内减去被覆盖的 X 区间"的纯整数运算，零原生句柄。
4. **逐组 `SetClip(path)` 会为每个组块每帧新建一个 Region**。
   → 组底纹改用 `FillPath` 让纹理被路径直接裁剪（并且 AA 边缘与组块填充一致）。
5. **绝对耗时里的大头是既有 GDI 文本绘制**（`TextRenderer`，4 卡档整帧 ~50–65 ms），与 A07 无关；
   测"装饰层成本"必须用**配对 + 不画文字**的口径，否则增量会被文本抖动淹没。
6. **同机其他 agent 的在途文件会让主工程编译不过**：见 §1.1 的处置方式（仓库外 MSBuild 覆盖，不改他人文件）。

---

## 7. 未验证项与已知限制

### 未验证（需 A13 / A14 在真实窗口补做）

1. **真实 DPI / 跨屏 / 用户缩放**下的像素表现：本线只做离屏渲染与**物理像素**几何，
   未在 120/144/192 DPI 的真实窗口里验证（图案间距会按 `options.EffectiveScale` 换算，未在真机核对）。
2. **实窗 CPU 对比**（§7.4 要求）：离屏数字只是下界，不含 DWM / 窗口呈现 / 桌面合成；需 A13 接线后做实窗进程 CPU 对比。
3. **真实窗口轮廓（Region）**：A07 只提供绘制几何，A09 的 Region 所有权与 `DWMWCP_DONOTROUND` 不在本线。
4. **局部失效 / 连续 100 次重绘的实窗表现**：A04 已指出既有渲染器在局部失效时不重画底色，
   新增半透明装饰会放大这个问题（**A13 必须保证失效区底色被重画**）。
5. **用户自定义透明度的窗口**（`Opacity`）：装饰层是半透明叠加，用户把窗口透明度调低时对比度未验证。
6. **可读性（对比度）**：§7.2 要求普通文字对比度 ≥4.5:1、扫描线/辉光不能遮盖告警文字；
   本线只证明了"底纹不改变文字实心笔画像素"，**没有**做真实背景下的对比度测量。

### 已知限制

1. **4 卡 + 两倍物理尺寸档的装饰层成本超出 §7.4 初始预算**（全开 2.3–2.5 ms vs 2 ms）：
   据实上报，见 §5.4 的层次分解与处置建议；**没有**自行放宽预算写成"通过"。
   同档**关闭辉光**时为 1.97–2.12 ms，**正好压在这条 2 ms 线上**（三次运行里一次在内、两次略超），
   属于"基本达标但无余量"，不能算"稳在预算内"。普通档（240×788）0.69 ms/帧稳定在 1 ms 预算内。
2. **抗锯齿边缘像素会被底纹改变**：GDI 的字形边缘与底色混合，底纹会改变混合结果。
   这是"文字画在底纹之上"的固有行为（任何背景都会如此），**不是**"底纹覆盖文字"；
   文字的**实心笔画像素逐像素一致**（探针已证）。
3. **窗口底纹按组块"包围矩形"排除**：组块圆角之外的四个小角不铺窗口底纹（露出底板），
   与"整屏铺底纹"在圆角处有极小的像素差异（每组 4 个角，肉眼不可见）。
4. **半透明组块背景时窗口底纹铺满整个客户区**（不排除组块）：保证"底纹透过组块透出来"的原义，
   代价是该场景下成本回到"整屏一遍"。
5. **离屏渲染不含 DWM / 窗口呈现**：所有数字都是离屏下界。
6. **4 卡档的数字受同机负载影响明显**（同期有别的 agent 在并行构建）：探针跑 3 轮取中位数，
   证据里同时记录了最差一轮。
7. **探针本身没有单元测试工程**：它是独立 console 自证程序（按任务要求），
   未加入 `tests/LiteMonitor.Tests`（该目录属于其他 agent 写集）。
8. **本线不接线到主面板**：所有装饰目前**不会**出现在真实界面上，接线由 A13 统一完成（见 §3.3）。

---

## 8. 交接清单（给主集成者 / A12 / A13）

* [x] 窗口底纹（扫描线 / 网格，含"两图案合成一张瓦片"与"按客户区减组块切条带"两项优化）
* [x] 组底纹 + 组框（组框向内，不溢出；底纹不溢出客户区）
* [x] 向内的窗口边框 + 辉光高光（复用 A05 的内描边带，客户端外 0 像素被改动）
* [x] 进度条：旧条逐像素不变 / 渐变条（整槽缓存后裁剪）/ 纹理条（斜纹平铺）
* [x] 横屏：复用底板 / 底纹 / 外框，不新增组框分组，无条指标不凭空出现条
* [x] 自证工程 9/9 通过（关闭态零副作用、100 次幂等、层次正确、边界、旧主题、旧条、进度缓存、每帧成本）
* [ ] **A13**：按 §3.3 接线（层次顺序不可调换；`groups` 一定要传；横屏不调 `RenderGroupDecor` / `RenderBar`）
* [ ] **A13**：保证局部失效时**重画底色**（A04 已指出既有缺陷会被新装饰放大）
* [ ] **A12 / A13**：按 §5.4 的建议决定 4 卡档的辉光 / 组框取舍（或接受 ~0.5–0.8 ms 超出）
* [ ] **A13 / A14**：真实窗口 + 真实 DPI + 实窗 CPU + 对比度补测
