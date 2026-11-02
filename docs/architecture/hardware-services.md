# 硬件服务层

`src/System/HardwareServices` 是硬件采样的实现层，负责与底层驱动（LibreHardwareMonitorLib、Windows 性能计数器、Windows API）交互，把 CPU、GPU、内存、网络、磁盘、电池等数据标准化为统一的键值对供上层消费。

采集的契约、节拍与缺失语义见 [../specs/hardware-sampling.md](../specs/hardware-sampling.md)；跨进程隔离见 [process-isolation.md](process-isolation.md)。

---

## 1. 分层

```
HardwareMonitor (主控，父目录)
  └─ HardwareSamplingCoordinator   采样节拍与代次
       └─ DemandRegistry            消费者需求登记
  └─ HardwareValueProvider         对上层的取值入口
       ├─ SensorMap → HardwareRules / SensorMatcher / FanMapper / ComponentProcessor
       ├─ NetworkManager → NetworkAdapterSource / NetworkAddressCache / NetworkChangeSource / NetworkSampleFreshness
       ├─ DiskManager
       ├─ BatteryService
       ├─ FpsCounter
       └─ PerformanceCounterManager
```

---

## 2. 采集主控与分发

| 组件 | 职责 |
|---|---|
| `HardwareMonitor` | 采集系统入口与总指挥。初始化 `Computer` 对象、管理 `Update` 循环、协调各子服务的生命周期（启动、暂停、重载、释放）。 |
| `IHardwareCollector` | 采集层的就绪契约：是否已完成初始化、是否可以进行真实采集。 |
| `HardwareSamplingCoordinator` | 采样节拍与**采集代次**的推进者。代次只在发生真实采集后才递增；采样异常必须向上传播，由协调器计入失败集合。 |
| `DemandRegistry` | 消费者需求登记。传感器的开启窗口由消费方显式登记与释放，不由界面遍历触发库更新。 |
| `HardwareValueProvider` | 对上层提供统一的 `GetValue(key)` 入口。它不直接读取硬件，而是按 Key 的类型（`NET.`、`DISK.`、`CPU.` 等）分发到对应管理器，并处理缓存。 |
| `HardwareClientAdapter` | 进程内模式的兼容桥：把旧式单桶需求调用适配到按消费者分桶的新需求模型。 |

## 3. 快照与发布

| 组件 | 职责 |
|---|---|
| `SnapshotPublisher` | 发布不可变快照批次。批次内一旦形成即不再变化；采集尚未完成第一次真实采集时返回空批次，缺失原因为 `initializing`，不伪造成功批次。 |
| `CatalogPublisher` | 发布硬件目录。目录整体替换，读者不会看到半截状态。 |
| `CommittedMemoryPublisher` | 发布已提交内存状态，带所属的采集代次。 |
| `CommittedMemoryReader` | 读取已提交内存的原始数据源。 |

## 4. 传感器映射与识别

| 组件 | 职责 |
|---|---|
| `SensorMap` | 映射核心。维护「硬件 → Key」的映射表，遍历全部传感器，调用 `SensorMatcher` 识别用途并绑定到标准 Key。 |
| `HardwareRules` | 业务规则。定义硬件优先级（如独显优先于核显）、显存类型判定（共享 / 专用），决定哪个硬件的数据更可信。 |
| `SensorMatcher` | 名称匹配。把五花八门的原始传感器名称（如 `CPU Package`、`Tctl/Tdie`）翻译为标准 Key。 |
| `FanMapper` | 风扇与水泵的传感器映射。 |
| `ComponentProcessor` | 复杂聚合。处理无法直接映射的数据，例如多核负载的平均值、多核温度的最大值。 |

## 5. 设备身份与槽位

| 组件 | 职责 |
|---|---|
| `DeviceIdentity` | 稳定设备身份。身份字段优先取稳定设备 ID，无法取得时回退到 LHM Identifier；该身份用于跨重启保持绑定，不用于标识「跳过更新」。 |
| `DeviceBindingStore` | 设备绑定持久化。 |
| `DeviceMigration` | 旧配置迁移，给出迁移结论。 |
| `DeviceSlotResolver` | 槽位解析，向界面与下游包公开设备选择结论。 |

