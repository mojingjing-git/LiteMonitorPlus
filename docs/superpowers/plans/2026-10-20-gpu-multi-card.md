# GPU 多显卡监控改造 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 LiteMonitor 同时显示 N 张显卡的独立指标（温度/占用/功耗/频率/显存/风扇），并顺带修复「GPU.Fan 风扇读数串到另一张卡」的既有 bug。

**Architecture:** 监控项 Key 从单卡的 `GPU.Load` 扩展为带序号的 `GPU.{index}.{Metric}`。序号即被监控显卡在监控集合中的下标。该命名同时满足三个既有机制，使它们零改动继续工作：`MetricUtils.GetType` 走子串匹配（`GPU.1.Temp` 仍含 `TEMP`）、`Settings.IsAnyEnabled("GPU")` 走前缀匹配、`MonitorItemConfig.UIGroup` 取 `Key.Split('.')[0]`（仍归入 `GPU` 分组）。数据层在 `SensorMap.Rebuild` 中按序号为每张卡建立独立映射；显示层在标签/阈值/峰值/历史四处做「带序号 Key → 基础 Key」的归一化回退。

**Tech Stack:** C# / .NET 8 (`net8.0-windows`) / WinForms / LibreHardwareMonitorLib 0.9.6 / xUnit（仅测试工程）

## Global Constraints

- 目标框架固定 `net8.0-windows`；使用本地 SDK `G:\TOOL\dotnet-sdk\dotnet.exe`（版本 8.0.426），**不得**安装到系统或修改系统 PATH。
- 除测试工程外**不得新增任何 NuGet 依赖**；主工程依赖保持 `LibreHardwareMonitorLib 0.9.6` + `System.Text.Json 9.0.10` 不变。
- **向后兼容硬约束**：`Settings.MonitoredGpuCount` 默认值必须是 `1`。在默认配置下，程序行为与改造前**逐项一致**（含 GPU.Fan 读数来源从「最后一张卡」纠正为「第一张卡」——这是 bug 修复，属预期差异）。
- 不得修改 `resources/plugins/` 下 13 个内置插件 JSON 的任何内容。
- 不得修改 `resources/themes/`、`resources/lang/` 中除新增菜单文案以外的任何现有条目。
- 提交粒度：**每个 Task 结束提交一次**，提交信息用中文正文 + 英文 type 前缀（如 `feat(gpu): ...`）。
- 全部分支工作基于 `feat/multi-gpu` 分支，从 `master`（含计划提交 `81bd139`）切出。
- 复用既有锁策略：硬件层持 `_lock`（`HardwareMonitor._lock`），UI 线程取值走 `Monitor.TryEnter(10ms)`，**不得**在 UI 线程发起阻塞式硬件调用。
  - ⚠️ `SensorMap` 另有自己的私有 `_lock`（`SensorMap.cs:37`），与 `HardwareMonitor._lock` **不是同一个对象**。获取顺序始终是 `HardwareMonitor._lock` → `SensorMap._lock`，不构成死锁；但跨这两个锁访问同一份状态时必须走「整体替换」而不是「边改边读」（见 Task 2 / Task 6 的 `GpuRegistry.ReplaceAll`）。

- 注释语言：与被改文件保持一致（`src/System/**` 用中文，`src/Core/**` 中英皆可），新增文件统一中文注释。
- **监控显卡数量必须按实际检测到的显卡数夹紧**（见 Task 4 / Task 5.5 / Task 12.3），否则 2 卡机器上选 8 会凭空多出 6 组永远没有数据的空白监控项。
- **带序号 Key 的语言包兜底必须归一化**（见 Task 7.5）：`LanguageManager.T("Items." + Key)` 遇到 `GPU.0.Load` 会回退成字面量 `GPU.0.Load`，凡是拼 `"Items." + Key` / `"Short." + Key` 的地方都要先过 `GpuKey.ToBaseKey`。
- **设置页只写 `MonitoredGpuCount`，不直接改 `MonitorItems`**（见 Task 13）：监控项统一由 Task 5.5 在保存路径生成，绕开 draft/live 机制。
- **行尾符**：仓库里的 `src/**` 源文件是 **CRLF**，而本计划文档是 **LF**。执行"把 A 替换为 B"时若直接用字符串 `Replace`，会因为 `\r\n` 与 `\n` 不一致而**匹配失败**。请先统一行尾（读取时把 `\r\n` 归一为 `\n`，写回时按目标文件的原样写回），或改用按行匹配。



## 仓库与工具链现状（已就绪，无需重复准备）

- Fork: `https://github.com/mojingjing-git/LiteMonitor`（上游 `Diorser/LiteMonitor`）
- 工作副本: `G:\TOOL\LiteMonitor-fork`，`origin`=fork，`upstream`=上游
- SDK: `G:\TOOL\dotnet-sdk\dotnet.exe`（8.0.426），命令前缀见下方「命令速查」
- 基线编译已通过（Release，0 错误；**基线本身约有 227 条既有警告**，本改造不要求清零，只要不新增错误即可）
  - 按本计划改完后，警告数会从 227 升到 **230~233**（只改到 Task 5 时是 230，改完全部 Task 是 233）——
    新增的几条全是新代码的可空性提示（`GpuItemSync.cs` 3×CS8601、`GpuRegistry.cs` 1×CS8625、
    `SystemHardwarPage.cs` 1×CS8618 + 1×CS8604），**错误数仍是 0**。看到 230+ 不要以为是出了问题。
  - ⚠️ 用 `grep ': warning '` 逐行数会得到 **454**（`-v minimal` 把同一条警告打印两遍），
    以 MSBuild 汇总行为准。

- `使用说明(必读).txt` 已被 csproj 引用但上游仓库未提交，已从本机安装目录补入工作区（**待 Task 1 提交**）

### 命令速查

```powershell
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
```

---

## File Structure

### 新建文件

| 文件 | 职责 |
|---|---|
| `src/Core/GpuKey.cs` | 显卡 Key 的解析/构造/归一化。纯静态、无依赖。 |
| `src/Core/GpuRegistry.cs` | 运行时「序号 → 显卡简称/全名」注册表，供 UI 打标签。 |
| `src/Core/GpuItemSync.cs` | 保证 `Settings.MonitorItems` 中的显卡项与 `MonitoredGpuCount` 一致，并完成旧格式迁移。 |
| `tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj` | xUnit 测试工程。 |
| `tests/LiteMonitor.Tests/GpuKeyTests.cs` | GpuKey 解析测试。 |
| `tests/LiteMonitor.Tests/GpuItemSyncTests.cs` | 监控项同步/迁移测试。 |
| `tests/LiteMonitor.Tests/HardwareRulesGpuNameTests.cs` | 显卡简称提取测试。 |
| `tests/LiteMonitor.Tests/MetricUtilsGpuKeyTests.cs` | 归一化与类型判定测试。 |

### 修改文件

| 文件 | 改动 |
|---|---|
| `src/Core/Settings.cs` | 新增 `MonitoredGpuCount`、`RecordedMaxGpuDynamic`、`DetectedGpuVramTotalGBByKey`。 |
| `src/Core/SettingsHelper.cs` | `Load()` 中调用 `GpuItemSync.Ensure`（**必须在 `InternAllStrings()` 之后**）；`InternAllStrings` 放行带序号的显卡 Key；`UpdateMaxRecord` 改为按基础 Key 比对并记录每卡峰值。 |

| `src/Core/MetricUtils.cs` | `GetType` 天然兼容（不改）；`GetAdaptivePercentage` 归一化 + 每卡峰值优先；显存容量模式按卡取总容量。 |
| `src/Core/MetricLabelResolver.cs` | 多卡标签加显卡简称；任务栏简称用显卡简称。 |
| `src/Core/HardwareHistoryLogger.cs` | 历史采样支持带序号的显卡 Key。 |
| `src/Core/Actions/AppActions.cs` | `ApplyMonitorLayout` 开头调用 `GpuItemSync.Ensure`（保存设置时同步 + 夹紧，见 Task 5.5）。 |
| `src/System/HardwareServices/HardwareRules.cs` | 新增 `GetShortGpuName`。 |
| `src/System/HardwareServices/SensorMap.cs` | 多卡映射核心；`CachedGpus` 列表；修 GPU.Fan 串卡。 |
| `src/System/HardwareServices/ComponentProcessor.cs` | Clock/Power 按序号取对应显卡。 |
| `src/System/HardwareServices/HardwareValueProvider.cs` | 显存按序号取；**带序号 Fan 键走 `UpdateMaxRecord`**（见 Task 8.0）；`OnUpdateTickStarted` 感知显卡数量变化。 |
| `src/System/HardwareMonitor.cs` | 刷新所有被监控显卡；新增 `GetMonitoredGpuCount()`（Task 12.3）与非阻塞的 `TryGetDetectedGpuCount()`（Task 5.5）。 |
| `src/System/HardwareServices/HardwareScanner.cs` | 新增只读缓存的 `CachedGpuCount`（Task 5.5，供 UI 线程在拿不到硬件锁时兜底）。 |
| `src/UI/UIController.cs` | **标签/短标签兜底按基础 Key 归一化**（见 Task 7.5）；运行时显卡数量被夹紧时重建布局（见 Task 12.3）。 |
| `src/UI/HardwareTrendForm.cs` | **历史曲线展示侧支持带序号 Key**（见 Task 11.5）。 |
| `src/UI/Settings/SystemHardwarPage.cs` | 新增「监控显卡数量」下拉（按实际检测卡数填充）。 |
| `src/UI/Helpers/MenuMonitorHelper.cs` | `ApplyMonitorLayout` 新增 `Settings cfg` 首参后的 3 处调用点同步改签名。 |

| `resources/lang/*.json`（9 个） | 新增 `Menu.GpuMonitorCount` 与 `Menu.GpuMonitorCountTip`。 |
| `.gitignore` | **新建**（当前仓库没有此文件），忽略 `bin/`、`obj/`。 |
| `LiteMonitor.csproj` | 排除 `tests\**`（SDK 默认 glob 会把测试文件编进主工程，见 Task 1 Step 3.5）。 |
| `使用说明(必读).txt` | 提交（补上上游 csproj 的 `Content` 引用了但未入库的文件）。⚠️ **缺失会导致编译失败** —— 实测报 `error MSB3030: 无法复制文件"使用说明(必读).txt"`，所以 Task 1 必须先把它补上再编译。 |


---

## Task 1: 工程基线与测试骨架

**Files:**
- Create: `tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj`
- Create: `tests/LiteMonitor.Tests/Placeholder.cs`
- Create: `.gitignore`（仓库当前没有此文件，是新建而非修改）
- Modify: `使用说明(必读).txt`（新增，从未提交状态转为已提交）
- Modify: `LiteMonitor.sln`（加入测试工程）
- Modify: `LiteMonitor.csproj`（**必须**: 把 `tests\**` 从主工程的编译 glob 里排除，见 Step 3.5）


**Interfaces:**
- Consumes: 无
- Produces: 可运行的 `dotnet test` 命令；`G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj` 引用主工程 `LiteMonitor.csproj`

- [ ] **Step 1: 切分支**

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
git checkout -b feat/multi-gpu
git branch --show-current
```

Expected: 输出 `feat/multi-gpu`

- [ ] **Step 2: 写 .gitignore**

> 仓库当前**没有** `.gitignore`（`Test-Path` 为 False），这一步是**新建**。

覆盖 `bin/`、`obj/`、`*.user`、`settings.json` 等运行期产物。写入 `G:\TOOL\LiteMonitor-fork\.gitignore`：


```gitignore
# 构建产物
bin/
obj/
*.user
*.suo

