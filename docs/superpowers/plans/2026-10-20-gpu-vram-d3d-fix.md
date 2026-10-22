# 多显卡显存串卡修复计划（方案 A：D3D 通道优先 + 每卡真实容量校正）

> **状态**：待实施（本文件只是计划，尚未改动任何源码）
> **编写时间**：2026-10-20
> **适用分支**：`chore/rebrand`（该分支已包含 `feat/multi-gpu` 的全部 19 个提交，见 §1.4）
> **文件性质**：独立新文件。编写本计划的过程**没有改动任何既有 plan 的任何一行，也没有改动任何既有源码行**。

---

## 0. 摘要

| 项目 | 结论 |
|---|---|
| **症状** | 多卡机器上，第 2 张及之后的显卡显示的是第 1 张卡的显存用量与容量 |
| **根因** | LHM 0.9.6 的 NVIDIA 显存传感器走的是 NVAPI 的**旧版全局接口**，多卡时所有卡拿到同一份数据（**上游缺陷，不是我们的映射错**） |
| **主修法** | 方案 A：每张卡的显存用量改用 **D3D 通道**（按适配器统计，每卡独立）；容量用**注册表里的每卡真实容量**校正 |
| **影响面** | **对 NVIDIA 多卡生效；对 AMD / Intel 零影响**（它们的厂商通道本就按卡独立，容量与注册表一致 → 判定不触发 → 行为完全不变） |
| **改动量** | 新增 2 个文件 + 改 2 个文件 + 新增 1 个测试文件 |
| **单卡用户** | **零行为变化**（判定不触发，继续用厂商通道，保留 CUDA/ROCm 独占显存的可见性） |
| **是否需要上游配合** | 不需要。不改 NuGet 包，不改 LHM 源码。 |

---

## 1. 背景与症状

### 1.1 用户报告的现象

真机（RTX 3080 Ti + RTX 3080）启用多卡监控后：

- 0 号卡（3080 Ti）显存：显示正常
- 1 号卡（3080）显存：**显示的是 3080 Ti 的数字**（例如"已用 6.3 GB / 共 12 GB"，而这张卡实际是 20 GB 且几乎没占用）

### 1.2 根因（一句话）

> LHM 0.9.6 里 NVIDIA 的 `GPU Memory Used / Total / Free` 三个传感器来自 NVAPI 的旧接口
> `NvAPI_GPU_GetMemoryInfo`（接口号 `0x774AA982`，参数类型是 **DisplayHandle**）。
> 这个旧接口在现代驱动上返回的是**全局的一份显存信息**，多张 NVIDIA 卡会拿到完全相同的数值。
> D3D 通道（`D3D Dedicated Memory Used`）则是**按 PCI 总线号逐卡匹配**的，每张卡各读各的，因此完全正确。

### 1.3 实测证据（真机，2026-10-20）

采集方式：复用 LiteMonitor 的 `HardwareMonitor` 单例 + `mon.UpdateAll()`，逐张卡逐传感器打印。

| 传感器 | 3080 Ti (`/gpu-nvidia/0`) | 3080 (`/gpu-nvidia/1`) | nvidia-smi 权威值 | 判定 |
|---|---|---|---|---|
| `GPU Memory Used` (SmallData) | 6495 | **6495** | Ti=6330 / 3080=**0** | ❌ 串卡 |
| `GPU Memory Total` (SmallData) | 12288 | **12288** | Ti=12288 / 3080=**20480** | ❌ 串卡 |
| `GPU Memory Free` (SmallData) | 5792 | **5792** | — | ❌ 串卡 |
| `GPU Memory` (Load) | 52.86 | **52.86** | — | ❌ 串卡（由上面两个派生） |
| `D3D Dedicated Memory Used` | 6305.19 | **4.11** | Ti=6330 / 3080=**0** | ✅ 每卡正确 |
| `D3D Shared Memory Used` | 423.31 | 0.52 | — | ✅ 每卡正确 |
| `GPU Core` (Temp / Clock / Load) | 60 / 480 / 35 | 36 / 0 / 0 | — | ✅ 每卡正确 |
| `GPU Memory` (Clock) | 810 | **405** | — | ✅ 每卡正确（**证明只有显存这一组是全局的**） |
| `GPU Fan 1/2`、`GPU Power`、`GPU Package`、`GPU Board Power`、`GPU Hot Spot`、`GPU Memory Junction`、`GPU PCIe Rx/Tx`、`D3D 3D` 等 | — | — | — | ✅ 全部每卡正确 |

**结论**：串卡范围精确到 4 个传感器，全部来自同一次 NVAPI 调用；同一张卡上其它 NVAPI 传感器（时钟、功耗、风扇）都是按卡正确的 → 说明问题**不是**"整个 NVAPI 句柄绑错"，而是**显存这一组接口本身在多卡下被全局化**。

### 1.4 分支拓扑（实施前必读）

```
master ────────────────┐
                        ├─ 8903e04  fix(gpu): 修复多显卡改造的 4 处 code review 发现
feat/multi-gpu ────────┤
                        └─ … rebrand 的若干提交 …
                             chore/rebrand   ← 当前所在分支 (编写时 HEAD = a07334a)
```

- `master` 与 `feat/multi-gpu` 指向**同一个 commit** `8903e04`（多卡改造已并入 master）
- `chore/rebrand` 从 `8903e04` 继续往前，因此**当前工作区已经包含全部多卡代码**
- ⇒ 实施显存修复**不需要切分支**，但建议在 `chore/rebrand` 的进行中改动落定后**另起分支**（如 `fix/gpu-vram-d3d`），避免与用户的改名工作冲突

### 1.5 与既有 plan 的关系

| 文件 | 本次是否改动 |
|---|---|
| `docs/superpowers/plans/2026-10-20-gpu-multi-card.md`（多卡改造 plan，3733 行） | **未改动任何一行** |
| `docs/superpowers/plans/2026-10-20-rebrand-compliance.md`（改名合规 plan） | **未改动任何一行** |
| 本文件 `2026-10-20-gpu-vram-d3d-fix.md` | 新建 |

实施阶段会触碰 `SensorMatcher.cs` / `HardwareValueProvider.cs`——这两个文件在多卡 plan 里也改过，但**多卡 plan 是文档不是代码**，不存在行冲突；真正的冲突风险是与"改名"分支的同文件改动，故建议错开。

---

## 2. 品牌兼容性结论（用户直接提问的回答）

### 2.1 结论速览

> **"别的品牌会不会导致这种情况"——分三层回答：**
>
> 1. **这种"串卡"现象本身**：在 LHM 0.9.6 上**只有 NVIDIA 会**（已实测复现）。AMD 的显存接口按适配器索引取值，结构上不会串；Intel 独显的显存数据本来就来自 D3D 通道，也按卡取值。
> 2. **Intel 有一个同型隐患**：它的 D3D 设备号在极端情况下会退化成"取第一个 Intel 设备"，双 Intel 独显可能串卡（本机无法实测，见 §2.4）。
> 3. **本方案（方案 A）对 AMD / Intel 零影响**：判定规则是"厂商通道报的容量是否等于该卡真实容量"，AMD/Intel 两者一致 → 判定不触发 → 继续走原路径。

### 2.2 逐品牌分析（基于 LHM v0.9.6 源码 + 真机实测）

| 品牌 / 类型 | LHM 0.9.6 的 `GPU Memory Used/Total/Free` 来源 | 是否按卡独立 | 多卡串卡风险 | D3D 通道 | 本方案影响 |
|---|---|---|---|---|---|
| **NVIDIA 独显** | NVAPI `NvAPI_GPU_GetMemoryInfo`（`0x774AA982`，传 **DisplayHandle**） | ❌ **实测否** | 🔴 **已实测复现** | 有 `Used`，**无 `Total`** | 切换到 D3D + 注册表容量 |
| **AMD 独显** | ADL `ADL2_Adapter_DedicatedVRAMUsage_Get(_context, _adapterInfo.AdapterIndex, …)`；容量用 `ADL2_Adapter_MemoryInfoX4_Get(_context, _adapterInfo.AdapterIndex, …)` | ✅ 是（**带 AdapterIndex**） | 🟢 结构上不会 | 有 `Used/Total/Free` | **零影响** |
| **Intel 独显 (Arc)** | 本身就是 D3D 通道：`D3DDisplayDevice.GetDeviceInfoByIdentifier(_d3dDeviceId, …)`，`_d3dDeviceId` 由 **vendor+device+BDF** 精确匹配 | ✅ 是 | 🟡 兜底路径有隐患（§2.4） | 有 `Used/Total/Free` | **零影响** |
| **Intel 核显** | 只有 `D3D Shared Memory Used/Free/Total` | ✅ 是 | 🟢 不会 | 有 | **零影响** |
| **虚拟显示适配器** | LHM 不枚举（`HardwareType` 不是 Gpu*） | — | — | — | 无影响 |

**为什么 AMD 免疫**——上游源码坐标（`LibreHardwareMonitorLib/Hardware/Gpu/AmdGpu.cs`）：

```csharp
// AmdGpu.cs:343-348  —— 关键: 第二个参数是 AdapterIndex, 每张卡自己的索引
if (AtiAdlxx.ADL_Method_Exists(nameof(AtiAdlxx.ADL2_Adapter_DedicatedVRAMUsage_Get)) &&
    AtiAdlxx.ADL2_Adapter_DedicatedVRAMUsage_Get(_context, _adapterInfo.AdapterIndex, out vramUsed) == AtiAdlxx.ADLStatus.ADL_OK)
{
    _memoryUsed.Value = vramUsed;
}
```

**为什么 NVIDIA 会**——上游源码坐标（`LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs`）：

```csharp
// NvidiaGpu.cs:705-724  —— 走 NVAPI 的旧版全局接口
if (_displayHandle != null)
{
    NvApi.NvMemoryInfo memoryInfo = GetMemoryInfo(out status);   // -> NvAPI_GPU_GetMemoryInfo(_displayHandle.Value, …)
    if (status == NvApi.NvStatus.OK)
    {
        uint free  = memoryInfo.CurrentAvailableDedicatedVideoMemory;
        uint total = memoryInfo.DedicatedVideoMemory;
        _memoryTotal.Value = total / 1024;
        _memoryFree.Value  = free  / 1024;
        _memoryUsed.Value  = (total - free) / 1024;
        _memoryLoad.Value  = ((float)(total - free) / total) * 100;
    }
}
```

```csharp
// NvidiaGpu.cs:1128-1143
private NvApi.NvMemoryInfo GetMemoryInfo(out NvApi.NvStatus status)
{
    ...
    status = NvApi.NvAPI_GPU_GetMemoryInfo(_displayHandle.Value, ref memoryInfo);
    return status == NvApi.NvStatus.OK ? memoryInfo : default;
}
```

