# B0b 验收记录 —— 行为 fixture 采集 + 性能基线

## 1. 标识

- 工作包：P-B0b（B0 收尾：行为 fixture 采集 + 性能基线）
- 分支：`pkg/B0-fixture-perf`（独立工作树 `G:\TOOL\LiteMonitor-fork\.worktrees\b0-tail`）
- 基线 SHA（开工时 master，即 fixture 期望值的固化基点）：`67afb8a`
- 执行日期：2026-10-26
- 禁改遵守：`src/` 零改动；`scripts/architecture-assert.ps1`、`architecture-baseline.json`、CI 阈值均未触碰
- 改动清单（全部为新建文件，无既有文件修改）：
  - `tests/LiteMonitor.Tests/MetricFormatBehaviorFixtureTests.cs`（行为 fixture，181 用例）
  - `scripts/perf-baseline.ps1`（性能基线采集脚本）
  - `docs/verification/baseline/perf-baseline-*.md` + `.json` + `perf-probe-*/`（实测基线入库）

## 2. 行为 fixture（Plan B §6.1 第一行：数值格式和单位）

### 2.1 方法：抄题不出题

所有期望值均为 2026-10-26 对基点 `67afb8a` **实跑采集**后逐字固化：先用临时采集
测试（已删除，不入库）把全部计划输入跑一遍并落盘实际输出，再转写为
`[Theory]/[InlineData]` 固定用例；正式用例首跑即 181/181 全绿，证明期望值来自
实跑而非手写。四类 × 数值域成矩阵，断言逐字比较（含空串与空格）。

### 2.2 用例矩阵

| 类别（Plan B §6.1 第一行） | 覆盖 API | 用例数 | 说明 |
|---|---|---|---|
| 正常/紧凑格式 | `GetValueStr(key,value,compact,userFormat)` | 33+5 | 全类型表（负载/温度/频率/功耗/电压/电流/转速/FPS/网速/数据量），compact 真/假成对固化 |
| 阈值边界 | `GetState(key,value)` | 50 | `GetThresholds` 全部 7 组阈值的 warn/crit 两侧；自适应类（频率/功耗/转速/FPS/电压/电流）80%/90% 归一化边界；电池反向阈值（≤crit 严重）；NaN；电池负值 |
| 用户单位/自定义格式 | `GetDisplayUnit(key,unit,fmt)` + 内存 `userFormat` | 10+10 | null=默认 / 空串=隐藏 / `{u}` 占位替换 / 纯文本覆盖 / `%` 覆盖不换算容量 |
| 多语言 | `GetUnitStr(CPU.Voltage)` × `LanguageManager` | 7 | zh/en/ja 三包切换 + `SetOverride` 覆盖通道 + 词典缺 key 后缀回退 |
| 内存口径 | `GetValueStr`/`GetUnitStr` MEM/VRAM | 14 | 百分比/容量、容量未知回退、多卡按 Key 查显存、`{u}` 让位全局口径 |
| 千位进制边界 | `FormatDataSizeParts`/`FormatDataSize` | 17 | 1023/1024/1023999/1048576、0/1/1e12/TB 级、负值、decimals 0/1、带后缀 |
| 发布点语义 | CPU.Voltage / MEM.Commit.Load | 14 | 数值与来源同发布点、缺失 `--` 非 0、VID 标记独立于用户单位、实测无标记、分子分母同快照固定 GB |
| MetricItem 组装 | `GetFormattedText`/`CachedValueText`/`CachedUnitText` | 22 | 标准类型 × Panel/Taskbar 上下文、用户格式、TextValue 自动补单位、0.005 缓存阈值、缓存值分离暴露 |
| **合计** | | **181** | 全绿（0 失败 0 跳过） |

数值域覆盖：真实 0、缺失 null（一般键按 0、CPU.Voltage/提交内存显示 `--`）、极大 1e12、
极小 0.001/0.004、负值（温度 -5.5/-0.001、放电功率 -20.5、字节数 -1024）、
千位进制边界四点、自适应小数切换边界（9.995/99.95/99.96）。

Key 全部采用产品默认监控项的真实大小写（`CPU.Load`/`CPU.Clock`/`GPU.Fan`/`NET.Down`/
`DATA.DayDown` 等，见 `SettingsHelper` 默认表），另保留 2 条**非产品大小写观察行**（见 2.3 #3）。

多语言说明：`LanguageManager.Load` 可在测试内切换（语言包随主工程 Content 复制到测试
输出目录，且本测试工程已关并行），故多语言为真实覆盖而非"记录限制"。

### 2.3 疑似 bug / 行为异常清单（只记录，不顺手修正期望值）

按可疑程度排序，全部已用 fixture 固化当前行为：

