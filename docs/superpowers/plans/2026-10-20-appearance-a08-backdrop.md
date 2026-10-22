# A08 —— 静态背景与降级（BackdropRenderer）实施结果

> 计划：[2026-10-20-appearance-agent-execution.md](2026-10-20-appearance-agent-execution.md)（v4）§3.1 / §3.3 / §4 / §5(A08) / §7
> 基线：[2026-10-20-appearance-a00-baseline.md](2026-10-20-appearance-a00-baseline.md)
> 实施者：A08 工作线（背景静态层 + 降级决策）
> 状态：**已完成**（静态背景层 + 降级路径；实时捕获后端仍属 A06/A15）

---

## 1. 实际基线

| 项 | 值 |
|---|---|
| 仓库 | `G:\TOOL\LiteMonitor-fork` |
| 分支 | `master` |
| HEAD | `97f793dccac6d5ab37120602ecab4f6238a855da`（与 A00 冻结基线一致，实施期间未变） |
| 主程序集 SHA256（Release，探针输出目录内副本） | `8F6463FA46F6A8AED01E3A747AD13983CD1F47C227E1C1EBF6683A9F5CA76A24` |
| SDK | 8.0.426（`C:\Program Files\dotnet` 与 `G:\TOOL\dotnet-sdk` 各一份） |
| 主工程构建 | `dotnet build LiteMonitor.csproj -c Release` → **0 错误 / 232 警告**（A00 基线为 242 警告，差值来自其它 agent 在途改动） |
| 探针工程构建 | `dotnet build tests/AppearanceStaticBackdropProbe/AppearanceStaticBackdropProbe.csproj -c Release` → **0 错误 / 0 警告** |
| 证据生成时间 | 2026-10-20 17:14:46 +08:00 |

**未改动**：`src/Core/ThemeManager.cs`、`src/Core/UIUtils.cs`、`src/Core/ThemeAppearanceOptions.cs`、
`src/ThemeEditor/ThemeFileService.cs`、`src/UI/**`、`src/System/WebServer/**`、`src/System/HardwareServices/**`、
`LiteMonitor.csproj`、`tests/LiteMonitor.Tests/**`、`tests/LiteMonitor.PerformanceProbe/**`、
`tests/AppearanceFixtures/**`、`tests/AppearanceBackdropProbe/**`、`docs/APPEARANCE_PLAN.md`。
未执行 `git add` / `git commit` / `git stash`，未改 `.gitignore`，未读写 `LiteMonitor-old-config-20261020.zip`。

---

## 2. 修改文件清单（全部为新增）

| 文件 | 行数 | 作用 |
|---|---|---|
| `src/System/Backdrop/BackdropStatus.cs` | 175 | 状态模型：`BackdropRenderMode` / `BackdropDegradeReason` / `BackdropFrameSource` / `BackdropLayer` / `BackdropStatus` |
| `src/System/Backdrop/IBackdropFrameSource.cs` | 38 | 实时背景帧来源接口（供 A15 实现） |
| `src/System/Backdrop/BackdropModeResolver.cs` | 222 | 模式解析与降级决策（纯策略，不触碰 Graphics） |
| `src/System/Backdrop/BackdropGradient.cs` | 71 | 任意角度静态渐变（轴计算 + 画刷） |
| `src/System/Backdrop/BackdropNoiseCache.cs` | 201 | 固定种子噪点纹理 + 有界 LRU 缓存 |
| `src/System/Backdrop/BackdropEdgeGlow.cs` | 93 | 边缘辉光（四条渐变带，无位图） |
| `src/System/Backdrop/BackdropRenderer.cs` | 227 | 背景层渲染器（层次编排 + 状态 + 失效入口） |
| `tests/AppearanceStaticBackdropProbe/AppearanceStaticBackdropProbe.csproj` | 47 | 独立自证工程（未加入 sln，sln 归主集成者） |
| `tests/AppearanceStaticBackdropProbe/ProbeSupport.cs` | 158 | 自证设施（画布 / 像素哈希 / 选项构造 / 假后端） |
| `tests/AppearanceStaticBackdropProbe/Program.cs` | 802 | 18 个用例 + JSON 证据输出 |

命名空间：`LiteMonitor.src.SystemServices.Backdrop`（沿用 `src/System/` 的既有约定）。
新增 NuGet 依赖：**0 个**（主工程与探针工程均未新增 PackageReference）。