- `NvApi.NvAPI_GPU_GetMemoryInfo` 的接口号是 `0x774AA982`（`Interop/NvApi.cs:86`），签名是
  `NvStatus NvAPI_GPU_GetMemoryInfo(NvDisplayHandle displayHandle, ref NvMemoryInfo)`（`Interop/NvApi.cs:147`）——**旧版 API 以 DisplayHandle 为入参**，现代驱动上返回全局的一份显存信息。
- 而同一张卡上 `NvAPI_GPU_GetAllClockFrequencies(_handle, …)` 传的是 **PhysicalGpuHandle**（`NvidiaGpu.cs:1158`），所以时钟等是按卡正确的 → 与 §1.3 的实测完全吻合。

**为什么 D3D 通道每卡正确**——上游源码坐标（`NvidiaGpu.cs:340-423`）：LHM 用 NVML 读到的 **PCI bus 号**去和注册表 `LocationInformation` 里的总线号比对，匹配上才绑定该 D3D 适配器：

```csharp
// NvidiaGpu.cs:405
if (pci.bus.ToString() == bus)
    isMatch = true;
```

→ D3D 适配器与显卡是**按 PCI 总线**一一对应的，所以 `D3D Dedicated Memory Used` 天然每卡独立。

### 2.3 为什么这次的现象在旧版本也存在（不是多卡改造引入的）

- 旧版只有全局 Key `GPU.VRAM.Used`，遍历顺序是 3080 Ti 先 → 旧版**一直显示 3080 Ti 的显存**；
- 用户旧版 `PreferredGpu = ''`（自动模式）→ 监控的恰好就是 3080 Ti → **数字"看起来对"，从来没暴露过这个 bug**；
- 旧版若把「首选显卡」手动选成 3080，同样会显示 7.37 GB（= 3080 Ti 的数字）——**同一个 bug，只是没人触发**。

> 顺带修正多卡 plan（Task 14 Step 4）里的一处失实预期：
> "改造前两张卡会都显示 3080 的 0%" **是错的**——实际两张卡都会显示 **3080 Ti** 的数值。

### 2.4 Intel 的同型隐患（本机无法实测，需在文档中标注）

`IntelDiscreteGpu.UpdateMemorySensors()` 在 `_d3dDeviceId` 为空时的兜底：

```csharp
// IntelDiscreteGpu.cs:518-538
if (string.IsNullOrEmpty(_d3dDeviceId))
{
    string[] deviceIdentifiers = D3DDisplayDevice.GetDeviceIdentifiers();
    foreach (string deviceId in deviceIdentifiers)
    {
        if (deviceId.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) != -1)
        {
            if (D3DDisplayDevice.GetDeviceInfoByIdentifier(deviceId, out … testInfo))
            {
                if (testInfo.GpuDedicatedLimit > 0 && !testInfo.Integrated)
                {
                    _d3dDeviceId = deviceId;   // ← 取"第一个"符合条件的, 不区分是哪张卡
                    break;
                }
            }
        }
    }
}
```

正常路径 `GetD3DDeviceId()`（`IntelDiscreteGpu.cs:197-227`）是按 **vendor + device + BDF** 精确匹配的，没问题；**只有这条兜底路径**会在"双 Intel 独显 + 拿不到 BDF"时退化成取第一个 → 潜在串卡。

**本机无法验证**：这台机器上有一张 Intel Arc A770，但 PnP 状态为 `Unknown`（设备已移除/禁用），LHM 枚举不到，WMI 也不列出，因此 Intel 路径**只能靠源码推断**，无法实测。→ 记入 §7 的"使用限制"文档。

### 2.5 结论对本方案的影响

方案 A 的判定规则是**"厂商通道报的容量 vs 该卡真实容量"**，而不是"按品牌硬编码"：

- NVIDIA 3080：厂商报 12288，真实 20480 → **不一致 → 切换** ✅
- NVIDIA 3080 Ti：厂商报 12288，真实 12288 → **一致 → 不切换** ✅（保留 CUDA 可见性，行为与旧版一致）
- AMD 任意卡：厂商报值 = 真实容量 → **一致 → 不切换** ✅（AMD 完全不受影响）
- Intel 独显：厂商报值来自 D3D = 真实容量 → **一致 → 不切换** ✅

→ **不需要为不同品牌写分支代码**，规则天然跨品牌且对 AMD/Intel 零影响。

---

## 3. 方案设计

### 3.1 候选方案对比

| 方案 | 做法 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| **A（本计划）** | 容量一致性判定 + D3D 通道取用量 + 注册表容量 | 每卡正确；单卡零行为变化；AMD/Intel 零影响；不依赖上游 | 需要读一次注册表 | ✅ **采纳** |
| **A'** | 无条件把显存改用 D3D 通道 | 最简单 | 单卡用户也会被改口径 → **丢失 CUDA/ROCm 独占显存的可见性**；AMD 也会被改 | ❌ 过度 |
| **B** | LiteMonitor 自带 NVML P/Invoke (`nvmlDeviceGetMemoryInfo`) | NVIDIA 上最权威、含 CUDA | 只对 NVIDIA 有效；新增原生依赖；**还要解决 NVML index ↔ LHM 序号的映射**（本机两者顺序相反：NVML 0=3080，LHM 0=3080 Ti） | 🟡 备选（若日后要支持"计算型显存"口径可叠加） |
| **C** | 上游 LHM 提 issue / 换版本 | 最彻底 | 不受我们控制，0.9.6 短期内无解 | ❌ |
| **D** | 只在 UI 层打补丁 | 最省事 | 历史记录 / 网页端 / 曲线窗口全都还是错的 | ❌ |

### 3.2 方案 A 的数据流

```
                     ┌──────────────────────── 每张卡的三个来源 ────────────────────────┐
                     │                                                                │
  LHM 厂商通道       │  GPU Memory Used      GPU Memory Total     GPU Memory Free     │  ← 可能全局化 (NVIDIA)
  (SensorMap)        │  → GPU.{i}.VRAM.Used  → GPU.{i}.VRAM.Total  → GPU.{i}.VRAM.Free
                     │                                                                │
  LHM D3D 通道       │  D3D Dedicated Memory Used  D3D Dedicated Memory Total        │  ← 必按卡独立
  (SensorMap)        │  → GPU.{i}.VRAM.D3D.Used → GPU.{i}.VRAM.D3D.Total
                     │                                                                │
  Windows 注册表     │  Class\{4d36e968-…}\<NNNN>\HardwareInformation.qwMemorySize  │  ← 每张适配器各一份, 真值
  (新增)             │  + DriverDesc (卡名)                                         │
                     └────────────────────────────────────────────────────────────────┘
                                              │
                                              ▼
                          GpuVramCapacity.GetTotalMB(卡全名)  →  真实容量 (MB)
                                              │
                                              ▼
              ┌───────────────────────────────────────────────────────────┐
              │  判定: |厂商Total − 真实Total| > max(1MB, 真实Total×1%) ?   │
              │     是 ⇒ 厂商通道在报告别的卡 ⇒ 用 D3D 通道 + 真实容量      │
              │     否 ⇒ 厂商通道可信 ⇒ 用厂商通道 (行为与旧版完全一致)     │
              └───────────────────────────────────────────────────────────┘
                                              │
                                              ▼
                    HardwareValueProvider.GetGpuVramPercent(key)
                        → 百分比 (used / total × 100)
                        → 同时把"校正后的 total"写进 Settings.DetectedGpuVramTotalGBByKey
                                              │
                                              ▼
                    MetricUtils.GetGpuVramTotalGB(key) → 容量模式显示
```

### 3.3 判定规则（精确表述）

设某张卡的：

- `T_vendor` = LHM 厂商通道报的 `GPU Memory Total`（MB）
- `T_real`   = 注册表查到的**该卡**真实容量（MB）
- `T_d3d`    = LHM D3D 通道的 `D3D Dedicated Memory Total`（MB，**只有 AMD/Intel 有，NVIDIA 没有**）
- `A_all`    = 注册表里**全部**显示适配器的容量集合（不只是被监控的卡）

**第一道门：这张卡有没有资格参与判定？**（三条全过才有资格）

| 守卫 | 判据 | 挡掉什么 |
|---|---|---|
| **G1 独显守卫** | `HardwareRules.UsesDedicatedVramName(显卡全名)` == true | Intel 核显（UHD/Iris/Arc 核显）、`AMD Radeon(TM) Graphics` 这类无型号核显 |
| **G2 口径守卫** | 该卡 `GPU.VRAM.Used` / `GPU.VRAM.Total` 对应的 LHM 传感器名**不含 "Shared"** | 核显的共享显存口径（`D3D Shared Memory *`） |
| **G3 容量下限** | `T_real >= 512MB` | 注册表里的 UMA 窗口 / 无意义小值 |

> **为什么必须有 G1/G2**：笔记本上"Intel 核显 + NVIDIA 独显"是极常见的组合。
> 核显的显存走 `D3D Shared Memory *`（上限通常几 GB），而注册表里核显记的是 **UMA 窗口大小**
> （通常几百 MB～2 GB）——**这两个数本来就不该相等**。拿它当基准会把核显误判成"串卡"，
> 然后把核显的显存切到 `D3D Dedicated Memory Used`（核显的专用显存切分通常只有几十 MB），
> 结果核显显存恒显 0% —— **比原 bug 更糟**。所以核显一律不参与判定，行为保持原样。

**第二道门：厂商通道到底可信吗？**（G1/G2/G3 都过了才判定）

```
厂商通道不可信  <=>  ( |T_vendor - T_real| > max(1MB, T_real x 1%) )              # 本卡对不上
                 AND ( 存在 t 属于 A_all : |T_vendor - t| <= max(1MB, t x 1%) )   # 且命中别的卡
```

> **为什么必须要有第二段的"交叉证据"**：
> 注册表 `qwMemorySize` 记的是**驱动可见容量**，不一定等于物理容量。
> 本机实测 Intel Arc A770：注册表 16256 MB，而物理 16 GB = 16384 MB，**差 0.78%**，
> 已经贴在 1% 容差边缘；驱动预留显存的笔记本卡差得更多。
> 如果只看"对不上"就判定串卡，这些卡会被误切 -> 丢掉 CUDA/ROCm 可见性（单卡笔记本上是常态）。
> 加上"必须命中**另一块**适配器的容量"之后：
> 误切场景里 `T_vendor` 命中的是**本卡自己**的量，命中不了别的卡 -> 不判定 -> 维持旧行为 OK
> 真串卡场景里 `T_vendor` 正是**另一张卡**的容量 -> 命中 -> 判定 OK
>
> **`A_all` 必须取注册表里的全部适配器，不能只取被监控的卡**——
> 否则"只监控 1 张卡且正好选到串的那张"会因为"另一张卡不在监控集合里"而失去证据。

