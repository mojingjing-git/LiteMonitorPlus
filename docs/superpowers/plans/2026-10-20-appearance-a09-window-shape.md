# A09 窗口轮廓策略 —— 实施结果

> 交付物：独立的新窗口外观组件 + 独立自证探针 + 本结果文档。
> **未修改** `src/UI/MainForm_Transparent.cs`、`src/UI/Helpers/MainFormWinHelper.cs`
> 及任何其他 agent 的写集（事件接线属 A13）。

---

## 1. 基线

| 项 | 值 |
|---|---|
| 仓库 | `G:\TOOL\LiteMonitor-fork`（分支 master） |
| HEAD | `97f793dccac6d5ab37120602ecab4f6238a855da` |
| 主程序集 | `bin\Release\net8.0-windows\LiteMonitor++.dll` |
| 主程序集 SHA256 | `AFAED3D17E85B2BAD1CA7A4DCCFF33DC8F823A29F15667CA8295C1238C6BE7E4`（构建于 2026-10-20T17:48:11+08:00，含 A09 新文件） |
| 探针程序集 | `tests\AppearanceWindowShapeProbe\output\AppearanceWindowShapeProbe.dll` |
| 操作系统 | Windows 11 专业版 10.0.26300.0（`Environment.OSVersion.Version`） |
| SDK | .NET 8.0.426 / `net8.0-windows` |
| 依赖 | **未新增任何 NuGet 依赖**；仅 `ProjectReference` 主工程 |
| 源码行尾 | CRLF（UTF-8 无 BOM），与仓库既有文件一致 |

上游依赖（已交付、本包只消费未修改）：
* `src/Core/ThemeAppearanceOptions.cs`（冻结合同，未改）
* `src/UI/Decor/DecorGeometry.cs`（A05，路径一律走 `BuildFramePath` / `BuildRoundedPath`，未另造路径算法）

---

## 2. 修改文件清单（全部为本包新建，未改动任何他人文件）

| 文件 | 说明 |
|---|---|
| `src/UI/Helpers/WindowAppearanceController.cs` | **新文件**：窗口轮廓统一入口（741 行，含 XML 注释） |
| `tests/AppearanceWindowShapeProbe/AppearanceWindowShapeProbe.csproj` | 新建自证工程（独立 `output\`，不污染主工程输出） |
| `tests/AppearanceWindowShapeProbe/Program.cs` | 探针入口（`--out` 可选参数；退出码 0/1/2） |
| `tests/AppearanceWindowShapeProbe/WindowShapeProbeRunner.cs` | 11 个用例的跑批 |
| `tests/AppearanceWindowShapeProbe/ProbeCase.cs` | 用例结果载体 |
| `tests/AppearanceWindowShapeProbe/README.md` | 用法与限制说明 |
| `docs/superpowers/plans/2026-10-20-appearance-a09-window-shape.md` | 本文档 |

> 未拆分第二个源文件（`WindowAppearanceNative.cs`），因此无需与主集成者确认后再拆。
> 组件不含任何事件绑定，**没有新增 Resize 绑定**。

---

## 3. 公共 API 与接线说明

### 3.1 类型与成员

命名空间 `LiteMonitor.src.UI.Helpers`。

```csharp
public enum WindowShapeStrategy { SystemRounded = 0, Chamfered = 1, Rounded = 2 }

public readonly struct WindowShapeResult
{
    public bool Applied { get; }              // 是否真的应用
    public bool Skipped { get; }              // 因无句柄等跳过（不是失败）
    public WindowShapeStrategy Strategy { get; }
    public int CornerPreference { get; }      // 实际传给 DWM 的值（未调用为 -1）
    public bool RegionApplied { get; }
    public Rectangle ClientBounds { get; }     // 本次使用的完整客户区
    public int CornerLengthPx { get; }        // 物理像素（未二次缩放）
    public int RadiusPx { get; }
    public int InsetPx { get; }
    public string Note { get; }
}

public sealed class WindowAppearanceDiagnostics   // 只读计数，供泄漏自检
{
    public int ApplyCount, DwmCornerCalls, DwmCornerFailures, LastDwmHr,
               DwmBorderCalls, PathsCreated, PathsDisposed, RegionsCreated,
               RegionsDisposedByController, SkippedNoHandle, OffThreadRejections,
               LastWindowDeviceDpi { get; }
}

public sealed class WindowAppearanceController : IDisposable
{
    public WindowAppearanceController(Form form);            // 必须在 UI 线程构造

