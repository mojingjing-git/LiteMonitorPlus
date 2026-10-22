# 外观夹具数据（A04）

本目录是 **LiteMonitor++ 外观探针**（`tests/LiteMonitor.PerformanceProbe/`）的**固定夹具数据**。

| 项 | 说明 |
|---|---|
| 唯一事实来源 | `appearance-fixtures.json`（本目录） |
| 消费者 | `tests/LiteMonitor.PerformanceProbe/AppearanceFixture.cs`（读取）+ `AppearanceProbe.cs`（渲染） |
| 复制到探针输出目录 | 由 `LiteMonitor.PerformanceProbe.csproj` 的一条 `<Content Include>` 完成（不新增程序集引用） |
| 运行时回退 | 探针输出目录找不到时，按仓库根回溯找**同一份**文件；两条路径指向同一个文件，不会内容漂移 |

## 为什么要独立成目录

- 夹具是**数据**，不是代码：评审时可以直接 diff 这份 JSON，不必读渲染代码。
- 夹具一旦冻结，**改造前后必须用同一份夹具**跑，才能逐像素对比。
- 探针**动态发现主题**（读 `resources/themes/*.json`），所以模型线交付
  `CyberPunk_Mech.json` 之后**重新 build 探针**即可自动纳入夹具矩阵，**不需要改这里**。

## 夹具覆盖的维度

| 维度 | 取值 | 来源 |
|---|---|---|
| 主题 | 运行时发现（当前 10 个旧主题；新主题到位后自动纳入） | `resources/themes/*.json` |
| 布局 | `vertical` / `horizontal` | `axes.modes` |
| 显卡卡数 | 1 / 4 | `axes.gpuCounts` |
| DPI | 96 / 120 / 144 / 192 | `axes.dpis`（**离屏模拟**） |
| 用户缩放 | 1.0 / 2.0 | `axes.userScales`（**离屏模拟**） |
| 边界场景 | 长组名 / 自定义标签 / 零项 / 窄窗 | `edgeScenarios` |

## 关键约束

- **不依赖硬件**：显卡简称是夹具里写死的字符串（`gpus[].shortName`），
  通过 `GpuRegistry.ReplaceAll` 注入，**不枚举任何真实显卡**。
- **不依赖驱动**：探针不创建 `HardwareMonitor`、不 `Open()` 硬件、不做 PawnIO 检查。
- **可复现**：所有指标值由 `valueModel` 纯函数推导；Dashboard 文本由 `dashValues` 固定注入
  （否则会随机器名 / 当前时间 / 开机时长变化，破坏"同一夹具两次跑出同一像素"的前提）。
- **不读不写用户配置**：探针自己 `new Settings()` 并注入 `Settings._instance` 单例。

## 已知边界

- `dpi` 与 `userScale` 都是**离屏模拟**：布局尺寸走生产同一条 `Theme.Scale(dpiScale, userScale)` 路径，
  字形点值再乘 `dpiScale` 逼近真实高 DPI 窗口的 GDI 光栅化。
  **这不能替代 v4 §7.2 要求的真实 96/120/144/192 DPI 窗口测试。**
- 离屏渲染**不包含** DWM / 窗口呈现 / 桌面合成 / 窗口透明度 / 任务栏。
- 横屏的列间距由 `HorizontalLayout` 内部按**进程真实系统 DPI** 缩放
  （`Graphics.FromHwnd(IntPtr.Zero)`），离屏模拟改写不了；
  模拟 DPI 与真实系统 DPI 不同时，横屏列间距与真实高 DPI 窗口不一致。