**择源**：

```
厂商通道不可信时：
    used  = D3D.Dedicated.Used            (若 D3D 通道缺失 => 整体无值, 不混算)
    total = T_real ?? T_d3d ?? T_vendor
厂商通道可信时：
    used  = 厂商 Used ?? D3D.Dedicated.Used
    total = 厂商 Total ?? T_d3d ?? T_real
```

> 厂商通道不可信时**绝不**用"别的卡的 used"去除"本卡的 total"——那样会算出 6495/20480 = 31.7%
> 这种又错又怪的值，比原来更让人误解。D3D 通道缺失就宁可无值。
### 3.4 Key 命名约定（新增内部 Key，不对用户暴露）

| Key | 来源传感器 | 用途 |
|---|---|---|
| `GPU.{i}.VRAM.Used` | `GPU Memory Used` | 厂商通道（现状不变） |
| `GPU.{i}.VRAM.Total` | `GPU Memory Total` | 厂商通道（现状不变） |
| `GPU.{i}.VRAM.Free` | `GPU Memory Free` | 厂商通道（现状不变） |
| `GPU.{i}.VRAM.Load` | `GPU Memory` (Load) | 厂商通道（现状不变） |
| **`GPU.{i}.VRAM.D3D.Used`** | `D3D Dedicated Memory Used` | **新增**：D3D 通道用量 |
| **`GPU.{i}.VRAM.D3D.Total`** | `D3D Dedicated Memory Total` | **新增**：D3D 通道容量（NVIDIA 无） |
| **`GPU.{i}.VRAM.D3D.Free`** | `D3D Dedicated Memory Free` | **新增**：D3D 通道空闲（备用） |

这些 Key 落在既有的 `SensorMap` 字典里，但：

- `GpuItemSync` 只生成 `GPU.{i}.{Load,Temp,Clock,Power,Fan,VRAM}` 六种用户监控项 → **不会出现在设置页/监控项列表**
- `LiteWebServer` 遍历的是"已配置监控项"而非原始传感器表 → **不会出现在网页端**
- `MetricUtils.GetType("GPU.1.VRAM.D3D.Used")` 会判成 `Memory`，但该 Key 永不被渲染 → 无影响
- `GpuKey.TryParse("GPU.1.VRAM.D3D.Used")` → `(index=1, metric="VRAM.D3D.Used")`，`Build` 可逆 → **多卡序号机制天然支持**

> `D3D Shared Memory *` **不拆新 Key**，继续走既有的 `GPU.VRAM.Used/Total`（核显口径不变），
> 避免动到核显行为。

---

## 4. 任务清单

| Task | 内容 | 触碰文件 | 风险 |
|---|---|---|---|
| 1 | 新增 `GpuVramCapacity`：读注册表拿每卡真实显存容量 | **新增** `src/System/HardwareServices/GpuVramCapacity.cs` | 低 |
| 2 | `SensorMatcher` 拆出 D3D 专用显存 Key；抽出可测的 `MatchGpu` | 改 `src/System/HardwareServices/SensorMatcher.cs` | 中 |
| 3 | 新增 `GpuVramSource`：纯函数择源逻辑 | **新增** `src/System/HardwareServices/GpuVramSource.cs` | 低 |
| 4 | `HardwareValueProvider.GetGpuVramPercent` 接入 Task 1/3；修正单位换算隐患 | 改 `src/System/HardwareServices/HardwareValueProvider.cs` | 中 |
| 4.5（可选） | `GPU.VRAM.Load` 排除 `GPU Memory Controller` | 改 `src/System/HardwareServices/SensorMatcher.cs` | 低 |
| 5 | 回归测试 | **新增** `tests/LiteMonitor.Tests/GpuVramTests.cs` | 低 |
| 6 | 使用限制文档 + 真机验收清单 | 改文档（新增章节） | 低 |

**建议分支**：`fix/gpu-vram-d3d`（从 `chore/rebrand` 拉），每个 Task 一次提交。

---

### Task 1：新增 `GpuVramCapacity`（每卡真实显存容量）

**目标**：给每张被监控的显卡查到"它到底有多少显存"，作为判定厂商通道是否串卡的基准，并作为串卡时的正确分母。

**新增文件**：`src/System/HardwareServices/GpuVramCapacity.cs`
（行尾：工作区 CRLF，仓库 blob 为 LF —— 本仓库 `core.autocrlf = true`，见 §5.2）

**数据来源**（真机已验证）：

```
HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\<NNNN>
    DriverDesc                          → 适配器名（与 LHM 的 hw.Name 对得上）
    HardwareInformation.qwMemorySize    → 显存字节数（QWORD）
```

真机采集结果（2026-10-20）：

| 注册表键 | DriverDesc | qwMemorySize | 折算 |
|---|---|---|---|
| `0001` | NVIDIA GeForce RTX 3080 | 21474836480 | 20480 MB |
| `0005` | NVIDIA GeForce RTX 3080 Ti | 12884901888 | 12288 MB |
| `0003` | Intel(R) Arc(TM) A770 Graphics | 17045651456 | 16256 MB（本机该卡已禁用，不参与匹配） |
| `0000/0004/0006/0008` | 各种虚拟显示适配器 | （无此值） | 跳过 |

**代码骨架**（新增文件，UTF-8，**CRLF**，与 `src/**` 其余文件一致）：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 每张显卡的真实显存总容量 (从 Windows 注册表读取)。
    ///
    /// 为什么需要它:
    ///   LHM 0.9.6 的 NVIDIA 显存传感器走的是 NVAPI 的旧版全局接口
    ///   (NvAPI_GPU_GetMemoryInfo, 接口号 0x774AA982, 参数是 DisplayHandle),
    ///   多卡机器上多张 NVIDIA 卡会拿到同一份显存数据 —— 也就是"第 2 张卡显示第 1 张卡的显存"。
    ///   注册表里每个显示适配器都单独记着自己的显存容量, 可以当作"这张卡到底有多少显存"的参照:
    ///     1) 用它判断厂商通道是不是在报告别的卡 (容量命中另一块适配器就是串了)
    ///     2) 串了的时候用它当正确的分母
    ///
    /// 数据来源:
    ///   HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\&lt;NNNN&gt;
    ///     DriverDesc                      -> 适配器名 (与 LHM 的 hw.Name 对得上)
    ///     HardwareInformation.qwMemorySize -> 显存字节数 (QWORD, 真机实测为 Int64)
    ///
    /// 注意: 注册表记的是"驱动可见容量", 不一定等于物理容量
    ///   (实测 Intel Arc A770 = 16256 MB, 物理 16 GB = 16384 MB, 差 0.78%),
    ///   所以判定时必须配"交叉证据", 不能只看"对不上"。
    /// </summary>
    public static class GpuVramCapacity
    {
        private const string ClassSubKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private const long MinPlausibleBytes = 64L * 1024 * 1024;   // 小于 64MB 的当噪声丢掉

        private static readonly object _lock = new object();
        private static Dictionary<string, float> _byNormalizedName = new(StringComparer.Ordinal);   // 归一化名 -> 容量 (MB)
        private static List<float> _allTotalsMB = new();                                          // 全部适配器的容量 (MB)
        private static bool _loaded;

        /// <summary>
        /// 归一化显卡名, 便于和注册表 DriverDesc 对比。
        /// 去掉 (R)/(TM)/®/™ , 压缩空白, 忽略大小写。
        /// "NVIDIA GeForce RTX 3080" / "NVIDIA GeForce RTX 3080 "  ->  同一个 key
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string n = name
                .Replace("(R)", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("®", " ")
                .Replace("™", " ");
            n = string.Join(" ", n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return n.ToUpperInvariant();
        }

        /// <summary>
        /// 取某张卡 (用 LHM 的 hw.Name) 的真实显存容量, 单位 MB。找不到返回 null。
        /// 同名多卡 (同型号) 时, 只要各条记录的容量一致就返回该值 —— 同型号容量本来就一样。
        /// 同名但容量不一致 (无法判定) 时返回 null, 宁可不用也不猜。
        /// </summary>
        public static float? GetTotalMB(string gpuName)
        {
            string key = NormalizeName(gpuName);
            if (key.Length == 0) return null;

            EnsureLoaded();
            lock (_lock)
            {
                return _byNormalizedName.TryGetValue(key, out float mb) ? mb : (float?)null;
            }
        }

        /// <summary>
        /// 注册表里**全部**显示适配器的显存容量 (MB)。
        /// 用来做"串卡交叉证据": 厂商通道报的容量如果命中了这里的某个值,
        /// 而且那个值不是本卡自己的容量, 就说明它在报告别的卡。
        /// 必须取全部适配器 (而不是只取被监控的卡), 否则"只监控 1 张卡且正好选到串的那张"
        /// 会因为"另一张卡不在监控集合里"而失去证据。
        /// </summary>
        public static IReadOnlyList<float> GetAllTotalsMB()
        {
            EnsureLoaded();
            lock (_lock)
            {
                return _allTotalsMB.ToArray();
            }
        }

        private static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
                _byNormalizedName.Clear();
                _allTotalsMB.Clear();

                try
                {
                    using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using RegistryKey? classKey = root.OpenSubKey(ClassSubKey);
                    if (classKey == null) return;

                    // 先按名字收集所有候选 (同型号可能有多条)
                    var candidates = new Dictionary<string, List<float>>(StringComparer.Ordinal);

                    foreach (string subName in classKey.GetSubKeyNames())
                    {
                        // ★ 这个类键下不只有适配器, 还有 "Configuration" / "Properties" 等系统子键。
                        //   "Properties" 是受保护的系统键, OpenSubKey 会抛 UnauthorizedAccessException
                        //   ("Requested registry access is not allowed.") —— 真机已实测。
                        //   所以这里既只认纯数字的适配器键, 又给每个子键单独兜一层异常,
                        //   免得一个打不开的子键把整张表都废掉 (外层 catch 会清空全部结果)。
                        if (!IsAllDigits(subName)) continue;

                        RegistryKey? adapterKey;
                        try
                        {
                            adapterKey = classKey.OpenSubKey(subName);
                        }
                        catch
                        {
                            continue;
                        }
                        if (adapterKey == null) continue;

                        using (adapterKey)
                        {
                            string? desc = adapterKey.GetValue("DriverDesc") as string;
                            object? sizeObj = adapterKey.GetValue("HardwareInformation.qwMemorySize");
                            if (string.IsNullOrWhiteSpace(desc) || sizeObj == null) continue;

                            if (!TryToInt64(sizeObj, out long bytes) || bytes < MinPlausibleBytes) continue;

                            string norm = NormalizeName(desc);
                            if (norm.Length == 0) continue;

                            float mb = (float)(bytes / (1024.0 * 1024.0));
                            _allTotalsMB.Add(mb);

                            if (!candidates.TryGetValue(norm, out List<float>? list))
                            {
                                list = new List<float>();
                                candidates[norm] = list;
                            }
                            list.Add(mb);
                        }
                    }

                    foreach (var kv in candidates)
                    {
                        // 同名多条: 容量一致才敢用
                        float min = kv.Value.Min();
                        float max = kv.Value.Max();
                        if (Math.Abs(max - min) < 0.5f) _byNormalizedName[kv.Key] = min;
                    }
                }
                catch
                {
                    // 读不到就当没有, 判定规则会退化成"厂商通道可信", 即维持旧行为
                    _byNormalizedName = new Dictionary<string, float>(StringComparer.Ordinal);
                    _allTotalsMB = new List<float>();
                }
            }
        }

        /// <summary>是否全为数字 (用来筛出类键下的适配器子键, 排除 Configuration / Properties)</summary>
        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static bool TryToInt64(object value, out long result)
        {
            switch (value)
            {
                case long l:
                    result = l;
                    return true;
                case int i:
                    result = i;
                    return true;
                case uint ui:
                    result = ui;
                    return true;
                case ulong ul when ul <= long.MaxValue:
                    result = (long)ul;
                    return true;
                case byte[] bytes when bytes.Length == 8:
                    result = BitConverter.ToInt64(bytes, 0);
                    return true;
                default:
                    return long.TryParse(
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out result);
            }
        }
    }
}
```

**注意**：`Microsoft.Win32` 在 `net8.0-windows` 下无需额外包引用。

**验收标准**：

- [ ] 在真机上，`GpuVramCapacity.GetTotalMB("NVIDIA GeForce RTX 3080")` 返回 `20480`
- [ ] `GetTotalMB("NVIDIA GeForce RTX 3080 Ti")` 返回 `12288`
- [ ] `GetTotalMB("不存在的卡")` 返回 `null`
- [ ] `GetTotalMB("")` / `null` 返回 `null`，不抛异常
- [ ] 读不到注册表（异常路径）时不抛异常，退化为"无校正值"

**提交**：

```
feat(gpu): 新增按注册表读取每卡真实显存容量的工具
```

---

### Task 2：`SensorMatcher` 拆出 D3D 专用显存 Key

**目标**：让 D3D 通道的显存数据**不被厂商通道顶掉**，而是各自独立成 Key，交给取值层择一。

**现状问题**（`src/System/HardwareServices/SensorMatcher.cs:71-110`）：

```csharp
if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
{
    ...
    if (s.SensorType == SensorType.SmallData && (Has(name, "memory") || Has(name, "dedicated") || Has(name, "shared")))
    {
        bool isShared = Has(name, "shared");
        bool isDedicated = Has(name, "dedicated");
        bool isGenericMem = !isShared && !isDedicated && Has(name, "memory");

        if (preferShared)
        {
            if (isShared && Has(name, "used")) return "GPU.VRAM.Used";
            if (isShared && Has(name, "total")) return "GPU.VRAM.Total";
            if (isGenericMem && Has(name, "used")) return "GPU.VRAM.Used";
        }
        else
        {
            if (!isShared)
            {
                if (Has(name, "used")) return "GPU.VRAM.Used";     // ← "D3D Dedicated Memory Used" 也命中这里
                if (Has(name, "total")) return "GPU.VRAM.Total";
            }
        }
    }
    ...
}
```

`"D3D Dedicated Memory Used"` 命中 `GPU.VRAM.Used`，然后被 `SensorMap.Rebuild` 的冲突规则顶掉：

```csharp
// src/System/HardwareServices/SensorMap.cs:283-290
bool existingIsD3D = existing.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
bool newIsD3D = s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
if (existingIsD3D && !newIsD3D)
{
    newMap[finalKey] = s;      // ← 正确的 D3D 被换成串卡的厂商值
}
```

**改动**：

1. 在 GPU 分支里**先**处理 `D3D Dedicated Memory *`，给它独立 Key；
2. 通用显存分支**排除**名字里带 `d3d` 的传感器（否则又会撞车）；
3. 把 GPU 分支抽成可单测的 `MatchGpu(name, sensorType, preferShared)`。

**改动后的代码**（`src/System/HardwareServices/SensorMatcher.cs`）：

```csharp
            // --- GPU ---
            if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                string? gpuKey = MatchGpu(name, s.SensorType, HardwareRules.ShouldUseSharedMemory(hw));
                if (gpuKey != null) return gpuKey;
            }
