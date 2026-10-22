# AppearanceBackdropProbe —— A06 液态玻璃技术验证探针

> 所属工作包：**A06（玻璃技术验证与路线裁决）**
> 权威规格：`docs/superpowers/plans/2026-10-20-appearance-agent-execution.md` §4
> 结果文件：`docs/superpowers/plans/2026-10-20-appearance-a06-glass-validation.md`

本工程**不是**功能实现，只是一台**取证仪**：它回答"实时玻璃这条路在本机到底走不走得通"，
产出可复核的本地证据（JSON + PNG）与支持矩阵。

---

## 1. 它做什么

| 场景 | 命令 | 对应 v4 §4 条目 | 产出 |
|---|---|---|---|
| 环境 | `--scenario env` | 环境记录要求 | `environment.json` |
| DWM 系统材质 | `--scenario dwm` | 第 1 条 | `dwm-backdrop.json` + `dwm/**/*.png` |
| 捕获后端 / EXCLUDEFROMCAPTURE | `--scenario capture` | 第 2、3 条 | `capture-backends.json` + `capture/**/*.png` |
| 排除区实时性诊断 | `--scenario diag` | 第 2、3 条补充 | `liveness-diagnostic.json` + `diag/*.png` |
| 模糊半径合同 | `--scenario blur` | 第 4 条 | `blur-radius.json` + `blur/*.png` |
| 性能与资源 | `--scenario perf` | §7.4 | `performance.json` |
| 全部 | `--scenario all` | — | 以上全部 |

> **`diag` 场景的作用**：判定 `WDA_EXCLUDEFROMCAPTURE` 排除出来的"洞"是**实时**的还是**冻结快照**。
> 做法：同一时刻抓两处 —— 探针窗口矩形（排除区）与探针之外的场景矩形（含每帧变化的时间戳）。
> 若"探针之外"在变而"探针之内"不变，说明排除区是冻结的，不能用于实时玻璃。
> 该场景还验证"瞬时排除"缓解方案（设置 → 抓一帧 → 立即清除）。

> **测量有效性守卫**：所有"背后内容是否响应"的判定都附带 `SceneRepaintsDuringMeasurement` /
> `SceneRepaintsDuringObservation`。若该值为 0（背景根本没动），结论一律标记为**无效**而不是"没有响应"——
> 避免把装置失灵误判成技术失败。

## 2. 哪些结论是"自动断言"，哪些是"截图观察"

**自动断言（JSON 里的数字，可复算）**

- `SelfMarkerFraction`：捕获画面中"本窗口自身品红标记色"的像素占比。
  `>0.005` ⇒ 抓到了自己 ⇒ 存在自反馈。
- `StaticPatchFraction`：捕获画面中"场景静态纯色块 RGB(24,92,168)"的像素占比。
  `>0.5` ⇒ 抓到的是真实背景而不是自己的窗口。
- `MeanAbsDiffVsReference`：捕获画面与**同源离屏渲染**的场景参考图的平均绝对差。
  `≈0` ⇒ 像素级一致；`>50` ⇒ 画面里是别的东西。
- `TemporalDiff`：相隔 1.2 秒的两帧之间的平均绝对差。
  `=0` ⇒ 背后内容**不随时间变化** ⇒ 不是实时模糊（这是判定 Mica/静态材质的关键指标）。
- `HueDistanceToExpected`：捕获画面色相区与场景理论色相（120°/秒）的夹角差。
  `<45°` ⇒ 背后内容实时响应。
- `MeasuredEdgeRisePx`：阶跃边缘 10%–90% 上升沿宽度（像素）。
  高斯下 `≈2.563σ`，用来把"模糊半径"变成可断言的数值。
- 性能：`CPUms` 为**进程 CPU 时间**（非墙钟）；占用率 `= CPUms/1000/逻辑核数×100%`。

**截图观察（必须人眼确认，PNG 为准）**

- DWM 材质到底画成了什么样（静态灰？壁纸色？还是真的背景？）。
- 自反馈演示里的拖影 / 文字重影是否肉眼可见。
- 外部观察者（≈用户录屏软件）视角下窗口是否消失。

## 3. 安全边界与退出方式

