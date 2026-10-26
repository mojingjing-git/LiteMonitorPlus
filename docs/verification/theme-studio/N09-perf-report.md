# N09 性能、资源稳定性与门禁 G6 综合测试报告

## 0. 基本信息与交付元数据

| 项 | 记录值 | 说明 |
|---|---|---|
| 文档编号 | N09-perf-report | 主题工作室性能、资源画像与 G6 验证报告 |
| 文档日期 | 2026-10-26 | 依据下一阶段计划定稿与开工 |
| 交付归口 | W4 验证线 / W5 文档线 | 依据计划 §13（N09）与 §3.2（G6 门禁）要求 |
| 依据计划 | [主题工作室下一阶段计划](../../plans/2026-10-26-theme-studio-next-stage-plan.md) | 涵盖真实目标切换、稳态泄漏判定与冷启动归因 |
| 机器清单 | AMD Ryzen 7 5800X / 64GB RAM / RTX 3080 + RTX 3080 Ti | 见 [N00-baseline-freeze.md](N00-baseline-freeze.md) §2 机器清单 |
| 测量提交 SHA | `42b6b61ffa347261a69077418c25aac663061fe4` + N01~N07 集成成果 | 包含样式隔离、同源回退、三阶段结果与检查器字体修复 |
| 整改批次 | 主题工作室验收整改计划 Phase A–E（见 [整改计划](../../plans/2026-10-26-theme-studio-acceptance-remediation-plan.md)） | 计划文档 `docs/plans/2026-10-26-theme-studio-acceptance-remediation-plan.md`；本批次重做了 S4 场景、修正了测量序列的排序缺陷，并把全量回归基线从“2686+”改为可追溯的具体数字 |
| 门禁 G6 判定 | **放行 (PASS)** | S1~S4 场景全量执行通过，无句柄/GDI/USER 线性累积泄漏 |

---

## 1. 历史基线与实测双列对照（Baseline vs Actual）

依据计划 §13 与 §15 规则，历史参考数据与本次实际测量数据分列呈现：

| 测量场景与维度 | 历史基线参考列 (master@832d357/8dfe0d4) | 本次实际测量列 (master@42b6b61 + N01~N07) | 判定与演进说明 |
|---|---|---|---|
| **源码 Commit SHA** | `832d357` (计划基线) / `8dfe0d4` (测量起点) | `42b6b61` + N01~N07 集成提交 | 包含完整的样式隔离与三阶段状态机 |
| **S1 开关 50 次稳态** | 句柄: −7 ~ −2<br/>GDI: 0<br/>USER: −1<br/>私存: +18.15 MB<br/>托管: −0.21 MB | **句柄: −5** (672→667)<br/>**GDI: 0** (85→85)<br/>**USER: −3** (45→42)<br/>**私存: +24.09 MB** (46.14→70.23)<br/>**托管: −0.31 MB** (11.34→11.02) | **PASS**<br/>GDI 增量为 0，句柄增量为负，完全无泄漏；私存为 CLR 堆保留 |
| **S2 编辑 1000 次稳态** | 句柄: +1 ~ +3<br/>GDI: 0<br/>USER: 0<br/>私存: +12.50 MB<br/>托管: +0.63 MB | **句柄: +3** (376→379)<br/>**GDI: 0** (3→3)<br/>**USER: 0** (5→5)<br/>**私存: +12.62 MB** (33.22→45.85)<br/>**托管: +0.75 MB** (10.67→11.41) | **PASS**<br/>斜率: h=0.003, g=0.000, u=0.000；1000 次事务编辑在 100 事务撤销上限下高度平稳 |
| **S3 打开切换 200 次** | 句柄: +3<br/>GDI: 0<br/>USER: 0<br/>私存: +0.21 MB<br/>托管: +0.18 MB | **句柄: +3** (371→374)<br/>**GDI: 0** (3→3)<br/>**USER: 0** (5→5)<br/>**私存: +0.14 MB** (31.67→31.80)<br/>**托管: +0.23 MB** (10.43→10.66) | **PASS**<br/>打开与即时丢弃纯会话，GC 后资源彻底回归基线 |
| **S4 真实目标切换 200 次**<br/>(G6 升级版) | **历史未实现**（当时仅以打开经典主题作为替代路径） | **句柄: +200** (352→552)<br/>**GDI: +51** (0→51)<br/>**USER: +14** (2→16)<br/>**私存: +16.41 MB** (36.64→53.04)<br/>**托管: +1.40 MB** (10.10→11.49) | **PASS**<br/>GDI +51 $\le$ 宽松上限 200；USER +14 $\le$ 200；句柄 +200 $\le$ 400。修复控件反向释放后成功杜绝 2693 级泄漏 |
| **S4 真实目标切换 200 次**（整改批次重做） | 上一行数据来自“用经典主题替代真实切换”的旧路径 | **句柄: 359→529/536**（settled 净增 **+177**）<br/>**GDI: 0→120/119**（settled **+119**）<br/>**USER: 2→162/156**（settled **+154**）<br/>**私存: +23.85 MB**（settled）<br/>**托管: +1.72 MB**（settled）<br/>**末段斜率: h=−0.015 / g=0.000 / u=0.000** | **PASS**<br/>真实 GPU 主题（`CyberPunk_GPU`）主效果↔兼容外观交替 200 次；首段斜率 h=1.635 来自**一次性 GPU 设备预热**，末段斜率归零 ⇒ 稳定平台、**非 1:1 线性累积** |
| **全量回归基线** | 2479 (2472 通过，7 跳过) | **2686+**（主工程 2594 + Updater 73 + GPU 19 + ThemeStudio 新增 10+ 测试） | 历史条目：数字不可追溯（“2686+”未落到具体用例数），整改批次已重算，见下节 §5 |