    // —— 统一入口 ——
    public WindowShapeResult Apply(ThemeAppearanceOptions options,
                                   int cornerRadiusPx = -1,
                                   int frameOuterInsetPx = 0);
    public WindowShapeResult Reapply();                       // 尺寸/DPI 变化后重算
    public WindowShapeResult Reset();                         // 交回系统
    public void OnHandleCreated();
    public void OnHandleDestroyed();
    public bool TryGetCurrentCornerPreference(out int value); // 诊断
    public void Dispose();

    // —— 只读状态 ——
    public Form Form { get; }
    public int UiThreadId { get; }
    public WindowShapeStrategy LastStrategy { get; }
    public int LastCornerPreference { get; }
    public Rectangle LastClientBounds { get; }
    public int LastCornerLengthPx { get; }
    public int LastRadiusPx { get; }
    public int LastInsetPx { get; }
    public bool HasAppliedRegion { get; }
    public WindowAppearanceDiagnostics Diagnostics { get; }

    // —— 可调项 ——
    public int LegacyCornerRadiusPx { get; set; } = -1;        // 旧路径 Win10 分支半径（-1=自动读主题）
    public bool? DwmCornerSupportOverride { get; set; }        // 仅供探针模拟旧系统
    public DwmSetWindowAttributeHook? DwmSetWindowAttributeInterceptor { get; set; } // 仅供探针断言

