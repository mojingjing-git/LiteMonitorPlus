# P01/P02 低功耗纯策略

## 范围

- `PowerMode`：Off / Auto / Eco。
- `PowerPolicy.ResolveDisplay`：可见时 Off=正常 FPS、Auto+电池=15 FPS、Eco=10 FPS；不可见时 Paused。
- `ResolveAnimationFps`：Eco≤20、Auto+电池≤30。
- `ResolveSampleInterval`：Eco≥2s、Auto+电池≥1.5s；其他保持原周期。
- 纯函数，不读系统状态、不持有 Timer/窗口；功率与可见性由组合根注入。

## 验证

- `PowerPolicyTests` 6/6 通过。
- `LiteMonitor.csproj` Release 0 warnings / 0 errors。
- 日志：`p01-p02-tests.log`、`p01-p02-build.log`。

## 未接线

- 尚未接入 `FrameScheduler`、`GpuPresentationHostController`、`HardwareSamplingCoordinator`。
- 未实现托盘/设置页电源模式选择、真实电池/供电切换验收。
- P03–P07 未开始。