```

```csharp
        /// <summary>
        /// GPU 传感器匹配 (抽成独立方法, 便于单元测试, 不需要构造 IHardware/ISensor 桩)。
        ///
        /// ★★★ [显存修复] D3D 通道单独建 Key ★★★
        /// D3D 通道按适配器 (adapter) 统计, 每张卡各读各的, 不会串卡;
        /// 厂商通道 (NVAPI / ADL) 在 LHM 0.9.6 的 NVIDIA 实现上会被"全局化", 多卡时多张卡拿到同一份数据。
        /// 所以这里把 D3D 专用显存拆到独立 Key, 由取值层 (GpuVramSource) 按规则择一,
        /// 而不是像以前那样让厂商值把 D3D 值顶掉 (SensorMap 的冲突规则是"厂商优先")。
        /// </summary>
        public static string? MatchGpu(string name, SensorType sensorType, bool preferShared)
        {
            if (sensorType == SensorType.Load && (Has(name, "core") || Has(name, "d3d 3d"))) return "GPU.Load";
            if (sensorType == SensorType.Temperature && (Has(name, "core") || Has(name, "hot spot") || Has(name, "soc") || Has(name, "vr"))) return "GPU.Temp";

            // ★ [显存修复] D3D 专用显存 -> 独立 Key
            //   注意: 这里只拦 "D3D Dedicated Memory *"。核显的 "D3D Shared Memory *"
            //   仍然要走下面的通用分支 (它就是核显 GPU.VRAM.Used 的来源), 不能一起拦掉,
            //   否则核显的显存映射会整个消失。
            bool isD3DDedicated = Has(name, "d3d") && Has(name, "dedicated");
            if (sensorType == SensorType.SmallData && isD3DDedicated)
            {
                if (Has(name, "used")) return "GPU.VRAM.D3D.Used";
                if (Has(name, "total")) return "GPU.VRAM.D3D.Total";
                if (Has(name, "free")) return "GPU.VRAM.D3D.Free";
            }

            // VRAM Logic (简化且准确)
            // 1. 根据硬件规则判断是否应该优先找共享内存 (核显)
            if (sensorType == SensorType.SmallData
                && (Has(name, "memory") || Has(name, "dedicated") || Has(name, "shared")))
            {
                bool isShared = Has(name, "shared");
                bool isDedicated = Has(name, "dedicated");
                // Generic "Memory Used" (Vendor sensors often just say "Memory Used")
                bool isGenericMem = !isShared && !isDedicated && Has(name, "memory");

                if (preferShared)
                {
                    // Integrated: Only accept Shared Memory
                    // ★ 核显的 "D3D Shared Memory Used" 就是从这里进 GPU.VRAM.Used 的
                    if (isShared && Has(name, "used")) return "GPU.VRAM.Used";
                    if (isShared && Has(name, "total")) return "GPU.VRAM.Total";

                    // Fallback: 如果没有 Shared Memory (比如某些老驱动)，但有 Generic Memory，勉强用一下
                    // 但必须排除 Dedicated (核显通常没有专用显存，如果有也是假的或者极小)
                    if (isGenericMem && Has(name, "used")) return "GPU.VRAM.Used";
                }
                else
                {
                    // Discrete: Accept Dedicated or Generic Memory (Block Shared)
                    // ★ D3D 专用的已经在上面单独建 Key 了, 这里跳过, 避免被当成厂商口径
                    if (!isShared && !isD3DDedicated)
                    {
                        if (Has(name, "used")) return "GPU.VRAM.Used";
                        if (Has(name, "total")) return "GPU.VRAM.Total";
                    }
                }
            }

            // 显存占用率: 只认 "GPU Memory", 不要 "GPU Memory Controller" (那是显存控制器利用率, 不是占用率)
            if (sensorType == SensorType.Load && Has(name, "memory") && !Has(name, "controller"))
                return "GPU.VRAM.Load";

            return null;
        }
