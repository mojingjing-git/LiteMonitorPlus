# A00 —— 主集成者基线与合同冻结记录

> 计划：[2026-10-20-appearance-agent-execution.md](2026-10-20-appearance-agent-execution.md)（v4）
> 冻结时间：2026-10-20 16:20 (Asia/Shanghai)
> 冻结人：主集成者（总集成负责人）
> 状态：已完成

---

## 1. 实际 HEAD 与工作区

| 项 | 值 |
|---|---|
| HEAD | `97f793dccac6d5ab37120602ecab4f6238a855da` |
| HEAD 标题 | `merge: performance sampling hardening` |
| 分支 | `master` |
| 已跟踪的改动 | `docs/APPEARANCE_PLAN.md`（v4 修订）<br>`docs/superpowers/plans/2026-10-20-performance-sampling-verification.md` |
| 未跟踪文件 | `LiteMonitor-old-config-20261020.zip`（用户备份，**不碰**）<br>`docs/HARDWARE-ROBUSTNESS-RESEARCH-2026-10-20.md`<br>`docs/superpowers/plans/2026-10-20-appearance-agent-execution.md`（本计划）<br>`docs/superpowers/plans/2026-10-20-hardware-stability-hardening.md`<br>`docs/superpowers/plans/2026-10-20-hardware-stability-parallel-dispatch.md`<br>`docs/superpowers/plans/2026-10-20-performance-sampling-merge.md` |

**约束**：以上未跟踪文件属用户与其他计划，**任何 agent 不得 `git add -A`**，
提交时必须显式列路径。`LiteMonitor-old-config-20261020.zip` 是用户自己的配置备份，禁止读写。

## 2. 10 个旧主题的不可变基线（SHA256 前 16 位）

改造过程中这些文件**不应被重写**（只允许新增两个新主题文件）。
若某个哈希变化，说明有人改写了旧主题，必须在交付里说明原因。

| 主题 | SHA256（前 16） |
|---|---|
| AuroraDream_Purple.json | `0D098F3AA66EA7BC` |
| CyberBlue_Neon.json | `213D6F92AE3D0075` |
| DarkFlat_Classic.json | `84B905210DE2FAE8` |
| DarkNeo_Modern.json | `619A4400D272CA74` |
| DeepOcean_Blue.json | `E5E3E7D0FFD38E76` |
| Dracula_Vampire.json | `A365E91E2364B932` |
| LightPure_Elegant.json | `950DCCDEA5E3BA53` |
| MatrixGreen_Terminal.json | `2E2038E6CE356D35` |
| Monokai_Pro.json | `7A347796A001B13F` |
| SolarAmber_Warm.json | `1D6182A337F22BCD` |

## 3. 必须保留的公开接口（v4 §3.2「保留现有公开 Render 签名」）

| 文件 | 签名 |
|---|---|
| `src/UI/UIRenderer.cs` | `public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t)` |
| `src/UI/HorizontalRenderer.cs` | `public static void Render(Graphics g, Theme t, List<Column> cols, int panelWidth)` |
| `src/UI/TaskbarRenderer.cs` | `public static void Render(Graphics g, List<Column> cols, bool light)` |

新增上下文一律用**重载**传入，不改这三处签名。

## 4. 项目路径（csproj / sln）

| 项目 | 路径 |
|---|---|
| 主工程 | `LiteMonitor.csproj`（`net8.0-windows`，x64） |
| 解决方案 | `LiteMonitor.sln` |
| 单元测试 | `tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj`（xUnit，25 个测试文件） |
| 性能探针 | `tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj` |
| A06 试验工程 | `tests/AppearanceBackdropProbe/`（**待建**，项目文件由主集成者接入） |

SDK：`G:\TOOL\dotnet-sdk\dotnet.exe`（8.0.426），也可用系统 `dotnet`。
主工程基线构建：**0 错误 / 242 个既有警告 / 3.48 s**。

## 5. 关键类型位置（file owner 冻结依据）

| 类型 | 当前位置 | 归属线 |
|---|---|---|
| `Theme` / `LayoutConfig` / `FontConfig` / `ColorConfig` / `ThresholdConfig` / `ThemeManager` | `src/Core/ThemeManager.cs` | 模型线（A01/A03） |
| `ThemeFileService` | `src/ThemeEditor/ThemeFileService.cs` | 保存线（A02） |
| `ThemeEditorForm` | `src/ThemeEditor/ThemeEditorForm.cs` | 编辑器线（A10） |
| `ThemePreviewControl` | `src/ThemeEditor/ThemePreviewControl.cs` | 编辑器线（A10） |
| `DecorRenderer`（待建） | `src/UI/Decor/` | 装饰线（A05/A07/A09） |
| `BackdropService`（待建） | `src/System/Backdrop/` | 背景线（A06/A08/A15） |

