# 硬件服务层 (HardwareServices) 架构文档

本文档详细介绍了 `src/System/HardwareServices` 目录下的文件结构、职责划分以及组件之间的协作关系。

## 1. 概述 (Overview)

`HardwareServices` 是 LiteMonitor++ 的硬件监控核心层，负责与底层驱动 (LibreHardwareMonitor / Windows API) 交互，采集 CPU、GPU、内存、网络、磁盘等硬件数据，并将其标准化为统一的键值对 (Key-Value) 供 UI 层展示。

## 2. 核心架构图 (Architecture)

```mermaid
graph TD
    HM[HardwareMonitor (主控)] --> HVP[HardwareValueProvider (数据分发)]
    
    HVP --> SM[SensorMap (通用传感器)]
    HVP --> NM[NetworkManager (网络)]
    HVP --> DM[DiskManager (磁盘)]
    HVP --> FPS[FpsCounter (帧率)]
    HVP --> BAT[BatteryService (电池)]
    
    SM --> HR[HardwareRules (硬件规则)]
    SM --> Matcher[SensorMatcher (名称匹配)]
    SM --> CP[ComponentProcessor (复杂聚合)]
    
    NM --> PCM[PerformanceCounterManager (Win系统计数器)]
    DM --> PCM
```

## 3. 文件职责详解 (File Responsibilities)

### 3.1 核心管理与分发 (Core Management)

| 文件名 | 职责描述 |
| :--- | :--- |
| **HardwareMonitor.cs** | **(位于父目录)** 整个硬件监控系统的入口和总指挥。负责初始化 `Computer` 对象，管理 `Update` 循环，协调各子服务的生命周期（启动、暂停、重载、释放）。 |
| **HardwareValueProvider.cs** | **数据中枢**。对上层 UI 提供统一的 `GetValue(key)` 接口。它不直接读取硬件，而是根据 Key 的类型（如 `NET.`、`DISK.`、`CPU.`），将请求分发给相应的管理器（NetworkManager, SensorMap 等），并处理数据缓存。 |

### 3.2 传感器映射与识别 (Sensor Mapping)

| 文件名 | 职责描述 |
| :--- | :--- |
| **SensorMap.cs** | **映射核心**。负责维护 `Hardware` -> `Key` 的映射表（FastMap）。它遍历所有硬件传感器，调用 `SensorMatcher` 识别其用途，并将其绑定到标准 Key（如 `CPU.Temp`）。 |
| **HardwareRules.cs** | **业务规则**。定义硬件的优先级（如 独显 > 核显）、显存类型判断（共享 vs 专用）等核心逻辑。它是"裁判员"，决定哪个硬件的数据更值得信赖。 |
| **SensorMatcher.cs** | **翻译官**。负责字符串匹配逻辑。将五花八门的原始传感器名称（如 "CPU Package", "Tctl/Tdie"）翻译为标准 Key。 |
| **FanMapper.cs** | **风扇映射**。专门处理风扇控制相关的传感器映射逻辑，辅助 SensorMap 识别风扇和水泵。 |
| **ComponentProcessor.cs** | **数据聚合**。处理无法直接映射的复杂数据。例如计算 CPU 多核负载的平均值、寻找多核温度的最大值等需要二次计算的场景。 |

### 3.3 专用硬件管理器 (Dedicated Managers)

| 文件名 | 职责描述 |
| :--- | :--- |
| **NetworkManager.cs** | **网络管家**。负责网速监控（上传/下载）。内置流量统计、单位换算、以及基于 PerformanceCounter 的高性能采样逻辑。 |
| **DiskManager.cs** | **磁盘管家**。负责磁盘读写速度、活动时间及温度监控。支持按盘符过滤特定磁盘。 |
| **BatteryService.cs** | **电池服务**。处理电池充放电逻辑。负责修正功率/电流的正负号（充电为正，放电为负），并提供测试用的模拟数据生成功能。 |
| **FpsCounter.cs** | **帧率服务**。负责获取游戏帧率（通常通过 RTSS 共享内存或其他 Hook 方式）。 |

### 3.4 基础设施与工具 (Infrastructure & Utils)

| 文件名 | 职责描述 |
| :--- | :--- |
| **PerformanceCounterManager.cs** | **系统计数器封装**。封装 Windows `PerformanceCounter` API。提供比 LHM 更快、更稳定的 CPU/网络/磁盘数据读取方式，是 NetworkManager 的底层依赖。 |
| **DriverInstaller.cs** | **驱动加载器**。负责安全地安装和加载 `WinRing0.sys` 或 `InpOut` 驱动，解决权限问题和文件占用问题，确保 LHM 能读取底层寄存器。 |
| **HardwareScanner.cs** | **UI 辅助工具**。提供静态辅助方法，用于扫描并列出当前系统中的网卡、磁盘、风扇列表，供设置界面的下拉菜单使用。 |
| **SystemOptimizer.cs** | **内存优化**。提供 `EmptyWorkingSet` 等系统级 API 调用，用于定期修剪程序的内存占用，保持轻量化。 |

## 4. 关键设计原则

1.  **解耦 (Decoupling)**: 
    *   `SensorMap` 不再包含具体的硬件判定逻辑，而是委托给 `HardwareRules`。
    *   `SensorMap` 不再包含繁琐的字符串匹配，而是委托给 `SensorMatcher`。
2.  **优先级 (Priority)**:
    *   通过 `HardwareRules` 确保高优先级硬件（如独显）的数据不会被低优先级硬件（如核显）覆盖。
3.  **高性能 (Performance)**:
    *   `HardwareValueProvider` 实现了缓存机制，避免频繁重复计算。
    *   `NetworkManager` 和 `DiskManager` 在可能的情况下优先使用 Windows 计数器而非驱动轮询。

---
*文档生成时间: 2026-10-20*