```

> 实现时把 `s_isLoad` / `s_isTemp` 直接写成 `sensorType == SensorType.Load` / `== SensorType.Temperature`（上面只是为了示意）。

**为什么这样改之后 `SensorMap` 的冲突规则不用动**：

- `D3D Dedicated Memory Used` 现在映射到 `GPU.VRAM.D3D.Used`，**不再和** `GPU.VRAM.Used` 抢同一个 Key → 原来的"厂商顶掉 D3D"规则对显存不再生效
- `GPU.Load` 仍由 `"GPU Core"`（厂商）与 `"D3D 3D"`（D3D）竞争 → 原规则"厂商优先"继续让 `GPU Core` 胜出 → **GPU 使用率口径不变** ✅
- → **不动 `SensorMap.cs`**，改动面更小、回归风险更低

**验收标准**：

- [ ] `MatchGpu("D3D Dedicated Memory Used", SmallData, false)` == `"GPU.VRAM.D3D.Used"`
- [ ] `MatchGpu("D3D Dedicated Memory Total", SmallData, false)` == `"GPU.VRAM.D3D.Total"`
- [ ] `MatchGpu("GPU Memory Used", SmallData, false)` == `"GPU.VRAM.Used"`（**不变**）
- [ ] `MatchGpu("GPU Memory Total", SmallData, false)` == `"GPU.VRAM.Total"`（**不变**）
- [ ] `MatchGpu("D3D Shared Memory Used", SmallData, true)` == `"GPU.VRAM.Used"`（核显**不变**）
- [ ] `MatchGpu("D3D 3D", Load, false)` == `"GPU.Load"`（**不变**）
- [ ] `MatchGpu("GPU Core", Temperature, false)` == `"GPU.Temp"`（**不变**）
- [ ] `MatchGpu("GPU Memory Controller", Load, false)` → 见 Task 4.5

**提交**：

```
fix(gpu): 显存匹配拆出 D3D 通道独立 Key，避免被厂商通道顶掉
```

---

### Task 3：新增 `GpuVramSource`（纯函数择源）

**目标**：把"用厂商通道还是 D3D 通道"的判定做成**无副作用、可单测**的纯函数。

**新增文件**：`src/System/HardwareServices/GpuVramSource.cs`

```csharp
using System;
using System.Collections.Generic;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 显存数据来源的判定。
    ///
    /// 背景:
    ///   LHM 0.9.6 的 NVIDIA 显存传感器来自 NVAPI 旧版全局接口, 多卡时多张卡拿到同一份数据。
    ///   D3D 通道按适配器统计, 每卡独立, 但口径与厂商通道略有差异 (且不含纯计算型占用)。
    ///   因此不能无条件切到 D3D —— 单卡场景下厂商通道是对的, 切过去反而丢失 CUDA/ROCm 独占显存的可见性。
    ///
    /// 判定依据 ("串卡"要同时满足两条):
    ///   1) 厂商通道报的容量与本卡真实容量对不上;
    ///   2) 且这个报出来的容量**命中了系统里另一块适配器的容量**。
    /// 第 2 条是必须的: 注册表记的是驱动可见容量, 未必等于物理容量
    ///   (实测 Intel Arc A770 = 16256 MB vs 物理 16384 MB, 差 0.78%),
    ///   只看第 1 条会把这类卡误判成串卡, 白白丢掉 CUDA/ROCm 可见性。
    /// </summary>
    public static class GpuVramSource
    {
        /// <summary>容量比较容差: 绝对 1MB 或相对 1%, 取大者。</summary>
        private const float AbsToleranceMB = 1f;
        private const float RelTolerance = 0.01f;

        /// <summary>
        /// 容量下限: 低于这个值的多半是核显的 UMA 窗口或无意义值, 不拿来当基准。
        /// </summary>
        public const float MinPlausibleTotalMB = 512f;

        /// <summary>两个容量是否算"同一个"。</summary>
        public static bool SameCapacity(float a, float b)
        {
            float diff = Math.Abs(a - b);
            float tolerance = Math.Max(AbsToleranceMB, Math.Max(a, b) * RelTolerance);
            return diff <= tolerance;
        }

        /// <summary>
        /// 厂商通道报的容量是否与该卡真实容量不符 (即厂商通道在报告别的卡)。
        /// 任何一项缺失 / 不可信都判为"可信" —— 缺证据时维持旧行为。
        /// </summary>
        public static bool IsVendorChannelForeign(
            float? vendorTotalMB,
            float? realTotalMB,
            IReadOnlyList<float> allAdapterTotalsMB)
        {
            if (!vendorTotalMB.HasValue || !realTotalMB.HasValue) return false;
            if (vendorTotalMB.Value <= 0 || realTotalMB.Value <= 0) return false;

            // ★ 真实容量太小 -> 多半是核显的 UMA 窗口, 不参与判定
            if (realTotalMB.Value < MinPlausibleTotalMB) return false;

            // 本卡对得上 -> 可信
            if (SameCapacity(vendorTotalMB.Value, realTotalMB.Value)) return false;

            // 对不上 -> 必须命中"另一块适配器"的容量才算串卡。
            // (本卡自己的容量已经排除在外了: 上面 SameCapacity 为 false, 所以它命中不了)
            if (allAdapterTotalsMB == null) return false;
            for (int i = 0; i < allAdapterTotalsMB.Count; i++)
            {
                float t = allAdapterTotalsMB[i];
                if (t <= 0) continue;
                if (SameCapacity(vendorTotalMB.Value, t)) return true;
            }

            return false;
        }

        /// <summary>
        /// 选出该卡最终使用的 (used, total), 单位 MB。
        /// useD3D 由调用方决定 (它要先把 G1/G2/G3 三道守卫过一遍)。
        /// </summary>
        public static (float? used, float? total) Select(
            float? vendorUsedMB,
            float? vendorTotalMB,
            float? d3dUsedMB,
            float? d3dTotalMB,
            float? realTotalMB,
            bool useD3D)
        {
            if (useD3D)
            {
                // 厂商通道在报告别的卡 -> 改用 D3D 通道, 容量用注册表真值
                // ★ D3D 通道缺失时宁可无值: 绝不能拿"别的卡的 used"去除"本卡的 total",
                //   那样会算出 6495/20480 = 31.7% 这种又错又怪的值, 比原来更让人误解。
                if (!d3dUsedMB.HasValue) return (null, realTotalMB ?? d3dTotalMB ?? vendorTotalMB);
                float? used = d3dUsedMB;
                float? total = realTotalMB ?? d3dTotalMB ?? vendorTotalMB;
                return (used, total);
            }

            // 厂商通道可信 -> 维持原口径, D3D 只作兜底
            float? usedNormal = vendorUsedMB ?? d3dUsedMB;
            float? totalNormal = vendorTotalMB ?? d3dTotalMB ?? realTotalMB;
            return (usedNormal, totalNormal);
        }
    }
}
```

**配套：给 `HardwareRules` 加一个独显判定**

在 `src/System/HardwareServices/HardwareRules.cs` 里新增（与该文件既有的 `IsDiscreteArc` 同一套语义）：

```csharp
        /// <summary>
        /// 这张卡的显存是不是走"专用显存"口径 (即独显)。
        ///
        /// 用途: 显存串卡判定只对独显生效。
        ///   核显在注册表里记的是 UMA 窗口大小, 与它实际使用的共享显存上限本来就不该相等,
        ///   拿它当基准会把核显误判成"串卡", 再切到几乎没有的 D3D 专用显存口径,
        ///   结果核显显存恒显 0% —— 比原 bug 更糟。
        /// </summary>
        public static bool UsesDedicatedVramName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            // Intel: 只认 Arc A/B/Pro 独显; UHD / Iris / Arc 核显 都不算
            if (Has(name, "Intel")) return IsDiscreteArc(name);

            // AMD: "AMD Radeon(TM) Graphics" 这种无型号的核显不算; 有具体型号的按独显处理
            if (Has(name, "AMD"))
            {
                return !name.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
                       && !name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase);
            }

            // NVIDIA: 一律按独显处理
            if (Has(name, "NVIDIA")) return true;

            return false;
        }
```

**验收标准**：

- [ ] `IsVendorChannelForeign(12288, 20480)` == `true`（3080 的串卡情形）
- [ ] `IsVendorChannelForeign(12288, 12288)` == `false`（3080 Ti 的正常情形）
- [ ] `IsVendorChannelForeign(null, 20480)` == `false`（缺证据 → 保守）
- [ ] `IsVendorChannelForeign(12288, null)` == `false`
- [ ] `IsVendorChannelForeign(20480, 20480)` == `false`
- [ ] `IsVendorChannelForeign(20470, 20480)` == `false`（1MB 内容差内）
- [ ] `IsVendorChannelForeign(20250, 20480)` == `true`（超出 1% 相对容差）
- [ ] `IsVendorChannelForeign(8192, 512)` == `false`（**低于 512MB 下限 → 核显 UMA 窗口，不判定**）
- [ ] `Select(..., useD3D: true)` 返回 D3D 的 used
- [ ] `Select(..., useD3D: false)` 返回厂商的 used
- [ ] `HardwareRules.UsesDedicatedVramName("NVIDIA GeForce RTX 3080")` == `true`
- [ ] `HardwareRules.UsesDedicatedVramName("AMD Radeon RX 7900 XTX")` == `true`
- [ ] `HardwareRules.UsesDedicatedVramName("Intel(R) Arc(TM) A770 Graphics")` == `true`
- [ ] `HardwareRules.UsesDedicatedVramName("Intel(R) UHD Graphics 770")` == `false`（核显）
- [ ] `HardwareRules.UsesDedicatedVramName("AMD Radeon(TM) Graphics")` == `false`（核显）

**提交**：

```
feat(gpu): 新增显存数据来源判定逻辑
```

---

### Task 4：`HardwareValueProvider.GetGpuVramPercent` 接入

**目标**：把 Task 1/2/3 接起来，让面板 / 网页端 / 曲线窗口拿到的显存数字每卡正确。

**现状代码**（`src/System/HardwareServices/HardwareValueProvider.cs:531-565`）：

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
```

**改动后的代码**：