    // —— DWM 常量（公开便于核对）——
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaBorderColor = 34;
    public const int DwmcpDefault = 0;      // 系统默认，**不是**禁用圆角
    public const int DwmcpDoNotRound = 1;   // 禁用圆角（切角用这个）
    public const int DwmcpRound = 2;        // 系统圆角
    public const int DwmcpRoundSmall = 3;
}
```

### 3.2 样式 → 窗口形状映射

| FrameStyle | 策略 | Region | DWM 角偏好（属性 33） |
|---|---|---|---|
| 0 无 / 旧路径 | `SystemRounded` | 交回系统（`null`） | **2 = ROUND**（与既有 `MainFormWinHelper.ApplyRoundedCorners` 在 Win11 上完全一致 → 旧主题观感不变） |
| 1 切角 | `Chamfered` | 切角路径（`DecorGeometry.BuildFramePath`，与画框同一套几何） | **1 = DONOTROUND** |
| 2 圆角描边 | `Rounded` | 圆角路径（半径同画框；半径=0 时不设 Region，交回系统） | **2 = ROUND** |
| 3 静态全息 | `Chamfered` | 切角路径（全息外层即切角，同 A05 规则） | **1 = DONOTROUND** |
| 未知值 | `SystemRounded` | 交回系统 | 2 |

* 每次应用还会顺手写一次 `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE`（属性 34），
  与既有 helper 行为一致（旧主题观感不变）；**不影响**角偏好断言（探针按属性号过滤）。
* 无 DWM 角偏好能力的系统（Win10）：不调用属性 33；圆角改用圆角 Region；
  旧路径沿用既有 `ThemeManager.Current.Layout.CornerRadius`（可用 `LegacyCornerRadiusPx` 覆盖）。

### 3.3 Region / 路径所有权（v4 §3.4 硬要求）

| 对象 | 创建者 | 释放者 | 时机 |
|---|---|---|---|
| `GraphicsPath` | 本组件（`DecorGeometry.BuildFramePath`） | **本组件** | 构造出 `Region` 后**立即**释放（`Region` 会复制路径内容） |
| `Region` | 本组件 | **WinForms（`Control`）** | 赋给 `Control.Region` 后由 Control 持有；替换 / 置空 / 控件销毁时由 Control 释放 |
| 窗口 HRGN | WinForms（`SetWindowRgn`） | 系统 / 窗口 | 窗口销毁或换 Region 时由系统释放 |

依据（.NET 8 `Control.Region` setter 源码）：
`oldRegion?.Dispose();` —— **Control 会释放自己持有的旧 Region**，因此本组件
**绝不** Dispose 赋给 `Control.Region` 的实例（否则变成"赋值的对象被提前释放"的悬空引用）。
既有 `MainFormWinHelper.ApplyRoundedCorners` 里 `_form.Region?.Dispose(); _form.Region = new Region(gp);`
的写法正是在窗体仍引用它时把它释放掉，本组件不沿用该写法。

`Diagnostics.RegionsDisposedByController` 恒为 0（可回归验证"组件不重复释放"）。

### 3.4 A13 接线建议（7 个时机）

> 组件**不绑定任何事件**；下表是建议的调用点。现有 `Resize` 绑定有两处
> （`MainFormWinHelper.InitializeStyle` 内一处、`MainForm.BindEvents` 内一处），
> 请收敛成**唯一一处**。

| 时机 | 调用 | 说明 |
|---|---|---|
| ① 初始化 | `Apply(options)` | 必须在 helper 已创建、**最终尺寸已确定**之后调用（否则 bounds 还没定） |
| ② 同尺寸换主题 | `Apply(newOptions)` | 用新 options 重新应用；`options.ClientBounds` 为最新完整客户区 |
| ③ Resize | `Reapply()` | **唯一一处** Resize 绑定里调用；`Reapply` 用窗体当前 `ClientRectangle` 重算 bounds，样式参数沿用上次 `Apply` |
| ④ DPI / 横竖变化 | `Apply(newOptions)`（options 里的 `DeviceDpi`/`FrameLengthPx` 已按新 DPI 重算）后紧接 `Reapply()` | 横竖切换会改变客户区尺寸，必须重算 |
| ⑤ HandleCreated | `OnHandleCreated()` | 句柄重建后旧的窗口 Region 已随旧窗口销毁，必须重新应用一次 |
| ⑥ HandleDestroyed | `OnHandleDestroyed()` | 置空 Region（由 WinForms 释放），此后不再调用任何 Win32 |
| ⑦ 退出 | `Dispose()` | 置空 Region；窗体随后被销毁 |

接线骨架（**A13 自有写集**，本包未提供也未修改）：

```csharp
// MainForm（示例）
private readonly WindowAppearanceController _appearance;   // 在 UI 线程构造
_appearance = new WindowAppearanceController(this);
this.HandleCreated   += (_, __) => _appearance.OnHandleCreated();
this.HandleDestroyed += (_, __) => _appearance.OnHandleDestroyed();
// ★ 唯一一处 Resize 绑定：
this.Resize += (_, __) => _appearance.Reapply();
// 初始化/换肤/DPI 变化：
_appearance.Apply(appearanceOptions);
// 退出：
_appearance.Dispose();
```

要点：
* 组件**不碰**置顶 / 鼠标穿透 / 淡入的既有时序（`MainFormWinHelper` 的 `TopMost`、`ClickThrough`、
  `StartFadeIn` 全部保持原样）。
* 组件**不消费** `BorderColor`（那是绘制层的事；消费颜色前必须先判 `FrameEnabled`）。
* `Apply` 的 `cornerRadiusPx` **必须与 A07 绘制外框时使用的半径一致**（默认取 `options.FrameLengthPx`），
  否则窗口轮廓与画出来的边框对不上。
* `frameOuterInsetPx` 建议保持 **0**：Region 会裁掉客户区，非 0 会把边缘内容切掉。
  A07 绘制时默认 `outerInsetPx = 1`（抗锯齿安全边距），两者相差 1 像素属预期。
* `MainFormWinHelper.ApplyRoundedCorners()` 若被本组件取代，需由 A13 决定如何收敛；
  本包**没有**改动该文件（避免出现第二个窗口轮廓写入者）。

### 3.5 冻结下游合同的落实

| 合同 | 落实方式 |
|---|---|
| 一律消费 `ThemeAppearanceOptions`，用 `FrameWidthPx`/`FrameLengthPx`（物理像素），**不得再乘缩放** | 组件内**没有任何缩放系数**；`_lastCornerLengthPx = options.FrameLengthPx` 直接使用。探针在 192 DPI 下实测轮廓切角正好在 16 物理像素（不是 32） |
| 消费 `BorderColor` 前必须先判 `FrameEnabled` | 组件完全不消费 `BorderColor` |
| 完整窗口 bounds 用 `options.ClientBounds`；不得用 `g.VisibleClipBounds` 或 `(-5,-5)` 填充矩形 | `Apply` 用 `options.ClientBounds`；`Reapply` 用 `_form.ClientRectangle`（完整客户区，非可见裁剪区） |
| 实际 DPI 必须取当前窗口 `DeviceDpi`，禁止用全局 `UIUtils.ScaleFactor` | 组件**不做任何 DPI 换算**；`options.DeviceDpi` 仅记录到诊断（`Diagnostics.LastWindowDeviceDpi` 记录窗口实际 DPI 供核对） |
| 归一化只作用于运行时副本，绝不回写用户 JSON | 组件只读 options，不接触 `Theme` / JSON |

---

## 4. 测试命令与退出码

```powershell
# 1) 主工程编译（含本包新文件）
dotnet build LiteMonitor.csproj -c Release
#   → 已成功生成，0 个错误（232 个警告全部来自其它既有文件）

