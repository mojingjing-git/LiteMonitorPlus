# 验证证据记录模板（E08 基础）

- 日期：2026-10-26 引入（完善计划 §12 第二条 / P-E08a）。
- 用途：每一批验收结果（acceptance 报告、回归复验、性能/隔离 A/B）附一份按本模板填写的证据记录，
  使"报告结论"与"实际构建产物、实际执行的测试"可以逐项对账。
- 填写方式：机器可推导字段用 `scripts/collect-evidence.ps1` 生成（见 §12），人工判断字段（跳过原因、
  解释、遗留）手写补充。允许整段引用脚本输出，但不得改写数字。
- 原则：
  1. 测试版本与实际运行版本分开记录——测到的是哪份 DLL，就记录哪份 DLL 的摘要。
  2. 跳过必须逐条给原因；"本地没环境"记录为待验，不得写成门禁通过。
  3. 数字一律以 TRX（total/executed/passed/failed/notExecuted 逐用例 outcome）或裸命令退出码为准，
     不以控制台摘要、提交标题或记忆为准。

## 证据记录

<!-- 复制以下全部内容到具体报告；`【脚本】`标记的字段可由 collect-evidence.ps1 生成，
     `【人工】`标记的字段必须人工填写。 -->

### 1. 源状态 【脚本】

| 字段 | 值 |
|---|---|
| 仓库根 | `<绝对路径>` |
| 分支 | `<git rev-parse --abbrev-ref HEAD>` |
| 源 SHA（HEAD） | `<git rev-parse HEAD>` |
| 工作树状态 | `干净` / `dirty（见下）` |
| dirty 补丁摘要 | `git status --porcelain` 逐行清单；若有已跟踪文件改动，附 `git diff` 输出的 SHA-256 |
| 未跟踪文件 | 逐行清单（含本机材料如 `.cua-helper/` 等，注明"不入库"） |
| 观察基线（可选） | `<对比用 SHA>` |

### 2. 配置 【脚本】

| 字段 | 值 |
|---|---|
| 构建配置 | `Debug` / `Release`（含测试运行配置） |
| TargetFramework | `net8.0-windows` |
| 输出路径覆盖 | 无 / `-p:BaseOutputPath=bin/<profile>/`（逐条列出所用覆盖属性） |
| 环境变量 | `NUGET_PACKAGES`、`DOTNET_CLI_HOME`、`LITEMONITOR_HOST_EXE`（如有）实际取值 |
| 主程序/Host 目录锚点 | Host 目录、主输出目录（构建清单锚点）绝对路径 |

### 3. 产物摘要 【脚本】

> 摘要口径：SHA-256（大写十六进制），与 `I2HostBinaries.ComputeSha256` / 测试输出 `[I2HostBinaries]` 证据行一致。

#### 3.1 测试程序集

| 程序集 | 路径 | SHA-256 | 修改时间(UTC) |
|---|---|---|---|
| LiteMonitor.Tests | `<…\LiteMonitor.Tests.dll>` | `<…>` | `<…>` |
| LiteMonitor.Updater.Tests | `<…>` | `<…>` | `<…>`（未运行则注"未运行"） |
| LiteMonitor.GpuNonperformance.Tests | `<…>` | `<…>`（未运行则注"未运行"） | `<…>` |

#### 3.2 主程序 / Host / Contracts

| 产物 | 路径 | SHA-256 | 备注 |
|---|---|---|---|
| 主程序 | `<…\bin\<cfg>\net8.0-windows\LiteMonitor++.exe/.dll>` | `<…>` | 实际运行版本 |
| Host | `<…\src\HardwareHost\bin\<cfg>\net8.0-windows\LiteMonitor.HardwareHost.exe/.dll>` | `<…>` | 测试实际定位使用的目录；如有 `LITEMONITOR_HOST_EXE` 注明 |
| Contracts | `<…\LiteMonitor.Contracts.dll>` | `<…>` | Host 目录内副本；另有测试输出目录引用副本时并列记录 |