1. **`GetValueStr` 的 `compact` 形参完全失效**：任务栏路径（`MetricItem.GetFormattedText`
   横排）传 `compact=true` 期望整数紧凑显示，但 `GetValueStr` 从不读该参数
   （`src/Core/MetricUtils.cs:156-160` 数据量分支写死 `-1`，其余类型无紧凑逻辑）。
   fixture：VS 组真/假成对输出一致 + 专项 `FIX_GetValueStr_compact形参不改变输出`。
   影响：任务栏显示宽度不受控；B4 拆分格式规则时需裁决（修 or 固化现状）。
2. **`{u}/s` 用户格式在 Panel 上下文产生 `MB/s/s` 双后缀**：设置界面提示用户用
   `{u}/s`（`SettingsPanel` 上下文返回的占位符就是这个形式），但主界面
   `GetUnitStr(Panel)` 已返回带 `/s` 的单位，`GetDisplayUnit` 替换后重复。
   fixture：DU06（`("NET.Down","MB/s","{u}/s") → "MB/s/s"`）。高疑为真实 UI 缺陷。
3. **`GetAdaptivePercentage` 的 RecordedMax 匹配区分大小写**（`baseKey == "CPU.Clock"`
   等，与同函数族 `GetType` 的 OrdinalIgnoreCase 口径不一致）：大小写失配的 Key
   （如手写配置 `CPU.CLOCK`）匹配不到峰值 → max=1 → 恒 100% → **恒 CRIT**。
   产品默认 Key 均为 PascalCase 不受影响；fixture 固化观察行 STX1/STX2。
4. **1023999 字节显示 `1000KB`**：1000 阈值优化的注释目标就是避免 "1023 KB" 长串，
   但 `FormatValueAdaptive` 的四舍五入在 999.999KB 处又产生 4 字符数值 + "1000KB"。
   fixture：FD03/FD11。
5. **float 边界落点**：`99.95f→"99.9"`、`9.995f→"9.99"`（float(99.95) < double(99.95)，
   自适应切换点永远取不到"恰好"）；`GetState` 同类：FPS 129.6/144=0.89999… 落不到
   CRIT、CPU.Voltage 1.28/1.6f 落不到 WARN。fixture：VS06/VS09、ST33、ST35。
6. **1023 字节显示 `1.00KB`**（VS32/FD01）：0.999KB 舍入显示为 1.00，显示值大于
   实际（1.00KB=1024B>1023B）。低危显示瑕疵。
7. **`-0.001 → "-0.00"` 负零**（VS11）。低危显示瑕疵。
8. **`CPU.Temp` 1e12 → `"999999995904"`**（VS13）：float(1e12) 全数字展开、无缩写，
   极端值宽度爆炸。仅异常传感器会出现，低危。
9. **`FormatDataSize(1023,"B",0) → "1KBB"`**（FDS04）：自定义后缀与单位字面拼接的
   边缘效果，符合当前拼接语义。
10. **BAT.Percent 阈值硬编码 (60,20)**：`GetThresholds` 对电池百分比绕过用户
    `Thresholds` 设置（改 `Load.Warn` 不影响电池告警）。行为固化 ST11-ST17。

### 2.4 环境依赖项（非缺陷、固化时注明）

- 电池键在 Panel/Taskbar 上下文的充电后缀 `+` 依赖**宿主机实时电源状态**
  （`GetPowerStatus` 直读 `SystemInformation`，无注入点）—— fixture 采用
  双接受断言（`"%"` 或 `"%+"`），采集机（台式未充电）实测为无后缀；
  Settings 上下文（无后缀路径）已逐字固化。
- `MEM.Load` 容量口径依赖 `Settings.DetectedRamTotalGB`（静态字段），
  fixture 显式设置/还原，不依赖硬件探测。

### 2.5 新增测试数（供协调者上调 MIN_TEST_TOTAL）

- `LiteMonitor.Tests`：**+181**（0 跳过）。其余两工程零变化。
- 分支内全量结果：`LiteMonitor.Tests` 2322 通过/7 跳过；
  `GpuNonperformance.Tests` 19 通过；`Updater.Tests` 73 通过。

## 3. 性能基线（Plan B §6.3 / §8 口径）

- 脚本：`scripts/perf-baseline.ps1`（管理员 PowerShell 运行；可重复执行，B6 前后同机同配置复跑即得对照）
- 实测入库：`docs/verification/baseline/perf-baseline-20261026-0345.md`（摘要）+
  `.json`（含每秒原始采样）+ `perf-probe-20261026-0345/`（探针原始数据与日志，退出码 0）
- 环境提要：QINGLING（MS-7C35）/ Ryzen 7 5800X（8C/16T）/ 63.9 GB /
  **RTX 3080 + 3080 Ti**（驱动 32.0.16.1088）/ Win11 专业版 build 26300 / .NET SDK 8.0.426 /
  HEAD `67afb8a`、Release、全新默认配置（不碰用户 settings.json）、提权运行。

### 3.1 启动时间（进程启动 → WaitForInputIdle）

| 轮 | 启动 (ms) | 备注 |
|---|---|---|
| 1 | 353 | 含驱动准备，偏慢 |
| 2 | 332 | 稳态轮 |

