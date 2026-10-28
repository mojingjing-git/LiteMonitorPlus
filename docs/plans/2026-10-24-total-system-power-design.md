# 总系统功耗（SYS.Power）派生指标设计说明

- 日期：2026-10-24
- 分支建议：task/total-power
- 状态：已实现（必需档 A0–A7 + 推荐 B3 落地，dotnet build 0 错误 0 警告；B1/B2/C1/C2 未做）
- 适用范围：新增"整机/总功耗"监控项，汇总 CPU 与各被监控显卡的实时功耗
- 关联调研：已确认 CPU.Power / GPU.i.Power / BAT.Power 均已具备，聚合为派生指标无需任何新硬件接口
- 审核修订记录：基于独立逐行源码核对结果修订（当时的评审记录未单独入库）

---

## 1. 背景与根因（已逐行复核）

### 1.1 现有功耗数据源（已具备）

| 数据源 | 内部 Key | 采集位置 | 当前是否已被监控 |
|--------|----------|----------|------------------|
| CPU 封装功耗 | CPU.Power | SensorMatcher 把 Power 类且名含 package/cores 映射成此键；ComponentProcessor.GetCompositeValue 取值并带 >600W 熔断（ComponentProcessor.cs:148） | 已作为监控项（默认隐藏，SettingsHelper.cs:602） |
| GPU 功耗（按卡） | GPU.0.Power / GPU.1.Power … | ComponentProcessor 遍历该卡 SensorType.Power，匹配 package/ppt/board/core/gpu，带 >1200W 熔断（ComponentProcessor.cs:186） | 多显卡分别有 Key |
| 电池功耗 | BAT.Power | SensorMatcher 直接映射（充放电符号由 BatteryService 修正） | 仅笔记本 |
| 主板/平台功耗 | — | 当前 SensorMatcher 无任何主板 Power 分支 | 未接 |

关键流水证据：

- src/System/HardwareServices/HardwareValueProvider.cs:859 在 key.Contains("Power") 分支统一走 ComponentProcessor.GetCompositeValue(key, _manualSensorCache) 取复合功耗值——这是 SYS.Power 应插入的同一入口。
- 峰值记录 RecordedMaxCpuPower / RecordedMaxGpuPower 已在 src/Core/Settings.cs:156/158（MetricUtils.GetAdaptivePercentage 在 :649/:651 用其做进度条自适应百分比）。
- 单位/类型自动识别：MetricUtils 中 key.IndexOf("POWER") → MetricType.Power → 单位 "W"；阈值、三色报警、进度条对 Power 类型均有现成处理（MetricUtils.cs:79/88/547/606）。

### 1.2 为什么总功耗"准确"是估计值（准确度上限认知）

软件走 LibreHardwareMonitorLib 传感器读数，无墙插/电源直读能力（个别 USB 上报 PSU 除外，不在本项目可达范围）。
因此"总功耗"本质是各部件功耗之和的估计值，天然缺：

- 主板 VRM / PCH / 供电损耗
- 内存条自身功耗
- 风扇 / 水泵 / RGB
- 各转换环节能效损耗

→ 软件侧能算到的最准 = "CPU + 各 GPU +（可选）主板"的组件和，必然低估真实墙插功耗。
要让它逼近真实，唯一办法是接 PSU 直读（硬件层，超出本项目范围）。

### 1.3 重复计算风险（最易算错的点）

- 核显（iGPU）：Intel 核显功耗在 LHM 里是挂在 Intel GPU 硬件下的独立传感器，通常不在 CPU Package 内 → 求和时应包含（正确）。AMD APU 的 Package 往往已含整颗 die（含核显）→ 若再单独加核显会重复。**但**：仓库当前没有任何可靠的 iGPU/APU 判据（详见 §3.2 B2 的修订说明），因此 APU 去重暂不可靠实现。
- CPU "Cores" vs "Package"：SensorMatcher.cs:61 是单条 OR `Has(name,"package") || Has(name,"cores")`，SensorMap 缓存写入是 first-wins（:290 `if (!newMap.ContainsKey(finalKey))`），OR 的书写顺序不决定谁被缓存——取决于 LHM 枚举顺序。要强制 package 优先需两遍扫描，不是短路。

