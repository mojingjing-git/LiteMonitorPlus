# LiteMonitor++ 重品牌化与合规手册

> 本文档是 `docs/superpowers/plans/2026-10-20-rebrand-compliance.md` 的**落地版速查手册**：
> 记录最终定案的品牌名、三张替换映射表、替换执行规则、发布打包流程与发布前 checklist。
> 计划文档记录「为什么这么做」，本文档记录「现在是什么、怎么做」。

---

## 0. 品牌定案

| 占位符 | 最终值 | 说明 |
|---|---|---|
| `{{BRAND}}` | `LiteMonitor++` | 品牌名 / exe 名 / 程序集名 / 计划任务名 / 日志名 / User-Agent |
| `{{REPO}}` | `mojingjing-git/LiteMonitorPlus` | GitHub 仓库名（**GitHub 不允许仓库名含 `+`**） |
| `{{VERSION}}` | `1.0.0` | 独立号段，与上游 `1.3.6` 区分 |
| `{{SITE}}` | *（无官网）* | 所有原「官网」位改为「上游项目」致谢链接 |

**为什么品牌叫 `LiteMonitor++`、仓库叫 `LiteMonitorPlus`**：

- `+` 在 Windows 文件名与 .NET 程序集名合法（已实测 `<AssemblyName>LiteMonitor++</AssemblyName>` 可编译、可被 `Type.GetType` 解析）；
- 但 **GitHub 仓库名与 URL 不允许 `+`**（已实测全站 0 个含 `+` 的仓库），所以仓库用 `LiteMonitorPlus`；
- 已有 `Codex++` / `Notepad++` 这类后缀式命名的先例，**`+` 不构成商标侵权**。

**命名空间 `LiteMonitor` 刻意不改**：89/89 个 `.cs` 文件都要动，改完永久失去与上游 merge 的能力，
而收益只有崩溃堆栈里的一个字符串；品牌带 `Lite` 前缀后，命名空间反而与品牌自洽。

---

## 1. 映射表 A —— 用户可见品牌

| # | 位置 | 上游原值 | 现值 | 落地位置 |
|---|---|---|---|---|
| A1 | 语言包标题（9 个） | `⚡️LiteMonitor` | `⚡️LiteMonitor++` | `resources/lang/*.json:2` |
| A2 | 关于菜单（中） | `关于 LiteMonitor` | `关于 LiteMonitor++` | `resources/lang/zh.json` |
| A3 | 关于菜单（繁） | `關於 LiteMonitor` | `關於 LiteMonitor++` | `resources/lang/zh-tw.json` |
| A4 | 关于窗口标题 | `⚡️LiteMonitor` | `⚡️LiteMonitor++` | `src/UI/AboutForm.cs:46` |
| A5 | 关于窗口「官网」 | `LiteMonitor.cn` | `上游项目: litemonitor.cn` | `src/UI/AboutForm.cs:82,93`（**上游链接按致谢义务保留**） |
| A6 | 关于窗口 GitHub | `github.com/Diorser/LiteMonitor` | `github.com/mojingjing-git/LiteMonitorPlus` | `src/UI/AboutForm.cs:104,115,140` |
| A7 | 托盘提示 | `LiteMonitor` | `LiteMonitor++` | `src/UI/Helpers/MainFormBizHelper.cs` |
| A8 | 更新弹窗标题（托盘） | `⚡️LiteMonitor_v` | `⚡️LiteMonitor++_v` | `src/UI/Helpers/MainFormBizHelper.cs` |
| A9 | 设置引导框 | `LiteMonitor Setup` | `LiteMonitor++ Setup` | `src/UI/Helpers/MenuMonitorHelper.cs:54` |
| A10 | 设置窗消息框 | `LiteMonitor` | `LiteMonitor++` | `src/UI/SettingsForm.cs:236` |
| A11 | 菜单消息框 | `LiteMonitor` | `LiteMonitor++` | `src/UI/MenuManager.cs:134` |
| A12 | 任务栏设置消息框 | `LiteMonitor` | `LiteMonitor++` | `src/UI/Settings/TaskbarPage.cs:253` |
| A13 | 硬件详情窗标题 | `LiteMonitor - Hardware Info` | `LiteMonitor++ - Hardware Info` | `src/UI/HardwareInfoForm.cs:26` |
| A14 | 主题编辑器标题 | `LiteMonitor Theme Editor` | `LiteMonitor++ Theme Editor` | `src/ThemeEditor/ThemeEditorForm.cs:42` |
| A15 | Web 页面标题 | `LiteMonitor WebServer` | `LiteMonitor++ WebServer` | `src/System/WebServer/WebPageContent.cs:31` |
| A16 | 更新对话框标题 | `⚡️LiteMonitor - ` | `⚡️LiteMonitor++ - ` | `src/UI/UpdateDialog.cs:51` |
| A17 | 更新对话框 UA | `LiteMonitor-Updater/1.0` | `LiteMonitor++-Updater/1.0` | `src/UI/UpdateDialog.cs` |
| A18 | 插件作者字段（12 个） | `"author": "LiteMonitor"` | `"author": "LiteMonitor++"` | `resources/plugins/*.json` |

