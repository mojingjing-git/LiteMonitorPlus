# 自维护分发版：重品牌化与合规清理 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把上游 `Diorser/LiteMonitor` 的派生代码，改造成一份**完全自持**的可分发版本：用户可见品牌全部替换、上游基础设施（更新源 / 驱动源 / 插件索引 / 城市库 / API 端点）全部解耦、许可证与致谢齐备、文档声明与代码事实一致、发布包可复现。

**Architecture:** 三层替换。**L1 合规层**（LICENSE / NOTICE / README 致谢）建立分发合法性；**L2 解耦层**把所有"运行时依赖上游服务器"的硬编码 URL 换成自有源——这是本计划优先级最高的部分，不做的话改名版用户会被更新检查导流回上游；**L3 品牌层**分"用户可见"与"功能性"两类处理，功能性点位（互斥体名 / 计划任务名 / 崩溃日志名 / 预编译二进制名）成对迁移以免打断既有用户环境。

**Tech Stack:** C# / .NET 8 (`net8.0-windows`) / WinForms / LibreHardwareMonitorLib 0.9.6 / SDK 8.0.426

---

## Global Constraints

- 目标框架固定 `net8.0-windows`；**统一使用本地 SDK** `G:\TOOL\dotnet-sdk\dotnet.exe`（8.0.426），**不得**安装到系统或修改系统 PATH。
- 除测试工程外**不得新增任何 NuGet 依赖**；主工程依赖保持 `LibreHardwareMonitorLib 0.9.6` + `System.Text.Json 9.0.10` 不变。
- **不得删除任何上游版权声明**。`Copyright (c) 2025 Diorser` 必须出现在 `LICENSE`、`NOTICE`、README 致谢段、`AboutForm` 四处。
- **不得删除上游项目链接**。`https://github.com/Diorser/LiteMonitor` 必须作为"上游项目 / Upstream"保留在 `NOTICE` 与 README 致谢段中。
- 提交粒度：**每个 Task 结束提交一次**，提交信息用中文正文 + 英文 type 前缀（如 `feat(rebrand): ...`）。
- **`git add` 必须用显式路径**，**禁止 `git add -A`**。当前工作区存在其他任务遗留的未跟踪文件（`src/Plugins/PluginMarket.cs`、`src/UI/Settings/PluginMarketPage.cs`），`-A` 会把它们误提交进来。
- **命名空间 `LiteMonitor` 默认不改**（理由见 Task 7）。Task 7 是可选的、延后的、且必须在多显卡计划合并进 master 之后单独执行。
- 替换一律用**占位符** `{{BRAND}}` / `{{OWNER}}` / `{{REPO}}` / `{{REPO_OWNER}}` / `{{REPO_NAME}}` / `{{SITE}}` / `{{VERSION}}` 表达，本计划不含具体品牌名。执行前先完成 Task 0.1。
- 注释语言：与被改文件保持一致（`src/System/**` 用中文，`src/Core/**` 中英皆可），新增文件统一中文注释。
- **本计划在多显卡计划之后执行**（见「与多显卡计划的冲突消解」）。

---

## Task 0 决策记录（已定案，2026-10-20）

| 占位符 | 取值 | 说明 |
|---|---|---|
| `{{BRAND}}` | `LiteMonitor++` | 品牌显示名 / exe 名 / 程序集名 / 计划任务名 / 崩溃日志名 / User-Agent |
| `{{OWNER}}` | `mojingjing-git` | 本项目作者署名（LICENSE 与 NOTICE 的第二份版权） |
| `{{REPO}}` | `mojingjing-git/LiteMonitorPlus` | GitHub 仓库 |
| `{{REPO_OWNER}}` | `mojingjing-git` | |
| `{{REPO_NAME}}` | `LiteMonitorPlus` | |
| `{{SITE}}` | *(无)* | 没有官网，相关"官网"行改为"上游项目"致谢链接 |
| `{{VERSION}}` | `1.0.0` | 独立号段，与上游 1.3.6 区分 |

### 命名规则的硬约束：GitHub 仓库名不允许 `+`

**实测证据**（2026-10-20）：
- GitHub API `search/repositories?q=%2B+in:name` → `total_count: 0`
- GitHub API `search/repositories?q=%2B%2B+in:name` → `total_count: 0`
- 全站不存在任何名字含 `+` 的仓库

因此形成**双形态命名**：

| 用途 | 名称 | 能否含 `+` |
|---|---|---|
| 品牌显示 / exe / 程序集 / 计划任务 / 日志 / UA | `LiteMonitor++` | ✅ Windows 文件名与 .NET 程序集名均允许（已实测 `<AssemblyName>LiteMonitor++</AssemblyName>` 编译通过） |
| GitHub 仓库 / 发布包 / 资源下载 URL | `LiteMonitorPlus` | ❌ URL 与仓库名不允许 `+` |

**代码中不出现 `+` 标识符**：`namespace` 保持 `LiteMonitor` 不变（Task 7 默认不执行），故不存在 C# 标识符非法问题；且代码中**没有** `Type.GetType()` / `Assembly.Load()` 等跨程序集限定解析（已全仓核实），`+` 不会破坏运行时类型解析。

### 命名冲突核查结论

| 检索对象 | 结果 | 处置 |
|---|---|---|
| `Diorser/LiteMonitor`（上游） | 6683 star / 316 fork，活跃维护 | 保留并致谢 |
| `YuChen-OmO/LiteMonitor-Fork` | **已存在**，C#，"修复了集成显卡不能显示温度" | 直接先例：给 fork 加后缀命名是本项目既有社区实践 |
| 其它 `LiteMonitor` 同名仓库 | Python / Java / PHP 的 litemonitor 库、C++ 版等 | 与本项目品类不同，无冲突 |
| `Monitor`（Windows 版，pzkl.cn） | 功能与 LiteMonitor 高度重合的中文系统监控工具 | **因品牌带 `Lite` 前缀而避开**，不构成撞名 |
| `Monitor++`（Android App，Jet Sun Tech） | 相机监视器，同名不同类 | 同上，已避开 |
| `Notepad++` / `DebugView++` / `Win-M++` | `++` 后缀是开源生态"派生/增强版"的成熟约定 | 支持本命名方案 |

**结论**：`LiteMonitor++` 血缘自明、保留上游拼写、MIT 致谢义务天然满足，且与既有 fork 命名实践一致。

---

## 仓库与工具链现状（已核实）

- Fork: `https://github.com/mojingjing-git/LiteMonitor`（上游 `Diorser/LiteMonitor`）
- 工作副本: `G:\TOOL\LiteMonitor-fork`，`origin`=fork，`upstream`=上游
- SDK: `G:\TOOL\dotnet-sdk\dotnet.exe`（8.0.426）。系统 `C:\Program Files\dotnet\dotnet.exe` 也是 8.0.426，**但统一用本地路径**，与多显卡计划保持一致、避免 PATH 歧义。
- 规模: 89 个 `.cs` 文件 / 28,722 行（含空行）；**全部 89 个文件**使用 `namespace LiteMonitor*`（15 个 namespace 变体）
- 现有分支: `master`（HEAD `81bd139`）；`feat/multi-gpu` **尚未创建**
- **`resources/assets/` 不会被 csproj 复制到输出目录**。csproj 的 `Content` 只有 `themes/`、`lang/`、`plugins/`、`resources\LiteMonitor.Updater.exe`、`使用说明(必读).txt` 五项。`driver.zip`、`PawnIO_setup.exe`、`LiteMonitorFPS.exe`、`app.ico` 与 7 个 PNG 只存在于仓库中 —— **发布包目前靠手工拼装**（把 `assets/` 平铺进 `resources/`）。见 Task 6.5。
- `bin/Release/net8.0-windows/resources/` 实测内容：`lang/`、`plugins/`、`themes/`、`LiteMonitor.Updater.exe` —— **无 `assets/`**

### 命令速查

```powershell
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
```

> ⚠️ 后续所有命令块统一写成 `& $dotnet build ...`。**不要**写成裸 `dotnet build`——那会解析到 PATH 上的另一个 dotnet，与本计划的 SDK 约定不一致。

---

## ⚠️ 关键事实：本计划必须排在多显卡计划之后

多显卡计划 `docs/superpowers/plans/2026-10-20-gpu-multi-card.md`（当前工作区版本：**123,673 字节 / 2,413 行 / 17 个 Task 段**，含 `Task 5.5` / `Task 7.5` / `Task 11.5`）**已经写死了大量 `path:line` 引用**。本计划一旦替换品牌字符串，这些行号引用会大面积漂移。

> ⚠️ **该计划仍在被另一个会话改动**（`git status` 显示 `M docs/superpowers/plans/2026-10-20-gpu-multi-card.md`）。**本计划定稿前必须重跑一次跨计划冲突表核对**，以最终版本为准。

**结论：本计划从多显卡的 `feat/multi-gpu` 分支切出，在其全部 Task 完成后执行。**

### 与多显卡计划的冲突消解表

| 文件 | 多显卡计划 | 本计划 | 消解方式 |
|---|---|---|---|
| `resources/lang/*.json`（9 个） | Task 13 新增 `Menu.GpuMonitorCount` / `Menu.GpuMonitorCountTip` | Task 3 改 `Title` / `About` | ✅ 无语义冲突。两边都只**新增/替换单个键值**，不重排 JSON 结构 —— 这样 diff 可审阅。Task 8.4 的 JSON 合法性校验是守门 |
| `.gitignore` | Task 1 新建 | Task 6 期望存在 | ⚠️ **由多显卡 Task 1 负责创建**。本计划 Task 6 只校验 + 补充，**不重建** |
| `使用说明(必读).txt` | Task 1 提交（原样） | Task 5 改写内容 | ✅ 两次提交：Task 1 先提交原样，Task 5 再改。**不要合并成一次** |
| `src/UI/Settings/SystemHardwarPage.cs` | Task 13 新增显卡数量下拉 | Task 3 不碰此文件 | ✅ 无冲突 |
| **`src/UI/Helpers/MenuMonitorHelper.cs`** | Task 7.5 标签兜底归一化 | **Task 3.4（`:54` 消息框标题）、Task 7.1（`:28`/`:58` 全限定名）** | ⚠️ **真冲突**。两处都改同一文件 → 必须多显卡先完成、本计划后改。**不要并行编辑** |
| `src/Core/Settings.cs` | Task 5 新增 `MonitoredGpuCount` | 全程不碰 | ✅ 无冲突 |
| `src/Core/SettingsHelper.cs` | Task 4 / 5 接入 | Task 7（若执行）改 namespace | ⚠️ Task 7 触及。Task 7 延后到多显卡合并后 |
| `src/Core/MetricUtils.cs`、`src/Core/MetricLabelResolver.cs`、`src/Core/HardwareHistoryLogger.cs` | Task 9 / 10 / 11 深度改造 | Task 7（若执行）改 namespace | ⚠️ 同上 |
| `src/System/HardwareMonitor.cs`、`src/System/HardwareServices/{SensorMap,HardwareRules,ComponentProcessor,HardwareValueProvider}.cs` | Task 6~12 深度改造 | Task 7（若执行）改 namespace | ⚠️ 同上 |
| `src/UI/UIController.cs`、`src/UI/HardwareTrendForm.cs`、`src/Core/Actions/AppActions.cs` | Task 11.5 / 7.5 改造 | Task 7（若执行）改 namespace | ⚠️ 同上 |
| `LiteMonitor.sln` | Task 1 加入测试工程 | 全程不碰 | ✅ 无冲突 |
| `src/Plugins/PluginMarket.cs`、`src/UI/Settings/PluginMarketPage.cs` | 附录 B 明示"不引入插件远程安装" | Task 2.9 核对索引源域名 | ⚠️ **这两个文件当前是未跟踪状态且在开发中**（另一个会话在写）。Task 2.9 必须在插件市场代码**稳定后**执行，否则 URL 会被覆盖回去 |
| `docs/superpowers/plans/2026-10-20-gpu-multi-card.md` | — | Task 0.3 基线锚点 | ⚠️ 该文件**仍在变动**。Task 0.3 需记录其 commit SHA 作为基线 |

---

## 品牌映射表

执行 Task 2~4 时，下表是全部替换点。`{{BRAND}}` 为新品牌名（不含空格、`-`、`_`）。

### A. 用户可见品牌（Task 3）

| # | 位置 | 现值 | 新值 | 文件:行 |
|---|---|---|---|---|
| A1 | 语言包标题 | `⚡️LiteMonitor` | `⚡️{{BRAND}}` | `resources/lang/{zh,zh-tw,en,ja,ko,ru,fr,de,es}.json:2`（9 个） |
| A2 | 关于菜单（中） | `关于 LiteMonitor` | `关于 {{BRAND}}` | `resources/lang/zh.json:84` |
| A3 | 关于菜单（繁） | `關於 LiteMonitor` | `關於 {{BRAND}}` | `resources/lang/zh-tw.json:81` |
| A4 | 关于窗口标题 | `⚡️LiteMonitor` | `⚡️{{BRAND}}` | `src/UI/AboutForm.cs:46` |
| A5 | 关于窗口官网 | `LiteMonitor.cn` | `{{SITE}}` | `src/UI/AboutForm.cs:82,93` |
| A6 | 关于窗口 GitHub | `github.com/Diorser/LiteMonitor` | `github.com/{{REPO}}` | `src/UI/AboutForm.cs:102,113,138` |
| A7 | 托盘提示 | `LiteMonitor` | `{{BRAND}}` | `src/UI/Helpers/MainFormBizHelper.cs:161` |
| A8 | 更新弹窗标题（托盘） | `⚡️LiteMonitor_v` | `⚡️{{BRAND}}_v` | `src/UI/Helpers/MainFormBizHelper.cs:371` |
| A9 | 设置引导框 | `LiteMonitor Setup` | `{{BRAND}} Setup` | `src/UI/Helpers/MenuMonitorHelper.cs:54` |
| A10 | 设置窗消息框 | `LiteMonitor` | `{{BRAND}}` | `src/UI/SettingsForm.cs:236` |
| A11 | 菜单消息框 | `LiteMonitor` | `{{BRAND}}` | `src/UI/MenuManager.cs:134` |
| A12 | 任务栏设置消息框 | `LiteMonitor` | `{{BRAND}}` | `src/UI/Settings/TaskbarPage.cs:253` |
| A13 | 硬件详情窗标题 | `LiteMonitor - Hardware Info` | `{{BRAND}} - Hardware Info` | `src/UI/HardwareInfoForm.cs:26` |
| A14 | 主题编辑器标题 | `LiteMonitor Theme Editor` | `{{BRAND}} Theme Editor` | `src/ThemeEditor/ThemeEditorForm.cs:42` |
| A15 | Web 页面标题 | `LiteMonitor WebServer` | `{{BRAND}} WebServer` | `src/System/WebServer/WebPageContent.cs:31` |
| A16 | 更新对话框标题 | `⚡️LiteMonitor - ` | `⚡️{{BRAND}} - ` | `src/UI/UpdateDialog.cs:51` |
| A17 | 更新对话框 UA | `LiteMonitor-Updater/1.0` | `{{BRAND}}-Updater/1.0` | `src/UI/UpdateDialog.cs:233` |
| A18 | 插件作者字段 | `"author": "LiteMonitor"` | `"author": "{{BRAND}}"` | `resources/plugins/*.json:6`（**12 个**） |

