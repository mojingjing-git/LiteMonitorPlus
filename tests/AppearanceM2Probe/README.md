# AppearanceM2Probe —— M2 合同 §9 验收门槛自证探针

本工程验证 **M2 合同 §9**（`docs/superpowers/plans/2026-10-20-appearance-m2-contract.md`）中的验收门槛，分两组：

| 组别 | 门槛 | 判定方式 |
|---|---|---|
| **源码级** | 第 7 条、第 10 条 | 源码 / csproj 文本扫描 |
| **运行时** | 第 1、2、3、4、5、6、8、9 条 | 探针自有的**假后端**驱动真实实现 |

> 合同来源：`docs/superpowers/plans/2026-10-20-appearance-m2-contract.md` §9「A15 的验收门槛」。
> 源码级门槛不依赖捕获后端的任何 API，可与 A15 的实现并行推进、独立验证；
> 运行时门槛直接跑 A15 落地后的实现（`BackdropCaptureService` / `ScreenCaptureFrameSource` / `BackdropImageProcessor`）。

> ⚠️ 本工程**不进 sln**（与既有 `AppearanceWiringProbe` / `LiteMonitor.PerformanceProbe` 的惯例一致）。
> ⚠️ 不新增任何 NuGet 依赖，只引用主项目与 BCL。
> ⚠️ 探针**只读取源码文本与 csproj 文本**：不创建 `HardwareMonitor`、不 Open 硬件、
> 不做任何屏幕捕获、不读也不写用户配置目录。
> ⚠️ 运行时门槛里的"捕获"全部由探针自有的**假后端**现画小位图产生：
> **绝不**调用 `CopyFromScreen` / `BitBlt` / 任何抓屏 API。

## 两条门槛与 M2 合同 §9 的对应关系

| 用例 | 合同 §9 门槛 | 检查方式（合同原文） |
|---|---|---|
| `M2 门槛#7 绘制路径无捕获调用` | **第 7 条** | 源码扫描（`BitBlt`/`CopyFromScreen`/`DXGI` 等零命中） |
| `M2 门槛#10 无新增 NuGet 依赖` | **第 10 条** | `*.csproj` diff |

## 用法

```powershell
# 由集成者串行构建后运行（本探针自身不负责构建）
tests/AppearanceM2Probe/output/AppearanceM2Probe.exe [--repo-root <仓库根>] [--out <证据目录>]
```

