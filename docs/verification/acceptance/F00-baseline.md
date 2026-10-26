# F00 基线固化记录（2026-10-25）

> W0 协调者任务产物。外派包（`docs/plans/dispatch/`）全部以此为"开工基线"；与本文不符时先停手、在验收记录中报告差异。

## 1. 版本身本

| 项 | 值 |
|---|---|
| HEAD（开工时） | `98c9237`（docs(plan) 主题市场执行记录，纯文档） |
| 母计划代码基线 | `3ae64e8`（ci 门禁上调 + M5 测试） |
| 验证基线 fork | `2a1c57d`（上游 `3b91b5c` / 1.3.7） |

`2a1c57d..HEAD` 源码改动仅 3 个文件（主题市场 M5/M6）：`src/Plugins/PluginMarket.cs`、`src/Plugins/ThemeMarket.cs`、`src/UI/Settings/ThemePreviewService.cs`。

**33 个任务目标文件逐一 diff 核验：32 个与验证基线逐字节一致**；唯一漂移 `ThemePreviewService.cs`（F14a 目标）⇒ **F14a 执行者必须重新定位行号引用**；验证报告引用的 `PluginMarketPage.cs:65-90` 未漂移。

## 2. 静态门禁（禁止外派包修改；协调者批次集成时统一上调）

| 变量 | 值 | 位置 |
|---|---|---|
| `MIN_TEST_TOTAL` | 1876 | ci.yml:90、release.yml:67（两文件逐字一致是仓库惯例） |
| `MAX_SKIPPED_TESTS` | 1 | ci.yml:100、release.yml:70 |

## 3. 依赖解析（F09 的输入）

- `LibreHardwareMonitorLib` **0.9.6**（LiteMonitor.csproj:49）
- `DiskInfoToolkit` **1.1.2**（传递依赖，obj/project.assets.json）
- LHM 上游 NVML 修复 PR #2539 已合并（merge `3aa9c6c`），**是否进入任何已发布 NuGet 包未经证实**——这正是 P-F09a 要回答的问题。

## 4. 执行机环境（实机可用性判定用）

| 项 | 值 | 对派发包的含义 |
|---|---|---|
| OS | Windows 11（10.0.26300） | Win10 任务栏路径（F05）本机不可验，标记待实机 |
| GPU | **NVIDIA RTX 3080 + RTX 3080 Ti**（另有多块虚拟显示适配器：GameViewer/Meta/MuMu/Todesk） | **双同型号系 NVIDIA 卡本机存在**：F09b 的 NVML 场景可部分本机实测；F07 的"双同型号 GPU 迁移"可用夹具+本机双卡 |
| 显示器 | 含虚拟显示器拓扑 | F04/F05/F06 多屏/DPI 场景以夹具与 Win32 测试宿主为准，人工多屏项待实机 |
| 工具链 | dotnet（C:\Program Files\dotnet）、**pwsh 7**（run-probes.ps1 必须用 pwsh，5.1 会因 Resolve-Dotnet.ps1 的 #requires 拒跑） | |

⚠️ 机器差异声明：验证报告自称"本机为单卡 AMD 台式机"，与当前执行机（双 NVIDIA）不符。验证的**代码级取证结论不受影响**（引用抽查全部命中）；其"诚实清单"中未复现的行为项本来就列为待实机，不因换机升级。

## 5. 探针基础设施（F00-2 已完成）

- **WebProbe 入库**：`tests/WebProbe/`（Program.cs + WebProbe.csproj，代码自 `.tmp/verification/webprobe/` 原样迁移，未改一字）。反射驱动真实产品代码（Settings/LiteWebServer/WebSessionManager），裸 TCP 手写字节；`System.Text.Json 10.0.3` 必须与产品一致。
- **run-probes.ps1 新增 `web` 组**：`pwsh scripts/run-probes.ps1 -Groups web -SkipUnitTests` = 构建主工程（同配置）→ 构建 WebProbe → 以沙箱 settings 跑 12 用例矩阵 → 完整输出落 `docs/reviews/evidence/appearance-probes/WebProbe/run-output.txt`，settings 沙箱在同级 `settings/`（`SettingsHelper.PathOverride` 隔离，不碰用户 settings.json）。
- **冒烟结果（2026-10-25，PASS）与"修复前"锚点**：

| 用例 | 结果 | 含义 |
|---|---|---|
| C1～C4（#589 四种头名大小写） | 101 | 既有修复保持 |
| **C5（Upgrade 无空格）/ C6（两个空格）** | **200 + 登录页 HTML** | **WEB-OWS 复现（P-F01 的红灯基线）** |
| **C7（Host=隧道域名）** | **403** | **#580 复现（P-F02 的红灯基线）** |
| C9/C10（Key 非法/重复） | 400 | 保持 |
| C11（无票据） | 401 | 保持 |
| C12（X-Forwarded-Host=隧道域名） | 101 | **死设置行为级证明**：转发头被完全忽略 |

- **Win32 探针不存在**：`.tmp/verification/` 只有 webprobe 与两份结果 txt；P-F04/P-F05/P-F06 需自建 Win32 测试宿主（派发单已写明）。

## 6. 编辑注意事项（给所有会改 `.ps1` 的人）

`scripts/run-probes.ps1` 为 **UTF-8 带 BOM** 编码（Windows PowerShell 5.1 兼容）。部分编辑器/工具写回会剥 BOM，5.1 即按 ANSI 误读中文导致解析错乱。写回后若 5.1 报怪异解析错，用 pwsh 补 BOM：

```powershell
$p='scripts/run-probes.ps1'; $c=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8);
[IO.File]::WriteAllText($p,$c,(New-Object Text.UTF8Encoding $true))
```

## 7. 工作树状态（开工时）

未跟踪：`docs/UPSTREAM-*`（3 份）、`docs/development-windows-firewall.md`、`docs/plans/2026-10-25-*`（2 份）、`docs/verification/`；已修改：`scripts/run-probes.ps1`；新增：`tests/WebProbe/`。以上均为 F00/文档产物，**外派包开工前由用户决定是否先提交**（建议提交，作为派发基线点）。

## 8. 验收记录基础设施（F00-3 已完成）

`docs/verification/acceptance/` 已建；模板见 [TEMPLATE.md](TEMPLATE.md)；每个 P-F0x 完成后写 `F0x-report.md`。