### B. 功能性品牌（Task 4）—— 改动会打断既有用户环境，**必须成对处理**

| # | 位置 | 现值 | 新值 | 文件:行 | 迁移风险 |
|---|---|---|---|---|---|
| B1 | 计划任务名 | `LiteMonitor_AutoStart` | `{{BRAND}}_AutoStart` | `src/System/AutoStart.cs:12` | ⚠️ 旧任务会残留 → 需补清理逻辑（Task 4.1） |
| B2 | 计划任务描述 | `LiteMonitor Auto Start` | `{{BRAND}} Auto Start` | `src/System/AutoStart.cs:173` | 无 |
| B3 | 临时 XML 名 | `LiteMonitor_Task_{guid}.xml` | `{{BRAND}}_Task_{guid}.xml` | `src/System/AutoStart.cs:35` | 无（临时文件） |
| B4 | 防火墙指引文案 | `LiteMonitor` | `{{BRAND}}` | `src/UI/Settings/MainPanelPage.cs:203,205,218,220` | ⚠️ **必须与 exe 名严格一致** |
| B5 | 崩溃日志名 | `LiteMonitor_Error.log` | `{{BRAND}}_Error.log` | `src/System/Program.cs:136` | 老用户旧日志不再续写，可接受 |
| B6 | 崩溃弹窗标题 | `LiteMonitor Crash` | `{{BRAND}} Crash` | `src/System/Program.cs:149` | 无 |
| B7 | 单实例互斥体 | `Global\LiteMonitor_SingleInstance_*` | **建议不改** | `src/System/Program.cs:31,45,49,63` | ⚠️ 改了会与上游版双开（见 Task 4.3） |
| B8 | 主 exe 名 | `LiteMonitor` | `{{BRAND}}` | `LiteMonitor.csproj`（`<AssemblyName>`） | 防火墙条目、快捷方式随之变 |
| B9 | Updater 程序集名 | `LiteMonitor.Updater` | `{{BRAND}}.Updater` | `LiteMonitor.Updater/LiteMonitor.Updater.csproj:18` | ⚠️ **需重新 publish**，见 Task 4.6 |
| B10 | Updater 定位主 exe | `LiteMonitor.exe` | `{{BRAND}}.exe` | `LiteMonitor.Updater/Program.cs:13,32,189,212,216` | ⚠️ **不改则更新链断裂**，见 Task 4.6 |
| B11 | Updater 找自己 | `LiteMonitor.Updater.exe` | `{{BRAND}}.Updater.exe` | `LiteMonitor.Updater/Program.cs:86`、`src/System/UpdateChecker.cs:356,382` | ⚠️ 见 Task 4.6 |
| B12 | Updater 产物名 | `resources/LiteMonitor.Updater.exe` | `resources/{{BRAND}}.Updater.exe` | `LiteMonitor.csproj:44` | 见 Task 4.6 |
| B13 | FPS 组件名 | `LiteMonitorFPS.exe` | `{{BRAND}}FPS.exe` | `resources/assets/LiteMonitorFPS.exe`（**预编译，无源码**） | ⚠️ 只能改名，见 Task 4.7 |
| B14 | FPS 下载 URL | `.../LiteMonitorFPS.exe` | `.../{{BRAND}}FPS.exe` | `src/System/HardwareServices/DriverInstaller.cs:40-42` | 无 |
| B15 | FPS 本地落地路径 | `resources/LiteMonitorFPS.exe` | `resources/{{BRAND}}FPS.exe` | `src/System/HardwareServices/DriverInstaller.cs:122,199` | 无 |
| B16 | FPS 解压条目匹配 | `LiteMonitorFPS.exe` | `{{BRAND}}FPS.exe` | `src/System/HardwareServices/DriverInstaller.cs:279,294` | ⚠️ **不改则组件装不上** |
| B17 | FPS 进程名 | `LiteMonitorFPS` | `{{BRAND}}FPS` | `src/System/HardwareServices/FpsCounter.cs:67,550` | ⚠️ 进程名 = 文件名去扩展名 |
| B18 | FPS 组件路径 | `LiteMonitorFPS.exe` | `{{BRAND}}FPS.exe` | `src/System/HardwareServices/FpsCounter.cs:88` | 无 |
| B19 | User-Agent（插件） | `LiteMonitor/1.0` | `{{BRAND}}/1.0` | `src/Plugins/PluginExecutor.cs:66,451`、`src/Plugins/Native/CryptoNative.cs:55`、`src/Plugins/Native/CityCodeResolver.cs:151` | 无 |
| B20 | 发布包名 | `LiteMonitor_v{0}-win-x64.zip` | `{{BRAND}}_v{0}-win-x64.zip` | `src/System/UpdateChecker.cs:63,65,67` | ⚠️ 必须与 Task 6.4 实际发布包名一致 |
| B21 | 版本号 | `1.3.6` | `{{VERSION}}` | `LiteMonitor.csproj:15,17`、`resources/version.json:2` | ⚠️ 与上游同号，见 Task 6.4 |

### C. 刻意保留（不得替换）

| 位置 | 内容 | 原因 |
|---|---|---|
| `LICENSE` | `Copyright (c) 2025 Diorser` | MIT 强制义务 |
| `NOTICE` | 上游仓库链接 `https://github.com/Diorser/LiteMonitor` | 致谢义务 |
| `README.md` / `README.en.md` 致谢段 | 上游链接与原作者名 | 致谢义务 |
| `src/UI/AboutForm.cs:20-21` | `© 2025 Diorser / LiteMonitor Project` | **双份版权的其中一份** |
| `docs/superpowers/plans/*.md` | 计划文档中的历史引用 | 变更记录 |
| `src/Plugins/PluginMarket.cs:67` | `raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins` | 已是自有仓库，仅需核对 |

---

## File Structure

### 新建文件

| 文件 | 职责 |
|---|---|
| `LICENSE` | MIT 许可证全文 + 上游与本方双份版权声明 |
| `NOTICE` | 致谢、变更说明、第三方组件清单、上游链接 |
| `docs/REBRAND.md` | 品牌映射表、替换执行手册、发布打包流程、发布前 checklist |
| `scripts/pack-release.ps1` | **发布包拼装脚本**（当前完全靠手工，见 Task 6.5） |

### 修改文件（按 Task 分组）

| Task | 文件 |
|---|---|
| 1 | `README.md`、`README.en.md` |
| 2 | `src/System/UpdateChecker.cs`、`resources/version.json`、`src/System/HardwareServices/DriverInstaller.cs`、`src/Plugins/Native/CityCodeResolver.cs`、`resources/plugins/Crypto.json`、`src/Plugins/PluginMarket.cs`、`src/UI/MenuManager.cs`、`src/UI/Settings/PluginPage.cs`、`resources/plugins/GitHub.json` |
| 3 | `resources/lang/*.json`（9 个）、`resources/plugins/*.json`（12 个）、`src/UI/AboutForm.cs`、`src/UI/MenuManager.cs`、`src/UI/HardwareInfoForm.cs`、`src/UI/UpdateDialog.cs`、`src/ThemeEditor/ThemeEditorForm.cs`、`src/System/WebServer/WebPageContent.cs`、`src/UI/Helpers/MainFormBizHelper.cs`、`src/UI/Helpers/MenuMonitorHelper.cs`、`src/UI/SettingsForm.cs`、`src/UI/Settings/TaskbarPage.cs`、`src/System/UpdateChecker.cs:139` |
| 4 | `src/System/AutoStart.cs`、`src/System/Program.cs`、`src/UI/Settings/MainPanelPage.cs`、`LiteMonitor.csproj`、`LiteMonitor.Updater/LiteMonitor.Updater.csproj`、`LiteMonitor.Updater/Program.cs`、`src/System/UpdateChecker.cs:356,382`、`src/System/HardwareServices/DriverInstaller.cs`、`src/System/HardwareServices/FpsCounter.cs`、`src/Plugins/PluginExecutor.cs`、`src/Plugins/Native/CryptoNative.cs`、`src/Plugins/Native/CityCodeResolver.cs`、`resources/LiteMonitor.Updater.exe`、`resources/assets/LiteMonitorFPS.exe` |
| 5 | `使用说明(必读).txt`、`resources/plugins/PLUGIN_DEV_GUIDE.md`、`src/System/HardwareServices/HardwareReadme.md`、`src/UI/Settings/SETTINGS_README_DEV.md` |
| 6 | `.gitignore`（校验）、`resources/assets/app.ico`、`LiteMonitor.csproj`（版本号）、`scripts/pack-release.ps1`（新建）、`bin/`+`obj/`（清理，不入库） |
| 7 | 全部 89 个 `.cs`（仅当显式执行 Task 7） |

---

## Task 0: 决策门（不写代码，纯决策）

**Files:** 无
**Interfaces:** Produces: 一份填好的品牌名与替换映射表，供 Task 2~4 机械执行

- [ ] **Step 0.1: 确定品牌名**

确定 `{{BRAND}}`（程序集名 / exe 名 / 显示名）、`{{OWNER}}`（你的名字或组织）、`{{REPO}}`（自有仓库 `owner/repo`）、`{{REPO_OWNER}}` / `{{REPO_NAME}}`（仓库拆分形式）、`{{SITE}}`（官网域名，若无则留空并删掉相关行）。

**校验规则**（不满足则重取）：
- 不含 `LiteMonitor` / `Lite` / `Monitor` 的完整连写（避免商标混淆与搜索撞车）
- 仅 ASCII 字母 / 数字 / `-` / `_`
- 长度 ≤ 24（会进入互斥体名、计划任务名、临时文件名；`Program.cs:46` 已有 250 字符上限保护）
- 与 exe 名一致 —— Windows 防火墙列表显示的是 **exe 文件名**，Task 4.2 的引导文案依赖这一点

- [ ] **Step 0.2: 向上游索取 LICENSE 文件（法务阻塞项，不阻塞技术执行）**

上游 `Diorser/LiteMonitor` **仓库根目录没有 LICENSE 文件**（已核实：GitHub API `/repos/Diorser/LiteMonitor/license` → 404；`/contents/` 根目录仅 9 项 —— `LiteMonitor.Updater/` / `LiteMonitor.csproj` / `LiteMonitor.sln` / `Properties/` / `README.md` / `README.en.md` / `app.manifest` / `resources/` / `src/`）。README 声明 MIT，但授权文书本身缺失。

**动作**：在 `https://github.com/Diorser/LiteMonitor/issues` 开 issue，或邮件联系 `tueur2010@gmail.com`（上游提交者邮箱），请求：
1. 把 MIT `LICENSE` 文件（含 `Copyright (c) 2025 Diorser`）提交进仓库；**或**
2. 书面确认"本项目以 MIT 协议授权，可自由使用、修改、再分发"

**为什么必须做**：README 里的一句话是"授权意图的陈述"，不是"授权文书"。316 个 fork 已存在，法律追诉风险约等于零；但改名后**是你**在承担对外分发的责任，缺一份书面授权会让你自己的法务位置变弱。**10 分钟就能彻底消除这个不确定性。**

- [ ] **Step 0.3: 确认执行顺序与分支**

确认多显卡计划全部 Task 已完成并合并到 `master`，然后记录基线并切分支：

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'G:\TOOL\LiteMonitor-fork'
git checkout master
git pull origin master
git log --oneline -1 'docs/superpowers/plans/2026-10-20-gpu-multi-card.md'
git checkout -b chore/rebrand
git branch --show-current
```

Expected: 记录下多显卡计划的 commit SHA（**冲突表以此版本为准**），然后输出 `chore/rebrand`

---

## Task 1: 合规骨架（LICENSE / NOTICE / README 致谢）

**Files:**
- Create: `LICENSE`
- Create: `NOTICE`
- Modify: `README.md`、`README.en.md`

**Interfaces:**
- Consumes: Task 0.1 的 `{{OWNER}}`；Task 0.2 的上游确认（未回复也先按 MIT 写，收到确认后补一行说明）
- Produces: 合法可分发的 LICENSE + NOTICE；README 顶部有清晰的上游致谢与免责声明

- [ ] **Step 1.1: 写 `LICENSE`**

写入 `G:\TOOL\LiteMonitor-fork\LICENSE`（MIT 全文，**双份版权**）：

```
MIT License

Copyright (c) 2025 Diorser
Copyright (c) 2026 {{OWNER}}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

