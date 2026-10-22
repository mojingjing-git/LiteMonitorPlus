# ADR-0001：D3D11 呈现与桌面捕获原型决定

> 状态：呈现与捕获功能通过；WDA 捕获排除策略已由用户接受  
> 日期：2026-10-21  
> 执行模型：DeepSeek V4.1 Flash

## 决定

首版原型采用：

1. D3D11 硬件设备。
2. DXGI HWND-bound flip-model swap chain。
3. Direct2D/DirectWrite 绘制透明前景纹理。
4. 单一 HLSL pass 合成背景、材质和清晰前景。
5. DXGI Desktop Duplication 获取同一输出适配器的桌面帧。
6. 玻璃实时模式使用 `WDA_EXCLUDEFROMCAPTURE`，赛博朋克固定模式不创建捕获。

顶层 HWND 直接宿主与独立子 HWND 宿主均实际运行；首版选择顶层 HWND，减少附属窗口的 Z 序、移动和关闭同步复杂度。子宿主保留为能力回退，不是首版默认。

## 实测

参考输出：NVIDIA GeForce RTX 3080 Ti，3840x2160。

| 项目 | 结果 | 限制 |
|---|---|---|
| D3D11 着色器与交换链 | 通过 | Windows 11 26300，x64 |
| 无边框、置顶、`Form.Opacity=0.85`、点击穿透样式 | 通过 | 30 秒以上交互未覆盖 |
| 子 HWND 宿主 | 通过 | 只验证玻璃/赛博固定输入和一次实时玻璃 |
| Mica/Acrylic 替代路线 | 未采用 | 不能提供任意清晰背景纹理供折射 shader 采样 |
| Desktop Duplication | 通过 | 仅 SDR 单输出，当前为 3840x2160 BGRA8 |
| WDA 自排除 | 通过 | 本程序从外部系统截图消失 |
| 无 WDA 对照 | 失败/保留证据 | 出现自身嵌套、文字重影和黑底 |
| 外部录屏共存 | 局部证据 | 使用系统截图路径验证；尚未覆盖第三方录屏软件 |

一次 6 帧实时玻璃运行的捕获间隔 p95 为 `130 ms`。样本太短，只证明帧可到达，不能作为性能通过或生产预算证据。

## WDA 取舍

`WDA_EXCLUDEFROMCAPTURE` 针对整个顶层窗口，不区分材质背景与前景文字，并影响其他受支持捕获途径。当前路线可得到干净动态背景且无自反馈，但会让本程序在系统截图/录屏中不可见。

这是已确认的产品行为。用户于 2026-10-21 选择：

- 玻璃实时模式在受支持的系统截图/录屏中整体排除；
- 旧主题、固定输入研究与普通回退继续可见；
- 发布说明和诊断必须明确披露排除范围。

## 未验证

- Windows 10 22H2。
- HDR、SDR/HDR 混合和颜色管理。
- 多屏负坐标、混合 DPI、旋转、显示器拔插。
- 锁屏、UAC 安全桌面、远程会话、受保护内容。
- 第三方录屏软件和跨适配器拷贝成本。
- 200% DPI 下的全部几何和窗口行为。
- 连续 30 分钟资源与性能预算。

## 证据

基线目录：

```text
docs/reviews/evidence/gpu-themes/694a8c5/R03/presentation/
docs/reviews/evidence/gpu-themes/694a8c5/R04/joint/
```

关键文件：

- `hwnd-route-v2.png`：顶层 HWND、Opacity、点击穿透回读。
- `child-route-v2.png`：子 HWND 宿主回读。
- `live-glass-excluded-v1.png`：WDA 排除下动态背景玻璃。
- `external-capture-excluded-v1.png`：同一时刻外部截图，只显示专用背景。
- `live-glass-no-exclusion-v1.png` / `external-capture-no-exclusion-v1.png`：无排除的自反馈对照。