---

## 3. 公共 API 与接线说明

### 3.1 主集成者（A13）怎么调用

`src/System/Backdrop/BackdropRenderer.cs` → `BackdropRenderer`：

```csharp
// 1) 随窗口/主题创建一次（不要每帧 new）
using var backdrop = new BackdropRenderer(/* 可传 IBackdropFrameSource，A15 提供 */);

// 2) 在绘制层次的最底层调用（见下方“层次”说明）
BackdropStatus status = backdrop.Render(g, options);

// 3) 主题代次 / 尺寸 / DPI 变化时失效
backdrop.Invalidate();

// 4) 窗口销毁时释放
backdrop.Dispose();
```

**层次（v4 §3.3）**：`底板/背景 → 窗口底纹 → 组块填充 → 组底纹/组框 → 标题/指标/条 → 向内边框/高光`。
本组件只负责**最底层的背景**（底板 → 渐变/帧 → tint → 噪点 → 边缘辉光），**不画任何文字**。
建议接线点：`src/UI/UIRenderer.cs` → `UIRenderer.Render` 现有实心背景填充之后、组块与标题之前；
横屏走 `src/UI/HorizontalRenderer.cs` → `HorizontalRenderer.Render` 同理。两者签名由主集成者用重载接入，本线未改。

**坐标**：一律用 `options.ClientBounds`（完整客户区）。本组件**不使用** `Graphics.VisibleClipBounds`，
也**不使用**旧的 `(-5,-5)` 背景填充矩形定位任何新几何。

**状态隔离**：`Render` 内部用 `Graphics.Save()` / `Graphics.Restore()` 包裹全部绘制，调用方无需额外保存状态。

**幂等保证**：`Render` 每次都先铺一张**不透明底板**，因此同一 options 连续绘制 N 次像素完全一致，
不会在半透明 tint 上累积变色（已用像素哈希证明，见 §5）。

### 3.2 A15 怎么提供实时后端

实现 `src/System/Backdrop/IBackdropFrameSource.cs` → `IBackdropFrameSource`：

| 成员 | 语义 |
|---|---|
| `IsReady` | 后端是否已就绪可提供帧 |
| `UnavailableReason` | 未就绪原因（人类可读）。**必须给**，会进入降级诊断文本 |
| `TryGetFrame(double blurPx, out Image? frame)` | 取当前帧；`blurPx` 就是 `options.BlurPx`（物理像素）。**返回的帧是借用引用，渲染器不释放** |

渲染器行为：
- `options.PreviewSource != null` → **直接用它作底图**，完全不访问后端（预览不启动捕获，已用"后端调用次数 = 0"证明）。
- 否则 mode = glass 且后端就绪 → 用后端帧作底图，`Source = LiveFrame`。
- 否则 → **降级 gradient → solid**，并给出原因码与可读原因（见 §3.4）。

### 3.3 状态与降级（可诊断，不静默）

`src/System/Backdrop/BackdropStatus.cs` → `BackdropStatus`（只读**值类型**，正常路径零堆分配）：

| 成员 | 说明 |
|---|---|
| `RequestedMode` | 主题请求的模式：`solid` / `gradient` / `glass` |
| `EffectiveMode` | 实际生效模式（`BackdropRenderMode` 枚举） |
| `EffectiveModeName` | 生效模式的字符串名（写日志用） |
| `IsDegraded` | 是否发生降级 |
| `ReasonCode` | 降级原因码（`BackdropDegradeReason`） |
| `Reason` | **降级时必定非空**的可读原因文本 |
| `Source` | 底图来源：`Legacy` / `Gradient` / `PreviewFrame` / `LiveFrame` |
| `Layers` | 本次实际绘制的层（`BackdropLayer` 标志组合），用于证明"关闭时零副作用" |
| `RequiredBlurPx` | 本次要求的模糊半径（来自 `options.BlurPx`） |
| `PaintedAnything` | 是否绘制了任何一层 |
| `ToLogString()` | 生成一行可写日志的诊断文本 |

降级原因码（`BackdropDegradeReason`）：`None` / `BackendMissing` / `BackendNotReady` / `BackendFrameInvalid` /
`GradientUnavailable` / `InvalidBounds`。