> ⚠️ 版权年份 `2025` 取自上游 `src/UI/AboutForm.cs:20` 的 `© 2025 Diorser`，与上游仓库创建时间 2025-11-10 一致。若上游回复了准确年份，以回复为准。

- [ ] **Step 1.2: 写 `NOTICE`**

写入 `G:\TOOL\LiteMonitor-fork\NOTICE`：

```
{{BRAND}}
Copyright (c) 2026 {{OWNER}}

本产品基于开源项目 LiteMonitor 派生、修改并自行分发。

上游项目：LiteMonitor
原作者：Diorser
上游仓库：https://github.com/Diorser/LiteMonitor
上游主页：https://litemonitor.cn
上游协议：MIT License

本派生版本依照 MIT License 发布，完整许可文本见同目录 LICENSE 文件。
上游版权声明（Copyright (c) 2025 Diorser）在所有分发副本中均予保留。

本项目名称、图标、界面与发行渠道与上游项目无关联，非上游官方版本。
"LiteMonitor" 商标及名称归原作者所有，本项目不使用。

------------------------------------------------------------------------
第三方组件
------------------------------------------------------------------------
- LibreHardwareMonitorLib 0.9.6 —— MPL-2.0，https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- System.Text.Json 9.0.10 —— MIT，https://github.com/dotnet/runtime
- PawnIO 驱动 —— 由上游项目分发，本项目沿用其官方安装包
- PresentMon / {{BRAND}}FPS —— BSD-3-Clause，https://github.com/GameTechDev/PresentMon
```

> `PawnIO` 与 `PresentMon` 的实际许可由其各自上游决定，此处仅作来源声明；发布前请核对并修正。

- [ ] **Step 1.3: README 顶部加致谢横幅**

在 `README.md` 第 1 行之前插入：

```markdown
> [!NOTE]
> **本项目基于 [LiteMonitor](https://github.com/Diorser/LiteMonitor)（原作者 Diorser，MIT 协议）派生修改。**
> 上游项目主页：https://litemonitor.cn ｜ 本项目为非官方版本，与原作者无关联。
> 完整许可证见 [LICENSE](./LICENSE)，变更与致谢见 [NOTICE](./NOTICE)。
```

`README.en.md` 插入等价英文版本（`[!NOTE]` 提示块，措辞对齐）。

- [ ] **Step 1.4: README 许可证段改写**

`README.md:175-176` 当前为：

```
## 📄 开源协议
本项目基于 **MIT License** 开源，可自由使用、修改与分发。
```

改为：

```
## 📄 开源协议

本项目基于 **MIT License** 发布，可自由使用、修改与分发。

- 完整许可文本：[LICENSE](./LICENSE)
- 上游项目与变更说明：[NOTICE](./NOTICE)
- 上游项目：https://github.com/Diorser/LiteMonitor （原作者 Diorser）
```

`README.en.md:174-176` 同步改写。

- [ ] **Step 1.5: README 其余上游链接处理**

`README.md` 中指向上游的链接共 5 处 + 2 处 exe 名：

| 行 | 内容 | 处理 |
|---|---|---|
| 9 | 下载链接 `github.com/Diorser/LiteMonitor/releases/latest` + `litemonitor.cn/` | 改为自己的 releases + 自己的镜像站 |
| 36 | Issues 反馈链接 | 改为 `github.com/{{REPO}}/issues` |
| 81 | Releases 页面链接 | 改为 `github.com/{{REPO}}/releases` |
| 82 | `解压后运行 LiteMonitor.exe` | 改为 `{{BRAND}}.exe` |
| 163 | `git clone https://github.com/Diorser/LiteMonitor.git` | 改为 `git clone https://github.com/{{REPO}}.git` |
| 170 | `/bin/Release/net8.0-windows/LiteMonitor.exe` | 改为 `{{BRAND}}.exe` |
| 182 | 项目主页 | 改为 `github.com/{{REPO}}` |

`README.en.md` 对应行：8、9、65、66、163、170、182。

> ⚠️ **README 里仍需保留一处上游链接** —— 放在 Task 1.3 插入的致谢横幅里，指向 `https://github.com/Diorser/LiteMonitor`。这是 MIT 的致谢惯例。

- [ ] **Step 1.6: 提交**

```powershell
git add LICENSE NOTICE README.md README.en.md
git commit -m "docs(license): 补齐 MIT 许可证、NOTICE 致谢与 README 上游声明

上游仓库根目录缺少 LICENSE 文件（GitHub API license=null），本仓库补齐。
保留上游 Copyright (c) 2025 Diorser，并新增本方版权声明。"
```

---

## Task 2: 解除上游基础设施耦合（最高优先级）

**这是本计划最重要的一步。** 不做的话，改名版的用户会被自动更新检查引导去下载**上游的 LiteMonitor**，你辛苦拉来的用户被全部导流回原作者。

**Files:**
- Modify: `src/System/UpdateChecker.cs:43-54`（**整数组**）、`62-67`、`140`
- Modify: `resources/version.json:5`
- Modify: `src/System/HardwareServices/DriverInstaller.cs:30-43`
- Modify: `src/Plugins/Native/CityCodeResolver.cs:15`
- Modify: `resources/plugins/Crypto.json`
- Modify: `src/Plugins/PluginMarket.cs:67`（仅核对）
- Modify: `src/UI/MenuManager.cs:600,604`
- Modify: `src/UI/Settings/PluginPage.cs:135-139`
- Modify: `resources/plugins/GitHub.json:14`

**Interfaces:**
- Consumes: Task 0.1 的 `{{REPO}}` / `{{REPO_OWNER}}` / `{{REPO_NAME}}` / `{{BRAND}}` / `{{SITE}}`
- Produces: 程序运行时**不再请求任何 `Diorser/*` 或 `litemonitor.cn` 域名**

- [ ] **Step 2.1: `UpdateChecker` 版本源整数组替换（⚠️ 含主源）**

`src/System/UpdateChecker.cs:43-54` 现状：

```csharp
private static readonly string[] VersionJsonUrls =
{
     // 国官网源
    "https://litemonitor.cn/update/version.json",     // ← :46  主源！
    
     // Gitee RAW（自动 fallback 使用）
    "https://gitee.com/Diorser/LiteMonitor/raw/master/resources/version.json",   // :49

     // GitHub RAW（自动 fallback 使用）
    "https://raw.githubusercontent.com/Diorser/LiteMonitor/master/resources/version.json",  // :52
};
```

> ⚠️ **`:46` 是数组第一项（主源），最容易漏改。** 漏了它，每次"检查更新"仍会先打 `litemonitor.cn`。

**整数组替换为**：

```csharp
private static readonly string[] VersionJsonUrls =
{
     // 自有源（主）
    "https://raw.githubusercontent.com/{{REPO}}/master/resources/version.json",

     // Gitee 镜像（自动 fallback 使用）
    "https://gitee.com/{{REPO_OWNER}}/{{REPO_NAME}}/raw/master/resources/version.json",
};
```

- [ ] **Step 2.2: `UpdateChecker` 下载源替换**

`src/System/UpdateChecker.cs:62-67` 现状：

```csharp
// Gitee Releases
"https://gitee.com/Diorser/LiteMonitor/releases/download/v{0}/LiteMonitor_v{0}-win-x64.zip",
// 国内 CDN
"https://litemonitor.cn/update/LiteMonitor_v{0}-win-x64.zip",
// Github Releases
"https://github.com/Diorser/LiteMonitor/releases/download/v{0}/LiteMonitor_v{0}-win-x64.zip",
```

改为（**文件名模板必须与 Task 6.3 的发布包命名一致**）：

```csharp
// GitHub Releases
"https://github.com/{{REPO}}/releases/download/v{0}/{{BRAND}}_v{0}-win-x64.zip",
// Gitee 镜像
"https://gitee.com/{{REPO_OWNER}}/{{REPO_NAME}}/releases/download/v{0}/{{BRAND}}_v{0}-win-x64.zip",
```

> 若不建 Gitee 镜像，删掉第二行即可；但**至少保留 GitHub 一条**，否则 `sortedUrls` 可能为空导致更新功能异常。
> **不要把上游 URL 留作 fallback** —— 那等于把用户导流回去（见附录 B）。

- [ ] **Step 2.3: 更新弹窗文案换源**

`src/System/UpdateChecker.cs:140` 现状：

```csharp
Description = $"更新日志：\n{changelog} \n更新日期：\n{releaseDate}\n\n官网：https://litemonitor.cn \nGitHub：https://github.com/Diorser/LiteMonitor",
```

改为：

```csharp
Description = $"更新日志：\n{changelog} \n更新日期：\n{releaseDate}\n\n官网：https://{{SITE}} \nGitHub：https://github.com/{{REPO}}",
```

（若无官网，删掉"官网"一行。）

- [ ] **Step 2.4: `version.json` 换 downloadUrl**

`resources/version.json:5` 现状：`"downloadUrl": "https://github.com/Diorser/LiteMonitor/releases"`

改为 `"downloadUrl": "https://github.com/{{REPO}}/releases"`，`version` 改成自有版本号（见 Task 6.4）。

- [ ] **Step 2.5: `DriverInstaller` 驱动包与 FPS 源替换**

`src/System/HardwareServices/DriverInstaller.cs:30-35` 现状：

```csharp
private readonly string[] _driverPackageUrls = new[]
{
    "https://gitee.com/Diorser/LiteMonitor/raw/master/resources/assets/driver.zip",
    "https://litemonitor.cn/update/driver.zip", 
    "https://github.com/Diorser/LiteMonitor/raw/master/resources/assets/driver.zip" 
};
```

`:38-43`（FPS 组件）同理。

改为自有源（并在 `resources/assets/` 下自备同名文件）：

```csharp
private readonly string[] _driverPackageUrls = new[]
{
    "https://raw.githubusercontent.com/{{REPO}}/master/resources/assets/driver.zip",
    "https://gitee.com/{{REPO_OWNER}}/{{REPO_NAME}}/raw/master/resources/assets/driver.zip"
};

private readonly string[] _presentMonUrls = new[]
{
    "https://raw.githubusercontent.com/{{REPO}}/master/resources/assets/{{BRAND}}FPS.exe",
    "https://gitee.com/{{REPO_OWNER}}/{{REPO_NAME}}/raw/master/resources/assets/{{BRAND}}FPS.exe"
};
```

> ⚠️ **上游源技术上可以继续用**（MIT 允许再分发第三方二进制），但你既然要做自有分发，就该自己托管 —— 上游随时可能换路径、删文件或改版本，你的用户会突然装不上驱动。

- [ ] **Step 2.6: 补上下载完整性校验（安全缺口，强烈建议）**

**已核实**：`DriverInstaller.cs` 全文**没有任何 SHA256 / 哈希 / 签名校验代码**（`SHA256|HashAlgorithm|ComputeHash|Authenticode` 零命中），且 `:692` 确有 `Verb = "runas"` —— 即"下载即提权执行"成立。源站被劫持 = 用户机器上提权执行任意代码。

1. 加常量：

```csharp
// driver.zip 的 SHA256（小写 hex，无空格）
private const string DriverPackageSha256 = "{{DRIVER_ZIP_SHA256}}";
// FPS 组件的 SHA256
private const string FpsComponentSha256 = "{{FPS_EXE_SHA256}}";
```

2. 加校验方法：

```csharp
private static bool VerifySha256(string filePath, string expectedHex)
{
    try
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}
```

3. 在 `InstallDriverPackage` 的解压分支与 `InstallFpsComponent` 的落地分支调用，校验失败则中止并提示。**绝不降级为"警告后继续执行"。**

4. 生成哈希：

```powershell
Get-FileHash 'G:\TOOL\LiteMonitor-fork\resources\assets\driver.zip' -Algorithm SHA256 | Select-Object -ExpandProperty Hash
Get-FileHash 'G:\TOOL\LiteMonitor-fork\resources\assets\{{BRAND}}FPS.exe' -Algorithm SHA256 | Select-Object -ExpandProperty Hash
```

> 补充说明：`LiteMonitor.Updater/Program.cs:201` 有一处 `md5.ComputeHash`，但那是对**路径**取哈希用于定位目录，与下载完整性无关。

- [ ] **Step 2.7: `CityCodeResolver` 城市库源换掉**

`src/Plugins/Native/CityCodeResolver.cs:15` 现状：

```csharp
private const string JSON_DB_URL = "https://litemonitor.cn/update/CityCode.json";
```

这是**运行时强依赖上游服务器存活**的接口（上游域名失效 → 天气插件的省市定位直接报错）。

改为本地分发（**推荐**）：把 `CityCode.json` 放进 `resources/assets/`，改为

```csharp
private static readonly string LocalJsonPath =
    Path.Combine(AppContext.BaseDirectory, "resources", "assets", "CityCode.json");
```

读取时优先本地，缺失再回退远程（`https://raw.githubusercontent.com/{{REPO}}/master/resources/assets/CityCode.json`）。本地方案离线可用、零外部依赖。

> ⚠️ 注意：`resources/assets/` 不会被 csproj 复制到输出目录（见 Task 6.5），所以本地方案**必须配合 Task 6.5 的打包脚本**才能生效。

- [ ] **Step 2.8: `Crypto.json` 的 API 端点换掉**

`resources/plugins/Crypto.json` 中的 `https://crypto.litemonitor.cn/?symbol={{symbol}}` 是上游自建的行情代理。

`src/Plugins/Native/CryptoNative.cs:190-194` 已有 4 家交易所的 fallback 实现（Bybit / GateIO / Binance / OKX）。**推荐**：把 `Crypto.json` 的默认端点改为公共 API（如 `https://api.binance.com/api/v3/ticker/24hr?symbol={{symbol}}`），把上游代理降为备选。

- [ ] **Step 2.9: 核对 `PluginMarket` 索引源**

`src/Plugins/PluginMarket.cs:67` 现状：

```csharp
"https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/index.json";
```