- `--repo-root <路径>`：显式指定仓库根（默认从程序所在目录向上逐级查找）。
- `--out <路径>`：证据输出目录（默认 `output\evidence\`）。

退出码：`0` = 全部通过；`1` = 有用例失败；`2` = 探针自身异常。
证据 JSON：

- `output\evidence\m2-source-gate-evidence.json` —— 源码级门槛（kind = `appearance_m2_source_gate_probe_result`）
- `output\evidence\m2-runtime-gate-evidence.json` —— 运行时门槛（kind = `appearance_m2_runtime_gate_probe_result`）

## 仓库根定位口径

从 `AppContext.BaseDirectory` 向上逐级查找**第一个同时包含 `src/` 目录与 `LiteMonitor.csproj` 文件**的祖先目录。
找不到（含 `--repo-root` 指向的目录不合格）时，**两条用例都判失败并写明原因** —— 不允许静默通过。

## 用例一：门槛 #7 —— 绘制路径无捕获调用

### 扫描范围

递归扫描 `src/` 下**全部 `.cs` 文件**（跳过 `bin/`、`obj/`）。

### 抓屏原语清单

`BitBlt`、`CopyFromScreen`、`PrintWindow`、`CreateCompatibleDC`、`CreateCompatibleBitmap`、
`SetWindowDisplayAffinity`、`GetDC(`、`DXGI`、`D3D11CreateDevice`、`IDXGIOutputDuplication`

### 判定口径

| 范围 | 判定 |
|---|---|
| `src/System/Backdrop/**`（M2 捕获子系统） | **允许**出现原语（记入证据 `allowedDirectoryHits`） |
| `src/Rendering/**`（本轮 GPU 渲染子系统：设备创建、桌面复制、前景绘制、呈现） | **允许**出现原语（记入证据 `allowedDirectoryHits`） |
| `src/UI/Settings/TaskbarPage.cs`（任务栏页既有取色截图，与外观无关） | **允许**（记入证据 `whitelistHits`） |
| `src/UI/AppearanceRuntime.cs`、`src/UI/Decor/`、`src/ThemeEditor/`、`src/Core/`、`src/System/Backdrop/BackdropRenderer.cs` | **必须零命中（硬断言）** |
| 其余 `src/` 文件 | 命中即判失败 |

★ `src/System/Backdrop/BackdropRenderer.cs` 虽位于允许目录内，但它是**必须零命中**的对象：
其文件头注释声明「本文件不出现 BitBlt/PrintWindow/DXGI 等抓屏 API」，探针把这条声明当成硬断言。

★ 2026-10-22 补充：`src/Rendering/**` 是本轮 N03 的 GPU 渲染子系统。
其中的 `DXGI` / `D3D11CreateDevice` 是 GPU 路线必需的设备 API，`SetWindowDisplayAffinity` 是 live 捕获的
WDA 排除，都不属于「外观层绕过捕获子系统私自抓屏」，因此纳入允许目录；
真正的屏幕抓取原语（`BitBlt` / `CopyFromScreen` / `PrintWindow` / `IDXGIOutputDuplication`）
在 `src/Rendering` 之外仍然命中即失败。

### 注释剔除规则（关键）

**匹配前必须先剔除行注释（`//`、含 `///` XML 文档注释）与块注释（`/* */`）**，
字符串/字符字面量内容也一并剔除；否则注释里提到的 API 名会被误判成命中
（`BackdropRenderer.cs` 的自证声明就会把自己判失败）。

剔除采用逐字符状态机（行注释 / 块注释 / 普通字符串 / 逐字字符串 `@"..."` / 原始字符串 `"""..."""` / 字符字面量），
剔除时用空格替换、保留换行，因此**行号与原文一一对应**。

证据里的 `rawTextMentionCount`（原始文本中的提及数，含注释）与
`suppressedByCommentStripping`（被注释剔除挡掉的提及数）用于证明剔除逻辑确实生效。

## 用例二：门槛 #10 —— 无新增 NuGet 依赖

- 检查范围：根 `LiteMonitor.csproj` + `src/` 下若存在的独立 `*.csproj`。
- 基线（恰好等于，多一个或少一个都判失败）：
  - `LibreHardwareMonitorLib` `0.9.6`
  - `System.Text.Json` `10.0.3`
  - `Microsoft.Diagnostics.Tracing.TraceEvent` `4.0.0`
  - `Vortice.Direct2D1` `3.8.3`
  - `Vortice.Direct3D11` `3.8.3`
- 2026-10-22 更新：GPU 路线（N03）引入 Vortice.Direct2D1 / Vortice.Direct3D11；
  `Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0` 强制要求 `System.Text.Json >= 10.0.3`，
  因此 `System.Text.Json` 基线同步升到 `10.0.3`。
- 证据 `details.actualPackages` 列出实际集合（包 Id / 版本 / 声明它的工程），
  `missingPackages` / `extraPackages` / `versionMismatches` 给出差异。

## 运行时门槛（第 1 / 2 / 3 / 4 / 5 / 6 / 8 / 9 条）

实现见 `RuntimeGateRunner.cs`。这一组**直接跑 A15 落地后的真实实现**，但所有"捕获"都由探针自有的
`FakeCaptureBackend`（实现 `IBackdropCaptureBackend`）现画小位图产生 —— 不碰屏幕、不抓屏。

### 用例总览

| 用例 | 合同 §9 门槛 | 判定口径（一句话） |
|---|---|---|
| `M2 门槛#1 捕获后端 IsPreview=false` | 第 1 条 | `BackdropCaptureService` / `ScreenCaptureFrameSource` 的 `IsPreview` 恒为 `false`；把固定样例图包成帧后端的适配器恒为 `true` |
| `M2 门槛#2 预览路径后端调用次数=0` | 第 2 条 | 构造带 `previewSource` 的 `AppearanceRuntime` + 计数型假后端，渲染一次后 `TryGetFrame` 调用次数 = 0 且 `Source == PreviewFrame` |
| `M2 门槛#3 Source 三值语义正确` | 第 3 条 | 真实帧 ⇒ `LiveFrame`；预览固定图 ⇒ `PreviewFrame`；后端不可用 / 无有效帧 ⇒ 走降级链（`Gradient`/`Legacy`）且必须带原因码与可读原因文本 |
| `M2 门槛#4 代次不匹配的帧被丢弃且被回收` | 第 4 条 | 换掉三个代次后产生的旧代次帧不得被 `TryAcquireFrame` 交给 UI，且必须由服务自己回收（`FramesDroppedStale` 递增 + 存活位图归零） |
| `M2 门槛#5 pending 最多一份` | 第 5 条 | UI 一帧也不取时连续产出多帧：`PendingFrameCount` 恒 ≤ 1，存活位图数有界，停止后归零 |
| `M2 门槛#6 取消后恢复显示亲和性` | 第 6 条 | 服务 `Stop`（有上限等待）后，后端 `RestoreDisplayAffinity` 至少被调用一次 |
| `M2 门槛#8 CPU 与经过时间分别计量` | 第 8 条 | `BackdropServiceMetrics` 上 CPU 口径字段与经过时间口径字段**同时存在且互为不同字段**；两个口径都被分别填值 |
| `M2 门槛#9 缓冲预算有界` | 第 9 条 | `MaxBufferPixels == 2_000_000`、`MaxBufferBytes == 32 MiB`；超限源图被直接拒绝（先判后分配）；接入预算处理器后服务不发布超限帧并释放它 |

### 逐条细则

#### #1 捕获后端 `IsPreview => false`

- 断言三个类型的实际取值并写进证据：`BackdropCaptureService.IsPreview`、
  `ScreenCaptureFrameSource.IsPreview`、`FixedImageFrameSource.IsPreview`（探针内定义的
  "把固定样例图包成帧后端"的适配器）。
- 前两者必须是 `false`（合同 §2.2 第 1 条），第三者必须是 `true`
  （合同 §2.2 第 3 条：任何把固定/合成图包装成帧后端的代码都必须自报预览来源）。
- 三个断言合起来证明该语义**自洽**：真实捕获不会被误记成预览，预览也不会被误记成实时。

#### #2 预览路径后端调用次数 = 0

- `AppearanceRuntime(frameSource: 计数型假后端, previewSource: 固定样例图)`；
- 跑一次 `BackdropRenderer.Render`；
- 断言 `TryGetFrame` 调用次数 == 0（**结构性保证**：预览路径根本不访问后端），
  且 `Status.Source == PreviewFrame`、`EffectiveMode == Glass`。

#### #3 `Source` 三值语义

分别构造三种情形并把 `Source` / `EffectiveMode` / `IsDegraded` / `ReasonCode` / `Reason`
写进证据的 `details.scenarios[]`：

- (a) 真实捕获：后端 `IsPreview=false` 且给出有效帧 ⇒ `LiveFrame`；
- (b) 预览固定图：`options.PreviewSource != null` ⇒ `PreviewFrame`（且后端调用次数 = 0）；
- (c1) 后端 `IsReady=false` ⇒ 降级，原因码 `BackendNotReady`；
- (c2) 后端 `IsReady=true` 但取不到帧 ⇒ 降级，原因码 `BackendFrameInvalid`。

(c) 的两种情形都必须满足：`Source` 既不是 `LiveFrame` 也不是 `PreviewFrame`、
`IsDegraded == true`、`ReasonCode != None`、`Reason` 文本非空。

#### #4 代次不匹配的帧被丢弃且被回收

- 假后端装上**捕获闸门**（`ManualResetEventSlim`）：服务进入捕获后停在闸门上；
- 探针此时调用 `UpdateRequest` 换掉**主题 / 句柄 / 尺寸**三个代次，再放行闸门 ——
  于是这一帧属于**旧代次**；
- 断言：`Metrics.FramesDroppedStale >= 1`（服务自己回收了它）、旧代次帧**没有**被
  `TryAcquireFrame` 交给 UI、停止并释放 UI 租约后存活位图数归零；
- ★ 注意：**新代次**的帧能接管是**正常**行为，不算缺陷 —— 断言的是"接管到的每一帧的代次
  都必须与当前请求一致"，而不是"接不到帧"。

#### #5 pending 最多一份

- 假后端持续产帧，UI **一帧也不取**（这正是 pending 会堆积的压力场景）；
- 每 5 ms 采样 `PendingFrameCount`（最大值必须 ≤ 1），每 50 ms 采样存活位图数
  （"假后端产出 − 已释放"，上界 ≤ 2 = pending + 在途）；
- 停止服务后存活位图数必须归零。

#### #6 取消后恢复显示亲和性

- 假后端记录 `RestoreDisplayAffinity` 的调用次数；
- 服务 `Start` → 至少完成一次捕获 → `Stop(TimeSpan)`（有上限等待）；
- 断言计数 ≥ 1，且后台任务在上限内退出（取消必须可等待但有上限）。

#### #8 CPU 与经过时间分别计量

- **结构断言**（反射 `BackdropServiceMetrics` 的属性）：CPU 口径
  `CpuMsTotal` / `CpuMsPerFrameAvg` 与经过时间口径 `ElapsedMsLast` / `ElapsedMsP95` /
  `ElapsedMsMax` 以及五段 `captureMs` / `filterMs` / `publishMs` / `waitUiMs` /
  `excludeWindowMs`（对应 `Last*` / `Avg*` / `Max*`）**同时存在**、类型都是 `double`、
  且两个口径的字段名集合**互不相交**（不得是同一个字段被重复解读）；
- **运行时断言**：服务跑若干轮后 `ElapsedMsLast > 0`、`LastWaitUiMs > 0`、
  `ElapsedMsLast == capture+filter+publish+wait`（经过时间口径就是墙钟四段之和）、
  `CpuMsTotal <= ElapsedMsLast`；
- ★ **CPU 口径为 0 时不得据此断言"经过时间也为 0"**：Windows 进程 CPU 时钟粒度约
  15.6 ms，单轮差值落在粒度以内时记 0 表示**不可判定**、不表示零开销。
  证据里 `details.cpuClockNote` 写明这一点，并同时给出两个口径的实测值。

#### #9 缓冲预算有界

- 断言常量：`BackdropImageProcessor.MaxBufferPixels == 2_000_000`、
  `MaxBufferBytes == 32 MiB`；
- 喂一张 `2000×2000`（4,000,000 像素 > 上限）的位图给
  `BackdropImageProcessor.Process`：必须返回 `null`、结果标记为失败、给出可读原因，
  且 `CurrentTempBytes` **不变**（先判后分配，不做"先分配再判断"）；
- 再让假后端产出同样超限的帧，并把它接到服务上（`BudgetFrameProcessor` 适配器把
  `BackdropImageProcessor` 接到 `IBackdropFrameProcessor`）：
  服务必须**拒绝或按规则降级**而不是照单全收 —— `FramesPublished == 0`、
  `CaptureFailures >= 1`、带降级原因码与可读原因文本，且该帧被释放；
- ★ 证据里另外记录一条**未被预算检查覆盖的路径**：`details.rawPathNote` ——
  服务在 `processor == null` 时不做缓冲预算检查，会把后端给出的超限尺寸原样发布
  （`details.rawPathWithoutProcessor.PublishedFrames`）。这一条**不判失败**
  （预算检查由 `BackdropImageProcessor` 承担，接入后服务不再发布超限帧），
  但必须让集成者看见，请据此决定是否要在服务层再加一道尺寸闸门。

### 位图释放判定（泄漏证据）

`System.Drawing.Bitmap` 是 `sealed`，无法继承打点；net8.0 的 `System.Drawing.Image`
无参构造是 `protected internal`，跨程序集派生调用 `base()` 会被 C# 拒绝（已实测）。
因此探针用"还能不能再拿到 `Graphics` / `LockBits`"作为**释放判据**，
并在探针启动时**自检**（存活位图不得被判为已释放、已释放位图必须被识别）。
自检不通过时，所有"是否泄漏"的判定一律标为**不可判定**（写进
`details.releaseProbeNote`），绝不谎报"已释放"。

## 证据 JSON 结构

两份证据文件结构一致（`kind` 不同）：

```jsonc
{
  "kind": "appearance_m2_source_gate_probe_result",   // 运行时组为 appearance_m2_runtime_gate_probe_result
  "repoRoot": "…",
  "cases": [
    {
      "name": "M2 门槛#7 绘制路径无捕获调用",
      "passed": true,
      "failures": [],
      "details": {
        "scannedFileCount": 129,
        "scannedLineCount": 0,
        "hitCount": 0,
        "violationCount": 0,
        "hits": [ { "file": "…", "line": 1, "primitive": "…", "classification": "…", "isViolation": false, "code": "…" } ],
        "allowedDirectoryHits": [],
        "whitelistHits": [],
        "zeroHitScopes": [ { "scope": "…", "path": "…", "hitCount": 0, "passed": true } ]
      }
    },
    {
      "name": "M2 门槛#10 无新增 NuGet 依赖",
      "passed": true,
      "failures": [],
      "details": {
        "expectedPackages": [ { "id": "…", "version": "…" } ],
        "actualPackages": [ { "id": "…", "version": "…", "project": "…" } ]
      }
    }
  ],
  "allPassed": true
}
```

运行时组的用例条目同结构，`details` 里是各门槛的**可复核数字与字段名**，例如：

```jsonc
{
  "name": "M2 门槛#8 CPU 与经过时间分别计量",
  "passed": true,
  "failures": [],
  "details": {
    "gate": "M2 合同 §9 第 8 条",
    "cpuClockFields": [ "CpuMsTotal", "CpuMsPerFrameAvg" ],
    "elapsedClockFields": [ "ElapsedMsLast", "ElapsedMsP95", "ElapsedMsMax", "LastCaptureMs", "…", "MaxExcludeWindowMs" ],
    "metrics.CpuMsTotal": 0.0,
    "metrics.CpuMsPerFrameAvg": 0.0,
    "metrics.ElapsedMsLast": 10.2,
    "metrics.ElapsedMsP95": 10.4,
    "metrics.LastWaitUiMs": 5.1,
    "cpuClockNote": "CPU 口径记 0：Windows 进程 CPU 时钟粒度约 15.6 ms …… 不可判定"
  }
}
```

## 已知边界

- 零命中范围若被改名/删除，`details.zeroHitScopes[].exists` 会记为 `false`（**不判失败**，
  因为门槛考的是「有没有抓屏调用」；但集成者应据此复核范围是否仍然有效）。
- `LiteMonitor.Updater/LiteMonitor.Updater.csproj` 不在本门槛判定范围内
  （合同第 10 条针对主工程），但会出现在 `details.otherProjectsOutOfScope` 里供人工确认。
- 运行时门槛里凡涉及"位图是否已释放"的判定，都依赖释放探测的**自检结果**
  （`details.releaseProbeReliable` / `details.releaseProbeNote`）：自检不通过时该项标为
  「不可判定」，不据此判失败，也**不**据此宣称"无泄漏"。
- 运行时门槛的用例耗时约 5 秒（#5 需要连续产帧约 1.5 秒），每个等待都有上限，不会挂死。
- 本工程**不进 sln**，由集成者串行构建后运行；本探针自身不负责构建。