典型降级文本（实测）：
- `未注册实时背景后端（IBackdropFrameSource 为空）；glass 已降级为 gradient。`
- `实时背景后端未就绪：DWM 系统材质在当前系统不可用；glass 已降级为 gradient。`
- `实时背景后端未提供有效帧：帧句柄失效；glass 已降级为 gradient。`
- `实时背景后端不可用（...）且渐变两端颜色均不可用；glass 已降级为 solid（沿用旧背景路径）。`

### 3.4 模式语义与默认值（与 v4 §3.1 对齐）

| 主题配置 | 本组件行为 |
|---|---|
| `Backdrop` 缺失 / `Mode = solid` / 未知模式 | **完全不绘制**：不创建画刷、不分配位图，旧背景路径原样保留（像素一致，0 字节分配） |
| `Mode = gradient` | 不透明底板 → 静态渐变 → tint → 噪点 → 边缘辉光 |
| `Mode = glass` | 后端就绪：底板 → 帧 → tint → 噪点 → 边缘辉光；否则降级 gradient → solid |
| `Opacity`（默认 0.75） | 背景 tint 层的额外 alpha 系数，与 `Tint` 自身 alpha **相乘**，只作用于背景层 |
| `Blur`（默认 12） | 物理像素值由 `options.BlurPx` 给出，本组件只负责**传给后端**并记在状态里，不自行模糊 |
| `GradientAngle`（默认 90） | 角度语义：**0° = 左→右，90° = 上→下，180° = 右→左，270° = 下→上**，其它角度线性插值 |
| 渐变两端缺省 | 起始色 = `Background`，结束色 = `GroupBackground` |
| `Tint` 缺省 | 取 `Background` |
| `Noise`（默认 0） | 0 → **不生成位图、不创建画刷、不绘制**；>0 → 固定种子、可复现 |
| `EdgeGlow`（默认 0） | 0 → **完全跳过**（不创建画刷） |
| `Fps`（默认 2） | 只对实时捕获后端有意义，本组件不消费（由 A15 负责） |

### 3.5 三条必须让接线方知道的实现决定

1. **不透明底板**：`gradient` / `glass` 模式每次绘制都先铺一层"背景色的不透明版本"
   （`Background` 的 RGB + A=255）。这是幂等性的来源；代价是这两种模式下背景不再半透明。
   `solid` 模式不动背景，旧主题的半透明表现完全保留。
2. **tint 会盖住渐变**：按冻结合同，tint 叠加在渐变之上，而 `Tint` 缺省 = `Background`、`Opacity` 缺省 = 0.75，
   所以**默认设置下渐变会被 75% 的背景色盖住**。要看到纯渐变，主题里需显式把 `Opacity` 设为 `0`
   或把 `Tint` 设为透明色。这是 v4 §3.1 的直接结果，不是实现取舍；已在 A08 侧记录，供 A10（编辑器）与
   A12（主题）设默认值时参考。
3. **边缘辉光取 `TintColor`**：本组件不消费 `BorderColor` / `ShadowColor`（那属于外框与窗口辉光，
   属 A05/A09 线），因此边缘辉光用背景染色色，alpha 随强度线性变化。

### 3.6 资源核算

| 项 | 数值 |
|---|---|
| 噪点纹理块 | 固定 128×128 32bpp = **65,536 B/块** |
| 噪点缓存上限 | 4 块（LRU 淘汰）→ **本组件位图占用上限 256 KiB** |
| 其它资源 | 渐变/染色用 GDI+ 画刷（每次渲染创建、随即释放），**不缓存整屏位图** |
| 与 16MiB 共享预算的关系 | 本组件 ≤ 256 KiB，仅占共享预算的 1.6%；与 A05 的装饰缓存同属 16MiB 体系，**由主集成者合并核算**（本线未做跨线联合核算） |
| 缓存键 | 噪点按强度（+ 固定种子常量）索引；纹理与窗口尺寸无关（平铺），因此尺寸/DPI 变化不需要重建噪点 |

### 3.7 失效入口

`src/System/Backdrop/BackdropRenderer.cs` → `BackdropRenderer.Invalidate()`：释放噪点纹理与画刷。
调用时机：主题代次变化、窗口尺寸变化、DPI 变化、窗口退出。**只能在两次绘制之间调用**。

---

## 4. 测试命令与退出码