✅ **这已经是自有仓库**，无需改动。只需确认该 `index.json` 内的插件条目不引用 `litemonitor.cn` 等上游域名。

> ⚠️ **本步骤必须在插件市场代码稳定后执行**。`src/Plugins/PluginMarket.cs` 与 `src/UI/Settings/PluginMarketPage.cs` 当前是**未跟踪且在开发中**（另一个会话在写），过早改动会被覆盖。

- [ ] **Step 2.10: 菜单外链替换**

- `src/UI/MenuManager.cs:600` → `https://github.com/{{REPO}}/issues`
- `src/UI/MenuManager.cs:604` → `https://github.com/{{REPO}}/releases`

- [ ] **Step 2.11: 插件开发指南链接替换**

`src/UI/Settings/PluginPage.cs:137` 的 `ProcessStartInfo("https://github.com/Diorser/LiteMonitor/blob/master/resources/plugins/PLUGIN_DEV_GUIDE.md")` → `https://github.com/{{REPO}}/blob/master/resources/plugins/PLUGIN_DEV_GUIDE.md`

- [ ] **Step 2.12: GitHub 插件默认仓库值**

`resources/plugins/GitHub.json:14` 现状 `"default": "diorser/LiteMonitor"` → `"default": "{{REPO}}"`

- [ ] **Step 2.13: 验证解耦彻底（审计命令）**

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'G:\TOOL\LiteMonitor-fork'
# 模式必须包含纯 "LiteMonitor"，否则只看 Diorser 会漏掉 litemonitor.cn 之外的残留
$hits = git ls-files | Where-Object { $_ -notmatch '^(bin|obj)/' } | ForEach-Object {
  Select-String -Path $_ -Pattern 'Diorser|litemonitor\.cn' -ErrorAction SilentlyContinue
}
$hits | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
```

**Expected（允许清单，必须逐条确认落在这些位置）**：
- `LICENSE`（`Copyright (c) 2025 Diorser`）
- `NOTICE`（上游链接与致谢）
- `README.md` / `README.en.md`（致谢横幅与许可证段）
- `src/UI/AboutForm.cs`（`© 2025 Diorser / LiteMonitor Project` —— 刻意保留的版权行）
- `docs/superpowers/plans/*.md`（两份计划文档的历史引用）

**必须为零**：以上 5 类之外的任何命中。出现即为漏改。

> 补充审计（查纯 `LiteMonitor` 残留，模式不同）：

```powershell
git ls-files | Where-Object { $_ -notmatch '^(bin|obj)/' } | ForEach-Object {
  Select-String -Path $_ -Pattern 'LiteMonitor' -ErrorAction SilentlyContinue
} | Where-Object { $_.Line -notmatch '^\s*(namespace|using)\s' } | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
```

Expected: 命中的每一处都应落在品牌映射表 A/B/C 的某一行（或在 Task 3/4 尚未执行时留待后续 Task 处理）。

- [ ] **Step 2.14: 提交**

```powershell
git add src/System/UpdateChecker.cs src/System/HardwareServices/DriverInstaller.cs `
        src/Plugins/Native/CityCodeResolver.cs src/Plugins/PluginMarket.cs `
        src/UI/MenuManager.cs src/UI/Settings/PluginPage.cs `
        resources/version.json resources/plugins/Crypto.json resources/plugins/GitHub.json
git commit -m "feat(rebrand): 解除对上游基础设施的运行时耦合

- UpdateChecker VersionJsonUrls 整数组换为自有源（原主源 litemonitor.cn 已移除）
- UpdateChecker 下载源换为自有仓库
- version.json downloadUrl 换为自有 releases
- DriverInstaller 驱动包/FPS 源换为自有仓库
- 补上下载产物 SHA256 校验（原实现无任何完整性校验即提权执行）
- CityCodeResolver 城市库改本地优先，移除对 litemonitor.cn 的运行时依赖
- Crypto 插件端点改用公共行情 API
- 菜单反馈/更新日志、插件开发指南、GitHub 插件默认值换为自有仓库"
```

---

## Task 3: 用户可见品牌切换

**Files:** 见 File Structure 表 Task 3 行（9 个语言包 + 12 个插件 JSON + 10 个 .cs）

**Interfaces:**
- Consumes: Task 0.1 的 `{{BRAND}}` / `{{SITE}}` / `{{REPO}}`
- Produces: 界面上不再出现 "LiteMonitor" 字样（关于窗口的版权行除外，那里要写双份版权）

- [ ] **Step 3.1: 语言包 `Title` / `About`**

9 个语言包第 2 行均为 `"Title": "⚡️LiteMonitor",` → `"Title": "⚡️{{BRAND}}",`

- `resources/lang/zh.json:84` — `"About": "关于 LiteMonitor",` → `"关于 {{BRAND}}"`
- `resources/lang/zh-tw.json:81` — `"About": "關於 LiteMonitor",` → `"關於 {{BRAND}}"`

其余 7 个语言包的 `About` 无品牌字样，**不动**。

> ⚠️ 多显卡计划 Task 13 会往这 9 个文件加 2 个 key。改 brand 时**只动 `Title` 和 `About` 这两个条目**，不要重排 JSON 结构 —— 保持 diff 可审阅。

- [ ] **Step 3.2: 插件 JSON 的 `author` 字段**

12 个插件 JSON 的第 6 行均为 `"author": "LiteMonitor",` → `"author": "{{BRAND}}",`

文件：`Bilibili` / `Crypto` / `Exchange` / `GitHub` / `Hitokoto` / `Holiday` / `ProxyLatency` / `PublicIP` / `Stock` / `UniversalAPI` / `Weather` / `WeiboHot`（`.json`）

> ⚠️ `resources/plugins/Crypto.json` 还要做 Task 2.8 的端点替换，**两处改动在同一文件，分开提交或合并到一个 commit 但分清 hunk**。

- [ ] **Step 3.3: 关于窗口**

- `AboutForm.cs:46` — `Text = "⚡️LiteMonitor",` → `"⚡️{{BRAND}},"`
- `AboutForm.cs:82` — `Text = $"{strWebPrefix}: LiteMonitor.cn",` → `$"{strWebPrefix}: {{SITE}},"`
- `AboutForm.cs:93` — `ProcessStartInfo("https://LiteMonitor.cn")` → `"https://{{SITE}}"`
- `AboutForm.cs:102` — `Text = "GitHub: github.com/Diorser/LiteMonitor",` → `"GitHub: github.com/{{REPO}},"`
- `AboutForm.cs:113` — `ProcessStartInfo("https://github.com/Diorser/LiteMonitor")` → `"https://github.com/{{REPO}}"`
- `AboutForm.cs:138` — `ProcessStartInfo("https://github.com/Diorser/LiteMonitor/issues")` → `"https://github.com/{{REPO}}/issues"`

- [ ] **Step 3.4: 关于窗口版权行（法务关键）**

`src/UI/AboutForm.cs:20-21` 现状：

```csharp
string strDesc = isZh ? "一款开源的轻量级硬件监控软件。\n© 2025 Diorser / LiteMonitor Project" 
                      : "A lightweight desktop hardware monitor.\n© 2025 Diorser / LiteMonitor Project";
```

改为**双份版权 + 上游致谢**：

```csharp
string strDesc = isZh
    ? "一款开源的轻量级硬件监控软件。\n\n基于 LiteMonitor (Diorser) 派生修改\n© 2025 Diorser / LiteMonitor Project\n© 2026 {{OWNER}} / {{BRAND}} Project\n\nMIT License · github.com/{{REPO}}"
    : "A lightweight desktop hardware monitor.\n\nForked & modified from LiteMonitor (Diorser)\n© 2025 Diorser / LiteMonitor Project\n© 2026 {{OWNER}} / {{BRAND}} Project\n\nMIT License · github.com/{{REPO}}";
```

> ⚠️ **法务要点**：只写自己的版权而删掉 Diorser 那行，是 MIT 协议下最典型的违规。**两行都必须留。**

- [ ] **Step 3.5: 托盘 / 菜单 / 消息框**

- `src/UI/Helpers/MainFormBizHelper.cs:161` — `_tray.Text = "LiteMonitor";` → `"{{BRAND}}"`
- `src/UI/Helpers/MainFormBizHelper.cs:371` — `"⚡️LiteMonitor_v" + UpdateChecker.GetCurrentVersion()` → `"⚡️{{BRAND}}_v" + ...`
- `src/UI/Helpers/MenuMonitorHelper.cs:54` — `MessageBox.Show(msg, "LiteMonitor Setup", ...)` → `"{{BRAND}} Setup"`
- `src/UI/SettingsForm.cs:236` — `MessageBox.Show("...", "LiteMonitor", ...)` → `"{{BRAND}}"`
- `src/UI/MenuManager.cs:134` — `MessageBox.Show(msg, "LiteMonitor", ...)` → `"{{BRAND}}"`
- `src/UI/Settings/TaskbarPage.cs:253` — `MessageBox.Show(..., "LiteMonitor", ...)` → `"{{BRAND}}"`

> ⚠️ `MenuMonitorHelper.cs` 与多显卡计划的 Task 7.5 冲突（见冲突表）。**必须等多显卡合并后再改这个文件。**

- [ ] **Step 3.6: 窗口标题 / Web 标题 / 更新对话框**

- `src/UI/HardwareInfoForm.cs:26` — `T("LiteMonitor - Hardware Info", "LiteMonitor - 系统硬件详情")` → `T("{{BRAND}} - Hardware Info", "{{BRAND}} - 系统硬件详情")`
- `src/ThemeEditor/ThemeEditorForm.cs:42` — `Text = "LiteMonitor Theme Editor";` → `"{{BRAND}} Theme Editor"`
- `src/System/WebServer/WebPageContent.cs:31` — `<title>LiteMonitor WebServer</title>` → `<title>{{BRAND}} WebServer</title>`
- `src/UI/UpdateDialog.cs:51` — `$"⚡️LiteMonitor - {_context.Title}"` → `$"⚡️{{BRAND}} - {_context.Title}"`（中英两处都在同一行，**两处都要改**）
- `src/UI/UpdateDialog.cs:233` — `http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteMonitor-Updater/1.0")` → `"{{BRAND}}-Updater/1.0"`
- `src/System/UpdateChecker.cs:139` — `VersionLabel = $"⚡️LiteMonitor_v{latest}"` → `$"⚡️{{BRAND}}_v{latest}"`

- [ ] **Step 3.7: 验证界面无残留**

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
git ls-files '*.cs' 'resources/lang/*.json' 'resources/plugins/*.json' 'resources/themes/*.json' | ForEach-Object {
  Select-String -Path $_ -Pattern '"[^"]*LiteMonitor[^"]*"' -ErrorAction SilentlyContinue
} | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
```

**Expected：零命中。**（Task 3 后所有用户可见品牌都应清空；`AboutForm.cs:20-21` 的版权行是**中文/英文文本里含 "LiteMonitor Project"**，会被该模式命中 —— **这是刻意保留的**，需人工确认。）

若要严格自动化，把模式改为排除版权行：

```powershell
... | Where-Object { $_.Line -notmatch 'Diorser' } | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
```

- [ ] **Step 3.8: 提交**

```powershell
git add resources/lang resources/plugins src/UI/AboutForm.cs src/UI/MenuManager.cs `
        src/UI/HardwareInfoForm.cs src/UI/UpdateDialog.cs src/ThemeEditor/ThemeEditorForm.cs `
        src/System/WebServer/WebPageContent.cs src/UI/Helpers/MainFormBizHelper.cs `
        src/UI/Helpers/MenuMonitorHelper.cs src/UI/SettingsForm.cs src/UI/Settings/TaskbarPage.cs `
        src/System/UpdateChecker.cs
git commit -m "feat(rebrand): 用户可见品牌切换为 {{BRAND}}

- 语言包 Title 9 处、About 2 处
- 插件 JSON author 字段 12 处
- 关于窗口：标题、官网、GitHub 链接；版权行改为上游+本方双份
- 托盘提示、菜单、设置引导、任务栏设置消息框
- 硬件详情/主题编辑器/Web 页面/更新对话框标题与 UA
- 刻意保留 AboutForm 中\"基于 LiteMonitor 派生\"的出处声明"
```

---

## Task 4: 功能性品牌点与既有用户环境迁移

**本 Task 全部是"改名字会打断用户环境"的点位，必须成对处理。**

**Files:** 见 File Structure 表 Task 4 行

**Interfaces:**
- Consumes: Task 0.1 的 `{{BRAND}}`
- Produces: exe / 计划任务 / 崩溃日志 / User-Agent / 更新链全部带新品牌，且不影响老用户已有的自启动与防火墙条目

- [ ] **Step 4.1: 计划任务名 + 旧任务清理（成对改）**

`src/System/AutoStart.cs:12`：`private const string TaskName = "LiteMonitor_AutoStart";` → `"{{BRAND}}_AutoStart"`

`src/System/AutoStart.cs:173`：`new XElement(ns + "Description", "LiteMonitor Auto Start")` → `"{{BRAND}} Auto Start"`

`src/System/AutoStart.cs:35`：临时 XML 名 `$"LiteMonitor_Task_{Guid.NewGuid()}.xml"` → `$"{{BRAND}}_Task_{Guid.NewGuid()}.xml"`

**旧任务清理（必须做，否则老用户升级后开机启动两次）**：

已核实 `AutoStart.cs` 的实现机制：
- `:52` — `schtasks.exe /Create /TN "{TaskName}" /XML "{tempXmlPath}" /F`
- `:86` — `schtasks.exe /Delete /TN "{TaskName}" /F`
- `:32` — `Set()` 开头 `if (IsEnabledForCurrentExe(exePath)) return;` **早退**

即：**创建前没有删除同名任务**，且 `Set()` 在 `:32` 就早退了。因此必须在**早退之后、创建之前**插入清理：

1. 加旧名常量：

```csharp
private const string TaskName = "{{BRAND}}_AutoStart";
// 旧版本遗留的计划任务名（品牌迁移前创建），需在首次启用时清理，避免开机启动两次
private const string LegacyTaskName = "LiteMonitor_AutoStart";
```

2. 在 `Set()` 的 `:32` 早退**之后**、`:52` 创建**之前**插入：

```csharp
// 清理旧品牌名遗留的计划任务（仅当旧任务存在时才删；不存在时忽略）
try
{
    var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{LegacyTaskName}\" /F")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    using var p = Process.Start(psi);
    if (p != null) { p.WaitForExit(5000); }
    // 退出码非 0 = 任务不存在，属正常情况，无需提示
}
catch { /* 清理失败不应阻断自启动设置 */ }
```

> ⚠️ 现有代码**从不检查 `schtasks` 的退出码**，也未 `RedirectStandardError`。新代码按"退出码非 0 = 任务不存在"的语义处理，不要把"任务不存在"当错误弹窗。
>
> ⚠️ 这个清理是**一次性**的：清理完成后旧任务已不存在，后续 `schtasks /Delete` 会返回非 0 并被静默忽略 —— 无副作用。

- [ ] **Step 4.2: 防火墙引导文案（成对改，与 exe 名绑定）**

`src/UI/Settings/MainPanelPage.cs:203,205,218,220` 告诉用户在 Windows 防火墙列表里找 `LiteMonitor`：

```
"3. 在列表中找到【LiteMonitor】。\n" +
"   ✅ 左侧名字【LiteMonitor】\n"
"3. Find 'LiteMonitor' in the list.\n" +
"   ✅ Left Name [LiteMonitor]\n"
```

→ 全部换成 `{{BRAND}}`。

> **必须与 exe 文件名严格一致**：Windows 防火墙规则列表显示的是**可执行文件的名字**。exe 改名而文案没改 = 用户按指引找不到自己的程序。

- [ ] **Step 4.3: 单实例互斥体（建议不改）**

`src/System/Program.cs:31,45,49,63` 四处互斥体名。

**默认保持 `LiteMonitor_SingleInstance_*` 不变。** 理由：改了之后，旧版 LiteMonitor 与新版 `{{BRAND}}` 的互斥体不同，**同一台机器上可以同时运行两个实例**，互相抢硬件传感器、抢任务栏位置、抢 TCP 5000 端口（Web 监控默认端口）。

**只有在同时满足以下两条时才改**：
1. 你的版本**不打算与上游 LiteMonitor 共存**于同一台机器（绝大多数情况成立）
2. 你愿意在发布说明里写明"与官方 LiteMonitor 可能同时运行"

改的话：`Program.cs:31,63` 的 `Global\LiteMonitor_SingleInstance_Mutex_UniqueKey` 与 `Program.cs:45,49` 的 `Global\LiteMonitor_SingleInstance_{...}_Mutex` 全部替换为 `{{BRAND}}`。

- [ ] **Step 4.4: 崩溃日志与弹窗标题**

- `src/System/Program.cs:136` — `Path.Combine(AppContext.BaseDirectory, "LiteMonitor_Error.log")` → `"{{BRAND}}_Error.log"`
- `src/System/Program.cs:149` — `"LiteMonitor Crash", MessageBoxButtons.OK, MessageBoxIcon.Error` → `"{{BRAND}} Crash"`

> 老用户的 `LiteMonitor_Error.log` 不会被新版本继续写入，这是可接受的（历史日志留在旧文件里）。

- [ ] **Step 4.5: 主程序集名 / exe 名 / 产品信息**

`LiteMonitor.csproj` 当前**没有显式 `AssemblyName`**，程序集名默认取项目文件名 `LiteMonitor`。改为：

```xml
<PropertyGroup>
  ...
  <AssemblyName>{{BRAND}}</AssemblyName>
  <RootNamespace>LiteMonitor</RootNamespace>
  <Product>{{BRAND}}</Product>
  <Company>{{OWNER}}</Company>
  <Copyright>Copyright © 2026 {{OWNER}}. Based on LiteMonitor by Diorser (MIT).</Copyright>
  <Description>{{BRAND}} — 轻量级桌面硬件监控工具，基于 LiteMonitor 派生</Description>
  ...