---

## 2. 映射表 B —— 功能性品牌（改动会打断既有用户环境，**必须成对处理**）

| # | 位置 | 上游原值 | 现值 | 迁移风险与配套动作 |
|---|---|---|---|---|
| B1 | 计划任务名 | `LiteMonitor_AutoStart` | `LiteMonitor++_AutoStart` | ⚠️ 旧任务会残留 → 已补 `DeleteLegacyTask()` 清理 |
| B2 | 计划任务描述 | `LiteMonitor Auto Start` | `LiteMonitor++ Auto Start` | 无 |
| B3 | 临时 XML 名 | `LiteMonitor_Task_{guid}.xml` | `LiteMonitor++_Task_{guid}.xml` | 无（临时文件） |
| B4 | 防火墙指引文案 | `LiteMonitor` | `LiteMonitor++` | ⚠️ **必须与 exe 名严格一致**（`MainPanelPage.cs` 中英文各 2 处） |
| B5 | 崩溃日志名 | `LiteMonitor_Error.log` | `LiteMonitor++_Error.log` | 老用户旧日志不再续写，可接受 |
| B6 | 崩溃弹窗标题 | `LiteMonitor Crash` | `LiteMonitor++ Crash` | 无 |
| B7 | 单实例互斥体 | `Global\LiteMonitor_SingleInstance_*` | **刻意不改** | ⚠️ 改了会与上游版同机双开（抢传感器 / 任务栏 / 5000 端口） |
| B8 | 主 exe 名 | `LiteMonitor.exe` | `LiteMonitor++.exe` | `LiteMonitor.csproj` 的 `<AssemblyName>` |
| B9 | Updater 程序集名 | `LiteMonitor.Updater` | `LiteMonitor++.Updater` | ⚠️ **需重新 publish** |
| B10 | Updater 定位主 exe | `LiteMonitor.exe` | `LiteMonitor++.exe` | ⚠️ **不改则更新链断裂**（`LiteMonitor.Updater/Program.cs`） |
| B11 | Updater 找自己 | `LiteMonitor.Updater.exe` | `LiteMonitor++.Updater.exe` | ⚠️ `UpdateChecker.cs` 的 `ContainsMainExe` / `updaterNames` |
| B12 | Updater 产物名 | `resources/LiteMonitor.Updater.exe` | `resources/LiteMonitor++.Updater.exe` | `LiteMonitor.csproj` 的 `Content` |
| B13 | FPS 组件名 | `LiteMonitorFPS.exe` | `LiteMonitorPlusFPS.exe` | ⚠️ 预编译无源码，**只能改名**；改名后 URL 安全 |
| B14 | FPS 下载 URL | `.../LiteMonitorFPS.exe` | `.../LiteMonitorPlusFPS.exe` | `DriverInstaller.cs` |
| B15 | FPS 本地落地路径 | `resources/LiteMonitorFPS.exe` | `resources/LiteMonitorPlusFPS.exe` | `DriverInstaller.cs` |
| B16 | FPS 解压条目匹配 | `LiteMonitorFPS.exe` | `LiteMonitorPlusFPS.exe` | ⚠️ **不改则组件装不上** —— 详见下方「已修的坑」 |
| B17 | FPS 进程名 | `LiteMonitorFPS` | `LiteMonitorPlusFPS` | 进程名 = 文件名去扩展名 |
| B18 | FPS 组件路径 | `LiteMonitorFPS.exe` | `LiteMonitorPlusFPS.exe` | `FpsCounter.cs:88` |
| B19 | User-Agent（插件） | `LiteMonitor/1.0` | `LiteMonitor++/1.0` | `PluginExecutor.cs` / `CryptoNative.cs` / `CityCodeResolver.cs` |
| B20 | 发布包名 | `LiteMonitor_v{0}-win-x64.zip` | `LiteMonitorPlus_v{0}-win-x64.zip` | ⚠️ **必须与 `scripts/pack-release.ps1` 实际产物名一致** |
| B21 | 版本号 | `1.3.6` | `1.0.0` | `LiteMonitor.csproj` / `resources/version.json` |