```powershell
# 1) 主工程构建
dotnet build LiteMonitor.csproj -c Release
# 退出码 0；0 错误 / 232 警告

# 2) 自证工程构建（输出到自己的 output\ 目录）
dotnet build tests/AppearanceStaticBackdropProbe/AppearanceStaticBackdropProbe.csproj -c Release
# 退出码 0；0 错误 / 0 警告

# 3) 运行自证程序（18 个用例，输出 JSON 证据）
dotnet tests/AppearanceStaticBackdropProbe/output/AppearanceStaticBackdropProbe.dll
# 退出码 0；用例 18 个，通过 18 个，失败 0 个
```

**并行构建说明（实测）**：实施期间主工程两次因**其它 agent 正在编辑**的文件而编译失败
（`src/System/WebServer/WebPageContent.cs`，以及 `src/UI/Decor/**`）。
本线**未改动**这些文件，也未删除任何 `obj/`；在装饰线修复前，本线用一份**仓库外**的临时 MSBuild 覆盖文件
（`%TEMP%\a08-compile-check\exclude-wip-decor.targets`，仅做求值期 `Compile Remove`）验证自身代码可编译，
装饰线修复后已用**不带任何覆盖**的正式命令复跑通过（§1）。

---

## 5. 实测数字与像素哈希结论

证据文件：`tests/AppearanceStaticBackdropProbe/output/a08-static-backdrop-evidence.json`（11,126 B，2026-10-20 17:14:46）

| 用例 | 结论 | 关键数字 |
|---|---|---|
| C1 solid = 旧路径 | PASS | 旧路径哈希 `6F07F9B5…AE489` == 渲染后哈希 `6F07F9B5…AE489`；`Layers=None`；**每次渲染分配 0 字节** |
| C1b 整个 Backdrop 缺失 | PASS | 哈希同 C1 不变；`requestedMode=solid`；**0 字节/渲染** |
| C2a gradient 缺省 | PASS | 缺省起始色 = Background、结束色 = GroupBackground、角度 90°；顶端像素与起始色偏差 ≤ 2、底端 ≤ 6；沿 90° 方向单调 |
| C2b gradient 角度 | PASS | 0/45/90/180/270 五个角度的起止方向全部正确（左右/上下/对角三区均值比较，差值 > 30） |
| C3a glass 无后端 | PASS | `effective=gradient`、`degraded=true`、`code=BackendMissing`、原因非空；降级后哈希 `2E287EAC…6BDB` == 直接 gradient 的哈希 |
| C3b 后端未就绪 | PASS | `code=BackendNotReady`，原因文本包含后端自述原因；后端取帧次数 = 0 |
| C3c 后端无效帧 | PASS | `code=BackendFrameInvalid`；后端取帧次数 = 1 |
| C3d 渐变不可用 | PASS | `effective=solid`、`code=GradientUnavailable`、`Layers=None`、像素未被改动 |
| C3e 后端就绪 | PASS | `effective=glass`、`degraded=false`、`source=LiveFrame`；中心像素 = 帧色 `FF3366FF`；`options.BlurPx` 已传给后端；借用帧未被释放 |
| C4 tint × Opacity | PASS | Opacity=0 → 无染色层（像素 `FF000000`）；=0.5 → `FF800000`（alpha=128）；=1 → `FFFF0000`；Tint 缺省取 Background → `FF202020`；`rgba(255,0,0,0.5)` × 0.5 → `FF400000`（**Tint 自身 alpha 与 Opacity 相乘**）；客户区外像素保持 `FFFF00FF` 不变（**Opacity 只作用于背景层**） |
| C5a noise=0 | PASS | 噪点缓存条目 = 0、占用字节 = 0、`Layers` 不含 Noise |
| C5b noise>0 | PASS | 两次渲染哈希一致 `834C0834…73FE`；缓存条目 = 1、占用 = 65,536 B；与 noise=0 的画面哈希不同（噪点确实生效） |
| C6 EdgeGlow=0 | PASS | 分配：纯渐变 168 B/帧 == EdgeGlow=0 的 168 B/帧；EdgeGlow=60 为 296 B/帧；两者画面哈希不同（辉光生效） |
| C7 幂等 100 次 | PASS | 渐变+tint+噪点+辉光：单次 `1DB9ECA2…DC11` == 100 次后 `1DB9ECA2…DC11`；glass 降级路径同样 `4F9756F9…C5656` == `4F9756F9…C5656` |
| C8a 1×1 | PASS | 正常渲染，不抛异常 |
| C8b 宽高为 0 | PASS | `code=InvalidBounds`、`Layers=None`、像素未被改动 |
| C9 预览固定源 | PASS | `source=PreviewFrame`、`degraded=false`、**后端取帧次数 = 0**（预览不启动捕获）；固定源被正常绘制（中心像素 `FF22CC88`）且**未被释放** |
| C10 后台线程 | PASS | 线程数 9 → 9（无新增后台线程/计时器） |