</PropertyGroup>
```

> ⚠️ `RootNamespace` **保持 `LiteMonitor` 不动** —— 改了会连带影响 `Properties\Resources.Designer.cs` 里的资源命名空间前缀（`LiteMonitor.Properties.Resources`），而该文件是自动生成的。命名空间整体重命名属于 Task 7。

- [ ] **Step 4.6: Updater 程序集名 + 定位逻辑 + 重新 publish（⚠️ 最易踩坑）**

**这是一个五处联动的陷阱**，任一漏改都会让更新链断裂：

| # | 位置 | 现值 | 新值 |
|---|---|---|---|
| 1 | `LiteMonitor.Updater/LiteMonitor.Updater.csproj:18` | `<AssemblyName>LiteMonitor.Updater</AssemblyName>` | `{{BRAND}}.Updater` |
| 2 | `LiteMonitor.Updater/Program.cs:13` | `private const string ExeName = "LiteMonitor.exe";` | `"{{BRAND}}.exe"` |
| 3 | `LiteMonitor.Updater/Program.cs:32` | 日志文案 `"[Fatal] 找不到 LiteMonitor.exe，更新终止！"` | `"[Fatal] 找不到 {{BRAND}}.exe，更新终止！"` |
| 4 | `LiteMonitor.Updater/Program.cs:86` | `rel.EndsWith("LiteMonitor.Updater.exe", ...)` | `"{{BRAND}}.Updater.exe"` |
| 5 | `LiteMonitor.Updater/Program.cs:189,212,216` | `ContainsLiteMonitorExe(string dir)` | 改名 `ContainsBrandExe`（**或保留方法名，只改内部匹配**） |
| 6 | `LiteMonitor.Updater/LiteMonitor.Updater.csproj:31` | `<StartupObject>LiteMonitor.Updater.Program</StartupObject>` | `{{BRAND}}.Updater.Program` |
| 7 | `src/System/UpdateChecker.cs:356` | `string[] updaterNames = { "Updater", "LiteMonitor.Updater" };` | `{ "Updater", "{{BRAND}}.Updater" }` |
| 8 | `src/System/UpdateChecker.cs:382` | `e.FullName.EndsWith("LiteMonitor.Updater.exe", ...)` | `"{{BRAND}}.Updater.exe"` |
| 9 | `LiteMonitor.csproj:44` | `<Content Include="resources\LiteMonitor.Updater.exe">` | `resources\{{BRAND}}.Updater.exe` |
| 10 | `src/UI/UpdateDialog.cs:381` | `Path.Combine(resourcesDir, "LiteMonitor.Updater.exe")` | `"{{BRAND}}.Updater.exe"` |
| 11 | `src/System/UpdateChecker.cs:387-388` | 兜底 `EndsWith("Updater.exe")` | **不用改**（能匹配 `{{BRAND}}.Updater.exe`） |
| 12 | `resources/LiteMonitor.Updater.exe`（165,996 B 预编译） | — | 重新 publish 产物替换 |

> ⚠️ **`Program.cs:13` 的 `ExeName` 是整个更新链的入口**：Updater 靠它定位主程序目录（`:189` 的 `ContainsLiteMonitorExe` 匹配目录内是否存在该 exe），找不到就在 `:32` 打印 fatal 并终止。**只改 csproj 不改 `Program.cs` → 重建出的 Updater 必然走 fatal 分支，用户点更新后什么都没发生。**
>
> ⚠️ `UpdateChecker.cs:387-388` 有一条 `EndsWith("Updater.exe")` 的兜底，所以 `:382` 漏改**不会**导致"静默失效"—— 但 `:356` 的进程名清理列表会漏掉旧进程名，导致更新时旧 updater 进程不被终止。**两处都要改。**

**重新 publish（不是 build！）**：

`LiteMonitor.Updater.csproj` 有 `<PublishSingleFile>true</PublishSingleFile>` + `<SelfContained>true</SelfContained>` + `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`。`PublishSingleFile` **只在 `dotnet publish` 生效**；`dotnet build` 产出的是 apphost + `.dll` + 运行时文件躺在 `bin\...\win-x64\`，**不是可独立运行的单文件**。

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
& $dotnet publish 'LiteMonitor.Updater\LiteMonitor.Updater.csproj' -c Release -r win-x64 --self-contained false
Copy-Item 'LiteMonitor.Updater\bin\Release\net8.0-windows\win-x64\publish\{{BRAND}}.Updater.exe' `
          'resources\{{BRAND}}.Updater.exe' -Force
Get-Item 'resources\{{BRAND}}.Updater.exe' | Select-Object Name, Length
```

> ⚠️ `--self-contained false` 与现存的 `resources/LiteMonitor.Updater.exe`（165,996 B、**无 `DOTNETBUNDLE` 标记**、依赖 `Microsoft.NETCore.App`）一致 —— 即**框架依赖**单文件。发布时目标机器需有 .NET 8 Desktop Runtime（`使用说明` 已声明此要求）。
>
> 若改用 `--self-contained true`，产物约 60+ MB 且自带运行时，**与上游的发布形态不一致**，需同步更新 `使用说明` 的运行时说明。
>
> 同时更新 `LiteMonitor.Updater/LiteMonitor.Updater.csproj:21-23`：

```xml
<Product>{{BRAND}} Auto Updater</Product>
<Description>Automatic update component for the {{BRAND}} system monitoring tool.</Description>
<Copyright>Copyright © 2026 {{OWNER}}</Copyright>
```

- [ ] **Step 4.7: FPS 组件名（预编译二进制，只能改名）**

**四类点位，缺一不可**：

| # | 位置 | 用途 | 不改的后果 |
|---|---|---|---|
| 1 | `resources/assets/LiteMonitorFPS.exe`（369 KB） | 仓库内的组件本体 | 打包时找不到 |
| 2 | `src/System/HardwareServices/DriverInstaller.cs:40-42` | 下载 URL | 运行时下载 404 |
| 3 | `src/System/HardwareServices/DriverInstaller.cs:122,199` | 本地落地路径 / 目标路径 | 组件装到别的位置 |
| 4 | `src/System/HardwareServices/DriverInstaller.cs:279,294` | **ZIP 内条目名匹配 + IOException 捕获条件** | ⚠️ **组件被解压到错误目录或不落盘** |
| 5 | `src/System/HardwareServices/FpsCounter.cs:88` | `ExePath` 常量 | 找不到组件 |
| 6 | `src/System/HardwareServices/FpsCounter.cs:67` | 进程名优先级表 | 进程识别失败 |
| 7 | `src/System/HardwareServices/FpsCounter.cs:550` | `Process.GetProcessesByName("LiteMonitorFPS")` 僵尸清理 | ⚠️ **僵尸进程不被杀，退出时残留** |

操作：

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'G:\TOOL\LiteMonitor-fork'
Copy-Item 'resources\assets\LiteMonitorFPS.exe' "resources\assets\{{BRAND}}FPS.exe" -Force
```

然后逐条替换上表中的字面量（`LiteMonitorFPS.exe` → `{{BRAND}}FPS.exe`，`"LiteMonitorFPS"` → `"{{BRAND}}FPS"`）。

> ⚠️ **已核实：`LiteMonitorFPS.exe` 无源码**（全仓只有 `FpsCounter.cs` 与该 exe 本身），**无法重新构建，只能改名**。这与 Updater 不同（Updater 有 `LiteMonitor.Updater/Program.cs`，可以 publish）。
>
> ⚠️ **改名前先确认二进制内是否硬编码自身文件名**（`FpsCounter` 可能用路径或进程名定位自己）。检查方法：把副本改名后直接运行 `resources\assets\{{BRAND}}FPS.exe --help`（或不带参数），看是否报错/输出异常；也可用 `strings` 工具扫。若发现硬编码，**保留原名 `LiteMonitorFPS.exe` 不动**，只改 URL 与本地路径引用逻辑。
>
> ⚠️ 改名后**必须重新计算 Task 2.6 的 SHA256**（新文件名不影响内容哈希，但 Task 2.6 的常量要填新文件的哈希值）。

- [ ] **Step 4.8: User-Agent**

- `src/Plugins/PluginExecutor.cs:66,451` — `"LiteMonitor/1.0"` → `"{{BRAND}}/1.0"`
- `src/Plugins/Native/CryptoNative.cs:55` — 同上
- `src/Plugins/Native/CityCodeResolver.cs:151` — 同上

- [ ] **Step 4.9: 编译验证**

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
& $dotnet build 'LiteMonitor.csproj' -c Release
```

Expected: `生成成功`，0 错误。输出 `bin\Release\net8.0-windows\{{BRAND}}.exe`。

- [ ] **Step 4.10: 提交**

```powershell
git add LiteMonitor.csproj LiteMonitor.Updater resources/ src/System/AutoStart.cs `
        src/System/Program.cs src/System/UpdateChecker.cs src/UI/UpdateDialog.cs `
        src/UI/Settings/MainPanelPage.cs src/System/HardwareServices/DriverInstaller.cs `
        src/System/HardwareServices/FpsCounter.cs src/Plugins/PluginExecutor.cs `
        src/Plugins/Native/CryptoNative.cs src/Plugins/Native/CityCodeResolver.cs
git commit -m "feat(rebrand): 功能性品牌点迁移

- 计划任务名换为 {{BRAND}}_AutoStart，补旧任务 LiteMonitor_AutoStart 清理逻辑
- 防火墙引导文案与 exe 名对齐
- 主程序集/产品信息、崩溃日志名、User-Agent 换为自有品牌
- Updater 程序集、定位主 exe 逻辑、进程名清理列表全部同步；
  改用 dotnet publish 产出框架依赖单文件