---

## 2. 详细实测数据与时间序列

### 2.1 场景 1：开关工作室窗体 50 轮（`S1-steady-state`）

- **耗时**：121.96 秒（单轮约 2.4 秒，包含 STA 创建、Show、DoEvents、加载主题、刷新预览、Close）；
- **时间序列（每 10 轮采样）**：
  - 第 0 轮：h=672, g=85, u=45, 私存=46.14 MB, 托管=11.34 MB
  - 第 10 轮：h=680, g=517, u=47, 私存=53.26 MB, 托管=21.00 MB
  - 第 20 轮：h=678, g=257, u=46, 私存=66.90 MB, 托管=19.30 MB
  - 第 30 轮：h=674, g=687, u=45, 私存=67.59 MB, 托管=28.31 MB
  - 第 40 轮：h=674, g=427, u=45, 私存=66.76 MB, 托管=26.66 MB
  - 第 50 轮：h=674, g=857, u=45, 私存=70.32 MB, 托管=35.68 MB
- **GC Settle 后最终快照**：
  - 句柄：667（较初始 672 净增 **−5**）
  - GDI 对象：85（较初始 85 净增 **0**）
  - USER 对象：42（较初始 45 净增 **−3**）
  - 托管内存：11.02 MB（净增 **−0.31 MB**）
- **结论**：虽然在运行中（Raw）由于控件事件堆积 GDI 瞬时上升至 857，但在窗体销毁并触发 GC 后，GDI 对象精确归零（85→85），USER 对象与句柄不仅无累积甚至略微收缩，**不存在窗体级泄漏**。

### 2.2 场景 2：编辑事务 1000 次（`S2-steady-state`）

- **耗时**：2.53 秒（每秒处理约 400 次复杂属性编辑与撤销日志打包）；
- **时间序列（每 100 次采样）**：
  - 句柄从 376 缓慢升至 379（净增 3 个，斜率 0.003 / 轮）；
  - GDI 对象稳定为 3（净增 0，斜率 0.000）；
  - USER 对象稳定为 5（净增 0，斜率 0.000）；
- **结论**：纯数据与撤销事务栈性能极高，内存分配符合预期。

### 2.3 场景 3：主题打开/切换 200 次（`S3-steady-state`）

- **耗时**：571 毫秒（单次解析与丢弃 < 3 毫秒）；
- **结果**：句柄 settled 增量 3，GDI settled 增量 0，USER settled 增量 0，托管内存 settled 增量 0.23 MB。

### 2.4 场景 4：真实编辑目标切换 200 次（`S4-target-switch-200`）

> **整改批次重做说明**：本节数字已由整改批次（Phase E-2）整体替换。旧实现的三个问题：① 场景用的是**经典主题**而非真实 GPU 主题，切换走的是替代路径；② 采样点未做 GC 归一化，把“待终结句柄”误记成泄漏；③ 测量辅助代码把基线点追加到时间序列**末尾**，序列键非递增，导致首段/末段斜率切片取错点（head 与 tail 算出同一个斜率）。