其它已验证的硬性约束：
- `src/System/Backdrop/**` 内**不含**任何抓屏 API（`BitBlt` / `PrintWindow` / `CopyFromScreen` / DXGI /
  `SetWindowDisplayAffinity` 等）与计时器/后台任务；全目录检索仅命中一处说明性注释。
- 归一化只作用于运行时副本：本线**只读** `ThemeAppearanceOptions`，**不回写**用户 JSON，也未改动
  `ThemeAppearanceOptions` / `ThemeManager`。
- 借用引用（`BackgroundFrame` / `PreviewSource` / 后端帧）**一律不释放**（C3e、C9 已验证）。

---

## 6. 未验证项与已知限制

1. **未做真实窗口验证**：全部用例都是离屏渲染（`Bitmap` + `Graphics`），
   未在真实 MainForm 上叠加 DWM/窗口呈现验证。真实窗口的 DPI、局部重绘、圆角/Region 交互需在 A13/A14 补。
2. **只覆盖 96 DPI**：`options.EffectiveScale` 的缩放路径（边缘辉光带宽）未做 120/144/192 DPI 实测。
3. **玻璃路径只用假后端**：本线按职责不实现捕获；实时正确性、预算、帧所有权属 A06/A15，
   本线的"glass 生效"结论只覆盖"后端给出有效帧时渲染器如何消费"。
4. **性能预算未测**：v4 §7.4 的"每帧额外 CPU ≤ 1ms"等预算未在真实窗口/真实频率下测量。
   已测的只是**分配量**（solid 0 B、渐变 168 B/帧、+辉光 296 B/帧）。
5. **16MiB 共享预算未做跨线联合核算**：A08 自身 ≤ 256 KiB，与 A05 装饰缓存的合并占用需主集成者核算。
6. **横屏未接线**：`HorizontalRenderer` 的接入由 A13 完成；本组件只用 `options.ClientBounds`，
   横屏传不同的 bounds 即可，但未实测。
7. **视觉观感未评审**：噪点强度、边缘辉光宽度（96 DPI 下最大 24px，线性映射）未经真人眼评审，
   A12/A14 可能需要调参。
8. **探针工程未加入 sln**：`tests/AppearanceStaticBackdropProbe/AppearanceStaticBackdropProbe.csproj`
   尚未登记进 `LiteMonitor.sln`（sln 与所有 csproj 接线归主集成者），请接入。
9. **工具链坑（供其它 agent 参考）**：本机 SDK 8.0.426 上，**插值字符串中的条件表达式必须加括号**
   （`$"[{(ok ? "A" : "B")}]"`），否则报 CS8076 / CS8361；
   另外 .NET 的 `Color.op_Equality` 对"**命名颜色** vs 同色的构造颜色"返回 false
   （`Color.Magenta == Color.FromArgb(255,255,0,255)` 为 false，但两者 `ToArgb()` 相同），
   断言颜色请比较 `ToArgb()` 整数。

---

## 7. 交接清单（给主集成者）

- [ ] 在 `UIRenderer.Render` / `HorizontalRenderer.Render` 的**背景层位置**接入 `BackdropRenderer.Render`（用重载传入 `ThemeAppearanceOptions`）。
- [ ] 窗口/主题生命周期：创建时 `new BackdropRenderer(...)`，换肤/尺寸/DPI 变化时 `Invalidate()`，退出时 `Dispose()`。
- [ ] 把 `BackdropStatus` 接到 UI/日志（例如状态栏或日志文件），让降级原因**可见**（禁止静默）。
- [ ] 把 `tests/AppearanceStaticBackdropProbe/AppearanceStaticBackdropProbe.csproj` 加入 `LiteMonitor.sln`。
- [ ] 与 A05 合并核算 16MiB 纹理/装饰位图预算。
- [ ] A15 提供 `IBackdropFrameSource` 实现后，把 `options.BlurPx` 透传给捕获滤波后端。