```csharp
        /// <summary>
        /// 计算显存占用百分比。
        /// key 形如 "GPU.VRAM" (旧) 或 "GPU.1.VRAM" (多卡), 由此派生出 Used / Total 子 Key。
        ///
        /// ★★★ [显存修复] 双通道择源 ★★★
        /// 厂商通道 (NVAPI/ADL) 在 LHM 0.9.6 的 NVIDIA 实现上会被"全局化", 多卡时多张卡拿到同一份数据;
        /// D3D 通道按适配器统计, 每卡独立。
        /// 判定交给 GpuVramSource: 厂商通道报的容量命中了"另一块适配器"的容量 ⇒ 它在报告别的卡
        /// ⇒ 换 D3D 通道。没有证据 ⇒ 维持厂商通道 (单卡 / AMD / Intel 场景行为完全不变)。
        /// </summary>
        private float? GetGpuVramPercent(string key)
        {
            // "GPU.VRAM" -> "GPU." ; "GPU.1.VRAM" -> "GPU.1."
            string prefix = key.Substring(0, key.Length - "VRAM".Length);
            string cardKey = prefix.TrimEnd('.');        // "GPU" / "GPU.1"

            // --- 1. 厂商通道 (NVAPI / ADL) ---
            float? usedVendor  = ToMB(GetValue(prefix + "VRAM.Used"));
            float? totalVendor = ToMB(GetValue(prefix + "VRAM.Total"));

            // --- 2. D3D 通道 (按适配器统计, 每卡独立) ---
            float? usedD3D  = ToMB(GetValue(prefix + "VRAM.D3D.Used"));
            float? totalD3D = ToMB(GetValue(prefix + "VRAM.D3D.Total"));

            // --- 3. 该卡真实容量 (注册表) + 全部适配器容量 (串卡交叉证据) ---
            string fullName = GetGpuFullName(cardKey);
            float? totalReal = GpuVramCapacity.GetTotalMB(fullName);
            IReadOnlyList<float> allTotals = GpuVramCapacity.GetAllTotalsMB();

            // --- 4. 守卫: 这张卡有没有资格参与"串卡"判定 ---
            // 核显一律不参与: 注册表里核显记的是 UMA 窗口大小, 与它实际用的共享显存上限
            // 本来就不该相等, 拿它当基准会把核显误判成串卡, 再切到几乎没有的 D3D 专用显存口径,
            // 结果核显显存恒显 0% —— 比原 bug 更糟。
            bool eligible = HardwareRules.UsesDedicatedVramName(fullName)
                            && !IsSharedMemoryCard(prefix);

            // --- 5. 择源 ---
            bool useD3D = eligible && GpuVramSource.IsVendorChannelForeign(totalVendor, totalReal, allTotals);
            var (used, total) = GpuVramSource.Select(
                usedVendor, totalVendor, usedD3D, totalD3D, totalReal, useD3D);

            if (used.HasValue && total.HasValue && total > 0)
            {
                // total 的单位是 MB
                float gb = total.Value / 1024f;

                // 旧单卡缓存 (保留原有语义)
                if (Settings.DetectedGpuVramTotalGB <= 0 && gb > 0 && gb < 1024)
                {
                    Settings.DetectedGpuVramTotalGB = gb;
                }

                // ★★★ [多显卡] 每张卡单独记录自己的显存总容量 ★★★
                // 写的是"校正后"的容量 (串卡时用注册表真值), 容量模式显示才会对。
                // 不能先到先得: 择源结果会随"被监控卡集合"变化, 值变了必须覆盖。
                if (gb > 0 && gb < 1024)
                {
                    if (!Settings.DetectedGpuVramTotalGBByKey.TryGetValue(cardKey, out float oldGb)
                        || Math.Abs(oldGb - gb) > 0.01f)
                    {
                        Settings.DetectedGpuVramTotalGBByKey[cardKey] = gb;
                    }
                }

                return used / total * 100f;
            }

            if (_manualSensorCache.TryGetValue(prefix + "VRAM.Load", out var s) && s.Value.HasValue) return s.Value;
            return null;
        }
        /// <summary>
        /// 统一把传感器的显存数值换算成 MB。
        /// LHM 0.9.x 报 MB; 换新版 LHM 报字节。10485760 MB (= 10 TB) 实际不可能出现,
        /// 命中即代表单位是字节。
        /// </summary>
        private static float? ToMB(float? value)
        {
            if (!value.HasValue) return null;
            return value.Value > 10485760f ? value.Value / 1048576f : value.Value;
        }
        /// <summary>
        /// 该卡的显存是否走"共享显存"口径 (核显)。
        /// 判据: 它映射到的 LHM 传感器名里带 "Shared" (核显是 "D3D Shared Memory *")。
        /// </summary>
        private bool IsSharedMemoryCard(string prefix)
        {
            if (_manualSensorCache.TryGetValue(prefix + "VRAM.Used", out var usedSensor)
                && usedSensor?.Name != null
                && usedSensor.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_manualSensorCache.TryGetValue(prefix + "VRAM.Total", out var totalSensor)
                && totalSensor?.Name != null
                && totalSensor.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        /// <summary>
        /// 取 "GPU" / "GPU.1" 对应显卡的全名, 供注册表按名字匹配真实显存容量。
        /// </summary>
        private static string GetGpuFullName(string cardKey)
        {
            int index = -1;
            if (cardKey == GpuKey.Prefix.TrimEnd('.')) index = 0;      // "GPU" -> 0 号卡
            else if (cardKey.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = cardKey.Substring(GpuKey.Prefix.Length);
                if (int.TryParse(rest, out int i) && i >= 0) index = i;
            }
            return index >= 0 ? GpuRegistry.GetFullName(index) : "";
        }
```

**要点说明**：

1. **单位换算提前到每个来源单独做**（`ToMB`），避免"used 是 MB、total 是字节"的混算；这同时修掉了原来
   `if (total > 10485760)` 这个"在 0.9.6 下永不触发"的死代码隐患。
2. **写回 `DetectedGpuVramTotalGBByKey` 的是校正后的容量** → `MetricUtils.GetGpuVramTotalGB`（容量模式显示）自动受益。
3. `GpuRegistry.GetFullName(index)` 在 `SensorMap.Rebuild` 时写入，与 `GPU.{i}` 序号一一对应 ✅。
4. 核显路径不变：核显的 `GPU.VRAM.Used` 来自 `D3D Shared Memory Used`（名字含 "Shared"），
   `IsSharedMemoryCard` 会把它挡掉；再加上 `HardwareRules.UsesDedicatedVramName` 对核显返回 false
   → **双重保险，核显一律不参与判定，行为完全不变**。
5. 三道守卫任一不过 → `useD3D = false` → 维持厂商通道（与改动前逐位一致）。

**验收标准**：

- [ ] 3080 Ti（0 号卡）：显存百分比与改动前基本一致（厂商通道未被切换）
- [ ] 3080（1 号卡）：显存百分比 ≈ 0%，且**容量显示为 20 GB**（不再是 12 GB）
- [ ] 单卡机器：显存显示与改动前**完全一致**
- [ ] 核显机器：显存显示与改动前**完全一致**
- [ ] `Settings.DetectedGpuVramTotalGBByKey["GPU.1"]` == 20（而不是 12）
- [ ] `Settings.DetectedGpuVramTotalGBByKey["GPU.0"]` == 12

**提交**：

```
fix(gpu): 显存按卡择源，串卡时改用 D3D 通道并用真实容量校正
```

---

### Task 4.5（可选）：`GPU.VRAM.Load` 排除 `GPU Memory Controller`

**问题**：NVIDIA 每张卡有两个 Load 型传感器名字里带 "memory"：

- `GPU Memory` (Load) → 真正的显存占用率
- `GPU Memory Controller` (Load) → **显存控制器利用率**，不是显存占用率

现有规则 `if (s.SensorType == SensorType.Load && Has(name, "memory")) return "GPU.VRAM.Load";` 会把两者都命中，
`SensorMap` 里先到先得 → 实测 `GPU.VRAM.Load` 拿到的是 **`GPU Memory Controller`**（Ti=31、3080=0），
而不是真正的 `GPU Memory`（52.86）。

这个值只在 `Used`/`Total` 都缺失时才被用作兜底（`GetGpuVramPercent` 最后一行），平时不影响显示，
但属于同一区域的隐患，建议顺手修掉。

**改动**（`SensorMatcher.MatchGpu` 末尾）：

```csharp
            // 显存占用率: 只认 "GPU Memory", 不要 "GPU Memory Controller" (那是控制器利用率)
            if (sensorType == SensorType.Load && Has(name, "memory") && !Has(name, "controller"))
                return "GPU.VRAM.Load";
```

**验收标准**：

- [ ] `MatchGpu("GPU Memory", Load, false)` == `"GPU.VRAM.Load"`
- [ ] `MatchGpu("GPU Memory Controller", Load, false)` == `null`

**提交**：

```
fix(gpu): 显存占用率不再误用显存控制器利用率
```

> 若担心影响面，可先不合入本 Task（它是独立的一行改动，随时可摘）。

---

### Task 5：回归测试

**新增文件**：`tests/LiteMonitor.Tests/GpuVramTests.cs`

测试工程用 **xunit 2.9.2，无 Mock 框架**（见 `tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj`），
所以本 Task 的用例**只测纯函数**，不需要构造 `IHardware` / `ISensor` 桩：