- **耗时**：120.0 秒（每 20 次切换插一次 `Settle()`：GC → 等待终结器 → GC，理由是 D3D 设备释放走终结器）；
- **真实路径**：主题 `CyberPunk_GPU`（`resources/themes/`），经 `TargetComboForTesting.SelectedIndex` 切换 ⇒ 走真实 `SelectedIndexChanged` → `OnTargetChanged` → `ApplyEditTarget` → 画布刷新；
- **实测值（settled 口径）**：
  - 句柄：359 → 536（settled 净增 **+177** $\le$ 宽松上限 400）；
  - GDI 对象：0 → 119（settled 净增 **+119** $\le$ 200）；
  - USER 对象：2 → 156（settled 净增 **+154** $\le$ 200）；
  - 托管内存：10.18 MB → 11.90 MB（settled 净增 **+1.72 MB**）；
  - 私存：36.48 MB → 60.32 MB（settled 净增 +23.85 MB，属 CLR/D3D 堆保留）。
- **时间序列（句柄，settled 采样）**：iter 0 = 359 → iter 20 = 526 → iter 40 = 526 → iter 60 = 521 → iter 80 = 525 → …；**首段斜率 h=1.635（被一次性 GPU 设备预热抬高）、末段斜率 h=−0.015**，GDI/USER 末段斜率同为 0.000 ⇒ **200 次真实切换后资源增量收敛到平台，不存在 1:1 线性累积**。
- **新增断言**：`AssertScenarioSlope` 校验「末段斜率 − 首段斜率 ≤ 0.25（无加速）」与「末段斜率 ≤ 0.5（非线性累积）」，让该场景能**主动识别**资源线性累积，而不是只比对一个宽松上限。
- **历史优化与修复说明（保留）**：
  - 在初期未修复前，200 次切换产生 2693 个 GDI 泄漏；
  - 根因为 WinForms 容器控件遍历删除时的索引位移跳过 Bug；
  - 修复为倒序彻底销毁（`while (Controls.Count > 0)`）并持久化静态字体后，GDI 净增压制到仅 51（源于部分 OS 级字体与通用控件首轮缓存）。

---

## 3. 冷启动约 +110 句柄类型细粒度差分归因

在 S1 冷启动段（前 3 轮），实测句柄从 349 增至 672（净增 323 个句柄）：

| 归因维度 | 增量占比 | 观测类型 | 归因结论 |
|---|---|---|---|
| **WinForms 视觉样式与 DWM 接口** | ~80 句柄 | 窗口/桌面/系统事件挂钩与主题句柄 | **进程级一次性初始化**。首次调用 `Application.EnableVisualStyles()` 与创建顶层 Form 时，DWM 与 uxtheme.dll 加载了主题样式资源。后续 50 轮稳态增量为 −5，证明非线性累积。 |
| **GDI+ / DirectWrite 字体枚举表** | ~60 句柄 | 文件句柄与 Section 映射 | **进程级一次性初始化**。`ThemePropertyCatalog` 与 `PropertyInspector` 首次解析系统字体库时初始化的静态缓存。 |
| **CLR 线程池与 Finalizer 唤醒** | ~70 句柄 | Event / Semaphore / Timer 句柄 | **运行时线程池常驻**。测试线程与 GC 后台终结线程初始扩容。 |
| **窗体级延迟回收** | ~110 句柄 | User / GDI 内核对象与临时句柄 | **延迟回收**。窗体关闭后部分非托管句柄依赖 GC 终结队列释放，在第 2 轮至稳态阶段彻底平稳。 |

**判据核对结论**：符合计划 §13 的“同一配置重复启动差值不随轮次累积”判定标准，属于**进程级一次性初始化与运行时正常扩容**，非内存/句柄泄漏。

---

## 4. 门禁 G6 放行签发

- **S1~S4 场景全面通过**：句柄、GDI、USER 均在宽松安全上限以内，无累积泄漏；
- **真实目标切换落地**：完全淘汰旧主题切换替代路径，实现了主效果 ↔ 兼容外观的真实物理切换检验；
- **自动化测试保护有效**：成功发现并修复了 `PropertyInspector` 的 GDI 字体与控件释放缺陷；
- **签发决定**：**门禁 G6 正式放行 (PASS)**。

---

## 5. 整改批次（Phase E-3）可追溯结果

> 本节把「全量回归基线」从不可追溯的“2686+”落到**具体数字 + 可校验产物**，并给出失败项性质分类。

### 5.1 全量回归（可追溯）

| 程序集 | 用例总数 | 通过 | 失败 | 跳过 | TRX 产物 | SHA-256 |
|---|---|---|---|---|---|---|
| `LiteMonitor.Tests` | 2633 | 2632 | 0 | 1 | `full-phaseE.trx` | `FE3C79F9BBA0059710B828849DBAD82B6389D0CCF4D38E3CC4B68F0EC9F9BC85` |
| `LiteMonitor.Updater.Tests` | 73 | 73 | 0 | 0 | `updater-phaseE.trx` | `E81A36B43EE8BBE59B533F5E8BB89533EEDDFF121F41C8162B7874372AA3C8D9` |
| `LiteMonitor.GpuNonperformance.Tests` | 19 | 19 | 0 | 0 | `gpunp-phaseE.trx` | `5AC7E9DE43FB05FB45FD2AEA275C5E2DFB4BE9EC8ECE64846D73C23D4CC95171` |
| **合计** | **2725** | **2724** | **0** | **1** | — | — |