### 3.2 60 秒采样窗口（默认配置静置，1s 粒度）

| 轮 | CPU 秒 (Δ) | CPU%（全核均摊） | 私有字节 min/avg/max | 稳态均值(去前5s) | 线程 首→末 | 句柄 首→末 |
|---|---|---|---|---|---|---|
| 1 | 9.656 | 1.005 | 12.3 / 75.8 / 95.7 MB | 77.0 MB | 9→28 | 248→830 |
| 2 | 8.515 | 0.886 | 12.0 / 75.9 / 94.3 MB | 76.5 MB | 9→28 | 239→831 |

两轮窗口内线程/句柄爬升后走平（句柄 ≈830 封顶），无持续增长趋势；原始每秒采样
见 json 的 `gui.launches[].samples`。

### 3.3 采集探针（9 组全跑，预热 15s/稳态 60s）

| 组 | 场景 | 采集请求 | 实际采集轮 | 整机 CPU% |
|---|---|---|---|---|
| A | 1000ms、默认单卡、Web 关 | 60 | 60 | 0.2113 |
| B | 100ms、默认单卡、Web 关 | 599 | 60 | 0.1969 |
| C | 100ms、GPU 项全关 | 600 | 60 | 0.07 |
| D | 100ms、Web 开无人访问 | 600 | 60 | 0.2327 |
| E | 100ms、单已鉴权网页 | 600 | 60 | 0.2165 |
| F | 100ms、多网页+HTTP 轮询 | 600 | 60 | 0.1595 |
| G | 100ms、多卡本地监控 | 540 | 60 | 0.3629 |
| H1 | 3000ms、Web 关 | 20 | 15 | 0.0455 |
| H2 | 3000ms、Web 开 | 20 | 56 | 0.1416 |

★ 关键基线事实（Plan B §6.1"采样"行的通过条件）：100ms 高刷新组的**采集轮数恒为 60**
（= 60s × 1000ms 门限），采样次数**不随显示刷新频率倍增**；多消费者（E/F）与多卡（G）
同样不放大采集轮数。单设备 Update 成本（15 项）与兼容缓冲绘制微基准在
`perf-probe-20261026-0345/probe-results.json` 的 `deviceUpdateCosts`/`renderBench`。

### 3.4 采集过程记录（诚实披露）

- 第一轮（03:19）：GUI 阶段被守卫跳过（用户自有实例 PID 50300 在跑，无法归属 CPU），
  探针完整；该轮产物已删除不作为基线。
- 第二轮（03:3x）：脚本统计段 bug（`OrderedDictionary` 无 `ContainsKey`）在首轮
  采样后崩溃，暴露后修复（`Contains` + try/finally 保证任何路径回收自启实例）。
- 第三轮（03:45，入库）：GUI 两轮 + 探针 9 组全绿，退出码 0。修复后的脚本已按
  第三轮逻辑验证（探针摘要表可正常产出）。

## 4. 无法自动采集项（不造数）

- **UI 刷新延迟**（帧节奏/单帧绘制耗时）：产品无内部计时发布点；无侵入外部测量
  需高速抓屏+图像差分，误差不可控。待 B6 渲染路径契约化后补计时点。
- **设备目录查询延迟**（目录重扫/热插拔识别时长）：无生产侧计时点。待
  DeviceCatalog 契约化后补。
- **GPU 宿主渲染路径**：属 `tools/GpuHostLifecycleProbe` 独立口径，不在本脚本范围。
- **多展示端一致性耗时**（GDI+/GPU/横排/任务栏/Web 各端格式化差异）：仅兼容缓冲
  绘制被 renderBench 部分覆盖，完整多端计时无发布点。

## 5. 偏离与说明

- **master 已前进**：派工基点 `67afb8a` 之后，主会话已把 B1/B2 合入 master
  （`2a505f6`/`950f026`/`5f16218`）。本分支保持派工时点基点不动；fixture 期望值
  固化于 `67afb8a`。**合并预检已通过（2026-10-26）**：在 `5f16218`（B1/B2 之后）
  的独立检出上运行本 fixture 文件，181/181 全绿——B1/B2 消费方迁移未改变任何
  格式行为；协调者合并本分支时应保持该结果。
- **工作树伪脏文件**：`tests/LiteMonitor.Tests/PluginMarketRecoveryBoundaryTests.cs`
  在全新工作树检出后即显示整文件修改（`--ignore-cr-at-eol` 下 diff 为空，系该文件
  以 CRLF 入库与 `.gitattributes` 规整化规则的既有不一致在新鲜检出时暴露）。非本包
  改动，未纳入提交，建议协调者择机 `git add --renormalize` 修复。
- NuGet 缓存按派工单使用工作树本地副本 `.worktrees/b0-tail/.nuget-packages`
  （自主工作树缓存拷贝而来），与主工作树零争抢。