```csharp
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 显存串卡修复的回归测试。
    ///
    /// 本测试工程只有 xunit, 没有 Mock 框架, 所以全部用例都只测纯函数
    /// (SensorMatcher.MatchGpu / GpuVramSource / HardwareRules.UsesDedicatedVramName),
    /// 不构造 IHardware / ISensor 桩。
    /// </summary>
    public class GpuVramTests
    {
        // 模拟注册表里的适配器容量集合: 3080 Ti = 12288MB, 3080 = 20480MB
        private static readonly float[] RealAdapterTotals = { 12288f, 20480f };

        // ---------- GpuVramCapacity.NormalizeName ----------

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080", "NVIDIA GEFORCE RTX 3080")]
        [InlineData("NVIDIA GeForce RTX 3080 ", "NVIDIA GEFORCE RTX 3080")]
        [InlineData("Intel(R) Arc(TM) A770 Graphics", "INTEL ARC A770 GRAPHICS")]
        [InlineData("  AMD   Radeon RX 7900 XTX ", "AMD RADEON RX 7900 XTX")]
        public void NormalizeName_归一化后去掉商标并压缩空白(string input, string expected)
        {
            Assert.Equal(expected, GpuVramCapacity.NormalizeName(input));
        }

        [Fact]
        public void NormalizeName_空输入返回空串()
        {
            Assert.Equal("", GpuVramCapacity.NormalizeName(null));
            Assert.Equal("", GpuVramCapacity.NormalizeName("   "));
        }

        [Fact]
        public void GetTotalMB_不存在的卡返回null()
        {
            Assert.Null(GpuVramCapacity.GetTotalMB("根本不存在的显卡型号 XXXX"));
        }

        [Fact]
        public void GetTotalMB_空输入返回null()
        {
            Assert.Null(GpuVramCapacity.GetTotalMB(""));
            Assert.Null(GpuVramCapacity.GetTotalMB(null));
        }

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080", 20480f)]
        [InlineData("NVIDIA GeForce RTX 3080 Ti", 12288f)]
        public void GetTotalMB_本机实测的独显容量(string name, float expectedMB)
        {
            float? actual = GpuVramCapacity.GetTotalMB(name);
            if (!actual.HasValue) return;   // 别的机器上没有这张卡, 跳过
            Assert.Equal(expectedMB, actual.Value, 1);
        }

        [Fact]
        public void GetAllTotalsMB_读到的容量都是合理量级()
        {
            var all = GpuVramCapacity.GetAllTotalsMB();
            Assert.NotNull(all);
            if (all.Count == 0) return;     // 受限环境读不到, 跳过
            Assert.Contains(all, v => v >= 512f);
        }

        // ---------- GpuVramSource.SameCapacity ----------

        [Theory]
        [InlineData(12288f, 12288f, true)]
        [InlineData(20480f, 20480f, true)]
        [InlineData(20470f, 20480f, true)]   // 10MB 差, 在 1% 相对容差(204MB)内
        [InlineData(20250f, 20480f, false)]  // 230MB 差, 超出容差
        [InlineData(12288f, 20480f, false)]
        public void SameCapacity_容差内算同一个(float a, float b, bool expected)
        {
            Assert.Equal(expected, GpuVramSource.SameCapacity(a, b));
        }

        // ---------- GpuVramSource.IsVendorChannelForeign ----------

        [Fact]
        public void IsVendorChannelForeign_厂商报的容量命中另一块卡就是串卡()
        {
            // 3080 这张卡: 厂商报 12288 (其实是 Ti 的容量), 注册表真值 20480
            Assert.True(GpuVramSource.IsVendorChannelForeign(12288f, 20480f, RealAdapterTotals));
        }

        [Fact]
        public void IsVendorChannelForeign_本卡对得上就判为可信()
        {
            Assert.False(GpuVramSource.IsVendorChannelForeign(12288f, 12288f, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(20480f, 20480f, RealAdapterTotals));
        }

        [Fact]
        public void IsVendorChannelForeign_对不上但命中不了别的卡就不判定()
        {
            // 驱动可见容量 != 物理容量时 (实测 Arc A770: 16256 vs 16384) 会走到这里,
            // 此时厂商报的 16384 命中不了系统里任何另一块适配器 -> 不能判定成串卡
            Assert.False(GpuVramSource.IsVendorChannelForeign(16384f, 16256f, RealAdapterTotals));
        }

        [Fact]
        public void IsVendorChannelForeign_缺证据时判为可信()
        {
            Assert.False(GpuVramSource.IsVendorChannelForeign(null, 20480f, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(12288f, null, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(null, null, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(0f, 20480f, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(12288f, 20480f, null));
        }

        [Fact]
        public void IsVendorChannelForeign_真实容量低于下限不判定()
        {
            // 核显的 UMA 窗口 (几百 MB) 不能拿来当基准
            Assert.False(GpuVramSource.IsVendorChannelForeign(8192f, 512f, RealAdapterTotals));
        }

        // ---------- GpuVramSource.Select ----------

        [Fact]
        public void Select_厂商串卡时改用D3D通道和真实容量()
        {
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: 6495f, vendorTotalMB: 12288f,
                d3dUsedMB: 4.1f, d3dTotalMB: null,
                realTotalMB: 20480f,
                useD3D: true);

            Assert.Equal(4.1f, used.Value, 3);      // 用 D3D 的 used
            Assert.Equal(20480f, total.Value, 3);    // 用注册表真值
        }

        [Fact]
        public void Select_厂商串卡但D3D缺失时不混算()
        {
            // 绝不能拿"别的卡的 used"(6495) 去除"本卡的 total"(20480) -> 31.7% 这种又错又怪的值
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: 6495f, vendorTotalMB: 12288f,
                d3dUsedMB: null, d3dTotalMB: null,
                realTotalMB: 20480f,
                useD3D: true);

            Assert.False(used.HasValue);
            Assert.Equal(20480f, total.Value, 3);
        }

        [Fact]
        public void Select_厂商可信时维持原口径()
        {
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: 6495f, vendorTotalMB: 12288f,
                d3dUsedMB: 6305f, d3dTotalMB: 12288f,
                realTotalMB: 12288f,
                useD3D: false);

            Assert.Equal(6495f, used.Value, 3);      // 仍用厂商值 (保留 CUDA 可见性)
            Assert.Equal(12288f, total.Value, 3);
        }

        [Fact]
        public void Select_厂商缺值时用D3D兜底()
        {
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: null, vendorTotalMB: null,
                d3dUsedMB: 4.1f, d3dTotalMB: 12288f,
                realTotalMB: 12288f,
                useD3D: false);

            Assert.Equal(4.1f, used.Value, 3);
            Assert.Equal(12288f, total.Value, 3);
        }

        // ---------- HardwareRules.UsesDedicatedVramName ----------

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080", true)]
        [InlineData("NVIDIA GeForce RTX 3080 Ti", true)]
        [InlineData("AMD Radeon RX 7900 XTX", true)]
        [InlineData("Intel(R) Arc(TM) A770 Graphics", true)]
        [InlineData("Intel(R) UHD Graphics 770", false)]     // 核显
        [InlineData("AMD Radeon(TM) Graphics", false)]        // 核显
        [InlineData("", false)]
        [InlineData(null, false)]
        public void UsesDedicatedVramName_只有独显才参与串卡判定(string name, bool expected)
        {
            Assert.Equal(expected, HardwareRules.UsesDedicatedVramName(name));
        }

        // ---------- SensorMatcher.MatchGpu ----------

        [Theory]
        [InlineData("D3D Dedicated Memory Used", "GPU.VRAM.D3D.Used")]
        [InlineData("D3D Dedicated Memory Total", "GPU.VRAM.D3D.Total")]
        [InlineData("D3D Dedicated Memory Free", "GPU.VRAM.D3D.Free")]
        public void MatchGpu_D3D专用显存走独立Key(string sensorName, string expected)
        {
            Assert.Equal(expected,
                SensorMatcher.MatchGpu(sensorName, LibreHardwareMonitor.Hardware.SensorType.SmallData, false));
        }

        [Theory]
        [InlineData("GPU Memory Used", "GPU.VRAM.Used")]
        [InlineData("GPU Memory Total", "GPU.VRAM.Total")]
        public void MatchGpu_厂商显存Key不变(string sensorName, string expected)
        {
            Assert.Equal(expected,
                SensorMatcher.MatchGpu(sensorName, LibreHardwareMonitor.Hardware.SensorType.SmallData, false));
        }

        [Fact]
        public void MatchGpu_核显仍走共享显存()
        {
            // ★ 这条最关键: 核显的显存**只有** "D3D Shared Memory *" 一个来源,
            //   一旦被通用分支的 D3D 排除条件误伤, 核显的显存映射会整个消失。
            Assert.Equal("GPU.VRAM.Used",
                SensorMatcher.MatchGpu("D3D Shared Memory Used", LibreHardwareMonitor.Hardware.SensorType.SmallData, true));
            Assert.Equal("GPU.VRAM.Total",
                SensorMatcher.MatchGpu("D3D Shared Memory Total", LibreHardwareMonitor.Hardware.SensorType.SmallData, true));
        }

        [Fact]
        public void MatchGpu_非显存指标不变()
        {
            Assert.Equal("GPU.Load",
                SensorMatcher.MatchGpu("D3D 3D", LibreHardwareMonitor.Hardware.SensorType.Load, false));
            Assert.Equal("GPU.Temp",
                SensorMatcher.MatchGpu("GPU Core", LibreHardwareMonitor.Hardware.SensorType.Temperature, false));
        }

        [Fact]
        public void MatchGpu_显存占用率不再误用显存控制器利用率()
        {
            Assert.Equal("GPU.VRAM.Load",
                SensorMatcher.MatchGpu("GPU Memory", LibreHardwareMonitor.Hardware.SensorType.Load, false));
            Assert.Null(
                SensorMatcher.MatchGpu("GPU Memory Controller", LibreHardwareMonitor.Hardware.SensorType.Load, false));
        }
    }
}
```

**注意**：如果 `GpuVramSource` / `GpuVramCapacity` 放在 `LiteMonitor.src.SystemServices` 命名空间下，
测试工程需要能访问 —— `SensorMatcher` 已经是 `public static` 且在同一命名空间，现有测试
（`HardwareRulesGpuNameTests.cs`）已经引用了 `LiteMonitor.src.SystemServices`，照抄其 using 即可。

**验收标准**：

- [ ] `dotnet test` 全绿，且用例数从 49 增加到 49 + 本 Task 的用例数
- [ ] 既有 49 个用例**一个都不能挂**（尤其 `MetricUtilsGpuKeyTests` / `GpuKeyTests` / `GpuItemSyncTests`）

**提交**：

```
test(gpu): 补充显存通道择源与 D3D Key 拆分的回归测试
```

---

### Task 6：文档与真机验收

**6.1 使用限制文档**

在 `docs/` 下新增（或追加到既有的使用说明文档）一节，说明：

1. **NVIDIA 多卡**：LHM 0.9.6 的厂商显存接口在多卡下会全局化，本程序已自动改用 D3D 通道 + 注册表真实容量校正；
2. **口径差异**：D3D 口径统计的是"经图形/计算 API 提交到该适配器的专用显存"，
   纯计算型（CUDA / ROCm 直接分配的独占显存）**可能不计入**——单卡场景下程序仍用厂商通道，不存在该差异；
3. **Intel 双独显**：LHM 的 D3D 设备号在拿不到 BDF 时会退化成"取第一个 Intel 设备"，存在潜在串卡（上游缺陷，本项目无法规避）；
4. **容量口径**：显存容量以注册表记录的物理容量为准（NVIDIA 20 GB 卡显示 20 GB，不是 19 GB）。

**6.2 真机验收清单**（由用户在测试机上执行）

前置：按 `G:\TOOL\启动多卡测试.ps1 -GpuCount 2` 启动测试版。

| # | 检查项 | 期望结果 | 判定依据 |
|---|---|---|---|
| 1 | 0 号卡（3080 Ti）显存百分比 | ≈ 51%（6305/12288） | 与改动前基本一致 |
| 2 | 0 号卡显存容量 | 12 GB | 注册表真值 |
| 3 | **1 号卡（3080）显存百分比** | **≈ 0%** | 改动前错误地显示 ≈52% |
| 4 | **1 号卡显存容量** | **20 GB** | 改动前错误地显示 12 GB |
| 5 | 1 号卡显存已用量 | ≈ 0 GB（4 MB） | nvidia-smi: used=0 MiB |
| 6 | 网页端两个 GPU 的显存 | 同面板一致 | — |
| 7 | 历史曲线窗口的显存曲线 | 两条线分开，不再重合 | — |
| 8 | 重启程序后显存仍正确 | 同上 | 验证容量缓存 `DetectedGpuVramTotalGBByKey` 的写入时机 |
| 9 | 切回"只监控 1 张卡"（且选 3080） | 显存应显示 0.0 GB / 20 GB | 验证单卡场景也能被正确识别为串卡 |

对照基准（`nvidia-smi --query-gpu=index,pci.bus_id,name,memory.total,memory.used --format=csv`）：

```
index, pci.bus_id, name, memory.total [MiB], memory.used [MiB]
0, 00000000:2D:00.0, NVIDIA GeForce RTX 3080, 20480 MiB, 0 MiB
1, 00000000:2E:00.0, NVIDIA GeForce RTX 3080 Ti, 12288 MiB, 6330 MiB
```

> 注意 LHM 与 NVML 的**卡序不同**（LHM 0 号 = 3080 Ti，NVML 0 号 = 3080），这是上游枚举顺序差异，与本修复无关。

**提交**：

```
docs(gpu): 补充多显卡显存口径与已知限制说明
```

---

## 5. 验证与回滚

### 5.1 构建与测试基线

| 项目 | 基线 |
|---|---|
| SDK | `G:\TOOL\dotnet-sdk\dotnet.exe`（8.0.426）—— 本机**唯一**可用 SDK，**不得安装、不得改 PATH** |
| 构建命令 | `G:\TOOL\dotnet-sdk\dotnet.exe build LiteMonitor.csproj -c Release` |
| 警告基线 | **231 个警告 / 0 个错误**（MSBuild 汇总行；`-v minimal` 会把同一条警告打印两遍，逐行 grep 得 462 是虚高） |
| 测试基线 | **49 个用例全通过**（Task 5 后为 49 + 新增数） |

> 本计划**不新增任何第三方包**，不改变依赖树。

### 5.2 行尾与编码约定

本仓库 `core.autocrlf = true`，因此**索引里的 blob 一律是 LF**（`git ls-files --eol` 全部显示 `i/lf`），
工作区里 `.cs` / 部分 `.md` 呈现 CRLF 是 autocrlf 检出转换的结果，**不是内容差异**。