## 6. 对当前源码的重新确认（不沿用历史结论）

v4 §2 列出的 P1/P2 项已逐条对当前源码复核，**结论全部成立**：

| v4 断言 | 复核方式 | 结果 |
|---|---|---|
| `DWMWCP_NONE=0` 是错的，禁用圆角应为 `DWMWCP_DONOTROUND=1` | 查 MS 官方枚举文档 | ✅ 成立。官方枚举只有 `DEFAULT=0 / DONOTROUND=1 / ROUND=2 / ROUNDSMALL=3`（**没有 NONE 这个成员**）。0 是"系统决定"，很可能仍是 ROUND |
| `SafeWrite` 先删目标再移动 | 读 `ThemeFileService.SafeWrite` | ✅ 成立。`File.WriteAllText(tmp)` → `File.Delete(path)` → `File.Move(tmp, path)`；Move 失败则**原文件已丢失** |
| Web 挂载点在 `WebSessionManager` 而非 `LiteWebServer` | grep 两文件 | ✅ 成立。HTML 组装在 `WebSessionManager` 的 `WebPageContent.IndexHtml.Replace(...)`；`LiteWebServer` 对 `WebPageContent`/`IndexHtml` 引用数为 **0** |
| 旧基线竖屏 scale 2 物理尺寸是 480×1576 | 读 `baseline/probe-results.json` | ✅ 成立。基线记录 `width=480, height=1576`，此前文档写成 240×1576 有误 |
| `DwmEnableBlurBehindWindow` 自 Win8 起不再产生该模糊 | 依 v4 引用文档 | ✅ 接受，不再把老 Aero blur 当通用模糊实现 |
| 系统材质 API 存在，不因"需要 Windows App SDK"而排除 | 依 v4 引用文档 | ✅ 接受。`DwmSetWindowAttribute` + `DWMWA_SYSTEMBACKDROP_TYPE` 是文档化 Win32 API，是否在当前 layered/GDI 客户区可用**交 A06 实测裁决** |

## 7. 双计划冲突登记（v4 §6 末段要求）

`docs/superpowers/plans/2026-10-20-hardware-stability-hardening.md` 状态为**待实施**，
其基线为 `949835f`，**落后当前 HEAD 46 个提交**（含 performance sampling hardening 合并，
该合并改动了 `UIController.cs` +182、`HardwareValueProvider` +465、`FpsCounter` +1739、
`WebSessionManager` +869，并新增 `HardwareSamplingCoordinator`）。

该计划的并行派发表声明其主 agent 独占写集包含：
`HardwareMonitor`、`HardwareValueProvider`、`DiskManager`、`HardwareScanner`、`SensorMap`、
`GpuRegistry`、`HardwareErrorLog`、`Settings`、`SettingsHelper`、`SettingsForm`、`AppActions`、
**`UIController`**、**`TaskbarForm`**、**主窗体生命周期**、**所有 csproj/sln/依赖锁定**。

**与本计划主集成者独占写集的重叠**：`UIController.cs`、`TaskbarForm.cs`、
`MainForm_Transparent.cs`、`MainFormWinHelper.cs`、所有 csproj/sln。

**裁决**：

1. **总集成负责人唯一**。两个计划不得各设一个主 agent 同时改这些文件。
2. 本次由主集成者执行外观计划；硬件稳定性计划在其开始时**必须以新的 HEAD 重新冻结基线**
   （当前 46 个提交已改变其接口前提），并复用本次已冻结的 file owner 表，不得重复认领。
3. 硬件稳定性计划执行时，`HardwareValueProvider` / `HardwareSamplingCoordinator` 等
   采样侧文件**不在**外观计划写集内，可并行；共享的窗口/渲染/项目文件仍串行。
4. 外观计划**不引入第二套采样时钟**，接入现有采样协调器（v4 §6 末段 + 性能采样计划既有约束）。

## 8. 派发波次

| 波次 | 包 | 执行 |
|---|---|---|
| 1 | A00 | 主集成者（已完成） |
| 2 | A01 → A03（模型）、A02（保存）、A04（验证）、A06（背景/技术验证） | 并行，各线独占写集 |
| 3 | A05 → A07 → A09（装饰）、A08（背景静态）、A10（编辑器）、A11（Web）、A12（主题） | 依赖模型合同就绪后放行 |
| 4 | A13（M1 集成，主集成者） | 串行 |
| 5 | A14（M0/M1 验收 + 独立审查） | 串行 |
| 6 | A15 → A16 → A17（M2，**A06 放行后才做真实后端**） | 串行 |

**同机串行项**：正式打包、CPU 测量、录屏/屏幕捕获实验。
其它 agent 在这些窗口内继续纯代码与假后端工作。