- FPS 组件改名为 {{BRAND}}FPS.exe，同步落地路径、ZIP 条目匹配、进程名与僵尸清理
- 单实例互斥体保持不变（避免与上游版本双开）"
```

---

## Task 5: 文档与声明修正

**Files:**
- Modify: `使用说明(必读).txt`
- Modify: `resources/plugins/PLUGIN_DEV_GUIDE.md`
- Modify: `src/System/HardwareServices/HardwareReadme.md`
- Modify: `src/UI/Settings/SETTINGS_README_DEV.md`

**Interfaces:**
- Consumes: Task 1~4 的成果
- Produces: 所有对外文档与代码事实一致，声明强度与实际能力匹配

- [ ] **Step 5.1: `使用说明(必读).txt` 修正**

**① 品牌段（第 1~4 行）**

```
=========================================================
         					LiteMonitor 使用说明
					     [ 开源 | 免费 | 安全 | 透明 ]
=========================================================
```

→ 换品牌名，并把"安全"改为与实际一致的措辞（见 ②）。

**② 「关于本项目」段的失实声明**

当前 `使用说明(必读).txt:53-57`：

```
[4] 关于本项目 (Open Source)
LiteMonitor 是一款代码完全开源的免费软件。
我们承诺代码公开透明，绝无任何恶意后门或隐私窃取行为。
因涉及底层硬件数据读取，部分杀毒软件可能会拦截此软件。
```

**问题**：本项目包含**插件市场**（`src/Plugins/PluginMarket.cs`），会从远程索引下载并**在本地进程内执行**第三方 JSON 脚本（`PluginExecutor`）。"绝无任何恶意后门"在有远程代码安装通道的前提下是**过强且不可完全兑现的承诺**。这不是许可证问题（MIT 不管这个），但改名后这个承诺是**你在作的**。

改为：

```
[4] 关于本项目 (Open Source)
{{BRAND}} 是一款基于开源项目 LiteMonitor (原作者 Diorser, MIT 协议) 派生修改的
免费软件，源码完全公开。

安全说明：
- 程序本体不包含任何后门或隐私窃取逻辑。
- 但本程序的【插件系统】支持从远程源下载并在本地执行第三方脚本。
  安装插件即等同于执行该脚本作者提供的代码，请只安装可信来源的插件。
- 因涉及底层硬件数据读取，部分杀毒软件可能会拦截本程序。
  如果运行或设置开机启动时出现拦截，请将程序目录添加至杀软信任/白名单。
```

**③ 多显卡描述失实**

当前 `使用说明(必读).txt:66-68`：

```
* 多显卡监控增强版 (本 Fork):
  https://github.com/mojingjing-git/LiteMonitor
  支持同时监控多张显卡，详见设置 -> 系统与硬件 -> 监控显卡数量。
```

**已核实**：截至本计划执行时，代码**只有单卡 `PreferredGpu` 选择**（`src/Core/Settings.cs:36`；`src/UI/Settings/SystemHardwarPage.cs:173-176` 有且仅有一个 `_cbGpu` 下拉），`src/System/HardwareServices/SensorMap.cs:103-119` 明确**只映射一张卡**（其余 GPU 硬件被 `return` 跳过）。`MonitoredGpuCount` 在 `src/Core/Settings.cs` 中**不存在**。字符串「监控显卡数量」全仓**仅**出现在 `使用说明(必读).txt`（及其 `bin/` 副本）与多显卡计划文档中，C#/JSON 零命中。

因此本 Step 按多显卡计划的实际落地状态二选一：
- **多显卡计划尚未合并** → 改为"支持指定要监控的显卡（单选）"
- **多显卡计划已合并** → 保留"支持同时监控多张显卡"，并把设置路径写成与实际实现一致的措辞

同时把 fork 链接换成自己的仓库：

```
* 本项目主页 & 源码查看:
  https://github.com/{{REPO}}

* 上游项目（原作者 Diorser, MIT 协议）:
  https://github.com/Diorser/LiteMonitor
```

**④ 其余上游链接与品牌**

| 行 | 内容 | 处理 |
|---|---|---|
| 24 | `gitee.com/Diorser/LiteMonitor/raw/master/resources/assets/PawnIO_setup.exe` | 改为自己的源（`{{REPO}}`） |
| 39 | `LiteMonitor_Error.log（崩溃错误日志）` | 改为 `{{BRAND}}_Error.log`（与 Task 4.4 一致） |
| 60-61 | `项目主页 & 源码查看: github.com/Diorser/LiteMonitor` | 改为 `github.com/{{REPO}}` |
| 63-64 | `问题反馈: github.com/Diorser/LiteMonitor/issues` | 保留为"上游问题反馈"或删除（避免用户把 bug 提给原作者） |

- [ ] **Step 5.2: 插件开发指南**

`resources/plugins/PLUGIN_DEV_GUIDE.md` 中 7 处 "LiteMonitor"：泛指本软件的表述改为 `{{BRAND}}`；指向上游仓库的链接改为自有仓库；**保留一处"本插件系统最初由 LiteMonitor 项目设计"的历史说明**。

- [ ] **Step 5.3: 内部文档**

- `src/System/HardwareServices/HardwareReadme.md` — 标题/说明中的 "LiteMonitor" 改为 `{{BRAND}}`；末尾"文档生成时间"更新
- `src/UI/Settings/SETTINGS_README_DEV.md` — 同上

- [ ] **Step 5.4: 提交**

```powershell
git add '使用说明(必读).txt' resources/plugins/PLUGIN_DEV_GUIDE.md `
        src/System/HardwareServices/HardwareReadme.md src/UI/Settings/SETTINGS_README_DEV.md
git commit -m "docs(rebrand): 修正文档中的品牌、失实描述与过强安全声明

- 使用说明：品牌、主页链接、PawnIO 下载源、崩溃日志名、致谢上游
- 使用说明：\"绝无任何恶意后门\"改为与插件系统实际能力匹配的措辞
- 使用说明：多显卡描述按多显卡计划实际落地状态修正
- 插件开发指南与内部文档品牌同步"
```

---

## Task 6: 工程卫生与发布打包

**Files:**
- Modify: `.gitignore`（**校验并补充**，不重建）
- Modify: `LiteMonitor.csproj`（版本号）
- Modify: `resources/assets/app.ico`（图标）
- Create: `scripts/pack-release.ps1`
- 清理: `bin/`、`obj/`（不入库）

**Interfaces:**
- Consumes: Task 0.1 的 `{{VERSION}}`
- Produces: 版本号与上游不冲突、构建产物不入库、图标为自有品牌、**发布包可复现**

- [x] **Step 6.1: 校验 `.gitignore`**

> **实测结果**：`.gitignore` 已存在（多显卡 Task 1 创建），`bin/` 与 `obj/` 两条规则均命中。
> 补充了 `.review-scratch/`（评审临时产物）与 `dist/`（发布包输出目录）。

多显卡计划 Task 1 已创建 `.gitignore`。**本 Task 只校验，不重建**：

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
if (Test-Path '.gitignore') { Get-Content '.gitignore' } else { Write-Output 'MISSING - 需补建' }
git check-ignore -v bin obj
```

Expected: 两条都命中 `.gitignore` 的规则。若缺失，补上：

```gitignore
# 构建产物
bin/
obj/
*.user
*.suo

# 运行期数据
settings.json
settings.json.bak
HardwareHistory.json
HardwareHistory.json.bak
TrafficHistory.json
TrafficHistory.json.bak
*.log

# 评审/临时产物
.review-scratch/
```

- [x] **Step 6.2: 清理陈旧第三方 DLL**

> ⚠️ **本步原结论已被证伪，执行时已更正（2026-10-20）**。
> 原计划判断 `bin/Release/net8.0-windows/` 下的 `BlackSharp.Core.dll` / `DiskInfoToolkit.dll` /
> `HidSharp.dll` / `RAMSPDToolkit-NDD.dll` / `System.IO.Ports.dll` 是「源码零引用的陈旧残留」。
> **实际核查 `LiteMonitor++.deps.json` 的依赖图后确认：它们是 LibreHardwareMonitorLib 0.9.6 的
> 真实传递依赖** —— LHM 依赖 `DiskInfoToolkit` / `HidSharp` / `Mono.Posix.NETStandard` /
> `RAMSPDToolkit-NDD` / `System.IO.Ports` / `System.Management` / `System.Threading.AccessControl`，
> 而 `DiskInfoToolkit` 与 `RAMSPDToolkit-NDD` 又依赖 `BlackSharp.Core`。
> 源码里搜不到这些名字只是因为它们是被反射/延迟加载的传感器后端（磁盘信息、SPD 内存传感器等）。
> **结论：这些 DLL 必须随包分发，不可删除、也不可移出发布包。**

原计划中唯一仍然成立、且必须执行的部分：

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
# bin/ 与 obj/ 已被 .gitignore 忽略，删除不影响 git 历史
```

`bin/` 下确实混有**上游 2026-01 的旧发布产物**（`bin/Release/net8.0-windows/win-x64/` 整目录，
里面还是 `LiteMonitor.exe` 旧名与旧 `LiteMonitor.Updater.exe`），必须清掉，
否则 `dotnet publish` 的输出目录会被误认为可用产物。删除后重新构建即可。


- [x] **Step 6.3: 版本号策略**

> **实测结果**：版本号在 Task 4 改程序集名时已顺手改为 `1.0.0` / `1.0.0.0`，
> `resources/version.json` 的 `version` 同为 `1.0.0`，`changelog` 与 `releaseDate`
> 已改写为本项目首个自有版本的说明。三处一致，无需再改。

当前 `LiteMonitor.csproj:15,17` 是 `1.3.6` / `1.3.6.0`，`resources/version.json:2` 是 `"1.3.6"` —— **与上游完全同号**。

**问题**：
1. 同一台机器上装了上游 1.3.6 和你的 1.3.6 → 版本号无法区分
2. `UpdateChecker` 弹窗显示 "LiteMonitor_v1.3.6" 会让人以为是官方版本
3. 将来同步上游时版本号会冲突

**方案**：用**独立号段**，建议 `{{VERSION}}` = `1.0.0`（从 1.0.0 重新起算），`AssemblyVersion` 保持 `1.0.0.0` 不变（上游也是这么做的）。

三处必须同步：

| 位置 | 字段 | 值 |
|---|---|---|
| `LiteMonitor.csproj:15` | `<Version>` | `{{VERSION}}` |
| `LiteMonitor.csproj:17` | `<FileVersion>` | `{{VERSION}}.0` |
| `resources/version.json:2` | `"version"` | `"{{VERSION}}"` |

> `resources/version.json` 的 `changelog` / `releaseDate` 也要改成你自己的首个版本说明（当前是上游 1.3.6 的日志，会误导用户）。

- [x] **Step 6.4: 确认发布包命名与 Task 2.2 一致**

> **实测结果**：发布包名 `LiteMonitorPlus_v{Version}-win-x64.zip`，
> 与 `UpdateChecker.cs` 的下载模板 `LiteMonitorPlus_v{0}-win-x64.zip` 一致。
> `scripts/pack-release.ps1` 启动时会自检该一致性，不一致直接中止打包。

Task 2.2 的下载 URL 模板是 `{{BRAND}}_v{0}-win-x64.zip`。**发布包的实际文件名必须严格匹配**，否则更新会下载到一个不存在的文件。

- [x] **Step 6.5: 写发布打包脚本（当前完全靠手工）**

**已核实的现状**：`resources/assets/` **不会**被 csproj 复制到输出目录。csproj 的 `Content` 有 6 项（`themes/` `lang/` `plugins/` `resources\assets\CityCode.json` `resources\LiteMonitor++.Updater.exe` `使用说明`）。

**这意味着**：`driver.zip`、`PawnIO_setup.exe`、`LiteMonitorPlusFPS.exe`、`app.ico` 和 7 个 PNG **不在构建产物里**。上游的发布 ZIP 是**手工把 `assets/` 平铺进 `resources/`** 拼出来的。

**实际实现**：`scripts/pack-release.ps1`（已落地，与本计划原先的草稿有出入，以脚本为准）。要点：

1. **自检一致性**：启动时校验 `UpdateChecker.cs` 的下载模板、`LiteMonitor.csproj` 的 `<Version>`、`resources/version.json` 的 `version` 三者与 `-Version` 参数一致，不一致直接中止 —— 包名对不上会导致「检查更新」下载到不存在的文件。
2. **先发 Updater**：发布 `LiteMonitor.Updater`（单文件 / 框架依赖）并刷新仓库内 `resources\LiteMonitor++.Updater.exe`，再发布主程序 —— 因为主程序的 `Content` 引用的是仓库里那份 Updater，顺序反了会把旧版打进包。
3. **`dotnet publish` 而非 `build`**：主程序 csproj 没有 `PublishSingleFile`，`dotnet publish -r win-x64 --self-contained false` 产出的是框架依赖多文件形态（与上游发布包一致，2.8 MB 级）。
4. **补 `CopyToPublishDirectory`**：`resources\LiteMonitor++.Updater.exe` 与 `使用说明(必读).txt` 原先只配了 `CopyToOutputDirectory`，**`publish` 时不会被复制**，已补上。
5. **只平铺运行时需要的资源**：`driver.zip` / `LiteMonitorPlusFPS.exe` / `CityCode.json` → `resources/`。`app.ico` 与 7 个 PNG 是**编译期嵌入资源**（`Properties.Resources.*`，resx 以 `ResXFileRef` 引用 `resources/assets/`），运行时不需要从磁盘读，**不打进发布包**。
6. **删除 `resources/assets/`**：csproj 会把 `CityCode.json` 复制到 `resources/assets/`，与平铺副本重复，删掉以免包内冗余。
7. **打上 LICENSE / NOTICE**：MIT 分发义务，每个分发副本都必须带许可声明。
8. 产物：`dist/LiteMonitorPlus_v{Version}-win-x64.zip`，脚本末尾输出包 SHA256。

**实测产物**：`LiteMonitorPlus_v1.0.0-win-x64.zip`，6.18 MB，58 个条目。