# 2) 自证探针编译 + 运行
dotnet build tests\AppearanceWindowShapeProbe\AppearanceWindowShapeProbe.csproj -c Release
tests\AppearanceWindowShapeProbe\output\AppearanceWindowShapeProbe.exe
```

| 命令 | 退出码 |
|---|---|
| `dotnet build LiteMonitor.csproj -c Release` | **0** |
| `dotnet build tests\AppearanceWindowShapeProbe\AppearanceWindowShapeProbe.csproj -c Release` | **0**（0 警告 / 0 错误） |
| `tests\AppearanceWindowShapeProbe\output\AppearanceWindowShapeProbe.exe` | **0**（11/11 用例通过） |

证据文件：`tests/AppearanceWindowShapeProbe/output/evidence/window-shape-evidence.json`
（探针输出目录独立，不污染主工程输出，也不读写用户配置目录）。

---

## 5. 实测数字

### 5.1 样式映射（`style_mapping`）

| FrameStyle | 策略 | 实际传入 DWM（属性 33） | 是否设置 Region |
|---|---|---|---|
| 0 | SystemRounded | **2** | 否（交回系统） |
| 1 | Chamfered | **1** | 是（切角路径） |
| 2 | Rounded | **2** | 是（圆角路径） |
| 3 | Chamfered | **1** | 是（切角路径） |
| 99（未知） | SystemRounded | **2** | 否 |

* `chamfer_uses_do_not_round_one`：切角时传给 `DwmSetWindowAttribute` 的值恒为 **1**，
  且断言 **≠ 0**（`DWMWCP_DEFAULT` 不是"禁用圆角"）。
* `dwm_corner_preference_readback`（**走真实 DWM 调用**，再用 `DwmGetWindowAttribute` 读回）：
  切角 → **1**，圆角 → **2**，无框 → **2**（系统自己也确认了取值）。

### 5.2 可逆性（`reversible_style_switch`）

```
初始状态: strategy=SystemRounded, corner=2, hasRegion=False
切角    : strategy=Chamfered,    corner=1, hasRegion=True
圆角    : strategy=Rounded,      corner=2, hasRegion=True
无框    : strategy=SystemRounded, corner=2, hasRegion=False
最终状态: strategy=SystemRounded, corner=2, hasRegion=False   ← 与初始状态一致
```
切角 → 圆角 → 无框 后 `form.Region == null`、DWM 角偏好回到 2，**无残留**。

### 5.3 Region 所有权与泄漏（`region_ownership_no_leak`）

| 指标 | 基线 | 第 1 轮（100 轮切换） | 第 2 轮（再 100 轮） |
|---|---|---|---|
| GDI 对象数（`GetGuiResources(GR_GDIOBJECTS)`） | 16 | 16 | **16** |
| USER 对象数（`GR_USEROBJECTS`） | 16 | 16 | **16** |
| 内核句柄数（`Process.HandleCount`） | 260 | 261 | **261**（第 2 轮不再增长） |
| `GraphicsPath` 创建 / 释放 | — | — | **407 / 407**（相等） |
| `Region` 创建 / 由组件释放 | — | — | **407 / 0**（所有权在 WinForms） |

* 所有权实证：把 Region 赋给 `form.Region` 后再 `form.Region = null`，
  该 Region 实例随即**被 WinForms 释放**（再访问抛 `ArgumentException`）→ 所有权确实在 Control。
* `GetGuiResources` 统计的是 **GDI** 对象（窗口 HRGN 属于此类）；GDI+ 的 `Region`/`GraphicsPath`
  不是 GDI 对象，因此另用"创建数 = 释放数"的计数交叉验证（见上表最后两行）。

### 5.4 边界（`boundary_sizes`）

| 客户区 | style=1（切角） | style=2（圆角） |
|---|---|---|
| 1×1 | Region = 1×1@(0,0)（不抛异常） | 不设 Region（半径被夹紧为 0 → 交回系统） |
| 0×0 / 0×120 / 240×0 | **不设 Region**（无非法 Region） | **不设 Region** |
| 2×2 | Region = 2×2@(0,0) | Region = 2×2 |
| 60×40（切角超半宽） | Region = 60×40@(0,0) | Region = 60×40 |
| 240×160 | Region = 240×160 | Region = 240×160 |

全部 28 个组合（7 种尺寸 × 4 种样式）**无异常、无非法 Region**；
1×1 窗口 `Reapply()` 亦不抛异常。

### 5.5 物理像素只用一次（`physical_pixels_used_once`）

| DPI | `options.FrameLengthPx` | 传给轮廓的边长 | 轮廓实测切角位置（Region 最上一行扫描线左边界） |
|---|---|---|---|
| 192（dpiScale=2.0） | 16 | 16 | **16.0**（若二次缩放会是 32） |
| 96（dpiScale=1.0） | 8 | 8 | **8.0** |

> 测量方法说明：`Region.IsVisible` 在 GDI+ 中对边界有约 1~2 像素容差
> （实测 x=14.5 已被判为可见，而实际切角线在 x=15.5），**不能**用来判断点是否在切角内；
> 精确测量必须用 `Region.GetRegionScans`（探针按此实现，见 `geometry_diagnostic` 用例的读数）。

### 5.6 线程与句柄

* `ui_thread_only`：从后台线程调用 `Apply` → 抛 `InvalidOperationException`，
  `Diagnostics.OffThreadRejections` 递增。
* `handle_destroyed_no_win32`：句柄重建（`RecreateHandle`）后自动重新应用（Region 重新挂上、
  DWM 再写一次）；窗体 `Dispose()` 后再调 `Apply` **不抛异常**，且
  **DWM 调用次数保持 2 → 2 不变**（`SkippedNoHandle = 1`）。

---

## 6. 未验证项与已知限制

1. **Win10 分支未在真实 Win10 上验证**：本机是 Windows 11 11.0.26300。
   Win10（无 DWM 角偏好能力）路径只做了代码级实现与 `DwmCornerSupportOverride` 模拟入口，
   真实 Win10 行为**未验证**。旧路径的 `CornerRadius` 取值沿用既有 helper（未按 DPI 缩放），
   以保证旧主题观感不变。
2. **未做实窗观感/截图对比**：本包只验证窗口级形状（Region 几何 + DWM 属性），
   "切角轮廓与画出来的边框是否视觉对齐"需要 A07 的绘制结果 + A13 接线后做实窗截图比对。
3. **未验证真实 DPI（120/144/192）实窗**：探针用 `dpiScale` 合成 options 验证物理像素不被二次缩放，
   但没有在 192 DPI 的真实显示器上验证窗口轮廓。
4. **`Region` 的 GDI+ 内存不计入 `GetGuiResources`**：GDI+ 对象不是 GDI 对象，
   泄漏证据依赖"创建数 = 释放数"计数 + WinForms 释放实证，而非句柄计数。
5. **与 A07 的半径一致性靠约定**：组件不校验 A07 的绘制半径，
   必须由 A13 保证 `Apply(cornerRadiusPx)` 与 A07 的绘制半径取同一个值。
6. **未改动 `MainFormWinHelper`**：该文件仍有它自己的一套 `ApplyRoundedCorners`（含 `Region?.Dispose()`
   的旧写法）与**两处** Resize 绑定。A13 接线时需收敛为**唯一一处** Resize 绑定，
   并决定由谁最终写窗口形状（避免两个写入者互相打架）。
7. **`DwmSetWindowAttributeInterceptor` / `DwmCornerSupportOverride` 是测试接缝**：
   生产环境保持 `null` 即可（走原生实现）；若长期保留，建议在 A13 接线完成后评估是否收敛为 internal。
8. 探针在 STA 线程上创建窗体但**不显示**（只创建句柄），因此未验证"可见窗口"的实际呈现。

---

## 7. 验收对照

| 验收标准 | 结果 |
|---|---|
| DWM 属性值正确（DONOTROUND=1） | ✅ 传值断言 = 1 且 ≠ 0；系统读回 = 1 |
| 切角 / 圆角 / 无框可逆 | ✅ 初始与最终状态完全一致，无残留 Region / DWM 属性 |
| Region 与路径无泄漏 | ✅ 路径 407/407 全部释放；Region 由 WinForms 释放；200 轮切换后 GDI/USER/句柄数不增长 |
| 边界不抛异常 | ✅ 28 个尺寸×样式组合 + 1×1 `Reapply()` 全部通过 |
| 未改动任何他人文件 | ✅ 仅新增 2 个路径（源文件 + 自证工程 + 本文档） |