# 运行期数据（不应进版本库）
settings.json
settings.json.bak
HardwareHistory.json
HardwareHistory.json.bak
TrafficHistory.json
TrafficHistory.json.bak
*.log
```

- [ ] **Step 3: 创建测试工程文件**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <IsPackable>false</IsPackable>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LiteMonitor.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\LiteMonitor.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3.5: 把 `tests/**` 从主工程的编译范围里排除（必须做）**

> ⚠️ **漏了这一步, 整个计划在 Task 1 就编译不过**。
> `LiteMonitor.csproj` 位于**仓库根目录**, 而 SDK 的默认 glob 是 `**\*.cs`,
> 所以 `tests\LiteMonitor.Tests\*.cs` 会被**主工程一起编译**。
> 主工程没有引用 xunit, 于是每个 `[Fact]` / `[Theory]` / `Assert` 都会报
> `CS0246: 未能找到类型或命名空间 "Xunit"`。
> 实测: 4 个测试文件 → **109 个错误**; 而且后续每个 Task 的「编译并跑测试」都过不去
> （`dotnet test` 会先构建被污染的主工程）。
>
> `LiteMonitor.csproj` 里已经有 `LiteMonitor.Updater\**` / `LiteMonitor.Wpf\**` 的排除写法,
> 照着加 `tests\**` 即可。

在 `G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj` 中，把：

```xml
<ItemGroup>
  <Compile Remove="LiteMonitor.Updater\**" />
  <Compile Remove="LiteMonitor.Wpf\**" />
  <EmbeddedResource Remove="LiteMonitor.Updater\**" />
  <EmbeddedResource Remove="LiteMonitor.Wpf\**" />
  <None Remove="LiteMonitor.Updater\**" />
  <None Remove="LiteMonitor.Wpf\**" />
</ItemGroup>
```

替换为：

```xml
<ItemGroup>
  <Compile Remove="LiteMonitor.Updater\**" />
  <Compile Remove="LiteMonitor.Wpf\**" />
  <Compile Remove="tests\**" />
  <EmbeddedResource Remove="LiteMonitor.Updater\**" />
  <EmbeddedResource Remove="LiteMonitor.Wpf\**" />
  <EmbeddedResource Remove="tests\**" />
  <None Remove="LiteMonitor.Updater\**" />
  <None Remove="LiteMonitor.Wpf\**" />
  <None Remove="tests\**" />
</ItemGroup>
```

> `None` / `EmbeddedResource` 也要排除, 否则 `tests\**` 下的文件会被当成"内容"复制到输出目录。

改完立刻验证主工程仍然是 0 错误：

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
```

Expected: **0 错误**（约 227 条既有警告）。如果这里冒出成片的 `CS0246 ... Xunit`, 说明没改对。

- [ ] **Step 4: 写占位测试文件**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\Placeholder.cs`：

```csharp
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 冒烟测试：确认测试工程能引用主工程并运行。
    /// 后续 Task 会用真实测试替换本文件。
    /// </summary>
    public class Placeholder
    {
        [Fact]
        public void TestHarness_Works()
        {
            Assert.True(true);
        }
    }
}
```

- [ ] **Step 5: 把测试工程加入解决方案**

```powershell
$dotnet sln 'G:\TOOL\LiteMonitor-fork\LiteMonitor.sln' add 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj'
```

Expected: 输出 `Project ... added to the solution.`

- [ ] **Step 6: 运行测试确认骨架可用**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: `通过! - 失败: 0, 已通过: 1, 已跳过: 0, 总计: 1`（中文本地化输出）或等价的 `Passed! - Failed: 0, Passed: 1`

- [ ] **Step 7: 提交**

```powershell
git add .gitignore '使用说明(必读).txt' tests/ LiteMonitor.sln LiteMonitor.csproj
git commit -m "chore: 建立多显卡改造的工程基线与测试骨架

- 新增 xUnit 测试工程 tests/LiteMonitor.Tests
- 主工程 csproj 排除 tests/**，避免测试文件被主工程一起编译
- .gitignore 忽略构建产物与运行期数据文件
- 补交 使用说明(必读).txt（上游 csproj 的 Content 引用了它但该文件未入库；
  缺失会导致编译失败 MSB3030，必须在编译前补上）"

```

---

## Task 2: GpuKey 与 GpuRegistry

**Files:**
- Create: `src/Core/GpuKey.cs`
- Create: `src/Core/GpuRegistry.cs`
- Create: `tests/LiteMonitor.Tests/GpuKeyTests.cs`
- Delete: `tests/LiteMonitor.Tests/Placeholder.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `LiteMonitor.src.Core.GpuKey`：`const string Prefix = "GPU."`、`const int MaxGpuCount = 8`、`static bool TryParse(string key, out int index, out string metric)`、`static string Build(int index, string metric)`、`static string BaseKey(string metric)`、`static string ToBaseKey(string key)`
  - `LiteMonitor.src.Core.GpuRegistry`：`static void ReplaceAll(GpuInfo[] entries)`、`static void Set(int index, string shortName, string fullName)`、`static string GetShortName(int index)`、`static string GetFullName(int index)`、`static void Clear()`；以及 `LiteMonitor.src.Core.GpuInfo`（`ShortName` / `FullName`）


- [ ] **Step 1: 写失败测试**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\GpuKeyTests.cs`：

```csharp
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuKeyTests
    {
        [Theory]
        [InlineData("GPU.0.Temp", 0, "Temp")]
        [InlineData("GPU.1.Temp", 1, "Temp")]
        [InlineData("GPU.10.VRAM", 10, "VRAM")]
        [InlineData("gpu.3.power", 3, "power")]
        public void TryParse_带序号的Key_解析出序号与指标(string key, int idx, string metric)
        {
            bool ok = GpuKey.TryParse(key, out int index, out string m);
            Assert.True(ok);
            Assert.Equal(idx, index);
            Assert.Equal(metric, m);
        }

        [Theory]
        [InlineData("GPU.Temp")]
        [InlineData("GPU.VRAM")]
        [InlineData("CPU.Temp")]
        [InlineData("GPU.")]
        [InlineData("GPU.-1.Temp")]
        [InlineData("GPU.x.Temp")]
        [InlineData("")]
        [InlineData(null)]
        public void TryParse_非带序号或非法Key_返回false(string key)
        {
            Assert.False(GpuKey.TryParse(key, out _, out _));
        }

        [Fact]
        public void Build_拼接出带序号Key()
        {
            Assert.Equal("GPU.2.Power", GpuKey.Build(2, "Power"));
        }

        [Fact]
        public void BaseKey_还原基础Key()
        {
            Assert.Equal("GPU.Temp", GpuKey.BaseKey("Temp"));
        }

        [Theory]
        [InlineData("GPU.1.Temp", "GPU.Temp")]
        [InlineData("GPU.Temp", "GPU.Temp")]
        [InlineData("CPU.Power", "CPU.Power")]
        public void ToBaseKey_归一化到基础Key(string input, string expected)
        {
            Assert.Equal(expected, GpuKey.ToBaseKey(input));
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译错误 `CS0246: 未能找到类型或命名空间 "GpuKey"`

- [ ] **Step 3: 实现 GpuKey**

写入 `G:\TOOL\LiteMonitor-fork\src\Core\GpuKey.cs`：

```csharp
using System;
using System.Globalization;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 多显卡监控的 Key 命名规则。
    ///
    /// 单显卡 (旧格式) : "GPU.Load"
    /// 多显卡 (新格式) : "GPU.0.Load"  —— 序号 = 被监控显卡在监控集合中的下标 (从 0 开始)
    ///
    /// 之所以这样设计, 是为了让既有逻辑零改动地继续工作:
    ///   - MetricUtils.GetType 走子串匹配, "GPU.1.Temp" 仍含 "TEMP" -> Temperature
    ///   - Settings.IsAnyEnabled("GPU") 走前缀匹配, 仍能命中
    ///   - MonitorItemConfig.UIGroup 取 Key.Split('.')[0], 仍归到 "GPU" 分组
    /// </summary>
    public static class GpuKey
    {
        /// <summary>显卡 Key 前缀</summary>
        public const string Prefix = "GPU.";

        /// <summary>设置项允许的显卡数量上限</summary>
        public const int MaxGpuCount = 8;

        /// <summary>
        /// 解析带序号的显卡 Key。
        /// "GPU.1.Temp" -> (index=1, metric="Temp") ; "GPU.Temp" -> false
        /// </summary>
        public static bool TryParse(string key, out int index, out string metric)
        {
            index = -1;
            metric = "";
            if (string.IsNullOrEmpty(key)) return false;
            if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string rest = key.Substring(Prefix.Length);
            int dot = rest.IndexOf('.');
            if (dot <= 0) return false;

            if (!int.TryParse(rest.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out index))
                return false;
            if (index < 0) return false;

            metric = rest.Substring(dot + 1);
            return metric.Length > 0;
        }

        /// <summary>构造带序号的 Key: (1, "Temp") -> "GPU.1.Temp"</summary>
        public static string Build(int index, string metric) => $"{Prefix}{index}.{metric}";

        /// <summary>基础 Key: "Temp" -> "GPU.Temp" (语言包 / 阈值回退用)</summary>
        public static string BaseKey(string metric) => Prefix + metric;

        /// <summary>
        /// 把带序号的 Key 归一化成基础 Key, 用于查语言包 / 阈值 / 峰值记录。
        /// "GPU.1.Temp" -> "GPU.Temp" ; "GPU.Temp" -> "GPU.Temp"
        /// </summary>
        public static string ToBaseKey(string key)
        {
            if (TryParse(key, out _, out string metric)) return BaseKey(metric);
            return key ?? "";
        }
    }
}
```

- [ ] **Step 4: 实现 GpuRegistry**

写入 `G:\TOOL\LiteMonitor-fork\src\Core\GpuRegistry.cs`：

```csharp
using System;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 运行时显卡信息注册表: 把 "GPU.{序号}" 对应到显卡的简称 / 全名, 供 UI 打标签用。
    /// 由 SensorMap 在建立映射时写入。
    /// </summary>
    public static class GpuRegistry
    {
        // ★★★ 用 volatile 引用持有不可变快照 ★★★
        // SensorMap.Rebuild 是在锁外边填边改的, 读侧 (UI 线程) 可能看到
        // "第 0 张已更新、第 1 张还没更新"的半成品注册表。
        // 整体替换一个数组引用后, 读侧要么看到旧的完整快照, 要么看到新的完整快照。
        private static volatile GpuInfo[] _entries = new GpuInfo[GpuKey.MaxGpuCount];

        /// <summary>整体替换注册表 (原子操作, 读侧不会看到半更新状态)</summary>
        public static void ReplaceAll(GpuInfo[] entries)
        {
            var copy = new GpuInfo[GpuKey.MaxGpuCount];
            if (entries != null)
            {
                int n = Math.Min(entries.Length, copy.Length);
                for (int i = 0; i < n; i++) copy[i] = entries[i] ?? new GpuInfo();
            }
            _entries = copy;
        }

        /// <summary>登记某序号对应的显卡 (单项写入, 仅供调试/单点更新)</summary>
        public static void Set(int index, string shortName, string fullName)
        {
            if (index < 0 || index >= GpuKey.MaxGpuCount) return;
            var copy = (GpuInfo[])_entries.Clone();
            copy[index] = new GpuInfo { ShortName = shortName ?? "", FullName = fullName ?? "" };
            _entries = copy;
        }

        /// <summary>取显卡简称 (如 "3080 Ti")，越界返回空串</summary>
        public static string GetShortName(int index)
        {
            var e = _entries;
            return (index >= 0 && index < e.Length && e[index] != null) ? e[index].ShortName : "";
        }

        /// <summary>取显卡全名 (如 "NVIDIA GeForce RTX 3080 Ti")，越界返回空串</summary>
        public static string GetFullName(int index)
        {
            var e = _entries;
            return (index >= 0 && index < e.Length && e[index] != null) ? e[index].FullName : "";
        }

        /// <summary>清空注册表 (硬件重载时调用)</summary>
        public static void Clear() => ReplaceAll(null);
    }

    /// <summary>一张显卡的简称 / 全名。</summary>
    public sealed class GpuInfo
    {
        public string ShortName { get; set; } = "";
        public string FullName { get; set; } = "";
    }

}
```

- [ ] **Step 5: 删除占位测试并运行测试**

删除 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\Placeholder.cs`，然后：

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过（17 个测试用例：TryParse 4 + 非法 Key 8 + Build 1 + BaseKey 1 + ToBaseKey 3）


- [ ] **Step 6: 提交**

```powershell
git add src/Core/GpuKey.cs src/Core/GpuRegistry.cs tests/
git commit -m "feat(gpu): 新增多显卡 Key 命名规则与运行时显卡注册表

GpuKey 负责带序号 Key 的解析/构造/归一化, 使既有 GetType/IsAnyEnabled/UIGroup
逻辑零改动兼容多卡; GpuRegistry 供 UI 在打标签时取显卡简称。"
```

---

## Task 3: HardwareRules 显卡简称提取

**Files:**
- Modify: `src/System/HardwareServices/HardwareRules.cs`（在 `ShouldUseSharedMemory` 之后追加方法）
- Create: `tests/LiteMonitor.Tests/HardwareRulesGpuNameTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `LiteMonitor.src.SystemServices.HardwareRules.GetShortGpuName(string name) -> string`

- [ ] **Step 1: 写失败测试**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\HardwareRulesGpuNameTests.cs`：

```csharp
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    public class HardwareRulesGpuNameTests
    {
        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080 Ti", "3080 Ti")]
        [InlineData("NVIDIA GeForce RTX 3080", "3080")]
        [InlineData("AMD Radeon RX 7900 XTX", "7900 XTX")]
        [InlineData("AMD Radeon(TM) Graphics", "Graphics")]
        [InlineData("AMD Radeon(TM) RX Vega 8", "Vega 8")]
        [InlineData("AMD Radeon RX 6800 XT", "6800 XT")]
        [InlineData("Intel Arc B580", "B580")]
        [InlineData("Intel(R) UHD Graphics 770", "UHD Graphics 770")]
        [InlineData("Intel Arc Graphics", "Graphics")]
        public void GetShortGpuName_剥离厂商与系列前缀(string full, string expected)

        {
            Assert.Equal(expected, HardwareRules.GetShortGpuName(full));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void GetShortGpuName_空输入返回空串(string input)
        {
            Assert.Equal("", HardwareRules.GetShortGpuName(input));
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译错误 `CS0117: "HardwareRules" 不包含 "GetShortGpuName" 的定义`

- [ ] **Step 3: 实现 GetShortGpuName**

在 `src/System/HardwareServices/HardwareRules.cs` 的 `ShouldUseSharedMemory` 方法之后、`class` 结束括号之前插入：

```csharp
        /// <summary>
        /// 从显卡全名提取简短显示名, 用于多显卡监控时区分不同显卡。
        /// "NVIDIA GeForce RTX 3080 Ti" -> "3080 Ti"
        /// "AMD Radeon RX 7900 XTX"      -> "7900 XTX"
        /// "Intel Arc B580"              -> "B580"
        /// </summary>
        public static string GetShortGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            // 0. ★★★ 先剥离注册商标标记并归一化空格 ★★★
            //    LHM 里 AMD 核显的原始名就是 "AMD Radeon(TM) Graphics"。
            //    如果不先剥掉 "(TM)"，下面的前缀匹配会全部失配:
            //    "AMD Radeon " (带空格) 匹配不上 "AMD Radeon(TM)"，
            //    于是退化成命中 "AMD "，留下 "Radeon(TM) Graphics"。
            string n = name
                .Replace("(R)", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("®", " ")
                .Replace("™", " ");
            n = string.Join(" ", n.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            // 1. 剥离厂商前缀
            string[] vendorPrefixes =
            {
                // 注意: 这里**不能**写 "Intel(R) " —— Step 0 已经把 "(R)" 剥成空格,
                // "Intel(R) UHD Graphics 770" 归一化后是 "Intel UHD Graphics 770",
                // 那个前缀永远匹配不到, 留着只是误导。
                "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ",
                "Intel ", "Apple ", "Microsoft "
            };

            foreach (var p in vendorPrefixes)
            {
                if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    n = n.Substring(p.Length).Trim();
                    break;
                }
            }

            // 2. 剥离系列前缀 (RTX / RX / Arc ...)
            string[] seriesPrefixes = { "RTX ", "GTX ", "RX ", "Radeon ", "Arc " };
            foreach (var s in seriesPrefixes)
            {
                if (n.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                {
                    n = n.Substring(s.Length).Trim();
                    break;
                }
            }

            return n.Length > 0 ? n : "";
        }
```

- [ ] **Step 4: 运行测试确认通过**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过

- [ ] **Step 5: 提交**

```powershell
git add src/System/HardwareServices/HardwareRules.cs tests/LiteMonitor.Tests/HardwareRulesGpuNameTests.cs
git commit -m "feat(gpu): 新增显卡简称提取规则

从显卡全名剥离厂商前缀与系列前缀, 得到用于区分多卡的短名 (如 3080 Ti)。"
```

---

## Task 4: GpuItemSync 监控项同步与迁移

**Files:**
- Create: `src/Core/GpuItemSync.cs`
- Create: `tests/LiteMonitor.Tests/GpuItemSyncTests.cs`
- Modify: `src/Core/Settings.cs`（新增 `MonitoredGpuCount`，供 GpuItemSync 读取数量）

**Interfaces:**
- Consumes: `LiteMonitor.src.Core.GpuKey`（Task 2）、`LiteMonitor.Settings`（既有）、`LiteMonitor.MonitorItemConfig`（既有）

- Produces:
  - `Settings.MonitoredGpuCount : int`（默认 1）
  - `LiteMonitor.src.Core.GpuItemSync.Ensure(Settings cfg, int availableGpuCount = GpuKey.MaxGpuCount)`；常量 `GpuItemSync.Metrics`、`GpuItemSync.SortStride = 10`
  - `Ensure` 会把 `MonitoredGpuCount` 夹紧到 `min(MonitoredGpuCount, availableGpuCount, GpuKey.MaxGpuCount)`，并把夹紧后的值**回写**到 `cfg.MonitoredGpuCount`，让设置页能立刻看到真实生效值


- [ ] **Step 1: 在 Settings 中新增 MonitoredGpuCount**

在 `src/Core/Settings.cs` 中，把这一行：

```csharp
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();
```

替换为：

```csharp
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        // ★★★ [多显卡] 监控显卡数量: 1 = 只监控 1 张 (沿用旧行为), N = 按优先级监控前 N 张 ★★★
        public int MonitoredGpuCount { get; set; } = 1;
```

> `Settings.cs` 已 `using System.Collections.Generic;`，无需补 using。

- [ ] **Step 2: 写失败测试**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\GpuItemSyncTests.cs`：

```csharp
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuItemSyncTests
    {
        private static Settings NewSettings(int count, params MonitorItemConfig[] items)
        {
            var s = new Settings { MonitoredGpuCount = count };
            s.MonitorItems = new List<MonitorItemConfig>(items);
            return s;
        }

        [Fact]
        public void Ensure_旧单卡配置_迁移成带序号形式()
        {
            var s = NewSettings(1,
                new MonitorItemConfig { Key = "CPU.Load", VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Load", VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "GPU.Temp", VisibleInPanel = true });

            GpuItemSync.Ensure(s);

            Assert.DoesNotContain(s.MonitorItems, x => x.Key == "GPU.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Temp");
            // 可见性从旧项继承
            Assert.True(s.MonitorItems.First(x => x.Key == "GPU.0.Load").VisibleInTaskbar);
        }

        [Fact]
        public void Ensure_两张卡_生成两组共十二项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);

            var gpuItems = s.MonitorItems.Where(x => x.Key.StartsWith("GPU.")).ToList();
            Assert.Equal(12, gpuItems.Count);
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.1.Load");
        }

        [Fact]
        public void Ensure_减少数量_裁掉多余序号的项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.1.Temp");

            s.MonitoredGpuCount = 1;
            GpuItemSync.Ensure(s);

            Assert.DoesNotContain(s.MonitorItems, x => x.Key.StartsWith("GPU.1."));
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Temp");
        }

        [Fact]
        public void Ensure_重复调用_不产生重复项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);
            int first = s.MonitorItems.Count;
            GpuItemSync.Ensure(s);
            Assert.Equal(first, s.MonitorItems.Count);
        }

        [Fact]
        public void Ensure_保留用户已有的自定义()
        {
            var s = NewSettings(1);
            GpuItemSync.Ensure(s);

            var item = s.MonitorItems.First(x => x.Key == "GPU.0.Temp");
            item.UserLabel = "我的温度";
            item.VisibleInTaskbar = true;

            GpuItemSync.Ensure(s);

            var again = s.MonitorItems.First(x => x.Key == "GPU.0.Temp");
            Assert.Equal("我的温度", again.UserLabel);
            Assert.True(again.VisibleInTaskbar);
        }

        [Fact]
        public void Ensure_数量非法时_夹到一和上限之间()
        {
            var s = NewSettings(0);
            GpuItemSync.Ensure(s);
            Assert.Equal(6, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));

            s.MonitoredGpuCount = 999;
            GpuItemSync.Ensure(s);
            Assert.Equal(6 * GpuKey.MaxGpuCount, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
        }

        [Fact]
        public void Ensure_实际卡数不足时_夹紧并回写数量()
        {
            // 机器上只有 2 张卡, 但配置里写了 8
            var s = NewSettings(8);
            GpuItemSync.Ensure(s, availableGpuCount: 2);

            // 只生成 2 组, 不会出现 6 组空白监控项
            Assert.Equal(12, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
            Assert.DoesNotContain(s.MonitorItems, x => x.Key.StartsWith("GPU.2."));
            // 数量被夹紧后回写, 让设置页显示真实生效值
            Assert.Equal(2, s.MonitoredGpuCount);
        }

        [Fact]
        public void Ensure_实际卡数充足时_不改动数量()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s, availableGpuCount: 4);
            Assert.Equal(2, s.MonitoredGpuCount);
            Assert.Equal(12, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
        }

        [Fact]
        public void Ensure_保留范围内的自定义带序号Key()
        {
            // 用户/插件自建的 "GPU.0.Custom" 不该被静默删掉
            // (Task 5 Step 2.5 放行了带序号的 Key, Ensure 就不该再把它删掉)
            var s = NewSettings(2, new MonitorItemConfig
            {
                Key = "GPU.0.Custom",
                UserLabel = "我的自定义",
                SortIndex = 999,
                TaskbarSortIndex = 999,
            });

            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Custom");
            var custom = s.MonitorItems.First(x => x.Key == "GPU.0.Custom");
            Assert.Equal("我的自定义", custom.UserLabel);
        }

        [Fact]
        public void Ensure_丢弃超出卡数的自定义带序号Key()
        {
            // count=2 时 "GPU.2.Custom" 属于越界项, 不能复活 ——
            // 否则又会出现计划要消灭的"空白监控项"。
            var s = NewSettings(2, new MonitorItemConfig
            {
                Key = "GPU.2.Custom",
                UserLabel = "越界",
                SortIndex = 999,
                TaskbarSortIndex = 999,
            });

            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.DoesNotContain(s.MonitorItems, x => x.Key.StartsWith("GPU.2."));
            // 标准项 2 组 x 6 = 12, 越界的自定义项不计入
            Assert.Equal(12, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
        }

    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译错误 `CS0246: 未能找到类型或命名空间 "GpuItemSync"`

- [ ] **Step 4: 实现 GpuItemSync**

写入 `G:\TOOL\LiteMonitor-fork\src\Core\GpuItemSync.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 显卡监控项同步器。
    ///
    /// 职责: 保证 Settings.MonitorItems 中的显卡监控项与 Settings.MonitoredGpuCount 保持一致。
    ///   - 每个被监控的显卡生成一组 "GPU.{序号}.{指标}" 监控项
    ///   - 从旧版单卡格式 ("GPU.Load") 迁移时继承可见性 / 排序 / 单位, 但不继承用户自定义名称
    ///     (名称改由显卡简称自动生成, 否则两张卡会显示同一个名字)
    ///   - 已存在的同 Key 项保留用户的全部自定义
    /// </summary>
    public static class GpuItemSync
    {
        /// <summary>显卡基础指标定义: (指标名, 排序基准, 主界面默认可见, 任务栏默认可见)</summary>
        public static readonly (string metric, int sort, bool panel, bool taskbar)[] Metrics =
        {
            ("Load",  301, true,  true),
            ("Temp",  302, true,  false),
            ("Clock", 303, false, false),
            ("Power", 304, false, false),
            ("Fan",   305, false, false),
            ("VRAM",  306, true,  false),
        };

        /// <summary>每张卡占用的排序位宽度, 让同一分组内按卡顺序排列</summary>
        public const int SortStride = 10;

        /// <summary>
        /// 同步显卡监控项。可重复调用, 在**同一个 cap** 下幂等。
        /// </summary>
        public static void Ensure(Settings cfg, int availableGpuCount = GpuKey.MaxGpuCount)
        {
            if (cfg?.MonitorItems == null) return;

            // ★★★ [多显卡] 上限 = 实际检测到的卡数 与 配置上限 取小 ★★★
            // 没有这一步, 用户在 2 卡机器上选 8 会生成 6 组永远没有数据的空白监控项。
            int cap = Math.Max(1, Math.Min(GpuKey.MaxGpuCount, availableGpuCount));

            int count = cfg.MonitoredGpuCount;
            if (count < 1) count = 1;
            if (count > cap) count = cap;

            // 夹紧结果回写, 让设置页显示真实生效值
            if (cfg.MonitoredGpuCount != count) cfg.MonitoredGpuCount = count;

            var items = cfg.MonitorItems;

            // ★★★ 先清掉 null 元素 (code review 补) ★★★
            // settings.json 被手改坏 / 插件写坏时, MonitorItems 里可能出现 null 项。
            // 下面每处 it.Key 都会 NRE, 而 Ensure 是在 SettingsHelper.Load() 的**启动路径**上
            // 调用的 —— 一次 NRE 就能让程序起不来。顺带也把"无意义的 null 项"清干净。
            items.RemoveAll(it => it == null);

            // 1. 旧版单卡项作为迁移模板
            var legacy = new Dictionary<string, MonitorItemConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (it.Key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase)
                    && !GpuKey.TryParse(it.Key, out _, out _))
                {
                    legacy[it.Key] = it;
                }
            }

            // 2. 已存在的带序号项 (保留用户自定义)
            var indexed = new Dictionary<string, MonitorItemConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (GpuKey.TryParse(it.Key, out _, out _))
                {
                    indexed[it.Key] = it;
                }
            }

            // 3. 清掉全部显卡相关项 (旧格式 + 不再需要的序号)
            items.RemoveAll(it => it.Key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase));

            // 4. 按当前数量重新生成
            for (int i = 0; i < count; i++)
            {
                foreach (var (metric, sort, panel, taskbar) in Metrics)
                {
                    string key = GpuKey.Build(i, metric);
                    if (indexed.TryGetValue(key, out var existing))
                    {
                        items.Add(existing);
                        continue;
                    }

                    string baseKey = GpuKey.BaseKey(metric);
                    legacy.TryGetValue(baseKey, out var tpl);

                    // ★★★ 排序基准 (code review 修正): 取"0 号卡同一指标"的实际排序值 ★★★
                    //   优先用已存在的 GPU.0.{metric} —— 它承载了用户在"监控项显示"里拖过的顺序;
                    //   其次用旧格式项 (首次迁移时 legacy 还在); 最后才用默认值。
                    //   ⚠️ 不能只看 legacy: 第 1 次 Ensure 就把 legacy 项删光了,
                    //   之后把卡数从 1 调到 2 时, 新卡会回落成默认值 (301+10=311),
                    //   而 0 号卡仍保留用户自定义的 500 —— 面板顺序会倒挂成
                    //   "GPU.0.Temp → GPU.1.* → GPU.0.Load"。
                    int baseSort = sort;
                    int baseTaskbarSort = sort;

                    if (indexed.TryGetValue(GpuKey.Build(0, metric), out var firstCard))
                    {
                        if (firstCard.SortIndex != 0) baseSort = firstCard.SortIndex;
                        if (firstCard.TaskbarSortIndex != 0) baseTaskbarSort = firstCard.TaskbarSortIndex;
                    }
                    else if (tpl != null)
                    {
                        if (tpl.SortIndex != 0) baseSort = tpl.SortIndex;
                        if (tpl.TaskbarSortIndex != 0) baseTaskbarSort = tpl.TaskbarSortIndex;
                    }

                    items.Add(new MonitorItemConfig
                    {
                        Key = key,
                        // ★ 排序索引同样继承旧项, 否则用户在"监控项显示"里拖过的顺序会被重置 ★
                        SortIndex = baseSort + i * SortStride,
                        TaskbarSortIndex = baseTaskbarSort + i * SortStride,
                        VisibleInPanel = tpl?.VisibleInPanel ?? panel,
                        VisibleInTaskbar = tpl?.VisibleInTaskbar ?? taskbar,
                        // 任务栏自定义名要继承 (UserLabel 是故意不继承的: 两张卡同名没意义)
                        TaskbarLabel = tpl?.TaskbarLabel,
                        UnitPanel = tpl?.UnitPanel,
                        UnitTaskbar = tpl?.UnitTaskbar,
                    });

                }
            }

            // 4.5 ★★★ 保留非标准指标 (例如用户/插件自建的 "GPU.0.Custom") ★★★
            //     Task 5 Step 2.5 让 InternAllStrings 放行了所有带序号的显卡 Key,
            //     如果这里把它们一起删掉, 那一步放行就白做了 (实测会静默丢失)。
            //     ⚠️ **只保留当前监控数量范围内**的自定义项 (customIndex < count) ——
            //     否则 count=2 时 "GPU.5.Custom" 会被复活, 又冒出计划要消灭的空白监控项,
            //     而且会打破"MonitorItems 里不存在 GPU.{超出卡数}.*"这条不变量。 ★★★
            //     已知限制: 越界的自定义项一旦被裁掉, **之后把数量调大也不会自动找回**
            //     (裁掉就是真删了, 不做历史保留) —— 这是有意为之, 避免空白监控项复活。
            //     另: 非规范大小写的 Key (如 "GPU.0.load") 会**原样保留**, 不规范成 "GPU.0.Load"。
            var standardMetrics = new HashSet<string>(
                Metrics.Select(m => m.metric), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in indexed)
            {
                if (GpuKey.TryParse(kv.Key, out int customIndex, out string customMetric)
                    && customIndex < count
                    && !standardMetrics.Contains(customMetric))
                {
                    items.Add(kv.Value);
                }
            }
        }
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过

- [ ] **Step 6: 提交**

```powershell
git add src/Core/GpuItemSync.cs src/Core/Settings.cs tests/LiteMonitor.Tests/GpuItemSyncTests.cs
git commit -m "feat(gpu): 新增显卡监控项同步器

按 MonitoredGpuCount 生成 GPU.{序号}.{指标} 监控项, 并把旧的单卡格式
(GPU.Load 等) 迁移过来, 保留可见性/单位/用户自定义。"
```

- [ ] **Step 5.5: code review 补的两处健壮性修正（实测复现后追加）**

> 独立 code review 用临时 harness 实测复现了这两个问题, 都在 `Ensure` 里。

**5.5.1 null 元素导致启动崩溃**：`settings.json` 被手改坏 / 插件写坏时，`MonitorItems` 里可能有 `null` 项。
`InternAllStrings` 显式容忍 null（`if (item == null) continue;`）但**不删除**它们 →
`Ensure` 里 `it.Key` 抛 NRE → 而 `Ensure` 是在 `SettingsHelper.Load()` 的**启动路径**上调用的 →
`Program.cs` 的 `Main` 只有 `finally` 没有 `catch` → **一次 NRE 就让程序起不来**。

修法：`Ensure` 开头加 `items.RemoveAll(it => it == null);`（顺带把无意义的 null 项清干净）。

**5.5.2 卡数从 1 增到 2 时第二组排序回落默认值**：
第 1 次 `Ensure` 就把 legacy 模板删光了，所以之后把卡数改成 2 时，
新卡的排序基准取不到模板 → 回落成默认值 `301+10=311`，
而 0 号卡仍保留用户自定义的 `500` → 面板顺序倒挂成 `GPU.0.Temp → GPU.1.* → GPU.0.Load`。

修法：排序基准改为**优先取已存在的 `GPU.0.{metric}`**（它承载了用户拖拽过的顺序），
其次才用 legacy 模板，最后用默认值。

**5.5.3 补两条回归测试**（写入 `tests/LiteMonitor.Tests/GpuItemSyncTests.cs`）：

```csharp
        [Fact]
        public void Ensure_监控项里有null元素_不崩溃且清掉null()
        {
            var s = NewSettings(1);
            s.MonitorItems.Add(null);
            s.MonitorItems.Add(new MonitorItemConfig { Key = "CPU.Load" });

            GpuItemSync.Ensure(s);

            Assert.DoesNotContain(s.MonitorItems, x => x == null);
            Assert.Contains(s.MonitorItems, x => x.Key == "CPU.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Load");
        }

        [Fact]
        public void Ensure_卡数从一增到二_第二组排序跟随第一组不倒挂()
        {
            var s = NewSettings(1,
                new MonitorItemConfig { Key = "GPU.Load", SortIndex = 500, TaskbarSortIndex = 500 });

            GpuItemSync.Ensure(s);
            Assert.Equal(500, s.MonitorItems.First(x => x.Key == "GPU.0.Load").SortIndex);

            s.MonitoredGpuCount = 2;
            GpuItemSync.Ensure(s);

            int si0 = s.MonitorItems.First(x => x.Key == "GPU.0.Load").SortIndex;
            int si1 = s.MonitorItems.First(x => x.Key == "GPU.1.Load").SortIndex;

            Assert.Equal(500, si0);
            Assert.Equal(510, si1);
            Assert.True(si1 > si0, "第二张卡必须排在第一张卡之后");
        }
```

---

## Task 5: Settings 字段与 SettingsHelper 接入

**Files:**
- Modify: `src/Core/Settings.cs`（在 `MonitorItems` 属性附近新增三个成员）
- Modify: `src/Core/SettingsHelper.cs`（`Load()` 中调用 `GpuItemSync.Ensure`；`UpdateMaxRecord` 改为归一化比对）
- Modify: `tests/LiteMonitor.Tests/GpuItemSyncTests.cs`（追加一个 `Ensure` 与 `Load` 联动的用例可选；本步至少保证既有测试仍通过）
  - ⚠️ **会调 `SettingsHelper.Load()` 的用例必须串行化**: `SettingsHelper.FilePath` 固定指向
    `AppContext.BaseDirectory\settings.json`, 同一个测试程序集里两个调 `Load()` 的用例会**并行写同一个文件**,
    实测抛 `IOException: The process cannot access the file ... because it is used by another process`。
    做法: 给这些用例所在的类加 `[Collection("cfgfile")]`, 并建一个不带 `Fact` 的
    `public class CfgFileCollection : ICollectionFixture<object> {}` 定义该 collection。

**Interfaces:**
- Consumes: `GpuItemSync.Ensure`（Task 4）、`GpuKey.ToBaseKey`（Task 2）
- Produces:
  - `Settings.RecordedMaxGpuDynamic : Dictionary<string, float>`
  - `Settings.DetectedGpuVramTotalGBByKey : ConcurrentDictionary<string, float>`（静态，`[JsonIgnore]`）

> ⚠️ `RecordedMaxGpuDynamic` 是 `Dictionary<string, float>`（非 `ConcurrentDictionary`）:
> **code review 修正** —— 原注释写的「它只在硬件线程 (`UpdateMaxRecord`) 里写, UI 线程只读」**是反的**。
> 实际调用链是 `UpdateMaxRecord` ← `ComponentProcessor.GetCompositeValue` / `HardwareValueProvider.ReadFanValue`
> ← `HardwareValueProvider.GetValue` ← `HardwareMonitor.Get` ← **UI 线程** (`UIController.Tick`)
> **或网页服务线程** (`LiteWebServer` 的 `hw.Get(item.Key)`)。
> 好消息: `GetValue` 入口有 `Monitor.TryEnter(_lock, 10)`, **所有写都被 `_lock` 串行化**,
> 不会出现 Dictionary 结构损坏 (8 线程裸写确实会抛 `InvalidOperationException`, 但这条路径够不着)。
> 读侧 (`MetricUtils.GetAdaptivePercentage`) 不持锁, 最坏情况是读到旧值 →
> 某一帧回落全局峰值、进度条百分比偶发跳变, 不会崩。
> 但 `SettingsChanger.Merge` 的反射默认分支对集合类型是**按引用赋值**
> （`SettingsChanger.cs:74-77`, 只有 `PluginInstances` / `Thresholds` 做了 JSON 深拷贝）,
> 所以设置窗口打开期间产生的每卡峰值会在保存时**被 draft 覆盖回旧值**。
> 这是可接受的 (峰值本来就是"历史最大值", 丢一点不影响), 但要知道这个行为。


- [ ] **Step 1: 在 Settings 中新增字段**

在 `src/Core/Settings.cs` 中，把这一行：

```csharp
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();
```

替换为：

```csharp
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        // ★★★ [多显卡] 各显卡各自的峰值记录 (key = 完整 Key, 如 "GPU.1.Power"), 供进度条按卡自适应 ★★★
        public Dictionary<string, float> RecordedMaxGpuDynamic { get; set; } = new Dictionary<string, float>();

        // ★★★ [多显卡] 运行时缓存: 各显卡的显存总容量 (GB), key = "GPU" / "GPU.0" / "GPU.1" ★★★
        // 用 ConcurrentDictionary: 硬件线程 (HardwareValueProvider.GetGpuVramPercent) 写,
        // UI 线程 (MetricUtils.GetGpuVramTotalGB) 读 —— 普通 Dictionary 并发写会损坏内部结构。
        [JsonIgnore]
        public static ConcurrentDictionary<string, float> DetectedGpuVramTotalGBByKey { get; } = new ConcurrentDictionary<string, float>();

```

> 注：`MonitoredGpuCount` 已在 Task 4 加入（`GpuItemSync` 依赖它），此处不要重复添加。
> `Settings.cs` 已 `using System.Collections.Generic;` 与 `using System.Text.Json.Serialization;`，无需补 using。若编译报 `JsonIgnore` 找不到，补 `using System.Text.Json.Serialization;`。

- [ ] **Step 2: 在 SettingsHelper.Load 中接入同步**

> ⚠️ **插入点极其重要**: 必须放在 `s.InternAllStrings();` **之后**。
> `InternAllStrings`（`SettingsHelper.cs:244`）会用 `GetStandardKeys()` 白名单清理"孤儿项",
> 而该白名单来自 `InitDefaultItems()`（`SettingsHelper.cs:156`）, 里面只有旧格式的
> `"GPU.Load"` / `"GPU.Temp"` / `"GPU.VRAM"`。
> `"GPU.0.Load"` 这类带序号 Key 既不在白名单里、也不以 `DASH.` 开头 → **会被当场删光**。
> 若 `Ensure` 放在它之前, 默认单卡模式下主界面**一个显卡指标都不会显示**。

在 `src/Core/SettingsHelper.cs` 的 `Load()` 方法中，把这一段：

```csharp
            s.InternAllStrings();
```

替换为：

```csharp
            s.InternAllStrings();

            // 3. [多显卡] 同步显卡监控项。
            //    ★ 必须在 InternAllStrings 之后 ★ (见上方说明)
            //    也必须在 CheckAndAppendMissingItems 之后 —— 那一步会把缺失的默认项
            //    (含旧的 "GPU.Load" 单卡项) 补回来, 这里再统一迁移成 "GPU.{序号}.{指标}"。
            //    注意: 此刻硬件可能还没枚举完, 拿不到实际卡数, 这里先按上限夹紧;
            //    真实卡数会在保存设置时 (Task 5.5) 与运行时 (Task 12.3) 再次夹紧。
            GpuItemSync.Ensure(s);
```

- [ ] **Step 2.5: 让 InternAllStrings 保留带序号的显卡 Key**

`InternAllStrings` 的白名单里没有带序号的 Key, 会把它们当成"孤儿项"删掉 ——
用户对 `GPU.0.Temp` 之类做的自定义 (可见性 / 排序 / 用户名) 会全部丢失。

在 `src/Core/SettingsHelper.cs` 的 `InternAllStrings` 方法中，把：

```csharp
                    // 2. Is it a valid Plugin item? 
                    // [Safety] 只要是 DASH. 开头，一律保留，防止误删用户自定义的简单插件
                    if (item.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // 保留所有 DASH.*
                    }
                    keysToRemove.Add(item);
```

替换为：

```csharp
                    // 2. Is it a valid Plugin item? 
                    // [Safety] 只要是 DASH. 开头，一律保留，防止误删用户自定义的简单插件
                    if (item.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // 保留所有 DASH.*
                    }

                    // 3. ★★★ [多显卡] 带序号的显卡 Key ("GPU.0.Temp") 一律保留 ★★★
                    // 白名单 GetStandardKeys() 里只有旧格式的 "GPU.Temp",
                    // 不加这一句的话, 多显卡监控项会被当成"孤儿项"全部删光,
                    // 用户对它们做的自定义 (可见性 / 排序 / 用户名) 也会一起丢。
                    if (GpuKey.TryParse(item.Key, out _, out _))
                    {
                        continue;
                    }

                    keysToRemove.Add(item);
```

- [ ] **Step 2.6: 老配置迁移分支也要找回带序号的显卡 Key**

> `InternAllStrings` 是 Step 2.5 管的, 但**老配置走的是另一条路**:
> `SettingsHelper.Load()`（:50-55）在"所有项 `TaskbarSortIndex == 0`"时会走
> `RebuildAndMigrateSettings`（:291-330）, 那个方法会**用默认清单重建整个 `MonitorItems`**,
> 只额外找回 `DASH.*` 项 —— 带序号的显卡 Key 在到达 `Ensure` 之前就被丢掉了
> （实测: 老配置下 `GPU.0.Custom` 加载后不存在）。

在 `src/Core/SettingsHelper.cs` 的 `RebuildAndMigrateSettings` 方法中，把：

```csharp
            // 3. [Fix] 找回插件项：追加 DASH 开头且不在标准列表中的配置
            // 必须过滤掉 DASH.HOST/DASH.Time 等标准项，否则会重复！
            migratedList.AddRange(settings.MonitorItems
                .Where(x => x.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase) && !standardKeys.Contains(x.Key))
                .Select(x => { 
                    if (x.TaskbarSortIndex == 0) x.TaskbarSortIndex = x.SortIndex; 
                    return x; 
                }));
```

替换为：

```csharp
            // 3. [Fix] 找回插件项：追加 DASH 开头且不在标准列表中的配置
            // 必须过滤掉 DASH.HOST/DASH.Time 等标准项，否则会重复！
            migratedList.AddRange(settings.MonitorItems
                .Where(x => x.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase) && !standardKeys.Contains(x.Key))
                .Select(x => { 
                    if (x.TaskbarSortIndex == 0) x.TaskbarSortIndex = x.SortIndex; 
                    return x; 
                }));

            // 3.5 ★★★ [多显卡] 同样找回带序号的显卡 Key ("GPU.0.Custom") ★★★
            //     这一步跑在 InternAllStrings / GpuItemSync.Ensure **之前**,
            //     不找回的话, 老配置 (所有项 TaskbarSortIndex == 0) 走迁移分支时,
            //     用户的自定义显卡项在到达 Ensure 之前就被丢掉了 (实测会丢)。
            //     越界的序号 (超出 MonitoredGpuCount) 由 GpuItemSync.Ensure 的 4.5 段再过滤。
            migratedList.AddRange(settings.MonitorItems
                .Where(x => GpuKey.TryParse(x.Key, out _, out _) && !standardKeys.Contains(x.Key))
                .Select(x => {
                    if (x.TaskbarSortIndex == 0) x.TaskbarSortIndex = x.SortIndex;
                    return x;
                }));
```

> `GpuKey` 在 `LiteMonitor.src.Core` 命名空间下, 而 `SettingsHelper.cs` 本身在 `namespace LiteMonitor`。
> ⚠️ C# 的名字查找只会**逐层向外**找外层命名空间, **不会**下钻到子命名空间 ——
> 所以"是子命名空间所以能直接访问"这个说法是错的。
> 这里能直接访问, 真正的原因是 `SettingsHelper.cs` 顶部**已经有** `using LiteMonitor.src.Core;`。
> 用 `Select-String -Path 'G:\TOOL\LiteMonitor-fork\src\Core\SettingsHelper.cs' -Pattern '^using'` 确认一下;
> 万一哪天这行 using 被删了, 这里就要补回来, 否则会报 `CS0246: 未能找到类型或命名空间 "GpuKey"`。


- [ ] **Step 3: 改造 UpdateMaxRecord**

在 `src/Core/SettingsHelper.cs` 中，把 `UpdateMaxRecord` 方法整体替换为：

```csharp
        public static void UpdateMaxRecord(this Settings settings, string key, float val)
        {
            bool changed = false;
            if (val <= 0 || float.IsNaN(val) || float.IsInfinity(val)) return;
            
            if (key.Contains("Clock") && val > 10000) return; 
            if (key.Contains("Power") && val > 1000) return;
            if ((key.Contains("Fan") || key.Contains("Pump")) && val > 10000) return;

            // ★★★ [多显卡] 带序号的 Key ("GPU.1.Power") 归一化成基础 Key ("GPU.Power") 后再比对 ★★★
            string baseKey = GpuKey.ToBaseKey(key);

            if (baseKey == "CPU.Power" && val > settings.RecordedMaxCpuPower) { settings.RecordedMaxCpuPower = val; changed = true; }
            else if (baseKey == "CPU.Clock" && val > settings.RecordedMaxCpuClock) { settings.RecordedMaxCpuClock = val; changed = true; }
            else if (baseKey == "GPU.Power" && val > settings.RecordedMaxGpuPower) { settings.RecordedMaxGpuPower = val; changed = true; }
            else if (baseKey == "GPU.Clock" && val > settings.RecordedMaxGpuClock) { settings.RecordedMaxGpuClock = val; changed = true; }
            
            else if (baseKey == "CPU.Fan" && val > settings.RecordedMaxCpuFan) { settings.RecordedMaxCpuFan = val; changed = true; }
            else if (baseKey == "CPU.Pump" && val > settings.RecordedMaxCpuPump) { settings.RecordedMaxCpuPump = val; changed = true; }
            else if (baseKey == "GPU.Fan" && val > settings.RecordedMaxGpuFan) { settings.RecordedMaxGpuFan = val; changed = true; }
            else if (baseKey == "CASE.Fan" && val > settings.RecordedMaxChassisFan) { settings.RecordedMaxChassisFan = val; changed = true; }

            // ★★★ [多显卡] 每张卡单独记一份峰值, 让各自的进度条独立自适应 ★★★
            if (key != baseKey)
            {
                if (!settings.RecordedMaxGpuDynamic.TryGetValue(key, out float cur) || val > cur)
                {
                    settings.RecordedMaxGpuDynamic[key] = val;
                    changed = true;
                }
            }

            if (changed && (DateTime.Now - settings.LastAutoSaveTime).TotalSeconds > 30)
            {
                settings.Save();
                settings.LastAutoSaveTime = DateTime.Now;
            }
        }
```

- [ ] **Step 4: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误；测试全部通过

- [ ] **Step 5: 提交**

```powershell
git add src/Core/Settings.cs src/Core/SettingsHelper.cs
git commit -m "feat(gpu): 新增多显卡配置项并在配置加载时同步监控项

新增 MonitoredGpuCount / RecordedMaxGpuDynamic / DetectedGpuVramTotalGBByKey;
UpdateMaxRecord 改为按归一化后的基础 Key 比对, 并按卡单独记录峰值。"
```

---

## Task 5.5: 保存设置时同步显卡监控项

> **为什么需要这一步**: 设置窗口是 draft/live 双份模型（`SettingsForm` 持有 `_draftCfg`，保存时
> `SettingsChanger.Merge(_cfg, _draftCfg)`）。用户在设置页把「监控显卡数量」改成 2 之后,
> 写进 live 的只有 `MonitoredGpuCount`; `MonitorItems` 里的 `GPU.1.*` 项必须在保存路径里重新生成,
> 否则界面上不会出现第二组显卡指标。
>
> 放在 `AppActions.ApplyMonitorLayout` 开头, 是因为它 (1) 在 `SettingsChanger.Merge` 之后执行,
> (2) 在 `ui.RebuildLayout()` 之前执行, (3) 之后 `SettingsForm.cs:251` 会把 live 回写进 draft。

**Files:**
- Modify: `src/Core/Actions/AppActions.cs`
- Modify: `src/System/HardwareMonitor.cs`
- Modify: `src/System/HardwareServices/HardwareScanner.cs`
- Modify: `src/UI/SettingsForm.cs`（落盘前先同步监控项，见 Step 2.5）

**Interfaces:**
- Consumes: `GpuItemSync.Ensure`（Task 4）、`HardwareMonitor.TryGetDetectedGpuCount`（本任务新增）
- Produces: 保存设置时自动生成 `GPU.{序号}.{指标}` 监控项，并按实际卡数夹紧

- [ ] **Step 1: 给 ApplyMonitorLayout 增加 Settings 参数**

当前签名是 `public static void ApplyMonitorLayout(UIController? ui, MainForm form, bool rebuildMenus = true)`,
拿不到 `Settings`。**先加参数**:

```csharp
public static void ApplyMonitorLayout(Settings cfg, UIController? ui, MainForm form, bool rebuildMenus = true)
```

然后用 `Select-String -Path 'G:\TOOL\LiteMonitor-fork\src\**\*.cs' -Pattern 'ApplyMonitorLayout'` 找出**全部**调用点并补上 `cfg` 实参。已知有 4 处:

| 文件 | 行 | 改法 |
|---|---|---|
| `src/Core/Actions/AppActions.cs` | 40 | `ApplyMonitorLayout(cfg, ui, mainForm);` |
| `src/UI/Helpers/MenuMonitorHelper.cs` | 86 | `AppActions.ApplyMonitorLayout(cfg, ui, form, rebuildMenus: false);` |
| `src/UI/Helpers/MenuMonitorHelper.cs` | 119 | `AppActions.ApplyMonitorLayout(cfg, ui, form, rebuildMenus: false);` |
| `src/UI/Helpers/MenuMonitorHelper.cs` | 271 | `AppActions.ApplyMonitorLayout(cfg, ui, form, rebuildMenus: false);` |

（这 4 处的作用域里都已有 `cfg` 变量；改完务必用上面的 `Select-String` 再确认一次没有漏网的调用点。）

- [ ] **Step 1.5: 新增非阻塞的显卡数量查询**

> **为什么不能直接用现成的 `HardwareMonitor.ListAllGpuOptions()`**:
> 它内部是 `lock (Instance!._lock)`, 而 `ApplyMonitorLayout` 跑在 **UI 线程**
> (保存设置 / 右键菜单)。硬件刷新 (`ReloadComputerSafe`) 持锁可达数秒,
> 直接调用会把界面冻住 —— 也违反本计划 Global Constraints 里
> 「不得在 UI 线程发起阻塞式硬件调用」的约束。
>
> 这里改成: 最多等锁 10ms, 拿不到就读扫描器缓存, **绝不阻塞**。

在 `src/System/HardwareServices/HardwareScanner.cs` 中, 紧跟 `ClearCache()` 方法之后插入：

```csharp
        /// <summary>
        /// 只读缓存里已检测到的显卡数量, 不碰硬件、不加锁。
        /// 供 UI 线程在拿不到硬件锁时快速拿到"上次检测到的卡数"。
        /// </summary>
        public static int CachedGpuCount => _cachedGpuOptions?.Count ?? 0;
```

在 `src/System/HardwareMonitor.cs` 中, 紧跟 `ListAllGpuOptions()` 方法之后插入：

```csharp
        /// <summary>
        /// 取得"已检测到的显卡数量", **不阻塞 UI 线程**。
        ///
        /// 快路径: 最多等锁 10ms, 拿到就精确取一次。
        /// 慢路径: 硬件正在刷新 (ReloadComputerSafe 持锁数秒) 时, 直接读扫描器缓存,
        ///         绝不阻塞 —— 缓存里的卡数最多是"上一次"的, 但足够用来夹紧。
        /// 都拿不到时返回 0, 由调用方决定"不夹紧"。
        /// </summary>
        public static int TryGetDetectedGpuCount()
        {
            var inst = Instance;
            if (inst == null) return 0;

            if (Monitor.TryEnter(inst._lock, 10))
            {
                try
                {
                    return HardwareScanner.ListAllGpuOptions(inst._computer)?.Count ?? 0;
                }
                catch
                {
                    return 0;
                }
                finally
                {
                    Monitor.Exit(inst._lock);
                }
            }

            // 拿不到锁: 退回到扫描器缓存 (不碰硬件, 不阻塞)
            return HardwareScanner.CachedGpuCount;
        }
```

> `Monitor` 是 `System.Threading.Monitor`（`HardwareMonitor.cs:9` 已有 `using System.Threading;`）。
> `inst._lock` / `inst._computer` 是 `HardwareMonitor` 的私有字段, 同一个类里访问得到。
> `HardwareScanner` 与 `HardwareMonitor` 同在 `LiteMonitor.src.SystemServices` 命名空间。

- [ ] **Step 2: 在 ApplyMonitorLayout 开头同步监控项**

> ⚠️ 下面代码块里的签名**已经是 Step 1 改过之后**的样子 (`Settings cfg` 已在首位),
> 别再拿旧签名去搜; Step 2 是在 Step 1 的基础上继续改的。

把：

```csharp
        public static void ApplyMonitorLayout(Settings cfg, UIController? ui, MainForm form, bool rebuildMenus = true)
        {
            // ★★★ [新增] 动态检查硬件开启需求 (热切换) ★★★
            // 如果用户开启了风扇/水泵，自动开启 USB 控制器；反之关闭
            HardwareMonitor.Instance?.RefreshHardwareConfig();
```

替换为：

```csharp
        public static void ApplyMonitorLayout(Settings cfg, UIController? ui, MainForm form, bool rebuildMenus = true)
        {
            // ★★★ [多显卡] 同步显卡监控项 ★★★
            // 1) 把 MonitoredGpuCount 落到 MonitorItems 上 (生成 GPU.{序号}.{指标})
            // 2) 按实际检测到的显卡数夹紧 —— 避免选 8 张但机器只有 2 张时产生空白监控项
            // 必须在本方法重建布局之前执行, 否则 UI 读到的还是旧的 MonitorItems。
            if (cfg != null)
            {
                // ★ 非阻塞取实际卡数 (见 Step 1.5): 绝不 lock 硬件锁 ★
                int detectedGpuCount = HardwareMonitor.TryGetDetectedGpuCount();

                // 探测不到时 (0) 不做夹紧, 但仍然要按配置数量重新生成监控项 ——
                // 否则用户改了「监控显卡数量」保存后不会生效。
                int cap = detectedGpuCount > 0
                    ? detectedGpuCount
                    : Math.Max(1, cfg.MonitoredGpuCount);

                GpuItemSync.Ensure(cfg, availableGpuCount: cap);
            }

            // ★★★ [新增] 动态检查硬件开启需求 (热切换) ★★★
            // 如果用户开启了风扇/水泵，自动开启 USB 控制器；反之关闭
            HardwareMonitor.Instance?.RefreshHardwareConfig();
```

> `AppActions.cs` 在 `namespace LiteMonitor.src.Core.Actions`, 而 `GpuKey`/`GpuItemSync` 在**外层**的
> `LiteMonitor.src.Core` —— C# 的名字查找会逐层向外找外层命名空间, 所以这里**不需要**补 using。
> `HardwareMonitor` 在 `LiteMonitor.src.SystemServices`, 该文件已有 `using LiteMonitor.src.SystemServices;`
> （`AppActions.cs` 顶部确认一下即可）。

- [ ] **Step 2.5: 在落盘前同步监控项（SettingsForm）**

> **为什么需要这一步**: `SettingsForm` 的保存顺序是
> `SettingsChanger.Merge`（:240）→ `_cfg.Save()`（:244）→ `AppActions.ApplyAllSettings`（:247）
> → `ApplyMonitorLayout` → `GpuItemSync.Ensure`。
> 也就是说 **Ensure 发生在 Save 之后** —— 磁盘上的 `MonitorItems` 会比内存晚一轮
> （实测: 磁盘 6 项 / 内存 12 项），要等下次启动 `Load()` 才自愈。
> 后果: 保存后立刻读 `settings.json` 看不到 `GPU.1.*`（Task 14 Step 5.5 的验收会误判），
> 而且保存后立刻崩溃/断电会丢掉这次的多卡设置。
>
> 这里在落盘**之前**先同步一次。

在 `src/UI/SettingsForm.cs` 中，把：

```csharp
            // 3. 持久化保存
            _cfg.Save();
```

替换为：

```csharp
            // 2.5 ★★★ [多显卡] 落盘前先同步显卡监控项 ★★★
            //     必须在 _cfg.Save() 之前 —— ApplyAllSettings 里那一次 Ensure
            //     发生在保存之后, 磁盘上的 MonitorItems 会比内存晚一轮。
            int detectedGpuCount = LiteMonitor.src.SystemServices.HardwareMonitor.TryGetDetectedGpuCount();
            int gpuCap = detectedGpuCount > 0
                ? detectedGpuCount
                : Math.Max(1, _cfg.MonitoredGpuCount);
            GpuItemSync.Ensure(_cfg, availableGpuCount: gpuCap);

            // 3. 持久化保存
            _cfg.Save();
```

> `GpuItemSync` 在 `LiteMonitor.src.Core`, 该文件已有 `using LiteMonitor.src.Core;` —— 无需补 using。
> `HardwareMonitor` 在 `LiteMonitor.src.SystemServices`, 而 `SettingsForm.cs` 在 `namespace LiteMonitor.src.UI`,
> **C# 的名字查找不会下钻到兄弟命名空间** —— 所以这里用完整限定名
> `LiteMonitor.src.SystemServices.HardwareMonitor`（或给该文件补一行 `using LiteMonitor.src.SystemServices;`）。
>
> `GpuItemSync.Ensure` 在**同一个 cap** 下是幂等的（实测连调两次, 键集合完全一致、无重复项）。
> 但这两处各自独立调用 `HardwareMonitor.TryGetDetectedGpuCount()` ——
> 若两次之间硬件被热插拔导致 cap 变了, 第二次 Ensure（在 Save 之后）会再夹一次, 磁盘又会落后一轮。
> 这个窗口极小（保存流程几十毫秒内完成）且下次启动自愈, 知道即可。

- [ ] **Step 3: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 4: 提交**

```powershell
git add src/Core/Actions/AppActions.cs src/UI/Helpers/MenuMonitorHelper.cs src/System/HardwareMonitor.cs src/System/HardwareServices/HardwareScanner.cs src/UI/SettingsForm.cs
git commit -m "feat(gpu): 保存设置时同步显卡监控项并按实际卡数夹紧

设置页只写 MonitoredGpuCount; 这里在重建布局之前统一生成 GPU.{序号}.{指标},
并把数量夹到实际检测到的显卡数, 避免选 8 张但只有 2 张时出现空白监控项。"
```

---

## Task 6: SensorMap 多卡映射核心（同时修 GPU.Fan 串卡）


**Files:**
- Modify: `src/System/HardwareServices/SensorMap.cs`

**Interfaces:**
- Consumes: `GpuKey`（Task 2）、`GpuRegistry`（Task 2）、`HardwareRules.GetShortGpuName`（Task 3）
- Produces:
  - `SensorMap.CachedGpus : List<IHardware>`（被监控显卡，按序号排列）
  - `SensorMap.GetGpuByIndex(int index) -> IHardware?`

- [ ] **Step 1: 新增 CachedGpus 字段与访问器**

在 `src/System/HardwareServices/SensorMap.cs` 中，把：

```csharp
        // 显卡硬件缓存 (用于快速定位)
        public IHardware? CachedGpu { get; private set; }
```

替换为：

```csharp
        // 显卡硬件缓存 (用于快速定位)
        public IHardware? CachedGpu { get; private set; }

        // ★★★ [多显卡] 被监控的显卡列表, 下标即 Key 中的序号 ★★★
        public List<IHardware> CachedGpus { get; private set; } = new List<IHardware>();

        /// <summary>取指定序号的被监控显卡, 越界返回 null</summary>
        public IHardware? GetGpuByIndex(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= CachedGpus.Count) return null;
                return CachedGpus[index];
            }
        }
```

- [ ] **Step 2: 在 Clear 中一并清理**

在 `Clear()` 方法里，把：

```csharp
                CachedGpu = null;
                CpuBusSpeedSensor = null;
```

替换为：

```csharp
                CachedGpu = null;
                CachedGpus = new List<IHardware>();
                CpuBusSpeedSensor = null;
                GpuRegistry.Clear();
```

- [ ] **Step 3: 新增选卡方法 SelectMonitoredGpus**

在 `src/System/HardwareServices/SensorMap.cs` 的 `Rebuild` 方法之前插入：

```csharp
        /// <summary>
        /// 选出被监控的显卡集合。
        /// 规则:
        ///   - MonitoredGpuCount == 1 : 沿用旧逻辑, 优先用 PreferredGpu 指定的卡, 否则取优先级最高的一张
        ///   - MonitoredGpuCount >= 2 : 按优先级取前 N 张
        /// 顺序与 Rebuild 中的遍历顺序一致, 保证 Key 中的序号稳定。
        /// </summary>
        private static List<IHardware> SelectMonitoredGpus(Computer computer, Settings cfg)
        {
            var all = computer.Hardware
                .Where(HardwareScanner.IsGpuHardware)
                .OrderBy(h => HardwareRules.GetHwPriority(h))
                .ToList();

            if (all.Count == 0) return new List<IHardware>();

            int want = cfg.MonitoredGpuCount;
            if (want < 1) want = 1;

            if (want == 1)
            {
                var selected = HardwareScanner.FindGpuByConfig(computer, cfg.PreferredGpu ?? "");
                if (selected != null) return new List<IHardware> { selected };
                return new List<IHardware> { all[0] };
            }

            return all.Take(Math.Min(want, all.Count)).ToList();
        }

        /// <summary>
        /// 在被监控显卡列表中按引用查找序号, 找不到返回 -1。
        /// 这里刻意不用 Dictionary + ReferenceEqualityComparer —— 该比较器只实现了
        /// IEqualityComparer&lt;object&gt;, 不能直接用于 Dictionary&lt;IHardware, int&gt;。
        /// </summary>
        private static int FindGpuIndex(List<IHardware> monitored, IHardware hw)
        {
            for (int i = 0; i < monitored.Count; i++)
            {
                if (ReferenceEquals(monitored[i], hw)) return i;
            }
            return -1;
        }
```

> 需要 `using System.Collections.Generic;`（文件已有）与 `using System.Linq;`（文件已有）。

- [ ] **Step 4: 改造 Rebuild 的选卡与注册逻辑**

在 `Rebuild` 方法中，把：

```csharp
            string preferredGpu = cfg.PreferredGpu ?? "";
            IHardware? selectedGpu = HardwareScanner.FindGpuByConfig(computer, preferredGpu);
```

替换为：

```csharp
            // ★★★ [多显卡] 选出被监控的显卡集合 (序号即 Key 中的序号) ★★★
            var monitoredGpus = SelectMonitoredGpus(computer, cfg);

            // ★★★ [多显卡] 显卡简称的本地缓冲区, 在原子交换点整体写入 GpuRegistry ★★★
            var gpuInfoBuffer = new GpuInfo[GpuKey.MaxGpuCount];

```

- [ ] **Step 5: 改造 RegisterTo 的签名与显卡分支**

在 `Rebuild` 中，把：

```csharp
            // 局部递归函数
            void RegisterTo(IHardware hw)
            {
                //hw.Update();

                bool isGpuHardware = HardwareScanner.IsGpuHardware(hw);

                // 用户选择的显卡仍存在时只映射指定显卡；配置失效或旧名称无法唯一定位时回退自动优先级。
                if (isGpuHardware &&
                    selectedGpu != null &&
                    !ReferenceEquals(hw, selectedGpu))
                {
                    return;
                }
```

替换为：

```csharp
            // 局部递归函数
            void RegisterTo(IHardware hw, int gpuIndex)
            {
                //hw.Update();

                bool isGpuHardware = HardwareScanner.IsGpuHardware(hw);

                // ★★★ [多显卡] 只有在监控集合里的显卡才继续处理 ★★★
                if (isGpuHardware && gpuIndex < 0) return;
```

- [ ] **Step 6: 改造 GPU 缓存与风扇映射分支**

在 `RegisterTo` 内，把：

```csharp
                // --- 填充 GPU 缓存 (优化版：完全依赖优先级排序) ---
                if (isGpuHardware)
                {
                    // 因为外层循环已经按照 GetHwPriority 排序（强力显卡在前），
                    // 所以第一个遇到的显卡就是最强的，直接锁定即可。
                    // 移除了复杂的“后来者居上”判断逻辑，避免过度设计。
                    if (newGpu == null)
                    {
                        newGpu = hw;
                    }
                    
                    // ★★★ [新增] GPU 风扇映射 ★★★
                    var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    if (fan == null) fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);
                    if (fan != null) newMap["GPU.Fan"] = fan;
                }
```

替换为：

```csharp
                // --- 填充 GPU 缓存 (优化版：完全依赖优先级排序) ---
                if (isGpuHardware)
                {
                    if (newGpu == null)
                    {
                        newGpu = hw;
                    }

                    // ★★★ [多显卡] 登记显卡简称, 供 UI 打标签 ★★★
                    // 先写进本地数组, 最后在原子交换点整体替换 GpuRegistry ——
                    // 避免读侧 (UI 线程) 看到"半更新"的注册表。
                    if (gpuIndex >= 0 && gpuIndex < gpuInfoBuffer.Length)
                    {
                        gpuInfoBuffer[gpuIndex] = new GpuInfo
                        {
                            ShortName = HardwareRules.GetShortGpuName(hw.Name),
                            FullName = hw.Name
                        };
                    }


                    // ★★★ [多显卡] GPU 风扇映射 (按序号写各自的 Key) ★★★
                    // 修复: 旧代码这里是无条件赋值 newMap["GPU.Fan"] = fan,
                    //       多卡时后一张卡会覆盖前一张, 导致风扇读数串卡。
                    //       现在每张卡写进自己的 "GPU.{序号}.Fan", 互不干扰。
                    var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    if (fan == null) fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);
                    if (fan != null)
                    {
                        string fanKey = GpuKey.Build(gpuIndex, "Fan");
                        if (!newMap.ContainsKey(fanKey)) newMap[fanKey] = fan;

                        // 第 0 张卡额外写入旧格式 Key, 保证旧配置继续可用
                        if (gpuIndex == 0 && !newMap.ContainsKey("GPU.Fan")) newMap["GPU.Fan"] = fan;
                    }
                }
```

- [ ] **Step 7: 改造普通传感器映射以带上序号**

在 `RegisterTo` 内，把：

```csharp
                // --- 普通传感器映射 ---
                foreach (var s in hw.Sensors)
                {
                    string? key = SensorMatcher.Match(hw, s); // 调用 SensorMatcher 进行识别
                    if (!string.IsNullOrEmpty(key))
                    {
                        if (!newMap.ContainsKey(key))
                        {
                            newMap[key] = s;
                        }
                        else
                        {
                            // [Fix] 冲突解决策略：优先选择 Vendor (非 D3D) 传感器
                            // 必须严格保护高优先级显卡的数据，防止被低优先级显卡覆盖！
                            var existing = newMap[key];
                            
                            // 1. 如果现有数据来自"更强"的显卡 (Priority数值更小)，绝对禁止覆盖！
                            // (注意：RegisterTo 按照 0->100 顺序遍历，所以 existing 的优先级只可能 <= 当前 hw 的优先级)
                            // 如果 existing < hw，说明它是更强的显卡写入的，直接跳过。
                            if (HardwareRules.GetHwPriority(existing.Hardware) < HardwareRules.GetHwPriority(hw))
                            {
                                continue;
                            }

                            // 2. 如果优先级相同 (通常是同一张显卡的不同传感器，或者是两张同样的显卡)，
                            // 则应用 D3D vs Vendor 优选逻辑。
                            bool existingIsD3D = existing.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            bool newIsD3D = s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            
                            // 如果现有的是 D3D 而新来的不是 D3D -> 替换为新来的 (Upgrade)
                            if (existingIsD3D && !newIsD3D)
                            {
                                newMap[key] = s;
                            }
                            // 其他情况 (Vendor vs Vendor, Vendor vs D3D) -> 保持现有 (First Wins / Vendor Wins)
                        }
                    }
                }

                foreach (var sub in hw.SubHardware) RegisterTo(sub);
            }

            // 按优先级排序并注册
            var ordered = computer.Hardware.OrderBy(h => HardwareRules.GetHwPriority(h));
            foreach (var hw in ordered) RegisterTo(hw);
```

替换为：

```csharp
                // --- 普通传感器映射 ---
                foreach (var s in hw.Sensors)
                {
                    string? key = SensorMatcher.Match(hw, s); // 调用 SensorMatcher 进行识别
                    if (!string.IsNullOrEmpty(key))
                    {
                        // ★★★ [多显卡] 显卡指标改写成带序号的形式 ★★★
                        bool isGpuKey = isGpuHardware
                                        && key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase);
                        string finalKey = key;
                        if (isGpuKey)
                        {
                            string metric = key.Substring(GpuKey.Prefix.Length);
                            finalKey = GpuKey.Build(gpuIndex, metric);
                        }

                        if (!newMap.ContainsKey(finalKey))
                        {
                            newMap[finalKey] = s;
                        }
                        else
                        {
                            // [Fix] 冲突解决策略：优先选择 Vendor (非 D3D) 传感器
                            // 必须严格保护高优先级显卡的数据，防止被低优先级显卡覆盖！
                            var existing = newMap[finalKey];
                            
                            // 1. 如果现有数据来自"更强"的显卡 (Priority数值更小)，绝对禁止覆盖！
                            if (HardwareRules.GetHwPriority(existing.Hardware) < HardwareRules.GetHwPriority(hw))
                            {
                                continue;
                            }

                            // 2. 如果优先级相同，则应用 D3D vs Vendor 优选逻辑。
                            bool existingIsD3D = existing.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            bool newIsD3D = s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            
                            // 如果现有的是 D3D 而新来的不是 D3D -> 替换为新来的 (Upgrade)
                            if (existingIsD3D && !newIsD3D)
                            {
                                newMap[finalKey] = s;
                            }
                            // 其他情况 (Vendor vs Vendor, Vendor vs D3D) -> 保持现有 (First Wins / Vendor Wins)
                        }

                        // ★★★ [多显卡] 第 0 张卡额外写入旧格式 Key, 兼容旧配置 ★★★
                        if (isGpuKey && gpuIndex == 0 && !newMap.ContainsKey(key))
                        {
                            newMap[key] = s;
                        }
                    }
                }

                foreach (var sub in hw.SubHardware) RegisterTo(sub, gpuIndex);
            }

            // 按优先级排序并注册
            var ordered = computer.Hardware.OrderBy(h => HardwareRules.GetHwPriority(h));
            foreach (var hw in ordered)
            {
                int gpuIndex = FindGpuIndex(monitoredGpus, hw);
                RegisterTo(hw, gpuIndex);
            }
```

- [ ] **Step 8: 在原子交换处写入 CachedGpus**

在 `Rebuild` 的原子交换块中，把：

```csharp
                CpuCoreCache = newCpuCache;
                CachedGpu = newGpu;
                CpuBusSpeedSensor = newBusSensor; // ★ 更新 Bus Sensor 缓存
```

替换为：

```csharp
                CpuCoreCache = newCpuCache;
                CachedGpu = newGpu;
                CachedGpus = new List<IHardware>(monitoredGpus); // ★★★ [多显卡] ★★★
                CpuBusSpeedSensor = newBusSensor; // ★ 更新 Bus Sensor 缓存

                // ★★★ [多显卡] 显卡简称注册表整体替换 (原子) ★★★
                GpuRegistry.ReplaceAll(gpuInfoBuffer);

```

- [ ] **Step 9: 编译验证**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
```

Expected: 0 错误。**特别确认**没有 `CS0165 使用了未赋值的局部变量 "selectedGpu"` 之类残留（Step 4 已把 `selectedGpu` 的声明删掉，Step 7 也不再引用它）。

- [ ] **Step 10: 提交**

```powershell
git add src/System/HardwareServices/SensorMap.cs
git commit -m "feat(gpu): SensorMap 支持多显卡映射并修复风扇串卡

每张被监控显卡写入独立的 GPU.{序号}.{指标} 映射; 顺带修复旧代码里
newMap[\"GPU.Fan\"] 无条件赋值导致多卡时风扇读数被后一张卡覆盖的问题。"
```

---

## Task 7: ComponentProcessor 按序号取频率与功耗

**Files:**
- Modify: `src/System/HardwareServices/ComponentProcessor.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse`（Task 2）、`SensorMap.GetGpuByIndex`（Task 6）
- Produces: `ComponentProcessor.GetCompositeValue` 支持 `GPU.{序号}.Clock` / `GPU.{序号}.Power`

- [ ] **Step 1: 改造 GPU 分支**

在 `src/System/HardwareServices/ComponentProcessor.cs` 中，把：

```csharp
            if (key.StartsWith("GPU"))
            {
                var gpu = _sensorMap.CachedGpu;
                if (gpu == null) return null;
                if (key == "GPU.Clock")
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Clock && (SensorMap.Has(x.Name, "graphics") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "shader")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 6000.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
                else if (key == "GPU.Power")
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && (SensorMap.Has(x.Name, "package") || SensorMap.Has(x.Name, "ppt") || SensorMap.Has(x.Name, "board") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "gpu")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 1200.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
            }
```

替换为：

```csharp
            if (key.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
            {
                // ★★★ [多显卡] 解析出显卡序号, 取对应那张卡的传感器 ★★★
                int gpuIndex = -1;
                string metric = key.Substring(GpuKey.Prefix.Length);
                if (GpuKey.TryParse(key, out int parsedIndex, out string parsedMetric))
                {
                    gpuIndex = parsedIndex;
                    metric = parsedMetric;
                }

                var gpu = gpuIndex >= 0 ? _sensorMap.GetGpuByIndex(gpuIndex) : _sensorMap.CachedGpu;
                if (gpu == null) return null;

                if (metric.Equals("Clock", StringComparison.OrdinalIgnoreCase))
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Clock && (SensorMap.Has(x.Name, "graphics") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "shader")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 6000.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
                else if (metric.Equals("Power", StringComparison.OrdinalIgnoreCase))
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && (SensorMap.Has(x.Name, "package") || SensorMap.Has(x.Name, "ppt") || SensorMap.Has(x.Name, "board") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "gpu")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 1200.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
            }
```

- [ ] **Step 2: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 3: 提交**

```powershell
git add src/System/HardwareServices/ComponentProcessor.cs
git commit -m "feat(gpu): 频率与功耗按显卡序号取对应显卡的传感器"
```

---

## Task 7.5: 标签兜底按基础 Key 归一化

> **为什么需要这一步**: 主界面 / 横条 / 任务栏的默认标签来自
> `UIController.BuildMetrics()` 与 `BuildColumnsCore()` 里的
> `LanguageManager.T("Items." + Key)` 与 `LanguageManager.T("Short." + Key)`。
> 对 `GPU.0.Load` 来说, 语言包里只有 `Items.GPU.Load`, 于是
> `LanguageManager.GetOriginal` 回退到第一个 "." 之后的后缀, 得到字面量 **"GPU.0.Load"**。
> `MetricLabelResolver` 那条路径 (Task 10) 只在 `GpuRegistry` 有值时才命中;
> 启动初期或该卡不在监控集合时注册表是空的 —— **兜底路径必须自己也能显示正确文案**。

**Files:**
- Modify: `src/UI/UIController.cs`

**Interfaces:**
- Consumes: `GpuKey.ToBaseKey`（Task 2）
- Produces: 带序号 Key 的默认标签回退到基础 Key 的语言包条目

- [ ] **Step 1: 改 BuildMetrics 的默认标签**

在 `src/UI/UIController.cs` 的 `BuildMetrics()` 中，把：

```csharp
                string label = LanguageManager.T(UIUtils.Intern("Items." + cfgItem.Key));
```

替换为：

```csharp
                // ★★★ [多显卡] 带序号的 Key ("GPU.0.Load") 要回退到基础 Key ("GPU.Load") ★★★
                // 否则语言包查不到时 LanguageManager 会回退成字面量 "GPU.0.Load"。
                string label = LanguageManager.T(UIUtils.Intern("Items." + GpuKey.ToBaseKey(cfgItem.Key)));
```

并把紧邻的：

```csharp
                string defShort = LanguageManager.T(UIUtils.Intern("Short." + cfgItem.Key));
```

替换为：

```csharp
                string defShort = LanguageManager.T(UIUtils.Intern("Short." + GpuKey.ToBaseKey(cfgItem.Key)));
```

- [ ] **Step 2: 改 BuildColumnsCore 的默认标签**

在 `src/UI/UIController.cs` 的 `BuildColumnsCore()` 中，把：

```csharp
            string defLabel = LanguageManager.T(UIUtils.Intern("Items." + cfg.Key));
```

替换为：

```csharp
            string defLabel = LanguageManager.T(UIUtils.Intern("Items." + GpuKey.ToBaseKey(cfg.Key)));
```

并把：

```csharp
            string defShort = LanguageManager.T(UIUtils.Intern("Short." + cfg.Key));
```

替换为：

```csharp
            string defShort = LanguageManager.T(UIUtils.Intern("Short." + GpuKey.ToBaseKey(cfg.Key)));
```

- [ ] **Step 3: 确认 using 完整**

`src/UI/UIController.cs` 顶部需要 `using LiteMonitor.src.Core;`（`GpuKey`）。该文件第 1 行已有
`using LiteMonitor.src.Core;` —— 确认即可。

- [ ] **Step 4: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 5: 提交**

```powershell
git add src/UI/UIController.cs
git commit -m "fix(gpu): 标签兜底按基础 Key 归一化

带序号的 Key 查语言包时先归一化成基础 Key, 否则会回退成字面量 GPU.0.Load。"
```

---

## Task 8: 显存按卡计算


**Files:**
- Modify: `src/System/HardwareServices/HardwareValueProvider.cs`
- Modify: `src/Core/MetricUtils.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse`（Task 2）、`Settings.DetectedGpuVramTotalGBByKey`（Task 5）
- Produces:
  - `HardwareValueProvider.GetGpuVramPercent(string key) -> float?`（private）
  - `HardwareValueProvider.ReadFanValue(string key) -> float?`（private）
  - `MetricUtils.GetGpuVramTotalGB(string key) -> double`（private static）

- [ ] **Step 0: 让带序号的 Fan 键也走 UpdateMaxRecord**

> `GetValue` 里风扇是**精确 case** (`case "GPU.Fan":`), 峰值记录 (`_cfg.UpdateMaxRecord`) 就在那个 case 里。
> 带序号的 `"GPU.0.Fan"` 匹配不上, 会落到 default 分支之后的通用兜底 ——
> **能取到值, 但不会记录峰值**。两个后果:
> - 每卡独立峰值对风扇完全失效 (Task 9 的承诺落空);
> - 迁移后没人再请求 `"GPU.Fan"`, 全局 `RecordedMaxGpuFan` 也停止更新, 风扇进度条会恒红。

在 `src/System/HardwareServices/HardwareValueProvider.cs` 的 `GetValue` 方法中，把：

```csharp
                    // 8. 风扇/泵 (带 Max 记录)
                    case "CPU.Fan":
                    case "CPU.Pump":
                    case "CASE.Fan":
                    case "GPU.Fan":
                        if (_manualSensorCache.TryGetValue(key, out var sFan))
                        {
                            result = sFan.Value;
                        }
                        else 
                        {
                            if (_manualSensorCache.TryGetValue(key, out var autoS) && autoS.Value.HasValue)
                                result = autoS.Value.Value;
                        }
                        if (result.HasValue && result.Value < 10000f) 
                            _cfg.UpdateMaxRecord(key, result.Value);
                        break;
```

替换为：

```csharp
                    // 8. 风扇/泵 (带 Max 记录)
                    case "CPU.Fan":
                    case "CPU.Pump":
                    case "CASE.Fan":
                    case "GPU.Fan":
                        result = ReadFanValue(key);
                        break;
```

并在 `private float? ReadMoboTemperature(ISensor sensor)` 之前插入：

```csharp
        /// <summary>
        /// 读取风扇/泵读数并记录峰值。
        /// 同时支持旧格式 ("CPU.Fan" / "GPU.Fan") 与多卡格式 ("GPU.0.Fan") ——
        /// 带序号的 Key 必须走这里, 否则不会触发 UpdateMaxRecord, 每卡峰值永远记不上。
        /// </summary>
        private float? ReadFanValue(string key)
        {
            float? result = null;
            if (_manualSensorCache.TryGetValue(key, out var sFan))
            {
                result = sFan.Value;
            }
            if (result.HasValue && result.Value < 10000f)
                _cfg.UpdateMaxRecord(key, result.Value);
            return result;
        }
```

> 注: 原 `else` 分支用同一个 key 又查了一次 `_manualSensorCache` (条件恒真), 属于无效代码,
> 这里一并去掉, 行为不变。

- [ ] **Step 1: 改造 HardwareValueProvider 的显存分支**


在 `src/System/HardwareServices/HardwareValueProvider.cs` 的 `GetValue` 方法中，把：

```csharp
                    // 7. 显存
                    case "GPU.VRAM":
                        float? used = GetValue("GPU.VRAM.Used");
                        float? total = GetValue("GPU.VRAM.Total");
                        if (used.HasValue && total.HasValue && total > 0)
                        {
                            if (Settings.DetectedGpuVramTotalGB <= 0) Settings.DetectedGpuVramTotalGB = total.Value / 1024f;
                            if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                            result = used / total * 100f;
                        }
                        else
                        {
                            if (_manualSensorCache.TryGetValue("GPU.VRAM.Load", out var s) && s.Value.HasValue) result = s.Value;
                        }
                        break;
```

替换为：

```csharp
                    // 7. 显存
                    case "GPU.VRAM":
                        result = GetGpuVramPercent(key);
                        break;
```

- [ ] **Step 2: 在 default 分支加带序号的显存与风扇处理**

在 `GetValue` 的 `default:` 分支中，把：

```csharp
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            if (useCounter && key == "CPU.Clock")
                            {
                                result = _perfManager.GetCpuFreq();
                            }
                            
                            if (result == null)
                            {
                                result = _componentProcessor.GetCompositeValue(key, _manualSensorCache);
                            }
                        }
                        break;
```

替换为：

```csharp
                        else if (key.EndsWith(".Fan", StringComparison.OrdinalIgnoreCase) ||
                                 key.EndsWith(".Pump", StringComparison.OrdinalIgnoreCase))
                        {
                            // ★★★ [多显卡] 带序号的 "GPU.0.Fan" 也要记录每卡峰值 ★★★
                            // 精确 case "GPU.Fan" 接不住它, 落到这里统一处理。
                            result = ReadFanValue(key);
                        }
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            if (useCounter && key == "CPU.Clock")
                            {
                                result = _perfManager.GetCpuFreq();
                            }
                            
                            if (result == null)
                            {
                                result = _componentProcessor.GetCompositeValue(key, _manualSensorCache);
                            }
                        }
                        else if (GpuKey.TryParse(key, out _, out string gpuMetric)
                                 && gpuMetric.Equals("VRAM", StringComparison.OrdinalIgnoreCase))
                        {
                            // ★★★ [多显卡] "GPU.1.VRAM" 等带序号的显存项 ★★★
                            result = GetGpuVramPercent(key);
                        }
                        break;
```


- [ ] **Step 3: 新增 GetGpuVramPercent 方法**

在 `src/System/HardwareServices/HardwareValueProvider.cs` 中，把：

```csharp
        private float? ReadMoboTemperature(ISensor sensor)
```

替换为：

```csharp
        /// <summary>
        /// 计算显存占用百分比。
        /// key 形如 "GPU.VRAM" (旧) 或 "GPU.1.VRAM" (多卡), 由此派生出 Used / Total 子 Key。
        /// </summary>
        private float? GetGpuVramPercent(string key)
        {
            // "GPU.VRAM" -> "GPU." ; "GPU.1.VRAM" -> "GPU.1."
            string prefix = key.Substring(0, key.Length - "VRAM".Length);

            float? used = GetValue(prefix + "VRAM.Used");
            float? total = GetValue(prefix + "VRAM.Total");
            if (used.HasValue && total.HasValue && total > 0)
            {
                // ★★★ 单位归一化: LHM 0.9.x 报的是 MB, 新版 LHM 报的是字节 ★★★
                // 阈值 10485760 MB 实际不可能出现, 所以命中即代表单位是字节
                if (total > 10485760) { used /= 1048576f; total /= 1048576f; }

                // 现在 total 的单位是 MB
                float gb = total.Value / 1024f;

                // 旧单卡缓存 (保留原有语义)
                if (Settings.DetectedGpuVramTotalGB <= 0 && gb > 0 && gb < 1024)
                {
                    Settings.DetectedGpuVramTotalGB = gb;
                }

                // ★★★ [多显卡] 每张卡单独记录自己的显存总容量 ★★★
                string cardKey = prefix.TrimEnd('.');
                if (gb > 0 && gb < 1024 && !Settings.DetectedGpuVramTotalGBByKey.ContainsKey(cardKey))
                {
                    Settings.DetectedGpuVramTotalGBByKey[cardKey] = gb;
                }

                return used / total * 100f;
            }

            if (_manualSensorCache.TryGetValue(prefix + "VRAM.Load", out var s) && s.Value.HasValue) return s.Value;
            return null;
        }

        private float? ReadMoboTemperature(ISensor sensor)
```

- [ ] **Step 4: 改造 MetricUtils 的容量模式取值**

在 `src/Core/MetricUtils.cs` 的 `GetValueStr` 方法中，把：

```csharp
                 var cfg = Settings.Load();
                 if (cfg.MemoryDisplayMode == 1) // 容量模式
                 {
                     double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ? Settings.DetectedRamTotalGB 
                        : Settings.DetectedGpuVramTotalGB;

                     if (totalGB > 0)
                     {
                         // 转换为字节 -> 格式化 -> 取数值部分
                         // [Refactor] 使用自适应格式化 (-1)，确保 100GB 时不显示 .0
                         return FormatDataSizeParts((v / 100.0) * totalGB * 1073741824.0, -1).val;
                     }
                 }
```

替换为：

```csharp
                 var cfg = Settings.Load();
                 if (cfg.MemoryDisplayMode == 1) // 容量模式
                 {
                     double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ? Settings.DetectedRamTotalGB 
                        : GetGpuVramTotalGB(key);

                     if (totalGB > 0)
                     {
                         // 转换为字节 -> 格式化 -> 取数值部分
                         // [Refactor] 使用自适应格式化 (-1)，确保 100GB 时不显示 .0
                         return FormatDataSizeParts((v / 100.0) * totalGB * 1073741824.0, -1).val;
                     }
                 }
```

- [ ] **Step 5: 改造 MetricUtils 的单位取值**

在 `src/Core/MetricUtils.cs` 的 `GetUnitStr` 方法中，把：

```csharp
                double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) ? Settings.DetectedRamTotalGB : Settings.DetectedGpuVramTotalGB;
```

替换为：

```csharp
                double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) ? Settings.DetectedRamTotalGB : GetGpuVramTotalGB(key);
```

- [ ] **Step 6: 新增 GetGpuVramTotalGB 辅助方法**

在 `src/Core/MetricUtils.cs` 中，把：

```csharp
        public static double GetAdaptivePercentage(string key, double val)
```

替换为：

```csharp
        /// <summary>
        /// 取指定 Key 对应显卡的显存总容量 (GB)。
        /// "GPU.1.VRAM" -> 查 "GPU.1" ; "GPU.VRAM" -> 查 "GPU" (旧格式回退)
        /// </summary>
        private static double GetGpuVramTotalGB(string key)
        {
            string prefix = key ?? "";
            int vramPos = prefix.LastIndexOf("VRAM", StringComparison.OrdinalIgnoreCase);
            if (vramPos > 0) prefix = prefix.Substring(0, vramPos);
            prefix = prefix.TrimEnd('.');

            if (Settings.DetectedGpuVramTotalGBByKey.TryGetValue(prefix, out float total) && total > 0)
                return total;

            return Settings.DetectedGpuVramTotalGB; // 旧单卡缓存回退
        }

        public static double GetAdaptivePercentage(string key, double val)
```

- [ ] **Step 7: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 8: 提交**

```powershell
git add src/System/HardwareServices/HardwareValueProvider.cs src/Core/MetricUtils.cs
git commit -m "feat(gpu): 显存占用按卡独立计算

带序号的显存 Key 派生出各自的 Used/Total 子 Key; 容量模式按每张卡自己的
显存总容量换算, 并把单位归一化提到换算之前, 同时兼容 MB 与字节两种单位。"
```

---

## Task 9: 阈值自适应按卡归一化

**Files:**
- Modify: `src/Core/MetricUtils.cs`（`GetAdaptivePercentage`）
- Create: `tests/LiteMonitor.Tests/MetricUtilsGpuKeyTests.cs`

**Interfaces:**
- Consumes: `GpuKey.ToBaseKey`（Task 2）、`Settings.RecordedMaxGpuDynamic`（Task 5）
- Produces: `MetricUtils.GetAdaptivePercentage(string key, double val)` 对 `GPU.{序号}.*` 返回按该卡峰值算出的比例

- [ ] **Step 1: 写失败测试**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\MetricUtilsGpuKeyTests.cs`：

```csharp
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class MetricUtilsGpuKeyTests
    {
        [Theory]
        [InlineData("GPU.0.Temp", MetricType.Temperature)]
        [InlineData("GPU.1.Temp", MetricType.Temperature)]
        [InlineData("GPU.1.VRAM", MetricType.Memory)]
        [InlineData("GPU.1.Load", MetricType.Percent)]
        [InlineData("GPU.1.Power", MetricType.Power)]
        [InlineData("GPU.1.Clock", MetricType.Frequency)]
        [InlineData("GPU.1.Fan", MetricType.RPM)]
        public void GetType_带序号的Key_类型判定不变(string key, MetricType expected)
        {
            Assert.Equal(expected, MetricUtils.GetType(key));
        }

        [Fact]
        public void GetAdaptivePercentage_带序号的Key_使用该卡自己的峰值()
        {
            var cfg = Settings.Load();
            cfg.RecordedMaxGpuDynamic["GPU.1.Power"] = 200f;

            double pct = MetricUtils.GetAdaptivePercentage("GPU.1.Power", 100.0);
            Assert.Equal(0.5, pct, 3);

            // 另一张卡没有单独峰值时, 走全局 RecordedMaxGpuPower
            cfg.RecordedMaxGpuDynamic.Remove("GPU.2.Power");
            cfg.RecordedMaxGpuPower = 400f;
            double pct2 = MetricUtils.GetAdaptivePercentage("GPU.2.Power", 100.0);
            Assert.Equal(0.25, pct2, 3);
        }
    }
}
```

> 注：测试依赖 `Settings.Load()` 单例。若后续 Task 引入会污染单例的用例，需在测试里 `cfg.RecordedMaxGpuDynamic.Clear()` 复位。

- [ ] **Step 2: 运行测试确认失败**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: `GetAdaptivePercentage_带序号的Key_使用该卡自己的峰值` 失败（当前实现对 `GPU.1.Power` 取不到 max，返回 1.0 而非 0.5）

- [ ] **Step 3: 改造 GetAdaptivePercentage**

在 `src/Core/MetricUtils.cs` 中，把：

```csharp
        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            if (key == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (key == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (key == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (key == "GPU.Power") max = cfg.RecordedMaxGpuPower;
            else if (key == "CPU.Fan") max = cfg.RecordedMaxCpuFan;
            else if (key == "CPU.Pump") max = cfg.RecordedMaxCpuPump;
            else if (key == "CASE.Fan") max = cfg.RecordedMaxChassisFan;
            else if (key == "GPU.Fan") max = cfg.RecordedMaxGpuFan;
            else if (key == "FPS") max = cfg.RecordedMaxFps;
            else if (key == "CPU.Voltage") max = 1.6f;
            else if (key == "BAT.Power") max = 100f;
            else if (key == "BAT.Voltage") max = 20f;
            else if (key == "BAT.Current") max = 5f;

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }
```

替换为：

```csharp
        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            // ★★★ [多显卡] 带序号的 Key ("GPU.1.Power") 归一化成基础 Key ("GPU.Power") ★★★
            string baseKey = GpuKey.ToBaseKey(key);

            if (baseKey == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (baseKey == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (baseKey == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (baseKey == "GPU.Power") max = cfg.RecordedMaxGpuPower;
            else if (baseKey == "CPU.Fan") max = cfg.RecordedMaxCpuFan;
            else if (baseKey == "CPU.Pump") max = cfg.RecordedMaxCpuPump;
            else if (baseKey == "CASE.Fan") max = cfg.RecordedMaxChassisFan;
            else if (baseKey == "GPU.Fan") max = cfg.RecordedMaxGpuFan;
            else if (baseKey == "FPS") max = cfg.RecordedMaxFps;
            else if (baseKey == "CPU.Voltage") max = 1.6f;
            else if (baseKey == "BAT.Power") max = 100f;
            else if (baseKey == "BAT.Voltage") max = 20f;
            else if (baseKey == "BAT.Current") max = 5f;

            // ★★★ [多显卡] 该卡有独立峰值时优先使用, 让每张卡的进度条各自自适应 ★★★
            if (key != baseKey
                && cfg.RecordedMaxGpuDynamic != null
                && cfg.RecordedMaxGpuDynamic.TryGetValue(key, out float dynMax)
                && dynMax > 1f)
            {
                max = dynMax;
            }

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }
```

- [ ] **Step 4: 运行测试确认通过**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过

- [ ] **Step 5: 提交**

```powershell
git add src/Core/MetricUtils.cs tests/LiteMonitor.Tests/MetricUtilsGpuKeyTests.cs
git commit -m "fix(gpu): 阈值自适应对带序号的 Key 做归一化

否则 GPU.{序号}.Power 匹配不到全局最大值, 进度条会恒满且恒为红色。"
```

---

## Task 10: 多卡标签

**Files:**
- Modify: `src/Core/MetricLabelResolver.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse`（Task 2）、`GpuRegistry.GetShortName`（Task 2）
- Produces:
  - `MetricLabelResolver.ResolveLabel` 对 `GPU.{序号}.{指标}` 返回 `"{显卡简称} {基础标签}"`
  - `MetricLabelResolver.ResolveShortLabel` 对 `GPU.{序号}.{指标}` 返回显卡简称

- [ ] **Step 1: 改造 ResolveLabel**

在 `src/Core/MetricLabelResolver.cs` 中，把：

```csharp
        public static string ResolveLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义 (最高优先级)
            if (!string.IsNullOrEmpty(config.UserLabel)) 
                return config.UserLabel;
            
            // 2. 运行时动态注入 (高优先级)
```

替换为：

```csharp
        public static string ResolveLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义 (最高优先级)
            if (!string.IsNullOrEmpty(config.UserLabel)) 
                return config.UserLabel;

            // 2. [多显卡] 标签带上显卡简称, 例如 "3080 Ti 温度"
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out string metric))
            {
                string shortName = GpuRegistry.GetShortName(gpuIndex);
                if (!string.IsNullOrEmpty(shortName))
                {
                    string baseLabel = LanguageManager.T("Items." + GpuKey.BaseKey(metric));
                    return $"{shortName} {StripGpuPrefix(baseLabel)}".Trim();
                }
            }

            // 3. 运行时动态注入 (高优先级)
```

并把该方法剩余的注释编号顺延（原「3. 配置中的动态标签」改为「4.」，原「4. 默认/失败」改为「5.」）——**仅改注释，不改逻辑**。

- [ ] **Step 2: 改造 ResolveShortLabel**

在 `src/Core/MetricLabelResolver.cs` 中，把：

```csharp
        public static string ResolveShortLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义
            if (!string.IsNullOrEmpty(config.TaskbarLabel)) 
                return config.TaskbarLabel;

            // 2. 运行时动态注入
```

替换为：

```csharp
        public static string ResolveShortLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义
            if (!string.IsNullOrEmpty(config.TaskbarLabel)) 
                return config.TaskbarLabel;

            // 2. [多显卡] 任务栏用显卡简称区分 (数值单位已能区分指标: % / °C / W / GHz)
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out _))
            {
                string shortName = GpuRegistry.GetShortName(gpuIndex);
                if (!string.IsNullOrEmpty(shortName)) return shortName;
            }

            // 3. 运行时动态注入
```

并把该方法剩余的注释编号顺延（原「3. 配置中的动态标签」改为「4.」）——**仅改注释，不改逻辑**。

- [ ] **Step 3: 新增 StripGpuPrefix 辅助方法**

在 `src/Core/MetricLabelResolver.cs` 的 `ResolveShortLabel` 方法之后、`class` 结束括号之前插入：

```csharp
        /// <summary>
        /// 去掉基础标签里的 "GPU" 前缀, 避免拼成 "3080 Ti GPU温度"。
        /// "GPU温度" -> "温度" ; "GPU Load" -> "Load" ; "显存占用" -> "显存占用"
        /// </summary>
        private static string StripGpuPrefix(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            string t = label.Trim();
            if (t.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
            {
                t = t.Substring(3).Trim();
            }
            return t.Length > 0 ? t : label.Trim();
        }
```

- [ ] **Step 4: 确认 using 完整**

`src/Core/MetricLabelResolver.cs` 顶部需要 `using System;`（用到了 `StringComparison`）与 `using LiteMonitor.src.Core;`（同命名空间，可省）。若编译报 `StringComparison` 未找到，补 `using System;`。

- [ ] **Step 5: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 6: 提交**

```powershell
git add src/Core/MetricLabelResolver.cs
git commit -m "feat(gpu): 多显卡监控项标签带上显卡简称

主界面显示 \"3080 Ti 温度\" 这种形式, 任务栏简称用显卡名区分, 单位仍能区分指标。"
```

---

## Task 11: 历史记录支持多卡

**Files:**
- Modify: `src/Core/HardwareHistoryLogger.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse`（Task 2）
- Produces: `HardwareHistoryLogger.GetEnabledSupportedKeys` 额外产出带序号的显卡 Key（去重）

- [ ] **Step 1: 改造 GetEnabledSupportedKeys**

在 `src/Core/HardwareHistoryLogger.cs` 中，把：

```csharp
        private static IEnumerable<string> GetEnabledSupportedKeys(Settings cfg)
        {
            foreach (string key in _supportedKeys)
            {
                bool enabled = cfg.MonitorItems.Any(item =>
                    item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    (item.VisibleInPanel || item.VisibleInTaskbar));

                if (enabled) yield return key;
            }
        }
```

替换为：

```csharp
        private static IEnumerable<string> GetEnabledSupportedKeys(Settings cfg)
        {
            // 用 HashSet 去重 —— 同一个 key 出现两次会让采样值被重复累加
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string key in _supportedKeys)
            {
                bool enabled = cfg.MonitorItems.Any(item =>
                    item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    (item.VisibleInPanel || item.VisibleInTaskbar));

                if (enabled) result.Add(key);
            }

            // ★★★ [多显卡] 带序号的显卡 Key (如 "GPU.1.Temp") 也要记录 ★★★
            foreach (var item in cfg.MonitorItems)
            {
                if (!(item.VisibleInPanel || item.VisibleInTaskbar)) continue;
                if (GpuKey.TryParse(item.Key, out _, out _)) result.Add(item.Key);
            }

            return result;
        }
```

- [ ] **Step 2: 确认 using 完整**

`src/Core/HardwareHistoryLogger.cs` 顶部需要 `using System.Collections.Generic;`（`HashSet`）。若编译报未找到，补上。

- [ ] **Step 3: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 4: 提交**

```powershell
git add src/Core/HardwareHistoryLogger.cs
git commit -m "feat(gpu): 硬件历史记录支持多显卡 Key

用 HashSet 去重, 避免同一 key 被重复采样累加。"
```

---

## Task 11.5: 历史曲线展示侧支持带序号 Key

> **为什么需要这一步**: Task 11 只改了**记录侧** (`HardwareHistoryLogger.GetEnabledSupportedKeys`)。
> 展示侧 `HardwareTrendForm.GetCurrentKeys()` 返回的是**静态旧格式数组**
> (`HardwareHistoryLogger.TemperatureKeys` 等), 里面只有 `"GPU.Temp"` 这种旧 Key。
> 结果: `GPU.0.Temp` / `GPU.1.Temp` 会被采样、会写进 `HardwareHistory.json`,
> 但**监控历史窗口永远画不出这两条曲线** —— Task 14 Step 5 的验收标准必然失败。

**Files:**
- Modify: `src/UI/HardwareTrendForm.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse` / `GpuKey.BaseKey`（Task 2）
- Produces: 历史曲线窗口按分类列出带序号的显卡 Key，并隐藏失去对应监控项的旧格式 Key

- [ ] **Step 1: 改造 GetCurrentKeys**

在 `src/UI/HardwareTrendForm.cs` 中，把：

```csharp
        private string[] GetCurrentKeys()
        {
            return _category switch
            {
                TrendCategory.Temperature => HardwareHistoryLogger.TemperatureKeys,
                TrendCategory.Load => HardwareHistoryLogger.LoadKeys,
                TrendCategory.Power => HardwareHistoryLogger.PowerKeys,
                TrendCategory.Frequency => HardwareHistoryLogger.FrequencyKeys,
                TrendCategory.Fps => HardwareHistoryLogger.FpsKeys,
                _ => HardwareHistoryLogger.TemperatureKeys
            };
        }
```

替换为：

```csharp
        private string[] GetCurrentKeys()
        {
            string[] baseKeys = _category switch
            {
                TrendCategory.Temperature => HardwareHistoryLogger.TemperatureKeys,
                TrendCategory.Load => HardwareHistoryLogger.LoadKeys,
                TrendCategory.Power => HardwareHistoryLogger.PowerKeys,
                TrendCategory.Frequency => HardwareHistoryLogger.FrequencyKeys,
                TrendCategory.Fps => HardwareHistoryLogger.FpsKeys,
                _ => HardwareHistoryLogger.TemperatureKeys
            };

            // ★★★ [多显卡] 补上带序号的显卡 Key ("GPU.0.Temp" / "GPU.1.Temp") ★★★
            // 只加当前分类下、且用户确实开启了的项。
            var indexed = new List<string>();
            foreach (var item in _cfg.MonitorItems)
            {
                if (!(item.VisibleInPanel || item.VisibleInTaskbar)) continue;
                if (!GpuKey.TryParse(item.Key, out _, out string metric)) continue;

                string baseKey = GpuKey.BaseKey(metric);
                bool inCategory = baseKeys.Any(k =>
                    string.Equals(k, baseKey, StringComparison.OrdinalIgnoreCase));
                if (!inCategory) continue;

                if (!indexed.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                    indexed.Add(item.Key);
            }

            // ★★★ [多显卡] 旧格式的显卡 Key ("GPU.Temp") 在迁移后已没有对应的监控项,
            // 继续显示只会留下一条"冻结在升级前最后一个点"的旧曲线, 这里剔除。 ★★★
            // 注意: 不要写成 .Where(i => GpuKey.TryParse(i.Key, out _, out string m)).Select(i => GpuKey.BaseKey(m))
            // —— `m` 声明在 Where 的 lambda 里, 下一个 lambda 根本看不到它 (CS0103 编译错误)。
            var indexedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _cfg.MonitorItems)
            {
                if (GpuKey.TryParse(item.Key, out _, out string metric))
                    indexedBases.Add(GpuKey.BaseKey(metric));
            }


            var result = new List<string>();
            foreach (var k in baseKeys)
            {
                if (k.StartsWith("GPU.", StringComparison.OrdinalIgnoreCase) &&
                    indexedBases.Contains(k)) continue;
                result.Add(k);
            }
            result.AddRange(indexed);

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
```

- [ ] **Step 2: 补 using**

`src/UI/HardwareTrendForm.cs` 需要 `using System.Linq;`（`Any` / `Where` / `Select` / `Distinct` /
`Contains(..., IEqualityComparer)`）与 `using LiteMonitor.src.Core;`（`GpuKey`）。
用 `Select-String -Path 'G:\TOOL\LiteMonitor-fork\src\UI\HardwareTrendForm.cs' -Pattern '^using'` 确认后补齐。

- [ ] **Step 3: 曲线标签也归一化**

`GetMetricLabel(string key)` 的兜底是 `LanguageManager.T("Items." + key)`, 对 `"GPU.0.Temp"`
会得到字面量。把它改成:

```csharp
        private string GetMetricLabel(string key)
        {
            var item = _cfg.MonitorItems.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            string label = item != null ? MetricLabelResolver.ResolveLabel(item) : "";
            if (string.IsNullOrWhiteSpace(label)) label = LanguageManager.T("Items." + GpuKey.ToBaseKey(key));
            return string.IsNullOrWhiteSpace(label) ? key : label;
        }
```

> `GetUnit(string key)` 用的是 `MetricUtils.GetType(key)`, 对带序号 Key 天然正确 (走子串匹配), **不用改**。

- [ ] **Step 4: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 5: 提交**

```powershell
git add src/UI/HardwareTrendForm.cs
git commit -m "feat(gpu): 历史曲线窗口支持带序号的显卡 Key

记录侧已能采样 GPU.{序号}.{指标}, 展示侧同步补齐并剔除失去对应监控项的旧格式 Key。"
```

---

## Task 12: 硬件刷新层支持多卡


**Files:**
- Modify: `src/System/HardwareMonitor.cs`
- Modify: `src/System/HardwareServices/HardwareValueProvider.cs`（`OnUpdateTickStarted` 感知显卡数量变化）
- Modify: `src/UI/UIController.cs`（`Tick()` 里按实际卡数夹紧配置）

**Interfaces:**
- Consumes: `SensorMap.CachedGpus`（Task 6）、`Settings.MonitoredGpuCount`（Task 5）
- Produces: `HardwareMonitor.ShouldUpdateGpuHardware(IHardware hw)` 对所有被监控显卡返回 true

- [ ] **Step 1: 改造 ShouldUpdateGpuHardware**

在 `src/System/HardwareMonitor.cs` 中，把：

```csharp
        private bool ShouldUpdateGpuHardware(IHardware hw)
        {
            var activeGpu = _sensorMap.CachedGpu;
            if (activeGpu == null) return true;

            // 当前面板只有一组 GPU 指标，刷新非当前显卡只会增加启动和轮询耗时。
            return ReferenceEquals(hw, activeGpu);
        }

```

替换为：

```csharp
        private bool ShouldUpdateGpuHardware(IHardware hw)
        {
            var activeGpu = _sensorMap.CachedGpu;

            // ★★★ [多显卡] 映射还没建好时 (CachedGpu 与 CachedGpus 都为空),
            // 保持旧行为: 更新所有显卡。否则热插拔触发 SensorMap.Clear() 之后
            // 到下一次 Rebuild 之间, 显卡会完全不刷新。 ★★★
            if (activeGpu == null && _sensorMap.CachedGpus.Count == 0) return true;

            if (activeGpu != null && ReferenceEquals(hw, activeGpu)) return true;

            // ★★★ [多显卡] 更新所有被监控的显卡 ★★★
            foreach (var g in _sensorMap.CachedGpus)
            {
                if (ReferenceEquals(g, hw)) return true;
            }

            return false;
        }
```


- [ ] **Step 2: 在 HardwareValueProvider 感知显卡数量变化**

在 `src/System/HardwareServices/HardwareValueProvider.cs` 的字段区，把：

```csharp
        private string _lastPrefNet = "";
        private string _lastPrefGpu = "";
```

替换为：

```csharp
        private string _lastPrefNet = "";
        private string _lastPrefGpu = "";
        private int _lastGpuCount = 1;
```

在 `PreCacheAllSensors` 里，把：

```csharp
            _lastPrefNet = _cfg.PreferredNetwork;
            _lastPrefGpu = _cfg.PreferredGpu ?? "";
```

替换为：

```csharp
            _lastPrefNet = _cfg.PreferredNetwork;
            _lastPrefGpu = _cfg.PreferredGpu ?? "";
            _lastGpuCount = _cfg.MonitoredGpuCount;
```

在 `OnUpdateTickStarted` 里，把：

```csharp
                bool gpuChanged = _lastPrefGpu != (_cfg.PreferredGpu ?? "");
                if (gpuChanged)
                {
                    _sensorMap.Rebuild(_computer, _cfg);
                }

                // 自动检测配置变更：如果用户更改了首选风扇/磁盘/显卡，立即自动预热
                if (gpuChanged ||
                    _lastPrefCpuFan != _cfg.PreferredCpuFan ||
```

替换为：

```csharp
                bool gpuChanged = _lastPrefGpu != (_cfg.PreferredGpu ?? "");
                bool gpuCountChanged = _lastGpuCount != _cfg.MonitoredGpuCount;
                if (gpuChanged || gpuCountChanged)
                {
                    // ★★★ [多显卡] 显卡数量变了, 重建映射 ★★★
                    _sensorMap.Rebuild(_computer, _cfg);
                }

                // 自动检测配置变更：如果用户更改了首选风扇/磁盘/显卡，立即自动预热
                if (gpuChanged || gpuCountChanged ||
                    _lastPrefCpuFan != _cfg.PreferredCpuFan ||
```

- [ ] **Step 3: 新增实际显卡数查询 + 运行时夹紧**


> 场景: 配置里写了「监控 8 张」, 但这台机器只有 2 张卡 (或者用户拔掉了一张)。
> 启动时 `SettingsHelper.Load()` 拿不到实际卡数, 会按上限生成 8 组监控项,
> 其中 6 组永远没有数据 → 主界面多出 6 组空白指标。
> 这里在运行时用真实卡数夹紧, 并重建布局。

**3.1** 在 `src/System/HardwareMonitor.cs` 中, 紧跟 `ShouldUpdateGpuHardware` 方法之后插入:

```csharp
        /// <summary>
        /// 当前实际被监控的显卡数量 (以传感器映射为准)。
        /// 用于把配置里的"监控显卡数量"夹到真实卡数, 避免生成空白监控项。
        /// </summary>
        public int GetMonitoredGpuCount()
        {
            // ★ 无锁读 ★
            // CachedGpus 只会被整体替换 (Rebuild 的原子交换块 / Clear), 不会被就地修改,
            // 所以读 .Count 是安全的。
            // 刻意不用 lock(_lock): 这个方法会被 UI 线程 (UIController.Tick) 调用,
            // 而 ReloadComputerSafe 持锁可达数秒, 会把界面冻住。
            return _sensorMap.CachedGpus.Count;
        }

```

**3.2** 在 `src/UI/UIController.cs` 的 `Tick()` 方法中, 把:

```csharp
                await Task.Run(() => _mon.UpdateAll());
```

替换为:

```csharp
                await Task.Run(() => _mon.UpdateAll());

                // ★★★ [多显卡] 实际显卡数少于配置数时 (例如拔掉了第二张卡),
                // 夹紧配置并重建监控项, 否则会留下一组永远没有数据的空白指标。 ★★★
                int actualGpuCount = _mon.GetMonitoredGpuCount();
                if (actualGpuCount > 0 && _cfg.MonitoredGpuCount > actualGpuCount)
                {
                    GpuItemSync.Ensure(_cfg, availableGpuCount: actualGpuCount);
                    RebuildLayout();
                }
```

> `UIController.cs` 顶部已有 `using LiteMonitor.src.Core;`（`GpuKey` / `GpuItemSync` 同一命名空间）—— 确认即可。
> 注意: `Tick()` 里已有 `_busy` 防重入标志, `RebuildLayout()` 只是在 UI 线程重建布局, 不会重入。

- [ ] **Step 4: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 5: 提交**

```powershell
git add src/System/HardwareMonitor.cs src/System/HardwareServices/HardwareValueProvider.cs src/UI/UIController.cs
git commit -m "feat(gpu): 硬件刷新层支持多张显卡

被监控的所有显卡都会参与轮询; 显卡数量变化时自动重建传感器映射;
实际卡数少于配置数时夹紧配置, 避免生成空白监控项。"
```


---

## Task 12.5: code review 补的多卡适配（超温告警 + 网页端）

> **为什么需要这一步**: 前 13 个 Task 覆盖了"数据怎么按卡取", 但**没覆盖"按卡算完之后怎么展示"**。
> 独立 code review 实测发现两处**功能回退** —— 都不报错、都不崩, 只是多卡用户看不到该看的东西。

**Files:**
- Modify: `src/UI/UIController.cs`（`CheckTemperatureAlert` 逐卡检查）
- Modify: `src/System/WebServer/LiteWebServer.cs`（`primary` 标记用基础 Key 判定）

**Interfaces:**
- Consumes: `GpuKey.Build` / `GpuKey.ToBaseKey`（Task 2）、`GpuRegistry.GetShortName`（Task 2）
- Produces: 每张被监控显卡都能独立触发超温告警；网页端的 GPU 负载仍被标记为 `primary`

- [ ] **Step 1: 超温告警逐卡检查**

`src/UI/UIController.cs` 的 `CheckTemperatureAlert()` 里，把：

```csharp
            Check("CPU.Temp", _mon.Get("CPU.Temp"), globalThreshold, LanguageManager.T("Short.CPU.Temp"));
            Check("GPU.Temp", _mon.Get("GPU.Temp"), globalThreshold, LanguageManager.T("Short.GPU.Temp"));
            Check("MOBO.Temp", _mon.Get("MOBO.Temp"), globalThreshold, LanguageManager.T("Short.MOBO.Temp"));
```

替换为：

```csharp
            Check("CPU.Temp", _mon.Get("CPU.Temp"), globalThreshold, LanguageManager.T("Short.CPU.Temp"));

            // ★★★ [多显卡] 逐张显卡检查超温 ★★★
            //   ⚠️ 不能只查 "GPU.Temp": 迁移后 MonitorItems 里不再有它,
            //   而 SensorMap 只为 0 号卡写了这个旧格式 Key —— 那样等于**只告警第 0 张卡**,
            //   多卡用户最关心的"哪张卡过热"会完全失效。
            for (int gpuIdx = 0; gpuIdx < _cfg.MonitoredGpuCount; gpuIdx++)
            {
                string gpuTempKey = GpuKey.Build(gpuIdx, "Temp");
                string gpuShort = GpuRegistry.GetShortName(gpuIdx);
                string gpuAlertLabel = string.IsNullOrEmpty(gpuShort)
                    ? LanguageManager.T("Short.GPU.Temp")
                    : $"{gpuShort} {LanguageManager.T("Short.GPU.Temp")}";
                Check(gpuTempKey, _mon.Get(gpuTempKey), globalThreshold, gpuAlertLabel);
            }

            Check("MOBO.Temp", _mon.Get("MOBO.Temp"), globalThreshold, LanguageManager.T("Short.MOBO.Temp"));
```

> `_overheatStartTimes` 的 key 变成 `GPU.{序号}.Temp`, 每张卡**独立计时** 5 秒后弹窗。
> `_cfg.MonitoredGpuCount` 大于实际卡数时 `_mon.Get` 返回 null, `Check` 内部有 `val.HasValue` 保护, 不会误报。

- [ ] **Step 2: 网页端 primary 标记用基础 Key 判定**

`src/System/WebServer/LiteWebServer.cs` 里，把：

```csharp
                    else if (item.Key == "GPU.Core.Load" || item.Key == "GPU.Load") isPrimary = true;
```

替换为：

```csharp
                    // ★ [多显卡] 用基础 Key 归一化, 否则 "GPU.0.Load" / "GPU.1.Load" 匹配不上,
                    //   GPU 分组就再也拿不到负载状态 (分组环/状态色会失真)。
                    else if (item.Key == "GPU.Core.Load" || GpuKey.ToBaseKey(item.Key) == "GPU.Load") isPrimary = true;
```

- [ ] **Step 3: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 4: 提交**

```powershell
git add src/UI/UIController.cs src/System/WebServer/LiteWebServer.cs
git commit -m "fix(gpu): 超温告警与网页端状态支持多显卡

超温告警改为逐卡检查 (原来只查 GPU.Temp, 等于只告警第 0 张卡);
网页端 primary 标记改用基础 Key 判定, 带序号的 GPU Load 仍能命中。"
```

---

## Task 13: 设置页与语言包

**Files:**
- Modify: `src/UI/Settings/SystemHardwarPage.cs`
- Modify: `resources/lang/zh.json`、`en.json`、`zh-tw.json`、`ja.json`、`ko.json`、`ru.json`、`fr.json`、`de.json`、`es.json`

**Interfaces:**
- Consumes: `GpuItemSync.Ensure`（Task 4）、`Settings.MonitoredGpuCount`（Task 5）
- Produces: 设置页 → 系统与硬件 → 「监控显卡数量」下拉；语言条目 `Menu.GpuMonitorCount` / `Menu.GpuMonitorCountTip`

- [ ] **Step 1: 确认配置链路（已核实，无需改动）**

> ⚠️ **`SettingsPageBase.Config` 是 draft，不是 live**（这一点很容易搞反）：

```
MainForm._cfg  = Settings.Load()          // MainForm_Transparent.cs:16 —— live 单例
  -> UIController._cfg                     // UIController.cs:14
    -> new SettingsForm(_cfg, _ui, ...)    // MainFormBizHelper.cs:326
      -> _draftCfg = _cfg.DeepClone()      // SettingsForm.cs:56 —— JSON 深拷贝出的 draft
        -> page.SetContext(_draftCfg, ...) // SettingsForm.cs:160 —— 传的是 draft
          -> SettingsPageBase.Config      // SettingsBase.cs:34-37 —— 所以 Config == draft
```

> `SetContext` 全仓库**只有** `SettingsForm.cs:160` 这一个调用点, 传的是 `_draftCfg`,
> 所以页面里读写的 `Config` 一律是 draft。

因此 combo 的 setter 改 `Config.MonitoredGpuCount` 改的是 **draft** ——
这**正是**我们要的：保存时 `SettingsChanger.Merge(_cfg, _draftCfg)` 会把它同步回 live，
不需要任何额外的同步桥接代码。

> ⚠️ 正因为 `Config` 是 draft，**不要**在设置页里直接改 `Config.MonitorItems` ——
> 那样会绕过 draft/live 机制，且「监控项显示」页 (`MonitorPage`) 读的是 draft，
> 看不到新生成的显卡项。
> **设置页只写 `Config.MonitoredGpuCount`**, 监控项统一由 Task 5.5 在保存路径生成
> （Task 5.5 Step 2 在 `ApplyMonitorLayout` 里、Step 2.5 在 `SettingsForm` 落盘前各做一次）。
>
> ⚠️ 另外: 多卡模式 (数量 >= 2) 下 `PreferredGpu` 不再生效 ——
> `SelectMonitoredGpus` 会直接按优先级取前 N 张。此时「首选显卡」下拉仍然可以改, 但改了没有作用。
> 若要做得更严谨, 可以在数量 >= 2 时禁用该下拉 (`_cbGpu.Inner.Enabled = false` 并加灰化样式),
> 属于可选增强, 不阻塞本计划。


- [ ] **Step 1.5: 记录实际检测到的显卡数量**

「监控显卡数量」的下拉上限必须按**实际检测到的卡数**给, 不能给满 8 ——
否则 2 卡机器上选 8 会凭空多出 6 组永远没有数据的空白监控项。

在 `src/UI/Settings/SystemHardwarPage.cs` 的字段区，把：

```csharp
        private LiteComboBox _cbDisk, _cbNet, _cbGpu, _cbMobo;
        private LiteComboBox _cbFanCpu, _cbFanPump, _cbFanCase;
```

替换为：

```csharp
        private LiteComboBox _cbDisk, _cbNet, _cbGpu, _cbMobo;
        private LiteComboBox _cbFanCpu, _cbFanPump, _cbFanCase;

        // ★ 实际检测到的显卡数量, 决定「监控显卡数量」下拉的可选上限 ★
        private int _detectedGpuCount = 0;

        // ★「监控显卡数量」下拉的句柄: 构造时拿不到真实卡数, 拿到之后再收窄选项 ★
        //   (CreateSourceCard() 在构造函数里就会被调用, 编译器看不到这条赋值链, 故显式抑制)
        private LiteComboBox _cbGpuCount = null!;

        // ★ 程序化改动下拉选项时抑制回调 ★
        //   真正会触发 SelectedIndexChanged 的是后面的 `SelectedIndex = cur - 1` 赋值;
        //   实测 `Items.Clear()` 本身**不**触发该事件, 也不会把 cmb.Text 清空。
        //   抑制之后, 夹紧后的值由 RefreshGpuCountOptions 显式回写, 不走事件。
        private bool _suppressGpuCountEvent = false;

```

在 `PopulateAsyncData` 中 (注意方法名是 `PopulateAsyncData`, 由 `OnShow()` 调用, 不是 `LoadDataAsync`),
找到这一行:

```csharp
            var taskGpus  = Task.Run(() => HardwareMonitor.ListAllGpuOptions());
```

紧接着它下面的 `await Task.WhenAll(taskDisks, taskNets, taskGpus, taskFans, taskMobo);`
之后插入两行:

```csharp
                // ★ 实际检测到的显卡数量 (用于「监控显卡数量」下拉的上限) ★
                //   用局部变量承接: 直接写 `taskGpus.Result?.Count` 会把 Result 的可空状态
                //   污染到下面的 FillGpuSync(_cbGpu, taskGpus.Result, ...) 调用上 (CS8604)。
                var detectedGpus = taskGpus.Result;
                _detectedGpuCount = detectedGpus?.Count ?? 0;

                // ★ 真实卡数拿到了, 收窄「监控显卡数量」下拉的选项 ★
                // (构造时只能给满 1..8, 因为此刻硬件还没枚举完)
                RefreshGpuCountOptions();
```

再在 `SystemHardwarPage` 类里 (与 `PopulateAsyncData` 同级) 插入这个方法:

```csharp
        /// <summary>
        /// 按实际检测到的显卡数收窄「监控显卡数量」下拉的选项。
        /// CreateSourceCard() 是在构造函数里跑的 (InitializeUI), 那一刻硬件还没枚举完,
        /// _detectedGpuCount 恒为 0 —— 所以必须等 PopulateAsyncData() 之后再调一次。
        /// </summary>
        private void RefreshGpuCountOptions()
        {
            if (_cbGpuCount == null) return;

            // ★ 还没探测到显卡 (或探测失败) 时不要收窄选项, 更不要动用户的配置 ★
            if (_detectedGpuCount <= 0) return;

            int maxSelectable = Math.Max(1, Math.Min(GpuKey.MaxGpuCount, _detectedGpuCount));
            int cur = Math.Max(1, Math.Min(maxSelectable, Config?.MonitoredGpuCount ?? 1));

            try
            {
                // ★ 抑制事件回调: 下面改 **SelectedIndex** 会触发 SelectedIndexChanged,
                //   把中间态写进 draft。夹紧后的值由下面**显式**回写, 不走事件。 ★
                //   (实测: Items.Clear() 本身不触发该事件, 真正会触发的是 SelectedIndex 赋值)
                _suppressGpuCountEvent = true;

                _cbGpuCount.Items.Clear();
                for (int i = 1; i <= maxSelectable; i++) _cbGpuCount.Items.Add(i.ToString());
                _cbGpuCount.SelectedIndex = cur - 1;
            }
            finally
            {
                _suppressGpuCountEvent = false;
            }

            // ★★★ [关键] 把夹紧后的值显式写回 draft ★★★
            //   不写回会有个隐蔽的坑: 设置页**再次打开**时, SettingsPageBase.OnShow()
            //   会重跑 AddCombo 注册的刷新回调, 而它的逻辑是
            //     "当前值不在 Items 里 -> SelectedIndex = 0" ——
            //   于是超出实际卡数的值 (比如 3) 会被**静默改成 1**, 而不是我们期望的 2。
            //   先写回夹紧值, 下次打开时当前值就在 Items 里, 不会被改成 1。
            if (Config != null && Config.MonitoredGpuCount != cur)
            {
                Config.MonitoredGpuCount = cur;
            }
        }
```

> `LiteComboBox` 直接暴露了 `Items` / `SelectedIndex` / `Text`（`src/UI/Controls/LiteUI.cs:517-520`）,
> 不需要绕 `Inner`。
> 这里的收窄只是**显示层**的; 配置里超出实际卡数的值由 Task 5.5 的
> `GpuItemSync.Ensure(cfg, availableGpuCount)` 在保存时统一夹紧。


- [ ] **Step 2: 新增数量下拉**


在 `src/UI/Settings/SystemHardwarPage.cs` 的 `CreateSourceCard` 方法中，把：

```csharp
            _cbGpu = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.GpuSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredGpu ?? "",
                v => { if (Config != null) Config.PreferredGpu = v ?? ""; });
```

替换为：

```csharp
            _cbGpu = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.GpuSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredGpu ?? "",
                v => { if (Config != null) Config.PreferredGpu = v ?? ""; });

            // ★★★ [多显卡] 监控显卡数量下拉 ★★★
            // 1 = 只监控 1 张 (沿用旧行为), 2 及以上 = 按优先级监控前 N 张
            // ★ 选项上限按**实际检测到的显卡数**决定, 不能给满 8 ——
            //   否则 2 卡机器上选 8 会凭空多出 6 组永远没有数据的空白监控项。 ★
            // ★ 但 CreateSourceCard() 是在构造函数里跑的 (InitializeUI), 那一刻硬件还没枚举完,
            //   _detectedGpuCount 恒为 0 —— 所以这里先给满 1..8,
            //   等 PopulateAsyncData() 拿到真实卡数后再调 RefreshGpuCountOptions() 收窄。 ★
            var gpuCountOptions = Enumerable.Range(1, GpuKey.MaxGpuCount).Select(c => c.ToString()).ToList();
            _cbGpuCount = (LiteComboBox)group.AddCombo(this, "Menu.GpuMonitorCount", gpuCountOptions,
                () => (Config?.MonitoredGpuCount ?? 1).ToString(),
                v => {
                    if (Config == null) return;
                    // ★ 程序化刷新选项 (RefreshGpuCountOptions) 时会触发本回调, 此时忽略:
                    //   夹紧后的值由 RefreshGpuCountOptions 显式回写, 不走事件。 ★
                    if (_suppressGpuCountEvent) return;
                    // ★ 只写数量, 不在这里改 MonitorItems ★
                    // 监控项由 Task 5.5 (保存设置时) 统一生成, 避免绕过 draft/live 机制。
                    // 上限不在这里夹紧 —— 下拉的选项本身已按实际卡数收窄, 用户选不超;
                    // 真正的夹紧由 Task 5.5 的 GpuItemSync.Ensure(cfg, availableGpuCount) 在保存时完成。
                    Config.MonitoredGpuCount = Math.Max(1, MetricUtils.ParseInt(v));
                }
            );
            group.AddHint(LanguageManager.T("Menu.GpuMonitorCountTip"));


```

- [ ] **Step 3: 补齐 using**

`src/UI/Settings/SystemHardwarPage.cs` 顶部需要 `using LiteMonitor.src.Core;`（`GpuKey` / `GpuItemSync` / `MetricUtils`）。该文件已有 `using LiteMonitor.src.Core;` —— 确认即可。

- [ ] **Step 4: 添加语言条目**

在 `resources/lang/zh.json` 的 `Menu` 段中，在 `"GpuSource": "首选显卡",` 这一行后面插入：

```json
    "GpuMonitorCount": "监控显卡数量",
    "GpuMonitorCountTip": "⚠️多显卡模式下按性能优先级取前 N 张显卡，温度/占用/功耗等指标会分卡显示",
```

在 `resources/lang/en.json` 的 `Menu` 段中，在 `"GpuSource"` 对应行后面插入：

```json
    "GpuMonitorCount": "Monitored GPU count",
    "GpuMonitorCountTip": "⚠️In multi-GPU mode the top N GPUs by priority are monitored; temperature / load / power are shown per GPU",
```

其余语言包（`zh-tw.json` / `ja.json` / `ko.json` / `ru.json` / `fr.json` / `de.json` / `es.json`）在各自 `Menu` 段的 `"GpuSource"` 对应行后插入：

`zh-tw.json`:
```json
    "GpuMonitorCount": "監控顯示卡數量",
    "GpuMonitorCountTip": "⚠️多顯示卡模式下按效能優先級取前 N 張顯示卡，溫度/佔用/功耗等指標會分卡顯示",
```

`ja.json`:
```json
    "GpuMonitorCount": "監視する GPU の数",
    "GpuMonitorCountTip": "⚠️複数 GPU モードでは優先度の上位 N 枚を監視し、温度/使用率/消費電力は GPU ごとに表示します",
```

`ko.json`:
```json
    "GpuMonitorCount": "모니터링할 GPU 수",
    "GpuMonitorCountTip": "⚠️다중 GPU 모드에서는 우선순위 상위 N개를 모니터링하며 온도/사용률/전력은 GPU별로 표시됩니다",
```

`ru.json`:
```json
    "GpuMonitorCount": "Количество отслеживаемых GPU",
    "GpuMonitorCountTip": "⚠️В режиме нескольких GPU отслеживаются N видеокарт с наивысшим приоритетом; температура/загрузка/мощность показываются по каждой карте",
```

`fr.json`:
```json
    "GpuMonitorCount": "Nombre de GPU surveillés",
    "GpuMonitorCountTip": "⚠️En mode multi-GPU, les N GPU les plus prioritaires sont surveillées ; température / charge / puissance sont affichées par GPU",
```

`de.json`:
```json
    "GpuMonitorCount": "Anzahl überwachter GPUs",
    "GpuMonitorCountTip": "⚠️Im Multi-GPU-Modus werden die N GPUs mit höchster Priorität überwacht; Temperatur / Auslastung / Leistung werden pro GPU angezeigt",
```

`es.json`:
```json
    "GpuMonitorCount": "Número de GPU monitorizadas",
    "GpuMonitorCountTip": "⚠️En modo multi-GPU se monitorizan las N GPU con mayor prioridad; temperatura / carga / potencia se muestran por GPU",
```

> 注意：JSON 里的 `⚠️` 是合法字符，不需要转义。插入后必须用 JSON 解析器校验每个文件合法。

- [ ] **Step 5: 校验所有语言包 JSON 合法**

```powershell
Get-ChildItem 'G:\TOOL\LiteMonitor-fork\resources\lang\*.json' | ForEach-Object {
  try { Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null; "OK  $($_.Name)" }
  catch { "FAIL $($_.Name): $($_.Exception.Message)" }
}
```

Expected: 9 个文件全部 `OK`

- [ ] **Step 6: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 7: 提交**

```powershell
git add src/UI/Settings/SystemHardwarPage.cs resources/lang/
git commit -m "feat(gpu): 设置页新增监控显卡数量选项

按 1~8 张选择被监控的显卡数量, 切换后立即同步监控项; 补齐 9 种语言文案。"
```

---

## Task 14: 真机双卡验证

**Files:**
- 无源码改动；若发现问题，回到对应 Task 修复

**Interfaces:**
- Consumes: 全部前置 Task 的产出
- Produces: 一份可运行的 Release 构建 + 真机验证结论

- [ ] **Step 1: 发布构建到独立目录（不动现有安装）**

```powershell
$dotnet publish 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -r win-x64 --self-contained false `
  -o 'G:\TOOL\LiteMonitor-multi' -v minimal
```

Expected: 生成 `G:\TOOL\LiteMonitor-multi\LiteMonitor.exe` 与 `resources/` 目录

> **不要**覆盖 `G:\TOOL\LiteMonitor`（你正在使用的版本）。新目录的 `resources\plugins` 等由 publish 复制；`settings.json` 不存在时会自动生成默认配置。

- [ ] **Step 2: 启动并确认单卡默认行为不变**

```powershell
Start-Process 'G:\TOOL\LiteMonitor-multi\LiteMonitor.exe' -Verb RunAs
```

打开「设置 → 系统与硬件」，确认「监控显卡数量」默认为 `1`；关闭设置，观察主界面 GPU 区域与改造前一致（只显示一组 GPU 指标）。

- [ ] **Step 3: 切到 2 张并核对数据源**

在设置里把「监控显卡数量」改为 `2`，点保存。预期主界面 GPU 分组下出现两组指标，标签形如 `3080 Ti 温度` / `3080 温度`。

用 `nvidia-smi --query-gpu=index,name,temperature.gpu,power.draw,fan.speed --format=csv` 取真值对照：

| 期望 | 依据 |
|---|---|
| 第一组显示 3080 Ti 的温度/功耗 | LHM 枚举顺序为 Ti(priority 0) → 3080(priority 0)，`OrderBy` 稳定排序保序 |
| 第二组显示 3080 的温度/功耗 | 同上 |
| 两组数值与 nvidia-smi 对应行一致 | — |

- [ ] **Step 4: 验证 GPU.Fan 不再串卡**

在设置 → 监控项显示里把 `GPU.1.Fan`（第二张卡的风扇）设为可见，保存。
本机参考值：RTX 3080 风扇 0%（待机副卡），RTX 3080 Ti 风扇 55%。
预期：第一张卡的风扇显示约 55%，第二张卡显示约 0%。**改造前两张卡会都显示 3080 的 0%**（旧代码无条件覆盖导致）。

- [ ] **Step 5: 验证任务栏与历史曲线**

- 任务栏：把 `GPU.0.Temp` / `GPU.1.Temp` 设为任务栏可见，确认分别显示 `3080 Ti` / `3080` 前缀，数值与主界面一致。
- 历史曲线：等 1 分钟（采样间隔 10 秒），打开「监控历史」，确认 `GPU.0.Temp` 与 `GPU.1.Temp` 是两条独立曲线。
  > 依赖 Task 11.5: 展示侧 `GetCurrentKeys()` 必须能列出带序号的 Key, 否则这里看不到曲线。

- [ ] **Step 5.5: 验证数量夹紧（空白监控项）**

把「监控显卡数量」设成一个**大于实际卡数**的值（例如本机只有 2 张卡时选 8），
保存后确认：

1. 设置页的「监控显卡数量」**自动回落到 2**（`Ensure` 把夹紧结果回写到了 `MonitoredGpuCount`）。
2. 主界面 GPU 分组下**只有 2 组**指标，没有 6 组空白项。
3. `settings.json` 里 `MonitoredGpuCount` 为 `2`，`MonitorItems` 里不存在 `GPU.2.*` 之类的项。

> 若下拉本身就不给超过实际卡数的选项（Task 13 已按 `_detectedGpuCount` 限制），
> 这一步可以通过直接手改 `settings.json` 再重启来验证运行时夹紧路径（Task 12.3）。


- [ ] **Step 6: 验证网页版（可选）**

设置 → 主界面设置 → 开启「网页版显示」，浏览器访问 `http://127.0.0.1:5000`，确认 JSON 快照里同时出现 `GPU.0.*` 与 `GPU.1.*` 两组键。

- [ ] **Step 7: 回退到 1 张并确认配置正确裁剪**

把「监控显卡数量」改回 `1`，保存。检查 `G:\TOOL\LiteMonitor-multi\settings.json`，确认 `MonitorItems` 中不再有 `GPU.1.*` 的项。

- [ ] **Step 8: 回归确认旧安装不受影响**

```powershell
Get-Process -Name 'LiteMonitor' -ErrorAction SilentlyContinue |
  Select-Object Id, Path, StartTime | Format-Table -AutoSize
```

Expected: 原有 `G:\TOOL\LiteMonitor\LiteMonitor.exe` 进程仍在运行且配置未变。

- [ ] **Step 9: 提交最终验证记录**

在 `G:\TOOL\LiteMonitor-fork\docs\superpowers\plans\` 下新建 `2026-10-20-gpu-multi-card-verification.md`，写入 Step 2~8 的实际观察值（尤其是 nvidia-smi 对照表与风扇读数），然后：

```powershell
git add docs/
git commit -m "docs(gpu): 记录多显卡改造的真机验证结果"
```

- [ ] **Step 10: 推送分支**

```powershell
git push -u origin feat/multi-gpu
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

功能开关式回退：把设置里的「监控显卡数量」改回 `1`，程序即回到单卡行为（旧格式 Key 仍然被映射，第 0 张卡额外写入 `GPU.*` 旧键）。

---

## 附录 A: 改造前后 Key 对照

| 场景 | 改造前 | 改造后 |
|---|---|---|
| 单卡默认 | `GPU.Load` | `GPU.0.Load`（同时保留 `GPU.Load` 旧键映射） |
| 双卡 | 不支持 | `GPU.0.Load` + `GPU.1.Load` |
| 三卡 | 不支持 | `GPU.0.*` + `GPU.1.*` + `GPU.2.*` |

## 附录 B: 已知不做的事（YAGNI）

- 不做「全部显卡」自动模式：下拉是固定的 1~8 数字（上限会按实际检测到的卡数收窄，见 Task 13），
  不需要额外的「自动 / 全部」选项。
- 不做插件市场的多卡支持：插件 Key 走 `DASH.*` 命名，与 `GPU.*` 无关。
- 不改 `resources/plugins/` 下 13 个内置插件 JSON。
- 不引入插件远程安装能力（另一条独立计划线）。
- 不做每张卡的独立阈值：温度/占用/功耗阈值继续全局共享，峰值按卡独立自适应即可覆盖绝大多数场景。

### B.1 使用层面的已知限制（code review 阶段补充）

- **同型号双卡的简称相同**：两张完全一样的卡（如两张 3080）简称都是 `3080`，面板标签会显示成
  `3080 温度` / `3080 温度` 而无法区分。简称只取自显卡全名，不带序号。
  用户可在「监控项显示」里给第二张卡的指标单独改 `UserLabel` 来区分。
- **单卡用户自定义的显卡指标名会被清除**：迁移时 `GpuItemSync.Ensure` **故意不继承**旧格式项的
  `UserLabel`（两张卡同名没意义），但这对只有一张卡的用户来说同样是丢失。
  可见性 / 排序 / 单位 / 任务栏标签都是继承的，只有「主界面显示名」这一项会回退成自动生成的
  `显卡简称 + 指标`。
- **多卡模式下「首选显卡」不生效**：`SelectMonitoredGpus` 在数量 >= 2 时按优先级取前 N 张，
  忽略 `PreferredGpu`。下拉框仍可改，但没有实际作用（切回 1 张时恢复生效）。
- **同优先级显卡的序号可能跨重启漂移**：两张同型号卡的 `GetHwPriority` 相同，LINQ `OrderBy` 是稳定排序，
  顺序 = LHM 的 `computer.Hardware` 枚举顺序 —— 驱动重载 / 重启后这个顺序**可能变化**，
  于是「GPU.0」未必还是上次那张物理卡。由于每卡峰值 (`RecordedMaxGpuDynamic`) 与历史曲线
  都按序号存储，漂移后会张冠李戴。彻底解决需要用硬件 Identifier 而非序号做持久化标识，
  属于本计划范围之外。

## 附录 C: 旧 "GPU.*" 历史曲线的处理

- 迁移后 `MonitorItems` 里不再有 `"GPU.Temp"` 这类旧 Key, `GetEnabledSupportedKeys` 也就不会收录它们,
  于是升级前积累的 `GPU.Temp` 曲线会**停在升级前的最后一个点**(数据仍在 `HardwareHistory.json` 里, 没丢)。
- Task 11.5 的 `GetCurrentKeys()` 已经把这类"没有对应监控项的旧格式 Key"从列表里剔除,
  所以监控历史窗口**不会再显示那条僵死的旧曲线** —— 表现是旧曲线消失, 而不是卡在原地不动。
- 旧数据本身不主动清理, 留在 `HardwareHistory.json` 里也不影响使用 (文件有 7 天保留期, 会自然滚动掉)。

