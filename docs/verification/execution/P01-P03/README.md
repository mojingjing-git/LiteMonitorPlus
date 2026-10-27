# P01–P03 低功耗显示调度证据

日期：2026-10-27
状态：**核心策略与 GPU 宿主接线完成；GDI/WinForms 非 GPU 路径与实机功耗验收未完成**。

## 交付

| 项 | 文件 | 说明 |
|---|---|---|
| P01/P02 纯策略 | `src/Rendering/Power/PowerPolicy.cs` | `PowerMode`、`ResolveDisplay`、`ResolveAnimationFps`、`ResolveSampleInterval`；不读取系统状态、不持有窗口。 |
| P03 调度适配 | `src/Rendering/Power/PowerDisplayScheduler.cs` | 把策略结果落到 `FrameScheduler`；隐藏时只记录 Paused，不把 0 FPS 传给调度器。 |
| P03 GPU 宿主接线 | `src/Rendering/Presentation/GpuPresentationHostController.cs` | `UpdatePowerContext`、`ApplyPowerCadence`、`Form.VisibleChanged` 暂停/恢复；构造参数可注入初始模式。 |

## 验证

- 主项目 Release 构建：`build=0`，日志 `p01-p03-build.log`。
- 定向测试：`PowerPolicyTests` 6 + `PowerDisplaySchedulerTests` 3 = **9/9 通过**，日志 `p01-p03-tests.log`。
- GPU 宿主回归：`GpuPresentationTransitionTests` + `RenderingContractTests` = **19/19 通过**（本次运行，未写入日志文件）。
- `PowerPolicy` 覆盖：Off/Auto/Eco、AC/电池、隐藏暂停、动画与采样间隔下限、非法参数。
- `PowerDisplayScheduler` 覆盖：普通 60、Auto 电池 15、Eco 10、隐藏 Paused 且 `TryTakeFrame=false`。

## 未完成 / 不计为通过

- GDI/WinForms 非 GPU 刷新路径未接入 `PowerDisplayScheduler`；任务栏窗口未做独立可见性策略。
- 主窗最小化、锁屏/会话切换、AC/DC 真实事件源尚未接线；`UpdatePowerContext` 目前由测试显式调用，尚未接生产组合根。
- 实机功耗、帧率、GPU/CPU 资源与 10 分钟×3 轮性能协议未执行。
- P04–P07（采样频率协调、设置/诊断/场景协同、性能验收）未开始。