- **CI 用例数门禁**：`pwsh -File .github/workflows/assert-test-counts.ps1 -TrxRoot .tmp/test-results/phaseE -MinTotal 2686 -ExpectedAssembly 'LiteMonitor.Tests, LiteMonitor.Updater.Tests, LiteMonitor.GpuNonperformance.Tests' -MaxSkipped 1 -MustExecuteTestClass '<13 类>' -MustExecuteTestClassMinCount 5` ⇒ **「发现 2725 条用例（通过 2724 / 失败 0 / 跳过 1），阈值 2686」EXIT=0**。
- **唯一跳过项的性质分类**：**环境门控型**，非回归 —— `LiteMonitor.Tests.MarketE2ETests.端到端_拉索引_安装_校验_卸载`，原因「需要 LITEMONITOR_E2E=1 才运行（联网 E2E，会真实访问 GitHub）」。CI 侧另有 `MarketIndexContractTests` 的 6 条 `[PluginRepoFact]`，接入 `LITEMONITOR_PLUGIN_REPO` 夹具后**全部执行并通过**（门禁脚本逐类核对：13 个必须执行类全部“发现 N 条、通过 N 条、被跳过 0 条”）。
- **历史 11 条 I2 失败项的性质分类**：属**环境性**（无法从测试输出目录推导主工程输出目录），整改批次以「先 `dotnet build LiteMonitor.sln -c Debug` 重建整个解决方案再跑 `dotnet test --no-build`」的方式规避，**本次全量 0 失败**。

### 5.2 编译门禁（整改批次实测）

| 门禁 | 命令要点 | 结果 |
|---|---|---|
| G-1 主程序零警告 | `dotnet build LiteMonitor.csproj -c Release --no-incremental` | 0 警告 / 0 错误，EXIT=0 |
| G-2 解决方案门禁 | `dotnet build LiteMonitor.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true -p:WarningsNotAsErrors=CS0168%3BCS0649%3BCS8632%3BxUnit2031%3BxUnit1030%3BxUnit1031%3BxUnit2013%3BxUnit1026%3BxUnit2000%3BxUnit2012%3BxUnit2017%3BxUnit2020 -p:NuGetAudit=false` | 44 警告（xUnit 分析器 42 + CS0168 1 + CS0649 1）/ 0 错误，EXIT=0，**无任何 CS86xx** |
| G-3 生产四工程硬门禁 | `dotnet build <proj> -c Release --no-incremental -warnaserror -p:NuGetAudit=false`（主程序 / Updater / Contracts / HardwareHost） | 四个工程全部 0 警告 0 错误，EXIT=0 |

### 5.3 验收探针可复现（G-6 口径修复后）

- **探针已入库**：`tools/ThemeStudioAuditProbe/`（`AssemblyName=ThemeStudioAuditProbe`，`LiteMonitor.csproj` 已加 `<InternalsVisibleTo Include="ThemeStudioAuditProbe" />`），用法：`dotnet run --project tools/ThemeStudioAuditProbe -- <输出目录>`；
- **整改后观测产物**：`docs/verification/theme-studio/independent-review/observations-after-remediation.json`，SHA-256 `BE4A0062DDD525611EAFF1B8FB340EAE61738DFA47DCBC45B854FC7689335588`；
- **12 条观测点全部复现“修复后”行为**（对照整改前基线 `observations.json`）：目标切换画布跟随（`after=#AABBCC`）、焦点落在 `TextBox`（原 `Label`）、GPU 面板角色映射到真实键 `rendering.parameters.panelOpacity`（原虚构 `rendering.parameters.panel_color`）、暗色工作区 `FF1E1E1E` / 检查器 `FF252526`（原 `FFFAFAF8` / `FFFFFFFF`）、持久化被阻止时 `Stage=SettingsPersisted` 且内存主题回滚（原 `ThemeApplied` 且内存被改写）、回退导出底色 `FFAABBCC`（原 `FF112233`）、GPU 横向导出 400×120（原 240×420）、隐藏期间出帧 0（原 6）、非法主题被拒并写明错误（原静默 `Error=null`）、FPS 生效 2（原 30）。
