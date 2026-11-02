> [!NOTE]
> **本项目基于 [LiteMonitor](https://github.com/Diorser/LiteMonitor)（原作者 Diorser，MIT 协议）派生修改。**
> 上游项目主页：https://litemonitor.cn ｜ 本项目为非官方版本，与原作者无关联。
> 完整许可证见 [LICENSE](./LICENSE)，与上游的能力差异见 [NOTICE](./NOTICE)，架构文档见 [docs/](./docs/)。


# <img src="./resources/screenshots/logo.png"  width="28" style="vertical-align: middle; margin-top: -4px;" /> LiteMonitor++
一款轻量、可定制的开源桌面硬件监控软件 — 实时监测 CPU、GPU、内存、磁盘、网络等系统性能。

A lightweight and customizable desktop hardware monitoring tool — real-time monitoring of system performance such as CPU, GPU, memory, disk, and network.

支持横/竖屏/任务栏/网页显示、主题切换、多语言、透明度显示、三色报警等，界面简洁且高度可配置 。
> **立即下载最新版本：** [GitHub Releases → LiteMonitor++ 最新版](https://github.com/mojingjing-git/LiteMonitorPlus/releases/latest)

> 已支持【内存清理】和【自定义插件】功能，详见：[插件开发指南（无需编程）](./resources/plugins/PLUGIN_DEV_GUIDE.md)


![LiteMonitor++ 主界面](./resources/screenshots/overview.png)

###  横条模式 / 任务栏显示模式
![LiteMonitor++ 横屏/任务栏显示](./resources/screenshots/overview3.png)


# 系统监控功能

| 分类 | 监控指标 |
|------|-----------|
| **处理器（CPU）**  | 实时监测 CPU 使用率、温度、频率、功耗、风扇、水冷等数据。 |
| **显卡（GPU）**  | 展示 GPU 使用率、核心温度、显存、频率、功耗 风扇，兼容 NVIDIA / AMD / Intel 显卡。 |
| **主机（HOST）** | 显示系统内存占用、FPS刷新率、磁盘温度、主板温度、机箱风扇、整机总功耗等，清晰了解电脑综合情况。 |
| **电池（Battery）** | 监测电池状态（充电状态、电量、功耗、电流、电压等）。 |
| **磁盘（Disk）**   | 监控磁盘读取与写入速度（KB/s、MB/s），帮助分析存储 I/O 活跃情况。支持自动/手动选择磁盘。 |
| **网络（Network）** | 实时显示上传与下载速度（KB/s、MB/s），提供轻量级网络流量监控。支持自动/手动选择网卡。 |
| **流量统计（Traffic）** | 统计每日上传与下载流量，帮助分析网络使用习惯。 |
| **插件系统（Plugin）** | 支持监控天气、股票、加密货币、代理延迟、汇率等，支持自定义插件，扩展监控项与功能。 |
| **硬件传感器（Sensors）** | 硬件详情面板可以查看和监控所有系统硬件传感器数据。 |


> LiteMonitor++ 持续完善中，如需更多监控项或功能支持，欢迎在 [GitHub Issues](https://github.com/mojingjing-git/LiteMonitorPlus/issues) 中反馈建议！

---

###  网络测速功能
![LiteMonitor++ 网速测试](./resources/screenshots/overview4.png)  ![LiteMonitor++ 菜单](./resources/screenshots/overview5.jpg)


###  监控历史
![LiteMonitor++ 监控历史](./resources/screenshots/overview9.png)

###  历史流量统计
![LiteMonitor++ 主题编辑器](./resources/screenshots/overview7.png)

###  网页版监控
![LiteMonitor++ 网页版监控](./resources/screenshots/web.png)

---

# 产品功能

| 功能 | 说明 |
|---|---|
| 自定义主题 | 通过 JSON 定义颜色、字体、间距、圆角等，主题可扩展与复用。主题系统 v2 更易维护。 |
| **主题工作室** | 可视化编辑主题的全部属性：配色、材质参数、布局、圆角、折射与模糊强度，改动实时预览，导出主题包。 |
| **主题市场** | 浏览远程主题索引，一键安装与卸载主题；安装前校验主题包完整性。 |
| **三色报警** | 监控项根据阈值自动切换进度条/数值颜色，支持自定义颜色与网络/磁盘独立阈值。 |
| **规则化报警** | 除三色阈值外，可用「条件 + 持续时间 + 动作」组合出告警规则，动作包括弹窗、声音与写入告警日志。 |
| **情景模式** | 把一组设置保存为配置档案，可预检后整体应用；适合「游戏」「办公」「省电」等场景一键切换。 |
| **诊断中心** | 记录运行期事件，支持导出诊断包（配置、硬件信息、事件日志、截图），便于排查问题。 |
| **趋势统计** | 温度、负载、功耗、频率、FPS 等指标按秒分桶留存短期趋势，并按天分段写入历史文件，长期曲线可回看。 |
| 多语言界面 | 内置多语言，所有菜单/短标签/监控项即时国际化。 |
| 监控项显示管理 | 按需显示或隐藏 CPU、GPU、VRAM、内存、磁盘、网络等模块。 |
| **多显卡监控** | 同时监控多张独立显卡，温度、占用、功耗、显存等指标分卡显示与分卡分组标题。 |
| **横屏模式** | 全新横条布局，支持每列独立宽度、单位智能格式化、两行显示、自动计算面板宽度。 |
| 面板宽度调整 | 即时调整面板宽度，布局自动重排。 |
| **UI 缩放** | 自适应 DPI + 用户自定义缩放，界面与字体完美比例缩放。 |
| **动画平滑** | 数值更新支持平滑动画，降低突变带来的跳动感，可自行调节速度。 |
| 窗口与界面 | 圆角显示、透明度调节、阴影、高质量字体渲染，视觉干净优雅。 |
| 靠边自动隐藏 | 靠屏幕边缘自动收起，靠近边缘自动弹出，支持多屏幕正确判断。 |
| **限制拖出屏幕** | 选项开启后，窗口不可拖出屏幕可视区域。 |
| 鼠标穿透模式 | 启用后，窗口不拦截鼠标事件，可直接操作背后应用。 |
| UI 与主题即时切换 | 切换主题/语言后界面即时刷新，无需重启。 |
| 数值智能格式化 | 自动格式化单位与小数位，横屏模式支持智能“/s”去除、>=100 自动取整等。 |
| 自动更新检测 | 启动时静默检查新版本，手动检查时展示弹窗。支持国内与 GitHub 双源。 |
| 开机自启 | 通过计划任务方式实现管理员级别自启动。 |
| 配置文件存储 | 所有设置实时写入 `settings.json`，支持迁移与备份。 |


## GPU 材质主题与实时桌面背景

部分主题在 JSON 中声明 `rendering.backend = "gpu"`，用 DirectX 11 (DXGI / D3D11) 直接渲染面板材质，而不是传统 GDI+ 绘制。**主题声明与用户开关两者都满足**才会进入 GPU 路线，缺一都保持 GDI+ 外观。

### 三条渲染路线

| 路线 | 前置条件 | 表现 |
|---|---|---|
| **GDI+ 路线**（自动兜底，枚举 `Legacy`） | 主题未声明 `rendering.backend = "gpu"`、显卡不支持 DirectX 11、或 `--safe` 启动 | 传统 GDI+ 绘制，任何环境可用 |
| **GPU 固定背景**（枚举 `GpuFixedInput`） | GPU 主题 + 「GPU 材质渲染」开 | 真实玻璃材质（折射/厚度/高光），背景为程序化材质 |
| **GPU 实时桌面背景**（枚举 `GpuLiveBackground`） | 上一条全部满足 + 「实时桌面背景」开 + WDA 政策已接受 | 玻璃后面是**实时模糊桌面**；透明度 <100%（分层窗口）实测同样可用 |

设置面板 → **主面板页**：GPU 路线开关（材质渲染 / 实时桌面背景）就在主题选择下方，上方的路线状态行实时显示当前主题声明路线与实际生效路线；**「渲染路线」页**给出完整诊断（含真实原因码，如 `safe_mode` / `gpu_effects_opt_in_required`）。主题下拉与托盘菜单的主题项都会标注 `（GPU 材质）` / `（GDI+）`。

### 支持矩阵

| 项目 | 要求 | 不满足时 |
|---|---|---|
| 操作系统 | Windows 10 / 11 | 不支持 |
| 运行时 | .NET 8 桌面运行时（发布包为框架依赖） | 无法启动 |
| 显卡 | 提供 DXGI 适配器且支持 DirectX 11 | 自动回退 GDI+，面板正常显示 |
| 主题 | 主题 JSON 中 `rendering.backend = "gpu"` | 该主题保持 GDI+ 外观 |
| 用户开关 | 设置 → 主面板 →「GPU 材质渲染」 | 不开启则不进入 GPU 路线 |
| 多屏 / 混合 DPI | 支持，可拖到副屏（含负坐标原点） | — |
| 实时桌面背景 | 需 DXGI Desktop Duplication 与 WDA 支持 | 捕获失败时保留可读的固定材质 |

### WDA（窗口捕获排除）行为

开启「实时桌面背景」后，程序用 `WDA_EXCLUDEFROMCAPTURE` 把**整个顶层窗口**从受支持的屏幕捕获中排除：

- 受支持的系统截图与录屏工具**看不到 LiteMonitor++**，该区域显示为背景本身
- GDI+ 路线与**固定输入模式**（不启用实时背景）不受影响
- **赛博朋克（CyberPunk）主题始终为零抓屏**，不会请求桌面捕获
- 首次开启会弹出《实时玻璃捕获说明》确认；不接受则保持关闭

### 回退行为

- 未开启「GPU 材质渲染」→ 主题即使声明了 GPU 也不会启用
- 显卡不支持 DXGI、设备创建失败 → 回退 GDI+ 路线
- 实时背景捕获失败 → **撤回窗口级捕获排除**（面板重新出现在截图 / 录屏里），同时继续显示可读的固定 GPU 材质，不会留下一个"空洞"
- GPU 启动失败后不会自动反复重试；在设置里重新应用一次设置才会再尝试
- 启动参数 `--safe` / `--safe-mode` / `--disable-gpu` 可强制使用 GDI+ 路线

---

## 安装与使用

### 前置要求

| 项目 | 要求 | 说明 |
|---|---|---|
| 操作系统 | Windows 10 / 11 | — |
| 运行时 | .NET 8 桌面运行时（Desktop Runtime） | 发布包为框架依赖发布，未安装运行时无法启动 |
| 权限 | 管理员权限 | 程序清单声明 `requireAdministrator`；用于安装 PawnIO 驱动、写入程序目录、注册开机自启计划任务 |
| 驱动 | PawnIO | 随包附带官方安装包 `resources/assets/PawnIO_setup.exe`；用于读取 MSR / SuperIO / SMBus / PCI 寄存器等标准 API 无法访问的传感器数据 |

### 安装步骤

1. 前往 [Releases 页面](https://github.com/mojingjing-git/LiteMonitorPlus/releases) 下载最新版压缩包
2. 解压后运行 `LiteMonitor++.exe`
3. 程序会自动根据系统语言加载对应语言文件

---

## 插件市场

设置 → 插件市场，可以直接从社区插件仓库一键安装插件。

| 项 | 说明 |
|---|---|
| 插件源 | 固定为 [awesome-litemonitor-plugins](https://github.com/mojingjing-git/awesome-litemonitor-plugins)，程序内不提供修改入口 |
| 安装位置 | 程序目录下的 `resources/plugins/`，与内置插件同级 |
| 卸载 | 在市场页点「卸载」，只会删除通过市场安装的插件；内置插件永不可卸载 |
| 完整性校验 | 每个插件在索引里带 SHA-256，安装时校验下载内容的原始字节，不匹配即中止 |

已装插件是**纯声明式 JSON 配置**（只声明「去哪取数、怎么提取、怎么显示」），**不具备执行第三方脚本或代码的能力**。但插件会按其配置发起网络请求，可能把你填写的 API Key 发送给它声明的接口 —— 安装前请查看插件 README 里说明的请求域名。

> 写入程序目录需要权限。若程序装在 `C:\Program Files\` 下，安装会失败，请以管理员身份运行本程序。

想自己写插件？见 [`resources/plugins/PLUGIN_DEV_GUIDE.md`](./resources/plugins/PLUGIN_DEV_GUIDE.md)。

---

## 主题系统

主题文件位于 `resources/themes/` 目录，当前内置 20 个主题（其中 6 个为 GPU 材质主题）。

示例：
```json
{
  "name": "DarkFlat_Classic",
  "layout": { "rowHeight": 40, "cornerRadius": 10 },
  "color": {
    "background": "#202225",
    "textPrimary": "#EAEAEA",
    "barLow": "#00C853"
  }
}
```

> 主题系统 v2 新特点：
> - 布局字段更精简、统一
> - 字体与布局分别独立缩放
> - 所有布局由 Theme.Scale 自动处理
> - 更容易构建自己的主题模板

GPU 材质主题在 JSON 中额外声明 `rendering.backend = "gpu"` 与 `rendering.parameters`（折射、厚度、模糊半径、玻璃样式等）。参数取值范围与回退规则见 [docs/architecture/rendering.md](docs/architecture/rendering.md)。

---

## 设置文件（settings.json）

配置文件写在**程序目录**下的 `settings.json`（便携布局，与程序同目录）。全部持久化项由 `src/Core/Settings.cs` 的可写属性定义，共 106 项；下表按语义分组列出常用项，其余项保持默认值即可。

| 分组 | 常用字段 | 说明 |
|---|---|---|
| 外观 | `Skin` | 当前主题名称 |
| 外观 | `UIScale` | 用户界面缩放倍率 |
| 外观 | `Opacity` | 透明度（0.1 ~ 1.0） |
| 外观 | `HorizontalMode` | 横屏 / 竖屏显示模式 |
| 外观 | `PanelWidth` | 面板宽度（两种模式共用） |
| 外观 | `AnimationSpeed` | 数值平滑动画速度 |
| 外观 | `CornerRadius` / `ShadowEnabled` / `TransparentEnabled` | 圆角、阴影、透明窗口开关 |
| 交互 | `TopMost` | 是否置顶窗口 |
| 交互 | `AutoHide` | 是否启用靠边自动隐藏 |
| 交互 | `ClampToScreen` | 拖动后限制窗口在屏幕内 |
| 交互 | `ClickThrough` | 是否启用鼠标穿透 |
| 交互 | `MonitorItems` | 各监控项的开关、排序、分组标题与自定义上下限（`MonitorItemConfig`） |
| 采集 | `RefreshMs` | 刷新间隔（支持完整预设） |
| 采集 | `PreferredNetwork` / `PreferredDisk` | 手动选择网卡 / 磁盘（空 = 自动） |
| 采集 | `DiskProbeMode` | 磁盘探测档位（关闭 / 仅计数器 / 完整） |
| 采集 | `LowPowerMode` | 低功耗模式（关闭 / 自动 / 节能） |
| 报警 | `AlertRules` | 规则化报警规则列表 |
| 报警 | `NetworkThreshold` / `DiskThreshold` | 网络与磁盘的独立阈值 |
| 报警 | `AlertSoundEnabled` / `AlertPopupEnabled` | 告警声音与弹窗开关 |
| 情景 | `ScenarioProfiles` | 情景模式的配置档案列表 |
| 趋势 | `TrendEnabled` / `TrendRetentionDays` | 趋势统计开关与历史保留天数 |
| 插件 | `InstalledPlugins` / `PluginSettings` | 已安装插件与各插件的私有配置 |
| 安全 | `WebServerEnabled` / `WebServerBindMode` / `WebServerPassword` | Web 面板开关、监听范围与访问口令 |
| 安全 | `UpdateChannel` / `AutoCheckUpdate` | 更新通道与自动检查开关 |
| 系统 | `Language` | 当前语言（自动检测或手动切换） |
| 系统 | `AutoStart` | 是否开机启动 |
| 系统 | `Theme` / `WebPort` 等 | 其余项均可在设置界面中修改，改动实时落盘 |

> 口令与插件密钥不以明文落盘：口令使用 DPAPI（CurrentUser）保护，配置文件写入采用「临时文件 + 原子替换」，写盘失败时不会把明文落盘。

---

## 架构概览

源码位于 `src/`。`src/Contracts`、`src/HardwareHost`、`src/Cli` 是独立工程，其余目录编译进主程序集 `LiteMonitor++`；`LiteMonitor.Updater/` 位于仓库根目录。

| 模块 | 职责 |
|------|------|
| `System/Program.cs` | 程序入口：单实例互斥、全局异常捕获、DPI 初始化、驱动前置检查 |
| `UI/MainForm_Transparent.cs` | 主窗体、拖拽、菜单托盘、自动隐藏、透明度、位置保存 |
| `UI/UIController.cs` | 主题加载、DPI/UIScale 缩放、布局重构、渲染入口、定时刷新 |
| `UI/UIRenderer.cs` | 竖屏渲染器（组块、进度条、标题渲染） |
| `UI/HorizontalRenderer.cs` | 横屏渲染器（两行布局、智能标签与数值） |
| `UI/UILayout.cs` / `UI/HorizontalLayout.cs` | 竖屏动态布局计算 / 横屏列宽与面板总宽度计算 |
| `UI/Decor/` | GDI+ 装饰层绘制（外框、进度条、几何、纹理缓存） |
| `UI/Settings/` | 设置面板各分页（主面板、渲染路线、任务栏、监控项、阈值、系统与硬件、插件、插件市场、主题市场、情景、诊断） |
| `UI/LiveGlassController.cs` | GPU 路线的实时桌面捕获与呈现控制 |
| `UI/RenderingTransitionCoordinator.cs` | GPU ↔ GDI+ 路线切换的状态机与资源交接 |
| `Core/Actions/` | 应用动作聚合：程序动作、设置切换、Web 端动作 |
| `Core/Alerts/` | 规则化报警：模型、评估器、运行时与旧阈值迁移 |
| `Core/Scenarios/` | 情景模式：配置档案、预检、应用协调与投影 |
| `Core/Trends/` | 趋势数据：短期缓冲、历史存储（V2）与视图指标 |
| `Core/ThemeManager.cs` | 主题加载、颜色解析、字体构建 |
| `Core/LanguageManager.cs` | 多语言加载、扁平化 Key 访问 |
| `Core/Settings.cs` | `settings.json` 配置模型；口令与插件密钥的迁移、加密与落盘 |
| `Core/MetricItem.cs` / `Core/MetricUtils.cs` | 监控项模型、数值格式化、三色阈值判定、进度条比例 |
| `System/HardwareMonitor.cs` | 采集 CPU/GPU/MEM/NET/DISK 信息；自动 / 手动设备选择 |
| `System/HardwareServices/` | 采样协调器（≤1Hz 单一入口）、传感器映射与设备选路、显存容量、CPU 电压、提交内存、派生指标（整机功耗）、驱动安装 |
| `System/NetworkTraffic/` | ETW 网络流量采集、零拷贝解码、按远端 IP 排除流量 |
| `System/Pipe/` | 与硬件采样子进程之间的命名管道通道与监督器 |
| `System/WebServer/` | 网页监控服务：HTTP/1.1 + WebSocket、会话与口令协议、主题 CSS 映射 |
| `System/UpdateChecker.cs` | GitHub + 国内双源版本检测、更新元数据签名校验 |
| `System/AutoStart.cs` | 管理计划任务，实现开机自启 |
| `System/InfoService/` | 系统信息查询（电池、系统摘要等） |
| `System/Backdrop/` | GDI+ 路线的桌面背景捕获与模糊链 |
| `Rendering/` | GPU 渲染路线：契约、配置与路由判定、设备与资源账本、几何、材质、场景、呈现、桌面复制捕获 |
| `Rendering/Capture/` | GPU 路线的实时桌面帧源 |
| `Rendering/Power/` | 整机功耗派生 |
| `Plugins/` | 插件模型、网络沙箱（方法面 / 主机白名单 / 重定向复核 / 并发上限 / SSRF 地址策略）、执行器、市场安装与卸载事务 |
| `ThemeEditor/` | 主题工作室：属性编辑、预览与主题文件读写服务 |
| `Diagnostics/` | 诊断中心：事件存储、诊断 Hub 与诊断包收集（含扩展提供程序） |
| `Contracts/` | 跨进程契约库：IPC 编解码、消息类型、采样模型（独立工程） |
| `HardwareHost/` | 硬件采样子进程：最小采样实现与管道会话（独立工程） |
| `Cli/` | 命令行工具：主题包校验（与主程序共享 `ThemeMarketValidator` 源码）（独立工程） |
| `LiteMonitor.Updater/` | 独立更新器：摘要校验、事务替换与失败回滚 |

> ⚠️ **两条背景捕获链并存**：`System/Backdrop/`（GDI+ 路线）与 `Rendering/Capture/`（GPU 路线）各自独立实现帧源、WDA 排除与清理逻辑，互不共享。
> 改动捕获相关代码前，请先确认自己处于哪一条链上。

分层职责、依赖方向与契约详见 [`docs/architecture/`](docs/architecture/)：

| 文档 | 内容 |
|---|---|
| [overview.md](docs/architecture/overview.md) | 程序集与进程拓扑、分层依赖图、跨进程数据流 |
| [layering.md](docs/architecture/layering.md) | 各层职责边界与依赖约束 |
| [process-isolation.md](docs/architecture/process-isolation.md) | 硬件采集进程隔离与管道协议 |
| [rendering.md](docs/architecture/rendering.md) | 三条渲染路线、GPU 渲染栈与材质参数 |
| [rendering-transition.md](docs/architecture/rendering-transition.md) | GPU ↔ GDI+ 切换合同与错误码 |
| [plugin-sandbox.md](docs/architecture/plugin-sandbox.md) | 插件沙箱与网络访问闸门 |
| [market.md](docs/architecture/market.md) | 插件市场与主题市场的信任边界与安装事务 |
| [known-gaps.md](docs/architecture/known-gaps.md) | 已知缺口与限制 |

---

## 编译说明

### 环境要求
- Windows 10 / 11
- .NET 8 SDK
- Visual Studio 2022 或 Rider

### 编译命令
```bash
git clone https://github.com/mojingjing-git/LiteMonitorPlus.git
cd LiteMonitorPlus
dotnet build -c Release
```

输出文件：
```
/bin/Release/net8.0-windows/LiteMonitor++.exe
```

---

## 开源协议

本项目基于 **MIT License** 发布，可自由使用、修改与分发。

- 完整许可文本：[LICENSE](./LICENSE)
- 上游项目与能力差异：[NOTICE](./NOTICE)
- 上游项目：https://github.com/Diorser/LiteMonitor （原作者 Diorser）

---

## 联系方式
**本项目作者**：mojingjing-git
**项目主页**：[https://github.com/mojingjing-git/LiteMonitorPlus](https://github.com/mojingjing-git/LiteMonitorPlus)

**上游项目（原作者 Diorser，MIT 协议）**：[https://github.com/Diorser/LiteMonitor](https://github.com/Diorser/LiteMonitor)