一致性结论【人工】：测试引用的 Contracts/Host 是否与 3.2 记录同源同摘要；I2 测试的
`[I2HostBinaries]` 证据行（TRX StdOut 内）是否与上表吻合。

### 4. 依赖锁定情况 【脚本】

| 字段 | 值 |
|---|---|
| NuGet 锁文件 | 仓库当前未启用 packages.lock.json；以各工程 `obj/project.assets.json` 为准 |
| `project.assets.json` | 逐工程列出：路径 + `libraries` 数量 + 关键直接依赖（如 LibreHardwareMonitorLib/0.9.6、Vortice.Direct2D1/3.8.3、System.Text.Json/10.0.3、xunit/2.9.2） |
| 包缓存 | `NUGET_PACKAGES` 实际路径 |
| 包源 | restore 日志中实际使用的源（公司内网/nuget.org 等） |

### 5. 执行命令 【人工/脚本】

逐条记录实际执行的命令与退出码（原样，不得事后"整理"）：

```text
<命令 1>            → 退出码 <n>
<命令 2>            → 退出码 <n>
```

### 6. 测试结果（TRX 口径）【脚本+人工】

> 口径：TRX 的 `total / executed / passed / failed / notExecuted` 计数器 + 逐用例 outcome。
> 注意 notExecuted=0 不等于没有跳过（xunit 的 Skip 体现为 outcome=NotExecuted 的用例，须逐条列出）。

| 程序集 | 发现(total) | 执行(executed) | 通过 | 失败 | 跳过(notExecuted) | TRX 文件 |
|---|---|---|---|---|---|---|
| LiteMonitor.Tests | | | | | | `<路径>` |
| LiteMonitor.Updater.Tests | | | | | | `<路径>` |
| LiteMonitor.GpuNonperformance.Tests | | | | | | `<路径>` |

跳过用例逐项原因【人工】：

| 用例全名 | 跳过原因 | 后续动作 |
|---|---|---|

失败用例逐项处置【人工】（如有）：

| 用例全名 | 失败信息摘要 | 定位结论 |
|---|---|---|

未执行项说明【人工】：本次未跑的程序集/配置（如 Release、GPU 性能、完整 CI 门禁）逐条说明原因，
不得写"完整发布门禁通过"。

### 7. 机器环境 【脚本】

| 字段 | 值 |
|---|---|
| OS | `<Caption> <Version> <Build>` |
| CPU / 内存 | `<名称>` / `<总容量>` |
| .NET SDK | `<dotnet --version>` |
| PowerShell | `<PSVersionTable.PSVersion>`（脚本收集环境用） |
| 是否有目标硬件/GPU 驱动 | 有 / 无（影响 F 系列/性能项结论效力） |

### 8. 人工记录与解释 【人工】

- 本轮结论、与上一轮的差异、发现的问题与遗留。
- GUI/实机步骤（如执行过）：按冒烟清单逐步记录"步骤 → 期望 → 实际 → 判定"。
- 与既有报告（如审查报告 G1～G5、R1～R9）条目的对应关系。

## collect-evidence.ps1 使用说明

```powershell
# 生成证据骨架（不上传、不改 CI、只读取仓库与产物）：
pwsh -NoProfile -File scripts/collect-evidence.ps1 -RepoRoot . -Configuration Debug -OutFile .tmp/evidence-<批次>.md
# 可选：
#   -TrxRoot <目录>          递归统计该目录下 *.trx 的 total/executed/passed/failed/notExecuted
#   -IncludeAssetsJson       附各工程 project.assets.json 的依赖摘要
#   -HostExe <路径>          指定实际使用的 Host 产物（默认用 src/HardwareHost/bin/<配置>/net8.0-windows）
```

脚本只生成模板 §1/§2/§3/§4/§5(命令行回显)/§6(计数器)/§7 的机器字段；
§6 的逐项原因、§8 的人工解释必须手写。脚本输出仅写入 `-OutFile` 指定路径。