---

## 2. 设计目标

1. 新增派生监控项 SYS.Power（标签"总功耗 / Total Power"），归 HOST 分组（与 MEM.Load、FPS 同组）。
   - **关键**：HOST 归组是显式枚举（Settings.cs:795 与 :866-871），SYS.Power 不在列表里会落到一个独立的 "SYS" 组。必须显式补进归组条件（见 A0）。这正是 MEM.Commit.Load 当年被显式补进的原因（Settings.cs:793-794 注释）。
2. 求和口径：SYS.Power = CPU.Power + Σ(GPU.i.Power)，仅累加非 null 的有效读数；全部分量无效时返回 null（UI 显示"—"，与 CPU.Power 一致）。
3. 每个分量沿用其原钳制（CPU.Power 的 >600W 熔断在 ComponentProcessor.cs:148 保持；GPU 的 >1200W 熔断在 :186 保持）；SYS.Power 自身设一个独立的更高 sanity ceiling（2000W）仅过滤传感器异常，不约束正常量程。
4. 同代次时间对齐：所有分量在同一采集代次快照内读取（复用 HardwareValueProvider 的 _cache / OnCollectionSettled 机制），避免跨代次混加产生抖动。GetValue 内部有 _cache.TryGetValue（:612），递归调用同代次安全。
5. 单位/阈值/三色报警/横竖屏/任务栏/WebServer 全部自动适配（落到 Power 类型即可，MetricUtils.cs:79/88）。
6. **峰值自记**：SYS.Power 取值后必须显式调用 _cfg.UpdateMaxRecord("SYS.Power", val)（与 CPU/GPU Power 在 ComponentProcessor.cs:149/187 的做法一致），否则 RecordedMaxSysPower 永远停在初始值，自适应进度条失效。

---

## 3. 实现方案（必需 + 推荐档）

### 3.1 必需项（A0–A7）

| # | 文件 | 改动 |
|---|------|------|
| **A0** | src/Core/Settings.cs | **[审核新增·H1 修复]** 在 :795 与 :866-871 两处 HOST 归组条件都追加 `\|\| _key == "SYS.Power"`（与 MEM.Commit.Load 同范式）。不加此步，SYS.Power 会落到独立的 "SYS" 组，§2.1 目标不成立。 |
| A1 | src/System/HardwareServices/HardwareValueProvider.cs | 在 switch(key)（约 line 630）新增 case "SYS.Power":，内部递归调 GetValue 取 CPU.Power 与各 GPU.i.Power 累加；任一分量为 null 则跳过（非 0）；**取到结果后显式调 _cfg.UpdateMaxRecord("SYS.Power", result.Value)**（见 §4.1 修订伪代码） |
| A2 | src/Core/SettingsHelper.cs | InitDefaultItems()（约 line 587）HOST 段新增 new MonitorItemConfig { Key = "SYS.Power", SortIndex = 407, VisibleInPanel = false }（SortIndex=407 紧邻 CASE.Fan=406，真正落在 HOST 段内；VisibleInPanel=false 与 CPU/GPU Power 默认隐藏一致，见 §7） |
| A3 | src/Core/MetricUtils.cs | GetAdaptivePercentage（:640）分支新增 else if (baseKey == "SYS.Power") max = cfg.RecordedMaxSysPower; |
| A4 | src/Core/Settings.cs | 新增 public float RecordedMaxSysPower { get; set; } = 800.0f;（与 RecordedMaxCpuPower 同区，约 :156） |
| A5 | src/Core/SettingsHelper.cs（UpdateMaxRecord，:938） | **[审核修订·H2(b) 修复]** (1) 放宽 :944 的既有 Power guard：`if (key.Contains("Power") && !key.StartsWith("SYS.") && val > 1000) return;` 并新增 `if (key.StartsWith("SYS.") && val > 2000) return;`（与 B3 口径对齐）；(2) 在 baseKey 分支（:950 后）新增 else if (baseKey == "SYS.Power" && val > settings.RecordedMaxSysPower) { settings.RecordedMaxSysPower = val; changed = true; } |
| A6 | resources/lang/{zh,zh-tw,en,ja,ko,ru,de,es,fr}.json | 各在 "Items" 对象内新增 "SYS.Power": "<本地化>"（zh=总功耗，zh-tw=總功耗，en=Total Power，ja=総消費電力，ko=총 소비 전력，ru=Общая мощность，de=Gesamtverbrauch，es=Potencia total，fr=Puissance totale）。注意：键名不带 "Items." 前缀，前缀是查找串（MetricLabelResolver.cs:39 用 "Items."+Key）；同时在各文件 "Short" 对象内加 "SYS.Power": "SPWR"（短标签，参考 CPU.Power="CPWR"、GPU.Power="GPWR" 的 [类首字母]+PWR 模式） |
| **A7** | src/UI/Settings/SystemHardwarPage.cs | **[审核新增·M3 修复]** 在 :568-575 校准段新增 AddCalib("Items.SYS.Power", "W", () => Config?.RecordedMaxSysPower ?? 800, v => { if(Config!=null) Config.RecordedMaxSysPower = v; });，让用户可手动校准 SYS.Power 进度条上限（与 CPU/GPU Power 同范式） |