### 已修的坑：驱动包里的 FPS 条目名

改名时漏了一处 **`resources/assets/driver.zip` 内部**的条目名 —— 包内仍是上游旧名
`LiteMonitorFPS.exe`，而代码自 B16 起只认 `LiteMonitorPlusFPS.exe`，
结果「安装驱动包」这条路径下 FPS 组件实际装不上。

修复方式：**只改条目名，不动二进制**。包内两个条目的 SHA256 与仓库中同名资产逐一相同：

```
LiteMonitorPlusFPS.exe  ee4eba300a521b16291b7ace14d95efb9fa149b0ef1d31b15df4ee345c4e002b
PawnIO_setup.exe        1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032
```

重建后 `driver.zip` 的 SHA256 变为
`343afd65b9d9ef4d6189d3b579487a6140cd2f4c2812d44aaa42464b5173c295`，
已同步写入 `DriverInstaller.cs` 的 `DriverPackageSha256` 常量。

---

## 3. 映射表 C —— 刻意保留（**不得替换**）

| 位置 | 内容 | 原因 |
|---|---|---|
| `LICENSE` | `Copyright (c) 2025 Diorser` | **MIT 强制义务** |
| `NOTICE` | 上游仓库链接 `https://github.com/Diorser/LiteMonitor` | 致谢义务 |
| `README.md` / `README.en.md` 致谢段 | 上游链接与原作者名 | 致谢义务 |
| `src/UI/AboutForm.cs:20-23` | `© 2025 Diorser / LiteMonitor Project` | **双份版权的其中一份** |
| `src/UI/AboutForm.cs:84,95` | `litemonitor.cn` 致谢链接 | 致谢义务 |
| `docs/superpowers/plans/*.md` | 计划文档中的历史引用 | 变更记录 |
| `src/Plugins/PluginMarket.cs:67` | `raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins` | 已是自有仓库，仅需核对 |
| `src/System/AutoStart.cs:16` | `LegacyTaskName = "LiteMonitor_AutoStart"` | 旧任务清理逻辑要靠它识别 |

> **判据**：凡是指向**上游网络端点**的（更新源 / 驱动源 / 城市库 / 行情接口）必须解除；
> 凡是**署名与出处**的必须保留。二进制审计的判据是前者，不是后者。

---

## 4. 替换执行手册

### 4.1 审计命令

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'

# 上游网络端点（必须 0 命中）
git grep -n -E 'litemonitor\.cn|Diorser' -- '*.cs' '*.json' '*.csproj'

# 旧品牌名残留（计划文档与 C 表除外）
git grep -n -E '(?<!Lite)MonitorPlus' -- '*.cs' '*.json' '*.md'

