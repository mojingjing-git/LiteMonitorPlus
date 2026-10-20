# AppearanceWiringProbe —— A13 主面板接线自证探针

本工程验证 **A13（M1 主面板集成接线）** 的接线正确性。

> ⚠️ 本工程**不进 sln**（与既有 `LiteMonitor.PerformanceProbe` 的惯例一致）。
> ⚠️ 不新增任何 NuGet 依赖，只引用主项目与 BCL。
> ⚠️ 不创建 `HardwareMonitor`、不 Open 硬件、不做任何屏幕捕获；全部在**离屏位图**上完成。
> ⚠️ 探针自己 `new Settings()` 并注入 `Settings._instance` 单例，**不读也不写**用户常用配置目录。

## 用法

```powershell
dotnet build tests/AppearanceWiringProbe/AppearanceWiringProbe.csproj -c Release
tests/AppearanceWiringProbe/output/AppearanceWiringProbe.exe [--out <证据目录>]
```

退出码：`0` = 全部通过；`1` = 有用例失败；`2` = 探针自身异常。
证据 JSON：`output/evidence/wiring-evidence.json`。

## 为什么需要它

外观探针（`LiteMonitor.PerformanceProbe --appearance-only`）走的是
`UIRenderer.Render(g, groups, t)` 这个**旧签名入口**——那是为了保留"改造前的画面"作为可比基准。

而 A13 接线之后，**生产代码走的是新入口**（带外观运行时）。因此光有旧入口的基线，
不足以证明"新能力全关时画面不变"。本探针补的就是这一段：**同一个场景、同一个主题，
分别用旧入口和新入口各渲染一次，逐像素比对。**

## 断言清单

| 组 | 用例 | 断言 |
|---|---|---|
| ① | 旧主题竖屏（10 主题 × 4 档位 = 40） | 新入口与旧入口**逐像素一致**（差异 = 0） |
| ② | 旧主题横屏（10 主题 × 2 档位 = 20） | 同上 |
| ③ | 机能主题竖屏（4 档位） | 新旧**必须有差异**；`FrameEnabled`/`Scanline`/`Glow`/`Grid`/`BarStyle` 全部开启；背景层有效模式 = `Gradient`；**新画面里底板色残留像素 = 0**（证明底板让位、渐变铺满客户区），且旧画面作为对照组必须确有底板色 |
| ④ | 机能主题横屏（2 档位） | 新旧必须有差异；`FrameEnabled` 开启 |
| ⑤ | 旧主题外观快照不变量 | 10 个旧主题的快照里 `FrameEnabled`/`Scanline`/`Glow`/`Grid` 均为 false、`BarStyle` 为旧条、`BackdropMode` 为 `solid`、`BackgroundFrame` 为空、`ClientBounds` 与传入一致 |
| ⑥ | 外观运行时快照复用与失效 | 参数不变时连续 100 次 `Update` 返回**同一个**快照对象；客户区尺寸 / DPI 变化时**必须重建** |
| ⑦ | 换肤时代次自增 | 每次 `OnThemeChanged` 代次 +1 |
| ⑧ | 窗口轮廓接线行为 | 旧主题 → `DWM 角偏好 = 2 (ROUND)`；切角主题 → `1 (DONOTROUND)` 且 **Region 已设置**、切角边长 == `options.FrameLengthPx`；尺寸变化后重应用仍保持切角；切回旧主题恢复 `ROUND(2)` |

## 档位覆盖

- 竖屏：`(dpi=96, 1×)`、`(120, 1×)`、`(144, 2×)`、`(192, 2×)`
- 横屏：`(96, 1×)`、`(192, 2×)`

缩放走的是**与生产完全同一条路径**：`theme.Scale(dpiScale, userScale)` → 面板宽度 `240 × finalScale`。

## 窗口轮廓用例的线程要求

用例 ⑧ 会创建真实 `Form`（只取 `form.Handle` 创建句柄，**不显示**、位置在 `-32000,-32000` 离屏）。
WinForms 窗体必须在 **STA 线程**上创建与操作，因此该用例在独立的 STA 线程里执行
（与 A09 自证探针 `AppearanceWindowShapeProbe` 的规矩一致）。