### 3.2 推荐项（提升准确度）

| # | 文件 | 改动 | 说明 |
|---|------|------|------|
| B1 | src/System/HardwareServices/SensorMatcher.cs | CPU Power 匹配强制优先 package（避免命中 cores 导致偏小）| **[审核修订·M2]** 不能靠 OR 短路（SensorMap 是 first-wins，OR 顺序无效）。需改为两遍扫描：第一遍只收名含 "package" 的 Power 传感器；第二遍仅当第一遍无果时再收 "cores"。或保持单遍但在 SensorMap 写入处加优先级比较（package 优于 cores）。 |
| B2 | （暂缓） | iGPU/APU 重复计算规避 | **[审核修订·H3]** 仓库当前无可靠 APU 判据：HardwareType.GpuAmd 同时覆盖独显与 APU 核显；无 IsIntegrated/IsApU 字段；HardwareRules.cs:87 的 iGPU 判定只覆盖 Intel 不覆盖 AMD。在补出可靠判据前**不要做 B2**，否则会错跳独立 Radeon 的功耗（台式机 SYS.Power 退化为仅 CPU）。可靠判据三选一：(1) 名字规则（APU 核显常叫 "Radeon Graphics"/"Vega"，独显叫 "Radeon RX …"）在 GpuRegistry 加 IsIntegrated；(2) 显存类型（独显 Dedicated，核显 Shared，扩展 HardwareRules.ShouldUseSharedMemory 到 AMD）；(3) 不做去重，改在 UI/文档提示"AMD APU 平台 SYS.Power 可能含核显重复，以 CPU.Power 为准"。 |
| B3 | src/System/HardwareServices/HardwareValueProvider.cs | SYS.Power 独立 sanity ceiling：if (sum > 2000f) return null;（仅防传感器异常，不约束 700W 级正常台式机）| 数值合理。**注意**：必须同时按 A5 放宽 UpdateMaxRecord 路径的 1000W guard，否则 >1000W 的合法总功耗峰值永远记不进 RecordedMaxSysPower（两条路径口径不一致）。 |
| B4 | 复用现有 _cache / OnCollectionSettled | 确保 SYS.Power 在当前代次内完成所有分量读取（无新增代码，仅调用方式对齐）| GetValue 内部 _cache.TryGetValue（:612）保证递归调用同代次安全 |

### 3.3 可选项（进阶，覆盖不确定）

| # | 文件 | 改动 | 风险 |
|---|------|------|------|
| C1 | SensorMatcher.cs | 新增主板功耗映射：HardwareType.Motherboard/SuperIO 下 Power 类、名含 powersupply/system/host/platform → MB.Power，并入总和 | 仅少数主板有，覆盖率低；best-effort 有则加无则忽略 |
| C2 | BatteryService.cs / ComponentProcessor.cs | 笔记本放电态（AcOnline==false 且 BAT.Power>0）时 SYS.Power 直接采用 BAT.Power 作为整机代理，不求和（天然含主板/内存/损耗，更准）| 需判定电源状态，逻辑分支清晰；对笔记本体验提升明显，建议一并 |