> ⚠️ **MIT 的分发义务在第 3 步**：`LICENSE`（含 `Copyright (c) 2025 Diorser`）必须出现在**每一个分发副本**中。上游的发布 ZIP 里没有 LICENSE（因为上游仓库里就没有），**你的发布包必须补上**。这是最容易漏、后果最严重的一步。
>
> ⚠️ 若 Task 2.7 采用"城市库本地化"方案，需在第 2 步把 `CityCode.json` 放进 `resources/assets/`（它现在不在仓库里，需要从上游下载一次并纳入版本管理）。

- [x] **Step 6.6: 图标**

`resources/assets/app.ico`（31,971 字节）是上游图标（黄闪电 + 薄荷绿圆底）。

**商标要点**：MIT **不授予商标许可**，沿用上游图标 + 改名分发，容易让用户误认为是官方版本。

**已选 A（自绘新图标）并已落地**：

- 7 个 PNG 已逐一核查：`CleanMem.png` / `HardwareInfo.png` / `MonitorHistory.png` / `NetworkTest.png` / `Settings.png` / `ThemeIcon.png` / `Traffic.png` 均为**无品牌信息的单色功能图标**（齿轮、扫帚等），**不含上游 Logo，无需替换**。
- 这 7 个 PNG 与 `app.ico` 本身是**编译期嵌入资源**：`Properties/Resources.resx` 以 `ResXFileRef` 引用 `..\resources\assets\*`，运行时不从磁盘读取。因此它们**必须留在仓库**（构建需要），但**不必打进发布包**。
- 新图标由 `scripts/make-icon.ps1` **纯代码生成**（GDI+ 绘制 + 自写多帧 ICO 编码器），不依赖任何外部素材：
  - 圆角方形深色底板（slate-800 → 近黑蓝垂直渐变）
  - 青色闪电（性能监控语义），线性渐变填充
  - 右下角琥珀色「++」品牌后缀
- 「++」的位置不是随手摆的：脚本按**「到闪电的垂线距离 == 到圆角边界的垂线距离」**这一平衡条件解出坐标（`t = (R - b) / (k + √2)`，R=58），并把推导写进脚本注释，改动闪电或加号尺寸时位置会自动重新求解。
- 输出为 **7 帧 ICO**（16/24/32/48/64/128/256），每帧为 32 位未压缩 DIB —— 兼容性优于 PNG 压缩帧。
- 32 px 以下「++」不可辨识，小尺寸帧自动改用无后缀的简化构图。
- `LiteMonitor.csproj:10` 的 `<ApplicationIcon>resources\assets\app.ico</ApplicationIcon>` 路径不变。
- **实测**：新图标已随 `LiteMonitor++.exe` 正确嵌入（`ExtractIconEx` 可提取 16/32 两档，图案与设计一致），Web 面板 favicon 走 `Icon.ExtractAssociatedIcon(Application.ExecutablePath)`，自动取到新图标。

> 代价：ico 由 31 KB 变为 372 KB，exe 由 187 KB 变为 528 KB，dll 由 840 KB 变为 1.5 MB。
> 对框架依赖发布包（6.18 MB）而言可接受，换来的是彻底摆脱「像官方版」的观感。

- [x] **Step 6.7: 提交**

实际拆成 **两个提交**（原计划写的是一个，但本步同时修掉了一个重命名遗留的实质性 bug，
不宜混在 `chore` 里）：

```powershell
# 提交 1：修 bug
git add src/UI/AboutForm.cs src/UI/UpdateDialog.cs src/System/HardwareServices/DriverInstaller.cs `
          resources/assets/driver.zip NOTICE
git commit -F <msg1>

# 提交 2：工程卫生与打包
git add .gitignore LiteMonitor.csproj resources/assets/app.ico resources/LiteMonitor++.Updater.exe `
          scripts/ docs/superpowers/plans/2026-10-20-rebrand-compliance.md
git commit -F <msg2>
```

> ⚠️ **禁止 `git add -A`** —— 会把其他会话的未跟踪文件一并提交。

---

## Task 7: 可选 — 命名空间与程序集彻底重命名（延后，默认不执行）

**为什么默认不做**：
- 89/89 个 `.cs` 文件全部使用 `namespace LiteMonitor*`（15 个 namespace 变体），改动量等于全仓库重写
- 改动后**永远无法与上游 merge**，每次同步上游都要手工解冲突
- **零用户可见收益、零法务收益** —— MIT 允许保留原命名空间；版权声明在 `LICENSE` / `NOTICE` / `AboutForm` / csproj `<Copyright>` 四处，不在命名空间里
- 唯一可见效果是崩溃堆栈里显示 `LiteMonitor.src.Core.MetricUtils` 而非 `{{BRAND}}.src.Core.MetricUtils`

**什么时候该做**：你决定彻底放弃与上游同步、且希望二进制里不留任何 "LiteMonitor" 字符串时。

**执行时机**：**必须在多显卡计划完全合并进 master 之后单独执行**，否则多显卡计划里 2,400+ 行的 `path:line` 引用全部失效。

- [ ] **Step 7.1: 机械替换命名空间**

对 89 个 `.cs` 文件执行：

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'G:\TOOL\LiteMonitor-fork'
$rx = '(?m)^(\s*(?:namespace|using(?:\s+static)?)\s+)LiteMonitor\b'
$files = git ls-files '*.cs'
foreach ($f in $files) {
  $c = Get-Content $f -Raw
  $n = $c -replace $rx, '${1}{{BRAND}}'
  if ($n -cne $c) { Set-Content -Path $f -Value $n -NoNewline -Encoding UTF8 }
}
```

> ⚠️ **正则的 `\s+` 必须在分组外、分组之后**（`(?:namespace|using(?:\s+static)?)\s+`），不能塞进 `using` 分支里。
>
> 已实测两种写法的差异：
> - ❌ `(?m)^(\s*(?:namespace|using(?:\s+static)?\s+))LiteMonitor\b` —— `using` 分支带了 `\s+`、但 `namespace` 分支没带 → **`namespace` 声明行全部漏改**（`namespace LiteMonitor.src.Core` 保持原样），编译时新旧命名空间混用，直接失败
> - ✅ `(?m)^(\s*(?:namespace|using(?:\s+static)?)\s+)LiteMonitor\b` —— 三种形态全覆盖
>
> ⚠️ **必须含 `using(?:\s+static)?`**。若写成 `(?m)^(\s*(?:namespace|using)\s+)LiteMonitor\b`（`using` 后必须紧跟 `LiteMonitor`），会**漏掉 5 处 `using static`**：
> - `src/UI/Helpers/TaskbarBizHelper.cs:6`
> - `src/UI/Helpers/TaskbarStrategyWin10.cs:4`
> - `src/UI/Helpers/TaskbarStrategyWin11.cs:4`
> - `src/UI/Helpers/TaskbarWinHelper.cs:9`
> - `src/UI/TaskbarForm.cs:7`
>
> 漏改这 5 处 → 编译直接失败（`using static {{旧名}}...` 找不到类型）。
>
> ⚠️ 正则**不会**碰到 `using LibreHardwareMonitor.Hardware;`（`using` 后是 `LibreHardwareMonitor`，`LiteMonitor\b` 在该位置不匹配）—— 但这依赖 `\b` 的正确性，**改完必须跑下面的干跑校验 + Task 7.3 的编译验证**。

**干跑校验（改盘前先跑，确认命中符合预期）**：

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'G:\TOOL\LiteMonitor-fork'
$rx = '(?m)^(\s*(?:namespace|using(?:\s+static)?)\s+)LiteMonitor\b'
$hits = 0; $bad = 0
foreach ($f in (git ls-files '*.cs')) {
  $c = Get-Content $f -Raw
  $n = $c -replace $rx, '${1}{{BRAND}}'
  # 命中数
  $hits += ([regex]::Matches($c, $rx)).Count
  # 残留检查：替换后仍不该有行首 namespace/using 指向 LiteMonitor
  $bad += ([regex]::Matches($n, '(?m)^\s*(?:namespace|using(?:\s+static)?\s+)LiteMonitor\b')).Count
  # 第三方命名空间必须原样保留
  if ($n -match 'using LibreHardwareMonitor') {
    if ($c -notmatch 'using LibreHardwareMonitor') { Write-Output "DANGER: 第三方命名空间被破坏 -> $f" }
  }
}
Write-Output "命中 $hits 处；替换后残留 $bad 处（必须为 0）"
```

Expected: `命中 N 处；替换后残留 0 处（必须为 0）`，且无 `DANGER` 输出。
>
> ⚠️ 正则**也不匹配**非行首的全限定名。已知 3 处需**手工**替换：
> - `src/UI/MenuManager.cs:454` — `new LiteMonitor.src.UI.SettingsForm(...)`
> - `src/UI/Helpers/MenuMonitorHelper.cs:28` — `new LiteMonitor.src.UI.SettingsForm(...)`
> - `src/UI/Helpers/MenuMonitorHelper.cs:58` — `new LiteMonitor.src.UI.SettingsForm(...)`

- [ ] **Step 7.2: 同步改 csproj / 资源 / 测试工程**

- `LiteMonitor.csproj` — `<RootNamespace>LiteMonitor</RootNamespace>` → `{{BRAND}}`
- `Properties/Resources.Designer.cs` — 命名空间前缀 `LiteMonitor.Properties` → `{{BRAND}}.Properties`
  > ⚠️ **改完 RootNamespace 后要重新生成** `Resources.Designer.cs`（Visual Studio 中对 `Resources.resx` 执行"重新生成自定义工具"），让它与新的 RootNamespace 一致。
  > **风险反过来**：如果**不**重新生成，`Resources.Designer.cs` 里的 `"LiteMonitor.Properties.Resources"` 资源名会与清单中的资源名失配 → **运行期 `MissingManifestResourceException`**。改完务必重新生成并确认 `Resources.Designer.cs` 里的资源名前缀已更新。
- `LiteMonitor.Updater/LiteMonitor.Updater.csproj:31` — `<StartupObject>LiteMonitor.Updater.Program</StartupObject>` → `{{BRAND}}.Updater.Program`
- `LiteMonitor.Updater/Program.cs` — `namespace LiteMonitor.Updater` → `namespace {{BRAND}}.Updater`
- `tests/LiteMonitor.Tests/` — `namespace LiteMonitor.Tests` 与 `<RootNamespace>LiteMonitor.Tests</RootNamespace>` 同步
- `LiteMonitor.sln` — 项目文件名保持 `LiteMonitor.csproj`（重命名项目文件会带来大量路径变更，收益极低，**建议不改**）

- [ ] **Step 7.3: 编译 + 测试**

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
& $dotnet build 'LiteMonitor.csproj' -c Release
& $dotnet build 'LiteMonitor.Updater\LiteMonitor.Updater.csproj' -c Release
& $dotnet test 'tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误；测试全通过。

- [ ] **Step 7.4: 提交**

```powershell
git add -A
git commit -m "refactor(rebrand): 命名空间根 LiteMonitor → {{BRAND}}

仅改 namespace/using/using static 声明行，不触碰 LibreHardwareMonitor 第三方命名空间。
与上游的 merge 能力因此放弃，需知悉。"
```

---

## Task 8: 编译与真机验证

**Files:** 无（只验证）
**Interfaces:** Consumes: Task 1~7 全部成果；Produces: 可发布结论

- [x] **Step 8.1: 干净构建**

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
Remove-Item -Recurse -Force bin, obj -ErrorAction SilentlyContinue
& $dotnet build 'LiteMonitor.csproj' -c Release
```

**实测结果**：**0 个错误，232 个警告**。

> ⚠️ 原计划写的「0 错误 0 警告」不成立：232 个 `CS8602 / CS8600 / CS8618` 等 nullable
> 警告是**上游遗留**（全仓 89 个 `.cs` 从未开过严格可空检查），与本次重品牌改动无关，
> 改动前后数量一致。修它们属于另一条独立的工作线，不在本计划范围。

- [x] **Step 8.2: 确认产物无陈旧残留**

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
Get-ChildItem 'bin\Release\net8.0-windows' -File | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}}
```

**实测结果**：主 exe 为 `LiteMonitor++.exe`（515.5 KB，含自制多帧图标）。

> ⚠️ 原计划写的「**不存在** `DiskInfoToolkit.dll` / `HidSharp.dll` / `BlackSharp.Core.dll` / `RAMSPDToolkit-NDD.dll`」
> **是错的**（与 Step 6.2 同一处误判）：它们是 LibreHardwareMonitorLib 0.9.6 的真实传递依赖，
> **必须随包分发**。真正需要清掉的是 `bin/Release/net8.0-windows/win-x64/` —— 上游 2026-01
> 的旧发布产物（里面还是 `LiteMonitor.exe` 旧名），已随 `bin/`、`obj/` 一并删除。
>
> 顺带说明：这些 DLL 在**源码里搜不到名字**，是因为它们是被延迟加载/反射调用的传感器后端
> （磁盘信息、SPD 内存传感器等），不代表没用。

- [x] **Step 8.3: 关键字符串审计**

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
# exe 是 UTF-16 字符串表
Select-String -Path 'bin\Release\net8.0-windows\{{BRAND}}.exe' -Pattern 'litemonitor\.cn|Diorser' -Encoding Unicode -ErrorAction SilentlyContinue |
  Select-Object -First 10 | ForEach-Object { $_.Line.Trim() }
