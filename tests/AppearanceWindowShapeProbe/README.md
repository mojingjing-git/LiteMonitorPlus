# AppearanceWindowShapeProbe（A09 窗口轮廓策略自证探针）

独立 console 自证程序，验证 `src/UI/Helpers/WindowAppearanceController.cs` 的窗口轮廓策略。

## 用途

* 不依赖 xUnit / 测试框架，只引用主项目（`ProjectReference`）与 BCL。
* 不创建 HardwareMonitor、不 Open 硬件、**不做屏幕捕获**、不读写用户配置目录。
* 只创建一个**不显示**的离屏窗体（仅创建句柄），用来验证 `Control.Region` 与 DWM 角偏好行为。
* 输出独立证据 JSON：`output\evidence\window-shape-evidence.json`。

## 运行

```powershell
dotnet build tests\AppearanceWindowShapeProbe\AppearanceWindowShapeProbe.csproj -c Release
tests\AppearanceWindowShapeProbe\output\AppearanceWindowShapeProbe.exe
# 可选：指定证据目录
tests\AppearanceWindowShapeProbe\output\AppearanceWindowShapeProbe.exe --out <证据目录>
```

退出码：`0` = 全部用例通过；`1` = 有用例失败；`2` = 探针自身异常。

## 覆盖的用例

| 用例 | 验证内容 |
|---|---|
| `style_mapping` | FrameStyle 0/1/2/3/未知 → 轮廓策略 + DWM 角偏好值 + 是否设置 Region |
| `chamfer_uses_do_not_round_one` | 切角实际传给 `DwmSetWindowAttribute` 的值是 **1(DONOTROUND)**，不是 0(DEFAULT) |
| `reversible_style_switch` | 切角 → 圆角 → 无框 连续切换，最终状态与初始状态一致，无残留 Region / DWM 属性 |
| `region_ownership_no_leak` | Region 所有权（由 WinForms 释放）+ 200 轮切换后 GDI/USER/句柄数不增长 + 路径创建数 = 释放数 |
| `boundary_sizes` | 1×1 / 0×0 / 0×120 / 240×0 / 2×2 / 60×40 不抛异常、不产生非法 Region |
| `physical_pixels_used_once` | 192 DPI 下 `FrameLengthPx=16` → 窗口轮廓切角正好在 16 物理像素（不二次缩放） |
| `geometry_diagnostic` | 记录路径顶点、`IsVisible` 命中测试容差、扫描线读数（诊断用） |
| `ui_thread_only` | 非 UI 线程调用抛 `InvalidOperationException` |
| `path_copied_into_region` | `Region` 复制路径内容 → 路径可立即释放 |
| `dwm_corner_preference_readback` | 用**真实** DWM 调用后用 `DwmGetWindowAttribute` 读回角偏好（1 / 2 / 2） |
| `handle_destroyed_no_win32` | 句柄重建后重新应用；句柄销毁后不再调用任何 Win32，也不抛异常 |
| `resize_reclamps_corner_length` | **R4 反例**：旧快照被小高度夹紧后，窗口变高必须按新高度重算切角长度（40 → 64），并断言 Region 轮廓 == 当前快照推导出的轮廓 |
| `theme_switch_updates_corner_length` | **R4 反例**：同尺寸换肤后切角长度随新主题的 `frameLength` 更新（16 → 48 → 16） |
| `layout_switch_updates_corner_length` | **R4 反例**：横竖布局切换导致客户区高度变化后，切角长度重新夹紧（50 → 64 → 50） |
| `resize_binding_unique` | **A13 硬约束**：窗口轮廓接线的 `Resize +=` 订阅仍然只有一处（源码扫描） |

## 已知限制

* Win10（无 DWM 角偏好能力）分支只能靠 `DwmCornerSupportOverride` 模拟，未在真实 Win10 上验证。
* `Region.IsVisible` 在 GDI+ 中对边界有约 1~2 像素容差，**不能**用来判断"点是否在切角内"；
  精确测量切角位置必须用 `Region.GetRegionScans`（见 `physical_pixels_used_once`）。
* 探针只验证窗口级形状与资源，**不验证**实际像素观感（需 A13 接线后做实窗截图对比）。