# 运行时 URL 全量清单（人工确认每一条都指向自有域）
git grep -n -E 'https?://' -- '*.cs' '*.json' | Select-String -NotMatch '^\s*docs/'
```

### 4.2 替换规则

| 场景 | 规则 |
|---|---|
| 品牌名 `LiteMonitor` → `LiteMonitor++` | 直接替换，**但要排除 C 表**（`Diorser` 署名、计划文档、`LegacyTaskName`） |
| 仓库名 | 用 `LiteMonitorPlus`（URL 安全），**不要**用 `LiteMonitor++` |
| 命名空间 | **不动** |
| 二进制资产 | `LiteMonitorFPS.exe` → `LiteMonitorPlusFPS.exe`（只能改名，无源码） |
| 文本文件编码 | UTF-8 **无 BOM** + CRLF（仓库 `core.autocrlf=true`，提交时会被归一化） |

> ⚠️ **多行文本替换在 CRLF 文件中会 MISS**：替换前先把 needle/repl 归一化到 LF，
> 替换后再按文件原有行尾还原。
>
> ⚠️ **正则 `(?<![\w.])LiteMonitor\b(?![\w.])` 会误伤 `using LiteMonitor;`**（`using` 后是独立词），
> 且会漏掉 `\nLiteMonitor`（`n` 是 word char，触发负向后行断言）。**按文件逐个核对**，别指望一条正则通吃。

### 4.3 排除清单（替换时必须跳过）

```
docs/superpowers/plans/**          # 计划文档，历史引用即变更记录
LICENSE  NOTICE                    # MIT 署名义务
resources/plugins/*.json           # 插件的 author 已改，但插件内部 URL 需逐条核对
src/Plugins/PluginMarket.cs:67     # 已是自有仓库
src/System/AutoStart.cs:16        # LegacyTaskName
src/UI/AboutForm.cs:20-23,84,95   # 双份版权 + 上游致谢链接
```

---

## 5. 发布打包流程

### 5.1 一条命令

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
pwsh -File scripts/pack-release.ps1 -Version 1.0.0 -Dotnet 'G:\TOOL\dotnet-sdk\dotnet.exe'
```

产物：`dist/LiteMonitorPlus_v1.0.0-win-x64.zip`（约 6.2 MB，58 个条目），脚本末尾输出包 SHA256。

> `-Dotnet` 可省略：脚本会依次尝试 `$env:LITEMONITOR_DOTNET` 与 PATH 上的 `dotnet`；
> 若两者都不可用会直接报错并提示，不会静默用错 SDK。

### 5.2 脚本做了什么

1. **自检一致性**（任一不符即中止打包）
   - 包名 vs `UpdateChecker.cs` 的下载模板 `LiteMonitorPlus_v{0}-win-x64.zip`
   - `LiteMonitor.csproj` 的 `<Version>` vs `-Version`
   - `resources/version.json` 的 `version` vs `-Version`
2. **先发 Updater**（单文件 / 框架依赖），刷新仓库内 `resources\LiteMonitor++.Updater.exe`，
   再发主程序 —— 主程序的 `Content` 引用的是仓库里那份，顺序反了会把旧版打进包。
3. **平铺运行时资源**到 `resources/`：`driver.zip` / `LiteMonitorPlusFPS.exe` / `CityCode.json`。
4. **删除 `resources/assets/`**：csproj 会把 `CityCode.json` 复制进去，与平铺副本重复。
5. **打入 LICENSE / NOTICE**（MIT 分发义务，每个分发副本都必须带）。
6. 压缩并输出 SHA256。

### 5.3 发布包结构

```
LiteMonitor++.exe                 主程序
LiteMonitor++.dll / .deps.json / .runtimeconfig.json
LibreHardwareMonitorLib.dll 等    运行时依赖（见下方说明）
resources/
  LiteMonitor++.Updater.exe       自动更新组件（单文件）
  driver.zip                      驱动包（含 PawnIO 安装包 + FPS 组件）
  LiteMonitorPlusFPS.exe          FPS 帧率统计组件
  CityCode.json                   离线城市库
  lang/ (9)  themes/ (10)  plugins/ (12)
使用说明(必读).txt
LICENSE  NOTICE
```

**为什么 `DiskInfoToolkit.dll` / `HidSharp.dll` / `BlackSharp.Core.dll` / `RAMSPDToolkit-NDD.dll` /
`Mono.Posix.NETStandard.dll` / `MonoPosixHelper.dll` 必须在包里**：
它们是 LibreHardwareMonitorLib 0.9.6 的真实传递依赖**（`deps.json` 依赖图可证），
源码里搜不到名字只是因为它们是被延迟加载/反射调用的传感器后端（磁盘信息、SPD 内存传感器等）。
**删掉会直接导致传感器读不到。**

**为什么 `app.ico` 与 7 个 PNG 不在包里**：
它们是**编译期嵌入资源**（`Properties/Resources.resx` 以 `ResXFileRef` 引用 `resources/assets/*`），
运行时不从磁盘读取。仓库里必须保留（构建需要），但不必分发。

### 5.4 图标

应用图标由 `scripts/make-icon.ps1` **纯代码生成**（GDI+ 绘制 + 自写多帧 ICO 编码器），
不依赖任何外部素材，也不复用上游的「黄闪电 + 薄荷绿圆底」。

```powershell
pwsh -File scripts/make-icon.ps1
```

- 7 帧：16 / 24 / 32 / 48 / 64 / 128 / 256，32 位未压缩 DIB（兼容性优于 PNG 压缩帧）；
- 32 px 以下「++」不可辨识，小尺寸帧自动改用无后缀的简化构图；
- 「++」位置按**「到闪电的垂线距离 == 到圆角边界的垂线距离」**这条平衡条件解出，
  改动闪电或加号尺寸时位置会自动重解（推导写在脚本注释里）；
- 脚本附带 ICO 结构回读校验，产出**可复现**（同输入必得同字节）。

---

## 6. 发布前 checklist

### 6.1 每次发布前必做的 10 件事

1. 确认 `LiteMonitor.csproj` 的 `<Version>`、`resources/version.json` 的 `version`、
   `-Version` 参数**三处一致**；
2. 确认 `resources/version.json` 的 `changelog` / `releaseDate` 写的是**本次**的变更；
3. 跑 `pwsh -File scripts/pack-release.ps1 -Version <版本>`，确认脚本的自检全过；
4. 检查产物名是 `LiteMonitorPlus_v<版本>-win-x64.zip`（**与 `UpdateChecker` 模板严格一致**）；
5. 确认包内含 `LICENSE` 与 `NOTICE`（MIT 强制义务，**最容易漏、后果最严重**）；
6. 确认包内 `resources/` 下有 `driver.zip` / `LiteMonitorPlusFPS.exe` / `CityCode.json`；
7. 若改动了 `driver.zip` 或 FPS 组件，**同步更新 `DriverInstaller.cs` 的两个 SHA256 常量**
   （`DriverPackageSha256` / `FpsComponentSha256`）并重新核对；
8. 跑 `git grep -n -E 'litemonitor\.cn|Diorser' -- '*.cs' '*.json' '*.csproj'`，
   确认命中项**全部在 C 表（刻意保留）内**；
9. 跑二进制字符串审计，确认**无上游网络端点**（`gitee.com/Diorser` / `github.com/Diorser` /
   `raw.githubusercontent.com/Diorser` 全部 0 命中）；
10. 上传 Release 资产时**用脚本输出的同名文件名**，再把 `resources/assets/` 下的资源推到仓库
    （驱动源 / FPS 源 / 城市库都从仓库 raw 取）。

### 6.2 功能验证清单

| # | 验证项 | 期望 | 自动 / 人工 |
|---|---|---|---|
| 1 | 启动 | 进程名 `LiteMonitor++`，UI 正常起来，无模态弹窗阻塞 | ✅ 已自动验证 |
| 2 | 单实例 | 第二个实例立即退出（ExitCode=0） | ✅ 已自动验证 |
| 3 | 关于窗口 | 双份版权 + 链接指向自有仓库 | ✅ 已自动验证 |
| 4 | 崩溃日志 | `LiteMonitor++_Error.log` | ✅ 路径常量已验证 |
| 5 | 检查更新 | 无上游域名 | ✅ 二进制审计已验证 |
| 6 | 驱动安装 | 走**本地** `resources/driver.zip` 成功；改错哈希须**拒绝执行** | ⚠️ 人工（需提权） |
| 7 | FPS 监控 | `LiteMonitorPlusFPS.exe` 启动并出数 | ⚠️ 人工（需在发布包上验） |
| 8 | 开机自启 | 任务计划出现 `LiteMonitor++_AutoStart`；旧的 `LiteMonitor_AutoStart` 已消失 | ⚠️ 人工（需管理员） |
| 9 | 防火墙引导 | 文案名称与 exe 一致 | ✅ 已自动验证 |
| 10 | Web 监控 | `http://localhost:5000` 标题 = `LiteMonitor++ WebServer` | ✅ 已端到端验证 |
| 11 | 主题编辑器 | 标题 = `LiteMonitor++ Theme Editor` | ✅ 已自动验证 |
| 12 | 插件 | 12 个插件可加载；城市库断网可用 | ✅ 已自动验证（JSON 合法 + 城市库在包内） |
| 13 | 多语言 | 9 种语言无旧品牌残留 | ✅ 已自动验证 |
| 14 | 卸载/清理 | 计划任务、崩溃日志、托盘图标无残留 | ⚠️ 人工 |

> Web 面板默认 `WebServerEnabled = false`（`Settings.cs:141`），不开时 5000 端口不监听属预期。
> 验证方法：改包内 `settings.json` 的 `WebServerEnabled` 为 `true` 后重启。

---

## 7. 已知阻塞项

| # | 阻塞项 | 影响 | 谁来解决 |
|---|---|---|---|
| 1 | **上游 LICENSE 文件仍缺失** | 上游仓库根目录没有 LICENSE 文件（GitHub API `"license": null`），其 MIT 授权意图仅见于 README | **只能由用户去提**：在 `github.com/Diorser/LiteMonitor/issues` 开 issue 或邮件 `tueur2010@gmail.com` 索取。当前 `NOTICE` 已如实写明「上游仓库根目录尚未提交 LICENSE 文件，授权意图以其 README 声明为准」 |
| 2 | **已解除：自有仓库 `mojingjing-git/LiteMonitorPlus` 已创建** | 2026-10-21 已按用户授权创建公开 GitHub 仓库并推送源码、资源和修复后的更新器；默认分支为 `master`，匹配现有资源 URL | 交付验收见 `docs/reviews/2026-10-21-remediation-validation.md`；正式 Release 仍见下一项，Gitee 镜像未在本次创建 |
| 3 | **自有发布源尚未就绪** | `https://github.com/mojingjing-git/LiteMonitorPlus/releases/download/v{0}/LiteMonitorPlus_v{0}-win-x64.zip` | 仓库建好后创建 Release，资产名用脚本输出的同名 |
| 4 | 无官网 | `{{SITE}}` 为空，所有原「官网」位改为「上游项目」致谢链接 | 可选，后续有域名再加 |

---

## 8. 去 emoji 规范（软件内禁用 emoji）

软件内**所有用户可见内容**禁用 emoji（彩色图标），改为纯文字或纯文本符号。

### 8.1 适用范围

| 范围 | 处理 |
|---|---|
| `src/**/*.cs` 的**字符串字面量** | 必须去 emoji |
| `resources/lang/*.json`（9 个） | 必须去 emoji |
| `resources/plugins/*.json`（12 个） | 必须去 emoji |
| `resources/plugins/PLUGIN_DEV_GUIDE.md`、`README.md`、`README.en.md` | 必须去 emoji |
| **源码注释** | 不动（开发者可见，不是软件内内容） |
| `docs/**`、`src/UI/Settings/SETTINGS_README_DEV.md` | 不动（内部文档） |

### 8.2 保留的符号（这些**不是** emoji，删了会丢语义）

| 符号 | 码位 | 用途 |
|---|---|---|
| `↑` `↓` | U+2191 / U+2193 | 上行/下行方向语义。语言包 `Short.NET.Up` `Short.NET.Down` 用它取代原先的彩色三角 |
| `↑↑` `↓↓` | U+2191 ×2 | 每日流量累计，取代原先的彩色双三角（单三角=即时速率，双三角=日累计） |
| `→` | U+2192 | 文档与插件标题里的方向指示 |
| `▲` `▼` | U+25B2 / U+25BC | 任务栏监控项的排序按钮 |
| `★` | U+2605 | 中文注释里的章节标记（**源码注释里 2262 处，全仓保留**） |
| `©` | U+00A9 | 版权声明（MIT 义务，不可删） |

### 8.3 承载语义、不能删成空串的几处

| 位置 | 原值 | 现值 |
|---|---|---|
| `PluginConstants.STATUS_ERROR` | 彩色叉号 | `[ERROR]` |
| `PluginConstants.STATUS_UNKNOWN` | 彩色问号 | `[UNKNOWN]` |
| `PluginConstants.STATUS_EMPTY` | 禁止通行号 | `[EMPTY]` |
| `MetricUtils` 充电单位后缀 | 闪电号 | `+` |
| `TaskbarPage` 取色按钮 | 画笔图标 | `LanguageManager.T("Menu.ScreenColorPickerShort")`（9 个语言包各给一个单字） |
| `Weather.json` 天气映射 | 天气图标 | 天气文字本身（如 `晴`） |
| `Weather.json` 三个 `emoji_*` 显示模式 | 图标 | 文字模式。**`value` 键 `emoji_temp` / `emoji_only` / `emoji_range` 不得改名**，否则用户已保存的设置会失效 |
| `WeiboHot.json` 榜单标记 | 图标 | 文字 + 一个尾空格（`爆 ` / `热 ` / `新 ` / `荐 ` / `沸 `） |

### 8.4 审计命令

```powershell
# 软件内不应再有 emoji（src/ 需排除注释，可用脚本的字符串感知扫描）
# 仓库内仍有 emoji 的文件（应只剩 docs/ 与源码注释）
Get-ChildItem -Recurse -Include *.cs,*.json,*.md,*.txt |
  Where-Object { $_.FullName -notmatch '\\(bin|obj|dist|\.git)\\' } |
  ForEach-Object {
    $t = Get-Content $_.FullName -Raw
    if ($t -match '[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF]') { $_.FullName }
  }
```

---

## 9. 常用命令速查

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'

# 启动 dev 版（构建 + 平铺驱动/FPS 资源 + 启动）
& pwsh -File scripts/run-dev.ps1
& pwsh -File scripts/run-dev.ps1 -NoLaunch        # 只构建不启动

# 构建
& $dotnet build 'LiteMonitor.csproj' -c Release -v quiet

# 发布 Updater（必须 publish，不能 build —— PublishSingleFile 只在 publish 生效）
& $dotnet publish 'LiteMonitor.Updater\LiteMonitor.Updater.csproj' -c Release -r win-x64 --self-contained false
Copy-Item 'LiteMonitor.Updater\bin\Release\net8.0-windows\win-x64\publish\LiteMonitor++.Updater.exe' `
          'resources\LiteMonitor++.Updater.exe' -Force

# 打包发布
& pwsh -File scripts/pack-release.ps1 -Version 1.0.0 -Dotnet $dotnet

# 重新生成图标
& pwsh -File scripts/make-icon.ps1
```

> ⚠️ **禁止 `git add -A`** —— 会把其他会话的未跟踪文件一并提交。
> 提交前用 `git status --short` 核对暂存范围。

### 9.1 dev 版与发布版的差异

| 项 | 发布包 | dev 版（`scripts/run-dev.ps1`） |
|---|---|---|
| 驱动包 `resources/driver.zip` | 打包时平铺，**离线可装** | 脚本同样会平铺，**离线可装** |
| FPS 组件 `resources/LiteMonitorPlusFPS.exe` | 打包时平铺 | 脚本同样会平铺 |
| 单实例互斥体 | 按 exe 所在目录生成 | 同上，**与发布版目录不同 → 可并存** |
| `settings.json` / 崩溃日志 | 发布包目录 | `bin\Debug\net8.0-windows\`，与发布版隔离 |
| 提权 | `app.manifest` 要求管理员，启动必弹 UAC | 同上 |