```

**实测结果（exe 与 dll 同时扫，UTF-16 + UTF-8 双编码）**：

| 模式 | exe | dll | 判定 |
|---|---|---|---|
| `gitee.com/Diorser` | 干净 | 干净 | ✅ 上游更新源已解除 |
| `github.com/Diorser` | 干净 | 干净 | ✅ 上游下载源已解除 |
| `raw.githubusercontent.com/Diorser` | 干净 | 干净 | ✅ 上游资源源已解除 |
| `litemonitor.cn` | 干净 | 命中 `: litemonitor.cn` | ⚠️ **按要求保留** |
| `Diorser` | 命中版权串 | 命中版权串 | ⚠️ **按要求保留** |

> ⚠️ 原计划写的「无 `litemonitor.cn` / `Diorser` 命中」**与合规要求冲突**：
> 派生的「关于」窗与 LICENSE/NOTICE **必须**保留上游署名与上游链接（否则违反 MIT 的
> 版权声明保留义务，也无法让用户看清这是派生版）。真正要保证的是
> **不存在指向上游的网络端点**（更新源 / 驱动源 / 城市库 / 行情接口），
> 上表前三行才是本步的判据。
>
> dll 中 `litemonitor.cn` 的唯一出处是 `AboutForm.cs` 的「上游项目: litemonitor.cn」致谢链接
> （`strWebPrefix` 来自语言包，Task 3 已把「官网」改为「上游项目」）。

- [ ] **Step 8.4: 语言包与插件 JSON 合法性**

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
Get-ChildItem 'resources\lang\*.json','resources\plugins\*.json' | ForEach-Object {
  try { Get-Content $_.FullName -Raw | ConvertFrom-Json | Out-Null; "OK   $($_.Name)" }
  catch { "FAIL $($_.Name): $($_.Exception.Message)" }
}
```

Expected: 9 个语言包 + 12 个插件 JSON 全部 `OK`。（多显卡 Task 13 与本计划 Task 3.1/3.2 都改过这些文件，这一步是防 JSON 结构被改坏的守门。）

- [ ] **Step 8.5: 发布包完整性检查**

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'G:\TOOL\LiteMonitor-fork'
pwsh -File scripts/pack-release.ps1 -Version '{{VERSION}}'
$zip = Get-ChildItem 'dist\*.zip' | Select-Object -First 1
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
$names = $z.Entries | ForEach-Object { $_.FullName }
$z.Dispose()
$names | Where-Object { $_ -match 'LICENSE|NOTICE|driver\.zip|FPS\.exe|CityCode\.json|\.exe$' }
```

**Expected（逐条核对，缺一即发布失败）**：
- `LICENSE` **必须在**（MIT 强制义务）
- `NOTICE` 在
- `resources/driver.zip` 在
- `resources/{{BRAND}}FPS.exe` 在
- `resources/CityCode.json` 在（若采用本地化方案）
- 主程序 `{{BRAND}}.exe` 在

- [x] **Step 8.6: 真机功能验证清单**

**已自动化验证的部分**（在**发布包**上跑，不是构建产物 —— 构建产物缺 `resources/assets/`）：

| # | 验证项 | 期望 | 结果 |
|---|---|---|---|
| 1 | 启动 | 进程名 = `LiteMonitor++` | ✅ 任务管理器进程名实测 `LiteMonitor++`；`EnumWindows` 枚举到 1 个可见主窗 + 3 个隐藏窗，**无模态弹窗阻塞**（透明无边框窗体，故 `MainWindowTitle` 为空属正常） |
| 2 | 单实例 | 第二个实例立即退出 | ✅ 第二个实例 4 秒内自行退出，ExitCode=0 |
| 3 | 关于窗口 | 双份版权 + 指向自有仓库 | ✅ 源码确认：`© 2025 Diorser / LiteMonitor Project` + `© 2026 mojingjing-git / LiteMonitor++ Project` + `github.com/mojingjing-git/LiteMonitorPlus` |
| 4 | 崩溃日志 | `LiteMonitor++_Error.log` | ✅ 路径常量确认（`Program.cs:136`）；实测运行 15 秒无异常、未生成日志 |
| 5 | 检查更新 | 无上游域名 | ✅ 二进制审计：`gitee.com/Diorser` / `github.com/Diorser` / `raw.githubusercontent.com/Diorser` 全部零命中 |
| 9 | 防火墙引导 | 文案名称与 exe 一致 | ✅ `MainPanelPage.cs` 中英文各 2 处均为 `LiteMonitor++` |
| 10 | Web 监控 | 页面标题 = `LiteMonitor++ WebServer` | ✅ **端到端实测**：在包内 `settings.json` 打开 `WebServerEnabled`，`http://localhost:5000` 返回 200，`<title>` = `LiteMonitor++ WebServer`（默认该项为 `false`，所以不开时端口不监听属预期） |
| 11 | 主题编辑器 | `LiteMonitor++ Theme Editor` | ✅ `ThemeEditorForm.cs:42` 确认 |
| 12 | 插件 | 12 个插件 + 城市库离线可用 | ✅ 12 个插件 JSON 全部合法且**无上游域名残留**；`resources/CityCode.json` 已在包内，`CityCodeResolver` 优先读本地、远程仅作回退 |
| 13 | 多语言 | 9 种语言无旧品牌残留 | ✅ 9 个语言包中 `LiteMonitor` 仅以 `LiteMonitor++` 形式出现（品牌字段），无旧名残留 |
| — | 配置持久化 | 正常读写 | ✅ 运行后生成 `settings.json` / `HardwareHistory.json` / `TrafficHistory.json` |

**仍需人工交互确认的项**（无法自动化，涉及 GUI 点击 / 提权 / 真实硬件）：

| # | 验证项 | 怎么验 |
|---|---|---|
| 6 | 驱动安装 | 未装 PawnIO 的机器上点「安装驱动」，确认走**本地 `resources/driver.zip`**（离线）而非下载；再把 `DriverInstaller.cs:29` 的 `DriverPackageSha256` 改错一次，确认**拒绝执行**并弹校验失败 |
| 7 | FPS 监控 | 开启 FPS → 确认 `resources/LiteMonitorPlusFPS.exe` 正常启动并出数 |
| 8 | 开机自启 | 启用自启 → 任务计划程序出现 `LiteMonitor++_AutoStart`；旧机器上确认 `LiteMonitor_AutoStart` 已被清理；重启后只启动一个实例 |
| 14 | 卸载/清理 | 卸载后计划任务、崩溃日志、托盘图标无残留 |

- [x] **Step 8.7: 写发布前 checklist 文档**

新建 `docs/REBRAND.md`，写入：
- 品牌映射表 A/B/C 三张表（本计划对应章节）
- 替换执行手册（grep 命令 + 替换规则 + 排除清单）
- `scripts/pack-release.ps1` 的用法与产物清单
- Task 8.6 的验证清单
- 每次发布前必做的 10 件事

- [x] **Step 8.8: 最终提交**

```powershell
git add docs/REBRAND.md
git commit -m "docs(rebrand): 补充品牌映射表、替换手册与发布前 checklist"
```

---

## 回滚方案

每个 Task 一次独立提交，若某步出问题：

```powershell
git log --oneline
git revert <commit-sha>          # 保留历史，安全回退
# 或
git reset --hard <commit-sha>    # 仅限未推送的本地分支
```

**高风险 Task 的回滚要点**：

| Task | 回滚风险 |
|---|---|
| Task 2 | 改了 URL 后若自有源还没上线 → **更新功能会失败**。回滚前先确认自有源已就绪。**应急可临时把上游 URL 加回数组末尾作 fallback**（但注意这会把用户导流回上游，见附录 B） |
| Task 4.6 | Updater 改名后若 `Program.cs:13` 的 `ExeName` 或 `UpdateChecker.cs:356,382` 没同步 → **更新后主程序仍是旧版**。回滚必须同时回滚 csproj、`Program.cs`、`UpdateChecker.cs`、`UpdateDialog.cs:381` 与 `resources/` 下的二进制，**五者缺一不可** |
| Task 4.7 | FPS 组件改名后若 `DriverInstaller.cs:279,294` 的 ZIP 条目匹配没同步 → **组件装不上**。回滚时把文件名改回 `LiteMonitorFPS.exe` 并同步这 4 处 |
| Task 4.1 | 计划任务改名后若清理逻辑有 bug → 老用户机器上**开机启动两次**。回滚时把 TaskName 改回 `LiteMonitor_AutoStart` |
| Task 7 | 命名空间重命名后**放弃上游 merge 能力**。回滚成本是全仓库 89 文件的 revert，**所以 Task 7 单独成一个 commit，不要与别的 Task 混在一起** |

---

## 附录 A：上游耦合点全清单（Task 2 的核对表）

| 域名 / 路径 | 位置 | 类型 | 本计划处理 |
|---|---|---|---|
| `litemonitor.cn/update/version.json` | `UpdateChecker.cs:46` | **运行时依赖（主源！）** | Task 2.1 |
| `gitee.com/Diorser/LiteMonitor` | `UpdateChecker.cs:49,63` | 运行时依赖 | Task 2.1, 2.2 |
| `raw.githubusercontent.com/Diorser/LiteMonitor` | `UpdateChecker.cs:52` | 运行时依赖 | Task 2.1 |
| `github.com/Diorser/LiteMonitor` | `UpdateChecker.cs:67,140` | 运行时依赖 | Task 2.2, 2.3 |
| `litemonitor.cn/update/LiteMonitor_v{0}-win-x64.zip` | `UpdateChecker.cs:65` | 运行时依赖 | Task 2.2 |
| `litemonitor.cn/update/driver.zip` | `DriverInstaller.cs:33` | 运行时依赖 | Task 2.5 |
| `litemonitor.cn/update/LiteMonitorFPS.exe` | `DriverInstaller.cs:41` | 运行时依赖 | Task 2.5 |
| `litemonitor.cn/update/CityCode.json` | `CityCodeResolver.cs:15` | 运行时依赖 | Task 2.7 |
| `crypto.litemonitor.cn` | `resources/plugins/Crypto.json` | 运行时依赖 | Task 2.8 |
| `LiteMonitor.cn` | `AboutForm.cs:82,93` | 界面链接 | Task 3.3 |
| `github.com/Diorser/LiteMonitor/issues` | `MenuManager.cs:600`, `AboutForm.cs:138` | 界面链接 | Task 2.10, 3.3 |
| `github.com/Diorser/LiteMonitor/releases` | `MenuManager.cs:604` | 界面链接 | Task 2.10 |
| `github.com/Diorser/LiteMonitor/blob/.../PLUGIN_DEV_GUIDE.md` | `PluginPage.cs:137` | 界面链接 | Task 2.11 |
| `github.com/Diorser/LiteMonitor` | `README.md:9,36,81,163,182`、`README.en.md:8,65,163,182` | 文档链接 | Task 1.3, 1.4, 1.5 |
| `gitee.com/Diorser/LiteMonitor` | `使用说明(必读).txt:24` | 文档链接 | Task 5.1 ④ |
| `diorser/LiteMonitor` | `resources/plugins/GitHub.json:14` | 插件默认值 | Task 2.12 |
| `raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins` | `PluginMarket.cs:67` | 运行时依赖 | ✅ **已是自有**，Task 2.9 仅核对 |
| `https://github.com/mojingjing-git/LiteMonitor` | `使用说明(必读).txt:67` | 文档链接 | Task 5.1 ③ |
| `github.com/Diorser/LiteMonitor` | `NOTICE`（新建） | 致谢链接 | ✅ **必须保留** |

---

## 附录 B：已知不做的事（YAGNI）

- **不改 namespace**（默认）：成本 = 全仓库 89 文件；收益 = 崩溃堆栈里的字符串。见 Task 7 的完整论证。
- **不重命名项目文件 / sln**：`LiteMonitor.csproj` 保持原名，IDE 里的项目名不反映品牌不影响用户。
- **不做"官方版"兼容层**：不试图与上游共享配置目录、插件目录、计划任务。**明确放弃与上游共存**。
- **不保留上游更新源作 fallback**：Task 2.2 若加回上游 URL，你的用户会再次被导流。宁可更新功能不可用，也不要静默导流。
- **不改 `resources/themes/` 10 个主题文件**：它们只含配色，不含品牌。
- **不改 7 个不含品牌的语言包 `About` 条目**：只有 zh / zh-tw 含 "LiteMonitor" 字样。
- **不重新实现 `LiteMonitorFPS.exe`**：无源码，只能改名。
- **不引入插件远程安装**：多显卡计划附录 B 已明示属于独立计划线；本计划只处理其**索引源域名**。
- **不为上游补 LICENSE 文件**：那是上游作者的义务（Task 0.2 走沟通渠道），不在本计划的修改范围内。

---

## 附录 C：与前序对话结论的对应

| 前序结论 | 本计划对应 |
|---|---|
| 改名 + 自行分发在 MIT 下合法，唯一义务是保留版权声明与许可声明 | Task 1（LICENSE 双份版权）+ Task 3.4（AboutForm 双份版权）+ Task 6.5（打包时带上 LICENSE） |
| 上游没有 LICENSE 文件，授权文书缺失 | Task 0.2（索取）+ Task 1.1（自建）+ Task 1.2（NOTICE） |
| `使用说明(必读).txt` 是上游"只存在于发布包、未入库"的文件 | Task 5.1（改写内容）+ 多显卡 Task 1（提交） |
| `UpdateChecker` 指向原作者 → 用户被导流回上游 | **Task 2.1~2.4（最高优先级）**，含主源 `litemonitor.cn`（`:46`） |
| "绝无任何恶意后门"与插件市场矛盾 | Task 5.1 ② |
| "多显卡"描述与代码不符 | Task 5.1 ③ |
| 驱动/FPS 下载无完整性校验即提权执行 | Task 2.6（补 SHA256） |
| 仓库无 `.gitignore`，`bin/` `obj/` 未忽略 | Task 6.1（由多显卡 Task 1 创建，本计划校验） |
| `bin/` 里有源码不引用的陈旧第三方 DLL | Task 6.2 |
| 版本号 1.3.6 与上游同号 | Task 6.3 |
| 命名空间 89/89 文件全用 `LiteMonitor` | Task 7（可选、延后、默认不做） |
| 发布包靠手工拼装、`resources/assets/` 不进输出 | Task 6.5（新增打包脚本） |