---

## 4. 关键实现要点（伪代码锚点）

### 4.1 SYS.Power 取值（A1 + B3 + H2 修复）

```csharp
case "SYS.Power":
{
    float? cpu = GetValue("CPU.Power");          // 走既有路径，含 >600W 熔断（ComponentProcessor.cs:148）
    float sum = 0f;
    bool any = cpu.HasValue;
    if (cpu.HasValue) sum += cpu.Value;

    for (int i = 0; i < _cfg.MonitoredGpuCount; i++)
    {
        // B2 暂缓：不做 APU 核显去重（无可靠判据，见 §3.2）
        float? gpu = GetValue(GpuKey.Build(i, "Power"));  // 走既有 GPU 路径，含 >1200W 熔断（ComponentProcessor.cs:186）
        if (gpu.HasValue) { sum += gpu.Value; any = true; }
    }

    result = any ? (float?)sum : null;

    // B3 sanity：仅过滤传感器异常，不约束正常量程
    if (result.HasValue && result.Value > 2000f) result = null;

    // ★ [审核新增·H2(a) 修复] 必须显式记峰值，否则 RecordedMaxSysPower 永远是初值
    //   对照 ComponentProcessor.cs:149 (_cfg.UpdateMaxRecord for CPU.Power) / :187 (GPU.Power)
    if (result.HasValue) _cfg.UpdateMaxRecord("SYS.Power", result.Value);

    break;
}
```

说明：
- GetValue 为 HardwareValueProvider 内部的既有取值入口（:589），与 switch 共用代次缓存（_cache.TryGetValue 在 :612）。
- 递归调用 GetValue 在同一线程内安全：_lock 是 Monitor（可重入），各 key 独立缓存，无冲突。
- **UpdateMaxRecord 在 GetValue 锁内的先例**：ComponentProcessor.GetCompositeValue（在 HardwareValueProvider.cs:868 被锁内调用）已在其 :149/:187 调 _cfg.UpdateMaxRecord；ReadFanValue（:1053）同样在锁内调。SYS.Power 在 case 内调 UpdateMaxRecord 与这两处同范式，非新增重入风险。
- 遍历 0..MonitoredGpuCount-1 直接对齐现有多显卡处理逻辑（SensorMap.CachedGpus + GpuKey.Build），零新概念。
- MonitoredGpuCount 默认 1（Settings.cs:289）。

### 4.2 注册监控项（A2）

```csharp
// [4xx] HOST 段内，紧邻 CASE.Fan（SortIndex=406）
new MonitorItemConfig { Key = "SYS.Power", SortIndex = 407, VisibleInPanel = false },
```

注意：VisibleInPanel = false（与 §7"默认隐藏"一致，与 CPU.Power/GPU.Power 的默认隐藏一致）。SortIndex = 407 真正落在 HOST 段（401-407）内，不是 660（660 会落在 DISK IO 与 NET 之间，不在 HOST 段）。

### 4.3 HOST 归组（A0）

```csharp
// Settings.cs:795（CachedUIGroup 缓存分支）
if (_key == "MEM.Load" || _key == "MEM.Commit.Load" || _key == "MOBO.Temp"
    || _key == "DISK.Temp" || _key == "CASE.Fan" || _key == "FPS"
    || _key == "SYS.Power")                          // ★ 新增
    CachedUIGroup = "HOST";

// Settings.cs:866-871（UIGroup fallback 分支，必须同步）
if (Key == "MEM.Load" || Key == "MEM.Commit.Load" || Key == "MOBO.Temp"
    || Key == "DISK.Temp" || Key == "CASE.Fan" || Key == "FPS"
    || Key == "SYS.Power")                           // ★ 新增
    return "HOST";
```

### 4.4 UpdateMaxRecord guard 修订（A5）