| 路径 | 仓库 blob | 工作区 | 编码 |
|---|---|---|---|
| `src/**/*.cs` | LF | CRLF | UTF-8（无 BOM） |
| `tests/**/*.cs` | LF | CRLF | UTF-8（无 BOM） |
| `docs/superpowers/plans/*.md` | LF | LF 或 CRLF 均可 | UTF-8（无 BOM） |

> 本计划文件本身：**LF / UTF-8 无 BOM**。

改动既有源码前必须先归一化行尾（`git ls-files --eol` 确认目标文件当前是 `i/lf`），
避免整文件被改写成 CRLF 而在 diff 里炸出上千行噪声。

> 提交前用 `git ls-files --eol` 复核新增文件是否仍是 `i/lf`。

### 5.3 回滚

每个 Task 一次提交，**回滚 = 按 Task 逆序 `git revert`**。Task 1/3 是纯新增文件，revert 后不影响既有行为；
Task 2/4 是行为改动，revert 后回到"厂商通道"口径（即当前有 bug 的状态）。

**紧急开关**（可选，建议加）：在 `Settings` 里加一个 `UseGpuD3dVramSource`（默认 `true`），
让用户在发现异常时一键关掉新逻辑。**若采纳，需要多改 `Settings.cs` + `SettingsHelper.cs` 各一处**，
并同步 9 个语言包（若 UI 暴露该项）。—— 本计划默认**不引入该开关**（保持改动面最小），
如果希望有，请在实施前告诉我。

---

## 6. 风险登记册

| # | 风险 | 等级 | 缓解 |
|---|---|---|---|
| R1 | 注册表读取在受限环境失败 | 低 | 全程 try/catch，失败即"无校正值" → 判定不触发 → 维持旧行为 |
| R2 | 注册表卡名与 LHM 名字对不上 | 中 | `NormalizeName` 去 `(R)/(TM)/®/™` + 压缩空白 + 忽略大小写；真机已验证 NVIDIA 两侧完全一致；查不到就不校正 |
| R3 | 同型号双卡在注册表里有两条同名记录 | 低 | 同名但容量一致 → 取该值；同名但容量不一致 → 视为无法判定，返回 null（不猜） |
| R4 | 切到 D3D 后丢失 CUDA/ROCm 独占显存可见性 | 中 | 判定只在"厂商通道报的容量 ≠ 该卡真实容量"时触发；单卡/AMD/Intel 都不触发。**这是本方案最需要向用户说明的取舍** |
| R5 | D3D 口径与厂商口径有 ~1% 误差 | 低 | 已实测（Ti: D3D 6305 vs nvidia-smi 6330，差 0.4%），可接受 |
| R6 | 核显被误判（切到几乎没有的 D3D 专用显存 → 核显显存恒显 0%） | ~~中~~ → **低** | **三道守卫**：G1 `UsesDedicatedVramName` 排除 Intel 核显 / `AMD Radeon(TM) Graphics`；G2 传感器名含 "Shared" 即判为核显口径；G3 真实容量 < 512MB 不判定。任一不过即维持旧行为 |
| R7 | 改动 `SensorMatcher` 影响其它 GPU 指标 | 中 | 只在 GPU 分支加"先判 D3D 专用显存"，`GPU.Load` / `GPU.Temp` / `GPU.VRAM.Load` 规则原样保留；Task 5 用例逐条锁死原行为 |
| R8 | 新的内部 Key 泄漏到 UI / 网页端 | 低 | `GpuItemSync` 只生成 6 种用户项；`LiteWebServer` 遍历已配置监控项 → 均不受影响；Task 6 验收清单第 6 项实测 |
| R9 | 与用户的 `chore/rebrand` 进行中改动冲突 | 中 | 建议另起分支 `fix/gpu-vram-d3d`；实施前先 `git status` 确认工作区干净 |
| R10 | 上游 LHM 升级后行为变化 | 低 | 判定规则是"自校准"的：上游修好了 → 容量对得上 → 不触发切换；上游变化 → 判定自动跟随 |

---

## 7. 附录

### 附录 A：上游源码坐标（LibreHardwareMonitorLib v0.9.6，tag `v0.9.6`，commit `3d331e3`）

| 主题 | 文件:行 |
|---|---|
| NVIDIA 显存走 NVAPI 旧接口 | `LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs:705-724` |
| `GetMemoryInfo` 实现 | `LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs:1128-1143` |
| NVAPI 接口号 `0x774AA982` | `LibreHardwareMonitorLib/Interop/NvApi.cs:86` |
| NVAPI 委托签名（DisplayHandle） | `LibreHardwareMonitorLib/Interop/NvApi.cs:147` |
| NVIDIA D3D 按 PCI bus 绑定 | `LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs:340-423`（比对在 `:405`） |
| NVIDIA 显存传感器创建 | `LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs:447-450`（Free/Used/Total/Load） |
| NVIDIA D3D 传感器创建 | `LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs:425-426`（**只有 Used/Shared Used，无 Total**） |
| AMD 显存走 ADL（带 AdapterIndex） | `LibreHardwareMonitorLib/Hardware/Gpu/AmdGpu.cs:154-160`（容量）、`:343-348`（用量） |
| AMD D3D 传感器（含 Total） | `LibreHardwareMonitorLib/Hardware/Gpu/AmdGpu.cs:130-135` |
| Intel 独显显存走 D3D | `LibreHardwareMonitorLib/Hardware/Gpu/IntelDiscreteGpu.cs:516-568` |
| Intel D3D 设备号按 BDF 匹配 | `LibreHardwareMonitorLib/Hardware/Gpu/IntelDiscreteGpu.cs:197-227` |
| Intel 兜底"取第一个 Intel 设备" | `LibreHardwareMonitorLib/Hardware/Gpu/IntelDiscreteGpu.cs:518-538` |
| Intel 核显只有 D3D Shared | `LibreHardwareMonitorLib/Hardware/Gpu/IntelIntegratedGpu.cs:69-80` |
| `D3DDisplayDevice` 是 **internal** | `LibreHardwareMonitorLib/Hardware/D3DDisplayDevice.cs:17` |
| LHM 的 NVML 封装**无显存接口** | `LibreHardwareMonitorLib/Interop/NvidiaML.cs`（只有 power / pcie / pciinfo） |

> `D3DDisplayDevice` 是 `internal static class` ⇒ **LiteMonitor 无法直接调用它**取 `GpuVideoMemoryLimit`（真实容量），
> 这是本方案必须绕道注册表的原因。

### 附录 B：本项目关键坐标

| 主题 | 文件:行 |
|---|---|
| GPU 传感器匹配（要改） | `src/System/HardwareServices/SensorMatcher.cs:71-110` |
| 冲突规则（D3D 被顶掉的地方，**本方案不需改**） | `src/System/HardwareServices/SensorMap.cs:283-290` |
| 显存百分比计算（要改） | `src/System/HardwareServices/HardwareValueProvider.cs:531-565` |
| 通用传感器兜底读取 | `src/System/HardwareServices/HardwareValueProvider.cs:496-510` |
| 每卡显存容量缓存 | `src/Core/Settings.cs:158-162` |
| 容量读取（UI 侧） | `src/Core/MetricUtils.cs:404-419` |
| 容量模式显示 | `src/Core/MetricUtils.cs:110-127` |
| 显卡全名注册表 | `src/Core/GpuRegistry.cs:46-51` |
| 多卡 Key 规则 | `src/Core/GpuKey.cs` |

### 附录 C：真机原始数据（2026-10-20 采集）

**nvidia-smi**

```
index, pci.bus_id, name, memory.total [MiB], memory.used [MiB]
0, 00000000:2D:00.0, NVIDIA GeForce RTX 3080, 20480 MiB, 0 MiB
1, 00000000:2E:00.0, NVIDIA GeForce RTX 3080 Ti, 12288 MiB, 6330 MiB
```

**Windows 注册表（显示适配器类）**

```
Key   DriverDesc                        QwMemSizeBytes  MemMB     MatchingDeviceId
0000  GameViewer Virtual Display Adapter               -          -          Root\GameViewerIddDriver
0001  NVIDIA GeForce RTX 3080            21474836480    20480.00   pci\ven_10de&dev_2206
0003  Intel(R) Arc(TM) A770 Graphics     17045651456    16256.00   PCI\VEN_8086&DEV_56A0
0004  Meta Virtual Monitor                             -          -          Root\MetaVirtualScreenDriver
0005  NVIDIA GeForce RTX 3080 Ti         12884901888    12288.00   pci\ven_10de&dev_2208
0006  MuMu Virtual Display Adapter                    -          -          Root\MuMuIddDriver
0008  Todesk Virtual Display Adapter                  -          -          Root\tdIdd
```

**LHM 侧标识与关键传感器**

```
### NVIDIA GeForce RTX 3080 Ti   id=/gpu-nvidia/0  prio=0
      D3D Dedicated Memory Used    SmallData   6305.1875
      GPU Memory Total             SmallData   12288
      GPU Memory Free              SmallData   5792
      GPU Memory Used              SmallData   6495
      GPU Memory                   Load        52.856827

### NVIDIA GeForce RTX 3080   id=/gpu-nvidia/1  prio=0
      D3D Dedicated Memory Used    SmallData   4.109375
      GPU Memory Total             SmallData   12288     <<< 与 0 号卡同值
      GPU Memory Free              SmallData   5792      <<< 与 0 号卡同值
      GPU Memory Used              SmallData   6495      <<< 与 0 号卡同值
      GPU Memory                   Load        52.856827 <<< 与 0 号卡同值
```

**PnP 设备状态**

```
Status    FriendlyName                        InstanceId
OK        NVIDIA GeForce RTX 3080             PCI\VEN_10DE&DEV_2206&...
OK        NVIDIA GeForce RTX 3080 Ti          PCI\VEN_10DE&DEV_2208&...
Unknown   Intel(R) Arc(TM) A770 Graphics      PCI\VEN_8086&DEV_56A0&...
```

→ Intel Arc A770 状态为 `Unknown`（已移除/禁用），**LHM 枚举不到 → Intel 路径无法在本机实测**。

### 附录 D：术语

| 术语 | 含义 |
|---|---|
| 厂商通道 | LHM 通过 NVAPI / ADL 拿到的 `GPU Memory Used/Total/Free` 传感器 |
| D3D 通道 | LHM 通过 `D3DKMTQueryStatistics` 按适配器拿到的 `D3D Dedicated/Shared Memory *` 传感器 |
| 串卡 | 多张卡显示同一张卡的数据 |
| 真实容量 | Windows 注册表 `HardwareInformation.qwMemorySize` 记录的该适配器物理显存字节数 |
| BDF | PCI 的 Bus/Device/Function 三元组，用于精确标识一块 PCI 设备 |