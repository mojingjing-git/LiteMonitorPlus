# T04 趋势窗增强证据

日期：2026-10-27。状态：代码与自动化完成，实机主题/DPI 截图待 G2。

## 交付

- `src/Core/Trends/TrendViewMetrics.cs`：时间桶去重、范围裁剪、覆盖率计算。
- `src/UI/HardwareTrendForm.cs`：
  - 指标快捷入口面板；点击选中表格行并高亮图表；表格选中同步快捷按钮。
  - 图表标题显示当前桶宽与覆盖率 `observed/expected`。
  - `ApplyTheme()` 读取 `SettingsThemeManager.CurrentPalette`；`AutoScaleMode = Dpi`。
- `src/UI/Controls/TrendChartControl.cs`：新增 `ApplyPalette(background, grid, text, dim)`，不改变系列颜色。

## 自动化

- `TrendViewMetricsTests` 5/5。
- 趋势定向（`FullyQualifiedName~Trend`，含 T04/T01-T03）19/19。
- `LiteMonitor.csproj` Release 构建 0 warnings / 0 errors。
- 日志：`t04-build.log`、`t04-tests.log`。

## 未完成的手工验收

- 真实 100%/125%/150%/200% DPI 下布局与字体。
- 明/暗主题切换后趋势窗、表格、图表与提示框配色。
- 指标快捷入口在 32 指标下的滚动/可读性。

上述项留 G2 实机证据；本文件不声明 G2 通过。