```csharp
// SettingsHelper.cs:943-944 修订前：
//   if (key.Contains("Clock") && val > 10000) return;
//   if (key.Contains("Power") && val > 1000) return;          // ← 会误杀 SYS.Power 的合法 >1000W

// 修订后：
if (key.Contains("Clock") && val > 10000) return;
if (key.Contains("Power") && !key.StartsWith("SYS.") && val > 1000) return;  // CPU/GPU Power 不变
if (key.StartsWith("SYS.") && val > 2000) return;                            // SYS.Power 放宽到 2000W（与 B3 对齐）
```

### 4.5 语言包（A6）

```json
// zh.json — "Items" 对象内（键名不带前缀）：
"SYS.Power": "总功耗"

// zh.json — "Short" 对象内：
"SYS.Power": "SPWR"
```

### 4.6 校准入口（A7）

```csharp
// SystemHardwarPage.cs :568-575 段，紧邻 GPU.Power 之后新增：
AddCalib("Items.SYS.Power", "W",
    () => Config?.RecordedMaxSysPower ?? 800,
    v => { if (Config != null) Config.RecordedMaxSysPower = v; });
```

---

## 5. 兼容性 / 回归风险

- 无破坏：新增 Key 不影响 CPU.Power / GPU.Power / BAT.Power 任何既有逻辑；MetricUtils、MetricItem、三色报警、横/竖屏、WebServer 全部按 Power 类型自动适配。
- 迁移安全：InitDefaultItems 仅追加一项；既有用户配置通过 SettingsHelper 的"缺失项补全"逻辑自动获得，不会丢既有设置。
- 多显卡一致：遍历 MonitoredGpuCount 与现有 GpuItemSync.Ensure / AppActions 的显卡项生成逻辑同源，不会越界或串卡。
- HOST 归组：A0 是无破坏的白名单追加，不影响其他 Key 的归组。

---

## 6. 验收 / 验证

1. 单元测试（tests/LiteMonitor.Tests）：从零新增 4 个用例（注：仓库无现成 Power 单测范式可照抄，probe 基线仅在 baseline/probe-results.json 有记录）：
   - 正常求和：CPU(150W) + 2 张 GPU(300W/250W) → 700W；
   - 某卡为 null → 仅累加有效分量（CPU + 1 张 GPU）；
   - 全 null → 返回 null；
   - >2000W 异常 → 返回 null（B3）；
   - **峰值自记**：连续上报 700W 后 RecordedMaxSysPower 应从 800 升到 ... 不，应从初值被刷新到 700（UpdateMaxRecord 只记更大的，初值 800 > 700 不触发，需用 >800 的用例验证，如 CPU+GPU=950W）。
2. UI 验证：启动后设置页 → HOST 分组应出现"总功耗"（不是独立的"SYS"分组），默认隐藏，勾选后面板/任务栏正确显示 W 单位与三色报警。
3. 校准验证：设置页"最大值校准"段应出现"总功耗 (最大值)"输入框（A7）。
4. Web 验证：网页版 LiteWebServer 经 MetricLabelResolver 同路径显示（LiteWebServer.cs:514 已用同一解析器）。
5. 探针：如环境允许，用 tools/ 下既有探针或新建最小探针做真机读数比对（与 docs/reviews/evidence/ 既有证据同范式）。

---

## 7. 触发条件 / 待主人拍板

- 是否采用 必需(A0–A7) 档（覆盖绝大多数台式机，修正了归组 + 峰值自记 + guard 口径）？**→ 建议采用**
- 是否追加 推荐 B1（package 优先，两遍扫描实现）+ B3（2000W sanity）？**→ 建议采用**（B3 必须与 A5 同步，否则口径不一致）
- B2（APU 核显去重）：**暂缓**，待补出可靠判据后再做。不做 B2 时 AMD APU 平台 SYS.Power 可能含核显重复，需在文档/README 提示。
- 是否追加 可选 C1（主板功耗）/ C2（笔记本电池代理）？**→ C2 建议一并（笔记本体验提升明显）；C1 覆盖低可暂缓**
- SYS.Power 默认隐藏（VisibleInPanel=false），由用户开启——与 CPU/GPU Power 默认隐藏一致。