- **不上传任何桌面图像**：全部 PNG 落在本工程的 `output/` 目录，只在本地。
- **不捕获整块桌面**：只抓自建试验矩形（场景窗口 + 探针窗口覆盖范围）。
- **不改系统设置、不写用户配置目录、不改 `settings.json`**。
- **不安装驱动、不启动 ETW/PresentMon、不下载外部程序**。
- **不碰用户的其它窗口**：试验窗口是自建的 TopMost 窗口，用完即销毁。
- `SetWindowDisplayAffinity` **只作用于本探针进程自己的窗口**，进程退出即失效。

**安全退出**：直接关闭探针进程（或让它跑完自动退出）即可。窗口无任务栏项、`WS_EX_TRANSPARENT` 鼠标穿透，
不会挡住用户操作；若卡住，用 `Stop-Process -Name AppearanceBackdropProbe`。

## 4. 权限级别（重要差异）

- 本探针默认 **asInvoker**（`app.manifest`），因为自动化运行必须无人值守，
  `requireAdministrator` 会弹 UAC 阻塞探针。
- 生产主程序 `LiteMonitor++` 是 **requireAdministrator**（仓库根 `app.manifest`）。
- 需要提权复测时：

  ```powershell
  dotnet build tests/AppearanceBackdropProbe/AppearanceBackdropProbe.csproj -c Release -p:RequireAdmin=true
  ```

  该开关会改用 `app.manifest.admin`（requireAdministrator）。
- 每次运行的权限级别、完整性级别都写进 `environment.json`。
  **没有提权就只代表 asInvoker 下的结论**，不能据此推断提权场景。

## 5. 试验台构造

- 场景窗口：棋盘 + 静态纯色块 `RGB(24,92,168)` + 色相循环区（120°/秒）+ 移动扫描条（2 秒来回）。
- 探针窗口：复刻生产 `MainForm` 的窗口条件
  —— `FormBorderStyle.None`、`WS_EX_TOOLWINDOW`、`WS_EX_TRANSPARENT`、`WS_EX_LAYERED`、
  `Form.Opacity`、GDI+ 文本、`OnPaintBackground` 空实现、Win11 走 DWM 圆角 / Win10 走 `Form.Region`。
- 探针与场景用**同一段绘制代码**生成屏幕内容与离屏参考图，保证"抓到的是不是真实背景"可判定。
- 尺寸按物理像素给出（`AutoScaleMode.None`），并按目标显示器 DPI 缩放（与生产窗口一致）。
- 外部观察者：`scripts/external-capture.ps1`，**独立 pwsh 进程**抓屏，
  既充当旁观者，也充当"用户录屏软件"的等价视角。

## 6. 运行

```powershell
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
$probe  = 'tests\AppearanceBackdropProbe\bin\Release\net8.0-windows\AppearanceBackdropProbe.dll'

dotnet build tests\AppearanceBackdropProbe\AppearanceBackdropProbe.csproj -c Release

& $dotnet $probe --scenario all --duration 30 --fps 2 `
    --out tests\AppearanceBackdropProbe\output\run-<时间戳>
```

常用参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `--scenario` | `all` | `env` / `dwm` / `capture` / `blur` / `perf` / `all` |
| `--out` | `output\<时间戳>` | 证据输出目录（自有，不污染其它目录） |
| `--display` | 自动（优先负坐标显示器） | `Screen.AllScreens` 的索引 |
| `--width` / `--height` | `240` / `788` | 探针**逻辑**尺寸（按 DPI 换算物理像素） |
| `--duration` / `--fps` | `30` / `2` | 连续观察时长与帧率 |
| `--blur` / `--capture-scale` | `12` / `1.0` | 模糊半径与捕获降采样比例 |
| `--pwsh` | 自动探测 | 外部观察者用的 pwsh.exe 路径 |

## 7. 不引入新依赖

工程只用 BCL + WindowsDesktop（`System.Drawing`、`System.Text.Json`）。
**没有新增任何 NuGet 包**；`Microsoft.Windows.SDK.NET.Ref`（WinRT / Windows.Graphics.Capture 所需）
在本机不存在，若要评估 WGC 需要另行引入，会违反"试验工程不得引入新 NuGet 依赖"的约束 —— 见结果文件的未验证项。

## 8. 接线说明

本工程**不在** `LiteMonitor.sln` 内。若主集成者要纳入解决方案，
请在 `LiteMonitor.sln` 里添加 `tests/AppearanceBackdropProbe/AppearanceBackdropProbe.csproj`；
主工程 `LiteMonitor.csproj` 已有 `<Compile Remove="tests\**" />`，不会误编译本工程。