## 6. GPU 与电压专项

| 组件 | 职责 |
|---|---|
| `GpuVramSource` | 显存容量择源。多个来源的容量比较容差为绝对 1MB 或相对 1%，取大者。 |
| `GpuVramCapacity` | 解析适配器子键下的数值型容量键，排除 `Configuration` / `Properties` 等非数值键。 |
| `GpuWarmup` | 对「传感器尚未上线」的显卡补一次 `Update()`。只做 GPU 操作，不触碰主板 / 电池 / 磁盘，避免恢复启动时的全硬件更新卡顿。 |
| `CpuVoltageResolver` | CPU 电压来源解析，结果按已分类 / 未分类输出，未分类项不进入最终结果。 |

## 7. 探测策略

| 组件 | 职责 |
|---|---|
| `DiskProbePolicy` | 磁盘探测档位。**仅计数器**档位不构造任何存储设备，不打开设备句柄、不做 SMART 探测：磁盘速度与活动率来自 Windows 性能计数器，磁盘温度与 SMART 缺失时显示为缺失值而非 0。 |
| `ControllerProbePolicy` | 控制器（USB / HID）探测档位。自动档位仅在监控项需要风扇或水泵时才开启控制器扫描。 |

## 8. 网络

| 组件 | 职责 |
|---|---|
| `NetworkManager` | 网速监控：流量统计、单位换算、基于性能计数器的高性能采样。 |
| `NetworkAdapterSource` | 网卡枚举，提供连接名等描述信息。 |
| `NetworkAddressCache` | 地址缓存，记录读到的真实地址，避免每次取值重复解析。 |
| `NetworkChangeSource` | 网络变更事件来源，提供变更种类。 |
| `NetworkSampleFreshness` | 样本新鲜度判定，区分「本次读取成功拿到新样本」与沿用旧值。 |

## 9. 磁盘、电池与帧率

| 组件 | 职责 |
|---|---|
| `DiskManager` | 磁盘读写速度、活动时间与温度监控，支持按盘符过滤。 |
| `BatteryService` | 电池充放电逻辑。修正功率与电流的正负号（充电为正、放电为负），并提供测试用的模拟数据生成。 |
| `FpsCounter` | 帧率采集，通过外部 FPS 组件取数。 |

## 10. 基础设施

| 组件 | 职责 |
|---|---|
| `PerformanceCounterManager` | Windows `PerformanceCounter` API 封装。提供比驱动轮询更快更稳定的 CPU / 网络 / 磁盘读数，是 `NetworkManager` 与 `DiskManager` 的底层依赖。 |
| `DriverInstaller` | 驱动加载。安装并加载 PawnIO 驱动（要求版本不低于 2.2.0），处理权限与文件占用问题，确保底层传感器可读。 |
| `HardwareScanner` | 界面辅助：扫描并列出当前系统中的网卡、磁盘、风扇，供设置界面下拉使用。 |
| `SystemOptimizer` | 内存整理，调用 `EmptyWorkingSet` 等系统级 API 修剪占用。 |
| `HardwareErrorLog` | 硬件层错误日志。单文件超过上限后滚动为 `.1` 归档，避免无限膨胀。 |
| `MaintenanceScheduler` | 周期性维护任务的调度。 |

---

## 11. 关键设计原则

1. **解耦**：`SensorMap` 不含具体硬件判定逻辑，判定委托给 `HardwareRules`；`SensorMap` 不含字符串匹配，匹配委托给 `SensorMatcher`。
2. **优先级**：由 `HardwareRules` 保证高优先级硬件（如独显）的数据不被低优先级硬件（如核显）覆盖。
3. **性能**：`HardwareValueProvider` 带缓存，避免重复计算；`NetworkManager` 与 `DiskManager` 在可能时优先使用 Windows 计数器而非驱动轮询。
4. **缺失与零分离**：任何未采集到的值必须以缺失形式表达，**不得**用 0 或占位值冒充。
